using System;
using System.IO;

namespace DevBitsLab.Mcp.SourceGraph.IntegrationTests;

/// <summary>
/// Copy <c>tests/fixtures/MultiScope/</c> into a per-test temp directory so the test can mutate
/// <c>.sourcegraph.json</c> in isolation without polluting the source-controlled fixture. Skips
/// any <c>bin</c>/<c>obj</c>/<c>.sourcegraph</c> subtrees so tests start with a clean slate.
/// Disposing returns the temp dir back to the OS.
/// </summary>
internal sealed class MultiScopeFixtureCopy : IDisposable
{
    public string Root { get; }

    private MultiScopeFixtureCopy(string root) { Root = root; }

    public static MultiScopeFixtureCopy Create()
    {
        var sourceRoot = ServerHarness.LocateFixture("MultiScope");
        var tempRoot = Path.Join(Path.GetTempPath(), "live-scope-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        CopyTree(sourceRoot, tempRoot);
        return new MultiScopeFixtureCopy(tempRoot);
    }

    public string ConfigPath => Path.Join(Root, ".sourcegraph.json");

    public string ScopeDbPath(string scopeId) =>
        Path.Join(Root, ".sourcegraph", "scopes", scopeId + ".db");

    public void WriteConfig(string json) => File.WriteAllText(ConfigPath, json);

    public void DeleteConfig() => File.Delete(ConfigPath);

    private static void CopyTree(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            if (ShouldSkip(rel)) continue;
            Directory.CreateDirectory(Path.Join(destination, rel));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (ShouldSkip(rel)) continue;
            var target = Path.Join(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool ShouldSkip(string relativePath)
    {
        var s = relativePath.Replace('\\', '/');
        return s.Contains("/bin/", StringComparison.Ordinal)
            || s.StartsWith("bin/", StringComparison.Ordinal)
            || s.Contains("/obj/", StringComparison.Ordinal)
            || s.StartsWith("obj/", StringComparison.Ordinal)
            || s.Contains("/.sourcegraph/", StringComparison.Ordinal)
            || s.StartsWith(".sourcegraph/", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
    }
}
