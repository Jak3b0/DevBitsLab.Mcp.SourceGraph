namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// The actions a single keystroke can dispatch. Three tiers per design Decision 3:
///
/// <list type="bullet">
/// <item><b>Read-only</b>: navigation, force-refresh, quit, help. No state mutation, no gating.</item>
/// <item><b>In-place</b>: <c>Reindex</c>, <c>Wire</c>, <c>Pull</c>, <c>Verify</c> (idempotent / additive,
/// no gating) and <c>Rebuild</c>, <c>Unwire</c> (destructive, gate behind <see cref="ConfirmModal"/>).</item>
/// <item><b>Guided</b>: <c>Init</c>, <c>Demo</c>, <c>OpenLog</c>, <c>OpenConfig</c> — suspend Live, spawn
/// subprocess with inherited streams, resume.</item>
/// </list>
/// </summary>
internal enum DashboardAction
{
    None,

    // Read-only navigation
    MoveUp,
    MoveDown,
    NextSection,
    PreviousSection,
    OpenDetail,
    CloseDetail,
    Quit,
    ToggleHelp,
    ForceRefresh,

    /// <summary>
    /// The section-aware "act on the selected row" action <c>Enter</c> is bound to. The dispatcher
    /// in <see cref="DashboardCli"/> routes this to a section-specific concrete action (reindex
    /// for Scopes, wire/unwire toggle for Clients, pull for Embeddings, etc.). Kept as a
    /// distinct enum value rather than overloading <see cref="OpenDetail"/> so a future
    /// detail-pane feature can reclaim Enter on a section-by-section basis.
    /// </summary>
    PrimaryAction,

    // In-place (no gate)
    ReindexScope,
    WireClient,
    EmbeddingsPull,
    EmbeddingsVerify,

    // In-place (gate via ConfirmModal)
    RebuildScope,
    UnwireClient,

    // Guided (suspend + subprocess)
    InitGuided,
    DemoGuided,
    OpenLogInPager,
    OpenConfigInEditor,
}
