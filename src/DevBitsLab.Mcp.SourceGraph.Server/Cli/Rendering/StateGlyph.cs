using DevBitsLab.Mcp.SourceGraph.Server.Tools;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;

/// <summary>
/// The five-state vocabulary the polished <c>init</c> output uses to mark every row. Each kind
/// maps to one of two tokens: an emoji form (default) or an ASCII form when
/// <see cref="LeafFormatter.Suppressed"/> is true. Token width is the same three display cells
/// in either form so column alignment holds across modes.
/// </summary>
internal enum StateGlyphKind
{
    /// <summary>Positive: selected, passed, wrote, unchanged, indexed.</summary>
    On,
    /// <summary>Inactive: row exists but is not selected; "off" default.</summary>
    Off,
    /// <summary>Soft warning: non-fatal, explanation follows.</summary>
    Warn,
    /// <summary>Hard skip / conflict: blocked, user action needed.</summary>
    Skip,
    /// <summary>Unsupported / N/A: combination has no writer / no handler.</summary>
    Unsupported,
}

/// <summary>
/// Resolves a <see cref="StateGlyphKind"/> to its rendered token. Returns the emoji glyph (plus a
/// trailing space) by default; falls back to a 3-cell-wide ASCII bracket token when
/// <see cref="LeafFormatter.Suppressed"/> is true.
/// </summary>
internal static class StateGlyph
{
    /// <summary>
    /// Token for the given state. Always three display cells wide so column alignment is preserved
    /// when callers print rows like <c>{glyph}{key:18}    {value}</c>. The trailing space is part
    /// of the token (so the caller writes <c>$"{StateGlyph.For(...)}{rest}"</c> without a manual
    /// separator).
    /// </summary>
    public static string For(StateGlyphKind kind)
    {
        if (LeafFormatter.Suppressed)
        {
            return kind switch
            {
                StateGlyphKind.On => "[x] ",
                StateGlyphKind.Off => "[ ] ",
                StateGlyphKind.Warn => "[!] ",
                StateGlyphKind.Skip => "[X] ",
                StateGlyphKind.Unsupported => "[-] ",
                _ => "[ ] ",
            };
        }
        return kind switch
        {
            StateGlyphKind.On => "🌿 ",
            StateGlyphKind.Off => "· ",
            StateGlyphKind.Warn => "⚠ ",
            StateGlyphKind.Skip => "✗ ",
            StateGlyphKind.Unsupported => "— ",
            _ => "· ",
        };
    }
}
