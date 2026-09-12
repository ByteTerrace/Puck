using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// The array builtins run while lowering, so the document carries the array they produced and never the call.
public class LambdaBuiltinTests {
    private static JsonObject Lower(string body) {
        var diagnostics = new DiagnosticBag();
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
            PuckParser.ParseDocument($"schema: \"puck.world.def.v1\"\n\n{body}"),
            diagnostics: diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        return lowered.Value!;
    }

    private static DiagnosticBag LowerForDiagnostics(string body) {
        var diagnostics = new DiagnosticBag();

        WorldDocumentEmitter.LowerWithDiagnostics(
            PuckParser.ParseDocument($"schema: \"puck.world.def.v1\"\n\n{body}"),
            diagnostics: diagnostics);

        return diagnostics;
    }

    private static long[] Numbers(JsonNode? node) =>
        Assert.IsType<JsonArray>(node).Select(item => item!.GetValue<long>()).ToArray();

    [Fact]
    public void TestRangeProducesAWholeNumberSequence() {
        Assert.Equal([3L, 4L, 5L, 6L], Numbers(Lower("cells: range(3, 4)")["cells"]));
        Assert.Empty(Assert.IsType<JsonArray>(Lower("cells: range(0, 0)")["cells"]));
    }

    [Fact]
    public void TestMapAppliesItsLambdaToEachItem() {
        Assert.Equal([0L, 2L, 4L, 6L], Numbers(Lower("cells: map(range(0, 4), i => i * 2)")["cells"]));
    }

    [Fact]
    public void TestMapLambdaMayReadTheIndexAsWell() {
        Assert.Equal([10L, 21L, 32L], Numbers(Lower("cells: map([10, 20, 30], (item, index) => item + index)")["cells"]));
    }

    [Fact]
    public void TestFilterKeepsTheItemsItsLambdaAccepts() {
        Assert.Equal([0L, 2L, 4L], Numbers(Lower("cells: filter(range(0, 6), i => i % 2 == 0)")["cells"]));
    }

    [Fact]
    public void TestReduceFoldsFromItsSeed() {
        Assert.Equal(10L, Lower("total: reduce(range(1, 4), 0, (running, item) => running + item)")["total"]?.GetValue<long>());
    }

    [Fact]
    public void TestLengthAndConcat() {
        Assert.Equal(4L, Lower("count: length(range(0, 4))")["count"]?.GetValue<long>());
        Assert.Equal([1L, 2L, 3L], Numbers(Lower("cells: concat([1, 2], [3])")["cells"]));
    }

    [Fact]
    public void TestALambdaParameterShadowsAConstantOfTheSameName() {
        var lowered = Lower("""
            let i = 100

            cells: map(range(0, 3), i => i)
            """);

        Assert.Equal([0L, 1L, 2L], Numbers(lowered["cells"]));
    }

    [Fact]
    public void TestConstantsAreVisibleInsideALambdaBody() {
        var lowered = Lower("""
            let step = 5

            cells: map(range(0, 3), i => i * step)
            """);

        Assert.Equal([0L, 5L, 10L], Numbers(lowered["cells"]));
    }

    [Fact]
    public void TestMapMayProduceObjects() {
        var rows = Assert.IsType<JsonArray>(Lower("""
            variables: map(range(0, 2), i => { name: "v", initial: i })
            """)["variables"]);

        Assert.Equal(2, rows.Count);
        Assert.Equal("v", Assert.IsType<JsonObject>(rows[0])["name"]?.GetValue<string>());
        Assert.Equal(1L, Assert.IsType<JsonObject>(rows[1])["initial"]?.GetValue<long>());
    }

    [Fact]
    public void TestBuiltinsNest() {
        Assert.Equal([0L, 4L], Numbers(Lower("cells: filter(map(range(0, 4), i => i * 2), v => v % 4 == 0)")["cells"]));
    }

    [Fact]
    public void TestARefusedBuiltinIsNamed() {
        Assert.Contains(LowerForDiagnostics("cells: map(3, i => i)"), diagnostic =>
            (diagnostic.Code == PuckDiagnosticCodes.BuiltinRefused) && diagnostic.Message.Contains("first argument is an array"));

        Assert.Contains(LowerForDiagnostics("cells: range(0, -1)"), diagnostic =>
            diagnostic.Code == PuckDiagnosticCodes.BuiltinRefused);
    }

    [Fact]
    public void TestALambdaOutsideABuiltinIsRefused() {
        Assert.Contains(LowerForDiagnostics("thing: i => i * 2"), diagnostic =>
            diagnostic.Code == PuckDiagnosticCodes.LambdaOutsideBuiltin);
    }

    [Fact]
    public void TestAParenthesizedExpressionIsStillOne() {
        // `(a, b)` is a lambda parameter list only when an arrow follows; otherwise the scan rewinds.
        Assert.Equal(7L, Lower("total: (3 + 4)")["total"]?.GetValue<long>());
    }
}
