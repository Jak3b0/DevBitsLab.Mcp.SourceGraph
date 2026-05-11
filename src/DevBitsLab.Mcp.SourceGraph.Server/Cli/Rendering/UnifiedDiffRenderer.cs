using System.Text;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;

/// <summary>
/// Renders a minimal unified diff between two byte sequences (existing vs. proposed file content)
/// to a <see cref="TextWriter"/>. Used by <c>init --diff</c> to preview what a writer would
/// change when its plan would otherwise land in <c>SkipExistingDiffers</c>.
///
/// Output shape (3 lines of context by default):
/// <code>
/// --- &lt;fromLabel&gt;
/// +++ &lt;toLabel&gt;
/// @@ -a,b +c,d @@
///  context-line
/// -removed-line
/// +added-line
///  context-line
/// </code>
/// </summary>
internal static class UnifiedDiffRenderer
{
    public static void Render(
        byte[] existing,
        byte[] proposed,
        string fromLabel,
        string toLabel,
        TextWriter writer,
        int contextLines = 3)
    {
        var existingText = Encoding.UTF8.GetString(existing ?? Array.Empty<byte>());
        var proposedText = Encoding.UTF8.GetString(proposed ?? Array.Empty<byte>());

        // Headers
        writer.WriteLine($"--- {fromLabel}");
        writer.WriteLine($"+++ {toLabel}");

        // Compute a line-level diff. The Differ produces per-line ops; we group adjacent changes
        // into hunks bounded by `contextLines` lines of leading/trailing context.
        var differ = new Differ();
        var chunker = new LineChunker();
        var model = differ.CreateDiffs(existingText, proposedText, false, false, chunker);

        var pieces = model.DiffBlocks;
        if (pieces.Count == 0)
        {
            // No differences — nothing more to emit. (Caller should check before invoking, but be
            // tolerant.)
            return;
        }

        var oldLines = model.PiecesOld;
        var newLines = model.PiecesNew;

        foreach (var hunk in GroupHunks(pieces.ToList(), oldLines.Length, newLines.Length, contextLines))
        {
            // Header `@@ -a,b +c,d @@` — 1-based starting line; counts are line counts.
            var oldCount = hunk.OldEnd - hunk.OldStart;
            var newCount = hunk.NewEnd - hunk.NewStart;
            writer.WriteLine($"@@ -{hunk.OldStart + 1},{oldCount} +{hunk.NewStart + 1},{newCount} @@");

            // Walk the hunk's range, emitting context / removals / additions.
            var oi = hunk.OldStart;
            var ni = hunk.NewStart;
            foreach (var block in hunk.Blocks)
            {
                // Emit any context lines that come before this block (up to block.DeleteStartA).
                while (oi < block.DeleteStartA)
                {
                    writer.WriteLine(" " + oldLines[oi]);
                    oi++;
                    ni++;
                }
                // Emit deletions.
                for (var i = 0; i < block.DeleteCountA; i++)
                {
                    writer.WriteLine("-" + oldLines[oi + i]);
                }
                // Emit insertions.
                for (var i = 0; i < block.InsertCountB; i++)
                {
                    writer.WriteLine("+" + newLines[block.InsertStartB + i]);
                }
                oi += block.DeleteCountA;
                ni += block.InsertCountB;
            }
            // Emit trailing context up to OldEnd.
            while (oi < hunk.OldEnd)
            {
                writer.WriteLine(" " + oldLines[oi]);
                oi++;
                ni++;
            }
        }
    }

    private static IReadOnlyList<Hunk> GroupHunks(
        IReadOnlyList<DiffBlock> blocks,
        int totalOld,
        int totalNew,
        int context)
    {
        var hunks = new List<Hunk>();
        if (blocks.Count == 0) return hunks;

        Hunk? current = null;
        foreach (var b in blocks)
        {
            var oldStart = Math.Max(0, b.DeleteStartA - context);
            var oldEnd = Math.Min(totalOld, b.DeleteStartA + b.DeleteCountA + context);
            var newStart = Math.Max(0, b.InsertStartB - context);
            var newEnd = Math.Min(totalNew, b.InsertStartB + b.InsertCountB + context);

            if (current is null || oldStart > current.OldEnd)
            {
                if (current is not null) hunks.Add(current);
                current = new Hunk
                {
                    OldStart = oldStart,
                    OldEnd = oldEnd,
                    NewStart = newStart,
                    NewEnd = newEnd,
                    Blocks = new List<DiffBlock> { b },
                };
            }
            else
            {
                // Merge into the existing hunk by extending the windows.
                current.OldEnd = Math.Max(current.OldEnd, oldEnd);
                current.NewEnd = Math.Max(current.NewEnd, newEnd);
                current.Blocks.Add(b);
            }
        }
        if (current is not null) hunks.Add(current);
        return hunks;
    }

    private sealed class Hunk
    {
        public int OldStart;
        public int OldEnd;
        public int NewStart;
        public int NewEnd;
        public List<DiffBlock> Blocks = new();
    }
}
