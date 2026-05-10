using System.Reflection;
using ModelContextProtocol.Server;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Single chokepoint for stamping the source-graph brand mark (🌿) onto every built-in MCP tool's
/// catalog identity (<see cref="ModelContextProtocol.Protocol.Tool.Title"/> and
/// <see cref="ModelContextProtocol.Protocol.Tool.Description"/>). Runs as a post-<c>Build()</c>
/// mutation pass parallel to <see cref="ToolDescriptionFormatter.ApplyTriggersFromAttributes"/>:
/// walks the registered <see cref="McpServerTool"/> set, filters to built-ins (declaring type
/// carries <see cref="McpServerToolTypeAttribute"/>), and writes <c>"🌿 " + Name</c> into
/// <c>Title</c> and prepends <c>"🌿 "</c> to <c>Description</c>.
///
/// Plugin tools registered via <see cref="Plugins.ToolRegistry"/> are NOT stamped — their backing
/// methods (when any) live on types that don't carry <see cref="McpServerToolTypeAttribute"/>.
///
/// Honours <see cref="LeafFormatter.Suppressed"/> — when suppression is active the pass is a
/// no-op, leaving <c>Title</c> null/unset and <c>Description</c> unbranded.
///
/// Idempotent: running the pass twice produces the same result as running once.
/// </summary>
internal static class ToolIdentityFormatter
{
    /// <summary>
    /// Apply the brand mark to every built-in tool's <c>Title</c> and <c>Description</c>. Skips
    /// plugin tools (declaring type without <see cref="McpServerToolTypeAttribute"/>) and is a
    /// no-op when <see cref="LeafFormatter.Suppressed"/> is true.
    /// </summary>
    public static void ApplyBrandMark(IEnumerable<McpServerTool> tools)
    {
        if (LeafFormatter.Suppressed) return;

        foreach (var tool in tools)
        {
            if (!IsBuiltInTool(tool)) continue;

            var protocolTool = tool.ProtocolTool;

            // Title: populate from Name when null/empty; otherwise prepend mark if missing.
            // The Name field is the snake_case identifier ("find_definition") — Title becomes
            // its branded display form ("🌿 find_definition").
            var title = protocolTool.Title;
            if (string.IsNullOrEmpty(title))
            {
                protocolTool.Title = LeafFormatter.Mark + protocolTool.Name;
            }
            else if (!title.StartsWith(LeafFormatter.Mark, StringComparison.Ordinal))
            {
                protocolTool.Title = LeafFormatter.Mark + title;
            }

            // Description: prepend mark when present and not already branded. A null/empty
            // description is left unchanged — branding with a bare glyph is pointless and would
            // paper over a tool-registration bug.
            var description = protocolTool.Description;
            if (!string.IsNullOrEmpty(description) &&
                !description.StartsWith(LeafFormatter.Mark, StringComparison.Ordinal))
            {
                protocolTool.Description = LeafFormatter.Mark + description;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="tool"/>'s backing method's declaring type carries
    /// <see cref="McpServerToolTypeAttribute"/> — the marker the SDK's
    /// <c>WithToolsFromAssembly()</c> sweep uses to identify first-party tool types. Plugin-
    /// registered tools (via <see cref="Plugins.ToolRegistry.AddTool(string, string, System.Delegate)"/>)
    /// either lack a <see cref="MethodInfo"/> in <c>Metadata</c> or have one whose declaring type
    /// is the plugin assembly's class — neither carries the attribute.
    /// </summary>
    private static bool IsBuiltInTool(McpServerTool tool)
    {
        var method = tool.Metadata?.OfType<MethodInfo>().FirstOrDefault();
        return method?.DeclaringType?.GetCustomAttribute<McpServerToolTypeAttribute>() is not null;
    }
}
