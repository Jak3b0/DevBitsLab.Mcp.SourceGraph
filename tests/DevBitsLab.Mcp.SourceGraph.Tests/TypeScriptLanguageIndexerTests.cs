using System.Linq;
using System.Text;
using DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// End-to-end coverage for <see cref="TypeScriptLanguageIndexer"/> against the bundled
/// TypeScript / TSX / JavaScript grammars. Validates declarations, references, and JSX
/// instantiates-edge emission. Cross-file resolution is intentionally not exercised at this
/// version — module resolver wiring lands in a follow-up.
/// </summary>
public sealed class TypeScriptLanguageIndexerTests
{
    [Fact]
    public async Task Indexes_typescript_function_declaration()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes("export function greet(name: string): string { return name; }");
        var ctx = new IndexContext("/repo/src/foo.ts", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);
        var declaration = events.OfType<IndexEvent.SymbolDeclared>().Should().ContainSingle().Subject;

        declaration.Name.Should().Be("greet");
        declaration.Kind.Should().Be("method");
        declaration.CanonicalKey.Should().Be("ts:M:src/foo.ts::greet");
    }

    [Fact]
    public async Task Indexes_typescript_class_with_method()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes("export class Counter { tick() { return 1; } }");
        var ctx = new IndexContext("/repo/src/counter.ts", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);
        var symbols = events.OfType<IndexEvent.SymbolDeclared>().ToList();

        symbols.Should().Contain(s => s.Name == "Counter" && s.Kind == "class");
        symbols.Should().Contain(s => s.Name == "tick" && s.Kind == "method");
    }

    [Fact]
    public async Task Indexes_typescript_interface_and_type_alias()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes(
            "export interface User { name: string; }\nexport type UserId = string;");
        var ctx = new IndexContext("/repo/src/types.ts", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);
        var symbols = events.OfType<IndexEvent.SymbolDeclared>().ToList();

        symbols.Should().Contain(s => s.Name == "User" && s.Kind == "interface");
        symbols.Should().Contain(s => s.Name == "UserId" && s.Kind == "type-alias");
    }

    [Fact]
    public async Task Distinguishes_const_from_variable_in_lexical_declaration()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes("const API_BASE = 'https://example.com'; let counter = 0;");
        var ctx = new IndexContext("/repo/src/config.ts", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);
        var symbols = events.OfType<IndexEvent.SymbolDeclared>().ToList();

        symbols.Should().Contain(s => s.Name == "API_BASE" && s.Kind == "constant");
        symbols.Should().Contain(s => s.Name == "counter" && s.Kind == "variable");
    }

    [Fact]
    public async Task Tsx_grammar_parses_jsx_and_emits_instantiates_edge_for_pascal_components()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes(
            "function Page() { return <Button onClick={handler} disabled />; }");
        var ctx = new IndexContext("/repo/src/page.tsx", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        var edge = events.OfType<IndexEvent.EdgeEmitted>()
            .Should().ContainSingle(e => e.EdgeKindName == "instantiates" && e.TargetCanonicalKey.Contains("Button")).Subject;
        edge.Metadata.Should().NotBeNull();

        // Props payload is JSON-encoded so the GraphStoreEmitter's `payload: { ... }` markdown
        // sub-line can render a structured list and consumers can parse it back to an array
        // without worrying about embedded-comma escapes.
        var propsJson = edge.Metadata!["props"];
        propsJson.Should().StartWith("[").And.EndWith("]");
        var parsedProps = System.Text.Json.JsonSerializer.Deserialize<string[]>(propsJson)!;
        parsedProps.Should().BeEquivalentTo(new[] { "onClick", "disabled" });

        // The source canonical key is the enclosing function (`Page`), not the file-namespace.
        // The matching SymbolDeclared comes from the walk's normal declaration dispatch — no
        // synthetic symbol is needed when the JSX is inside a function/method/class.
        edge.SourceCanonicalKey.Should().EndWith("::Page",
            "the JSX edge's source is the nearest enclosing declaration, not the file");
        events.OfType<IndexEvent.SymbolDeclared>()
            .Should().Contain(s => s.CanonicalKey == edge.SourceCanonicalKey && s.Name == "Page");
    }

    [Fact]
    public async Task Jsx_inside_const_targets_the_lexical_declaration_as_source()
    {
        var indexer = new TypeScriptLanguageIndexer();
        // Common modern-React idiom: a const-bound arrow component. Walk-up from JSX should
        // skip the unnamed `arrow_function` and stop at the `lexical_declaration` which maps
        // to a variable kind.
        var bytes = Encoding.UTF8.GetBytes("export const Root = () => <App />;");
        var ctx = new IndexContext("/repo/src/main.tsx", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        var edge = events.OfType<IndexEvent.EdgeEmitted>()
            .Should().ContainSingle(e => e.EdgeKindName == "instantiates" && e.TargetCanonicalKey.Contains("App")).Subject;
        edge.SourceCanonicalKey.Should().EndWith("::Root",
            "the parent-walk picks the nearest *named* declaration ancestor as source");
        // The const Root declaration is emitted by the walk's normal declaration dispatch,
        // so no synthetic file-namespace symbol is needed when an enclosing declaration exists.
        events.OfType<IndexEvent.SymbolDeclared>()
            .Should().Contain(s => s.CanonicalKey == edge.SourceCanonicalKey);
        events.OfType<IndexEvent.SymbolDeclared>()
            .Should().NotContain(s => s.Kind == "namespace",
                "the file-namespace fallback should not fire when an enclosing named declaration exists");
    }

    [Fact]
    public async Task Tsx_skips_lower_cased_html_tags()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes("function Page() { return <div className='foo' />; }");
        var ctx = new IndexContext("/repo/src/html.tsx", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        events.OfType<IndexEvent.EdgeEmitted>()
            .Should().NotContain(e => e.TargetCanonicalKey.Contains("div"),
                "lowercase JSX tags are HTML elements, not user-symbol references");
    }

    [Fact]
    public async Task Javascript_grammar_handles_plain_js()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes("function greet(name) { return name; }");
        var ctx = new IndexContext("/repo/src/foo.js", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);
        var declaration = events.OfType<IndexEvent.SymbolDeclared>().Should().ContainSingle().Subject;

        declaration.Name.Should().Be("greet");
        declaration.CanonicalKey.Should().StartWith("js:M:");
    }

    [Fact]
    public async Task Call_expression_emits_reference()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes(
            "function greet(name: string) { return name; }\ngreet('hello');");
        var ctx = new IndexContext("/repo/src/foo.ts", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);
        var refs = events.OfType<IndexEvent.ReferenceFound>().ToList();

        refs.Should().Contain(r => r.Kind == "call" && r.TargetCanonicalKey.Contains("greet"));
    }

    [Fact]
    public async Task FileScanned_sentinel_emitted_once()
    {
        var indexer = new TypeScriptLanguageIndexer();
        var bytes = Encoding.UTF8.GetBytes("function a() {}");
        var ctx = new IndexContext("/repo/src/a.ts", bytes, "test", "/repo");

        var events = await indexer.IndexAsync(ctx, CancellationToken.None);

        events.OfType<IndexEvent.FileScanned>().Should().ContainSingle();
    }
}
