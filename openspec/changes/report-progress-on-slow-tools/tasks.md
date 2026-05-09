## 1. Foundation: `Format.Progress` helper

- [ ] 1.1 Add `public static ProgressNotificationValue Progress(double fraction, string message)` to the existing `Format` static helpers (alongside `Format.Location`, `Format.AppendTable`, etc.). Implementation: `new ProgressNotificationValue { Progress = fraction, Total = 1.0, Message = message }`. XML doc note: messages SHALL be short imperatives, no user-controlled substrings.
- [ ] 1.2 Verify the SDK's `ProgressNotificationValue` namespace import is available (`ModelContextProtocol.Protocol` per the spike). Adjust usings as needed in the file containing `Format`.
- [ ] 1.3 No new test for the helper alone — its behaviour is trivial. Coverage comes via the per-tool tests in groups 2–4.
- [ ] 1.4 `dotnet build` clean. CI green on a no-op pass.

## 2. Convert `semantic_search` (vertical slice)

- [ ] 2.1 In `GraphTools.cs`, find `SemanticSearchAsync` and add an `IProgress<ProgressNotificationValue>? progress = null` parameter immediately before the existing `CancellationToken ct = default` parameter.
- [ ] 2.2 Inside the tool body, emit progress at three checkpoints:
  - Before `generator.EmbedAsync(...)`: `progress?.Report(Format.Progress(0.0, "encoding query"));`
  - After `EmbedAsync` returns, before `host.EmbeddingsStore.SearchAsync(...)`: `progress?.Report(Format.Progress(0.5, "searching"));`
  - After `SearchAsync` returns, before the `StringBuilder` formatting loop: `progress?.Report(Format.Progress(0.9, "formatting results"));`
- [ ] 2.3 Add `ProgressReportingTests.cs` in the test project. Use a fake `Progress<ProgressNotificationValue>` (delegating to `captured.Add`) and invoke the tool body via `ToolMetrics.TrackAsync` (or directly, depending on what the existing test infrastructure makes easier). Assert: 3 captured values; `Progress` strictly monotonic increasing; messages match the documented strings; `Total == 1.0` on every captured value.
- [ ] 2.4 Add a no-op-path test: invoke the same tool with no `progress` argument; assert it runs to completion without exceptions and produces the expected result text.
- [ ] 2.5 `dotnet test` — full suite green.

## 3. Convert `impact_of_change`

- [ ] 3.1 Add the `IProgress<ProgressNotificationValue>? progress = null` parameter to `ImpactOfChangeAsync` (before `CancellationToken ct = default`).
- [ ] 3.2 Emit one checkpoint at the start of the tool body, after symbol resolution and before the recursive CTE: `progress?.Report(Format.Progress(0.0, "querying"));`
- [ ] 3.3 Add scenario tests in `ProgressReportingTests.cs`: capture asserts 1 entry with `Progress = 0.0` and `Message = "querying"`.

## 4. Convert `module_summary`

- [ ] 4.1 Add the `IProgress<ProgressNotificationValue>? progress = null` parameter to `ModuleSummaryAsync` (before `CancellationToken ct = default`).
- [ ] 4.2 Emit one checkpoint at the start of the tool body, before the in-degree aggregate: `progress?.Report(Format.Progress(0.0, "querying"));`
- [ ] 4.3 Add scenario tests in `ProgressReportingTests.cs`.

## 5. Documentation

- [ ] 5.1 In `README.md`, add a short subsection describing which tools emit `notifications/progress` and the protocol-level opt-in mechanism (clients send a `progressToken` on `tools/call`). Include a one-line code example showing a JSON-RPC `tools/call` request with `progressToken` set.
- [ ] 5.2 In `CLAUDE.md`, add a one-liner note that future Claude sessions know to expect progress notifications on `semantic_search`, `impact_of_change`, and `module_summary` when running through a progress-aware client.

## 6. Verification

- [ ] 6.1 `dotnet build` clean.
- [ ] 6.2 `dotnet test` — full suite green, including the new `ProgressReportingTests`.
- [ ] 6.3 Drive a real `tools/call` JSON-RPC roundtrip against `tests/fixtures/Sample.sln`: send a `tools/call` for `semantic_search` WITH a `progressToken` field on the request, and confirm the server emits three `notifications/progress` messages over the wire (in addition to the final response). Then send the same call WITHOUT the `progressToken` and confirm zero progress messages are emitted.
- [ ] 6.4 Run `openspec validate report-progress-on-slow-tools --strict`.

## 7. Spec sync (archive)

- [ ] 7.1 Run `openspec archive report-progress-on-slow-tools --yes`. Confirm the new "Progress notifications on slow tools" requirement lands in `openspec/specs/mcp-tools/spec.md` cleanly (1 ADDED requirement, no MODIFIED).
