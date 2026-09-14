using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Parser coverage for the concise state-row declarations (state-authoring stages 2, 7, 8): the
/// <c>table</c>/<c>slot</c>/<c>pile</c>/<c>grid</c> keywords' disambiguation from an ordinary property of the same
/// name, and the shape of every declaration node the parser produces.</summary>
public class StateDeclarationParserTests {
    private static BlockNode FirstWorldBlock(DocumentNode doc) {
        var stateBlock = Assert.IsType<BlockNode>(@object: doc.Statements[0]);

        return Assert.IsType<BlockNode>(@object: stateBlock.Statements[0]);
    }
    private static DocumentNode ParseClean(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var (doc, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.NotNull(@object: doc);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );
        return doc!;
    }

    [Fact]
    public void TableDeclarationParsesNameKindModifiersAndCells() {
        var doc = ParseClean(body: """
            state {
                world {
                    table vitals : Int bounds(minimum: 0, maximum: 100) {
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
        Assert.Equal(
            "Int",
            table.Kind
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
        var doc = ParseClean(body: """
            state {
                world {
                    slot gold : Int = 10 bounds(minimum: 0)
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var slot = Assert.IsType<StateSlotDeclarationNode>(@object: world.Statements[0]);

        Assert.Equal(
            "gold",
            slot.Name
        );
        Assert.Equal(
            "Int",
            slot.Kind
        );
        Assert.NotNull(@object: slot.Value);
        Assert.Single(collection: slot.Modifiers);
    }
    [Fact]
    public void SlotDeclarationWithNoValueLeavesValueNull() {
        var doc = ParseClean(body: """
            state {
                world {
                    slot uninitialized : Int
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
        var doc = ParseClean(body: """
            state {
                world {
                    row { name: "deckCards" kind: "Int" capacity: 4 }
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
        var doc = ParseClean(body: """
            state {
                world {
                    grid board : Int dimensions(width: 8, depth: 8) wrap(Both) {
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
            "Int",
            grid.Kind
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
        var doc = ParseClean(body: """
            state {
                world {
                    grid board : Int dimensions(width: 8, depth: 8)
                }
            }
            """);
        var world = FirstWorldBlock(doc: doc);
        var grid = Assert.IsType<StateGridDeclarationNode>(@object: world.Statements[0]);

        Assert.False(condition: grid.HasBody);
        Assert.Empty(collection: grid.Cells);
    }
    [Fact]
    public void TableAndSlotColonPropertiesInAnOrdinaryBlockStillParseAsProperties() {
        var doc = ParseClean(body: """
            host {
                table: 5
                slot: "y"
            }
            """);
        var host = Assert.IsType<BlockNode>(@object: doc.Statements[0]);
        var table = Assert.IsType<PropertyNode>(@object: host.Statements[0]);
        var slot = Assert.IsType<PropertyNode>(@object: host.Statements[1]);

        Assert.Equal(
            "table",
            table.Name
        );
        Assert.Equal(
            "slot",
            slot.Name
        );
    }
    [Fact]
    public void TableAndSlotColonPropertiesInsideARowBlockStillParseAsProperties() {
        var doc = ParseClean(body: """
            state {
                world {
                    row {
                        name: "x"
                        kind: "Int"
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
    [Fact]
    public void PileAndGridColonPropertiesStillParseAsProperties() {
        var doc = ParseClean(body: """
            host {
                pile: 1
                grid: 2
            }
            """);
        var host = Assert.IsType<BlockNode>(@object: doc.Statements[0]);
        var pile = Assert.IsType<PropertyNode>(@object: host.Statements[0]);
        var grid = Assert.IsType<PropertyNode>(@object: host.Statements[1]);

        Assert.Equal(
            "pile",
            pile.Name
        );
        Assert.Equal(
            "grid",
            grid.Name
        );
    }
}
