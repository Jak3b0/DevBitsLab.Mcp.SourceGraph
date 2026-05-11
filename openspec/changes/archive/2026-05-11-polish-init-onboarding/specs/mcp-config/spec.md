## MODIFIED Requirements

### Requirement: Project-scoped defaults; user-scope opt-in
The `init` subcommand SHALL default each client's write target to that client's project-scoped path when one exists. Writing to a user-scoped path SHALL require explicit per-client opt-in via one of:

1. The `--user-<client>` flag (for clients that have both project- and user-scope paths).
2. A `--client <id>` selection naming a client whose only target is user-scope (today: `claude-desktop`).
3. Interactive picker confirmation: the row for a user-scope-only client SHALL be visible by default and SHALL be selected for the run (either by being default-on under detection OR by being added via `+slug` in the batched prompt).

Claude Desktop SHALL remain user-scope only (no project-scope path exists). The picker row for Claude Desktop SHALL be visible by default in interactive mode. Claude Desktop's default-on state in the picker SHALL be driven by detection of its platform-specific config file (`%APPDATA%\Claude\claude_desktop_config.json` on Windows, `~/Library/Application Support/Claude/claude_desktop_config.json` on macOS, `~/.config/Claude/claude_desktop_config.json` on Linux): default-on iff the file exists, default-off otherwise. The `--claude-desktop` CLI flag SHALL force the default-on state regardless of detection.

#### Scenario: Default init in a fresh repo skips Claude Desktop
- **WHEN** `sourcegraph-mcp init --yes` is invoked with no `--user-*` flags, no `--claude-desktop` flag, no `--client` flag, and no detected Claude Desktop config file on the platform-specific path, in a repo with a `.slnx` and Claude Code installed
- **THEN** `<root>/.mcp.json` is written; no file under the user's home directory is read or written; Claude Desktop is not wired

#### Scenario: Detected Claude Desktop config defaults the picker on
- **WHEN** `sourcegraph-mcp init --yes` is invoked on a system where `~/Library/Application Support/Claude/claude_desktop_config.json` already exists (macOS path), with no explicit `--claude-desktop` flag
- **THEN** the Claude Desktop user-scope file is written or merged into (preserving any non-`sourcegraph` server entries already present); the closing report's `Apply` phase shows a `🌿` row for `claude-desktop` and names the user-scope path

#### Scenario: --user-cursor writes to home
- **WHEN** `sourcegraph-mcp init --yes --client cursor --user-cursor` is invoked
- **THEN** `~/.cursor/mcp.json` is written or merged into; `<root>/.cursor/mcp.json` is not touched; the closing report names the home-tree path explicitly

#### Scenario: --claude-desktop forces wiring without a detected file
- **WHEN** `sourcegraph-mcp init --yes --claude-desktop` is invoked on a system where the platform-specific Claude Desktop config file does not exist
- **THEN** the user-scope Claude Desktop file is created at the platform-specific path; a new `mcpServers.sourcegraph` entry is inserted; the closing report names the created user-scope path
