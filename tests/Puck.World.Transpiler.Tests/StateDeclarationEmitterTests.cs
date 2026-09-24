using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Lowering coverage for the concise state-row declarations: each declaration form emits exactly the JSON
/// an independently authored explicit row would, and every refusal in the contract's list fires with its own
/// diagnostic code on its own line.</summary>
public class StateDeclarationEmitterTests {
    // Each declaration, the index of the world row it lowers to, and that row as an author would write it by hand.
    private static readonly Dictionary<string, (string Body, int Row, string Expected)> Declarations = new(comparer: StringComparer.Ordinal) {
        ["a table with capacity, bounds and a cell advance"] = (
            Body: """
                state {
                    world {
                        table vitals capacity(3) bounds(0..100, overflow: Saturate) {
                            health = 100
                            mana = 50 advance(perSecond: 5)
                        }
                    }
                }
                """,
            Row: 0,
            Expected: """
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
        ),
        // StateRow.Min and Max are each independently optional, so a one-sided bound is legal.
        ["a slot with a one-sided bound"] = (
            Body: """
                state {
                    world {
                        slot gold = 10 bounds(0..)
                    }
                }
                """,
            Row: 0,
            Expected: """{ "name": "gold", "kind": "Int", "value": 10, "min": 0 }"""
        ),
        ["a slot with no default omits its value"] = (
            Body: """
                state {
                    world {
                        slot uninitialized
                    }
                }
                """,
            Row: 0,
            Expected: """{ "name": "uninitialized", "kind": "Int" }"""
        ),
        ["an empty table with no capacity is a keys domain, never a slot"] = (
            Body: """
                state {
                    world {
                        table x { }
                    }
                }
                """,
            Row: 0,
            Expected: """{ "name": "x", "kind": "Int", "domain": { "$type": "keys" } }"""
        ),
        ["behavior(none) lowers to the enum's wire spelling"] = (
            Body: """
                state {
                    world {
                        table flags {
                            a = 1 behavior(none)
                            b = 2
                        }
                    }
                }
                """,
            Row: 0,
            Expected: """
                {
                    "name": "flags",
                    "kind": "Int",
                    "cells": [
                        { "key": "a", "value": 1, "behavior": "None" },
                        { "key": "b", "value": 2 }
                    ]
                }
                """
        ),
        // The `row { }` escape hatch works the same inside a `world { }` declaration block as in the `world [ ]`
        // array form.
        ["a row inside a declaration block lowers unchanged"] = (
            Body: """
                state {
                    world {
                        row { name: "custom" kind: Text value: "hello" }
                    }
                }
                """,
            Row: 0,
            Expected: """{ "name": "custom", "kind": "Text", "value": "hello" }"""
        ),
        ["a pile is an ordered keysOf row"] = (
            Body: """
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
                """,
            Row: 1,
            Expected: """
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
        ),
    };
    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["PUCK049: a declaration outside a world block"] = new(
            Body: "state {\n    slot x\n}\n",
            Code: "PUCK049",
            Needle: "slot x"
        ),
        ["PUCK050: a declaration block beside a world array"] = new(
            Body: "state {\n    world [\n        { name: \"x\" kind: Int }\n    ]\n    world {\n        slot y\n    }\n}\n",
            Code: "PUCK050",
            Needle: "world {"
        ),
        ["PUCK051: a name declared twice"] = new(
            Body: "state {\n    world {\n        slot x\n        slot x\n    }\n}\n",
            Code: "PUCK051",
            Needle: "slot x"
        ),
        ["PUCK052: a name the language reserves"] = new(
            Body: "state {\n    world {\n        slot $x : Int\n    }\n}\n",
            Code: "PUCK052",
            Needle: "$x"
        ),
        ["PUCK053: a bound that is not a number"] = new(
            Body: "state {\n    world {\n        slot x = 5 bounds(\"oops\"..)\n    }\n}\n",
            Code: "PUCK053",
            Needle: "\"oops\""
        ),
        ["PUCK053: a grid with no dimensions"] = new(
            Body: "state {\n    world {\n        grid board { }\n    }\n}\n",
            Code: "PUCK053",
            Needle: "grid board"
        ),
        ["PUCK054: more cells than capacity"] = new(
            Body: "state {\n    world {\n        table x capacity(1) {\n            a = 1\n            b = 2\n        }\n    }\n}\n",
            Code: "PUCK054",
            Needle: "capacity(1)"
        ),
        ["PUCK055: capacity on a slot"] = new(
            Body: "state {\n    world {\n        slot x capacity(2)\n    }\n}\n",
            Code: "PUCK055",
            Needle: "capacity(2)"
        ),
        ["PUCK056: an advance beside behavior(none)"] = new(
            Body: "state {\n    world {\n        table x {\n            a = 1 advance(perSecond: 1) behavior(none)\n        }\n    }\n}\n",
            Code: "PUCK056",
            Needle: "behavior(none)"
        ),
        ["PUCK057: an unknown modifier"] = new(
            Body: "state {\n    world {\n        slot x unknownMod(1)\n    }\n}\n",
            Code: "PUCK057",
            Needle: "unknownMod(1)"
        ),
        ["PUCK058: an advance below the finest rate"] = new(
            Body: "state {\n    world {\n        slot x advance(perSecond: 1e-19)\n    }\n}\n",
            Code: "PUCK058",
            Needle: "1e-19"
        ),
        ["PUCK058: an advance far below the finest rate"] = new(
            Body: "state {\n    world {\n        slot x advance(perSecond: 1e-30)\n    }\n}\n",
            Code: "PUCK058",
            Needle: "1e-30"
        ),
        ["PUCK059: a pile of an undeclared row"] = new(
            Body: "state {\n    world {\n        pile deck of missingRow {\n            king\n        }\n    }\n}\n",
            Code: "PUCK059",
            Needle: "missingRow"
        ),
        ["PUCK060: a pile of a slot"] = new(
            Body: "state {\n    world {\n        slot notAPile = 1\n        pile deck of notAPile {\n            king\n        }\n    }\n}\n",
            Code: "PUCK060",
            Needle: "pile deck of notAPile"
        ),
        ["PUCK061: a card piled twice"] = new(
            Body: "state {\n    world {\n        table cardNames capacity(2) {\n            king = 0\n            queen = 1\n        }\n        pile deck of cardNames {\n            king\n            king\n        }\n    }\n}\n",
            Code: "PUCK061",
            Needle: "king"
        ),
        ["PUCK062: a pile larger than its source"] = new(
            Body: "state {\n    world {\n        table cardNames capacity(2) {\n            king = 0\n            queen = 1\n        }\n        pile deck of cardNames capacity(5) {\n            king\n            queen\n        }\n    }\n}\n",
            Code: "PUCK062",
            Needle: "capacity(5)"
        ),
        ["PUCK063: a fraction in a grid"] = new(
            Body: "state {\n    world {\n        grid board dimensions(width: 2, depth: 2) {\n            \"0\" = 1.5\n        }\n    }\n}\n",
            Code: "PUCK063",
            Needle: "grid board"
        ),
        ["PUCK064: a grid cell outside its dimensions"] = new(
            Body: "state {\n    world {\n        grid board dimensions(width: 2, depth: 2) {\n            \"9\" = 1\n        }\n    }\n}\n",
            Code: "PUCK064",
            Needle: "\"9\" = 1"
        ),
        ["PUCK065: an inverse whose rows do not agree"] = new(
            Body: "state {\n    world {\n        table tokensRow capacity(1) {\n            a = 0\n        }\n        table codesRow capacity(1) {\n            a = 1\n        }\n        grid board dimensions(width: 1, depth: 1) inverse(tokens: tokensRow, codes: codesRow) {\n            \"0\" = 1\n        }\n    }\n}\n",
            Code: "PUCK065",
            Needle: "inverse(tokens: tokensRow, codes: codesRow)"
        ),
        ["PUCK066: a grid declared beside a lattice of its name"] = new(
            Body: "state {\n    lattices [\n        { \"$type\": \"grid\", \"name\": \"board\", origin [0, 0, 0], \"cellSize\": 1, \"width\": 1, \"depth\": 1 }\n    ]\n    world {\n        grid board dimensions(width: 1, depth: 1)\n    }\n}\n",
            Code: "PUCK066",
            Needle: "grid board dimensions(width: 1, depth: 1)"
        ),
    };

    private static JsonObject WorldRow(JsonObject json, int index) => Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["world"])[index]);

    public static TheoryData<string> DeclarationNames() => new(values: Declarations.Keys);
    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    // Every value decides the row's kind together, so a fraction in a later cell, an arithmetic initializer or a
    // fractional bound widens a row whose first value is whole, and a name bound by `let` reads as the value it is
    // bound to.
    [InlineData("table speeds {\n            walk = 1\n            run = 2.5\n        }", "Fixed")]
    [InlineData("table counts {\n            walk = 1\n            run = 2\n        }", "Int")]
    [InlineData("slot pace = basePace", "Fixed")]
    [InlineData("slot lives = baseLives", "Int")]
    [InlineData("slot open = true", "Bool")]
    [InlineData("slot label = \"ready\"", "Text")]
    [InlineData("slot status = baseLabel", "Text")]
    [InlineData("slot total = 1 + 0.5 bounds(0.5..)", "Fixed")]
    [Theory]
    public void ADeclarationReadsItsKindFromEveryValueItSpells(string declaration, string kind) {
        var json = WorldSources.LowerClean(body: $$"""
            let basePace = 1.5
            let baseLives = 3
            let baseLabel = "ready"

            state {
                world {
                    {{declaration}}
                }
            }
            """);

        Assert.Equal(
            actual: WorldRow(index: 0, json: json)["kind"]?.ToString(),
            expected: kind
        );
    }
    [MemberData(nameof(DeclarationNames))]
    [Theory]
    public void ADeclarationCompilesByteIdenticallyToAnExplicitRow(string name) {
        var (body, row, expected) = Declarations[name];

        Assert.Null(@object: JsonMismatch.Find(
            actual: WorldRow(index: row, json: WorldSources.LowerClean(body: body)),
            expected: JsonNode.Parse(json: expected),
            path: name
        ));
    }
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void ARefusedDeclarationNamesItsCodeAndLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
    [Fact]
    public void GridDeclarationCompilesByteIdenticallyToAnExplicitCellsOfRowAndTopology() {
        var json = WorldSources.LowerClean(body: """
            state {
                world {
                    grid board dimensions(width: 2, depth: 2) wrap(Both) cellSize(2) origin(1, 0, 1) band(0.5) empty(-1) {
                        "0" = 4
                        "3" = 5
                    }
                }
            }
            """);

        Assert.Null(@object: JsonMismatch.Find(
            actual: WorldRow(index: 0, json: json),
            expected: JsonNode.Parse(json: """
            {
                "name": "board",
                "kind": "Int",
                "domain": { "$type": "cellsOf", "topology": "board", "empty": -1 },
                "cells": [
                    { "key": "0", "value": 4 },
                    { "key": "3", "value": 5 }
                ]
            }
            """),
            path: "row"
        ));

        var lattices = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["lattices"]);
        var topology = Assert.IsType<JsonObject>(@object: lattices[0]);

        Assert.Null(@object: JsonMismatch.Find(
            actual: topology,
            expected: JsonNode.Parse(json: """
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
            """),
            path: "topology"
        ));
    }
}
