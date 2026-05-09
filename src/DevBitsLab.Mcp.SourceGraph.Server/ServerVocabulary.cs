using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Extensions.Logging;

namespace DevBitsLab.Mcp.SourceGraph.Server;

/// <summary>
/// Computes the open-language vocabularies the MCP server publishes alongside its
/// <c>ServerInstructions</c>: every distinct edge-kind, symbol-kind, and annotation-flavor the
/// active scope exposes. The lists are the union of the SDK's well-known constants
/// (<see cref="EdgeKinds"/>, <see cref="SymbolKinds"/>) with whatever the scope DB currently
/// contains, so plugin-defined kinds (e.g. <c>"vue-directive"</c>, <c>"renders-component"</c>)
/// surface alongside the built-in C# kinds without a code change in the host.
///
/// <para>The lists are surfaced as JSON arrays of lower-case kebab-case identifiers in the
/// MCP <c>initialize</c> response. The wire shape is documented in
/// <see cref="VocabularyResult"/>; we publish through
/// <c>McpServerOptions.Capabilities.Experimental</c>, which the MCP SDK round-trips into the
/// <c>capabilities.experimental</c> object on the wire — the spec's stable extension point for
/// non-standard server capabilities. Clients that don't know to look ignore the field; clients
/// that do (the agent harness) read it once at session start and use it to validate the
/// <c>kind</c> argument they pass to tools like <c>list_callers</c>.</para>
///
/// <para>Suppression mirrors <see cref="ServerInstructions"/>: pass <c>--no-instructions</c> or
/// set <c>SOURCEGRAPH_NO_INSTRUCTIONS=1</c> and the arrays are not added to the capabilities
/// payload at all.</para>
/// </summary>
internal static class ServerVocabulary
{
    /// <summary>The capability key under <c>capabilities.experimental</c> the vocabulary lives at.</summary>
    public const string CapabilityKey = "sourcegraph.vocabulary";

    /// <summary>
    /// Compute the vocabulary for every scope in <paramref name="scopes"/>: opens each scope's DB
    /// read-only, queries the three distinct-kind columns, and unions with the SDK constants.
    /// Each missing or unreadable scope is skipped silently — the host stays up regardless and the
    /// vocabulary just falls back to the constants for that scope.
    /// </summary>
    public static async Task<VocabularyResult> ComputeAsync(
        IReadOnlyList<Scope> scopes,
        ILogger logger,
        CancellationToken ct = default)
    {
        var edgeKinds = new SortedSet<string>(StringComparer.Ordinal);
        var symbolKinds = new SortedSet<string>(StringComparer.Ordinal);
        var annotationFlavors = new SortedSet<string>(StringComparer.Ordinal);

        // SDK-declared constants always present, even on a fresh / empty scope.
        foreach (var v in EnumerateConstantStrings(typeof(EdgeKinds))) edgeKinds.Add(v);
        foreach (var v in EnumerateConstantStrings(typeof(SymbolKinds))) symbolKinds.Add(v);
        // No SDK constant for annotation flavors — flavor names are scoped to language plugins.
        // Built-in C# uses "csharp-attribute"; declare it here so single-language repos still get
        // a useful list before any indexed annotation has landed.
        annotationFlavors.Add("csharp-attribute");

        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();
            var dbPath = ScopeLayout.ScopeDbPath(scope.Root, scope.Id);
            if (!File.Exists(dbPath))
            {
                continue; // first run — index hasn't built the DB yet; constants are sufficient.
            }
            try
            {
                await using var store = new SqliteGraphStore(dbPath);
                await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
                foreach (var v in await store.GetDistinctEdgeKindsAsync(ct).ConfigureAwait(false))
                    if (!string.IsNullOrEmpty(v)) edgeKinds.Add(v.ToLowerInvariant());
                foreach (var v in await store.GetDistinctSymbolKindsAsync(ct).ConfigureAwait(false))
                    if (!string.IsNullOrEmpty(v)) symbolKinds.Add(v.ToLowerInvariant());
                foreach (var v in await store.GetDistinctAnnotationFlavorsAsync(ct).ConfigureAwait(false))
                    if (!string.IsNullOrEmpty(v)) annotationFlavors.Add(v.ToLowerInvariant());
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Vocabulary probe failed for scope `{Id}` at {Path}", scope.Id, dbPath);
            }
        }

        return new VocabularyResult(
            edgeKinds.ToList(),
            symbolKinds.ToList(),
            annotationFlavors.ToList());
    }

    /// <summary>
    /// Enumerate every <c>public const string</c> field declared on <paramref name="constantsType"/>.
    /// Used to pick up the SDK's well-known kind / flavor identifiers without a hand-maintained
    /// echo of the same list here.
    /// </summary>
    private static IEnumerable<string> EnumerateConstantStrings(Type constantsType)
    {
        foreach (var field in constantsType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (!field.IsLiteral || field.IsInitOnly) continue;
            if (field.FieldType != typeof(string)) continue;
            if (field.GetRawConstantValue() is string s && !string.IsNullOrEmpty(s))
            {
                yield return s.ToLowerInvariant();
            }
        }
    }
}

/// <summary>
/// Wire shape published under <c>capabilities.experimental["sourcegraph.vocabulary"]</c> in the
/// MCP <c>initialize</c> response. Each list is sorted, lower-case, and kebab-case. Client code
/// that doesn't know about this field ignores it; clients that do read it once per session and
/// use it to validate <c>kind</c> arguments before sending a tool call.
/// </summary>
public sealed record VocabularyResult(
    IReadOnlyList<string> EdgeKinds,
    IReadOnlyList<string> SymbolKinds,
    IReadOnlyList<string> AnnotationFlavors);
