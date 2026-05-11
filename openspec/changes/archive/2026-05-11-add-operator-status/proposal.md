## Why

Operators today have to stitch together a picture of "what state is this system in" from six disjoint CLI subcommands: `doctor` for SDK/git/wiring, `scopes list` and `scopes info <name>` for per-scope health, `embeddings status` for the model cache, plus `usage_stats` for recent activity and an eyeball of `.sourcegraph/usage.jsonl` / `heals.jsonl` for incidents. Each emits in its own format. There's no single "what's going on?" surface.

This change introduces a `DashboardSnapshot` record — a single aggregator that pulls today's state into one structure — and ships `sourcegraph-mcp status` as the polished static rendering of that snapshot. `status` is the operator console's headless half: scripts grep it, CI consumes its `--json` mode, and humans read it for a one-screen environment + scopes + clients + embeddings + recent-activity overview. It's also the data-shape contract the upcoming live dashboard (`add-operator-dashboard`) will consume.

`doctor` keeps working unchanged so existing CI and scripts don't break; the refactor pulls its internals onto the same snapshot so the two views stay in lockstep going forward.

## What Changes

- **`DashboardSnapshot` record** — a new internal data type aggregating five state surfaces: `Environment` (SDK, git, repo root, `.sourcegraph.json` state — what `OnboardingDetector` produces today), `Scopes` (list of scope rows with name, status, symbol/ref counts, last-indexed time, failed-projects/files arrays — from `_meta.db` + per-scope DB read-only queries), `Clients` (per-client config presence and `sourcegraph` entry status — from `OnboardingDetector.ClientConfigsDetected`), `Embeddings` (model id, cache directory, cached file rows, total size — from `EmbeddingsManager` / today's `embeddings status` data), `RecentActivity` (last N entries from `.sourcegraph/usage.jsonl` and `.sourcegraph/heals.jsonl`, merged and sorted by timestamp).
- **`sourcegraph-mcp status` subcommand** — new top-level subcommand. Renders the snapshot to stdout as a phase-headed report using the state-glyph language defined by `polish-init-onboarding`. Reads SQLite scope/_meta DBs in read-only mode; works whether `serve` is running concurrently or not. Exit codes: `0` if every snapshot dimension is healthy, `2` if any dimension shows a warning (drift detected, embedding cache absent, scope partial), `1` if any dimension is hard-fail (DB unwritable, malformed `.sourcegraph.json`).
- **`--json` mode on `status`** — emits the `DashboardSnapshot` as a stable JSON document for scripting. Same shape and field names will be consumed by the live dashboard's renderers and (later) by any operator-facing tooling that wants the data without parsing prose. snake_case fields throughout.
- **`--watch` mode on `status` (interactive only)** — when stdin is a tty and `--watch` is passed, the subcommand re-renders the snapshot in place every N seconds (default `2`, configurable via `--watch-interval <seconds>`). On non-tty contexts, `--watch` is silently downgraded to a single snapshot. Useful for "I'm waiting for the index to finish."
- **`doctor` internals refactor (no spec change)** — `DoctorCli.RunAsync` is rewritten to consume `DashboardSnapshot` and project the same pass/warn/fail check list it emits today. Output format, exit codes, and `--json` shape are byte-identical to today — `doctor` callers see no change. This keeps the two surfaces in lockstep so the snapshot is the single source of truth.
- **`--no-color` on `status`** — disables ANSI colour codes (independent of `--no-leaf`, which controls the glyph language). Mirrors `demo --no-color`.

## Capabilities

### New Capabilities
<!-- None — `status` is a new subcommand under the existing `cli` capability. -->

### Modified Capabilities

- `cli`: One new requirement (`status subcommand`) defining the new top-level verb, its output format (phase-headed, leaf-state glyph language), its `--json` shape, its exit-code semantics, and the `--watch` mode. Two ancillary requirements add `status output state-glyph language` (a reference back to `polish-init-onboarding`'s glyph contract, extended to status) and `status snapshot data sources` (lists the five aggregated surfaces and their data shape, so the JSON contract is testable). `doctor subcommand` requirement is left unchanged.

## Impact

- **Code**: New file `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/StatusCli.cs` (the new subcommand wiring). New folder `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/Snapshot/` with `DashboardSnapshot.cs` (the record), `SnapshotBuilder.cs` (aggregator), and small per-surface helper classes (`EnvironmentSurface.cs`, `ScopesSurface.cs`, `ClientsSurface.cs`, `EmbeddingsSurface.cs`, `RecentActivitySurface.cs`). New rendering module `Cli/Rendering/StatusRenderer.cs` that takes the snapshot + a `TextWriter` and emits the phase-headed report. `DoctorCli.cs` is rewritten to consume the snapshot instead of running its own `OnboardingDetector` call directly — observable output preserved. `CommandLine.cs` adds the `status` verb plus `--watch`, `--watch-interval`, `--no-color`.
- **Spec**: One delta on `cli` (three ADDED requirements).
- **Tests**: New tests in `tests/Server.Tests/` cover `SnapshotBuilder` (round-trip from a fixture repo state to a populated snapshot), `StatusRenderer` (phase-headed output, glyph language, `--json` shape), `--watch` mode (single-snapshot fallback under non-tty). Existing `DoctorCliTests` get one addition: a "doctor and status agree" cross-test that runs both against a fixture repo and confirms each `doctor` check line corresponds to a snapshot field.
- **Public API / dependencies**: No new NuGet dependencies (rendering stays in the BCL `Console` + `TextWriter` world; the snapshot reads SQLite via the existing `Microsoft.Data.Sqlite` already used by storage). The `--json` shape becomes a stable contract once shipped; later additions are append-only (new fields, not renames).
- **Documentation**: `README.md` adds a `status` subcommand entry in the Command-line interface section. The Quickstart section gains a `sourcegraph-mcp status` line as a recommended verify step alongside `demo`. `CLAUDE.md` adds a one-liner pointing future Claude sessions at `status` as the first stop for "what's the system doing?" questions.
