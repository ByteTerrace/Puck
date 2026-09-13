using Puck.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class MultilineObjectFormattingTests {
    [Fact]
    public void DelimitersInCommentsAndStringsRemainLiteral() {
        const string Source = "items [\n{ name: \"{[,] }\", value: 2 } // { ,\n]\n";
        var formatted = PuckFormatter.Format(Source);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "name: \"{[,] }\",\n    value: 2"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "} // { ,"
        );
        Assert.Equal(
            formatted,
            PuckFormatter.Format(formatted)
        );
    }
    [InlineData(2, true, "  ")]
    [InlineData(4, true, "    ")]
    [InlineData(4, false, "\t")]
    [Theory]
    public void ExplicitIndentationIsHonored(int tabSize, bool insertSpaces, string indent) {
        var formatted = PuckFormatter.Format(
            insertSpaces: insertSpaces,
            source: "host { width: 1280, height: 720 }",
            tabSize: tabSize
        );

        Assert.Equal(
            actual: formatted,
            expected: $"host {{\n{indent}width: 1280,\n{indent}height: 720\n}}\n"
        );
        Assert.Equal(
            formatted,
            PuckFormatter.Format(
                insertSpaces: insertSpaces,
                source: formatted,
                tabSize: tabSize
            )
        );
    }
    [Fact]
    public void RecordsExpandTheirMembersAndKeepVectorsCompact() {
        const string Source = "stations [{ index: 0, name: \"isolated\", p [0, 0, 0], yaw: 0 }]";
        const string Expected = "stations [\n  {\n    index: 0,\n    name: \"isolated\",\n    p [0, 0, 0],\n    yaw: 0\n  }\n]\n";
        var formatted = PuckFormatter.Format(Source);

        Assert.Equal(
            actual: formatted,
            expected: Expected
        );
        Assert.Equal(
            formatted,
            PuckFormatter.Format(formatted)
        );
    }
}
