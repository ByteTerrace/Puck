using System.Numerics;
using Xunit;

namespace Puck.State.Tests;

public sealed class CostBoundLawTests {
    [Fact]
    public void UncertaintyAndOverflowSurviveCompositionExceptProvedZeroRepetition() {
        var unknown = CostBound.Unmodeled("missing kernel");
        Assert.True((unknown + CostBound.Known(5)).IsUnmodeled);
        Assert.True(CostBound.Max(CostBound.Known(long.MaxValue), unknown).IsUnmodeled);
        Assert.True((CostBound.Known(long.MaxValue) + CostBound.Known(1)).IsOverflow);
        Assert.True((CostBound.Known(long.MaxValue) * 2).IsOverflow);
        Assert.Equal(CostBound.Zero, unknown * 0);
        Assert.Equal(CostBound.Zero, CostBound.Overflow * 0);
    }

    [Fact]
    public void MemoryServiceUsesExactArithmeticWithExplicitOverflow() {
        var profile = new MemoryClassProfile(7, 11, 3, 2);
        foreach (var bytes in new[] { 0L, 1L, 3L, 9_007_199_254_740_993L, long.MaxValue }) {
            var quotient = BigInteger.DivRem((BigInteger)bytes * 2, 3, out var remainder);
            var expected = 2 * 7 + 3 * 11 + quotient + (remainder.IsZero ? 0 : 1);
            Assert.Equal((long)expected, profile.Cycles(2, 3, bytes).Cycles);
        }
        var overflow = new MemoryClassProfile(long.MaxValue, 0, 1, 1).Cycles(2, 0, 0);
        Assert.True(overflow.IsOverflow);
        // The multiplication exceeds Int64, but the exact quotient still fits.
        Assert.Equal(long.MaxValue, new MemoryClassProfile(0, 0, 2, 2).Cycles(0, 0, long.MaxValue).Cycles);
        Assert.Throws<ArgumentOutOfRangeException>(() => default(MemoryClassProfile).Cycles(0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.Cycles(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryClassProfile(-1, 0, 1, 1).Cycles(0, 0, 0));
    }

    [Fact]
    public void PortableModelDoesNotInventMemoryCoefficients() {
        foreach (var access in Enum.GetValues<MemoryAccessClass>()) {
            Assert.True(CostModel.MemoryCycles(access, 1, 1, 64).IsUnmodeled);
            Assert.Equal(CostBound.Zero, CostModel.MemoryCycles(access, 0, 0, 0));
        }
        Assert.True(CostModel.MemoryCycles((MemoryAccessClass)byte.MaxValue, 0, 0, 0).IsUnmodeled);
    }
}
