using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using FluentAssertions;
using Spectre.Console.Testing;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// Tests for <see cref="DashboardActions"/>: confirm-modal gating on destructive actions
/// (<c>R</c> rebuild, <c>u</c> unwire), no-gate on idempotent ones (<c>r</c> reindex, <c>w</c>
/// wire, <c>p</c> pull, <c>v</c> verify), the 30-second watchdog branch via a synthetic
/// long-running body, and the unwire JSON-config-rewrite happy path.
///
/// <para>
/// Tests don't exercise the subprocess-spawning paths (reindex/rebuild/guided actions); those
/// shell out to <c>sourcegraph-mcp index</c> which would re-enter the full indexer graph.
/// Instead we drive the test seams: <see cref="DashboardActions.UnwireFromConfigFile"/>,
/// <see cref="DashboardActions.RunWatchdoggedAsync"/>, the cancel-on-confirm-deny path.
/// </para>
/// </summary>
public sealed class DashboardActionsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configPath;

    public DashboardActionsTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _configPath = Path.Join(_tempRoot, ".mcp.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunAsync_Quit_returnsQuitSignal()
    {
        var ctx = MakeContext(MakeSnapshot());
        var result = await DashboardActions.RunAsync(DashboardAction.Quit, ctx);
        result.Quit.Should().BeTrue();
        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ForceRefresh_returnsSuccess_andRequestsRebuild()
    {
        var ctx = MakeContext(MakeSnapshot());
        var result = await DashboardActions.RunAsync(DashboardAction.ForceRefresh, ctx);
        result.Ok.Should().BeTrue();
        result.Message.Should().Contain("refresh");
    }

    [Fact]
    public async Task RunAsync_navigationActions_returnNoop()
    {
        var ctx = MakeContext(MakeSnapshot());
        var move = await DashboardActions.RunAsync(DashboardAction.MoveUp, ctx);
        move.Quit.Should().BeFalse();
        move.Ok.Should().BeTrue();
        move.Message.Should().BeEmpty();
    }

    [Fact]
    public async Task UnwireClient_userDeclinesConfirm_doesNotMutateFile()
    {
        File.WriteAllText(_configPath, """
            {
              "mcpServers": {
                "sourcegraph": { "command": "sourcegraph-mcp", "args": ["serve"] },
                "other": { "command": "x" }
              }
            }
            """);
        var snap = MakeSnapshot() with
        {
            Clients = new[]
            {
                new ClientRow("claude-code", "project", _configPath, true, true),
            },
        };
        var console = new TestConsole();
        console.Input.PushTextWithEnter("n");  // user declines
        var ctx = new DashboardActionContext(
            Snapshot: snap,
            Selection: new DashboardSelection(DashboardSection.Clients, 0),
            Console: console,
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var before = File.ReadAllText(_configPath);
        var result = await DashboardActions.RunAsync(DashboardAction.UnwireClient, ctx);
        result.Ok.Should().BeTrue();
        result.Message.Should().Contain("cancelled");
        File.ReadAllText(_configPath).Should().Be(before, "file is unchanged when user declines confirm");
    }

    [Fact]
    public async Task UnwireClient_userConfirms_removesEntryFromConfig()
    {
        File.WriteAllText(_configPath, """
            {
              "mcpServers": {
                "sourcegraph": { "command": "sourcegraph-mcp", "args": ["serve"] },
                "other": { "command": "x" }
              }
            }
            """);
        var snap = MakeSnapshot() with
        {
            Clients = new[]
            {
                new ClientRow("claude-code", "project", _configPath, true, true),
            },
        };
        var console = new TestConsole();
        console.Input.PushTextWithEnter("y");  // user confirms
        var ctx = new DashboardActionContext(
            Snapshot: snap,
            Selection: new DashboardSelection(DashboardSection.Clients, 0),
            Console: console,
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var result = await DashboardActions.RunAsync(DashboardAction.UnwireClient, ctx);
        result.Ok.Should().BeTrue();
        var after = File.ReadAllText(_configPath);
        after.Should().NotContain("\"sourcegraph\":");
        after.Should().Contain("\"other\":", "other entries are preserved");
    }

    [Fact]
    public void UnwireFromConfigFile_missingFile_returnsFailure()
    {
        var result = DashboardActions.UnwireFromConfigFile(Path.Join(_tempRoot, "missing.json"));
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public void UnwireFromConfigFile_malformedJson_returnsFailure()
    {
        File.WriteAllText(_configPath, "{ broken");
        var result = DashboardActions.UnwireFromConfigFile(_configPath);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("malformed");
    }

    [Fact]
    public void UnwireFromConfigFile_noSourcegraphEntry_returnsFailure()
    {
        File.WriteAllText(_configPath, """
            {
              "mcpServers": {
                "other": { "command": "x" }
              }
            }
            """);
        var result = DashboardActions.UnwireFromConfigFile(_configPath);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("no sourcegraph");
    }

    [Fact]
    public void UnwireFromConfigFile_copilotShape_removesFromServersKey()
    {
        // Copilot uses `servers` instead of `mcpServers`; the dashboard tries both.
        File.WriteAllText(_configPath, """
            {
              "servers": {
                "sourcegraph": { "type": "stdio", "command": "sourcegraph-mcp" }
              }
            }
            """);
        var result = DashboardActions.UnwireFromConfigFile(_configPath);
        result.Ok.Should().BeTrue();
        File.ReadAllText(_configPath).Should().NotContain("sourcegraph");
    }

    [Fact]
    public void UnwireFromConfigFile_configWithComments_refuses()
    {
        File.WriteAllText(_configPath, """
            // a comment
            {
              "mcpServers": {
                "sourcegraph": { "command": "sourcegraph-mcp" }
              }
            }
            """);
        var result = DashboardActions.UnwireFromConfigFile(_configPath);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("comments");
    }

    [Fact]
    public async Task RunWatchdoggedAsync_underTimeout_returnsBodyResult()
    {
        var ctx = MakeContext(MakeSnapshot(), new DashboardActionPolicy(TimeSpan.FromSeconds(2)));
        var result = await DashboardActions.RunWatchdoggedAsync(
            async () => { await Task.Yield(); return DashboardActionResult.Success("done"); },
            ctx,
            CancellationToken.None);
        result.Ok.Should().BeTrue();
        result.Message.Should().Be("done");
    }

    [Fact]
    public async Task RunWatchdoggedAsync_overTimeout_returnsFailure()
    {
        // Use a very tight watchdog so the test runs fast.
        var ctx = MakeContext(MakeSnapshot(), new DashboardActionPolicy(TimeSpan.FromMilliseconds(50)));
        var result = await DashboardActions.RunWatchdoggedAsync(
            async () => { await Task.Delay(2000); return DashboardActionResult.Success("never"); },
            ctx,
            CancellationToken.None);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("watchdog");
    }

    [Fact]
    public async Task RebuildScope_userDeclinesConfirm_returnsCancelled()
    {
        var snap = MakeSnapshot();
        var console = new TestConsole();
        console.Input.PushTextWithEnter("n");
        var ctx = new DashboardActionContext(
            Snapshot: snap,
            Selection: new DashboardSelection(DashboardSection.Scopes, 0),
            Console: console,
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var result = await DashboardActions.RunAsync(DashboardAction.RebuildScope, ctx);
        result.Ok.Should().BeTrue();
        result.Message.Should().Contain("cancelled");
    }

    [Fact]
    public async Task ReindexScope_wrongSection_returnsFailure()
    {
        // r requires the Scopes section. With Clients selected, it short-circuits.
        var ctx = new DashboardActionContext(
            Snapshot: MakeSnapshot(),
            Selection: new DashboardSelection(DashboardSection.Clients, 0),
            Console: new TestConsole(),
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var result = await DashboardActions.RunAsync(DashboardAction.ReindexScope, ctx);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Scopes");
    }

    [Fact]
    public void ResolvePrimaryAction_Clients_wiredRow_dispatchesUnwire()
    {
        // Enter on a Clients row whose ContainsSourcegraphEntry == true must resolve to
        // UnwireClient — the wire/unwire toggle the redesign added.
        var snap = MakeSnapshot() with
        {
            Clients = new[]
            {
                new ClientRow("claude-code", "project", _configPath, Exists: true, ContainsSourcegraphEntry: true),
            },
        };
        var sel = new DashboardSelection(DashboardSection.Clients, 0);
        var action = DashboardPrimaryAction.Resolve(sel, snap);
        action.Should().Be(DashboardAction.UnwireClient);
    }

    [Fact]
    public void ResolvePrimaryAction_Clients_unwiredRow_dispatchesWire()
    {
        var snap = MakeSnapshot() with
        {
            Clients = new[]
            {
                new ClientRow("claude-code", "project", _configPath, Exists: true, ContainsSourcegraphEntry: false),
            },
        };
        var sel = new DashboardSelection(DashboardSection.Clients, 0);
        var action = DashboardPrimaryAction.Resolve(sel, snap);
        action.Should().Be(DashboardAction.WireClient);
    }

    [Fact]
    public void ResolvePrimaryAction_Scopes_dispatchesReindex()
    {
        var sel = new DashboardSelection(DashboardSection.Scopes, 0);
        DashboardPrimaryAction.Resolve(sel, MakeSnapshot()).Should().Be(DashboardAction.ReindexScope);
    }

    [Fact]
    public void ResolvePrimaryAction_Embeddings_dispatchesPull()
    {
        var sel = new DashboardSelection(DashboardSection.Embeddings, 0);
        DashboardPrimaryAction.Resolve(sel, MakeSnapshot()).Should().Be(DashboardAction.EmbeddingsPull);
    }

    [Fact]
    public void ResolvePrimaryAction_RecentActivity_returnsNone()
    {
        // No first-class detail pane yet; routing returns None so the dispatcher can surface
        // a discoverability hint instead of hanging.
        var sel = new DashboardSelection(DashboardSection.RecentActivity, 0);
        DashboardPrimaryAction.Resolve(sel, MakeSnapshot()).Should().Be(DashboardAction.None);
    }

    [Fact]
    public void ResolveForView_Scopes_dispatchesReindex()
    {
        // The view-aware entry point that LoopState uses; same dispatch outcome as the legacy
        // section-based one, but takes a DashboardView directly.
        DashboardPrimaryAction.ResolveForView(DashboardView.Scopes, 0, MakeSnapshot())
            .Should().Be(DashboardAction.ReindexScope);
    }

    [Fact]
    public void ResolveForView_Clients_wired_dispatchesUnwire()
    {
        var snap = MakeSnapshot() with
        {
            Clients = new[]
            {
                new ClientRow("claude-code", "project", _configPath, Exists: true, ContainsSourcegraphEntry: true),
            },
        };
        DashboardPrimaryAction.ResolveForView(DashboardView.Clients, 0, snap)
            .Should().Be(DashboardAction.UnwireClient);
    }

    [Fact]
    public void ResolveForView_Embeddings_dispatchesPull()
    {
        DashboardPrimaryAction.ResolveForView(DashboardView.Embeddings, 0, MakeSnapshot())
            .Should().Be(DashboardAction.EmbeddingsPull);
    }

    [Fact]
    public void ResolveForView_Environment_dispatchesNone()
    {
        // Environment is read-only; no primary action.
        DashboardPrimaryAction.ResolveForView(DashboardView.Environment, 0, MakeSnapshot())
            .Should().Be(DashboardAction.None);
    }

    [Fact]
    public async Task RunAsync_viewTransitionActions_returnNoop()
    {
        // The new view-transition actions (GoHome / OpenScopes / …) are handled by the LoopState
        // before they ever reach the dispatcher. The dispatcher returns Noop for them so a stray
        // call doesn't surface as "no primary action" toast.
        var ctx = MakeContext(MakeSnapshot());
        foreach (var a in new[]
        {
            DashboardAction.GoHome, DashboardAction.OpenScopes, DashboardAction.OpenClients,
            DashboardAction.OpenEmbeddings, DashboardAction.OpenRecentActivity, DashboardAction.OpenEnvironment,
        })
        {
            var r = await DashboardActions.RunAsync(a, ctx);
            r.Ok.Should().BeTrue($"{a} should be Noop-success");
            r.Message.Should().BeEmpty($"{a} should not produce a status toast");
        }
    }

    [Fact]
    public void DashboardActionResult_severity_factoryDefaults()
    {
        // Spec: Success → Success, Failure → Fail, Noop → Info, Info → Info.
        DashboardActionResult.Success("x").Severity.Should().Be(ToastSeverity.Success);
        DashboardActionResult.Failure("x").Severity.Should().Be(ToastSeverity.Fail);
        DashboardActionResult.Noop.Severity.Should().Be(ToastSeverity.Info);
        DashboardActionResult.Info("x").Severity.Should().Be(ToastSeverity.Info);
    }

    [Fact]
    public void ResolveSourceGraphLaunch_prefers_processPath_dll()
    {
        // ProcessPath in test runs is typically the testhost or vstest .dll; the helper should
        // detect the .dll suffix and prepend `dotnet`.
        var (file, args) = DashboardActions.ResolveSourceGraphLaunch(new[] { "index", "/x.sln" });
        if (Environment.ProcessPath?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true)
        {
            file.Should().Be("dotnet");
            args[0].Should().EndWith(".dll");
            args.Last().Should().Be("/x.sln");
        }
        else
        {
            // Apphost case: ProcessPath itself is the binary.
            file.Should().NotBeNullOrEmpty();
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Scope management (Add / Remove) — dashboard's `N` and `D` actions
    // ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveScope_userDeclinesConfirm_doesNotMutateConfig()
    {
        var sgPath = Path.Join(_tempRoot, ".sourcegraph.json");
        var slnPath = Path.Join(_tempRoot, "x.slnx");
        File.WriteAllText(slnPath, "<Solution />");
        File.WriteAllText(sgPath, """
            {
              "scopes": [
                { "name": "default", "solutions": ["x.slnx"] },
                { "name": "other", "solutions": ["x.slnx"] }
              ]
            }
            """);
        var console = new Spectre.Console.Testing.TestConsole();
        console.Input.PushTextWithEnter("n"); // decline confirm
        var ctx = new DashboardActionContext(
            Snapshot: MakeSnapshotWithTwoScopes(),
            Selection: new DashboardSelection(DashboardSection.Scopes, 0),
            Console: console,
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var before = File.ReadAllText(sgPath);
        var result = await DashboardActions.RunAsync(DashboardAction.RemoveScope, ctx);
        result.Ok.Should().BeTrue();
        result.Message.Should().Contain("cancelled");
        File.ReadAllText(sgPath).Should().Be(before, "config is unchanged when user declines confirm");
    }

    [Fact]
    public async Task RemoveScope_userConfirms_removesFromConfig()
    {
        var sgPath = Path.Join(_tempRoot, ".sourcegraph.json");
        var slnPath = Path.Join(_tempRoot, "x.slnx");
        File.WriteAllText(slnPath, "<Solution />");
        File.WriteAllText(sgPath, """
            {
              "scopes": [
                { "name": "default", "solutions": ["x.slnx"] },
                { "name": "other", "solutions": ["x.slnx"] }
              ]
            }
            """);
        var console = new Spectre.Console.Testing.TestConsole();
        console.Input.PushTextWithEnter("y"); // confirm
        var ctx = new DashboardActionContext(
            Snapshot: MakeSnapshotWithTwoScopes(),
            Selection: new DashboardSelection(DashboardSection.Scopes, 0),
            Console: console,
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var result = await DashboardActions.RunAsync(DashboardAction.RemoveScope, ctx);
        result.Ok.Should().BeTrue();
        var after = File.ReadAllText(sgPath);
        after.Should().NotContain("\"name\": \"default\"");
        after.Should().Contain("\"name\": \"other\"", "other scopes are preserved");
    }

    [Fact]
    public async Task RemoveScope_lastScope_isRefused()
    {
        var sgPath = Path.Join(_tempRoot, ".sourcegraph.json");
        File.WriteAllText(Path.Join(_tempRoot, "x.slnx"), "<Solution />");
        File.WriteAllText(sgPath, """
            {
              "scopes": [
                { "name": "only", "solutions": ["x.slnx"] }
              ]
            }
            """);
        var console = new Spectre.Console.Testing.TestConsole();
        var ctx = new DashboardActionContext(
            Snapshot: MakeSnapshot(),
            Selection: new DashboardSelection(DashboardSection.Scopes, 0),
            Console: console,
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var before = File.ReadAllText(sgPath);
        var result = await DashboardActions.RunAsync(DashboardAction.RemoveScope, ctx);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("last scope");
        File.ReadAllText(sgPath).Should().Be(before, "guard refuses before any file mutation");
    }

    [Fact]
    public async Task RemoveScope_indexingScope_isRefused()
    {
        var sgPath = Path.Join(_tempRoot, ".sourcegraph.json");
        File.WriteAllText(Path.Join(_tempRoot, "x.slnx"), "<Solution />");
        File.WriteAllText(sgPath, """
            {
              "scopes": [
                { "name": "default", "solutions": ["x.slnx"] },
                { "name": "other", "solutions": ["x.slnx"] }
              ]
            }
            """);
        var indexingSnapshot = MakeSnapshotWithTwoScopes() with
        {
            Scopes = new[]
            {
                new ScopeRow("default", "indexing", 0, 0, null,
                    Array.Empty<string>(), Array.Empty<string>(), false),
                new ScopeRow("other", "ok", 100, 200, DateTimeOffset.UtcNow,
                    Array.Empty<string>(), Array.Empty<string>(), false),
            },
        };
        var ctx = new DashboardActionContext(
            Snapshot: indexingSnapshot,
            Selection: new DashboardSelection(DashboardSection.Scopes, 0),
            Console: new Spectre.Console.Testing.TestConsole(),
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var result = await DashboardActions.RunAsync(DashboardAction.RemoveScope, ctx);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("indexing");
    }

    [Fact]
    public async Task RemoveScope_wrongSection_returnsFailure()
    {
        var ctx = new DashboardActionContext(
            Snapshot: MakeSnapshot(),
            Selection: new DashboardSelection(DashboardSection.Clients, 0),
            Console: new Spectre.Console.Testing.TestConsole(),
            Freshness: null,
            Root: _tempRoot,
            Policy: DashboardActionPolicy.Default);
        var result = await DashboardActions.RunAsync(DashboardAction.RemoveScope, ctx);
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Scopes");
    }

    [Fact]
    public void AddScopeToConfig_validInput_persistsScope()
    {
        var sgPath = Path.Join(_tempRoot, ".sourcegraph.json");
        var slnPath = Path.Join(_tempRoot, "x.slnx");
        File.WriteAllText(slnPath, "<Solution />");
        var config = DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfigLoader.Synthesise(
            _tempRoot,
            new[] { slnPath });
        var result = DevBitsLab.Mcp.SourceGraph.Server.Cli.ScopesCli.AddScopeToConfig(
            _tempRoot, config, "frontend", slnPath, isolated: false);
        result.Ok.Should().BeTrue();
        File.Exists(sgPath).Should().BeTrue();
        File.ReadAllText(sgPath).Should().Contain("frontend");
    }

    [Fact]
    public void AddScopeToConfig_invalidName_returnsFailure()
    {
        var slnPath = Path.Join(_tempRoot, "x.slnx");
        File.WriteAllText(slnPath, "<Solution />");
        var config = DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfigLoader.Synthesise(
            _tempRoot,
            new[] { slnPath });
        // Capital letters violate the kebab-case slug rule.
        var result = DevBitsLab.Mcp.SourceGraph.Server.Cli.ScopesCli.AddScopeToConfig(
            _tempRoot, config, "Frontend", slnPath, isolated: false);
        result.Ok.Should().BeFalse();
        result.ExitCode.Should().Be(2);
        result.Message.Should().Contain("Invalid");
    }

    [Fact]
    public void AddScopeToConfig_duplicateName_returnsFailure()
    {
        var slnPath = Path.Join(_tempRoot, "x.slnx");
        File.WriteAllText(slnPath, "<Solution />");
        var config = DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfigLoader.Synthesise(
            _tempRoot,
            new[] { slnPath });
        // The synthesised default config already has a scope named "default".
        var result = DevBitsLab.Mcp.SourceGraph.Server.Cli.ScopesCli.AddScopeToConfig(
            _tempRoot, config, "default", slnPath, isolated: false);
        result.Ok.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.Message.Should().Contain("already exists");
    }

    [Fact]
    public void RemoveScopeFromConfig_missingName_returnsFailure()
    {
        var slnPath = Path.Join(_tempRoot, "x.slnx");
        File.WriteAllText(slnPath, "<Solution />");
        var config = DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfigLoader.Synthesise(
            _tempRoot,
            new[] { slnPath });
        var result = DevBitsLab.Mcp.SourceGraph.Server.Cli.ScopesCli.RemoveScopeFromConfig(
            _tempRoot, config, "nonexistent");
        result.Ok.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public void RemoveScopeFromConfig_existing_persistsRemoval()
    {
        var sgPath = Path.Join(_tempRoot, ".sourcegraph.json");
        var slnPath = Path.Join(_tempRoot, "x.slnx");
        File.WriteAllText(slnPath, "<Solution />");
        File.WriteAllText(sgPath, """
            {
              "scopes": [
                { "name": "frontend", "solutions": ["x.slnx"] },
                { "name": "backend", "solutions": ["x.slnx"] }
              ]
            }
            """);
        var config = DevBitsLab.Mcp.SourceGraph.Storage.ScopeConfigLoader.Load(_tempRoot);
        var result = DevBitsLab.Mcp.SourceGraph.Server.Cli.ScopesCli.RemoveScopeFromConfig(
            _tempRoot, config, "frontend");
        result.Ok.Should().BeTrue();
        var after = File.ReadAllText(sgPath);
        after.Should().NotContain("\"name\": \"frontend\"");
        after.Should().Contain("\"name\": \"backend\"");
    }

    [Fact]
    public void DashboardViewGating_AddScope_isScopesOnly()
    {
        DashboardViewGating.IsAllowed(DashboardAction.AddScope, DashboardView.Scopes).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.AddScope, DashboardView.Clients).Should().BeFalse();
        DashboardViewGating.IsAllowed(DashboardAction.AddScope, DashboardView.Home).Should().BeFalse();
    }

    [Fact]
    public void DashboardViewGating_RemoveScope_isScopesOnly()
    {
        DashboardViewGating.IsAllowed(DashboardAction.RemoveScope, DashboardView.Scopes).Should().BeTrue();
        DashboardViewGating.IsAllowed(DashboardAction.RemoveScope, DashboardView.Clients).Should().BeFalse();
        DashboardViewGating.IsAllowed(DashboardAction.RemoveScope, DashboardView.Home).Should().BeFalse();
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────────

    private DashboardActionContext MakeContext(DashboardSnapshot snap, DashboardActionPolicy? policy = null) =>
        new(
            Snapshot: snap,
            Selection: new DashboardSelection(DashboardSection.Scopes, 0),
            Console: new TestConsole(),
            Freshness: null,
            Root: _tempRoot,
            Policy: policy ?? DashboardActionPolicy.Default);

    private DashboardSnapshot MakeSnapshot() => new(
        Environment: new EnvironmentSurface(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: _tempRoot,
            SolutionFiles: new[] { Path.Join(_tempRoot, "x.slnx") },
            SourceGraphConfigStatus: "missing",
            SourceGraphConfigError: null),
        Scopes: new[]
        {
            new ScopeRow("default", "ok", 100, 200, DateTimeOffset.UtcNow,
                Array.Empty<string>(), Array.Empty<string>(), false),
        },
        Clients: new[]
        {
            new ClientRow("claude-code", "project", _configPath, true, true),
        },
        Embeddings: new EmbeddingsSurface(
            ModelId: "test-model",
            CacheDir: Path.Join(_tempRoot, "cache"),
            CachePresent: false,
            TotalBytes: 0,
            Verified: false),
        RecentActivity: Array.Empty<ActivityEntry>(),
        BuiltAt: DateTimeOffset.UtcNow,
        UsageLogPath: Path.Join(_tempRoot, ".sourcegraph", "usage.jsonl"),
        HealsLogPath: Path.Join(_tempRoot, ".sourcegraph", "heals.jsonl"),
        ExitCode: 0);

    /// <summary>
    /// Variant with two scopes (`default` + `other`) for tests that exercise the remove-scope
    /// path, which now refuses to remove the last remaining scope. The two-scope shape lets
    /// the action complete without firing the "last scope" guard.
    /// </summary>
    private DashboardSnapshot MakeSnapshotWithTwoScopes() => MakeSnapshot() with
    {
        Scopes = new[]
        {
            new ScopeRow("default", "ok", 100, 200, DateTimeOffset.UtcNow,
                Array.Empty<string>(), Array.Empty<string>(), false),
            new ScopeRow("other", "ok", 50, 75, DateTimeOffset.UtcNow,
                Array.Empty<string>(), Array.Empty<string>(), false),
        },
    };
}
