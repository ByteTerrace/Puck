using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckFormatter"/> against the concise state-row declarations: formatting is idempotent and
/// never changes what a document compiles to.</summary>
public class StateDeclarationFormatterTests {
    private const string DeclarationSource = """
        schema: "puck.world.definition.v1"

        state {
            world {
                table vitals : Int capacity(3) bounds(minimum: 0, maximum: 100, overflow: Saturate) {
                    health = 100
                    mana = 50 advance(perSecond: 5)
                }

                slot gold : Int = 10 bounds(minimum: 0)

                table cardNames : Int capacity(2) {
                    king = 0
                    queen = 1
                }

                pile deck of cardNames capacity(2) {
                    king
                    queen
                }

                grid board : Int dimensions(width: 2, depth: 2) wrap(Both) empty(-1) {
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
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source);

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

    [Fact]
    public void FormattingEveryDeclarationFormTwiceIsIdempotent() {
        var pass1 = PuckFormatter.Format(DeclarationSource);
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }
    [Fact]
    public void FormattingPreservesWhatTheDeclarationsCompileTo() {
        var (beforeJson, beforeDiagnostics) = Lower(source: DeclarationSource);
        var formatted = PuckFormatter.Format(DeclarationSource);
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
}
