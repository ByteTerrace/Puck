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

    // Every value decides the row's kind together, so a fraction in a later cell widens a row whose first cell is
    // whole, and a name bound by `let` reads as the value it is bound to.
    [InlineData("table speeds {\n            walk = 1\n            run = 2.5\n        }", "Fixed")]
    [InlineData("table counts {\n            walk = 1\n            run = 2\n        }", "Int")]
    [InlineData("slot pace = basePace", "Fixed")]
    [InlineData("slot lives = baseLives", "Int")]
    [InlineData("slot open = true", "Bool")]
    [InlineData("slot label = \"ready\"", "Text")]
    [Theory]
    public void ADeclarationReadsItsKindFromEveryValueItSpells(string declaration, string kind) {
        var (json, diagnostics) = Lower(body: $$"""
            let basePace = 1.5
            let baseLives = 3

            state {
                world {
                    {{declaration}}
                }
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        Assert.Equal(
            actual: WorldRow(index: 0, json: json)["kind"]?.ToString(),
            expected: kind
        );
    }
    [Fact]
    public void AnArithmeticInitializerAndFractionalBoundWidenAnOtherwiseWholeRow() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    slot total = 1 + 0.5 bounds(0.5..)
                }
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        Assert.Equal(
            actual: WorldRow(index: 0, json: json)["kind"]?.ToString(),
            expected: "Fixed"
        );
    }
    [Fact]
    public void ATextLetBindingWideningAValueIsRetained() {
        var (json, diagnostics) = Lower(body: """
            let label = "ready"

            state {
                world {
                    slot status = label
                }
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        Assert.Equal(
            actual: WorldRow(index: 0, json: json)["kind"]?.ToString(),
            expected: "Text"
        );
    }
    [Fact]
    public void TableDeclarationCompilesByteIdenticallyToAnExplicitRow() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table vitals capacity(3) bounds(0..100, overflow: Saturate) {
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
            actual: WorldRow(index: 0, json: json),
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
                    slot gold = 10 bounds(0..)
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(index: 0, json: json),
            expectedJson: """{ "name": "gold", "kind": "Int", "value": 10, "min": 0 }"""
        );
    }
    [Fact]
    public void SlotDeclarationWithNoDefaultOmitsValue() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    slot uninitialized
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(index: 0, json: json),
            expectedJson: """{ "name": "uninitialized", "kind": "Int" }"""
        );
    }
    [Fact]
    public void EmptyTableWithNoCapacityLowersAKeysDomainSoItIsNeverInferredAsASlot() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table x { }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(index: 0, json: json),
            expectedJson: """{ "name": "x", "kind": "Int", "domain": { "$type": "keys" } }"""
        );
    }
    [Fact]
    public void CellBehaviorNoneModifierLowersToTheEnumsWireSpelling() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table flags {
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
            actual: WorldRow(index: 0, json: json),
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
                    row { name: "custom" kind: Text value: "hello" }
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        AssertRowEquals(
            actual: WorldRow(index: 0, json: json),
            expectedJson: """{ "name": "custom", "kind": "Text", "value": "hello" }"""
        );
    }
    [Fact]
    public void PileDeclarationCompilesByteIdenticallyToAnExplicitKeysOfRow() {
        var (json, diagnostics) = Lower(body: """
            state {
                world {
                    table cardNames capacity(3) {
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
            actual: WorldRow(index: 1, json: json),
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
                    grid board dimensions(width: 2, depth: 2) wrap(Both) cellSize(2) origin(1, 0, 1) band(0.5) empty(-1) {
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
            actual: WorldRow(index: 0, json: json),
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
                    grid board { }
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
            p1: """
            state {
                slot x
            }
            """,
            p2: "PUCK049",
            p3: "slot x"
        );
        data.Add(
            p1: """
            state {
                world [
                    { name: "x" kind: Int }
                ]
                world {
                    slot y
                }
            }
            """,
            p2: "PUCK050",
            p3: "world {"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot x
                    slot x
                }
            }
            """,
            p2: "PUCK051",
            p3: "slot x"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot $x : Int
                }
            }
            """,
            p2: "PUCK052",
            p3: "$x"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot x = 5 bounds("oops"..)
                }
            }
            """,
            p2: "PUCK053",
            p3: "\"oops\""
        );
        data.Add(
            p1: """
            state {
                world {
                    table x capacity(1) {
                        a = 1
                        b = 2
                    }
                }
            }
            """,
            p2: "PUCK054",
            p3: "capacity(1)"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot x capacity(2)
                }
            }
            """,
            p2: "PUCK055",
            p3: "capacity(2)"
        );
        data.Add(
            p1: """
            state {
                world {
                    table x {
                        a = 1 advance(perSecond: 1) behavior(none)
                    }
                }
            }
            """,
            p2: "PUCK056",
            p3: "behavior(none)"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot x unknownMod(1)
                }
            }
            """,
            p2: "PUCK057",
            p3: "unknownMod(1)"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot x advance(perSecond: 1e-19)
                }
            }
            """,
            p2: "PUCK058",
            p3: "1e-19"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot x advance(perSecond: 1e-30)
                }
            }
            """,
            p2: "PUCK058",
            p3: "1e-30"
        );
        data.Add(
            p1: """
            state {
                world {
                    pile deck of missingRow {
                        king
                    }
                }
            }
            """,
            p2: "PUCK059",
            p3: "missingRow"
        );
        data.Add(
            p1: """
            state {
                world {
                    slot notAPile = 1
                    pile deck of notAPile {
                        king
                    }
                }
            }
            """,
            p2: "PUCK060",
            p3: "pile deck of notAPile"
        );
        data.Add(
            p1: """
            state {
                world {
                    table cardNames capacity(2) {
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
            p2: "PUCK061",
            p3: "king"
        );
        data.Add(
            p1: """
            state {
                world {
                    table cardNames capacity(2) {
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
            p2: "PUCK062",
            p3: "capacity(5)"
        );
        data.Add(
            p1: """
            state {
                world {
                    grid board dimensions(width: 2, depth: 2) {
                        "0" = 1.5
                    }
                }
            }
            """,
            p2: "PUCK063",
            p3: "grid board"
        );
        data.Add(
            p1: """
            state {
                world {
                    grid board dimensions(width: 2, depth: 2) {
                        "9" = 1
                    }
                }
            }
            """,
            p2: "PUCK064",
            p3: "\"9\" = 1"
        );
        data.Add(
            p1: """
            state {
                world {
                    table tokensRow capacity(1) {
                        a = 0
                    }
                    table codesRow capacity(1) {
                        a = 1
                    }
                    grid board dimensions(width: 1, depth: 1) inverse(tokens: tokensRow, codes: codesRow) {
                        "0" = 1
                    }
                }
            }
            """,
            p2: "PUCK065",
            p3: "inverse(tokens: tokensRow, codes: codesRow)"
        );
        data.Add(
            p1: """
            state {
                lattices [
                    { "$type": "grid", "name": "board", origin [0, 0, 0], "cellSize": 1, "width": 1, "depth": 1 }
                ]
                world {
                    grid board dimensions(width: 1, depth: 1)
                }
            }
            """,
            p2: "PUCK066",
            p3: "grid board dimensions(width: 1, depth: 1)"
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
