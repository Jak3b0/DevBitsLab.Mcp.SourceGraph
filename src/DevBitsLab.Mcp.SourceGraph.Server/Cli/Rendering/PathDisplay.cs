namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;

/// <summary>
/// Renders an absolute path in the shortest form that's still unambiguous in a CLI context:
/// repo-relative when the path is inside <c>--root</c>, <c>~/</c>-substituted when inside the
/// user's home directory, full absolute otherwise. Inside-repo wins the tie-break when a path
/// happens to be under both roots.
/// </summary>
internal static class PathDisplay
{
    /// <summary>
    /// Returns the rendered form of <paramref name="absolute"/> against the given roots. The
    /// repo-relative form omits any leading <c>./</c>; the home form starts with <c>~/</c>; the
    /// absolute fall-through is whatever the caller passed in. Path separator is normalised to
    /// the platform's native form via <see cref="Path.GetRelativePath(string, string)"/>.
    /// </summary>
    public static string Render(string absolute, string root, string? homePath)
    {
        if (string.IsNullOrEmpty(absolute)) return absolute;

        // Repo-relative wins when the path is genuinely under root. The relative form must not
        // escape with `..`; otherwise it's not "inside" by any honest definition.
        if (!string.IsNullOrEmpty(root))
        {
            var rel = SafeRelative(root, absolute);
            if (rel is not null && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                return rel == "." ? "." : rel;
            }
        }

        if (!string.IsNullOrEmpty(homePath))
        {
            var rel = SafeRelative(homePath, absolute);
            if (rel is not null && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                return rel == "." ? "~" : "~" + Path.DirectorySeparatorChar + rel;
            }
        }

        return absolute;
    }

    private static string? SafeRelative(string baseDir, string target)
    {
        try
        {
            return Path.GetRelativePath(baseDir, target);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
