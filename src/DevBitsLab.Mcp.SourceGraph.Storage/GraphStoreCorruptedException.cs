using Microsoft.Data.Sqlite;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Thrown by <see cref="SqliteGraphStore"/> when an underlying <see cref="SqliteException"/>
/// surfaces error code <c>11</c> (<c>SQLITE_CORRUPT</c>) or <c>26</c> (<c>SQLITE_NOTADB</c>) —
/// the two codes that unambiguously indicate the on-disk file is damaged. Other SQLite errors
/// (busy locks, transient I/O) propagate as the original <see cref="SqliteException"/> unchanged.
///
/// The wrapping happens at the <see cref="IGraphStore"/> boundary so all callers (tool bodies,
/// indexer, embeddings store) see a typed exception they can catch and dispatch on, instead of
/// having to reason about SQLite-specific error codes themselves.
/// </summary>
public sealed class GraphStoreCorruptedException : Exception
{
    public string ScopeId { get; }
    public SqliteException InnerSqliteException { get; }

    public GraphStoreCorruptedException(string scopeId, SqliteException inner)
        : base($"Scope `{scopeId}` SQLite store reported corruption: {inner.Message} (code {inner.SqliteErrorCode})", inner)
    {
        ScopeId = scopeId;
        InnerSqliteException = inner;
    }
}
