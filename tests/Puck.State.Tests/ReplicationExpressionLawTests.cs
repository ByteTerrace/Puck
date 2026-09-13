using Xunit;

namespace Puck.State.Tests;

/// <summary>Periodic mask expressions agree with a bit-by-bit oracle and refuse invalid widths and patterns.</summary>
public sealed class ReplicationExpressionLawTests {
    private static long Repeat(long pattern, int width) {
        var expected = 0UL;

        for (var bit = 0; (bit < 64); bit++) {
            if ((((ulong)pattern) & (1UL << (bit % width))) != 0) { expected |= (1UL << bit); }
        }
        return unchecked((long)expected);
    }

    [Fact]
    public void EveryBytePatternAndFermatConstructionAgreesWithTheOracle() {
        for (var pattern = 0; (pattern < 256); pattern++) {
            Assert.Equal(
                Repeat(
                    pattern: pattern,
                    width: 8
                ),
                ExpressionFunctionLawTests.EvalPublic(text: $"repeatBits({pattern}, 8)")
            );
        }
        Assert.Equal(
            0x5555555555555555L,
            ExpressionFunctionLawTests.EvalPublic(text: "repeatBits(1, 2)")
        );
        Assert.Equal(
            0x3333333333333333L,
            ExpressionFunctionLawTests.EvalPublic(text: "repeatBits(3, 4)")
        );
        Assert.Equal(
            0x0F0F0F0F0F0F0F0FL,
            ExpressionFunctionLawTests.EvalPublic(text: "repeatBits(15, 8)")
        );
    }
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [Theory]
    public void EveryWidthReplicatesEachBitAndKeepsTheSignBit(int width) {
        Assert.Equal(
            Repeat(
                pattern: 1,
                width: width
            ),
            ExpressionFunctionLawTests.EvalPublic(text: $"replicationMask({width})")
        );
        for (var bit = 0; (bit < width); bit++) {
            var pattern = (1L << bit);

            Assert.Equal(
                Repeat(
                    pattern: pattern,
                    width: width
                ),
                ExpressionFunctionLawTests.EvalPublic(text: $"repeatBits({pattern}, {width})")
            );
        }
        var full = ((width == 64)
            ? -1L
            : ((1L << width) - 1)
        );

        Assert.Equal(
            -1L,
            ExpressionFunctionLawTests.EvalPublic(text: $"repeatBits({full}, {width})")
        );
        Assert.Equal(
            0L,
            ExpressionFunctionLawTests.EvalPublic(text: $"repeatBits(0, {width})")
        );
    }
    [Fact]
    public void InvalidArgumentsRefuseWithoutNarrowingOrTruncatingThem() {
        long[] widths = [long.MinValue, -1, 0, 3, 7, 9, 31, 63, 65, 128, 4_294_967_304L, long.MaxValue];

        foreach (var width in widths) {
            Assert.False(condition: ExpressionFunctionLawTests.TryEvalPublic(text: $"replicationMask({width})"));
            Assert.False(condition: ExpressionFunctionLawTests.TryEvalPublic(text: $"repeatBits(1, {width})"));
        }
        foreach (var width in new[] { 1, 2, 4, 8, 16, 32 }) {
            Assert.False(condition: ExpressionFunctionLawTests.TryEvalPublic(text: $"repeatBits({(1L << width)}, {width})"));
            Assert.False(condition: ExpressionFunctionLawTests.TryEvalPublic(text: $"repeatBits(-1, {width})"));
        }
        Assert.False(condition: ExpressionArithmetic.TryUnary(
            kind: CellKind.Fixed,
            operand: 8,
            operation: ExpressionOp.ReplicationMask,
            value: out var unary
        ));
        Assert.Equal(
            actual: unary,
            expected: 0L
        );
        Assert.False(condition: ExpressionArithmetic.TryBinary(
            kind: CellKind.Fixed,
            left: 1,
            operation: ExpressionOp.RepeatBits,
            right: 8,
            value: out var binary
        ));
        Assert.Equal(
            actual: binary,
            expected: 0L
        );
    }
}
