using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Display options for the dashboard renderer. Mirrors the static-renderer's
/// <c>StatusRenderOptions</c> shape so the same path-rendering rule and the same colour
/// suppression apply in both surfaces.
/// </summary>
/// <param name="Root">Repository root; used for path display in the header bar and per-row paths.</param>
/// <param name="Home">User home directory; used for <c>~/</c> substitution.</param>
/// <param name="NoColor">Suppress ANSI colour codes — composes with Spectre's native <c>NO_COLOR</c> handling.</param>
/// <param name="Version">Server version string shown in the header.</param>
internal sealed record DashboardRenderOptions(string Root, string? Home, bool NoColor, string Version);

/// <summary>
/// Per-section <see cref="IRenderable"/> builders for the dashboard. Consumes the same
/// <see cref="DashboardSnapshot"/> the static <c>StatusRenderer</c> consumes; reuses
/// <see cref="StateGlyph"/> and <see cref="PathDisplay"/> so glyph language and path rendering
/// stay in lock-step across surfaces.
///
/// <para>
/// Each <c>Build*</c> method returns a self-contained <see cref="IRenderable"/> so unit tests
/// can capture them via <see cref="AnsiConsole.Record"/> without standing up the full layout.
/// The selection cursor is threaded through as the optional <c>selectedRow</c> parameter so the
/// renderer stays pure (no hidden static state).
/// </para>
/// </summary>
internal static class DashboardRenderer
{
    /// <summary>The two-line dashboard header: brand mark + version + rendered root path.</summary>
    public static IRenderable BuildHeader(DashboardSnapshot snapshot, DashboardRenderOptions options)
    {
        var leaf = LeafFormatter.Suppressed ? "[x]" : LeafFormatter.Mark.TrimEnd();
        var leafText = Markup.Escape(leaf);
        var version = Markup.Escape(options.Version);
        var rendered = Markup.Escape(PathDisplay.Render(options.Root, options.Root, options.Home));
        // Use a Panel so the header has a visible border that sets it apart from the section
        // grid; the alignment keeps the brand + path on one line under the typical 80-col width.
        var text = $"{leafText} [bold]SourceGraph[/]  v{version}  [grey]{rendered}[/]";
        return new Panel(new Markup(text))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    /// <summary>The single-line footer with the documented key hints.</summary>
    public static IRenderable BuildFooter(string statusMessage = "")
    {
        var hint = Markup.Escape(DashboardKeyMap.FooterHint);
        var status = string.IsNullOrEmpty(statusMessage) ? "" : "  |  " + Markup.Escape(statusMessage);
        var text = $"[grey]{hint}[/]{status}";
        return new Panel(new Markup(text))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    /// <summary>The Environment section: SDK / git / repo root / solutions / .sourcegraph.json.</summary>
    public static IRenderable BuildEnvironmentSection(DashboardSnapshot snapshot, DashboardRenderOptions options)
    {
        var env = snapshot.Environment;
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().Width(4))
            .AddColumn(new GridColumn().Width(20))
            .AddColumn(new GridColumn().PadRight(0));

        AddRow(grid,
            kind: env.DotnetSdkVersion is null ? StateGlyphKind.Skip : StateGlyphKind.On,
            key: ".NET SDK",
            value: env.DotnetSdkVersion ?? "(not detected)");
        AddRow(grid,
            kind: env.GitOnPath ? StateGlyphKind.On : StateGlyphKind.Warn,
            key: "git on PATH",
            value: env.GitOnPath ? "yes" : "no");
        AddRow(grid,
            kind: StateGlyphKind.On,
            key: "repo root",
            value: PathDisplay.Render(env.RepoRootPath, options.Root, options.Home));
        var solutions = env.SolutionFiles.Count == 0
            ? "(none)"
            : string.Join(", ", env.SolutionFiles.Select(p => PathDisplay.Render(p, options.Root, options.Home)));
        AddRow(grid,
            kind: env.SolutionFiles.Count == 0 ? StateGlyphKind.Warn : StateGlyphKind.On,
            key: "solutions",
            value: solutions);
        var (cfgKind, cfgValue) = env.SourceGraphConfigStatus switch
        {
            "valid" => (StateGlyphKind.On, "valid"),
            "missing" => (StateGlyphKind.On, "missing (single-scope synth path)"),
            "malformed" => (StateGlyphKind.Skip, $"MALFORMED — {env.SourceGraphConfigError}"),
            _ => (StateGlyphKind.Warn, env.SourceGraphConfigStatus),
        };
        AddRow(grid, cfgKind, ".sourcegraph.json", cfgValue);

        return Section("Environment", grid, focused: false);
    }

    /// <summary>The Scopes section: one row per registered scope. <paramref name="selectedRow"/>
    /// highlights one row under the focused section.</summary>
    public static IRenderable BuildScopesSection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false, int selectedRow = 0)
    {
        if (snapshot.Scopes.Count == 0)
        {
            var empty = new Markup($"  {EscapedGlyph(StateGlyphKind.Off)}(no scopes registered — run `sourcegraph-mcp serve` once to materialise)");
            return Section("Scopes", empty, focused);
        }
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))   // cursor
            .AddColumn(new GridColumn().NoWrap().Width(4))   // glyph
            .AddColumn(new GridColumn().NoWrap().Width(18))  // name
            .AddColumn(new GridColumn().NoWrap().Width(10))  // status
            .AddColumn(new GridColumn().NoWrap().Width(14))  // symbol count
            .AddColumn(new GridColumn().NoWrap().Width(14))  // ref count
            .AddColumn(new GridColumn().NoWrap());           // age
        for (var i = 0; i < snapshot.Scopes.Count; i++)
        {
            var s = snapshot.Scopes[i];
            var kind = s.Status switch
            {
                "ok" => StateGlyphKind.On,
                "partial" => StateGlyphKind.Warn,
                "degraded" => StateGlyphKind.Skip,
                "indexing" => StateGlyphKind.Warn,
                _ => StateGlyphKind.Off,
            };
            var cursor = focused && i == selectedRow ? "▶" : " ";
            var ageLabel = s.LastIndexedAt.HasValue
                ? FormatRelativeTime(DateTimeOffset.UtcNow - s.LastIndexedAt.Value)
                : "(never)";
            grid.AddRow(
                Markup.Escape(cursor),
                EscapedGlyph(kind),
                Markup.Escape(s.Name),
                Markup.Escape(s.Status),
                Markup.Escape($"{s.SymbolCount,8} symbols"),
                Markup.Escape($"{s.ReferenceCount,8} refs"),
                Markup.Escape(ageLabel));
        }
        return Section("Scopes", grid, focused);
    }

    /// <summary>The Clients section: one row per detected MCP client config.</summary>
    public static IRenderable BuildClientsSection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false, int selectedRow = 0)
    {
        if (snapshot.Clients.Count == 0)
        {
            var empty = new Markup($"  {EscapedGlyph(StateGlyphKind.Off)}(no client configs detected)");
            return Section("Clients", empty, focused);
        }
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))
            .AddColumn(new GridColumn().NoWrap().Width(4))
            .AddColumn(new GridColumn().NoWrap().Width(15))
            .AddColumn(new GridColumn().NoWrap().Width(8))
            .AddColumn(new GridColumn().NoWrap());
        var ordered = snapshot.Clients
            .OrderBy(c => c.Scope == "project" ? 0 : 1)
            .ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var c = ordered[i];
            var kind = c switch
            {
                { ContainsSourcegraphEntry: true } => StateGlyphKind.On,
                { Exists: true } => StateGlyphKind.Off,
                _ => StateGlyphKind.Unsupported,
            };
            var cursor = focused && i == selectedRow ? "▶" : " ";
            grid.AddRow(
                Markup.Escape(cursor),
                EscapedGlyph(kind),
                Markup.Escape(c.Slug),
                Markup.Escape(c.Scope),
                Markup.Escape(PathDisplay.Render(c.Path, options.Root, options.Home)));
        }
        return Section("Clients", grid, focused);
    }

    /// <summary>The Embeddings section: one row with model id + cache size + verified flag.</summary>
    public static IRenderable BuildEmbeddingsSection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false)
    {
        var emb = snapshot.Embeddings;
        var kind = emb.CachePresent ? StateGlyphKind.On : StateGlyphKind.Warn;
        var verifiedLabel = emb.Verified ? "verified" : "unverified";
        var sizeLabel = emb.CachePresent ? FormatBytes(emb.TotalBytes) : "(absent)";
        var grid = new Grid().AddColumn(new GridColumn().PadRight(0));
        grid.AddRow(new Markup(
            $"  {EscapedGlyph(kind)}{Markup.Escape(emb.ModelId)}   {Markup.Escape(sizeLabel)}   [grey]{Markup.Escape(verifiedLabel)}[/]"));
        grid.AddRow(new Markup(
            $"    cache: [grey]{Markup.Escape(PathDisplay.Render(emb.CacheDir, options.Root, options.Home))}[/]"));
        return Section("Embeddings", grid, focused);
    }

    /// <summary>The Recent activity section: one row per tool call / heal entry.</summary>
    public static IRenderable BuildRecentActivitySection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false, int selectedRow = 0, int maxRows = 12)
    {
        if (snapshot.RecentActivity.Count == 0)
        {
            var empty = new Markup($"  {EscapedGlyph(StateGlyphKind.Off)}(no recorded activity)");
            return Section("Recent activity", empty, focused);
        }
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))
            .AddColumn(new GridColumn().NoWrap().Width(4))
            .AddColumn(new GridColumn().NoWrap().Width(10))
            .AddColumn(new GridColumn().NoWrap().Width(22))
            .AddColumn(new GridColumn().NoWrap().Width(14))
            .AddColumn(new GridColumn().NoWrap());
        // Show the most recent rows first; cap at maxRows so the section fits within the layout.
        var rows = snapshot.RecentActivity
            .OrderByDescending(a => a.Ts)
            .Take(maxRows)
            .ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var a = rows[i];
            var kind = a.Ok ? StateGlyphKind.On : StateGlyphKind.Skip;
            var time = a.Ts.ToLocalTime().ToString("HH:mm:ss");
            var name = a.Detail ?? a.Kind;
            var scope = a.Scope ?? "-";
            var cursor = focused && i == selectedRow ? "▶" : " ";
            grid.AddRow(
                Markup.Escape(cursor),
                EscapedGlyph(kind),
                Markup.Escape(time),
                Markup.Escape(name),
                Markup.Escape(scope),
                Markup.Escape($"{a.Ms,5}ms"));
        }
        return Section("Recent activity", grid, focused);
    }

    private static void AddRow(Grid grid, StateGlyphKind kind, string key, string value)
    {
        grid.AddRow(
            EscapedGlyph(kind),
            Markup.Escape(key),
            Markup.Escape(value));
    }

    /// <summary>
    /// Wrap a section body in a Spectre <see cref="Panel"/> with the section name as its header.
    /// The focused section gets a bright border; others get a dim border so the eye can follow
    /// the navigation cursor across the layout.
    /// </summary>
    private static Panel Section(string title, IRenderable body, bool focused)
    {
        var border = focused ? BoxBorder.Heavy : BoxBorder.Rounded;
        var headerStyle = focused ? "bold" : "grey";
        return new Panel(body)
        {
            Border = border,
            Header = new PanelHeader($"[{headerStyle}]{Markup.Escape(title)}[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    private static string EscapedGlyph(StateGlyphKind kind)
    {
        // StateGlyph.For returns a token with a trailing space; Markup.Escape keeps the
        // emoji and the bracket chars intact (no special-glyph escapes needed).
        return Markup.Escape(StateGlyph.For(kind));
    }

    /// <summary>Operator-friendly relative time like <c>2m ago</c> / <c>3h ago</c>; matches StatusRenderer.</summary>
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
            _ => $"{bytes / GiB:F2} GiB",
        };
    }
}
