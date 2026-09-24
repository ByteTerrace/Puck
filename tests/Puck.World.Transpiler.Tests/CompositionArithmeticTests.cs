using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Composition arguments preserve vector arithmetic and validate their declared geometric kinds before
/// a module expands.</summary>
public sealed class CompositionArithmeticTests {
    [Fact]
    public void PointAndAngleArgumentsAcceptUnitsAndPreserveExactVectorArithmetic() {
        var compilation = WorldCompiler.Compile(
            allowMultiple: true,
            cancellationToken: TestContext.Current.CancellationToken,
            defaultSchema: "puck.world.definition.v1",
            imports: ImportHandling.Ignore,
            source: """
                module region(origin: Point, footprint: Point, heading: Angle) {
                  ground floor { center: origin size: footprint }
                  spawn arrival { at: origin yaw: heading }
                }
                world sample = region(
                  origin: [1m, 2m, 3m] + [4m, 5m, 6m],
                  footprint: [10m, 12m] - [2m, 4m],
                  heading: 90deg
                )
                """
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport());
        var world = Assert.Single(collection: compilation.Worlds).Json;
        var spawn = world["spawnPoints"]?[0];
        var shape = world["prototypes"]?[0]?["document"]?["shapes"]?[0];

        Assert.Equal(expected: [5m, 7m, 9m], actual: spawn?["position"]?.AsArray().Select(selector: Exact));
        Assert.Equal(expected: 90m, actual: Exact(value: spawn?["yawDegrees"]));
        Assert.Equal(expected: 4m, actual: Exact(value: shape?["scale"]?[0]));
        Assert.Equal(expected: 4m, actual: Exact(value: shape?["scale"]?[2]));
    }

    private static decimal Exact(JsonNode? value) {
        Assert.True(condition: DocumentNumbers.TryExact(node: value, number: out var number));
        return number;
    }

    [InlineData("[1m, 2m] + [3m, 4m, 5m]")]
    [InlineData("[1m, 2m, 3m] - [4m, 5m]")]
    [Theory]
    public void PointArgumentsRefuseMismatchedVectorLengths(string expression) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: $$"""
                module region(origin: Point) { spawn arrival { at: origin } }
                use region(origin: {{expression}})
                """
        );

        Assert.Contains(
            collection: compilation.Diagnostics,
            filter: static diagnostic => (
                (diagnostic.Severity == DiagnosticSeverity.Error) &&
                (diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "needs numbers") ||
                    diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "must be a Point"))
            )
        );
        Assert.False(condition: compilation.Success);
    }
}
