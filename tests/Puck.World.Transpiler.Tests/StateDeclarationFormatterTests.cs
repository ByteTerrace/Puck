using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckPrinter"/> against the concise state-row declarations: formatting is idempotent and
/// never changes what a document compiles to.</summary>
public class StateDeclarationFormatterTests {
    private const string DeclarationSource = """
        schema: "puck.world.definition.v1"

        state {
            world {
                table vitals capacity(3) bounds(0..100, overflow: Saturate) {
                    health = 100
                    mana = 50 advance(perSecond: 5)
                }

                slot gold = 10 bounds(0..)

                table cardNames capacity(2) {
                    king = 0
                    queen = 1
                }

                pile deck of cardNames capacity(2) {
                    king
                    queen
                }

                grid board dimensions(width: 2, depth: 2) wrap(Both) empty(-1) {
                    "0" = 1
                    "3" = 2
                }
            }
        }

        rule "r" {
            when vitals.mana >= 10
            if vitals.mana >= 10 {
                vitals.mana = vitals.mana - 10
            } else {
                gold = gold - 1
            }
        }

        """;

    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string source) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }

    [Fact]
    public void FormattingEveryDeclarationFormTwiceIsIdempotent() {
        var pass1 = PuckFormat.Format(DeclarationSource);
        var pass2 = PuckFormat.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }
    [Fact]
    public void FormattingPreservesWhatTheDeclarationsCompileTo() {
        var (beforeJson, beforeDiagnostics) = Lower(source: DeclarationSource);
        var formatted = PuckFormat.Format(DeclarationSource);

        var (afterJson, afterDiagnostics) = Lower(source: formatted);

        Assert.False(condition: beforeDiagnostics.HasErrors);
        Assert.False(condition: afterDiagnostics.HasErrors);

        var mismatch = JsonMismatch.Find(
            actual: afterJson,
            expected: beforeJson,
            path: "$"
        );

        Assert.Null(@object: mismatch);
    }

    private const string SqlDeclarationSource = """
        schema: "puck.world.definition.v1"

        sql {
            -- Define the fighters table
            CREATE TABLE fighters (
                id TEXT PRIMARY KEY,
                hp INT NOT NULL DEFAULT 100 CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE,
                mana INT DEFAULT 0 ADVANCE 5 PER SECOND,
                title TEXT
            ) CAPACITY 32;

            /* Initial seed fighters */
            INSERT INTO fighters (id, hp, title) VALUES
                ('hero', 80, 'Hero''s Journey'),
                ('goblin', 30, 'Goblin');

            DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);

            CREATE RULE regen EVERY TICK AS
                UPDATE fighters SET mana = mana + 1 WHERE mana < 100;

            CREATE RULE heal EVERY TICK AS
            BEGIN ATOMIC
                UPDATE fighters SET hp = 100 WHERE id = 'hero';
            EXCEPTION
                UPDATE fighters SET hp = 0 WHERE id = 'hero';
            END;
        }

        """;

    [Fact]
    public void FormattingSqlDeclarationTwiceIsIdempotent() {
        var pass1 = PuckFormat.Format(SqlDeclarationSource);
        var pass2 = PuckFormat.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }
    [Fact]
    public void FormattingPreservesWhatSqlDeclarationsCompileTo() {
        var (beforeJson, beforeDiagnostics) = Lower(source: SqlDeclarationSource);
        var formatted = PuckFormat.Format(SqlDeclarationSource);

        var (afterJson, afterDiagnostics) = Lower(source: formatted);

        Assert.False(condition: beforeDiagnostics.HasErrors, userMessage: beforeDiagnostics.FormatReport(""));
        Assert.False(condition: afterDiagnostics.HasErrors, userMessage: afterDiagnostics.FormatReport(""));

        var mismatch = JsonMismatch.Find(
            actual: afterJson,
            expected: beforeJson,
            path: "$"
        );

        Assert.Null(@object: mismatch);
    }
}
