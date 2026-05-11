using System.Diagnostics;
using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Implementation of the <c>sourcegraph-mcp init</c> subcommand: detects environment, picks
/// MCP clients (interactive or flag-driven), runs each client's writer, optionally pre-warms
/// the index, and prints a closing report. Project-scoped writes are the default; user-scope
/// writes require an explicit per-client flag.
///
/// The rendering layer lives in <see cref="InitRenderer"/>; this method composes the phase
/// transitions and threads the right inputs into each renderer call. The split keeps the policy
/// (what fires when, what's selected) here and the presentation (column widths, glyphs, two-
/// space indentation) in the renderer module so future operator-console work can reuse the same
/// row primitives.
/// </summary>
internal static class InitCli
{
    public static async Task<int> RunAsync(CommandLine cli)
    {
        var root = cli.ResolvedRepoRoot();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        var detection = await OnboardingDetector.DetectAsync(root).ConfigureAwait(false);
        var interactive = !cli.Yes && !cli.PrintOnly && IsStdinInteractive();

        if (!cli.PrintOnly)
        {
            InitRenderer.RenderBanner(Console.Out);
            InitRenderer.RenderEnvironment(Console.Out, detection, root, home);
        }

        if (detection.SourceGraphConfigStatus == SourceGraphConfigStatus.Malformed)
        {
            await Console.Error.WriteLineAsync(
                $"error: .sourcegraph.json is malformed: {detection.SourceGraphConfigError}")
                .ConfigureAwait(false);
            return 1;
        }

        // Resolve solution + scope mode.
        var (solutionPath, useRootMode) = ResolveSolutionMode(cli, detection, interactive);

        // If multi-solution and no .sourcegraph.json yet, scaffold one (delegates to existing
        // init-scopes core logic). Skipped under --print-only because it would write to disk.
        if (!cli.PrintOnly && useRootMode &&
            detection.SourceGraphConfigStatus == SourceGraphConfigStatus.Missing &&
            detection.SolutionFiles.Count > 1)
        {
            var ok = await ScaffoldSourceGraphConfigAsync(root, detection, interactive).ConfigureAwait(false);
            if (!ok) return 1;
        }

        // Resolve enabled clients (slug → user-scope?).
        var enabledClients = ResolveEnabledClients(cli, detection, interactive);
        if (enabledClients.Count == 0)
        {
            Console.WriteLine("No clients selected. Nothing to do.");
            return 0;
        }

        var installMode = ParseInstallMode(cli.InstallMode);

        // Apply phase heading — emitted once, before the writer loop, so progress rows can stream
        // under it as each writer runs.
        if (!cli.PrintOnly)
        {
            InitRenderer.RenderApplyHeading(Console.Out);
        }

        // Run each writer. Collect results for the closing report.
        var results = new List<WriterRunResult>();
        foreach (var (clientId, useUserScope) in enabledClients)
        {
            var writer = MakeWriter(clientId);
            var targetPath = useUserScope
                ? writer.DefaultUserPath()
                : writer.DefaultProjectPath(root);
            if (string.IsNullOrEmpty(targetPath))
            {
                // Specific guidance per known combo so the user knows why the scope was skipped.
                var msg = (clientId, useUserScope) switch
                {
                    (ClientId.Copilot, true) =>
                        "user-scope Copilot wiring (chat.mcp.servers in settings.json) is not " +
                        "supported by `init` in v1; use the project-scope `.vscode/mcp.json` " +
                        "(re-run without --user-copilot) or paste the snippet from `--print-only` " +
                        "into your VS Code user settings manually",
                    _ => $"client `{clientId.ToSlug()}` has no {(useUserScope ? "user" : "project")}-scope target path",
                };
                Console.Error.WriteLine($"warn: skipping {clientId.ToSlug()} ({(useUserScope ? "user" : "project")}): {msg}");
                if (!cli.PrintOnly)
                {
                    InitRenderer.RenderApplyRow(Console.Out, WriterAction.SkipUnsupported,
                        clientId.ToSlug(), targetPath ?? "(no target path)", msg, root, home);
                }
                results.Add(new WriterRunResult(clientId, useUserScope, "(no target path)",
                    WriterAction.SkipUnsupported, msg));
                continue;
            }
            byte[]? existing = null;
            try
            {
                if (File.Exists(targetPath)) existing = File.ReadAllBytes(targetPath);
            }
            catch (IOException ex)
            {
                var msg = $"could not read existing file: {ex.Message}";
                if (!cli.PrintOnly)
                {
                    InitRenderer.RenderApplyRow(Console.Out, WriterAction.SkipExistingDiffers,
                        clientId.ToSlug(), targetPath, msg, root, home);
                }
                results.Add(new WriterRunResult(clientId, useUserScope, targetPath,
                    WriterAction.SkipExistingDiffers, msg));
                continue;
            }
            // Force `--no-history` into the emitted args when git isn't on PATH. Without git the
            // server's history pipeline can't function, and the detection summary already told
            // the user this would happen — making the implication explicit avoids a confusing
            // half-broken first run.
            var noHistory = cli.NoHistory || !detection.GitOnPath;
            var ctx = new WriterContext(
                Root: root,
                TargetPath: targetPath,
                UseUserScope: useUserScope,
                InstallMode: installMode,
                SolutionPath: useRootMode ? null : solutionPath,
                ServerProjectPath: null,
                NoEmbeddings: cli.NoEmbeddings,
                NoHistory: noHistory,
                Force: cli.Force,
                ExistingContent: existing);
            var plan = writer.Plan(ctx);

            if (cli.PrintOnly)
            {
                PrintPlanToStdout(plan);
                results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                    plan.Action, plan.Description));
                continue;
            }

            // --diff: when the plan would skip-on-conflict (or under --force, would overwrite a
            // differing entry), render a unified diff of existing-vs-proposed bytes so the user
            // can see what changes. --force composes: print the diff first, then proceed with
            // the write. The diff is a no-op for Insert / NoOpAlreadyMatches / SkipHasComments /
            // SkipUnsupported plans — those carry no useful "what changed" comparison.
            var diffApplies = cli.Diff && existing is not null && (
                plan.Action == WriterAction.SkipExistingDiffers ||
                plan.Action == WriterAction.ReplaceOurs);
            if (diffApplies)
            {
                Rendering.UnifiedDiffRenderer.Render(
                    existing!,
                    plan.ContentBytes,
                    fromLabel: plan.TargetPath,
                    toLabel: plan.TargetPath + ".proposed",
                    writer: Console.Out);
            }

            try
            {
                writer.Apply(plan);
            }
            catch (IOException ex)
            {
                var msg = $"apply failed: {ex.Message}";
                InitRenderer.RenderApplyRow(Console.Out, WriterAction.SkipExistingDiffers,
                    clientId.ToSlug(), plan.TargetPath, msg, root, home);
                results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                    WriterAction.SkipExistingDiffers, msg));
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                var msg = $"apply failed: {ex.Message}";
                InitRenderer.RenderApplyRow(Console.Out, WriterAction.SkipExistingDiffers,
                    clientId.ToSlug(), plan.TargetPath, msg, root, home);
                results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                    WriterAction.SkipExistingDiffers, msg));
                continue;
            }

            // Render the Apply row for the outcome.
            InitRenderer.RenderApplyRow(Console.Out, plan.Action,
                clientId.ToSlug(), plan.TargetPath, plan.Description, root, home);

            // Comment-aware degraded path: even when not --print-only, a SkipHasComments outcome
            // emits the snippet to stdout so the user can paste manually.
            if (plan.Action == WriterAction.SkipHasComments)
            {
                PrintPlanToStdout(plan);
            }

            results.Add(new WriterRunResult(clientId, useUserScope, plan.TargetPath,
                plan.Action, plan.Description));
        }

        // Pre-warm — interactive default is ON; --yes default is OFF.
        var prewarmDefault = interactive && !cli.PrintOnly;
        var prewarm = cli.Prewarm ?? prewarmDefault;
        if (prewarm && !cli.PrintOnly && !string.IsNullOrEmpty(solutionPath))
        {
            await PrewarmAsync(solutionPath, root).ConfigureAwait(false);
        }

        if (!cli.PrintOnly)
        {
            InitRenderer.RenderNext(Console.Out, new[]
            {
                "Open this repo in your MCP client.",
                "Verify with `sourcegraph-mcp demo`.",
            });
        }

        // Exit code: 0 unless any writer skipped because of a conflict; 2 in that case (matches
        // the doctor / vocabulary --strict precedent for "warning-as-failure" CI signals).
        return results.Any(r => r.Action == WriterAction.SkipExistingDiffers) ? 2 : 0;
    }

    /// <summary>
    /// True if stdin is a terminal (we can prompt), false if it's a pipe / redirected. Used to
    /// silently fall back to <c>--yes</c>-style defaults in non-tty contexts even when the user
    /// didn't pass <c>--yes</c> explicitly.
    /// </summary>
    private static bool IsStdinInteractive()
    {
        try { return !Console.IsInputRedirected; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Decide whether to use <c>--solution &lt;path&gt;</c> mode (single solution, args carry the
    /// resolved path) or <c>--root</c> mode (multi-scope, args carry the workspace folder).
    /// In single-solution and explicit-flag cases we use the path; otherwise we use root mode.
    /// </summary>
    private static (string? SolutionPath, bool UseRootMode) ResolveSolutionMode(
        CommandLine cli, OnboardingDetectionResult d, bool interactive)
    {
        // Explicit --solution wins.
        if (cli.Solutions.Count == 1)
        {
            return (cli.Solutions[0], UseRootMode: false);
        }
        if (cli.Solutions.Count > 1)
        {
            return (SolutionPath: null, UseRootMode: true);
        }
        // Existing .sourcegraph.json → root mode (multi-scope is configured).
        if (d.SourceGraphConfigStatus == SourceGraphConfigStatus.Valid)
        {
            return (SolutionPath: null, UseRootMode: true);
        }
        // No solutions detected → use the workspace-folder placeholder (user fills in later).
        if (d.SolutionFiles.Count == 0)
        {
            return ("${workspaceFolder}/MySolution.slnx", UseRootMode: false);
        }
        // Single solution → use it.
        if (d.SolutionFiles.Count == 1)
        {
            // Encode as ${workspaceFolder}-relative when possible so the resulting config is
            // portable across machines.
            var rel = TryRelativeWorkspacePath(d.RepoRootPath, d.SolutionFiles[0]);
            return (rel, UseRootMode: false);
        }
        // Multiple solutions, no .sourcegraph.json → root mode (and we'll scaffold the config
        // inside RunAsync).
        return (SolutionPath: null, UseRootMode: true);
    }

    private static string TryRelativeWorkspacePath(string root, string absolute)
    {
        var rel = Path.GetRelativePath(root, absolute);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return absolute;
        }
        return "${workspaceFolder}/" + rel.Replace('\\', '/');
    }

    /// <summary>
    /// Decide which (clientId, useUserScope) pairs to wire. Honours <c>--client &lt;id&gt;</c>,
    /// <c>--no-&lt;client&gt;</c>, <c>--user-&lt;client&gt;</c>, and <c>--claude-desktop</c>; falls back
    /// to "every client whose project-scope path is sensible" when no explicit flags are set.
    /// </summary>
    private static List<(ClientId Id, bool UseUserScope)> ResolveEnabledClients(
        CommandLine cli, OnboardingDetectionResult d, bool interactive)
    {
        // Build the candidate set from --client (when set), or from auto-detected defaults.
        HashSet<ClientId> candidates;
        if (cli.Clients.Count > 0)
        {
            candidates = new HashSet<ClientId>();
            foreach (var raw in cli.Clients)
            {
                foreach (var slug in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ClientIdExtensions.TryParseSlug(slug, out var id))
                    {
                        candidates.Add(id);
                    }
                    else
                    {
                        Console.Error.WriteLine($"warn: unknown --client value: {slug}");
                    }
                }
            }
        }
        else
        {
            // Default candidates come from detection-driven signals. claude-code and copilot are
            // always on; cursor / continue / claude-desktop flip based on what's installed on this
            // machine (matches the `init batched client picker` requirement).
            var defaults = OnboardingDetector.ComputePickerDefaults(d.RepoRootPath);
            candidates = new HashSet<ClientId>(defaults.Where(kv => kv.Value).Select(kv => kv.Key));
        }
        // --claude-desktop forces default-on regardless of detection (documented escape hatch).
        if (cli.ClaudeDesktop) candidates.Add(ClientId.ClaudeDesktop);

        // Apply --no-<client> drops. Slugs that don't parse to a known ClientId are silently
        // ignored — the parser already rejected them with a warn earlier in this method.
        foreach (var id in cli.NoClients.Select(TryParseClientIdOrNull).Where(x => x.HasValue))
        {
            candidates.Remove(id!.Value);
        }

        // Interactive picker (only when no explicit --client was given).
        if (interactive && cli.Clients.Count == 0)
        {
            candidates = InteractiveClientPicker(candidates, d);
        }

        // Map to (id, useUserScope). User-scope is requested via --user-<client>.
        var results = new List<(ClientId, bool)>();
        foreach (var id in candidates.OrderBy(c => (int)c))
        {
            var slug = id.ToSlug();
            // Claude Desktop is always user-scope.
            var useUser = id == ClientId.ClaudeDesktop || cli.UserClients.Contains(slug);
            results.Add((id, useUser));
        }
        return results;
    }

    private static HashSet<ClientId> InteractiveClientPicker(
        HashSet<ClientId> autoSelected, OnboardingDetectionResult d)
    {
        // Section-6 wiring will replace this method with the batched-grammar picker. Today's body
        // is the per-row [Y/n] flow with every client (including claude-desktop) always visible.
        Console.WriteLine("Which clients should I wire up? Type '+slug -slug' to edit, Enter to accept, 'n' to skip all.");
        var defaults = OnboardingDetector.ComputePickerDefaults(d.RepoRootPath);
        var picked = new HashSet<ClientId>();
        var slugs = new List<string>();
        foreach (var id in Enum.GetValues<ClientId>())
        {
            var defaultYes = autoSelected.Contains(id) || defaults[id];
            var glyph = defaultYes
                ? Rendering.StateGlyph.For(Rendering.StateGlyphKind.On)
                : Rendering.StateGlyph.For(Rendering.StateGlyphKind.Off);
            Console.WriteLine($"  {glyph}{id.ToSlug(),-15}");
            if (defaultYes) slugs.Add(id.ToSlug());
        }
        var knownSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            "claude-code", "copilot", "cursor", "continue", "claude-desktop",
        };
        Console.Write("  Accept defaults? [Y/n]  or edit (e.g. \"+cursor -copilot\"): ");
        var raw = Console.ReadLine();
        var defaultsSet = new HashSet<string>(slugs, StringComparer.Ordinal);
        var result = BatchedPickerInput.Parse(raw ?? string.Empty, defaultsSet, knownSlugs);
        if (result.NeedsReprompt)
        {
            Console.Write("  Invalid input. Accept defaults? [Y/n]  or edit (e.g. \"+cursor -copilot\"): ");
            raw = Console.ReadLine();
            result = BatchedPickerInput.Parse(raw ?? string.Empty, defaultsSet, knownSlugs);
            if (result.NeedsReprompt)
            {
                // Second invalid input: treat as 'n' (deselect all) and proceed.
                result = BatchedPickerInput.AllOff();
            }
        }
        foreach (var slug in result.UnknownSlugs)
        {
            Console.Error.WriteLine($"warn: unknown picker slug ignored: {slug}");
        }
        foreach (var slug in result.Selection)
        {
            if (ClientIdExtensions.TryParseSlug(slug, out var id))
            {
                picked.Add(id);
            }
        }
        return picked;
    }

    private static ClientId? TryParseClientIdOrNull(string slug) =>
        ClientIdExtensions.TryParseSlug(slug, out var id) ? id : null;

    private static bool NormaliseYesNo(string? line, bool defaultYes)
    {
        if (string.IsNullOrWhiteSpace(line)) return defaultYes;
        var c = char.ToLowerInvariant(line.Trim()[0]);
        return c switch
        {
            'y' => true,
            'n' => false,
            _ => defaultYes,
        };
    }

    private static InstallMode ParseInstallMode(string? raw) => raw?.ToLowerInvariant() switch
    {
        null => ClientConfigWriters.InstallMode.Global,
        "global" => ClientConfigWriters.InstallMode.Global,
        "local-tool" => ClientConfigWriters.InstallMode.LocalTool,
        "in-repo" => ClientConfigWriters.InstallMode.InRepo,
        _ => ClientConfigWriters.InstallMode.Global,
    };

    private static IClientConfigWriter MakeWriter(ClientId id) => id switch
    {
        ClientId.ClaudeCode => new ClaudeCodeWriter(),
        ClientId.Copilot => new CopilotWriter(),
        ClientId.Cursor => new CursorWriter(),
        ClientId.Continue => new ContinueWriter(),
        ClientId.ClaudeDesktop => new ClaudeDesktopWriter(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "no writer for this ClientId"),
    };

    private static async Task<bool> ScaffoldSourceGraphConfigAsync(
        string root, OnboardingDetectionResult d, bool interactive)
    {
        if (interactive)
        {
            Console.WriteLine($"Detected {d.SolutionFiles.Count} solutions; will scaffold .sourcegraph.json (one scope per solution).");
            Console.Write("Proceed? [Y/n] ");
            if (!NormaliseYesNo(Console.ReadLine(), defaultYes: true))
            {
                Console.WriteLine("Skipping .sourcegraph.json scaffolding.");
                return true;
            }
        }
        var fakeCli = new CommandLine().WithRoot(root);
        var rc = await ScopesCli.RunInitAsync(fakeCli).ConfigureAwait(false);
        return rc == 0;
    }

    /// <summary>
    /// Print the snippet a writer would emit, prefixed by a comment line naming the target path
    /// and (when relevant) the writer's <see cref="WriterPlan.Description"/>. Used both for
    /// <c>--print-only</c> and the comment-aware degraded path; in the latter case the
    /// description carries the user-facing reason ("config has comments — paste manually") so
    /// surfacing it is what tells the user why nothing got written.
    /// </summary>
    private static void PrintPlanToStdout(WriterPlan plan)
    {
        Console.WriteLine($"# would write to: {plan.TargetPath}");
        if (plan.Action == WriterAction.SkipHasComments && !string.IsNullOrEmpty(plan.Description))
        {
            Console.WriteLine($"# {plan.Description}");
        }
        Console.Write(System.Text.Encoding.UTF8.GetString(plan.ContentBytes));
        Console.WriteLine();
    }

    private static async Task PrewarmAsync(string solutionPath, string root)
    {
        // Resolve to an absolute path. ${workspaceFolder} expansion handled by ExpandTokens.
        var expanded = CommandLine.ExpandTokens(solutionPath)
            .Replace("${workspaceFolder}", root, StringComparison.Ordinal);
        var abs = Path.IsPathRooted(expanded) ? expanded : Path.Join(root, expanded);
        if (!File.Exists(abs))
        {
            Console.Error.WriteLine($"warn: --prewarm requested but solution not found at {abs}");
            return;
        }
        var solutionName = Path.GetFileName(abs);
        InitRenderer.RenderPreWarmHeading(Console.Out, solutionName);
        var sw = Stopwatch.StartNew();
        // Shell out for the pre-warm so we don't re-create the indexer construction graph that
        // lives in Program.cs. The strategy is "re-invoke the same binary we are now," because
        // it's guaranteed to know the `index` subcommand and live with no install dependency.
        //
        // The entry binary is discovered in two ways with different reliability:
        //
        //   1. `Assembly.GetEntryAssembly()?.Location` — the entry .dll path. Reliable in
        //      every run mode (dev `dotnet <dll>`, global tool, `dotnet publish` apphost,
        //      local-tool manifest); empty only under single-file deployment.
        //
        //   2. `Environment.ProcessPath` — the executable that started the process. In dev
        //      mode this is the dotnet host (e.g. `/usr/local/share/dotnet/dotnet`), which is
        //      NOT a sourcegraph binary — so we only use it when it looks like an apphost
        //      (path ends with `sourcegraph-mcp` / `sourcegraph-mcp.exe`).
        //
        // Strategies are tried in order; the first one that starts AND exits 0 wins. We try
        // the entry-self forms first because they always work in dev mode (the most common
        // local-test context). The `dotnet sourcegraph-mcp` global-tool form is the fallback
        // for the single-file edge case where neither entry hint is available.
        var attempts = new List<(string FileName, string[] Args)>();
        var entryDll = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(entryDll) && entryDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add(("dotnet", new[] { entryDll, "index", abs }));
        }
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && IsSourceGraphApphost(processPath))
        {
            attempts.Add((processPath, new[] { "index", abs }));
        }
        // Global-tool fallback. Only reachable when neither entry hint resolved (single-file
        // publish without an embedded dll path). Carries the original implementation's
        // assumption that `sourcegraph-mcp` is installed on PATH via `dotnet tool install -g`.
        attempts.Add(("dotnet", new[] { "sourcegraph-mcp", "index", abs }));

        // Capture stderr from each attempt rather than inheriting it; we only surface output
        // from the attempt we accept (exit 0) so a transient first-strategy failure doesn't
        // leak a confusing "Could not execute…" message before the (successful) fallback.
        // stdout we still inherit because successful indexer runs print progress that's worth
        // seeing live; an interleave with our own rendering is acceptable since the indexer is
        // the only live writer at this point.
        string? lastStderr = null;
        int? lastExit = null;
        foreach (var (fileName, args) in attempts)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi);
                if (p is null) continue;
                // Drain stderr concurrently with the wait so a chatty stderr can't deadlock
                // the child. We re-emit it only if this attempt wins.
                var stderrTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                if (p.ExitCode == 0)
                {
                    if (!string.IsNullOrEmpty(stderr)) Console.Error.Write(stderr);
                    sw.Stop();
                    InitRenderer.RenderPreWarmSummary(Console.Out, p.ExitCode, sw.Elapsed, solutionName);
                    return;
                }
                // Non-zero exit. Could be "tool not installed" (try next) OR "indexer failed"
                // (would surface as the final result if no later attempt wins). Remember and
                // continue.
                lastStderr = stderr;
                lastExit = p.ExitCode;
                continue;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // FileName not found / not executable — try the next strategy.
                continue;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"warn: pre-warm failed (i/o): {ex.Message}");
                return;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"warn: pre-warm failed (process state): {ex.Message}");
                return;
            }
        }
        // No strategy succeeded. Re-emit the most recent stderr (closest to what the user
        // would have wanted to see) and a single summary line for the closing report.
        sw.Stop();
        if (!string.IsNullOrEmpty(lastStderr)) Console.Error.Write(lastStderr);
        if (lastExit.HasValue)
        {
            InitRenderer.RenderPreWarmSummary(Console.Out, lastExit.Value, sw.Elapsed, solutionName);
        }
        else
        {
            Console.Error.WriteLine("warn: pre-warm could not start any subprocess. Run `sourcegraph-mcp index <solution>` manually if needed.");
        }
    }

    /// <summary>
    /// Heuristic: is <paramref name="processPath"/> a sourcegraph-mcp apphost (vs. the dotnet
    /// host or some unrelated launcher)? Apphosts produced by <c>dotnet publish</c> and
    /// <c>dotnet tool install -g</c> name themselves after the assembly entry-point; we accept
    /// any file whose base name (without `.exe`) is exactly <c>sourcegraph-mcp</c>.
    /// </summary>
    private static bool IsSourceGraphApphost(string processPath)
    {
        var name = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(name, "sourcegraph-mcp", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WriterRunResult(
        ClientId ClientId,
        bool UserScope,
        string TargetPath,
        WriterAction Action,
        string Description);
}

/// <summary>
/// Internal helper to construct a <see cref="CommandLine"/> with just <c>RepoRoot</c> set, used
/// when init delegates to <see cref="ScopesCli.RunInitAsync"/> for multi-solution scaffolding.
/// </summary>
internal static class CommandLineFactoryExtensions
{
    public static CommandLine WithRoot(this CommandLine _, string root)
    {
        // CommandLine's setters are init-only; the cleanest way to "construct with a root" is to
        // round-trip through Parse with a synthetic args array.
        return CommandLine.Parse(new[] { "init-scopes", "--root", root });
    }
}
