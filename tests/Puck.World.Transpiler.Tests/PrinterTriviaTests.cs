using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>What the syntax tree carries for the printer: the comments, the blank lines, and the line breaks the
/// author chose. A tree that dropped any of them could not print a source back as it was written.</summary>
public class PrinterTriviaTests {
    // Each source below is already in the printer's own layout, so formatting it is the round trip: parse, print,
    // and compare against the bytes that went in.
    private static void RoundTrips(string source) {
        Assert.Equal(
            actual: PuckFormat.Format(source),
            expected: source
        );
    }

    [Fact]
    public void ACommentKeepsItsPlaceWhereverItWasWritten() {
        RoundTrips(source: """
            // A header above the schema.
            schema: "puck.world.definition.v1"
            // Above the first statement.
            host {
              // Above a property.
              authority: "test.host" // Beside a property.
              /* A block comment inside the body. */
              presentation: windowed
              // Above the closing brace.
            }
            // At the end of the file.

            """);
    }
    [Fact]
    public void ABlankLineTheAuthorLeftIsStillThere() {
        RoundTrips(source: """
            host {
              authority: "test.host"

              presentation: windowed
            }

            views {
              count: 1
            }

            """);
    }
    [Fact]
    public void ARunOfBlankLinesKeepsItsLength() {
        const string Source = "host {\n  a: 1\n\n\n\n  b: 2\n}\n";

        Assert.Equal(
            actual: PuckFormat.Format(Source),
            expected: Source
        );
    }
    [Fact]
    public void AnArrayWrittenOnOneLineStaysOnOneLine() {
        RoundTrips(source: """
            placement "seat" {
              position [0, 1, 2]
            }

            """);
    }
    [Fact]
    public void AnArrayWrittenOverSeveralLinesKeepsThem() {
        RoundTrips(source: """
            palette {
              colors [
                6209
                32767
              ]
            }

            """);
    }
    [Fact]
    public void ACommentInsideAnArrayKeepsItsElement() {
        RoundTrips(source: """
            palette {
              colors [
                // The background entry.
                6209
                32767 // The brightest entry.
              ]
            }

            """);
    }
    [Fact]
    public void AnArrayKeepsTheRowsItsAuthorWrote() {
        RoundTrips(source: """
            // Cell ordinal = row * 3 + file.
            let opening = [
              0, 0, 0
              0, 2, 0
              0, 0, 0
            ]

            """);
    }
    // A base is the author's, not the reader's: an address written in hexadecimal is unreadable as a decimal.
    [Fact]
    public void AHexadecimalLiteralKeepsItsBase() {
        RoundTrips(source: """
            let busAx = 0xC200
            let busAy = -0x01

            """);
    }
    [MemberData(nameof(GetShippedWorldSources))]
    [Theory]
    public void AShippedSourceRoundTripsByteIdentical(string relativePath) {
        var source = File.ReadAllText(path: Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        ));

        Assert.Equal(
            actual: PuckFormat.Format(source),
            expected: source
        );
    }
    public static TheoryData<string> GetShippedWorldSources() => ShippedWorlds.Sources();
}
