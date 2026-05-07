## ADDED Requirements

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
