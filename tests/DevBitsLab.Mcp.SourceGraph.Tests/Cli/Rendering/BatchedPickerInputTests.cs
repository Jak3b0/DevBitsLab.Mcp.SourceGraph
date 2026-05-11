using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Rendering;

/// <summary>
/// Covers the parser for the single batched-picker prompt input. Grammar branches: empty / y / Y
/// (defaults); n / N (deselect all); +/- slugs (flip from defaults); unknown slug (warn and skip);
/// malformed token (reprompt signal).
/// </summary>
public sealed class BatchedPickerInputTests
{
    private static readonly IReadOnlySet<string> KnownSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-code", "copilot", "cursor", "continue", "claude-desktop",
    };

    private static IReadOnlySet<string> Defaults(params string[] slugs) =>
        new HashSet<string>(slugs, StringComparer.Ordinal);

    [Fact]
    public void EmptyInput_returnsDefaults()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeFalse();
        result.Selection.Should().BeEquivalentTo(defaults);
        result.UnknownSlugs.Should().BeEmpty();
    }

    [Fact]
    public void WhitespaceOnly_returnsDefaults()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("   \t  ", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeFalse();
        result.Selection.Should().BeEquivalentTo(defaults);
    }

    [Fact]
    public void LowerY_returnsDefaults()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("y", defaults, KnownSlugs);
        result.Selection.Should().BeEquivalentTo(defaults);
        result.NeedsReprompt.Should().BeFalse();
    }

    [Fact]
    public void UpperY_returnsDefaults()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("Y", defaults, KnownSlugs);
        result.Selection.Should().BeEquivalentTo(defaults);
    }

    [Fact]
    public void LowerN_deselectsAll()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("n", defaults, KnownSlugs);
        result.Selection.Should().BeEmpty();
        result.NeedsReprompt.Should().BeFalse();
    }

    [Fact]
    public void UpperN_deselectsAll()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("N", defaults, KnownSlugs);
        result.Selection.Should().BeEmpty();
    }

    [Fact]
    public void PlusSlug_addsToDefaults()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("+cursor", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeFalse();
        result.Selection.Should().BeEquivalentTo(new[] { "claude-code", "copilot", "cursor" });
    }

    [Fact]
    public void MinusSlug_removesFromDefaults()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("-copilot", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeFalse();
        result.Selection.Should().BeEquivalentTo(new[] { "claude-code" });
    }

    [Fact]
    public void MixedPlusMinus_appliesInOrder()
    {
        var defaults = Defaults("claude-code", "copilot");
        var result = BatchedPickerInput.Parse("+cursor -copilot +continue", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeFalse();
        result.Selection.Should().BeEquivalentTo(new[] { "claude-code", "cursor", "continue" });
    }

    [Fact]
    public void UnknownSlug_warnsAndIgnores()
    {
        var defaults = Defaults("claude-code");
        var result = BatchedPickerInput.Parse("+sublime", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeFalse();
        result.Selection.Should().BeEquivalentTo(new[] { "claude-code" });
        result.UnknownSlugs.Should().BeEquivalentTo(new[] { "sublime" });
    }

    [Fact]
    public void MalformedToken_signalsReprompt()
    {
        var defaults = Defaults("claude-code");
        var result = BatchedPickerInput.Parse("cursor", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeTrue();
    }

    [Fact]
    public void GarbageInput_signalsReprompt()
    {
        var defaults = Defaults("claude-code");
        var result = BatchedPickerInput.Parse("xyz123", defaults, KnownSlugs);
        result.NeedsReprompt.Should().BeTrue();
    }

    [Fact]
    public void AllOff_isEmpty()
    {
        var result = BatchedPickerInput.AllOff();
        result.Selection.Should().BeEmpty();
        result.NeedsReprompt.Should().BeFalse();
    }
}
