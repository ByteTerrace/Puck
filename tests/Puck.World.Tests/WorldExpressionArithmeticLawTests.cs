using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldExpressionArithmeticLawTests {
    private static readonly CellKind[] s_kinds = [CellKind.Int, CellKind.Fixed];
    private static readonly long[] s_values = [long.MinValue, long.MinValue + 1, long.MinValue / 2,
        -65537, -65536, -65535, -32769, -32768, -3, -2, -1, 0, 1, 2, 3, 32767, 32768, 32769,
        65535, 65536, 65537, long.MaxValue / 2, long.MaxValue - 1, long.MaxValue];
    private static readonly ExpressionOp[] s_operations = [ExpressionOp.Add, ExpressionOp.Subtract,
        ExpressionOp.Multiply, ExpressionOp.Divide, ExpressionOp.Minimum, ExpressionOp.Maximum];

    [Theory]
    [InlineData(CellKind.Int)]
    [InlineData(CellKind.Fixed)]
    public void NonthrowingEvaluationPreservesCheckedSemanticsAtSignedAndRoundingBoundaries(CellKind kind) {
        foreach (var operation in s_operations) {
            foreach (var left in s_values) {
                foreach (var right in s_values) {
                    var expected = CheckedOracle(operation, kind, left, right, out var expectedValue);
                    Assert.Equal(expected, ExpressionArithmetic.TryBinary(operation, kind, left, right, out var actual));
                    Assert.Equal(expectedValue, actual);
                }
            }
        }
    }

    private static bool CheckedOracle(ExpressionOp op, CellKind kind, long left, long right, out long value) {
        try {
            var a = FixedQ4816.FromRawBits(left); var b = FixedQ4816.FromRawBits(right);
            value = op switch {
                ExpressionOp.Add => checked(left + right),
                ExpressionOp.Subtract => checked(left - right),
                ExpressionOp.Multiply => kind == CellKind.Int ? checked(left * right) : checked(a * b).Value,
                ExpressionOp.Divide => kind == CellKind.Int ? checked(left / right) : checked(a / b).Value,
                ExpressionOp.Minimum => Math.Min(left, right),
                ExpressionOp.Maximum => Math.Max(left, right),
                _ => throw new InvalidOperationException(),
            };
            return true;
        } catch (ArithmeticException) { value = 0; return false; }
    }

    [Fact]
    public void SuccessAndRefusalPathsAllocateNothingIncludingOverflowAndZeroDivision() {
        static long Run() {
            var accepted = 0L;
            foreach (var kind in s_kinds) {
                foreach (var operation in s_operations) {
                    foreach (var left in s_values) {
                        foreach (var right in s_values) {
                            if (ExpressionArithmetic.TryBinary(operation, kind, left, right, out _)) { accepted++; }
                        }
                    }
                }
            }
            return accepted;
        }
        var expected = Run();
        for (var index = 0; index < 8; index++) { Assert.Equal(expected, Run()); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = Run();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void UnsupportedKindsAndOperationsRefuseWithZeroResult() {
        Assert.False(ExpressionArithmetic.TryBinary(ExpressionOp.Add, CellKind.Bool, 1, 1, out var value));
        Assert.Equal(0, value);
        Assert.False(ExpressionArithmetic.TryBinary(ExpressionOp.Clamp, CellKind.Fixed, 1, 1, out value));
        Assert.Equal(0, value);
    }
}
