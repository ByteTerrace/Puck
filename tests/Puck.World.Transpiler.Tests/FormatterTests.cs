using Puck.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class FormatterTests {
    [Fact]
    public void TestFormatterPreservesPlaceholderCollisions() {
        var input = "// __puck_literal_0_0\n"
            + "let text = \"__puck_literal_1_0\"\n"
            + "let `__puck_literal_2_0` = \"__puck_literal_2147483648_0\"\n"
            + "// __puck_literal__puck_literal_3_0\n";
        var formatted = PuckFormatter.Format(input);
        Assert.Equal(input, formatted);
        Assert.Equal(formatted, PuckFormatter.Format(formatted));
    }

    [Fact]
    public void TestFormatterPreservesLongPlaceholderPrefixWithManyLiterals() {
        var literal = "__puck_literal_" + new string('_', 100_000);
        var input = "let longText = \"" + literal + "\"\n"
            + string.Concat(Enumerable.Range(0, 1_000).Select(index => $"let text{index} = \"value{index}\"\n"));
        var formatted = PuckFormatter.Format(input);
        Assert.Equal(input, formatted);
        Assert.Equal(formatted, PuckFormatter.Format(formatted));
    }

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
        Assert.Contains("host {", formatted);
        Assert.Contains("entities [", formatted);
        // 4 spaces indentation
        Assert.Contains("    authority: \"test.host\"", formatted);
        Assert.Contains("    presentation: windowed", formatted);
    }

    [Fact]
    public void TestFormatterPreservesComments() {
        var input = @"// Header comment
puck: 1

/* Host section block comment */
host {
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

host {
    authority: ""test.host""
    presentation: windowed
}

entities [
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
host {
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
cells [
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
        Assert.Contains("cells [\n    {\n        key: \"feltColor\"", formatted);
    }

    // A ternary's colon carries a space on both sides (Puck.State.ExpressionSpelling's own spelling); stripping the
    // leading one splices "x : y" into "x: y", a different, unreadable binding.
    [Fact]
    public void TestFormatterPreservesTernaryColonSpacing() {
        var input = "rule \"r\" {\n"
            + "    bind ownBefore : Int = turn == 0 ? a : b\n"
            + "}\n";
        var formatted = PuckFormatter.Format(input);
        Assert.Contains("bind ownBefore : Int = turn == 0 ? a : b", formatted);
    }

    // A backquoted name (`N,S,E,W`) is one lexeme; its commas are name characters, not statement punctuation, and
    // must not gain the space the comma rule inserts everywhere else.
    [Fact]
    public void TestFormatterPreservesBackquotedNameCommas() {
        var input = "rule \"r\" {\n"
            + "    when `$board:attacks:board:-5:-4:N,S,E,W`[5] != 0\n"
            + "}\n";
        var formatted = PuckFormatter.Format(input);
        Assert.Contains("`$board:attacks:board:-5:-4:N,S,E,W`[5]", formatted);
    }

    [Fact]
    public void TestFormatterPreservesACommentBetweenTwoRules() {
        var input = "rule \"first\" {\n"
            + "    a = 1\n"
            + "}\n"
            + "\n"
            + "// a comment between two rules\n"
            + "rule \"second\" {\n"
            + "    b = 2\n"
            + "}\n";
        var formatted = PuckFormatter.Format(input);
        Assert.Contains("// a comment between two rules", formatted);

        var lines = formatted.Split('\n');
        var commentIndex = Array.FindIndex(lines, line => line.Contains("// a comment between two rules", StringComparison.Ordinal));
        var secondRuleIndex = Array.FindIndex(lines, line => line.Contains("rule \"second\"", StringComparison.Ordinal));
        Assert.True(commentIndex >= 0 && secondRuleIndex > commentIndex);
    }

    // The decompiler's one-time-import header must still be readable as a header after formatting, not folded into
    // whatever follows it.
    [Fact]
    public void TestFormatterKeepsTheLeadingHeaderCommentAsTheFirstLine() {
        var input = "// Decompiled from a canonical Puck world document — a one-time import.\n"
            + "// 'let'/'template' cannot be recovered; re-running the decompiler will not\n"
            + "// preserve hand-authored constants or templates added after this file was\n"
            + "// generated. Treat this file as a starting point, not a synced mirror.\n"
            + "\n"
            + "\n"
            + "puck: 1\n";
        var formatted = PuckFormatter.Format(input);
        var lines = formatted.Split('\n');
        Assert.Equal("// Decompiled from a canonical Puck world document — a one-time import.", lines[0]);
    }
}
