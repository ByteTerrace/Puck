using System.Reflection;

using BenchmarkDotNet.Attributes;
using Xunit;

using Puck.Cli.Bench;
using Puck.State;

namespace Puck.Cli.Tests;

public sealed class StateKernelMatrixLawTests {
    private static readonly int[] Breakpoints = [0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256];

    private static void AssertBreakpoints(IReadOnlyList<int> capacities, int maximum) {
        Assert.Contains(
            collection: capacities,
            expected: 0
        );
        Assert.Contains(
            collection: capacities,
            expected: 1
        );
        Assert.Equal(
            maximum,
            capacities[^1]
        );
        for (var power = 2; (power <= maximum); power *= 2) {
            foreach (var capacity in new[] { (power - 1), power, (power + 1) }) {
                if (capacity <= maximum) {
                    Assert.Contains(
                        collection: capacities,
                        expected: capacity
                    );
                }
            }
        }
    }
    private static IReadOnlyList<object?> Values<T>(string property) {
        var member = (typeof(T).GetProperty(name: property)
            ?? throw new InvalidOperationException(message: $"{typeof(T).Name} declares no scenario parameter '{property}'."));

        return (member.GetCustomAttribute<ParamsAttribute>()?.Values
            ?? throw new InvalidOperationException(message: $"{typeof(T).Name}.{property} is not a benchmark scenario parameter."));
    }

    [Fact]
    public void TheArithmeticMatrixCrossesKindOperandWidthAndOutcome() {
        Assert.Equal(
            [KernelKind.Int, KernelKind.Fixed],
            Values<StateArithmeticKernels>(property: nameof(StateArithmeticKernels.Kind))
        );
        Assert.Equal(
            [KernelOperands.Small, KernelOperands.Wide],
            Values<StateArithmeticKernels>(property: nameof(StateArithmeticKernels.OperandWidth))
        );
        Assert.Equal(
            [KernelOutcome.Successful, KernelOutcome.Refused],
            Values<StateArithmeticKernels>(property: nameof(StateArithmeticKernels.Outcome))
        );
        foreach (var entry in new[] { "BinaryAdd", "BinaryDivide", "BinaryMultiply", "BinaryParallelBitExtract", "BinaryShiftLeft", "BitField", "BitInsert", "UnaryAbs", "UnaryReverseBits", "UnaryNegate", "UnaryPopCount" }) {
            Assert.NotNull(@object: typeof(StateArithmeticKernels).GetMethod(name: entry)?.GetCustomAttribute<BenchmarkAttribute>());
        }
    }
    [Fact]
    public void TheExpressionMatrixCoversEveryCapacityBreakpointAndBothReadShapes() {
        Assert.Equal(
            Breakpoints.Cast<object?>(),
            Values<StateExpressionKernels>(property: nameof(StateExpressionKernels.Tokens))
        );
        Assert.Equal(
            [KernelRead.Literal, KernelRead.Direct, KernelRead.Indirect],
            Values<StateExpressionKernels>(property: nameof(StateExpressionKernels.Read))
        );
        Assert.Equal(
            [KernelShape.DependentChain, KernelShape.IndependentEvaluations],
            Values<StateExpressionKernels>(property: nameof(StateExpressionKernels.Shape))
        );
        AssertBreakpoints(
            capacities: Breakpoints,
            maximum: RuleCapacity.MaxExpressionTokens
        );
    }
    [Fact]
    public void TheBulkMatrixCoversZeroOneEveryPowerOfTwoAndItsNeighboursToTheRowMaximum() => AssertBreakpoints(
        capacities: Values<StateBulkKernels>(property: nameof(StateBulkKernels.Capacity))
            .Select(selector: value => ((int)value!))
            .ToList(),
        maximum: StateCapacity.MaxRows
    );
    [Fact]
    public void EveryExpressionScenarioBuildsAProgramOfItsOwnTokenCountAndEvaluates() {
        var reader = new StateKernelReader(cells: StateKernelReader.CellCount);

        foreach (var tokens in Breakpoints) {
            foreach (var read in Enum.GetValues<KernelRead>()) {
                foreach (var shape in Enum.GetValues<KernelShape>()) {
                    var program = StateKernelPrograms.Build(
                        read: read,
                        reader: reader,
                        shape: shape,
                        tokens: tokens
                    );

                    Assert.Equal(
                        tokens,
                        program.Length
                    );
                    Assert.Equal(
                        (tokens > 0),
                        Puck.State.Rules.RuleExpressions.TryEvaluate(
                            fault: out _,
                            kind: CellKind.Int,
                            program: program,
                            reader: reader,
                            value: out _
                        )
                    );
                }
            }
        }
    }
}
