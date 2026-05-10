using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Storage;
using DevBitsLab.Mcp.SourceGraph.Watcher;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Covers the file-watch + parse-tolerance contract of <see cref="ScopeConfigWatcher"/>:
///   - parse-tolerance on malformed JSON (no event emitted)
///   - file deletion → revert to synthesised default
///   - present and parseable → Updated event with the parsed config
/// </summary>
public sealed class ScopeConfigWatcherTests
{
    private static readonly TimeSpan ShortDebounce = TimeSpan.FromMilliseconds(60);

    private static string MakeTempRoot()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-watcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    private static async Task<ScopeConfigChange?> ReadOneWithTimeoutAsync(
        ScopeConfigWatcher watcher, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var change in watcher.ReadAllAsync(cts.Token))
            {
                return change;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task ValidSave_emitsUpdated()
    {
        var root = MakeTempRoot();
        try
        {
            await using var watcher = new ScopeConfigWatcher(root, debounce: ShortDebounce);
            File.WriteAllText(
                Path.Combine(root, ScopeConfigLoader.FileName),
                """{ "scopes": [ { "name": "foo", "solutions": ["foo.sln"] } ], "default_scope": "foo" }""");

            var change = await ReadOneWithTimeoutAsync(watcher, TimeSpan.FromSeconds(2));
            change.Should().NotBeNull();
            change.Should().BeOfType<ScopeConfigChange.Updated>();
            change!.Config.Scopes.Should().ContainSingle().Which.Id.Should().Be("foo");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task MalformedSave_doesNotEmit()
    {
        var root = MakeTempRoot();
        try
        {
            await using var watcher = new ScopeConfigWatcher(root, debounce: ShortDebounce);
            File.WriteAllText(Path.Combine(root, ScopeConfigLoader.FileName), "{ this is not json");
            var change = await ReadOneWithTimeoutAsync(watcher, TimeSpan.FromSeconds(1));
            // Parse fails → log info, no event. The timeout path is the test pass.
            change.Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task FileDeletedAfterValidSave_emitsReverted()
    {
        var root = MakeTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, ScopeConfigLoader.FileName),
                """{ "scopes": [ { "name": "foo", "solutions": ["foo.sln"] } ] }""");

            await using var watcher = new ScopeConfigWatcher(
                root,
                discoveredSolutions: Array.Empty<string>(),
                debounce: ShortDebounce);

            // Drain the initial create event (the file was already there when the watcher started
            // — actually it should NOT trigger anything because EnableRaisingEvents flips on
            // *after* the file write happens above. The OS won't replay history. To force an event
            // we modify the file once, then delete, and look for the deletion-driven change.)
            File.WriteAllText(
                Path.Combine(root, ScopeConfigLoader.FileName),
                """{ "scopes": [ { "name": "foo", "solutions": ["foo.sln"] }, { "name": "bar", "solutions": ["bar.sln"] } ] }""");
            var firstChange = await ReadOneWithTimeoutAsync(watcher, TimeSpan.FromSeconds(2));
            firstChange.Should().NotBeNull();

            File.Delete(Path.Combine(root, ScopeConfigLoader.FileName));
            var change = await ReadOneWithTimeoutAsync(watcher, TimeSpan.FromSeconds(2));
            change.Should().NotBeNull();
            change.Should().BeOfType<ScopeConfigChange.Reverted>();
            change!.Config.Scopes.Should().ContainSingle().Which.Id.Should().Be("default");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task DisposeAsync_completesQuickly()
    {
        var root = MakeTempRoot();
        try
        {
            var watcher = new ScopeConfigWatcher(root, debounce: ShortDebounce);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await watcher.DisposeAsync();
            sw.Stop();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "dispose should not hang waiting for events that won't come");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
