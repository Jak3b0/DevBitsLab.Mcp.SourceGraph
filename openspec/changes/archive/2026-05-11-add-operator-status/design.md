## Context

The state surfaces that the operator console needs already exist, but each ships through a different subcommand and serialisation:

- **Environment**: `OnboardingDetector.DetectAsync` returns `OnboardingDetectionResult`. Used by `doctor`, `init`. Lives at `Cli/OnboardingDetector.cs`.
- **Scopes**: `_meta.db` (the scope registry) plus per-scope SQLite DBs at `.sourcegraph/scopes/<id>.db`. Read via `IScopeRegistry` today; `scopes list` and `list_scopes` already query this.
- **Clients**: `OnboardingDetector.ClientConfigsDetected` — same call as Environment.
- **Embeddings**: `EmbeddingsManager` exposes the active model and per-file manifest; the `embeddings status` CLI verb already prints this.
- **Recent activity**: `.sourcegraph/usage.jsonl` and `.sourcegraph/heals.jsonl` are appended to by `ToolMetrics` and the heal pipeline respectively. Today they're tailable from a shell; no in-process reader exists.

None of these surfaces interlocks; each subcommand re-invokes its own detector. The data-flow looks like:

```
   doctor         ──► OnboardingDetector ──► console
   scopes list    ──► _meta.db          ──► console
   embeddings     ──► EmbeddingsManager  ──► console
   usage_stats    ──► usage.jsonl tail   ──► MCP tool response (not CLI)
```

This change introduces a single aggregator (`SnapshotBuilder`) that produces one immutable `DashboardSnapshot` record from those five sources, plus two renderers that consume the snapshot: `StatusRenderer` (human-readable, phase-headed) and a JSON serializer that emits the snake_case `--json` shape.

```
   sources                snapshot                renderers
   ─────────────────────  ────────────────────────  ────────────────────
   OnboardingDetector ─┐                          ┌─► StatusRenderer (prose)
   _meta.db + scope DBs─┼─► DashboardSnapshot ────┼─► JSON serializer
   EmbeddingsManager  ─┤                          └─► (future: dashboard
   usage.jsonl tail   ─┤                              renderers in
   heals.jsonl tail   ─┘                              `add-operator-dashboard`)
```

The snapshot is read-only — `SnapshotBuilder.BuildAsync` opens read-only SQLite handles, never holds them past the build, and tolerates a concurrent `serve` process writing. Stale-read tolerance: scope counts are read with `SELECT COUNT(*)` (point-in-time consistent under SQLite's MVCC); the JSONL tails read the last N bytes and parse line-by-line, dropping the last incomplete line.

## Goals / Non-Goals

**Goals:**
- One immutable record carries every piece of state the operator console needs. Adding a new field is appending one record property + one builder query + one renderer line.
- `status` ships as the polished static rendering, replacing what users would otherwise piece together from four subcommands.
- `--json` emits a stable, snake_case, append-only-compatible shape that scripts and the live dashboard both consume.
- `doctor`'s observable behaviour does not change: same checks, same wording, same exit codes, same `--json` shape. Internally it pulls from the snapshot.
- The snapshot reads from disk only; no in-process MCP server contact required. `status` works whether `serve` is running or not.

**Non-Goals:**
- Reactive / push-based updates. The live dashboard (`add-operator-dashboard`) layers a watcher on top; `status --watch` is a poll-based convenience.
- Aggregating across multiple repos. Snapshot is single-`--root`.
- A unified "explain this finding" affordance. Drill-down narrative lives in the existing subcommands (`scopes info <name>`, `embeddings verify`). `status` is breadth, not depth.
- Mutating actions. Even `--watch` is read-only. Actions belong to the dashboard (Change 3).
- Replacing `usage_stats` MCP tool. `status` is CLI-only; `usage_stats` remains the agent-facing surface (and is unchanged).

## Decisions

### Decision 1 — `DashboardSnapshot` as an immutable C# record with snake_case JSON

```csharp
public sealed record DashboardSnapshot(
    EnvironmentSurface Environment,
    IReadOnlyList<ScopeRow> Scopes,
    IReadOnlyList<ClientRow> Clients,
    EmbeddingsSurface Embeddings,
    IReadOnlyList<ActivityEntry> RecentActivity,
    DateTimeOffset BuiltAt);

public sealed record ScopeRow(
    string Name,
    string Status,            // "ok" | "partial" | "degraded" | "indexing"
    long SymbolCount,
    long ReferenceCount,
    DateTimeOffset? LastIndexedAt,
    IReadOnlyList<string> FailedProjects,
    IReadOnlyList<string> FailedFiles,
    bool Isolated);

public sealed record ClientRow(
    string Slug,              // "claude-code" | "copilot" | ...
    string Scope,             // "project" | "user"
    string Path,
    bool Exists,
    bool ContainsSourcegraphEntry);

public sealed record ActivityEntry(
    DateTimeOffset Ts,
    string Kind,              // "tool_call" | "heal" | "boot_reconcile" | ...
    string? Scope,
    bool Ok,
    int Ms,
    string? Detail);
```

The PascalCase record properties serialize to snake_case JSON via `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }`. Built-in System.Text.Json supports this in .NET 8+.

**Alternatives considered:**
- A class with mutable fields. Records are the idiomatic choice in modern C#; immutability simplifies the aggregator and renderer contracts.
- A union/tagged variant per surface. Overkill for five fixed surfaces.
- Inheriting from a base `Surface` interface. No polymorphism is needed; concrete records are simpler.

### Decision 2 — `SnapshotBuilder.BuildAsync` reads from disk, not from a live `serve` process

The snapshot is built by opening read-only SQLite handles on `_meta.db` and each per-scope DB, calling `OnboardingDetector.DetectAsync` (which is already pure-detection, no in-process state), reading the `EmbeddingsManager.GetManifestStatus()` data, and tailing the last ~512 KB of each JSONL log (configurable via `--activity-bytes`, default 524288).

This means `status` works in three modes without code changes:
1. **No `serve` running** — opens its own read-only handles. Standard case.
2. **`serve` running in same repo** — SQLite's WAL mode allows concurrent readers. The snapshot may see writes from the live indexer mid-flight; that's fine (the snapshot is a point-in-time view).
3. **`serve` running on a different repo** — irrelevant; the snapshot is rooted by `--root`.

No daemon, no socket, no shared in-memory state. The cost is "snapshot is at-most-poll-frequency fresh", which matches the operator-console use case.

**Alternatives considered:**
- Querying a running `serve` over an internal HTTP/IPC endpoint. Introduces a coordination problem and a service-lifecycle complication for an unobserved benefit (the snapshot would be sub-second fresher).
- Sharing in-memory state via a named pipe / mutex. Same tradeoff plus platform compatibility cost.

### Decision 3 — `status` exit-code semantics

The output prose is always emitted (stdout). Exit code reflects the aggregate health:

| Condition | Exit |
|---|---|
| Every dimension healthy | `0` |
| Any dimension warns: scope `partial`/`indexing`, drift detected, embedding cache absent, git missing | `2` |
| Any dimension hard-fails: SDK absent, `.sourcegraph.json` malformed, scope `degraded` with corruption, DB dir unwritable | `1` |

This matches `doctor`'s convention exactly. `--json` carries the same code in an `exit_code` top-level field. CI scripts that already `if [ $? -eq 2 ]` after `doctor` work against `status` unchanged.

**Alternatives considered:**
- `0` always (use `--json` parsing for health). Loses the "fail the CI step" affordance.
- Reverse semantics (0 unhealthy, nonzero healthy). Violates POSIX convention.

### Decision 4 — `--watch` is poll-based with cursor-back-to-top redraw

In tty mode, `--watch` writes the snapshot, then on each tick emits ANSI `\x1b[H` (cursor home) followed by `\x1b[J` (clear to end of screen) and re-renders. The previous frame's content is overwritten in place; no flicker, no scrollback pollution. Default interval `2` seconds, settable via `--watch-interval <seconds>`. `Ctrl+C` exits cleanly via `Console.CancelKeyPress`.

Non-tty contexts (CI, pipe) downgrade silently: `--watch` is honoured as a single snapshot. The reason: redraw in a pipe would either spam frames (no cursor positioning honoured) or eat memory (buffer-then-flush). Single-shot is the safe default.

**Alternatives considered:**
- Spectre.Console's `Live` renderer. Adds a dep that the dashboard (Change 3) will take, but `status --watch` is intentionally light — keeping it dep-free preserves `status` as the headless CLI surface.
- File-system watchers (`FileSystemWatcher` on `usage.jsonl` + `_meta.db`). Reactive but more complex; the 2-second poll matches the operator-eye refresh expectation.

### Decision 5 — `doctor` becomes a `status` projection, observable surface unchanged

`DoctorCli.RunAsync` is rewritten to:

1. Call `SnapshotBuilder.BuildAsync` once.
2. Project the snapshot to today's eight `DoctorCheck` records using a fixed mapping (e.g., `snapshot.Environment.DotnetSdkVersion` → `DoctorCheck("dotnet-sdk", ...)`).
3. Pass the resulting list to today's `EmitHuman` / `EmitJson` functions unchanged.

Tests that currently mock `OnboardingDetector` for `DoctorCli` switch to building a fixture snapshot. The `--json` output bytes are golden-tested for "no change from current shape."

**Alternatives considered:**
- Keep `doctor` independent (no refactor). Risks the two surfaces drifting as new data is added to `status`. The point of this change is single source of truth.
- Rename `doctor` to `status` with back-compat alias. More disruptive for CI scripts; keeping both names lets users migrate at their pace.

## Risks / Trade-offs

- **Risk: `_meta.db` schema changes break `SnapshotBuilder` silently.** → Mitigation: the same migrator logic that drives `IScopeRegistry`'s queries today is reused via a tiny `ScopeRegistryReader` helper that lives next to `IScopeRegistry`; schema-version mismatches surface as a `DashboardSnapshot.Environment.Errors` field with a string detail rather than crashing the build.
- **Risk: SQLite read-only handle contention with a live `serve` indexer.** → Mitigation: open with `SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX`; the WAL mode that `LiveIndexService` already uses means readers never block writers. Tests cover a "serve writing while status reads" race using a synthetic indexer that hammers the per-scope DB.
- **Risk: JSONL tail parsing trips on a partial last line.** → Mitigation: tail reader drops the last line if it doesn't end with `\n` (it's an in-progress write); a unit test pins this. The 512 KB cap is a safety floor — even a year of activity stays well under that.
- **Risk: `--watch` reads SQLite handles every tick and exhausts FDs.** → Mitigation: each `BuildAsync` opens its handles in a `using` scope and closes them before returning. No FDs leak. Verified with a stress test that runs `--watch-interval 0.1` for 60 s and checks `lsof` doesn't grow.
- **Trade-off: `--json` shape becomes a stable contract.** Acceptable cost; the structure is small (six top-level fields) and the data shapes are dictated by what the underlying state surfaces produce. Future additions append only — never rename or remove a field without a major-version bump.
- **Trade-off: `doctor` refactor risks a subtle behaviour shift.** Mitigation: a golden-file test pins `doctor --json` output against a fixture repo before the refactor; the test stays green through and after the refactor. Any wording change in the human-readable output is captured by the existing `DoctorCliTests`.

## Migration Plan

This change ships in two PRs, in order:

1. **PR A — Snapshot + status (no doctor refactor).** Lands `DashboardSnapshot`, `SnapshotBuilder`, `StatusCli`, `StatusRenderer`, JSON serializer, tests. `doctor` continues to run its own detection. Status is usable end-to-end.
2. **PR B — Doctor as a status projection.** Rewrites `DoctorCli.RunAsync` to consume the snapshot. Golden-file test confirms `doctor --json` byte-equivalence. Internal cleanup only.

Rollback: PR B reverts cleanly (revert the `DoctorCli` rewrite). PR A's surface is new (`status` subcommand); reverting it removes the verb and the new helper files.

## Open Questions

1. **JSONL log path discoverability.** Today `.sourcegraph/usage.jsonl` and `.sourcegraph/heals.jsonl` live in the per-`--root` `.sourcegraph/` directory. Should `--json` include the absolute path of each log so a scripted consumer can tail them itself? Decision: yes — adds two top-level string fields, `usage_log_path` and `heals_log_path`. Trivial cost, immediate utility.
2. **`--watch` and the leaf state-glyph language.** When the snapshot updates and a previously `🌿 ok` scope flips to `⚠ partial`, does the glyph itself flash or just swap? Default: swap (no flash). A `--watch-flash` toggle is out of scope; revisit if user feedback wants it.
3. **`status` against an uninitialised repo (no `.sourcegraph/` directory).** Today `doctor` reports the missing scope dir as a fail. `status` should match: same warn/fail policy, no special "uninitialised" code path.
4. **Backwards-compat: does any tooling already use the name `status` for something else?** Quick grep of the codebase: no — `status` is not currently a subcommand. The MCP `usage_stats` tool is named differently. Free.
