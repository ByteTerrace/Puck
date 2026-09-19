using Puck.Transpiler.Ast;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>One escape grammar: the printer writes only what the reader reads back, so formatting a source can
/// never change the value a string carries.</summary>
public class StringGrammarTests {
    // Every character class an escape grammar has to decide about, plus the two fence-shaped hazards.
    public static TheoryData<string> Values() => new(values: [
        "",
        "plain",
        "a\\b",
        "c:\\temp\\x",
        "trailing\\",
        "\\\\",
        "a\"b",
        "\"",
        "a'b",
        "line\nbreak",
        "carriage\rreturn",
        "tab\there",
        "nul\0here",
        "backspace\bhere",
        "bell\ahere",
        "delete\u007Fhere",
        "unit\u001Fhere",
        "café — π",
        "\u00A0nbsp",
        "brace{hole}",
        "double{{brace}}",
        "fence\"\"\"inside",
        "// not a comment",
        "/* not a comment */",
    ]);

    private static string Format(string source) {
        var result = PuckPrinter.Format(
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.True(
            condition: (result.Value is not null),
            userMessage: $"does not parse:{Environment.NewLine}{result.Diagnostics.FormatReport(source)}{Environment.NewLine}{source}"
        );

        return result.Value!;
    }
    private static string ValueOf(string source, string property) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: $"does not compile:{Environment.NewLine}{compilation.Diagnostics.FormatReport(source)}{Environment.NewLine}{source}"
        );

        return compilation.RequireJson()[propertyName: property]!.GetValue<string>();
    }

    // The printer's own spelling of a value is the one the law starts from: whatever an author wrote, the printer
    // has to write something the reader reads back as the same value, and has to keep writing the same thing.
    [MemberData(nameof(Values))]
    [Theory]
    public void APrintedStringReadsBackAsTheValueItCameFrom(string value) {
        var source = PuckPrinter.Print(document: new DocumentNode(
            Basis: null,
            Schema: "puck.world.definition.v1",
            Statements: [
                new PropertyNode(
                    Name: "documentId",
                    Value: new LiteralExpressionNode(Value: value)
                ),
            ]
        ));
        var printed = Format(source: source);

        Assert.Equal(
            actual: printed,
            expected: source
        );
        Assert.Equal(
            actual: ValueOf(
                property: "documentId",
                source: printed
            ),
            expected: value
        );
    }
    [MemberData(nameof(Values))]
    [Theory]
    public void AFencedStringFormatsToTheSameValue(string value) {
        if (!PuckStrings.CanFence(text: value)) {
            return;
        }

        var source = $"schema: \"puck.world.definition.v1\"\ndocumentId: \"\"\"{value}\"\"\"\n";
        var formatted = Format(source: source);

        Assert.Equal(
            actual: formatted,
            expected: source
        );
        Assert.Equal(
            actual: ValueOf(
                property: "documentId",
                source: formatted
            ),
            expected: value
        );
    }
    // An interpolated name carries its holes through the same escaping, so a quote inside a hole's own string
    // argument stays inside the one string rather than closing it, and `{{` still spells one literal brace.
    [InlineData("placement $\"stem-{row[\\\"id\\\"]}-{{literal}}\" {")]
    [InlineData("placement $\"\"\"stem-{row[\"id\"]}-{{literal}}\"\"\" {")]
    [Theory]
    public void AnInterpolatedNameKeepsItsHolesAndItsFence(string header) {
        var source = string.Join(
            separator: "\n",
            values: [
                "schema: \"puck.world.definition.v1\"",
                "let rows = [{ id: \"a\" }]",
                "placements {",
                "  for row in rows {",
                ("    " + header),
                "      prototype: \"stem\"",
                "    }",
                "  }",
                "}",
                "",
            ]
        );

        Assert.Equal(
            actual: Format(source: source),
            expected: source
        );
    }
    [Fact]
    public void AnUnknownEscapeIsRefusedRatherThanGuessedAt() {
        var result = PuckPrinter.Format(
            source: "documentId: \"a\\qb\"\n",
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.Null(@object: result.Value);
        Assert.True(condition: result.Diagnostics.HasErrors);
    }
}
