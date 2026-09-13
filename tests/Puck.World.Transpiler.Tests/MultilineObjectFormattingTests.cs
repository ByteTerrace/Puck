using Puck.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class MultilineObjectFormattingTests {
    [Fact]
    public void RecordsExpandTheirMembersAndKeepVectorsCompact() {
        const string source = "stations [{ index: 0, name: \"isolated\", p [0, 0, 0], yaw: 0 }]";
        const string expected = "stations [\n  {\n    index: 0,\n    name: \"isolated\",\n    p [0, 0, 0],\n    yaw: 0\n  }\n]\n";
        var formatted = PuckFormatter.Format(source);
        Assert.Equal(expected, formatted);
        Assert.Equal(formatted, PuckFormatter.Format(formatted));
    }

    [Theory]
    [InlineData(2, true, "  ")]
    [InlineData(4, true, "    ")]
    [InlineData(4, false, "\t")]
    public void ExplicitIndentationIsHonored(int tabSize, bool insertSpaces, string indent) {
        var formatted = PuckFormatter.Format("host { width: 1280, height: 720 }", tabSize, insertSpaces);
        Assert.Equal($"host {{\n{indent}width: 1280,\n{indent}height: 720\n}}\n", formatted);
        Assert.Equal(formatted, PuckFormatter.Format(formatted, tabSize, insertSpaces));
    }

    [Fact]
    public void DelimitersInCommentsAndStringsRemainLiteral() {
        const string source = "items [\n{ name: \"{[,] }\", value: 2 } // { ,\n]\n";
        var formatted = PuckFormatter.Format(source);
        Assert.Contains("name: \"{[,] }\",\n    value: 2", formatted);
        Assert.Contains("} // { ,", formatted);
        Assert.Equal(formatted, PuckFormatter.Format(formatted));
    }
}
