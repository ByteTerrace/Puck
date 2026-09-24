using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Editing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The range laws: <see cref="LspJson.Range(SourceSpan, string?)"/> ends a span where its last character is
/// followed, on the line it ends on, and every range the language server answers with is built there.</summary>
public class LspJsonLawTests {
    private const string RuleSource = "rule \"r\" {\n    hp += 1\n}\n";

    private static (int Line, int Character) Read(JsonNode? position) => ((((int?)position?["line"]) ?? -1), (((int?)position?["character"]) ?? -1));

    [Fact]
    public void AMultiLineSpanEndsOnTheLineItEndsOn() {
        var range = LspJson.Range(
            source: RuleSource,
            span: new SourceSpan(Column: 1, Length: (RuleSource.IndexOf(value: '}') + 1), Line: 1, Offset: 0)
        );

        Assert.Equal(actual: Read(position: range["start"]), expected: (0, 0));
        Assert.Equal(actual: Read(position: range["end"]), expected: (2, 1));
    }
    [Fact]
    public void ASpanOnOneLineEndsItsLengthAlongIt() {
        var offset = RuleSource.IndexOf(value: 'h');
        var range = LspJson.Range(
            source: RuleSource,
            span: new SourceSpan(Column: 5, Length: 7, Line: 2, Offset: offset)
        );

        Assert.Equal(actual: Read(position: range["start"]), expected: (1, 4));
        Assert.Equal(actual: Read(position: range["end"]), expected: (1, 11));
    }
    [Fact]
    public void ASpanWithoutItsTextOrWithoutLengthCoversItsStartLine() {
        var span = new SourceSpan(Column: 1, Length: RuleSource.Length, Line: 1, Offset: 0);

        Assert.Equal(actual: Read(position: LspJson.Range(source: null, span: span)["end"]), expected: (0, RuleSource.Length));
        Assert.Equal(actual: Read(position: LspJson.Range(source: RuleSource, span: (span with { Length = 0 }))["end"]), expected: (0, 1));
    }
    [Fact]
    public void OffsetAndPositionAreInverse() {
        for (var offset = 0; (offset <= RuleSource.Length); offset++) {
            var (line, character) = LspJson.PositionOf(offset: offset, source: RuleSource);

            Assert.Equal(actual: LspJson.Offset(character: character, line: line, source: RuleSource), expected: offset);
        }
    }
    [Fact]
    public async Task ADocumentSymbolSpansItsWholeConstruct() {
        var symbols = Assert.IsType<JsonArray>(@object: await LanguageServerClient.DocumentRequestAsync(
            method: "textDocument/documentSymbol",
            text: RuleSource
        ));
        var rule = Assert.IsType<JsonObject>(@object: Assert.Single(collection: symbols));

        Assert.Equal(actual: Read(position: rule["range"]?["start"]), expected: (0, 0));
        Assert.Equal(actual: Read(position: rule["range"]?["end"]), expected: (2, 1));
    }
}
