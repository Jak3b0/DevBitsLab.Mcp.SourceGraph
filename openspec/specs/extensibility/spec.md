# Extensibility

## Purpose

Open the indexing pipeline and the MCP tool surface to third-party extension
without forking the repo. Three contracts (`ILanguageIndexer`, `ICodeAnalyzer`,
`IMcpToolPlugin`) shipped via a separate NuGet SDK package let plugin authors
add new languages, custom analyzers, and prefix-namespaced MCP tools. Plugin
discovery happens via `.sourcegraph.json` `plugins[]`; per-plugin
`AssemblyLoadContext` isolation keeps failures contained and prevents
dependency conflicts.

## Requirements

### Requirement: ILanguageIndexer plugin contract
The SDK SHALL expose an `ILanguageIndexer` contract that declares the file extensions it handles and produces a stream of `IndexEvent` values for a given document.

#### Scenario: Built-in C# indexer implements the contract
- **WHEN** the server starts
- **THEN** `RoslynLanguageIndexer` is registered for `.cs` files and routes pass-1 / pass-2 work through `IndexEvent` emissions

#### Scenario: Third-party indexer registered
- **WHEN** a plugin in `.sourcegraph.json` declares `ILanguageIndexer` for `.py`
- **THEN** the server dispatches `.py` files to that plugin and ignores them in the C# indexer

### Requirement: ICodeAnalyzer plugin contract
The SDK SHALL expose an `ICodeAnalyzer` contract whose `AnalyzeAsync` is invoked once per indexed document with a context and an `IGraphEmitter`, allowing the analyzer to add symbols, references, edges, or attributes.

#### Scenario: Custom analyzer emits an Endpoint symbol
- **WHEN** an `AspNetCoreEndpointAnalyzer` runs against a document containing `app.MapGet("/api", h)`
- **THEN** it emits a synthetic `Endpoint` symbol via `IGraphEmitter` and the server persists it via the same `UpsertSymbolAsync` path used by the built-in indexer

### Requirement: IMcpToolPlugin contract
The SDK SHALL expose an `IMcpToolPlugin` contract whose `RegisterAsync` adds tools to the MCP server; tool names emitted by a plugin SHALL be prefixed with the plugin's declared `Prefix` followed by `.`.

#### Scenario: Plugin tool name prefixing
- **WHEN** a plugin with `Prefix = "mediatr"` registers `find_handlers`
- **THEN** the MCP server exposes the tool as `mediatr.find_handlers` and built-in tools (e.g. `find_definition`) keep unprefixed names

### Requirement: Plugin discovery via .sourcegraph.json
The host SHALL load plugins listed under `plugins[]` in `.sourcegraph.json`, supporting both NuGet packages (`{package, version}`) and DLL paths (`{path}`).

#### Scenario: NuGet plugin loaded
- **WHEN** `.sourcegraph.json` lists `{ "package": "DevBitsLab.Mcp.SourceGraph.Analyzers.AspNetCore", "version": "1.0.0" }`
- **THEN** the host restores it into `<repo>/.sourcegraph/plugins/restore/` and loads its assembly into a dedicated `AssemblyLoadContext`

#### Scenario: Path plugin loaded
- **WHEN** `.sourcegraph.json` lists `{ "path": ".sourcegraph/plugins/MyAnalyzer.dll" }`
- **THEN** the host loads that DLL directly into a per-plugin ALC

### Requirement: Per-plugin failure isolation
A plugin whose contract methods throw SHALL be marked `failed` in the registry; the rest of the system continues to operate, and the failure is surfaced via `plugins list`.

#### Scenario: Bad plugin doesn't crash the host
- **WHEN** an `ICodeAnalyzer.AnalyzeAsync` throws on a particular document
- **THEN** the analyzer is marked `failed` for that pass, other analyzers complete, the indexer returns success, and `plugins list` reports the failure with the exception type and message

### Requirement: Plugin status introspection
The CLI SHALL expose `plugins list` and `plugins info <name>` subcommands that report every loaded plugin's id, version, status (`loaded | failed | disabled`), implemented contracts, and registered tools / file extensions.

#### Scenario: Inspect plugins
- **WHEN** the user runs `sourcegraph-mcp plugins list` after starting a server with two plugins (one healthy, one failed)
- **THEN** the output is a table showing both plugins with their statuses and versions

### Requirement: IToolRegistry.AddTool optional trigger argument
`IToolRegistry.AddTool` SHALL accept an optional `trigger` argument alongside the existing `(toolName, description, handler)` parameters. When the argument is supplied, the host SHALL append `Use when: <trigger>` as the final paragraph of the tool's effective description before registering the tool with the underlying MCP server. When the argument is omitted (or null), the description SHALL pass through unchanged.

#### Scenario: Plugin registers a tool with a trigger
- **WHEN** a plugin's `RegisterAsync` calls `registry.AddTool("find_handlers", "Find MediatR handlers for a request type.", handler, trigger: "\"who handles MediatR request X?\"")`
- **THEN** the host's `tools/list` response includes a tool whose description ends with the line `Use when: "who handles MediatR request X?"`

#### Scenario: Plugin registers a tool without a trigger
- **WHEN** a plugin's `RegisterAsync` calls `registry.AddTool("find_handlers", "Find MediatR handlers.", handler)` (no `trigger` argument)
- **THEN** the host's `tools/list` response includes the tool whose description matches the supplied text verbatim, with no appended line

#### Scenario: Trigger argument is additive, not breaking
- **WHEN** an existing plugin compiled against the previous SDK version (without the `trigger` parameter) is loaded by the host
- **THEN** the plugin loads without recompilation and its tools register normally with no `Use when:` line appended
