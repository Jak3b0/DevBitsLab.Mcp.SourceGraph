using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Indexing;
using DevBitsLab.Mcp.SourceGraph.Server.Scoping;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using DevBitsLab.Mcp.SourceGraph.Server.Tools.Output;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Verifies <c>list_scopes</c> rendering and structured output for partial-status scopes.
/// Synthesises a <see cref="ScopeHost"/> with non-empty <c>FailedProjects</c> /
/// <c>FailedFiles</c> and asserts that:
/// <list type="bullet">
///   <item>The status cell renders the partial status + message inline.</item>
///   <item>A "Failed projects / files (last index)" sub-list is emitted with the failure detail.</item>
///   <item>The structured output's <c>failed_projects</c> / <c>failed_files</c> arrays carry
///         the same data so MCP clients consuming <c>structuredContent</c> see it without
///         parsing prose.</item>
///   <item>Healthy scopes' rendering stays unchanged — no failure sub-list, empty arrays.</item>
/// </list>
/// </summary>
public sealed class ListScopesPartialOutputTests : IAsyncLifetime
{
    private string _tmpDir = string.Empty;
    private SqliteGraphStore? _store;
    private RoslynIndexer? _indexer;

    public async Task InitializeAsync()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "list-scopes-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _store = new SqliteGraphStore(Path.Combine(_tmpDir, "graph.db"));
        await _store.EnsureSchemaAsync();
        _indexer = new RoslynIndexer(_store);
    }

    public async Task DisposeAsync()
    {
        if (_indexer is not null) await _indexer.DisposeAsync();
        if (_store is not null) await _store.DisposeAsync();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Build a minimally-populated <see cref="ScopeHost"/> without running an index. The host's
    /// store/indexer are real instances so the disposal chain works; FailedProjects /
    /// FailedFiles / Status are set directly via the public setters because we're testing the
    /// rendering, not the indexing pipeline that produces them.
    /// </summary>
    private ScopeHost BuildHost(
        string id,
        string status,
        string? statusMessage,
        IReadOnlyList<ProjectFailure>? failedProjects = null,
        IReadOnlyList<FileFailure>? failedFiles = null)
    {
        var scope = new Scope(
            Id: id,
            Name: id,
            Root: _tmpDir,
            ProjectSet: new ScopeProjectSet.Solutions(
                Items: new[] { "synthetic.sln" },
                Exclude: Array.Empty<string>()),
            Isolated: false,
            LastIndexedAt: DateTimeOffset.UtcNow);
        var host = new ScopeHost(
            scope,
            _store!,
            new DisabledEmbeddingsStore(dimension: 0),
            _indexer!,
            solutionPath: Path.Combine(_tmpDir, "synthetic.sln"))
        {
            Status = status,
            StatusMessage = statusMessage,
            FailedProjects = failedProjects ?? Array.Empty<ProjectFailure>(),
            FailedFiles = failedFiles ?? Array.Empty<FileFailure>(),
            LastIndexedAt = DateTimeOffset.UtcNow,
        };
        host.MarkReady();
        return host;
    }

    private static (string Prose, ListScopesResult Structured) RenderViaTool(ScopeRouter router)
    {
        // ListScopesAsync is async only because it routes through ToolMetrics.TrackAsync. The
        // body is synchronous. Wait synchronously here — we know the future is already settled
        // by the time TrackAsync wraps it (Task.FromResult).
        var task = ScopeTools.ListScopesAsync(router);
        task.Wait();
        var result = task.Result;

        // The result has two content blocks in order: (1) the markdown table+failure detail,
        // (2) the audience-restricted metadata block (latency/scopes count). Grab the first
        // non-audience text block to assert against the user-visible prose.
        var prose = "";
        if (result.Content is { } blocks)
        {
            foreach (var block in blocks)
            {
                if (block is TextContentBlock t)
                {
                    var isAudienceMeta = t.Annotations?.Audience is { Count: > 0 };
                    if (!isAudienceMeta)
                    {
                        prose = t.Text;
                        break;
                    }
                }
            }
        }
        // The tool's structured content is serialised by ToolOutputJsonContext with a snake_case
        // property naming policy. Mirror that here so deserialization picks up the right names.
        var deserOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        var structured = result.StructuredContent.HasValue
            ? JsonSerializer.Deserialize<ListScopesResult>(result.StructuredContent.Value.GetRawText(), deserOpts)!
            : throw new InvalidOperationException("list_scopes did not emit structuredContent");
        return (prose, structured);
    }

    [Fact]
    public void Partial_scope_renders_failed_projects_and_files_in_prose_and_structured()
    {
        var router = new ScopeRouter();
        var failedProjects = new[]
        {
            new ProjectFailure("Legacy.WebForms", "compilation null"),
        };
        var failedFiles = new[]
        {
            new FileFailure("/repo/Quirky.cs", "Pass 1 walk failed: NullReferenceException at GetSemanticModelAsync"),
        };
        router.Register(BuildHost(
            id: "backend",
            status: "partial",
            statusMessage: "1 project(s), 1 file(s) failed to index.",
            failedProjects: failedProjects,
            failedFiles: failedFiles));

        var (prose, structured) = RenderViaTool(router);

        // Markdown: status cell shows partial(message); failure sub-list carries each entry.
        prose.Should().Contain("partial (1 project(s), 1 file(s) failed to index.)",
            "the status cell must surface the status + message inline so operators reading the table see why");
        prose.Should().Contain("**Failed projects / files (last index):**",
            "non-empty failure lists trigger the sub-list section");
        prose.Should().Contain("`Legacy.WebForms`", "failed project name must be in the prose");
        prose.Should().Contain("compilation null", "failed project reason must be in the prose");
        prose.Should().Contain("`/repo/Quirky.cs`", "failed file path must be in the prose");
        prose.Should().Contain("Pass 1 walk failed", "failed file reason must be in the prose");

        // StructuredContent: typed shape carries the same data for programmatic consumers.
        structured.Scopes.Should().ContainSingle();
        var row = structured.Scopes[0];
        row.Status.Should().Be("partial");
        row.StatusMessage.Should().Be("1 project(s), 1 file(s) failed to index.");
        row.FailedProjects.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ListScopesProjectFailure("Legacy.WebForms", "compilation null"));
        row.FailedFiles.Should().ContainSingle()
            .Which.Path.Should().Be("/repo/Quirky.cs");
    }

    [Fact]
    public void Healthy_scope_renders_without_failure_sublist_and_with_empty_arrays()
    {
        var router = new ScopeRouter();
        router.Register(BuildHost(id: "frontend", status: "ok", statusMessage: null));

        var (prose, structured) = RenderViaTool(router);

        // Healthy scopes don't trigger the failure sub-list — keeps the prose clean.
        prose.Should().NotContain("**Failed projects / files (last index):**",
            "healthy scopes must not emit the failure sub-list section");
        prose.Should().NotContain("partial",
            "healthy scopes must not show partial in the status cell");

        // Structured output is consistent: empty arrays, not omitted, so consumers can probe
        // `failed_projects.length === 0` rather than handling missing properties.
        structured.Scopes.Should().ContainSingle()
            .Which.Status.Should().Be("ok");
        structured.Scopes[0].FailedProjects.Should().BeEmpty();
        structured.Scopes[0].FailedFiles.Should().BeEmpty();
    }

    [Fact]
    public void Mixed_scope_set_only_emits_failure_sublist_for_partial_scopes()
    {
        var router = new ScopeRouter();
        router.Register(BuildHost(id: "frontend", status: "ok", statusMessage: null));
        router.Register(BuildHost(
            id: "backend",
            status: "partial",
            statusMessage: "1 project failed.",
            failedProjects: new[] { new ProjectFailure("Bad.Project", "compilation null") }));

        var (prose, structured) = RenderViaTool(router);

        // Only the partial scope shows a failure entry.
        prose.Should().Contain("**Failed projects / files (last index):**");
        prose.Should().Contain("`Bad.Project`");
        // The healthy scope's row in the table doesn't get a failure-detail section.
        var failureSectionStart = prose.IndexOf("**Failed projects / files (last index):**", StringComparison.Ordinal);
        prose.Substring(failureSectionStart).Should().NotContain("- `frontend`:",
            "the healthy scope must not appear under the failure sub-list");

        structured.Scopes.Should().HaveCount(2);
    }
}
