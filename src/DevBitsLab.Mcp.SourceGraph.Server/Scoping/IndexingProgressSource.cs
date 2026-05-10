using ModelContextProtocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Per-scope cold-start progress broadcaster. <see cref="LiveIndexService"/> creates one per
/// scope, emits at coarse phase boundaries during the initial index, and tools that block on
/// <see cref="ScopeHost.Ready"/> while indexing is in flight subscribe to forward each event
/// as a wire-level <c>notifications/progress</c> message tagged with the originating
/// <c>tools/call</c> request's <c>progressToken</c>.
/// </summary>
internal interface IIndexingProgressSource
{
    /// <summary>
    /// Fires per phase checkpoint. Handlers SHOULD NOT block — the broadcaster's lock is held
    /// across the dispatch loop, so a slow handler delays every subsequent emission.
    /// </summary>
    event Action<ProgressNotificationValue> Reported;

    /// <summary>True once the source has emitted its <c>"ready"</c> terminal event. Subscribers
    /// can gate their subscription off this flag instead of re-checking per emission.</summary>
    bool IsReady { get; }
}

/// <summary>
/// Default broadcasting implementation. Thread-safe subscribe/unsubscribe; <see cref="Emit"/>
/// fires every subscriber's handler in the order they registered. Once <see cref="MarkReady"/>
/// has been invoked, the source becomes a no-op for any subsequent <see cref="Emit"/> call —
/// matches the spec contract that the source emits during initial indexing only and stops
/// after <c>ready</c>.
/// </summary>
internal sealed class IndexingProgressSource : IIndexingProgressSource
{
    private readonly object _lock = new();
    private Action<ProgressNotificationValue>? _handlers;
    private volatile bool _isReady;

    public bool IsReady => _isReady;

    public event Action<ProgressNotificationValue> Reported
    {
        add { lock (_lock) _handlers += value; }
        remove { lock (_lock) _handlers -= value; }
    }

    /// <summary>Emit a progress event to every subscriber. No-op once <see cref="MarkReady"/>
    /// has fired (terminal-state contract).</summary>
    public void Emit(ProgressNotificationValue value)
    {
        if (_isReady) return;
        Action<ProgressNotificationValue>? snapshot;
        lock (_lock) snapshot = _handlers;
        snapshot?.Invoke(value);
    }

    /// <summary>Emit the terminal <c>ready</c> event and flip the IsReady flag. Subsequent
    /// <see cref="Emit"/> calls are dropped.</summary>
    public void MarkReady()
    {
        if (_isReady) return;
        Action<ProgressNotificationValue>? snapshot;
        lock (_lock) snapshot = _handlers;
        snapshot?.Invoke(new ProgressNotificationValue { Progress = 1.0f, Total = 1.0f, Message = "ready" });
        _isReady = true;
    }
}
