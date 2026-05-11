using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Display options for the dashboard renderer. Colour suppression is NOT a field here —
/// <c>--no-color</c> is handled one layer up in <c>DashboardCli.RunAsync</c> by constructing an
/// <see cref="Spectre.Console.IAnsiConsole"/> with <c>ColorSystemSupport.NoColors</c>, which
/// makes every Markup tag in this renderer render without ANSI. There's no second knob needed
/// inside the renderer itself.
/// </summary>
/// <param name="Root">Repository root; used for path display in the header bar and per-row paths.</param>
/// <param name="Home">User home directory; used for <c>~/</c> substitution.</param>
/// <param name="Version">Server version string shown in the header.</param>
internal sealed record DashboardRenderOptions(string Root, string? Home, string Version);

/// <summary>
/// Per-view <see cref="IRenderable"/> builders for the dashboard. Built around the
/// <see cref="DashboardTheme"/> palette + glyph vocabulary — borderless, dot-driven. The home
/// view shows a summary + numeric menu; each detail view shows a full table plus a per-row
/// <c>Selected:</c> drawer.
///
/// <para>
/// Each <c>Build*</c> method returns a self-contained <see cref="IRenderable"/> so unit tests
/// can capture them via <c>AnsiConsole.Record</c> without standing up the full layout. The
/// selection cursor is threaded through as the optional <c>selectedRow</c> parameter so the
/// renderer stays pure (no hidden static state).
/// </para>
/// </summary>
internal static class DashboardRenderer
{
    /// <summary>
    /// The dashboard header: brand leaf + bold name + dimmed path · version. When
    /// <paramref name="view"/> is a detail view, a breadcrumb (<c>› Section</c>) is appended
    /// and a right-aligned <c>[Esc / h] back to home</c> hint is shown.
    /// </summary>
    public static IRenderable BuildHeader(DashboardSnapshot snapshot, DashboardRenderOptions options,
        DashboardView view = DashboardView.Home)
    {
        var leaf = LeafFormatter.Suppressed ? "[x]" : DashboardTheme.BrandLeaf;
        var leafText = Markup.Escape(leaf);
        var version = Markup.Escape(options.Version);
        // PathDisplay's repo-relative rule would render the root path against itself as ".",
        // which tells the operator nothing about which repo they're looking at. The header
        // wants the path itself, so we apply only the home-relative rule (~/...) and fall
        // back to absolute by passing an empty base — this disables the repo-relative branch.
        var rendered = Markup.Escape(PathDisplay.Render(options.Root, root: "", options.Home));
        var brand = DashboardTheme.Brand;
        var muted = DashboardTheme.Muted;
        var mutedDim = DashboardTheme.MutedDim;

        string leftCell;
        string rightCell;
        if (view == DashboardView.Home)
        {
            leftCell = $"{leafText} [bold {brand}]SourceGraph[/]";
            rightCell = $"[{muted}]{rendered} · v{version}[/]";
        }
        else
        {
            var sectionName = Markup.Escape(view.DisplayName());
            leftCell = $"{leafText} [bold {brand}]SourceGraph[/]  [{mutedDim}]›[/]  [bold {brand}]{sectionName}[/]";
            // The back-to-home hint goes in the right cell on detail views; the path·version
            // line is on home only (consistent with the mock).
            rightCell = $"[{muted}]{Markup.Escape("[Esc / h] back to home")}[/]";
        }

        // Two-cell grid for left/right alignment on the header line.
        var headerGrid = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap().RightAligned());
        headerGrid.AddRow(new Markup(leftCell), new Markup(rightCell));

        var separator = $"[{mutedDim}]{Markup.Escape(new string('─', 72))}[/]";

        var rows = new Rows(headerGrid, new Markup(separator));
        return new Padder(rows).PadLeft(1).PadRight(1).PadTop(0).PadBottom(0);
    }

    /// <summary>The two-row footer: per-view key hint + toast row. Toast colour follows severity.</summary>
    public static IRenderable BuildFooter(DashboardCli.ToastState toast = default,
        DashboardView view = DashboardView.Home)
    {
        var separator = $"[{DashboardTheme.MutedDim}]{Markup.Escape(new string('─', 72))}[/]";

        // The footer hint is now view-aware — each view advertises only the keys that do
        // something in its scope.
        var hintMarkup = new Markup(DashboardKeyMap.For(view));

        IRenderable toastRow = string.IsNullOrEmpty(toast.Text)
            ? new Markup(" ") // keeps the row height stable
            : new Markup(RenderToast(toast));

        var rows = new Rows(new Markup(separator), hintMarkup, new Markup(""), toastRow);
        return new Padder(rows).PadLeft(1).PadRight(1).PadTop(0).PadBottom(0);
    }

    /// <summary>
    /// Convenience overload that lets old callers pass a bare message; severity defaults to
    /// <see cref="ToastSeverity.Info"/>. Used by tests and any caller that doesn't have a
    /// <see cref="DashboardCli.ToastState"/> handy. View defaults to <see cref="DashboardView.Home"/>.
    /// </summary>
    public static IRenderable BuildFooter(string statusMessage)
    {
        var toast = string.IsNullOrEmpty(statusMessage)
            ? default
            : new DashboardCli.ToastState(statusMessage, DateTimeOffset.UtcNow, ToastSeverity.Info);
        return BuildFooter(toast, DashboardView.Home);
    }

    /// <summary>Render the toast text with colour/fade based on age + severity.</summary>
    private static string RenderToast(DashboardCli.ToastState toast)
    {
        var age = DateTimeOffset.UtcNow - toast.SetAt;
        // First 1.5 s: full colour. Next 3.5 s: muted. Past 5 s: empty (the caller hides it).
        string colour;
        if (age < TimeSpan.FromMilliseconds(1500))
        {
            colour = toast.Severity switch
            {
                ToastSeverity.Success => DashboardTheme.Ok,
                ToastSeverity.Warn => DashboardTheme.Warn,
                ToastSeverity.Fail => DashboardTheme.Fail,
                _ => DashboardTheme.Muted,
            };
        }
        else
        {
            colour = DashboardTheme.Muted;
        }
        var prefix = toast.Severity switch
        {
            ToastSeverity.Success => LeafFormatter.Suppressed ? "[x]" : "✓",
            ToastSeverity.Warn => LeafFormatter.Suppressed ? "[!]" : "⚠",
            ToastSeverity.Fail => LeafFormatter.Suppressed ? "[X]" : "✗",
            _ => LeafFormatter.Suppressed ? "[ ]" : "·",
        };
        return $"  [{colour}]{Markup.Escape(prefix)}  {Markup.Escape(toast.Text)}[/]";
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Home view
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The home / welcome view: at-a-glance summary block (one row per area) + numeric menu.
    /// </summary>
    /// <param name="snapshot">Current dashboard snapshot.</param>
    /// <param name="options">Render options (path display etc.).</param>
    /// <param name="menuIndex">Index of the currently highlighted menu item (0..4).</param>
    public static IRenderable BuildHome(DashboardSnapshot snapshot, DashboardRenderOptions options, int menuIndex)
    {
        var rows = new List<IRenderable>();
        rows.Add(new Markup("")); // breathing room under the title-bar separator

        // Summary block — five rows, one per area.
        var summary = BuildHomeSummary(snapshot, options);
        rows.Add(summary);

        AppendDivider(rows);

        // Menu — five entries (one per detail view).
        rows.Add(BuildHomeMenu(menuIndex));

        return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
    }

    /// <summary>
    /// Build the at-a-glance summary block. Five rows, one per snapshot area, each rendering a
    /// status dot + label + compact summary text.
    /// </summary>
    private static IRenderable BuildHomeSummary(DashboardSnapshot snapshot, DashboardRenderOptions options)
    {
        var (envKind, envText) = ComputeEnvironmentSummary(snapshot, options);
        var (scopesKind, scopesText) = ComputeScopesSummary(snapshot);
        var (clientsKind, clientsText) = ComputeClientsSummary(snapshot);
        var (embKind, embText) = ComputeEmbeddingsSummary(snapshot);
        var (recentKind, recentText) = ComputeRecentSummary(snapshot);

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(3))  // dot
            .AddColumn(new GridColumn().NoWrap().Width(24)) // label (bold)
            .AddColumn(new GridColumn().NoWrap());          // summary text (muted)

        AddSummaryRow(grid, envKind, "Environment", envText);
        AddSummaryRow(grid, scopesKind, "Scopes", scopesText);
        AddSummaryRow(grid, clientsKind, "Clients", clientsText);
        AddSummaryRow(grid, embKind, "Embeddings", embText);
        AddSummaryRow(grid, recentKind, "Recent activity", recentText);
        return grid;
    }

    private static void AddSummaryRow(Grid grid, StatusKind kind, string label, string text)
    {
        grid.AddRow(
            new Markup(DashboardTheme.Dot(kind)),
            new Markup($"[bold]{Markup.Escape(label)}[/]"),
            new Markup($"[{DashboardTheme.Muted}]{Markup.Escape(text)}[/]"));
    }

    /// <summary>Build the numeric menu — 1..5 mapping to each detail view.</summary>
    private static IRenderable BuildHomeMenu(int menuIndex)
    {
        var entries = HomeMenuEntries;
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))  // selection bar
            .AddColumn(new GridColumn().NoWrap().Width(4))  // number
            .AddColumn(new GridColumn().NoWrap().Width(20)) // section name
            .AddColumn(new GridColumn().NoWrap());          // description

        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var selected = i == menuIndex;
            var nameMarkup = selected
                ? $"[bold {DashboardTheme.Brand}]{Markup.Escape(e.Name)}[/]"
                : $"[{DashboardTheme.Muted}]{Markup.Escape(e.Name)}[/]";
            var numberMarkup = selected
                ? $"[bold {DashboardTheme.Brand}]{Markup.Escape(e.Number)}[/]"
                : $"[{DashboardTheme.Muted}]{Markup.Escape(e.Number)}[/]";
            var descMarkup = $"[{DashboardTheme.Muted}]{Markup.Escape(e.Description)}[/]";
            grid.AddRow(
                SelectionMarker(selected),
                new Markup(numberMarkup),
                new Markup(nameMarkup),
                new Markup(descMarkup));
        }
        return grid;
    }

    /// <summary>Static menu entries — number, view, name, description.</summary>
    internal static readonly (string Number, DashboardView View, string Name, string Description)[] HomeMenuEntries =
    {
        ("1", DashboardView.Scopes,         "Scopes",          "Reindex, rebuild, inspect per scope"),
        ("2", DashboardView.Clients,        "Clients",         "Wire / unwire MCP clients"),
        ("3", DashboardView.Embeddings,     "Embeddings",      "Pull, verify the embedding cache"),
        ("4", DashboardView.RecentActivity, "Recent activity", "Live log of tool calls and heals"),
        ("5", DashboardView.Environment,    "Environment",     "Build / git / repo detail (read-only)"),
    };

    // ────────────────────────────────────────────────────────────────────────────────
    // Summary computations (home view)
    // ────────────────────────────────────────────────────────────────────────────────

    private static (StatusKind, string) ComputeEnvironmentSummary(DashboardSnapshot snapshot, DashboardRenderOptions options)
    {
        var env = snapshot.Environment;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(env.DotnetSdkVersion)) parts.Add(env.DotnetSdkVersion);
        if (env.GitOnPath) parts.Add("git");
        if (env.SolutionFiles.Count > 0)
        {
            parts.Add(Path.GetFileName(env.SolutionFiles[0]));
        }
        var text = parts.Count == 0 ? "(no environment detected)" : string.Join(" · ", parts);

        StatusKind kind;
        if (env.DotnetSdkVersion is null || env.SourceGraphConfigStatus == "malformed")
            kind = StatusKind.Fail;
        else if (!env.GitOnPath || env.SolutionFiles.Count == 0)
            kind = StatusKind.Warn;
        else
            kind = StatusKind.Ok;
        return (kind, text);
    }

    private static (StatusKind, string) ComputeScopesSummary(DashboardSnapshot snapshot)
    {
        var scopes = snapshot.Scopes;
        var total = scopes.Count;
        if (total == 0) return (StatusKind.Off, "(none)");

        // Single-pass histogram over the scope statuses. Cheaper than four LINQ Counts and
        // keeps the dispatch close to the labels.
        int ok = 0, partial = 0, indexing = 0, degraded = 0;
        foreach (var s in scopes)
        {
            switch (s.Status)
            {
                case "ok": ok++; break;
                case "partial": partial++; break;
                case "indexing": indexing++; break;
                case "degraded": degraded++; break;
            }
        }

        var bits = new List<string> { $"{total} total" };
        if (ok > 0) bits.Add($"{ok} ok");
        if (partial > 0) bits.Add($"{partial} partial");
        if (indexing > 0) bits.Add($"{indexing} indexing");
        if (degraded > 0) bits.Add($"{degraded} degraded");

        StatusKind kind;
        if (degraded > 0) kind = StatusKind.Fail;
        else if (partial > 0 || indexing > 0) kind = StatusKind.Warn;
        else kind = StatusKind.Ok;
        return (kind, string.Join("  ·  ", bits));
    }

    private static (StatusKind, string) ComputeClientsSummary(DashboardSnapshot snapshot)
    {
        var total = snapshot.Clients.Count;
        var wired = snapshot.Clients.Count(c => c.ContainsSourcegraphEntry);
        if (total == 0) return (StatusKind.Off, "0 / 0 wired");
        var kind = wired > 0 ? StatusKind.Ok : StatusKind.Off;
        return (kind, $"{wired} / {total} wired");
    }

    private static (StatusKind, string) ComputeEmbeddingsSummary(DashboardSnapshot snapshot)
    {
        var emb = snapshot.Embeddings;
        if (!emb.CachePresent)
        {
            return (StatusKind.Warn, $"{emb.ModelId}  ·  (absent)");
        }
        return (StatusKind.Ok, $"{emb.ModelId}  ·  {FormatBytes(emb.TotalBytes)}");
    }

    private static (StatusKind, string) ComputeRecentSummary(DashboardSnapshot snapshot)
    {
        var activity = snapshot.RecentActivity;
        if (activity.Count == 0) return (StatusKind.Off, "(no activity yet)");
        // Most recent entry leads the label.
        var latest = activity.OrderByDescending(a => a.Ts).First();
        var rel = FormatRelativeTime(DateTimeOffset.UtcNow - latest.Ts);
        var kind = latest.Ok ? StatusKind.Ok : StatusKind.Fail;
        var name = latest.Detail ?? latest.Kind;
        return (kind, $"{activity.Count} events  ·  last: {name} · {rel}");
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Detail views
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Environment detail view: read-only key/value table; no row selection.</summary>
    public static IRenderable BuildEnvironmentDetail(DashboardSnapshot snapshot, DashboardRenderOptions options)
    {
        var env = snapshot.Environment;
        var rows = new List<IRenderable> { new Markup("") };

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(3)) // dot
            .AddColumn(new GridColumn().NoWrap().Width(22)) // key
            .AddColumn(new GridColumn().NoWrap()); // value

        // .NET SDK
        AddEnvDetailRow(grid,
            env.DotnetSdkVersion is null ? StatusKind.Fail : StatusKind.Ok,
            ".NET SDK",
            env.DotnetSdkVersion ?? "(not detected)");
        AddEnvDetailRow(grid,
            env.GitOnPath ? StatusKind.Ok : StatusKind.Warn,
            "git on PATH",
            env.GitOnPath ? "yes" : "no");
        AddEnvDetailRow(grid,
            StatusKind.Ok,
            "repo root",
            PathDisplay.Render(env.RepoRootPath, options.Root, options.Home));
        var solutions = env.SolutionFiles.Count == 0
            ? "(none detected)"
            : $"{string.Join(", ", env.SolutionFiles.Select(p => PathDisplay.Render(p, options.Root, options.Home)))} ({env.SolutionFiles.Count} detected)";
        AddEnvDetailRow(grid,
            env.SolutionFiles.Count == 0 ? StatusKind.Warn : StatusKind.Ok,
            "solutions",
            solutions);
        var (cfgKind, cfgValue) = env.SourceGraphConfigStatus switch
        {
            "valid" => (StatusKind.Ok, "valid"),
            "missing" => (StatusKind.Ok, "missing (single-scope synth path)"),
            "malformed" => (StatusKind.Fail, $"MALFORMED — {env.SourceGraphConfigError}"),
            _ => (StatusKind.Warn, env.SourceGraphConfigStatus),
        };
        AddEnvDetailRow(grid, cfgKind, ".sourcegraph.json", cfgValue);

        rows.Add(grid);
        return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
    }

    private static void AddEnvDetailRow(Grid grid, StatusKind kind, string key, string value)
    {
        grid.AddRow(
            new Markup(DashboardTheme.Dot(kind)),
            new Markup($"[bold]{Markup.Escape(key)}[/]"),
            new Markup(Markup.Escape(value)));
    }

    /// <summary>The Scopes detail view: full per-scope table + Selected drawer + failed-projects bullets.</summary>
    public static IRenderable BuildScopesDetail(DashboardSnapshot snapshot, DashboardRenderOptions options,
        int selectedRow = 0, int maxVisibleRows = 8)
    {
        var rows = new List<IRenderable> { new Markup("") };
        if (snapshot.Scopes.Count == 0)
        {
            rows.Add(new Markup($"  {DashboardTheme.Dot(StatusKind.Off)} [{DashboardTheme.Muted}](no scopes registered — run `sourcegraph-mcp serve` once to materialise)[/]"));
            return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
        }

        // Column widths chosen to fit the 80-col minimum. Sum: 2 + 14 + 10 + 11 + 11 + 12 + 8 = 68
        // cells of content + 3 of padding = 71 cells. Leaves room for the body region's own
        // padding without triggering column collapse.
        const int nameW = 14, statusW = 10, symW = 11, refsW = 11, ageW = 12, failedW = 8;

        // Header row.
        var hcol = DashboardTheme.MutedDim;
        rows.Add(new Markup(string.Concat(
            Cell("", 2),
            Cell("Name", nameW, hcol),
            Cell("Status", statusW, hcol),
            Cell("Symbols", symW, hcol, rightAligned: true),
            Cell("Refs", refsW, hcol, rightAligned: true),
            Cell("Last indexed", ageW, hcol),
            Cell("Failed", failedW, hcol))));

        var (start, end, moreAbove, moreBelow) = Viewport(snapshot.Scopes.Count, selectedRow, maxVisibleRows);
        if (moreAbove > 0) rows.Add(new Markup($"  [{DashboardTheme.MutedDim}]↑ {moreAbove} more[/]"));
        for (var i = start; i < end; i++)
        {
            var s = snapshot.Scopes[i];
            var kind = MapScopeStatus(s.Status);
            var ageLabel = s.LastIndexedAt.HasValue
                ? FormatRelativeTime(DateTimeOffset.UtcNow - s.LastIndexedAt.Value)
                : "(never)";
            var failed = s.FailedProjects.Count > 0
                ? $"{s.FailedProjects.Count} proj{(s.FailedProjects.Count == 1 ? "" : "s")}"
                : "—";
            var selected = i == selectedRow;
            var nameColor = selected ? $"bold {DashboardTheme.Brand}" : "";

            var line = string.Concat(
                DashboardTheme.SelectionDot(selected) + " ",
                Cell(s.Name, nameW, nameColor),
                Cell(s.Status, statusW, DashboardTheme.ColorFor(kind)),
                Cell($"{s.SymbolCount:N0}", symW, rightAligned: true),
                Cell($"{s.ReferenceCount:N0}", refsW, rightAligned: true),
                Cell(ageLabel, ageW, DashboardTheme.Muted),
                Cell(failed, failedW, DashboardTheme.Muted));
            rows.Add(new Markup(line));
        }
        if (moreBelow > 0) rows.Add(new Markup($"  [{DashboardTheme.MutedDim}]↓ {moreBelow} more[/]"));

        // Inline separator + Selected drawer.
        if (selectedRow >= 0 && selectedRow < snapshot.Scopes.Count)
        {
            AppendDivider(rows);
            rows.Add(BuildScopeDrawer(snapshot.Scopes[selectedRow]));
        }
        return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
    }

    private static StatusKind MapScopeStatus(string status) => status switch
    {
        "ok" => StatusKind.Ok,
        "partial" => StatusKind.Warn,
        "degraded" => StatusKind.Fail,
        "indexing" => StatusKind.Warn,
        _ => StatusKind.Off,
    };

    private static IRenderable BuildScopeDrawer(ScopeRow scope)
    {
        var ageLabel = scope.LastIndexedAt.HasValue
            ? FormatRelativeTime(DateTimeOffset.UtcNow - scope.LastIndexedAt.Value)
            : "(never)";
        var statusText = scope.Status switch
        {
            "partial" when scope.FailedProjects.Count > 0 =>
                $"partial — {scope.FailedProjects.Count} project{(scope.FailedProjects.Count == 1 ? "" : "s")} failed",
            _ => scope.Status,
        };
        var fields = new List<(string Key, string Value)>
        {
            ("status", statusText),
            ("symbols", scope.SymbolCount.ToString("N0")),
            ("refs", scope.ReferenceCount.ToString("N0")),
            ("last", ageLabel),
            ("database", $".sourcegraph/scopes/{scope.Name}.db"),
        };
        if (scope.Isolated)
        {
            fields.Add(("isolated", "yes"));
        }
        var hasFailedProjects = scope.FailedProjects.Count > 0;
        return BuildSelectedDrawer(
            title: $"Selected: {scope.Name}",
            fields: fields,
            bullets: hasFailedProjects ? scope.FailedProjects : null,
            bulletsHeader: hasFailedProjects ? "Failed projects:" : null);
    }

    /// <summary>The Clients detail view: full per-client table + Selected drawer.</summary>
    public static IRenderable BuildClientsDetail(DashboardSnapshot snapshot, DashboardRenderOptions options,
        int selectedRow = 0, int maxVisibleRows = 8)
    {
        var rows = new List<IRenderable> { new Markup("") };
        if (snapshot.Clients.Count == 0)
        {
            rows.Add(new Markup($"  {DashboardTheme.Dot(StatusKind.Off)} [{DashboardTheme.Muted}](no client configs detected)[/]"));
            return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
        }

        var ordered = snapshot.Clients.OrderBy(c => c.Scope == "project" ? 0 : 1).ToArray();
        // Column widths chosen so total + padding fits comfortably inside the dashboard's
        // 80-col minimum width — 2 (selection) + 16 + 9 + 14 + 28 = 69 cells of content + 3
        // of left/right padding = 72. Path is truncated with an ellipsis if it exceeds 28 cells.
        const int slugW = 16, scopeW = 9, stateW = 14, pathW = 28;

        var (start, end, moreAbove, moreBelow) = Viewport(ordered.Length, selectedRow, maxVisibleRows);
        if (moreAbove > 0)
        {
            rows.Add(new Markup($"  [{DashboardTheme.MutedDim}]↑ {moreAbove} more[/]"));
        }
        for (var i = start; i < end; i++)
        {
            var c = ordered[i];
            var (kind, stateLabel) = c switch
            {
                { ContainsSourcegraphEntry: true } => (StatusKind.Ok, "wired"),
                { Exists: true } => (StatusKind.Off, "not wired"),
                _ => (StatusKind.Unsupported, "not present"),
            };
            var path = PathDisplay.Render(c.Path, options.Root, options.Home);
            var selected = i == selectedRow;
            var slugColor = selected ? $"bold {DashboardTheme.Brand}" : "";
            // Selection column: glyph already carries colour markup, just need a trailing space
            // to make the cell two cells wide.
            var line = string.Concat(
                DashboardTheme.SelectionDot(selected) + " ",
                Cell(c.Slug, slugW, slugColor),
                Cell(c.Scope, scopeW, DashboardTheme.Muted),
                Cell(stateLabel, stateW, DashboardTheme.ColorFor(kind)),
                Cell(path, pathW, DashboardTheme.Muted));
            rows.Add(new Markup(line));
        }
        if (moreBelow > 0)
        {
            rows.Add(new Markup($"  [{DashboardTheme.MutedDim}]↓ {moreBelow} more[/]"));
        }

        if (selectedRow >= 0 && selectedRow < ordered.Length)
        {
            AppendDivider(rows);
            rows.Add(BuildClientDrawer(ordered[selectedRow], options));
        }
        return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
    }

    private static IRenderable BuildClientDrawer(ClientRow client, DashboardRenderOptions options)
    {
        string state = client switch
        {
            { ContainsSourcegraphEntry: true } => "wired — entry present in config",
            { Exists: true } => "file exists but no sourcegraph entry — pressing ⏎ will wire it",
            _ => "file does not exist — pressing ⏎ will create it",
        };
        var target = PathDisplay.Render(client.Path, options.Root, options.Home);
        var fields = new List<(string Key, string Value)>
        {
            ("scope", client.Scope),
            ("target", target),
            ("state", state),
        };
        return BuildSelectedDrawer($"Selected: {client.Slug}", fields);
    }

    /// <summary>The Embeddings detail view: model identity + four headline values + hint.</summary>
    public static IRenderable BuildEmbeddingsDetail(DashboardSnapshot snapshot, DashboardRenderOptions options)
    {
        var emb = snapshot.Embeddings;
        var rows = new List<IRenderable> { new Markup("") };

        // Embeddings shows a single conceptual row (one model) so there's no selection cue —
        // the leading-indicator column is dropped entirely; the key column carries the label
        // and the value column carries the value.
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(14)) // key
            .AddColumn(new GridColumn().NoWrap());          // value

        var pairs = new (string Key, string Value)[]
        {
            ("Model", emb.ModelId),
            ("Cache", PathDisplay.Render(emb.CacheDir, options.Root, options.Home)),
            ("Size", emb.CachePresent ? FormatBytes(emb.TotalBytes) : "(absent)"),
            ("Verified", emb.Verified ? "yes" : "no"),
        };
        foreach (var (key, value) in pairs)
        {
            grid.AddRow(
                new Markup($"[bold]{Markup.Escape(key)}[/]"),
                new Markup(Markup.Escape(value)));
        }
        rows.Add(grid);

        rows.Add(new Markup(""));
        rows.Add(new Markup($"[{DashboardTheme.Muted}]  Tip: run `sourcegraph-mcp embeddings status` for the per-file view.[/]"));

        return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
    }

    /// <summary>The Recent activity detail view: scrollable log + Selected drawer.</summary>
    public static IRenderable BuildRecentActivityDetail(DashboardSnapshot snapshot, DashboardRenderOptions options,
        int selectedRow = 0, int maxRows = 24)
    {
        var rows = new List<IRenderable> { new Markup("") };
        if (snapshot.RecentActivity.Count == 0)
        {
            rows.Add(new Markup($"  {DashboardTheme.Dot(StatusKind.Off)} [{DashboardTheme.Muted}](no recorded activity)[/]"));
            return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
        }

        var rowsList = snapshot.RecentActivity
            .OrderByDescending(a => a.Ts)
            .Take(maxRows)
            .ToArray();

        // Column widths chosen to fit the 80-col minimum. Sum: 2 + 10 + 24 + 14 + 3 + 12 = 65
        // cells of content + 3 padding = 68. Detail column truncated to 24 with ellipsis if
        // longer. The status-dot column is 2 cells (DashboardTheme.Dot returns "● " etc.).
        const int timeW = 10, nameW = 24, scopeW = 14, msW = 12;

        var (start, end, moreAbove, moreBelow) = Viewport(rowsList.Length, selectedRow, maxRows);
        if (moreAbove > 0) rows.Add(new Markup($"  [{DashboardTheme.MutedDim}]↑ {moreAbove} more[/]"));
        for (var i = start; i < end; i++)
        {
            var a = rowsList[i];
            var kind = a.Ok ? StatusKind.Ok : StatusKind.Fail;
            var time = a.Ts.ToLocalTime().ToString("HH:mm:ss");
            var name = a.Detail ?? a.Kind;
            var scope = a.Scope ?? "-";
            var msLabel = a.Ok ? FormatMillis(a.Ms) : $"failed: {FormatMillis(a.Ms)}";
            var selected = i == selectedRow;
            var nameColor = selected ? $"bold {DashboardTheme.Brand}" : "";

            var line = string.Concat(
                DashboardTheme.SelectionDot(selected) + " ",
                Cell(time, timeW, DashboardTheme.Muted),
                Cell(name, nameW, nameColor),
                Cell(scope, scopeW, DashboardTheme.Muted),
                DashboardTheme.Dot(kind) + " ",
                Cell(msLabel, msW, DashboardTheme.Muted));
            rows.Add(new Markup(line));
        }
        if (moreBelow > 0) rows.Add(new Markup($"  [{DashboardTheme.MutedDim}]↓ {moreBelow} more[/]"));

        if (selectedRow >= 0 && selectedRow < rowsList.Length)
        {
            var sel = rowsList[selectedRow];
            AppendDivider(rows);
            var time = sel.Ts.ToLocalTime().ToString("HH:mm:ss");
            var name = sel.Detail ?? sel.Kind;
            var statusText = sel.Ok ? "ok" : "failed";
            var fields = new List<(string Key, string Value)>
            {
                ("scope", sel.Scope ?? "-"),
                ("status", statusText),
                ("duration", FormatMillis(sel.Ms)),
                ("detail", sel.Detail ?? "(none)"),
            };
            rows.Add(BuildSelectedDrawer($"Selected: {name} @ {time}", fields));
        }
        return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Selected: drawer helper
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the <c>Selected:</c> drawer that lives under each detail view's table. Renders the
    /// title in bold, the key/value fields aligned, and an optional bullet list (used by the
    /// Scopes view for failed-projects).
    /// </summary>
    private static IRenderable BuildSelectedDrawer(string title, IEnumerable<(string Key, string Value)> fields,
        IEnumerable<string>? bullets = null, string? bulletsHeader = null)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[bold {DashboardTheme.Brand}]{Markup.Escape(title)}[/]"),
        };
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))  // indent
            .AddColumn(new GridColumn().NoWrap().Width(12)) // key
            .AddColumn(new GridColumn().NoWrap());          // value
        foreach (var (k, v) in fields)
        {
            grid.AddRow(
                new Markup(" "),
                new Markup($"[{DashboardTheme.MutedDim}]{Markup.Escape(k)}[/]"),
                new Markup(Markup.Escape(v)));
        }
        rows.Add(grid);

        if (bullets is not null)
        {
            rows.Add(new Markup(""));
            if (!string.IsNullOrEmpty(bulletsHeader))
            {
                rows.Add(new Markup($"[{DashboardTheme.MutedDim}]{Markup.Escape(bulletsHeader)}[/]"));
            }
            foreach (var b in bullets)
            {
                rows.Add(new Markup($"  [{DashboardTheme.Muted}]•[/] {Markup.Escape(b)}"));
            }
        }
        return new Rows(rows);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Append a blank line + horizontal separator + blank line to <paramref name="rows"/>. Every
    /// detail view uses this exact pattern to visually divide the data table from the
    /// <c>Selected:</c> drawer, so factor it out to keep the layout consistent.
    /// </summary>
    private static void AppendDivider(List<IRenderable> rows)
    {
        rows.Add(new Markup(""));
        rows.Add(new Markup($"[{DashboardTheme.MutedDim}]{Markup.Escape(new string('─', 72))}[/]"));
        rows.Add(new Markup(""));
    }

    /// <summary>
    /// Render the leading selection-indicator cell. Selected rows get a brand-coloured fisheye
    /// <c>◉</c> (filled circle with built-in outline); non-selected rows get a muted hollow
    /// <c>○</c>. Replaces the prior <c>▌</c> bar-plus-status-dot pattern: under the new model,
    /// the leading dot communicates "you are here" and the row's status meaning lives in the
    /// colour of its status word. Honours <see cref="LeafFormatter.Suppressed"/>.
    /// </summary>
    private static Markup SelectionMarker(bool selected) => new(DashboardTheme.SelectionDot(selected));

    /// <summary>
    /// Build a fixed-width cell as a Markup-ready string. Truncates with an ellipsis when
    /// <paramref name="plain"/> is wider than <paramref name="width"/>, pads with spaces
    /// otherwise. The result is safe to concatenate with other cell strings into a single
    /// <see cref="Markup"/> line — sidestepping Spectre's <see cref="Grid"/> column
    /// negotiation entirely. The Grid approach mis-handled narrow body widths by squeezing
    /// columns down to 1-char width and then "no-wrapping" each character onto its own line
    /// (`p\nr\no\nj\ne\nc\nt`); pre-formatting bypasses that whole pathology.
    /// </summary>
    /// <param name="plain">Raw text — exactly what the user sees, no markup.</param>
    /// <param name="width">Total cell width in display cells.</param>
    /// <param name="colorTag">Optional Spectre colour tag (e.g. <c>"#5fa07a"</c> or <c>"grey50 bold"</c>); empty for default.</param>
    /// <param name="rightAligned">Pad to the LEFT of the content when true.</param>
    internal static string Cell(string plain, int width, string colorTag = "", bool rightAligned = false)
    {
        if (width <= 0) return string.Empty;
        string visible;
        if (plain.Length > width)
        {
            // Truncate with a single ellipsis. width == 1 collapses to just the ellipsis.
            visible = width == 1 ? "…" : plain[..(width - 1)] + "…";
        }
        else
        {
            visible = plain;
        }
        var padCount = Math.Max(0, width - visible.Length);
        var pad = padCount == 0 ? string.Empty : new string(' ', padCount);
        var escaped = Markup.Escape(visible);
        var inner = string.IsNullOrEmpty(colorTag) ? escaped : $"[{colorTag}]{escaped}[/]";
        return rightAligned ? pad + inner : inner + pad;
    }

    /// <summary>
    /// Compute a viewport over <paramref name="totalRows"/> that keeps
    /// <paramref name="selectedRow"/> visible within <paramref name="maxVisible"/> rows.
    /// Returns the [start, end) indices to render plus the "more above"/"more below" counts
    /// the caller renders as hint rows. When the list fits, start=0 and end=totalRows.
    /// </summary>
    internal static (int Start, int End, int MoreAbove, int MoreBelow) Viewport(
        int totalRows, int selectedRow, int maxVisible)
    {
        if (totalRows <= maxVisible) return (0, totalRows, 0, 0);
        if (maxVisible <= 0) return (0, 0, 0, totalRows);
        // Keep selected near the centre; clamp at list ends so the slice always has maxVisible rows.
        var half = maxVisible / 2;
        var start = Math.Max(0, Math.Min(selectedRow - half, totalRows - maxVisible));
        var end = start + maxVisible;
        return (start, end, start, totalRows - end);
    }

    /// <summary>Operator-friendly relative time like <c>2m ago</c> / <c>3h ago</c>; matches StatusRenderer.</summary>
    internal static string FormatRelativeTime(TimeSpan delta)
    {
        if (delta.TotalSeconds < 60) return $"{(int)Math.Max(0, delta.TotalSeconds)}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 48) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    /// <summary>Operator-friendly latency: <c>42 ms</c>, <c>1.2 s</c>.</summary>
    private static string FormatMillis(int ms)
    {
        if (ms < 1000) return $"{ms} ms";
        return $"{ms / 1000.0:F1} s";
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
