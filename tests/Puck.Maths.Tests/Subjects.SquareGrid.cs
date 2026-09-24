using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? SquareCoordinateOperations(long[] left, long[] right) {
        var a = new SquareCoordinate(
            X: ((int)left[0]),
            Y: ((int)left[1])
        );
        var b = new SquareCoordinate(
            X: ((int)right[0]),
            Y: ((int)right[1])
        );

        CheckSquareCoordinate(
            a,
            b,
            factor: ((int)right[0])
        );
        CheckSquareCoordinate(
            new(
                X: ((int)(left[0] % 129)),
                Y: ((int)(left[1] % 129))
            ),
            new(
                X: ((int)(right[0] % 129)),
                Y: ((int)(right[1] % 129))
            ),
            factor: ((int)(right[0] % 9))
        );
        var x = Oracles.RoundRationalTiesToEven(
            numerator: left[0],
            denominator: 65536
        );
        var y = Oracles.RoundRationalTiesToEven(
            numerator: right[0],
            denominator: 65536
        );

        ExpectSquareCoordinate(
            action: () => SquareCoordinate.Round(
                x: new(Value: left[0]),
                y: new(Value: right[0])
            ),
            x: x,
            y: y
        );
        return null;
    }

    private static void CheckSquareCoordinate(SquareCoordinate a, SquareCoordinate b, int factor) {
        BigInteger x = a.X, y = a.Y, bx = b.X, by = b.Y;

        Assert.Equal(
            expected: x,
            actual: ((BigInteger)a.X)
        );
        Assert.Equal(
            expected: y,
            actual: ((BigInteger)a.Y)
        );
        ExpectSquareScalar(
            action: () => a.Length,
            expected: (BigInteger.Abs(value: x) + BigInteger.Abs(value: y))
        );
        ExpectSquareScalar(
            action: () => a.Radius,
            expected: Oracles.SquareRadius(
                x: x,
                y: y
            )
        );
        ExpectSquareScalar(
            action: () => a.Norm,
            expected: ((x * x) + (y * y))
        );
        ExpectSquareScalar(
            action: () => SquareCoordinate.Distance(
                left: a,
                right: b
            ),
            expected: (BigInteger.Abs(value: (x - bx)) + BigInteger.Abs(value: (y - by)))
        );
        ExpectSquareCoordinate(
            action: () => (a + b),
            x: (x + bx),
            y: (y + by)
        );
        ExpectSquareCoordinate(
            action: () => (a - b),
            x: (x - bx),
            y: (y - by)
        );
        ExpectSquareCoordinate(
            action: () => -a,
            x: -x,
            y: -y
        );
        ExpectSquareCoordinate(
            action: () => (a * b),
            x: ((x * bx) - (y * by)),
            y: ((x * by) + (y * bx))
        );
        ExpectSquareCoordinate(
            action: () => (a * factor),
            x: (x * factor),
            y: (y * factor)
        );
        ExpectSquareCoordinate(
            action: a.RotatedLeft,
            x: -y,
            y: x
        );
        ExpectSquareCoordinate(
            action: a.RotatedRight,
            x: y,
            y: -x
        );
        var (dx, dy) = Oracles.SquareDirection(direction: factor);
        Assert.Equal(
            expected: new SquareCoordinate(
                X: ((int)dx),
                Y: ((int)dy)
            ),
            actual: SquareCoordinate.Direction(direction: factor)
        );
        ExpectSquareCoordinate(
            action: () => a.Neighbor(direction: factor),
            x: (x + dx),
            y: (y + dy)
        );
        Assert.Equal(
            expected: a,
            actual: (a + SquareCoordinate.AdditiveIdentity)
        );
        Assert.Equal(
            expected: a,
            actual: (a * SquareCoordinate.MultiplicativeIdentity)
        );
        Assert.Equal(
            expected: FormattableString.Invariant(formattable: $"SquareCoordinate {{ X = {a.X}, Y = {a.Y} }}"),
            actual: a.ToString()
        );
    }
    private static void ExpectSquareScalar(Func<int> action, BigInteger expected) =>
        ExpectValueOrOverflow(
            action: action,
            expected: () => ((int)expected),
            overflows: ((expected > int.MaxValue) || (expected < int.MinValue))
        );
    private static void ExpectSquareCoordinate(Func<SquareCoordinate> action, BigInteger x, BigInteger y) =>
        ExpectValueOrOverflow(
            action: action,
            expected: () => new SquareCoordinate(
                X: ((int)x),
                Y: ((int)y)
            ),
            overflows: ((x < int.MinValue) || (x > int.MaxValue) || (y < int.MinValue) || (y > int.MaxValue))
        );
    private static void ExpectSquareIndex(Func<SquareIndex> action, BigInteger x, BigInteger y) =>
        ExpectValueOrOverflow(
            action: () => action().Value,
            expected: () => ((long)Oracles.SquareIndexAt(
                x: x,
                y: y
            )),
            overflows: (Oracles.SquareRadius(
                x: x,
                y: y
            ) > SquareIndex.MaxRadius)
        );
    private static long SquareRaw(long raw) => ((raw & long.MaxValue) % (SquareIndex.MaxValue + 1));

    public static string? SquareIndexOperations(long[] left, long[] right) {
        CheckSquareIndex(
            SquareRaw(raw: left[0]),
            SquareRaw(raw: right[0]),
            factor: ((int)left[1])
        );
        CheckSquareIndex(
            ((left[0] & long.MaxValue) % 1089),
            ((right[0] & long.MaxValue) % 1089),
            factor: ((int)(left[1] % 9))
        );
        return null;
    }

    private static void CheckSquareIndex(long value, long other, int factor) {
        var a = new SquareIndex(value: value);
        var b = new SquareIndex(value: other);

        var (x, y) = Oracles.SquareCoordinateAt(index: value);
        var (bx, by) = Oracles.SquareCoordinateAt(index: other);
        Assert.Equal(
            expected: new SquareCoordinate(
                X: ((int)x),
                Y: ((int)y)
            ),
            actual: a.ToCoordinate()
        );
        Assert.Equal(
            expected: value,
            actual: SquareIndex.FromCoordinate(coordinate: new(
                X: ((int)x),
                Y: ((int)y)
            )).Value
        );
        Assert.Equal(
            expected: ((int)Oracles.SquareRadius(
                x: x,
                y: y
            )),
            actual: a.Radius
        );
        Assert.Equal(
            expected: ((long)((x * x) + (y * y))),
            actual: a.Norm
        );
        Assert.Equal(
            expected: ((long)(BigInteger.Abs(value: (x - bx)) + BigInteger.Abs(value: (y - by)))),
            actual: SquareIndex.Distance(
                left: a,
                right: b
            )
        );
        ExpectSquareIndex(
            action: a.Conjugate,
            x: x,
            y: -y
        );
        ExpectSquareIndex(
            action: a.Swap,
            x: y,
            y: x
        );
        ExpectSquareIndex(
            action: () => -a,
            x: -x,
            y: -y
        );
        ExpectSquareIndex(
            action: () => a.Scale(factor: factor),
            x: (x * factor),
            y: (y * factor)
        );
        ExpectSquareIndex(
            action: () => (a + b),
            x: (x + bx),
            y: (y + by)
        );
        ExpectSquareIndex(
            action: () => (a - b),
            x: (x - bx),
            y: (y - by)
        );
        ExpectSquareIndex(
            action: () => a.Translate(displacement: new(
                X: ((int)bx),
                Y: ((int)by)
            )),
            x: (x + bx),
            y: (y + by)
        );
        ExpectSquareIndex(
            action: () => (a * b),
            x: ((x * bx) - (y * by)),
            y: ((x * by) + (y * bx))
        );
        var rx = x;
        var ry = y;

        for (var i = 0; (i < (((factor % 4) + 4) % 4)); ++i) { (rx, ry) = (-ry, rx); }
        ExpectSquareIndex(
            action: () => a.Rotate(turns: factor),
            x: rx,
            y: ry
        );
        foreach (var direction in new[] { 0, 1, 2, 3, factor }) {
            var (dx, dy) = Oracles.SquareDirection(direction: direction);
            ExpectSquareIndex(
                action: () => a.Neighbor(direction: direction),
                x: (x + dx),
                y: (y + dy)
            );
        }
        Assert.Equal(
            expected: a,
            actual: a.Rotate(turns: 4)
        );
        Assert.Equal(
            expected: a,
            actual: a.Conjugate().Conjugate()
        );
        Assert.Equal(
            expected: a,
            actual: a.Swap().Swap()
        );
        Assert.Equal(
            expected: a.Rotate(turns: 3),
            actual: a.Conjugate().Rotate(turns: 1).Conjugate()
        );
        Assert.Equal(
            expected: a,
            actual: (a + SquareIndex.AdditiveIdentity)
        );
        Assert.Equal(
            expected: a,
            actual: (a * SquareIndex.MultiplicativeIdentity)
        );
        CheckSquareSuccessor(value: value);
    }
    private static void CheckSquareSuccessor(long value) {
        if (value == SquareIndex.MaxValue) { return; }
        var a = new SquareIndex(value: value).ToCoordinate();
        var b = new SquareIndex(value: (value + 1)).ToCoordinate();

        Assert.Equal(
            expected: BigInteger.One,
            actual: (BigInteger.Abs(value: (((BigInteger)a.X) - b.X)) + BigInteger.Abs(value: (((BigInteger)a.Y) - b.Y)))
        );
    }

    public static string? SquareGridBoundaries() {
        Assert.Equal(
            actual: SquareCoordinate.NeighborCount,
            expected: 4
        );
        Assert.Equal(
            expected: default,
            actual: new SquareIndex(value: 0).ToCoordinate()
        );
        var example = SquareIndex.FromCoordinate(coordinate: new(
            X: 2,
            Y: 1
        ));

        Assert.Equal(
            expected: 11L,
            actual: example.Value
        );
        Assert.Equal(
            expected: 15L,
            actual: example.Rotate(turns: 1).Value
        );
        Assert.Equal(
            expected: 12L,
            actual: example.Neighbor(direction: 1).Value
        );
        Assert.Equal(
            expected: 4,
            actual: new SquareCoordinate(
                X: 2,
                Y: 2
            ).Length
        );
        Assert.Equal(
            expected: 2,
            actual: new SquareCoordinate(
                X: 2,
                Y: 2
            ).Radius
        );
        var seen = new HashSet<SquareCoordinate>();

        foreach (var (index, x, y) in Oracles.SquareWalk(count: 1089)) {
            var cell = new SquareCoordinate(
                X: ((int)x),
                Y: ((int)y)
            );

            Assert.True(condition: seen.Add(item: cell));
            Assert.Equal(
                expected: cell,
                actual: new SquareIndex(value: index).ToCoordinate()
            );
            Assert.Equal(
                expected: index,
                actual: SquareIndex.FromCoordinate(coordinate: cell).Value
            );
            CheckSquareSuccessor(value: index);
        }
        BigInteger maximum = SquareIndex.MaxRadius;

        Assert.Equal(
            expected: ((BigInteger)SquareIndex.MaxValue),
            actual: (BigInteger.Pow(
                exponent: 2,
                value: ((2 * maximum) + 1)
            ) - 1)
        );
        Assert.True(condition: ((BigInteger.Pow(
            exponent: 2,
            value: ((2 * maximum) + 3)
        ) - 1) > long.MaxValue));
        foreach (var value in new[] { long.MinValue, -1, (SquareIndex.MaxValue + 1), long.MaxValue }) {
            Assert.Throws<ArgumentOutOfRangeException>(
                paramName: "value",
                testCode: () => new SquareIndex(value: value)
            );
        }
        foreach (var radius in new[] { 1, 2, 3, 16, 65536, (SquareIndex.MaxRadius - 1), SquareIndex.MaxRadius }) {
            var start = ((long)BigInteger.Pow(
                exponent: 2,
                value: ((2 * ((BigInteger)radius)) - 1)
            ));
            var end = (((long)BigInteger.Pow(
                exponent: 2,
                value: ((2 * ((BigInteger)radius)) + 1)
            )) - 1);

            Assert.Equal(
                expected: new SquareCoordinate(
                    X: radius,
                    Y: (1 - radius)
                ),
                actual: new SquareIndex(value: start).ToCoordinate()
            );
            Assert.Equal(
                expected: new SquareCoordinate(
                    X: radius,
                    Y: -radius
                ),
                actual: new SquareIndex(value: end).ToCoordinate()
            );
            CheckSquareSuccessor(value: (start - 1));
            CheckSquareSuccessor(value: end);
            for (var corner = 1; (corner <= 4); ++corner) {
                for (var delta = -1; (delta <= 1); ++delta) {
                    var value = (start + ((((((2L * corner) * radius) - 1) + delta) + (8L * radius)) % (8L * radius)));

                    foreach (var factor in new[] { int.MinValue, -3, -1, 0, 1, 2, 3, int.MaxValue }) {
                        CheckSquareIndex(
                            factor: factor,
                            other: SquareIndex.MaxValue,
                            value: value
                        );
                    }
                }
            }
        }
        Assert.Equal(
            expected: (4L * SquareIndex.MaxRadius),
            actual: SquareIndex.Distance(
                left: new(value: SquareIndex.MaxValue),
                right: -new SquareIndex(value: SquareIndex.MaxValue)
            )
        );
        foreach (var x in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
            foreach (var y in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
                CheckSquareCoordinate(
                    new(
                        X: x,
                        Y: y
                    ),
                    new(
                        X: y,
                        Y: x
                    ),
                    factor: int.MinValue
                );
                ExpectSquareIndex(
                    action: () => SquareIndex.FromCoordinate(coordinate: new(
                        X: x,
                        Y: y
                    )),
                    x: x,
                    y: y
                );
            }
        }
        foreach (var raw in new[] { -163841L, -163840, -163839, -98304, -32769, -32768, -32767, 32767, 32768, 32769, 98304, 163840, long.MinValue, long.MaxValue }) {
            var rounded = Oracles.RoundRationalTiesToEven(
                denominator: 65536,
                numerator: raw
            );

            ExpectSquareCoordinate(
                action: () => SquareCoordinate.Round(
                    x: new(Value: raw),
                    y: new(Value: raw)
                ),
                x: rounded,
                y: rounded
            );
        }
        CheckSquareIndex(
            factor: int.MinValue,
            other: 0,
            value: 0
        );
        return null;
    }
}
