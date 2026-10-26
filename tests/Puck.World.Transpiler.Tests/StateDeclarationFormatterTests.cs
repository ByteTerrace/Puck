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
    [Fact]
    public void FormattingEveryDeclarationFormIsStable() => PuckFormat.AssertStable(source: DeclarationSource);
}
