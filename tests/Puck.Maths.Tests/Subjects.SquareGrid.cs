using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? SquareCoordinateOperations(long[] left, long[] right) {
        var a = new SquareCoordinate(X: (int)left[0], Y: (int)left[1]);
        var b = new SquareCoordinate(X: (int)right[0], Y: (int)right[1]);
        CheckSquareCoordinate(a, b, factor: (int)right[0]);
        CheckSquareCoordinate(new(X: (int)(left[0] % 129), Y: (int)(left[1] % 129)),
            new(X: (int)(right[0] % 129), Y: (int)(right[1] % 129)), factor: (int)(right[0] % 9));
        var x = Oracles.RoundRationalTiesToEven(numerator: left[0], denominator: 65536);
        var y = Oracles.RoundRationalTiesToEven(numerator: right[0], denominator: 65536);
        ExpectSquareCoordinate(() => SquareCoordinate.Round(x: new(Value: left[0]), y: new(Value: right[0])), x, y);
        return null;
    }

    private static void CheckSquareCoordinate(SquareCoordinate a, SquareCoordinate b, int factor) {
        BigInteger x = a.X, y = a.Y, bx = b.X, by = b.Y;
        Assert.Equal(expected: x, actual: (BigInteger)a.X);
        Assert.Equal(expected: y, actual: (BigInteger)a.Y);
        ExpectSquareScalar(() => a.Length, BigInteger.Abs(x) + BigInteger.Abs(y));
        ExpectSquareScalar(() => a.Radius, Oracles.SquareRadius(x, y));
        ExpectSquareScalar(() => a.Norm, (x * x) + (y * y));
        ExpectSquareScalar(() => SquareCoordinate.Distance(a, b), BigInteger.Abs(x - bx) + BigInteger.Abs(y - by));
        ExpectSquareCoordinate(() => a + b, x + bx, y + by);
        ExpectSquareCoordinate(() => a - b, x - bx, y - by);
        ExpectSquareCoordinate(() => -a, -x, -y);
        ExpectSquareCoordinate(() => a * b, (x * bx) - (y * by), (x * by) + (y * bx));
        ExpectSquareCoordinate(() => a * factor, x * factor, y * factor);
        ExpectSquareCoordinate(a.RotatedLeft, -y, x);
        ExpectSquareCoordinate(a.RotatedRight, y, -x);
        var (dx, dy) = Oracles.SquareDirection(factor);
        Assert.Equal(expected: new SquareCoordinate((int)dx, (int)dy), actual: SquareCoordinate.Direction(factor));
        ExpectSquareCoordinate(() => a.Neighbor(factor), x + dx, y + dy);
        Assert.Equal(expected: a, actual: a + SquareCoordinate.AdditiveIdentity);
        Assert.Equal(expected: a, actual: a * SquareCoordinate.MultiplicativeIdentity);
        Assert.Equal(expected: FormattableString.Invariant($"SquareCoordinate {{ X = {a.X}, Y = {a.Y} }}"), actual: a.ToString());
    }

    private static void ExpectSquareScalar(Func<int> action, BigInteger expected) {
        if (expected > int.MaxValue || expected < int.MinValue) { Assert.Throws<OverflowException>(() => action()); }
        else { Assert.Equal(expected: (int)expected, actual: action()); }
    }
    private static void ExpectSquareCoordinate(Func<SquareCoordinate> action, BigInteger x, BigInteger y) {
        if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue) { Assert.Throws<OverflowException>(() => action()); }
        else { Assert.Equal(expected: new SquareCoordinate((int)x, (int)y), actual: action()); }
    }
    private static void ExpectSquareIndex(Func<SquareIndex> action, BigInteger x, BigInteger y) {
        if (Oracles.SquareRadius(x, y) > SquareIndex.MaxRadius) { Assert.Throws<OverflowException>(() => action()); }
        else { Assert.Equal(expected: (long)Oracles.SquareIndexAt(x, y), actual: action().Value); }
    }
    private static long SquareRaw(long raw) => (raw & long.MaxValue) % (SquareIndex.MaxValue + 1);

    public static string? SquareIndexOperations(long[] left, long[] right) {
        CheckSquareIndex(SquareRaw(left[0]), SquareRaw(right[0]), factor: (int)left[1]);
        CheckSquareIndex((left[0] & long.MaxValue) % 1089, (right[0] & long.MaxValue) % 1089, factor: (int)(left[1] % 9));
        return null;
    }

    private static void CheckSquareIndex(long value, long other, int factor) {
        var a = new SquareIndex(value);
        var b = new SquareIndex(other);
        var (x, y) = Oracles.SquareCoordinateAt(value);
        var (bx, by) = Oracles.SquareCoordinateAt(other);
        Assert.Equal(expected: new SquareCoordinate((int)x, (int)y), actual: a.ToCoordinate());
        Assert.Equal(expected: value, actual: SquareIndex.FromCoordinate(new((int)x, (int)y)).Value);
        Assert.Equal(expected: (int)Oracles.SquareRadius(x, y), actual: a.Radius);
        Assert.Equal(expected: (long)((x * x) + (y * y)), actual: a.Norm);
        Assert.Equal(expected: (long)(BigInteger.Abs(x - bx) + BigInteger.Abs(y - by)), actual: SquareIndex.Distance(a, b));
        ExpectSquareIndex(a.Conjugate, x, -y);
        ExpectSquareIndex(a.Swap, y, x);
        ExpectSquareIndex(() => -a, -x, -y);
        ExpectSquareIndex(() => a.Scale(factor), x * factor, y * factor);
        ExpectSquareIndex(() => a + b, x + bx, y + by);
        ExpectSquareIndex(() => a - b, x - bx, y - by);
        ExpectSquareIndex(() => a.Translate(new((int)bx, (int)by)), x + bx, y + by);
        ExpectSquareIndex(() => a * b, (x * bx) - (y * by), (x * by) + (y * bx));
        var rx = x;
        var ry = y;
        for (var i = 0; i < ((factor % 4) + 4) % 4; ++i) { (rx, ry) = (-ry, rx); }
        ExpectSquareIndex(() => a.Rotate(factor), rx, ry);
        foreach (var direction in new[] { 0, 1, 2, 3, factor }) {
            var (dx, dy) = Oracles.SquareDirection(direction);
            ExpectSquareIndex(() => a.Neighbor(direction), x + dx, y + dy);
        }
        Assert.Equal(expected: a, actual: a.Rotate(4));
        Assert.Equal(expected: a, actual: a.Conjugate().Conjugate());
        Assert.Equal(expected: a, actual: a.Swap().Swap());
        Assert.Equal(expected: a.Rotate(3), actual: a.Conjugate().Rotate(1).Conjugate());
        Assert.Equal(expected: a, actual: a + SquareIndex.AdditiveIdentity);
        Assert.Equal(expected: a, actual: a * SquareIndex.MultiplicativeIdentity);
        CheckSquareSuccessor(value);
    }

    private static void CheckSquareSuccessor(long value) {
        if (value == SquareIndex.MaxValue) { return; }
        var a = new SquareIndex(value).ToCoordinate();
        var b = new SquareIndex(value + 1).ToCoordinate();
        Assert.Equal(expected: BigInteger.One, actual: BigInteger.Abs((BigInteger)a.X - b.X) + BigInteger.Abs((BigInteger)a.Y - b.Y));
    }

    public static string? SquareGridBoundaries() {
        Assert.Equal(expected: 4, actual: SquareCoordinate.NeighborCount);
        Assert.Equal(expected: default, actual: new SquareIndex(0).ToCoordinate());
        var example = SquareIndex.FromCoordinate(new(X: 2, Y: 1));
        Assert.Equal(expected: 11L, actual: example.Value);
        Assert.Equal(expected: 15L, actual: example.Rotate(1).Value);
        Assert.Equal(expected: 12L, actual: example.Neighbor(1).Value);
        Assert.Equal(expected: 4, actual: new SquareCoordinate(2, 2).Length);
        Assert.Equal(expected: 2, actual: new SquareCoordinate(2, 2).Radius);
        var seen = new HashSet<SquareCoordinate>();
        foreach (var (index, x, y) in Oracles.SquareWalk(count: 1089)) {
            var cell = new SquareCoordinate((int)x, (int)y);
            Assert.True(seen.Add(cell));
            Assert.Equal(expected: cell, actual: new SquareIndex(index).ToCoordinate());
            Assert.Equal(expected: index, actual: SquareIndex.FromCoordinate(cell).Value);
            CheckSquareSuccessor(index);
        }
        BigInteger maximum = SquareIndex.MaxRadius;
        Assert.Equal(expected: (BigInteger)SquareIndex.MaxValue, actual: BigInteger.Pow((2 * maximum) + 1, 2) - 1);
        Assert.True(BigInteger.Pow((2 * maximum) + 3, 2) - 1 > long.MaxValue);
        foreach (var value in new[] { long.MinValue, -1, SquareIndex.MaxValue + 1, long.MaxValue }) {
            Assert.Throws<ArgumentOutOfRangeException>(paramName: "value", testCode: () => new SquareIndex(value));
        }
        foreach (var radius in new[] { 1, 2, 3, 16, 65536, SquareIndex.MaxRadius - 1, SquareIndex.MaxRadius }) {
            var start = (long)BigInteger.Pow((2 * (BigInteger)radius) - 1, 2);
            var end = (long)BigInteger.Pow((2 * (BigInteger)radius) + 1, 2) - 1;
            Assert.Equal(expected: new SquareCoordinate(radius, 1 - radius), actual: new SquareIndex(start).ToCoordinate());
            Assert.Equal(expected: new SquareCoordinate(radius, -radius), actual: new SquareIndex(end).ToCoordinate());
            CheckSquareSuccessor(start - 1);
            CheckSquareSuccessor(end);
            for (var corner = 1; corner <= 4; ++corner) {
                for (var delta = -1; delta <= 1; ++delta) {
                    var value = start + (((2L * corner * radius) - 1 + delta + (8L * radius)) % (8L * radius));
                    foreach (var factor in new[] { int.MinValue, -3, -1, 0, 1, 2, 3, int.MaxValue }) {
                        CheckSquareIndex(value, SquareIndex.MaxValue, factor);
                    }
                }
            }
        }
        Assert.Equal(expected: 4L * SquareIndex.MaxRadius, actual: SquareIndex.Distance(new(SquareIndex.MaxValue), -new SquareIndex(SquareIndex.MaxValue)));
        foreach (var x in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
            foreach (var y in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
                CheckSquareCoordinate(new(x, y), new(y, x), factor: int.MinValue);
                ExpectSquareIndex(() => SquareIndex.FromCoordinate(new(x, y)), x, y);
            }
        }
        foreach (var raw in new[] { -163841L, -163840, -163839, -98304, -32769, -32768, -32767, 32767, 32768, 32769, 98304, 163840, long.MinValue, long.MaxValue }) {
            var rounded = Oracles.RoundRationalTiesToEven(raw, 65536);
            ExpectSquareCoordinate(() => SquareCoordinate.Round(new(raw), new(raw)), rounded, rounded);
        }
        CheckSquareIndex(0, 0, int.MinValue);
        return null;
    }
}
