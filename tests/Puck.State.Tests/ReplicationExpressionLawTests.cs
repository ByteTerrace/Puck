using Xunit;

namespace Puck.State.Tests;

/// <summary>Periodic mask expressions agree with a bit-by-bit oracle and refuse invalid widths and patterns.</summary>
public sealed class ReplicationExpressionLawTests {
    private static long Repeat(long pattern, int width) {
        var expected = 0UL;
        for (var bit = 0; bit < 64; bit++) {
            if (((ulong)pattern & (1UL << (bit % width))) != 0) { expected |= 1UL << bit; }
        }
        return unchecked((long)expected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void EveryWidthReplicatesEachBitAndKeepsTheSignBit(int width) {
        Assert.Equal(Repeat(1, width), ExpressionFunctionLawTests.EvalPublic($"replicationMask({width})"));
        for (var bit = 0; bit < width; bit++) {
            var pattern = 1L << bit;
            Assert.Equal(Repeat(pattern, width), ExpressionFunctionLawTests.EvalPublic($"repeatBits({pattern}, {width})"));
        }
        var full = width == 64 ? -1L : (1L << width) - 1;
        Assert.Equal(-1L, ExpressionFunctionLawTests.EvalPublic($"repeatBits({full}, {width})"));
        Assert.Equal(0L, ExpressionFunctionLawTests.EvalPublic($"repeatBits(0, {width})"));
    }

    [Fact]
    public void EveryBytePatternAndFermatConstructionAgreesWithTheOracle() {
        for (var pattern = 0; pattern < 256; pattern++) {
            Assert.Equal(Repeat(pattern, 8), ExpressionFunctionLawTests.EvalPublic($"repeatBits({pattern}, 8)"));
        }
        Assert.Equal(0x5555555555555555L, ExpressionFunctionLawTests.EvalPublic("repeatBits(1, 2)"));
        Assert.Equal(0x3333333333333333L, ExpressionFunctionLawTests.EvalPublic("repeatBits(3, 4)"));
        Assert.Equal(0x0F0F0F0F0F0F0F0FL, ExpressionFunctionLawTests.EvalPublic("repeatBits(15, 8)"));
    }

    [Fact]
    public void InvalidArgumentsRefuseWithoutNarrowingOrTruncatingThem() {
        long[] widths = [long.MinValue, -1, 0, 3, 7, 9, 31, 63, 65, 128, 4_294_967_304L, long.MaxValue];
        foreach (var width in widths) {
            Assert.False(ExpressionFunctionLawTests.TryEvalPublic($"replicationMask({width})"));
            Assert.False(ExpressionFunctionLawTests.TryEvalPublic($"repeatBits(1, {width})"));
        }
        foreach (var width in new[] { 1, 2, 4, 8, 16, 32 }) {
            Assert.False(ExpressionFunctionLawTests.TryEvalPublic($"repeatBits({1L << width}, {width})"));
            Assert.False(ExpressionFunctionLawTests.TryEvalPublic($"repeatBits(-1, {width})"));
        }
        Assert.False(ExpressionArithmetic.TryUnary(ExpressionOp.ReplicationMask, CellKind.Fixed, 8, out var unary));
        Assert.Equal(0L, unary);
        Assert.False(ExpressionArithmetic.TryBinary(ExpressionOp.RepeatBits, CellKind.Fixed, 1, 8, out var binary));
        Assert.Equal(0L, binary);
    }
}
