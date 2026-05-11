using System.Diagnostics;
using System.Text.Json.Nodes;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Embeddings;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Selection state the dashboard threads through to its actions. The dashboard's main loop owns
/// the focused section and per-section row index; actions consume these as inputs but never
/// mutate them — navigation lives in <see cref="DashboardActionDispatcher.Apply"/>.
/// </summary>
internal sealed record DashboardSelection(DashboardSection FocusedSection, int RowIndex);

/// <summary>
/// Severity of the action's outcome — drives the toast colour in the renderer. <c>Success</c> is
/// brand-green, <c>Warn</c> amber, <c>Fail</c> red; <c>Info</c> renders muted (and the no-message
/// case is hidden entirely).
/// </summary>
internal enum ToastSeverity
{
    Success,
    Warn,
    Fail,
    Info,
}

/// <summary>
/// Outcome of one action invocation. Carries a short status message the dashboard surfaces in
/// its footer/status bar plus a flag the loop honours when the user pressed quit. Failure-mode
/// actions also set <see cref="Ok"/> false so the status bar can render the failure inline.
/// </summary>
internal sealed record DashboardActionResult(bool Ok, bool Quit, string Message, ToastSeverity Severity = ToastSeverity.Success)
{
    public static DashboardActionResult Noop => new(Ok: true, Quit: false, Message: "", Severity: ToastSeverity.Info);
    public static DashboardActionResult QuitSignal => new(Ok: true, Quit: true, Message: "quitting", Severity: ToastSeverity.Info);
    public static DashboardActionResult Success(string msg) => new(Ok: true, Quit: false, Message: msg, Severity: ToastSeverity.Success);
    public static DashboardActionResult Failure(string msg) => new(Ok: false, Quit: false, Message: msg, Severity: ToastSeverity.Fail);
    public static DashboardActionResult Info(string msg) => new(Ok: true, Quit: false, Message: msg, Severity: ToastSeverity.Info);
}

/// <summary>
/// Context passed to every action: the active snapshot, the user's selection, the console for
/// prompts, and the freshness source so an action can trigger an out-of-band rebuild on
/// completion. Tests inject mocks for <c>console</c> and <c>freshness</c>.
/// </summary>
internal sealed record DashboardActionContext(
    DashboardSnapshot Snapshot,
    DashboardSelection Selection,
    IAnsiConsole Console,
    FreshnessSource? Freshness,
    string Root,
    DashboardActionPolicy Policy);

/// <summary>
/// Watchdog + subprocess timing knobs the dashboard reads. Production wiring uses the
/// <see cref="Default"/> instance; tests substitute a tighter watchdog to exercise the
/// 30-second branch without sleeping that long.
/// </summary>
internal sealed record DashboardActionPolicy(TimeSpan InPlaceWatchdog)
{
    /// <summary>30-second watchdog per design "Risks / Trade-offs" mitigation.</summary>
    public static readonly DashboardActionPolicy Default = new(InPlaceWatchdog: TimeSpan.FromSeconds(30));
}

/// <summary>
/// Action dispatcher for the dashboard. Each method takes a <see cref="DashboardActionContext"/>
/// and returns a <see cref="DashboardActionResult"/> the main loop renders in the footer.
///
/// <para>
/// In-place actions delegate to existing code paths (the same ones the headless CLI subcommands
/// use): <see cref="EmbeddingsManager"/> for the embeddings tier; <see cref="IClientConfigWriter"/>
/// for the wire/unwire tier; subprocess invocation of <c>sourcegraph-mcp index &lt;solution&gt;</c>
/// for the reindex/rebuild tier (LiveIndexService is a hosted service that needs the full
/// <c>serve</c> infrastructure — running it inline from a one-shot dashboard process would
/// require standing up the same DI graph; the spec's principle "no business logic forks into
/// the dashboard" is honoured by re-using the existing CLI verb instead).
/// </para>
///
/// <para>
/// Destructive actions (<see cref="DashboardAction.RebuildScope"/>, <see cref="DashboardAction.UnwireClient"/>)
/// gate via <see cref="ConfirmModal"/>; idempotent ones don't. The 30-second watchdog wraps
/// every in-place action; on timeout the action's task is cancelled and the failure surfaces
/// in the status bar.
/// </para>
/// </summary>
internal static class DashboardActions
{
    /// <summary>Run <paramref name="action"/> against <paramref name="ctx"/> and return its outcome.</summary>
    public static async Task<DashboardActionResult> RunAsync(DashboardAction action, DashboardActionContext ctx,
        CancellationToken token = default)
    {
        return action switch
        {
            DashboardAction.None => DashboardActionResult.Noop,
            DashboardAction.Quit => DashboardActionResult.QuitSignal,

            // Read-only actions resolved by the main loop's selection / view state — no work
            // to do here. View transitions (GoHome / OpenScopes / …) are handled inline by the
            // LoopState before this dispatcher is reached, but we list them so a stray call
            // can't fall through to the default _.
            DashboardAction.MoveUp or DashboardAction.MoveDown or
            DashboardAction.NextSection or DashboardAction.PreviousSection or
            DashboardAction.OpenDetail or DashboardAction.CloseDetail or
            DashboardAction.ToggleHelp or
            DashboardAction.GoHome or DashboardAction.OpenScopes or
            DashboardAction.OpenClients or DashboardAction.OpenEmbeddings or
            DashboardAction.OpenRecentActivity or DashboardAction.OpenEnvironment
                => DashboardActionResult.Noop,

            // PrimaryAction (Enter) is mapped section-by-section by the caller (DashboardCli's
            // dispatch helper rewrites it to one of ReindexScope / WireClient / UnwireClient /
            // EmbeddingsPull). If it reaches the dispatcher as a raw PrimaryAction the caller's
            // routing failed; treat as a no-op so the user gets a visible "nothing happened"
            // toast rather than a hang.
            DashboardAction.PrimaryAction => DashboardActionResult.Info("no primary action for this section"),

            DashboardAction.ForceRefresh => ForceRefresh(ctx),

            // In-place — gated (destructive)
            DashboardAction.RebuildScope => await RebuildScopeAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.UnwireClient => await UnwireClientAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.RemoveScope => RemoveScope(ctx),

            // In-place — not gated
            DashboardAction.ReindexScope => await ReindexScopeAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.WireClient => await WireClientAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.EmbeddingsPull => await EmbeddingsPullAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.EmbeddingsVerify => await EmbeddingsVerifyAsync(ctx, token).ConfigureAwait(false),

            // Inline form — suspends Live in the caller, runs Spectre prompts inline.
            DashboardAction.AddScope => AddScope(ctx),

            // Guided
            DashboardAction.InitGuided => await InitGuidedAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.DemoGuided => await DemoGuidedAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.OpenLogInPager => await OpenLogInPagerAsync(ctx, token).ConfigureAwait(false),
            DashboardAction.OpenConfigInEditor => await OpenConfigInEditorAsync(ctx, token).ConfigureAwait(false),

            _ => DashboardActionResult.Noop,
        };
    }

    private static DashboardActionResult ForceRefresh(DashboardActionContext ctx)
    {
        ctx.Freshness?.RequestImmediateRebuild();
        return DashboardActionResult.Success("snapshot refresh requested");
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // In-place actions (destructive — gated)
    // ────────────────────────────────────────────────────────────────────────────────

    private static async Task<DashboardActionResult> RebuildScopeAsync(DashboardActionContext ctx, CancellationToken token)
    {
        if (ctx.Selection.FocusedSection != DashboardSection.Scopes)
            return DashboardActionResult.Failure("rebuild requires the Scopes section selected");
        if (ctx.Snapshot.Scopes.Count == 0)
            return DashboardActionResult.Failure("no scope to rebuild");
        if (ctx.Selection.RowIndex < 0 || ctx.Selection.RowIndex >= ctx.Snapshot.Scopes.Count)
            return DashboardActionResult.Failure("selection out of range");
        var scope = ctx.Snapshot.Scopes[ctx.Selection.RowIndex];

        if (!ConfirmModal.Prompt(ctx.Console, "rebuild", scope.Name))
            return DashboardActionResult.Success("rebuild cancelled");

        // The rebuild path runs by re-invoking `sourcegraph-mcp index <solution>` for the scope.
        // We need the scope's solution path; the snapshot doesn't carry it, so we read the
        // .sourcegraph.json (or fall back to detection). For the synthesised default scope we
        // probe the env for solution files.
        var solution = ResolveSolutionForScope(ctx, scope.Name);
        if (solution is null)
            return DashboardActionResult.Failure($"no solution resolved for scope '{scope.Name}'");

        return await RunWatchdoggedAsync(async () =>
        {
            var rc = await RunSourceGraphCommandAsync(new[] { "index", solution }, token).ConfigureAwait(false);
            ctx.Freshness?.RequestImmediateRebuild();
            return rc == 0
                ? DashboardActionResult.Success($"rebuilt scope '{scope.Name}'")
                : DashboardActionResult.Failure($"rebuild exited with code {rc}");
        }, ctx, token).ConfigureAwait(false);
    }

    private static async Task<DashboardActionResult> UnwireClientAsync(DashboardActionContext ctx, CancellationToken token)
    {
        if (ctx.Selection.FocusedSection != DashboardSection.Clients)
            return DashboardActionResult.Failure("unwire requires the Clients section selected");
        var clients = OrderedClients(ctx.Snapshot);
        if (clients.Count == 0) return DashboardActionResult.Failure("no client to unwire");
        if (ctx.Selection.RowIndex < 0 || ctx.Selection.RowIndex >= clients.Count)
            return DashboardActionResult.Failure("selection out of range");
        var client = clients[ctx.Selection.RowIndex];
        if (!client.ContainsSourcegraphEntry)
            return DashboardActionResult.Failure($"{client.Slug} is not currently wired");

        if (!ConfirmModal.Prompt(ctx.Console, "unwire", client.Slug))
            return DashboardActionResult.Success("unwire cancelled");

        return await RunWatchdoggedAsync(() =>
        {
            var result = UnwireFromConfigFile(client.Path);
            ctx.Freshness?.RequestImmediateRebuild();
            return Task.FromResult(result);
        }, ctx, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove the selected scope from <c>.sourcegraph.json</c>. Gated behind
    /// <see cref="ConfirmModal"/>; the per-scope DB on disk is preserved (re-add cache semantics).
    /// </summary>
    private static DashboardActionResult RemoveScope(DashboardActionContext ctx)
    {
        if (ctx.Selection.FocusedSection != DashboardSection.Scopes)
            return DashboardActionResult.Failure("remove requires the Scopes section selected");
        if (ctx.Snapshot.Scopes.Count == 0)
            return DashboardActionResult.Failure("no scope to remove");
        if (ctx.Selection.RowIndex < 0 || ctx.Selection.RowIndex >= ctx.Snapshot.Scopes.Count)
            return DashboardActionResult.Failure("selection out of range");
        var scope = ctx.Snapshot.Scopes[ctx.Selection.RowIndex];

        if (!ConfirmModal.Prompt(ctx.Console, "remove scope", scope.Name))
            return DashboardActionResult.Success("remove cancelled");

        ScopeConfig config;
        try
        {
            config = ScopeConfigLoader.Load(ctx.Root);
        }
        catch (ScopeConfigException ex)
        {
            return DashboardActionResult.Failure($"failed to read .sourcegraph.json: {ex.Message}");
        }
        var result = Cli.ScopesCli.RemoveScopeFromConfig(ctx.Root, config, scope.Name);
        if (!result.Ok)
            return DashboardActionResult.Failure(result.Message);
        ctx.Freshness?.RequestImmediateRebuild();
        return DashboardActionResult.Success($"removed scope '{scope.Name}'");
    }

    /// <summary>
    /// Show the inline add-scope form and persist the result. Runs synchronously inside the
    /// outer-loop suspend window (Live region is exited before this is called, the same way
    /// guided init/demo are dispatched). The form itself is the deliberate action — no
    /// confirm-modal gate.
    /// </summary>
    private static DashboardActionResult AddScope(DashboardActionContext ctx)
    {
        if (ctx.Selection.FocusedSection != DashboardSection.Scopes)
            return DashboardActionResult.Failure("add requires the Scopes section selected");

        ScopeConfig config;
        try
        {
            config = ScopeConfigLoader.Load(ctx.Root);
        }
        catch (ScopeConfigException ex)
        {
            return DashboardActionResult.Failure($"failed to read .sourcegraph.json: {ex.Message}");
        }

        var result = AddScopeForm.Prompt(ctx.Console, ctx.Root, config);
        return result.Outcome switch
        {
            AddScopeForm.Outcome.Saved => TickAndReturn(ctx, DashboardActionResult.Success(result.Message)),
            AddScopeForm.Outcome.Cancelled => DashboardActionResult.Info(result.Message),
            AddScopeForm.Outcome.Failed => DashboardActionResult.Failure(result.Message),
            _ => DashboardActionResult.Failure("unexpected add-scope form outcome"),
        };

        static DashboardActionResult TickAndReturn(DashboardActionContext ctx, DashboardActionResult r)
        {
            ctx.Freshness?.RequestImmediateRebuild();
            return r;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // In-place actions (idempotent — no gate)
    // ────────────────────────────────────────────────────────────────────────────────

    private static async Task<DashboardActionResult> ReindexScopeAsync(DashboardActionContext ctx, CancellationToken token)
    {
        if (ctx.Selection.FocusedSection != DashboardSection.Scopes)
            return DashboardActionResult.Failure("reindex requires the Scopes section selected");
        if (ctx.Snapshot.Scopes.Count == 0) return DashboardActionResult.Failure("no scope to reindex");
        if (ctx.Selection.RowIndex < 0 || ctx.Selection.RowIndex >= ctx.Snapshot.Scopes.Count)
            return DashboardActionResult.Failure("selection out of range");
        var scope = ctx.Snapshot.Scopes[ctx.Selection.RowIndex];

        var solution = ResolveSolutionForScope(ctx, scope.Name);
        if (solution is null)
            return DashboardActionResult.Failure($"no solution resolved for scope '{scope.Name}'");

        return await RunWatchdoggedAsync(async () =>
        {
            var rc = await RunSourceGraphCommandAsync(new[] { "index", solution }, token).ConfigureAwait(false);
            ctx.Freshness?.RequestImmediateRebuild();
            return rc == 0
                ? DashboardActionResult.Success($"reindexed scope '{scope.Name}'")
                : DashboardActionResult.Failure($"reindex exited with code {rc}");
        }, ctx, token).ConfigureAwait(false);
    }

    private static async Task<DashboardActionResult> WireClientAsync(DashboardActionContext ctx, CancellationToken token)
    {
        if (ctx.Selection.FocusedSection != DashboardSection.Clients)
            return DashboardActionResult.Failure("wire requires the Clients section selected");
        var clients = OrderedClients(ctx.Snapshot);
        if (clients.Count == 0) return DashboardActionResult.Failure("no client to wire");
        if (ctx.Selection.RowIndex < 0 || ctx.Selection.RowIndex >= clients.Count)
            return DashboardActionResult.Failure("selection out of range");
        var client = clients[ctx.Selection.RowIndex];
        if (client.ContainsSourcegraphEntry)
            return DashboardActionResult.Success($"{client.Slug} already wired (no change)");
        if (!ClientIdExtensions.TryParseSlug(client.Slug, out var clientId))
            return DashboardActionResult.Failure($"unknown client slug '{client.Slug}'");

        return await RunWatchdoggedAsync(() =>
        {
            var writer = MakeWriter(clientId);
            var existing = ReadFileBytesIfExists(client.Path);
            var solutions = ctx.Snapshot.Environment.SolutionFiles;
            var solutionPath = solutions.Count > 0 ? solutions[0] : null;
            var writerCtx = new WriterContext(
                Root: ctx.Root,
                TargetPath: client.Path,
                UseUserScope: client.Scope == "user",
                InstallMode: InstallMode.Global,
                SolutionPath: solutionPath,
                ServerProjectPath: null,
                NoEmbeddings: false,
                NoHistory: !ctx.Snapshot.Environment.GitOnPath,
                Force: false,
                ExistingContent: existing);
            var plan = writer.Plan(writerCtx);
            try
            {
                writer.Apply(plan);
            }
            catch (Exception ex)
            {
                return Task.FromResult(DashboardActionResult.Failure($"wire failed: {ex.Message}"));
            }
            ctx.Freshness?.RequestImmediateRebuild();
            var msg = plan.Action switch
            {
                WriterAction.Insert => $"wired {client.Slug}",
                WriterAction.ReplaceOurs => $"replaced {client.Slug}",
                WriterAction.NoOpAlreadyMatches => $"{client.Slug} already matches",
                WriterAction.SkipExistingDiffers => $"{client.Slug} differs — use `i` to run init --force",
                WriterAction.SkipHasComments => $"{client.Slug} has JS comments; paste manually",
                _ => $"{client.Slug} skipped: {plan.Description}",
            };
            var ok = plan.Action is WriterAction.Insert or WriterAction.ReplaceOurs or WriterAction.NoOpAlreadyMatches;
            return Task.FromResult(ok ? DashboardActionResult.Success(msg) : DashboardActionResult.Failure(msg));
        }, ctx, token).ConfigureAwait(false);
    }

    private static async Task<DashboardActionResult> EmbeddingsPullAsync(DashboardActionContext ctx, CancellationToken token)
    {
        return await RunWatchdoggedAsync(async () =>
        {
            var mgr = BuildEmbeddingsManager(ctx);
            try
            {
                var status = await mgr.PullAsync(ctx.Snapshot.Embeddings.ModelId, token).ConfigureAwait(false);
                ctx.Freshness?.RequestImmediateRebuild();
                return DashboardActionResult.Success($"pulled {status.ModelId} ({status.Files.Count} files)");
            }
            catch (Exception ex)
            {
                return DashboardActionResult.Failure($"pull failed: {ex.Message}");
            }
        }, ctx, token).ConfigureAwait(false);
    }

    private static async Task<DashboardActionResult> EmbeddingsVerifyAsync(DashboardActionContext ctx, CancellationToken token)
    {
        return await RunWatchdoggedAsync(async () =>
        {
            var mgr = BuildEmbeddingsManager(ctx);
            try
            {
                var status = await mgr.VerifyAsync(ctx.Snapshot.Embeddings.ModelId, token).ConfigureAwait(false);
                var bad = status.Files.Count(f => f.Match == false);
                ctx.Freshness?.RequestImmediateRebuild();
                return bad == 0
                    ? DashboardActionResult.Success($"verified {status.ModelId}")
                    : DashboardActionResult.Failure($"verify mismatch on {bad} file(s)");
            }
            catch (Exception ex)
            {
                return DashboardActionResult.Failure($"verify failed: {ex.Message}");
            }
        }, ctx, token).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Guided actions (suspend → subprocess → resume)
    // ────────────────────────────────────────────────────────────────────────────────

    private static Task<DashboardActionResult> InitGuidedAsync(DashboardActionContext ctx, CancellationToken token)
        => RunGuidedAsync(ctx, new[] { "init", "--root", ctx.Root }, "init", token);

    private static Task<DashboardActionResult> DemoGuidedAsync(DashboardActionContext ctx, CancellationToken token)
    {
        var args = new List<string> { "demo", "--root", ctx.Root };
        if (ctx.Selection.FocusedSection == DashboardSection.Scopes
            && ctx.Selection.RowIndex >= 0 && ctx.Selection.RowIndex < ctx.Snapshot.Scopes.Count)
        {
            args.Add("--scope");
            args.Add(ctx.Snapshot.Scopes[ctx.Selection.RowIndex].Name);
        }
        return RunGuidedAsync(ctx, args.ToArray(), "demo", token);
    }

    private static Task<DashboardActionResult> OpenLogInPagerAsync(DashboardActionContext ctx, CancellationToken token)
    {
        var pager = Environment.GetEnvironmentVariable("PAGER");
        if (string.IsNullOrEmpty(pager)) pager = "less";
        var args = pager == "less" ? new[] { "-R", ctx.Snapshot.UsageLogPath } : new[] { ctx.Snapshot.UsageLogPath };
        return RunGuidedSubprocessAsync(ctx, pager, args, "pager", token);
    }

    private static Task<DashboardActionResult> OpenConfigInEditorAsync(DashboardActionContext ctx, CancellationToken token)
    {
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrEmpty(editor)) editor = "vi";
        var cfg = Path.Join(ctx.Root, ".sourcegraph.json");
        return RunGuidedSubprocessAsync(ctx, editor, new[] { cfg }, "editor", token);
    }

    /// <summary>
    /// Suspend Spectre's Live (caller is responsible for doing that), spawn the subprocess with
    /// inherited stdio, await exit, and return. The caller is the dashboard main loop which
    /// drops out of the <c>AnsiConsole.Live</c> block around this call.
    /// </summary>
    private static async Task<DashboardActionResult> RunGuidedAsync(DashboardActionContext ctx, string[] sgArgs, string label, CancellationToken token)
    {
        var (file, args) = ResolveSourceGraphLaunch(sgArgs);
        return await RunGuidedSubprocessAsync(ctx, file, args, label, token).ConfigureAwait(false);
    }

    private static async Task<DashboardActionResult> RunGuidedSubprocessAsync(DashboardActionContext ctx, string file, string[] args, string label, CancellationToken token)
    {
        try
        {
            // Pin the subprocess CWD to the dashboard's --root so editors, pagers, and any
            // implicit-CWD-aware tool operate against the same repo the snapshot describes —
            // not the dashboard process's launch directory, which may be elsewhere.
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false,
                WorkingDirectory = ctx.Root,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return DashboardActionResult.Failure($"{label} subprocess failed to start");
            await p.WaitForExitAsync(token).ConfigureAwait(false);
            ctx.Freshness?.RequestImmediateRebuild();
            return p.ExitCode == 0
                ? DashboardActionResult.Success($"{label} completed")
                : DashboardActionResult.Failure($"{label} exited with code {p.ExitCode}");
        }
        catch (Exception ex)
        {
            return DashboardActionResult.Failure($"{label} failed: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Run an action under the policy's watchdog. On timeout the inner task is cancelled and a
    /// failure result is surfaced; on inner success the result is returned verbatim. Failures
    /// from inside the inner task pass through unchanged.
    /// </summary>
    public static async Task<DashboardActionResult> RunWatchdoggedAsync(Func<Task<DashboardActionResult>> body,
        DashboardActionContext ctx, CancellationToken outer)
    {
        var policy = ctx.Policy;
        // The body delegate is parameterless by design — every in-place action it wraps reaches
        // outside our process (writer file IO, SQLite, EmbeddingsManager subprocess) and there's
        // no reliable cancellation handle inside those code paths. The watchdog race below
        // therefore only times out the WAIT; the inner task itself is left to drain on its own
        // after we report the timeout. Don't allocate a linked CTS here just to throw it away.
        try
        {
            var inner = body();
            var done = await Task.WhenAny(inner, Task.Delay(policy.InPlaceWatchdog, outer)).ConfigureAwait(false);
            if (done == inner) return await inner.ConfigureAwait(false);
            // Watchdog tripped — the dashboard reports the timeout and the next snapshot tick
            // redraws whatever state the still-running body eventually produces.
            return DashboardActionResult.Failure($"action exceeded {policy.InPlaceWatchdog.TotalSeconds:F0}s watchdog");
        }
        catch (OperationCanceledException)
        {
            return DashboardActionResult.Failure("action cancelled");
        }
    }

    private static IClientConfigWriter MakeWriter(ClientId id) => id switch
    {
        ClientId.ClaudeCode => new ClaudeCodeWriter(),
        ClientId.Copilot => new CopilotWriter(),
        ClientId.Cursor => new CursorWriter(),
        ClientId.Continue => new ContinueWriter(),
        ClientId.ClaudeDesktop => new ClaudeDesktopWriter(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "no writer for this ClientId"),
    };

    private static byte[]? ReadFileBytesIfExists(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static EmbeddingsManager BuildEmbeddingsManager(DashboardActionContext ctx)
    {
        var modelInfo = new EmbeddingModelInfo(
            ctx.Snapshot.Embeddings.ModelId,
            DefaultEmbeddingModel.Dimension);
        var store = new ModelStore(NullLogger<ModelStore>.Instance);
        return new EmbeddingsManager(store, modelInfo, NullLogger<EmbeddingsManager>.Instance);
    }

    /// <summary>
    /// Snapshot clients in display order (project-scope first then user-scope), matching the
    /// rendering order so the selection cursor maps to the right row.
    /// </summary>
    private static IReadOnlyList<ClientRow> OrderedClients(DashboardSnapshot snapshot) =>
        snapshot.Clients.OrderBy(c => c.Scope == "project" ? 0 : 1).ToArray();

    /// <summary>
    /// Resolve the solution path for a named scope from <c>.sourcegraph.json</c>; falls back to
    /// the first detected solution under <c>--root</c> when the scope name is the synthesised
    /// default. Returns null when no solution can be determined.
    /// </summary>
    internal static string? ResolveSolutionForScope(DashboardActionContext ctx, string scopeName)
    {
        try
        {
            var config = ScopeConfigLoader.Load(ctx.Root);
            foreach (var s in config.Scopes)
            {
                if (string.Equals(s.Name, scopeName, StringComparison.Ordinal)
                    && s.ProjectSet is ScopeProjectSet.Solutions solutions
                    && solutions.Items.Count > 0)
                {
                    var first = solutions.Items[0];
                    return Path.IsPathRooted(first) ? first : Path.Join(s.Root, first);
                }
            }
        }
        catch (ScopeConfigException) { /* fall through to env detection */ }
        catch (IOException) { /* fall through to env detection */ }

        // Fall back to the first detected solution under <root> for the synthesised default scope.
        var detected = ctx.Snapshot.Environment.SolutionFiles;
        return detected.Count > 0 ? detected[0] : null;
    }

    /// <summary>
    /// Locate the right way to relaunch <c>sourcegraph-mcp</c>: try the binary by name first
    /// (global tool / on PATH), then the current ProcessPath. Mirrors InitCli's pre-warm logic.
    /// </summary>
    internal static (string File, string[] Args) ResolveSourceGraphLaunch(string[] sgArgs)
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            if (processPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var args = new List<string> { processPath };
                args.AddRange(sgArgs);
                return ("dotnet", args.ToArray());
            }
            // Native apphost.
            return (processPath, sgArgs);
        }
        return ("sourcegraph-mcp", sgArgs);
    }

    /// <summary>
    /// Re-invoke <c>sourcegraph-mcp</c> with the given arguments, inheriting stdio so the
    /// invocation is visible to the user when the dashboard isn't in Live mode. For in-place
    /// actions we capture nothing (the caller already cleared the live region); the user sees
    /// indexer output stream past, which is the same shape as <c>init</c>'s pre-warm.
    /// </summary>
    internal static async Task<int> RunSourceGraphCommandAsync(string[] sgArgs, CancellationToken token)
    {
        var (file, args) = ResolveSourceGraphLaunch(sgArgs);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return -1;
        await p.WaitForExitAsync(token).ConfigureAwait(false);
        return p.ExitCode;
    }

    /// <summary>
    /// Remove the <c>sourcegraph</c> entry from a JSON-based MCP config file. Used by the unwire
    /// action. Honours the same comment-aware refusal that the writer's Plan/Apply path does —
    /// we leave a config with JS comments alone and tell the user to edit manually.
    /// </summary>
    internal static DashboardActionResult UnwireFromConfigFile(string path)
    {
        if (!File.Exists(path)) return DashboardActionResult.Failure($"config not found: {path}");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException ex) { return DashboardActionResult.Failure($"read failed: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return DashboardActionResult.Failure($"read denied: {ex.Message}"); }

        // Comment-aware refusal: same chokepoint the writer uses.
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (CommentDetector.HasJsonComments(text))
            return DashboardActionResult.Failure("config has JS comments — edit `e` to remove manually");

        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (System.Text.Json.JsonException ex) { return DashboardActionResult.Failure($"malformed config: {ex.Message}"); }
        if (root is not JsonObject topObj) return DashboardActionResult.Failure("config root is not an object");

        // Try the canonical key `mcpServers` (Claude Code / Cursor / Claude Desktop) AND
        // `servers` (Copilot). The dashboard doesn't know which writer authored the file at
        // unwire time; trying both is honest given the unified "remove the sourcegraph entry"
        // contract.
        var removed = TryRemoveSourcegraphEntry(topObj, "mcpServers")
                   || TryRemoveSourcegraphEntry(topObj, "servers");
        if (!removed)
            return DashboardActionResult.Failure("no sourcegraph entry to remove");

        try
        {
            var json = topObj.ToJsonString(WriterJson.Indented2) + "\n";
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return DashboardActionResult.Success($"unwired sourcegraph from {Path.GetFileName(path)}");
        }
        catch (IOException ex) { return DashboardActionResult.Failure($"write failed: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return DashboardActionResult.Failure($"write denied: {ex.Message}"); }
    }

    private static bool TryRemoveSourcegraphEntry(JsonObject root, string topKey)
    {
        if (root[topKey] is not JsonObject map) return false;
        if (!map.ContainsKey("sourcegraph")) return false;
        map.Remove("sourcegraph");
        return true;
    }
}
