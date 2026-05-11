using System;
using System.IO;
using System.Threading.Tasks;
using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Covers the four scoping invariants required by add-scoping:
///   - one-shot migration of legacy graph.db
///   - synthesised default scope when no .sourcegraph.json present
///   - schema validation rejects malformed configs
///   - scope id validation
/// </summary>
public sealed class ScopeTests
{
    [Fact]
    public void ScopeIdValidator_acceptsKebabCase()
    {
        ScopeIdValidator.IsValid("default").Should().BeTrue();
        ScopeIdValidator.IsValid("frontend").Should().BeTrue();
        ScopeIdValidator.IsValid("foo-bar-baz").Should().BeTrue();
        ScopeIdValidator.IsValid("a1b2").Should().BeTrue();
    }

    [Fact]
    public void ScopeIdValidator_rejectsBadIds()
    {
        ScopeIdValidator.IsValid("").Should().BeFalse();
        ScopeIdValidator.IsValid("Foo").Should().BeFalse(); // uppercase
        ScopeIdValidator.IsValid("-foo").Should().BeFalse(); // leading hyphen
        ScopeIdValidator.IsValid("foo bar").Should().BeFalse(); // space
        ScopeIdValidator.IsValid(new string('a', 65)).Should().BeFalse(); // too long
        ScopeIdValidator.IsValid("foo/bar").Should().BeFalse(); // slash (filename safety)
    }

    [Fact]
    public void ScopeConfigLoader_synthesisesDefaultWhenNoFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var config = ScopeConfigLoader.Load(tmp);
            config.Scopes.Should().HaveCount(1);
            config.Scopes[0].Id.Should().Be("default");
            config.DefaultScope.Should().Be("default");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_readsMultiScopeJson()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [
                    { "name": "frontend", "solutions": ["src/frontend.slnx"] },
                    { "name": "backend",  "solutions": ["src/backend.slnx"], "exclude": ["**/Generated/**"] },
                    { "name": "vendor",   "paths": ["third_party/**/*.csproj"], "isolated": true }
                  ],
                  "default_scope": "backend"
                }
                """);
            var config = ScopeConfigLoader.Load(tmp);
            config.Scopes.Should().HaveCount(3);
            config.DefaultScope.Should().Be("backend");
            config.Scopes[0].ProjectSet.Should().BeOfType<ScopeProjectSet.Solutions>();
            config.Scopes[1].ProjectSet.Should().BeOfType<ScopeProjectSet.Solutions>();
            config.Scopes[2].ProjectSet.Should().BeOfType<ScopeProjectSet.Paths>();
            config.Scopes[2].Isolated.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_emptyScopesArray_returnsEmptyConfigForRecovery()
    {
        // When the user removes the last scope (via CLI `scopes remove` or hand-edit),
        // `.sourcegraph.json` ends up with `"scopes": []`. The loader treats that as a
        // recoverable empty config — not a hard error — so subsequent `scopes add` /
        // dashboard `[N]` can write a fresh scope over the empty file. Throwing here used to
        // leave the user stuck with "malformed .sourcegraph.json" on every load.
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"), """
                { "scopes": [] }
                """);
            var loaded = ScopeConfigLoader.Load(tmp);
            loaded.Scopes.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_emptyScopes_addScopeThenRoundTrip_persistsOnlyTheNewScope()
    {
        // End-to-end recovery: load empty -> add -> save -> reload should show exactly one
        // scope (the new one), no synth-default sneaking in.
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"), """{ "scopes": [] }""");
            File.WriteAllText(Path.Combine(tmp, "x.slnx"), "<Solution />");
            var emptyConfig = ScopeConfigLoader.Load(tmp);
            var result = Server.Cli.ScopesCli.AddScopeToConfig(tmp, emptyConfig, "fresh", "x.slnx", isolated: false);
            result.Ok.Should().BeTrue();
            var reloaded = ScopeConfigLoader.Load(tmp);
            reloaded.Scopes.Should().HaveCount(1);
            reloaded.Scopes[0].Name.Should().Be("fresh");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_emptyScopesArray_preservesPluginsAndNullsDefaultScope()
    {
        // Recovery path invariants: plugins[] survives (no silent data loss), and
        // default_scope is null'd out so callers don't follow a stale id into a deleted scope.
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"), """
                {
                  "scopes": [],
                  "default_scope": "removed-scope",
                  "plugins": [
                    { "package": "MyOrg.MyPlugin", "version": "1.2.3" }
                  ]
                }
                """);
            var loaded = ScopeConfigLoader.Load(tmp);
            loaded.Scopes.Should().BeEmpty();
            loaded.DefaultScope.Should().BeNull("a default_scope pointing at a deleted scope is more dangerous than silently dropping it during recovery");
            loaded.Plugins.Should().HaveCount(1);
            loaded.Plugins![0].Package.Should().Be("MyOrg.MyPlugin");
            loaded.Plugins![0].Version.Should().Be("1.2.3");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsMalformedJson()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"), "{ this is not json }");
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*not valid JSON*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsDuplicateNames()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [
                    { "name": "foo", "solutions": ["a.slnx"] },
                    { "name": "foo", "solutions": ["b.slnx"] }
                  ]
                }
                """);
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*more than once*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsInvalidId()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [
                    { "name": "Bad-ID", "solutions": ["a.slnx"] }
                  ]
                }
                """);
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*invalid id*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeConfigLoader_rejectsUnknownDefaultScope()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, ".sourcegraph.json"),
                """
                {
                  "scopes": [{ "name": "foo", "solutions": ["a.slnx"] }],
                  "default_scope": "missing"
                }
                """);
            var act = () => ScopeConfigLoader.Load(tmp);
            act.Should().Throw<ScopeConfigException>().WithMessage("*default_scope*missing*");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeLayout_migratesLegacyDb()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Stand up a legacy graph.db at the old layout (any non-empty file works for the rename).
            var sourcegraphDir = ScopeLayout.SourcegraphDir(tmp);
            Directory.CreateDirectory(sourcegraphDir);
            var legacy = ScopeLayout.LegacyDbPath(tmp);
            File.WriteAllBytes(legacy, new byte[] { 0x01, 0x02 });

            var migrated = ScopeLayout.MigrateLegacyDb(tmp);

            migrated.Should().BeTrue();
            File.Exists(legacy).Should().BeFalse("legacy file should be moved, not copied");
            File.Exists(ScopeLayout.ScopeDbPath(tmp, "default")).Should().BeTrue();

            // Idempotency: second invocation is a no-op (legacy is already gone).
            ScopeLayout.MigrateLegacyDb(tmp).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ScopeLayout_skipsMigrationWhenDestinationExists()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Both legacy and new exist: don't clobber the new file.
            var sourcegraphDir = ScopeLayout.SourcegraphDir(tmp);
            Directory.CreateDirectory(ScopeLayout.ScopesDirectory(tmp));
            File.WriteAllBytes(ScopeLayout.LegacyDbPath(tmp), new byte[] { 0x01 });
            File.WriteAllBytes(ScopeLayout.ScopeDbPath(tmp, "default"), new byte[] { 0x02, 0x03 });

            var migrated = ScopeLayout.MigrateLegacyDb(tmp);

            migrated.Should().BeFalse("destination already exists");
            File.Exists(ScopeLayout.LegacyDbPath(tmp)).Should().BeTrue("legacy preserved");
            File.ReadAllBytes(ScopeLayout.ScopeDbPath(tmp, "default")).Length.Should().Be(2, "destination unchanged");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task ScopeRegistry_persistsAndLoadsRows()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "scope-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var dbPath = Path.Combine(tmp, "_meta.db");
            await using var registry = new SqliteScopeRegistry(dbPath);
            await registry.EnsureSchemaAsync();

            await registry.UpsertAsync(new ScopeRow(
                Id: "foo",
                Name: "foo",
                Root: tmp,
                ProjectSetJson: "{\"Kind\":\"solutions\",\"Items\":[\"a.slnx\"],\"Exclude\":null}",
                Isolated: false,
                LastIndexedAt: DateTimeOffset.UtcNow,
                Status: "ok"));

            var rows = await registry.ListAsync();
            rows.Should().HaveCount(1);
            rows[0].Id.Should().Be("foo");

            var single = await registry.GetAsync("foo");
            single.Should().NotBeNull();
            single!.Status.Should().Be("ok");

            await registry.RemoveAsync("foo");
            (await registry.ListAsync()).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
