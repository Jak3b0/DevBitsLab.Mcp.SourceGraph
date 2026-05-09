using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DevBitsLab.Mcp.SourceGraph.Indexing.Xaml.Parser;
using DevBitsLab.Mcp.SourceGraph.Sdk;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// <see cref="ILanguageProjectFactory"/> for the in-tree XAML indexer. Walks every
/// <c>.csproj</c> under the repo root, locates the <c>.xaml</c> files belonging to each project
/// (via <c>&lt;Page&gt;</c> / <c>&lt;ApplicationDefinition&gt;</c> items, falling back to a
/// directory scan when the items aren't present), and builds a per-project resource cache from
/// <c>App.xaml</c>'s <c>Application.Resources</c>, any <c>MergedDictionaries</c>, and a theme
/// <c>Generic.xaml</c> if present in <c>Themes/</c>.
/// </summary>
public sealed class XamlLanguageProjectFactory : ILanguageProjectFactory
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> ProjectMarkers { get; } = new[] { "*.csproj", "*.xaml" };

    /// <inheritdoc />
    public Task<IReadOnlyList<ILanguageProject>> DiscoverAsync(string repoRoot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repoRoot)) throw new ArgumentException("repoRoot must be non-empty", nameof(repoRoot));
        if (!Directory.Exists(repoRoot)) return Task.FromResult<IReadOnlyList<ILanguageProject>>(Array.Empty<ILanguageProject>());

        var projects = new List<ILanguageProject>();
        IEnumerable<string> csprojFiles;
        try
        {
            csprojFiles = Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<ILanguageProject>>(Array.Empty<ILanguageProject>());
        }

        foreach (var csproj in csprojFiles)
        {
            ct.ThrowIfCancellationRequested();
            var project = TryBuildProject(csproj);
            if (project is not null) projects.Add(project);
        }

        return Task.FromResult<IReadOnlyList<ILanguageProject>>(projects);
    }

    private static XamlLanguageProject? TryBuildProject(string csprojPath)
    {
        try
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
            var xamlFiles = EnumerateXamlFiles(csprojPath, projectDir);
            if (xamlFiles.Count == 0) return null;

            var resourceCache = BuildResourceCache(xamlFiles);
            return new XamlLanguageProject(Path.GetFullPath(csprojPath), xamlFiles, resourceCache);
        }
        catch (IOException) { return null; }
        catch (XmlException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Find the <c>.xaml</c> files that belong to the project. Reads the csproj XML for explicit
    /// <c>&lt;Page&gt;</c> / <c>&lt;ApplicationDefinition&gt;</c> items first; if either yields
    /// nothing, falls back to a recursive directory scan from the project's directory. Falls back
    /// quietly when the csproj uses globbed includes that the indexer can't statically expand —
    /// the directory scan covers those cases.
    /// </summary>
    private static IReadOnlyList<string> EnumerateXamlFiles(string csprojPath, string projectDir)
    {
        var fromItems = ReadXamlItemsFromCsproj(csprojPath, projectDir);
        if (fromItems.Count > 0) return fromItems;

        // Fallback: walk the project directory for *.xaml. Skip bin/ and obj/ so we don't pick up
        // the markup-compiler's intermediate copies.
        var results = new List<string>();
        foreach (var path in Directory.EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(projectDir, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            results.Add(Path.GetFullPath(path));
        }
        return results;
    }

    private static IReadOnlyList<string> ReadXamlItemsFromCsproj(string csprojPath, string projectDir)
    {
        var results = new List<string>();
        try
        {
            using var stream = File.OpenRead(csprojPath);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
                CloseInput = false,
            };
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (!IsXamlItemElement(reader.LocalName)) continue;
                var include = reader.GetAttribute("Include") ?? reader.GetAttribute("Update");
                if (string.IsNullOrEmpty(include)) continue;
                if (include.IndexOfAny(new[] { '*', '?' }) >= 0) continue; // globbed; defer to fs walk
                if (!include.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = include.Replace('\\', '/');
                var absolute = Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(projectDir, relative));
                results.Add(absolute);
            }
        }
        catch (XmlException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
        return results;
    }

    private static bool IsXamlItemElement(string localName) =>
        string.Equals(localName, "Page", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(localName, "ApplicationDefinition", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walk every <c>x:Key</c>-bearing element across the supplied XAML files and build the
    /// resource cache. v1 scopes the cache to the same project's files (no cross-project cascade);
    /// the open question is documented in design.md.
    /// </summary>
    private static Dictionary<string, ResourceDefinition> BuildResourceCache(IReadOnlyList<string> xamlFiles)
    {
        var cache = new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal);
        // App.xaml first (so its keys win on collision), followed by Themes/Generic.xaml, then
        // the rest. Most apps have at most one App.xaml + one Generic.xaml so the linear scan here
        // is fine.
        var ordered = xamlFiles
            .OrderBy(p => Path.GetFileName(p).Equals("App.xaml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => Path.GetFileName(p).Equals("Generic.xaml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        foreach (var path in ordered)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            XamlDocument doc;
            try { doc = XamlReader.Parse(bytes); }
            catch (XmlException) { continue; }

            XamlReader.Walk(doc.Root, (element, _) =>
            {
                var keyAttr = element.FindAttribute(XamlReader.XamlNamespace, "Key");
                if (keyAttr is null) return;
                if (cache.ContainsKey(keyAttr.Value)) return; // App.xaml wins, see ordering above.
                cache[keyAttr.Value] = new ResourceDefinition(
                    key: keyAttr.Value,
                    filePath: path,
                    line: element.Line,
                    column: element.Column,
                    elementName: element.LocalName);
            });
        }
        return cache;
    }
}
