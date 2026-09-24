using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A string indexes like the array of its UTF-16 code units, which is the unit length(value) counts, so a map
// authored as rows of characters reads cell by cell at compile time.
public class StringIndexTests {
    private static WorldCompilation Compile(string body) =>
        WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: $"schema: \"puck.world.definition.v1\"\n\n{body}"
        );

    [Fact]
    public void AStringIndexYieldsTheOneCharacterStringAtThatPosition() {
        var compilation = Compile(body: """
            let row = "b.R=Y"

            cells: map(range(0, length(row)), i => row[i])
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport());
        Assert.Equal(
            expected: ["b", ".", "R", "=", "Y"],
            actual: Assert.IsType<JsonArray>(@object: compilation.RequireJson()["cells"]).Select(selector: item => item!.GetValue<string>())
        );
    }
    [Fact]
    public void TwoStringsCompareEqualByTheirCodeUnits() {
        var compilation = Compile(body: """
            let row = "b.R"

            cells: filter(map(range(0, length(row)), i => row[i]), glyph => glyph != ".")
            same: row == "b.R"
            other: row == "b.r"
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport());
        var json = compilation.RequireJson();

        Assert.Equal(
            expected: ["b", "R"],
            actual: Assert.IsType<JsonArray>(@object: json["cells"]).Select(selector: item => item!.GetValue<string>())
        );
        Assert.Equal(expected: 1L, actual: json["same"]!.GetValue<long>());
        Assert.Equal(expected: 0L, actual: json["other"]!.GetValue<long>());
    }
    [Fact]
    public void AStringHasNoOrder() {
        Assert.Contains(
            collection: Compile(body: "less: \"a\" < \"b\"").Diagnostics,
            filter: item => (item.Code == PuckDiagnosticCodes.InvalidValue)
        );
    }
    [InlineData("cells: \"abc\"[3]", "outside the string's 0..2")]
    [InlineData("cells: \"abc\"[-1]", "outside the string's 0..2")]
    [InlineData("cells: \"abc\"[\"x\"]", "a string index is a whole number")]
    [InlineData("cells: 4[0]", "only an array, an object or a string can be indexed")]
    [Theory]
    public void AnIndexAStringCannotAnswerIsRefusedByName(string body, string refusal) {
        var diagnostic = Assert.Single(collection: Compile(body: body).Diagnostics, predicate: item => (item.Code == PuckDiagnosticCodes.IndexRefused));

        Assert.Contains(expectedSubstring: refusal, actualString: diagnostic.Message, comparisonType: StringComparison.Ordinal);
    }
}
