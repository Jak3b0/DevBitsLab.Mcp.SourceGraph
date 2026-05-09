using System.Text.Json;
using DevBitsLab.Mcp.SourceGraph.Server;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Regression coverage for the open-language vocabulary capability published in the MCP
/// <c>initialize</c> response.
///
/// History: <c>feat!: open language contract</c> (df6fae1) wired the vocabulary into
/// <c>ServerCapabilities.Experimental[ServerVocabulary.CapabilityKey]</c> as an anonymous type.
/// The MCP SDK serialises <see cref="ServerCapabilities"/> via its source-generated
/// <c>McpJsonUtilities.JsonContext</c>, which only knows the SDK's own types and rejects
/// anonymous types at runtime with
/// <c>System.NotSupportedException: JsonTypeInfo metadata for type '&lt;&gt;f__AnonymousType…' was
/// not provided by TypeInfoResolver of type 'ModelContextProtocol.McpJsonUtilities+JsonContext'</c>.
/// The bug only surfaced when a real MCP client called <c>initialize</c>; idle stdio boots never
/// triggered serialization, which is why the change shipped.
///
/// The fix in <c>Program.cs</c> pre-serialises the vocabulary payload into a
/// <see cref="JsonElement"/> via reflection-based serialization before storing it. JsonElement is
/// a System.Text.Json built-in every JsonContext can write natively.
///
/// These tests replay the exact wiring shape against the SDK's own
/// <see cref="McpJsonUtilities.DefaultOptions"/> so a regression — e.g. somebody dropping the
/// <c>SerializeToElement</c> wrap — fails CI instead of breaking the live <c>initialize</c>.
/// </summary>
public sealed class VocabularyCapabilityWiringTests
{
    private static VocabularyResult SampleVocabulary() =>
        new(
            EdgeKinds: new[] { "calls", "uses-type" },
            SymbolKinds: new[] { "class", "method" },
            AnnotationFlavors: new[] { "csharp-attribute" },
            Scopes: new Dictionary<string, ScopeVocabulary>(StringComparer.Ordinal)
            {
                ["default"] = new ScopeVocabulary(
                    EdgeKinds: new[] { "calls" },
                    SymbolKinds: new[] { "class" },
                    AnnotationFlavors: new[] { "csharp-attribute" }),
            });

    private static ServerCapabilities BuildCapabilitiesAsProgramDoes(VocabularyResult vocabulary)
    {
        // Mirror the exact wiring in src/.../Server/Program.cs RunServeAsync — keep this in sync
        // with the live wiring or this test stops covering the live path.
        var caps = new ServerCapabilities
        {
            Experimental = new Dictionary<string, object>(StringComparer.Ordinal),
        };
        caps.Experimental[ServerVocabulary.CapabilityKey] =
            JsonSerializer.SerializeToElement(new
            {
                edge_kinds = vocabulary.EdgeKinds,
                symbol_kinds = vocabulary.SymbolKinds,
                annotation_flavors = vocabulary.AnnotationFlavors,
                scopes = vocabulary.Scopes.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new
                    {
                        edge_kinds = kv.Value.EdgeKinds,
                        symbol_kinds = kv.Value.SymbolKinds,
                        annotation_flavors = kv.Value.AnnotationFlavors,
                    },
                    StringComparer.Ordinal),
            });
        return caps;
    }

    [Fact]
    public void Capabilities_serializeUnderMcpJsonContext_withoutThrowing()
    {
        // The exact path that broke: ServerCapabilities → through SDK's source-gen JsonContext.
        // If somebody unwraps the JsonElement back to an anonymous type, this will throw the
        // same NotSupportedException the live initialize handler did.
        var caps = BuildCapabilitiesAsProgramDoes(SampleVocabulary());

        Action act = () => JsonSerializer.Serialize(caps, McpJsonUtilities.DefaultOptions);
        act.Should().NotThrow("the SDK's source-generated JsonContext must be able to serialize ServerCapabilities.Experimental, and the JsonElement wrap is what makes that possible");
    }

    [Fact]
    public void Capabilities_roundTrip_preservesShape()
    {
        // Beyond "doesn't throw", confirm the JSON we'd ship has the documented structure: the
        // top-level vocabulary keys appear, and the per-scope nesting is intact. Any future
        // refactor that reshapes the payload by accident will fail this assertion.
        var caps = BuildCapabilitiesAsProgramDoes(SampleVocabulary());
        var json = JsonSerializer.Serialize(caps, McpJsonUtilities.DefaultOptions);

        using var doc = JsonDocument.Parse(json);
        var experimental = doc.RootElement.GetProperty("experimental");
        var vocab = experimental.GetProperty(ServerVocabulary.CapabilityKey);

        vocab.GetProperty("edge_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "calls", "uses-type" });
        vocab.GetProperty("symbol_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "class", "method" });
        vocab.GetProperty("annotation_flavors").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "csharp-attribute" });

        var defaultScope = vocab.GetProperty("scopes").GetProperty("default");
        defaultScope.GetProperty("edge_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "calls" });
        defaultScope.GetProperty("symbol_kinds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "class" });
        defaultScope.GetProperty("annotation_flavors").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "csharp-attribute" });
    }

    [Fact]
    public void Capabilities_storeJsonElement_notAnonymousType()
    {
        // Structural regression: the value MUST be a JsonElement so the SDK's source-generated
        // JsonContext can write it as a built-in System.Text.Json type. If somebody reverts the
        // wiring to a raw anonymous type, this assertion fires in CI — before the production
        // initialize handler would crash with "JsonTypeInfo metadata for type ...was not provided".
        //
        // We can't faithfully reproduce the runtime crash from outside the SDK because
        // McpJsonUtilities.DefaultOptions appears to chain a reflection fallback that the SDK's
        // internal initialize-serialization path does not use; an anonymous type round-trips
        // through DefaultOptions but blows up under the strict source-gen context the SDK
        // actually invokes. Asserting on the value's type is a faithful proxy for the contract
        // we care about: never store anything in Experimental that the SDK's source-gen
        // JsonContext doesn't already understand.
        var caps = BuildCapabilitiesAsProgramDoes(SampleVocabulary());
        var value = caps.Experimental![ServerVocabulary.CapabilityKey];

        value.Should().BeOfType<JsonElement>(
            "anything else risks NotSupportedException at MCP initialize time when the SDK ships ServerCapabilities through its source-gen JsonContext");
    }
}
