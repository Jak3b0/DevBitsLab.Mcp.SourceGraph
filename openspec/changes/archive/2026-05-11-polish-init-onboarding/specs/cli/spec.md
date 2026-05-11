## MODIFIED Requirements

### Requirement: init subcommand
The CLI SHALL accept `sourcegraph-mcp init` that runs an interactive (default) or flag-driven (`--yes`) onboarding flow producing per-client MCP configuration files, optionally pre-warming the index, and printing a closing report. Default writes SHALL be project-scoped (under `--root`, default CWD); user-scope writes SHALL require an explicit per-client opt-in flag OR — for clients with no project-scope path — interactive picker confirmation.

The subcommand SHALL accept the following flags:

- `--yes` / `-y` — non-interactive; accept all picker defaults documented in this requirement.
- `--client <id>` (repeatable) — restrict to the listed clients (`claude-code`, `copilot`, `cursor`, `continue`, `claude-desktop`).
- `--no-<client>` — exclude one client even if it would otherwise be auto-selected.
- `--user-<client>` — write that client's config to its user-scope path instead of the project-scope path.
- `--claude-desktop` — force the picker default for the `claude-desktop` row to ON, overriding detection. (Back-compat alias from when the flag gated row visibility entirely; the row is now always visible in the picker.)
- `--solution <path>` (repeatable) — override solution-discovery; passes through to `init-scopes` core logic when multiple solutions are configured.
- `--no-embeddings` / `--no-history` — propagate the corresponding `serve` flag into the written `args` array.
- `--prewarm` / `--no-prewarm` — opt in to / out of running `RoslynIndexer.IndexSolutionOnceAsync` after writing configs.
- `--install-mode {global,local-tool,in-repo}` — choose the resulting `command`/`args` shape: `global` invokes `sourcegraph-mcp` directly (default); `local-tool` emits `command: "dotnet"`, `args: ["sourcegraph-mcp", ...]` and assumes the repo already has a `.config/dotnet-tools.json` listing the tool (created via `dotnet new tool-manifest && dotnet tool install DevBitsLab.Mcp.SourceGraph.Tool`; `init` does not create or merge the manifest in v1); `in-repo` emits `command: "dotnet"`, `args: ["run", "--project", "<server csproj>", "--no-build", "--", "serve", ...]`.
- `--print-only` — print the per-client config snippets to stdout with `# would write to: <path>` comment lines; write no files.
- `--force` — overwrite an existing `sourcegraph` server entry without prompting (in interactive mode) or without skipping (in `--yes` mode); never modifies other servers' entries.
- `--diff` — when a writer would emit `SkipExistingDiffers`, print a unified diff of the existing target file versus the proposed `ContentBytes` to stdout before recording the skip; read-only without `--force`. See the `init --diff conflict preview` requirement for full semantics.
- `--root <path>` — repository root (default CWD).

#### Scenario: Interactive init wires Claude Code in a fresh repo
- **WHEN** a user runs `sourcegraph-mcp init` in a repo containing `MySln.slnx` and accepts the picker defaults at the single batched prompt
- **THEN** `<root>/.mcp.json` is written with the `mcpServers.sourcegraph` entry; the closing report names the file written and suggests `sourcegraph-mcp demo` as the next step

#### Scenario: Non-interactive init for CI
- **WHEN** a user runs `sourcegraph-mcp init --yes --client copilot --client claude-code --print-only` from a CI script
- **THEN** the command exits `0` after writing nothing, having printed two config snippets to stdout — one prefixed with `# would write to: <root>/.vscode/mcp.json` (Copilot's `servers`/`type` shape) and one prefixed with `# would write to: <root>/.mcp.json` (Claude Code's `mcpServers` shape)

#### Scenario: Init merges into an existing config without clobbering other servers
- **WHEN** the user has a pre-existing `<root>/.mcp.json` containing an `mcpServers.other-server` entry, and `sourcegraph-mcp init --yes --client claude-code` is invoked
- **THEN** the resulting `.mcp.json` contains both `mcpServers.other-server` (unchanged) and a new `mcpServers.sourcegraph` entry; the closing report's `Apply` phase shows a `🌿 wrote` row for `claude-code` and notes that an existing other-server entry was preserved

#### Scenario: Init refuses to overwrite a differing existing entry without --force
- **WHEN** the user has a pre-existing `<root>/.mcp.json` containing an `mcpServers.sourcegraph` entry whose `args` differ from what we would write, and `sourcegraph-mcp init --yes --client claude-code` (without `--force`) is invoked
- **THEN** the file is left unchanged; the `Apply` phase contains a `✗ conflict — skipped` row for `claude-code` with a hanging detail line naming the file and suggesting `--diff` to inspect or `--force` to overwrite; the process exits `2`

#### Scenario: Claude Desktop is always visible in the picker
- **WHEN** `sourcegraph-mcp init` is invoked interactively with no `--claude-desktop` flag and no detected Claude Desktop config file
- **THEN** the `Clients to wire` phase renders a `claude-desktop` row marked off-default (state glyph `·`, or `[ ]` under `--no-leaf`); pressing Enter at the batched picker prompt does NOT wire Claude Desktop, and the closing report does NOT include a Claude Desktop entry

#### Scenario: Detection bumps Claude Desktop default-on
- **WHEN** `sourcegraph-mcp init --yes` is invoked on a system where `~/Library/Application Support/Claude/claude_desktop_config.json` already exists, with no explicit `--claude-desktop` flag
- **THEN** Claude Desktop's picker row is default-on; the `Apply` phase contains a `🌿` row for `claude-desktop` (user scope); no project-scope file is created or modified for Claude Desktop

#### Scenario: --claude-desktop forces default-on
- **WHEN** `sourcegraph-mcp init --yes --claude-desktop` is invoked on a system where no Claude Desktop config file exists
- **THEN** Claude Desktop is wired anyway (a new platform-specific user-scope config file is created and the `mcpServers.sourcegraph` entry is inserted); the `Apply` phase shows a `🌿 wrote` row for `claude-desktop` whose path renders with `~/` substitution where applicable

#### Scenario: Pre-warm runs after writing configs
- **WHEN** `sourcegraph-mcp init --yes --client claude-code --prewarm --solution ./MySln.slnx` is invoked
- **THEN** after the `.mcp.json` write completes, `RoslynIndexer.IndexSolutionOnceAsync` is invoked against `./MySln.slnx`; the `Pre-warm` phase summary line reads `🌿 indexed MySln.slnx in T.Ts` (or `[x] indexed MySln.slnx in T.Ts` under `--no-leaf`)

## ADDED Requirements

### Requirement: init output state-glyph language
The `init` subcommand SHALL render its human-readable output (banner, detection summary, picker rows, apply rows, pre-warm summary, closing report) using a single state-glyph vocabulary applied uniformly across every phase. The five states and their tokens SHALL be:

- **on / passed / wrote / unchanged**: `🌿` (U+1F33F followed by U+0020); ASCII fallback `[x] ` under `--no-leaf` or `SOURCEGRAPH_NO_LEAF=1`
- **off / not selected**: `·` (U+00B7 followed by U+0020); ASCII fallback `[ ] `
- **soft warning**: `⚠` (U+26A0 followed by U+0020); ASCII fallback `[!] `
- **hard skip / conflict**: `✗` (U+2717 followed by U+0020); ASCII fallback `[X] `
- **unsupported / N/A**: `—` (U+2014 followed by U+0020); ASCII fallback `[-] `

Column alignment SHALL be preserved across the emoji and ASCII forms by treating each token's width as three display cells.

The `init` subcommand SHALL organise its output under named phase headings: `Environment`, `Clients to wire`, `Apply`, `Pre-warm` (omitted when no pre-warm runs in this invocation), and `Next`. Phase headings SHALL appear at the left margin without a leading state glyph; rows under a phase heading SHALL be indented exactly two spaces.

The opt-out mechanism (`--no-leaf` CLI flag and `SOURCEGRAPH_NO_LEAF=1` env var) SHALL apply to the entire state-glyph vocabulary and to any banner `🌿` mark in `init` output — the same `LeafFormatter.Suppressed` flag that controls server tool responses.

#### Scenario: Successful first-run renders all five phases
- **WHEN** `sourcegraph-mcp init --yes` is invoked in a repo with one solution and Claude Code's project config absent, with `--prewarm` set
- **THEN** stdout contains exactly five phase headings — `Environment`, `Clients to wire`, `Apply`, `Pre-warm`, `Next` — each on its own line at the left margin; rows under each heading are two-space-indented; the `Apply` phase contains a row beginning with `🌿 wrote` for the `claude-code` writer

#### Scenario: --no-leaf substitutes ASCII fallbacks
- **WHEN** `sourcegraph-mcp init --yes --no-leaf` is invoked
- **THEN** no `🌿` (U+1F33F) appears on stdout; every state-glyph position contains a three-character ASCII token from the set `[x] `, `[ ] `, `[!] `, `[X] `, `[-] `; phase headings render identically to the emoji-enabled path; the banner line shows `SourceGraph init` without a leading leaf

#### Scenario: --print-only omits the Pre-warm phase
- **WHEN** `sourcegraph-mcp init --yes --print-only` is invoked
- **THEN** the `Pre-warm` phase heading does not appear on stdout; the `Apply` phase still renders with each row showing the would-write verb under the configured glyph language

#### Scenario: SOURCEGRAPH_NO_LEAF env var matches --no-leaf flag behaviour
- **WHEN** `sourcegraph-mcp init --yes` is invoked with `SOURCEGRAPH_NO_LEAF=1` in the environment and without the `--no-leaf` flag
- **THEN** the rendered output is byte-identical to the `--yes --no-leaf` invocation against the same repo state

### Requirement: init batched client picker
When `init` is invoked interactively (stdin is a tty AND `--yes` was not passed AND `--print-only` was not passed), the `Clients to wire` phase SHALL render every supported client as a row showing its default-selected state with the state-glyph language defined in the `init output state-glyph language` requirement (`🌿`/`[x]` selected, `·`/`[ ]` off), then prompt the user exactly once with a batched prompt allowing one of the following responses:

1. Empty input, `y`, or `Y` — accept the displayed defaults verbatim.
2. `n` or `N` — deselect every row; no client is wired.
3. A whitespace-separated sequence of tokens of the form `+<slug>` or `-<slug>` — start from the displayed defaults, then flip each named client (`+` adds to the selection, `-` removes from it). Slugs that don't match the supported set SHALL produce a warning on stderr and SHALL be ignored.

On any input that doesn't match one of those three forms, the prompt SHALL be re-displayed once with the example shown; a second invalid input SHALL be treated as `n` (deselect all) and the run SHALL proceed.

The supported slug set SHALL match the `--client` flag's supported values: `claude-code`, `copilot`, `cursor`, `continue`, `claude-desktop`.

The picker default for each client SHALL be computed from `OnboardingDetector` signals:

- `claude-code`: default-on always.
- `copilot`: default-on always.
- `cursor`: default-on iff `<root>/.cursor/` directory or `~/.cursor/mcp.json` exists; otherwise default-off.
- `continue`: default-on iff `<root>/.continue/` directory or `~/.continue/mcp/sourcegraph.yaml` exists; otherwise default-off.
- `claude-desktop`: default-on iff the platform-specific Claude Desktop config file exists, OR if the `--claude-desktop` flag was passed; otherwise default-off.

The `--client <id>` flag SHALL override the picker entirely (the rows are rendered but only the listed clients are selected, regardless of defaults). The `--no-<client>` flag SHALL force the named client off in the displayed defaults.

#### Scenario: Empty input accepts displayed defaults
- **WHEN** the picker shows `claude-code` and `copilot` as default-on (`🌿`) and `cursor`, `continue`, `claude-desktop` as default-off (`·`); user presses Enter at the prompt
- **THEN** `claude-code` and `copilot` writers run; `cursor`, `continue`, `claude-desktop` writers do not

#### Scenario: `+slug -slug` edits the default selection
- **WHEN** the picker shows `claude-code` and `copilot` as default-on; user types `+cursor -copilot` and presses Enter
- **THEN** `claude-code` and `cursor` writers run; `copilot` writer does not; `continue` and `claude-desktop` writers do not (off-defaults preserved)

#### Scenario: Unknown slug warns and is ignored
- **WHEN** the user types `+sublime` at the picker prompt
- **THEN** a warning is printed to stderr naming the unknown slug; the picker proceeds with the displayed defaults (the unknown token is dropped)

#### Scenario: `n` deselects all
- **WHEN** the user types `n` at the picker prompt
- **THEN** no writer runs; the `Apply` phase shows the line `No clients selected. Nothing to do.`; the process exits `0`

#### Scenario: Detection-driven cursor default
- **WHEN** the user runs `sourcegraph-mcp init` interactively in a repo where `<root>/.cursor/` exists; defaults are computed
- **THEN** the picker's `cursor` row is rendered with the `🌿` (or `[x]`) state glyph; pressing Enter wires `cursor` along with `claude-code` and `copilot`

#### Scenario: Detection-driven continue default-off when no install fingerprint
- **WHEN** the user runs `sourcegraph-mcp init` interactively in a repo with no `.continue/` directory and no `~/.continue/` directory
- **THEN** the picker's `continue` row is rendered with the `·` (or `[ ]`) state glyph; pressing Enter does NOT wire `continue`

### Requirement: init --diff conflict preview
The `init` subcommand SHALL accept a `--diff` flag. When the flag is set, AND a writer's plan would land in `SkipExistingDiffers`, AND the run is not under `--print-only`, the subcommand SHALL render a unified diff with three lines of surrounding context comparing the existing target file's bytes against the writer's proposed `ContentBytes`, printed to stdout under the conflicting `Apply` row as a hanging-detail block. The diff `---` header SHALL name the target file path; the `+++` header SHALL name `<target>.proposed`.

`--diff` SHALL be read-only by default. When combined with `--force`, the diff SHALL be printed first AND the write SHALL then proceed (the existing `--force` behaviour, augmented with the diff print). Without `--force`, the file SHALL be left unchanged and the process SHALL exit `2`.

`--diff` SHALL be a no-op for plans other than `SkipExistingDiffers` (`Insert`, `NoOpAlreadyMatches`, `ReplaceOurs`, `SkipHasComments`, `SkipUnsupported`).

#### Scenario: --diff shows the unified diff and exits 2 without --force
- **WHEN** `<root>/.mcp.json` contains an `mcpServers.sourcegraph` entry whose `args` array differs from what `init` would write, and `sourcegraph-mcp init --yes --client claude-code --diff` is invoked
- **THEN** the `Apply` phase shows `✗ conflict — skipped  claude-code  .mcp.json` (or `[X] conflict — skipped  ...` under `--no-leaf`); a unified diff is printed below the row indented one level further, with `---` and `+++` headers naming `.mcp.json` and `.mcp.json.proposed` respectively, and `-`/`+` markers on the differing lines; the file is not modified; the process exits `2`

#### Scenario: --diff combined with --force prints diff then writes
- **WHEN** the same conflicting `<root>/.mcp.json` is present and `sourcegraph-mcp init --yes --client claude-code --diff --force` is invoked
- **THEN** the unified diff is printed first; then the `Apply` phase contains `🌿 replaced  claude-code  .mcp.json` (or `[x] replaced  ...` under `--no-leaf`); the file is rewritten with the proposed content; the process exits `0`

#### Scenario: --diff on an insert plan is a no-op
- **WHEN** `<root>/.mcp.json` does not exist and `sourcegraph-mcp init --yes --client claude-code --diff` is invoked
- **THEN** the `Apply` phase shows `🌿 wrote  claude-code  .mcp.json`; no diff output is printed; the process exits `0`

#### Scenario: --diff on a SkipHasComments plan is a no-op
- **WHEN** `<root>/.mcp.json` contains line comments (`// ...`) outside string literals and `sourcegraph-mcp init --yes --client claude-code --diff` is invoked
- **THEN** today's comment-aware degraded-mode behaviour runs unchanged: the would-write snippet is printed to stdout with the `# config has comments at ...` warning, the file is not modified, and no unified diff is produced; the process exits `0`
