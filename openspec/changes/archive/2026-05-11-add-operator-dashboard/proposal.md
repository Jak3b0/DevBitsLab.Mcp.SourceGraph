## Why

`sourcegraph-mcp status` gives operators a polished one-shot view; what's still missing is the live, interactive surface — the "operator console" where a maintainer can sit, watch the system, and act on what they see without thumb-typing a CLI verb per action. Today actions are scattered across `init --client …`, `reconcile_drift`, `repair_scope`, `embeddings pull`, `embeddings verify`, `scopes add / remove`, and direct `.sourcegraph.json` edits. Operators run them in sequence, each as a separate subcommand, with no shared context.

This change ships `sourcegraph-mcp dashboard` — a Spectre.Console-backed TUI that renders the `DashboardSnapshot` live, refreshes on a 1-second tick plus filesystem-watcher events on `usage.jsonl` / `heals.jsonl`, and exposes the existing actions as key-accelerated commands. Three action depths ship in one cut: **read-only** navigation through the snapshot, **guided** invocations that shell out to existing subcommands while the dashboard pauses, and **in-place** safe mutations (re-wire a client, pull a model, reindex a scope) handled inside the dashboard process.

Bare `sourcegraph-mcp` (no subcommand) drops into the dashboard when stdin is a tty — the most discoverable possible default for the human-facing surface — and falls back to `status` when redirected, so CI scripts that ran the command bare keep getting a useful snapshot.

## What Changes

- **`sourcegraph-mcp dashboard` subcommand** — full-screen Spectre.Console TUI rendering the `DashboardSnapshot` produced by `add-operator-status`. Five sections (Environment, Scopes, Clients, Embeddings, Recent activity), navigable with arrow keys; selection state lives in the dashboard, not in the underlying snapshot. Quits with `q` or `Ctrl+C` returning exit `0`.
- **Bare-command dispatch** — `sourcegraph-mcp` with no positional args dispatches to `dashboard` when `stdin` is a tty, to `status` (single snapshot to stdout) when `stdin` is redirected. The existing help text is shown only when `--help` / `-h` is passed explicitly.
- **Three action depths**:
  - **Read-only**: navigation between sections (`↑↓` / `j k`), drilling into a row (`Enter`) opens a detail pane, returning to the section with `Esc`.
  - **Guided**: keys that delegate to an existing CLI subcommand by suspending the TUI, invoking the subcommand visibly, then returning to the dashboard. Used for actions that are slow, verbose, or already well-served by their CLI verb (e.g. `[i]` runs `init` interactively).
  - **In-place**: safe mutations executed directly inside the dashboard process via the same code paths the CLI subcommands use. Used for fast, deterministic actions: `[w]` (wire a missing client), `[u]` (unwire — with confirmation), `[p]` (embeddings pull), `[v]` (embeddings verify), `[r]` (reindex selected scope), `[R]` (rebuild selected scope — with confirmation).
- **Destructive-action confirm modal** — every in-place action that mutates state requires explicit confirmation: a Spectre modal prompt of the form `<verb> <target>? [y/N]` with `Esc` defaulting to no. Applied to: `[u]` unwire, `[R]` rebuild, `embeddings remove`, `scope remove`. Read-only and additive in-place actions (wire, pull, verify, reindex) do not gate.
- **Refresh model** — the snapshot is rebuilt on a 1-second poll plus `FileSystemWatcher` events on `usage.jsonl` and `heals.jsonl` (so tool-call activity surfaces within ~100 ms instead of waiting for the next tick). The full SQLite re-read happens at most once per second to bound CPU under burst activity.
- **`--no-color` / `--no-leaf` honoured** — both knobs continue to apply; the dashboard's Spectre layout uses bracketed-ASCII fallbacks when emoji is disabled. Spectre's own colour output respects `NO_COLOR` env var per its own convention; `--no-color` augments that.
- **`Spectre.Console` dependency** — pinned to the current stable 0.49.x line; AOT-trim-friendly per its own metadata. ~1 MB extra in published-tool size.

## Capabilities

### New Capabilities
<!-- None — the dashboard is a new subcommand under the existing `cli` capability. -->

### Modified Capabilities

- `cli`: One new requirement (`dashboard subcommand`) defining the new top-level verb, the Spectre dependency, the five sections, the three action depths and their key bindings, and the refresh model. One new requirement (`Bare command dispatch`) defining the tty-vs-redirected behaviour of `sourcegraph-mcp` invoked with no positional args. One new requirement (`Dashboard action confirmation`) defining which actions require modal confirmation. The existing `Subcommand routing` requirement is left unchanged in this delta; the implementation's argument parser already tolerates bare invocation (today it falls through to help), and the new `Bare command dispatch` requirement layers the tty-aware behaviour on top without conflicting.

## Impact

- **Code**: New project-internal folder `src/DevBitsLab.Mcp.SourceGraph.Server/Dashboard/` with:
  - `DashboardCli.cs` — subcommand entry point and the main loop.
  - `DashboardLayout.cs` — Spectre.Console `Layout` composition for the five sections plus the status bar.
  - `DashboardRenderer.cs` — per-section `IRenderable` builders consuming `DashboardSnapshot`.
  - `DashboardActions.cs` — the action dispatcher; methods for each `[w] [u] [p] [v] [r] [R] [i] [d] [e]` key.
  - `DashboardKeyMap.cs` — central key-binding table (arrow + letter accelerators today; vim aliases later if requested).
  - `ConfirmModal.cs` — modal prompt helper.
  - `FreshnessSource.cs` — wraps `SnapshotBuilder.BuildAsync` plus the FileSystemWatcher; exposes an `IObservable<DashboardSnapshot>` (or a callback subscription) that the main loop subscribes to.
- `Program.cs` — bare-command dispatch: detect `Console.IsInputRedirected` and route accordingly. No new flag added; the existing argument-parser fall-through is augmented.
- `CommandLine.cs` — register the `dashboard` subcommand verb; help text addition.
- `DevBitsLab.Mcp.SourceGraph.Server.csproj` — add the `Spectre.Console` NuGet reference (pinned).
- **Spec**: One delta on `cli` (three ADDED requirements).
- **Tests**: A new test project / suite `tests/Server.Tests/Dashboard/` covers (a) `DashboardKeyMap` invariants (every documented key resolves to exactly one action), (b) `DashboardRenderer` snapshot rendering (each section's `IRenderable` matches a Spectre golden file under emoji and no-leaf modes), (c) `DashboardActions` happy paths via mocked subcommand callbacks, (d) `FreshnessSource` event coalescing under burst JSONL writes. Live TUI behavior is hard to test end-to-end; we test the components, not the keypress-to-pixel pipeline. Manual smoke-test plan added under `notes/manual-smoke-test.md` in the change directory.
- **Public API / dependencies**: New NuGet dependency `Spectre.Console` (MIT, ~1 MB published size, AOT-friendly). No MCP wire format changes. No schema migrations. The existing CLI subcommands continue to work unchanged — the dashboard is purely additive.
- **Documentation**: `README.md` gains a Dashboard section under "Command-line interface" describing the key bindings and confirming actions. The Quickstart line `sourcegraph-mcp` (bare) is added as an explicit recommended verb. `CLAUDE.md` adds a one-liner explaining what bare `sourcegraph-mcp` does and that the dashboard is the human-facing console (distinct from the agent-facing MCP server).
