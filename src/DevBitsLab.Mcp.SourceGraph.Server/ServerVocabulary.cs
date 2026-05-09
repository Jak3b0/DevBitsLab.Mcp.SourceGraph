using System.Reflection;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Microsoft.Data.Sqlite;
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
    /// in true <see cref="SqliteOpenMode.ReadOnly"/> mode, queries the three distinct-kind columns,
    /// and unions the result with the SDK constants. The probe deliberately bypasses
    /// <see cref="SqliteGraphStore"/> so it never runs <c>EnsureSchemaAsync</c> (which would
    /// drop-and-rebuild a stale schema as a side effect of vocabulary collection) and never flips
    /// the journal_mode pragma. A scope DB at an older schema version simply errors out on the
    /// missing columns; the catch falls back to the SDK constants for that scope. Each missing or
    /// unreadable scope is skipped silently — the host stays up regardless.
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
                await ProbeScopeAsync(dbPath, edgeKinds, symbolKinds, annotationFlavors, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller cancelled — propagate cleanly rather than swallowing.
                throw;
            }
            catch (Exception ex) when (
                ex is IOException or SqliteException or InvalidOperationException or UnauthorizedAccessException)
            {
                // File missing/locked, schema older than the columns we query, ambient state weird.
                // Probe is best-effort; the SDK constants already cover the common case.
                logger.LogDebug(ex, "Vocabulary probe failed for scope `{Id}` at {Path}", scope.Id, dbPath);
            }
        }

        return new VocabularyResult(
            edgeKinds.ToList(),
            symbolKinds.ToList(),
            annotationFlavors.ToList());
    }

    /// <summary>
    /// Open <paramref name="dbPath"/> in true read-only mode and union its three distinct-kind
    /// columns into the supplied sets. Uses raw <see cref="SqliteCommand"/> so we bypass
    /// <see cref="SqliteGraphStore"/>'s schema migration and journal-mode pragmas — this path must
    /// have zero side effects on the file. Throws on missing columns / older schemas; the caller
    /// catches and falls back to the SDK constants.
    /// </summary>
    private static async Task ProbeScopeAsync(
        string dbPath,
        SortedSet<string> edgeKinds,
        SortedSet<string> symbolKinds,
        SortedSet<string> annotationFlavors,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            // ReadOnly mode prevents any writes, schema migration, or WAL/journal pragmas from
            // mutating the file. Pooling=false keeps this transient connection out of the shared
            // pool that the indexer-side SqliteGraphStore uses.
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await ReadDistinctIntoAsync(connection, "SELECT DISTINCT kind_name FROM edges ORDER BY kind_name", edgeKinds, ct).ConfigureAwait(false);
        await ReadDistinctIntoAsync(connection, "SELECT DISTINCT kind_name FROM symbols ORDER BY kind_name", symbolKinds, ct).ConfigureAwait(false);
        await ReadDistinctIntoAsync(connection, "SELECT DISTINCT flavor FROM annotations ORDER BY flavor", annotationFlavors, ct).ConfigureAwait(false);
    }

    private static async Task ReadDistinctIntoAsync(
        SqliteConnection connection,
        string sql,
        SortedSet<string> sink,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Filter+map inline rather than via Where() — async stream readers don't compose with
            // System.Linq cleanly without an extra IAsyncEnumerable adapter dependency.
            if (reader.IsDBNull(0)) continue;
            var v = reader.GetString(0);
            if (v.Length == 0) continue;
            sink.Add(v.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Enumerate every <c>public const string</c> field declared on <paramref name="constantsType"/>.
    /// Used to pick up the SDK's well-known kind / flavor identifiers without a hand-maintained
    /// echo of the same list here.
    /// </summary>
    private static IEnumerable<string> EnumerateConstantStrings(Type constantsType) =>
        constantsType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => f.GetRawConstantValue() as string)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!.ToLowerInvariant());
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
