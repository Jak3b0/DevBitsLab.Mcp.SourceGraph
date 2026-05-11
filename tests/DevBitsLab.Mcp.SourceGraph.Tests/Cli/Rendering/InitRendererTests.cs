using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Rendering;

/// <summary>
/// Covers <see cref="InitRenderer"/>: phase-header presence, two-space indentation invariant
/// under headers, glyph-presence in Apply rows, and the "omit when inputs empty" rule for
/// <c>Pre-warm</c>.
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
        lines.Should().Contain(l => l.StartsWith("Environment"), "phase heading at left margin");
        // Every non-empty, non-heading line under the phase is indented two spaces.
        var phaseRows = lines.SkipWhile(l => !l.StartsWith("Environment")).Skip(1)
            .TakeWhile(l => !string.IsNullOrWhiteSpace(l) && !IsPhaseHeading(l));
        foreach (var row in phaseRows)
        {
            row.Should().StartWith("  ", $"row '{row}' must be indented two spaces under Environment");
        }
    }

    [Fact]
    public void RenderEnvironment_emitsLeafForPassRow_andWarnForMissingGit()
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
        output.Should().Contain("🌿"); // pass rows
        output.Should().Contain("⚠"); // git-missing row
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
        lines.Should().Contain(l => l.StartsWith("Clients to wire"));
        var slugRows = lines.Where(l => l.Contains("claude-code") || l.Contains("cursor")).ToList();
        slugRows.Should().AllSatisfy(l => l.Should().StartWith("  "), "rows under headings are 2-space indented");
        // Default-on row has the leaf glyph; default-off has the middle-dot.
        sw.ToString().Should().Contain("🌿").And.Contain("·");
    }

    [Fact]
    public void RenderClientsToWire_emptyRows_emitsNothing()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderClientsToWire(sw, Array.Empty<ClientPickerRow>());
        sw.ToString().Should().BeEmpty();
    }

    [Fact]
    public void RenderApplyRow_insertEmitsLeafAndVerb()
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
        line.Should().StartWith("  🌿 ").And.Contain("wrote").And.Contain("claude-code").And.Contain(".mcp.json");
    }

    [Fact]
    public void RenderApplyRow_skipExistingDiffersEmitsXAndDescription()
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
    public void RenderApplyRow_skipHasCommentsEmitsWarnAndDescription()
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
        output.Should().Contain("⚠ ").And.Contain("comments");
    }

    [Fact]
    public void RenderApplyRow_skipUnsupportedEmitsEmDashAndDescription()
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
        output.Should().Contain("— ").And.Contain("unsupported").And.Contain("user-scope Copilot");
    }

    [Fact]
    public void RenderApplyRow_noOpEmitsLeafAndNoChange()
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
        line.Should().Contain("🌿").And.Contain("no change");
    }

    [Fact]
    public void RenderPreWarmSummary_exitZero_emitsLeafAndElapsed()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderPreWarmSummary(sw, exitCode: 0, elapsed: TimeSpan.FromSeconds(11.4), solutionName: "MyApp.slnx");
        sw.ToString().Should().Contain("🌿").And.Contain("indexed").And.Contain("MyApp.slnx").And.Contain("11.4");
    }

    [Fact]
    public void RenderPreWarmSummary_nonZeroExit_emitsWarn()
    {
        using var sw = new StringWriter();
        InitRenderer.RenderPreWarmSummary(sw, exitCode: 2, elapsed: TimeSpan.FromSeconds(7.0), solutionName: "MyApp.slnx");
        sw.ToString().Should().Contain("⚠").And.Contain("pre-warm exit 2");
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
        lines.Should().Contain(l => l.StartsWith("Next"));
        var nextRows = lines.SkipWhile(l => !l.StartsWith("Next")).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l));
        nextRows.Should().AllSatisfy(l => l.Should().StartWith("  "));
    }

    private static bool IsPhaseHeading(string line)
    {
        // Heuristic: known phase names at left margin.
        return line.StartsWith("Environment") || line.StartsWith("Clients to wire")
            || line.StartsWith("Apply") || line.StartsWith("Pre-warm") || line.StartsWith("Next");
    }
}
