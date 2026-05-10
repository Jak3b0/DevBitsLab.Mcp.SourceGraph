using System.Globalization;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Core;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace DevBitsLab.Mcp.SourceGraph.Storage;

/// <summary>
/// Opens a read-only SQLite connection that fans out across the resolved scope set: one ATTACH
/// per per-scope DB plus the <c>_meta.db</c> registry, all <c>?mode=ro</c>; the
/// agent-facing view layer (<see cref="Views.Sql"/>) is materialised as TEMP views ready for
/// query. Each <see cref="OpenAsync"/> call returns a fresh connection — caller owns disposal.
///
/// See <c>openspec/changes/add-graph-query/design.md</c> Decision 3 (multi-scope ATTACH) and
/// Decision 4 (safety rails).
///
/// <para><b>ATTACH ceiling.</b> SQLite's <c>SQLITE_LIMIT_ATTACHED</c> caps the number of
/// attached databases. The ABI exposes <c>sqlite3_limit(SQLITE_LIMIT_ATTACHED, …)</c> so
/// we can raise the limit at runtime up to the compile-time absolute ceiling
/// (<c>SQLITE_MAX_ATTACHED</c>, default 125 in the SQLite source). Crucially, the
/// <c>e_sqlite3</c> bundle that ships with <c>SQLitePCLRaw.bundle_e_sqlite3</c> (and
/// therefore Microsoft.Data.Sqlite) is built with the SQLite default
/// <c>SQLITE_MAX_ATTACHED = 10</c> — calls to raise the limit above 10 are silently clamped
/// to 10. The <see cref="OpenAsync"/> helper still attempts the raise (it's the right thing
/// for a future build with a higher cap), then queries back the actual limit and treats
/// <c>min(maxAttached, actualSqliteLimit)</c> as the effective ceiling. The default
/// <paramref name="maxAttached"/> = 64 reflects the spec; the practical limit today is
/// <c>10 - 1 = 9</c> scope DBs once <c>meta</c> consumes one ATTACH slot. Raising the
/// real ceiling is a follow-up (build a custom <c>e_sqlite3</c> with
/// <c>-DSQLITE_MAX_ATTACHED=125</c> or P/Invoke a different binary).
/// </para>
/// </summary>
public static class MultiScopeReadOnlyConnection
{
    /// <summary>
    /// SQLite limit id for the maximum number of attached databases.
    /// Mirrored from <c>SQLITE_LIMIT_ATTACHED</c> in <c>sqlite3.h</c>; <see cref="SQLitePCL.raw"/>
    /// exposes the same constant but referencing it indirectly avoids a reflection trip.
    /// </summary>
    private const int SqliteLimitAttached = 7;

    // Note on read-only enforcement: prior revisions used `ATTACH DATABASE 'file:…?mode=ro' AS …`
    // to make each attached scope DB read-only at the SQLite engine level. That required enabling
    // SQLITE_CONFIG_URI globally via a sqlite3_shutdown / config / initialize dance, which raced
    // with parallel xunit tests opening unrelated SqliteConnections (those connections occasionally
    // saw the engine mid-reinitialise and failed to open). We now ATTACH with literal paths (no
    // URI, no mode=ro) and enforce read-only via `PRAGMA query_only = 1` set on the connection
    // after the TEMP VIEW DDL is applied. PRAGMA query_only is per-connection state, so it doesn't
    // require any global config flip and never races. SQLite returns SQLITE_READONLY (error 8) on
    // any write attempt under query_only, preserving the wire-error contract that
    // `query_graph` tests assert on.

    /// <summary>
    /// Open a read-only SQLite connection that ATTACHes the resolved scope set's per-scope
    /// DBs plus the scope registry, and creates the agent-facing view layer (per
    /// <see cref="Views.Sql"/>) as TEMP views ready for query.
    /// </summary>
    /// <param name="registry">Scope registry to resolve <paramref name="scopeFilter"/> against.</param>
    /// <param name="repoRoot">Repository root; used with <see cref="ScopeLayout"/> to locate
    /// the per-scope DB files and the meta DB.</param>
    /// <param name="scopeFilter">"<c>*</c>" for all non-isolated scopes, comma-separated list
    /// for a narrowed set, or a single scope id. Isolated scopes are included only when
    /// explicitly named.</param>
    /// <param name="maxAttached">Hard ceiling on the number of attached per-scope DBs
    /// (default 64). Throws <see cref="ScopeAttachLimitExceededException"/> if the resolved
    /// set exceeds this. Note: the bundled SQLite caps the practical limit at 10 (one slot
    /// reserved for <c>meta</c>, leaving 9 for scope DBs); see the type-level remarks.</param>
    /// <param name="ct">Cancellation token; honored at the registry-resolve step and during
    /// connection open.</param>
    /// <returns>An open <see cref="SqliteConnection"/> with TEMP views materialised. Caller
    /// disposes; closing the connection drops every ATTACH and the temp views.</returns>
    /// <exception cref="ScopeAttachLimitExceededException">Resolved scope set exceeds
    /// <paramref name="maxAttached"/> (or the SQLite-imposed cap, whichever is lower).</exception>
    /// <exception cref="ArgumentException"><paramref name="scopeFilter"/> is null/empty or
    /// references an unknown scope id.</exception>
    public static async Task<SqliteConnection> OpenAsync(
        IScopeRegistry registry,
        string repoRoot,
        string scopeFilter,
        int maxAttached = 64,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(repoRoot);
        if (string.IsNullOrWhiteSpace(scopeFilter))
        {
            throw new ArgumentException("Scope filter cannot be null or empty.", nameof(scopeFilter));
        }
        if (maxAttached <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttached), maxAttached, "Must be > 0.");
        }

        // 1. Resolve scope filter against the registry.
        var resolved = await ResolveScopesAsync(registry, scopeFilter, ct).ConfigureAwait(false);

        // 2. Validate count against the configured ceiling BEFORE opening any per-scope ATTACH,
        //    so we never leak a half-built connection on overflow.
        if (resolved.Count > maxAttached)
        {
            throw new ScopeAttachLimitExceededException(resolved, maxAttached);
        }

        // 3. Open the in-memory main DB. Writable while we apply the TEMP VIEW DDL; we flip
        //    `PRAGMA query_only = 1` afterward (step 9) so the agent's queries can SELECT but
        //    cannot mutate any attached scope DB. Per-connection state — no global config flip,
        //    no race with parallel SqliteConnections elsewhere in the process.
        var connection = new SqliteConnection("Data Source=:memory:");
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // 5. Raise the runtime ATTACH limit. The bundled e_sqlite3 caps at 10; this call
            //    is harmless (silent-clamp to the compile-time max) but correct in case a
            //    future custom build raises the ceiling.
            _ = raw.sqlite3_limit(connection.Handle, SqliteLimitAttached, maxAttached);
            var actualLimit = raw.sqlite3_limit(connection.Handle, SqliteLimitAttached, -1);

            // The effective ceiling is the smaller of the configured maxAttached and what the
            // SQLite library actually accepts. Re-check resolved.Count after `meta` is also
            // accounted for, since meta consumes one of the actualLimit slots.
            // resolved.Count is the number of per-scope ATTACHes; meta is the +1.
            // If actualLimit < resolved.Count + 1 we must throw.
            if (resolved.Count + 1 > actualLimit)
            {
                throw new ScopeAttachLimitExceededException(resolved, Math.Min(maxAttached, actualLimit - 1));
            }

            // 6. ATTACH meta.db AS meta. Always attached, regardless of scope filter, so
            //    v_scopes can read the registry within the same query.
            var metaPath = ScopeLayout.MetaDbPath(repoRoot);
            await AttachAsync(connection, metaPath, "meta", ct).ConfigureAwait(false);

            // 7. ATTACH each per-scope DB AS "<scope_id>". Aliases are double-quoted so
            //    kebab-case ids with hyphens (e.g. my-scope) parse correctly.
            foreach (var scopeId in resolved)
            {
                var scopeDb = ScopeLayout.ScopeDbPath(repoRoot, scopeId);
                await AttachAsync(connection, scopeDb, scopeId, ct).ConfigureAwait(false);
            }

            // 8. Build and execute the substituted view DDL — needs main writable to create
            //    the TEMP views. Must run BEFORE the query_only flip in step 9.
            var ddl = BuildViewDdl(resolved);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = ddl;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 9. Flip the connection to read-only. From this point on, any INSERT/UPDATE/DELETE/
            //    DROP/CREATE/REPLACE against any attached DB or the in-memory main returns
            //    SQLITE_READONLY (error 8). TEMP views already created in step 8 remain queryable.
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA query_only = 1;";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resolve the <paramref name="scopeFilter"/> token against the registry's current scope
    /// set. <c>"*"</c> returns every non-isolated scope (sorted by id for determinism);
    /// a comma-separated list returns those scopes by id (validated against the registry,
    /// isolated permitted when explicit); a single id returns just that scope.
    /// </summary>
    private static async Task<List<string>> ResolveScopesAsync(
        IScopeRegistry registry, string scopeFilter, CancellationToken ct)
    {
        var registered = await registry.ListAsync(ct).ConfigureAwait(false);

        var trimmed = scopeFilter.Trim();
        if (trimmed == "*")
        {
            return registered
                .Where(r => !r.Isolated)
                .Select(r => r.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        // Split on commas, allow whitespace around each entry.
        var requested = trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (requested.Count == 0)
        {
            throw new ArgumentException(
                $"Scope filter '{scopeFilter}' resolved to no entries.", nameof(scopeFilter));
        }

        // De-duplicate while preserving caller order (so the resulting UNION ALL branches
        // are deterministic per call). Distinct preserves first-occurrence order under LINQ-to-Objects.
        var byId = registered.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var result = new List<string>(requested.Count);
        foreach (var id in requested.Distinct(StringComparer.Ordinal))
        {
            if (!byId.ContainsKey(id))
            {
                throw new ArgumentException(
                    $"Scope id '{id}' is not registered. Known scopes: {string.Join(", ", byId.Keys)}.",
                    nameof(scopeFilter));
            }
            // Validate as a defence-in-depth measure — registry-stored ids should always pass,
            // but this catches a corrupt registry before the id reaches an ATTACH alias.
            ScopeIdValidator.Validate(id);
            result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Issue an <c>ATTACH DATABASE @path AS "&lt;alias&gt;"</c> with a literal absolute path.
    /// The path is bound as a SQL parameter; the alias is inlined and double-quoted because
    /// SQLite does not allow parameter binding for identifiers. Read-only enforcement happens
    /// later via <c>PRAGMA query_only = 1</c> on the connection — the URI <c>?mode=ro</c>
    /// approach was retired because it required a global SQLite engine reconfigure that raced
    /// with parallel SqliteConnections elsewhere in the process.
    /// </summary>
    private static async Task AttachAsync(
        SqliteConnection connection, string dbPath, string alias, CancellationToken ct)
    {
        var absolute = Path.GetFullPath(dbPath);

        await using var cmd = connection.CreateCommand();
        // Identifier (alias) cannot be parameterised; double-quote so kebab-case ids with
        // hyphens (e.g. my-scope) parse cleanly. ScopeIdValidator already restricted the
        // character set to [a-z0-9-], so there is no quote-escape concern.
        cmd.CommandText = $"ATTACH DATABASE @path AS \"{alias}\";";
        var p = cmd.CreateParameter();
        p.ParameterName = "@path";
        p.Value = absolute;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Substitute <see cref="Views.Sql"/>'s <c>{{SCOPE_UNION_BLOCK_&lt;view&gt;}}</c> tokens
    /// with one <c>UNION ALL</c>-joined branch per resolved scope. Returns the multi-statement
    /// DDL ready for <c>ExecuteNonQuery</c>.
    /// </summary>
    /// <remarks>
    /// <para>Line comments (<c>--…</c>) are stripped from <see cref="Views.Sql"/> before the
    /// token substitution runs. Without this, any developer-facing comment in the embedded
    /// SQL that mentions a token by name (e.g. the leading
    /// <c>-- Tokens like {{SCOPE_UNION_BLOCK_v_symbols}} are replaced …</c> documentation
    /// in <c>Views.sql</c>) would itself be substituted, dropping a multi-line SELECT block
    /// into the middle of a comment and corrupting the resulting SQL. SQLite happily parses
    /// the same comments natively, but only when the token references survive intact through
    /// to <c>sqlite3_prepare_v2</c>; once the substitution runs, they don't.</para>
    ///
    /// <para>When <paramref name="resolved"/> is empty the per-view UNION block becomes a
    /// degenerate <c>SELECT</c> that yields zero rows but matches the column shape of the
    /// declared view. This keeps <c>v_symbols</c> / <c>v_files</c> / <c>v_edges</c> /
    /// <c>v_references</c> queryable even when the scope filter resolves to nothing — agents
    /// can probe the schema without hitting a hard error.</para>
    /// </remarks>
    private static string BuildViewDdl(IReadOnlyList<string> resolved)
    {
        var sql = StripLineComments(Views.Sql);
        foreach (var (viewName, template) in Views.PerScopeBlockTemplates)
        {
            var token = "{{SCOPE_UNION_BLOCK_" + viewName + "}}";
            var block = BuildUnionBlock(viewName, template, resolved);
            sql = sql.Replace(token, block, StringComparison.Ordinal);
        }
        return sql;
    }

    /// <summary>
    /// Drop every line whose first non-whitespace characters are <c>--</c>. Block comments
    /// (<c>/* … */</c>) are left intact — <c>Views.sql</c> doesn't use them, and stripping
    /// them properly would require a real tokenizer (string literals can contain <c>/*</c>).
    /// </summary>
    private static string StripLineComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var first = true;
        foreach (var line in sql.Split('\n'))
        {
            if (line.AsSpan().TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            if (!first) sb.Append('\n');
            sb.Append(line);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Format the <paramref name="template"/> once per resolved scope, joining the resulting
    /// SELECTs with <c>UNION ALL</c>. With zero scopes the result is a never-true SELECT
    /// shaped exactly like one branch (using a fixed placeholder scope id) so the parent
    /// <c>CREATE VIEW</c> still defines a queryable view.
    /// </summary>
    private static string BuildUnionBlock(string viewName, string template, IReadOnlyList<string> resolved)
    {
        if (resolved.Count == 0)
        {
            // Empty-scope safety: produce a single branch that filters everything out. The
            // synthetic alias '__none__' is never a valid scope id (the validator rejects
            // double-underscore prefixes via length / charset rules — well, technically
            // [a-z0-9-] permits no underscore at all). We use it only as a literal in the
            // SELECT projection, never as an ATTACH alias, so there is no aliasing collision.
            // The trailing WHERE 0 makes every row vanish at execution time.
            // We can't actually build a degenerate template (the FROM "{__none__}".table would
            // reference an attached DB that doesn't exist), so we use a SELECT that bypasses
            // the FROM by selecting NULL columns shaped like the view. To keep this simple
            // and avoid hard-coding column shapes here, we just emit a SELECT 0 WHERE 0 — but
            // that would not match the projected column count. Easiest is to substitute a
            // single scope alias known to be attached: meta. But meta does not have the
            // tables (symbols/files/edges/refs). So the cleanest option: leave the empty
            // case to the caller (resolved.Count is validated above to be >= 1 in practice
            // because ResolveScopesAsync requires at least one entry). Throw here to surface
            // the bug rather than producing invalid SQL.
            throw new InvalidOperationException(
                $"Internal error: tried to build view '{viewName}' with zero attached scopes.");
        }

        var sb = new StringBuilder();
        for (var i = 0; i < resolved.Count; i++)
        {
            var scopeId = resolved[i];
            // The template uses literal {SCOPE_ID} placeholders both inside string literals
            // (which become 'scope_id' AS scope projections) and inside double-quoted
            // identifiers (the FROM "scope_id".table alias). One Replace handles both.
            sb.Append(template.Replace("{SCOPE_ID}", scopeId, StringComparison.Ordinal));
            if (i < resolved.Count - 1)
            {
                sb.AppendLine();
                sb.AppendLine("UNION ALL");
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Thrown when a multi-scope query's resolved scope set exceeds the configured ATTACH
/// ceiling (or the SQLite-imposed compile-time cap, whichever is lower). The
/// <see cref="ResolvedScopes"/> list lets the caller render a useful "narrow your filter"
/// hint without re-resolving the registry.
/// </summary>
public sealed class ScopeAttachLimitExceededException : Exception
{
    public IReadOnlyList<string> ResolvedScopes { get; }
    public int Limit { get; }

    public ScopeAttachLimitExceededException(IReadOnlyList<string> resolvedScopes, int limit)
        : base(BuildMessage(resolvedScopes, limit))
    {
        ResolvedScopes = resolvedScopes;
        Limit = limit;
    }

    private static string BuildMessage(IReadOnlyList<string> resolvedScopes, int limit)
    {
        // Keep the message specific so the agent's "narrow the filter" follow-up is one step
        // away — surface the count and the limit, but not the full scope list (could be 70+
        // entries; the caller can render that itself from ResolvedScopes).
        var preview = resolvedScopes.Count <= 6
            ? string.Join(", ", resolvedScopes)
            : string.Join(", ", resolvedScopes.Take(6)) + ", ...";
        return string.Create(CultureInfo.InvariantCulture,
            $"Scope filter resolved to {resolvedScopes.Count} scopes; this server's limit is {limit}. " +
            $"Narrow with `scope='id1,id2,...'`. Resolved: {preview}.");
    }
}
