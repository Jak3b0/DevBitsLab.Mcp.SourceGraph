## ADDED Requirements

### Requirement: status subcommand
The CLI SHALL accept `sourcegraph-mcp status` that renders a single point-in-time snapshot of the operator console's five state surfaces — Environment, Scopes, Clients, Embeddings, Recent activity — to stdout, using the phase-headed layout and state-glyph language defined elsewhere in this spec.

The subcommand SHALL accept the following flags:

- `--root <path>` — repository root (default CWD).
- `--json` — emit a stable JSON document instead of human-readable prose. See the `status snapshot data sources` requirement for the document shape.
- `--watch` — when stdin is a tty, re-render the snapshot in place on a poll interval. When stdin is not a tty, the flag is silently downgraded to a single snapshot.
- `--watch-interval <seconds>` — integer seconds between `--watch` re-renders (default `2`, minimum `1`). Ignored when `--watch` is not set.
- `--no-color` — disable ANSI colour codes in the human-readable output (independent of `--no-leaf`, which controls the glyph language).
- `--activity-bytes <N>` — tail at most N bytes from each JSONL log (default `524288`).

The exit code SHALL follow the convention: `0` if every snapshot dimension is healthy; `2` if any dimension reports a warning (scope status `partial` or `indexing`, drift detected, embedding cache absent, git missing); `1` if any dimension hard-fails (`.NET 10` SDK absent, `.sourcegraph.json` malformed, scope status `degraded` with corruption, scope DB directory unwritable).

`status` SHALL read SQLite databases (`_meta.db` and per-scope DBs) in read-only mode and SHALL NOT require a concurrent `sourcegraph-mcp serve` process. The subcommand SHALL tolerate a concurrently-running `serve` writing to the same DBs (SQLite WAL mode supports concurrent readers).

#### Scenario: Healthy environment renders all five phases
- **WHEN** `sourcegraph-mcp status` is invoked in a repo with a valid `.sourcegraph.json`, .NET 10 + git on PATH, two ok scopes, the default embedding model populated, and a non-empty `usage.jsonl`
- **THEN** stdout contains exactly five phase headings — `Environment`, `Scopes`, `Clients`, `Embeddings`, `Recent activity` — each rendered with phase-internal rows using the state-glyph language (`🌿` for healthy items); the process exits `0`

#### Scenario: A degraded scope drops the exit code to 1
- **WHEN** `sourcegraph-mcp status` is invoked in a repo where one configured scope has `status = "degraded"` in `_meta.db` and an integrity-check failure recorded
- **THEN** the `Scopes` phase renders that scope's row with the `✗` (or `[X]` under `--no-leaf`) glyph and a hanging-detail line naming the recommended action (`repair_scope mode=rebuild`); the process exits `1`

#### Scenario: A partial scope drops the exit code to 2
- **WHEN** `sourcegraph-mcp status` is invoked in a repo where one scope has `status = "partial"` and the `failed_projects` array is non-empty
- **THEN** the `Scopes` phase renders that scope's row with the `⚠` (or `[!]`) glyph; the hanging-detail line lists the failed project names; the process exits `2`

#### Scenario: --json emits the stable contract document
- **WHEN** `sourcegraph-mcp status --json` is invoked
- **THEN** stdout contains a single JSON document parseable as the `DashboardSnapshot` shape documented in `status snapshot data sources`; no human-readable prose precedes or follows the JSON; the document's `exit_code` field matches the process exit code

#### Scenario: --watch refreshes in place under a tty
- **WHEN** `sourcegraph-mcp status --watch --watch-interval 1` is invoked under a tty and runs for 3 seconds before SIGINT
- **THEN** stdout contains the snapshot rendered 3 or 4 times (one initial frame + 2 or 3 refreshes); each refresh emits the ANSI cursor-home + clear-to-end sequence so the previous frame is overwritten in place; on SIGINT the process exits `0` cleanly with no stray "interrupted" message

#### Scenario: --watch downgrades to single snapshot under non-tty
- **WHEN** `sourcegraph-mcp status --watch --watch-interval 1 | cat` is invoked
- **THEN** exactly one snapshot is rendered on stdout (no ANSI cursor codes, no re-renders); the process exits with the snapshot-evaluated exit code; the `--watch-interval` value is ignored

### Requirement: status output state-glyph language
The `status` subcommand SHALL use the same five-state-glyph vocabulary defined in the `init output state-glyph language` requirement: `🌿` for on/passed/healthy (ASCII fallback `[x] ` under `--no-leaf` or `SOURCEGRAPH_NO_LEAF=1`), `·` for off/inactive (ASCII `[ ] `), `⚠` for warning (ASCII `[!] `), `✗` for hard-fail (ASCII `[X] `), `—` for unsupported/N/A (ASCII `[-] `). Column alignment SHALL be preserved across the emoji and ASCII forms at three display cells per token.

The `status` subcommand SHALL organise its human-readable output under named phase headings: `Environment`, `Scopes`, `Clients`, `Embeddings`, `Recent activity`. Phase headings SHALL appear at the left margin without a leading state glyph; rows under a phase heading SHALL be indented exactly two spaces.

The `--no-color` flag SHALL suppress ANSI colour codes but SHALL leave the glyph language intact (emoji rendering doesn't require ANSI colour codes).

#### Scenario: --no-leaf substitutes ASCII fallbacks
- **WHEN** `sourcegraph-mcp status --no-leaf` is invoked in a healthy repo
- **THEN** no `🌿` (U+1F33F) byte sequence appears on stdout; every state-glyph position contains a three-character ASCII token from the set `[x] `, `[ ] `, `[!] `, `[X] `, `[-] `; phase headings render identically to the emoji-enabled path

#### Scenario: SOURCEGRAPH_NO_LEAF env var matches --no-leaf flag behaviour
- **WHEN** `sourcegraph-mcp status` is invoked with `SOURCEGRAPH_NO_LEAF=1` in the environment and without the `--no-leaf` flag
- **THEN** the rendered output is byte-identical to the `--no-leaf` invocation against the same repo state

#### Scenario: --no-color suppresses ANSI codes without affecting glyphs
- **WHEN** `sourcegraph-mcp status --no-color` is invoked under a tty
- **THEN** stdout contains no ANSI escape sequences (no `\x1b[` byte sequences); the state-glyph language is unchanged (`🌿` still appears for healthy items unless `--no-leaf` is also set)

### Requirement: status snapshot data sources
The snapshot rendered by `status` (and consumed by future operator-console renderers) SHALL aggregate five state surfaces in a single immutable record. Each surface SHALL be populated from the data sources documented below, and each SHALL be addressable in the `--json` output via a stable snake_case top-level field.

**1. `environment`** — sourced from `OnboardingDetector.DetectAsync`. JSON fields: `dotnet_sdk_version` (string or null), `git_on_path` (bool), `repo_root_path` (absolute string), `solution_files` (array of absolute strings), `sourcegraph_config_status` (one of `missing`, `valid`, `malformed`), `sourcegraph_config_error` (string or null).

**2. `scopes`** — sourced from `_meta.db` plus per-scope DB read-only queries. JSON shape: array of objects with fields `name` (string), `status` (one of `ok`, `partial`, `degraded`, `indexing`), `symbol_count` (integer), `reference_count` (integer), `last_indexed_at` (ISO-8601 string or null), `failed_projects` (array of strings, empty when `status != partial`), `failed_files` (array of strings, empty when `status != partial`), `isolated` (bool, true when the scope's config has `isolated: true`).

**3. `clients`** — sourced from `OnboardingDetector.ClientConfigsDetected`. JSON shape: array of objects with fields `slug` (one of `claude-code`, `copilot`, `cursor`, `continue`, `claude-desktop`), `scope` (one of `project`, `user`), `path` (absolute string), `exists` (bool), `contains_sourcegraph_entry` (bool).

**4. `embeddings`** — sourced from `EmbeddingsManager`. JSON fields: `model_id` (string, the active model identifier), `cache_dir` (absolute string), `cache_present` (bool), `total_bytes` (integer, sum of all cached files; zero when `cache_present == false`), `verified` (bool, true when the cache has been successfully `embeddings verify`-ed against pinned SHAs since the last `pull`).

**5. `recent_activity`** — sourced from a byte-bounded tail of `.sourcegraph/usage.jsonl` and `.sourcegraph/heals.jsonl`, merged and sorted by timestamp ascending, capped at the most recent 50 entries. JSON shape: array of objects with fields `ts` (ISO-8601 string), `kind` (string — e.g. `tool_call`, `heal`, `boot_reconcile`), `scope` (string or null), `ok` (bool), `ms` (integer), `detail` (string or null — a one-line human-readable summary).

The top-level JSON document SHALL also include: `built_at` (ISO-8601 string, when the snapshot was assembled), `usage_log_path` (absolute string), `heals_log_path` (absolute string), and `exit_code` (integer matching the process exit code: 0, 1, or 2).

Additions to the JSON shape in future revisions SHALL be append-only — adding new top-level fields or new fields to nested objects is permitted; renaming or removing fields requires a major-version bump in the spec.

#### Scenario: --json document has all six top-level surface fields
- **WHEN** `sourcegraph-mcp status --json` is invoked in any valid repo
- **THEN** the emitted JSON document has top-level keys `environment`, `scopes`, `clients`, `embeddings`, `recent_activity`, `built_at`, `usage_log_path`, `heals_log_path`, and `exit_code` — no other top-level keys exist at v1

#### Scenario: scopes array reflects _meta.db rows verbatim
- **WHEN** `sourcegraph-mcp status --json` is invoked in a repo with three configured scopes: `frontend` (ok), `backend` (partial, with `failed_projects: ["legacy.csproj", "old.csproj"]`), and `vendor` (ok, isolated)
- **THEN** the `scopes` array contains exactly three objects in declared-order; the `backend` row's `failed_projects` matches `["legacy.csproj", "old.csproj"]`; the `vendor` row's `isolated` is `true`; the `frontend` and `backend` rows' `isolated` is `false`

#### Scenario: recent_activity merges and sorts both log files
- **WHEN** `sourcegraph-mcp status --json` is invoked in a repo whose `usage.jsonl` ends with three tool-call entries at timestamps T1 < T3 < T5 and whose `heals.jsonl` ends with two heal entries at T2 and T4 (interleaved with the usage entries)
- **THEN** the `recent_activity` array contains all five entries in timestamp-ascending order (T1, T2, T3, T4, T5); each entry's `kind` field reflects its source log (`tool_call` for `usage.jsonl` rows, `heal` / `boot_reconcile` / etc. for `heals.jsonl` rows)

#### Scenario: snapshot tolerates a partial trailing JSONL line
- **WHEN** `sourcegraph-mcp status --json` is invoked while a concurrent `serve` process is mid-write to `usage.jsonl` (the last byte is mid-object, no trailing newline)
- **THEN** the partial line is dropped silently from `recent_activity`; the snapshot reports the prior complete entries; no parse-error appears in stderr; the process exits successfully
