using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? EncodedOperations(long[] left, long[] right) {
        var raw = unchecked((ulong)left[0]);
        var amount = unchecked((ulong)right[0]);

        CheckElegantOperations(
            amount: ((byte)amount),
            value: ((byte)raw)
        );
        CheckElegantOperations(
            amount: ((ushort)amount),
            value: ((ushort)raw)
        );
        CheckElegantOperations(
            amount: ((uint)amount),
            value: ((uint)raw)
        );
        CheckElegantOperations(
            amount: amount,
            value: raw
        );
        CheckElegantOperations(
            amount: ((nuint)amount),
            value: ((nuint)raw)
        );
        CheckElegantOperations(
            value: (((UInt128)raw) << 64) | unchecked((ulong)left[1]),
            amount: ((UInt128)amount)
        );
        CheckEncodedHexOperations(
            value: HexIndexRaw(raw: left[0]),
            factor: ((int)right[0])
        );
        CheckEncodedHexOperations(
            value: ((long)(raw % 3169)),
            factor: ((int)(right[0] % 9))
        );
        return null;
    }

    private static void CheckElegantOperations<T>(T value, T amount) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var (x, y) = Oracles.ElegantCoordinate(index: BigInteger.CreateChecked(value: value));
        var k = BigInteger.CreateChecked(value: amount);
        var actual = value.ElegantUnpair<T, T>();

        Assert.Equal(
            expected: x,
            actual: BigInteger.CreateChecked(value: actual.x)
        );
        Assert.Equal(
            expected: y,
            actual: BigInteger.CreateChecked(value: actual.y)
        );
        Assert.Equal(
            expected: value,
            actual: actual.x.ElegantPair<T, T>(other: actual.y)
        );
        Assert.Equal(
            expected: BigInteger.Max(
                left: x,
                right: y
            ),
            actual: BigInteger.CreateChecked(value: value.ElegantMaximum())
        );
        Assert.Equal(
            expected: BigInteger.Min(
                left: x,
                right: y
            ),
            actual: BigInteger.CreateChecked(value: value.ElegantMinimum())
        );
        Assert.Equal(
            expected: BigInteger.Abs(value: (x - y)),
            actual: BigInteger.CreateChecked(value: value.ElegantDifference())
        );
        Assert.Equal(
            expected: (x + y),
            actual: BigInteger.CreateChecked(value: value.ElegantSum())
        );
        ExpectElegantResult(
            action: () => value.ElegantSwap(),
            expected: Oracles.ElegantIndex(
                x: y,
                y: x
            )
        );
        ExpectElegantResult(
            action: () => value.ElegantTranslate(amount: amount),
            expected: Oracles.ElegantIndex(
                x: (x + k),
                y: (y + k)
            )
        );
        ExpectElegantResult(
            action: () => value.ElegantScale(factor: amount),
            expected: Oracles.ElegantIndex(
                x: (x * k),
                y: (y * k)
            )
        );
    }
    private static void ExpectElegantResult<T>(Func<T> action, BigInteger expected) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if (expected > BigInteger.CreateChecked(value: ~T.Zero)) { Assert.Throws<OverflowException>(testCode: () => action()); } else { Assert.Equal(
            expected: T.CreateChecked(value: expected),
            actual: action()
        ); }
    }
    private static void CheckEncodedHexOperations(long value, int factor) {
        var subject = new HexagonalIndex(value: value);

        var (q, r) = Oracles.HexIndexCoordinate(index: value);
        ExpectHexIndex(
            action: subject.Swap,
            q: r,
            r: q
        );
        ExpectHexIndex(
            action: () => subject.Scale(factor: factor),
            q: (q * factor),
            r: (r * factor)
        );
        ExpectHexIndex(
            action: () => subject.Translate(displacement: new(
                Q: factor,
                R: factor
            )),
            q: (q + factor),
            r: (r + factor)
        );
        Assert.Equal(
            expected: subject,
            actual: subject.Swap().Swap()
        );
        Assert.Equal(
            expected: ((long)(((q * q) - (q * r)) + (r * r))),
            actual: subject.Norm
        );
    }

    public static string? EncodedOperationsBoundaries() {
        Assert.Equal(
            expected: 5UL,
            actual: 2u.ElegantPair<uint, ulong>(other: 1u)
        );
        Assert.Equal(
            expected: 7UL,
            actual: 5UL.ElegantSwap()
        );
        Assert.Equal(
            expected: 13UL,
            actual: 5UL.ElegantTranslate(amount: 1UL)
        );
        Assert.Equal(
            expected: 18UL,
            actual: 5UL.ElegantScale(factor: 2UL)
        );
        foreach (var index in new UInt128[] { 0, 1, 2, (UInt128.One << 64), UInt128.MaxValue }) {
            foreach (var amount in new UInt128[] { 0, 1, 2, (UInt128.One << 64), UInt128.MaxValue }) {
                CheckElegantOperations(
                    amount: amount,
                    value: index
                );
            }
        }
        // Complete byte carrier, with scales straddling both parity and overflow boundaries.
        for (var index = 0; (index <= byte.MaxValue); ++index) {
            foreach (var amount in new byte[] { 0, 1, 2, 3, 15, 16, 255 }) {
                CheckElegantOperations(
                    amount: amount,
                    value: ((byte)index)
                );
            }
        }
        foreach (var radius in HexIndexBoundaryRadii) {
            foreach (var side in Enumerable.Range(
                count: 6,
                start: 0
            )) {
                var start = (1 + ((3L * radius) * (radius - 1)));

                foreach (var offset in new[] { 0L, (radius - 1L), ((long)radius), ((3L * radius) - 1), ((6L * radius) - 1) }) {
                    var value = (start + ((offset + (((long)side) * radius)) % (6L * radius)));

                    foreach (var factor in new[] { int.MinValue, -3, -1, 0, 1, 2, 3, int.MaxValue }) {
                        CheckEncodedHexOperations(
                            factor: factor,
                            value: value
                        );
                    }
                }
            }
        }
        CheckEncodedHexOperations(
            factor: int.MinValue,
            value: 0
        );
        CheckEncodedHexOperations(
            factor: int.MaxValue,
            value: 0
        );
        foreach (var q in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
            foreach (var r in new[] { int.MinValue, -1, 0, 1, int.MaxValue }) {
                Assert.Equal(
                    expected: FormattableString.Invariant(formattable: $"HexagonalCoordinate {{ Q = {q}, R = {r} }}"),
                    actual: new HexagonalCoordinate(
                        Q: q,
                        R: r
                    ).ToString()
                );
            }
        }
        return null;
    }
}
