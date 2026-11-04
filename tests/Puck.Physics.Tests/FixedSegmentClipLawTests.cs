using System.Numerics;
using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedSegmentClipLawTests {
    private static FixedQ4816 Raw(long value) => FixedQ4816.FromRawBits(value);
    private static long RoundRatio(BigInteger numerator, BigInteger denominator) {
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        var quotient = BigInteger.DivRem(numerator * 65536, denominator, out var remainder);
        var distance = BigInteger.Abs(remainder) * 2;
        if (distance > denominator || (distance == denominator && !quotient.IsEven)) { quotient += numerator.Sign; }
        return (long)quotient;
    }

    [Fact]
    public void EndpointGridMatchesIndependentRationalBoundaryClipping() {
        // Exact integer endpoints around a non-symmetric slab; the retained interval is already narrowed by another
        // axis. The oracle intersects rational boundaries before a single independent nearest-even quantization.
        for (var start = -5; start <= 5; start++) {
            for (var end = -5; end <= 5; end++) {
                var enter = Raw(16384);
                var exit = Raw(49152);
                var actual = FixedSegmentClip.Axis(Raw(start * 65536L), Raw(end * 65536L),
                    Raw(-2 * 65536L), Raw(65536), ref enter, ref exit);
                var expectedEnter = 16384L;
                var expectedExit = 49152L;
                var possible = !((start < -2 && end < -2) || (start > 1 && end > 1));
                if (possible && start != end) {
                    var first = RoundRatio(-2 - start, end - start);
                    var second = RoundRatio(1 - start, end - start);
                    if (first > second) { (first, second) = (second, first); }
                    expectedEnter = Math.Max(expectedEnter, first);
                    expectedExit = Math.Min(expectedExit, second);
                }
                possible &= expectedEnter <= expectedExit;
                Assert.Equal(possible, actual);
                if (actual) {
                    Assert.Equal(expectedEnter, enter.Value);
                    Assert.Equal(expectedExit, exit.Value);
                }
            }
        }
    }

    [Theory]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(long.MaxValue, long.MinValue)]
    public void ExtremeEndpointsUseTheirWideDifference(long start, long end) {
        var enter = FixedQ4816.Zero;
        var exit = FixedQ4816.One;
        Assert.True(FixedSegmentClip.Axis(Raw(start), Raw(end), Raw(-1000000000), Raw(1000000000), ref enter, ref exit));
        var first = RoundRatio((BigInteger)(-1000000000) - start, (BigInteger)end - start);
        var second = RoundRatio((BigInteger)1000000000 - start, (BigInteger)end - start);
        Assert.Equal(Math.Min(first, second), enter.Value);
        Assert.Equal(Math.Max(first, second), exit.Value);
    }

    [Fact]
    public void SeparatedEndpointOneRawBeyondTheSlabCannotRoundIntoIt() {
        var enter = FixedQ4816.Zero;
        var exit = FixedQ4816.One;
        Assert.False(FixedSegmentClip.Axis(Raw(1), Raw(long.MaxValue), Raw(-65536), Raw(0), ref enter, ref exit));
        Assert.False(FixedSegmentClip.Axis(Raw(-1), Raw(long.MinValue), Raw(0), Raw(65536), ref enter, ref exit));
    }
}
