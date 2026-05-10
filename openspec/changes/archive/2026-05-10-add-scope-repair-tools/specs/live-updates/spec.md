## ADDED Requirements

### Requirement: Bounded retry on initial workspace open

`LiveIndexService` (and `ScopeHost.ColdIndexAsync` for the per-scope path) SHALL wrap the workspace-open + initial-index sequence in a bounded retry loop of up to 3 attempts, with exponential backoff `[1000ms, 5000ms, 25000ms]` between attempts. The first attempt runs immediately; subsequent attempts run after the corresponding backoff delay.

A retry SHALL fire when any attempt throws an exception other than `OperationCanceledException`. `OperationCanceledException` SHALL be rethrown immediately without retry — cooperative shutdown wins.

When an attempt N > 1 succeeds, the service SHALL emit a heal event with `kind = "workspace-open-retried"`, `ok = true`, `details = "succeeded on attempt N"`. When all 3 attempts fail, the service SHALL emit a heal event with `kind = "workspace-open-retried"`, `ok = false`, `details = "all 3 attempts failed: <last exception message>"`, then proceed with today's path: log at error level and mark the scope `degraded` with the original exception's message.

The retry SHALL NOT fire on watcher-driven incremental reindexes (`IndexChangedFilesAsync`) — only on the cold-index path. Steady-state reindex failures continue to follow the existing per-batch logging path (`"Scope `{Id}`: failed to apply change batch"`).

#### Scenario: Transient workspace failure recovers on second attempt
- **GIVEN** `RoslynIndexer.OpenAsync` throws `IOException("dotnet restore in progress")` on the first attempt and succeeds on the second
- **WHEN** the cold-index path fires
- **THEN** the second attempt's success is recorded; total wall-clock elapsed is at least 1000ms (the 1s backoff); `heals.jsonl` contains one line with `kind = "workspace-open-retried"`, `ok = true`, `details = "succeeded on attempt 2"`; the scope is marked `ok` in the registry; no other heal event is emitted

#### Scenario: All retries exhausted, scope marked degraded
- **GIVEN** `RoslynIndexer.OpenAsync` throws on all 3 attempts with the same exception message
- **WHEN** the cold-index path fires
- **THEN** total wall-clock elapsed is at least 31000ms (1s + 5s + 25s backoffs); `heals.jsonl` contains one line with `kind = "workspace-open-retried"`, `ok = false`, `details = "all 3 attempts failed: <message>"`; the scope is marked `degraded` with the exception message in the registry's `status_message`; the host stays up

#### Scenario: Cancellation during backoff is honoured immediately
- **GIVEN** the workspace open throws on the first attempt and the cancellation token is signalled mid-backoff (during the 1s wait)
- **WHEN** the await completes with `OperationCanceledException`
- **THEN** the retry loop exits immediately without a third attempt; no heal event is written; the scope's status is left at whatever the calling path sets on cancellation (typically not modified)

#### Scenario: Steady-state reindex failure is unchanged
- **GIVEN** the cold-index succeeded (or the bounded-retry path is not active because the scope is already past initial bring-up)
- **WHEN** a watcher-driven `IndexChangedFilesAsync` call throws an `IOException` for one batch
- **THEN** the failure is logged at error level via the existing `"Scope `{Id}`: failed to apply change batch"` path; no retry is attempted at the steady-state layer; no heal event is written; the scope remains `ok` (the next batch may succeed)
