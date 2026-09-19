using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Every position the grammar admits a comment, and every blank-line run length: a formatted source keeps
/// them byte for byte. A comment the printer drops is data loss in the verb an editor runs on save.</summary>
public class PrinterCommentPositionTests {
    private static void RoundTrips(string source) {
        Assert.Equal(
            actual: PuckFormat.Format(source),
            expected: source
        );
    }

    [Fact]
    public void EveryStatementAndArrayPositionKeepsItsComment() {
        RoundTrips(source: """"
            // P1: above the schema line.
            schema: "puck.world.definition.v1"
            // P3a: above the first statement.
            let note = """a raw fence // this is not a comment, and /* neither is this */"""
            let blank1 = 1

            let blank2 = 2


            let blank3 = 3



            let blank4 = 4
            host {
              // P4: inside a block, above the first statement.
              authority: "test.host" // P5: trailing on a statement.
              /* P6: a block comment between statements. */
              presentation: windowed
              // P8: before the closing brace.
            }
            // P2: between two blocks.
            palette {
              colors [
                // P7a: inside an array, above a row.
                6209, 6210
                32767, 32766 // P7b: trailing on an array row.
                // P7c: before the array's closing bracket.
              ]
            }
            // P9: after the last statement, at end of file.

            """");
    }
    [Fact]
    public void AHeaderAndAnOpeningBraceKeepTheirComments() {
        RoundTrips(source: """
            schema: "puck.world.definition.v1"
            host { // Q1: on the same line as the opening brace.
              authority: "test.host"
            }
            palette /* Q2: between a header and its brace */ {
              colors [
                1, 2, 3,
                4, 5, 6,
              ]
            }
            layout study {
              /* Q3: a block comment
                 spread over two lines
                 with its own indentation. */
              count: 1
              origin {
                x: 1 // Q4
                y: 2
              }
            }

            """);
    }
    [Fact]
    public void ATypeBodyAndABranchKeepTheirComments() {
        RoundTrips(source: """
            schema: "puck.world.definition.v1"
            enum Phase {
              // R1: inside an enum body.
              setup
              play
            }
            record Point {
              // R2: inside a record body.
              x: int
              y: int
            }
            slot counter: int = 0
            rule "branch" {
              when counter > 0
              if counter > 1 {
                counter += 1
                // R3: before an if-body closing brace.
              } else {
                counter += 2
                // R4: before an else-body closing brace.
              }
            }
            rule /* R5: between the rule keyword and its name */ "named" {
              counter += 1
            }

            """);
    }
    [Fact]
    public void ATransactionAndAnArgumentListKeepTheirComments() {
        RoundTrips(source: """
            schema: "puck.world.definition.v1"
            slot counter: int = 0
            rule "tx" {
              when counter > 0
              transaction {
                counter += 1
                // S1: before a transaction body's closing brace.
              } onFailure {
                counter += 2
                // S2: before an onFailure body's closing brace.
              }
            }
            placement "seat" {
              position [
                1,
                2
              ]
              facing: select(
                // S3: inside a call's argument list.
                1,
                2,
                3
              )
            }

            """);
    }
}
