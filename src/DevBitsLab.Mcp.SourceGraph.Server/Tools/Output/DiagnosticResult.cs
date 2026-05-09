using ModelContextProtocol.Protocol;

namespace DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;

/// <summary>
/// Single source of truth for the short-circuit diagnostic shape every converted MCP tool
/// returns when it can't produce a structured result — symbol not found, unknown edge kind,
/// disabled subsystem (e.g. <c>--no-history</c>), etc. Wraps the message in a
/// <see cref="CallToolResult"/> whose only content item is a single <see cref="TextContentBlock"/>,
/// so the leaf chokepoint (which brand-marks the first user-visible text block) still attaches the
/// 🌿 prefix and the agent reads the prose.
///
/// No <see cref="CallToolResult.StructuredContent"/> is set on the diagnostic path: the spec's
/// "Empty result populates structured content" scenario applies to *result* emptiness (the query
/// ran and produced zero rows) — diagnostic short-circuits represent input or environment
/// errors, not zero-row results, so they ship as plain prose only.
/// </summary>
public static class DiagnosticResult
{
    /// <summary>
    /// Build a single-text <see cref="CallToolResult"/> carrying <paramref name="message"/>.
    /// Caller-side conventions:
    /// <list type="bullet">
    ///   <item>Use for symbol-not-found / unknown-flag short-circuits inside tool bodies that
    ///     return <c>Task&lt;CallToolResult&gt;</c>.</item>
    ///   <item>Don't use for zero-row results — those should still ship the structured shape
    ///     (e.g. <c>{"hits": []}</c>) so consumers can branch on collection length rather than
    ///     parsing prose.</item>
    /// </list>
    /// </summary>
    public static CallToolResult Build(string message) =>
        new()
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
        };
}
