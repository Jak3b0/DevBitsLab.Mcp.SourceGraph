using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Rendering;

/// <summary>
/// Covers <see cref="StateGlyph"/>: every kind in both rendering modes, plus the column-width
/// invariant. Tests run in the <c>CliConsole</c> collection because they mutate
/// <see cref="LeafFormatter.Suppressed"/>, which is process-global.
/// </summary>
[Collection("CliConsole")]
public sealed class StateGlyphTests : IDisposable
{
    private readonly bool _initialSuppressed;

    public StateGlyphTests()
    {
        _initialSuppressed = LeafFormatter.Suppressed;
    }

    public void Dispose()
    {
        LeafFormatter.Suppressed = _initialSuppressed;
    }

    [Fact]
    public void EmojiMode_returnsLeafForOn()
    {
        LeafFormatter.Suppressed = false;
        StateGlyph.For(StateGlyphKind.On).Should().Be("🌿 ");
    }

    [Fact]
    public void EmojiMode_returnsMiddleDotForOff()
    {
        LeafFormatter.Suppressed = false;
        StateGlyph.For(StateGlyphKind.Off).Should().Be("· ");
    }

    [Fact]
    public void EmojiMode_returnsWarnGlyphForWarn()
    {
        LeafFormatter.Suppressed = false;
        StateGlyph.For(StateGlyphKind.Warn).Should().Be("⚠ ");
    }

    [Fact]
    public void EmojiMode_returnsCrossForSkip()
    {
        LeafFormatter.Suppressed = false;
        StateGlyph.For(StateGlyphKind.Skip).Should().Be("✗ ");
    }

    [Fact]
    public void EmojiMode_returnsEmDashForUnsupported()
    {
        LeafFormatter.Suppressed = false;
        StateGlyph.For(StateGlyphKind.Unsupported).Should().Be("— ");
    }

    [Fact]
    public void AsciiMode_returnsBracketX_forOn()
    {
        LeafFormatter.Suppressed = true;
        StateGlyph.For(StateGlyphKind.On).Should().Be("[x] ");
    }

    [Fact]
    public void AsciiMode_returnsBracketSpace_forOff()
    {
        LeafFormatter.Suppressed = true;
        StateGlyph.For(StateGlyphKind.Off).Should().Be("[ ] ");
    }

    [Fact]
    public void AsciiMode_returnsBracketBang_forWarn()
    {
        LeafFormatter.Suppressed = true;
        StateGlyph.For(StateGlyphKind.Warn).Should().Be("[!] ");
    }

    [Fact]
    public void AsciiMode_returnsBracketBigX_forSkip()
    {
        LeafFormatter.Suppressed = true;
        StateGlyph.For(StateGlyphKind.Skip).Should().Be("[X] ");
    }

    [Fact]
    public void AsciiMode_returnsBracketDash_forUnsupported()
    {
        LeafFormatter.Suppressed = true;
        StateGlyph.For(StateGlyphKind.Unsupported).Should().Be("[-] ");
    }

    [Fact]
    public void AsciiTokens_are3CellsWide_acrossAllKinds()
    {
        // The ASCII fallback is the form whose column-width property we can assert in a portable
        // way: `[x] ` etc. are exactly 4 chars where each char is a single display cell. The
        // emoji form is one emoji-width cell plus a space; conceptually the same width (3 cells)
        // but display width depends on the renderer's emoji handling — we only assert on ASCII
        // here.
        LeafFormatter.Suppressed = true;
        foreach (var kind in Enum.GetValues<StateGlyphKind>())
        {
            var token = StateGlyph.For(kind);
            token.Length.Should().Be(4, $"ASCII fallback for {kind} should be 3 chars + trailing space");
        }
    }

    [Fact]
    public void EmojiTokens_endWithSpace_acrossAllKinds()
    {
        // The trailing space is part of the token so callers don't need to add a separator.
        LeafFormatter.Suppressed = false;
        foreach (var kind in Enum.GetValues<StateGlyphKind>())
        {
            var token = StateGlyph.For(kind);
            token.Should().EndWith(" ", $"emoji token for {kind} ends with trailing space");
        }
    }
}
