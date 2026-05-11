## Context

The `add-operator-status` change establishes the snapshot model (`DashboardSnapshot`) and a static rendering (`StatusCli`). The dashboard layered here reuses the same snapshot — it does **not** introduce a parallel data model. The dashboard's job is presentation + interaction; everything underneath is shared.

```
   DashboardSnapshot  ─┬─► StatusCli (static, Change 2)
                      │
                      └─► DashboardCli (live + interactive, Change 3)
                              │
                              └─► DashboardActions  ─┬─► InitCli writers (Change 1)
                                                      ├─► ReconcileScope / RepairScope (existing)
                                                      ├─► EmbeddingsManager (existing)
                                                      └─► ScopesCli mutators (existing)
```

The action layer threads back into the very same code paths the headless CLI subcommands use. There's no "dashboard fork" of any business logic; the dashboard is a renderer plus a key dispatcher plus a confirm modal.

Spectre.Console (MIT, 0.49.x line) gives us:
- `Layout` for the five-section composition with proper terminal-size adaptation.
- `Live` for the in-place re-rendering driven by the freshness source.
- `Tree` / `Table` / `Markup` for each section's body.
- `AnsiConsole.Prompt(new ConfirmationPrompt(...))` for the confirm modal.
- Built-in `NO_COLOR` env-var handling that composes with our `--no-color` / `--no-leaf` knobs.

Spectre runs cross-platform and AOT-friendly. The current published-tool size is ~16 MB; Spectre adds ~1 MB. Acceptable.

The freshness source is the trickiest design point: re-reading SQLite every keystroke is wasteful; never refreshing means stale rows after a `serve` indexer finishes. The hybrid (1-second poll + `FileSystemWatcher` events on the two JSONL files) gives both bounded resource use and sub-second visibility for activity, which is what operators watch most.

## Goals / Non-Goals

**Goals:**
- One coherent live operator console that shows scope health, client wiring, embedding-cache status, and recent activity in one terminal window.
- Three action depths (read-only / guided / in-place) shipped together so the first version delivers the full vision.
- Bare `sourcegraph-mcp` lands in the dashboard for tty users — most discoverable possible default.
- All key actions delegate to existing code paths; no business logic forks into the dashboard.
- Destructive actions gate behind explicit confirmation; safe ones don't.
- Refresh model couples 1-second poll with FileSystemWatcher events on the two JSONL logs so live activity surfaces sub-second.

**Non-Goals:**
- Vim-style key bindings (`hjkl`, `:` command mode). Arrow + letter-accelerator is the v1 default; vim aliases could ship as a later refinement based on user feedback. The `DashboardKeyMap` design accommodates aliasing.
- Custom themes / colour overrides. Spectre's defaults plus `NO_COLOR` / `--no-color` are the v1 surface.
- Persistent dashboard state (last-selected-scope, last-viewed-tab). Each launch starts fresh; persisting state needs a config-file dance that's out of scope.
- Mouse support. Spectre supports it but adds platform variance; v1 is keyboard-only.
- Multi-pane / split-screen views. The dashboard renders five fixed sections in one screen.
- A "log tail" pane streaming `usage.jsonl` line-by-line. The `Recent activity` section caps at 50 entries; `[l]` opens the full log in `$PAGER` (guided action), which is the right tool for streaming.
- Refactoring `DashboardSnapshot`. Change 2 ships the snapshot; Change 3 consumes it without modification.

## Decisions

### Decision 1 — Spectre.Console as the TUI substrate

Pinned to the latest stable 0.49.x. Justifications already covered in proposal; key technical reasons:

- Production-tested in `azure-cli` port, `dotnet/aspire` tooling, and many .NET CLI tools that ship as global tools.
- `Live` API supports our exact use case (re-render on snapshot change) with built-in flicker prevention.
- AOT compatibility documented; we're not AOT-publishing today but want the door open.
- Bracketed-ASCII rendering when `NO_COLOR` is set composes well with `--no-leaf`.

**Alternatives considered:**
- `Terminal.Gui` v2. More general-purpose (window management, focus stacks). Overkill for our single-screen flow; heavier.
- Hand-rolled with `Console` + ANSI escapes. The terminal-size resize handling, focus management, and event loop quickly become a tar pit.

### Decision 2 — Bare-command dispatch via `Console.IsInputRedirected`

In `Program.Main`, before `CommandLine.Parse`, detect:

```
if (args.Length == 0)
{
    if (Console.IsInputRedirected || !Environment.UserInteractive)
        args = new[] { "status" };
    else
        args = new[] { "dashboard" };
}
```

`Console.IsInputRedirected` is true when stdin is a pipe or file. `Environment.UserInteractive` covers the niche case of a non-redirected stdin in a non-interactive context (rare; service-account runs). Either being true routes to `status`.

`--help` / `-h` is parsed before this branching (already today), so `sourcegraph-mcp --help` continues to print help.

**Alternatives considered:**
- Make bare-command always `status`. Loses the discoverability win; users have to know to type `dashboard`.
- Make bare-command always `dashboard`. Breaks CI scripts that run `sourcegraph-mcp` and parse stdout — `dashboard` requires a tty.
- Add a `--dashboard` flag. Doesn't solve the discovery problem; users still have to read help to find the verb.

### Decision 3 — Three action tiers and their key-binding table

The full key map (v1):

| Key | Tier | Action | Confirmation |
|---|---|---|---|
| `↑` / `↓` / `j` / `k` | read-only | navigate row within section | — |
| `Tab` / `Shift+Tab` | read-only | next / previous section | — |
| `Enter` | read-only | open detail pane for selected row | — |
| `Esc` | read-only | close detail pane; close confirm modal | — |
| `q` / `Ctrl+C` | read-only | quit dashboard with exit 0 | — |
| `?` | read-only | open keybindings help overlay | — |
| `r` | in-place | reindex selected scope (`reconcile_drift`) | no |
| `R` | in-place | rebuild selected scope (`repair_scope rebuild`) | **yes** |
| `w` | in-place | wire missing client (calls into InitCli writers) | no |
| `u` | in-place | unwire selected client | **yes** |
| `p` | in-place | embeddings pull (active model) | no |
| `v` | in-place | embeddings verify | no |
| `i` | guided | suspend dashboard, run `init` interactively, resume | — |
| `d` | guided | suspend, run `demo` for selected scope, resume | — |
| `l` | guided | open `usage.jsonl` in `$PAGER` | — |
| `e` | guided | open `.sourcegraph.json` in `$EDITOR` | — |
| `s` | read-only | force snapshot refresh now (out-of-band) | — |

**Tier definitions:**

- **read-only**: no state mutation; selection state lives in the dashboard only.
- **in-place**: mutates state inside the dashboard process; calls existing internal code paths; refreshes the snapshot afterwards.
- **guided**: suspends the dashboard (`AnsiConsole.Clear()`, restore cursor), spawns the corresponding subcommand as a subprocess with stdout/stderr inherited; on subprocess exit, redraws the dashboard. The user sees the subcommand's full output (good for `init` interactive flow, `demo`, `$PAGER`, `$EDITOR`).

**Alternatives considered:**
- A four-tier model splitting "fast in-place" from "slow in-place." Conflates orthogonal concerns (mutation kind vs. perceived latency); a busy indicator handles slow in-place actions cleanly.
- All actions guided (no in-place). Loses the "dashboard updates as you act" feel; every keystroke flashes the screen.
- All actions in-place (no guided). Forces fragile reimplementations of `init`'s interactive picker and `demo`'s output pipeline inside the dashboard.

### Decision 4 — Confirmation policy: destructive only

The principle: any action whose effects can't be cheaply re-done OR that touches user-visible state outside `.sourcegraph/` gates behind a `Spectre ConfirmationPrompt`. Specifically:

- `R` rebuild scope: archives the current DB; cheap to redo, but takes minutes.
- `u` unwire client: removes the `sourcegraph` entry from the target config file; the user can re-run `init` to restore, but the explicit "no" branch needs to exist.
- *(future)* `[D]` remove scope: removes a row from `.sourcegraph.json`. Strongly destructive; gate even with backups.

Reindex (`r`), wire (`w`), pull (`p`), verify (`v`) are all idempotent and additive — re-runnable without loss. They don't gate.

The confirm modal renders as a Spectre `ConfirmationPrompt` with the prompt text `<verb> <target>?` and default `N` (Esc-friendly). Returns boolean to the action dispatcher.

### Decision 5 — Refresh model: 1-second poll + FileSystemWatcher event coalescing

A single `FreshnessSource` instance:

```csharp
public sealed class FreshnessSource : IDisposable
{
    private readonly string _root;
    private readonly Timer _pollTimer;
    private readonly FileSystemWatcher _usageWatcher;
    private readonly FileSystemWatcher _healsWatcher;
    private DateTimeOffset _lastRebuild = DateTimeOffset.MinValue;
    private DashboardSnapshot? _latest;
    public event Action<DashboardSnapshot>? SnapshotChanged;
    public DashboardSnapshot? Latest => _latest;
    // poll tick: if no rebuild in last 1000 ms, rebuild
    // watcher event: schedule rebuild on a 100ms debounce timer
}
```

Coalescing rule: at most one rebuild per second under sustained activity (the poll tick AND the watcher trigger compete for the same throttle). Burst activity (10 tool calls in 200 ms) coalesces into one rebuild scheduled 100 ms after the burst.

`SnapshotBuilder.BuildAsync` runs in the thread-pool; the main loop subscribes to `SnapshotChanged` and triggers a Spectre re-render on the UI thread.

**Alternatives considered:**
- Pure 1-second poll. Misses sub-second activity; recent tool calls take up to 1 s to surface.
- Pure FileSystemWatcher (no poll). Misses scope status changes that don't write to the two JSONL files (e.g. an indexer crash that leaves a scope `degraded` without a heal event).
- Push from `serve` over an internal channel. Couples lifetimes; the dashboard works whether `serve` is running or not.

### Decision 6 — Resize handling and minimum dimensions

The dashboard uses Spectre's `Layout` which adapts to terminal size automatically. We declare a minimum of `80 × 24` (the historical VT100 default). On smaller terminals, the dashboard prints `terminal too small (need ≥80×24)` and exits with code `2` rather than rendering a broken layout.

`Console.WindowWidth` / `WindowHeight` are queried at startup; SIGWINCH-style resize events (via Spectre's `Live` API) automatically trigger a re-render.

### Decision 7 — Guided actions: suspend / spawn / resume

For `[i]`, `[d]`, `[l]`, `[e]`:

1. `AnsiConsole.Clear()` plus restore the cursor to row 0, col 0.
2. Spawn the subcommand as a subprocess with stdout/stderr/stdin inherited.
3. `WaitForExitAsync`.
4. Print a single line `\n[press Enter to return]` and read a key.
5. Re-enter the Spectre `Live` block; redraw the dashboard from the latest snapshot.

The user sees the subcommand's full interactive flow (e.g., `init` picker, `demo` per-step output) and returns to the dashboard cleanly. The 100-ms freshness watcher captures any JSONL writes the subcommand emitted, so the refreshed dashboard reflects the subprocess's effects.

## Risks / Trade-offs

- **Risk: Spectre.Console version drift breaks the layout.** → Mitigation: pin to a specific 0.49.x patch; subscribe to `Spectre.Console` changelog; the renderer goes through one `DashboardLayout` class so any API change has a single fix site.
- **Risk: FileSystemWatcher misses events under macOS APFS file-event coalescing.** → Mitigation: the 1-second poll guarantees a worst-case 1-second freshness floor regardless of watcher reliability.
- **Risk: in-place actions deadlock if they fail to release SQLite handles.** → Mitigation: every action uses `using` scopes for connections; a watchdog cancels any action that exceeds 30 seconds and surfaces the failure in the dashboard's status bar with the option to retry or quit.
- **Risk: guided actions leave the terminal in a weird state (cursor mode, raw mode).** → Mitigation: wrap each guided invocation in a `try`/`finally` that restores `Console.CursorVisible = true` and clears any leftover ANSI mode. Test by running each guided action in a CI smoke test that captures the terminal state.
- **Risk: confirm modal flow is annoying for power users.** → Mitigation: scoped tightly to destructive actions; reindex/wire/pull/verify don't gate. Power users who want bulk action stay on the CLI subcommands.
- **Risk: published-tool size grows by ~1 MB.** → Acceptable; mitigated by Spectre's small per-feature footprint and lazy assembly loading.
- **Trade-off: Dashboard's exit code is `0` regardless of snapshot health.** Operators expect `q` to return cleanly. CI shouldn't use `dashboard`; that's `status`'s job.
- **Trade-off: No vim keys at v1.** Configurable via a future change if user feedback warrants. The `DashboardKeyMap` central table makes the addition mechanical.

## Migration Plan

This change ships as a single PR (`add-operator-dashboard`) after the prerequisite PRs (`polish-init-onboarding` and `add-operator-status`) land. There's no on-disk state to migrate. Rollback removes the `dashboard` subcommand and reverts the bare-command dispatch in `Program.cs`; `status` continues to work as the headless half.

The Spectre.Console dependency lands with the dashboard PR; reverting that PR also reverts the NuGet package addition cleanly.

## Open Questions

1. **`Ctrl+L` for force-redraw.** Conventional in TUIs (matches readline). Currently `s` is bound to "force snapshot refresh". Could add `Ctrl+L` as an alias for the redraw-only path (re-render the current snapshot without rebuilding). Decision: defer to v1.1; one binding can grow later.
2. **`?` help overlay style.** Modal full-screen vs. inline footer expansion. Modal is more discoverable; inline footer is less disruptive. Decision: inline footer at v1, expand to modal if footer is too cramped under all five sections' bindings.
3. **`scopes add` and `scopes remove` keys.** Currently absent from the v1 key map (operators run them via `[e]` editing `.sourcegraph.json`). Worth `[N]` for new scope and `[D]` for delete scope? Decision: defer to follow-up; v1 ships without to constrain confirm-modal scope.
4. **Should `dashboard` support `--root`?** Currently yes (defaults to CWD same as every other subcommand). Worth surfacing as a section header so multi-repo operators know which repo they're looking at. Decision: yes — render `~/work/MyApp` (relative-path-rendered) in the dashboard header bar.
