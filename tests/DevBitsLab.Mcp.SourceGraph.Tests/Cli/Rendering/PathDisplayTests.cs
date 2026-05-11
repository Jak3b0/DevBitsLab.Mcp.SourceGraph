using DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Rendering;

/// <summary>
/// Covers <see cref="PathDisplay"/>: repo-relative form preferred when inside <c>--root</c>;
/// <c>~/</c> substitution when inside the home dir; absolute fall-through otherwise; and the
/// tie-break (under both → repo-relative wins).
/// </summary>
public sealed class PathDisplayTests
{
    [Fact]
    public void Render_underRoot_returnsRelative()
    {
        var root = Path.Join(Path.GetTempPath(), "myrepo");
        var inside = Path.Join(root, ".mcp.json");
        var rendered = PathDisplay.Render(inside, root, homePath: "/home/test");
        rendered.Should().Be(".mcp.json");
    }

    [Fact]
    public void Render_underRootNested_returnsRelative()
    {
        var root = Path.Join(Path.GetTempPath(), "myrepo");
        var inside = Path.Join(root, ".vscode", "mcp.json");
        var rendered = PathDisplay.Render(inside, root, homePath: "/home/test");
        rendered.Should().Be(Path.Join(".vscode", "mcp.json"));
    }

    [Fact]
    public void Render_underHome_returnsTildePrefixed()
    {
        var home = Path.Join(Path.GetTempPath(), "fake-home");
        var inHome = Path.Join(home, ".cursor", "mcp.json");
        // root is far away — not under it.
        var root = Path.Join(Path.GetTempPath(), "other-repo");
        var rendered = PathDisplay.Render(inHome, root, home);
        rendered.Should().StartWith("~" + Path.DirectorySeparatorChar);
        rendered.Should().EndWith(Path.Join(".cursor", "mcp.json"));
    }

    [Fact]
    public void Render_neitherRootNorHome_returnsAbsolute()
    {
        var path = "/var/log/something.log";
        var root = "/home/test/repo";
        var home = "/home/test";
        var rendered = PathDisplay.Render(path, root, home);
        rendered.Should().Be(path);
    }

    [Fact]
    public void Render_underBothRootAndHome_prefersRoot()
    {
        // A pathological setup where root is itself under home — repo-relative should win.
        var home = Path.Join(Path.GetTempPath(), "fake-home2");
        var root = Path.Join(home, "myrepo");
        var inside = Path.Join(root, ".mcp.json");
        var rendered = PathDisplay.Render(inside, root, home);
        rendered.Should().Be(".mcp.json", "repo-relative is preferred over ~/-relative when both apply");
    }

    [Fact]
    public void Render_emptyPath_passthrough()
    {
        var rendered = PathDisplay.Render("", "/root", "/home");
        rendered.Should().Be("");
    }

    [Fact]
    public void Render_homeNull_fallsThroughToAbsolute()
    {
        var path = "/var/log/test.log";
        var rendered = PathDisplay.Render(path, "/some/other/root", homePath: null);
        rendered.Should().Be(path);
    }
}
