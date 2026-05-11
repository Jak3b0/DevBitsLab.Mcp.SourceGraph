## Context

`InitCli.RunAsync` today is a top-down procedure: it prints a banner + detection summary inline (`Console.WriteLine` peppered through `PrintDetectionSummary`), runs an `InteractiveClientPicker` that does N inline `Console.Write` + `ReadLine` round-trips, applies writers, then prints a closing report through `PrintClosingReport`. Every glyph and string is hard-coded at its emit site. The brand mark `🌿` only appears in the banner heading; everywhere else, outcomes use `✓ ⚠ ⓘ`.

The `OnboardingDetector` already has all the data needed to make the picker smarter — it produces `IReadOnlyList<DetectedClientConfig>` carrying the project-scope and user-scope path for each client, whether the file exists, and whether it contains a `sourcegraph` entry. The picker just doesn't read that. The `WriterPlan` record is similarly rich: `Action`, `TargetPath`, `Description`, `ContentBytes` — the byte content for the would-write document is already plumbed through.

The `--no-leaf` opt-out, the `LeafFormatter` helper, and the env var `SOURCEGRAPH_NO_LEAF` already exist (from `add-leaf-brand-mark`). They're scoped to MCP tool responses today; this change extends their reach to the CLI surfaces. The existing `LeafFormatter.Suppressed` flag is the right hook.

`Claude Desktop` is gated out of the picker by an explicit `claudeDesktopOptedIn` parameter to `InteractiveClientPicker`, plus a "Claude Desktop is always user-scope" line at the end of `ResolveEnabledClients`. The user-scope mechanics are right; the visibility gate is what's wrong.

## Goals / Non-Goals

**Goals:**
- One coherent visual language across the entire `init` flow: phase headings, leaf-as-state glyph, aligned columns, relative paths, single-line pre-warm summary.
- Replace N per-row `[Y/n]` prompts with a single batched prompt (`Enter` accepts defaults, `+/-slug` edits them, `n` deselects all).
- Surface `claude-desktop` in the picker by default (default-off unless detected), without changing its user-scope-write semantics.
- Add `--diff` so users can preview *what* a `SkipExistingDiffers` conflict looks like before reaching for `--force`.
- All output rendering routes through one renderer module so future changes (status, dashboard) consume the same primitives.

**Non-Goals:**
- A TUI-style picker with arrow-key navigation. The batched `+/- slug` text grammar is sufficient for v1; the live dashboard (`add-operator-dashboard`) carries the TUI investment.
- Localisation. The leaf glyph and the prompt strings are English-only.
- Validating that the user's terminal renders Unicode correctly. `--no-leaf` covers operators on terminals that struggle with emoji; we don't auto-detect.
- Restructuring the `WriterPlan` / `IClientConfigWriter` contract. Writers' inputs and outputs are unchanged; the polish lives in the rendering layer that surrounds them.
- Changing `init`'s exit-code semantics. `0 / 2` (conflict) / `1` (hard error) carry forward unchanged.
- Polishing `doctor` output (deferred to `add-operator-status`, which subsumes `doctor` data into `status`).

## Decisions

### Decision 1 — Leaf-state glyph language

The five-glyph vocabulary:

| state | glyph | colour intent | meaning |
|---|---|---|---|
| on / passed / wrote / unchanged | `🌿` | green (default emoji rendering) | the positive signal; "this thing succeeded / is selected" |
| off / not selected | `·` (U+00B7) | dim | "this row exists but is inactive" |
| soft warning | `⚠` (U+26A0) | yellow | "non-fatal; explanation follows" |
| hard skip / conflict | `✗` (U+2717) | red | "blocked; user action needed" |
| unsupported / N/A | `—` (U+2014) | dim | "this combination has no writer" |

`--no-leaf` (or `SOURCEGRAPH_NO_LEAF=1`) maps:

| state | ascii fallback |
|---|---|
| on | `[x]` |
| off | `[ ]` |
| warning | `[!]` |
| hard skip | `[X]` |
| unsupported | `[-]` |

The fallback is a square-bracket-prefixed token of the same width (3 chars), preserving column alignment. Implementation: a `StateGlyph` static class returning the right token from `LeafFormatter.Suppressed`.

**Alternatives considered:**
- Using `✓` for on (today's mixed convention). Loses the brand reinforcement; we already shipped `🌿` as the server-wide voice mark.
- Using a six-glyph vocabulary that distinguishes `wrote` from `unchanged` from `selected`. Too many semantic codes in one column; the second-line description carries the verb.
- Keeping ASCII as primary and emoji as opt-in. Inverts the polish bar; emoji renders correctly in every modern terminal (iTerm2, Kitty, Alacritty, Windows Terminal, VS Code integrated, JetBrains Run consoles) and `--no-leaf` exists for the rest.

### Decision 2 — Phase-headed layout, indented columns

Five named phases, each rendered as a left-aligned heading on its own line, contents indented two spaces, columns aligned within the phase but not across phases:

```
🌿 SourceGraph init                                                  v0.8.0

  Environment
    {glyph} {key:18}    {value}

  Clients to wire
    {glyph} {slug:15}  {scope:8} {detail-line}

    {batched picker prompt}

  Apply
    {glyph} {verb:18}  {slug:15}  {relative-path}
       {hanging detail line on conflict, indented +4}

  Pre-warm
    {glyph} {summary line}

  Next
    {prose suggestion}
```

The phase header itself uses **no** glyph — it's a section anchor, not a state. `Pre-warm` is omitted entirely when no pre-warm runs (e.g. `--no-prewarm` or `--print-only`).

**Alternatives considered:**
- Box-drawing characters (`├─`, `└─`) for phase separation. Looks pretty in mocks, but breaks under output redirection, narrow terminals, and screen readers. Indentation is universally readable.
- Single-column "log lines" with no phase headers. Today's shape; the readability complaint is exactly that we have no anchors.

### Decision 3 — Batched picker grammar: `Enter`, `n`, or `+slug -slug`

The picker first prints all rows with their default state (`🌿` selected, `·` not), then a single prompt:

```
  Accept defaults? [Y/n]  or edit (e.g. "+cursor -copilot"): _
```

Input parsing:

| input | meaning |
|---|---|
| empty / `y` / `Y` | accept the displayed defaults |
| `n` / `N` | deselect every row; no client gets wired |
| any whitespace-separated tokens of form `+<slug>` or `-<slug>` | start from the displayed defaults, then apply each token as a flip (`+` selects, `-` deselects); unknown slugs are warned and ignored |
| any other input | re-prompt once with a hint; second invalid input deselects all (matches `n`) and proceeds |

Slugs accepted: the same set already accepted by `--client` (`claude-code`, `copilot`, `cursor`, `continue`, `claude-desktop`).

**Alternatives considered:**
- Per-row `[Y/n]` (today). Loud, repetitive, doesn't reinforce defaults.
- Numbered selection (`1,3,5`). Easier to mistype; obscures slugs that the rest of the CLI uses.
- Full TUI (arrow + space). Saves for the dashboard work in `add-operator-dashboard`; over-investment for `init`.

### Decision 4 — Detection-driven default selection

For each client, the picker default is computed from `OnboardingDetector` signals:

| client | default-on rule |
|---|---|
| `claude-code` | always on (project-scope `.mcp.json` is the canonical sourcegraph wire-up) |
| `copilot` | always on (`.vscode/mcp.json` is committed in most VS Code repos) |
| `cursor` | on iff `.cursor/` directory exists OR `~/.cursor/mcp.json` exists |
| `continue` | on iff `.continue/` directory exists OR `~/.continue/` exists |
| `claude-desktop` | on iff the platform-specific user config file exists |

The `--client <id>` flag (and its `--no-<id>` siblings) override the picker entirely, as today. The `--claude-desktop` flag forces `claude-desktop`'s default-on regardless of detection — it's the documented escape hatch when detection misses (custom install path, brand new install).

**Alternatives considered:**
- Always-on for every client (today's behaviour). Presumptuous for `continue` and `claude-desktop` against users who don't use them.
- Always-off, force user to pick. Loses the "out-of-the-box wired up" experience.
- Detection-driven for all five (no always-on for `claude-code` / `copilot`). Risks an empty default for users in repos without any client config yet — the very first-run case `init` is meant to serve.

### Decision 5 — `--diff` renders unified diff of existing vs proposed

When a writer's plan would be `SkipExistingDiffers`, `--diff` is on, and we're not under `--print-only`:

1. Read the existing target file bytes.
2. Run the writer's plan to get the proposed `ContentBytes`.
3. Render a unified diff (3 lines of context) with the existing path as the "from" and `<target>.proposed` as the "to" label.
4. Print under the `Apply` row's hanging-detail block.
5. Skip the write (no behaviour change without `--force`).

Diff library choice: `DiffPlex` (already MIT-licensed, no native deps, ~50 KB). Project doesn't currently take a `DiffPlex` dependency; this is a small addition.

`--diff` and `--force` compose: with both set, the diff is printed first, then the write proceeds. Without `--diff`, today's "name the conflict" behaviour is unchanged. `--diff` outside a conflict (e.g., `Insert`, `NoOpAlreadyMatches`) is a no-op — diff renderer only fires on `SkipExistingDiffers`.

**Alternatives considered:**
- Hand-rolled line-level diff. Quick but the alignment edge cases (large blocks, JSON formatting differences) are exactly what DiffPlex handles correctly out of the box.
- Spawning `git diff --no-index`. Forks a process per conflict; works only when git is on PATH (which we already warn about); ties output format to the user's git config.
- Print full proposed content (today's `--print-only` for one writer). Doesn't show what's *changing*; user has to eyeball the difference.

### Decision 6 — Path display: relative-to-root by default

Inside `Apply` and `Clients to wire`, paths under `--root` render as repo-relative (no leading `./`). Paths in the user tree render with `~/` substitution. Absolute paths only appear:
- In hanging conflict-detail lines (so the user can copy-paste).
- In the closing summary if `--print-only` (since the user is presumably reading to copy-paste).

The render helper takes `(absolutePath, root, homePath)` and returns the shortest readable form. Tests cover the three cases (under-root, under-home, neither).

### Decision 7 — Pre-warm: child stream inherits, summary line follows

Today's `PrewarmAsync` inherits the child's stdout. We keep that. The single-line summary `🌿 indexed <name> in <s>s` is appended after the child exits. On non-zero exit, the line becomes `⚠ pre-warm exit {code} after {s}s` and the closing report carries a warning row.

Capturing the indexer stream into a structured progress channel is out of scope for this change — the indexer doesn't currently emit structured progress. The `add-operator-dashboard` change carries that investment (live progress in the TUI) and may circle back to capture stdout for a quieter `init` mode then.

## Risks / Trade-offs

- **Risk: tests pinning exact output strings break en masse.** → Mitigation: rendering routes through a single `InitRenderer` (or equivalent) class with public methods per phase; tests assert on phase-level invariants (column alignment, glyph presence) rather than full-line byte-exact strings. The migration follows the same pattern `add-leaf-brand-mark` used (snapshot regenerate + targeted assertion rewrites), with the test-impact audit happening in tasks step 1.
- **Risk: batched picker is unfamiliar.** → Mitigation: prompt example (`+cursor -copilot`) is shown inline; `n` is a recognised shortcut; the second invalid-input round still proceeds (no infinite loop); `--yes` skips the picker entirely.
- **Risk: detection-driven defaults misfire when users have stale `.cursor/` from a long-uninstalled Cursor.** → Mitigation: detection probes the *config file* presence too (`~/.cursor/mcp.json` for cursor, equivalent for continue), not just the directory. Stale dirs don't carry config files.
- **Risk: `--no-leaf` users get an asymmetric experience (square-bracket fallback may not look as polished).** → Mitigation: column alignment is preserved (every fallback token is 3 chars); the phase-headed layout still works without emoji. Smoke-test the `--no-leaf` path in a screenshot-comparison pair.
- **Risk: `claude-desktop` default-on by detection wires user-tree files where the user expected project-only.** → Mitigation: `claude-desktop` writes user-scope only (today's invariant, unchanged). Picker confirmation is the gate; the user sees the row in the picker before any write happens. `--yes` follows the picker default (detected → on, undetected → off), which is the safer side for ambiguous cases.
- **Trade-off: removing the per-row `[Y/n]` prompt.** Some scripts may have piped per-row answers. None are documented or shipped. The `--yes` non-interactive path is the supported automation interface; we add an `--accept-defaults` alias if needed during migration.
- **Trade-off: `DiffPlex` adds a dependency.** Small (~50 KB, MIT, AOT-friendly). Worth the cost for the `--diff` user value.

## Migration Plan

This is an in-place CLI surface change. There's no on-disk state to migrate; the writers' file outputs are unchanged. Two compatibility notes for early adopters:

1. **Per-row picker input is removed.** Anyone scripting `init` interactively (rare; documented path is `--yes`) needs to switch to either `--yes --client <slug>` (recommended) or pipe a single batched-prompt input string.
2. **`--claude-desktop`'s semantics shift.** From "required to wire" to "force default-on for that picker row." Existing scripts continue to work — the flag still results in `claude-desktop` getting wired — but operators who used it as a "make it appear in the picker" flag now find it visible by default.

Rollback: `init` is a stateless subcommand. Reverting this change reverts `Cli/InitCli.cs` to today's shape; no committed user files (`/.mcp.json`, etc.) are affected since file content is unchanged.

## Open Questions

1. **Auto-chain `init` → `demo`.** Should interactive `init` (post-prewarm, post-write, no conflicts) automatically run the canned `demo` for the "ah, it works" moment? Adds 2–4 s. Decision: defer to a follow-up — out of this change's scope.
2. **`--no-leaf` glyph choice.** `[x]` and `[ ]` are intuitive, but `[*]` is one keystroke faster on some keyboards. Going with `[x]`/`[ ]` to match the broader convention; revisit if user feedback prefers `[*]`.
3. **Should `--diff` imply `--print-only` semantics for that one writer?** Currently `--diff` only kicks in for `SkipExistingDiffers`. A `--diff` run that lands an `Insert` simply writes (the existing file was absent — no diff to render). Decision: leave as-is; `--print-only` is the orthogonal "don't write" flag.
