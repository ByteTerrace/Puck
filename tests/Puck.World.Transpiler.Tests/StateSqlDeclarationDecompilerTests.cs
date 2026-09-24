using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler inverse for SQL-projected state declarations and rules: decompiling with sql=true and
/// recompiling reproduces the original JSON, while non-representable constructs fall back to native Puck syntax.</summary>
public class StateSqlDeclarationDecompilerTests {
    /// <summary>A canonical document, what its print must and must not say, and whether it prints with the SQL
    /// projection.</summary>
    private sealed record Projection(string Json, string[] Printed, string[] NotPrinted, bool Sql = true);

    private static readonly Dictionary<string, Projection> Projections = new(comparer: StringComparer.Ordinal) {
        ["a slot is DECLARE"] = new(
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
            Printed: ["sql {", "DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);"]
        ),
        ["sibling rows are one table and an insert"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["sql {", "CREATE TABLE characters (", "INSERT INTO characters (id, hp, mana) VALUES"]
        ),
        ["a pile is an ordered table"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["sql {", "CREATE TABLE deck (id TEXT PRIMARY KEY REFERENCES cards) ORDERED CAPACITY 52;"]
        ),
        ["a bool row is a key-only table"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["sql {", "CREATE TABLE tokens (id TEXT PRIMARY KEY) CAPACITY 10;", "INSERT INTO tokens (id) VALUES"]
        ),
        ["a rule is CREATE RULE"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["sql {", "DECLARE heroHp INT DEFAULT 100;", "CREATE RULE healHero ON ENTER AS", "UPDATE heroHp SET value = value + 10;"]
        ),
        ["without the projection a slot stays native"] = new(
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
            NotPrinted: ["sql {"],
            Printed: ["slot gold"],
            Sql: false
        ),
        // hp and mana may decompile to DECLARE in sql { }, but copyManaToHp must NOT be emitted as an UPDATE in sql { }
        ["a fromState effect stays native"] = new(
            Json: """
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
                """,
            NotPrinted: ["UPDATE hp SET"],
            Printed: ["rule \"copyManaToHp\""]
        ),
        ["a comparandState gate stays native"] = new(
            Json: """
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
                """,
            NotPrinted: ["CREATE RULE checkHpMana"],
            Printed: ["rule \"checkHpMana\""]
        ),
        ["a compareValue not of kind Fixed stays native"] = new(
            Json: """
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
                """,
            NotPrinted: ["CREATE RULE checkHpInt"],
            Printed: ["rule \"checkHpInt\""]
        ),
        ["a per-key rule stays native"] = new(
            Json: """
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
                """,
            NotPrinted: ["CREATE RULE regenAll"],
            Printed: ["rule \"regenAll\""]
        ),
        ["a single-key gate stays native"] = new(
            Json: """
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
                """,
            NotPrinted: ["CREATE RULE checkHeroHp"],
            Printed: ["rule \"checkHeroHp\""]
        ),
        ["a compareValue over an indexed operand stays native"] = new(
            Json: """
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
                """,
            NotPrinted: ["CREATE RULE checkComplex"],
            Printed: ["rule \"checkComplex\""]
        ),
        ["a rule reading a grid row is CREATE RULE"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["CREATE RULE checkBoard ON ENTER AS"]
        ),
        ["a Vector table"] = new(
            Json: """
                {
                    "schema": "puck.world.definition.v1",
                    "state": {
                        "spaces": [
                            { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 8 }
                        ],
                        "world": [
                            {
                                "name": "memoriesEmbedding",
                                "kind": "Vector",
                                "space": "lore",
                                "capacity": 128,
                                "evicts": true,
                                "cells": [
                                    { "key": "ambush", "value": "fwAAAAAAAAA" }
                                ]
                            }
                        ]
                    }
                }
                """,
            NotPrinted: [],
            Printed: ["sql {", "CREATE TABLE memories", "embedding VECTOR(lore)", "CAPACITY 128 EVICTS", "INSERT INTO memories (id, embedding) VALUES", "('ambush', vector('fwAAAAAAAAA'))"]
        ),
        ["a Vector slot"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["sql {", "DECLARE playerEmbedding VECTOR(lore);"]
        ),
        ["a similarity gate"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["CREATE RULE checkSimilarity ON ENTER AS", "WHERE similarity(playerEmbedding, enemyEmbedding) > 0.6;"]
        ),
        ["a Vector update"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["CREATE RULE copyEmbedding ON ENTER AS", "UPDATE targetEmbedding SET value = sourceEmbedding;"]
        ),
        ["nearest is INSERT SELECT"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["INSERT INTO recalled (id, score)", "SELECT id, similarity(embedding, queryEmbedding)", "FROM memories", "ORDER BY embedding <=> queryEmbedding", "LIMIT 3;", "CREATE RULE recallMemories ON ENTER AS"]
        ),
        ["nearest with a threshold, an exclusion and farthest"] = new(
            Json: """
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
                """,
            NotPrinted: [],
            Printed: ["CREATE RULE recallFarthest ON ENTER AS", "id <> 'ignored'", "similarity(embedding, queryEmbedding) <= 0.5", "ORDER BY embedding <=> queryEmbedding DESC", "LIMIT 5;"]
        ),
    };

    public static TheoryData<string> ProjectionNames() => new(values: Projections.Keys);
    [MemberData(nameof(ProjectionNames))]
    [Theory]
    public void ADocumentPrintsItsProjectionAndRoundTrips(string name) {
        var projection = Projections[name];
        var printed = WorldSources.AssertRoundTrips(
            original: WorldSources.Canonical(json: projection.Json),
            sql: projection.Sql
        );

        var missing = projection.Printed.Where(predicate: text => !printed.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();
        var present = projection.NotPrinted.Where(predicate: text => printed.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();

        Assert.True(
            condition: ((missing.Length == 0) && (present.Length == 0)),
            userMessage: $"{name}: missing [{string.Join(separator: " | ", values: missing)}], present [{string.Join(separator: " | ", values: present)}]{Environment.NewLine}{printed}"
        );
    }
}
