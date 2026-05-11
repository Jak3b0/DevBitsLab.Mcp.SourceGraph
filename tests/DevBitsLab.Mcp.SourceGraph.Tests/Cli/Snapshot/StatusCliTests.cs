using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Snapshot;

/// <summary>
/// End-to-end coverage for the <c>status</c> subcommand: exit-code evaluation, JSON shape,
/// human-readable phase headings, and the partial-trailing-line scenario.
/// </summary>
[Collection("CliConsole")]
public sealed class StatusCliTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TextWriter _originalStdout;
    private readonly TextWriter _originalStderr;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public StatusCliTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-status-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _originalStdout = Console.Out;
        _originalStderr = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalStdout);
        Console.SetError(_originalStderr);
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task Status_emptyRepo_emitsFivePhases()
    {
        var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot });
        var rc = await StatusCli.RunAsync(cli);
        rc.Should().BeOneOf(0, 2);
        var output = _stdout.ToString();
        output.Should().Contain("Environment");
        output.Should().Contain("Scopes");
        output.Should().Contain("Clients");
        output.Should().Contain("Embeddings");
        output.Should().Contain("Recent activity");
    }

    [Fact]
    public async Task Status_json_hasTopLevelKeys()
    {
        var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot, "--json" });
        var rc = await StatusCli.RunAsync(cli);
        var output = _stdout.ToString();
        output.Should().StartWith("{");
        using var doc = JsonDocument.Parse(output);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeEquivalentTo(new[]
        {
            "environment", "scopes", "clients", "embeddings",
            "recent_activity", "built_at", "usage_log_path", "heals_log_path", "exit_code",
        });
        doc.RootElement.GetProperty("exit_code").GetInt32().Should().Be(rc);
    }

    [Fact]
    public async Task Status_partialTrailingLine_droppedFromActivity()
    {
        var dotDir = Path.Join(_tempRoot, ".sourcegraph");
        Directory.CreateDirectory(dotDir);
        File.WriteAllText(Path.Join(dotDir, "usage.jsonl"),
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:00Z", tool = "ok", ok = true, ms = 1, scope = "default" }) + "\n" +
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:01Z", tool = "also_ok", ok = true, ms = 1, scope = "default" }) + "\n" +
            "{\"ts\":\"2025-01-01T12:00");  // truncated mid-string

        var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot, "--json" });
        await StatusCli.RunAsync(cli);
        var output = _stdout.ToString();
        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("recent_activity").GetArrayLength().Should().Be(2);
        _stderr.ToString().Should().NotContain("error");
    }

    [Fact]
    public async Task Status_malformedConfig_exits1()
    {
        File.WriteAllText(Path.Join(_tempRoot, ".sourcegraph.json"), "{ broken");
        var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot });
        var rc = await StatusCli.RunAsync(cli);
        rc.Should().Be(1);
    }

    [Fact]
    public async Task Status_noLeaf_substitutesAsciiTokens()
    {
        // SOURCEGRAPH_NO_LEAF=1 mirrors --no-leaf; the renderer ought to swap every glyph to its
        // [x] / [ ] / [!] / [X] / [-] ASCII form.
        Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_LEAF", "1");
        var initial = DevBitsLab.Mcp.SourceGraph.Server.Tools.LeafFormatter.Suppressed;
        DevBitsLab.Mcp.SourceGraph.Server.Tools.LeafFormatter.Suppressed = true;
        try
        {
            var cli = CommandLine.Parse(new[] { "status", "--root", _tempRoot });
            await StatusCli.RunAsync(cli);
            var output = _stdout.ToString();
            output.Should().NotContain("🌿");
            output.Should().MatchRegex(@"\[[ xX!\-]\] ");
        }
        finally
        {
            DevBitsLab.Mcp.SourceGraph.Server.Tools.LeafFormatter.Suppressed = initial;
            Environment.SetEnvironmentVariable("SOURCEGRAPH_NO_LEAF", null);
        }
    }

    [Fact]
    public void EvaluateExit_healthyEnv_returnsZero()
    {
        var snap = BuildSyntheticSnapshot();
        StatusCli.EvaluateExit(snap).Should().Be(0);
    }

    [Fact]
    public void EvaluateExit_missingGit_returnsTwo()
    {
        var snap = BuildSyntheticSnapshot() with
        {
            Environment = BuildSyntheticSnapshot().Environment with { GitOnPath = false },
        };
        StatusCli.EvaluateExit(snap).Should().Be(2);
    }

    [Fact]
    public void EvaluateExit_missingSdk_returnsOne()
    {
        var snap = BuildSyntheticSnapshot() with
        {
            Environment = BuildSyntheticSnapshot().Environment with { DotnetSdkVersion = null },
        };
        StatusCli.EvaluateExit(snap).Should().Be(1);
    }

    [Fact]
    public void EvaluateExit_degradedScope_returnsOne()
    {
        var basis = BuildSyntheticSnapshot();
        var snap = basis with
        {
            Scopes = new[]
            {
                new ScopeRow("a", "degraded", 0, 0, null,
                    Array.Empty<string>(), Array.Empty<string>(), false),
            },
        };
        StatusCli.EvaluateExit(snap).Should().Be(1);
    }

    [Fact]
    public void EvaluateExit_partialScope_returnsTwo()
    {
        var basis = BuildSyntheticSnapshot();
        var snap = basis with
        {
            Scopes = new[]
            {
                new ScopeRow("a", "partial", 1, 2, null,
                    new[] { "Bad.csproj" }, Array.Empty<string>(), false),
            },
        };
        StatusCli.EvaluateExit(snap).Should().Be(2);
    }

    [Fact]
    public void EvaluateExit_malformedConfig_returnsOne()
    {
        var basis = BuildSyntheticSnapshot();
        var snap = basis with
        {
            Environment = basis.Environment with
            {
                SourceGraphConfigStatus = "malformed",
                SourceGraphConfigError = "broken",
            },
        };
        StatusCli.EvaluateExit(snap).Should().Be(1);
    }

    [Fact]
    public void EvaluateExit_cacheAbsent_returnsTwo()
    {
        var basis = BuildSyntheticSnapshot();
        var snap = basis with
        {
            Embeddings = basis.Embeddings with { CachePresent = false, TotalBytes = 0 },
        };
        StatusCli.EvaluateExit(snap).Should().Be(2);
    }

    /// <summary>
    /// A synthetic healthy snapshot: existing repo dir + present .NET SDK + git + embeddings cache.
    /// Tests mutate one field at a time via <c>with</c>-expressions to probe each branch of
    /// <see cref="StatusCli.EvaluateExit"/>.
    /// </summary>
    private DashboardSnapshot BuildSyntheticSnapshot() => new(
        Environment: new EnvironmentSurface(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: _tempRoot,
            // Include one synthetic solution path so the "healthy environment" baseline matches
            // EvaluateExit's warning rule: a repo with no detectable .slnx / .sln warns
            // (consistent with doctor + StatusRenderer + DashboardRenderer).
            SolutionFiles: new[] { Path.Join(_tempRoot, "Test.slnx") },
            SourceGraphConfigStatus: "missing",
            SourceGraphConfigError: null),
        Scopes: Array.Empty<ScopeRow>(),
        Clients: Array.Empty<ClientRow>(),
        Embeddings: new EmbeddingsSurface(
            ModelId: "m",
            CacheDir: "/c",
            CachePresent: true,
            TotalBytes: 100,
            Verified: false),
        RecentActivity: Array.Empty<ActivityEntry>(),
        BuiltAt: DateTimeOffset.UtcNow,
        UsageLogPath: "/u",
        HealsLogPath: "/h",
        ExitCode: 0);
}
