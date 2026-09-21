using Xunit;

namespace Puck.World.Transpiler.Tests;

public class FormatterTests {
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
        var formatted = PuckFormat.Format(unformatted);

        Assert.DoesNotContain(
            actualString: formatted,
            expectedSubstring: "[ {"
        );
        Assert.DoesNotContain(
            actualString: formatted,
            expectedSubstring: "} {"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "cells [\n  {\n    key: \"feltColor\""
        );
    }
    [Fact]
    public void TestFormatterBasicIndentationAndBraces() {
        var unformatted = @"
puck: 1
host {
authority: ""test.host""
presentation: windowed
}
entities [
{
name: ""Player""
components [
{ type: ""Transform"" }
]
}
]";

        var formatted = PuckFormat.Format(unformatted);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "host {"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "entities ["
        );
        // Default: two spaces per level
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "  authority: \"test.host\""
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "  presentation: windowed"
        );
    }
    [Fact]
    public void TestFormatterColonAndCommaSpacing() {
        var unformatted = @"puck:1
host {
authority:""my-auth"",presentation:headless
}
";
        var formatted = PuckFormat.Format(unformatted);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "puck: 1"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "authority: \"my-auth\""
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "presentation: headless"
        );
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
        var pass1 = PuckFormat.Format(input);
        var pass2 = PuckFormat.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }
    // The decompiler's one-time-import header must still be readable as a header after formatting, not folded into
    // whatever follows it.
    [Fact]
    public void TestFormatterKeepsTheLeadingHeaderCommentAsTheFirstLine() {
        var input = "// Decompiled from a canonical Puck world document — a one-time import.\n// 'let'/'template' cannot be recovered; re-running the decompiler will not\n// preserve hand-authored constants or templates added after this file was\n// generated. Treat this file as a starting point, not a synced mirror.\n\n\npuck: 1\n";
        var formatted = PuckFormat.Format(input);
        var lines = formatted.Split('\n');

        Assert.Equal(
            "// Decompiled from a canonical Puck world document — a one-time import.",
            lines[0]
        );
    }
    [Fact]
    public void TestFormatterPreservesACommentBetweenTwoRules() {
        var input = "rule \"first\" {\n    a = 1\n}\n\n// a comment between two rules\nrule \"second\" {\n    b = 2\n}\n";
        var formatted = PuckFormat.Format(input);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "// a comment between two rules"
        );

        var lines = formatted.Split('\n');
        var commentIndex = Array.FindIndex(
            array: lines,
            match: line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "// a comment between two rules"
            )
        );
        var secondRuleIndex = Array.FindIndex(
            array: lines,
            match: line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "rule \"second\""
            )
        );

        Assert.True(condition: ((commentIndex >= 0) && (secondRuleIndex > commentIndex)));
    }
    // A backquoted name (`N,S,E,W`) is one lexeme; its commas are name characters, not statement punctuation, and
    // must not gain the space the comma rule inserts everywhere else.
    [Fact]
    public void TestFormatterPreservesBackquotedNameCommas() {
        var input = "rule \"r\" {\n    when `$board:attacks:board:-5:-4:N,S,E,W`[5] != 0\n}\n";
        var formatted = PuckFormat.Format(input);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "`$board:attacks:board:-5:-4:N,S,E,W`[5]"
        );
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
        var formatted = PuckFormat.Format(input);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "// Header comment"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "/* Host section block comment */"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "// inline comment"
        );
    }
    [Fact]
    public void TestFormatterPrintsAVeryLongStringLiteralUnchanged() {
        var literal = ("__puck_literal_" + new string(
            c: '_',
            count: 100_000
        ));
        var input = ((("let longText = \"" + literal) + "\"\n")
            + string.Concat(values: Enumerable.Range(
            count: 1_000,
            start: 0
        ).Select(selector: index => $"let text{index} = \"value{index}\"\n")));
        var formatted = PuckFormat.Format(input);

        Assert.Equal(
            actual: formatted,
            expected: input
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(formatted)
        );
    }
    // A ternary's colon carries a space on both sides (Puck.State.ExpressionSpelling's own spelling); splicing
    // "x : y" into "x: y" is a different, unreadable binding.
    [Fact]
    public void TestFormatterPreservesTernaryColonSpacing() {
        var input = "rule \"r\" {\n    local ownBefore = turn == 0 ? a : b\n    score = 1\n}\n";
        var formatted = PuckFormat.Format(input);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "local ownBefore = turn == 0 ? a : b"
        );
    }
}
