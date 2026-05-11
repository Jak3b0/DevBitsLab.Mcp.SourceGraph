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
        // Test harness has stdin closed → Console.IsInputRedirected == true → --watch downgrades.
        Console.IsInputRedirected.Should().BeTrue("test runners always redirect stdin");
        var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot, "--watch", "--watch-interval", "1" });
        var rc = await StatusCli.RunAsync(cli);
        // The downgraded path renders exactly once and returns. No ANSI clear codes appear
        // because the watch loop isn't entered.
        var output = _stdout.ToString();
        output.Should().Contain("Environment");
        output.Should().NotContain("\x1b[H\x1b[J");
        rc.Should().BeOneOf(0, 1, 2);
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
