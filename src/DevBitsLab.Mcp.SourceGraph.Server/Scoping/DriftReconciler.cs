using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Storage;

namespace DevBitsLab.Mcp.SourceGraph.Server.Scoping;

/// <summary>
/// Drift-reconciliation logic. Walks a scope's source tree, compares each file's on-disk SHA-256
/// to the DB's stored value, and applies the symmetric difference (reindex changed + index added
/// + remove vanished). Pulled out as a standalone helper so the comparison logic is unit-testable
/// without standing up a real <see cref="LiveIndexService"/>.
/// </summary>
internal static class DriftReconciler
{
    /// <summary>
    /// Compute the diff between disk and the per-scope DB. Returns the four sets and the total
    /// scanned + a partial flag (true when the walk hit <paramref name="maxFiles"/>).
    /// </summary>
    public static async Task<DriftDiff> ComputeAsync(
        ScopeHost host,
        int maxFiles,
        CancellationToken ct)
    {
        // Walk the source tree under the scope's root. SourceTreeWalker uses the same exclusion
        // rules as SolutionWatcher (obj/, bin/, .git/, .sourcegraph/) so the file set matches.
        var disk = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var partial = false;
        var scanned = 0;
        await foreach (var entry in SourceTreeWalker.WalkAsync(host.Scope.Root, maxFiles, ct).ConfigureAwait(false))
        {
            disk[entry.Path] = entry.Sha256;
            scanned++;
            if (scanned >= maxFiles) partial = true;
        }

        // Read every (path, sha) row from the DB. Comparing case-insensitive paths to mirror
        // SqliteGraphStore's path-matching elsewhere.
        var dbRows = await host.Store.GetAllFileShasAsync(ct).ConfigureAwait(false);
        var db = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dbRows) db[row.Path] = row.ContentSha256;

        var changed = new List<string>();
        var added = new List<string>();
        var removed = new List<string>();
        var unchanged = 0;

        foreach (var (path, sha) in disk)
        {
            if (db.TryGetValue(path, out var dbSha))
            {
                if (ByteArrayEquals(sha, dbSha)) unchanged++;
                else changed.Add(path);
            }
            else
            {
                added.Add(path);
            }
        }
        foreach (var path in db.Keys)
        {
            if (!disk.ContainsKey(path)) removed.Add(path);
        }

        return new DriftDiff(scanned, changed, added, removed, unchanged, partial);
    }

    /// <summary>
    /// Apply <paramref name="diff"/> by feeding the union of changed + added + removed paths to
    /// <see cref="RoslynIndexer.IndexChangedFilesAsync"/> — the same path the watcher uses for
    /// incremental updates. The indexer handles each kind correctly internally: changed files are
    /// re-indexed, removed files (File.Exists == false) trigger the per-file delete path, added
    /// files (if part of the workspace) are indexed.
    /// </summary>
    public static async Task ApplyAsync(ScopeHost host, DriftDiff diff, CancellationToken ct)
    {
        var union = new List<string>(diff.Changed.Count + diff.Added.Count + diff.Removed.Count);
        union.AddRange(diff.Changed);
        union.AddRange(diff.Added);
        union.AddRange(diff.Removed);
        if (union.Count == 0) return;
        await host.Indexer.IndexChangedFilesAsync(union, ct).ConfigureAwait(false);
    }

    private static bool ByteArrayEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>
/// Output of <see cref="DriftReconciler.ComputeAsync"/>. <see cref="Scanned"/> is the total file
/// count from the on-disk walk; <see cref="Partial"/> is true when the walk hit max_files.
/// </summary>
internal sealed record DriftDiff(
    int Scanned,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    int Unchanged,
    bool Partial);
