using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Top-level <c>sourcegraph-mcp status</c> subcommand. Aggregates a <see cref="DashboardSnapshot"/>
/// via <see cref="SnapshotBuilder.BuildAsync"/>, evaluates the exit code by walking each surface,
/// then dispatches to either <see cref="StatusRenderer.RenderHuman"/> or the JSON serializer.
///
/// <para>
/// Exit semantics:
///   <list type="bullet">
///   <item><c>0</c> — every surface healthy.</item>
///   <item><c>2</c> — any surface warns (partial / indexing scope, embedding cache absent, git missing).</item>
///   <item><c>1</c> — any surface hard-fails (SDK missing, .sourcegraph.json malformed, degraded scope, DB dir unwritable).</item>
///   </list>
/// </para>
///
/// <para>
/// <c>--watch</c> is honoured only under a tty (<see cref="Console.IsInputRedirected"/> == false);
/// piped/CI invocations downgrade silently to a single snapshot.
/// </para>
/// </summary>
internal static class StatusCli
{
    /// <summary>
    /// Production entry point: resolves flags, builds the snapshot, renders, returns the
    /// evaluated exit code. The "is stdio redirected?" probe defaults to a check covering
    /// stdin AND stdout (so <c>status --watch | cat</c> correctly downgrades — stdout being a
    /// pipe means the ANSI cursor codes would otherwise pour into the pipe).
    /// </summary>
    public static Task<int> RunAsync(CommandLine cli)
        => RunAsync(cli, static () => Console.IsInputRedirected || Console.IsOutputRedirected);

    /// <summary>
    /// Test-injectable overload: same flow as the public <see cref="RunAsync(CommandLine)"/>
    /// but routes the "is stdio redirected?" probe through the caller. Tests use this to make the
    /// watch/one-shot branch decision deterministic regardless of the host runner's stdio state.
    /// The probe should return true if EITHER stdin or stdout is redirected — watch mode needs
    /// both attached to a tty so the redraw codes don't pollute a downstream consumer.
    /// </summary>
    internal static async Task<int> RunAsync(CommandLine cli, Func<bool> isStdioRedirected)
    {
        var root = cli.ResolvedRepoRoot();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

        // `--watch` + `--json` is rejected: the watch loop emits ANSI cursor-positioning codes
        // before each re-render, which would interleave with the JSON document and break any
        // downstream parser. A user wanting "live JSON" should either use the dashboard or wrap
        // `status --json` in their own poller (which is what `--watch` is shorthand for in the
        // human-readable case).
        if (cli.Watch && cli.Json)
        {
            await Console.Error.WriteLineAsync(
                "status: --watch and --json are mutually exclusive (the watch loop interleaves ANSI redraw codes with stdout, which would corrupt the JSON document).").ConfigureAwait(false);
            return 2;
        }

        var options = new SnapshotOptions(
            ActivityBytes: cli.ActivityBytes ?? 524288,
            RecentActivityCap: 50,
            ModelId: cli.Model);

        if (cli.Watch && !isStdioRedirected())
        {
            return await RunWatchAsync(cli, root, home, options).ConfigureAwait(false);
        }

        // Default (non-watch / piped) path: build once, render once, exit.
        var snapshot = await SnapshotBuilder.BuildAsync(root, options).ConfigureAwait(false);
        snapshot = snapshot with { ExitCode = EvaluateExit(snapshot) };
        RenderOnce(snapshot, cli, root, home);
        return snapshot.ExitCode;
    }

    /// <summary>
    /// Watch loop: re-renders the snapshot in place every <c>--watch-interval</c> seconds until
    /// <see cref="Console.CancelKeyPress"/> fires. Uses ANSI cursor-home + clear-to-end (no
    /// external Spectre dependency); guarantees clean shutdown on Ctrl+C.
    /// </summary>
    /// <remarks>
    /// On Ctrl+C the watch loop returns <c>0</c> regardless of the most recent snapshot's exit
    /// code. The spec scenario "--watch refreshes in place under a tty" explicitly requires
    /// clean SIGINT exit semantics, and propagating a non-zero snapshot status (e.g. an
    /// embedding-cache warning) would surprise an operator who hits Ctrl+C to exit a healthy
    /// tail. The exit-code semantics on the one-shot path are unchanged.
    /// </remarks>
    private static async Task<int> RunWatchAsync(CommandLine cli, string root, string? home, SnapshotOptions options)
    {
        using var cts = new CancellationTokenSource();
        var cancelledViaCtrlC = false;
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancelledViaCtrlC = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        var interval = TimeSpan.FromSeconds(Math.Max(1, cli.WatchInterval ?? 2));
        var first = true;
        var lastExit = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Wrap the whole loop body so an OperationCanceledException from either
                // `BuildAsync` or `Task.Delay` (both honour `cts.Token`) is treated as a
                // clean Ctrl+C exit. Without this, a Ctrl+C arriving mid-build would
                // propagate out and `--watch` would return non-zero with a stack trace.
                try
                {
                    if (!first)
                    {
                        // Cursor home + clear to end. Stable on every terminal that honours ANSI.
                        Console.Write("\x1b[H\x1b[J");
                    }
                    first = false;
                    var snap = await SnapshotBuilder.BuildAsync(root, options, cts.Token).ConfigureAwait(false);
                    snap = snap with { ExitCode = EvaluateExit(snap) };
                    RenderOnce(snap, cli, root, home);
                    lastExit = snap.ExitCode;
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* clean exit on Ctrl+C */ }
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
        return cancelledViaCtrlC ? 0 : lastExit;
    }

    /// <summary>
    /// One render — JSON or prose — to <see cref="Console.Out"/>. Extracted so the watch loop
    /// and the one-shot path share the same dispatch.
    /// </summary>
    private static void RenderOnce(DashboardSnapshot snapshot, CommandLine cli, string root, string? home)
    {
        if (cli.Json)
        {
            var json = DashboardSnapshotJson.Serialize(snapshot);
            Console.Out.WriteLine(json);
            return;
        }
        var options = new StatusRenderOptions(Root: root, Home: home, NoColor: cli.NoColor);
        StatusRenderer.RenderHuman(snapshot, Console.Out, options);
    }

    /// <summary>
    /// Walk the snapshot surface-by-surface and decide the aggregate exit code per spec:
    /// <c>1</c> on hard-fail, <c>2</c> on warn, <c>0</c> on healthy.
    /// </summary>
    public static int EvaluateExit(DashboardSnapshot snapshot)
    {
        var hardFail = false;
        var warn = false;

        // Environment.
        if (snapshot.Environment.SourceGraphConfigStatus == "malformed") hardFail = true;
        if (string.IsNullOrEmpty(snapshot.Environment.DotnetSdkVersion)) hardFail = true;
        if (!Directory.Exists(snapshot.Environment.RepoRootPath)) hardFail = true;
        if (!snapshot.Environment.GitOnPath) warn = true;
        // A repo with no detectable .slnx / .sln warns. Matches what StatusRenderer and
        // DashboardRenderer.ComputeEnvironmentSummary surface as a warning dot, and what
        // doctor reports as a warn check. Without this the exit code and the rendered
        // surface disagree on "is this repo healthy?".
        if (snapshot.Environment.SolutionFiles.Count == 0) warn = true;

        // Scopes.
        foreach (var s in snapshot.Scopes)
        {
            if (s.Status == "degraded") hardFail = true;
            if (s.Status is "partial" or "indexing") warn = true;
        }

        // Per-scope DB dir writability — fail when the dir exists but isn't writable. We don't
        // require the dir to exist (a fresh repo has none, and that's not a fail).
        var scopeDir = Path.Join(snapshot.Environment.RepoRootPath, ".sourcegraph", "scopes");
        if (Directory.Exists(scopeDir) && !TestWritability(scopeDir)) hardFail = true;

        // Embeddings.
        if (!snapshot.Embeddings.CachePresent) warn = true;

        if (hardFail) return 1;
        if (warn) return 2;
        return 0;
    }

    private static bool TestWritability(string dir)
    {
        try
        {
            var probe = Path.Join(dir, ".sg-status-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
