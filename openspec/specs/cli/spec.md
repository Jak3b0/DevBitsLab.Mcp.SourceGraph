# CLI

## Purpose

Expose the indexer and MCP server as a single user-friendly executable
(`sourcegraph-mcp`) with focused subcommands for one-shot indexing,
long-running stdio service, stats, and DB cleanup, plus token expansion so a
project-scoped `.mcp.json` can use placeholders like `${workspaceFolder}`.
## Requirements
### Requirement: Subcommand routing
The CLI SHALL accept four top-level subcommands (`serve`, `index`, `stats`,
`clear`) and exit with code `2` on an unrecognised subcommand or argument.

#### Scenario: Run serve with a solution
- **WHEN** `sourcegraph-mcp serve --solution <sln>` is invoked
- **THEN** the host starts an MCP stdio server, registers the
  `LiveIndexService` background service, and runs the indexer against the
  given solution

#### Scenario: Run a one-shot index
- **WHEN** `sourcegraph-mcp index <sln>` is invoked
- **THEN** `RoslynIndexer.IndexSolutionOnceAsync` runs against the solution,
  prints `indexed N files, M symbols, R refs in T s`, and exits `0`

#### Scenario: Print database stats
- **WHEN** `sourcegraph-mcp stats` is invoked against an existing graph DB
- **THEN** the file/symbol/reference/edge counts and the resolved DB path
  are printed to stdout

#### Scenario: Clear the database
- **WHEN** `sourcegraph-mcp clear` is invoked
- **THEN** the graph DB file is deleted (if present), an empty schema is
  recreated, and `cleared <path>` is printed

### Requirement: Help and error UX
The CLI SHALL show a usage block on `--help` / `-h` and on argument errors,
listing every supported subcommand and flag.

#### Scenario: Display help
- **WHEN** the user runs `sourcegraph-mcp --help`
- **THEN** the help text lists `serve`, `index`, `stats`, `clear` plus the
  `--solution`, `-s`, and `--db` flags and their defaults

### Requirement: Token expansion in path arguments
The CLI SHALL expand `${VAR}` placeholders in `--solution` and `--db` values
against process environment variables, with a special-case for
`${workspaceFolder}` that falls back to `WORKSPACE_FOLDER`,
`CLAUDE_PROJECT_DIR`, and `MCP_WORKSPACE_FOLDER` in that order.

#### Scenario: Expand workspaceFolder via env var
- **WHEN** the CLI receives `--solution '${workspaceFolder}/My.slnx'` and
  the MCP client did not expand the token, but `MCP_WORKSPACE_FOLDER=/abs`
  is in the environment
- **THEN** the resolved `SolutionPath` is `/abs/My.slnx`

#### Scenario: Expand a generic env var
- **WHEN** the CLI receives `--solution '${HOME}/repo/My.slnx'`
- **THEN** the resolved `SolutionPath` substitutes `$HOME` from the
  environment

#### Scenario: Reject unresolved placeholders
- **WHEN** the CLI receives a value containing a placeholder that resolves
  to nothing (e.g. `${nope}/foo.sln`)
- **THEN** parsing fails with an `ArgumentException` whose message names
  the offending flag, prints the helpful "use ${workspaceFolder} or set
  MCP_WORKSPACE_FOLDER" guidance, and the process exits with code `2`

### Requirement: Sensible default DB path resolution
`ResolvedDbPath` SHALL pick the database location with a deterministic
priority: `--db` if given, then `<solution-dir>/.sourcegraph/graph.db` if
`--solution` is given, then a per-user cache directory
(`$XDG_CACHE_HOME` / `%LOCALAPPDATA%` / `~/.cache`), then `$TMPDIR`.

#### Scenario: Per-solution DB beside the .slnx
- **WHEN** the CLI is given `--solution /work/My.slnx` and no `--db`
- **THEN** the resolved DB path is
  `/work/.sourcegraph/graph.db` and the directory is created if missing

#### Scenario: Per-user fallback when no solution
- **WHEN** the CLI is invoked from CWD `/` (e.g. by an MCP host) without
  `--solution` or `--db`
- **THEN** the DB lands at the per-user cache path; CWD is never used

### Requirement: Embedding-related CLI flags
The CLI SHALL accept `--model <id>` to override the embedding model, `--no-embeddings` to disable the embedding pipeline entirely, and `--no-model-download` to disable the auto-download step while still using a pre-populated cache. All three flags apply to `serve` and `index`. The `--no-model-download` flag SHALL also be settable via the `SOURCEGRAPH_NO_MODEL_DOWNLOAD` environment variable.

#### Scenario: Disable embeddings
- **WHEN** `sourcegraph-mcp serve --solution <sln> --no-embeddings` is invoked
- **THEN** no per-scope embeddings drain is started, the model is not downloaded, and `semantic_search` returns the disabled-message

#### Scenario: Override model
- **WHEN** the user passes `--model nomic-ai/CodeRankEmbed`
- **THEN** the server resolves and (if needed) downloads that model best-effort (no SHA-256 verification, atomic rename still in place), ignores any cached embeddings whose `model_version` is different, and re-embeds on next index

#### Scenario: Disable auto-download with empty cache
- **WHEN** the user passes `--no-model-download` and the cache directory has no `model.onnx` or `tokenizer.json`
- **THEN** no HTTP request is issued, the embedding pipeline is disabled for this session (same payload as `--no-embeddings`), and the warning text names the cache path so the operator can pre-populate it

#### Scenario: Disable auto-download with populated cache
- **WHEN** the user passes `--no-model-download` and the cache directory already contains valid `model.onnx` + `tokenizer.json`
- **THEN** the cached model is loaded and embeddings run normally; no HTTP request is issued

#### Scenario: Disable auto-download via environment variable
- **WHEN** the user starts the server with `SOURCEGRAPH_NO_MODEL_DOWNLOAD=1` and no `--no-model-download` flag
- **THEN** the server behaves identically to the `--no-model-download` flag form

### Requirement: Scope-management subcommands
The CLI SHALL accept `sourcegraph-mcp scopes list`, `sourcegraph-mcp scopes add <name> ...`, and `sourcegraph-mcp scopes remove <name>` to inspect and edit `.sourcegraph.json`.

#### Scenario: List scopes
- **WHEN** the user runs `sourcegraph-mcp scopes list` in a repo with three configured scopes
- **THEN** the command prints each scope's id, name, kind (solutions/projects/paths), isolation flag, and last-indexed timestamp

### Requirement: init-scopes scaffolder
The CLI SHALL accept `sourcegraph-mcp init-scopes` that discovers .slnx files at the repo root and writes a `.sourcegraph.json` listing one scope per discovered solution.

#### Scenario: Bootstrap from siblings
- **WHEN** the user runs `init-scopes` in a repo containing `frontend.slnx` and `backend.slnx` at the root
- **THEN** `.sourcegraph.json` is written with two scopes (`frontend`, `backend`), each pointing at its solution, and no `default_scope` is set

### Requirement: vocabulary subcommand
The CLI SHALL accept a `vocabulary` top-level subcommand that exposes diagnostic information about the soft-registry kind vocabulary. At v1 the only nested verb is `list`, which is also the default if the user invokes `vocabulary` with no nested verb. Future revisions may add `add` / `validate` / `import` if a strict registry is introduced.

#### Scenario: Run vocabulary list with default options
- **WHEN** `sourcegraph-mcp vocabulary list` is invoked from a repo root with at least one configured scope
- **THEN** the command exits `0`, prints one section per scope to stdout, and each section enumerates the scope's `edge_kinds`, `symbol_kinds`, and `annotation_flavors` arrays as observed in storage

#### Scenario: Vocabulary subcommand defaults to list
- **WHEN** `sourcegraph-mcp vocabulary` is invoked with no nested verb
- **THEN** the command behaves identically to `sourcegraph-mcp vocabulary list` (the only verb at v1 is the default)

#### Scenario: Unknown nested verb errors out
- **WHEN** `sourcegraph-mcp vocabulary register` (or any string other than `list`) is invoked
- **THEN** the command prints an unknown-subcommand error to stderr and exits `2`

### Requirement: vocabulary list output format
The `vocabulary list` subcommand SHALL print, for each scope known to the active `IScopeRegistry`, a header naming the scope id, then three labelled lists (`edge_kinds`, `symbol_kinds`, `annotation_flavors`). Each entry in each list SHALL be tagged with its source (`sdk` if the value matches a constant exposed by `EdgeKinds` / `SymbolKinds`; `plugin: <id>@<version>` if it matches a registered plugin's declared kinds; otherwise `unknown`) and a live emission count obtained by counting matching rows in the scope's storage (`COUNT(*) FROM edges WHERE kind_name = ?`, etc.).

#### Scenario: Single-language scope output
- **WHEN** `vocabulary list` runs against a scope whose only loaded indexer is the built-in C# Roslyn indexer with a freshly-indexed `Sample.sln`
- **THEN** every `edge_kinds` entry is tagged `[sdk, emitted: <N>]` and every `symbol_kinds` entry is tagged the same way; `annotation_flavors` is `csharp-attribute  [sdk, emitted: <N>]`

#### Scenario: Polyglot scope shows mixed sources
- **WHEN** `vocabulary list` runs against a scope that loads the built-in C# indexer and a hypothetical XAML indexer plugin with id `xaml-indexer@1.0.0`
- **THEN** SDK constants are tagged `[sdk]`, XAML-emitted kinds are tagged `[plugin: xaml-indexer@1.0.0]`, and live emission counts reflect the actual storage state for each

#### Scenario: Empty scope output
- **WHEN** `vocabulary list` runs against a scope whose storage is missing (cold scope, never indexed — the per-scope DB file does not exist on disk)
- **THEN** the section header for that scope is followed by a single `(no database at <path> — never indexed)` note, the `edge_kinds` / `symbol_kinds` / `annotation_flavors` lists are empty (no SDK fabricated `emitted: 0` rows), no error is produced, and the command continues to the next scope

### Requirement: vocabulary list drift detection
After printing the per-scope kind lists, the `vocabulary list` subcommand SHALL print a "Drift candidates" section that compares pairs of kinds within each scope using Levenshtein distance with threshold ≤2. Pairs that meet the threshold SHALL be listed in the form `<kind-a> ~ <kind-b>` so a maintainer can spot likely typos (`bind-path` vs `binds-path`).

#### Scenario: No drift detected
- **WHEN** every kind in every scope is at Levenshtein distance >2 from every other kind
- **THEN** the "Drift candidates" section header is followed by `(none)` and the command exits `0`

#### Scenario: Drift detected
- **WHEN** a scope's `edge_kinds` includes both `binds-path` and `bind-path` (Levenshtein distance 1)
- **THEN** the "Drift candidates" section lists `bind-path ~ binds-path` and the command still exits `0` by default

### Requirement: vocabulary list strict mode
The `vocabulary list` subcommand SHALL accept an optional `--strict` flag. When set, the command SHALL exit with code `2` if any drift candidate was reported (the full output is still printed first).

#### Scenario: Strict mode with no drift
- **WHEN** `vocabulary list --strict` is invoked against a scope with no drift candidates
- **THEN** the command exits `0`

#### Scenario: Strict mode with drift
- **WHEN** `vocabulary list --strict` is invoked against a scope where `bind-path` and `binds-path` both occur
- **THEN** the command prints the full output (including the drift candidate) and exits `2`

### Requirement: vocabulary list scope filter
The `vocabulary list` subcommand SHALL accept an optional `--scope <id>` flag that restricts the output to a single scope id; the default is to print every scope known to the registry.

#### Scenario: Filter to one scope
- **WHEN** `vocabulary list --scope backend` is invoked in a repo whose `.sourcegraph.json` declares scopes `backend`, `frontend`, and `vendor`
- **THEN** only the `backend` section is printed; `frontend` and `vendor` are not visited

### Requirement: Embeddings subcommand group
The CLI SHALL accept a `sourcegraph-mcp embeddings <verb>` top-level subcommand group that exposes inspection and management of the embedding model cache. At v1 the supported verbs are `status`, `pull`, `remove`, and `verify`. An unknown nested verb SHALL exit with code `2` and an error message naming the supported verbs.

#### Scenario: Default model status
- **WHEN** `sourcegraph-mcp embeddings status` is invoked with no `--model` override
- **THEN** the command prints the cache directory path, the active model id and dimension, one row per manifest file (`localName`, presence flag, size in bytes when present, computed SHA-256 when present, pinned SHA when the manifest specifies one and a `match` indicator), and the free-disk bytes on the cache volume; the exit code is `0`

#### Scenario: Explicit pull
- **WHEN** `sourcegraph-mcp embeddings pull` is invoked with no `--model` override and an empty cache
- **THEN** the command synchronously downloads the active model's manifest files into the cache directory, prints a final status snapshot identical to the `status` verb's output, and exits `0`

#### Scenario: Pull is idempotent
- **WHEN** `sourcegraph-mcp embeddings pull` is invoked against a populated cache
- **THEN** no HTTP request is issued, the existing files are left untouched, the status snapshot is printed, and the command exits `0`

#### Scenario: Remove the active model
- **WHEN** `sourcegraph-mcp embeddings remove` is invoked with no flags and the active model's cache directory is populated
- **THEN** the command deletes the active model's per-id directory under `models/`, prints `{ "modelId": "<active>", "removedDirs": [...], "freedBytes": N }` (or the equivalent prose), and exits `0`

#### Scenario: Remove all cached models
- **WHEN** `sourcegraph-mcp embeddings remove --all` is invoked
- **THEN** every per-id directory under `models/` is deleted (the `models/` parent itself is preserved), the printed report names every removed directory and the total bytes freed, and the command exits `0`

#### Scenario: Conflicting --model and --all rejected
- **WHEN** `sourcegraph-mcp embeddings remove --model jinaai/foo --all` is invoked
- **THEN** the command prints an `ArgumentException` message naming both flags, prints the `embeddings remove` usage line, and exits `2` without touching disk

#### Scenario: Verify, no pinned SHA in manifest
- **WHEN** `sourcegraph-mcp embeddings verify --model someorg/custom-model` is invoked against a populated cache for a non-default model whose manifest has no pinned SHA-256 strings (the override-model path uses a best-effort manifest)
- **THEN** the command prints the computed SHA of every cached file alongside a `(no pinned SHA — informational only)` note and exits `0`

#### Scenario: Verify, pinned SHA matches
- **WHEN** `sourcegraph-mcp embeddings verify` is invoked against a populated cache and every cached file's computed SHA matches its manifest pinned SHA
- **THEN** every row carries `match: true` and the command exits `0`

#### Scenario: Verify, pinned SHA mismatch
- **WHEN** `sourcegraph-mcp embeddings verify` is invoked against a populated cache where at least one cached file's computed SHA does not match its manifest pinned SHA
- **THEN** the affected rows carry `match: false`, the prose names the failing files, and the command exits `2`

#### Scenario: Inspect a non-active cached model
- **WHEN** the user passes `sourcegraph-mcp embeddings status --model someorg/other-model` against a cache containing both the active model and `someorg/other-model`
- **THEN** the printed status reflects the `someorg/other-model` directory only; the active model's data is not included in the report

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
- **THEN** the resulting `.mcp.json` contains both `mcpServers.other-server` (unchanged) and a new `mcpServers.sourcegraph` entry; the closing report's `Apply` phase shows a `● wrote` row (brand-coloured ok-dot, or `[x] wrote` under `--no-leaf`) for `claude-code` and notes that an existing other-server entry was preserved

#### Scenario: Init refuses to overwrite a differing existing entry without --force
- **WHEN** the user has a pre-existing `<root>/.mcp.json` containing an `mcpServers.sourcegraph` entry whose `args` differ from what we would write, and `sourcegraph-mcp init --yes --client claude-code` (without `--force`) is invoked
- **THEN** the file is left unchanged; the `Apply` phase contains a `✗ conflict — skipped` row for `claude-code` with a hanging detail line naming the file and suggesting `--diff` to inspect or `--force` to overwrite; the process exits `2`

#### Scenario: Claude Desktop is always visible in the picker
- **WHEN** `sourcegraph-mcp init` is invoked interactively with no `--claude-desktop` flag and no detected Claude Desktop config file
- **THEN** the `Clients to wire` phase renders a `claude-desktop` row marked off-default (off-dot `○`, or `[ ]` under `--no-leaf`); pressing Enter at the batched picker prompt does NOT wire Claude Desktop, and the closing report does NOT include a Claude Desktop entry

#### Scenario: Detection bumps Claude Desktop default-on
- **WHEN** `sourcegraph-mcp init --yes` is invoked on a system where `~/Library/Application Support/Claude/claude_desktop_config.json` already exists, with no explicit `--claude-desktop` flag
- **THEN** Claude Desktop's picker row is default-on; the `Apply` phase contains a `●` row for `claude-desktop` (user scope); no project-scope file is created or modified for Claude Desktop

#### Scenario: --claude-desktop forces default-on
- **WHEN** `sourcegraph-mcp init --yes --claude-desktop` is invoked on a system where no Claude Desktop config file exists
- **THEN** Claude Desktop is wired anyway (a new platform-specific user-scope config file is created and the `mcpServers.sourcegraph` entry is inserted); the `Apply` phase shows a `● wrote` row (or `[x] wrote` under `--no-leaf`) for `claude-desktop` whose path renders with `~/` substitution where applicable

#### Scenario: Pre-warm runs after writing configs
- **WHEN** `sourcegraph-mcp init --yes --client claude-code --prewarm --solution ./MySln.slnx` is invoked
- **THEN** after the `.mcp.json` write completes, `RoslynIndexer.IndexSolutionOnceAsync` is invoked against `./MySln.slnx`; the `Pre-warm` phase summary line reads `● indexed MySln.slnx in T.Ts` (or `[x] indexed MySln.slnx in T.Ts` under `--no-leaf`)

### Requirement: doctor subcommand
The CLI SHALL accept `sourcegraph-mcp doctor` that runs a read-only environment diagnostic and prints a per-check `pass | warn | fail` summary. The subcommand SHALL accept `--root <path>` and `--json` flags. The exit code SHALL follow the convention: `0` if every check passed, `2` if at least one warn was raised, `1` if any check produced a hard fail.

The diagnostic SHALL cover at minimum: `.NET SDK >= 10.0` on PATH; `git` on PATH; `--root` readable; presence and parseability of `.sourcegraph.json` (or graceful absence); embedding model cache presence and size; per-scope DB writability under `<root>/.sourcegraph/scopes/`; per-client config-file presence and whether each contains a `sourcegraph` server entry that matches what `init` would write today.

#### Scenario: Healthy environment
- **WHEN** `sourcegraph-mcp doctor` is invoked in a repo with a valid `.sourcegraph.json`, .NET 10 SDK on PATH, git on PATH, and `.mcp.json` already wired
- **THEN** every check prints `[OK]` (or `✓` on a tty), the command exits `0`, and the summary line reads `8/8 checks passed`

#### Scenario: Missing git surfaces as a warn
- **WHEN** `sourcegraph-mcp doctor` is invoked on a system where `git` is not on PATH
- **THEN** the corresponding check prints `[WARN]` with the message `git not on PATH — \`who_authored\` and \`recent_changes\` will return empty; pass --no-history to silence`, the command exits `2`, and other checks continue to run

#### Scenario: Missing .NET SDK surfaces as a fail
- **WHEN** `sourcegraph-mcp doctor` is invoked in an environment where `.NET 10` is not installed
- **THEN** the corresponding check prints `[FAIL]` with a message naming the expected version and a download URL; the command exits `1`

#### Scenario: --json output is machine-readable
- **WHEN** `sourcegraph-mcp doctor --json` is invoked
- **THEN** stdout is a single JSON document with shape `{"checks": [{"name": "...", "status": "pass|warn|fail", "message": "..."}, ...], "exit_code": <code>}` and no human-readable preamble

### Requirement: demo subcommand
The CLI SHALL accept `sourcegraph-mcp demo` that runs four canned MCP tool calls (`ping`, `graph_stats`, `search_symbols`, `find_definition`) against the active scope and prints each result's markdown — leaf prefix included — to stdout. The subcommand SHALL accept `--scope <id>`, `--root <path>`, and `--no-color` flags.

If `graph_stats` reports zero symbols (the scope was never indexed), the subcommand SHALL bail with a "no symbols indexed — run `sourcegraph-mcp index <solution>` first, or run `init --prewarm`" message and exit `2`.

#### Scenario: Demo against a freshly-indexed scope
- **WHEN** `sourcegraph-mcp demo` is invoked in a repo where the default scope has been indexed
- **THEN** four bordered sections appear on stdout — one per canned call, each labeled with its tool name — and each section's body begins with a `🌿` glyph (unless `--no-color` or `SOURCEGRAPH_NO_LEAF=1` is set, in which case the leaf is suppressed but the section borders remain)

#### Scenario: Demo bails on an empty graph
- **WHEN** `sourcegraph-mcp demo` is invoked against a scope whose DB has zero symbols
- **THEN** the `ping` and `graph_stats` sections still print; instead of attempting `search_symbols`/`find_definition`, the command prints the empty-graph guidance message and exits `2`

#### Scenario: Demo with --scope picks a specific scope
- **WHEN** `sourcegraph-mcp demo --scope frontend` is invoked in a multi-scope repo where only `frontend` is indexed
- **THEN** all four canned calls run against the `frontend` scope; the closing summary reads `demo against scope=frontend: ok`

### Requirement: init-scopes integration
The CLI SHALL preserve the existing `init-scopes` subcommand behaviour and the existing `init-scopes` requirement; `init` SHALL invoke the same scope-discovery logic internally when multiple `.slnx`/`.sln` files are detected, so an existing setup script that calls `init-scopes` continues to work unchanged.

#### Scenario: init-scopes still works standalone
- **WHEN** a user runs `sourcegraph-mcp init-scopes` in a multi-solution repo (without going through `init`)
- **THEN** `.sourcegraph.json` is written with one scope per discovered solution, exactly as before this change

#### Scenario: init delegates scope discovery to the same code path
- **WHEN** a user runs `sourcegraph-mcp init` in a repo containing `frontend.slnx` and `backend.slnx`
- **THEN** the resulting `.sourcegraph.json` is identical to the file `init-scopes` would have written, and the closing report names both `frontend` and `backend` as configured scopes

### Requirement: init output status-dot language
The `init` subcommand SHALL render its human-readable output (banner, detection summary, picker rows, apply rows, pre-warm summary, closing report) using a single status-dot vocabulary applied uniformly across every row position. The leaf `🌿` is reserved for the brand mark in the banner line and in MCP tool responses; it MUST NOT appear in any per-row status position. The five row states and their tokens SHALL be:

- **on / passed / wrote / unchanged**: `●` (U+25CF) in brand colour `#5fa07a`, followed by U+0020; ASCII fallback `[x] ` under `--no-leaf` or `SOURCEGRAPH_NO_LEAF=1`
- **off / not selected**: `○` (U+25CB); ASCII fallback `[ ] `
- **soft warning**: `◐` (U+25D0) in warn colour `#e0a040`, followed by U+0020; ASCII fallback `[!] `
- **hard skip / conflict / fail**: `✗` (U+2717) in fail colour `#d4544b`, followed by U+0020; ASCII fallback `[X] `
- **unsupported / N/A**: `−` (U+2212) in muted colour, followed by U+0020; ASCII fallback `[-] `

Column alignment SHALL be preserved across the emoji and ASCII forms by treating each token's width as three display cells.

The `init` subcommand SHALL organise its output under named phase headings: `Environment`, `Clients to wire`, `Apply`, `Pre-warm` (omitted when no pre-warm runs in this invocation), and `Next`. Each phase heading SHALL be prefixed with the section-leader glyph `◆` (U+25C6) in brand colour, followed by U+0020, then the heading name in brand-coloured bold (matching the dashboard's detail-view section headers). Under `--no-leaf` the `◆` leader downgrades to the bracketed ASCII token `[*]` (three display cells, same column width). Rows under a phase heading SHALL be indented exactly four spaces so the dot column lines up two cells inside the section leader.

The opt-out mechanism (`--no-leaf` CLI flag and `SOURCEGRAPH_NO_LEAF=1` env var) SHALL apply to the entire status-dot vocabulary, the section-leader glyph, and any banner `🌿` brand mark in `init` output — the same `LeafFormatter.Suppressed` flag that controls server tool responses.

#### Scenario: Successful first-run renders all five phases
- **WHEN** `sourcegraph-mcp init --yes` is invoked in a repo with one solution and Claude Code's project config absent, with `--prewarm` set
- **THEN** stdout contains exactly five phase headings — `◆ Environment`, `◆ Clients to wire`, `◆ Apply`, `◆ Pre-warm`, `◆ Next` — each on its own line at the left margin; rows under each heading are four-space-indented; the `Apply` phase contains a row beginning with `● wrote` for the `claude-code` writer; no `🌿` byte sequence appears in a row-status position

#### Scenario: --no-leaf substitutes ASCII fallbacks
- **WHEN** `sourcegraph-mcp init --yes --no-leaf` is invoked
- **THEN** no `🌿` (U+1F33F), `◆` (U+25C6), `●` (U+25CF), `○` (U+25CB), `◐` (U+25D0), `✗` (U+2717), or `−` (U+2212) appears on stdout; every status position contains a three-character ASCII token from the set `[x] `, `[ ] `, `[!] `, `[X] `, `[-] `; every phase heading begins with the bracketed ASCII section leader `[*] `; the banner line shows `SourceGraph init` without a leading leaf

#### Scenario: --print-only omits the Pre-warm phase
- **WHEN** `sourcegraph-mcp init --yes --print-only` is invoked
- **THEN** the `Pre-warm` phase heading does not appear on stdout; the `Apply` phase still renders with each row showing the would-write verb under the configured dot vocabulary

#### Scenario: SOURCEGRAPH_NO_LEAF env var matches --no-leaf flag behaviour
- **WHEN** `sourcegraph-mcp init --yes` is invoked with `SOURCEGRAPH_NO_LEAF=1` in the environment and without the `--no-leaf` flag
- **THEN** the rendered output is byte-identical to the `--yes --no-leaf` invocation against the same repo state

### Requirement: init batched client picker
When `init` is invoked interactively (stdin is a tty AND `--yes` was not passed AND `--print-only` was not passed), the `Clients to wire` phase SHALL render every supported client as a row showing its default-selected state with the status-dot vocabulary defined in the `init output status-dot language` requirement (`●`/`[x]` selected, `○`/`[ ]` off), then prompt the user exactly once with a batched prompt allowing one of the following responses:

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
- **WHEN** the picker shows `claude-code` and `copilot` as default-on (`●`) and `cursor`, `continue`, `claude-desktop` as default-off (`○`); user presses Enter at the prompt
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
- **THEN** the picker's `cursor` row is rendered with the `●` (or `[x]`) ok-dot; pressing Enter wires `cursor` along with `claude-code` and `copilot`

#### Scenario: Detection-driven continue default-off when no install fingerprint
- **WHEN** the user runs `sourcegraph-mcp init` interactively in a repo with no `.continue/` directory and no `~/.continue/` directory
- **THEN** the picker's `continue` row is rendered with the `○` (or `[ ]`) off-dot; pressing Enter does NOT wire `continue`

### Requirement: init --diff conflict preview
The `init` subcommand SHALL accept a `--diff` flag. When the flag is set, AND a writer's plan would land in `SkipExistingDiffers`, AND the run is not under `--print-only`, the subcommand SHALL render a unified diff with three lines of surrounding context comparing the existing target file's bytes against the writer's proposed `ContentBytes`, printed to stdout under the conflicting `Apply` row as a hanging-detail block. The diff `---` header SHALL name the target file path; the `+++` header SHALL name `<target>.proposed`.

`--diff` SHALL be read-only by default. When combined with `--force`, the diff SHALL be printed first AND the write SHALL then proceed (the existing `--force` behaviour, augmented with the diff print). Without `--force`, the file SHALL be left unchanged and the process SHALL exit `2`.

`--diff` SHALL be a no-op for plans other than `SkipExistingDiffers` (`Insert`, `NoOpAlreadyMatches`, `ReplaceOurs`, `SkipHasComments`, `SkipUnsupported`).

#### Scenario: --diff shows the unified diff and exits 2 without --force
- **WHEN** `<root>/.mcp.json` contains an `mcpServers.sourcegraph` entry whose `args` array differs from what `init` would write, and `sourcegraph-mcp init --yes --client claude-code --diff` is invoked
- **THEN** the `Apply` phase shows `✗ conflict — skipped  claude-code  .mcp.json` (or `[X] conflict — skipped  ...` under `--no-leaf`); a unified diff is printed below the row indented one level further, with `---` and `+++` headers naming `.mcp.json` and `.mcp.json.proposed` respectively, and `-`/`+` markers on the differing lines; the file is not modified; the process exits `2`

#### Scenario: --diff combined with --force prints diff then writes
- **WHEN** the same conflicting `<root>/.mcp.json` is present and `sourcegraph-mcp init --yes --client claude-code --diff --force` is invoked
- **THEN** the unified diff is printed first; then the `Apply` phase contains `● replaced  claude-code  .mcp.json` (or `[x] replaced  ...` under `--no-leaf`); the file is rewritten with the proposed content; the process exits `0`

#### Scenario: --diff on an insert plan is a no-op
- **WHEN** `<root>/.mcp.json` does not exist and `sourcegraph-mcp init --yes --client claude-code --diff` is invoked
- **THEN** the `Apply` phase shows `● wrote  claude-code  .mcp.json`; no diff output is printed; the process exits `0`

#### Scenario: --diff on a SkipHasComments plan is a no-op
- **WHEN** `<root>/.mcp.json` contains line comments (`// ...`) outside string literals and `sourcegraph-mcp init --yes --client claude-code --diff` is invoked
- **THEN** today's comment-aware degraded-mode behaviour runs unchanged: the would-write snippet is printed to stdout with the `# config has comments at ...` warning, the file is not modified, and no unified diff is produced; the process exits `0`

### Requirement: status subcommand
The CLI SHALL accept `sourcegraph-mcp status` that renders a single point-in-time snapshot of the operator console's five state surfaces — Environment, Scopes, Clients, Embeddings, Recent activity — to stdout, using the phase-headed layout and status-dot vocabulary defined elsewhere in this spec.

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
- **THEN** stdout contains exactly five `◆`-prefixed phase headings — `◆ Environment`, `◆ Scopes`, `◆ Clients`, `◆ Embeddings`, `◆ Recent activity` — each rendered with phase-internal rows using the status-dot vocabulary (`●` for healthy items); the process exits `0`

#### Scenario: A degraded scope drops the exit code to 1
- **WHEN** `sourcegraph-mcp status` is invoked in a repo where one configured scope has `status = "degraded"` in `_meta.db` and an integrity-check failure recorded
- **THEN** the `Scopes` phase renders that scope's row with the `✗` (or `[X]` under `--no-leaf`) fail-dot and a hanging-detail line naming the recommended action (`repair_scope mode=rebuild`); the process exits `1`

#### Scenario: A partial scope drops the exit code to 2
- **WHEN** `sourcegraph-mcp status` is invoked in a repo where one scope has `status = "partial"` and the `failed_projects` array is non-empty
- **THEN** the `Scopes` phase renders that scope's row with the `◐` (or `[!]`) warn-dot; the hanging-detail line lists the failed project names; the process exits `2`

#### Scenario: --json emits the stable contract document
- **WHEN** `sourcegraph-mcp status --json` is invoked
- **THEN** stdout contains a single JSON document parseable as the `DashboardSnapshot` shape documented in `status snapshot data sources`; no human-readable prose precedes or follows the JSON; the document's `exit_code` field matches the process exit code

#### Scenario: --watch refreshes in place under a tty
- **WHEN** `sourcegraph-mcp status --watch --watch-interval 1` is invoked under a tty and runs for 3 seconds before SIGINT
- **THEN** stdout contains the snapshot rendered 3 or 4 times (one initial frame + 2 or 3 refreshes); each refresh emits the ANSI cursor-home + clear-to-end sequence so the previous frame is overwritten in place; on SIGINT the process exits `0` cleanly with no stray "interrupted" message

#### Scenario: --watch downgrades to single snapshot under non-tty
- **WHEN** `sourcegraph-mcp status --watch --watch-interval 1 | cat` is invoked
- **THEN** exactly one snapshot is rendered on stdout (no ANSI cursor codes, no re-renders); the process exits with the snapshot-evaluated exit code; the `--watch-interval` value is ignored

### Requirement: status output status-dot language
The `status` subcommand SHALL use the same five-state status-dot vocabulary defined in the `init output status-dot language` requirement: `●` for on/passed/healthy in brand colour (ASCII fallback `[x] ` under `--no-leaf` or `SOURCEGRAPH_NO_LEAF=1`), `○` for off/inactive (ASCII `[ ] `), `◐` for warning in amber (ASCII `[!] `), `✗` for hard-fail in red (ASCII `[X] `), `−` for unsupported/N/A muted (ASCII `[-] `). The leaf `🌿` is reserved for the brand mark on the banner line and in MCP tool responses; it MUST NOT appear in any per-row status position. Column alignment SHALL be preserved across the emoji and ASCII forms at three display cells per token.

The `status` subcommand SHALL organise its human-readable output under named phase headings: `Environment`, `Scopes`, `Clients`, `Embeddings`, `Recent activity`. Each phase heading SHALL be prefixed with the section-leader glyph `◆` (U+25C6) in brand colour, followed by U+0020, then the heading name (matching the dashboard's detail-view section headers). Under `--no-leaf` the leader downgrades to `[*]`. Rows under a phase heading SHALL be indented exactly four spaces so the dot column lines up two cells inside the section leader.

The `--no-color` flag SHALL suppress ANSI colour codes but SHALL leave the dot-glyph vocabulary intact (emoji rendering doesn't require ANSI colour codes).

#### Scenario: --no-leaf substitutes ASCII fallbacks
- **WHEN** `sourcegraph-mcp status --no-leaf` is invoked in a healthy repo
- **THEN** no `🌿` (U+1F33F), `◆` (U+25C6), `●` (U+25CF), `○` (U+25CB), `◐` (U+25D0), `✗` (U+2717), or `−` (U+2212) byte sequence appears on stdout; every status position contains a three-character ASCII token from the set `[x] `, `[ ] `, `[!] `, `[X] `, `[-] `; every phase heading begins with the bracketed ASCII section leader `[*] `

#### Scenario: SOURCEGRAPH_NO_LEAF env var matches --no-leaf flag behaviour
- **WHEN** `sourcegraph-mcp status` is invoked with `SOURCEGRAPH_NO_LEAF=1` in the environment and without the `--no-leaf` flag
- **THEN** the rendered output is byte-identical to the `--no-leaf` invocation against the same repo state

#### Scenario: --no-color suppresses ANSI codes without affecting glyphs
- **WHEN** `sourcegraph-mcp status --no-color` is invoked under a tty
- **THEN** stdout contains no ANSI escape sequences (no `\x1b[` byte sequences); the status-dot vocabulary is unchanged (`●` still appears for healthy items unless `--no-leaf` is also set)

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

### Requirement: dashboard subcommand
The CLI SHALL accept `sourcegraph-mcp dashboard` that renders a full-screen Spectre.Console-backed live operator console consuming the `DashboardSnapshot` defined in the `status snapshot data sources` requirement. The dashboard SHALL display five sections — `Environment`, `Scopes`, `Clients`, `Embeddings`, `Recent activity` — backed by the same snapshot the `status` subcommand renders, and SHALL refresh that snapshot on a polling + watcher hybrid (see below).

The subcommand SHALL accept the following flags:

- `--root <path>` — repository root (default CWD); rendered in the dashboard header bar using the relative / `~/`-substituted form (matching `polish-init-onboarding`'s path-rendering rule).
- `--no-color` — disable ANSI colour codes; composes with the `NO_COLOR` env var Spectre honours natively.
- `--no-leaf` / `SOURCEGRAPH_NO_LEAF=1` — substitute the ASCII status-dot fallback (matches the `init output status-dot language` and `status output status-dot language` requirements above).

The dashboard SHALL refresh its snapshot using a hybrid model:

1. **Polling**: a timer rebuilds the snapshot at most once per `1000 ms`.
2. **Watcher**: `FileSystemWatcher` instances on `<root>/.sourcegraph/usage.jsonl` and `<root>/.sourcegraph/heals.jsonl` trigger a debounced rebuild `100 ms` after the most recent write event.

The two triggers SHALL coalesce: any rebuild request that fires within `1000 ms` of the previous rebuild SHALL be dropped (the most recent snapshot satisfies it). Snapshot rebuilds SHALL run on the thread-pool; rendering SHALL be confined to the UI thread.

The dashboard SHALL declare minimum terminal dimensions of `80 columns × 24 rows`. When the current terminal is smaller at startup, the dashboard SHALL print `terminal too small (need ≥80×24)` to stderr and exit with code `2`. The dashboard SHALL respond to terminal resize events by re-rendering the layout against the new dimensions.

The dashboard SHALL exit cleanly with code `0` on `q`, `Q`, or `Ctrl+C`; on uncaught exception, exit `1` after restoring the terminal cursor.

#### Scenario: Successful launch renders the five sections
- **WHEN** `sourcegraph-mcp dashboard` is invoked under a `100 × 40` terminal in a healthy repo
- **THEN** the first frame contains a Spectre layout with five labelled sections — `Environment`, `Scopes`, `Clients`, `Embeddings`, `Recent activity` — each rendered with phase-internal rows using the status-dot vocabulary; the header bar contains the dashboard banner with `🌿 SourceGraph` (or `[x] SourceGraph` under `--no-leaf`), the version, and the relative-path-rendered `--root`; the footer bar contains the key-binding hints `[q] quit  [?] help  [↑↓] nav  [Enter] details`

#### Scenario: Recent activity surfaces within 200 ms of a JSONL write
- **WHEN** the dashboard is running and a concurrent `serve` process appends one line to `usage.jsonl`
- **THEN** the `Recent activity` section re-renders to include the new entry within 200 ms of the write (100 ms watcher debounce + render latency); the snapshot's poll-tick rebuild SHALL NOT also fire for that event (the coalescing rule prevents the duplicate rebuild)

#### Scenario: Polling rebuild fires when no JSONL activity
- **WHEN** the dashboard runs for 5 seconds with no JSONL writes
- **THEN** the snapshot rebuilds at most 5 times (once per second); each rebuild's wall-clock time is recorded; no rebuild's duration exceeds 100 ms under the healthy-repo fixture

#### Scenario: Tiny terminal refuses to render
- **WHEN** `sourcegraph-mcp dashboard` is invoked under a `60 × 20` terminal
- **THEN** stderr contains the line `terminal too small (need ≥80×24)`; the process exits with code `2` without entering Spectre's `Live` mode

#### Scenario: q exits cleanly
- **WHEN** the dashboard is running and the user presses `q`
- **THEN** the Spectre `Live` block exits; the cursor is restored to visible state; no ANSI escape sequence remains in the terminal's output buffer; the process exits with code `0`

### Requirement: Bare command dispatch
The CLI SHALL accept `sourcegraph-mcp` invoked with no positional arguments and no `--help` / `-h` flag, and SHALL dispatch to either the `dashboard` subcommand or the `status` subcommand based on the stdin disposition: when stdin is a tty (`Console.IsInputRedirected == false` AND `Environment.UserInteractive == true`), the dispatch target SHALL be `dashboard`; otherwise the dispatch target SHALL be `status`.

Flags passed alongside the bare invocation (e.g. `sourcegraph-mcp --root /work/Repo`) SHALL be propagated to the dispatched subcommand.

The behaviour of `sourcegraph-mcp --help` / `sourcegraph-mcp -h` SHALL be unchanged from today: print the help text and exit `0`.

#### Scenario: Bare invocation under a tty enters the dashboard
- **WHEN** a user runs `sourcegraph-mcp` (no positional args, no `--help`) in an interactive terminal
- **THEN** the dashboard launches as if `sourcegraph-mcp dashboard` had been invoked; on `q` the process exits `0`

#### Scenario: Bare invocation under a pipe runs status
- **WHEN** `sourcegraph-mcp | cat` is invoked
- **THEN** the static `status` snapshot is printed to stdout (no ANSI control codes for live rendering); the process exits with the snapshot's exit code (0, 1, or 2); the dashboard is NOT entered

#### Scenario: --help still prints help
- **WHEN** `sourcegraph-mcp --help` is invoked (in either tty or redirected stdin)
- **THEN** stdout contains the help text starting with `sourcegraph-mcp — live code source graph MCP server for .NET`; the dashboard is NOT entered; the process exits `0`

#### Scenario: Bare invocation with --root propagates the flag
- **WHEN** `sourcegraph-mcp --root /some/other/repo` is invoked under a tty
- **THEN** the dashboard launches against `/some/other/repo` (the header bar names that root); the `--root` flag is passed through to the snapshot builder

### Requirement: Dashboard action confirmation
The `dashboard` subcommand SHALL gate every state-mutating action that is irreversible or that affects user-visible state outside `<root>/.sourcegraph/` behind a Spectre confirmation prompt. The prompt SHALL render as a modal overlay with the text `<verb> <target>? [y/N]`, default `No`, dismissable with `Esc` or `n` (both treated as `No`).

Specifically, the following in-place actions SHALL gate behind the confirm modal:

- `[R]` rebuild selected scope (archives the current scope DB before re-indexing).
- `[u]` unwire selected client (removes the `mcpServers.sourcegraph` entry from the target config file).

The following in-place actions SHALL NOT gate (they are idempotent / additive):

- `[r]` reindex selected scope (`reconcile_drift`).
- `[w]` wire missing client (calls the InitCli writer; results in `Insert` or `NoOpAlreadyMatches`).
- `[p]` embeddings pull (idempotent against a populated cache).
- `[v]` embeddings verify (read-only).

Read-only actions (`↑↓`, `Tab`, `Enter`, `Esc`, `q`, `?`, `s`) SHALL NOT gate.

Guided actions (`[i]`, `[d]`, `[l]`, `[e]`) SHALL NOT gate at the dashboard layer — the guided subcommand carries its own interaction model.

#### Scenario: `[u]` unwire prompts for confirmation
- **WHEN** the user navigates to a wired client row, presses `u`, and the confirm prompt appears
- **THEN** the prompt renders with the text `unwire claude-code? [y/N]` (or equivalent for the selected client); pressing `Esc` dismisses the prompt with no file modification; the `.mcp.json` file's mtime is unchanged; the dashboard returns to the Clients section with no error

#### Scenario: `[u]` unwire proceeds on explicit yes
- **WHEN** the user presses `u` on a wired `claude-code` row and answers `y` to the confirm prompt
- **THEN** the `mcpServers.sourcegraph` entry is removed from `<root>/.mcp.json` (other entries preserved); the snapshot rebuilds; the Clients section's `claude-code` row status-dot flips from `●` to `○` (or `[x]` to `[ ]` under `--no-leaf`)

#### Scenario: `[r]` reindex does NOT prompt
- **WHEN** the user navigates to a scope row and presses `r`
- **THEN** `reconcile_drift` runs immediately for the selected scope; no confirm prompt appears; the scope's status dot optionally flips to a transient `indexing` indicator while the operation runs; on completion the row re-renders with the updated state

#### Scenario: `[R]` rebuild prompts for confirmation
- **WHEN** the user presses `R` on a scope row
- **THEN** the confirm prompt renders with the text `rebuild backend? [y/N]` (or the slug of the selected scope); `n` or `Esc` dismisses with no action; `y` triggers `repair_scope mode=rebuild` for the selected scope

#### Scenario: `[v]` embeddings verify does NOT prompt
- **WHEN** the user presses `v`
- **THEN** `embeddings verify` runs against the active model immediately; no confirm prompt appears; the Embeddings section's `verified` indicator updates on completion (or surfaces a `◐`/`✗` status dot if a SHA mismatch is detected)

