using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The <c>parameter pass.member = value</c> statement against a graph row's <c>parameters</c>: it lowers to
/// the row's member, the formatter and the decompiler print it back, and it is the row's one spelling (PUCK120).</summary>
public sealed class GraphParameterStatementTests {
    private const string Graph = """
        let size = 4

        views {
          graph "board" {
            source: "board.graph.json"
            parameter draw.tiles = "state.tiles"
            parameter draw.width = size * 2
            parameter "tone-map".exposure = 0.5
          }
        }
        """;

    [Fact]
    public void AStatementLowersToItsRowsParametersMember() {
        var document = WorldSources.LowerClean(body: Graph);
        var parameters = Assert.IsType<JsonObject>(@object: document["views"]!["graphs"]![0]!["parameters"]);

        Assert.Equal(expected: "state.tiles", actual: parameters["draw"]!["tiles"]!.GetValue<string>());
        Assert.Equal(expected: 8L, actual: parameters["draw"]!["width"]!.GetValue<long>());
        Assert.Equal(expected: 0.5m, actual: parameters["tone-map"]!["exposure"]!.GetValue<decimal>());
    }
    [Fact]
    public void TheFormatterPrintsTheStatementAsWritten() {
        var document = WorldSources.ParseClean(body: Graph);
        var parameters = document.Statements.OfType<BlockNode>().Single().Statements.OfType<BlockNode>().Single().Statements.OfType<GraphParameterNode>().ToArray();

        Assert.Equal(expected: 3, actual: parameters.Length);
        Assert.True(condition: parameters[2].PassQuoted);
        Assert.Contains(actualString: PuckPrinter.Print(document: document), comparisonType: StringComparison.Ordinal, expectedSubstring: "parameter \"tone-map\".exposure = 0.5");
        Assert.Contains(actualString: PuckPrinter.Print(document: document), comparisonType: StringComparison.Ordinal, expectedSubstring: "parameter draw.width = size * 2");
    }
    [Fact]
    public void TheDecompilerPrintsStatementsThatRecompileToTheSameRow() {
        var document = WorldSources.LowerClean(body: Graph);
        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "parameter draw.tiles = \"state.tiles\"");
        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "parameter \"tone-map\".exposure = 0.5");
        Assert.DoesNotContain(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "parameters {");
        Assert.Equal(expected: document.ToJsonString(), actual: WorldSources.LowerSourceClean(source: printed).ToJsonString());
    }
    [InlineData("views {\n  graph \"board\" {\n    parameters {\n      draw {\n        width: 1\n      }\n    }\n  }\n}\n", "parameters {")]
    [InlineData("views {\n  graph \"board\" {\n    parameter draw.width = 1\n    parameter draw.width = 2\n  }\n}\n", "parameter draw.width = 2")]
    [InlineData("views {\n  layout \"main\" {\n    parameter draw.width = 1\n  }\n}\n", "parameter draw.width = 1")]
    [Theory]
    public void EveryOtherSpellingIsRefusedAtItsLine(string body, string needle) {
        var (_, diagnostics) = WorldSources.Lower(body: body);
        var refusal = Assert.Single(collection: diagnostics, predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.GraphParameter));
        var line = ((WorldSources.Header + body).Split(separator: '\n').ToList().FindIndex(match: text => text.Contains(comparisonType: StringComparison.Ordinal, value: needle)) + 1);

        Assert.Equal(expected: line, actual: refusal.Span.Line);
    }
}
