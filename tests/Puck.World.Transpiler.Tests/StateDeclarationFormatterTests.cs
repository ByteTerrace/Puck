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

    [InlineData(nameof(DeclarationSource))]
    [InlineData(nameof(SqlDeclarationSource))]
    [Theory]
    public void FormattingEveryDeclarationFormIsStable(string name) => PuckFormat.AssertStable(source: ((name == nameof(DeclarationSource))
        ? DeclarationSource
        : SqlDeclarationSource)
    );
}
