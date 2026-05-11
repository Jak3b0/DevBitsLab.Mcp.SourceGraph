using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;

/// <summary>
/// One immutable point-in-time view of the operator-console state surfaces. Produced by
/// <see cref="SnapshotBuilder.BuildAsync"/>; consumed by <c>StatusRenderer</c> (prose) and
/// <see cref="DashboardSnapshotJson"/> (the <c>--json</c> serializer).
///
/// <para>
/// Five surfaces aggregate here in the order specified by the <c>status snapshot data sources</c>
/// requirement: <see cref="Environment"/>, <see cref="Scopes"/>, <see cref="Clients"/>,
/// <see cref="Embeddings"/>, <see cref="RecentActivity"/>. Snake-case JSON shape is pinned by
/// <see cref="DashboardSnapshotJson"/> via <see cref="JsonNamingPolicy.SnakeCaseLower"/>; the
/// spec's contract is that additions are append-only.
/// </para>
/// </summary>
internal sealed record DashboardSnapshot(
    EnvironmentSurface Environment,
    IReadOnlyList<ScopeRow> Scopes,
    IReadOnlyList<ClientRow> Clients,
    EmbeddingsSurface Embeddings,
    IReadOnlyList<ActivityEntry> RecentActivity,
    DateTimeOffset BuiltAt,
    string UsageLogPath,
    string HealsLogPath,
    int ExitCode);

/// <summary>
/// Environment surface — SDK, git, repo root, solution files, .sourcegraph.json status. Sourced
/// from <c>OnboardingDetector.DetectAsync</c>; carries the malformed-config error verbatim so
/// the renderer can surface it without re-running detection. <see cref="SourceGraphConfigStatus"/>
/// is one of <c>"missing" | "valid" | "malformed"</c>.
///
/// <para>
/// Note: <c>SourceGraph</c> uses an explicit <see cref="JsonPropertyNameAttribute"/> because
/// the default <see cref="JsonNamingPolicy.SnakeCaseLower"/> policy splits CamelCase on every
/// capital letter (`SourceGraph` → `source_graph_config_status`), which doesn't match the
/// documented field name `sourcegraph_config_status`. The override pins the contract.
/// </para>
/// </summary>
internal sealed record EnvironmentSurface(
    string? DotnetSdkVersion,
    bool GitOnPath,
    string RepoRootPath,
    IReadOnlyList<string> SolutionFiles,
    [property: JsonPropertyName("sourcegraph_config_status")] string SourceGraphConfigStatus,
    [property: JsonPropertyName("sourcegraph_config_error")] string? SourceGraphConfigError);

/// <summary>
/// One row of the scopes surface. Mirrors the spec's required JSON fields exactly; the
/// <see cref="Status"/> value is one of <c>"ok" | "partial" | "degraded" | "indexing"</c>.
/// <see cref="FailedProjects"/> and <see cref="FailedFiles"/> are empty when <see cref="Status"/>
/// isn't <c>"partial"</c>.
/// </summary>
internal sealed record ScopeRow(
    string Name,
    string Status,
    long SymbolCount,
    long ReferenceCount,
    DateTimeOffset? LastIndexedAt,
    IReadOnlyList<string> FailedProjects,
    IReadOnlyList<string> FailedFiles,
    bool Isolated);

/// <summary>
/// One row of the clients surface. <see cref="Slug"/> matches
/// <c>ClientIdExtensions.ToSlug</c>; <see cref="Scope"/> is one of <c>"project" | "user"</c>.
/// </summary>
internal sealed record ClientRow(
    string Slug,
    string Scope,
    string Path,
    bool Exists,
    bool ContainsSourcegraphEntry);

/// <summary>
/// Embeddings surface — active model id, cache directory, presence/size, optional verified
/// flag. <see cref="Verified"/> is conservatively <c>false</c> at v1 (the verify state isn't
/// persisted yet); flipping it true is a follow-up.
/// </summary>
internal sealed record EmbeddingsSurface(
    string ModelId,
    string CacheDir,
    bool CachePresent,
    long TotalBytes,
    bool Verified);

/// <summary>
/// One merged row from <c>usage.jsonl</c> + <c>heals.jsonl</c>, sorted by timestamp ascending,
/// capped at the most recent N entries. <see cref="Kind"/> is the raw kind string from the
/// source log (<c>tool_call</c> for usage rows; <c>heal</c>, <c>boot_reconcile</c>, etc. for
/// heal rows).
/// </summary>
internal sealed record ActivityEntry(
    DateTimeOffset Ts,
    string Kind,
    string? Scope,
    bool Ok,
    int Ms,
    string? Detail);

/// <summary>
/// Stable serializer for <see cref="DashboardSnapshot"/>. Snake-case property naming via
/// <see cref="JsonNamingPolicy.SnakeCaseLower"/> is the contract; the system policy maps the
/// PascalCase C# property names (`DotnetSdkVersion`, `RepoRootPath`, …) to snake_case at the
/// wire (`dotnet_sdk_version`, `repo_root_path`, …) — adding a new C# property automatically
/// maps without per-field annotations. New fields are append-only by spec.
/// </summary>
internal static class DashboardSnapshotJson
{
    /// <summary>
    /// The shared options instance for serializing/deserializing the dashboard snapshot.
    /// Snake-case for property names so PascalCase records map to the documented JSON wire
    /// shape; indented output keeps the <c>--json</c> document human-scannable.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        IndentSize = 2,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes the snapshot to a UTF-8 JSON string using <see cref="Options"/>.</summary>
    public static string Serialize(DashboardSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    /// <summary>Deserializes a JSON string back into the snapshot record. Used by tests only.</summary>
    public static DashboardSnapshot? Deserialize(string json) =>
        JsonSerializer.Deserialize<DashboardSnapshot>(json, Options);
}
