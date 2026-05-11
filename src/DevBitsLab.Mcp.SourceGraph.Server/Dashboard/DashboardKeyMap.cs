namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Central key-binding table for the dashboard. One static lookup that every keystroke runs
/// through; aliases (<c>j</c> ≡ <c>↓</c>, <c>k</c> ≡ <c>↑</c>, <c>h</c> ≡ <c>Esc</c>) are
/// explicit table entries so unit tests can assert the documented surface exhaustively.
///
/// <para>
/// The home/detail-view rewrite changed the navigation model: <c>Tab</c> is no longer in service
/// (no section cycle). <c>Esc</c> / <c>h</c> return to home; <c>1..5</c> jump from the home
/// menu directly into a detail view. Section action keys (<c>r</c>, <c>R</c>, <c>w</c>, <c>u</c>,
/// <c>p</c>, <c>v</c>) only resolve their work in the matching detail view; the dispatcher
/// gates them silently in non-matching views.
/// </para>
///
/// <para>
/// Unmapped keys return <c>false</c> with <see cref="DashboardAction.None"/>. The dispatcher
/// drops these silently — TUIs that beep on every unknown keystroke get annoying fast.
/// </para>
/// </summary>
internal static class DashboardKeyMap
{
    /// <summary>
    /// Resolve a key press to a dashboard action. Returns <c>true</c> iff the key is bound to
    /// something other than <see cref="DashboardAction.None"/>.
    /// </summary>
    public static bool TryResolve(ConsoleKeyInfo key, out DashboardAction action)
    {
        action = Resolve(key);
        return action != DashboardAction.None;
    }

    private static DashboardAction Resolve(ConsoleKeyInfo key)
    {
        // Ctrl+C → Quit (same as `q`). Spectre's Live block surfaces Ctrl+C via the cancellation
        // token; we honour it here as a defensive belt-and-suspenders so a Ctrl+C arriving
        // through Console.ReadKey also exits cleanly.
        if ((key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control)
        {
            return key.Key switch
            {
                ConsoleKey.C => DashboardAction.Quit,
                _ => DashboardAction.None,
            };
        }

        // Arrow keys + Enter + Esc.
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: return DashboardAction.MoveUp;
            case ConsoleKey.DownArrow: return DashboardAction.MoveDown;
            case ConsoleKey.Enter: return DashboardAction.PrimaryAction;
            case ConsoleKey.Escape: return DashboardAction.GoHome;
            case ConsoleKey.Spacebar: return DashboardAction.None; // reserved
            // Tab keys are no longer bound — the section cycle is gone, replaced by the
            // home-view menu + view-aware navigation.
        }

        // Letter accelerators are case-sensitive: `r` reindex vs `R` rebuild differ. KeyChar is
        // the resolved character (with shift applied); we dispatch on it directly. Unknown
        // letters fall through to None.
        return key.KeyChar switch
        {
            // Read-only
            'q' or 'Q' => DashboardAction.Quit,
            '?' => DashboardAction.ToggleHelp,
            's' => DashboardAction.ForceRefresh,
            'j' => DashboardAction.MoveDown,
            'k' => DashboardAction.MoveUp,
            // 'h' is the documented vim-style alias for Esc → home.
            'h' => DashboardAction.GoHome,

            // Number keys: direct jumps to a detail view. Only meaningful from home but harmless
            // from any view — the dispatcher treats them as view transitions regardless.
            '1' => DashboardAction.OpenScopes,
            '2' => DashboardAction.OpenClients,
            '3' => DashboardAction.OpenEmbeddings,
            '4' => DashboardAction.OpenRecentActivity,
            '5' => DashboardAction.OpenEnvironment,

            // In-place (no gate). The dispatcher gates each to its matching view at run time.
            'r' => DashboardAction.ReindexScope,
            'w' => DashboardAction.WireClient,
            'p' => DashboardAction.EmbeddingsPull,
            'v' => DashboardAction.EmbeddingsVerify,

            // In-place (gate)
            'R' => DashboardAction.RebuildScope,
            'u' => DashboardAction.UnwireClient,

            // Guided
            'i' => DashboardAction.InitGuided,
            'd' => DashboardAction.DemoGuided,
            'l' => DashboardAction.OpenLogInPager,
            'e' => DashboardAction.OpenConfigInEditor,

            _ => DashboardAction.None,
        };
    }

    /// <summary>
    /// Per-view footer hint. The home view shows its own menu-style hint; each detail view shows
    /// only the keys that actually do something in its scope.
    ///
    /// <para>
    /// Returns Spectre markup with key glyphs in <see cref="DashboardTheme.Brand"/> and labels in
    /// <see cref="DashboardTheme.Muted"/>. Honours <see cref="Tools.LeafFormatter.Suppressed"/>:
    /// in the no-leaf path unicode glyphs (<c>⏎</c>, <c>↑↓</c>) are replaced with bracketed
    /// ASCII tokens (<c>[Enter]</c>, <c>[Up/Dn]</c>).
    /// </para>
    /// </summary>
    public static string For(DashboardView view)
    {
        if (Tools.LeafFormatter.Suppressed)
        {
            return view switch
            {
                DashboardView.Home =>
                    "[Up/Dn] select   [Enter] open   1-5 jump   [?] help   [q] quit",
                DashboardView.Scopes =>
                    "[Up/Dn] row   [Enter]/[r] reindex   [R] rebuild   [d] demo   [Esc] home   [q] quit",
                DashboardView.Clients =>
                    "[Up/Dn] row   [Enter] toggle wire   [w] wire   [u] unwire   [Esc] home   [q] quit",
                DashboardView.Embeddings =>
                    "[Enter]/[p] pull   [v] verify   [Esc] home   [q] quit",
                DashboardView.RecentActivity =>
                    "[Up/Dn] row   [Enter] details (TODO)   [l] open full log   [Esc] home   [q] quit",
                DashboardView.Environment =>
                    "[Esc] home   [q] quit",
                _ => "[Esc] home   [q] quit",
            };
        }

        var b = DashboardTheme.Brand;
        var m = DashboardTheme.Muted;
        return view switch
        {
            DashboardView.Home =>
                $"[{b}]↑↓[/] [{m}]select[/]   [{b}]⏎[/] [{m}]open[/]   [{b}]1-5[/] [{m}]jump[/]   [{b}]?[/] [{m}]help[/]   [{b}]q[/] [{m}]quit[/]",
            DashboardView.Scopes =>
                $"[{b}]↑↓[/] [{m}]row[/]   [{b}]⏎/r[/] [{m}]reindex[/]   [{b}]R[/] [{m}]rebuild[/]   [{b}]d[/] [{m}]demo[/]   [{b}]Esc[/] [{m}]home[/]   [{b}]q[/] [{m}]quit[/]",
            DashboardView.Clients =>
                $"[{b}]↑↓[/] [{m}]row[/]   [{b}]⏎[/] [{m}]toggle wire[/]   [{b}]w[/] [{m}]wire[/]   [{b}]u[/] [{m}]unwire[/]   [{b}]Esc[/] [{m}]home[/]   [{b}]q[/] [{m}]quit[/]",
            DashboardView.Embeddings =>
                $"[{b}]⏎/p[/] [{m}]pull[/]   [{b}]v[/] [{m}]verify[/]   [{b}]Esc[/] [{m}]home[/]   [{b}]q[/] [{m}]quit[/]",
            DashboardView.RecentActivity =>
                $"[{b}]↑↓[/] [{m}]row[/]   [{b}]⏎[/] [{m}]details (TODO)[/]   [{b}]l[/] [{m}]open full log[/]   [{b}]Esc[/] [{m}]home[/]   [{b}]q[/] [{m}]quit[/]",
            DashboardView.Environment =>
                $"[{b}]Esc[/] [{m}]home[/]   [{b}]q[/] [{m}]quit[/]",
            _ => $"[{b}]Esc[/] [{m}]home[/]   [{b}]q[/] [{m}]quit[/]",
        };
    }

    /// <summary>
    /// Multi-line key reference shown by the inline help overlay (`?`). Same vocabulary as the
    /// dashboard subcommand's man-page section; centralised here so docs and runtime can't drift.
    /// </summary>
    public const string HelpText = """
        Navigation:
          ↑/↓ or j/k      Move row within current view
          1-5             Jump from Home into a detail view
          Enter           Primary action (open menu item / section primary)
          Esc or h        Return to Home
          q / Ctrl+C      Quit (exit 0)
          ?               Toggle this help
          s               Force refresh snapshot now

        Section actions (only effective in the matching detail view):
          r               Reindex selected scope (reconcile_drift)
          R               Rebuild selected scope (CONFIRM)
          w               Wire missing client
          u               Unwire selected client (CONFIRM)
          p               Embeddings pull (active model)
          v               Embeddings verify

        Guided actions (suspend + subprocess):
          i               Run `sourcegraph-mcp init`
          d               Run `sourcegraph-mcp demo` for selected scope
          l               Open usage.jsonl in $PAGER
          e               Open .sourcegraph.json in $EDITOR
        """;
}
