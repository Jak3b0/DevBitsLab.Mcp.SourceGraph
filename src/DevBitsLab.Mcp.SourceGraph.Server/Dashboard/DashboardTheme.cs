using DevBitsLab.Mcp.SourceGraph.Server.Tools;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Status kinds the dashboard theme renders as colored dots. This is the single vocabulary used
/// across the dashboard, <c>init</c>, and <c>status</c>: the leaf <c>🌿</c> is reserved for the
/// brand mark (title bar / MCP tool responses); every row-status position uses the dot
/// vocabulary below.
/// </summary>
internal enum StatusKind
{
    /// <summary>Healthy / wired / present / ok.</summary>
    Ok,
    /// <summary>Soft warning — non-fatal, attention worthwhile.</summary>
    Warn,
    /// <summary>Hard failure — degraded / broken / blocked.</summary>
    Fail,
    /// <summary>Inactive — file exists but no sourcegraph entry, model cache absent, etc.</summary>
    Off,
    /// <summary>Unsupported — no writer / no handler for this combination.</summary>
    Unsupported,
}

/// <summary>
/// Centralised palette + glyph vocabulary for the dashboard. Constants only — no logic beyond a
/// single colorised helper that builds the dot Markup. Hex colours work in 256-colour terminals;
/// Spectre downgrades them to the nearest ANSI16 colour automatically when the terminal can't
/// render hex.
///
/// <para>
/// This vocabulary (●/◐/✗/○/−) is shared by every surface — dashboard, <c>init</c>,
/// <c>status</c> — so an operator who learns one mode immediately reads the others. The leaf
/// <c>🌿</c> is reserved for the brand mark (title bars + MCP tool responses) and never appears
/// in a row-status position. <see cref="LeafFormatter.Suppressed"/> activates an ASCII fallback
/// (<c>[x]</c> / <c>[!]</c> / <c>[X]</c> / <c>[ ]</c> / <c>[-]</c>) so the no-leaf path produces
/// the same token vocabulary everywhere.
/// </para>
/// </summary>
internal static class DashboardTheme
{
    // ─── Palette ────────────────────────────────────────────────────────────────────
    /// <summary>Brand colour — the sage-green the project uses everywhere.</summary>
    public const string Brand = "#5fa07a";
    /// <summary>Brand dim — for unfocused-section headers and subordinate decoration.</summary>
    public const string BrandDim = "#3d6c52";
    /// <summary>Healthy state colour. Same hex as <see cref="Brand"/>; named so callsites read.</summary>
    public const string Ok = "#5fa07a";
    /// <summary>Warning colour — muted amber.</summary>
    public const string Warn = "#e0a040";
    /// <summary>Failure colour — muted red.</summary>
    public const string Fail = "#d4544b";
    /// <summary>Muted text — section bodies that aren't currently focused, footer hint.</summary>
    public const string Muted = "grey50";
    /// <summary>Very-muted text — toast fade-to-disappear, second-line hint keys.</summary>
    public const string MutedDim = "grey39";

    // ─── Glyphs (Unicode, color-rendered separately) ────────────────────────────────
    /// <summary>Section-header leader. One glyph in <see cref="Brand"/> precedes the title.</summary>
    public const string SectionLeader = "◆";
    /// <summary>Healthy / on / present dot.</summary>
    public const string DotOn = "●";
    /// <summary>Inactive / off / available-but-empty dot.</summary>
    public const string DotOff = "○";
    /// <summary>Warning / partial / degraded dot (half-filled circle).</summary>
    public const string DotWarn = "◐";
    /// <summary>Failure / broken dot (cross). Distinct shape from dots so colourblind users still see the difference.</summary>
    public const string DotFail = "✗";
    /// <summary>Unsupported / N/A dot.</summary>
    public const string DotUnsupported = "−";
    /// <summary>Vertical bar marking the selected row in a focused section.</summary>
    public const string SelectedBar = "▌";
    /// <summary>Filler used for the unselected slot in the cursor column. Single space keeps column alignment when no row is selected.</summary>
    public const string Unselected = " ";
    /// <summary>The leaf glyph reserved for the title bar only. Other surfaces switched to dots.</summary>
    public const string BrandLeaf = "🌿";

    /// <summary>
    /// Render a status dot as Spectre <c>Markup</c>. Honours
    /// <see cref="LeafFormatter.Suppressed"/> — under <c>--no-leaf</c> the output is the same
    /// ASCII bracket-token vocabulary <see cref="DotPlain"/> emits, with no colour markup, so the
    /// dashboard, <c>init</c>, and <c>status</c> agree on a single fallback token per state.
    /// </summary>
    public static string Dot(StatusKind kind)
    {
        if (LeafFormatter.Suppressed)
        {
            // Escape the brackets so Spectre's Markup parser doesn't read them as tag delimiters
            // when this string is wrapped in `new Markup(...)`. The token vocabulary
            // ([x] / [!] / [X] / [ ] / [-]) is the same shape DotPlain emits.
            return kind switch
            {
                StatusKind.Ok => "[[x]]",
                StatusKind.Warn => "[[!]]",
                StatusKind.Fail => "[[X]]",
                StatusKind.Off => "[[ ]]",
                StatusKind.Unsupported => "[[-]]",
                _ => "[[ ]]",
            };
        }
        return kind switch
        {
            StatusKind.Ok => $"[{Ok}]{DotOn}[/]",
            StatusKind.Warn => $"[{Warn}]{DotWarn}[/]",
            StatusKind.Fail => $"[{Fail}]{DotFail}[/]",
            StatusKind.Off => $"[{Muted}]{DotOff}[/]",
            StatusKind.Unsupported => $"[{Muted}]{DotUnsupported}[/]",
            _ => $"[{Muted}]{DotOff}[/]",
        };
    }

    /// <summary>
    /// Render a status dot as a plain-text token for surfaces that write straight to a
    /// <see cref="System.IO.TextWriter"/> (e.g. <c>init</c>, <c>status</c>). Returns the dot
    /// glyph followed by a single space so the token occupies a stable three-cell column. Under
    /// <see cref="LeafFormatter.Suppressed"/> the bracketed ASCII fallback is returned instead —
    /// same vocabulary as the Markup-bearing <see cref="Dot(StatusKind)"/>, but without Spectre
    /// escape-doubling and without colour markup so the bytes are safe to pipe / capture / diff.
    /// </summary>
    public static string DotPlain(StatusKind kind)
    {
        if (LeafFormatter.Suppressed)
        {
            return kind switch
            {
                StatusKind.Ok => "[x] ",
                StatusKind.Warn => "[!] ",
                StatusKind.Fail => "[X] ",
                StatusKind.Off => "[ ] ",
                StatusKind.Unsupported => "[-] ",
                _ => "[ ] ",
            };
        }
        return kind switch
        {
            StatusKind.Ok => $"{DotOn} ",
            StatusKind.Warn => $"{DotWarn} ",
            StatusKind.Fail => $"{DotFail} ",
            StatusKind.Off => $"{DotOff} ",
            StatusKind.Unsupported => $"{DotUnsupported} ",
            _ => $"{DotOff} ",
        };
    }

    /// <summary>
    /// Render a section header like <c>"◆ Environment"</c> for plain-text writers. The leader
    /// glyph is the same <see cref="SectionLeader"/> the dashboard's Spectre layer uses for its
    /// detail-view section headers; here we emit just the glyph + space + name with no ANSI
    /// colour codes so the bytes are pipe / test-capture safe. Under
    /// <see cref="LeafFormatter.Suppressed"/> the leader downgrades to a bracketed ASCII token
    /// (<c>"[*]"</c>) keeping the three-cell column width consistent with the dot tokens.
    /// </summary>
    public static string SectionHeaderPlain(string name)
    {
        var leader = LeafFormatter.Suppressed ? "[*]" : SectionLeader;
        return $"{leader} {name}";
    }
}
