using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Validation;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Lowering coverage for the SQL-flavored state authoring dialect (docs/plans/state-sql.md):
/// genuine byte-identical emission against independently authored native Puck definitions,
/// and strict refusal diagnostics PUCK070-PUCK076 with exact source spans.</summary>
public class StateSqlDeclarationEmitterTests {

    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.False(
            condition: parseResult.Diagnostics.HasErrors,
            userMessage: parseResult.Diagnostics.FormatReport(source)
        );

        var diagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        return (loweringResult.Value!, diagnostics);
    }

    private static void AssertCompilesByteIdentical(string sqlBody, string nativeBody) {
        var (sqlJson, sqlDiag) = Lower(sqlBody);
        var (nativeJson, nativeDiag) = Lower(nativeBody);

        Assert.False(sqlDiag.HasErrors, sqlDiag.FormatReport("SQL compilation errors"));
        Assert.False(nativeDiag.HasErrors, nativeDiag.FormatReport("Native compilation errors"));

        WorldSemanticValidator.ValidateWorld(sqlJson, sourceMap: null, diagnostics: sqlDiag);
        Assert.False(sqlDiag.HasErrors, sqlDiag.FormatReport("SQL semantic validation errors"));

        var mismatch = JsonMismatch.Find(
            actual: sqlJson,
            expected: nativeJson,
            path: "$"
        );

        Assert.Null(mismatch);
    }

    // ---- Byte-identical state declarations -----------------------------------------------------------------

    [Fact]
    public void MultiColumnTableWithInsertCompilesByteIdentically() {
        AssertCompilesByteIdentical(
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
                """,
            nativeBody: """
                state {
                    world {
                        table fightersHp : Int capacity(32) bounds(minimum: 0, maximum: 100, overflow: Saturate) {
                            hero = 80
                            goblin = 30
                        }
                        table fightersMana : Int capacity(32) advance(perSecond: 5) {
                            hero = 0
                            goblin = 0
                        }
                        table fightersSpeed : Fixed capacity(32) {
                            hero = 1.5
                            goblin = 1.5
                        }
                        table fightersAlive : Bool capacity(32) {
                            hero = true
                            goblin = true
                        }
                        table fightersTitle : Text capacity(32) {
                            hero = "Hero"
                            goblin = "Goblin"
                        }
                    }
                }
                """
        );
    }

    [Fact]
    public void SlotDeclarationCompilesByteIdentically() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);
                    DECLARE uninitialized INT;
                }
                """,
            nativeBody: """
                state {
                    world {
                        slot gold : Int = 10 bounds(minimum: 0)
                        slot uninitialized : Int
                    }
                }
                """
        );
    }

    [Fact]
    public void KeyOnlyTableCompilesToBoolRow() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE tags (id TEXT PRIMARY KEY);
                    INSERT INTO tags (id) VALUES ('a'), ('b');
                }
                """,
            nativeBody: """
                state {
                    world {
                        table tags : Bool {
                            a = true
                            b = true
                        }
                    }
                }
                """
        );
    }

    [Fact]
    public void OrderedTableWithReferencesCompilesByteIdentically() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE cardNames (id TEXT PRIMARY KEY);
                    CREATE TABLE deck (id TEXT PRIMARY KEY REFERENCES cardNames) ORDERED CAPACITY 52;
                }
                """,
            nativeBody: """
                state {
                    world {
                        table cardNames : Bool {}
                        pile deck of cardNames capacity(52) {}
                    }
                }
                """
        );
    }

    [Fact]
    public void ColumnRowAliasCompilesToExplicitRowName() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE vitals (
                        id TEXT PRIMARY KEY,
                        hp INT AS vitals_health DEFAULT 100
                    );
                    INSERT INTO vitals (id) VALUES ('hero');
                }
                """,
            nativeBody: """
                state {
                    world {
                        table vitals_health : Int {
                            hero = 100
                        }
                    }
                }
                """
        );
    }

    [Fact]
    public void SqlBlockMergesWithNativeStateWorldDeclarationsInDocumentOrder() {
        AssertCompilesByteIdentical(
            sqlBody: """
                state {
                    world {
                        slot nativeGold : Int = 50
                    }
                }
                sql {
                    DECLARE sqlSilver INT DEFAULT 25;
                }
                """,
            nativeBody: """
                state {
                    world {
                        slot nativeGold : Int = 50
                        slot sqlSilver : Int = 25
                    }
                }
                """
        );
    }

    [Fact]
    public void PolicyDeclarationCompilesToVisibility() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);
                    CREATE POLICY p ON fighters FOR SELECT TO console;
                }
                """,
            nativeBody: """
                state {
                    world {
                        row {
                            name: "fightersHp"
                            kind: "Int"
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
        );
    }

    [Fact]
    public void ExplicitNullOmitsCellFromRow() {
        var (json, diag) = Lower("""
            sql {
                CREATE TABLE t (
                    id TEXT PRIMARY KEY,
                    hp INT DEFAULT 100,
                    note TEXT
                );
                INSERT INTO t (id, hp, note) VALUES ('hero', 50, NULL);
            }
            """);

        Assert.False(diag.HasErrors, diag.FormatReport(""));
        var world = (JsonArray)json["state"]!["world"]!;
        var hpRow = (JsonObject)world[0]!;
        var noteRow = (JsonObject)world[1]!;

        Assert.Equal("tHp", hpRow["name"]?.ToString());
        Assert.Single((JsonArray)hpRow["cells"]!);

        Assert.Equal("tNote", noteRow["name"]?.ToString());
        Assert.Null(noteRow["cells"]); // Omitted entirely!
    }

    [Fact]
    public void BooleanColumnAcceptsOneAndZero() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE flags (id TEXT PRIMARY KEY, active BOOL);
                    INSERT INTO flags (id, active) VALUES ('a', 1), ('b', 0);
                }
                """,
            nativeBody: """
                state {
                    world {
                        table flagsActive : Bool {
                            a = true
                            b = false
                        }
                    }
                }
                """
        );
    }

    // ---- Byte-identical rules ------------------------------------------------------------------------------

    [Fact]
    public void SingleKeyUpdateCompilesToSetAndAddState() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE heal EVERY TICK AS
                    BEGIN ATOMIC
                        UPDATE fighters SET hp = 100 WHERE id = 'hero';
                        UPDATE fighters SET hp = hp + 10 WHERE id = 'hero';
                    END;
                }
                """,
            nativeBody: """
                state {
                    world {
                        table fightersHp : Int {}
                    }
                }
                rule "heal" {
                    mode: "Level"
                    transaction {
                        fightersHp[hero] = 100
                        fightersHp[hero] += 10
                    }
                }
                """
        );
    }

    [Fact]
    public void SingleKeyUpdateReadingAnotherColumnCarriesKey() {
        var (json, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT, mana INT);

                CREATE RULE convertMana EVERY TICK AS
                    UPDATE fighters SET hp = mana WHERE id = 'hero';
            }
            """);

        Assert.False(diag.HasErrors, diag.FormatReport(""));
        var rule = (JsonObject)((JsonArray)json["rules"]!)[0]!;
        var effect = (JsonObject)((JsonArray)rule["effects"]!)[0]!;

        Assert.Equal("setState", effect["$type"]?.ToString());
        Assert.Equal("fightersHp", effect["state"]?.ToString());
        Assert.Equal("hero", effect["key"]?.ToString());
        Assert.Equal("fightersMana[hero]", effect["expression"]?.ToString());
    }

    [Fact]
    public void SetBasedUpdateCompilesToForEachRule() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE poison EVERY TICK AS
                        UPDATE fighters SET hp = hp - 5 WHERE hp > 10;
                }
                """,
            nativeBody: """
                state {
                    world {
                        table fightersHp : Int {}
                    }
                }
                rule "poison" {
                    mode: "Level"
                    forEach: "fightersHp"
                    when fightersHp[$each] > 10
                    fightersHp[$each] += -5
                }
                """
        );
    }

    [Fact]
    public void SetBasedDeleteCompilesToForEachRule() {
        AssertCompilesByteIdentical(
            sqlBody: """
                sql {
                    CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT DEFAULT 100);

                    CREATE RULE clearDead EVERY TICK AS
                        DELETE FROM fighters WHERE hp <= 0;
                }
                """,
            nativeBody: """
                state {
                    world {
                        table fightersHp : Int {}
                    }
                }
                rule "clearDead" {
                    mode: "Level"
                    forEach: "fightersHp"
                    when fightersHp[$each] <= 0
                    remove fightersHp[$each]
                }
                """
        );
    }

    [Fact]
    public void MultiStatementRuleWithSetBasedUpdateIsRefused() {
        var (_, diag) = Lower("""
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

        Assert.True(diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.SqlUnsupportedClause);
    }

    [Fact]
    public void BinarySubtractionWithoutSpacesParsesCorrectly() {
        var (json, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE sub EVERY TICK AS
                BEGIN ATOMIC
                    UPDATE fighters SET hp = hp-5 WHERE id = 'hero';
                    UPDATE fighters SET hp = 10-5 WHERE id = 'hero';
                END;
            }
            """);

        Assert.False(diag.HasErrors, diag.FormatReport(""));
        var rule = (JsonObject)((JsonArray)json["rules"]!)[0]!;
        var txn = (JsonObject)((JsonArray)rule["effects"]!)[0]!;
        var effects = (JsonArray)txn["effects"]!;

        Assert.Equal("addState", effects[0]?["$type"]?.ToString());
        Assert.Equal(-5L, (long)effects[0]!["value"]!);
        Assert.Equal("(10 - 5)", effects[1]?["expression"]?.ToString());
    }

    [Fact]
    public void SetIntColumnWithNonIntegerLiteralReportsPUCK053() {
        var (_, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = 1.5 WHERE id = 'hero';
            }
            """);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.StateDeclarationInvalidDefault);
    }

    [Fact]
    public void SetColumnWithNullReportsPUCK076() {
        var (_, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = NULL WHERE id = 'hero';
            }
            """);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.SqlSyntaxError);
    }

    [Fact]
    public void SetBoolColumnCompilesToNumericOneOrZero() {
        var (json, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, alive BOOL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET alive = TRUE WHERE id = 'hero';
            }
            """);

        Assert.False(diag.HasErrors, diag.FormatReport(""));
        var rule = (JsonObject)((JsonArray)json["rules"]!)[0]!;
        var effect = (JsonObject)((JsonArray)rule["effects"]!)[0]!;
        Assert.Equal("setState", effect["$type"]?.ToString());
        Assert.Equal(1L, (long)effect["value"]!);
    }

    [Fact]
    public void UnknownColumnInWhereClauseReportsPUCK059() {
        var (_, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = 10 WHERE unknownCol > 0;
            }
            """);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.StateDeclarationUnknownReference);
    }

    [Fact]
    public void ComparingPrimaryKeyWithInequalityReportsPUCK059() {
        var (_, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = 10 WHERE id <> 'hero';
            }
            """);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.StateDeclarationUnknownReference);
    }

    [Fact]
    public void InsertExceedingCapacityReportsPUCK054() {
        var (_, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT) CAPACITY 1;
                INSERT INTO fighters (id, hp) VALUES ('hero', 100), ('goblin', 50);
            }
            """);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.StateDeclarationCapacityTooSmall);
    }

    [Fact]
    public void PerKeyRuleOnAllNullableTableReportsPUCK073() {
        var (_, diag) = Lower("""
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE poison EVERY TICK AS
                    UPDATE fighters SET hp = hp - 5 WHERE hp > 10;
            }
            """);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag, d => d.Code == PuckDiagnosticCodes.SqlUnsupportedClause);
    }

    // ---- Refusal diagnostics PUCK070 - PUCK076 -------------------------------------------------------------

    public static TheoryData<string, string, string> RefusalCases() {
        var data = new TheoryData<string, string, string>();

        // PUCK070: SqlUnsupportedType (REAL / FLOAT / DOUBLE -> name FIXED)
        data.Add(
            """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, val FLOAT);
            }
            """,
            "PUCK070",
            "FLOAT"
        );
        data.Add(
            """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, val DOUBLE);
            }
            """,
            "PUCK070",
            "DOUBLE"
        );
        data.Add(
            """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, val REAL);
            }
            """,
            "PUCK070",
            "REAL"
        );

        // PUCK071: SqlCompositePrimaryKey (composite PRIMARY KEY (a, b))
        data.Add(
            """
            sql {
                CREATE TABLE t (a TEXT, b TEXT, PRIMARY KEY (a, b));
            }
            """,
            "PUCK071",
            "PRIMARY KEY (a, b)"
        );

        // PUCK072: SqlInvalidCheckShape (non-between, non-comparison check)
        data.Add(
            """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT CHECK (hp = 5));
            }
            """,
            "PUCK072",
            "hp = 5"
        );

        // PUCK073: SqlUnsupportedClause (GROUP BY, LIMIT, aggregations, IS NULL, etc.)
        data.Add(
            """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT CHECK (hp IS NOT NULL));
            }
            """,
            "PUCK073",
            "hp IS NOT NULL"
        );
        data.Add(
            """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE t SET hp = 10 GROUP BY id;
            }
            """,
            "PUCK073",
            "GROUP BY"
        );
        data.Add(
            """
            sql {
                CREATE TABLE t (id TEXT PRIMARY KEY, hp INT);

                CREATE RULE r EVERY TICK AS
                    UPDATE t SET hp = 10 LIMIT 5;
            }
            """,
            "PUCK073",
            "LIMIT"
        );

        // PUCK074: SqlSelfReferentialSetUpdate
        data.Add(
            """
            sql {
                CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT NOT NULL);

                CREATE RULE r EVERY TICK AS
                    UPDATE fighters SET hp = (SELECT hp FROM fighters WHERE id = 'hero') WHERE hp < 50;
            }
            """,
            "PUCK074",
            "(SELECT hp FROM fighters WHERE id = 'hero')"
        );

        // PUCK075: SqlMissingRequiredColumn (INSERT missing NOT NULL column with no default)
        data.Add(
            """
            sql {
                CREATE TABLE t (
                    id TEXT PRIMARY KEY,
                    req INT NOT NULL
                );
                INSERT INTO t (id) VALUES ('hero');
            }
            """,
            "PUCK075",
            "req INT NOT NULL"
        );

        // PUCK076: SqlSyntaxError
        data.Add(
            """
            sql {
                CREATE TABLE t (id);
            }
            """,
            "PUCK076",
            ")"
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
        var (_, diagnostics) = Lower("""
            sql {
                DECLARE counter INT;
                CREATE RULE r ON ENTER AS UPDATE counter SET value = 1.5;
            }
            """);

        Assert.True(
            condition: diagnostics.Any(d => d.Code == PuckDiagnosticCodes.StateDeclarationInvalidDefault),
            userMessage: $"Expected PUCK053 for 1.5 into INT slot, got: {diagnostics.FormatReport("")}"
        );
    }

    [Fact]
    public void SlotWrite_TextSlot_ProducesTextProperty() {
        var (json, diagnostics) = Lower("""
            sql {
                DECLARE greeting TEXT;
                CREATE RULE r ON ENTER AS UPDATE greeting SET value = 'hello';
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rules = json["rules"] as JsonArray;
        Assert.NotNull(rules);
        var eff = rules[0]?["effects"]?[0] as JsonObject;
        Assert.NotNull(eff);
        Assert.Equal("hello", eff["text"]?.ToString());
        Assert.Null(eff["expression"]);
    }

    [Fact]
    public void SlotWrite_IntSlot_ProducesValueProperty() {
        var (json, diagnostics) = Lower("""
            sql {
                DECLARE counter INT;
                CREATE RULE r ON ENTER AS UPDATE counter SET value = 42;
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rules = json["rules"] as JsonArray;
        Assert.NotNull(rules);
        var eff = rules[0]?["effects"]?[0] as JsonObject;
        Assert.NotNull(eff);
        Assert.Equal(42L, eff["value"]?.GetValue<long>());
    }

    // ---- Vector SQL dialect lowering (Part 1 Item 5) --------------------------------------------------------

    [Fact]
    public void VectorTable_WithCapacityAndEvicts_LowersExpectedRow() {
        var (json, diagnostics) = Lower("""
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128 EVICTS;
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var world = json["state"]?["world"] as JsonArray;
        Assert.NotNull(world);
        var row = world.FirstOrDefault(r => r?["name"]?.ToString() == "memoriesEmbedding") as JsonObject;
        Assert.NotNull(row);
        Assert.Equal("Vector", row["kind"]?.ToString());
        Assert.Equal("lore", row["space"]?.ToString());
        Assert.Equal(128, row["capacity"]?.GetValue<int>());
        Assert.True(row["evicts"]?.GetValue<bool>());
    }

    [Fact]
    public void VectorTable_WithInsertVectorLiteral_LowersCells() {
        var (json, diagnostics) = Lower("""
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) CAPACITY 128 EVICTS;

                INSERT INTO memories (key, embedding) VALUES
                    ('ambush', vector('AAAA'));
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var world = json["state"]?["world"] as JsonArray;
        Assert.NotNull(world);
        var row = world.FirstOrDefault(r => r?["name"]?.ToString() == "memoriesEmbedding") as JsonObject;
        Assert.NotNull(row);
        var cells = row["cells"] as JsonArray;
        Assert.NotNull(cells);
        Assert.Single(cells);
        Assert.Equal("ambush", cells[0]?["key"]?.ToString());
        Assert.Equal("AAAA", cells[0]?["value"]?.ToString());
    }

    [Fact]
    public void VectorSlot_LowersExpectedRow() {
        var (json, diagnostics) = Lower("""
            sql {
                DECLARE playerEmbedding VECTOR(lore);
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var world = json["state"]?["world"] as JsonArray;
        Assert.NotNull(world);
        var row = world.FirstOrDefault(r => r?["name"]?.ToString() == "playerEmbedding") as JsonObject;
        Assert.NotNull(row);
        Assert.Equal("Vector", row["kind"]?.ToString());
        Assert.Equal("lore", row["space"]?.ToString());
    }

    [Fact]
    public void VectorTable_DefaultSpaceInferredWhenSingleSpaceDeclared() {
        var (json, diagnostics) = Lower("""
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

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var world = json["state"]?["world"] as JsonArray;
        Assert.NotNull(world);
        var row = world.FirstOrDefault(r => r?["name"]?.ToString() == "memoriesEmbedding") as JsonObject;
        Assert.NotNull(row);
        Assert.Equal("lore", row["space"]?.ToString());
    }

    [Fact]
    public void VectorComparison_InWhereClause_LowersRule() {
        var (json, diagnostics) = Lower("""
            sql {
                DECLARE playerEmbedding VECTOR(lore);
                DECLARE enemyEmbedding VECTOR(lore);
                DECLARE alert BOOL;
                CREATE RULE checkSimilarity ON ENTER AS
                    UPDATE alert SET value = TRUE WHERE similarity(playerEmbedding, enemyEmbedding) > 0.6;
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rules = json["rules"] as JsonArray;
        Assert.NotNull(rules);
        var rule = rules[0] as JsonObject;
        Assert.NotNull(rule);
        var gate = rule["gate"] as JsonObject;
        Assert.NotNull(gate);
        Assert.Equal("compareValue", gate["$type"]?.ToString());
        Assert.Equal("similarity(playerEmbedding, enemyEmbedding)", gate["left"]?.ToString());
        Assert.Equal("Greater", gate["comparison"]?.ToString());
        Assert.Equal("0.6", gate["right"]?.ToString());
        Assert.Equal("Fixed", gate["kind"]?.ToString());
        var eff = rule["effects"]?[0] as JsonObject;
        Assert.NotNull(eff);
        Assert.Equal("setState", eff["$type"]?.ToString());
        Assert.Equal("alert", eff["state"]?.ToString());
    }

    [Fact]
    public void VectorUpdate_Assignment_LowersEffect() {
        var (json, diagnostics) = Lower("""
            sql {
                DECLARE targetEmbedding VECTOR(lore);
                DECLARE sourceEmbedding VECTOR(lore);
                CREATE RULE copyEmbedding ON ENTER AS
                    UPDATE targetEmbedding SET value = sourceEmbedding;
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rules = json["rules"] as JsonArray;
        Assert.NotNull(rules);
        var rule = rules[0] as JsonObject;
        Assert.NotNull(rule);
        var eff = rule["effects"]?[0] as JsonObject;
        Assert.NotNull(eff);
        Assert.Equal("setState", eff["$type"]?.ToString());
        Assert.Equal("targetEmbedding", eff["state"]?.ToString());
        Assert.Equal("sourceEmbedding", eff["expression"]?.ToString());
    }

    [Fact]
    public void InsertSelect_NearestTransform_LowersExpectedRule() {
        var (json, diagnostics) = Lower("""
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

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rules = json["rules"] as JsonArray;
        Assert.NotNull(rules);
        var rule = rules[0] as JsonObject;
        Assert.NotNull(rule);
        var eff = rule["effects"]?[0] as JsonObject;
        Assert.NotNull(eff);
        Assert.Equal("transformState", eff["$type"]?.ToString());
        var transform = eff["transform"] as JsonObject;
        Assert.NotNull(transform);
        Assert.Equal("nearest", transform["$type"]?.ToString());
        Assert.Equal("memoriesEmbedding", transform["from"]?.ToString());
        Assert.Equal("recalledScore", transform["into"]?.ToString());
        Assert.Equal("queryEmbedding", transform["query"]?.ToString());
        Assert.Equal(3, transform["k"]?.GetValue<int>());
    }

    [Fact]
    public void InsertSelect_NearestTransform_WithWhereAndExcludeAndFarthest() {
        var (json, diagnostics) = Lower("""
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

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rules = json["rules"] as JsonArray;
        Assert.NotNull(rules);
        var rule = rules[0] as JsonObject;
        Assert.NotNull(rule);
        var eff = rule["effects"]?[0] as JsonObject;
        Assert.NotNull(eff);
        Assert.Equal("transformState", eff["$type"]?.ToString());
        var transform = eff["transform"] as JsonObject;
        Assert.NotNull(transform);
        Assert.Equal("nearest", transform["$type"]?.ToString());
        Assert.Equal("0.5", transform["threshold"]?.ToString());
        Assert.True(transform["farthest"]?.GetValue<bool>());
        Assert.Equal("ignored", transform["exclude"]?.ToString());
    }

    [Fact]
    public void EvictsWithoutCapacity_RefusedWithDiagnostic() {
        var (_, diagnostics) = Lower("""
            sql {
                CREATE TABLE memories (
                    key TEXT PRIMARY KEY,
                    embedding VECTOR(lore)
                ) EVICTS;
            }
            """);

        Assert.True(
            condition: diagnostics.Any(d => d.Code == PuckDiagnosticCodes.SqlSyntaxError),
            userMessage: $"Expected PUCK070 for EVICTS without CAPACITY, got: {diagnostics.FormatReport("")}"
        );
    }

    [Fact]
    public void InsertSelect_AtTopLevel_RefusedWithDiagnostic() {
        var (_, diagnostics) = Lower("""
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
            condition: diagnostics.Any(d => d.Code == PuckDiagnosticCodes.SqlUnsupportedClause),
            userMessage: $"Expected PUCK073 for top-level INSERT SELECT, got: {diagnostics.FormatReport("")}"
        );
    }
}
