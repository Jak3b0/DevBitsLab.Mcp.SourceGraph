namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Stable agent-facing view layer over the per-scope SQLite tables. See
/// openspec/changes/add-graph-query/design.md (Decision 2) for the contract.
///
/// View names are stable; their column shape is the public API. Bump
/// <see cref="SchemaVersion"/> on any backwards-incompatible column change
/// (column removed, renamed, or whose type meaningfully changes).
///
/// The underlying tables (<c>symbols</c>, <c>edges</c>, <c>refs</c>, <c>files</c>) remain
/// implementation details and may evolve without bumping <see cref="SchemaVersion"/> —
/// only <see cref="Schema.Version"/> moves for those.
/// </summary>
public static class Views
{
    /// <summary>
    /// View-layer schema version. Independent from <see cref="Schema.Version"/> (the on-disk
    /// table schema). Bumps only when a view's column shape changes in a backwards-incompatible
    /// way.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The CREATE TEMP VIEW scaffolding loaded from <c>Views.sql</c>. Contains
    /// <c>{{SCOPE_UNION_BLOCK_&lt;view&gt;}}</c> placeholder tokens that the connection helper
    /// substitutes with per-scope UNION ALL blocks.
    /// </summary>
    public static string Sql { get; }

    /// <summary>
    /// Per-view per-scope SELECT template, keyed by view name. The template contains a
    /// <c>{SCOPE_ID}</c> placeholder; the connection helper formats one block per attached
    /// scope and joins them with <c>UNION ALL</c>, then substitutes the joined text into the
    /// matching <c>{{SCOPE_UNION_BLOCK_&lt;view&gt;}}</c> token in <see cref="Sql"/>.
    ///
    /// Keys: <c>"v_symbols"</c>, <c>"v_files"</c>, <c>"v_edges"</c>, <c>"v_references"</c>.
    /// (<c>v_scopes</c> is single-source from <c>meta.scopes</c> and has no per-scope template.)
    ///
    /// The ATTACH alias <c>"{SCOPE_ID}"</c> is double-quoted because scope ids can contain
    /// hyphens (<c>my-scope</c>) which SQLite would otherwise misparse.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PerScopeBlockTemplates { get; }

    /// <summary>
    /// Hand-curated descriptors for <c>describe_schema</c>'s response. Order matches the
    /// <see cref="PerScopeBlockTemplates"/> keys plus <c>v_scopes</c> at the end.
    /// </summary>
    public static IReadOnlyList<ViewDescriptor> All { get; }

    static Views()
    {
        Sql = LoadEmbedded("Views.sql");
        PerScopeBlockTemplates = BuildTemplates();
        All = BuildDescriptors();
    }

    private static string LoadEmbedded(string name)
    {
        var assembly = typeof(Views).Assembly;
        var resourceId = $"{typeof(Views).Namespace}.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceId)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceId}' not found. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> BuildTemplates()
    {
        // {SCOPE_ID} is the only placeholder. The double-quoted "{SCOPE_ID}" alias references
        // the per-scope ATTACHed DB by its id (which can contain hyphens).
        const string vSymbols = """
            SELECT '{SCOPE_ID}' AS scope, s.id AS id, s.name AS name, s.fqn AS fqn,
                   s.kind_name AS kind, s.accessibility AS accessibility,
                   (CASE WHEN s.accessibility = 6 THEN 1 ELSE 0 END) AS is_public,
                   (CASE WHEN s.kind_name IN ('class','interface','struct','record','enum','delegate') THEN 1 ELSE 0 END) AS is_type,
                   s.modifiers AS modifiers, s.xml_summary AS xml_summary,
                   s.container_id AS container_id, s.file_id AS file_id,
                   s.start_line AS start_line, s.start_col AS start_column
            FROM "{SCOPE_ID}".symbols s
            """;

        const string vFiles = """
            SELECT '{SCOPE_ID}' AS scope, f.id AS id, f.path AS path, f.content_sha256 AS sha,
                   f.last_indexed_at AS last_indexed_at, f.is_generated AS is_generated
            FROM "{SCOPE_ID}".files f
            """;

        const string vEdges = """
            SELECT '{SCOPE_ID}' AS scope, e.src AS src, e.dst AS dst,
                   e.kind_name AS kind, e.payload AS payload
            FROM "{SCOPE_ID}".edges e
            """;

        const string vReferences = """
            SELECT '{SCOPE_ID}' AS scope, r.symbol_id AS symbol_id, r.file_id AS file_id,
                   r.line AS line, r.col AS column_number,
                   (CASE r.kind WHEN 0 THEN 'def' WHEN 1 THEN 'ref' WHEN 2 THEN 'call'
                                WHEN 3 THEN 'impl' WHEN 4 THEN 'inherit'
                                WHEN 5 THEN 'read' WHEN 6 THEN 'write'
                                ELSE CAST(r.kind AS TEXT) END) AS kind
            FROM "{SCOPE_ID}".refs r
            """;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v_symbols"] = vSymbols,
            ["v_files"] = vFiles,
            ["v_edges"] = vEdges,
            ["v_references"] = vReferences,
        };
    }

    private static List<ViewDescriptor> BuildDescriptors()
    {
        return new List<ViewDescriptor>
        {
            new(
                "v_symbols",
                "Every declared symbol across the resolved scopes. One row per (scope, id). "
                + "Cross-scope joins use the composite (scope, id) tuple; single-scope queries "
                + "see a constant scope column and can join on bare id.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this row lives in."),
                    new("id", "INTEGER", false, "Per-scope symbol id; combine with `scope` for cross-scope uniqueness."),
                    new("name", "TEXT", false, "Unqualified symbol name (e.g. `Calculator`)."),
                    new("fqn", "TEXT", false, "Fully-qualified name (e.g. `Sample.Domain.Calculator`)."),
                    new("kind", "TEXT", false, "Symbol kind: `class`, `interface`, `struct`, `record`, `enum`, `delegate`, `method`, `field`, `property`, `event`, `namespace`, `xaml-view`, ... See `describe_schema.symbol_kinds` for the live vocabulary."),
                    new("accessibility", "INTEGER", false, "Roslyn `Accessibility`: 0=NotApplicable, 1=Private, 2=ProtectedAndInternal, 3=Protected, 4=Internal, 5=ProtectedOrInternal, 6=Public."),
                    new("is_public", "INTEGER", false, "1 when accessibility = 6 (Public); 0 otherwise. Convenience for the common filter."),
                    new("is_type", "INTEGER", false, "1 when kind in {class, interface, struct, record, enum, delegate}; 0 otherwise."),
                    new("modifiers", "TEXT", true, "Space-separated modifier list (e.g. `static abstract sealed`); NULL when no modifiers apply."),
                    new("xml_summary", "TEXT", true, "Plain-text body of the XML doc-comment `<summary>` tag, if present."),
                    new("container_id", "INTEGER", true, "Per-scope id of the enclosing symbol (containing type/method); NULL for top-level symbols. Join back to `v_symbols.id` within the same scope."),
                    new("file_id", "INTEGER", false, "Per-scope file id; join to `v_files.id` within the same scope to resolve the source path."),
                    new("start_line", "INTEGER", false, "1-based start line of the declaration in the source file."),
                    new("start_column", "INTEGER", false, "1-based start column of the declaration. (Renamed from underlying `start_col`.)"),
                }),

            new(
                "v_files",
                "Every indexed source file across the resolved scopes. One row per (scope, id).",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this row lives in."),
                    new("id", "INTEGER", false, "Per-scope file id; join from `v_symbols.file_id` and `v_references.file_id` within the same scope."),
                    new("path", "TEXT", false, "Absolute or scope-relative path to the source file."),
                    new("sha", "BLOB", false, "SHA-256 of the file's content at last index time. (Renamed from underlying `content_sha256`.)"),
                    new("last_indexed_at", "INTEGER", false, "Unix-millis timestamp of the last successful index pass over this file."),
                    new("is_generated", "INTEGER", false, "1 when the file is generated (e.g. EditorBrowsable hidden, `*.g.cs`); 0 otherwise."),
                }),

            new(
                "v_edges",
                "Every directed edge in the graph (calls / uses-type / inherits / implements / instantiates / throws / tests / binds-path / handles-event / ...) across the resolved scopes. Both `src` and `dst` are per-scope symbol ids; cross-scope edges do not exist (each edge lives in exactly one scope's DB).",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this edge lives in."),
                    new("src", "INTEGER", false, "Source symbol id (the caller / user / inheriter / ...). Join to `v_symbols.id` in the same scope."),
                    new("dst", "INTEGER", false, "Destination symbol id (the callee / used type / base / ...). Join to `v_symbols.id` in the same scope."),
                    new("kind", "TEXT", false, "Edge kind: `calls`, `uses-type`, `inherits`, `implements`, `instantiates`, `throws`, `tests`, `binds-path`, `handles-event`, `uses-resource`, ... See `describe_schema.edge_kinds` for the live vocabulary."),
                    new("payload", "TEXT", true, "Optional JSON payload carrying edge metadata (binding paths, event names, prop names). NULL when the edge kind has no associated metadata."),
                }),

            new(
                "v_references",
                "Every textual reference site across the resolved scopes (the `refs` table). One row per (scope, file, line, column) that mentions a symbol.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id this reference lives in."),
                    new("symbol_id", "INTEGER", false, "Per-scope id of the referenced symbol. Join to `v_symbols.id` within the same scope."),
                    new("file_id", "INTEGER", false, "Per-scope id of the file containing the reference site. Join to `v_files.id` within the same scope."),
                    new("line", "INTEGER", false, "1-based line of the reference site."),
                    new("column_number", "INTEGER", false, "1-based column of the reference site. (Renamed from underlying `col`; SQL reserves the bare identifier `column`.)"),
                    new("kind", "TEXT", false, "Reference kind, mapped from the underlying integer enum: `def` (Definition), `ref` (Reference), `call` (Call), `impl` (Implements), `inherit` (Inherits), `read`, `write`."),
                }),

            new(
                "v_scopes",
                "Every registered scope from the `_meta.db` registry. Single source (no per-scope union). Use this view to discover scope ids, status, and last-indexed times.",
                new List<ViewColumn>
                {
                    new("scope", "TEXT", false, "Scope id (the primary key from the registry; usable as the ATTACH alias for the scope's per-scope DB)."),
                    new("name", "TEXT", false, "Human-friendly scope name (from `.sourcegraph.json`)."),
                    new("root", "TEXT", false, "Filesystem root the scope's projects live under."),
                    new("isolated", "INTEGER", false, "1 when the scope is isolated (excluded from `scope='*'` fan-out); 0 otherwise."),
                    new("status", "TEXT", false, "One of `ok`, `degraded`, `indexing`."),
                    new("last_indexed_at", "INTEGER", false, "Unix-millis timestamp of the last completed index pass against the scope."),
                }),
        };
    }
}

/// <summary>
/// Hand-curated descriptor for one view, surfaced via <c>describe_schema</c>. The
/// <see cref="Columns"/> list is authoritative — agents reading <c>describe_schema</c>'s
/// response treat it as the contract.
/// </summary>
public sealed record ViewDescriptor(string Name, string Description, IReadOnlyList<ViewColumn> Columns);

/// <summary>
/// One column in a <see cref="ViewDescriptor"/>. <see cref="SqliteType"/> is the SQLite
/// affinity name (<c>TEXT</c>, <c>INTEGER</c>, <c>BLOB</c>); <see cref="Nullable"/> reflects
/// whether the column can be NULL when read via the view.
/// </summary>
public sealed record ViewColumn(string Name, string SqliteType, bool Nullable, string Description);
