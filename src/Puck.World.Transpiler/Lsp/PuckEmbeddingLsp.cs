using System.Text;
using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;

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
                var spaceName = (spaceBlock.Name ?? spaceBlock.Target ?? spaceBlock.Identifier);
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
        var embeddedCard = TryGetEmbeddedTextHoverCard(offset: offset, text: text, sourcePath: sourcePath);
        if (embeddedCard is not null) {
            return embeddedCard;
        }

        // 2. Check if hovering on an embedding keyword or transform
        if (!string.IsNullOrEmpty(word)) {
            return GetKeywordHoverCard(word: word);
        }

        return null;
    }

    /// <summary>Gets a hover card for embedding and vector keywords.</summary>
    /// <param name="word">The identifier or keyword to describe.</param>
    /// <returns>A markdown documentation card, or null.</returns>
    internal static string? GetKeywordHoverCard(string word) {
        return word switch {
            "spaces" => Card(
                title: "`spaces` Block",
                declaration: "spaces {\n    space lore { model: \"text-embedding-3-small\" revision: \"1\" dimensions: 256 }\n}",
                description: "Declares embedding spaces for the document (at most 16 spaces). Each space specifies `model`, `revision`, and `dimensions`."
            ),
            "space" => Card(
                title: "`space` Declaration / Modifier",
                declaration: "space name { model: \"...\" revision: \"...\" dimensions: N }\n// or\ntable memories : Vector = space(\"lore\")",
                description: "Defines an embedding space or associates a `Vector` row with a named space."
            ),
            "Vector" => Card(
                title: "`Vector` Cell Kind",
                declaration: "table memories : Vector = ...\nslot query : Vector = ...",
                description: "A vector state row whose cells hold normalized `sbyte` embedding vectors within a declared space."
            ),
            "evicts" => Card(
                title: "`evicts` Modifier",
                declaration: "table memories : Vector = capacity(100) evicts ...",
                description: "Specifies that a bounded table evicts the earliest or lowest-scoring cell when capacity is reached. Required on `Vector` rows used with `remember`."
            ),
            "embeds" => Card(
                title: "`embeds` Companion Modifier",
                declaration: "table logs : Text embeds(companionVectors, space: lore)",
                description: "Pairs an authored `Text` table with a companion `Vector` table to automatically populate and bake text embeddings."
            ),
            "embed" => Card(
                title: "`embed` Function",
                declaration: "embed(\"text\", [space: \"lore\"])",
                description: "Embeds a text literal into a vector using the specified or default space. Resolved and locked at bake time via `puck embed`."
            ),
            "vector" => Card(
                title: "`vector` Literal",
                declaration: "vector(\"base64url\")",
                description: "A raw vector literal encoded as unpadded URL-safe base64 `sbyte` components."
            ),
            "dot" => Card(
                title: "`dot` Function",
                declaration: "dot(vectorA, vectorB)",
                description: "Computes the exact integer dot product of two vectors in the same space."
            ),
            "similarity" => Card(
                title: "`similarity` Function",
                declaration: "similarity(vectorA, vectorB)",
                description: "Computes the cosine similarity of two normalized vectors in the same space as a `Fixed` decimal value."
            ),
            "identical" => Card(
                title: "`identical` Predicate Function",
                declaration: "identical(vectorA, vectorB)",
                description: "Evaluates to `true` if two vectors have identical components, `false` otherwise."
            ),
            "mix" => Card(
                title: "`mix` Transform",
                declaration: "transform stance = mix(into: \"current\", terms: [\n    { from: \"stance[$each]\", weight: 3 }\n    { from: embed(\"calm\"), weight: -1 }\n])",
                description: "Writes the normalized weighted sum of 1 to 8 vector terms into a destination vector cell."
            ),
            "mean" => Card(
                title: "`mean` Transform",
                declaration: "transform profile = mean(from: memories, into: \"self\", where: important)",
                description: "Writes the normalized mean vector of candidate cells in a table into a destination vector cell."
            ),
            "nearest" => Card(
                title: "`nearest` Transform",
                declaration: "transform recall = nearest(from: memories, query: \"situation\", into: recalled, k: 3)",
                description: "Finds the `k` nearest cells in a vector table to a query vector, scoring by dot/similarity or writing the best key into a Text slot."
            ),
            "remember" => Card(
                title: "`remember` Transform",
                declaration: "transform memory = remember(from: memories, query: \"situation\", threshold: 0.8)",
                description: "Inserts or reinforces a vector memory in an evicting vector table."
            ),
            _ => null,
        };
    }

    private static string? TryGetEmbeddedTextHoverCard(int offset, string text, string? sourcePath) {
        // Parse document with diagnostics to find AST node at offset
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source: text);
        var doc = parseResult.Value;
        if (doc is null) {
            return null;
        }

        var path = new List<SyntaxNode>();
        FindPath(node: doc, offset: offset, path: path);

        string? embeddedText = null;
        string? explicitSpace = null;

        for (var i = (path.Count - 1); i >= 0; i--) {
            var node = path[i];
            if (node is RhsOperandNode rhsOp && TryExtractEmbedFromText(text: rhsOp.Text, embeddedText: out embeddedText, explicitSpace: out explicitSpace)) {
                break;
            }

            if (node is ComparisonPredicateNode compPred) {
                if (TryExtractEmbedFromText(text: compPred.LeftText, embeddedText: out embeddedText, explicitSpace: out explicitSpace) ||
                    TryExtractEmbedFromText(text: compPred.RightText, embeddedText: out embeddedText, explicitSpace: out explicitSpace)) {
                    break;
                }
            }

            if (node is CallExpressionNode call && string.Equals(call.Name, "embed", StringComparison.OrdinalIgnoreCase)) {
                if (call.Arguments.Count > 0 && call.Arguments[0].Value is LiteralExpressionNode { Value: string strText }) {
                    embeddedText = strText;
                }
                if (call.Arguments.Count > 1) {
                    var spArg = call.Arguments.FirstOrDefault(a => string.Equals(a.Name, "space", StringComparison.OrdinalIgnoreCase))
                                ?? call.Arguments[1];
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
                        if (string.Equals(table.Kind, "Vector", StringComparison.OrdinalIgnoreCase)) {
                            embeddedText = cellStr;
                            explicitSpace = table.Modifiers.FirstOrDefault(m => string.Equals(m.Name, "space", StringComparison.OrdinalIgnoreCase))?.Arguments.FirstOrDefault()?.Value switch {
                                LiteralExpressionNode { Value: string s } => s,
                                IdentifierExpressionNode ident => ident.Name,
                                _ => null,
                            };
                            break;
                        }

                        var embedsMod = table.Modifiers.FirstOrDefault(m => string.Equals(m.Name, "embeds", StringComparison.OrdinalIgnoreCase));
                        if (embedsMod is not null) {
                            embeddedText = cellStr;
                            explicitSpace = embedsMod.Arguments.FirstOrDefault(a => string.Equals(a.Name, "space", StringComparison.OrdinalIgnoreCase))?.Value switch {
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
        if (string.IsNullOrEmpty(resolvedSpace)) {
            // Find default space from document
            var spacesBlock = doc.Statements.OfType<BlockNode>().FirstOrDefault(b => string.Equals(b.Identifier, "state", StringComparison.OrdinalIgnoreCase))
                ?.Statements.OfType<BlockNode>().FirstOrDefault(b => string.Equals(b.Identifier, "spaces", StringComparison.OrdinalIgnoreCase));
            if (spacesBlock is not null) {
                var spaceDecls = spacesBlock.Statements.OfType<BlockNode>().Where(b => string.Equals(b.Identifier, "space", StringComparison.OrdinalIgnoreCase)).ToList();
                if (spaceDecls.Count == 1) {
                    resolvedSpace = (spaceDecls[0].Name ?? spaceDecls[0].Target ?? spaceDecls[0].Identifier);
                }
            }
        }

        resolvedSpace ??= "default";

        // Load embedding lock
        var lockFile = EmbeddingLock.TryLoad(rootSourcePath: sourcePath);
        var sb = new StringBuilder();
        sb.AppendLine($"**Embedded Text** (`{resolvedSpace}`)\n");
        sb.AppendLine($"\"{embeddedText}\"\n");

        if (lockFile is null) {
            sb.AppendLine("- **Lock status:** Not locked (no lock file found; run `puck embed`)");
            sb.AppendLine($"- **Space:** `{resolvedSpace}`");
            return sb.ToString().TrimEnd();
        }

        if (!lockFile.Spaces.TryGetValue(resolvedSpace, out var space)) {
            if (lockFile.Spaces.Count == 1) {
                space = lockFile.Spaces.Values.First();
                resolvedSpace = lockFile.Spaces.Keys.First();
            } else {
                sb.AppendLine($"- **Lock status:** Not locked (space '{resolvedSpace}' not found in lock; run `puck embed`)");
                return sb.ToString().TrimEnd();
            }
        }

        var textHash = EmbeddingLock.ComputeTextHash(text: embeddedText);
        if (!space.Entries.TryGetValue(textHash, out var currentEntry)) {
            sb.AppendLine("- **Lock status:** Not locked (run `puck embed`)");
            sb.AppendLine($"- **Space:** `{resolvedSpace}` (`{space.Model}`, rev: `{space.Revision}`, dims: {space.Dimensions})");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("- **Lock status:** Locked");
        sb.AppendLine($"- **Model:** `{space.Model}` (rev: `{space.Revision}`, dims: {space.Dimensions})");
        sb.AppendLine($"- **Vector:** `{currentEntry.Vector}`\n");

        // Find three nearest locked texts
        if (StateVector.TryParseBase64Url(currentEntry.Vector, space.Dimensions, out var currentVector, out _)) {
            var nearestList = new List<(string Text, long Score)>();
            foreach (var other in space.Entries.Values) {
                if (string.Equals(other.Text, embeddedText, StringComparison.Ordinal)) {
                    continue;
                }

                if (StateVector.TryParseBase64Url(other.Vector, space.Dimensions, out var otherVector, out _)) {
                    var dot = SignedByteVectorFunctions.Dot(left: currentVector.Components, right: otherVector.Components);
                    nearestList.Add((other.Text, dot));
                }
            }

            if (nearestList.Count > 0) {
                nearestList.Sort((a, b) => {
                    var cmp = b.Score.CompareTo(a.Score);
                    return cmp != 0 ? cmp : string.Compare(a.Text, b.Text, StringComparison.Ordinal);
                });

                sb.AppendLine("**Nearest locked texts:**");
                var count = Math.Min(3, nearestList.Count);
                for (var i = 0; i < count; i++) {
                    var (nText, nScore) = nearestList[i];
                    sb.AppendLine($"{i + 1}. \"{nText}\" (score: {nScore})");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryExtractEmbedFromText(string text, out string? embeddedText, out string? explicitSpace) {
        embeddedText = null;
        explicitSpace = null;
        if (ExpressionSpelling.TryParseVector(text: text, token: out var vecOp, error: out _) && vecOp is VectorOperandToken.Embed emb) {
            embeddedText = emb.Text;
            explicitSpace = emb.Space;
            return true;
        }
        if (ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out _)) {
            foreach (var tok in tokens) {
                if (tok is ValueToken.VectorCall vc) {
                    if (vc.Left is VectorOperandToken.Embed leftEmb) {
                        embeddedText = leftEmb.Text;
                        explicitSpace = leftEmb.Space;
                        return true;
                    }
                    if (vc.Right is VectorOperandToken.Embed rightEmb) {
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
        while (declaration.Contains(value: fence, comparisonType: StringComparison.Ordinal)) {
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
        RangeExpressionNode range => [range.Start, range.End],
        _ => []
    };

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
