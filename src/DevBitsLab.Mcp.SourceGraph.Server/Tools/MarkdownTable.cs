namespace DevBitsLab.Mcp.SourceGraph.Server.Tools;

/// <summary>
/// Shared helpers for tools that emit GFM tables. The one well-known sharp edge is that a
/// literal <c>|</c> in cell content terminates the cell, and embedded newlines split the
/// row — both can occur naturally in user-controlled values like exception messages,
/// status strings, or filesystem paths.
/// </summary>
internal static class MarkdownTable
{
    /// <summary>
    /// Sanitise a value for inclusion in a GFM table cell:
    /// <list type="bullet">
    ///   <item>Escapes <c>|</c> as <c>\|</c> so it doesn't terminate the cell.</item>
    ///   <item>Collapses any <c>\r\n</c> / <c>\n</c> / <c>\r</c> into a single space so
    ///     embedded line breaks don't split the row.</item>
    ///   <item><c>null</c> / empty input returns <see cref="string.Empty"/>.</item>
    /// </list>
    /// Used by every tool whose markdown rendering interpolates user-supplied text into a
    /// table row (currently <c>list_scopes</c> and the diagnostic / lookup tables in
    /// <c>graph_stats</c> / <c>module_summary</c>).
    /// </summary>
    public static string EscapeCell(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
