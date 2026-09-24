using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A statement the parser cannot read draws one report, the parse fault, from everything an author is told
/// (<see cref="WorldSourceDiagnostics.Diagnose"/>): the section it stands in reports no empty statement, and the
/// unused-declaration lint reports nothing whose use may sit in the unread text.</summary>
public sealed class ParseFaultReportLawTests {
    public static TheoryData<string> Faults => new() {
        """
        let starters = [1, 2]

        state {
          world {
            slot known = reduce(starters, 0, (mask, e) => mask + (1 @ e))
          }
        }
        """,
        """
        let starters = [1, 2]

        known: reduce(starters, 0, (mask, e) => mask + (1 @ e))
        """,
        """
        let seat = 1

        placements {
          placement {
            id: "a"
            position [seat @ 2, 0, 0]
          }
        }
        """,
        """
        rule "probe" {
          123
          transform boardCombine(row: marked, operation: Copy, left: contested)
        }
        """,
    };

    [Fact]
    public void AShiftInsideAReduceParses() {
        var source = (WorldSources.Header + """
            let starters = [1, 2]

            state {
              world {
                slot known = reduce(starters, 0, (mask, e) => mask + (1 << e))
              }
            }
            """);

        var diagnostics = WorldSourceDiagnostics.DiagnoseSource(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: null
        ).Diagnostics;

        Assert.True(
            condition: (diagnostics.Count == 0),
            userMessage: diagnostics.FormatReport(source)
        );
    }
    [MemberData(memberName: nameof(Faults))]
    [Theory]
    public void AParseFaultIsTheOnlyReport(string body) {
        var source = (WorldSources.Header + body);
        var diagnostics = WorldSourceDiagnostics.Diagnose(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: null
        );

        Assert.True(
            condition: ((diagnostics.Count == 1) && (diagnostics[0].Code == PuckDiagnosticCodes.Syntax)),
            userMessage: diagnostics.FormatReport(source)
        );
    }
}
