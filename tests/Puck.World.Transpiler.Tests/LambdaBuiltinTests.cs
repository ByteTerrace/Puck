using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// The array builtins run while lowering, so the document carries the array they produced and never the call.
public class LambdaBuiltinTests {
    private static WorldCompilation Compile(string body) =>
        WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: $"schema: \"puck.world.definition.v1\"\n\n{body}"
        );
    private static JsonObject Lower(string body) {
        var compilation = Compile(body: body);

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: string.Join(
                separator: "\n",
                values: compilation.Diagnostics.Select(selector: d => $"{d.Code}: {d.Message}")
            )
        );

        return compilation.RequireJson();
    }
    private static DiagnosticBag LowerForDiagnostics(string body) => Compile(body: body).Diagnostics;
    private static long[] Numbers(JsonNode? node) =>
        Assert.IsType<JsonArray>(@object: node).Select(selector: item => item!.GetValue<long>()).ToArray();

    [Fact]
    public void TestALambdaOutsideABuiltinIsRefused() {
        Assert.Contains(
            collection: LowerForDiagnostics(body: "thing: i => i * 2"),
            filter: diagnostic =>
            (diagnostic.Code == PuckDiagnosticCodes.LambdaOutsideBuiltin)
        );
    }
    [Fact]
    public void TestALambdaParameterShadowsAConstantOfTheSameName() {
        var lowered = Lower(body: """
            let i = 100

            cells: map(range(0, 3), i => i)
            """);

        Assert.Equal(
            [0L, 1L, 2L],
            Numbers(node: lowered["cells"])
        );
    }
    [Fact]
    public void TestAParenthesizedExpressionIsStillOne() {
        // `(a, b)` is a lambda parameter list only when an arrow follows; otherwise the scan rewinds.
        Assert.Equal(
            7L,
            Lower(body: "total: (3 + 4)")["total"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestARefusedBuiltinIsNamed() {
        Assert.Contains(
            collection: LowerForDiagnostics(body: "cells: map(3, i => i)"),
            filter: diagnostic =>
            ((diagnostic.Code == PuckDiagnosticCodes.BuiltinRefused) && diagnostic.Message.Contains(value: "first argument is an array"))
        );

        Assert.Contains(
            collection: LowerForDiagnostics(body: "cells: range(0, -1)"),
            filter: diagnostic =>
            (diagnostic.Code == PuckDiagnosticCodes.BuiltinRefused)
        );
    }
    [Fact]
    public void TestBuiltinsNest() {
        Assert.Equal(
            [0L, 4L],
            Numbers(node: Lower(body: "cells: filter(map(range(0, 4), i => i * 2), v => v % 4 == 0)")["cells"])
        );
    }
    [Fact]
    public void TestConstantsAreVisibleInsideALambdaBody() {
        var lowered = Lower(body: """
            let step = 5

            cells: map(range(0, 3), i => i * step)
            """);

        Assert.Equal(
            [0L, 5L, 10L],
            Numbers(node: lowered["cells"])
        );
    }
    [Fact]
    public void TestFilterKeepsTheItemsItsLambdaAccepts() {
        Assert.Equal(
            [0L, 2L, 4L],
            Numbers(node: Lower(body: "cells: filter(range(0, 6), i => i % 2 == 0)")["cells"])
        );
    }
    [Fact]
    public void TestLengthAndConcat() {
        Assert.Equal(
            4L,
            Lower(body: "count: length(range(0, 4))")["count"]?.GetValue<long>()
        );
        Assert.Equal(
            [1L, 2L, 3L],
            Numbers(node: Lower(body: "cells: concat([1, 2], [3])")["cells"])
        );
    }
    [Fact]
    public void TestMapAppliesItsLambdaToEachItem() {
        Assert.Equal(
            [0L, 2L, 4L, 6L],
            Numbers(node: Lower(body: "cells: map(range(0, 4), i => i * 2)")["cells"])
        );
    }
    [Fact]
    public void TestMapLambdaMayReadTheIndexAsWell() {
        Assert.Equal(
            [10L, 21L, 32L],
            Numbers(node: Lower(body: "cells: map([10, 20, 30], (item, index) => item + index)")["cells"])
        );
    }
    [Fact]
    public void TestMapMayProduceObjects() {
        var rows = Assert.IsType<JsonArray>(@object: Lower(body: """
            variables: map(range(0, 2), i => { name: "v", initial: i })
            """)["variables"]);

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
            Numbers(node: Lower(body: "cells: range(3, 4)")["cells"])
        );
        Assert.Empty(collection: Assert.IsType<JsonArray>(@object: Lower(body: "cells: range(0, 0)")["cells"]));
    }
    [Fact]
    public void TestReduceFoldsFromItsSeed() {
        Assert.Equal(
            10L,
            Lower(body: "total: reduce(range(1, 4), 0, (running, item) => running + item)")["total"]?.GetValue<long>()
        );
    }
}
