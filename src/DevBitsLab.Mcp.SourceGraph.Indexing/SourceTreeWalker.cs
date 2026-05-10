using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace DevBitsLab.Mcp.SourceGraph.Indexing;

/// <summary>
/// Walks a directory tree and yields <c>(absolutePath, content_sha256)</c> tuples for every
/// non-ignored file under it. Used by the <c>reconcile_drift</c> tool to compute a comparison
/// set against the per-scope <c>files</c> table without re-implementing path filtering.
///
/// The exclusion list mirrors <c>SolutionWatcher.ShouldIgnore</c> (<c>obj/</c>, <c>bin/</c>,
/// <c>.git/</c>, <c>.sourcegraph/</c>) so a watcher-driven scan and an agent-driven drift
/// reconcile see the same file set. Reads each file as bytes and computes SHA-256 in-process; an
/// I/O failure on a single file is swallowed (the file is silently skipped) so a permission
/// hiccup on one entry doesn't poison the whole walk.
/// </summary>
public static class SourceTreeWalker
{
    /// <summary>
    /// Walk <paramref name="root"/> recursively and yield up to <paramref name="maxFiles"/>
    /// <c>(path, sha)</c> tuples. Order is filesystem-enumeration order (typically depth-first
    /// alphabetical on most platforms; not guaranteed by the underlying API). Stops yielding
    /// after <paramref name="maxFiles"/>; the caller is expected to set <c>partial = true</c>
    /// when the cap is hit.
    /// </summary>
    public static async IAsyncEnumerable<FileShaEntry> WalkAsync(
        string root,
        int maxFiles,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) yield break;

        var yielded = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (ShouldIgnore(path)) continue;

            byte[] sha;
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                sha = SHA256.HashData(bytes);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            yield return new FileShaEntry(path, sha);
            yielded++;
            if (yielded >= maxFiles) yield break;
        }
    }

    /// <summary>
    /// Path filter mirroring <c>SolutionWatcher.ShouldIgnore</c>. Kept inline (rather than imported)
    /// so the walker doesn't take a dependency on the Watcher project.
    /// </summary>
    private static bool ShouldIgnore(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        if (path.Contains($"{sep}obj{sep}", StringComparison.Ordinal)) return true;
        if (path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)) return true;
        if (path.Contains($"{sep}.git{sep}", StringComparison.Ordinal)) return true;
        if (path.Contains($"{sep}.sourcegraph{sep}", StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>One walk entry: the absolute path and the SHA-256 of the file's bytes.</summary>
public sealed record FileShaEntry(string Path, byte[] Sha256);
