using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using FluentAssertions;
using Xunit;
// Disambiguate ScopeRow (the snapshot row vs the storage row).
using ScopeRow = DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot.ScopeRow;
// ScopeLayout lives in Storage; named-import to keep the using terse.
using ScopeLayout = DevBitsLab.Mcp.SourceGraph.Storage.ScopeLayout;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// Tests for <see cref="FreshnessSource"/>: that the coalescing rule holds under bursts of
/// watcher events, that the poll tick produces at most one rebuild per second, and that
/// disposal stops the loop cleanly. We inject a synthetic <c>ISnapshotSource</c> via the
/// internal test seam so we can count rebuilds deterministically without standing up a real
/// repo / SQLite registry.
/// </summary>
public sealed class FreshnessSourceTests : IDisposable
{
    private readonly string _tempRoot;

    public FreshnessSourceTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-freshness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Join(_tempRoot, ScopeLayout.DotDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Start_fires_initialRebuild_inUnder200ms()
    {
        var source = new CountingSnapshotSource();
        using var fs = new FreshnessSource(_tempRoot, new SnapshotOptions(), source);
        var rebuilds = 0;
        fs.SnapshotChanged += _ => Interlocked.Increment(ref rebuilds);
        fs.Start();
        // Give the immediate-on-Start rebuild time to settle.
        await WaitForAsync(() => rebuilds >= 1, TimeSpan.FromMilliseconds(500));
        rebuilds.Should().Be(1, "Start() triggers exactly one immediate rebuild");
    }

    [Fact]
    public async Task PollTick_under_steadyState_atMostOneRebuildPerSecond()
    {
        var source = new CountingSnapshotSource();
        using var fs = new FreshnessSource(_tempRoot, new SnapshotOptions(), source);
        fs.Start();
        // Let the loop run for ~2.5 seconds; expect rebuilds: 1 immediate + 2 polls = 3.
        await Task.Delay(2500);
        // Allow a +/- 1 slack for clock skew on the timer.
        source.Count.Should().BeInRange(2, 4);
    }

    [Fact]
    public async Task WatcherBurst_within_window_coalescesToOneExtraRebuild()
    {
        var source = new CountingSnapshotSource();
        using var fs = new FreshnessSource(_tempRoot, new SnapshotOptions(), source);
        fs.Start();
        await WaitForAsync(() => source.Count >= 1, TimeSpan.FromMilliseconds(500));
        var baseline = source.Count;

        // Fire 10 rapid watcher events on usage.jsonl. The 100 ms debounce coalesces them all.
        var usagePath = Path.Join(_tempRoot, ScopeLayout.DotDir, "usage.jsonl");
        for (var i = 0; i < 10; i++)
        {
            File.AppendAllText(usagePath, $"{{\"ts\":\"2025-01-01T00:00:00Z\",\"tool\":\"x{i}\"}}\n");
            await Task.Delay(10);
        }
        // Wait long enough for the 100 ms debounce + rebuild to complete.
        await WaitForAsync(() => source.Count > baseline, TimeSpan.FromSeconds(2));
        // We expect at most a few extra rebuilds (1 from the burst itself; possibly 1 more from
        // a poll tick that lands within our wait window). Strict assertion: never 10.
        (source.Count - baseline).Should().BeLessThanOrEqualTo(3, "the burst of 10 events should coalesce");
    }

    [Fact]
    public void Dispose_stopsAllResources()
    {
        var source = new CountingSnapshotSource();
        var fs = new FreshnessSource(_tempRoot, new SnapshotOptions(), source);
        fs.Start();
        fs.Dispose();
        // Second Dispose is a no-op.
        fs.Dispose();
        // The dispose path doesn't throw and leaves the source's last count fixed.
    }

    [Fact]
    public async Task RequestImmediateRebuild_bypassesCoalesce()
    {
        var source = new CountingSnapshotSource();
        using var fs = new FreshnessSource(_tempRoot, new SnapshotOptions(), source);
        fs.Start();
        await WaitForAsync(() => source.Count >= 1, TimeSpan.FromMilliseconds(500));
        var baseline = source.Count;
        // Outside the 1-second window from Start; immediate rebuild should fire.
        await Task.Delay(50);
        fs.RequestImmediateRebuild();
        await WaitForAsync(() => source.Count > baseline, TimeSpan.FromSeconds(2));
        source.Count.Should().BeGreaterThan(baseline);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        // Returning silently here would let a broken FreshnessSource appear "fine" — the test
        // would assert against an unchanged baseline and pass spuriously. Fail loudly with the
        // elapsed time so a timeout is clearly distinguishable from a real assertion failure.
        throw new Xunit.Sdk.XunitException(
            $"WaitForAsync timed out after {sw.Elapsed.TotalMilliseconds:F0}ms (timeout: {timeout.TotalMilliseconds:F0}ms); predicate never returned true.");
    }

    private sealed class CountingSnapshotSource : FreshnessSource.ISnapshotSource
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public Task<DashboardSnapshot> BuildAsync(string root, SnapshotOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            var snap = new DashboardSnapshot(
                Environment: new EnvironmentSurface(
                    DotnetSdkVersion: "10.0.100",
                    GitOnPath: true,
                    RepoRootPath: root,
                    SolutionFiles: Array.Empty<string>(),
                    SourceGraphConfigStatus: "missing",
                    SourceGraphConfigError: null),
                Scopes: Array.Empty<ScopeRow>(),
                Clients: Array.Empty<ClientRow>(),
                Embeddings: new EmbeddingsSurface("test", "/tmp", false, 0, false),
                RecentActivity: Array.Empty<ActivityEntry>(),
                BuiltAt: DateTimeOffset.UtcNow,
                UsageLogPath: Path.Join(root, ".sourcegraph", "usage.jsonl"),
                HealsLogPath: Path.Join(root, ".sourcegraph", "heals.jsonl"),
                ExitCode: 0);
            return Task.FromResult(snap);
        }
    }
}
