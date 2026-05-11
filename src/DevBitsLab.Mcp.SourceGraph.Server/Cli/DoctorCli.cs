using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Implementation of the <c>sourcegraph-mcp doctor</c> subcommand: read-only environment
/// diagnostic. Reports SDK / git / solution / config / per-client-wiring status. Exit codes
/// follow the <c>vocabulary list --strict</c> precedent: 0 on all-pass, 2 on any warn,
/// 1 on any hard fail.
///
/// <para>
/// <b>Implementation note:</b> the check list is projected from a <see cref="DashboardSnapshot"/>
/// built by <see cref="SnapshotBuilder.BuildAsync"/>, not from a direct
/// <c>OnboardingDetector.DetectAsync</c> call. Observable behaviour — message wording, exit
/// codes, JSON shape — is preserved verbatim (pinned by <c>DoctorCliGoldenTests</c>); the
/// refactor moves doctor and <c>status</c> onto the same data source so the two surfaces
/// can't drift.
/// </para>
/// </summary>
internal static class DoctorCli
{
    public static async Task<int> RunAsync(CommandLine cli)
    {
        var jsonMode = cli.Json;
        var root = cli.ResolvedRepoRoot();

        // Build the unified snapshot. Doctor doesn't use the JSONL activity tail or per-scope
        // counts at v1, but pulling from the same builder ensures any future cross-surface
        // invariant (drift, integrity check, …) lands in doctor automatically.
        var snapshot = await SnapshotBuilder.BuildAsync(root, new SnapshotOptions()).ConfigureAwait(false);

        var checks = ProjectChecksFromSnapshot(snapshot);

        if (jsonMode)
        {
            EmitJson(checks);
        }
        else
        {
            EmitHuman(checks);
        }

        if (checks.Any(c => c.Status == DoctorStatus.Fail)) return 1;
        if (checks.Any(c => c.Status == DoctorStatus.Warn)) return 2;
        return 0;
    }

    /// <summary>
    /// Map a <see cref="DashboardSnapshot"/> to today's eight-check doctor list. The mapping is
    /// the documented byte-stable contract; every wording string here is identical to the
    /// pre-refactor doctor output (pinned by the golden-file tests).
    /// </summary>
    private static List<DoctorCheck> ProjectChecksFromSnapshot(DashboardSnapshot snapshot)
    {
        var checks = new List<DoctorCheck>();
        var env = snapshot.Environment;

        // 1. .NET SDK.
        if (string.IsNullOrEmpty(env.DotnetSdkVersion))
        {
            checks.Add(new("dotnet-sdk", DoctorStatus.Fail,
                "no .NET SDK on PATH (>= 10.0 required); see https://dotnet.microsoft.com/download"));
        }
        else
        {
            var ok = SdkVersionMeetsMin(env.DotnetSdkVersion, major: 10);
            checks.Add(new("dotnet-sdk", ok ? DoctorStatus.Pass : DoctorStatus.Fail,
                ok ? $".NET SDK {env.DotnetSdkVersion}"
                   : $".NET SDK {env.DotnetSdkVersion} is below the required 10.0"));
        }

        // 2. git.
        checks.Add(new("git", env.GitOnPath ? DoctorStatus.Pass : DoctorStatus.Warn,
            env.GitOnPath
                ? "git on PATH"
                : "git not on PATH — `who_authored` and `recent_changes` will return empty; pass --no-history to silence"));

        // 3. Repo root readable.
        checks.Add(new("repo-root", Directory.Exists(env.RepoRootPath) ? DoctorStatus.Pass : DoctorStatus.Fail,
            $"repo root: {env.RepoRootPath}"));

        // 4. Solution discoverable.
        checks.Add(env.SolutionFiles.Count > 0
            ? new DoctorCheck("solutions", DoctorStatus.Pass,
                $"discovered {env.SolutionFiles.Count} solution(s): {string.Join(", ", env.SolutionFiles.Select(Path.GetFileName))}")
            : new DoctorCheck("solutions", DoctorStatus.Warn,
                "no .slnx/.sln files at repo root — run `sourcegraph-mcp init --solution <path>` if you want to scaffold a config explicitly"));

        // 5. .sourcegraph.json status.
        switch (env.SourceGraphConfigStatus)
        {
            case "valid":
                checks.Add(new("sourcegraph-config", DoctorStatus.Pass, ".sourcegraph.json parses cleanly"));
                break;
            case "missing":
                checks.Add(new("sourcegraph-config", DoctorStatus.Pass, "no .sourcegraph.json (single-scope synth path)"));
                break;
            case "malformed":
                checks.Add(new("sourcegraph-config", DoctorStatus.Fail,
                    $".sourcegraph.json malformed: {env.SourceGraphConfigError}"));
                break;
        }

        // 6. Embedding model cache. Pre-refactor doctor reported "present" iff
        // `Directory.Exists(cache_dir)` regardless of whether it held files, then printed total
        // bytes (which can legitimately be zero for a freshly-created empty dir). We preserve
        // that wording here by querying `Directory.Exists` separately rather than reading
        // `cache_present` (which the snapshot exposes with the stricter "has files" semantics
        // documented in the JSON contract).
        var emb = snapshot.Embeddings;
        if (Directory.Exists(emb.CacheDir))
        {
            checks.Add(new("embedding-cache", DoctorStatus.Pass,
                $"embedding model cache present at {emb.CacheDir} ({emb.TotalBytes / 1024 / 1024} MB)"));
        }
        else
        {
            checks.Add(new("embedding-cache", DoctorStatus.Warn,
                $"embedding model cache absent at {emb.CacheDir} — `semantic_search` will return its disabled-message until model files are placed there (or pass --no-embeddings to silence)"));
        }

        // 7. Per-scope DB writability. Computed inline; snapshot doesn't carry writability today
        // (it's a function of permission state, not data).
        var scopeDir = Path.Join(env.RepoRootPath, ".sourcegraph", "scopes");
        var dbWritable = TestWritability(scopeDir);
        checks.Add(new("db-writable", dbWritable ? DoctorStatus.Pass : DoctorStatus.Fail,
            dbWritable ? $"per-scope DB dir writable: {scopeDir}"
                       : $"per-scope DB dir not writable: {scopeDir}"));

        // 8. Per-client config files. Walk the snapshot's `clients` list (the unified version of
        // `OnboardingDetectionResult.ClientConfigsDetected`); preserve the existing rule: only
        // existing config files are reported, absent slots are not a finding.
        foreach (var c in snapshot.Clients.Where(x => x.Exists))
        {
            var status = c.ContainsSourcegraphEntry ? DoctorStatus.Pass : DoctorStatus.Warn;
            var msg = c.ContainsSourcegraphEntry
                ? $"{c.Slug} config wired ({c.Scope}: {c.Path})"
                : $"{c.Slug} config exists but has no sourcegraph entry — run `sourcegraph-mcp init --client {c.Slug}` ({c.Scope}: {c.Path})";
            checks.Add(new($"client-{c.Slug}", status, msg));
        }
        return checks;
    }

    private static void EmitHuman(List<DoctorCheck> checks)
    {
        var color = !Environment.GetEnvironmentVariables().Contains("NO_COLOR")
            && !Console.IsOutputRedirected;

        Console.WriteLine("🌿 SourceGraph doctor");
        Console.WriteLine();
        foreach (var c in checks)
        {
            string glyph = c.Status switch
            {
                DoctorStatus.Pass => color ? "✓" : "[OK]",
                DoctorStatus.Warn => color ? "⚠" : "[WARN]",
                DoctorStatus.Fail => color ? "✗" : "[FAIL]",
                _ => "?",
            };
            Console.WriteLine($"  {glyph} {c.Name,-22} {c.Message}");
        }
        Console.WriteLine();
        var passed = checks.Count(c => c.Status == DoctorStatus.Pass);
        var warned = checks.Count(c => c.Status == DoctorStatus.Warn);
        var failed = checks.Count(c => c.Status == DoctorStatus.Fail);
        Console.WriteLine($"summary: {passed} pass, {warned} warn, {failed} fail");
    }

    private static void EmitJson(List<DoctorCheck> checks)
    {
        var exit = checks.Any(c => c.Status == DoctorStatus.Fail) ? 1
            : checks.Any(c => c.Status == DoctorStatus.Warn) ? 2
            : 0;
        var doc = new
        {
            checks = checks.Select(c => new
            {
                name = c.Name,
                status = c.Status switch
                {
                    DoctorStatus.Pass => "pass",
                    DoctorStatus.Warn => "warn",
                    DoctorStatus.Fail => "fail",
                    _ => "unknown",
                },
                message = c.Message,
            }).ToArray(),
            exit_code = exit,
        };
        Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 2,
        }));
    }

    private static bool SdkVersionMeetsMin(string version, int major)
    {
        var firstDot = version.IndexOf('.');
        if (firstDot <= 0) return false;
        return int.TryParse(version.AsSpan(0, firstDot), out var ver) && ver >= major;
    }

    private static bool TestWritability(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Join(dir, ".sg-doctor-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record DoctorCheck(string Name, DoctorStatus Status, string Message);
    private enum DoctorStatus { Pass, Warn, Fail }
}
