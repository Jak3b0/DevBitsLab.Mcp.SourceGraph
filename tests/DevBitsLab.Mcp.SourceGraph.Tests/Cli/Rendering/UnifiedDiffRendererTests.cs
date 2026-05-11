using System.Text;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Rendering;

/// <summary>
/// Covers <see cref="UnifiedDiffRenderer"/>: header shape, simple change-line marker, no-output
/// when inputs are equal.
/// </summary>
public sealed class UnifiedDiffRendererTests
{
    [Fact]
    public void Render_emitsFromAndToHeaders()
    {
        var existing = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var proposed = Encoding.UTF8.GetBytes("line1\nline2-changed\nline3\n");
        using var sw = new StringWriter();
        UnifiedDiffRenderer.Render(existing, proposed, ".mcp.json", ".mcp.json.proposed", sw);
        var output = sw.ToString();
        output.Should().Contain("--- .mcp.json");
        output.Should().Contain("+++ .mcp.json.proposed");
    }

    [Fact]
    public void Render_emitsAdditionAndRemovalLines()
    {
        var existing = Encoding.UTF8.GetBytes("a\nb\nc\n");
        var proposed = Encoding.UTF8.GetBytes("a\nB\nc\n");
        using var sw = new StringWriter();
        UnifiedDiffRenderer.Render(existing, proposed, "from", "to", sw);
        var output = sw.ToString();
        output.Should().Contain("-b");
        output.Should().Contain("+B");
    }

    [Fact]
    public void Render_emitsHunkHeader()
    {
        var existing = Encoding.UTF8.GetBytes("a\nb\nc\nd\ne\n");
        var proposed = Encoding.UTF8.GetBytes("a\nb\nC\nd\ne\n");
        using var sw = new StringWriter();
        UnifiedDiffRenderer.Render(existing, proposed, "from", "to", sw);
        var output = sw.ToString();
        output.Should().Contain("@@");
    }

    [Fact]
    public void Render_equalInputs_emitsHeadersOnly()
    {
        // No change → no hunks. Headers still get written (the caller decides whether to bother
        // calling Render in the first place). `TextWriter.WriteLine` honours the host's line
        // terminator (`\n` on Unix, `\r\n` on Windows), so we split on both forms to keep this
        // test cross-platform — splitting on `'\n'` alone leaves a trailing `\r` on Windows that
        // the byte-exact `Be(...)` assertion catches.
        var same = Encoding.UTF8.GetBytes("hello\nworld\n");
        using var sw = new StringWriter();
        UnifiedDiffRenderer.Render(same, same, "from", "to", sw);
        var lines = sw.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(l => l.Length > 0).ToList();
        lines.Should().HaveCount(2);
        lines[0].Should().Be("--- from");
        lines[1].Should().Be("+++ to");
    }

    [Fact]
    public void Render_handlesEmptyExisting()
    {
        var proposed = Encoding.UTF8.GetBytes("new content\n");
        using var sw = new StringWriter();
        UnifiedDiffRenderer.Render(Array.Empty<byte>(), proposed, "from", "to", sw);
        var output = sw.ToString();
        output.Should().Contain("--- from");
        output.Should().Contain("+++ to");
        output.Should().Contain("+new content");
    }
}
