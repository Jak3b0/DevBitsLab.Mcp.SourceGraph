using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// Renderer tests for the dashboard's per-section <see cref="IRenderable"/> builders. Spectre's
/// <see cref="AnsiConsole.Record()"/> API captures the rendered output as a string for assertion;
/// we don't compare against a frozen golden file because that would couple the test to Spectre's
/// internal escape sequences (which can shift across versions). Instead we assert structural
/// invariants: section title appears, glyph language for each surface, ASCII fallback under
/// <c>--no-leaf</c>.
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

    [Fact]
    public void BuildEnvironmentSection_emitsHeading_andEveryRow()
    {
        var output = RenderToString(r =>
            DashboardRenderer.BuildEnvironmentSection(Healthy(), DefaultOptions()));
        output.Should().Contain("Environment");
        output.Should().Contain(".NET SDK");
        output.Should().Contain(".sourcegraph.json");
    }

    [Fact]
    public void BuildScopesSection_emitsHeading_andEachScopeName()
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
        var output = RenderToString(_ => DashboardRenderer.BuildScopesSection(snap, DefaultOptions()));
        output.Should().Contain("Scopes");
        output.Should().Contain("frontend");
        output.Should().Contain("backend");
    }

    [Fact]
    public void BuildScopesSection_focused_drawsCursorOnSelectedRow()
    {
        var snap = Healthy() with
        {
            Scopes = new[]
            {
                new ScopeRow("a", "ok", 1, 1, null, Array.Empty<string>(), Array.Empty<string>(), false),
                new ScopeRow("b", "ok", 1, 1, null, Array.Empty<string>(), Array.Empty<string>(), false),
            },
        };
        var output = RenderToString(_ =>
            DashboardRenderer.BuildScopesSection(snap, DefaultOptions(), focused: true, selectedRow: 1));
        // The cursor character ▶ should be present when focused.
        output.Should().Contain("▶");
    }

    [Fact]
    public void BuildClientsSection_emitsHeading_andRows()
    {
        var output = RenderToString(_ =>
            DashboardRenderer.BuildClientsSection(Healthy(), DefaultOptions()));
        output.Should().Contain("Clients");
        output.Should().Contain("claude-code");
    }

    [Fact]
    public void BuildClientsSection_emptyClients_emitsParentheticalNotice()
    {
        var snap = Healthy() with { Clients = Array.Empty<ClientRow>() };
        var output = RenderToString(_ => DashboardRenderer.BuildClientsSection(snap, DefaultOptions()));
        output.Should().Contain("(no client configs detected)");
    }

    [Fact]
    public void BuildEmbeddingsSection_emitsModelIdAndCacheSize()
    {
        var output = RenderToString(_ =>
            DashboardRenderer.BuildEmbeddingsSection(Healthy(), DefaultOptions()));
        output.Should().Contain("Embeddings");
        output.Should().Contain("jina-embeddings");
        output.Should().Contain("MiB");
    }

    [Fact]
    public void BuildRecentActivitySection_emitsTimes_andDetails()
    {
        var output = RenderToString(_ =>
            DashboardRenderer.BuildRecentActivitySection(Healthy(), DefaultOptions()));
        output.Should().Contain("Recent activity");
        output.Should().Contain("search_symbols");
    }

    [Fact]
    public void BuildRecentActivitySection_emptyActivity_emitsParenthetical()
    {
        var snap = Healthy() with { RecentActivity = Array.Empty<ActivityEntry>() };
        var output = RenderToString(_ =>
            DashboardRenderer.BuildRecentActivitySection(snap, DefaultOptions()));
        output.Should().Contain("(no recorded activity)");
    }

    [Fact]
    public void Render_noLeaf_substitutesAsciiGlyphs()
    {
        LeafFormatter.Suppressed = true;
        var snap = Healthy();
        var output = RenderToString(_ => DashboardRenderer.BuildEnvironmentSection(snap, DefaultOptions()));
        output.Should().NotContain("🌿");
        // ASCII fallback tokens used by StateGlyph in --no-leaf mode.
        output.Should().MatchRegex(@"\[[ xX!\-]\]");
    }

    [Fact]
    public void BuildHeader_includesBrandMarkAndPath()
    {
        var snap = Healthy();
        var output = RenderToString(_ => DashboardRenderer.BuildHeader(snap, DefaultOptions()));
        // The header should mention SourceGraph + version + the relative-rendered root.
        output.Should().Contain("SourceGraph");
        // Brand mark in emoji mode; ASCII fallback under no-leaf.
        if (!LeafFormatter.Suppressed)
            output.Should().Contain("🌿");
        else
            output.Should().Contain("[x]");
    }

    [Fact]
    public void BuildFooter_containsKeyHint()
    {
        var output = RenderToString(_ => DashboardRenderer.BuildFooter());
        output.Should().Contain("quit");
        output.Should().Contain("help");
    }

    [Fact]
    public void FormatRelativeTime_shortDelta_secondsAgo()
    {
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromSeconds(45)).Should().Be("45s ago");
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromMinutes(11)).Should().Be("11m ago");
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromHours(5)).Should().Be("5h ago");
        DashboardRenderer.FormatRelativeTime(TimeSpan.FromDays(3)).Should().Be("3d ago");
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
        new(Root: "/r", Home: "/h", NoColor: false, Version: "0.8.0");

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
