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
