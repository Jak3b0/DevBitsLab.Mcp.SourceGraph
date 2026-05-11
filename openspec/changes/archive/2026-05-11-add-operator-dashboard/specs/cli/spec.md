## ADDED Requirements

### Requirement: dashboard subcommand
The CLI SHALL accept `sourcegraph-mcp dashboard` that renders a full-screen Spectre.Console-backed live operator console consuming the `DashboardSnapshot` defined in the `status snapshot data sources` requirement. The dashboard SHALL display five sections — `Environment`, `Scopes`, `Clients`, `Embeddings`, `Recent activity` — backed by the same snapshot the `status` subcommand renders, and SHALL refresh that snapshot on a polling + watcher hybrid (see below).

The subcommand SHALL accept the following flags:

- `--root <path>` — repository root (default CWD); rendered in the dashboard header bar using the relative / `~/`-substituted form (matching `polish-init-onboarding`'s path-rendering rule).
- `--no-color` — disable ANSI colour codes; composes with the `NO_COLOR` env var Spectre honours natively.
- `--no-leaf` / `SOURCEGRAPH_NO_LEAF=1` — substitute the ASCII state-glyph fallback (matches `polish-init-onboarding` and `add-operator-status` conventions).

The dashboard SHALL refresh its snapshot using a hybrid model:

1. **Polling**: a timer rebuilds the snapshot at most once per `1000 ms`.
2. **Watcher**: `FileSystemWatcher` instances on `<root>/.sourcegraph/usage.jsonl` and `<root>/.sourcegraph/heals.jsonl` trigger a debounced rebuild `100 ms` after the most recent write event.

The two triggers SHALL coalesce: any rebuild request that fires within `1000 ms` of the previous rebuild SHALL be dropped (the most recent snapshot satisfies it). Snapshot rebuilds SHALL run on the thread-pool; rendering SHALL be confined to the UI thread.

The dashboard SHALL declare minimum terminal dimensions of `80 columns × 24 rows`. When the current terminal is smaller at startup, the dashboard SHALL print `terminal too small (need ≥80×24)` to stderr and exit with code `2`. The dashboard SHALL respond to terminal resize events by re-rendering the layout against the new dimensions.

The dashboard SHALL exit cleanly with code `0` on `q`, `Q`, or `Ctrl+C`; on uncaught exception, exit `1` after restoring the terminal cursor.

#### Scenario: Successful launch renders the five sections
- **WHEN** `sourcegraph-mcp dashboard` is invoked under a `100 × 40` terminal in a healthy repo
- **THEN** the first frame contains a Spectre layout with five labelled sections — `Environment`, `Scopes`, `Clients`, `Embeddings`, `Recent activity` — each rendered with phase-internal rows using the state-glyph language; the header bar contains the dashboard banner with `🌿 SourceGraph` (or `[x] SourceGraph` under `--no-leaf`), the version, and the relative-path-rendered `--root`; the footer bar contains the key-binding hints `[q] quit  [?] help  [↑↓] nav  [Enter] details`

#### Scenario: Recent activity surfaces within 200 ms of a JSONL write
- **WHEN** the dashboard is running and a concurrent `serve` process appends one line to `usage.jsonl`
- **THEN** the `Recent activity` section re-renders to include the new entry within 200 ms of the write (100 ms watcher debounce + render latency); the snapshot's poll-tick rebuild SHALL NOT also fire for that event (the coalescing rule prevents the duplicate rebuild)

#### Scenario: Polling rebuild fires when no JSONL activity
- **WHEN** the dashboard runs for 5 seconds with no JSONL writes
- **THEN** the snapshot rebuilds at most 5 times (once per second); each rebuild's wall-clock time is recorded; no rebuild's duration exceeds 100 ms under the healthy-repo fixture

#### Scenario: Tiny terminal refuses to render
- **WHEN** `sourcegraph-mcp dashboard` is invoked under a `60 × 20` terminal
- **THEN** stderr contains the line `terminal too small (need ≥80×24)`; the process exits with code `2` without entering Spectre's `Live` mode

#### Scenario: q exits cleanly
- **WHEN** the dashboard is running and the user presses `q`
- **THEN** the Spectre `Live` block exits; the cursor is restored to visible state; no ANSI escape sequence remains in the terminal's output buffer; the process exits with code `0`

### Requirement: Bare command dispatch
The CLI SHALL accept `sourcegraph-mcp` invoked with no positional arguments and no `--help` / `-h` flag, and SHALL dispatch to either the `dashboard` subcommand or the `status` subcommand based on the stdin disposition: when stdin is a tty (`Console.IsInputRedirected == false` AND `Environment.UserInteractive == true`), the dispatch target SHALL be `dashboard`; otherwise the dispatch target SHALL be `status`.

Flags passed alongside the bare invocation (e.g. `sourcegraph-mcp --root /work/Repo`) SHALL be propagated to the dispatched subcommand.

The behaviour of `sourcegraph-mcp --help` / `sourcegraph-mcp -h` SHALL be unchanged from today: print the help text and exit `0`.

#### Scenario: Bare invocation under a tty enters the dashboard
- **WHEN** a user runs `sourcegraph-mcp` (no positional args, no `--help`) in an interactive terminal
- **THEN** the dashboard launches as if `sourcegraph-mcp dashboard` had been invoked; on `q` the process exits `0`

#### Scenario: Bare invocation under a pipe runs status
- **WHEN** `sourcegraph-mcp | cat` is invoked
- **THEN** the static `status` snapshot is printed to stdout (no ANSI control codes for live rendering); the process exits with the snapshot's exit code (0, 1, or 2); the dashboard is NOT entered

#### Scenario: --help still prints help
- **WHEN** `sourcegraph-mcp --help` is invoked (in either tty or redirected stdin)
- **THEN** stdout contains the help text starting with `sourcegraph-mcp — live code source graph MCP server for .NET`; the dashboard is NOT entered; the process exits `0`

#### Scenario: Bare invocation with --root propagates the flag
- **WHEN** `sourcegraph-mcp --root /some/other/repo` is invoked under a tty
- **THEN** the dashboard launches against `/some/other/repo` (the header bar names that root); the `--root` flag is passed through to the snapshot builder

### Requirement: Dashboard action confirmation
The `dashboard` subcommand SHALL gate every state-mutating action that is irreversible or that affects user-visible state outside `<root>/.sourcegraph/` behind a Spectre confirmation prompt. The prompt SHALL render as a modal overlay with the text `<verb> <target>? [y/N]`, default `No`, dismissable with `Esc` or `n` (both treated as `No`).

Specifically, the following in-place actions SHALL gate behind the confirm modal:

- `[R]` rebuild selected scope (archives the current scope DB before re-indexing).
- `[u]` unwire selected client (removes the `mcpServers.sourcegraph` entry from the target config file).

The following in-place actions SHALL NOT gate (they are idempotent / additive):

- `[r]` reindex selected scope (`reconcile_drift`).
- `[w]` wire missing client (calls the InitCli writer; results in `Insert` or `NoOpAlreadyMatches`).
- `[p]` embeddings pull (idempotent against a populated cache).
- `[v]` embeddings verify (read-only).

Read-only actions (`↑↓`, `Tab`, `Enter`, `Esc`, `q`, `?`, `s`) SHALL NOT gate.

Guided actions (`[i]`, `[d]`, `[l]`, `[e]`) SHALL NOT gate at the dashboard layer — the guided subcommand carries its own interaction model.

#### Scenario: `[u]` unwire prompts for confirmation
- **WHEN** the user navigates to a wired client row, presses `u`, and the confirm prompt appears
- **THEN** the prompt renders with the text `unwire claude-code? [y/N]` (or equivalent for the selected client); pressing `Esc` dismisses the prompt with no file modification; the `.mcp.json` file's mtime is unchanged; the dashboard returns to the Clients section with no error

#### Scenario: `[u]` unwire proceeds on explicit yes
- **WHEN** the user presses `u` on a wired `claude-code` row and answers `y` to the confirm prompt
- **THEN** the `mcpServers.sourcegraph` entry is removed from `<root>/.mcp.json` (other entries preserved); the snapshot rebuilds; the Clients section's `claude-code` row state-glyph flips from `🌿` to `·` (or `[x]` to `[ ]` under `--no-leaf`)

#### Scenario: `[r]` reindex does NOT prompt
- **WHEN** the user navigates to a scope row and presses `r`
- **THEN** `reconcile_drift` runs immediately for the selected scope; no confirm prompt appears; the scope's state glyph optionally flips to a transient `indexing` indicator while the operation runs; on completion the row re-renders with the updated state

#### Scenario: `[R]` rebuild prompts for confirmation
- **WHEN** the user presses `R` on a scope row
- **THEN** the confirm prompt renders with the text `rebuild backend? [y/N]` (or the slug of the selected scope); `n` or `Esc` dismisses with no action; `y` triggers `repair_scope mode=rebuild` for the selected scope

#### Scenario: `[v]` embeddings verify does NOT prompt
- **WHEN** the user presses `v`
- **THEN** `embeddings verify` runs against the active model immediately; no confirm prompt appears; the Embeddings section's `verified` indicator updates on completion (or surfaces a `⚠`/`✗` state glyph if a SHA mismatch is detected)
