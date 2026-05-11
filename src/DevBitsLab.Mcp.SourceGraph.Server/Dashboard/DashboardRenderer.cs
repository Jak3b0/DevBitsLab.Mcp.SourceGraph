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
/// Per-section <see cref="IRenderable"/> builders for the dashboard. Built around the
/// <see cref="DashboardTheme"/> palette + glyph vocabulary — borderless, dot-driven, with a
/// focused-section saturation gradient (focused = full colour; unfocused = dimmed body and
/// header).
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
    /// <summary>The dashboard header: brand leaf + bold name + dimmed path · version.</summary>
    public static IRenderable BuildHeader(DashboardSnapshot snapshot, DashboardRenderOptions options)
    {
        var leaf = LeafFormatter.Suppressed ? "[x]" : DashboardTheme.BrandLeaf;
        var leafText = Markup.Escape(leaf);
        var version = Markup.Escape(options.Version);
        var rendered = Markup.Escape(PathDisplay.Render(options.Root, options.Root, options.Home));
        var brand = DashboardTheme.Brand;
        var muted = DashboardTheme.Muted;

        var headerLine = $"{leafText} [bold {brand}]SourceGraph[/]   [{muted}]{rendered} · v{version}[/]";
        var separator = $"[{DashboardTheme.MutedDim}]{Markup.Escape(new string('─', 72))}[/]";

        var rows = new Rows(new Markup(headerLine), new Markup(separator));
        return new Padder(rows).PadLeft(1).PadRight(1).PadTop(0).PadBottom(0);
    }

    /// <summary>The two-row footer: key hint + toast row. Toast colour follows severity.</summary>
    public static IRenderable BuildFooter(DashboardCli.ToastState toast = default)
    {
        var separator = $"[{DashboardTheme.MutedDim}]{Markup.Escape(new string('─', 72))}[/]";

        // Two-row footer hint. FooterHint embeds Spectre markup (or ASCII brackets under
        // --no-leaf); pass through verbatim.
        var hintMarkup = new Markup(DashboardKeyMap.FooterHint);

        IRenderable toastRow = string.IsNullOrEmpty(toast.Text)
            ? new Markup(" ") // keeps the row height stable
            : new Markup(RenderToast(toast));

        var rows = new Rows(new Markup(separator), hintMarkup, new Markup(""), toastRow);
        return new Padder(rows).PadLeft(1).PadRight(1).PadTop(0).PadBottom(0);
    }

    /// <summary>
    /// Convenience overload that lets old callers pass a bare message; severity defaults to
    /// <see cref="ToastSeverity.Info"/>. Used by tests and any caller that doesn't have a
    /// <see cref="DashboardCli.ToastState"/> handy.
    /// </summary>
    public static IRenderable BuildFooter(string statusMessage)
    {
        var toast = string.IsNullOrEmpty(statusMessage)
            ? default
            : new DashboardCli.ToastState(statusMessage, DateTimeOffset.UtcNow, ToastSeverity.Info);
        return BuildFooter(toast);
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

    /// <summary>The Environment section: SDK / git / repo root / solutions / .sourcegraph.json. Never focusable.</summary>
    public static IRenderable BuildEnvironmentSection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false)
    {
        var env = snapshot.Environment;
        // Environment's right-aligned slot is a read-only annotation, not a key hint. Style it as
        // muted text so it reads as a label rather than an action key.
        var rows = new List<IRenderable>
        {
            BuildSectionHeader("Environment", contextKeys: $"[{DashboardTheme.Muted}]read-only[/]", focused: focused),
        };

        // .NET SDK
        rows.Add(BuildEnvRow(
            kind: env.DotnetSdkVersion is null ? StatusKind.Unsupported : StatusKind.Ok,
            key: ".NET SDK",
            value: env.DotnetSdkVersion ?? "(not detected)",
            focused: focused));
        // git
        rows.Add(BuildEnvRow(
            kind: env.GitOnPath ? StatusKind.Ok : StatusKind.Warn,
            key: "git",
            value: env.GitOnPath ? "on PATH" : "not on PATH",
            focused: focused));
        // repo root
        rows.Add(BuildEnvRow(
            kind: StatusKind.Ok,
            key: "repo root",
            value: PathDisplay.Render(env.RepoRootPath, options.Root, options.Home),
            focused: focused));
        // solution
        var solutions = env.SolutionFiles.Count == 0
            ? "(none)"
            : string.Join(", ", env.SolutionFiles.Select(p => PathDisplay.Render(p, options.Root, options.Home)));
        var solutionDetail = env.SolutionFiles.Count switch
        {
            0 => "",
            1 => "1 detected",
            _ => $"{env.SolutionFiles.Count} detected",
        };
        rows.Add(BuildEnvRow(
            kind: env.SolutionFiles.Count == 0 ? StatusKind.Warn : StatusKind.Ok,
            key: "solution",
            value: solutions,
            trailing: solutionDetail,
            focused: focused));
        // .sourcegraph.json
        var (cfgKind, cfgValue) = env.SourceGraphConfigStatus switch
        {
            "valid" => (StatusKind.Ok, "valid"),
            "missing" => (StatusKind.Ok, "auto (single-scope)"),
            "malformed" => (StatusKind.Fail, $"MALFORMED — {env.SourceGraphConfigError}"),
            _ => (StatusKind.Warn, env.SourceGraphConfigStatus),
        };
        rows.Add(BuildEnvRow(cfgKind, ".sourcegraph.json", cfgValue, focused: focused));

        rows.Add(new Markup("")); // trailing blank row separates from the next section
        return new Padder(new Rows(rows)).PadLeft(2).PadRight(1);
    }

    /// <summary>The Scopes section: one row per registered scope.</summary>
    public static IRenderable BuildScopesSection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false, int selectedRow = 0)
    {
        var contextKeys = BuildContextKeys(("⏎", "reindex"), ("⇧R", "rebuild"));
        var header = BuildSectionHeader("Scopes", contextKeys: contextKeys, focused: focused,
            subtitle: SelectedScopeName(snapshot, focused, selectedRow));
        if (snapshot.Scopes.Count == 0)
        {
            var empty = new Markup($"  {DashboardTheme.Dot(StatusKind.Off)} [{DashboardTheme.Muted}](no scopes registered — run `sourcegraph-mcp serve` once to materialise)[/]");
            return ComposeSection(header, new Rows(empty));
        }

        var rows = new List<IRenderable>();
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))  // selection bar
            .AddColumn(new GridColumn().NoWrap().Width(14)) // name
            .AddColumn(new GridColumn().NoWrap().Width(3))  // dot
            .AddColumn(new GridColumn().NoWrap().Width(11)) // status text
            .AddColumn(new GridColumn().NoWrap().Width(15).RightAligned()) // symbol count
            .AddColumn(new GridColumn().NoWrap().Width(13).RightAligned()) // ref count
            .AddColumn(new GridColumn().NoWrap()); // age / detail

        for (var i = 0; i < snapshot.Scopes.Count; i++)
        {
            var s = snapshot.Scopes[i];
            var kind = s.Status switch
            {
                "ok" => StatusKind.Ok,
                "partial" => StatusKind.Warn,
                "degraded" => StatusKind.Fail,
                "indexing" => StatusKind.Warn,
                _ => StatusKind.Off,
            };
            var ageLabel = s.LastIndexedAt.HasValue
                ? FormatRelativeTime(DateTimeOffset.UtcNow - s.LastIndexedAt.Value)
                : "(never)";
            var failedSuffix = s.FailedProjects.Count > 0 ? $" · {s.FailedProjects.Count} failed" : "";
            var isolatedSuffix = s.Isolated ? " · isolated" : "";
            var detail = $"{ageLabel}{failedSuffix}{isolatedSuffix}";

            var isSelected = focused && i == selectedRow;
            grid.AddRow(
                SelectionMarker(isSelected),
                ColourCell(Markup.Escape(s.Name), focused, bold: isSelected),
                new Markup(DashboardTheme.Dot(kind)),
                ColourCell(Markup.Escape(s.Status), focused),
                ColourCell(Markup.Escape($"{s.SymbolCount:N0} symbols"), focused),
                ColourCell(Markup.Escape($"{s.ReferenceCount:N0} refs"), focused),
                ColourCell(Markup.Escape(detail), focused, secondary: true));
        }
        rows.Add(grid);
        rows.Add(new Markup(""));
        return ComposeSection(header, new Rows(rows));
    }

    private static string? SelectedScopeName(DashboardSnapshot snap, bool focused, int selectedRow)
    {
        if (!focused || snap.Scopes.Count == 0) return null;
        if (selectedRow < 0 || selectedRow >= snap.Scopes.Count) return null;
        return snap.Scopes[selectedRow].Name;
    }

    /// <summary>The Clients section: one row per detected MCP client config.</summary>
    public static IRenderable BuildClientsSection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false, int selectedRow = 0)
    {
        var contextKeys = BuildContextKeys(("⏎", "toggle"), ("u", "unwire"));
        var header = BuildSectionHeader("Clients", contextKeys: contextKeys, focused: focused);
        if (snapshot.Clients.Count == 0)
        {
            var empty = new Markup($"  {DashboardTheme.Dot(StatusKind.Off)} [{DashboardTheme.Muted}](no client configs detected)[/]");
            return ComposeSection(header, new Rows(empty));
        }
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))  // selection bar
            .AddColumn(new GridColumn().NoWrap().Width(16)) // slug
            .AddColumn(new GridColumn().NoWrap().Width(10)) // scope
            .AddColumn(new GridColumn().NoWrap().Width(3))  // dot
            .AddColumn(new GridColumn().NoWrap());          // path / detail
        var ordered = snapshot.Clients.OrderBy(c => c.Scope == "project" ? 0 : 1).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var c = ordered[i];
            var kind = c switch
            {
                { ContainsSourcegraphEntry: true } => StatusKind.Ok,
                { Exists: true } => StatusKind.Off,
                _ => StatusKind.Unsupported,
            };
            var detail = c.ContainsSourcegraphEntry
                ? PathDisplay.Render(c.Path, options.Root, options.Home)
                : (c.Exists ? "not wired" : "not present");
            var isSelected = focused && i == selectedRow;
            grid.AddRow(
                SelectionMarker(isSelected),
                ColourCell(Markup.Escape(c.Slug), focused, bold: isSelected),
                ColourCell(Markup.Escape(c.Scope), focused, secondary: true),
                new Markup(DashboardTheme.Dot(kind)),
                ColourCell(Markup.Escape(detail), focused, secondary: !c.ContainsSourcegraphEntry));
        }
        var rows = new List<IRenderable> { grid, new Markup("") };
        return ComposeSection(header, new Rows(rows));
    }

    /// <summary>The Embeddings section: one row with model id + cache size + verified flag.</summary>
    public static IRenderable BuildEmbeddingsSection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false)
    {
        var emb = snapshot.Embeddings;
        var contextKeys = BuildContextKeys(("⏎", "pull"), ("v", "verify"));
        var header = BuildSectionHeader("Embeddings", contextKeys: contextKeys, focused: focused);
        var kind = emb.CachePresent ? StatusKind.Ok : StatusKind.Warn;
        var verifiedLabel = emb.Verified ? "verified" : "unverified";
        var sizeLabel = emb.CachePresent ? FormatBytes(emb.TotalBytes) : "(absent)";

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))  // bar / blank
            .AddColumn(new GridColumn().NoWrap().Width(3))  // dot
            .AddColumn(new GridColumn().NoWrap().Width(40)) // model
            .AddColumn(new GridColumn().NoWrap().Width(10).RightAligned()) // size
            .AddColumn(new GridColumn().NoWrap()); // verified flag
        grid.AddRow(
            // The single Embeddings row gets a selection-mirroring slot for consistency but
            // there's only one row; we mark it bar-on if the section is focused so the eye lands
            // on it the same way as the Scopes / Clients sections.
            SelectionMarker(focused),
            new Markup(DashboardTheme.Dot(kind)),
            ColourCell(Markup.Escape(emb.ModelId), focused, bold: focused),
            ColourCell(Markup.Escape(sizeLabel), focused),
            ColourCell(Markup.Escape(verifiedLabel), focused, secondary: true));
        var rows = new List<IRenderable> { grid, new Markup("") };
        return ComposeSection(header, new Rows(rows));
    }

    /// <summary>The Recent activity section: one row per tool call / heal entry.</summary>
    public static IRenderable BuildRecentActivitySection(DashboardSnapshot snapshot, DashboardRenderOptions options,
        bool focused = false, int selectedRow = 0, int maxRows = 12)
    {
        var contextKeys = BuildContextKeys(("l", "full log"));
        var header = BuildSectionHeader("Recent activity", contextKeys: contextKeys, focused: focused);
        if (snapshot.RecentActivity.Count == 0)
        {
            var empty = new Markup($"  {DashboardTheme.Dot(StatusKind.Off)} [{DashboardTheme.Muted}](no recorded activity)[/]");
            return ComposeSection(header, new Rows(empty));
        }
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))  // bar
            .AddColumn(new GridColumn().NoWrap().Width(10)) // time
            .AddColumn(new GridColumn().NoWrap().Width(22)) // detail / kind
            .AddColumn(new GridColumn().NoWrap().Width(14)) // scope
            .AddColumn(new GridColumn().NoWrap().Width(3))  // dot
            .AddColumn(new GridColumn().NoWrap()); // ms / annotation
        var rowsList = snapshot.RecentActivity
            .OrderByDescending(a => a.Ts)
            .Take(maxRows)
            .ToArray();
        for (var i = 0; i < rowsList.Length; i++)
        {
            var a = rowsList[i];
            var kind = a.Ok ? StatusKind.Ok : StatusKind.Fail;
            var time = a.Ts.ToLocalTime().ToString("HH:mm:ss");
            var name = a.Detail ?? a.Kind;
            var scope = a.Scope ?? "-";
            var msLabel = a.Ok ? FormatMillis(a.Ms) : $"failed: {FormatMillis(a.Ms)}";
            var isSelected = focused && i == selectedRow;
            grid.AddRow(
                SelectionMarker(isSelected),
                ColourCell(Markup.Escape(time), focused, secondary: true),
                ColourCell(Markup.Escape(name), focused, bold: isSelected),
                ColourCell(Markup.Escape(scope), focused, secondary: true),
                new Markup(DashboardTheme.Dot(kind)),
                ColourCell(Markup.Escape(msLabel), focused, secondary: true));
        }
        var rows = new List<IRenderable> { grid, new Markup("") };
        return ComposeSection(header, new Rows(rows));
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Layout / styling helpers
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compose a section header (one line) with a body (a stack of rows). Body is indented two
    /// spaces relative to the header so the section reads as a single visual block in the
    /// borderless layout.
    /// </summary>
    private static IRenderable ComposeSection(IRenderable header, IRenderable body)
    {
        var indentedBody = new Padder(body).PadLeft(2).PadRight(1);
        return new Rows(header, indentedBody);
    }

    /// <summary>
    /// Single section-header line: <c>◆ <b>Title</b> ─ subtitle … keys</c>. Renders dimmed when
    /// the section isn't focused so the eye is drawn to whichever section the user is steering.
    /// </summary>
    private static IRenderable BuildSectionHeader(string title, string contextKeys = "", bool focused = false,
        string? subtitle = null)
    {
        var leaderColour = focused ? DashboardTheme.Brand : DashboardTheme.BrandDim;
        var titleColour = focused ? DashboardTheme.Brand : DashboardTheme.Muted;
        var subtitleSuffix = string.IsNullOrEmpty(subtitle)
            ? ""
            : $" [{DashboardTheme.Muted}]─ {Markup.Escape(subtitle)}[/]";
        var leftCell = $"[{leaderColour}]{DashboardTheme.SectionLeader}[/] [bold {titleColour}]{Markup.Escape(title)}[/]{subtitleSuffix}";
        var rightCell = string.IsNullOrEmpty(contextKeys)
            ? ""
            : (focused
                ? contextKeys // contextKeys carries its own brand+muted markup
                : $"[{DashboardTheme.MutedDim}]{Markup.Escape(StripMarkup(contextKeys))}[/]");

        // Two-cell grid: left expands, right hugs.
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap().RightAligned());
        grid.AddRow(new Markup(leftCell), new Markup(rightCell));
        return grid;
    }

    /// <summary>
    /// Strip Spectre markup from a string. Used only for the unfocused-section context-keys hint:
    /// since we dim the whole thing in <see cref="DashboardTheme.MutedDim"/> we can't leave the
    /// per-token <c>[brand]…[/]</c> wrapping in place (Spectre would render the brand inside the
    /// dim wrapper). Simple regex-free walker matching <c>[…]</c>/<c>[/]</c> tokens.
    /// </summary>
    private static string StripMarkup(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '[')
            {
                var close = s.IndexOf(']', i);
                if (close < 0) break; // malformed; bail
                i = close + 1;
                continue;
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build the "context keys" right-aligned hint for a section header — comma separated
    /// <c>(glyph, label)</c> pairs. Glyphs go in brand colour, labels in muted grey.
    /// </summary>
    private static string BuildContextKeys(params (string Glyph, string Label)[] pairs)
    {
        if (pairs.Length == 0) return "";
        var b = DashboardTheme.Brand;
        var m = DashboardTheme.Muted;
        return string.Join("   ", pairs.Select(p =>
        {
            var glyph = LeafFormatter.Suppressed
                ? AsciiKeyHint(p.Glyph)
                : p.Glyph;
            return $"[{b}]{Markup.Escape(glyph)}[/] [{m}]{Markup.Escape(p.Label)}[/]";
        }));
    }

    /// <summary>Translate the unicode key glyph used in markup to an ASCII fallback for --no-leaf.</summary>
    private static string AsciiKeyHint(string glyph) => glyph switch
    {
        "⏎" => "[Enter]",
        "⇥" => "[Tab]",
        "↑↓" => "[Up/Dn]",
        "⇧R" => "[Shift+R]",
        _ => glyph,
    };

    /// <summary>
    /// Render the left-cursor column. A selected row shows a brand-coloured vertical bar; other
    /// rows show a single space (keeps column alignment). Honours <see cref="LeafFormatter.Suppressed"/>.
    /// </summary>
    private static Markup SelectionMarker(bool selected)
    {
        if (!selected) return new Markup(DashboardTheme.Unselected);
        if (LeafFormatter.Suppressed) return new Markup(">");
        return new Markup($"[{DashboardTheme.Brand}]{DashboardTheme.SelectedBar}[/]");
    }

    /// <summary>
    /// Wrap pre-escaped text in the right colour for a row cell:
    /// <list type="bullet">
    /// <item>focused, primary: default terminal colour (no wrapper)</item>
    /// <item>focused, bold: brand-bold</item>
    /// <item>focused, secondary: muted grey</item>
    /// <item>unfocused: all cells render in muted grey so the eye is drawn to the focused section</item>
    /// </list>
    /// </summary>
    private static Markup ColourCell(string escapedText, bool focused, bool bold = false, bool secondary = false)
    {
        if (!focused)
        {
            return new Markup($"[{DashboardTheme.Muted}]{escapedText}[/]");
        }
        if (bold)
        {
            return new Markup($"[bold {DashboardTheme.Brand}]{escapedText}[/]");
        }
        if (secondary)
        {
            return new Markup($"[{DashboardTheme.Muted}]{escapedText}[/]");
        }
        return new Markup(escapedText);
    }

    /// <summary>Build one Environment-style key/value row.</summary>
    private static IRenderable BuildEnvRow(StatusKind kind, string key, string value, string trailing = "", bool focused = false)
    {
        // Environment is always unfocused — body and key labels render in muted grey.
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2)) // blank cursor column
            .AddColumn(new GridColumn().NoWrap().Width(3)) // dot
            .AddColumn(new GridColumn().NoWrap().Width(20)) // key
            .AddColumn(new GridColumn().NoWrap().Width(38)) // value
            .AddColumn(new GridColumn().NoWrap()); // trailing detail
        var keyMarkup = $"[{DashboardTheme.Muted}]{Markup.Escape(key)}[/]";
        var valueMarkup = focused ? Markup.Escape(value) : $"[{DashboardTheme.Muted}]{Markup.Escape(value)}[/]";
        var trailingMarkup = string.IsNullOrEmpty(trailing) ? "" : $"[{DashboardTheme.MutedDim}]{Markup.Escape(trailing)}[/]";
        grid.AddRow(
            new Markup(DashboardTheme.Unselected),
            new Markup(DashboardTheme.Dot(kind)),
            new Markup(keyMarkup),
            new Markup(valueMarkup),
            new Markup(trailingMarkup));
        return grid;
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
