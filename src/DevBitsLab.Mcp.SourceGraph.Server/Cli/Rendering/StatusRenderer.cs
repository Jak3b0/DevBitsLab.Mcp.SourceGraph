using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;

/// <summary>
/// Options controlling how <see cref="StatusRenderer"/> writes prose to a <see cref="TextWriter"/>.
/// </summary>
/// <param name="Root">Repository root, used for path display.</param>
/// <param name="Home">Home directory, used for <c>~</c>-substitution.</param>
/// <param name="NoColor">
///     When true, suppress ANSI colour codes. Distinct from <see cref="LeafFormatter.Suppressed"/>
///     (which controls glyph language); <c>--no-color</c> never affects glyphs.
/// </param>
internal sealed record StatusRenderOptions(string Root, string? Home, bool NoColor);

/// <summary>
/// Phase-headed prose renderer for <see cref="DashboardSnapshot"/>. Mirrors
/// <see cref="InitRenderer"/>'s shape — five phases (Environment, Scopes, Clients, Embeddings,
/// Recent activity), each emitting a heading at the left margin followed by two-space-indented
/// rows. Glyph language and ASCII fallback live in <see cref="StateGlyph"/>; column alignment is
/// preserved across emoji and ASCII modes by design.
/// </summary>
internal static class StatusRenderer
{
    private const string Indent = "  ";

    // Column widths chosen to accommodate the longest label in each phase.
    private const int EnvKeyWidth = 18;
    // Scope status verb column: "indexing" (8) + buffer.
    private const int ScopeStatusWidth = 10;
    // Scope name column padded to align the status verb across rows.
    private const int ScopeNameWidth = 18;
    // Client slug column width: longest slug is "claude-desktop" (14).
    private const int ClientSlugWidth = 15;
    // Client scope column width: "project" / "user" (7).
    private const int ClientScopeWidth = 8;

    /// <summary>
    /// Render <paramref name="snapshot"/> to <paramref name="writer"/> using the
    /// state-glyph language. Header rows do not carry a glyph; under each header, rows are
    /// indented two spaces and begin with the appropriate state glyph token.
    /// </summary>
    public static void RenderHuman(
        DashboardSnapshot snapshot,
        TextWriter writer,
        StatusRenderOptions options)
    {
        RenderBanner(writer);
        RenderEnvironment(writer, snapshot.Environment, options);
        RenderScopes(writer, snapshot.Scopes);
        RenderClients(writer, snapshot.Clients, options);
        RenderEmbeddings(writer, snapshot.Embeddings, options);
        RenderRecentActivity(writer, snapshot.RecentActivity);
    }

    private static void RenderBanner(TextWriter writer)
    {
        var prefix = LeafFormatter.Suppressed ? "" : LeafFormatter.Mark;
        writer.WriteLine($"{prefix}SourceGraph status");
        writer.WriteLine();
    }

    private static void RenderEnvironment(TextWriter writer, EnvironmentSurface env, StatusRenderOptions options)
    {
        writer.WriteLine("Environment");
        // The glyph predicates here must mirror `StatusCli.EvaluateExit` for the same surface.
        // An empty SDK string is a hard-fail there (`string.IsNullOrEmpty`), and a missing repo
        // root directory is a hard-fail too — rendering either as `On` would let the human
        // surface say "healthy" while the exit code reports failure.
        WriteRow(writer,
            kind: string.IsNullOrEmpty(env.DotnetSdkVersion) ? StateGlyphKind.Skip : StateGlyphKind.On,
            key: ".NET SDK",
            value: env.DotnetSdkVersion ?? "(not detected)");
        WriteRow(writer,
            kind: env.GitOnPath ? StateGlyphKind.On : StateGlyphKind.Warn,
            key: "git on PATH",
            value: env.GitOnPath ? "yes" : "no");
        WriteRow(writer,
            kind: Directory.Exists(env.RepoRootPath) ? StateGlyphKind.On : StateGlyphKind.Skip,
            key: "repo root",
            value: PathDisplay.Render(env.RepoRootPath, options.Root, options.Home));
        var solutionsRendered = env.SolutionFiles.Count == 0
            ? "(none)"
            : string.Join(", ", env.SolutionFiles.Select(p => PathDisplay.Render(p, options.Root, options.Home)));
        WriteRow(writer,
            kind: env.SolutionFiles.Count == 0 ? StateGlyphKind.Warn : StateGlyphKind.On,
            key: "solutions",
            value: solutionsRendered);
        var (kind, value) = env.SourceGraphConfigStatus switch
        {
            "valid" => (StateGlyphKind.On, "valid"),
            "missing" => (StateGlyphKind.On, "missing (single-scope synth path)"),
            "malformed" => (StateGlyphKind.Skip, $"MALFORMED — {env.SourceGraphConfigError}"),
            _ => (StateGlyphKind.Warn, env.SourceGraphConfigStatus),
        };
        WriteRow(writer, kind, ".sourcegraph.json", value);
        writer.WriteLine();
    }

    private static void RenderScopes(TextWriter writer, IReadOnlyList<ScopeRow> scopes)
    {
        writer.WriteLine("Scopes");
        if (scopes.Count == 0)
        {
            writer.WriteLine($"{Indent}{StateGlyph.For(StateGlyphKind.Off)}(no scopes registered — run `sourcegraph-mcp serve` once to materialise)");
            writer.WriteLine();
            return;
        }
        foreach (var s in scopes)
        {
            var kind = s.Status switch
            {
                "ok" => StateGlyphKind.On,
                "partial" => StateGlyphKind.Warn,
                "degraded" => StateGlyphKind.Skip,
                "indexing" => StateGlyphKind.Warn,
                _ => StateGlyphKind.Off,
            };
            var glyph = StateGlyph.For(kind);
            var name = s.Name.PadRight(ScopeNameWidth);
            var status = s.Status.PadRight(ScopeStatusWidth);
            var ageLabel = s.LastIndexedAt.HasValue
                ? FormatRelativeTime(DateTimeOffset.UtcNow - s.LastIndexedAt.Value)
                : "(never)";
            writer.WriteLine(
                $"{Indent}{glyph}{name} {status} {s.SymbolCount,8} symbols  {s.ReferenceCount,8} refs  {ageLabel}");
            // Hanging detail line for partial / degraded rows so the operator sees the failure
            // names without consulting `scopes info`.
            if (s.Status == "partial" && s.FailedProjects.Count > 0)
            {
                writer.WriteLine($"{Indent}    failed projects: {string.Join(", ", s.FailedProjects)}");
            }
            if (s.Status == "partial" && s.FailedFiles.Count > 0)
            {
                writer.WriteLine($"{Indent}    failed files: {string.Join(", ", s.FailedFiles)}");
            }
            if (s.Status == "degraded")
            {
                writer.WriteLine($"{Indent}    recommended: run `repair_scope mode=rebuild`");
            }
        }
        writer.WriteLine();
    }

    private static void RenderClients(TextWriter writer, IReadOnlyList<ClientRow> clients, StatusRenderOptions options)
    {
        writer.WriteLine("Clients");
        if (clients.Count == 0)
        {
            writer.WriteLine($"{Indent}{StateGlyph.For(StateGlyphKind.Off)}(no client configs detected)");
            writer.WriteLine();
            return;
        }
        // Project-scope rows first, then user-scope, to match the init order.
        foreach (var c in clients.OrderBy(c => c.Scope == "project" ? 0 : 1))
        {
            var kind = c switch
            {
                { ContainsSourcegraphEntry: true } => StateGlyphKind.On,
                { Exists: true } => StateGlyphKind.Off,
                _ => StateGlyphKind.Unsupported,
            };
            var glyph = StateGlyph.For(kind);
            var slug = c.Slug.PadRight(ClientSlugWidth);
            var scope = c.Scope.PadRight(ClientScopeWidth);
            var pathDisplay = PathDisplay.Render(c.Path, options.Root, options.Home);
            writer.WriteLine($"{Indent}{glyph}{slug} {scope} {pathDisplay}");
        }
        writer.WriteLine();
    }

    private static void RenderEmbeddings(TextWriter writer, EmbeddingsSurface emb, StatusRenderOptions options)
    {
        writer.WriteLine("Embeddings");
        var kind = emb.CachePresent ? StateGlyphKind.On : StateGlyphKind.Warn;
        var verifiedLabel = emb.Verified ? "verified" : "unverified";
        var sizeLabel = emb.CachePresent ? FormatBytes(emb.TotalBytes) : "(absent)";
        writer.WriteLine($"{Indent}{StateGlyph.For(kind)}{emb.ModelId}   {sizeLabel}   {verifiedLabel}");
        writer.WriteLine($"{Indent}    cache: {PathDisplay.Render(emb.CacheDir, options.Root, options.Home)}");
        writer.WriteLine();
    }

    private static void RenderRecentActivity(TextWriter writer, IReadOnlyList<ActivityEntry> activity)
    {
        writer.WriteLine("Recent activity");
        if (activity.Count == 0)
        {
            writer.WriteLine($"{Indent}{StateGlyph.For(StateGlyphKind.Off)}(no recorded activity)");
            return;
        }
        foreach (var a in activity)
        {
            var kind = a.Ok ? StateGlyphKind.On : StateGlyphKind.Skip;
            var time = a.Ts.ToLocalTime().ToString("HH:mm:ss");
            var name = (a.Detail ?? a.Kind).PadRight(20);
            var scope = (a.Scope ?? "-").PadRight(12);
            writer.WriteLine($"{Indent}{StateGlyph.For(kind)}{time}  {name} {scope} {a.Ms,5}ms");
        }
    }

    private static void WriteRow(TextWriter writer, StateGlyphKind kind, string key, string value)
    {
        var glyph = StateGlyph.For(kind);
        var keyPadded = key.PadRight(EnvKeyWidth);
        writer.WriteLine($"{Indent}{glyph}{keyPadded} {value}");
    }

    /// <summary>
    /// Format a <see cref="TimeSpan"/> as a short relative-time string like <c>2m ago</c> /
    /// <c>3h ago</c>. Matches the operator-friendly shape used in <c>scopes info</c>.
    /// </summary>
    internal static string FormatRelativeTime(TimeSpan delta)
    {
        if (delta.TotalSeconds < 60) return $"{(int)Math.Max(0, delta.TotalSeconds)}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 48) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        return bytes switch
        {
            < (long)KiB => $"{bytes} B",
            < (long)MiB => $"{bytes / KiB:F1} KiB",
            < (long)GiB => $"{bytes / MiB:F1} MiB",
            _           => $"{bytes / GiB:F2} GiB",
        };
    }
}
