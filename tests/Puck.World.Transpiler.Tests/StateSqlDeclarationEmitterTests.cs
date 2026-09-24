using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Lowering coverage for the SQL-flavored state authoring dialect (src/Puck.World.Transpiler/README.md, "State SQL dialect"):
/// genuine byte-identical emission against independently authored native Puck definitions,
/// and strict refusal diagnostics with exact source lines.</summary>
public class StateSqlDeclarationEmitterTests {
    // Each SQL body and the native body an author would otherwise write; the SQL one must also pass semantic
    // validation on its own.
    private static readonly Dictionary<string, (string Sql, string Native)> Equivalences = new(comparer: StringComparer.Ordinal) {
        ["a multi-column table with an insert"] = (
            Sql: """
                sql {
                    CREATE TABLE fighters (
                        id     TEXT PRIMARY KEY,
                        hp     INT   NOT NULL DEFAULT 100 CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE,
                        mana   INT   DEFAULT 0 ADVANCE 5 PER SECOND,
                        speed  FIXED DEFAULT 1.5,
                        alive  BOOL  DEFAULT TRUE,
                        title  TEXT
                    ) CAPACITY 32;

                    INSERT INTO fighters (id, hp, title) VALUES
                        ('hero',   80, 'Hero'),
                        ('goblin', 30, 'Goblin');
                }
                """,
            Native: """
                state {
                    world {
                        table fightersHp capacity(32) bounds(0..100, overflow: Saturate) {
                            hero = 80
                            goblin = 30
                        }
                        table fightersMana capacity(32) advance(perSecond: 5) {
                            hero = 0
                            goblin = 0
                        }
                        table fightersSpeed capacity(32) {
                            hero = 1.5
                            goblin = 1.5
                        }
                        table fightersAlive capacity(32) {
                            hero = true
                            goblin = true
                        }
                        table fightersTitle capacity(32) {
                            hero = "Hero"
                            goblin = "Goblin"
                        }
                    }
                }
                """
        ),
        ["a slot declaration"] = (
            Sql: """
                sql {
                    DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);
                    DECLARE uninitialized INT;
                }
                """,
            Native: """
                state {
                    world {
                        slot gold = 10 bounds(0..)
                        slot uninitialized
                    }
                }
                """
        ),
        ["a key-only table is a bool row"] = (
            Sql: """
                sql {
                    CREATE TABLE tags (id TEXT PRIMARY KEY);
                    INSERT INTO tags (id) VALUES ('a'), ('b');
                }
                """,
            Native: """
                state {
                    world {
                        table tags {
                            a = true
                            b = true
                        }
                    }
                }
                """
        ),
        ["an ordered table with references is a pile"] = (
            Sql: """
                sql {
                    CREATE TABLE cardNames (id TEXT PRIMARY KEY);
                    INSERT INTO cardNames (id) VALUES ('ace'), ('king');
                    CREATE TABLE deck (id TEXT PRIMARY KEY REFERENCES cardNames) ORDERED CAPACITY 52;
                }
                """,
            Native: """
                state {
                    world {
                        table cardNames {
                            ace = true
                            king = true
                        }
                        pile deck of cardNames capacity(52) {}
                    }
                }
                """
        ),
        ["a column row alias names the row"] = (
            Sql: """
                sql {
                    CREATE TABLE vitals (
                        id TEXT PRIMARY KEY,
                        hp INT AS vitals_health DEFAULT 100
                    );
                    INSERT INTO vitals (id) VALUES ('hero');
                }
                """,
            Native: """
                state {
                    world {
                        table vitals_health {
                            hero = 100
                        }
                    }
                }
                """
        ),
        ["a sql block merges with native declarations in document order"] = (
            Sql: """
                state {
                    world {
                        slot nativeGold = 50
                    }
                }
                sql {
                    DECLARE sqlSilver INT DEFAULT 25;
                }
                """,
            Native: """
                state {
                    world {
                        slot nativeGold = 50
                        slot sqlSilver = 25
                    }
                }
                """
        ),
        ["a policy is visibility"] = (
            Sql: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);
                    CREATE POLICY p ON fighters FOR SELECT TO console;
                }
                """,
            Native: """
                state {
                    world {
                        row {
                            name: "fightersHp"
                            kind: Int
                            domain {
                                $type: "keys"
                            }
                            visibility {
                                readers ["console"]
                            }
                        }
                    }
                }
                """
        ),
        ["a bool column accepts one and zero"] = (
            Sql: """
                sql {
                    CREATE TABLE flags (id TEXT PRIMARY KEY, active BOOL);
                    INSERT INTO flags (id, active) VALUES ('a', 1), ('b', 0);
                }
                """,
            Native: """
                state {
                    world {
                        table flagsActive {
                            a = true
                            b = false
                        }
                    }
                }
                """
        ),
        ["single-key updates are set and add"] = (
            Sql: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE heal EVERY TICK AS
                    BEGIN ATOMIC
                        UPDATE fighters SET hp = 100 WHERE id = 'hero';
                        UPDATE fighters SET hp = hp + 10 WHERE id = 'hero';
                    END;
                }
                """,
            Native: """
                state {
                    world {
                        table fightersHp {}
                    }
                }
                rule "heal" {
                    mode: Level
                    transaction {
                        fightersHp[hero] = 100
                        fightersHp[hero] += 10
                    }
                }
                """
        ),
        ["a set-based update is a forEach rule"] = (
            Sql: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE poison EVERY TICK AS
                        UPDATE fighters SET hp = hp - 5 WHERE hp > 10;
                }
                """,
            Native: """
                state {
                    world {
                        table fightersHp {}
                    }
                }
                rule "poison" {
                    mode: Level
                    forEach: fightersHp
                    when fightersHp[$each] > 10
                    fightersHp[$each] += -5
                }
                """
        ),
        ["a set-based delete is a forEach rule"] = (
            Sql: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE clearDead EVERY TICK AS
                        DELETE FROM fighters WHERE hp <= 0;
                }
                """,
            Native: """
                state {
                    world {
                        table fightersHp {}
                    }
                }
                rule "clearDead" {
                    mode: Level
                    forEach: fightersHp
                    when fightersHp[$each] <= 0
                    remove fightersHp[$each]
                }
                """
        ),
    };
    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["PUCK070: a FLOAT column"] = new(
            Body: "sql {\n    CREATE TABLE t (id TEXT PRIMARY KEY, val FLOAT);\n}\n",
            Code: "PUCK070",
            Needle: "FLOAT"
        ) { Alone = true },
        ["PUCK070: a DOUBLE column"] = new(
            Body: "sql {\n    CREATE TABLE t (id TEXT PRIMARY KEY, val DOUBLE);\n}\n",
            Code: "PUCK070",
            Needle: "DOUBLE"
        ) { Alone = true },
        ["PUCK070: a REAL column"] = new(
            Body: "sql {\n    CREATE TABLE t (id TEXT PRIMARY KEY, val REAL);\n}\n",
            Code: "PUCK070",
            Needle: "REAL"
        ) { Alone = true },
        ["PUCK071: a composite primary key"] = new(
            Body: "sql {\n    CREATE TABLE t (a TEXT, b TEXT, PRIMARY KEY (a, b));\n}\n",
            Code: "PUCK071",
            Needle: "PRIMARY KEY (a, b)"
        ),
        ["PUCK072: a check that is neither a range nor a comparison"] = new(
            Body: "sql {\n    CREATE TABLE t (id TEXT PRIMARY KEY, hp INT CHECK (hp = 5));\n}\n",
            Code: "PUCK072",
            Needle: "hp = 5"
        ),
        ["PUCK073: an IS NOT NULL check"] = new(
            Body: "sql {\n    CREATE TABLE t (id TEXT PRIMARY KEY, hp INT CHECK (hp IS NOT NULL));\n}\n",
            Code: "PUCK073",
            Needle: "hp IS NOT NULL"
        ),
        ["PUCK073: GROUP BY"] = new(
            Body: "sql {\n    CREATE TABLE t (id TEXT PRIMARY KEY, hp INT NOT NULL);\n\n    CREATE RULE r EVERY TICK AS\n        UPDATE t SET hp = 10 GROUP BY id;\n}\n",
            Code: "PUCK073",
            Needle: "GROUP BY"
        ),
        ["PUCK073: LIMIT on an update"] = new(
            Body: "sql {\n    CREATE TABLE t (id TEXT PRIMARY KEY, hp INT NOT NULL);\n\n    CREATE RULE r EVERY TICK AS\n        UPDATE t SET hp = 10 LIMIT 5;\n}\n",
            Code: "PUCK073",
            Needle: "LIMIT"
        ),
        ["PUCK073: a set-based update inside a multi-statement rule"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);\n    DECLARE gold INT DEFAULT 10;\n\n    CREATE RULE invalid EVERY TICK AS\n    BEGIN ATOMIC\n        UPDATE fighters SET hp = hp - 1 WHERE hp > 0;\n        UPDATE gold SET value = 0;\n    END;\n}\n",
            Code: PuckDiagnosticCodes.SqlUnsupportedClause,
            Needle: "CREATE RULE invalid"
        ),
        ["PUCK073: a per-key rule over a table whose every column is nullable"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);\n\n    CREATE RULE poison EVERY TICK AS\n        UPDATE fighters SET hp = hp - 5 WHERE hp > 10;\n}\n",
            Code: PuckDiagnosticCodes.SqlUnsupportedClause,
            Needle: "UPDATE fighters SET hp = hp - 5"
        ),
        ["PUCK073: INSERT SELECT outside a rule"] = new(
            Body: "sql {\n    CREATE TABLE memories (\n        key TEXT PRIMARY KEY,\n        embedding VECTOR(lore)\n    ) CAPACITY 128;\n    CREATE TABLE recalled (\n        key TEXT PRIMARY KEY,\n        score FIXED\n    ) CAPACITY 3;\n    DECLARE queryEmbedding VECTOR(lore);\n    INSERT INTO recalled (key, score)\n    SELECT key, similarity(memories.embedding, queryEmbedding)\n    FROM memories\n    ORDER BY memories.embedding <=> queryEmbedding\n    LIMIT 3;\n}\n",
            Code: PuckDiagnosticCodes.SqlUnsupportedClause,
            Needle: "INSERT INTO recalled"
        ),
        ["PUCK074: a set update reading its own table"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);\n\n    CREATE RULE r EVERY TICK AS\n        UPDATE fighters SET hp = (SELECT hp FROM fighters WHERE id = 'hero') WHERE hp < 50;\n}\n",
            Code: "PUCK074",
            Needle: "(SELECT hp FROM fighters WHERE id = 'hero')"
        ),
        ["PUCK075: an insert missing a required column"] = new(
            Body: "sql {\n    CREATE TABLE t (\n        id TEXT PRIMARY KEY,\n        req INT NOT NULL\n    );\n    INSERT INTO t (id) VALUES ('hero');\n}\n",
            Code: "PUCK075",
            Needle: "req INT NOT NULL"
        ),
        ["PUCK076: a column with no type"] = new(
            Body: "sql {\n    CREATE TABLE t (id);\n}\n",
            Code: "PUCK076",
            Needle: "(id)"
        ) { Alone = true, Mentions = "has no type" },
        ["PUCK076: a column set to NULL"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);\n\n    CREATE RULE r EVERY TICK AS\n        UPDATE fighters SET hp = NULL WHERE id = 'hero';\n}\n",
            Code: PuckDiagnosticCodes.SqlSyntaxError,
            Needle: "NULL"
        ),
        ["PUCK076: EVICTS without CAPACITY"] = new(
            Body: "sql {\n    CREATE TABLE memories (\n        key TEXT PRIMARY KEY,\n        embedding VECTOR(lore)\n    ) EVICTS;\n}\n",
            Code: PuckDiagnosticCodes.SqlSyntaxError,
            Needle: "CREATE TABLE memories"
        ),
        ["PUCK053: a fraction set into an INT column"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);\n\n    CREATE RULE r EVERY TICK AS\n        UPDATE fighters SET hp = 1.5 WHERE id = 'hero';\n}\n",
            Code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            Needle: "hp = 1.5"
        ),
        ["PUCK053: a fraction set into an INT slot"] = new(
            Body: "sql {\n    DECLARE counter INT;\n    CREATE RULE r ON ENTER AS UPDATE counter SET value = 1.5;\n}\n",
            Code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            Needle: "value = 1.5"
        ),
        ["PUCK054: an insert beyond the table's capacity"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT) CAPACITY 1;\n    INSERT INTO fighters (id, hp) VALUES ('hero', 100), ('goblin', 50);\n}\n",
            Code: PuckDiagnosticCodes.StateDeclarationCapacityTooSmall,
            Needle: "INSERT INTO fighters"
        ),
        ["PUCK059: a where clause naming an unknown column"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);\n\n    CREATE RULE r EVERY TICK AS\n        UPDATE fighters SET hp = 10 WHERE unknownCol > 0;\n}\n",
            Code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
            Needle: "unknownCol"
        ) { Alone = true },
        ["PUCK059: the primary key compared by inequality"] = new(
            Body: "sql {\n    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);\n\n    CREATE RULE r EVERY TICK AS\n        UPDATE fighters SET hp = 10 WHERE id <> 'hero';\n}\n",
            Code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
            Needle: "id <> 'hero'"
        ) { Alone = true },
    };

    public static TheoryData<string> EquivalenceNames() => new(values: Equivalences.Keys);
    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    [MemberData(nameof(EquivalenceNames))]
    [Theory]
    public void ASqlDeclarationCompilesByteIdenticallyToItsNativeSpelling(string name) {
        var (sql, native) = Equivalences[name];
        var (sqlJson, sqlDiagnostics) = WorldSources.Lower(body: sql);
        var nativeJson = WorldSources.LowerClean(body: native);

        Assert.False(condition: sqlDiagnostics.HasErrors, userMessage: $"{name}: {sqlDiagnostics.FormatReport("SQL compilation errors")}");
        WorldSemanticValidator.ValidateWorld(sqlJson, sourceMap: null, diagnostics: sqlDiagnostics);
        Assert.False(condition: sqlDiagnostics.HasErrors, userMessage: $"{name}: {sqlDiagnostics.FormatReport("SQL semantic validation errors")}");
        Assert.Null(@object: JsonMismatch.Find(
            actual: sqlJson,
            expected: nativeJson,
            path: name
        ));
    }
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void ARefusedSqlStatementNamesItsCodeAndLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
    [Fact]
    public void ExplicitNullOmitsCellFromRow() {
        var (json, diag) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE t (
                    id TEXT PRIMARY KEY,
                    hp INT DEFAULT 100,
                    note TEXT
                );
                INSERT INTO t (id, hp, note) VALUES ('hero', 50, NULL);
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var world = ((JsonArray)json["state"]!["world"]!);
        var hpRow = ((JsonObject)world[0]!);
        var noteRow = ((JsonObject)world[1]!);

        Assert.Equal("tHp", hpRow["name"]?.ToString());
        Assert.Single(collection: ((JsonArray)hpRow["cells"]!));

        Assert.Equal("tNote", noteRow["name"]?.ToString());
        Assert.Null(@object: noteRow["cells"]); // Omitted entirely!
    }
    [Fact]
    public void SingleKeyUpdateReadingAnotherColumnCarriesKey() {
        var (json, diag) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT, mana INT);

                CREATE RULE convertMana EVERY TICK AS
                    UPDATE fighters SET hp = mana WHERE id = 'hero';
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rule = ((JsonObject)((JsonArray)json["rules"]!)[0]!);
        var effect = ((JsonObject)((JsonArray)rule["effects"]!)[0]!);

        Assert.Equal("setState", effect["$type"]?.ToString());
        Assert.Equal("fightersHp", effect["state"]?.ToString());
        Assert.Equal("hero", effect["key"]?.ToString());
        Assert.Equal("fightersMana[hero]", WorldExpressionJson.Text(node: effect["expression"]));
    }
    [Fact]
    public void BinarySubtractionWithoutSpacesParsesCorrectly() {
        var (json, diag) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE sub EVERY TICK AS
                BEGIN ATOMIC
                    UPDATE fighters SET hp = hp-5 WHERE id = 'hero';
                    UPDATE fighters SET hp = 10-5 WHERE id = 'hero';
                END;
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rule = ((JsonObject)((JsonArray)json["rules"]!)[0]!);
        var txn = ((JsonObject)((JsonArray)rule["effects"]!)[0]!);
        var effects = ((JsonArray)txn["effects"]!);

        Assert.Equal("addState", effects[0]?["$type"]?.ToString());
        Assert.Equal(-5L, ((long)effects[0]!["value"]!));
        Assert.Equal("10 - 5", WorldExpressionJson.Text(node: effects[1]?["expression"]));
    }
    [Fact]
    public void SetBoolColumnCompilesToNumericOneOrZero() {
        var (json, diag) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, alive BOOL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET alive = TRUE WHERE id = 'hero';
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rule = ((JsonObject)((JsonArray)json["rules"]!)[0]!);
        var effect = ((JsonObject)((JsonArray)rule["effects"]!)[0]!);

        Assert.Equal("setState", effect["$type"]?.ToString());
        Assert.Equal(1L, ((long)effect["value"]!));
    }
    [Fact]
    public void SlotWrite_TextSlot_ProducesTextProperty() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                DECLARE greeting TEXT;
                CREATE RULE r ON ENTER AS UPDATE greeting SET value = 'hello';
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rules = (json["rules"] as JsonArray);

        Assert.NotNull(@object: rules);
        var eff = (rules[0]?["effects"]?[0] as JsonObject);

        Assert.NotNull(@object: eff);
        Assert.Equal("hello", eff["text"]?.ToString());
        Assert.Null(@object: eff["expression"]);
    }
    [Fact]
    public void SlotWrite_IntSlot_ProducesValueProperty() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                DECLARE counter INT;
                CREATE RULE r ON ENTER AS UPDATE counter SET value = 42;
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rules = (json["rules"] as JsonArray);

        Assert.NotNull(@object: rules);
        var eff = (rules[0]?["effects"]?[0] as JsonObject);

        Assert.NotNull(@object: eff);
        Assert.Equal(42L, eff["value"]?.GetValue<long>());
    }
    [Fact]
    public void VectorTable_WithCapacityAndEvicts_LowersExpectedRow() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128 EVICTS;
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var world = (json["state"]?["world"] as JsonArray);

        Assert.NotNull(@object: world);
        var row = (world.FirstOrDefault(predicate: r => (r?["name"]?.ToString() == "memoriesEmbedding")) as JsonObject);

        Assert.NotNull(@object: row);
        Assert.Equal("Vector", row["kind"]?.ToString());
        Assert.Equal("lore", row["space"]?.ToString());
        Assert.Equal(128, row["capacity"]?.GetValue<int>());
        Assert.True(condition: row["evicts"]?.GetValue<bool>());
    }
    [Fact]
    public void VectorTable_WithInsertVectorLiteral_LowersCells() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128 EVICTS;

                INSERT INTO memories (key, embedding) VALUES
                    ('ambush', vector('fwAAAAAAAAA'));
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var world = (json["state"]?["world"] as JsonArray);

        Assert.NotNull(@object: world);
        var row = (world.FirstOrDefault(predicate: r => (r?["name"]?.ToString() == "memoriesEmbedding")) as JsonObject);

        Assert.NotNull(@object: row);
        var cells = (row["cells"] as JsonArray);

        Assert.NotNull(@object: cells);
        Assert.Single(collection: cells);
        Assert.Equal("ambush", cells[0]?["key"]?.ToString());
        Assert.Equal(WorldSources.SampleVector, cells[0]?["value"]?.ToString());
    }
    [Fact]
    public void VectorSlot_LowersExpectedRow() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                DECLARE playerEmbedding VECTOR(lore);
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var world = (json["state"]?["world"] as JsonArray);

        Assert.NotNull(@object: world);
        var row = (world.FirstOrDefault(predicate: r => (r?["name"]?.ToString() == "playerEmbedding")) as JsonObject);

        Assert.NotNull(@object: row);
        Assert.Equal("Vector", row["kind"]?.ToString());
        Assert.Equal("lore", row["space"]?.ToString());
    }
    [Fact]
    public void VectorTable_DefaultSpaceInferredWhenSingleSpaceDeclared() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                spaces [
                    { name: "lore" model: "text-embedding-3-small" revision: "1" dimensions: 256 }
                ]
            }
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR
                ) CAPACITY 128;
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var world = (json["state"]?["world"] as JsonArray);

        Assert.NotNull(@object: world);
        var row = (world.FirstOrDefault(predicate: r => (r?["name"]?.ToString() == "memoriesEmbedding")) as JsonObject);

        Assert.NotNull(@object: row);
        Assert.Equal("lore", row["space"]?.ToString());
    }
    [Fact]
    public void VectorComparison_InWhereClause_LowersRule() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                DECLARE playerEmbedding VECTOR(lore);
                DECLARE enemyEmbedding VECTOR(lore);
                DECLARE alert BOOL;
                CREATE RULE checkSimilarity ON ENTER AS
                    UPDATE alert SET value = TRUE WHERE similarity(playerEmbedding, enemyEmbedding) > 0.6;
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rules = (json["rules"] as JsonArray);

        Assert.NotNull(@object: rules);
        var rule = (rules[0] as JsonObject);

        Assert.NotNull(@object: rule);
        var gate = (rule["gate"] as JsonObject);

        Assert.NotNull(@object: gate);
        Assert.Equal("compareValue", gate["$type"]?.ToString());
        Assert.Equal("similarity(playerEmbedding, enemyEmbedding)", WorldExpressionJson.Text(node: gate["left"]));
        Assert.Equal("Greater", gate["comparison"]?.ToString());
        Assert.Equal("0.6", WorldExpressionJson.Text(node: gate["right"]));
        Assert.Equal("Fixed", gate["kind"]?.ToString());
        var eff = (rule["effects"]?[0] as JsonObject);

        Assert.NotNull(@object: eff);
        Assert.Equal("setState", eff["$type"]?.ToString());
        Assert.Equal("alert", eff["state"]?.ToString());
    }
    [Fact]
    public void VectorUpdate_Assignment_LowersEffect() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                DECLARE targetEmbedding VECTOR(lore);
                DECLARE sourceEmbedding VECTOR(lore);
                CREATE RULE copyEmbedding ON ENTER AS
                    UPDATE targetEmbedding SET value = sourceEmbedding;
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rules = (json["rules"] as JsonArray);

        Assert.NotNull(@object: rules);
        var rule = (rules[0] as JsonObject);

        Assert.NotNull(@object: rule);
        var eff = (rule["effects"]?[0] as JsonObject);

        Assert.NotNull(@object: eff);
        Assert.Equal("setState", eff["$type"]?.ToString());
        Assert.Equal("targetEmbedding", eff["state"]?.ToString());
        Assert.Equal("sourceEmbedding", WorldExpressionJson.Text(node: eff["expression"]));
    }
    [Fact]
    public void InsertSelect_NearestTransform_LowersExpectedRule() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128 EVICTS;
                CREATE TABLE recalled (
                    key TEXT PRIMARY KEY,
                    score FIXED
                ) CAPACITY 3;
                DECLARE queryEmbedding VECTOR(lore);
                CREATE RULE recallMemories ON ENTER AS
                    INSERT INTO recalled (key, score)
                    SELECT key, similarity(memories.embedding, queryEmbedding)
                    FROM memories
                    ORDER BY memories.embedding <=> queryEmbedding
                    LIMIT 3;
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rules = (json["rules"] as JsonArray);

        Assert.NotNull(@object: rules);
        var rule = (rules[0] as JsonObject);

        Assert.NotNull(@object: rule);
        var eff = (rule["effects"]?[0] as JsonObject);

        Assert.NotNull(@object: eff);
        Assert.Equal("transformState", eff["$type"]?.ToString());
        var transform = (eff["transform"] as JsonObject);

        Assert.NotNull(@object: transform);
        Assert.Equal("nearest", transform["$type"]?.ToString());
        Assert.Equal("memoriesEmbedding", transform["from"]?.ToString());
        Assert.Equal("recalledScore", transform["into"]?.ToString());
        Assert.Equal("queryEmbedding", transform["query"]?.ToString());
        Assert.Equal(3, transform["k"]?.GetValue<int>());
    }
    [Fact]
    public void InsertSelect_NearestTransform_WithWhereAndExcludeAndFarthest() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128 EVICTS;
                CREATE TABLE recalled (
                    key TEXT PRIMARY KEY,
                    score FIXED
                ) CAPACITY 5;
                DECLARE queryEmbedding VECTOR(lore);
                CREATE RULE recallFarthest ON ENTER AS
                    INSERT INTO recalled (key, score)
                    SELECT key, similarity(memories.embedding, queryEmbedding)
                    FROM memories
                    WHERE similarity(memories.embedding, queryEmbedding) > 0.5 AND key != 'ignored'
                    ORDER BY memories.embedding <=> queryEmbedding DESC
                    LIMIT 5;
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rules = (json["rules"] as JsonArray);

        Assert.NotNull(@object: rules);
        var rule = (rules[0] as JsonObject);

        Assert.NotNull(@object: rule);
        var eff = (rule["effects"]?[0] as JsonObject);

        Assert.NotNull(@object: eff);
        Assert.Equal("transformState", eff["$type"]?.ToString());
        var transform = (eff["transform"] as JsonObject);

        Assert.NotNull(@object: transform);
        Assert.Equal("nearest", transform["$type"]?.ToString());
        Assert.Equal("0.5", transform["threshold"]?.ToString());
        Assert.True(condition: transform["farthest"]?.GetValue<bool>());
        Assert.Equal("ignored", transform["exclude"]?.ToString());
    }
}
