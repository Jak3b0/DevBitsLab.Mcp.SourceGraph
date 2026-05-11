# Test impact audit — `polish-init-onboarding`

Audit covers `tests/DevBitsLab.Mcp.SourceGraph.Tests/` looking for strings that pin today's
init / picker / closing-report output. Each row records the file:line, what's pinned, and the
plan for the migration (substring or invariant style).

## Pinned strings in init-related tests

| File:line | Pinned string | Today's source | Migration plan |
|---|---|---|---|
| `OnboardingCliTests.cs:56` | `# would write to:` | `PrintPlanToStdout` | Keep — same prefix preserved in new renderer (`PrintPlanToStdout` unchanged for `--print-only`) |
| `OnboardingCliTests.cs:57` | `.mcp.json` | path in `# would write to:` | Substring, OK |
| `OnboardingCliTests.cs:58` | `"mcpServers"` | writer JSON content | Substring, OK |
| `OnboardingCliTests.cs:59` | `"sourcegraph"` | writer JSON content | Substring, OK |
| `OnboardingCliTests.cs:67` | `"servers"` | Copilot writer JSON | Substring, OK |
| `OnboardingCliTests.cs:68` | `"type": "stdio"` | Copilot writer JSON | Substring, OK |
| `OnboardingCliTests.cs:69` | NOT `"mcpServers"` | Copilot writer JSON | Substring, OK |
| `OnboardingCliTests.cs:82-84` | `.mcp.json`, `.vscode/mcp.json`, `.cursor/mcp.json` | `# would write to: <path>` | Substring, OK — relative paths preferred under new render |
| `OnboardingCliTests.cs:148` | NOT `.cursor/mcp.json` | `--no-cursor` filters writer | Substring, OK |
| `OnboardingCliTests.cs:159` | NOT `claude_desktop_config.json` | `--print-only` without `--claude-desktop` | **CHANGES**: Claude Desktop is now visible in picker; for `--print-only` the writer still runs only when picker default-on OR `--claude-desktop`. With no detection, default is off → snippet stays absent. Test stays as-is. |
| `OnboardingCliTests.cs:171` | `"wrote"` | closing-report token | Substring, OK — new renderer still uses verb `wrote` after the leaf glyph |
| `OnboardingCliTests.cs:178` | `"no change"` | closing-report `NoOpAlreadyMatches` | Substring — new renderer emits same verb after glyph |
| `OnboardingCliTests.cs:190` | `"skipped (unsupported)"` | closing-report `SkipUnsupported` | Substring — new renderer emits `skipped — unsupported` or similar; **UPDATE** to looser `"unsupported"` substring or matching new shape |
| `OnboardingCliTests.cs:399` | `"🌿"` (in DemoCliTests) | Demo leaf marker | Not init; left alone |

## Test files with no impact

- `OnboardingDetectorTests.cs` — exercises `DetectAsync` data shape, not init output. New
  detection-driven default booleans are additive: existing tests continue to pass.
- `ClientConfigWritersTests.cs` — exercises writer plan/apply contract, not the CLI surface.
- `DoctorCliTests.cs`, `DemoCliTests.cs` (in same file) — Change 2 owns Doctor; demo is
  unaffected.

## Tests piping per-row picker answers

None. The existing tests all use `--yes` (non-interactive) or `--print-only`. No piped-stdin
per-row `[Y/n]` flow exists today, so the batched picker swap doesn't break any current test.

## Summary

- 14 pinned-string assertions in `OnboardingCliTests.cs`.
- 12 stay as-is (substring on stable tokens — `wrote`, `no change`, `# would write to:`,
  writer-content JSON snippets, file paths).
- 1 changes phrasing slightly (`"skipped (unsupported)"` → `"unsupported"` substring) because
  the new layout drops parentheses in favour of an em-dash detail.
- 1 is on a Claude Desktop visibility check that still works under the new defaults (default-off
  without detection → still absent in `--print-only` output).
- 0 per-row picker tests need rewriting.

New tests to add under `tests/.../Cli/Rendering/`:

- `StateGlyphTests.cs`
- `PathDisplayTests.cs`
- `InitRendererTests.cs`
- `BatchedPickerInputTests.cs`
- `UnifiedDiffRendererTests.cs`

New test scenarios to add into `OnboardingCliTests.cs`:

- Detection-driven picker default-on for `cursor` (when `.cursor/` exists).
- `--claude-desktop` flag forces default-on without detection.
- `--diff` renders unified diff on `SkipExistingDiffers`.
- `--diff` no-op on `Insert`.
- `--diff --force` writes after printing diff.
