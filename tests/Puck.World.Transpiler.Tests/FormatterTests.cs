using Puck.World.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class FormatterTests {
    [Fact]
    public void TestFormatterBasicIndentationAndBraces() {
        var unformatted = @"
puck: 1
host:
{
authority: ""test.host""
presentation: windowed
}
entities:
[
{
name: ""Player""
components:
[
{ type: ""Transform"" }
]
}
]";

        var formatted = PuckFormatter.Format(unformatted);

        // Egyptian braces: opening brace on same line
        Assert.Contains("host: {", formatted);
        Assert.Contains("entities: [", formatted);
        // 4 spaces indentation
        Assert.Contains("    authority: \"test.host\"", formatted);
        Assert.Contains("    presentation: windowed", formatted);
    }

    [Fact]
    public void TestFormatterPreservesComments() {
        var input = @"// Header comment
puck: 1

/* Host section block comment */
host: {
    authority: ""test.host"" // inline comment
}
";
        var formatted = PuckFormatter.Format(input);
        Assert.Contains("// Header comment", formatted);
        Assert.Contains("/* Host section block comment */", formatted);
        Assert.Contains("// inline comment", formatted);
    }

    [Fact]
    public void TestFormatterIdempotency() {
        var input = @"puck: 1

host: {
    authority: ""test.host""
    presentation: windowed
}

entities: [
    {
        name: ""Entity1""
    }
]
";
        var pass1 = PuckFormatter.Format(input);
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(pass1, pass2);
    }

    [Fact]
    public void TestFormatterColonAndCommaSpacing() {
        var unformatted = @"puck:1
host:{
authority:""my-auth"",presentation:headless
}
";
        var formatted = PuckFormatter.Format(unformatted);
        Assert.Contains("puck: 1", formatted);
        Assert.Contains("authority: \"my-auth\"", formatted);
        Assert.Contains("presentation: headless", formatted);
    }

    [Fact]
    public void TestFormatterArrayOfObjectsDoesNotCollapseBraces() {
        var unformatted = @"
cells: [
{
key: ""feltColor""
value: feltColor
}
{
key: ""activeRound""
value: 1
}
]";
        var formatted = PuckFormatter.Format(unformatted);
        Assert.DoesNotContain("[ {", formatted);
        Assert.DoesNotContain("} {", formatted);
        Assert.Contains("cells: [\n    {\n        key: \"feltColor\"", formatted);
    }
}
