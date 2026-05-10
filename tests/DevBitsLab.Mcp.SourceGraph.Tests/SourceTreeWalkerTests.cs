using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Coverage for <see cref="SourceTreeWalker.WalkAsync"/>: discovers non-ignored files, applies the
/// shared exclusion list, computes correct SHA-256, and respects the <c>maxFiles</c> cap.
/// </summary>
public sealed class SourceTreeWalkerTests : IDisposable
{
    private readonly string _root;

    public SourceTreeWalkerTests()
    {
        _root = Path.Join(Path.GetTempPath(), "sourcegraph-walker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Walk_yields_only_nonIgnoredFiles_withCorrectSha()
    {
        // Plant 5 files: 3 valid under the root + a `obj/` subdir + a `.git/` subdir.
        var (kept1, kept1Sha) = await Plant(Path.Join(_root, "A.cs"), "class A {}");
        var (kept2, kept2Sha) = await Plant(Path.Join(_root, "src/B.cs"), "class B {}");
        var (kept3, kept3Sha) = await Plant(Path.Join(_root, "src/sub/C.cs"), "class C {}");
        await Plant(Path.Join(_root, "obj/Debug/D.cs"), "class D {}");
        await Plant(Path.Join(_root, ".git/HEAD"), "ref: refs/heads/main");

        var entries = new List<FileShaEntry>();
        await foreach (var entry in SourceTreeWalker.WalkAsync(_root, maxFiles: 100))
        {
            entries.Add(entry);
        }

        entries.Should().HaveCount(3);
        entries.Select(e => e.Path).Should().BeEquivalentTo(new[] { kept1, kept2, kept3 });
        entries.Single(e => e.Path == kept1).Sha256.Should().Equal(kept1Sha);
        entries.Single(e => e.Path == kept2).Sha256.Should().Equal(kept2Sha);
        entries.Single(e => e.Path == kept3).Sha256.Should().Equal(kept3Sha);
    }

    [Fact]
    public async Task Walk_respects_maxFiles_cap()
    {
        for (var i = 0; i < 5; i++)
        {
            await Plant(Path.Join(_root, $"F{i}.cs"), $"class F{i} {{}}");
        }
        var entries = new List<FileShaEntry>();
        await foreach (var entry in SourceTreeWalker.WalkAsync(_root, maxFiles: 2))
        {
            entries.Add(entry);
        }
        entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Walk_emptyDirectory_yieldsNothing()
    {
        var entries = new List<FileShaEntry>();
        await foreach (var entry in SourceTreeWalker.WalkAsync(_root, maxFiles: 100))
        {
            entries.Add(entry);
        }
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Walk_missingDirectory_yieldsNothing()
    {
        var entries = new List<FileShaEntry>();
        await foreach (var entry in SourceTreeWalker.WalkAsync(Path.Join(_root, "does-not-exist"), maxFiles: 100))
        {
            entries.Add(entry);
        }
        entries.Should().BeEmpty();
    }

    private static async Task<(string Path, byte[] Sha)> Plant(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(contents);
        await File.WriteAllBytesAsync(path, bytes);
        return (path, SHA256.HashData(bytes));
    }
}
