using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly int[] HexIndexBoundaryRadii = [1, 2, 3, 15, 16, 255, 256, 65_535, 65_536, 1_753_413_054, 1_753_413_055];
    private static readonly int[] HexIndexBoundaryTurns = [int.MinValue, -7, -6, -1, 0, 1, 6, 7, int.MaxValue];

    public static string? HexagonalIndexPerimeter() {
        var cells = new HashSet<HexagonalCoordinate>();
        foreach (var (index, q, r, radius) in Oracles.HexIndexPerimeter(maximumRadius: 32)) {
            var subject = new HexagonalIndex(value: (long)index);
            Assert.Equal(expected: new HexagonalCoordinate(Q: (int)q, R: (int)r), actual: subject.ToCoordinate());
            Assert.Equal(expected: (long)index, actual: HexagonalIndex.FromCoordinate(coordinate: new(Q: (int)q, R: (int)r)).Value);
            Assert.Equal(expected: radius, actual: subject.Radius);
            Assert.True(condition: cells.Add(item: subject.ToCoordinate()), userMessage: $"Repeated cell at index {index}.");
            if (index > 0) { CheckHexIndexSuccessor(value: (long)index - 1); }
        }
        Assert.Equal(expected: 1 + (3 * 32 * 33), actual: cells.Count);

        return null;
    }

    public static string? HexagonalIndexContinuity(long[] left, long[] right) {
        foreach (var value in new[] { HexIndexRaw(raw: left[0]), HexIndexRaw(raw: right[0]) }) {
            CheckHexIndexSuccessor(value: value);
            // Locate the ring independently, so a matching defect in Radius cannot hide a seam.
            var (q, r) = Oracles.HexIndexCoordinate(index: value);
            var radius = Oracles.HexIndexRadius(q, r);
            if (radius.IsZero) { continue; }
            CheckHexIndexSuccessor(value: (long)(3 * radius * (radius - 1)));
            CheckHexIndexSuccessor(value: (long)(3 * radius * (radius + 1)));
        }

        return null;
    }

    private static void CheckHexIndexSuccessor(long value) {
        if (value == HexagonalIndex.MaxValue) { return; }
        var a = new HexagonalIndex(value: value).ToCoordinate();
        var b = new HexagonalIndex(value: value + 1).ToCoordinate();
        var distance = Oracles.HexIndexRadius(q: (BigInteger)b.Q - a.Q, r: (BigInteger)b.R - a.R);
        Assert.True(condition: distance.IsOne, userMessage: $"Indices {value} and {value + 1}: ({a.Q}, {a.R}) → ({b.Q}, {b.R}), distance {distance}.");
    }

    public static string? HexagonalIndexGeometry(long[] left, long[] right) {
        CheckHexIndexGeometry(value: HexIndexRaw(raw: left[0]), other: HexIndexRaw(raw: right[0]), turns: (int)left[1]);
        return null;
    }

    private static long HexIndexRaw(long raw) => (raw & long.MaxValue) % (HexagonalIndex.MaxValue + 1);

    private static void CheckHexIndexGeometry(long value, long other, int turns) {
        var subject = new HexagonalIndex(value: value);
        var (q, r) = Oracles.HexIndexCoordinate(index: value);
        var (bq, br) = Oracles.HexIndexCoordinate(index: other);
        Assert.Equal(expected: new HexagonalCoordinate(Q: (int)q, R: (int)r), actual: subject.ToCoordinate());
        Assert.Equal(expected: value, actual: HexagonalIndex.FromCoordinate(coordinate: new(Q: (int)q, R: (int)r)).Value);
        CheckHexIndexSuccessor(value: value);
        Assert.Equal(expected: (int)Oracles.HexIndexRadius(q, r), actual: subject.Radius);
        Assert.Equal(expected: (long)((q * q) - (q * r) + (r * r)), actual: subject.Norm);
        Assert.Equal(expected: (long)Oracles.HexIndexRadius(q - bq, r - br), actual: HexagonalIndex.Distance(left: subject, right: new(value: other)));

        var rotatedQ = q;
        var rotatedR = r;
        var count = ((turns % 6) + 6) % 6;
        for (var i = 0; i < count; ++i) { (rotatedQ, rotatedR) = (rotatedQ - rotatedR, rotatedQ); }
        ExpectHexIndex(action: () => subject.Rotate(turns: turns), q: rotatedQ, r: rotatedR);
        ExpectHexIndex(action: subject.Conjugate, q: q - r, r: -r);
        Assert.Equal(expected: subject, actual: subject.Rotate(turns: 6));
        Assert.Equal(expected: subject, actual: subject.Conjugate().Conjugate());
        Assert.Equal(expected: subject.Rotate(turns: -count), actual: subject.Conjugate().Rotate(turns: count).Conjugate());

        for (var direction = 0; direction < 6; ++direction) {
            var (dq, dr) = Oracles.HexIndexDirection(direction);
            ExpectHexIndex(action: () => subject.Neighbor(direction: direction), q: q + dq, r: r + dr);
        }
    }

    public static string? HexagonalIndexArithmetic(long[] left, long[] right) {
        var a = new HexagonalIndex(value: HexIndexRaw(raw: left[0]));
        var b = new HexagonalIndex(value: HexIndexRaw(raw: right[0]));
        CheckHexIndexArithmetic(a, b);

        // Small coordinates admit nontrivial products and every intermediate in the distributive identity.
        var smallA = HexagonalIndex.FromCoordinate(coordinate: new(Q: (int)(left[0] % 129), R: (int)(left[1] % 129)));
        var smallB = HexagonalIndex.FromCoordinate(coordinate: new(Q: (int)(right[0] % 129), R: (int)(right[1] % 129)));
        CheckHexIndexArithmetic(smallA, smallB);
        var one = HexagonalIndex.MultiplicativeIdentity;
        var zero = HexagonalIndex.AdditiveIdentity;
        Assert.Equal(expected: 0L, actual: zero.Value);
        Assert.Equal(expected: 1L, actual: one.Value);
        Assert.Equal(expected: smallA, actual: smallA + zero);
        Assert.Equal(expected: smallA, actual: smallA * one);
        Assert.Equal(expected: zero, actual: smallA + (-smallA));
        Assert.Equal(expected: smallA + smallB, actual: smallB + smallA);
        Assert.Equal(expected: smallA * smallB, actual: smallB * smallA);
        Assert.Equal(expected: smallA * (smallB + one), actual: (smallA * smallB) + smallA);
        Assert.Equal(expected: (smallA + smallB).Rotate(turns: 1), actual: smallA.Rotate(turns: 1) + smallB.Rotate(turns: 1));
        return null;
    }

    private static void CheckHexIndexArithmetic(HexagonalIndex a, HexagonalIndex b) {
        var (aq, ar) = Oracles.HexIndexCoordinate(index: a.Value);
        var (bq, br) = Oracles.HexIndexCoordinate(index: b.Value);
        ExpectHexIndex(action: () => a + b, q: aq + bq, r: ar + br);
        ExpectHexIndex(action: () => a - b, q: aq - bq, r: ar - br);
        ExpectHexIndex(action: () => -a, q: -aq, r: -ar);
        ExpectHexIndex(action: () => a.Translate(displacement: new(Q: (int)bq, R: (int)br)), q: aq + bq, r: ar + br);
        var (pq, pr) = Oracles.HexIndexProduct(aq, ar, bq, br);
        ExpectHexIndex(action: () => a * b, q: pq, r: pr);
    }

    private static void ExpectHexIndex(Func<HexagonalIndex> action, BigInteger q, BigInteger r) {
        if (Oracles.HexIndexRadius(q, r) > HexagonalIndex.MaxRadius) {
            Assert.Throws<OverflowException>(testCode: () => action());
        } else {
            Assert.Equal(expected: (long)Oracles.HexIndexValue(q, r), actual: action().Value);
        }
    }

    public static string? HexagonalIndexBoundaries() {
        var maximumRadius = HexagonalIndex.MaxRadius;
        var maximumValue = HexagonalIndex.MaxValue;
        BigInteger radius = maximumRadius;
        Assert.Equal(expected: (BigInteger)maximumValue, actual: 3 * radius * (radius + 1));
        Assert.True(condition: 3 * (radius + 1) * (radius + 2) > long.MaxValue);
        Assert.True(condition: maximumValue <= long.MaxValue);
        Assert.Equal(expected: 0L, actual: default(HexagonalIndex).Value);
        Assert.Equal(expected: default, actual: new HexagonalIndex(value: 0).ToCoordinate());
        Assert.Equal(expected: default, actual: default(HexagonalIndex).Rotate(turns: int.MinValue));
        Assert.Equal(expected: default, actual: default(HexagonalIndex).Conjugate());
        Assert.Equal(expected: maximumValue, actual: new HexagonalIndex(value: maximumValue).Value);
        var example = HexagonalIndex.FromCoordinate(coordinate: new(Q: 2, R: 1));
        Assert.Equal(expected: 9L, actual: example.Value);
        Assert.Equal(expected: 11L, actual: example.Rotate(turns: 1).Value);
        Assert.Equal(expected: 23L, actual: example.Neighbor(direction: 1).Value);
        Assert.Equal(expected: 1L, actual: HexagonalIndex.Distance(left: example, right: example.Neighbor(direction: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(paramName: "value", testCode: () => new HexagonalIndex(value: -1));
        Assert.Throws<ArgumentOutOfRangeException>(paramName: "value", testCode: () => new HexagonalIndex(value: long.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(paramName: "value", testCode: () => new HexagonalIndex(value: maximumValue + 1));
        Assert.Throws<ArgumentOutOfRangeException>(paramName: "value", testCode: () => new HexagonalIndex(value: long.MaxValue));
        Assert.Throws<OverflowException>(testCode: () => HexagonalIndex.FromCoordinate(coordinate: new(Q: maximumRadius + 1, R: 0)));
        Assert.Throws<OverflowException>(testCode: () => HexagonalIndex.FromCoordinate(coordinate: new(Q: int.MinValue, R: int.MaxValue)));
        Assert.Throws<OverflowException>(testCode: () => HexagonalIndex.FromCoordinate(coordinate: new(Q: int.MaxValue, R: int.MaxValue)));
        Assert.Throws<OverflowException>(testCode: () => HexagonalIndex.FromCoordinate(coordinate: new(Q: int.MinValue, R: int.MinValue)));

        foreach (var ring in HexIndexBoundaryRadii) {
            var start = (long)(1 + (3 * (BigInteger)ring * (ring - 1)));
            CheckHexIndexGeometry(value: start - 1, other: 0, turns: 1);
            CheckHexIndexSuccessor(value: start - 1);
            var end = (long)(3 * (BigInteger)ring * (ring + 1));
            CheckHexIndexSuccessor(value: end);
            Assert.Equal(expected: new HexagonalCoordinate(Q: 1, R: 1 - ring), actual: new HexagonalIndex(value: start).ToCoordinate());
            Assert.Equal(expected: new HexagonalCoordinate(Q: 0, R: -ring), actual: new HexagonalIndex(value: end).ToCoordinate());
            for (var side = 0; side < 6; ++side) {
                var cornerOffset = (((long)side * ring) + ring - 1) % (6L * ring);
                for (var delta = -1; delta <= 1; ++delta) {
                    var index = start + ((cornerOffset + delta + (6L * ring)) % (6L * ring));
                    CheckHexIndexGeometry(value: index, other: maximumValue, turns: delta);
                }
            }
        }

        var outer = HexagonalIndex.FromCoordinate(coordinate: new(Q: maximumRadius, R: 0));
        Assert.Equal(expected: 2L * maximumRadius, actual: HexagonalIndex.Distance(left: outer, right: -outer));
        Assert.Throws<OverflowException>(testCode: () => outer.Neighbor(direction: 0));
        foreach (var turns in HexIndexBoundaryTurns) {
            CheckHexIndexGeometry(value: maximumValue, other: 0, turns: turns);
            var (q, r) = Oracles.HexIndexDirection(direction: turns);
            ExpectHexIndex(action: () => default(HexagonalIndex).Neighbor(direction: turns), q, r);
        }

        return null;
    }
}
