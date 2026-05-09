namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Prepends the source-graph brand mark (🌿 + space) to the first line of every built-in MCP
/// tool's response text before the result is shipped to the MCP client. The mark is the agent's
/// (and reading human's) at-a-glance signal that "this answer came from the live code graph."
/// Suppressed by the <c>--no-leaf</c> CLI flag or <c>SOURCEGRAPH_NO_LEAF=1</c> env var; when
/// <see cref="Suppressed"/> is true, <see cref="Brand"/> is a zero-overhead pass-through.
/// </summary>
public static class LeafFormatter
{
    public const string Mark = "🌿 ";
    public const string EnvVarName = "SOURCEGRAPH_NO_LEAF";

    /// <summary>
    /// Set once at process start by <c>Program.cs</c> from <c>--no-leaf</c> /
    /// <c>SOURCEGRAPH_NO_LEAF</c>. Not intended to flip mid-session.
    /// </summary>
    public static bool Suppressed { get; set; }

    public static string Brand(string toolResult)
    {
        if (Suppressed) return toolResult;
        if (string.IsNullOrEmpty(toolResult)) return toolResult;
        // Idempotency: a tool whose body emits its own leaf must not end up double-stamped.
        if (toolResult.StartsWith(Mark, StringComparison.Ordinal)) return toolResult;
        return Mark + toolResult;
    }
}
