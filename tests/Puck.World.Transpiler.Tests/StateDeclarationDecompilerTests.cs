using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler inverse for the concise state-row declarations: decompiling and recompiling reproduces the
/// original JSON, a keyed row with non-identifier keys prints quoted keys, and a row with a non-identifier name
/// falls back to <c>row { }</c>.</summary>
public class StateDeclarationDecompilerTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Recompile(string puckSource) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: puckSource
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }
    private static void AssertRoundTrips(JsonObject original) {
        var decompiled = WorldDecompiler.Decompile(root: original);

        var (recompiled, diagnostics) = Recompile(puckSource: decompiled);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"{diagnostics.FormatReport(decompiled)}\n---\n{decompiled}"
        );

        var mismatch = JsonMismatch.Find(
            actual: recompiled,
            expected: original,
            path: "$"
        );

        Assert.True(
            condition: (mismatch is null),
            userMessage: $"{mismatch}\n---\n{decompiled}"
        );
    }

    [Fact]
    public void TableDeclarationDecompilesAndRecompilesToTheSameJson() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
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
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "table vitals"
        );
        AssertRoundTrips(original: original);
    }
    [Fact]
    public void SlotDeclarationDecompilesAndRecompilesToTheSameJson() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "gold", "kind": "Int", "value": 10, "min": 0 }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "slot gold = 10 bounds(0..)"
        );
        AssertRoundTrips(original: original);
    }
    [Fact]
    public void FixedSlotWithOnlyMaximumDecompilesWithoutInventingAMinimum() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "temperature", "kind": "Fixed", "value": "0", "max": "10" }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(actualString: decompiled, comparisonType: StringComparison.Ordinal, expectedSubstring: "bounds(..10.0)");
        AssertRoundTrips(original: original);
    }
    [Fact]
    public void FixedSlotWithAdvanceDecompilesAndRecompilesToTheSameJson() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
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
            """));

        AssertRoundTrips(original: original);
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

        AssertRoundTrips(original: original);
    }
    [Fact]
    public void ExplicitIntLocalWithFractionalConstantDoesNotSugarIntoAChangedKind() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
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
            """));

        AssertRoundTrips(original: original);
    }
    [Fact]
    public void ExplicitIntLocalWithDivisionDoesNotSugarIntoAChangedKind() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
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
            """));

        AssertRoundTrips(original: original);
    }
    [Fact]
    public void PileDeclarationDecompilesAndRecompilesToTheSameJson() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
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
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "pile deck of cardNames"
        );
        AssertRoundTrips(original: original);
    }
    [Fact]
    public void GridDeclarationDecompilesAndRecompilesToTheSameJson() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
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
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "grid board dimensions(width: 2, depth: 2)"
        );
        AssertRoundTrips(original: original);
    }
    [Fact]
    public void DecompilingAKeyedRowWithNonIdentifierKeysPrintsQuotedKeysAndRecompilesByteIdentically() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
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
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"0\" = 1"
        );
        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"2C\" = 2"
        );
        AssertRoundTrips(original: original);
    }
    [Fact]
    public void ANonIdentifierRowNameFallsBackToRowBlock() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "2 players", "kind": "Int", "value": 1 }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "row {"
        );
        Assert.DoesNotContain(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "slot "
        );
        Assert.DoesNotContain(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "table "
        );
        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"2 players\""
        );
        AssertRoundTrips(original: original);
    }
    [Fact]
    public void ALoweredCapacitySurvivesTheTableAndPileSugar() {
        // A capacity written as `row { capacity: N }` reaches the decompiler through the generic scalar path, which
        // lowers a whole number to a long — the kind a reader asking only for an int refuses, dropping the modifier.
        var (lowered, loweringDiagnostics) = Recompile(puckSource: """
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

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: loweringDiagnostics.FormatReport("")
        );

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
        AssertRoundTrips(original: lowered);
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
        AssertRoundTrips(original: original);
    }
}
