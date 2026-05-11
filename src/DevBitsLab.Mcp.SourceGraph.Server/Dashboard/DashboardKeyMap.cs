namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Central key-binding table for the dashboard, per design Decision 3. One static lookup that
/// every keystroke runs through; aliases (<c>j</c> ≡ <c>↓</c>, <c>k</c> ≡ <c>↑</c>) are
/// explicit table entries so unit tests can assert the documented surface exhaustively.
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

        // Arrow keys + Tab.
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: return DashboardAction.MoveUp;
            case ConsoleKey.DownArrow: return DashboardAction.MoveDown;
            case ConsoleKey.Tab:
                return (key.Modifiers & ConsoleModifiers.Shift) == ConsoleModifiers.Shift
                    ? DashboardAction.PreviousSection
                    : DashboardAction.NextSection;
            case ConsoleKey.Enter: return DashboardAction.PrimaryAction;
            case ConsoleKey.Escape: return DashboardAction.CloseDetail;
            case ConsoleKey.Spacebar: return DashboardAction.None; // reserved
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

            // In-place (no gate)
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
    /// Two-line key hint for the footer. Primary keys on top (always visible); secondary in-place
    /// + guided keys on the second line so a first-time user finds them without pressing <c>?</c>.
    ///
    /// <para>
    /// Returns Spectre markup with key glyphs in <see cref="DashboardTheme.Brand"/> and labels in
    /// <see cref="DashboardTheme.Muted"/>. Honours <see cref="Tools.LeafFormatter.Suppressed"/>:
    /// in the no-leaf path the unicode glyphs (<c>⏎</c>, <c>⇥</c>, <c>↑↓</c>, <c>⇧R</c>) are
    /// replaced with bracketed ASCII tokens (<c>[Enter]</c>, <c>[Tab]</c>, <c>[Up/Dn]</c>,
    /// <c>[Shift+R]</c>) so terminals without nerd-font support still read cleanly.
    /// </para>
    /// </summary>
    public static string FooterHint
    {
        get
        {
            if (Tools.LeafFormatter.Suppressed)
            {
                const string row1 = "[Enter] act   [Tab] section   [Up/Dn] row   [q] quit   [?] more";
                const string row2 = "[w] wire  [u] unwire  [r] reindex  [Shift+R] rebuild  [p] pull  [v] verify  [i] init  [d] demo  [l] log  [e] config";
                return row1 + "\n" + row2;
            }
            // Markup: keys in brand, labels in muted grey. \n splits the two rows; Spectre's
            // Markup parser preserves the newline so the renderer can lay it out as two lines.
            var b = DashboardTheme.Brand;
            var m = DashboardTheme.Muted;
            var row1m = $"[{b}]⏎[/] [{m}]act[/]   [{b}]⇥[/] [{m}]section[/]   [{b}]↑↓[/] [{m}]row[/]   [{b}]q[/] [{m}]quit[/]   [{b}]?[/] [{m}]more[/]";
            var row2m = $"[{b}]w[/] [{m}]wire[/]  [{b}]u[/] [{m}]unwire[/]  [{b}]r[/] [{m}]reindex[/]  [{b}]⇧R[/] [{m}]rebuild[/]  [{b}]p[/] [{m}]pull[/]  [{b}]v[/] [{m}]verify[/]  [{b}]i[/] [{m}]init[/]  [{b}]d[/] [{m}]demo[/]  [{b}]l[/] [{m}]log[/]  [{b}]e[/] [{m}]config[/]";
            return row1m + "\n" + row2m;
        }
    }

    /// <summary>
    /// Multi-line key reference shown by the inline help overlay (`?`). Same vocabulary as the
    /// dashboard subcommand's man-page section; centralised here so docs and runtime can't drift.
    /// </summary>
    public const string HelpText = """
        Navigation:
          ↑/↓ or j/k      Move row within section
          Tab/Shift+Tab   Next/previous section
          Enter           Open detail pane
          Esc             Close detail / modal
          q / Ctrl+C      Quit (exit 0)
          ?               Toggle this help
          s               Force refresh snapshot now

        In-place actions:
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
