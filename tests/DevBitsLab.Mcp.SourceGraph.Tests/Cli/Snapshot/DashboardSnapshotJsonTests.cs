using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Snapshot;

/// <summary>
/// Pins the snake-case JSON contract for <see cref="DashboardSnapshot"/>. The wire shape is a
/// stable, append-only public surface (per the <c>status snapshot data sources</c> requirement);
/// these tests would break if a future refactor silently renamed or dropped a documented field.
/// </summary>
public sealed class DashboardSnapshotJsonTests
{
    private static DashboardSnapshot SampleSnapshot() => new(
        Environment: new EnvironmentSurface(
            DotnetSdkVersion: "10.0.100",
            GitOnPath: true,
            RepoRootPath: "/repo",
            SolutionFiles: new[] { "/repo/MyApp.slnx" },
            SourceGraphConfigStatus: "valid",
            SourceGraphConfigError: null),
        Scopes: new[]
        {
            new ScopeRow(
                Name: "frontend",
                Status: "ok",
                SymbolCount: 1000,
                ReferenceCount: 2000,
                LastIndexedAt: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
                FailedProjects: Array.Empty<string>(),
                FailedFiles: Array.Empty<string>(),
                Isolated: false),
        },
        Clients: new[]
        {
            new ClientRow("claude-code", "project", "/repo/.mcp.json", true, true),
        },
        Embeddings: new EmbeddingsSurface(
            ModelId: "jinaai/jina-embeddings-v2-base-code",
            CacheDir: "/home/user/.cache/devbitslab.sourcegraph/models",
            CachePresent: true,
            TotalBytes: 614000000,
            Verified: false),
        RecentActivity: new[]
        {
            new ActivityEntry(
                Ts: new DateTimeOffset(2025, 1, 1, 12, 1, 0, TimeSpan.Zero),
                Kind: "tool_call",
                Scope: "frontend",
                Ok: true,
                Ms: 12,
                Detail: "search_symbols"),
        },
        BuiltAt: new DateTimeOffset(2025, 1, 1, 12, 5, 0, TimeSpan.Zero),
        UsageLogPath: "/repo/.sourcegraph/usage.jsonl",
        HealsLogPath: "/repo/.sourcegraph/heals.jsonl",
        ExitCode: 0);

    [Fact]
    public void Serialize_emitsSnakeCaseTopLevelKeys()
    {
        var json = DashboardSnapshotJson.Serialize(SampleSnapshot());
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeEquivalentTo(new[]
        {
            "environment",
            "scopes",
            "clients",
            "embeddings",
            "recent_activity",
            "built_at",
            "usage_log_path",
            "heals_log_path",
            "exit_code",
        });
    }

    [Fact]
    public void Serialize_environmentSurface_hasDocumentedFieldNames()
    {
        var json = DashboardSnapshotJson.Serialize(SampleSnapshot());
        using var doc = JsonDocument.Parse(json);
        var env = doc.RootElement.GetProperty("environment");
        var keys = env.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeEquivalentTo(new[]
        {
            "dotnet_sdk_version",
            "git_on_path",
            "repo_root_path",
            "solution_files",
            "sourcegraph_config_status",
            "sourcegraph_config_error",
        });
    }

    [Fact]
    public void Serialize_scopeRow_hasDocumentedFieldNames()
    {
        var json = DashboardSnapshotJson.Serialize(SampleSnapshot());
        using var doc = JsonDocument.Parse(json);
        var scope = doc.RootElement.GetProperty("scopes")[0];
        var keys = scope.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeEquivalentTo(new[]
        {
            "name",
            "status",
            "symbol_count",
            "reference_count",
            "last_indexed_at",
            "failed_projects",
            "failed_files",
            "isolated",
        });
    }

    [Fact]
    public void Serialize_clientRow_hasDocumentedFieldNames()
    {
        var json = DashboardSnapshotJson.Serialize(SampleSnapshot());
        using var doc = JsonDocument.Parse(json);
        var client = doc.RootElement.GetProperty("clients")[0];
        var keys = client.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeEquivalentTo(new[]
        {
            "slug",
            "scope",
            "path",
            "exists",
            "contains_sourcegraph_entry",
        });
    }

    [Fact]
    public void Serialize_embeddingsSurface_hasDocumentedFieldNames()
    {
        var json = DashboardSnapshotJson.Serialize(SampleSnapshot());
        using var doc = JsonDocument.Parse(json);
        var emb = doc.RootElement.GetProperty("embeddings");
        var keys = emb.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeEquivalentTo(new[]
        {
            "model_id",
            "cache_dir",
            "cache_present",
            "total_bytes",
            "verified",
        });
    }

    [Fact]
    public void Serialize_activityEntry_hasDocumentedFieldNames()
    {
        var json = DashboardSnapshotJson.Serialize(SampleSnapshot());
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("recent_activity")[0];
        var keys = entry.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeEquivalentTo(new[]
        {
            "ts",
            "kind",
            "scope",
            "ok",
            "ms",
            "detail",
        });
    }

    [Fact]
    public void RoundTrip_preservesContent()
    {
        var original = SampleSnapshot();
        var json = DashboardSnapshotJson.Serialize(original);
        var roundtripped = DashboardSnapshotJson.Deserialize(json);
        roundtripped.Should().NotBeNull();
        roundtripped!.Environment.DotnetSdkVersion.Should().Be("10.0.100");
        roundtripped.Environment.GitOnPath.Should().BeTrue();
        roundtripped.Scopes.Should().HaveCount(1);
        roundtripped.Scopes[0].Status.Should().Be("ok");
        roundtripped.Scopes[0].SymbolCount.Should().Be(1000);
        roundtripped.Clients.Should().HaveCount(1);
        roundtripped.Clients[0].Slug.Should().Be("claude-code");
        roundtripped.Clients[0].ContainsSourcegraphEntry.Should().BeTrue();
        roundtripped.Embeddings.ModelId.Should().Be("jinaai/jina-embeddings-v2-base-code");
        roundtripped.RecentActivity.Should().HaveCount(1);
        roundtripped.RecentActivity[0].Kind.Should().Be("tool_call");
        roundtripped.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Deserialize_acceptsHandWrittenSnakeCase()
    {
        // Pin the deserialization mapping by parsing a hand-written fixture that uses the
        // documented JSON field names. If any of these names drift in the future, this test
        // is the canary.
        var fixtureJson = """
            {
              "environment": {
                "dotnet_sdk_version": "10.0.0",
                "git_on_path": true,
                "repo_root_path": "/r",
                "solution_files": [],
                "sourcegraph_config_status": "missing",
                "sourcegraph_config_error": null
              },
              "scopes": [],
              "clients": [],
              "embeddings": {
                "model_id": "m",
                "cache_dir": "/c",
                "cache_present": false,
                "total_bytes": 0,
                "verified": false
              },
              "recent_activity": [],
              "built_at": "2025-01-01T00:00:00+00:00",
              "usage_log_path": "/u",
              "heals_log_path": "/h",
              "exit_code": 2
            }
            """;
        var snap = DashboardSnapshotJson.Deserialize(fixtureJson);
        snap.Should().NotBeNull();
        snap!.Environment.SourceGraphConfigStatus.Should().Be("missing");
        snap.ExitCode.Should().Be(2);
        snap.UsageLogPath.Should().Be("/u");
        snap.HealsLogPath.Should().Be("/h");
    }
}
