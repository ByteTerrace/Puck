using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler inverse for SQL-projected state declarations and rules: decompiling with sql=true and
/// recompiling reproduces the original JSON, while non-representable constructs fall back to native Puck syntax.</summary>
public class StateSqlDeclarationDecompilerTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Recompile(string puckSource) {
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: puckSource,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.False(
            condition: parseResult.Diagnostics.HasErrors,
            userMessage: parseResult.Diagnostics.FormatReport(puckSource)
        );

        var diagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        return (loweringResult.Value!, diagnostics);
    }

    private static void AssertRoundTripsSql(JsonObject original) {
        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
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
    public void SlotDecompilesToDeclareAndRoundTrips() {
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

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);

        Assert.Contains("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void MultiColumnTableDecompilesToCreateTableAndInsertAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        {
                            "name": "charactersHp",
                            "kind": "Int",
                            "cells": [
                                { "key": "hero", "value": 100 },
                                { "key": "goblin", "value": 30 }
                            ],
                            "min": 0,
                            "max": 100
                        },
                        {
                            "name": "charactersMana",
                            "kind": "Int",
                            "cells": [
                                { "key": "hero", "value": 50 },
                                { "key": "goblin", "value": 10 }
                            ],
                            "min": 0,
                            "max": 50
                        }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);

        Assert.Contains("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE characters (", decompiled, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO characters (id, hp, mana) VALUES", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void PileDecompilesToOrderedTableAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        {
                            "name": "deck",
                            "kind": "Bool",
                            "domain": { "$type": "keysOf", "row": "cards", "ordered": true },
                            "capacity": 52,
                            "cells": []
                        }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);

        Assert.Contains("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE deck (id TEXT PRIMARY KEY REFERENCES cards) ORDERED CAPACITY 52;", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void KeyOnlyTableDecompilesAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        {
                            "name": "tokens",
                            "kind": "Bool",
                            "cells": [
                                { "key": "a", "value": true },
                                { "key": "b", "value": true }
                            ],
                            "capacity": 10
                        }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);

        Assert.Contains("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE tokens (id TEXT PRIMARY KEY) CAPACITY 10;", decompiled, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tokens (id) VALUES", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void RuleDecompilesToCreateRuleAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "heroHp", "kind": "Int", "value": 100 }
                    ]
                },
                "rules": [
                    {
                        "name": "healHero",
                        "mode": "Edge",
                        "effects": [
                            {
                                "$type": "addState",
                                "state": "heroHp",
                                "value": 10
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);

        Assert.Contains("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("DECLARE heroHp INT DEFAULT 100;", decompiled, StringComparison.Ordinal);
        Assert.Contains("CREATE RULE healHero ON ENTER AS", decompiled, StringComparison.Ordinal);
        Assert.Contains("UPDATE heroHp SET value = value + 10;", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void SqlFalseKeepsNativeSyntax() {
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

        var decompiled = WorldDecompiler.Decompile(root: original, sql: false);

        Assert.DoesNotContain("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("slot gold : Int", decompiled, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleWithFromStateEffect_FallsBackToNativePuck() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "hp", "kind": "Int", "value": 10 },
                        { "name": "mana", "kind": "Int", "value": 5 }
                    ]
                },
                "rules": [
                    {
                        "name": "copyManaToHp",
                        "mode": "Edge",
                        "effects": [
                            {
                                "$type": "setState",
                                "state": "hp",
                                "fromState": "mana"
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        // hp and mana may decompile to DECLARE in sql { }, but copyManaToHp must NOT be emitted as an UPDATE in sql { }
        Assert.DoesNotContain("UPDATE hp SET", decompiled, StringComparison.Ordinal);
        Assert.Contains("rule \"copyManaToHp\"", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void RuleWithComparandState_FallsBackToNativePuck() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "hp", "kind": "Int", "value": 10 },
                        { "name": "mana", "kind": "Int", "value": 5 }
                    ]
                },
                "rules": [
                    {
                        "name": "checkHpMana",
                        "mode": "Edge",
                        "gate": {
                            "$type": "compareState",
                            "state": "hp",
                            "comparandState": "mana",
                            "comparison": "GreaterThan"
                        },
                        "effects": [
                            {
                                "$type": "setState",
                                "state": "hp",
                                "value": 0
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.DoesNotContain("CREATE RULE checkHpMana", decompiled, StringComparison.Ordinal);
        Assert.Contains("rule \"checkHpMana\"", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void RuleWithNonFixedCompareValue_FallsBackToNativePuck() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "hp", "kind": "Int", "value": 10 }
                    ]
                },
                "rules": [
                    {
                        "name": "checkHpInt",
                        "mode": "Edge",
                        "gate": {
                            "$type": "compareValue",
                            "left": "hp",
                            "right": "5",
                            "kind": "Int",
                            "comparison": "GreaterThan"
                        },
                        "effects": [
                            {
                                "$type": "setState",
                                "state": "hp",
                                "value": 0
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.DoesNotContain("CREATE RULE checkHpInt", decompiled, StringComparison.Ordinal);
        Assert.Contains("rule \"checkHpInt\"", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void PerKeyRule_NotProjectedToSql() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        {
                            "name": "playerHp",
                            "kind": "Int",
                            "cells": [
                                { "key": "hero", "value": 100 }
                            ]
                        }
                    ]
                },
                "rules": [
                    {
                        "name": "regenAll",
                        "mode": "Edge",
                        "forEach": "playerHp",
                        "effects": [
                            {
                                "$type": "addState",
                                "state": "playerHp",
                                "key": "$each",
                                "value": 1
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.DoesNotContain("CREATE RULE regenAll", decompiled, StringComparison.Ordinal);
        Assert.Contains("rule \"regenAll\"", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void RuleWithSingleKeyGate_FallsBackToNativePuck() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        {
                            "name": "playerHp",
                            "kind": "Int",
                            "cells": [
                                { "key": "hero", "value": 100 }
                            ]
                        }
                    ]
                },
                "rules": [
                    {
                        "name": "checkHeroHp",
                        "mode": "Edge",
                        "gate": {
                            "$type": "compareState",
                            "state": "playerHp",
                            "key": "hero",
                            "value": 50,
                            "comparison": "GreaterThan"
                        },
                        "effects": [
                            {
                                "$type": "setState",
                                "state": "playerHp",
                                "key": "hero",
                                "value": 0
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.DoesNotContain("CREATE RULE checkHeroHp", decompiled, StringComparison.Ordinal);
        Assert.Contains("rule \"checkHeroHp\"", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void RuleWithCompareValueComplexOperand_FallsBackToNativePuck() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "hp", "kind": "Int", "value": 10 }
                    ]
                },
                "rules": [
                    {
                        "name": "checkComplex",
                        "mode": "Edge",
                        "gate": {
                            "$type": "compareValue",
                            "left": "cards[0]",
                            "right": "5",
                            "kind": "Fixed",
                            "comparison": "GreaterThan"
                        },
                        "effects": [
                            {
                                "$type": "setState",
                                "state": "hp",
                                "value": 0
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.DoesNotContain("CREATE RULE checkComplex", decompiled, StringComparison.Ordinal);
        Assert.Contains("rule \"checkComplex\"", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void RuleReadingNativeGridRow_ProjectsToSql() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        {
                            "name": "board",
                            "kind": "Int",
                            "domain": { "$type": "cellsOf", "topology": "board", "empty": -1 }
                        },
                        {
                            "name": "score",
                            "kind": "Int",
                            "value": 0
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
                },
                "rules": [
                    {
                        "name": "checkBoard",
                        "mode": "Edge",
                        "effects": [
                            {
                                "$type": "if",
                                "condition": {
                                    "$type": "compareValue",
                                    "left": "board[0]",
                                    "right": "0",
                                    "kind": "Fixed",
                                    "comparison": "Greater"
                                },
                                "then": [
                                    {
                                        "$type": "setState",
                                        "state": "score",
                                        "value": 10
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.Contains("CREATE RULE checkBoard ON ENTER AS", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void VectorTableDecompilesToCreateTableAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 256 }
                    ],
                    "world": [
                        {
                            "name": "memoriesEmbedding",
                            "kind": "Vector",
                            "space": "lore",
                            "capacity": 128,
                            "evicts": true,
                            "cells": [
                                { "key": "ambush", "value": "AAAA" }
                            ]
                        }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.Contains("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE memories", decompiled, StringComparison.Ordinal);
        Assert.Contains("embedding VECTOR(lore)", decompiled, StringComparison.Ordinal);
        Assert.Contains("CAPACITY 128 EVICTS", decompiled, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO memories (id, embedding) VALUES", decompiled, StringComparison.Ordinal);
        Assert.Contains("('ambush', vector('AAAA'))", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void VectorSlotDecompilesToDeclareAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 256 }
                    ],
                    "world": [
                        { "name": "playerEmbedding", "kind": "Vector", "space": "lore" }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.Contains("sql {", decompiled, StringComparison.Ordinal);
        Assert.Contains("DECLARE playerEmbedding VECTOR(lore);", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void VectorRuleWithSimilarityComparisonDecompilesAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 256 }
                    ],
                    "world": [
                        { "name": "playerEmbedding", "kind": "Vector", "space": "lore" },
                        { "name": "enemyEmbedding", "kind": "Vector", "space": "lore" },
                        { "name": "alert", "kind": "Bool", "value": false }
                    ]
                },
                "rules": [
                    {
                        "name": "checkSimilarity",
                        "mode": "Edge",
                        "gate": {
                            "$type": "compareValue",
                            "left": "similarity(playerEmbedding, enemyEmbedding)",
                            "comparison": "Greater",
                            "right": "0.6",
                            "kind": "Fixed"
                        },
                        "effects": [
                            {
                                "$type": "setState",
                                "state": "alert",
                                "value": 1
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.Contains("CREATE RULE checkSimilarity ON ENTER AS", decompiled, StringComparison.Ordinal);
        Assert.Contains("WHERE similarity(playerEmbedding, enemyEmbedding) > 0.6;", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void VectorUpdateRuleDecompilesAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 256 }
                    ],
                    "world": [
                        { "name": "targetEmbedding", "kind": "Vector", "space": "lore" },
                        { "name": "sourceEmbedding", "kind": "Vector", "space": "lore" }
                    ]
                },
                "rules": [
                    {
                        "name": "copyEmbedding",
                        "mode": "Edge",
                        "effects": [
                            {
                                "$type": "setState",
                                "state": "targetEmbedding",
                                "expression": "sourceEmbedding"
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.Contains("CREATE RULE copyEmbedding ON ENTER AS", decompiled, StringComparison.Ordinal);
        Assert.Contains("UPDATE targetEmbedding SET value = sourceEmbedding;", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void NearestRuleDecompilesToInsertSelectAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 256 }
                    ],
                    "world": [
                        { "name": "memoriesEmbedding", "kind": "Vector", "space": "lore", "capacity": 128, "evicts": true },
                        { "name": "recalledScore", "kind": "Fixed", "capacity": 3 },
                        { "name": "queryEmbedding", "kind": "Vector", "space": "lore" }
                    ]
                },
                "rules": [
                    {
                        "name": "recallMemories",
                        "mode": "Edge",
                        "effects": [
                            {
                                "$type": "transformState",
                                "transform": {
                                    "$type": "nearest",
                                    "from": "memoriesEmbedding",
                                    "query": "queryEmbedding",
                                    "into": "recalledScore",
                                    "k": 3
                                }
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.True(decompiled.Contains("CREATE RULE recallMemories ON ENTER AS"), decompiled);
        Assert.Contains("INSERT INTO recalled (id, score)", decompiled, StringComparison.Ordinal);
        Assert.Contains("SELECT id, similarity(embedding, queryEmbedding)", decompiled, StringComparison.Ordinal);
        Assert.Contains("FROM memories", decompiled, StringComparison.Ordinal);
        Assert.Contains("ORDER BY embedding <=> queryEmbedding", decompiled, StringComparison.Ordinal);
        Assert.Contains("LIMIT 3;", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }

    [Fact]
    public void NearestRuleWithWhereExcludeFarthestDecompilesAndRoundTrips() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 256 }
                    ],
                    "world": [
                        { "name": "memoriesEmbedding", "kind": "Vector", "space": "lore", "capacity": 128, "evicts": true },
                        { "name": "recalledScore", "kind": "Fixed", "capacity": 5 },
                        { "name": "queryEmbedding", "kind": "Vector", "space": "lore" }
                    ]
                },
                "rules": [
                    {
                        "name": "recallFarthest",
                        "mode": "Edge",
                        "effects": [
                            {
                                "$type": "transformState",
                                "transform": {
                                    "$type": "nearest",
                                    "from": "memoriesEmbedding",
                                    "query": "queryEmbedding",
                                    "into": "recalledScore",
                                    "k": 5,
                                    "threshold": "0.5",
                                    "exclude": "ignored",
                                    "farthest": true
                                }
                            }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, sql: true);
        Assert.Contains("CREATE RULE recallFarthest ON ENTER AS", decompiled, StringComparison.Ordinal);
        Assert.Contains("id <> 'ignored'", decompiled, StringComparison.Ordinal);
        Assert.Contains("similarity(embedding, queryEmbedding) <= 0.5", decompiled, StringComparison.Ordinal);
        Assert.Contains("ORDER BY embedding <=> queryEmbedding DESC", decompiled, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5;", decompiled, StringComparison.Ordinal);
        AssertRoundTripsSql(original: original);
    }
}
