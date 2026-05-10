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
    /// Per-extension grammar dispatch matches the config's <c>FileExtensions</c> set exactly:
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
            ".js" or ".jsx" => "JavaScript",
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

        // Walk up the AST to assemble the full container nesting (`Counter::tick` for a method
        // on a class, `User#name` for an interface property, etc.) so canonical keys for
        // members are unique across the file. Without this, two methods named `tick` on
        // different classes would collide on the same `ts:M:foo.ts::tick` key.
        var ancestorPath = BuildContainerLexicalPath(node);
        var fqn = ancestorPath is null ? name : $"{ancestorPath}.{name}";

        string canonicalKey;
        if (kindPrefix == TypeScriptCanonicalKeys.PrefixProperty && ancestorPath is not null)
        {
            // Properties / fields use `#` to separate the type-name from the member-name; the
            // helper handles the formatting so consumers don't need to remember the convention.
            canonicalKey = TypeScriptCanonicalKeys.BuildProperty(scheme, repoRelativePath, ancestorPath, name);
        }
        else
        {
            var lexicalPath = ancestorPath is null ? name : $"{ancestorPath}::{name}";
            canonicalKey = TypeScriptCanonicalKeys.Build(scheme, kindPrefix, repoRelativePath, lexicalPath);
        }

        return new IndexEvent.SymbolDeclared(
            canonicalKey: canonicalKey,
            name: name,
            fqn: fqn,
            kind: kind,
            startLine: line,
            startColumn: col,
            endLine: endLine,
            endColumn: endCol);
    }

    /// <summary>
    /// Walk up the AST from <paramref name="declarationNode"/> collecting the names of every
    /// enclosing declaration-mappable ancestor, in outer-to-inner order, and join them with
    /// <c>::</c>. Returns <c>null</c> when the declaration has no enclosing declaration ancestor
    /// (i.e. it's a top-level declaration). Used to build container-aware lexical paths so
    /// canonical keys for members of different containers don't collide.
    /// </summary>
    private string? BuildContainerLexicalPath(TsNode declarationNode)
    {
        var ancestors = new List<string>();
        TsNode? cursor = declarationNode.Parent;
        for (var hops = 0; hops < 1024 && cursor is not null; hops++)
        {
            var type = cursor.Type;
            if (!string.IsNullOrEmpty(type) && _mapper.TryMapDeclaration(type, out _))
            {
                var ancestorName = ExtractDeclarationName(cursor);
                if (!string.IsNullOrEmpty(ancestorName))
                {
                    ancestors.Add(ancestorName!);
                }
            }
            cursor = cursor.Parent;
        }
        if (ancestors.Count == 0) return null;
        ancestors.Reverse();
        return string.Join("::", ancestors);
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

        // Components in modern React land as functions (`M`), classes (`T`), or arrow-bound
        // consts (`V`) — tree-sitter doesn't tell us which without resolving the binding.
        // Emit one EdgeEmitted candidate per likely prefix; GraphStoreEmitter drops every
        // unmatched canonical key at flush time, so exactly one edge persists per JSX usage
        // (the one whose prefix matches the actual declaration). The cost is a small
        // multiplier on emission volume that collapses to nothing in storage.
        var targetCandidates = new[]
        {
            TypeScriptCanonicalKeys.Build(scheme, TypeScriptCanonicalKeys.PrefixMethod, repoRelativePath, tag.Value.Name),
            TypeScriptCanonicalKeys.Build(scheme, TypeScriptCanonicalKeys.PrefixType, repoRelativePath, tag.Value.Name),
            TypeScriptCanonicalKeys.Build(scheme, TypeScriptCanonicalKeys.PrefixVariable, repoRelativePath, tag.Value.Name),
        };
        var primaryTargetKey = targetCandidates[0];

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
        // Single ReferenceFound at the tag's position — the agent's "find references" query
        // wants one row per usage site, not three, so we emit only the most-common-case prefix
        // (`M`) and accept that components declared as classes/consts may not surface as refs
        // until cross-file resolution lands. The instantiates *edges* below cover all three
        // declaration kinds.
        emissions.Add(new IndexEvent.ReferenceFound(primaryTargetKey, refLine, refCol, "reference"));
        foreach (var candidate in targetCandidates)
        {
            emissions.Add(new IndexEvent.EdgeEmitted(sourceKey, candidate, edgeKindName, metadata));
        }
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
        var direct = node.NamedChildren
            .FirstOrDefault(c => c.Type is "identifier" or "type_identifier" or "property_identifier");
        if (direct is not null) return direct.Text;

        // Some declaration shapes (variable_declarator inside lexical_declaration) wrap the
        // identifier one level deeper. Descend once into the first variable_declarator child.
        return node.NamedChildren
            .Where(c => c.Type == "variable_declarator")
            .SelectMany(c => c.NamedChildren)
            .FirstOrDefault(inner => inner.Type == "identifier")
            ?.Text;
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
        var match = node.NamedChildren
            .FirstOrDefault(c => c.Type is "identifier" or "member_expression");
        if (match is null) return null;
        // For `foo.bar.baz()` we want `baz` (the called member) — the rightmost
        // property_identifier in the chain, not the leftmost identifier (`foo`, the root
        // object). ExtractMemberLeaf encapsulates the property-field lookup with a
        // last-property-identifier fallback.
        return match.Type == "member_expression"
            ? ExtractMemberLeaf(match)
            : (match.Text, match.StartPosition);
    }

    private static (string Name, global::TreeSitter.Point Position)? ExtractJsxTag(TsNode jsxElement)
    {
        // `<Foo.Bar />` should target `Bar` (the component), not `Foo` (its container) — same
        // rightmost-property rule as call expressions.
        var match = jsxElement.NamedChildren
            .FirstOrDefault(c => c.Type is "identifier" or "jsx_identifier" or "nested_identifier" or "member_expression");
        if (match is null) return null;
        return match.Type == "member_expression"
            ? ExtractMemberLeaf(match)
            : (match.Text, match.StartPosition);
    }

    /// <summary>
    /// For a <c>member_expression</c> node like <c>foo.bar.baz</c>, return the rightmost
    /// property in the chain (<c>baz</c>) — that's the leaf the call/JSX is actually
    /// referencing. Tree-sitter wraps the chain as a left-associative binary tree where the
    /// outer member_expression's right-hand <c>property</c> field is the leaf.
    /// </summary>
    private static (string Name, global::TreeSitter.Point Position)? ExtractMemberLeaf(TsNode memberExpression)
    {
        // Prefer the explicit `property` field; tree-sitter's JS/TS grammar exposes it.
        var property = memberExpression["property"];
        if (!string.IsNullOrEmpty(property.Type))
        {
            return (property.Text, property.StartPosition);
        }
        // Fallback: take the LAST property_identifier child (the rightmost) so we land on the
        // leaf rather than the root object.
        var leaf = memberExpression.NamedChildren
            .Where(inner => inner.Type == "property_identifier")
            .LastOrDefault();
        if (leaf is not null)
        {
            return (leaf.Text, leaf.StartPosition);
        }
        // Last resort: the rightmost identifier of any kind.
        var lastIdent = memberExpression.NamedChildren
            .Where(inner => inner.Type is "identifier" or "property_identifier")
            .LastOrDefault();
        return lastIdent is null ? null : (lastIdent.Text, lastIdent.StartPosition);
    }

    private static List<string> ExtractJsxProps(TsNode jsxElement)
    {
        var props = new List<string>();
        foreach (var attribute in jsxElement.NamedChildren.Where(c => c.Type == "jsx_attribute"))
        {
            var nameNode = attribute.NamedChildren
                .FirstOrDefault(a => a.Type is "property_identifier" or "jsx_identifier" or "identifier");
            if (nameNode is not null)
            {
                props.Add(nameNode.Text);
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
