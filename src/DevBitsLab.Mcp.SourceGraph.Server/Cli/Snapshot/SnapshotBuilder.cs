using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Data.Sqlite;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;

/// <summary>
/// Options influencing how <see cref="SnapshotBuilder"/> aggregates state. The defaults match
/// the spec: 512 KiB tail from each JSONL log, 50 entries kept in <c>recent_activity</c>.
/// </summary>
/// <param name="ActivityBytes">Tail size (bytes from end) for both JSONL logs.</param>
/// <param name="RecentActivityCap">Maximum entries kept in <c>recent_activity</c> after merge.</param>
internal sealed record SnapshotOptions(int ActivityBytes = 524288, int RecentActivityCap = 50);

/// <summary>
/// Aggregates the five state surfaces under <c>--root</c> into a single immutable
/// <see cref="DashboardSnapshot"/>. Reads SQLite handles in read-only mode and closes them
/// before returning so a concurrent <c>serve</c> writer is never contended. The snapshot is a
/// point-in-time view; callers re-call <see cref="BuildAsync"/> to refresh.
///
/// <para>
/// The <see cref="DashboardSnapshot.ExitCode"/> field defaults to <c>0</c> here; callers (notably
/// <c>StatusCli</c>) overwrite it with the evaluated exit code before serializing.
/// </para>
/// </summary>
internal static class SnapshotBuilder
{
    /// <summary>
    /// Build a snapshot for <paramref name="root"/>. No IO that mutates state; every SQLite
    /// handle is closed before this method returns. <paramref name="options"/> controls how
    /// many JSONL tail bytes to read and how many merged entries to keep.
    /// </summary>
    public static async Task<DashboardSnapshot> BuildAsync(
        string root,
        SnapshotOptions options,
        CancellationToken ct = default)
    {
        var rooted = Path.GetFullPath(root);

        // 1. Environment surface — re-uses OnboardingDetector's existing detection logic.
        var detection = await OnboardingDetector.DetectAsync(rooted, ct).ConfigureAwait(false);
        var env = new EnvironmentSurface(
            DotnetSdkVersion: detection.DotnetSdkVersion,
            GitOnPath: detection.GitOnPath,
            RepoRootPath: detection.RepoRootPath,
            SolutionFiles: detection.SolutionFiles,
            SourceGraphConfigStatus: detection.SourceGraphConfigStatus switch
            {
                SourceGraphConfigStatus.Valid => "valid",
                SourceGraphConfigStatus.Malformed => "malformed",
                _ => "missing",
            },
            SourceGraphConfigError: detection.SourceGraphConfigError);

        // 2. Scopes surface — read-only walk of _meta.db + each per-scope DB.
        var scopes = BuildScopes(rooted);

        // 3. Clients surface — re-uses the detection rows.
        var clients = BuildClients(detection.ClientConfigsDetected);

        // 4. Embeddings surface — cache dir presence + size + active model id.
        var embeddings = BuildEmbeddings();

        // 5. Recent activity — tails both JSONL logs, merges, sorts, caps.
        var usagePath = Path.Join(rooted, ScopeLayout.DotDir, "usage.jsonl");
        var healsPath = Path.Join(rooted, ScopeLayout.DotDir, "heals.jsonl");
        var activity = BuildActivity(usagePath, healsPath, options);

        return new DashboardSnapshot(
            Environment: env,
            Scopes: scopes,
            Clients: clients,
            Embeddings: embeddings,
            RecentActivity: activity,
            BuiltAt: DateTimeOffset.UtcNow,
            UsageLogPath: usagePath,
            HealsLogPath: healsPath,
            ExitCode: 0);
    }

    private static IReadOnlyList<ScopeRow> BuildScopes(string root)
    {
        var metaPath = ScopeLayout.MetaDbPath(root);
        if (!File.Exists(metaPath)) return Array.Empty<ScopeRow>();

        // We re-implement the read path here (rather than reusing SqliteScopeRegistry) because
        // SqliteScopeRegistry opens in ReadWriteCreate and ensures the schema on connect; we
        // want strict read-only so a fresh-snapshot call can't trigger writes against a DB
        // that's being read by a concurrent serve writer.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = metaPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        var rows = new List<ScopeRow>();
        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, root, isolated, last_indexed_at, status,
                       failed_projects_json, failed_files_json
                FROM scopes
                ORDER BY id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var scopeRoot = reader.GetString(2);
                var isolated = reader.GetInt64(3) != 0;
                var lastIndexedMs = reader.GetInt64(4);
                var status = reader.GetString(5);
                var failedProjectsJson = reader.IsDBNull(6) ? "[]" : reader.GetString(6);
                var failedFilesJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);

                var lastIndexed = lastIndexedMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(lastIndexedMs)
                    : (DateTimeOffset?)null;

                // Per-scope counts from <root>/.sourcegraph/scopes/<id>.db. Best-effort: a
                // missing or unreadable per-scope DB yields zero counts rather than aborting
                // the build.
                var (symbolCount, refCount) = ReadCountsForScope(root, id);

                var failedProjects = ParseFailureNames(failedProjectsJson);
                var failedFiles = ParseFailureNames(failedFilesJson);

                rows.Add(new ScopeRow(
                    Name: name,
                    Status: status,
                    SymbolCount: symbolCount,
                    ReferenceCount: refCount,
                    LastIndexedAt: lastIndexed,
                    FailedProjects: failedProjects,
                    FailedFiles: failedFiles,
                    Isolated: isolated));
            }
        }
        catch (SqliteException)
        {
            // The _meta.db may not yet have the v2 schema, or the file may be locked by a
            // concurrent migrator. Either way, we surface an empty scopes list rather than
            // crash — the renderer will note "no scopes" and the operator can run `serve`
            // once to materialise the registry.
            return Array.Empty<ScopeRow>();
        }
        catch (IOException)
        {
            return Array.Empty<ScopeRow>();
        }
        return rows;
    }

    private static (long Symbols, long Refs) ReadCountsForScope(string root, string scopeId)
    {
        var dbPath = ScopeLayout.ScopeDbPath(root, scopeId);
        if (!File.Exists(dbPath)) return (0, 0);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;
        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            using var symCmd = conn.CreateCommand();
            symCmd.CommandText = "SELECT COUNT(*) FROM symbols;";
            var symbols = (long)(symCmd.ExecuteScalar() ?? 0L);

            using var refCmd = conn.CreateCommand();
            refCmd.CommandText = "SELECT COUNT(*) FROM refs;";
            var refs = (long)(refCmd.ExecuteScalar() ?? 0L);
            return (symbols, refs);
        }
        catch (SqliteException) { return (0, 0); }
        catch (IOException) { return (0, 0); }
    }

    /// <summary>
    /// Best-effort projection of a <c>failed_projects_json</c> / <c>failed_files_json</c>
    /// payload into a plain string list. The persisted shape is an array of objects with at
    /// least a <c>ProjectPath</c> or <c>Path</c> property; we read whichever string field is
    /// present (so a future schema add of a new optional field doesn't break us). A malformed
    /// payload yields an empty list.
    /// </summary>
    private static IReadOnlyList<string> ParseFailureNames(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var list = new List<string>(doc.RootElement.GetArrayLength());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    list.Add(item.GetString() ?? "");
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    // The Core records use PascalCase (`ProjectPath`, `Path`) — peek each in
                    // order and take the first non-empty hit.
                    foreach (var name in new[] { "ProjectPath", "Path", "FilePath", "Name" })
                    {
                        if (item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            var s = v.GetString();
                            if (!string.IsNullOrEmpty(s))
                            {
                                list.Add(s);
                                break;
                            }
                        }
                    }
                }
            }
            return list;
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<ClientRow> BuildClients(IReadOnlyList<DetectedClientConfig> detected)
    {
        if (detected.Count == 0) return Array.Empty<ClientRow>();
        var rows = new List<ClientRow>(detected.Count);
        foreach (var c in detected)
        {
            rows.Add(new ClientRow(
                Slug: c.Client.ToSlug(),
                Scope: c.IsUserScope ? "user" : "project",
                Path: c.Path,
                Exists: c.Exists,
                ContainsSourcegraphEntry: c.ContainsSourcegraphEntry));
        }
        return rows;
    }

    private static EmbeddingsSurface BuildEmbeddings()
    {
        // Use the root cache dir (the parent of per-model directories) to preserve doctor's
        // pre-refactor wording — `embedding model cache present at <root-dir> (N MB)` — and to
        // keep the snapshot's total_bytes summing every cached model rather than only the
        // active one. The active model id stays available via <see cref="EmbeddingsSurface.ModelId"/>.
        var modelId = DefaultEmbeddingModel.ModelId;
        var cacheRoot = ModelStore.DefaultCacheDir();

        if (!Directory.Exists(cacheRoot))
        {
            return new EmbeddingsSurface(
                ModelId: modelId,
                CacheDir: cacheRoot,
                CachePresent: false,
                TotalBytes: 0,
                Verified: false);
        }

        long totalBytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories))
            {
                try { totalBytes += new FileInfo(f).Length; }
                catch (IOException) { /* skip unreadable file */ }
                catch (UnauthorizedAccessException) { /* skip unreadable file */ }
            }
        }
        catch (IOException) { /* directory disappeared mid-walk; report what we have */ }
        catch (UnauthorizedAccessException) { /* cache walk denied */ }

        return new EmbeddingsSurface(
            ModelId: modelId,
            CacheDir: cacheRoot,
            // Per spec: `total_bytes` is the sum of cached file sizes; `cache_present` is true
            // when at least one byte landed. An empty cache dir reports `false` so the
            // `status --json` consumer can tell "no model files" apart from "model files exist."
            CachePresent: totalBytes > 0,
            TotalBytes: totalBytes,
            // Verified state is not persisted at v1; conservatively report false. The dedicated
            // `sourcegraph-mcp embeddings verify` verb remains the way to confirm pinned SHAs.
            Verified: false);
    }

    private static IReadOnlyList<ActivityEntry> BuildActivity(
        string usagePath,
        string healsPath,
        SnapshotOptions options)
    {
        var entries = new List<ActivityEntry>();
        foreach (var element in JsonlTailReader.TailLines(usagePath, options.ActivityBytes))
        {
            var entry = ProjectUsageEntry(element);
            if (entry is not null) entries.Add(entry);
        }
        foreach (var element in JsonlTailReader.TailLines(healsPath, options.ActivityBytes))
        {
            var entry = ProjectHealEntry(element);
            if (entry is not null) entries.Add(entry);
        }
        entries.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        if (entries.Count > options.RecentActivityCap)
        {
            // Keep the most-recent N; drop from the head.
            entries.RemoveRange(0, entries.Count - options.RecentActivityCap);
        }
        return entries;
    }

    private static ActivityEntry? ProjectUsageEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var ts = ReadTimestamp(el, "ts");
        if (ts is null) return null;
        var tool = el.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "unknown" : "unknown";
        var scope = el.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;
        var ok = el.TryGetProperty("ok", out var o) && o.ValueKind != JsonValueKind.False;
        var ms = el.TryGetProperty("ms", out var m) && m.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(m.GetDouble()) : 0;
        return new ActivityEntry(
            Ts: ts.Value,
            // Usage log doesn't carry an explicit kind — every row is a tool call. Surfacing
            // the literal string here lets renderers and the JSON contract distinguish the
            // origin without consulting the row's tool name.
            Kind: "tool_call",
            Scope: scope,
            Ok: ok,
            Ms: ms,
            Detail: tool);
    }

    private static ActivityEntry? ProjectHealEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var ts = ReadTimestamp(el, "ts");
        if (ts is null) return null;
        var kind = el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString() ?? "heal" : "heal";
        var scope = el.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;
        var ok = el.TryGetProperty("ok", out var o) && o.ValueKind != JsonValueKind.False;
        var ms = el.TryGetProperty("ms", out var m) && m.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(m.GetDouble()) : 0;
        var details = el.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() : null;
        return new ActivityEntry(
            Ts: ts.Value,
            Kind: kind,
            Scope: scope,
            Ok: ok,
            Ms: ms,
            Detail: details);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), out var parsed))
        {
            return parsed;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        return null;
    }
}
