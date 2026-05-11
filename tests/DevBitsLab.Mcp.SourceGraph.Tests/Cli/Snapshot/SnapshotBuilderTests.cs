using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Cli.Snapshot;

/// <summary>
/// Round-trip tests for <see cref="SnapshotBuilder"/>. Each test builds a deterministic fixture
/// repo on disk (temp directory, real SQLite files, real JSONL contents), invokes
/// <see cref="SnapshotBuilder.BuildAsync"/>, and asserts that every surface populates from the
/// expected source.
///
/// <para>
/// The builder opens SQLite handles in read-only mode and closes them before returning, so
/// these tests can re-build the same fixture multiple times without lock contention.
/// </para>
/// </summary>
public sealed class SnapshotBuilderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _scopesDir;

    public SnapshotBuilderTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _scopesDir = ScopeLayout.ScopesDirectory(_tempRoot);
        Directory.CreateDirectory(_scopesDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task BuildAsync_emptyRepo_returnsSnapshotWithDefaultExitCode()
    {
        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
        snap.Environment.RepoRootPath.Should().Be(Path.GetFullPath(_tempRoot));
        snap.Scopes.Should().BeEmpty();
        // Clients always include user-scope detection (may be present if dev has them at home);
        // assert the array shape is reasonable rather than empty.
        snap.Clients.Should().NotBeNull();
        snap.RecentActivity.Should().BeEmpty();
        snap.ExitCode.Should().Be(0, "the builder defaults exit_code; the caller fills it in");
        snap.UsageLogPath.Should().EndWith(".sourcegraph" + Path.DirectorySeparatorChar + "usage.jsonl");
        snap.HealsLogPath.Should().EndWith(".sourcegraph" + Path.DirectorySeparatorChar + "heals.jsonl");
    }

    [Fact]
    public async Task BuildAsync_environment_pinsSolutionFiles()
    {
        File.WriteAllText(Path.Join(_tempRoot, "MyApp.slnx"), "<Solution/>");
        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
        snap.Environment.SolutionFiles.Should().ContainSingle()
            .Which.Should().EndWith("MyApp.slnx");
        snap.Environment.SourceGraphConfigStatus.Should().Be("missing");
    }

    [Fact]
    public async Task BuildAsync_environment_capturesMalformedConfig()
    {
        File.WriteAllText(Path.Join(_tempRoot, ".sourcegraph.json"), "{ broken json");
        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
        snap.Environment.SourceGraphConfigStatus.Should().Be("malformed");
        snap.Environment.SourceGraphConfigError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BuildAsync_scopes_readsRegistryRows()
    {
        // Stand up _meta.db with two scopes: an "ok" frontend and a "partial" backend.
        await using (var registry = new SqliteScopeRegistry(ScopeLayout.MetaDbPath(_tempRoot)))
        {
            await registry.EnsureSchemaAsync();
            await registry.UpsertAsync(new Storage.ScopeRow(
                Id: "frontend",
                Name: "Frontend",
                Root: _tempRoot,
                ProjectSetJson: "{}",
                Isolated: false,
                LastIndexedAt: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
                Status: "ok",
                StatusMessage: null,
                FailedProjects: Array.Empty<ProjectFailure>(),
                FailedFiles: Array.Empty<FileFailure>()));
            await registry.UpsertAsync(new Storage.ScopeRow(
                Id: "backend",
                Name: "Backend",
                Root: _tempRoot,
                ProjectSetJson: "{}",
                Isolated: false,
                LastIndexedAt: new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero),
                Status: "partial",
                StatusMessage: "1 project failed",
                FailedProjects: new[] { new ProjectFailure("Bad.csproj", "compilation null") },
                FailedFiles: Array.Empty<FileFailure>()));
        }

        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
        snap.Scopes.Should().HaveCount(2);

        var backend = snap.Scopes.Single(s => s.Name == "Backend");
        backend.Status.Should().Be("partial");
        backend.FailedProjects.Should().ContainSingle().Which.Should().Contain("Bad.csproj");
        backend.LastIndexedAt.Should().Be(new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero));

        var frontend = snap.Scopes.Single(s => s.Name == "Frontend");
        frontend.Status.Should().Be("ok");
        frontend.FailedProjects.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_scopes_pullsSymbolAndRefCountsFromPerScopeDb()
    {
        // Stand up _meta.db with a default scope plus a per-scope DB containing some rows.
        await using (var registry = new SqliteScopeRegistry(ScopeLayout.MetaDbPath(_tempRoot)))
        {
            await registry.EnsureSchemaAsync();
            await registry.UpsertAsync(new Storage.ScopeRow(
                Id: "default",
                Name: "default",
                Root: _tempRoot,
                ProjectSetJson: "{}",
                Isolated: false,
                LastIndexedAt: DateTimeOffset.UtcNow,
                Status: "ok",
                StatusMessage: null,
                FailedProjects: Array.Empty<ProjectFailure>(),
                FailedFiles: Array.Empty<FileFailure>()));
        }

        var dbPath = ScopeLayout.ScopeDbPath(_tempRoot, "default");
        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.EnsureSchemaAsync();
            var fileId = await store.UpsertFileAsync(
                Path.Join(_tempRoot, "A.cs"),
                contentSha256: new byte[32],
                indexedAt: DateTimeOffset.UtcNow);
            var symId = await store.UpsertSymbolAsync(
                canonicalKey: "csharp:T:A",
                new Symbol(
                    Id: 0, Name: "A", Fqn: "A", Kind: "class",
                    FileId: fileId, StartLine: 1, StartCol: 1, EndLine: 1, EndCol: 1,
                    Signature: null, ContainerId: null));
            await store.BulkInsertReferencesAsync(new[]
            {
                new SymbolReference(Id: 0, SymbolId: symId, FileId: fileId, Line: 2, Col: 1, Kind: ReferenceKind.Call),
            });
        }

        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
        var def = snap.Scopes.Single(s => s.Name == "default");
        def.SymbolCount.Should().Be(1);
        def.ReferenceCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildAsync_recentActivity_mergesAndSortsBothLogs()
    {
        var dotDir = Path.Join(_tempRoot, ScopeLayout.DotDir);
        Directory.CreateDirectory(dotDir);
        var usagePath = Path.Join(dotDir, "usage.jsonl");
        var healsPath = Path.Join(dotDir, "heals.jsonl");

        // Write timestamps T1 < T2 < T3 < T4 < T5 across both files so the merged result must
        // be ascending regardless of file order.
        File.WriteAllText(usagePath,
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:00Z", tool = "search_symbols", ok = true, ms = 5, scope = "default" }) + "\n" +
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:30Z", tool = "find_definition", ok = true, ms = 8, scope = "default" }) + "\n" +
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:01:00Z", tool = "graph_stats", ok = true, ms = 2, scope = "default" }) + "\n");
        File.WriteAllText(healsPath,
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:15Z", kind = "boot_reconcile", scope = "default", ok = true, ms = 100, details = "ready" }) + "\n" +
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:45Z", kind = "heal", scope = "default", ok = true, ms = 50, details = "drift" }) + "\n");

        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
        snap.RecentActivity.Should().HaveCount(5);
        var times = snap.RecentActivity.Select(a => a.Ts).ToList();
        times.Should().BeInAscendingOrder();
        snap.RecentActivity.Select(a => a.Kind).Should().Contain(new[] { "tool_call", "boot_reconcile", "heal" });
    }

    [Fact]
    public async Task BuildAsync_recentActivity_tolerablePartialTrailingLine()
    {
        var dotDir = Path.Join(_tempRoot, ScopeLayout.DotDir);
        Directory.CreateDirectory(dotDir);
        var usagePath = Path.Join(dotDir, "usage.jsonl");

        // Two complete lines + a partial (no trailing newline, half-object).
        File.WriteAllText(usagePath,
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:00Z", tool = "search_symbols", ok = true, ms = 5, scope = "default" }) + "\n" +
            JsonSerializer.Serialize(new { ts = "2025-01-01T12:00:30Z", tool = "graph_stats", ok = true, ms = 2, scope = "default" }) + "\n" +
            "{\"ts\":\"2025-01-01T12:01");  // intentionally truncated

        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
        // The partial line is dropped; two complete entries remain.
        snap.RecentActivity.Should().HaveCount(2);
    }

    [Fact]
    public async Task BuildAsync_recentActivity_capsAtRecentActivityCap()
    {
        var dotDir = Path.Join(_tempRoot, ScopeLayout.DotDir);
        Directory.CreateDirectory(dotDir);
        var usagePath = Path.Join(dotDir, "usage.jsonl");

        // 100 entries, ascending timestamps. With cap = 10, only the most recent 10 stay.
        using (var w = new StreamWriter(usagePath))
        {
            var start = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 100; i++)
            {
                var ts = start.AddSeconds(i).ToString("O");
                w.WriteLine(JsonSerializer.Serialize(new { ts, tool = $"t{i}", ok = true, ms = 1, scope = "default" }));
            }
        }

        var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions(RecentActivityCap: 10));
        snap.RecentActivity.Should().HaveCount(10);
        // The cap keeps the latest entries — last one has tool == "t99".
        snap.RecentActivity[^1].Detail.Should().Be("t99");
    }

    [Fact]
    public async Task BuildAsync_concurrent_writes_tolerated()
    {
        // Spawn a writer that appends to usage.jsonl while BuildAsync runs ten times in a loop;
        // assert no exception, monotonically-ordered activity within each result.
        var dotDir = Path.Join(_tempRoot, ScopeLayout.DotDir);
        Directory.CreateDirectory(dotDir);
        var usagePath = Path.Join(dotDir, "usage.jsonl");

        using var cts = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                var ts = DateTimeOffset.UtcNow.ToString("O");
                var line = JsonSerializer.Serialize(new { ts, tool = $"t{i++}", ok = true, ms = 1 }) + "\n";
                try
                {
                    using var fs = new FileStream(usagePath, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Delete);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    await fs.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
                catch (IOException) { /* file share race; retry */ }
                await Task.Delay(1).ConfigureAwait(false);
            }
        });

        try
        {
            for (var run = 0; run < 10; run++)
            {
                var snap = await SnapshotBuilder.BuildAsync(_tempRoot, new SnapshotOptions());
                // Even with the concurrent writer, no exception; activity should be ordered.
                snap.RecentActivity.Select(a => a.Ts).Should().BeInAscendingOrder();
            }
        }
        finally
        {
            cts.Cancel();
            try { await writer; } catch (OperationCanceledException) { }
        }
    }
}
