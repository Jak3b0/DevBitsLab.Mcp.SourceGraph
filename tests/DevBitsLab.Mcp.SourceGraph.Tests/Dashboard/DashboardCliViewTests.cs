using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// View-model tests for the dashboard CLI: per-view primary-action dispatch, per-view action
/// gating, and the end-to-end key → action → view-transition contract for number keys and
/// Esc/h. Drives the static seams (<see cref="DashboardKeyMap.TryResolve"/>,
/// <see cref="DashboardPrimaryAction.ResolveForView"/>, <see cref="DashboardViewGating.IsAllowed"/>)
/// without standing up the full LoopState (which depends on a live terminal).
/// </summary>
public sealed class DashboardCliViewTests
{
    // ────────────────────────────────────────────────────────────────────────────────
    // Number keys open the right view
    // ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData('1', (int)DashboardAction.OpenScopes)]
    [InlineData('2', (int)DashboardAction.OpenClients)]
    [InlineData('3', (int)DashboardAction.OpenEmbeddings)]
    [InlineData('4', (int)DashboardAction.OpenRecentActivity)]
    [InlineData('5', (int)DashboardAction.OpenEnvironment)]
    public void NumberKey_resolvesToMatchingOpenAction(char number, int expectedInt)
    {
        // The same key resolves the same way from any view — the LoopState dispatches view
        // transitions regardless of the current view, so '2' from inside Embeddings still
        // jumps to Clients.
        var key = new ConsoleKeyInfo(number, ConsoleKey.D0 + (number - '0'), false, false, false);
        DashboardKeyMap.TryResolve(key, out var action).Should().BeTrue();
        action.Should().Be((DashboardAction)expectedInt);
    }

    [Fact]
    public void Esc_resolvesToGoHome()
    {
        var key = new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false);
        DashboardKeyMap.TryResolve(key, out var action).Should().BeTrue();
        action.Should().Be(DashboardAction.GoHome);
    }

    [Fact]
    public void HKey_resolvesToGoHome()
    {
        var key = new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false);
        DashboardKeyMap.TryResolve(key, out var action).Should().BeTrue();
        action.Should().Be(DashboardAction.GoHome);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Section-specific actions are gated to the matching view
    // ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reindex_inScopesView_isAllowed()
    {
        DashboardViewGating.IsAllowed(DashboardAction.ReindexScope, DashboardView.Scopes).Should().BeTrue();
    }

    [Theory]
    [InlineData((int)DashboardView.Clients)]
    [InlineData((int)DashboardView.Embeddings)]
    [InlineData((int)DashboardView.RecentActivity)]
    [InlineData((int)DashboardView.Environment)]
    [InlineData((int)DashboardView.Home)]
    public void Reindex_inOtherView_isDropped(int viewInt)
    {
        // Firing 'r' (ReindexScope) from a non-Scopes view should drop silently — no toast,
        // no Failure. The dispatcher returns without invoking the action.
        DashboardViewGating.IsAllowed(DashboardAction.ReindexScope, (DashboardView)viewInt).Should().BeFalse();
        DashboardViewGating.IsAllowed(DashboardAction.RebuildScope, (DashboardView)viewInt).Should().BeFalse();
    }

    [Fact]
    public void Wire_inClientsView_isAllowed()
    {
        DashboardViewGating.IsAllowed(DashboardAction.WireClient, DashboardView.Clients).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.UnwireClient, DashboardView.Clients).Should().BeTrue();
    }

    [Theory]
    [InlineData((int)DashboardView.Scopes)]
    [InlineData((int)DashboardView.Embeddings)]
    [InlineData((int)DashboardView.RecentActivity)]
    [InlineData((int)DashboardView.Environment)]
    [InlineData((int)DashboardView.Home)]
    public void Wire_inOtherView_isDropped(int viewInt)
    {
        DashboardViewGating.IsAllowed(DashboardAction.WireClient, (DashboardView)viewInt).Should().BeFalse();
        DashboardViewGating.IsAllowed(DashboardAction.UnwireClient, (DashboardView)viewInt).Should().BeFalse();
    }

    [Fact]
    public void Pull_inEmbeddingsView_isAllowed()
    {
        DashboardViewGating.IsAllowed(DashboardAction.EmbeddingsPull, DashboardView.Embeddings).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.EmbeddingsVerify, DashboardView.Embeddings).Should().BeTrue();
    }

    [Theory]
    [InlineData((int)DashboardView.Scopes)]
    [InlineData((int)DashboardView.Clients)]
    [InlineData((int)DashboardView.RecentActivity)]
    [InlineData((int)DashboardView.Environment)]
    [InlineData((int)DashboardView.Home)]
    public void Pull_inOtherView_isDropped(int viewInt)
    {
        DashboardViewGating.IsAllowed(DashboardAction.EmbeddingsPull, (DashboardView)viewInt).Should().BeFalse();
        DashboardViewGating.IsAllowed(DashboardAction.EmbeddingsVerify, (DashboardView)viewInt).Should().BeFalse();
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // View-agnostic actions always allowed
    // ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData((int)DashboardView.Home)]
    [InlineData((int)DashboardView.Scopes)]
    [InlineData((int)DashboardView.Clients)]
    [InlineData((int)DashboardView.Embeddings)]
    [InlineData((int)DashboardView.RecentActivity)]
    [InlineData((int)DashboardView.Environment)]
    public void Quit_isAllowedInEveryView(int viewInt)
    {
        DashboardViewGating.IsAllowed(DashboardAction.Quit, (DashboardView)viewInt).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.ForceRefresh, (DashboardView)viewInt).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.ToggleHelp, (DashboardView)viewInt).Should().BeTrue();
    }

    [Theory]
    [InlineData((int)DashboardView.Home)]
    [InlineData((int)DashboardView.Scopes)]
    [InlineData((int)DashboardView.Clients)]
    [InlineData((int)DashboardView.Embeddings)]
    [InlineData((int)DashboardView.RecentActivity)]
    [InlineData((int)DashboardView.Environment)]
    public void ViewTransitions_areAllowedInEveryView(int viewInt)
    {
        var view = (DashboardView)viewInt;
        DashboardViewGating.IsAllowed(DashboardAction.GoHome, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.OpenScopes, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.OpenClients, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.OpenEmbeddings, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.OpenRecentActivity, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.OpenEnvironment, view).Should().BeTrue();
    }

    [Theory]
    [InlineData((int)DashboardView.Home)]
    [InlineData((int)DashboardView.Scopes)]
    [InlineData((int)DashboardView.Clients)]
    [InlineData((int)DashboardView.Embeddings)]
    [InlineData((int)DashboardView.RecentActivity)]
    [InlineData((int)DashboardView.Environment)]
    public void GuidedActions_alwaysAllowed(int viewInt)
    {
        var view = (DashboardView)viewInt;
        DashboardViewGating.IsAllowed(DashboardAction.InitGuided, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.DemoGuided, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.OpenLogInPager, view).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.OpenConfigInEditor, view).Should().BeTrue();
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Display name / section mapping
    // ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData((int)DashboardView.Home, "Home")]
    [InlineData((int)DashboardView.Scopes, "Scopes")]
    [InlineData((int)DashboardView.Clients, "Clients")]
    [InlineData((int)DashboardView.Embeddings, "Embeddings")]
    [InlineData((int)DashboardView.RecentActivity, "Recent activity")]
    [InlineData((int)DashboardView.Environment, "Environment")]
    public void DashboardView_DisplayName(int viewInt, string expected)
    {
        ((DashboardView)viewInt).DisplayName().Should().Be(expected);
    }

    [Fact]
    public void DashboardView_Home_hasNoSection()
    {
        DashboardView.Home.ToSection().Should().BeNull();
    }

    [Theory]
    [InlineData((int)DashboardView.Scopes, (int)DashboardSection.Scopes)]
    [InlineData((int)DashboardView.Clients, (int)DashboardSection.Clients)]
    [InlineData((int)DashboardView.Embeddings, (int)DashboardSection.Embeddings)]
    [InlineData((int)DashboardView.RecentActivity, (int)DashboardSection.RecentActivity)]
    [InlineData((int)DashboardView.Environment, (int)DashboardSection.Environment)]
    public void DashboardView_DetailViews_mapToSections(int viewInt, int sectionInt)
    {
        ((DashboardView)viewInt).ToSection().Should().Be((DashboardSection)sectionInt);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Primary action dispatch (view-aware)
    // ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PrimaryAction_inEnvironment_isNone()
    {
        // Environment is read-only — Enter does nothing.
        DashboardPrimaryAction.ResolveForView(DashboardView.Environment, 0, MakeSnapshot())
            .Should().Be(DashboardAction.None);
    }

    [Fact]
    public void PrimaryAction_inRecentActivity_isNone()
    {
        // Recent activity has no first-class detail view yet — Enter surfaces a hint via toast.
        DashboardPrimaryAction.ResolveForView(DashboardView.RecentActivity, 0, MakeSnapshot())
            .Should().Be(DashboardAction.None);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Live-suspend classification (regression for "Enter on already-wired client crashed
    // with Spectre concurrency error" — the resolved UnwireClient must be classified as
    // suspend-Live so the main loop drops out of the Live region before ConfirmModal opens)
    // ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData((int)DashboardAction.UnwireClient, true)]
    [InlineData((int)DashboardAction.RemoveScope, true)]
    [InlineData((int)DashboardAction.RebuildScope, true)]
    [InlineData((int)DashboardAction.ReindexScope, true)]
    [InlineData((int)DashboardAction.AddScope, true)]
    [InlineData((int)DashboardAction.InitGuided, true)]
    [InlineData((int)DashboardAction.DemoGuided, true)]
    [InlineData((int)DashboardAction.OpenLogInPager, true)]
    [InlineData((int)DashboardAction.OpenConfigInEditor, true)]
    [InlineData((int)DashboardAction.WireClient, false)]
    [InlineData((int)DashboardAction.EmbeddingsPull, false)]
    [InlineData((int)DashboardAction.EmbeddingsVerify, false)]
    [InlineData((int)DashboardAction.MoveUp, false)]
    [InlineData((int)DashboardAction.PrimaryAction, false)]
    public void LiveSuspend_classifiesActions(int actionInt, bool requiresSuspend)
    {
        DashboardLiveSuspend.RequiresSuspend((DashboardAction)actionInt)
            .Should().Be(requiresSuspend);
    }

    [Fact]
    public void EnterOnWiredClient_resolvesToUnwire_andRequiresSuspend()
    {
        // The composition that previously crashed: Enter on Clients view + a wired client
        // resolves to UnwireClient (which shows ConfirmModal). The main loop relies on
        // RequiresSuspend(resolved) to drop Live BEFORE the prompt opens. If this returns
        // false we're back to the Spectre "interactive functions concurrently" exception.
        var snap = MakeSnapshot() with
        {
            Clients = new[]
            {
                new ClientRow("continue", "project", "/r/.continue/mcp/sourcegraph.yaml", true, true),
            },
        };
        var resolved = DashboardPrimaryAction.ResolveForView(DashboardView.Clients, 0, snap);
        resolved.Should().Be(DashboardAction.UnwireClient);
        DashboardLiveSuspend.RequiresSuspend(resolved).Should().BeTrue();
    }

    [Fact]
    public void EnterOnUnwiredClient_resolvesToWire_andStaysInLive()
    {
        // The other half of the toggle: an unwired client resolves to WireClient, which is
        // pure file IO and safely runs inside the Live region.
        var snap = MakeSnapshot() with
        {
            Clients = new[]
            {
                new ClientRow("continue", "project", "/r/.continue/mcp/sourcegraph.yaml", true, false),
            },
        };
        var resolved = DashboardPrimaryAction.ResolveForView(DashboardView.Clients, 0, snap);
        resolved.Should().Be(DashboardAction.WireClient);
        DashboardLiveSuspend.RequiresSuspend(resolved).Should().BeFalse();
    }

    [Fact]
    public void EnterOnScopesView_resolvesToReindex_andRequiresSuspend()
    {
        // Enter on Scopes view dispatches reindex, which shells out to `sourcegraph-mcp index`
        // with inherited stdio — must suspend Live for the same reason as the modal path.
        var resolved = DashboardPrimaryAction.ResolveForView(DashboardView.Scopes, 0, MakeSnapshot());
        resolved.Should().Be(DashboardAction.ReindexScope);
        DashboardLiveSuspend.RequiresSuspend(resolved).Should().BeTrue();
    }

    private static DashboardSnapshot MakeSnapshot() => new(
        Environment: new EnvironmentSurface(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: "/r",
            SolutionFiles: new[] { "/r/x.slnx" },
            SourceGraphConfigStatus: "valid",
            SourceGraphConfigError: null),
        Scopes: new[]
        {
            new ScopeRow("default", "ok", 1000, 2000, DateTimeOffset.UtcNow,
                Array.Empty<string>(), Array.Empty<string>(), false),
        },
        Clients: new[]
        {
            new ClientRow("claude-code", "project", "/r/.mcp.json", true, true),
        },
        Embeddings: new EmbeddingsSurface(
            ModelId: "test-model",
            CacheDir: "/h/.cache",
            CachePresent: true,
            TotalBytes: 1024,
            Verified: false),
        RecentActivity: Array.Empty<ActivityEntry>(),
        BuiltAt: DateTimeOffset.UtcNow,
        UsageLogPath: "/r/.sourcegraph/usage.jsonl",
        HealsLogPath: "/r/.sourcegraph/heals.jsonl",
        ExitCode: 0);
}
