using System.IO;
using DevBitsLab.Mcp.SourceGraph.Indexing.TreeSitter;
using DevBitsLab.Mcp.SourceGraph.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TsNode = TreeSitter.Node;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.TypeScript;

/// <summary>
/// Built-in TypeScript / JavaScript / TSX / JSX language indexer. Subclass of the host's
/// <see cref="TreeSitterLanguageIndexer{TGrammarConfig}"/>, registered for the four extensions
/// <c>.ts</c> / <c>.tsx</c> / <c>.js</c> / <c>.jsx</c>.
///
/// <para>Per-file grammar dispatch via <see cref="GetGrammarName"/>: <c>.ts</c> uses the
/// <c>TypeScript</c> grammar; <c>.tsx</c> uses <c>TSX</c>; <c>.js</c> and <c>.jsx</c> use
/// <c>JavaScript</c> (which handles JSX inline). Canonical keys use the scheme matching the
/// extension (<c>ts:</c> / <c>tsx:</c> / <c>js:</c> / <c>jsx:</c>) — see
/// <see cref="TypeScriptCanonicalKeys"/>.</para>
/// </summary>
public sealed class TypeScriptLanguageIndexer : TreeSitterLanguageIndexer<TypeScriptGrammarConfig>
{
    private readonly TypeScriptNodeKindMapper _mapper = new();

    public TypeScriptLanguageIndexer(ILogger? logger = null)
        : base(new TypeScriptGrammarConfig(), logger ?? NullLogger.Instance)
    {
    }

    /// <inheritdoc />
    protected override INodeKindMapper Mapper => _mapper;

    /// <inheritdoc />
    /// <remarks>
    /// Per-extension grammar dispatch:
    /// <list type="bullet">
    /// <item><c>.ts</c> → <c>"TypeScript"</c></item>
    /// <item><c>.tsx</c> → <c>"TSX"</c></item>
    /// <item><c>.js</c> / <c>.jsx</c> → <c>"JavaScript"</c> (handles JSX inline)</item>
    /// </list>
    /// Unknown extensions fall back to the config's default <c>"TypeScript"</c>; the dispatcher
    /// shouldn't dispatch unknown extensions to this indexer in practice.
    /// </remarks>
    protected override string GetGrammarName(IndexContext ctx)
    {
        var ext = Path.GetExtension(ctx.FilePath).ToLowerInvariant();
        return ext switch
        {
            ".ts" => "TypeScript",
            ".tsx" => "TSX",
            ".js" or ".mjs" or ".cjs" => "JavaScript",
            ".jsx" => "JavaScript",
            _ => Config.GrammarName,
        };
    }

    /// <inheritdoc />
    protected override IndexEvent.SymbolDeclared? OnDeclarationNode(TsNode node, NodeMapping mapping, IndexContext ctx)
    {
        var scheme = TypeScriptCanonicalKeys.SchemeFromExtension(ctx.FilePath);
        if (scheme is null) return null;

        // Find the declaration's name. Most TS/JS container nodes carry a named child of type
        // `identifier` or `type_identifier` or `property_identifier` whose text is the name.
        var name = ExtractDeclarationName(node);
        if (string.IsNullOrEmpty(name)) return null;

        var repoRelativePath = MakeRepoRelative(ctx);
        var kindPrefix = SelectKindPrefix(mapping.Kind);

        // Refine the kind for `lexical_declaration` (const vs let): if the node's first text
        // begins with "const", emit Constant rather than the default Variable.
        var kind = mapping.Kind;
        if (kind == "variable" && node.Type == "lexical_declaration")
        {
            var firstChunk = node.Text;
            if (firstChunk is not null && firstChunk.AsSpan().TrimStart().StartsWith("const"))
            {
                kind = "constant";
            }
        }

        var (line, col) = TreeSitterAdapter.ToOneBased(node.StartPosition);
        var (endLine, endCol) = TreeSitterAdapter.ToOneBased(node.EndPosition);

        var canonicalKey = TypeScriptCanonicalKeys.Build(scheme, kindPrefix, repoRelativePath, name);

        return new IndexEvent.SymbolDeclared(
            canonicalKey: canonicalKey,
            name: name,
            fqn: name, // TS doesn't have a single "fully-qualified name" outside namespaces; keep simple at v1.
            kind: kind,
            startLine: line,
            startColumn: col,
            endLine: endLine,
            endColumn: endCol);
    }

    /// <inheritdoc />
    protected override IndexEvent.ReferenceFound? OnReferenceNode(TsNode node, string referenceKind, IndexContext ctx)
    {
        // For call_expression and similar, the actual referenced identifier is a child node;
        // we surface a position-level ref at the identifier's location. At v1 we don't resolve
        // cross-file: the IModuleResolver wiring lands as a follow-up, so refs here only
        // succeed when the target is intra-file (which the storage layer resolves at flush time
        // via canonical_key). We emit the ref with a placeholder canonical key constructed from
        // the identifier text + current file — the host drops it if no symbol matches.
        var scheme = TypeScriptCanonicalKeys.SchemeFromExtension(ctx.FilePath);
        if (scheme is null) return null;

        var identifier = ExtractCalleeIdentifier(node);
        if (identifier is null) return null;

        var (line, col) = TreeSitterAdapter.ToOneBased(identifier.Value.Position);

        // The target canonical key is a best-effort intra-file guess. If the symbol is declared
        // in this file, the storage layer resolves the integer id correctly; if not, the ref
        // becomes a stranded edge that future cross-file resolution can adopt.
        var repoRelativePath = MakeRepoRelative(ctx);
        var targetKey = TypeScriptCanonicalKeys.Build(
            scheme,
            referenceKind == "call" ? TypeScriptCanonicalKeys.PrefixMethod : TypeScriptCanonicalKeys.PrefixType,
            repoRelativePath,
            identifier.Value.Name);

        return new IndexEvent.ReferenceFound(targetKey, line, col, referenceKind);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<IndexEvent>? OnEdgeNode(TsNode node, string edgeKindName, IndexContext ctx)
    {
        // JSX elements: emit an instantiates edge from the file (treated as a namespace) to the
        // component. TS module-files ARE namespaces in the language semantics, so this isn't a
        // synthetic dodge — `import * as M from './foo'` is documented as importing the
        // namespace M. We pre-emit a SymbolDeclared for the file-namespace alongside the edge
        // so GraphStoreEmitter can resolve the source canonical key and persist the row;
        // emitting the symbol multiple times across edges is safe (UpsertSymbolAsync dedupes
        // by canonical key).
        if (edgeKindName != EdgeKinds.Instantiates) return null;
        if (node.Type != "jsx_self_closing_element" && node.Type != "jsx_opening_element") return null;

        var scheme = TypeScriptCanonicalKeys.SchemeFromExtension(ctx.FilePath);
        if (scheme is null) return null;

        var tag = ExtractJsxTag(node);
        if (tag is null) return null;

        // Skip lower-cased HTML-style tags (`<div>`, `<span>`, …): they don't reference user
        // symbols and would swamp the signal floor.
        if (char.IsLower(tag.Value.Name[0])) return null;

        var repoRelativePath = MakeRepoRelative(ctx);
        var fileNamespaceKey = BuildFileNamespaceKey(scheme, repoRelativePath);
        var targetKey = TypeScriptCanonicalKeys.Build(scheme, TypeScriptCanonicalKeys.PrefixMethod, repoRelativePath, tag.Value.Name);

        // Prefer the enclosing declaration as the edge source — it's the semantically meaningful
        // answer ("function `Page` instantiates `<Button>`" beats "the file instantiates").
        // Fall back to the file-namespace when the JSX appears at the top level of a module
        // (rare but legal, e.g. an exported JSX expression).
        var enclosingKey = TryFindEnclosingDeclarationKey(node, scheme, repoRelativePath);
        var sourceKey = enclosingKey ?? fileNamespaceKey;

        var props = ExtractJsxProps(node);
        Dictionary<string, string>? metadata = null;
        if (props.Count > 0)
        {
            metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // JSON-encoded array so consumers can parse structurally and the existing
                // `payload: { ... }` markdown sub-line renders something sensible. Plain
                // comma-joined values would round-trip but lose the list shape and break if a
                // value ever needs an embedded comma.
                ["props"] = "[" + string.Join(",", props.Select(p => "\"" + p + "\"")) + "]",
            };
        }

        var (refLine, refCol) = TreeSitterAdapter.ToOneBased(tag.Value.Position);
        var fileName = Path.GetFileName(ctx.FilePath);

        // When the source is the file-namespace, pre-emit its SymbolDeclared so the source key
        // resolves at flush time. When the source is an enclosing declaration, the
        // declaration's own SymbolDeclared is emitted by the walk's normal dispatch — no extra
        // event needed.
        var emissions = new List<IndexEvent>(3);
        if (sourceKey == fileNamespaceKey)
        {
            // Synthetic position (line 1, col 1) — we don't have the tree's full extent here;
            // it's a notional symbol whose identity matters more than its source range.
            emissions.Add(new IndexEvent.SymbolDeclared(
                canonicalKey: fileNamespaceKey,
                name: fileName,
                fqn: repoRelativePath,
                kind: SymbolKinds.Namespace,
                startLine: 1,
                startColumn: 1,
                endLine: 1,
                endColumn: 1));
        }
        emissions.Add(new IndexEvent.ReferenceFound(targetKey, refLine, refCol, "reference"));
        emissions.Add(new IndexEvent.EdgeEmitted(sourceKey, targetKey, edgeKindName, metadata));
        return emissions;
    }

    /// <summary>
    /// Walk up the AST from <paramref name="node"/> looking for the nearest ancestor whose
    /// node-type maps to a declaration. When found, reconstruct that declaration's canonical
    /// key (mirroring <see cref="OnDeclarationNode"/>'s logic so the keys match exactly).
    /// Returns <c>null</c> when no enclosing declaration exists — the caller falls back to
    /// the file-namespace key.
    /// </summary>
    private string? TryFindEnclosingDeclarationKey(TsNode node, string scheme, string repoRelativePath)
    {
        // TreeSitter.DotNet's Node is a class — Node.Parent returns null when called on the
        // tree's root. Cap at 1024 hops as a defence against pathologically nested trees.
        TsNode? cursor = node.Parent;
        for (var hops = 0; hops < 1024 && cursor is not null; hops++)
        {
            if (_mapper.TryMapDeclaration(cursor.Type, out var mapping))
            {
                var name = ExtractDeclarationName(cursor);
                if (!string.IsNullOrEmpty(name))
                {
                    var prefix = SelectKindPrefix(mapping.Kind);
                    return TypeScriptCanonicalKeys.Build(scheme, prefix, repoRelativePath, name);
                }
            }
            cursor = cursor.Parent;
        }
        return null;
    }

    private static string BuildFileNamespaceKey(string scheme, string repoRelativePath) =>
        TypeScriptCanonicalKeys.Build(scheme, TypeScriptCanonicalKeys.PrefixNamespace, repoRelativePath, "module");

    private static string? ExtractDeclarationName(TsNode node)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type is "identifier" or "type_identifier" or "property_identifier")
            {
                return child.Text;
            }
        }
        // Some declaration shapes (variable_declarator inside lexical_declaration) wrap the
        // identifier one level deeper. Descend once into the first variable_declarator child.
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == "variable_declarator")
            {
                foreach (var inner in child.NamedChildren)
                {
                    if (inner.Type is "identifier") return inner.Text;
                }
            }
        }
        return null;
    }

    private static (string Name, global::TreeSitter.Point Position)? ExtractCalleeIdentifier(TsNode node)
    {
        // call_expression's first named child is typically the callee — either an identifier
        // (`foo(...)`) or a member_expression (`foo.bar(...)`). For type_identifier nodes it's
        // the node itself.
        if (node.Type == "type_identifier")
        {
            return (node.Text, node.StartPosition);
        }
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == "identifier")
            {
                return (child.Text, child.StartPosition);
            }
            if (child.Type == "member_expression")
            {
                // Use the rightmost property_identifier as the called member.
                foreach (var inner in child.NamedChildren)
                {
                    if (inner.Type is "property_identifier" or "identifier")
                    {
                        return (inner.Text, inner.StartPosition);
                    }
                }
            }
        }
        return null;
    }

    private static (string Name, global::TreeSitter.Point Position)? ExtractJsxTag(TsNode jsxElement)
    {
        foreach (var child in jsxElement.NamedChildren)
        {
            if (child.Type is "identifier" or "jsx_identifier" or "nested_identifier")
            {
                return (child.Text, child.StartPosition);
            }
            if (child.Type == "member_expression")
            {
                foreach (var inner in child.NamedChildren)
                {
                    if (inner.Type is "property_identifier" or "identifier")
                    {
                        return (inner.Text, inner.StartPosition);
                    }
                }
            }
        }
        return null;
    }

    private static List<string> ExtractJsxProps(TsNode jsxElement)
    {
        var props = new List<string>();
        foreach (var child in jsxElement.NamedChildren)
        {
            if (child.Type == "jsx_attribute")
            {
                foreach (var attrChild in child.NamedChildren)
                {
                    if (attrChild.Type is "property_identifier" or "jsx_identifier" or "identifier")
                    {
                        props.Add(attrChild.Text);
                        break;
                    }
                }
            }
        }
        return props;
    }

    private static string SelectKindPrefix(string kind) => kind switch
    {
        "method" or "function" or "constructor" => TypeScriptCanonicalKeys.PrefixMethod,
        "class" or "interface" or "struct" or "enum" or "type-alias" or "delegate" or "record" => TypeScriptCanonicalKeys.PrefixType,
        "field" or "property" or "enum-member" => TypeScriptCanonicalKeys.PrefixProperty,
        "namespace" => TypeScriptCanonicalKeys.PrefixNamespace,
        "constant" or "variable" or "local" or "parameter" => TypeScriptCanonicalKeys.PrefixVariable,
        _ => TypeScriptCanonicalKeys.PrefixVariable,
    };

    private string MakeRepoRelative(IndexContext ctx)
    {
        try
        {
            var rel = Path.GetRelativePath(ctx.RepoRoot, ctx.FilePath);
            return rel.Replace('\\', '/');
        }
        catch (ArgumentException ex)
        {
            // ArgumentException is the only exception Path.GetRelativePath documents (paths
            // contain invalid characters or one is empty). Falling back to the absolute path
            // keeps the canonical key non-empty; logging at debug level surfaces the issue
            // without spamming the operator log on every file.
            Logger.LogDebug(ex, "Path.GetRelativePath failed for {Root} → {Path}; using absolute path as canonical-key body.", ctx.RepoRoot, ctx.FilePath);
            return ctx.FilePath.Replace('\\', '/');
        }
    }
}
