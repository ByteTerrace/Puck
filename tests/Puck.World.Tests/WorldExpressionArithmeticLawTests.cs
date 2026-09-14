using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldExpressionArithmeticLawTests {
    private static readonly CellKind[] Kinds = [CellKind.Int, CellKind.Fixed];
    private static readonly long[] Values = [long.MinValue, (long.MinValue + 1), (long.MinValue / 2),
        -65537, -65536, -65535, -32769, -32768, -3, -2, -1, 0, 1, 2, 3, 32767, 32768, 32769,
        65535, 65536, 65537, (long.MaxValue / 2), (long.MaxValue - 1), long.MaxValue];
    private static readonly ExpressionOp[] Operations = [ExpressionOp.Add, ExpressionOp.Subtract,
        ExpressionOp.Multiply, ExpressionOp.Divide, ExpressionOp.Minimum, ExpressionOp.Maximum];

    [InlineData(CellKind.Int)]
    [InlineData(CellKind.Fixed)]
    [Theory]
    public void NonthrowingEvaluationPreservesCheckedSemanticsAtSignedAndRoundingBoundaries(CellKind kind) {
        foreach (var operation in Operations) {
            foreach (var left in Values) {
                foreach (var right in Values) {
                    var expected = CheckedOracle(
                        kind: kind,
                        left: left,
                        op: operation,
                        right: right,
                        value: out var expectedValue
                    );

                    Assert.Equal(
                        expected,
                        ExpressionArithmetic.TryBinary(
                            kind: kind,
                            left: left,
                            operation: operation,
                            right: right,
                            value: out var actual
                        )
                    );
                    Assert.Equal(
                        actual: actual,
                        expected: expectedValue
                    );
                }
            }
        }
    }

    private static bool CheckedOracle(ExpressionOp op, CellKind kind, long left, long right, out long value) {
        try {
            var a = FixedQ4816.FromRawBits(value: left); var b = FixedQ4816.FromRawBits(value: right);

            value = op switch {
                ExpressionOp.Add => checked((left + right)),
                ExpressionOp.Subtract => checked((left - right)),
                ExpressionOp.Multiply => ((kind == CellKind.Int)
                ? checked((left * right))
                : checked((a * b)).Value),
                ExpressionOp.Divide => ((kind == CellKind.Int)
                ? checked((left / right))
                : checked((a / b)).Value),
                ExpressionOp.Minimum => Math.Min(
                val1: left,
                val2: right
            ),
                ExpressionOp.Maximum => Math.Max(
                val1: left,
                val2: right
            ),
                _ => throw new InvalidOperationException(),
            };
            return true;
        } catch (ArithmeticException) { value = 0; return false; }
    }

    [Fact]
    public void SuccessAndRefusalPathsAllocateNothingIncludingOverflowAndZeroDivision() {
        static long Run() {
            var accepted = 0L;

            foreach (var kind in Kinds) {
                foreach (var operation in Operations) {
                    foreach (var left in Values) {
                        foreach (var right in Values) {
                            if (ExpressionArithmetic.TryBinary(
                                kind: kind,
                                left: left,
                                operation: operation,
                                right: right,
                                value: out _
                            )) { accepted++; }
                        }
                    }
                }
            }
            return accepted;
        }
        var expected = Run();

        for (var index = 0; (index < 8); index++) { Assert.Equal(
            expected,
            Run()
        ); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = Run();
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(
            actual: actual,
            expected: expected
        );
        Assert.Equal(
            actual: bytes,
            expected: 0
        );
    }
    [Fact]
    public void UnsupportedKindsAndOperationsRefuseWithZeroResult() {
        Assert.False(condition: ExpressionArithmetic.TryBinary(
            kind: CellKind.Bool,
            left: 1,
            operation: ExpressionOp.Add,
            right: 1,
            value: out var value
        ));
        Assert.Equal(
            actual: value,
            expected: 0
        );
        Assert.False(condition: ExpressionArithmetic.TryBinary(
            kind: CellKind.Fixed,
            left: 1,
            operation: ExpressionOp.Clamp,
            right: 1,
            value: out value
        ));
        Assert.Equal(
            actual: value,
            expected: 0
        );
    }
}
