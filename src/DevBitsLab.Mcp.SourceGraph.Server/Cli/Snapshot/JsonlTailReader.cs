using System.Text;
using System.Text.Json;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;

/// <summary>
/// Tails the last N bytes of a JSONL log and parses each complete line into a
/// <see cref="JsonElement"/>. Designed for <c>usage.jsonl</c> + <c>heals.jsonl</c> tails
/// consumed by <see cref="SnapshotBuilder"/> — a partial first line (whose start byte fell
/// outside the tail window) is dropped, as is a trailing partial line (no <c>\n</c> at EOF,
/// i.e. a concurrent writer is still flushing).
/// </summary>
internal static class JsonlTailReader
{
    /// <summary>
    /// Open <paramref name="path"/>, seek to <c>max(0, length - bytesFromEnd)</c>, drop the
    /// partial first line (when the seek didn't land at offset 0), parse each remaining
    /// complete line, and drop a trailing line missing its terminator. Returns an empty list
    /// when the file doesn't exist or can't be opened. Best-effort — any IO/parse error during
    /// a single line is swallowed so a corrupt or in-flight write doesn't poison the rest.
    /// </summary>
    public static IReadOnlyList<JsonElement> TailLines(string path, int bytesFromEnd)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<JsonElement>();
        if (bytesFromEnd <= 0) return Array.Empty<JsonElement>();

        byte[] buffer;
        bool startsAtFileBeginning;
        bool endsAtNewline;
        try
        {
            // Open with FileShare.ReadWrite so a concurrent writer doesn't lock us out.
            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            if (length == 0) return Array.Empty<JsonElement>();

            var offset = Math.Max(0, length - bytesFromEnd);
            startsAtFileBeginning = offset == 0;
            fs.Seek(offset, SeekOrigin.Begin);
            var size = (int)(length - offset);
            buffer = new byte[size];
            var totalRead = 0;
            while (totalRead < size)
            {
                var n = fs.Read(buffer, totalRead, size - totalRead);
                if (n <= 0) break;
                totalRead += n;
            }
            if (totalRead < size)
            {
                // Short read — only keep what we got. Truncating preserves the "drop the
                // trailing partial line" invariant downstream.
                Array.Resize(ref buffer, totalRead);
            }
            endsAtNewline = buffer.Length > 0 && buffer[^1] == (byte)'\n';
        }
        catch (IOException) { return Array.Empty<JsonElement>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<JsonElement>(); }

        var text = Encoding.UTF8.GetString(buffer);
        // Split on '\n'; the last element will be empty when the buffer ended with '\n'.
        var lines = text.Split('\n');

        // Drop the partial first line when we seeked into the middle of the file.
        var first = startsAtFileBeginning ? 0 : 1;
        // Drop the trailing element if it's a partial (mid-write) line. When the buffer ends
        // with '\n', Split puts an empty string at the tail — that's harmless to skip too.
        var last = endsAtNewline ? lines.Length - 1 : lines.Length - 1;
        // Adjust: when endsAtNewline, the last entry is empty (between final '\n' and EOF).
        // When !endsAtNewline, the last entry is a partial mid-write line that must be skipped.
        // Both cases collapse to "skip lines[Length - 1]" by setting `last = lines.Length - 1`.
        var result = new List<JsonElement>(Math.Max(0, last - first));
        for (var i = first; i < last; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(line);
                element = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Skip a single bad line — corruption from a torn write or future schema row
                // shouldn't blow up the whole tail.
                continue;
            }
            result.Add(element);
        }
        return result;
    }
}
