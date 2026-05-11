using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Rendering;

/// <summary>
/// Covers <see cref="InitRenderer"/>: phase-header presence (◆-prefixed), indentation invariant
/// under headers, dot-glyph presence in Apply rows, and the "omit when inputs empty" rule for
/// <c>Pre-warm</c>. After the visual was unified with the dashboard the row tokens are
/// <c>●</c>/<c>○</c>/<c>◐</c>/<c>✗</c>/<c>−</c>; the leaf <c>🌿</c> stays only on the banner.
/// </summary>
[Collection("CliConsole")]
public sealed class InitRendererTests : IDisposable
{
    private readonly bool _initialSuppressed;

    public InitRendererTests()
    {
        _initialSuppressed = LeafFormatter.Suppressed;
        LeafFormatter.Suppressed = false;
    }

    public void Dispose()
    {
        LeafFormatter.Suppressed = _initialSuppressed;
    }

    [Fact]
    public void RenderBanner_emitsHeading()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderBanner(sw);
        sw.ToString().Should().Contain("SourceGraph init");
    }

    [Fact]
    public void RenderBanner_underNoLeaf_omitsLeafGlyph()
    {
        LeafFormatter.Suppressed = true;
        using var sw = new StringWriter();
        InitRenderer.RenderBanner(sw);
        sw.ToString().Should().NotContain("🌿");
    }

    [Fact]
    public void RenderEnvironment_includesHeading_andIndentsRows()
    {
        var detection = new OnboardingDetectionResult(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: "/tmp/myrepo",
            SolutionFiles: new[] { "/tmp/myrepo/MyApp.slnx" },
            SourceGraphConfigStatus: SourceGraphConfigStatus.Missing,
            SourceGraphConfigError: null,
            ClientConfigsDetected: Array.Empty<DetectedClientConfig>());
        using var sw = new StringWriter();
        InitRenderer.RenderEnvironment(sw, detection, root: "/tmp/myrepo", home: "/home/test");
        var lines = sw.ToString().Split('\n');
        // The heading is the section-leader (◆) followed by the phase name.
        lines.Should().Contain(l => l.Contains("Environment") && !l.StartsWith(" "),
            "phase heading at left margin");
        // Every non-empty, non-heading line under the phase is indented four spaces (matches the
        // dashboard's detail-view body indent).
        var phaseRows = lines.SkipWhile(l => !l.Contains("Environment") || l.StartsWith(" "))
            .Skip(1)
            .TakeWhile(l => !string.IsNullOrWhiteSpace(l) && !IsPhaseHeading(l));
        foreach (var row in phaseRows)
        {
            row.Should().StartWith("    ", $"row '{row}' must be indented four spaces under Environment");
        }
    }

    [Fact]
    public void RenderEnvironment_emitsOkDotForPassRow_andWarnForMissingGit()
    {
        var detection = new OnboardingDetectionResult(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: false,
            RepoRootPath: "/tmp/myrepo",
            SolutionFiles: new[] { "/tmp/myrepo/MyApp.slnx" },
            SourceGraphConfigStatus: SourceGraphConfigStatus.Missing,
            SourceGraphConfigError: null,
            ClientConfigsDetected: Array.Empty<DetectedClientConfig>());
        using var sw = new StringWriter();
        InitRenderer.RenderEnvironment(sw, detection, root: "/tmp/myrepo", home: "/home/test");
        var output = sw.ToString();
        // Leaves are reserved for the banner; row-status uses the dot vocabulary.
        output.Should().NotContain("🌿");
        output.Should().Contain("●"); // pass rows
        output.Should().Contain("◐"); // git-missing row (warn → half-circle)
    }

    [Fact]
    public void RenderEnvironment_emitsSectionLeader()
    {
        var detection = new OnboardingDetectionResult(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: "/tmp/myrepo",
            SolutionFiles: new[] { "/tmp/myrepo/MyApp.slnx" },
            SourceGraphConfigStatus: SourceGraphConfigStatus.Missing,
            SourceGraphConfigError: null,
            ClientConfigsDetected: Array.Empty<DetectedClientConfig>());
        using var sw = new StringWriter();
        InitRenderer.RenderEnvironment(sw, detection, root: "/tmp/myrepo", home: "/home/test");
        // ◆ section leader precedes the heading in brand position.
        sw.ToString().Should().Contain("◆ Environment");
    }

    [Fact]
    public void RenderClientsToWire_emitsHeadingAndIndentedRows()
    {
        var rows = new[]
        {
            new ClientPickerRow("claude-code", DefaultOn: true, Scope: "project", Detail: ".mcp.json"),
            new ClientPickerRow("cursor", DefaultOn: false, Scope: "project", Detail: ".cursor/mcp.json"),
        };
        using var sw = new StringWriter();
        InitRenderer.RenderClientsToWire(sw, rows);
        var lines = sw.ToString().Split('\n');
        lines.Should().Contain(l => l.Contains("Clients to wire") && !l.StartsWith(" "));
        var slugRows = lines.Where(l => l.Contains("claude-code") || l.Contains("cursor")).ToList();
        slugRows.Should().AllSatisfy(l => l.Should().StartWith("    "), "rows under headings are 4-space indented");
        // Default-on row has the ok-dot; default-off has the off-dot.
        sw.ToString().Should().Contain("●").And.Contain("○");
    }

    [Fact]
    public void RenderClientsToWire_emptyRows_emitsNothing()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderClientsToWire(sw, Array.Empty<ClientPickerRow>());
        sw.ToString().Should().BeEmpty();
    }

    [Fact]
    public void RenderApplyRow_insertEmitsOkDotAndVerb()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderApplyRow(sw,
            WriterAction.Insert,
            slug: "claude-code",
            targetPath: "/tmp/myrepo/.mcp.json",
            description: "would create new config",
            root: "/tmp/myrepo",
            home: "/home/test");
        var line = sw.ToString();
        line.Should().StartWith("    ● ").And.Contain("wrote").And.Contain("claude-code").And.Contain(".mcp.json");
    }

    [Fact]
    public void RenderApplyRow_skipExistingDiffersEmitsFailDotAndDescription()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderApplyRow(sw,
            WriterAction.SkipExistingDiffers,
            slug: "claude-code",
            targetPath: "/tmp/myrepo/.mcp.json",
            description: "existing differs",
            root: "/tmp/myrepo",
            home: "/home/test");
        var output = sw.ToString();
        output.Should().Contain("✗ ").And.Contain("conflict").And.Contain("existing differs");
    }

    [Fact]
    public void RenderApplyRow_skipHasCommentsEmitsWarnDotAndDescription()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderApplyRow(sw,
            WriterAction.SkipHasComments,
            slug: "claude-code",
            targetPath: "/tmp/myrepo/.mcp.json",
            description: "config has comments at /tmp/.mcp.json — please paste manually",
            root: "/tmp/myrepo",
            home: "/home/test");
        var output = sw.ToString();
        output.Should().Contain("◐ ").And.Contain("comments");
    }

    [Fact]
    public void RenderApplyRow_skipUnsupportedEmitsDashDotAndDescription()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderApplyRow(sw,
            WriterAction.SkipUnsupported,
            slug: "copilot",
            targetPath: "/some/path",
            description: "user-scope Copilot wiring is not supported in v1",
            root: "/tmp/myrepo",
            home: "/home/test");
        var output = sw.ToString();
        output.Should().Contain("− ").And.Contain("unsupported").And.Contain("user-scope Copilot");
    }

    [Fact]
    public void RenderApplyRow_noOpEmitsOkDotAndNoChange()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderApplyRow(sw,
            WriterAction.NoOpAlreadyMatches,
            slug: "claude-code",
            targetPath: "/tmp/myrepo/.mcp.json",
            description: "already wired",
            root: "/tmp/myrepo",
            home: "/home/test");
        var line = sw.ToString();
        line.Should().Contain("●").And.Contain("no change");
    }

    [Fact]
    public void RenderPreWarmSummary_exitZero_emitsOkDotAndElapsed()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderPreWarmSummary(sw, exitCode: 0, elapsed: TimeSpan.FromSeconds(11.4), solutionName: "MyApp.slnx");
        sw.ToString().Should().Contain("●").And.Contain("indexed").And.Contain("MyApp.slnx").And.Contain("11.4");
    }

    [Fact]
    public void RenderPreWarmSummary_nonZeroExit_emitsWarn()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderPreWarmSummary(sw, exitCode: 2, elapsed: TimeSpan.FromSeconds(7.0), solutionName: "MyApp.slnx");
        sw.ToString().Should().Contain("◐").And.Contain("pre-warm exit 2");
    }

    [Fact]
    public void RenderNext_empty_emitsNothing()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderNext(sw, Array.Empty<string>());
        sw.ToString().Should().BeEmpty();
    }

    [Fact]
    public void RenderNext_emitsHeadingAndIndentedSuggestions()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderNext(sw, new[] { "Run sourcegraph-mcp demo", "Verify with usage_stats" });
        var lines = sw.ToString().Split('\n');
        lines.Should().Contain(l => l.Contains("Next") && !l.StartsWith(" "));
        var nextRows = lines.SkipWhile(l => !l.Contains("Next") || l.StartsWith(" "))
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l));
        nextRows.Should().AllSatisfy(l => l.Should().StartWith("    "));
    }

    [Fact]
    public void NoLeaf_substitutesAsciiTokens_forRowsAndHeader()
    {
        // Under --no-leaf the dot vocabulary collapses to the bracketed ASCII tokens; the section
        // leader downgrades to [*] so the header still leads with a 3-cell glyph.
        LeafFormatter.Suppressed = true;
        var detection = new OnboardingDetectionResult(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: "/tmp/myrepo",
            SolutionFiles: new[] { "/tmp/myrepo/MyApp.slnx" },
            SourceGraphConfigStatus: SourceGraphConfigStatus.Missing,
            SourceGraphConfigError: null,
            ClientConfigsDetected: Array.Empty<DetectedClientConfig>());
        using var sw = new StringWriter();
        InitRenderer.RenderEnvironment(sw, detection, root: "/tmp/myrepo", home: "/home/test");
        var output = sw.ToString();
        output.Should().NotContain("●").And.NotContain("◐").And.NotContain("✗").And.NotContain("○").And.NotContain("−");
        output.Should().NotContain("◆");
        // ASCII row tokens.
        output.Should().Contain("[x] ");
        // Header substitution.
        output.Should().Contain("[*] Environment");
    }

    private static bool IsPhaseHeading(string line)
    {
        // Heuristic: known phase names appear at left margin (after ◆ or [*] section leader).
        return (line.StartsWith("◆") || line.StartsWith("[*]"))
            && (line.Contains("Environment") || line.Contains("Clients to wire")
                || line.Contains("Apply") || line.Contains("Pre-warm") || line.Contains("Next"));
    }
}
