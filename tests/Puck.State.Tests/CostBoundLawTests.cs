using System.Numerics;
using Xunit;

using Puck.Maths;

namespace Puck.State.Tests;

public sealed class CostBoundLawTests {
    [Fact]
    public void MemoryServiceUsesExactArithmeticWithExplicitOverflow() {
        var profile = new MemoryClassProfile(
            AdditionalLatencyCycles: 11,
            BandwidthDenominator: 2,
            BandwidthNumerator: 3,
            StartupCycles: 7
        );

        foreach (var bytes in new[] { 0L, 1L, 3L, 9_007_199_254_740_993L, long.MaxValue }) {
            var quotient = BigInteger.DivRem(
                dividend: (((BigInteger)bytes) * 2),
                divisor: 3,
                remainder: out var remainder
            );
            var expected = ((((2 * 7) + (3 * 11)) + quotient) + (remainder.IsZero
                ? 0
                : 1));

            Assert.Equal(
                ((long)expected),
                profile.Cycles(
                    bytesTransferred: bytes,
                    dependentAccesses: 3,
                    starts: 2
                ).Cycles
            );
        }
        var overflow = new MemoryClassProfile(
            AdditionalLatencyCycles: 0,
            BandwidthDenominator: 1,
            BandwidthNumerator: 1,
            StartupCycles: long.MaxValue
        ).Cycles(
            bytesTransferred: 0,
            dependentAccesses: 0,
            starts: 2
        );

        Assert.True(condition: overflow.IsOverflow);
        // The multiplication exceeds Int64, but the exact quotient still fits.
        Assert.Equal(
            long.MaxValue,
            new MemoryClassProfile(
                AdditionalLatencyCycles: 0,
                BandwidthDenominator: 2,
                BandwidthNumerator: 2,
                StartupCycles: 0
            ).Cycles(
                bytesTransferred: long.MaxValue,
                dependentAccesses: 0,
                starts: 0
            ).Cycles
        );
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => default(MemoryClassProfile).Cycles(
            bytesTransferred: 0,
            dependentAccesses: 0,
            starts: 0
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => profile.Cycles(
            bytesTransferred: 0,
            dependentAccesses: 0,
            starts: -1
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new MemoryClassProfile(
            AdditionalLatencyCycles: 0,
            BandwidthDenominator: 1,
            BandwidthNumerator: 1,
            StartupCycles: -1
        ).Cycles(
            bytesTransferred: 0,
            dependentAccesses: 0,
            starts: 0
        ));
    }
    [Fact]
    public void PortableModelDoesNotInventMemoryCoefficients() {
        foreach (var access in Enum.GetValues<MemoryAccessClass>()) {
            Assert.True(condition: CostModel.MemoryCycles(
                accessClass: access,
                bytesTransferred: 64,
                dependentAccesses: 1,
                starts: 1
            ).IsUnmodeled);
            Assert.Equal(
                CostBound.Zero,
                CostModel.MemoryCycles(
                    accessClass: access,
                    bytesTransferred: 0,
                    dependentAccesses: 0,
                    starts: 0
                )
            );
        }
        Assert.True(condition: CostModel.MemoryCycles(
            accessClass: ((MemoryAccessClass)byte.MaxValue),
            bytesTransferred: 0,
            dependentAccesses: 0,
            starts: 0
        ).IsUnmodeled);
    }
    [Fact]
    public void UncertaintyAndOverflowSurviveCompositionExceptProvedZeroRepetition() {
        var unknown = CostBound.Unmodeled(reason: "missing kernel");

        Assert.True(condition: (unknown + CostBound.Known(cycles: 5)).IsUnmodeled);
        Assert.True(condition: CostBound.Max(
            left: CostBound.Known(cycles: long.MaxValue),
            right: unknown
        ).IsUnmodeled);
        Assert.True(condition: (CostBound.Known(cycles: long.MaxValue) + CostBound.Known(cycles: 1)).IsOverflow);
        Assert.True(condition: (CostBound.Known(cycles: long.MaxValue) * 2).IsOverflow);
        Assert.Equal(
            CostBound.Zero,
            (unknown * 0)
        );
        Assert.Equal(
            CostBound.Zero,
            (CostBound.Overflow * 0)
        );
    }
}
