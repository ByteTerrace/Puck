using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler inverse for the concise state-row declarations: decompiling and recompiling reproduces the
/// original JSON, a keyed row with non-identifier keys prints quoted keys, and a row with a non-identifier name
/// falls back to <c>row { }</c>.</summary>
public class StateDeclarationDecompilerTests {
    /// <summary>A document, and what its print must and must not say.</summary>
    private sealed record Print(string Json, string[] Printed, string[] NotPrinted);

    private static readonly Dictionary<string, Print> Prints = new(comparer: StringComparer.Ordinal) {
        ["a table"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
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
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: ["table vitals"]
        ),
        ["a slot with a one-sided bound"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            { "name": "gold", "kind": "Int", "value": 10, "min": 0 }
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: ["slot gold = 10 bounds(0..)"]
        ),
        ["a Fixed slot with only a maximum invents no minimum"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            { "name": "temperature", "kind": "Fixed", "value": "0", "max": "10" }
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: ["bounds(..10.0)"]
        ),
        ["a Fixed slot with an advance"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            {
                                "name": "shield",
                                "kind": "Fixed",
                                "value": "10",
                                "min": "0",
                                "max": "10",
                                "advance": { "perSecondNumerator": -1, "perSecondDenominator": 2 }
                            }
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: []
        ),
        ["an Int local with a fractional constant keeps its kind"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            { "name": "flag", "kind": "Int", "value": 0 }
                        ]
                    },
                    "rules": [
                        {
                            "name": "r",
                            "locals": [
                                {
                                    "name": "k",
                                    "kind": "Int",
                                    "expression": { "instructions": [{ "op": "Constant", "value": 0.5 }] }
                                }
                            ],
                            "effects": [
                                { "$type": "setState", "state": "flag", "value": 1 }
                            ]
                        }
                    ]
                }
                """,
            NotPrinted: [],
            Printed: []
        ),
        ["an Int local with a division keeps its kind"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            { "name": "flag", "kind": "Int", "value": 0 }
                        ]
                    },
                    "rules": [
                        {
                            "name": "r",
                            "locals": [
                                {
                                    "name": "k",
                                    "kind": "Int",
                                    "expression": {
                                        "instructions": [
                                            { "op": "Constant", "value": 1 },
                                            { "op": "Constant", "value": 2 },
                                            { "op": "Divide" }
                                        ]
                                    }
                                }
                            ],
                            "effects": [
                                { "$type": "setState", "state": "flag", "value": 1 }
                            ]
                        }
                    ]
                }
                """,
            NotPrinted: [],
            Printed: []
        ),
        ["a pile"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            {
                                "name": "cardNames",
                                "kind": "Int",
                                "cells": [
                                    { "key": "king", "value": 0 },
                                    { "key": "queen", "value": 1 }
                                ],
                                "capacity": 2
                            },
                            {
                                "name": "deck",
                                "kind": "Bool",
                                "domain": { "$type": "keysOf", "row": "cardNames", "ordered": true },
                                "cells": [
                                    { "key": "king", "value": true },
                                    { "key": "queen", "value": true }
                                ],
                                "capacity": 2
                            }
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: ["pile deck of cardNames"]
        ),
        ["a grid"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            {
                                "name": "board",
                                "kind": "Int",
                                "domain": { "$type": "cellsOf", "topology": "board", "empty": -1 },
                                "cells": [
                                    { "key": "0", "value": 4 },
                                    { "key": "3", "value": 5 }
                                ]
                            }
                        ],
                        "lattices": [
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
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: ["grid board dimensions(width: 2, depth: 2)"]
        ),
        ["a keyed row with non-identifier keys prints them quoted"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            {
                                "name": "hand",
                                "kind": "Int",
                                "cells": [
                                    { "key": "0", "value": 1 },
                                    { "key": "2C", "value": 2 }
                                ],
                                "capacity": 4
                            }
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: ["\"0\" = 1", "\"2C\" = 2"]
        ),
        ["a row whose name is not an identifier is a row block"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "world": [
                            { "name": "2 players", "kind": "Int", "value": 1 }
                        ]
                    }
                }
                """,
            NotPrinted: ["slot ", "table "],
            Printed: ["row {", "\"2 players\""]
        ),
    };

    public static TheoryData<string> PrintNames() => new(values: Prints.Keys);
    [MemberData(nameof(PrintNames))]
    [Theory]
    public void ADeclarationPrintsAsItsSugarAndRoundTrips(string name) {
        var print = Prints[name];
        var printed = WorldSources.AssertRoundTrips(original: Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: print.Json)));
        var missing = print.Printed.Where(predicate: text => !printed.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();
        var present = print.NotPrinted.Where(predicate: text => printed.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();

        Assert.True(
            condition: ((missing.Length == 0) && (present.Length == 0)),
            userMessage: $"{name}: missing [{string.Join(separator: " | ", values: missing)}], present [{string.Join(separator: " | ", values: present)}]{Environment.NewLine}{printed}"
        );
    }
    [InlineData("Bool", false)]
    [InlineData("Bool", true)]
    [InlineData("Text", false)]
    [InlineData("Text", true)]
    [InlineData("Fixed", false)]
    [InlineData("Fixed", true)]
    [Theory]
    public void EmptyNonIntTableKeepsItsExplicitKind(string kind, bool explicitCells) {
        var cells = (explicitCells ? ", \"cells\": []" : "");
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse($$"""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "empty", "kind": "{{kind}}", "capacity": 2{{cells}} }
                    ]
                }
            }
            """));

        _ = WorldSources.AssertRoundTrips(original: original);
    }
    [Fact]
    public void ALoweredCapacitySurvivesTheTableAndPileSugar() {
        // A capacity written as `row { capacity: N }` reaches the decompiler through the generic scalar path, which
        // lowers a whole number to a long — the kind a reader asking only for an int refuses, dropping the modifier.
        var lowered = WorldSources.LowerSourceClean(source: """
            schema: "puck.world.definition.v1"

            state {
                world {
                    row {
                        name: "cardNames"
                        kind: Int
                        capacity: 3
                        cells [ { key: "a" value: 1 } ]
                    }
                    row {
                        name: "deck"
                        kind: Bool
                        domain { $type: "keysOf" row: "cardNames" ordered: true }
                        capacity: 3
                        cells [ ]
                    }
                }
            }
            """);


        var decompiled = WorldDecompiler.Decompile(root: lowered);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "table cardNames capacity(3)"
        );
        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "pile deck of cardNames capacity(3)"
        );
        _ = WorldSources.AssertRoundTrips(original: lowered);
    }
    [Fact]
    public void RecordFieldEnumsDecompileAsDeclarationsBeforeTheirRecords() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "enums": [{ "name": "Facing", "members": ["North", "South"] }],
                    "records": [{
                        "name": "Piece",
                        "fields": [{
                            "name": "facing",
                            "kind": "Int",
                            "enum": "Facing",
                            "default": { "kind": "Int", "value": 0 }
                        }]
                    }]
                }
            }
            """));
        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(actualString: decompiled, comparisonType: StringComparison.Ordinal, expectedSubstring: "enum Facing {");
        Assert.True(condition: (decompiled.IndexOf(comparisonType: StringComparison.Ordinal, value: "enum Facing") < decompiled.IndexOf(comparisonType: StringComparison.Ordinal, value: "record Piece")));
        _ = WorldSources.AssertRoundTrips(original: original);
    }
}
