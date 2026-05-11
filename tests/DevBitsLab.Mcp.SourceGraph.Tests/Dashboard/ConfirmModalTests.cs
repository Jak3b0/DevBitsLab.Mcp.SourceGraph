using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// Tests for <see cref="ConfirmModal"/>: drives the prompt against a Spectre
/// <see cref="TestConsole"/> with scripted input, asserts the returned boolean. Spectre's
/// <c>TestConsole</c> implements <see cref="IAnsiConsole"/> and lets us push key/string input
/// the prompt consumes via <see cref="IAnsiConsoleInput"/>.
/// </summary>
public sealed class ConfirmModalTests
{
    [Fact]
    public void Prompt_userEntersY_returnsTrue()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("y");
        var result = ConfirmModal.Prompt(console, "unwire", "claude-code");
        result.Should().BeTrue();
    }

    [Fact]
    public void Prompt_userEntersN_returnsFalse()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("n");
        var result = ConfirmModal.Prompt(console, "unwire", "claude-code");
        result.Should().BeFalse();
    }

    [Fact]
    public void Prompt_userJustPressesEnter_returnsDefaultFalse()
    {
        // Spectre's ConfirmationPrompt with DefaultValue=false takes Enter as the default.
        var console = new TestConsole();
        console.Input.PushTextWithEnter("");
        var result = ConfirmModal.Prompt(console, "rebuild", "backend");
        result.Should().BeFalse();
    }

    [Fact]
    public void Prompt_includesTargetInOutput()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("n");
        ConfirmModal.Prompt(console, "rebuild", "frontend");
        console.Output.Should().Contain("frontend");
    }

    [Fact]
    public void Prompt_userPressesEsc_returnsDefaultFalse()
    {
        // Esc is the documented non-destructive dismissal path. In Spectre's TextPrompt-derived
        // ConfirmationPrompt, Escape clears the pending input buffer without submitting; the
        // subsequent Enter then commits the unset value, which takes the default (false).
        var console = new TestConsole();
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Enter);
        var result = ConfirmModal.Prompt(console, "remove", "backend");
        result.Should().BeFalse();
    }
}
