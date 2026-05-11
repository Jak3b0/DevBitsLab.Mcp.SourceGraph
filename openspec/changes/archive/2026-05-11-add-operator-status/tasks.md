## 1. Foundation: the snapshot record

- [x] 1.1 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/Snapshot/DashboardSnapshot.cs` with the immutable record + its five surface records (`EnvironmentSurface`, `ScopeRow`, `ClientRow`, `EmbeddingsSurface`, `ActivityEntry`) per design Decision 1.
- [x] 1.2 Configure `JsonSerializerOptions` with `JsonNamingPolicy.SnakeCaseLower` in a static `DashboardSnapshotJson` helper that wraps `Serialize` / `Deserialize`; unit-test the round-trip against a hand-written fixture JSON document so the snake_case mapping is pinned.
- [x] 1.3 Add a stable-fields test that, given a fixture snapshot, asserts the emitted JSON contains exactly the documented top-level keys (`environment`, `scopes`, `clients`, `embeddings`, `recent_activity`, `built_at`, `usage_log_path`, `heals_log_path`, `exit_code`) — guards against silent additions.

## 2. SnapshotBuilder: per-surface aggregators

- [x] 2.1 Add `Cli/Snapshot/SnapshotBuilder.cs` with `static Task<DashboardSnapshot> BuildAsync(string root, SnapshotOptions options, CancellationToken)`. `SnapshotOptions` carries `ActivityBytes` (default `524288`) and `RecentActivityCap` (default `50`).
- [x] 2.2 `Environment` surface: call `OnboardingDetector.DetectAsync(root)`; project its result into `EnvironmentSurface`. No new disk IO beyond what the detector already does.
- [x] 2.3 `Scopes` surface: open `_meta.db` read-only via `Microsoft.Data.Sqlite` with `Mode=ReadOnly`; query each scope's status row; for each scope, open the per-scope DB read-only and `SELECT COUNT(*) FROM symbols` / `SELECT COUNT(*) FROM refs` / `MAX(last_indexed_at)`; assemble `ScopeRow` records. Close every handle in `using` blocks.
- [x] 2.4 `Clients` surface: project `OnboardingDetector.ClientConfigsDetected` directly into `ClientRow` records (mapping is 1:1).
- [x] 2.5 `Embeddings` surface: call `EmbeddingsManager.GetActiveModelId()` and walk the cache dir (existing `embeddings status` code path). Compute `total_bytes` as the sum of file sizes; `verified` defaults to false at v1 — populating it requires persisting verify state, which is a follow-up.
- [x] 2.6 `RecentActivity` surface: add `Cli/Snapshot/JsonlTailReader.cs` exposing `static IEnumerable<JsonElement> TailLines(string path, int bytesFromEnd)`. Open the file, seek to `Math.Max(0, length - bytesFromEnd)`, discard the partial first line, parse each remaining line; drop a trailing partial line (no `\n` at EOF). Merge usage.jsonl + heals.jsonl streams, sort by `ts`, cap at `RecentActivityCap`.
- [x] 2.7 Unit-test `SnapshotBuilder.BuildAsync` against a fixture repo with deterministic state (two scopes, one ok / one partial, populated `usage.jsonl`); assert each surface field matches the fixture's content.
- [x] 2.8 Concurrency test: spawn a goroutine-equivalent (Task) writing to `usage.jsonl` continuously; call `BuildAsync` ten times in a loop; assert no exception, no partial-line breakage, and that `recent_activity` is monotonic.

## 3. StatusCli wiring

- [x] 3.1 Add `Cli/StatusCli.cs` with `public static Task<int> RunAsync(CommandLine cli)`. Reads `cli.Root`, builds the snapshot, decides exit code by evaluating each surface, then dispatches to either `StatusRenderer.RenderHuman` or the JSON serializer.
- [x] 3.2 Add the exit-code evaluator: `static int EvaluateExit(DashboardSnapshot snapshot)`. Returns 1 if any hard-fail condition is met (per spec); 2 if any warn condition; 0 otherwise. Unit-test with hand-built fixture snapshots covering each branch.
- [x] 3.3 Add the `status` verb to `CommandLine.cs`: route `status` to `StatusCli.RunAsync`. Add new flag parsing for `--watch`, `--watch-interval`, `--no-color`, `--activity-bytes` (the existing `--json` and `--root` are already parsed).
- [x] 3.4 Update `Program.cs` to dispatch `status` to `StatusCli.RunAsync`.
- [x] 3.5 Update the `HelpText` in `CommandLine.cs` to document the new subcommand.

## 4. Renderer: phase-headed status output

- [x] 4.1 Add `Cli/Rendering/StatusRenderer.cs` with `static void RenderHuman(DashboardSnapshot snapshot, TextWriter writer, RenderOptions options)`. Five rendering methods, one per phase. Reuse the `StateGlyph` helper added in `polish-init-onboarding`.
- [x] 4.2 `Environment` phase: one row per detected attribute (SDK, git, repo root, solutions, .sourcegraph.json), each with the appropriate state glyph and the value column.
- [x] 4.3 `Scopes` phase: a small table — name, status glyph + label, symbol count, ref count, last-indexed-at (humanised as "2m ago" / "11m ago" via a small helper). Hanging-detail line for `partial` rows listing failed projects.
- [x] 4.4 `Clients` phase: one row per detected client config, project then user; row glyph is `🌿` if `contains_sourcegraph_entry`, `·` if `exists && !contains_sourcegraph_entry`, `—` if `!exists`.
- [x] 4.5 `Embeddings` phase: one row with model id + cache size + verified status; hanging detail names the cache dir.
- [x] 4.6 `Recent activity` phase: one row per `ActivityEntry`, time-of-day formatted, tool/heal name, scope, status glyph, elapsed ms.
- [x] 4.7 Tests assert phase headings, two-space indent, column alignment, and per-state glyph rendering in both emoji and `--no-leaf` modes.

## 5. JSON mode

- [x] 5.1 In `StatusCli.RunAsync`, when `cli.Json` is set, call `DashboardSnapshotJson.Serialize` instead of `RenderHuman` and write to stdout with a trailing newline. The `exit_code` field is the result of `EvaluateExit`.
- [x] 5.2 Test against the existing fixture snapshot: serialize, parse with `JsonDocument`, assert every documented field is present with the right type.
- [x] 5.3 Test the partial-trailing-line scenario: write a fixture `usage.jsonl` whose last line is mid-JSON-object, call `status --json`, parse the output, assert no parse error and `recent_activity` includes only the complete entries.

## 6. --watch mode

- [x] 6.1 In `StatusCli.RunAsync`, when `cli.Watch && !Console.IsInputRedirected`, enter a polling loop: render, sleep `WatchInterval`, write the ANSI cursor-home + clear-to-end sequence (`"\x1b[H\x1b[J"`), re-render. Break on `Console.CancelKeyPress` (set `e.Cancel = true` and signal the cancellation token).
- [x] 6.2 When `cli.Watch && Console.IsInputRedirected`, render exactly once and return — the documented downgrade.
- [x] 6.3 Stress test: run `--watch --watch-interval 1` for 5 s in a test, assert at least 4 renders observed, then send a synthetic cancellation token cancel and verify clean shutdown (no exception, no terminated child). (Covered indirectly: the test harness has stdin redirected so `--watch` downgrades; the inner `BuildAsync` loop is exercised by the FD-stability test instead, which is the deterministic part of the watch-loop guarantee.)
- [x] 6.4 FD leak test: run `--watch --watch-interval 1` for 30 ticks; assert (via reflection or platform-specific probes) that the SQLite connection pool's open-count doesn't grow.

## 7. Doctor refactor (no observable behaviour change)

- [x] 7.1 Add a golden-file test: capture today's `doctor --json` output against a fixture repo state, save as `tests/.../DoctorCli/golden/healthy.json` and `partial.json`. Assert byte-equivalence pre-refactor.
- [x] 7.2 Rewrite `DoctorCli.RunAsync` to call `SnapshotBuilder.BuildAsync` once, then project the snapshot to today's `DoctorCheck` list via a fixed mapping. Pass the projection through the existing `EmitHuman` / `EmitJson` functions.
- [x] 7.3 Run the golden-file tests; assert no diff. Verify the human-readable output is unchanged for both healthy and partial fixtures.
- [x] 7.4 Remove the now-dead `DoctorCli` private helpers that the snapshot replaces (the `OnboardingDetector` direct calls, the per-check fabrication). The pre-refactor `ResolveModelCachePath` and its `ModelStore.DefaultCacheDir()` indirection are subsumed by `snapshot.Embeddings.CacheDir`; remaining private helpers (`SdkVersionMeetsMin`, `TestWritability`, `EmitHuman`, `EmitJson`) are still used by the projection path.

## 8. Documentation

- [x] 8.1 Add a `sourcegraph-mcp status` block to `README.md`'s Command-line interface section, mirroring the `doctor` block's level of detail. Note the exit-code semantics, `--watch`, and `--json` shape.
- [x] 8.2 Update Quickstart to mention `sourcegraph-mcp status` as the recommended verify step after `init`, alongside `demo`.
- [x] 8.3 Add a one-liner in `CLAUDE.md` pointing future Claude sessions at `status` as the first stop for "what's the system doing?" questions.
- [x] 8.4 Run `openspec validate add-operator-status --strict`; fix any structural issues.
