using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Rendering;

/// <summary>
/// Tests for <see cref="StatusRenderer"/>: ◆-prefixed phase headings, four-space indentation
/// invariant under each heading, the dot vocabulary in both emoji and ASCII modes, and the
/// per-row mappings (partial scope → warn dot + hanging detail; degraded scope → fail dot +
/// repair hint).
/// </summary>
[Collection("CliConsole")]
public sealed class StatusRendererTests : IDisposable
{
    private readonly bool _initialSuppressed;

    public StatusRendererTests()
    {
        _initialSuppressed = LeafFormatter.Suppressed;
        LeafFormatter.Suppressed = false;
    }

    public void Dispose()
    {
        LeafFormatter.Suppressed = _initialSuppressed;
    }

    [Fact]
    public void Render_healthy_emitsFivePhaseHeadings()
    {
        var snap = Healthy();
        var output = Render(snap);
        output.Should().Contain("Environment").And.Contain("Scopes").And.Contain("Clients")
            .And.Contain("Embeddings").And.Contain("Recent activity");
        // Section leader ◆ should appear on each heading.
        output.Should().Contain("◆ Environment");
        output.Should().Contain("◆ Scopes");
        output.Should().Contain("◆ Clients");
        output.Should().Contain("◆ Embeddings");
        output.Should().Contain("◆ Recent activity");
    }

    [Fact]
    public void Render_eachRow_underHeading_isFourSpaceIndented()
    {
        var snap = Healthy();
        var output = Render(snap);
        // Spot-check: after each phase's "◆ <name>" line, the next non-blank line must start
        // with four spaces (the body indent under the section leader).
        var lines = output.Split('\n');
        var headings = new[] { "Environment", "Scopes", "Clients", "Embeddings", "Recent activity" };
        foreach (var heading in headings)
        {
            var idx = Array.FindIndex(lines, l => l.TrimEnd() == $"◆ {heading}");
            idx.Should().BeGreaterThan(-1, $"phase heading '◆ {heading}' should appear");
            // Find the next non-blank line after the heading.
            var next = lines.Skip(idx + 1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            next.Should().NotBeNull();
            next!.Should().StartWith("    ", $"rows under '◆ {heading}' should be four-space indented");
        }
    }

    [Fact]
    public void Render_partialScope_emitsWarnDotAndFailedProjects()
    {
        var snap = Healthy() with
        {
            Scopes = new[]
            {
                new ScopeRow("backend", "partial", 100, 200,
                    new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
                    new[] { "Legacy.csproj", "Old.csproj" },
                    Array.Empty<string>(),
                    false),
            },
        };
        var output = Render(snap);
        // Emoji-mode: ◐ (half-circle / warn) for partial.
        output.Should().Contain("◐");
        output.Should().Contain("backend");
        output.Should().Contain("Legacy.csproj");
        output.Should().Contain("Old.csproj");
    }

    [Fact]
    public void Render_degradedScope_emitsFailDotAndRepairHint()
    {
        var snap = Healthy() with
        {
            Scopes = new[]
            {
                new ScopeRow("a", "degraded", 0, 0, null,
                    Array.Empty<string>(), Array.Empty<string>(), false),
            },
        };
        var output = Render(snap);
        output.Should().Contain("✗");
        output.Should().Contain("repair_scope");
    }

    [Fact]
    public void Render_noLeaf_substitutesAsciiGlyphs()
    {
        LeafFormatter.Suppressed = true;
        var snap = Healthy();
        var output = Render(snap);
        output.Should().NotContain("🌿");
        // None of the emoji dot characters should appear.
        output.Should().NotContain("●").And.NotContain("○").And.NotContain("◐").And.NotContain("✗").And.NotContain("−");
        // ◆ section leader downgrades to [*].
        output.Should().NotContain("◆");
        output.Should().Contain("[*] Environment");
        // Three-cell-wide ASCII row tokens: [x] / [ ] / [!] / [X] / [-]
        output.Should().MatchRegex(@"\[[ xX!\-]\] ");
        // Phase headings render the same name in both modes.
        output.Should().Contain("Environment").And.Contain("Scopes");
    }

    [Fact]
    public void Render_emptyClients_emitsParentheticalNotice()
    {
        var snap = Healthy() with { Clients = Array.Empty<ClientRow>() };
        var output = Render(snap);
        output.Should().Contain("(no client configs detected)");
    }

    [Fact]
    public void Render_emptyActivity_emitsParentheticalNotice()
    {
        var snap = Healthy() with { RecentActivity = Array.Empty<ActivityEntry>() };
        var output = Render(snap);
        output.Should().Contain("(no recorded activity)");
    }

    [Fact]
    public void Render_clientWithEntry_emitsOkDot()
    {
        var snap = Healthy() with
        {
            Clients = new[]
            {
                new ClientRow("claude-code", "project", "/r/.mcp.json", true, true),
                new ClientRow("cursor", "user", "/h/.cursor/mcp.json", true, false),
                new ClientRow("continue", "project", "/r/.continue/mcp/sourcegraph.yaml", false, false),
            },
        };
        var output = Render(snap);
        var lines = output.Split('\n');
        // claude-code with entry → ● (ok).
        lines.Should().Contain(l => l.Contains("claude-code") && l.Contains("●"));
        // cursor exists but no entry → ○ (off).
        lines.Should().Contain(l => l.Contains("cursor") && l.Contains("○"));
        // continue absent → − (unsupported).
        lines.Should().Contain(l => l.Contains("continue") && l.Contains("−"));
    }

    [Fact]
    public void FormatRelativeTime_shortDelta_secondsAgo()
    {
        StatusRenderer.FormatRelativeTime(TimeSpan.FromSeconds(45)).Should().Be("45s ago");
        StatusRenderer.FormatRelativeTime(TimeSpan.FromMinutes(11)).Should().Be("11m ago");
        StatusRenderer.FormatRelativeTime(TimeSpan.FromHours(5)).Should().Be("5h ago");
        StatusRenderer.FormatRelativeTime(TimeSpan.FromDays(3)).Should().Be("3d ago");
    }

    private static string Render(DashboardSnapshot snap)
    {
        var sw = new StringWriter();
        var options = new StatusRenderOptions(Root: "/r", Home: "/h", NoColor: false);
        StatusRenderer.RenderHuman(snap, sw, options);
        return sw.ToString();
    }

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
