using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// Renderer tests for the dashboard's home view + per-detail-view <see cref="IRenderable"/>
/// builders. Spectre's <see cref="AnsiConsole.Record()"/> API captures the rendered output as a
/// string for assertion; we don't compare against a frozen golden file because that would
/// couple the test to Spectre's internal escape sequences (which can shift across versions).
/// Instead we assert structural invariants: title appears, breadcrumb present on detail views,
/// <c>Selected:</c> drawer appears when selection is valid, ASCII fallback under <c>--no-leaf</c>.
/// </summary>
[Collection("CliConsole")]
public sealed class DashboardRendererTests : IDisposable
{
    private readonly bool _initialSuppressed;

    public DashboardRendererTests()
    {
        _initialSuppressed = LeafFormatter.Suppressed;
        LeafFormatter.Suppressed = false;
    }

    public void Dispose()
    {
        LeafFormatter.Suppressed = _initialSuppressed;
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Home view
    // ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildHome_emitsAllFiveSummaryLabels()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildHome(Healthy(), DefaultOptions(), menuIndex: 0));
        output.Should().Contain("Environment");
        output.Should().Contain("Scopes");
        output.Should().Contain("Clients");
        output.Should().Contain("Embeddings");
        output.Should().Contain("Recent activity");
    }

    [Fact]
    public void BuildHome_emitsAllFiveMenuNumbers()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildHome(Healthy(), DefaultOptions(), menuIndex: 0));
        // The numeric menu prefixes 1..5 should all appear.
        foreach (var n in new[] { "1", "2", "3", "4", "5" })
        {
            output.Should().Contain(n, $"menu should advertise number {n}");
        }
    }

    [Fact]
    public void BuildHome_highlightedRow_drawsSelectionDot()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildHome(Healthy(), DefaultOptions(), menuIndex: 2));
        // Brand-coloured fisheye ◉ marks the highlighted menu row; muted ○ for the rest.
        output.Should().Contain(DashboardTheme.SelectedDot);
        output.Should().Contain(DashboardTheme.UnselectedDot);
    }

    [Fact]
    public void BuildHome_emptyScopes_summaryRowIsOff()
    {
        var snap = Healthy() with { Scopes = Array.Empty<ScopeRow>() };
        var output = RenderToString(_ => DashboardRenderer.BuildHome(snap, DefaultOptions(), menuIndex: 0));
        // Empty-scopes summary text uses the "(none)" tag.
        output.Should().Contain("Scopes");
        output.Should().Contain("(none)");
    }

    [Fact]
    public void BuildHome_recentActivity_summarisesLastEvent()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildHome(Healthy(), DefaultOptions(), menuIndex: 0));
        // The "last: <kind>" segment is the most informative bit; assert it appears.
        output.Should().Contain("last:");
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Detail views
    // ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildScopesDetail_emitsRowsAndDrawer()
    {
        var snap = Healthy() with
        {
            Scopes = new[]
            {
                new ScopeRow("frontend", "ok", 1000, 2000,
                    new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
                    Array.Empty<string>(), Array.Empty<string>(), false),
                new ScopeRow("backend", "partial", 500, 1000,
                    new DateTimeOffset(2025, 1, 1, 11, 0, 0, TimeSpan.Zero),
                    new[] { "Legacy.csproj" }, Array.Empty<string>(), false),
            },
        };
        var output = RenderToString(_ => DashboardRenderer.BuildScopesDetail(snap, DefaultOptions(), selectedRow: 1));
        output.Should().Contain("frontend");
        output.Should().Contain("backend");
        // Selection drawer for the selected (backend) scope.
        output.Should().Contain("Selected: backend");
        // Failed-projects bullets for the partial scope.
        output.Should().Contain("Failed projects");
        output.Should().Contain("Legacy.csproj");
    }

    [Fact]
    public void BuildScopesDetail_emptyScopes_emitsParenthetical()
    {
        var snap = Healthy() with { Scopes = Array.Empty<ScopeRow>() };
        var output = RenderToString(_ => DashboardRenderer.BuildScopesDetail(snap, DefaultOptions()));
        output.Should().Contain("(no scopes registered");
    }

    [Fact]
    public void BuildScopesDetail_focused_drawsCursorOnSelectedRow()
    {
        var snap = Healthy() with
        {
            Scopes = new[]
            {
                new ScopeRow("a", "ok", 1, 1, null, Array.Empty<string>(), Array.Empty<string>(), false),
                new ScopeRow("b", "ok", 1, 1, null, Array.Empty<string>(), Array.Empty<string>(), false),
            },
        };
        var output = RenderToString(_ => DashboardRenderer.BuildScopesDetail(snap, DefaultOptions(), selectedRow: 1));
        // The new model: ◉ marks the selected row in the leading column; ○ marks non-selected.
        output.Should().Contain(DashboardTheme.SelectedDot);
        output.Should().Contain(DashboardTheme.UnselectedDot);
    }

    [Fact]
    public void BuildClientsDetail_emitsRowsAndDrawer()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildClientsDetail(Healthy(), DefaultOptions(), selectedRow: 0));
        output.Should().Contain("claude-code");
        output.Should().Contain("Selected: claude-code");
        output.Should().Contain("scope");
        output.Should().Contain("target");
        output.Should().Contain("state");
    }

    [Fact]
    public void BuildClientsDetail_emptyClients_emitsParentheticalNotice()
    {
        var snap = Healthy() with { Clients = Array.Empty<ClientRow>() };
        var output = RenderToString(_ => DashboardRenderer.BuildClientsDetail(snap, DefaultOptions()));
        output.Should().Contain("(no client configs detected)");
    }

    [Fact]
    public void BuildEmbeddingsDetail_emitsModelAndCache()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildEmbeddingsDetail(Healthy(), DefaultOptions()));
        output.Should().Contain("Model");
        output.Should().Contain("jina-embeddings");
        output.Should().Contain("Cache");
        output.Should().Contain("Verified");
    }

    [Fact]
    public void BuildEmbeddingsDetail_absentCache_marksAsAbsent()
    {
        var snap = Healthy() with
        {
            Embeddings = new EmbeddingsSurface(
                ModelId: "jinaai/jina-embeddings-v2-base-code",
                CacheDir: "/h/.cache",
                CachePresent: false,
                TotalBytes: 0,
                Verified: false),
        };
        var output = RenderToString(_ => DashboardRenderer.BuildEmbeddingsDetail(snap, DefaultOptions()));
        output.Should().Contain("(absent)");
    }

    [Fact]
    public void BuildRecentActivityDetail_emitsTimes_andDetails()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildRecentActivityDetail(Healthy(), DefaultOptions(), selectedRow: 0));
        output.Should().Contain("search_symbols");
        output.Should().Contain("Selected:");
    }

    [Fact]
    public void BuildRecentActivityDetail_emptyActivity_emitsParenthetical()
    {
        var snap = Healthy() with { RecentActivity = Array.Empty<ActivityEntry>() };
        var output = RenderToString(_ => DashboardRenderer.BuildRecentActivityDetail(snap, DefaultOptions()));
        output.Should().Contain("(no recorded activity)");
    }

    [Fact]
    public void BuildEnvironmentDetail_emitsKeysOnly_noRowSelection()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildEnvironmentDetail(Healthy(), DefaultOptions()));
        output.Should().Contain(".NET SDK");
        output.Should().Contain("git on PATH");
        output.Should().Contain("repo root");
        output.Should().Contain(".sourcegraph.json");
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Header / footer / fallback
    // ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildHeader_home_includesBrandMarkAndPath()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildHeader(Healthy(), DefaultOptions(), DashboardView.Home));
        output.Should().Contain("SourceGraph");
        if (!LeafFormatter.Suppressed)
            output.Should().Contain("🌿");
        else
            output.Should().Contain("[x]");
    }

    [Fact]
    public void BuildHeader_detailView_includesBreadcrumbAndBackHint()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildHeader(Healthy(), DefaultOptions(), DashboardView.Clients));
        output.Should().Contain("SourceGraph");
        // Section name in the breadcrumb.
        output.Should().Contain("Clients");
        // Back-to-home hint right-aligned.
        output.Should().Contain("back to home");
    }

    [Fact]
    public void BuildHeader_detailView_includesBreadcrumbForRecentActivity()
    {
        // The Recent Activity view uses a multi-word display name; make sure that name appears.
        var output = RenderToString(_ => DashboardRenderer.BuildHeader(Healthy(), DefaultOptions(), DashboardView.RecentActivity));
        output.Should().Contain("Recent activity");
    }

    [Fact]
    public void BuildFooter_home_advertisesHomeKeys()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildFooter(default, DashboardView.Home));
        // Home footer mentions menu navigation, jump, help, quit.
        output.Should().Contain("select");
        output.Should().Contain("open");
        output.Should().Contain("quit");
    }

    [Fact]
    public void BuildFooter_scopesView_advertisesReindexAndRebuild()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildFooter(default, DashboardView.Scopes));
        output.Should().Contain("reindex");
        output.Should().Contain("rebuild");
        output.Should().Contain("home");
    }

    [Fact]
    public void BuildFooter_clientsView_advertisesWireAndUnwire()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildFooter(default, DashboardView.Clients));
        output.Should().Contain("wire");
        output.Should().Contain("unwire");
        output.Should().Contain("home");
    }

    [Fact]
    public void BuildFooter_embeddingsView_advertisesPullAndVerify()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildFooter(default, DashboardView.Embeddings));
        output.Should().Contain("pull");
        output.Should().Contain("verify");
        output.Should().Contain("home");
    }

    [Fact]
    public void BuildFooter_environmentView_advertisesHomeAndQuitOnly()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildFooter(default, DashboardView.Environment));
        output.Should().Contain("home");
        output.Should().Contain("quit");
        // No wire / reindex / pull keys — Environment is read-only.
        output.Should().NotContain("reindex");
        output.Should().NotContain("pull");
    }

    [Fact]
    public void Render_noLeaf_substitutesAsciiGlyphs()
    {
        LeafFormatter.Suppressed = true;
        // Use the Environment detail builder — it's the simplest dot-bearing view and renders
        // each dot in a stable position regardless of column wrap. Spectre's default test
        // console width can split [x] across two visual lines if the row's text column wraps;
        // Environment detail has short row content and the dot survives intact.
        var output = RenderToString(_ => DashboardRenderer.BuildEnvironmentDetail(Healthy(), DefaultOptions()));
        output.Should().NotContain("🌿");
        // ASCII fallback tokens used by DashboardTheme in --no-leaf mode.
        output.Should().MatchRegex(@"\[[ xX!\-]\]");
    }

    [Fact]
    public void FormatRelativeTime_shortDelta_secondsAgo()
    {
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromSeconds(45)).Should().Be("45s ago");
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromMinutes(11)).Should().Be("11m ago");
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromHours(5)).Should().Be("5h ago");
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromDays(3)).Should().Be("3d ago");
    }

    [Fact]
    public void SelectionDot_default_returnsFisheyeAndHollowCircle()
    {
        // Default mode: selected → brand-coloured fisheye ◉; not-selected → muted hollow ○.
        var sel = DashboardTheme.SelectionDot(selected: true);
        var unsel = DashboardTheme.SelectionDot(selected: false);
        sel.Should().Contain(DashboardTheme.SelectedDot);
        sel.Should().Contain(DashboardTheme.Brand);
        unsel.Should().Contain(DashboardTheme.UnselectedDot);
        unsel.Should().Contain(DashboardTheme.Muted);
    }

    [Fact]
    public void SelectionDot_noLeaf_returnsBracketTokens()
    {
        // Under --no-leaf: selected → [[>]], not-selected → [[ ]] (escape-doubled brackets so
        // Spectre's Markup parser doesn't interpret them).
        LeafFormatter.Suppressed = true;
        var sel = DashboardTheme.SelectionDot(selected: true);
        var unsel = DashboardTheme.SelectionDot(selected: false);
        sel.Should().Be("[[>]]");
        unsel.Should().Be("[[ ]]");
    }

    [Fact]
    public void HomeMenuEntries_haveStableShape()
    {
        // The menu drives both the renderer (selection cursor) and the dispatcher (number-key
        // jumps land on the right view). Lock in the order so a refactor can't accidentally
        // remap '3' from Embeddings to something else.
        DashboardRenderer.HomeMenuEntries.Should().HaveCount(5);
        DashboardRenderer.HomeMenuEntries[0].View.Should().Be(DashboardView.Scopes);
        DashboardRenderer.HomeMenuEntries[1].View.Should().Be(DashboardView.Clients);
        DashboardRenderer.HomeMenuEntries[2].View.Should().Be(DashboardView.Embeddings);
        DashboardRenderer.HomeMenuEntries[3].View.Should().Be(DashboardView.RecentActivity);
        DashboardRenderer.HomeMenuEntries[4].View.Should().Be(DashboardView.Environment);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Cell / Viewport helpers (the deterministic-row-layout primitives that replaced Spectre Grid)
    // ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo", 8, "foo     ")]                  // pads right
    [InlineData("toolongname", 8, "toolong…")]          // truncates with ellipsis
    [InlineData("", 5, "     ")]                        // empty pads to width
    [InlineData("a", 1, "a")]                           // exact fit
    [InlineData("ab", 1, "…")]                          // collapses to just the ellipsis at width 1
    public void Cell_padsAndTruncatesAsExpected(string plain, int width, string expected)
    {
        // Cell with no color tag returns the raw padded/truncated text (with Markup.Escape applied).
        // For the inputs here, none of the characters are markup-significant so Markup.Escape is a no-op.
        DashboardRenderer.Cell(plain, width).Should().Be(expected);
    }

    [Fact]
    public void Cell_appliesColorTag_andTruncates()
    {
        // The colour wraps the truncated visible text; padding is OUTSIDE the colour tag so the
        // ANSI reset doesn't apply to the trailing spaces.
        var result = DashboardRenderer.Cell("toolong", 5, "red");
        result.Should().Be("[red]tool…[/]");
    }

    [Fact]
    public void Cell_rightAligns()
    {
        var result = DashboardRenderer.Cell("42", 5, rightAligned: true);
        result.Should().Be("   42");
    }

    [Fact]
    public void Viewport_listFitsWithinWindow_returnsFullRange()
    {
        var (start, end, above, below) = DashboardRenderer.Viewport(totalRows: 3, selectedRow: 1, maxVisible: 8);
        start.Should().Be(0);
        end.Should().Be(3);
        above.Should().Be(0);
        below.Should().Be(0);
    }

    [Fact]
    public void Viewport_listLargerThanWindow_centersOnSelected()
    {
        // 20 rows, 8 visible, selected at row 10 → window centered around row 10.
        var (start, end, above, below) = DashboardRenderer.Viewport(totalRows: 20, selectedRow: 10, maxVisible: 8);
        (end - start).Should().Be(8);
        (start <= 10 && 10 < end).Should().BeTrue("selected row must be inside the window");
        above.Should().BeGreaterThan(0);
        below.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Viewport_selectedNearEnd_clampsToTail()
    {
        // 20 rows, 8 visible, selected at last row → window slides to the end.
        var (start, end, above, below) = DashboardRenderer.Viewport(totalRows: 20, selectedRow: 19, maxVisible: 8);
        start.Should().Be(12);
        end.Should().Be(20);
        above.Should().Be(12);
        below.Should().Be(0);
    }

    [Fact]
    public void Viewport_selectedAtStart_clampsToHead()
    {
        var (start, end, above, below) = DashboardRenderer.Viewport(totalRows: 20, selectedRow: 0, maxVisible: 8);
        start.Should().Be(0);
        end.Should().Be(8);
        above.Should().Be(0);
        below.Should().Be(12);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────────

    private static string RenderToString(Func<IAnsiConsole, IRenderable> build)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            // Plain output for tests — no colour codes to slice through.
            ColorSystem = ColorSystemSupport.NoColors,
            Ansi = AnsiSupport.No,
            Out = new AnsiConsoleOutput(new StringWriter()),
        });
        console.Write(build(console));
        return ((StringWriter)console.Profile.Out.Writer).ToString();
    }

    private static DashboardRenderOptions DefaultOptions() =>
        new(Root: "/r", Home: "/h", Version: "0.8.0");

    private static DashboardSnapshot Healthy() => new(
        Environment: new EnvironmentSurface(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: "/r",
            SolutionFiles: new[] { "/r/x.slnx" },
            SourceGraphConfigStatus: "valid",
            SourceGraphConfigError: null),
        Scopes: new[]
        {
            new ScopeRow("default", "ok", 1000, 2000,
                new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
                Array.Empty<string>(), Array.Empty<string>(), false),
        },
        Clients: new[]
        {
            new ClientRow("claude-code", "project", "/r/.mcp.json", true, true),
        },
        Embeddings: new EmbeddingsSurface(
            ModelId: "jinaai/jina-embeddings-v2-base-code",
            CacheDir: "/h/.cache/devbitslab.sourcegraph/models",
            CachePresent: true,
            TotalBytes: 614 * 1024 * 1024,
            Verified: false),
        RecentActivity: new[]
        {
            new ActivityEntry(DateTimeOffset.UtcNow.AddMinutes(-1), "tool_call", "default", true, 3, "search_symbols"),
        },
        BuiltAt: DateTimeOffset.UtcNow,
        UsageLogPath: "/r/.sourcegraph/usage.jsonl",
        HealsLogPath: "/r/.sourcegraph/heals.jsonl",
        ExitCode: 0);
}
