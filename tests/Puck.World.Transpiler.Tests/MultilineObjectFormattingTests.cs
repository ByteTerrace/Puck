using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The printer's line-break rule: an array or object keeps the shape its author gave it, and a block body
/// always occupies its own lines.</summary>
public class MultilineObjectFormattingTests {
    [Fact]
    public void DelimitersInCommentsAndStringsRemainLiteral() {
        const string Source = "items [\n{ name: \"{[,] }\", value: 2 } // { ,\n]\n";
        var formatted = PuckFormat.Format(source: Source);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "{ name: \"{[,] }\", value: 2 }"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "} // { ,"
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(source: formatted)
        );
    }
    // A source has one layout: every level indents by the printer's one width, whatever the input used.
    [InlineData("host { width: 1280, height: 720 }")]
    [InlineData("host {\n\twidth: 1280\n\theight: 720\n}\n")]
    [InlineData("host {\n    width: 1280\n    height: 720\n}\n")]
    [Theory]
    public void IndentationIsThePrintersOwn(string source) {
        var formatted = PuckFormat.Format(source: source);

        Assert.Equal(
            actual: formatted,
            expected: "host {\n  width: 1280\n  height: 720\n}\n"
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(source: formatted)
        );
    }
    [Fact]
    public void AnObjectWrittenOnOneLineStaysOnOneLine() {
        const string Source = "stations [{ index: 0, name: \"isolated\", p [0, 0, 0], yaw: 0 }]";
        const string Expected = "stations [{ index: 0, name: \"isolated\", p [0, 0, 0], yaw: 0 }]\n";
        var formatted = PuckFormat.Format(source: Source);

        Assert.Equal(
            actual: formatted,
            expected: Expected
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(source: formatted)
        );
    }
    [Fact]
    public void AnObjectWrittenOverSeveralLinesKeepsThem() {
        const string Source = "stations [\n  {\n    index: 0\n    name: \"isolated\"\n  }\n]\n";
        var formatted = PuckFormat.Format(source: Source);

        Assert.Equal(
            actual: formatted,
            expected: Source
        );
    }
}
