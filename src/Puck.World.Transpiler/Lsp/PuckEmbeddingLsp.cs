using System.Text;
using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Editing;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lsp;

/// <summary>Language Server Protocol support for state embeddings, spaces, transforms, and vector operations.</summary>
internal static class PuckEmbeddingLsp {
    /// <summary>Adds embedding, space, and vector keyword completions to the LSP completion list.</summary>
    /// <param name="items">The destination completion items array.</param>
    internal static void AddCompletions(JsonArray items) {
        LspJson.AddCompletion(
            detail: "Block: embedding spaces definition",
            insertText: "spaces {\n\tspace ${1:lore} { model: \"${2:text-embedding-3-small}\" revision: \"${3:1}\" dimensions: ${4:256} }\n}",
            items: items,
            kind: 14,
            label: "spaces"
        );
        LspJson.AddCompletion(
            detail: "Declaration: embedding space definition",
            insertText: "space ${1:lore} { model: \"${2:text-embedding-3-small}\" revision: \"${3:1}\" dimensions: ${4:256} }",
            items: items,
            kind: 14,
            label: "space"
        );
        LspJson.AddCompletion(
            detail: "Cell kind: normalized signed 8-bit embedding vector",
            insertText: "Vector",
            items: items,
            kind: 7,
            label: "Vector"
        );
        LspJson.AddCompletion(
            detail: "Table modifier: capacity eviction policy",
            insertText: "evicts",
            items: items,
            kind: 14,
            label: "evicts"
        );
        LspJson.AddCompletion(
            detail: "Table modifier: companion vector table",
            insertText: "embeds(${1:companionVectorTable})",
            items: items,
            kind: 14,
            label: "embeds"
        );
        LspJson.AddCompletion(
            detail: "Function: embed string literal into vector",
            insertText: "embed(\"${1:text}\")",
            items: items,
            kind: 3,
            label: "embed"
        );
        LspJson.AddCompletion(
            detail: "Function: vector literal from base64url",
            insertText: "vector(\"${1:base64url}\")",
            items: items,
            kind: 3,
            label: "vector"
        );
        LspJson.AddCompletion(
            detail: "Function: integer dot product of two vectors",
            insertText: "dot(${1:a}, ${2:b})",
            items: items,
            kind: 3,
            label: "dot"
        );
        LspJson.AddCompletion(
            detail: "Function: cosine similarity of two vectors",
            insertText: "similarity(${1:a}, ${2:b})",
            items: items,
            kind: 3,
            label: "similarity"
        );
        LspJson.AddCompletion(
            detail: "Predicate: whether two vectors are identical",
            insertText: "identical(${1:a}, ${2:b})",
            items: items,
            kind: 3,
            label: "identical"
        );
        LspJson.AddCompletion(
            detail: "Transform: normalized weighted sum of vector terms",
            insertText: "mix(into: \"${1:target}\", terms: [\n\t{ from: ${2:source}, weight: ${3:1} }\n])",
            items: items,
            kind: 3,
            label: "mix"
        );
        LspJson.AddCompletion(
            detail: "Transform: normalized mean of candidate vectors",
            insertText: "mean(from: ${1:table}, into: \"${2:target}\")",
            items: items,
            kind: 3,
            label: "mean"
        );
        LspJson.AddCompletion(
            detail: "Transform: find k nearest vector cells",
            insertText: "nearest(from: ${1:table}, query: ${2:vector}, into: ${3:dest}, k: ${4:1})",
            items: items,
            kind: 3,
            label: "nearest"
        );
        LspJson.AddCompletion(
            detail: "Transform: insert or reinforce a vector memory",
            insertText: "remember(from: ${1:table}, query: ${2:vector})",
            items: items,
            kind: 3,
            label: "remember"
        );
    }
    /// <summary>Creates a document symbol for a <c>spaces { ... }</c> block.</summary>
    /// <param name="spaces">The spaces block AST node.</param>
    /// <param name="source">The text the block was parsed from.</param>
    /// <returns>A document symbol JSON object representing the spaces block and its defined spaces.</returns>
    internal static JsonObject CreateSpacesSymbol(BlockNode spaces, string source) {
        var children = new JsonArray();

        foreach (var stmt in spaces.Statements) {
            if (stmt is BlockNode spaceBlock) {
                var childSymbol = Symbol(
                    kind: 5,
                    name: $"space {(spaceBlock.Name ?? (spaceBlock.Target ?? spaceBlock.Identifier))}",
                    node: spaceBlock,
                    selectionLength: spaceBlock.Identifier.Length,
                    source: source
                );
                var propChildren = new JsonArray();

                foreach (var s in spaceBlock.Statements) {
                    if (s is PropertyNode prop) {
                        propChildren.Add(item: Symbol(
                            kind: 7, // Property
                            name: prop.Name,
                            node: prop,
                            selectionLength: prop.Name.Length,
                            source: source
                        ));
                    }
                }

                if (propChildren.Count > 0) {
                    childSymbol["children"] = propChildren;
                }

                children.Add(item: childSymbol);
            }
        }

        var symbol = Symbol(
            kind: 5, // Class
            name: "spaces",
            node: spaces,
            selectionLength: spaces.Identifier.Length,
            source: source
        );

        symbol["children"] = children;
        return symbol;
    }

    // A symbol spans its whole construct; its selection is the name the construct opens with.
    private static JsonObject Symbol(string name, int kind, SyntaxNode node, int selectionLength, string source) => new() {
        ["name"] = name,
        ["kind"] = kind,
        ["range"] = LspJson.Range(source: source, span: node.Span),
        ["selectionRange"] = LspJson.Range(source: source, span: (node.Span with { Length = selectionLength })),
    };

    /// <summary>Gets a hover card for an embedding keyword or an embedded text under the cursor.</summary>
    /// <param name="offset">The character offset of the cursor in source text.</param>
    /// <param name="text">The complete document source text.</param>
    /// <param name="word">The word under the cursor, or null.</param>
    /// <param name="sourcePath">The local file path of the source document, if known.</param>
    /// <returns>A markdown-formatted hover card string, or null if position does not match.</returns>
    internal static string? GetEmbeddingHoverCard(int offset, string text, string? word, string? sourcePath) {
        // 1. Check if hovering on an embedded text literal
        var embeddedCard = TryGetEmbeddedTextHoverCard(offset: offset, sourcePath: sourcePath, text: text);

        if (embeddedCard is not null) {
            return embeddedCard;
        }

        // 2. Check if hovering on an embedding keyword or transform
        if (!string.IsNullOrEmpty(value: word)) {
            return GetKeywordHoverCard(word: word);
        }

        return null;
    }
    /// <summary>Gets a hover card for embedding and vector keywords.</summary>
    /// <param name="word">The identifier or keyword to describe.</param>
    /// <returns>A markdown documentation card, or null.</returns>
    internal static string? GetKeywordHoverCard(string word) {
        return word switch {
            "Vector" => PuckHoverInfo.Card(
                declaration: "table memories space(lore) = ...\nslot query space(lore) = ...",
                description: "A vector state row whose cells hold normalized `sbyte` embedding vectors within a declared space; a `space(...)` modifier is what infers the Vector kind.",
                title: "`Vector` Cell Kind"
            ),
            "embed" => PuckHoverInfo.Card(
                declaration: "embed(\"text\", [space: \"lore\"])",
                description: "Embeds a text literal into a vector using the specified or default space. Resolved and locked at bake time via `puck embed`.",
                title: "`embed` Function"
            ),
            "vector" => PuckHoverInfo.Card(
                declaration: "vector(\"base64url\")",
                description: "A raw vector literal encoded as unpadded URL-safe base64 `sbyte` components.",
                title: "`vector` Literal"
            ),
            "dot" => PuckHoverInfo.Card(
                declaration: "dot(vectorA, vectorB)",
                description: "Computes the exact integer dot product of two vectors in the same space.",
                title: "`dot` Function"
            ),
            "similarity" => PuckHoverInfo.Card(
                declaration: "similarity(vectorA, vectorB)",
                description: "Computes the cosine similarity of two normalized vectors in the same space as a `Fixed` decimal value.",
                title: "`similarity` Function"
            ),
            "identical" => PuckHoverInfo.Card(
                declaration: "identical(vectorA, vectorB)",
                description: "Evaluates to `true` if two vectors have identical components, `false` otherwise.",
                title: "`identical` Predicate Function"
            ),
            "mix" => PuckHoverInfo.Card(
                declaration: "transform mix(into: \"current\", terms: [\n    { from: \"stance[$each]\", weight: 3 }\n    { from: embed(\"calm\"), weight: -1 }\n])",
                description: "Writes the normalized weighted sum of 1 to 8 vector terms into a destination vector cell.",
                title: "`mix` Transform"
            ),
            "mean" => PuckHoverInfo.Card(
                declaration: "transform mean(from: memories, into: \"self\", where: important)",
                description: "Writes the normalized mean vector of candidate cells in a table into a destination vector cell.",
                title: "`mean` Transform"
            ),
            "nearest" => PuckHoverInfo.Card(
                declaration: "transform nearest(from: memories, query: \"situation\", into: recalled, k: 3)",
                description: "Finds the `k` nearest cells in a vector table to a query vector, scoring by dot/similarity or writing the best key into a Text slot.",
                title: "`nearest` Transform"
            ),
            "remember" => PuckHoverInfo.Card(
                declaration: "transform remember(from: memories, query: \"situation\", threshold: 0.8)",
                description: "Inserts or reinforces a vector memory in an evicting vector table.",
                title: "`remember` Transform"
            ),
            _ => null,
        };
    }

    private static string? TryGetEmbeddedTextHoverCard(int offset, string text, string? sourcePath) {
        // Parse document with diagnostics to find AST node at offset
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: text,
            vocabulary: WorldDocumentVocabulary.Instance
        );
        var doc = parseResult.Value;

        if (doc is null) {
            return null;
        }

        var path = SyntaxWalk.PathAt(offset: offset, root: doc);

        string? embeddedText = null;
        string? explicitSpace = null;

        for (var i = (path.Count - 1); (i >= 0); i--) {
            var node = path[i];

            if ((node is RhsOperandNode rhsOp) && TryExtractEmbedFromText(text: rhsOp.Expression.Text, embeddedText: out embeddedText, explicitSpace: out explicitSpace)) {
                break;
            }

            if (node is ComparisonPredicateNode compPred) {
                if (TryExtractEmbedFromText(text: compPred.Left.Text, embeddedText: out embeddedText, explicitSpace: out explicitSpace) ||
                    TryExtractEmbedFromText(text: compPred.Right.Text, embeddedText: out embeddedText, explicitSpace: out explicitSpace)) {
                    break;
                }
            }

            if ((node is CallExpressionNode call) && string.Equals(a: call.Name, b: "embed", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if ((call.Arguments.Count > 0) && (call.Arguments[0].Value is LiteralExpressionNode { Value: string strText })) {
                    embeddedText = strText;
                }
                if (call.Arguments.Count > 1) {
                    var spArg = (call.Arguments.FirstOrDefault(predicate: a => string.Equals(a: a.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase))
                                ?? call.Arguments[1]);

                    if (spArg.Value is LiteralExpressionNode { Value: string spStr }) {
                        explicitSpace = spStr;
                    } else if (spArg.Value is IdentifierExpressionNode spIdent) {
                        explicitSpace = spIdent.Name;
                    }
                }
                break;
            }

            if (node is StateCellEntryNode cell) {
                if (cell.Value is LiteralExpressionNode { Value: string cellStr }) {
                    // Check if enclosing table is Vector or has embeds
                    var table = path.OfType<StateTableDeclarationNode>().FirstOrDefault();

                    if (table is not null) {
                        var spaceMod = table.Modifiers.FirstOrDefault(predicate: m => string.Equals(a: m.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase));

                        // A `space(...)` modifier is the Vector signal a kind annotation no longer carries — see
                        // WorldDocumentEmitter's kind inference.
                        if (spaceMod is not null) {
                            embeddedText = cellStr;
                            explicitSpace = spaceMod.Arguments.FirstOrDefault()?.Value switch {
                                LiteralExpressionNode { Value: string s } => s,
                                IdentifierExpressionNode ident => ident.Name,
                                _ => null,
                            };
                            break;
                        }

                        var embedsMod = table.Modifiers.FirstOrDefault(predicate: m => string.Equals(a: m.Name, b: "embeds", comparisonType: StringComparison.OrdinalIgnoreCase));

                        if (embedsMod is not null) {
                            embeddedText = cellStr;
                            explicitSpace = embedsMod.Arguments.FirstOrDefault(predicate: a => string.Equals(a: a.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase))?.Value switch {
                                LiteralExpressionNode { Value: string s } => s,
                                IdentifierExpressionNode ident => ident.Name,
                                _ => null,
                            };
                            break;
                        }
                    }
                }
            }
        }

        if (embeddedText is null) {
            return null;
        }

        // Resolve space name
        var resolvedSpace = explicitSpace;

        if (string.IsNullOrEmpty(value: resolvedSpace)) {
            // Find default space from document
            var spacesBlock = doc.Statements.OfType<BlockNode>().FirstOrDefault(predicate: b => string.Equals(a: b.Identifier, b: "state", comparisonType: StringComparison.OrdinalIgnoreCase))
                ?.Statements.OfType<BlockNode>().FirstOrDefault(predicate: b => string.Equals(a: b.Identifier, b: "spaces", comparisonType: StringComparison.OrdinalIgnoreCase));

            if (spacesBlock is not null) {
                var spaceDecls = spacesBlock.Statements.OfType<BlockNode>().Where(predicate: b => string.Equals(a: b.Identifier, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase)).ToList();

                if (spaceDecls.Count == 1) {
                    resolvedSpace = (spaceDecls[0].Name ?? (spaceDecls[0].Target ?? spaceDecls[0].Identifier));
                }
            }
        }

        resolvedSpace ??= "default";

        // Load embedding lock
        var lockFile = EmbeddingLock.TryLoad(rootSourcePath: sourcePath);
        var sb = new StringBuilder();

        sb.AppendLine(handler: $"**Embedded Text** (`{resolvedSpace}`)\n");
        sb.AppendLine(handler: $"\"{embeddedText}\"\n");

        if (lockFile is null) {
            sb.AppendLine(value: "- **Lock status:** Not locked (no lock file found; run `puck embed`)");
            sb.AppendLine(handler: $"- **Space:** `{resolvedSpace}`");
            return sb.ToString().TrimEnd();
        }

        if (!lockFile.Spaces.TryGetValue(key: resolvedSpace, value: out var space)) {
            if (lockFile.Spaces.Count == 1) {
                space = lockFile.Spaces.Values.First();
                resolvedSpace = lockFile.Spaces.Keys.First();
            } else {
                sb.AppendLine(handler: $"- **Lock status:** Not locked (space '{resolvedSpace}' not found in lock; run `puck embed`)");
                return sb.ToString().TrimEnd();
            }
        }

        var textHash = EmbeddingText.Hash(text: embeddedText).Hex;

        if (!space.Entries.TryGetValue(key: textHash, value: out var currentEntry)) {
            sb.AppendLine(value: "- **Lock status:** Not locked (run `puck embed`)");
            sb.AppendLine(handler: $"- **Space:** `{resolvedSpace}` (`{space.Identity.Model}`, rev: `{space.Identity.Revision}`, dims: {space.Identity.Dimensions})");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine(value: "- **Lock status:** Locked");
        sb.AppendLine(handler: $"- **Model:** `{space.Identity.Model}` (rev: `{space.Identity.Revision}`, dims: {space.Identity.Dimensions})");
        sb.AppendLine(handler: $"- **Vector:** `{currentEntry.Vector}`\n");

        // Find three nearest locked texts
        if (StateVector.TryParseBase64Url(currentEntry.Vector, space.Identity.Dimensions, out var currentVector, out _)) {
            var nearestList = new List<(string Text, long Score)>();

            foreach (var other in space.Entries.Values) {
                if (string.Equals(a: other.Text, b: embeddedText, comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                if (StateVector.TryParseBase64Url(other.Vector, space.Identity.Dimensions, out var otherVector, out _)) {
                    var dot = SignedByteVectorFunctions.Dot(left: currentVector.Components, right: otherVector.Components);

                    nearestList.Add(item: (other.Text, dot));
                }
            }

            if (nearestList.Count > 0) {
                nearestList.Sort(comparison: (a, b) => {
                    var cmp = b.Score.CompareTo(value: a.Score);

                    return ((cmp != 0) ? cmp : string.Compare(comparisonType: StringComparison.Ordinal, strA: a.Text, strB: b.Text));
                });

                sb.AppendLine(value: "**Nearest locked texts:**");
                var count = Math.Min(val1: 3, val2: nearestList.Count);

                for (var i = 0; (i < count); i++) {
                    var (nText, nScore) = nearestList[i];
                    sb.AppendLine(handler: $"{(i + 1)}. \"{nText}\" (score: {nScore})");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
    private static bool TryExtractEmbedFromText(string text, out string? embeddedText, out string? explicitSpace) {
        embeddedText = null;
        explicitSpace = null;
        if (ExpressionSpelling.TryParseVector(error: out _, text: text, token: out var vecOp) && (vecOp is VectorOperand.Embed emb)) {
            embeddedText = emb.Text;
            explicitSpace = emb.Space;
            return true;
        }
        if (ExpressionSpelling.TryParse(error: out _, program: out var parsed, text: text)) {
            foreach (var tok in parsed.Instructions) {
                if (tok.Payload is InstructionPayload.Vector vc) {
                    if (vc.Left is VectorOperand.Embed leftEmb) {
                        embeddedText = leftEmb.Text;
                        explicitSpace = leftEmb.Space;
                        return true;
                    }
                    if (vc.Right is VectorOperand.Embed rightEmb) {
                        embeddedText = rightEmb.Text;
                        explicitSpace = rightEmb.Space;
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
