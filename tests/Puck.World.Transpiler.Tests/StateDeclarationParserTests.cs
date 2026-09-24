using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Parser coverage for the concise state-row declarations: the
/// <c>table</c>/<c>slot</c>/<c>pile</c>/<c>grid</c> keywords' disambiguation from an ordinary property of the same
/// name, and the shape of every declaration node the parser produces.</summary>
public class StateDeclarationParserTests {
    private static BlockNode FirstWorldBlock(DocumentNode doc) {
        var stateBlock = Assert.IsType<BlockNode>(@object: doc.Statements[0]);

        return Assert.IsType<BlockNode>(@object: stateBlock.Statements[0]);
    }

    [Fact]
    public void TableDeclarationParsesNameKindModifiersAndCells() {
        var doc = WorldSources.ParseClean(body: """
            state {
                world {
                    table vitals bounds(0..100) {
                        health = 100
                        mana = 50 advance(perSecond: 5)
                    }
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var table = Assert.IsType<StateTableDeclarationNode>(@object: world.Statements[0]);

        Assert.Equal(
            "vitals",
            table.Name
        );
        Assert.Single(collection: table.Modifiers);
        Assert.Equal(
            "bounds",
            table.Modifiers[0].Name
        );
        Assert.Equal(
            2,
            table.Cells.Count
        );
        Assert.Equal(
            "health",
            table.Cells[0].Key
        );
        Assert.Empty(collection: table.Cells[0].Modifiers);
        Assert.Equal(
            "mana",
            table.Cells[1].Key
        );
        Assert.Single(collection: table.Cells[1].Modifiers);
        Assert.Equal(
            "advance",
            table.Cells[1].Modifiers[0].Name
        );
    }
    [Fact]
    public void SlotDeclarationParsesOptionalValueAndModifiers() {
        var doc = WorldSources.ParseClean(body: """
            state {
                world {
                    slot gold = 10 bounds(0..)
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var slot = Assert.IsType<StateSlotDeclarationNode>(@object: world.Statements[0]);

        Assert.Equal(
            "gold",
            slot.Name
        );
        Assert.NotNull(@object: slot.Value);
        Assert.Single(collection: slot.Modifiers);
    }
    [Fact]
    public void SlotDeclarationWithNoValueLeavesValueNull() {
        var doc = WorldSources.ParseClean(body: """
            state {
                world {
                    slot uninitialized
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var slot = Assert.IsType<StateSlotDeclarationNode>(@object: world.Statements[0]);

        Assert.Null(@object: slot.Value);
        Assert.Empty(collection: slot.Modifiers);
    }
    [Fact]
    public void PileDeclarationParsesTokenRowAndTokens() {
        var doc = WorldSources.ParseClean(body: """
            state {
                world {
                    row { name: "deckCards" kind: Int capacity: 4 }
                    pile deck of deckCards capacity(4) {
                        king
                        queen
                        "jack of hearts"
                    }
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var pile = Assert.IsType<StatePileDeclarationNode>(@object: world.Statements[1]);

        Assert.Equal(
            "deck",
            pile.Name
        );
        Assert.Equal(
            "deckCards",
            pile.TokenRow
        );
        Assert.Single(collection: pile.Modifiers);
        Assert.Equal(
            "capacity",
            pile.Modifiers[0].Name
        );
        Assert.Equal(
            3,
            pile.Tokens.Count
        );
        Assert.Equal(
            "king",
            pile.Tokens[0].Key
        );
        Assert.Equal(
            "jack of hearts",
            pile.Tokens[2].Key
        );
    }
    [Fact]
    public void GridDeclarationParsesDimensionsModifiersAndBody() {
        var doc = WorldSources.ParseClean(body: """
            state {
                world {
                    grid board dimensions(width: 8, depth: 8) wrap(Both) {
                        "0" = 4
                    }
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var grid = Assert.IsType<StateGridDeclarationNode>(@object: world.Statements[0]);

        Assert.Equal(
            "board",
            grid.Name
        );
        Assert.Equal(
            2,
            grid.Modifiers.Count
        );
        Assert.True(condition: grid.HasBody);
        Assert.Single(collection: grid.Cells);
        Assert.Equal(
            "0",
            grid.Cells[0].Key
        );
    }
    [Fact]
    public void GridDeclarationWithNoBodyHasBodyFalseAndNoCells() {
        var doc = WorldSources.ParseClean(body: """
            state {
                world {
                    grid board dimensions(width: 8, depth: 8)
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var grid = Assert.IsType<StateGridDeclarationNode>(@object: world.Statements[0]);

        Assert.False(condition: grid.HasBody);
        Assert.Empty(collection: grid.Cells);
    }
    [Fact]
    public void TableSlotAndGridReportPuck107WhenTheyStillSpellAnExplicitKindOrRecord() {
        var source = """
            schema: "puck.world.definition.v1"

            state {
                record Card { rank: Int }
                world {
                    table vitals : Int { health = 100 }
                    table cards : Card { first = 1 }
                    slot gold : Int = 10
                    grid board : Int dimensions(width: 8, depth: 8)
                }
            }
            """;

        // A kind word is refused where it is read; whether `: Card` names an enum or a record is the lowering's answer.
        var (doc, parsed) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.NotNull(@object: doc);
        Assert.Equal(
            3,
            parsed.Where(predicate: d => (d.Code == "PUCK107")).Count()
        );

        var diagnostics = WorldSources.Compile(source: source).Diagnostics;

        Assert.Equal(
            4,
            diagnostics.Where(predicate: d => (d.Code == "PUCK107")).Count()
        );
        Assert.Contains(
            collection: diagnostics,
            filter: diagnostic => diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "pool cards of Card capacity(...)")
        );
    }
    // A declaration keyword followed by a colon is an ordinary property of that name.
    [InlineData("table: 5", "slot: \"y\"")]
    [InlineData("pile: 1", "grid: 2")]
    [Theory]
    public void DeclarationKeywordColonPropertiesInAnOrdinaryBlockStillParseAsProperties(string first, string second) {
        var doc = WorldSources.ParseClean(body: $$"""
            host {
                {{first}}
                {{second}}
            }
            """);
        var host = Assert.IsType<BlockNode>(@object: doc.Statements[0]);

        Assert.Equal(
            actual: host.Statements.Select(selector: static statement => Assert.IsType<PropertyNode>(@object: statement).Name),
            expected: [first[..first.IndexOf(value: ':')], second[..second.IndexOf(value: ':')]]
        );
    }
    [Fact]
    public void TableAndSlotColonPropertiesInsideARowBlockStillParseAsProperties() {
        var doc = WorldSources.ParseClean(body: """
            state {
                world {
                    row {
                        name: "x"
                        kind: Int
                        table: "note"
                        slot: "another note"
                    }
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var row = Assert.IsType<BlockNode>(@object: world.Statements[0]);
        var tableProp = Assert.IsType<PropertyNode>(@object: row.Statements[2]);
        var slotProp = Assert.IsType<PropertyNode>(@object: row.Statements[3]);

        Assert.Equal(
            "table",
            tableProp.Name
        );
        Assert.Equal(
            "slot",
            slotProp.Name
        );
    }
}
