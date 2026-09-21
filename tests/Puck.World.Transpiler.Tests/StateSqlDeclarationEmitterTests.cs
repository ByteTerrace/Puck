using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Lowering coverage for the SQL-flavored state authoring dialect (src/Puck.World.Transpiler/README.md, "State SQL dialect"):
/// genuine byte-identical emission against independently authored native Puck definitions,
/// and strict refusal diagnostics PUCK070-PUCK076 with exact source spans.</summary>
public class StateSqlDeclarationEmitterTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }
    private static void AssertCompilesByteIdentical(string sqlBody, string nativeBody) {
        var (sqlJson, sqlDiag) = Lower(body: sqlBody);
        var (nativeJson, nativeDiag) = Lower(body: nativeBody);

        Assert.False(condition: sqlDiag.HasErrors, userMessage: sqlDiag.FormatReport("SQL compilation errors"));
        Assert.False(condition: nativeDiag.HasErrors, userMessage: nativeDiag.FormatReport("Native compilation errors"));

        WorldSemanticValidator.ValidateWorld(sqlJson, sourceMap: null, diagnostics: sqlDiag);
        Assert.False(condition: sqlDiag.HasErrors, userMessage: sqlDiag.FormatReport("SQL semantic validation errors"));

        var mismatch = JsonMismatch.Find(
            actual: sqlJson,
            expected: nativeJson,
            path: "$"
        );

        Assert.Null(@object: mismatch);
    }

    // ---- Byte-identical state declarations -----------------------------------------------------------------

    [Fact]
    public void MultiColumnTableWithInsertCompilesByteIdentically() {
        AssertCompilesByteIdentical(
            nativeBody: """
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
                """,
            sqlBody: """
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
                """
        );
    }
    [Fact]
    public void SlotDeclarationCompilesByteIdentically() {
        AssertCompilesByteIdentical(
            nativeBody: """
                state {
                    world {
                        slot gold = 10 bounds(0..)
                        slot uninitialized
                    }
                }
                """,
            sqlBody: """
                sql {
                    DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);
                    DECLARE uninitialized INT;
                }
                """
        );
    }
    [Fact]
    public void KeyOnlyTableCompilesToBoolRow() {
        AssertCompilesByteIdentical(
            nativeBody: """
                state {
                    world {
                        table tags {
                            a = true
                            b = true
                        }
                    }
                }
                """,
            sqlBody: """
                sql {
                    CREATE TABLE tags (id TEXT PRIMARY KEY);
                    INSERT INTO tags (id) VALUES ('a'), ('b');
                }
                """
        );
    }
    [Fact]
    public void OrderedTableWithReferencesCompilesByteIdentically() {
        AssertCompilesByteIdentical(
            nativeBody: """
                state {
                    world {
                        table cardNames {
                            ace = true
                            king = true
                        }
                        pile deck of cardNames capacity(52) {}
                    }
                }
                """,
            sqlBody: """
                sql {
                    CREATE TABLE cardNames (id TEXT PRIMARY KEY);
                    INSERT INTO cardNames (id) VALUES ('ace'), ('king');
                    CREATE TABLE deck (id TEXT PRIMARY KEY REFERENCES cardNames) ORDERED CAPACITY 52;
                }
                """
        );
    }
    [Fact]
    public void ColumnRowAliasCompilesToExplicitRowName() {
        AssertCompilesByteIdentical(
            nativeBody: """
                state {
                    world {
                        table vitals_health {
                            hero = 100
                        }
                    }
                }
                """,
            sqlBody: """
                sql {
                    CREATE TABLE vitals (
                        id TEXT PRIMARY KEY,
                        hp INT AS vitals_health DEFAULT 100
                    );
                    INSERT INTO vitals (id) VALUES ('hero');
                }
                """
        );
    }
    [Fact]
    public void SqlBlockMergesWithNativeStateWorldDeclarationsInDocumentOrder() {
        AssertCompilesByteIdentical(
            nativeBody: """
                state {
                    world {
                        slot nativeGold = 50
                        slot sqlSilver = 25
                    }
                }
                """,
            sqlBody: """
                state {
                    world {
                        slot nativeGold = 50
                    }
                }
                sql {
                    DECLARE sqlSilver INT DEFAULT 25;
                }
                """
        );
    }
    [Fact]
    public void PolicyDeclarationCompilesToVisibility() {
        AssertCompilesByteIdentical(
            nativeBody: """
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
                """,
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);
                    CREATE POLICY p ON fighters FOR SELECT TO console;
                }
                """
        );
    }
    [Fact]
    public void ExplicitNullOmitsCellFromRow() {
        var (json, diag) = Lower(body: """
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
    public void BooleanColumnAcceptsOneAndZero() {
        AssertCompilesByteIdentical(
            nativeBody: """
                state {
                    world {
                        table flagsActive {
                            a = true
                            b = false
                        }
                    }
                }
                """,
            sqlBody: """
                sql {
                    CREATE TABLE flags (id TEXT PRIMARY KEY, active BOOL);
                    INSERT INTO flags (id, active) VALUES ('a', 1), ('b', 0);
                }
                """
        );
    }
    // ---- Byte-identical rules ------------------------------------------------------------------------------

    [Fact]
    public void SingleKeyUpdateCompilesToSetAndAddState() {
        AssertCompilesByteIdentical(
            nativeBody: """
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
                """,
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE heal EVERY TICK AS
                    BEGIN ATOMIC
                        UPDATE fighters SET hp = 100 WHERE id = 'hero';
                        UPDATE fighters SET hp = hp + 10 WHERE id = 'hero';
                    END;
                }
                """
        );
    }
    [Fact]
    public void SingleKeyUpdateReadingAnotherColumnCarriesKey() {
        var (json, diag) = Lower(body: """
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
    public void SetBasedUpdateCompilesToForEachRule() {
        AssertCompilesByteIdentical(
            nativeBody: """
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
                """,
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE poison EVERY TICK AS
                        UPDATE fighters SET hp = hp - 5 WHERE hp > 10;
                }
                """
        );
    }
    [Fact]
    public void SetBasedDeleteCompilesToForEachRule() {
        AssertCompilesByteIdentical(
            nativeBody: """
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
                """,
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE clearDead EVERY TICK AS
                        DELETE FROM fighters WHERE hp <= 0;
                }
                """
        );
    }
    [Fact]
    public void MultiStatementRuleWithSetBasedUpdateIsRefused() {
        var (_, diag) = Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);
                DECLARE gold INT DEFAULT 10;

                CREATE RULE invalid EVERY TICK AS
                BEGIN ATOMIC
                    UPDATE fighters SET hp = hp - 1 WHERE hp > 0;
                    UPDATE gold SET value = 0;
                END;
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(collection: diag, filter: d => (d.Code == PuckDiagnosticCodes.SqlUnsupportedClause));
    }
    [Fact]
    public void BinarySubtractionWithoutSpacesParsesCorrectly() {
        var (json, diag) = Lower(body: """
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
    public void SetIntColumnWithNonIntegerLiteralReportsPUCK053() {
        var (_, diag) = Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = 1.5 WHERE id = 'hero';
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(collection: diag, filter: d => (d.Code == PuckDiagnosticCodes.StateDeclarationInvalidDefault));
    }
    [Fact]
    public void SetColumnWithNullReportsPUCK076() {
        var (_, diag) = Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = NULL WHERE id = 'hero';
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(collection: diag, filter: d => (d.Code == PuckDiagnosticCodes.SqlSyntaxError));
    }
    [Fact]
    public void SetBoolColumnCompilesToNumericOneOrZero() {
        var (json, diag) = Lower(body: """
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
    public void UnknownColumnInWhereClauseReportsPUCK059() {
        var (_, diag) = Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = 10 WHERE unknownCol > 0;
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(collection: diag, filter: d => (d.Code == PuckDiagnosticCodes.StateDeclarationUnknownReference));
    }
    [Fact]
    public void ComparingPrimaryKeyWithInequalityReportsPUCK059() {
        var (_, diag) = Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = 10 WHERE id <> 'hero';
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(collection: diag, filter: d => (d.Code == PuckDiagnosticCodes.StateDeclarationUnknownReference));
    }
    [Fact]
    public void InsertExceedingCapacityReportsPUCK054() {
        var (_, diag) = Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT) CAPACITY 1;
                INSERT INTO fighters (id, hp) VALUES ('hero', 100), ('goblin', 50);
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(collection: diag, filter: d => (d.Code == PuckDiagnosticCodes.StateDeclarationCapacityTooSmall));
    }
    [Fact]
    public void PerKeyRuleOnAllNullableTableReportsPUCK073() {
        var (_, diag) = Lower(body: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE poison EVERY TICK AS
                    UPDATE fighters SET hp = hp - 5 WHERE hp > 10;
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(collection: diag, filter: d => (d.Code == PuckDiagnosticCodes.SqlUnsupportedClause));
    }
    // ---- Refusal diagnostics PUCK070 - PUCK076 -------------------------------------------------------------

    public static TheoryData<string, string, string> RefusalCases() {
        var data = new TheoryData<string, string, string>();

        // PUCK070: SqlUnsupportedType (REAL / FLOAT / DOUBLE -> name FIXED)
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, val FLOAT);
            }
            """,
            p2: "PUCK070",
            p3: "FLOAT"
        );
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, val DOUBLE);
            }
            """,
            p2: "PUCK070",
            p3: "DOUBLE"
        );
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, val REAL);
            }
            """,
            p2: "PUCK070",
            p3: "REAL"
        );

        // PUCK071: SqlCompositePrimaryKey (composite PRIMARY KEY (a, b))
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (a TEXT, b TEXT, PRIMARY KEY (a, b));
            }
            """,
            p2: "PUCK071",
            p3: "PRIMARY KEY (a, b)"
        );

        // PUCK072: SqlInvalidCheckShape (non-between, non-comparison check)
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT CHECK (hp = 5));
            }
            """,
            p2: "PUCK072",
            p3: "hp = 5"
        );

        // PUCK073: SqlUnsupportedClause (GROUP BY, LIMIT, aggregations, IS NULL, etc.)
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT CHECK (hp IS NOT NULL));
            }
            """,
            p2: "PUCK073",
            p3: "hp IS NOT NULL"
        );
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE t SET hp = 10 GROUP BY id;
            }
            """,
            p2: "PUCK073",
            p3: "GROUP BY"
        );
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE t SET hp = 10 LIMIT 5;
            }
            """,
            p2: "PUCK073",
            p3: "LIMIT"
        );

        // PUCK074: SqlSelfReferentialSetUpdate
        data.Add(
            p1: """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = (SELECT hp FROM fighters WHERE id = 'hero') WHERE hp < 50;
            }
            """,
            p2: "PUCK074",
            p3: "(SELECT hp FROM fighters WHERE id = 'hero')"
        );

        // PUCK075: SqlMissingRequiredColumn (INSERT missing NOT NULL column with no default)
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (
                    id TEXT PRIMARY KEY,
                    req INT NOT NULL
                );
                INSERT INTO t (id) VALUES ('hero');
            }
            """,
            p2: "PUCK075",
            p3: "req INT NOT NULL"
        );

        // PUCK076: SqlSyntaxError
        data.Add(
            p1: """
            sql {
                CREATE TABLE t (id);
            }
            """,
            p2: "PUCK076",
            p3: ")"
        );

        return data;
    }
    [MemberData(nameof(RefusalCases))]
    [Theory]
    public void RefusalFiresWithItsCodeAndSourceSpan(string body, string code, string needle) {
        var (_, diagnostics) = Lower(body: body);
        var match = diagnostics.FirstOrDefault(predicate: d => (d.Code == code));

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
    [Fact]
    public void SlotWrite_DecimalToIntegerSlot_RefusedWithInvalidDefault() {
        var (_, diagnostics) = Lower(body: """
            sql {
                DECLARE counter INT;
                CREATE RULE r ON ENTER AS UPDATE counter SET value = 1.5;
            }
            """);

        Assert.True(
            condition: diagnostics.Any(predicate: d => (d.Code == PuckDiagnosticCodes.StateDeclarationInvalidDefault)),
            userMessage: $"Expected PUCK053 for 1.5 into INT slot, got: {diagnostics.FormatReport("")}"
        );
    }
    [Fact]
    public void SlotWrite_TextSlot_ProducesTextProperty() {
        var (json, diagnostics) = Lower(body: """
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
        var (json, diagnostics) = Lower(body: """
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
    // ---- Vector SQL dialect lowering (Part 1 Item 5) --------------------------------------------------------

    [Fact]
    public void VectorTable_WithCapacityAndEvicts_LowersExpectedRow() {
        var (json, diagnostics) = Lower(body: """
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
        var (json, diagnostics) = Lower(body: """
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128 EVICTS;

                INSERT INTO memories (key, embedding) VALUES
                    ('ambush', vector('AAAA'));
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
        Assert.Equal("AAAA", cells[0]?["value"]?.ToString());
    }
    [Fact]
    public void VectorSlot_LowersExpectedRow() {
        var (json, diagnostics) = Lower(body: """
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
        var (json, diagnostics) = Lower(body: """
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
        var (json, diagnostics) = Lower(body: """
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
        var (json, diagnostics) = Lower(body: """
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
        var (json, diagnostics) = Lower(body: """
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
        var (json, diagnostics) = Lower(body: """
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
    [Fact]
    public void EvictsWithoutCapacity_RefusedWithDiagnostic() {
        var (_, diagnostics) = Lower(body: """
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) EVICTS;
            }
            """);

        Assert.True(
            condition: diagnostics.Any(predicate: d => (d.Code == PuckDiagnosticCodes.SqlSyntaxError)),
            userMessage: $"Expected PUCK070 for EVICTS without CAPACITY, got: {diagnostics.FormatReport("")}"
        );
    }
    [Fact]
    public void InsertSelect_AtTopLevel_RefusedWithDiagnostic() {
        var (_, diagnostics) = Lower(body: """
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128;
                CREATE TABLE recalled (
                    key TEXT PRIMARY KEY,
                    score FIXED
                ) CAPACITY 3;
                DECLARE queryEmbedding VECTOR(lore);
                INSERT INTO recalled (key, score)
                SELECT key, similarity(memories.embedding, queryEmbedding)
                FROM memories
                ORDER BY memories.embedding <=> queryEmbedding
                LIMIT 3;
            }
            """);

        Assert.True(
            condition: diagnostics.Any(predicate: d => (d.Code == PuckDiagnosticCodes.SqlUnsupportedClause)),
            userMessage: $"Expected PUCK073 for top-level INSERT SELECT, got: {diagnostics.FormatReport("")}"
        );
    }
}
