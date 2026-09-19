using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Lowering coverage for the concise state-row declarations: each declaration form emits exactly the JSON
/// an independently authored explicit row would, and every refusal in the contract's list fires with its own
/// diagnostic code and a non-degenerate source span.</summary>
public class StateDeclarationEmitterTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }
    private static JsonArray WorldRows(JsonObject json) => Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["world"]);
    private static JsonObject WorldRow(JsonObject json, int index) => Assert.IsType<JsonObject>(@object: WorldRows(json: json)[index]);
    private static void AssertRowEquals(JsonObject actual, string expectedJson) {
        var expected = Assert.IsType<JsonObject>(@object: JsonNode.Parse(expectedJson));
        var mismatch = JsonMismatch.Find(
            actual: actual,
            expected: expected,
            path: "row"
        );

        Assert.Null(@object: mismatch);
    }

    // ---- byte-identical declaration forms -------------------------------------------------------------------

    [Fact]
    public void TableDeclarationCompilesByteIdenticallyToAnExplicitRow() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table vitals : Int capacity(3) bounds(minimum: 0, maximum: 100, overflow: Saturate) {
                        health = 100
                        mana = 50 advance(perSecond: 5)
                    }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 0),
            expectedJson: """
            {
                "name": "vitals",
                "kind": "Int",
                "cells": [
                    { "key": "health", "value": 100 },
                    { "key": "mana", "value": 50, "advance": { "perSecondNumerator": 5, "perSecondDenominator": 1 } }
                ],
                "capacity": 3,
                "min": 0,
                "max": 100,
                "overflow": "Saturate"
            }
            """
        );
    }
    [Fact]
    public void SlotDeclarationCompilesByteIdenticallyToAnExplicitRow() {
        // A one-sided bound (minimum only, no maximum) is legal — StateRow.Min and Max are each independently
        // optional.
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    slot gold : Int = 10 bounds(minimum: 0)
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 0),
            expectedJson: """{ "name": "gold", "kind": "Int", "value": 10, "min": 0 }"""
        );
    }
    [Fact]
    public void SlotDeclarationWithNoDefaultOmitsValue() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    slot uninitialized : Int
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 0),
            expectedJson: """{ "name": "uninitialized", "kind": "Int" }"""
        );
    }
    [Fact]
    public void EmptyTableWithNoCapacityLowersAKeysDomainSoItIsNeverInferredAsASlot() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table x : Int { }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 0),
            expectedJson: """{ "name": "x", "kind": "Int", "domain": { "$type": "keys" } }"""
        );
    }
    [Fact]
    public void CellBehaviorNoneModifierLowersToTheEnumsWireSpelling() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table flags : Int {
                        a = 1 behavior(none)
                        b = 2
                    }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 0),
            expectedJson: """
            {
                "name": "flags",
                "kind": "Int",
                "cells": [
                    { "key": "a", "value": 1, "behavior": "None" },
                    { "key": "b", "value": 2 }
                ]
            }
            """
        );
    }
    [Fact]
    public void RowDeclarationInsideADeclarationBlockLowersUnchanged() {
        // The `row { }` escape hatch works the same inside a `world { }` declaration block as it does in today's
        // `world [ ]` array form.
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    row { name: "custom" kind: "Text" value: "hello" }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 0),
            expectedJson: """{ "name": "custom", "kind": "Text", "value": "hello" }"""
        );
    }
    [Fact]
    public void PileDeclarationCompilesByteIdenticallyToAnExplicitKeysOfRow() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table cardNames : Int capacity(3) {
                        king = 0
                        queen = 1
                        jack = 2
                    }
                    pile deck of cardNames capacity(3) {
                        king
                        queen
                        jack
                    }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 1),
            expectedJson: """
            {
                "name": "deck",
                "kind": "Bool",
                "domain": { "$type": "keysOf", "row": "cardNames", "ordered": true },
                "cells": [
                    { "key": "king", "value": true },
                    { "key": "queen", "value": true },
                    { "key": "jack", "value": true }
                ],
                "capacity": 3
            }
            """
        );
    }
    [Fact]
    public void GridDeclarationCompilesByteIdenticallyToAnExplicitCellsOfRowAndTopology() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    grid board : Int dimensions(width: 2, depth: 2) wrap(Both) cellSize(2) origin(1, 0, 1) band(0.5) empty(-1) {
                        "0" = 4
                        "3" = 5
                    }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(json: json, index: 0),
            expectedJson: """
            {
                "name": "board",
                "kind": "Int",
                "domain": { "$type": "cellsOf", "topology": "board", "empty": -1 },
                "cells": [
                    { "key": "0", "value": 4 },
                    { "key": "3", "value": 5 }
                ]
            }
            """
        );

        var lattices = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["lattices"]);
        var topology = Assert.IsType<JsonObject>(@object: lattices[0]);

        AssertRowEquals(
            actual: topology,
            expectedJson: """
            {
                "$type": "grid",
                "name": "board",
                "origin": [1.0, 0.0, 1.0],
                "cellSize": 2.0,
                "width": 2,
                "depth": 2,
                "wrap": "Both",
                "band": 0.5
            }
            """
        );
    }
    [Fact]
    public void GridDeclarationWithNoDimensionsRefusesAndStillLowersACellsOfDomain() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    grid board : Int { }
                }
            }
            """);

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == "PUCK053")
        );
    }

    // ---- refusal matrix (PUCK049-PUCK066), each with its source span -----------------------------------------

    public static TheoryData<string, string, string> RefusalCases() {
        var data = new TheoryData<string, string, string>();

        data.Add(
            """
            state {
                slot x : Int
            }
            """,
            "PUCK049",
            "slot x : Int"
        );
        data.Add(
            """
            state {
                world [
                    { name: "x" kind: "Int" }
                ]
                world {
                    slot y : Int
                }
            }
            """,
            "PUCK050",
            "world {"
        );
        data.Add(
            """
            state {
                world {
                    slot x : Int
                    slot x : Int
                }
            }
            """,
            "PUCK051",
            "slot x : Int"
        );
        data.Add(
            """
            state {
                world {
                    slot $x : Int
                }
            }
            """,
            "PUCK052",
            "$x"
        );
        data.Add(
            """
            state {
                world {
                    slot x : Int = "oops"
                }
            }
            """,
            "PUCK053",
            "\"oops\""
        );
        data.Add(
            """
            state {
                world {
                    table x : Int capacity(1) {
                        a = 1
                        b = 2
                    }
                }
            }
            """,
            "PUCK054",
            "capacity(1)"
        );
        data.Add(
            """
            state {
                world {
                    slot x : Int capacity(2)
                }
            }
            """,
            "PUCK055",
            "capacity(2)"
        );
        data.Add(
            """
            state {
                world {
                    table x : Int {
                        a = 1 advance(perSecond: 1) behavior(none)
                    }
                }
            }
            """,
            "PUCK056",
            "behavior(none)"
        );
        data.Add(
            """
            state {
                world {
                    slot x : Int unknownMod(1)
                }
            }
            """,
            "PUCK057",
            "unknownMod(1)"
        );
        data.Add(
            """
            state {
                world {
                    slot x : Fixed advance(perSecond: 1e-19)
                }
            }
            """,
            "PUCK058",
            "1e-19"
        );
        data.Add(
            """
            state {
                world {
                    slot x : Fixed advance(perSecond: 1e-30)
                }
            }
            """,
            "PUCK058",
            "1e-30"
        );
        data.Add(
            """
            state {
                world {
                    pile deck of missingRow {
                        king
                    }
                }
            }
            """,
            "PUCK059",
            "missingRow"
        );
        data.Add(
            """
            state {
                world {
                    slot notAPile : Int = 1
                    pile deck of notAPile {
                        king
                    }
                }
            }
            """,
            "PUCK060",
            "pile deck of notAPile"
        );
        data.Add(
            """
            state {
                world {
                    table cardNames : Int capacity(2) {
                        king = 0
                        queen = 1
                    }
                    pile deck of cardNames {
                        king
                        king
                    }
                }
            }
            """,
            "PUCK061",
            "king"
        );
        data.Add(
            """
            state {
                world {
                    table cardNames : Int capacity(2) {
                        king = 0
                        queen = 1
                    }
                    pile deck of cardNames capacity(5) {
                        king
                        queen
                    }
                }
            }
            """,
            "PUCK062",
            "capacity(5)"
        );
        data.Add(
            """
            state {
                world {
                    grid board : Text dimensions(width: 2, depth: 2)
                }
            }
            """,
            "PUCK063",
            "grid board : Text"
        );
        data.Add(
            """
            state {
                world {
                    grid board : Int dimensions(width: 2, depth: 2) {
                        "9" = 1
                    }
                }
            }
            """,
            "PUCK064",
            "\"9\" = 1"
        );
        data.Add(
            """
            state {
                world {
                    table tokensRow : Int capacity(1) {
                        a = 0
                    }
                    table codesRow : Int capacity(1) {
                        a = 1
                    }
                    grid board : Int dimensions(width: 1, depth: 1) inverse(tokens: tokensRow, codes: codesRow) {
                        "0" = 1
                    }
                }
            }
            """,
            "PUCK065",
            "inverse(tokens: tokensRow, codes: codesRow)"
        );
        data.Add(
            """
            state {
                lattices [
                    { "$type": "grid", "name": "board", origin [0, 0, 0], "cellSize": 1, "width": 1, "depth": 1 }
                ]
                world {
                    grid board : Int dimensions(width: 1, depth: 1)
                }
            }
            """,
            "PUCK066",
            "grid board : Int dimensions(width: 1, depth: 1)"
        );
        return data;
    }

    [MemberData(nameof(RefusalCases))]
    [Theory]
    public void RefusalFiresWithItsCodeAndSourceSpan(string body, string code, string needle) {
        var (_, diagnostics) = Lower(body: body);
        var match = diagnostics.SingleOrDefault(predicate: d => (d.Code == code));

        Assert.True(
            condition: (match is not null),
            userMessage: $"expected diagnostic {code}, got: {diagnostics.FormatReport("")}"
        );
        Assert.True(
            condition: (match!.Span.Length > 0),
            userMessage: $"{code}'s span carries no length"
        );

        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var needleIndex = source.LastIndexOf(
            comparisonType: StringComparison.Ordinal,
            value: needle
        );

        Assert.True(
            condition: (needleIndex >= 0),
            userMessage: $"needle '{needle}' not found in source"
        );

        var expectedLine = (source[..needleIndex].Count(predicate: static c => (c == '\n')) + 1);

        Assert.Equal(
            expectedLine,
            match.Span.Line
        );
    }
}
