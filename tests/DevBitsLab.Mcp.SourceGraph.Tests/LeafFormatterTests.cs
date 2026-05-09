using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Unit coverage for the brand-mark prefix helper. <see cref="LeafFormatter.Suppressed"/> is shared
/// process-wide static state, so tests that flip it always restore it via try/finally — and the
/// class is opted into the <c>LeafFormatterState</c> collection so other tests that depend on
/// <c>Suppressed = false</c> never race with these.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class LeafFormatterTests : IDisposable
{
    public LeafFormatterTests() => LeafFormatter.Suppressed = false;
    public void Dispose() => LeafFormatter.Suppressed = false;

    [Fact]
    public void Brand_prependsMark_onUnbrandedString()
    {
        LeafFormatter.Brand("3 hits for 'Calculator':")
            .Should().Be("\U0001F33F 3 hits for 'Calculator':");
    }

    [Fact]
    public void Brand_isIdempotent_whenInputAlreadyStartsWithMark()
    {
        var alreadyBranded = "\U0001F33F already done.";
        LeafFormatter.Brand(alreadyBranded).Should().Be(alreadyBranded);
    }

    [Fact]
    public void Brand_doesNotDoubleStamp_acrossSuccessiveCalls()
    {
        var once = LeafFormatter.Brand("once");
        var twice = LeafFormatter.Brand(once);
        twice.Should().Be("\U0001F33F once");
    }

    [Fact]
    public void Brand_passesThrough_emptyString()
    {
        LeafFormatter.Brand(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Brand_isPassThrough_whenSuppressed()
    {
        try
        {
            LeafFormatter.Suppressed = true;
            LeafFormatter.Brand("untouched").Should().Be("untouched");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void Brand_doesNotStampLeaf_whenSuppressedAndInputAlreadyHasOne()
    {
        try
        {
            LeafFormatter.Suppressed = true;
            // We don't strip pre-existing leaves on the way out — Suppressed only governs whether
            // *we* add one. A tool that hand-rolls a leaf in its body is the tool's choice.
            LeafFormatter.Brand("\U0001F33F preserved").Should().Be("\U0001F33F preserved");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void Mark_isHerbGlyphFollowedBySpace()
    {
        LeafFormatter.Mark.Should().Be("\U0001F33F ");
    }

    [Fact]
    public void EnvVarName_matchesExpectedKnob()
    {
        LeafFormatter.EnvVarName.Should().Be("SOURCEGRAPH_NO_LEAF");
    }
}
