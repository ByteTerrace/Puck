using Xunit;

namespace Puck.World.Transpiler.Tests;

public class FormatterTests {
    /// <summary>A source, what its formatted print must and must not carry; every print is also a fixed point of the
    /// formatter.</summary>
    private sealed record Print(string Input, string[] Printed, string[] NotPrinted);

    private static readonly Dictionary<string, Print> Prints = new(comparer: StringComparer.Ordinal) {
        ["an array of objects does not collapse its braces"] = new(
            Input: "\ncells [\n{\nkey: \"feltColor\"\nvalue: feltColor\n}\n{\nkey: \"activeRound\"\nvalue: 1\n}\n]",
            NotPrinted: ["[ {", "} {"],
            Printed: ["cells [\n  {\n    key: \"feltColor\""]
        ),
        // Two spaces per level by default.
        ["blocks and arrays indent their bodies"] = new(
            Input: "\npuck: 1\nhost {\nauthority: \"test.host\"\npresentation: windowed\n}\nentities [\n{\nname: \"Player\"\ncomponents [\n{ type: \"Transform\" }\n]\n}\n]",
            NotPrinted: [],
            Printed: ["host {", "entities [", "  authority: \"test.host\"", "  presentation: windowed"]
        ),
        ["a colon and a comma each take one space"] = new(
            Input: "puck:1\nhost {\nauthority:\"my-auth\",presentation:headless\n}\n",
            NotPrinted: [],
            Printed: ["puck: 1", "authority: \"my-auth\"", "presentation: headless"]
        ),
        ["uneven indentation settles in one pass"] = new(
            Input: "puck: 1\n\nhost {\n    authority: \"test.host\"\n  presentation: windowed\n}\n\nentities [\n    {\n        name: \"Entity1\"\n    }\n]\n",
            NotPrinted: [],
            Printed: []
        ),
        // A backquoted name is one lexeme; its commas are name characters, not statement punctuation, and must not
        // gain the space the comma rule inserts everywhere else.
        ["a backquoted name keeps its commas"] = new(
            Input: "rule \"r\" {\n    when `$board:attacks:board:-5:-4:N,S,E,W`[5] != 0\n}\n",
            NotPrinted: [],
            Printed: ["`$board:attacks:board:-5:-4:N,S,E,W`[5]"]
        ),
        ["line, block and inline comments survive"] = new(
            Input: "// Header comment\npuck: 1\n\n/* Host section block comment */\nhost {\n    authority: \"test.host\" // inline comment\n}\n",
            NotPrinted: [],
            Printed: ["// Header comment", "/* Host section block comment */", "// inline comment"]
        ),
        // A ternary's colon carries a space on both sides (Puck.State.ExpressionSpelling's own spelling); splicing
        // "x : y" into "x: y" is a different, unreadable binding.
        ["a ternary's colon keeps a space on both sides"] = new(
            Input: "rule \"r\" {\n    local ownBefore = turn == 0 ? a : b\n    score = 1\n}\n",
            NotPrinted: [],
            Printed: ["local ownBefore = turn == 0 ? a : b"]
        ),
    };

    public static TheoryData<string> PrintNames() => new(values: Prints.Keys);
    [MemberData(nameof(PrintNames))]
    [Theory]
    public void ASourceFormatsToItsCanonicalPrint(string name) {
        var print = Prints[name];
        var formatted = PuckFormat.Format(source: print.Input);
        var missing = print.Printed.Where(predicate: text => !formatted.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();
        var present = print.NotPrinted.Where(predicate: text => formatted.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();

        Assert.True(
            condition: ((missing.Length == 0) && (present.Length == 0)),
            userMessage: $"{name}: missing [{string.Join(separator: " | ", values: missing)}], present [{string.Join(separator: " | ", values: present)}]{Environment.NewLine}{formatted}"
        );
        Assert.Equal(
            actual: PuckFormat.Format(source: formatted),
            expected: formatted
        );
    }
    // The decompiler's one-time-import header must still be readable as a header after formatting, not folded into
    // whatever follows it.
    [Fact]
    public void TestFormatterKeepsTheLeadingHeaderCommentAsTheFirstLine() {
        var input = "// Decompiled from a canonical Puck world document — a one-time import.\n// 'let'/'template' cannot be recovered; re-running the decompiler will not\n// preserve hand-authored constants or templates added after this file was\n// generated. Treat this file as a starting point, not a synced mirror.\n\n\npuck: 1\n";
        var formatted = PuckFormat.Format(source: input);
        var lines = formatted.Split('\n');

        Assert.Equal(
            "// Decompiled from a canonical Puck world document — a one-time import.",
            lines[0]
        );
    }
    [Fact]
    public void TestFormatterPreservesACommentBetweenTwoRules() {
        var input = "rule \"first\" {\n    a = 1\n}\n\n// a comment between two rules\nrule \"second\" {\n    b = 2\n}\n";
        var formatted = PuckFormat.Format(source: input);

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
        var formatted = PuckFormat.Format(source: input);

        Assert.Equal(
            actual: formatted,
            expected: input
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(source: formatted)
        );
    }
}
