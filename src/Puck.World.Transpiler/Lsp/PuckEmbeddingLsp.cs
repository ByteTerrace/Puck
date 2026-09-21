using System.Text;
using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lsp;

/// <summary>Language Server Protocol support for state embeddings, spaces, transforms, and vector operations.</summary>
internal static class PuckEmbeddingLsp {
    private static void AddCompletion(JsonArray items, string label, string insertText, string detail, int kind) {
        items.Add(item: new JsonObject {
            ["label"] = label,
            ["kind"] = kind,
            ["detail"] = detail,
            ["insertText"] = insertText,
            ["insertTextFormat"] = 2,
        });
    }

    /// <summary>Adds embedding, space, and vector keyword completions to the LSP completion list.</summary>
    /// <param name="items">The destination completion items array.</param>
    internal static void AddCompletions(JsonArray items) {
        AddCompletion(
            detail: "Block: embedding spaces definition",
            insertText: "spaces {\n\tspace ${1:lore} { model: \"${2:text-embedding-3-small}\" revision: \"${3:1}\" dimensions: ${4:256} }\n}",
            items: items,
            kind: 14,
            label: "spaces"
        );
        AddCompletion(
            detail: "Declaration: embedding space definition",
            insertText: "space ${1:lore} { model: \"${2:text-embedding-3-small}\" revision: \"${3:1}\" dimensions: ${4:256} }",
            items: items,
            kind: 14,
            label: "space"
        );
        AddCompletion(
            detail: "Cell kind: normalized signed 8-bit embedding vector",
            insertText: "Vector",
            items: items,
            kind: 7,
            label: "Vector"
        );
        AddCompletion(
            detail: "Table modifier: capacity eviction policy",
            insertText: "evicts",
            items: items,
            kind: 14,
            label: "evicts"
        );
        AddCompletion(
            detail: "Table modifier: companion vector table",
            insertText: "embeds(${1:companionVectorTable})",
            items: items,
            kind: 14,
            label: "embeds"
        );
        AddCompletion(
            detail: "Function: embed string literal into vector",
            insertText: "embed(\"${1:text}\")",
            items: items,
            kind: 3,
            label: "embed"
        );
        AddCompletion(
            detail: "Function: vector literal from base64url",
            insertText: "vector(\"${1:base64url}\")",
            items: items,
            kind: 3,
            label: "vector"
        );
        AddCompletion(
            detail: "Function: integer dot product of two vectors",
            insertText: "dot(${1:a}, ${2:b})",
            items: items,
            kind: 3,
            label: "dot"
        );
        AddCompletion(
            detail: "Function: cosine similarity of two vectors",
            insertText: "similarity(${1:a}, ${2:b})",
            items: items,
            kind: 3,
            label: "similarity"
        );
        AddCompletion(
            detail: "Predicate: whether two vectors are identical",
            insertText: "identical(${1:a}, ${2:b})",
            items: items,
            kind: 3,
            label: "identical"
        );
        AddCompletion(
            detail: "Transform: normalized weighted sum of vector terms",
            insertText: "mix(into: \"${1:target}\", terms: [\n\t{ from: ${2:source}, weight: ${3:1} }\n])",
            items: items,
            kind: 3,
            label: "mix"
        );
        AddCompletion(
            detail: "Transform: normalized mean of candidate vectors",
            insertText: "mean(from: ${1:table}, into: \"${2:target}\")",
            items: items,
            kind: 3,
            label: "mean"
        );
        AddCompletion(
            detail: "Transform: find k nearest vector cells",
            insertText: "nearest(from: ${1:table}, query: ${2:vector}, into: ${3:dest}, k: ${4:1})",
            items: items,
            kind: 3,
            label: "nearest"
        );
        AddCompletion(
            detail: "Transform: insert or reinforce a vector memory",
            insertText: "remember(from: ${1:table}, query: ${2:vector})",
            items: items,
            kind: 3,
            label: "remember"
        );
    }
    /// <summary>Creates a document symbol for a <c>spaces { ... }</c> block.</summary>
    /// <param name="spaces">The spaces block AST node.</param>
    /// <returns>A document symbol JSON object representing the spaces block and its defined spaces.</returns>
    internal static JsonObject CreateSpacesSymbol(BlockNode spaces) {
        var symbol = new JsonObject {
            ["name"] = "spaces",
            ["kind"] = 5, // Class
            ["range"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = (spaces.Line - 1), ["character"] = (spaces.Column - 1) },
                ["end"] = new JsonObject { ["line"] = (spaces.Line - 1), ["character"] = ((spaces.Column - 1) + spaces.Length) },
            },
            ["selectionRange"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = (spaces.Line - 1), ["character"] = (spaces.Column - 1) },
                ["end"] = new JsonObject { ["line"] = (spaces.Line - 1), ["character"] = ((spaces.Column - 1) + spaces.Identifier.Length) },
            },
        };

        var children = new JsonArray();

        foreach (var stmt in spaces.Statements) {
            if (stmt is BlockNode spaceBlock) {
                var spaceName = (spaceBlock.Name ?? (spaceBlock.Target ?? spaceBlock.Identifier));
                var childSymbol = new JsonObject {
                    ["name"] = $"space {spaceName}",
                    ["kind"] = 5,
                    ["range"] = new JsonObject {
                        ["start"] = new JsonObject { ["line"] = (spaceBlock.Line - 1), ["character"] = (spaceBlock.Column - 1) },
                        ["end"] = new JsonObject { ["line"] = (spaceBlock.Line - 1), ["character"] = ((spaceBlock.Column - 1) + spaceBlock.Length) },
                    },
                    ["selectionRange"] = new JsonObject {
                        ["start"] = new JsonObject { ["line"] = (spaceBlock.Line - 1), ["character"] = (spaceBlock.Column - 1) },
                        ["end"] = new JsonObject { ["line"] = (spaceBlock.Line - 1), ["character"] = ((spaceBlock.Column - 1) + spaceBlock.Identifier.Length) },
                    },
                };

                var propChildren = new JsonArray();

                foreach (var s in spaceBlock.Statements) {
                    if (s is PropertyNode prop) {
                        propChildren.Add(item: new JsonObject {
                            ["name"] = prop.Name,
                            ["kind"] = 7, // Property
                            ["range"] = new JsonObject {
                                ["start"] = new JsonObject { ["line"] = (prop.Line - 1), ["character"] = (prop.Column - 1) },
                                ["end"] = new JsonObject { ["line"] = (prop.Line - 1), ["character"] = ((prop.Column - 1) + prop.Length) },
                            },
                            ["selectionRange"] = new JsonObject {
                                ["start"] = new JsonObject { ["line"] = (prop.Line - 1), ["character"] = (prop.Column - 1) },
                                ["end"] = new JsonObject { ["line"] = (prop.Line - 1), ["character"] = ((prop.Column - 1) + prop.Name.Length) },
                            },
                        });
                    }
                }

                if (propChildren.Count > 0) {
                    childSymbol["children"] = propChildren;
                }

                children.Add(item: childSymbol);
            }
        }

        symbol["children"] = children;
        return symbol;
    }
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
            "Vector" => Card(
                declaration: "table memories space(lore) = ...\nslot query space(lore) = ...",
                description: "A vector state row whose cells hold normalized `sbyte` embedding vectors within a declared space; a `space(...)` modifier is what infers the Vector kind.",
                title: "`Vector` Cell Kind"
            ),
            "embed" => Card(
                declaration: "embed(\"text\", [space: \"lore\"])",
                description: "Embeds a text literal into a vector using the specified or default space. Resolved and locked at bake time via `puck embed`.",
                title: "`embed` Function"
            ),
            "vector" => Card(
                declaration: "vector(\"base64url\")",
                description: "A raw vector literal encoded as unpadded URL-safe base64 `sbyte` components.",
                title: "`vector` Literal"
            ),
            "dot" => Card(
                declaration: "dot(vectorA, vectorB)",
                description: "Computes the exact integer dot product of two vectors in the same space.",
                title: "`dot` Function"
            ),
            "similarity" => Card(
                declaration: "similarity(vectorA, vectorB)",
                description: "Computes the cosine similarity of two normalized vectors in the same space as a `Fixed` decimal value.",
                title: "`similarity` Function"
            ),
            "identical" => Card(
                declaration: "identical(vectorA, vectorB)",
                description: "Evaluates to `true` if two vectors have identical components, `false` otherwise.",
                title: "`identical` Predicate Function"
            ),
            "mix" => Card(
                declaration: "transform mix(into: \"current\", terms: [\n    { from: \"stance[$each]\", weight: 3 }\n    { from: embed(\"calm\"), weight: -1 }\n])",
                description: "Writes the normalized weighted sum of 1 to 8 vector terms into a destination vector cell.",
                title: "`mix` Transform"
            ),
            "mean" => Card(
                declaration: "transform mean(from: memories, into: \"self\", where: important)",
                description: "Writes the normalized mean vector of candidate cells in a table into a destination vector cell.",
                title: "`mean` Transform"
            ),
            "nearest" => Card(
                declaration: "transform nearest(from: memories, query: \"situation\", into: recalled, k: 3)",
                description: "Finds the `k` nearest cells in a vector table to a query vector, scoring by dot/similarity or writing the best key into a Text slot.",
                title: "`nearest` Transform"
            ),
            "remember" => Card(
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

        var path = new List<SyntaxNode>();

        FindPath(node: doc, offset: offset, path: path);

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

        var textHash = EmbeddingLock.ComputeTextHash(text: embeddedText);

        if (!space.Entries.TryGetValue(key: textHash, value: out var currentEntry)) {
            sb.AppendLine(value: "- **Lock status:** Not locked (run `puck embed`)");
            sb.AppendLine(handler: $"- **Space:** `{resolvedSpace}` (`{space.Model}`, rev: `{space.Revision}`, dims: {space.Dimensions})");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine(value: "- **Lock status:** Locked");
        sb.AppendLine(handler: $"- **Model:** `{space.Model}` (rev: `{space.Revision}`, dims: {space.Dimensions})");
        sb.AppendLine(handler: $"- **Vector:** `{currentEntry.Vector}`\n");

        // Find three nearest locked texts
        if (StateVector.TryParseBase64Url(currentEntry.Vector, space.Dimensions, out var currentVector, out _)) {
            var nearestList = new List<(string Text, long Score)>();

            foreach (var other in space.Entries.Values) {
                if (string.Equals(a: other.Text, b: embeddedText, comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                if (StateVector.TryParseBase64Url(other.Vector, space.Dimensions, out var otherVector, out _)) {
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
    private static string Card(string title, string declaration, string? description = null) {
        var fence = "```";

        while (declaration.Contains(comparisonType: StringComparison.Ordinal, value: fence)) {
            fence += "`";
        }

        return ($"{title}\n\n{fence}puck\n{declaration}\n{fence}" + (string.IsNullOrWhiteSpace(value: description)
            ? ""
            : $"\n\n{description}"));
    }
    private static bool Contains(SyntaxNode node, int offset) =>
        ((offset >= node.Offset) && (offset < (node.Offset + node.Length)));
    private static IEnumerable<SyntaxNode> Children(SyntaxNode node) => node switch {
        DocumentNode document => document.Statements,
        BlockNode block => block.Statements,
        TemplateNode template => [.. template.Parameters, template.Body],
        TemplateParameterNode { DefaultValue: { } value } => [value],
        LetNode constant => [constant.Value],
        PropertyNode property => [property.Value],
        ExpressionStatementNode statement => [statement.Expression],
        ForStatementNode loop => [loop.Sequence, .. loop.Body],
        RepeatStatementNode repeat => [repeat.Count, .. repeat.Body],
        RuleBlockNode rule => rule.Statements,
        DecisionBlockNode decision => decision.Statements,
        OptionBlockNode option => option.Statements,
        OnNoChoiceBlockNode fallback => fallback.Effects,
        TransactionStatementNode transaction => [.. transaction.MainEffects, .. (transaction.OnFailureEffects ?? [])],
        IfStatementNode branch => [.. branch.Then, .. (branch.Else ?? [])],
        TransformStatementNode transform => [transform.Transform],
        SetCellStatementNode setCell => [setCell.Target, setCell.Rhs],
        AddCellStatementNode addCell => [addCell.Target, addCell.Rhs],
        CompoundAssignStatementNode compAssign => [compAssign.Target, compAssign.Rhs],
        PushStatementNode push => [push.Rhs],
        WhenStatementNode whenStmt => [whenStmt.Predicate],
        AndPredicateNode andPred => andPred.Operands,
        OrPredicateNode orPred => orPred.Operands,
        NotPredicateNode notPred => [notPred.Operand],
        CallExpressionNode call => call.Arguments,
        ArgumentNode argument => [argument.Value],
        StateTableDeclarationNode table => [.. table.Modifiers, .. table.Cells],
        StateSlotDeclarationNode { Value: { } slotValue } slot => [.. slot.Modifiers, slotValue],
        StateSlotDeclarationNode slot => slot.Modifiers,
        StatePileDeclarationNode pile => pile.Modifiers,
        StateGridDeclarationNode grid => [.. grid.Modifiers, .. grid.Cells],
        StateCellEntryNode cell => [cell.Value, .. cell.Modifiers],
        StateModifierNode modifier => modifier.Arguments,
        LambdaExpressionNode lambda => [lambda.Body],
        ArrayExpressionNode array => array.Elements,
        ObjectExpressionNode obj => obj.Properties,
        BinaryExpressionNode binary => [binary.Left, binary.Right],
        UnaryExpressionNode unary => [unary.Operand],
        IndexExpressionNode index => [index.Target, index.Index],
        MemberAccessExpressionNode member => [member.Target],
        RangeExpressionNode range => [.. OptionalRangeChildren(range: range)],
        _ => []
    };
    private static IEnumerable<SyntaxNode> OptionalRangeChildren(RangeExpressionNode range) {
        if (range.Start is { } start) { yield return start; }
        if (range.End is { } end) { yield return end; }
    }
    private static bool FindPath(SyntaxNode node, int offset, List<SyntaxNode> path) {
        if (!Contains(node: node, offset: offset)) {
            return false;
        }

        path.Add(item: node);
        foreach (var child in Children(node: node)) {
            if (FindPath(node: child, offset: offset, path: path)) {
                break;
            }
        }

        return true;
    }
}
