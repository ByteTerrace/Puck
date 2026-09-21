using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Parser coverage for embedding spaces, Vector rows, embed/vector literals, vector operators, and transforms.</summary>
public class EmbeddingDeclarationParserTests {
    private static BlockNode FirstBlock(DocumentNode doc, string identifier) {
        return Assert.IsType<BlockNode>(@object: doc.Statements.OfType<BlockNode>().First(predicate: b => string.Equals(a: b.Identifier, b: identifier, comparisonType: StringComparison.OrdinalIgnoreCase)));
    }
    private static DocumentNode ParseClean(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";

        var (doc, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.NotNull(@object: doc);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );
        return doc!;
    }

    [Fact]
    public void SpacesBlockParsesSpaceDeclarationsWithProperties() {
        var doc = ParseClean(body: """
            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 256
                    }
                }
            }
            """);

        var stateBlock = FirstBlock(doc: doc, identifier: "state");
        var spacesBlock = Assert.IsType<BlockNode>(@object: stateBlock.Statements[0]);

        Assert.Equal("spaces", spacesBlock.Identifier);

        var spaceBlock = Assert.IsType<BlockNode>(@object: spacesBlock.Statements[0]);

        Assert.Equal("space", spaceBlock.Identifier);
        Assert.Equal("lore", (spaceBlock.Name ?? spaceBlock.Target));

        var modelProp = Assert.IsType<PropertyNode>(@object: spaceBlock.Statements[0]);

        Assert.Equal("model", modelProp.Name);
        Assert.Equal("text-embedding-3-small", Assert.IsType<LiteralExpressionNode>(@object: modelProp.Value).Value);

        var revProp = Assert.IsType<PropertyNode>(@object: spaceBlock.Statements[1]);

        Assert.Equal("revision", revProp.Name);
        Assert.Equal("1", Assert.IsType<LiteralExpressionNode>(@object: revProp.Value).Value);

        var dimsProp = Assert.IsType<PropertyNode>(@object: spaceBlock.Statements[2]);

        Assert.Equal("dimensions", dimsProp.Name);
        Assert.Equal(256L, Assert.IsType<LiteralExpressionNode>(@object: dimsProp.Value).Value);
    }
    [Fact]
    public void VectorTableAndSlotDeclarationsParseCorrectly() {
        var doc = ParseClean(body: """
            state {
                world {
                    table memories space("lore") capacity(100) evicts {
                        intro = "Welcome to the world"
                    }
                    slot currentQuery space("lore")
                }
            }
            """);

        var stateBlock = FirstBlock(doc: doc, identifier: "state");
        var worldBlock = Assert.IsType<BlockNode>(@object: stateBlock.Statements[0]);

        var table = Assert.IsType<StateTableDeclarationNode>(@object: worldBlock.Statements[0]);

        Assert.Equal("memories", table.Name);
        Assert.Equal(3, table.Modifiers.Count);
        Assert.Equal("space", table.Modifiers[0].Name);
        Assert.Equal("capacity", table.Modifiers[1].Name);
        Assert.Equal("evicts", table.Modifiers[2].Name);
        Assert.Single(collection: table.Cells);
        Assert.Equal("intro", table.Cells[0].Key);
        Assert.Equal("Welcome to the world", Assert.IsType<LiteralExpressionNode>(@object: table.Cells[0].Value).Value);

        var slot = Assert.IsType<StateSlotDeclarationNode>(@object: worldBlock.Statements[1]);

        Assert.Equal("currentQuery", slot.Name);
        Assert.Single(collection: slot.Modifiers);
        Assert.Equal("space", slot.Modifiers[0].Name);
    }
    [Fact]
    public void TextTableWithEmbedsModifierParsesCorrectly() {
        var doc = ParseClean(body: """
            state {
                world {
                    table loreLog embeds(companionVectors) space("lore") {
                        entry1 = "First entry"
                    }
                }
            }
            """);

        var stateBlock = FirstBlock(doc: doc, identifier: "state");
        var worldBlock = Assert.IsType<BlockNode>(@object: stateBlock.Statements[0]);

        var table = Assert.IsType<StateTableDeclarationNode>(@object: worldBlock.Statements[0]);

        Assert.Equal("loreLog", table.Name);
        Assert.Equal(2, table.Modifiers.Count);
        Assert.Equal("embeds", table.Modifiers[0].Name);
        Assert.Equal("space", table.Modifiers[1].Name);
        Assert.Equal("companionVectors", Assert.IsType<IdentifierExpressionNode>(@object: table.Modifiers[0].Arguments[0].Value).Name);
    }
    [Fact]
    public void TransformStatementsParseAllFourKinds() {
        var doc = ParseClean(body: """
            rule "testTransforms" {
                transform mix(into: current, terms: [
                    { from: "stance[$each]", weight: 3 }
                    { from: embed("calm"), weight: -1 }
                ])
                transform mean(from: memories, into: self)
                transform nearest(from: memories, query: situation, into: recalled, k: 3)
                transform remember(from: memories, query: situation, threshold: 0.8)
            }
            """);

        var rule = Assert.IsType<RuleBlockNode>(@object: doc.Statements[0]);

        Assert.Equal("testTransforms", rule.Name);
        Assert.Equal(4, rule.Statements.Count);

        var t1 = Assert.IsType<TransformStatementNode>(@object: rule.Statements[0]);

        Assert.Equal("mix", t1.Transform.Name);

        var t2 = Assert.IsType<TransformStatementNode>(@object: rule.Statements[1]);

        Assert.Equal("mean", t2.Transform.Name);

        var t3 = Assert.IsType<TransformStatementNode>(@object: rule.Statements[2]);

        Assert.Equal("nearest", t3.Transform.Name);

        var t4 = Assert.IsType<TransformStatementNode>(@object: rule.Statements[3]);

        Assert.Equal("remember", t4.Transform.Name);
    }
    [Fact]
    public void EmbedAndVectorLiteralsParseInExpressions() {
        var doc = ParseClean(body: """
            rule "testLiterals" {
                when similarity(query, embed("calm", space: "lore")) >= 0.7
                push results = embed("discovered")
            }
            rule "testIdentical" {
                when identical(v1, vector("AAAA"))
                push results = vector("AAAA")
            }
            rule "testDot" {
                when dot(v1, v2) > 0
                push results = embed("discovered")
            }
            """);

        Assert.Equal(3, doc.Statements.Count);
        var r1 = Assert.IsType<RuleBlockNode>(@object: doc.Statements[0]);

        Assert.Equal("testLiterals", r1.Name);
        var r2 = Assert.IsType<RuleBlockNode>(@object: doc.Statements[1]);

        Assert.Equal("testIdentical", r2.Name);
        var r3 = Assert.IsType<RuleBlockNode>(@object: doc.Statements[2]);

        Assert.Equal("testDot", r3.Name);
    }
}
