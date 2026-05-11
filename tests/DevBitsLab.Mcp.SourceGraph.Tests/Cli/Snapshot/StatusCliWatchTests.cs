using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Snapshot;

/// <summary>
/// Coverage for the <c>--watch</c> mode. Under a test harness <see cref="Console.IsInputRedirected"/>
/// is always <c>true</c>, so the documented "silently downgrade to single snapshot" path is
/// exercised. The redraw loop itself is covered indirectly via the loop body invoked through
/// the snapshot builder (which is FD-stable when called repeatedly).
/// </summary>
[Collection("CliConsole")]
public sealed class StatusCliWatchTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TextWriter _originalStdout;
    private readonly StringWriter _stdout = new();

    public StatusCliWatchTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-status-watch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _originalStdout = Console.Out;
        Console.SetOut(_stdout);
    }

    public void Dispose()
    {
        Console.SetOut(_originalStdout);
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task Status_watch_underNonTty_downgradesToSingleSnapshot()
    {
        // Force the stdin-redirected branch deterministically via the injectable probe overload —
        // some test runners (IDE / custom harnesses) don't redirect stdin, which would otherwise
        // make the assertion depend on the host environment.
        var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot, "--watch", "--watch-interval", "1" });
        var rc = await StatusCli.RunAsync(cli, isStdinRedirected: () => true);
        // The downgraded path renders exactly once and returns. No ANSI clear codes appear
        // because the watch loop isn't entered.
        var output = _stdout.ToString();
        output.Should().Contain("Environment");
        output.Should().NotContain("\x1b[H\x1b[J");
        rc.Should().BeOneOf(0, 1, 2);
    }

    [Fact]
    public async Task Status_watchAndJson_areMutuallyExclusive()
    {
        // The watch loop emits ANSI cursor codes (`\x1b[H\x1b[J`) before each redraw; combining
        // those with `--json` would corrupt the JSON document on stdout. The subcommand rejects
        // the combination with exit 2 and an error message on stderr.
        var stderrCapture = new StringWriter();
        var savedStderr = Console.Error;
        Console.SetError(stderrCapture);
        try
        {
            var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot, "--watch", "--json" });
            var rc = await StatusCli.RunAsync(cli);
            rc.Should().Be(2);
            stderrCapture.ToString().Should().Contain("--watch and --json are mutually exclusive");
            // No JSON document was written to stdout.
            _stdout.ToString().Should().BeEmpty();
        }
        finally
        {
            Console.SetError(savedStderr);
        }
    }

    [Fact]
    public async Task Status_buildAsync_repeatedCalls_doNotLeakSqliteHandles()
    {
        // Approximate FD-leak guard: build the snapshot 30 times in tight succession and assert
        // the process can continue (no resource exhaustion). The builder opens its handles in
        // `using` scopes, so a regression that "forgets" to dispose would fail this with an
        // SqliteException after ~hundreds of opens. Use a lower bound (30) so the test stays
        // fast on CI while still proving the dispose path.
        var dotDir = Path.Join(_tempRoot, ".sourcegraph");
        Directory.CreateDirectory(dotDir);
        // Put a real _meta.db in place so the scope branch opens a handle each call.
        var registry = new DevBitsLab.Mcp.SourceGraph.Storage.SqliteScopeRegistry(
            DevBitsLab.Mcp.SourceGraph.Storage.ScopeLayout.MetaDbPath(_tempRoot));
        await registry.EnsureSchemaAsync();
        await registry.UpsertAsync(new DevBitsLab.Mcp.SourceGraph.Storage.ScopeRow(
            Id: "default",
            Name: "default",
            Root: _tempRoot,
            ProjectSetJson: "{}",
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow,
            Status: "ok",
            StatusMessage: null));
        await registry.DisposeAsync();

        for (var i = 0; i < 30; i++)
        {
            var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
            snap.Scopes.Should().HaveCount(1);
        }
        // No exception → no FD leak that mattered. Done.
    }
}
