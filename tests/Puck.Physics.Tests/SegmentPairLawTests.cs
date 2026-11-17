using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class SegmentPairLawTests {
    private static FixedVector3 Raw(long x, long y = 0, long z = 0) => new(
        X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z));

    [Fact]
    public void ClosestDeltaMatchesExactPairGeometryAtEveryScale() {
        // Exact geometric witnesses in raw units, including half-raw ties. Reversing either segment or
        // exchanging the two must retain the same displacement (negated when the pair is exchanged).
        (FixedVector3 P, FixedVector3 Q, FixedVector3 R, FixedVector3 S, FixedVector3 Delta)[] pairs = [
            (Raw(66), Raw(66), Raw(0), Raw(131), Raw(0)),
            (Raw(0), Raw(0), Raw(0), Raw(0), Raw(0)),
            (Raw(x: 4, y: 5, z: 6), Raw(x: 4, y: 5, z: 6), Raw(x: 1, y: 2, z: 3), Raw(x: 1, y: 2, z: 3), Raw(x: 3, y: 3, z: 3)),
            (Raw(-66), Raw(66), Raw(0, -66), Raw(0, 66), Raw(0)),
            (Raw(-66), Raw(66), Raw(-33, 7), Raw(99, 7), Raw(0, -7)),
            (Raw(-66), Raw(66), Raw(x: 0, y: -66, z: 9), Raw(x: 0, y: 66, z: 9), Raw(x: 0, y: 0, z: -9)),
            (Raw(0), Raw(5), Raw(7, 3), Raw(9, 3), Raw(-2, -3)),
            (Raw(0), Raw(5), Raw(9, 3), Raw(7, 3), Raw(-2, -3)),
            (Raw(0), Raw(10), Raw(5, 3), Raw(5, 8), Raw(0, -3)),
            (Raw(0), Raw(10), Raw(5, -8), Raw(5, -3), Raw(0, 3)),
            (Raw(1), Raw(1), Raw(0), Raw(1, 1), Raw(0)),
            (Raw(3), Raw(3), Raw(0), Raw(3, 3), Raw(2, -2)),
            (Raw(long.MinValue), Raw(long.MaxValue), Raw(x: 0, y: 0, z: 11), Raw(x: 0, y: 0, z: 11), Raw(x: 0, y: 0, z: -11)),
            (Raw(long.MinValue, long.MinValue), Raw(long.MaxValue, long.MaxValue),
                Raw(x: long.MinValue, y: long.MaxValue, z: 11), Raw(x: long.MaxValue, y: long.MinValue, z: 11), Raw(x: 0, y: 0, z: -11)),
            (Raw((long.MaxValue - 131), long.MinValue), Raw(long.MaxValue, long.MinValue),
                Raw((long.MaxValue - 65), (long.MinValue + 7)), Raw((long.MaxValue - 65), (long.MinValue + 7)), Raw(0, -7)),
            (Raw((-1L << 40), 0), Raw((1L << 40), 2), Raw(x: 0, y: -1, z: 7), Raw(x: 0, y: 3, z: 7), Raw(x: 0, y: 0, z: -7)),
        ];

        foreach (var (p, q, r, s, delta) in pairs) {
            Assert.Equal(delta, FixedDynamicBodyContacts.ClosestDelta(p1: p, p2: r, q1: q, q2: s));
            Assert.Equal(delta, FixedDynamicBodyContacts.ClosestDelta(p1: q, p2: r, q1: p, q2: s));
            Assert.Equal(delta, FixedDynamicBodyContacts.ClosestDelta(p1: p, p2: s, q1: q, q2: r));
            Assert.Equal(-delta, FixedDynamicBodyContacts.ClosestDelta(p1: r, p2: p, q1: s, q2: q));
        }
    }
}
