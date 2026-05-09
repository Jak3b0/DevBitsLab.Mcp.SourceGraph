## ADDED Requirements

### Requirement: Multi-content tool responses
Every built-in MCP tool's response SHALL be representable as an ordered list of `ContentBlock` items rather than a single concatenated text blob. The list MAY include `TextContentBlock`, `ResourceLinkBlock`, and other protocol-defined content types in any order. The wire-level encoding (`CallToolResult.content`) follows the MCP spec verbatim — clients that recognise the richer block types render them; clients that don't fall back to rendering only `TextContentBlock` items.

The first `TextContentBlock` in the list SHALL carry the substantive prose summary that the brand-mark chokepoint prefixes with `🌿 ` (when leaf suppression is not active). Trailing audience-restricted blocks SHALL NOT receive the brand mark.

#### Scenario: Tool returns a list of content blocks
- **WHEN** an MCP client invokes `find_references(symbol = "X")` and the server has matching results
- **THEN** the response's `content` array contains one leading `TextContentBlock` with the prose summary + body, zero or more `ResourceLinkBlock` items (one per result row), and at most one trailing `TextContentBlock` with `annotations.audience = ["assistant"]` carrying agent-only metadata

#### Scenario: Brand mark applies to first text block
- **WHEN** a built-in tool returns a content list whose first item is a `TextContentBlock`
- **THEN** the shipped response's `content[0].text` starts with `🌿 ` (or the unprefixed body text when leaf suppression is active), with subsequent content items unchanged

#### Scenario: First content item is not a text block
- **WHEN** a built-in tool returns a content list whose first item is not a `TextContentBlock` (e.g. begins with a resource link)
- **THEN** the chokepoint emits the list unchanged — the leaf is not stamped on a non-text item; if no `TextContentBlock` is present anywhere in the list, no leaf is added

#### Scenario: Older clients ignore unfamiliar block types
- **WHEN** an MCP client that doesn't recognise `resource_link` content blocks reads a response from a built-in tool that emitted them
- **THEN** the client renders only the `TextContentBlock` items and skips the unrecognised ones; the user sees a complete prose answer because the prose is self-sufficient

### Requirement: Structured content output
Every built-in tool whose result is naturally typed (a list of hits, a typed singleton record, a counts summary) SHALL ship its result as both renderable `content` and a typed `structuredContent` object. The tool's MCP catalog entry SHALL declare an `outputSchema` matching the structured-content shape, with the top-level schema being `{"type":"object", ...}` (the MCP SDK rejects non-object root schemas at registration time).

`structuredContent` payloads SHALL use named DTO types — never anonymous types — because the MCP SDK serialises them through a source-generated JSON context that does not know how to write anonymous types.

The pair (`content`, `structuredContent`) SHALL describe the same result. The number of items in any structured array SHALL equal the number of corresponding rows in the rendered prose.

#### Scenario: find_definition publishes structured hits
- **WHEN** the agent invokes `find_definition(symbol = "Calculator")` and the graph returns 3 hits
- **THEN** the response's `structuredContent` is a `{"hits": [...]}` object whose `hits` array has 3 typed entries with at least the fields `fqn`, `kind`, `filePath`, `line`, `column`, `signature`, `xmlSummary`; and the rendered prose lists the same 3 hits in the same order

#### Scenario: Output schema declared at tools/list time
- **WHEN** an MCP client calls `tools/list`
- **THEN** every tool that ships `structuredContent` carries an `outputSchema` field with `{"type":"object", "properties": ...}` matching the tool's structured-content payload

#### Scenario: Empty result populates structured content
- **WHEN** a tool that ships structured output returns no rows (e.g. `find_definition(symbol = "Nonexistent")`)
- **THEN** the response's `structuredContent` is the typed object with an empty array (e.g. `{"hits": []}`), not omitted; the prose carries the existing "No matches for 'X'." line

#### Scenario: Anonymous types rejected at request time
- **WHEN** a tool body assigns a compiler-generated anonymous type to `CallToolResult.StructuredContent` (or to `Meta`)
- **THEN** the chokepoint throws `InvalidOperationException` before the response is shipped, with a message identifying the offending tool and field, so the failure is diagnosable instead of opaque

### Requirement: Resource-link content items
Tools whose result rows correspond to individual symbols or files SHALL emit a `ResourceLinkBlock` per row alongside the rendered prose. Each `ResourceLinkBlock` SHALL carry a URI in the project's defined `graph://` scheme — `graph://symbol/<id>` for symbols, `graph://file/<path>` for files — pointing at a resource the project's `Resources/GraphResources.cs` subsystem can serve.

URIs SHALL be constructed via the centralised `GraphResourceUris` helper so the URI shape stays consistent between tools and resource handlers. Tools SHALL emit links only for entities they have just queried out of the graph; speculative or synthesised URIs are not allowed.

#### Scenario: find_references emits a link per reference row
- **WHEN** `find_references(symbol = "X")` returns 5 reference rows
- **THEN** the response's `content` includes 5 `ResourceLinkBlock` items, each with `uri = "graph://symbol/<id>"` matching the reference's symbol id, plus `name`, `description`, and `mimeType` populated for renderer cards

#### Scenario: Tool-emitted resource links resolve via the resource handler
- **WHEN** an MCP client follows a `ResourceLinkBlock.uri` from a tool response by calling `resources/read` against that URI
- **THEN** the resource handler in `Resources/GraphResources.cs` returns the typed resource card without "URI not found" — every emitted URI is dereferenceable

#### Scenario: Centralised URI helper
- **WHEN** any built-in tool needs to emit a graph resource URI
- **THEN** the URI is constructed via `GraphResourceUris.Symbol(id)` or `GraphResourceUris.File(path)` (not by hand-formatted string interpolation), so the URI shape stays consistent across all tools and any future change to the URI scheme lands in one place

### Requirement: Audience-restricted metadata content blocks
Tools MAY emit a trailing `TextContentBlock` carrying agent-only metadata — resolved scope, latency, edge-kind defaults, "X of N rows omitted due to limit" notices, cache hit info — with `annotations.audience = ["assistant"]` and `annotations.priority` set to a low value (typically 0.2). Such blocks reach the connected model but SHALL NOT be rendered to the human user by clients that respect the `audience` annotation.

The brand mark SHALL NOT be stamped on audience-restricted blocks. Multiple audience-restricted blocks per response are allowed but discouraged for compactness.

#### Scenario: Tool ships scope and latency metadata to the model
- **WHEN** a built-in tool runs to completion and produces metadata about scope resolution, query timing, or row truncation
- **THEN** that metadata may be emitted as a `TextContentBlock` whose `annotations.audience` array equals `["assistant"]`; the model receives the block in its tool-result payload, but a client honoring the `audience` annotation does not render the block to the human user

#### Scenario: Audience-restricted content is not brand-marked
- **WHEN** a tool's content list contains an audience-restricted `TextContentBlock`
- **THEN** the chokepoint does NOT prepend `🌿 ` to that block; the brand mark applies only to the first user-visible `TextContentBlock`

## MODIFIED Requirements

### Requirement: Tool response brand mark
The server SHALL prefix the first `TextContentBlock` of every built-in MCP tool's response with the green-leaf glyph `🌿` (U+1F33F) followed by a single space character (U+0020), before the response is shipped to the MCP client. The prefix SHALL apply uniformly to success responses, empty-result responses, and any error-string responses (i.e. any response a tool body returns through a content list whose first item is a `TextContentBlock`). When a tool returns a single-string body (legacy path, plus `PingTool` and any plugin-style return), the prefix applies to that string verbatim. When a tool returns a content list whose first item is not a `TextContentBlock`, no prefix is applied. When the leaf chokepoint is suppressed (`--no-leaf` / `SOURCEGRAPH_NO_LEAF=1`), no prefix is applied regardless of return type. Plugin-registered tools (registered via `IToolRegistry.AddTool`) SHALL NOT receive the brand-mark prefix.

#### Scenario: Built-in tool response leads with the leaf
- **WHEN** an MCP client invokes `find_definition(symbol = "Calculator")` against an indexed solution that contains `Sample.Domain.Calculator`
- **THEN** the response's first `TextContentBlock.text` starts with the byte sequence `🌿 ` (U+1F33F followed by U+0020), and the existing markdown content follows on the same line

#### Scenario: Empty-result response also leads with the leaf
- **WHEN** an MCP client invokes `find_definition(symbol = "Nonexistent")` against a graph with no matches
- **THEN** the response's first `TextContentBlock.text` starts with `🌿 ` followed by the no-match message (e.g. `🌿 No matches for 'Nonexistent'.`)

#### Scenario: Plugin tool response is not brand-marked
- **WHEN** an MCP client invokes a tool registered through `IToolRegistry.AddTool` (e.g. a plugin-supplied `xaml.find_view`) and the plugin's handler returns a string or content list
- **THEN** the response is shipped verbatim, with no leading `🌿 ` on any block

#### Scenario: Brand-mark prefix is idempotent on text bodies
- **WHEN** a built-in tool's body returns a string (or first text block) whose first characters are already `🌿 ` (e.g. due to internal pre-stamping)
- **THEN** the shipped response contains exactly one `🌿 ` prefix, not two stacked leaves

#### Scenario: Audience-restricted block is not brand-marked
- **WHEN** a tool's content list contains a `TextContentBlock` with `annotations.audience = ["assistant"]`
- **THEN** the chokepoint never stamps the brand mark on that block; the prefix applies only to the first user-visible (non-audience-restricted) `TextContentBlock`

#### Scenario: First block is not text
- **WHEN** a tool returns a content list whose first item is a `ResourceLinkBlock` or other non-text block
- **THEN** no `🌿 ` prefix is applied; if a later `TextContentBlock` exists, it is also not prefixed (the leaf attaches to the FIRST text block as-encountered, but only if it is the first item or first non-resource-link item — chokepoint behaviour is documented in `LeafFormatter.BrandFirstText`)
