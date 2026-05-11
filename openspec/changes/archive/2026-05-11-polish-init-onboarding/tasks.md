## 1. Test impact audit (no code yet)

- [x] 1.1 Enumerate every test that asserts on exact init output strings (`grep -rn -E '"✓|"⚠|"ⓘ|Heading|"Which clients|"Summary:|"Next:|"Pre-warming" tests/`); record the list in `notes/test-impact.md` under this change directory.
- [x] 1.2 For each pinned assertion, decide whether it stays string-exact (and updates to the new layout) or migrates to substring/invariant style. Mark each entry in the checklist.
- [x] 1.3 Identify tests that pipe per-row picker answers into `init` (rare; the supported automation is `--yes`). Each one needs to switch to `--yes --client <slug>` or to the single batched-prompt input form.

## 2. Foundation: state-glyph language + render helper

- [x] 2.1 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/Rendering/StateGlyph.cs` exposing `static string For(StateGlyphKind kind)` that returns the emoji form by default and the ASCII fallback when `LeafFormatter.Suppressed` is true. Cover the five `StateGlyphKind` values: `On`, `Off`, `Warn`, `Skip`, `Unsupported`.
- [x] 2.2 Add unit tests for `StateGlyph` covering: every kind in emoji mode, every kind in `--no-leaf` mode, and the column-width property (every returned token is three display cells wide).
- [x] 2.3 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/Rendering/PathDisplay.cs` exposing `static string Render(string absolute, string root, string? homePath)` that prefers repo-relative form (no leading `./`), falls back to `~/`-substituted form for user-tree paths, and uses absolute otherwise. Unit tests cover the three branches plus the "absolute under both" tiebreak (repo-relative wins).
- [x] 2.4 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/Rendering/InitRenderer.cs` exposing one method per phase: `RenderBanner`, `RenderEnvironment`, `RenderClientsToWire`, `RenderApplyRow`, `RenderPreWarmSummary`, `RenderNext`. Each method writes to an injected `TextWriter` (so tests can capture output without redirecting `Console.Out`). Internal pad widths are constants at the top of the file.
- [x] 2.5 Add tests for `InitRenderer` asserting phase-header presence, two-space indentation under headers, column alignment within each phase, and that no phase emits anything when its inputs are empty (e.g. `Pre-warm` is omitted on null input).

## 3. Detection-driven picker defaults + Claude Desktop visibility

- [x] 3.1 Extend `OnboardingDetector.DetectAsync` (or add a helper) to expose per-client "default-on?" booleans alongside today's `DetectedClientConfig` list. Match the rules in the spec's `init batched client picker` requirement: `claude-code` and `copilot` always on; `cursor` on iff `.cursor/` dir or `~/.cursor/mcp.json`; `continue` on iff `.continue/` dir or `~/.continue/mcp/sourcegraph.yaml`; `claude-desktop` on iff platform-specific config file present.
- [x] 3.2 Remove the `claudeDesktopOptedIn` gate from `InteractiveClientPicker` in `InitCli.cs`. Claude Desktop is always visible; its initial selection state comes from the detection-derived default (`true` if detected, `false` otherwise) OR is forced `true` by `--claude-desktop`.
- [x] 3.3 Update tests in `tests/Server.Tests/.../InitCliTests.cs` (or equivalent): add a fixture covering the detection-driven default-on for each client; add the `--claude-desktop` force-on path; remove the test asserting `claude-desktop` was filtered out of the picker.

## 4. Batched picker grammar

- [x] 4.1 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/BatchedPickerInput.cs` exposing `static PickerResult Parse(string raw, IReadOnlySet<string> defaults, IReadOnlySet<string> knownSlugs)`. Result carries the resolved selection set plus a list of unknown slugs that produced warnings. Empty/`y`/`Y` returns defaults; `n`/`N` returns empty selection; `+/-` tokens flip from defaults.
- [x] 4.2 Add unit tests covering each branch: empty input, `y`, `n`, single `+slug`, single `-slug`, mixed `+a -b +c`, unknown slug, malformed token (no `+`/`-` prefix).
- [x] 4.3 Replace `InteractiveClientPicker` in `InitCli.cs` with a single-prompt flow that calls `BatchedPickerInput.Parse`, re-prompts once on invalid input, and proceeds with `n`-behaviour on second invalid input.
- [x] 4.4 Update tests: any test that previously sent N per-row answers must now send one line of input. Verify the picker re-prompt path and the `n` shortcut.

## 5. `--diff` flag

- [x] 5.1 Add `DiffPlex` NuGet reference to `src/DevBitsLab.Mcp.SourceGraph.Server/DevBitsLab.Mcp.SourceGraph.Server.csproj` (pin to a current 1.x release; AOT-friendly).
- [x] 5.2 Add `--diff` flag to `CommandLine.cs` (parallel to `--force` and `--print-only`).
- [x] 5.3 Add `src/DevBitsLab.Mcp.SourceGraph.Server/Cli/Rendering/UnifiedDiffRenderer.cs` exposing `static void Render(byte[] existing, byte[] proposed, string fromLabel, string toLabel, TextWriter writer, int contextLines = 3)`. Use `DiffPlex.Chunkers.LineChunker` to split, render `---`/`+++` headers, then standard `@@ -a,b +c,d @@` hunks with three lines of context.
- [x] 5.4 In `InitCli.RunAsync`, when `cli.Diff && plan.Action == WriterAction.SkipExistingDiffers && !cli.PrintOnly`, call `UnifiedDiffRenderer.Render(existing, plan.ContentBytes, targetPath, targetPath + ".proposed", Console.Out)` after the `Apply` row prints and before recording the result.
- [x] 5.5 Tests cover: diff renders on `SkipExistingDiffers` without `--force` (exit 2); diff renders then writes proceed with `--force` (exit 0); `--diff` is no-op for `Insert`, `NoOpAlreadyMatches`, `SkipHasComments`.

## 6. Phase-headed layout, leaf-state output

- [x] 6.1 Replace the inline `Console.WriteLine` calls in `InitCli.RunAsync` and `PrintDetectionSummary` with calls into `InitRenderer`. The `Environment` phase consumes the detection result; the `Clients to wire` phase consumes the picker output; the `Apply` phase consumes the per-writer `WriterRunResult` list as it accumulates; the `Pre-warm` phase consumes the `PrewarmAsync` summary; the `Next` phase emits the verify suggestion.
- [x] 6.2 Update `PrintClosingReport` to render through `InitRenderer.RenderApplyRow` per result so every plan outcome — including `SkipHasComments` — surfaces its description in the report (close gap from today where `SkipHasComments` description prints mid-run but not in the report).
- [x] 6.3 Update `PrintPlanToStdout` (the `--print-only` helper) to emit the same row shape for `--print-only` mode — without phase headings other than `Apply`, since detection + picker phases are absent in `--yes --print-only` runs. Verify against the existing scenarios in the `init subcommand` requirement.
- [x] 6.4 Update tests: snapshot-based assertions regenerate; structural assertions (phase headings present in order, two-space indent under each, leaf glyph for `Insert` outcomes) replace any line-byte-exact assertions that fall through.

## 7. Pre-warm summary line

- [x] 7.1 In `PrewarmAsync`, after `WaitForExitAsync`, replace today's `Console.WriteLine($"  pre-warm: exit {p.ExitCode} in {sw.Elapsed.TotalSeconds:F1}s")` with `InitRenderer.RenderPreWarmSummary(p.ExitCode, sw.Elapsed)`. On exit 0 the summary line uses `🌿 indexed <solution-name> in <s>s`; on non-zero exit it uses `⚠ pre-warm exit <code> after <s>s`.
- [x] 7.2 Confirm the child indexer's stdout is still inherited (today's design — visibility during long-running indexer runs preserved). The summary line follows on completion.
- [x] 7.3 Tests: invoke `init --prewarm` against a fast fixture solution; assert the summary line shape under emoji and `--no-leaf` modes.

## 8. Documentation

- [x] 8.1 Update the Quickstart section in `README.md` to show the polished `init` output (sample block). Keep the prose flow and the existing flags lines; replace the example output paragraph.
- [x] 8.2 Update the "Claude Desktop" subsection of the README's `Wiring it into an MCP client` section to note that `--claude-desktop` is no longer required for the row to appear in the picker; clarify the detection-driven default-on behaviour.
- [x] 8.3 Add a one-line note in `CLAUDE.md` naming the state-glyph language (`🌿` = positive, `·` = off, `⚠` = warn, `✗` = skip, `—` = unsupported; under `--no-leaf` substitute `[x]/[ ]/[!]/[X]/[-]`) so future Claude sessions know what the rendering means at a glance.
- [x] 8.4 Run `openspec validate polish-init-onboarding --strict`; fix any structural issues. Confirm the change shows in `openspec list --json`.
