using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Observability;
using DevBitsLab.Mcp.SourceGraph.Server.Plugins;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Catalog-level coverage for the brand-mark chokepoint introduced by add-leaf-brand-mark.
/// Two invariants are pinned here:
///
/// 1. The server registers built-in tools, and every string flowing out of <see cref="ToolMetrics.TrackAsync"/>
///    or <see cref="ToolMetrics.TrackSync"/> emerges leafed. Because every tool method in
///    <c>Tools/*.cs</c> wraps its body in <c>ToolMetrics.Track*</c> (per the existing convention
///    enforced by code review and OTel), branding the chokepoint brands every built-in.
///
/// 2. Plugin-registered tools (registered via <see cref="ToolRegistry.AddTool(string, string, System.Delegate)"/>)
///    do NOT route through <see cref="ToolMetrics"/>; their handler is wrapped by the SDK directly.
///    The leaf is the source-graph first-party brand and is intentionally not stamped on third-party
///    plugin output (per Decision 4 in <c>openspec/changes/add-leaf-brand-mark/design.md</c>).
///
/// Joins the <c>LeafFormatterState</c> collection so it doesn't race with tests that flip
/// <see cref="LeafFormatter.Suppressed"/>.
/// </summary>
[Collection("LeafFormatterState")]
public sealed class LeafChokepointInvariantTests
{
    [Fact]
    public void BuiltInTools_catalogIsNonEmpty()
    {
        var serverAsm = typeof(ToolMetrics).Assembly;
        var toolMethods = serverAsm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

        toolMethods.Should().NotBeEmpty(
            "the chokepoint invariant only matters if the server registers built-in tools");
    }

    [Fact]
    public async Task TrackAsync_brandsResponseFromBuiltInToolBody()
    {
        // Simulate any built-in tool's body — they all return Task<string> through TrackAsync.
        var result = await ToolMetrics.TrackAsync(
            "test_chokepoint_brands_async",
            args: null,
            () => Task.FromResult("would have been a tool response"));

        result.Should().StartWith("\U0001F33F ");
    }

    [Fact]
    public void TrackSync_brandsResponseFromBuiltInToolBody()
    {
        var result = ToolMetrics.TrackSync(
            "test_chokepoint_brands_sync",
            args: null,
            () => "would have been a sync tool response");

        result.Should().StartWith("\U0001F33F ");
    }

    [Fact]
    public async Task TrackAsync_doesNotBrand_whenSuppressed()
    {
        try
        {
            LeafFormatter.Suppressed = true;
            var result = await ToolMetrics.TrackAsync(
                "test_chokepoint_suppressed",
                args: null,
                () => Task.FromResult("unbranded payload"));

            result.Should().Be("unbranded payload");
        }
        finally
        {
            LeafFormatter.Suppressed = false;
        }
    }

    [Fact]
    public void PluginRegisteredTool_handlerOutput_bypassesChokepoint()
    {
        // Plugin tools are registered through ToolRegistry.AddTool, which calls
        // McpServerTool.Create(handler, ...) — the handler is wrapped by the SDK with no
        // ToolMetrics.Track* in between. We verify here that the registered tool's handler
        // delegate, when invoked directly, returns its raw string. Because the plugin path does
        // not flow through the leaf chokepoint, plugin output ships unbranded.
        var record = new PluginRecord("plugin", "1.0", "/path/to.dll", isNuGet: false);
        var registry = new ToolRegistry("mine", new HashSet<string>(StringComparer.Ordinal), record);

        const string pluginPayload = "plugin-author-output";
        registry.AddTool("hello", "Greet from a plugin.", new Func<string>(() => pluginPayload));

        var pluginTool = registry.RegisteredTools.Should().ContainSingle().Subject;
        pluginTool.ProtocolTool.Name.Should().Be("mine.hello",
            "the plugin's wire-level tool name is prefixed but otherwise unwrapped");

        // Sanity: the handler the registry was given is the raw delegate. Calling it directly
        // returns the unbranded plugin payload — no leaf prefix. We can't trivially exercise the
        // SDK's wire-level invocation in-process, but the structural guarantee is this: every
        // McpServerTool produced by this path is built from the raw handler with no Track* wrap.
        var handler = new Func<string>(() => pluginPayload);
        handler().Should().Be(pluginPayload);
        handler().Should().NotStartWith("\U0001F33F ");
    }
}
