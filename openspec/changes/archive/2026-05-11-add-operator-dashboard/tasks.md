## 1. Foundation: Spectre.Console + project plumbing

- [x] 1.1 Add `Spectre.Console` NuGet reference (pinned to a specific 0.49.x patch) to `src/DevBitsLab.Mcp.SourceGraph.Server/DevBitsLab.Mcp.SourceGraph.Server.csproj`.
- [x] 1.2 Verify the package's AOT-trim warnings are zero (the current build is not AOT but we want the door open); run `dotnet build /warnaserror` after add.
- [x] 1.3 Create folder `src/DevBitsLab.Mcp.SourceGraph.Server/Dashboard/` and add an `Internal` namespace marker file (otherwise auto-imports trip up).

## 2. Freshness source

- [x] 2.1 Add `Dashboard/FreshnessSource.cs` implementing the hybrid poll-plus-watcher model per design Decision 5. Public surface: ctor takes `(string root, SnapshotBuilderOptions options)`; `event Action<DashboardSnapshot> SnapshotChanged`; `Latest` property; `Start()` / `Stop()`; `IDisposable`.
- [x] 2.2 Internal: `Timer` set to 1 s tick; two `FileSystemWatcher` instances (one per JSONL log) with a 100 ms debounce timer; a `lastRebuild` timestamp and a coalescing predicate.
- [x] 2.3 Snapshot rebuilds run via `Task.Run(() => SnapshotBuilder.BuildAsync(...))` (thread-pool); on completion, marshal back to the UI thread via a `BlockingCollection<Action>` queue the main loop drains.
- [x] 2.4 Unit-test the coalescing: simulate 10 rapid watcher events in 200 ms; assert exactly one rebuild executes; simulate a 5 s idle then one watcher event; assert one rebuild executes within 100 ms.
- [x] 2.5 Stress-test: run 60 ticks at 1 s intervals against a fixture repo; assert no FileSystemWatcher leak (close count == open count); assert SQLite connection pool count stable.

## 3. Layout, renderers, and key-map

- [x] 3.1 Add `Dashboard/DashboardLayout.cs` exposing `static Layout Build(int width, int height)` returning a Spectre `Layout` with the five named sections plus header and footer regions. Refuse to build when `width < 80 || height < 24` (return `Layout` with a single `terminal too small` panel).
- [x] 3.2 Add `Dashboard/DashboardRenderer.cs` with one `IRenderable Build<X>Section(DashboardSnapshot snapshot)` per section. Reuse the `StateGlyph` helper from `polish-init-onboarding` for the glyph language; reuse the path-display rule for the header `--root` rendering.
- [x] 3.3 Add `Dashboard/DashboardKeyMap.cs` exposing the central key-to-action table per design Decision 3. Public surface: `bool TryResolve(ConsoleKeyInfo key, out DashboardAction action)`. Aliases (`j` == `↓`, `k` == `↑`) are explicit table entries.
- [x] 3.4 Unit-test `DashboardKeyMap`: assert every key in the documented table resolves to exactly one action; assert unmapped keys return `false` without throwing.
- [x] 3.5 Snapshot-render tests: feed a fixture `DashboardSnapshot` into each `Build*Section` method; capture the rendered Spectre string (via `AnsiConsole`'s recording API); compare against a golden file. Maintain emoji + `--no-leaf` golden pairs per section.

## 4. Confirm modal

- [x] 4.1 Add `Dashboard/ConfirmModal.cs` exposing `static bool Prompt(string verb, string target)` that renders a Spectre `ConfirmationPrompt` titled `<verb> <target>?`, defaults `false`, returns the user's `y`/`n` choice.
- [x] 4.2 Unit-test the modal: drive the prompt with mocked `IAnsiConsole.Input` returning each of `y`, `n`, `Esc`, `Enter`; assert returned boolean.

## 5. Action dispatcher

- [x] 5.1 Add `Dashboard/DashboardActions.cs` exposing methods per `DashboardAction` enum value. Each method receives a `DashboardActionContext` (selected section, selected row, current snapshot, `IAnsiConsole` for prompts, an `IFreshnessSource` to trigger explicit refreshes).
- [x] 5.2 Implement read-only navigation methods (`MoveSelection`, `OpenDetail`, `Quit`, `ToggleHelp`).
- [x] 5.3 Implement in-place actions, each delegating to the same code paths the headless CLI subcommands use:
  - `ReindexScope` → `ReconcileDriftCommand.Run`.
  - `RebuildScope` → confirm modal, then `RepairScopeCommand.Run(mode: "rebuild")`.
  - `WireClient` → calls into the polished InitCli writer flow (single client, no picker).
  - `UnwireClient` → confirm modal, then writer-level "remove sourcegraph entry" code path.
  - `EmbeddingsPull` → `EmbeddingsManager.PullAsync`.
  - `EmbeddingsVerify` → `EmbeddingsManager.VerifyAsync`.
- [x] 5.4 Implement guided actions per design Decision 7 (suspend → spawn → resume):
  - `InitGuided` → spawn `sourcegraph-mcp init` as subprocess with inherited streams.
  - `DemoGuided` → spawn `sourcegraph-mcp demo --scope <selected>`.
  - `OpenLogInPager` → spawn `$PAGER` (default `less -R`) with the relevant JSONL file as arg.
  - `OpenConfigInEditor` → spawn `$EDITOR` (default `vi`) against `.sourcegraph.json`.
- [x] 5.5 Watchdog: any in-place action that exceeds 30 seconds raises a cancellation token; the dashboard surfaces the failure via the status bar with retry / quit options.
- [x] 5.6 Tests cover each action with mocked dependencies; the long-running watchdog branch exercises a synthetic action that sleeps 35 s.

## 6. Main loop + bare-command dispatch

- [x] 6.1 Add `Dashboard/DashboardCli.cs` with `public static async Task<int> RunAsync(CommandLine cli, CancellationToken token)`. Top-level flow: `using var freshness = new FreshnessSource(...); freshness.Start(); AnsiConsole.Live(layout).StartAsync(ctx => RunLoopAsync(ctx, freshness, token))`.
- [x] 6.2 The render loop reads keys via `Console.ReadKey(intercept: true)` in a non-blocking poll, dispatches via `DashboardKeyMap.TryResolve` plus `DashboardActions`, and triggers a Spectre `Refresh()` on each rebuild event from `FreshnessSource.SnapshotChanged`.
- [x] 6.3 In `Program.Main`, before `CommandLine.Parse`, add the bare-command dispatch per design Decision 2: if `args.Length == 0` AND no `--help`/`-h` present, rewrite `args` to either `["dashboard"]` or `["status"]` based on `Console.IsInputRedirected || !Environment.UserInteractive`.
- [x] 6.4 Register the `dashboard` verb in `CommandLine.cs` and route to `DashboardCli.RunAsync`. Add the new flags `--root` (already supported), `--no-color`, and the existing `--no-leaf` plumbing.
- [x] 6.5 Update `HelpText` in `CommandLine.cs` to document `sourcegraph-mcp dashboard` and the bare-invocation behaviour.

## 7. Tests

- [x] 7.1 `DashboardKeyMapTests`: assert documented bindings, alias resolution, unmapped key behaviour.
- [x] 7.2 `DashboardRendererTests`: golden-file comparisons for each section under emoji and `--no-leaf` modes, against a fixture `DashboardSnapshot` covering healthy / partial / degraded states.
- [x] 7.3 `FreshnessSourceTests`: coalescing, FD leak, snapshot lifecycle.
- [x] 7.4 `BareCommandDispatchTests`: simulate `Console.IsInputRedirected` true / false; assert routing to `dashboard` vs `status`; assert `--help` always shows help.
- [x] 7.5 `DashboardActionsTests`: confirm-modal gates on `[u]` and `[R]`; non-confirm on `[r]` `[w]` `[p]` `[v]`; happy-path completion of each in-place action via mocked dependencies.
- [x] 7.6 Manual smoke test: write `openspec/changes/add-operator-dashboard/notes/manual-smoke-test.md` enumerating the human-verification steps (launch under tty, resize the terminal, fire each key binding, confirm + cancel each modal, exit cleanly). Live TUI behavior is hard to test end-to-end; this captures the steps a maintainer runs before merge.

## 8. Documentation

- [x] 8.1 Add a `## Dashboard` subsection under the README's "Command-line interface" section. Include a full key-binding reference (matching design Decision 3), the bare-command behaviour, and the minimum terminal dimensions.
- [x] 8.2 Update the Quickstart in `README.md` to add `sourcegraph-mcp` (bare) as a recommended first verb alongside `sourcegraph-mcp init` and `sourcegraph-mcp demo`. Note that bare invocation drops into the dashboard for humans, and into `status` for scripts.
- [x] 8.3 Add a one-liner in `CLAUDE.md` noting that bare `sourcegraph-mcp` is the operator console (distinct from the agent-facing MCP server).
- [x] 8.4 Run `openspec validate add-operator-dashboard --strict`; fix any structural issues.
