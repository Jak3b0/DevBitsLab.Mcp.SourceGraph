using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// Covers <see cref="BareCommandDispatch"/>: the rewrite is bare-only (no subcommand present),
/// <c>--help</c>/<c>-h</c> is never rewritten, the dispatch target is <c>dashboard</c> under a
/// tty and <c>status</c> under redirected stdin, and flags pass through unchanged.
///
/// <para>
/// The production probe (<see cref="Console.IsInputRedirected"/>) can't be flipped from unit
/// tests; the helper takes a delegate so tests drive the dispatch logic directly. This is the
/// reason <see cref="BareCommandDispatch.Rewrite"/> is factored out from <c>Program.Main</c>.
/// </para>
/// </summary>
public sealed class BareCommandDispatchTests
{
    [Fact]
    public void Rewrite_empty_args_underTty_returnsDashboard()
    {
        var result = BareCommandDispatch.Rewrite(Array.Empty<string>(), () => false);
        result.Should().ContainSingle().Which.Should().Be("dashboard");
    }

    [Fact]
    public void Rewrite_empty_args_redirectedStdin_returnsStatus()
    {
        var result = BareCommandDispatch.Rewrite(Array.Empty<string>(), () => true);
        result.Should().ContainSingle().Which.Should().Be("status");
    }

    [Fact]
    public void Rewrite_helpFlag_leavesArgsUnchanged()
    {
        var args = new[] { "--help" };
        var result = BareCommandDispatch.Rewrite(args, () => false);
        result.Should().BeSameAs(args);
    }

    [Fact]
    public void Rewrite_shortHelpFlag_leavesArgsUnchanged()
    {
        var args = new[] { "-h" };
        var result = BareCommandDispatch.Rewrite(args, () => false);
        result.Should().BeSameAs(args);
    }

    [Fact]
    public void Rewrite_existingSubcommand_leavesArgsUnchanged()
    {
        var args = new[] { "serve", "--solution", "/x.slnx" };
        var result = BareCommandDispatch.Rewrite(args, () => false);
        result.Should().BeSameAs(args);
    }

    [Fact]
    public void Rewrite_flagsOnlyBare_underTty_prependsDashboard_andPreservesFlags()
    {
        // `sourcegraph-mcp --root /repo` is a bare invocation with a propagated flag.
        var result = BareCommandDispatch.Rewrite(new[] { "--root", "/repo" }, () => false);
        result.Should().BeEquivalentTo(new[] { "dashboard", "--root", "/repo" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Rewrite_flagsOnlyBare_redirected_prependsStatus_andPreservesFlags()
    {
        var result = BareCommandDispatch.Rewrite(new[] { "--root", "/repo" }, () => true);
        result.Should().BeEquivalentTo(new[] { "status", "--root", "/repo" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Rewrite_emptyArgsWithHelpAfter_unreachable()
    {
        // Defensive: an args array with --help anywhere never rewrites, even if the help flag
        // is the only token. This matches the "--help still prints help" scenario in the spec.
        var args = new[] { "--help" };
        var result = BareCommandDispatch.Rewrite(args, () => false);
        result.Should().BeSameAs(args);
    }

    [Fact]
    public void IsBare_emptyArgs_returnsTrue()
    {
        BareCommandDispatch.IsBare(Array.Empty<string>()).Should().BeTrue();
    }

    [Fact]
    public void IsBare_subcommandArg_returnsFalse()
    {
        BareCommandDispatch.IsBare(new[] { "serve" }).Should().BeFalse();
        BareCommandDispatch.IsBare(new[] { "dashboard" }).Should().BeFalse();
        BareCommandDispatch.IsBare(new[] { "status" }).Should().BeFalse();
    }

    [Fact]
    public void IsBare_flagFirst_returnsTrue()
    {
        // Only flags → bare; the user wanted the default subcommand.
        BareCommandDispatch.IsBare(new[] { "--root", "/x" }).Should().BeTrue();
        BareCommandDispatch.IsBare(new[] { "--no-color" }).Should().BeTrue();
    }

    [Fact]
    public void IsBare_valueBearingFlagThenSubcommand_returnsFalse()
    {
        // `sourcegraph-mcp --root /repo serve` carries a subcommand AFTER a value-bearing flag.
        // Treating it as bare would rewrite to `["dashboard", "--root", "/repo", "serve"]` and
        // confuse CommandLine.Parse. The walker must skip `--root`'s value (`/repo`) and detect
        // `serve` as a positional → not bare.
        BareCommandDispatch.IsBare(new[] { "--root", "/repo", "serve" }).Should().BeFalse();
        BareCommandDispatch.IsBare(new[] { "--scope", "frontend", "status" }).Should().BeFalse();
        BareCommandDispatch.IsBare(new[] { "--solution", "/x.slnx", "index" }).Should().BeFalse();
    }

    [Fact]
    public void IsBare_booleanFlagsThenSubcommand_returnsFalse()
    {
        // Boolean flags don't consume a following token — a positional that follows is still
        // a subcommand.
        BareCommandDispatch.IsBare(new[] { "--no-color", "status" }).Should().BeFalse();
        BareCommandDispatch.IsBare(new[] { "--yes", "--print-only", "init" }).Should().BeFalse();
    }

    [Fact]
    public void IsBare_multipleValueBearingFlags_returnsTrue()
    {
        // Bare invocation with two value-bearing flags chained: each consumes its value, no
        // positional remains.
        BareCommandDispatch.IsBare(new[] { "--root", "/repo", "--model", "someorg/m" }).Should().BeTrue();
    }

    [Fact]
    public void Rewrite_valueBearingFlagThenSubcommand_passesThrough()
    {
        // Regression guard for the IsBare bug: `--root /repo serve` must reach the existing
        // `serve` subcommand unchanged, not be rewritten to `dashboard --root /repo serve`.
        var args = new[] { "--root", "/repo", "serve" };
        var result = BareCommandDispatch.Rewrite(args, () => false);
        result.Should().BeSameAs(args);
    }
}
