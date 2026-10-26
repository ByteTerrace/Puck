using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// The array builtins run while lowering, so the document carries the array they produced and never the call.
public class LambdaBuiltinTests {
    private static long[] Numbers(JsonNode? node) =>
        Assert.IsType<JsonArray>(@object: node).Select(selector: item => item!.GetValue<long>()).ToArray();

    [Fact]
    public void TestALambdaOutsideABuiltinIsRefused() {
        Assert.Contains(
            collection: WorldSources.Diagnose(body: "thing: i => i * 2"),
            filter: diagnostic =>
            (diagnostic.Code == PuckDiagnosticCodes.LambdaOutsideBuiltin)
        );
    }
    [Fact]
    public void TestALambdaParameterShadowsAConstantOfTheSameName() {
        var lowered = WorldSources.LowerClean(body: """
            let i = 100

            _cells: map(range(0, 3), i => i)
            """);

        Assert.Equal(
            [0L, 1L, 2L],
            Numbers(node: lowered["_cells"])
        );
    }
    [Fact]
    public void TestAParenthesizedExpressionIsStillOne() {
        // `(a, b)` is a lambda parameter list only when an arrow follows; otherwise the scan rewinds.
        Assert.Equal(
            7L,
            WorldSources.LowerClean(body: "_total: (3 + 4)")["_total"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestARefusedBuiltinIsNamed() {
        Assert.Contains(
            collection: WorldSources.Diagnose(body: "_cells: map(3, i => i)"),
            filter: diagnostic =>
            ((diagnostic.Code == PuckDiagnosticCodes.BuiltinRefused) && diagnostic.Message.Contains(value: "first argument is an array"))
        );

        Assert.Contains(
            collection: WorldSources.Diagnose(body: "_cells: range(0, -1)"),
            filter: diagnostic =>
            (diagnostic.Code == PuckDiagnosticCodes.BuiltinRefused)
        );
    }
    [Fact]
    public void TestBuiltinsNest() {
        Assert.Equal(
            [0L, 4L],
            Numbers(node: WorldSources.LowerClean(body: "_cells: filter(map(range(0, 4), i => i * 2), v => v % 4 == 0)")["_cells"])
        );
    }
    [Fact]
    public void TestConstantsAreVisibleInsideALambdaBody() {
        var lowered = WorldSources.LowerClean(body: """
            let step = 5

            _cells: map(range(0, 3), i => i * step)
            """);

        Assert.Equal(
            [0L, 5L, 10L],
            Numbers(node: lowered["_cells"])
        );
    }
    [Fact]
    public void TestFilterKeepsTheItemsItsLambdaAccepts() {
        Assert.Equal(
            [0L, 2L, 4L],
            Numbers(node: WorldSources.LowerClean(body: "_cells: filter(range(0, 6), i => i % 2 == 0)")["_cells"])
        );
    }
    [Fact]
    public void TestLengthAndConcat() {
        Assert.Equal(
            4L,
            WorldSources.LowerClean(body: "_count: length(range(0, 4))")["_count"]?.GetValue<long>()
        );
        Assert.Equal(
            [1L, 2L, 3L],
            Numbers(node: WorldSources.LowerClean(body: "_cells: concat([1, 2], [3])")["_cells"])
        );
    }
    [Fact]
    public void TestMapAppliesItsLambdaToEachItem() {
        Assert.Equal(
            [0L, 2L, 4L, 6L],
            Numbers(node: WorldSources.LowerClean(body: "_cells: map(range(0, 4), i => i * 2)")["_cells"])
        );
    }
    [Fact]
    public void TestMapLambdaMayReadTheIndexAsWell() {
        Assert.Equal(
            [10L, 21L, 32L],
            Numbers(node: WorldSources.LowerClean(body: "_cells: map([10, 20, 30], (item, index) => item + index)")["_cells"])
        );
    }
    [Fact]
    public void TestMapMayProduceObjects() {
        var rows = Assert.IsType<JsonArray>(@object: WorldSources.LowerClean(body: """
            _variables: map(range(0, 2), i => { name: "v", initial: i })
            """)["_variables"]);

        Assert.Equal(
            2,
            rows.Count
        );
        Assert.Equal(
            "v",
            Assert.IsType<JsonObject>(@object: rows[0])["name"]?.GetValue<string>()
        );
        Assert.Equal(
            1L,
            Assert.IsType<JsonObject>(@object: rows[1])["initial"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestRangeProducesAWholeNumberSequence() {
        Assert.Equal(
            [3L, 4L, 5L, 6L],
            Numbers(node: WorldSources.LowerClean(body: "_cells: range(3, 4)")["_cells"])
        );
        Assert.Empty(collection: Assert.IsType<JsonArray>(@object: WorldSources.LowerClean(body: "_cells: range(0, 0)")["_cells"]));
    }
    [Fact]
    public void TestReduceFoldsFromItsSeed() {
        Assert.Equal(
            10L,
            WorldSources.LowerClean(body: "_total: reduce(range(1, 4), 0, (running, item) => running + item)")["_total"]?.GetValue<long>()
        );
    }
}
