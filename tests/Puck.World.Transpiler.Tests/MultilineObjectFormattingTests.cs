using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The printer's line-break rule: an array or object keeps the shape its author gave it, and a block body
/// always occupies its own lines.</summary>
public class MultilineObjectFormattingTests {
    [Fact]
    public void DelimitersInCommentsAndStringsRemainLiteral() {
        const string Source = "items [\n{ name: \"{[,] }\", value: 2 } // { ,\n]\n";
        var formatted = PuckFormat.Format(Source);

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
            PuckFormat.Format(formatted)
        );
    }
    [InlineData(2, true, "  ")]
    [InlineData(4, true, "    ")]
    [InlineData(4, false, "\t")]
    [Theory]
    public void ExplicitIndentationIsHonored(int tabSize, bool insertSpaces, string indent) {
        var formatted = PuckFormat.Format(
            insertSpaces: insertSpaces,
            source: "host { width: 1280, height: 720 }",
            tabSize: tabSize
        );

        Assert.Equal(
            actual: formatted,
            expected: $"host {{\n{indent}width: 1280\n{indent}height: 720\n}}\n"
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(
                insertSpaces: insertSpaces,
                source: formatted,
                tabSize: tabSize
            )
        );
    }
    [Fact]
    public void AnObjectWrittenOnOneLineStaysOnOneLine() {
        const string Source = "stations [{ index: 0, name: \"isolated\", p [0, 0, 0], yaw: 0 }]";
        const string Expected = "stations [{ index: 0, name: \"isolated\", p [0, 0, 0], yaw: 0 }]\n";
        var formatted = PuckFormat.Format(Source);

        Assert.Equal(
            actual: formatted,
            expected: Expected
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(formatted)
        );
    }
    [Fact]
    public void AnObjectWrittenOverSeveralLinesKeepsThem() {
        const string Source = "stations [\n  {\n    index: 0\n    name: \"isolated\"\n  }\n]\n";
        var formatted = PuckFormat.Format(Source);

        Assert.Equal(
            actual: formatted,
            expected: Source
        );
    }
}
