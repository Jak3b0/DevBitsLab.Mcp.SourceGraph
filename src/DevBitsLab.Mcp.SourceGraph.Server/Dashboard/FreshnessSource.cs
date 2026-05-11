using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Hybrid poll-plus-watcher refresh source for the dashboard. Rebuilds a
/// <see cref="DashboardSnapshot"/> on a 1-second poll AND on debounced
/// <see cref="FileSystemWatcher"/> events against the two JSONL logs
/// (<c>usage.jsonl</c>, <c>heals.jsonl</c>). Both triggers coalesce so at most one rebuild
/// fires per second under sustained activity.
///
/// <para>
/// Lifecycle: construct with <see cref="FreshnessSource(string, SnapshotOptions, ISnapshotSource)"/>,
/// subscribe to <see cref="SnapshotChanged"/>, call <see cref="Start"/>. Snapshot rebuilds run on
/// the thread-pool via <see cref="Task.Run(Func{Task})"/>; subscribers receive callbacks on the
/// thread-pool thread that completed the rebuild. The dashboard's main loop marshals back to the
/// UI thread by enqueueing the latest snapshot for the next Spectre <c>Live</c> render tick.
/// </para>
///
/// <para>
/// Disposal stops both the poll timer and both watchers; in-flight rebuilds complete naturally
/// (no cancellation token is threaded through because <see cref="SnapshotBuilder.BuildAsync"/>
/// is read-only and short-lived).
/// </para>
/// </summary>
internal sealed class FreshnessSource : IDisposable
{
    /// <summary>Coalescing window: at most one rebuild per this much wall-clock time.</summary>
    public static readonly TimeSpan MinRebuildInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>Debounce delay after a watcher event before the rebuild fires.</summary>
    public static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(100);

    private readonly string _root;
    private readonly SnapshotOptions _options;
    private readonly ISnapshotSource _snapshotSource;
    private readonly Lock _gate = new();

    private Timer? _pollTimer;
    private Timer? _debounceTimer;
    private FileSystemWatcher? _usageWatcher;
    private FileSystemWatcher? _healsWatcher;
    private DateTimeOffset _lastRebuildStarted = DateTimeOffset.MinValue;
    private bool _rebuildInFlight;
    private DashboardSnapshot? _latest;
    private volatile bool _disposed;

    /// <summary>
    /// Construct a freshness source rooted at <paramref name="root"/>. The optional
    /// <paramref name="snapshotSource"/> override exists for tests: production callers pass
    /// <c>null</c> and the real <see cref="SnapshotBuilder.BuildAsync"/> is used.
    /// </summary>
    public FreshnessSource(string root, SnapshotOptions options, ISnapshotSource? snapshotSource = null)
    {
        _root = root;
        _options = options;
        _snapshotSource = snapshotSource ?? RealSnapshotSource.Instance;
    }

    /// <summary>Most-recent successfully built snapshot, or null when none has completed yet.</summary>
    public DashboardSnapshot? Latest
    {
        get { lock (_gate) return _latest; }
    }

    /// <summary>Fires after each successful rebuild, on the thread-pool thread that completed it.</summary>
    public event Action<DashboardSnapshot>? SnapshotChanged;

    /// <summary>
    /// Start the poll timer and both file-watchers, and trigger an immediate initial rebuild so
    /// subscribers see a snapshot without waiting for the first poll tick. Safe to call once per
    /// instance; subsequent calls are no-ops.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FreshnessSource));
            if (_pollTimer is not null) return; // already started

            _pollTimer = new Timer(_ => OnPollTick(), state: null,
                dueTime: MinRebuildInterval, period: MinRebuildInterval);

            _debounceTimer = new Timer(_ => OnDebounceFire(), state: null,
                dueTime: Timeout.Infinite, period: Timeout.Infinite);

            var dotDir = Path.Join(_root, ScopeLayout.DotDir);
            // Create the .sourcegraph dotdir if it doesn't exist yet so the FileSystemWatchers
            // attach reliably from a cold start. Without this, launching the dashboard before
            // `serve` (or before any process has created `.sourcegraph/`) leaves the watchers
            // un-attached — `usage.jsonl` / `heals.jsonl` writes wouldn't trigger sub-second
            // refresh; recent activity would only surface on the 1-second poll.
            try { Directory.CreateDirectory(dotDir); }
            catch (IOException) { /* best-effort; the poll loop still gives 1s freshness */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
            if (Directory.Exists(dotDir))
            {
                _usageWatcher = TryStartWatcher(dotDir, "usage.jsonl");
                _healsWatcher = TryStartWatcher(dotDir, "heals.jsonl");
            }
        }
        // Immediate first rebuild outside the lock so SnapshotChanged subscribers can be wired
        // before the callback fires.
        TriggerRebuild(immediate: true);
    }

    /// <summary>Stop timers and watchers. Idempotent; safe to call multiple times.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _usageWatcher?.Dispose();
            _usageWatcher = null;
            _healsWatcher?.Dispose();
            _healsWatcher = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Force a rebuild outside the coalescing window. Used by the dashboard's force-refresh key
    /// (<c>s</c>) so the operator can demand a fresh snapshot without waiting for the next tick.
    /// </summary>
    public void RequestImmediateRebuild() => TriggerRebuild(immediate: true);

    private FileSystemWatcher? TryStartWatcher(string dir, string fileName)
    {
        try
        {
            var w = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            w.Changed += OnWatcherEvent;
            w.Created += OnWatcherEvent;
            return w;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private void OnWatcherEvent(object _, FileSystemEventArgs __)
    {
        // Schedule a single rebuild after WatcherDebounce; subsequent events within the window
        // reset the timer (one-shot semantics — period = Infinite).
        lock (_gate)
        {
            if (_disposed) return;
            _debounceTimer?.Change(WatcherDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnPollTick() => TriggerRebuild(immediate: false);

    private void OnDebounceFire() => TriggerRebuild(immediate: false);

    private void TriggerRebuild(bool immediate)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_disposed) return;
            if (_rebuildInFlight) return; // a rebuild is already running; the in-flight result satisfies this trigger
            if (!immediate && now - _lastRebuildStarted < MinRebuildInterval) return; // coalesce
            _lastRebuildStarted = now;
            _rebuildInFlight = true;
        }
        _ = Task.Run(RebuildAsync);
    }

    private async Task RebuildAsync()
    {
        try
        {
            var snapshot = await _snapshotSource.BuildAsync(_root, _options, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _latest = snapshot;
            }
            // Fire outside the lock so subscribers can't deadlock against a re-entrant request.
            try { SnapshotChanged?.Invoke(snapshot); }
            catch { /* subscriber threw; not our problem — keep the source alive */ }
        }
        catch (IOException)
        {
            // Repo went away mid-build (rare). Leave _latest unchanged; next tick will retry.
        }
        catch (Exception)
        {
            // Defensive: any unexpected snapshot failure must not kill the freshness loop. The
            // dashboard surfaces the stale snapshot until the next rebuild succeeds.
        }
        finally
        {
            lock (_gate) { _rebuildInFlight = false; }
        }
    }

    /// <summary>
    /// Test seam: tests inject a synthetic snapshot source to deterministically count rebuilds
    /// without dragging the real <see cref="SnapshotBuilder"/> into the freshness-coalescing
    /// assertions. Production wiring uses <see cref="RealSnapshotSource"/>.
    /// </summary>
    internal interface ISnapshotSource
    {
        Task<DashboardSnapshot> BuildAsync(string root, SnapshotOptions options, CancellationToken ct);
    }

    private sealed class RealSnapshotSource : ISnapshotSource
    {
        public static readonly RealSnapshotSource Instance = new();
        public Task<DashboardSnapshot> BuildAsync(string root, SnapshotOptions options, CancellationToken ct)
            => SnapshotBuilder.BuildAsync(root, options, ct);
    }
}
