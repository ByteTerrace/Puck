using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claim bodies for <see cref="BinaryIntegerFunctions.TryAdd{T}"/> and
/// <see cref="BinaryIntegerFunctions.TryNarrow{TWide, TNarrow}"/>. Every expectation is exact
/// <see cref="BigInteger"/> arithmetic compared against the destination's range; nothing here shares the subject's
/// sign-bit test or its saturating round trip.</summary>
internal static class TryArithmeticClaims {
    private static readonly BigInteger TwoTo64 = (BigInteger.One << 64);
    private static readonly BigInteger TwoTo128 = (BigInteger.One << 128);

    private static BigInteger Wrap(BigInteger value, BigInteger modulus, bool signed) {
        var reduced = (((value % modulus) + modulus) % modulus);

        return ((signed && (reduced >= (modulus >> 1)))
            ? (reduced - modulus)
            : reduced
        );
    }
    private static string? CheckAdd<T>(T left, T right, BigInteger minimum, BigInteger maximum, BigInteger modulus, bool signed) where T : IBinaryInteger<T> {
        var exact = (BigInteger.CreateTruncating(value: left) + BigInteger.CreateTruncating(value: right));
        var representable = ((exact >= minimum) && (exact <= maximum));
        var accepted = left.TryAdd(
            right: right,
            sum: out var sum
        );
        var expected = (representable
            ? exact
            : Wrap(
                modulus: modulus,
                signed: signed,
                value: exact
            )
        );

        if (accepted != representable) {
            return $"TryAdd<{typeof(T).Name}>({left}, {right}) returned {accepted}; the exact sum {exact} is {(representable ? "" : "not ")}representable";
        }

        return ((BigInteger.CreateTruncating(value: sum) != expected)
            ? $"TryAdd<{typeof(T).Name}>({left}, {right}) left {sum}, expected {expected}"
            : null
        );
    }
    // The unbounded carrier never overflows, so the add must accept every pair and leave the exact sum.
    private static string? CheckUnboundedAdd(BigInteger left, BigInteger right) {
        var accepted = left.TryAdd(
            right: right,
            sum: out var sum
        );

        return ((!accepted || (sum != (left + right)))
            ? $"TryAdd<BigInteger>({left}, {right}) returned {accepted} with {sum}, expected true with the exact sum"
            : null
        );
    }
    private static string? CheckNarrow<TWide, TNarrow>(TWide value, BigInteger minimum, BigInteger maximum) where TWide : IBinaryInteger<TWide> where TNarrow : IBinaryInteger<TNarrow> {
        var exact = BigInteger.CreateTruncating(value: value);
        var representable = ((exact >= minimum) && (exact <= maximum));
        var accepted = value.TryNarrow(result: out TNarrow result);
        var expected = (representable
            ? exact
            : BigInteger.Zero
        );

        if (accepted != representable) {
            return $"TryNarrow<{typeof(TWide).Name}, {typeof(TNarrow).Name}>({value}) returned {accepted}; the value is {(representable ? "" : "not ")}in range";
        }

        return ((BigInteger.CreateTruncating(value: result) != expected)
            ? $"TryNarrow<{typeof(TWide).Name}, {typeof(TNarrow).Name}>({value}) left {result}, expected {expected}"
            : null
        );
    }
    private static string? CheckLane(long x, long y) {
        var wideLeft = ((((Int128)x) << 64) + unchecked((ulong)y));
        var wideRight = ((((Int128)y) << 64) + unchecked((ulong)x));
        var shifted = ((((Int128)x) << 1) + y);
        var product = (((BigInteger)x) * y);

        return (CheckAdd(
            left: x,
            maximum: long.MaxValue,
            minimum: long.MinValue,
            modulus: TwoTo64,
            right: y,
            signed: true
        ) ?? (CheckAdd(
            left: unchecked((int)x),
            maximum: int.MaxValue,
            minimum: int.MinValue,
            modulus: (BigInteger.One << 32),
            right: unchecked((int)y),
            signed: true
        ) ?? (CheckAdd(
            left: unchecked((ulong)x),
            maximum: ulong.MaxValue,
            minimum: BigInteger.Zero,
            modulus: TwoTo64,
            right: unchecked((ulong)y),
            signed: false
        ) ?? (CheckAdd(
            left: unchecked((byte)x),
            maximum: byte.MaxValue,
            minimum: BigInteger.Zero,
            modulus: (BigInteger.One << 8),
            right: unchecked((byte)y),
            signed: false
        ) ?? (CheckAdd(
            left: wideLeft,
            maximum: ((BigInteger)Int128.MaxValue),
            minimum: ((BigInteger)Int128.MinValue),
            modulus: TwoTo128,
            right: wideRight,
            signed: true
        ) ?? (CheckUnboundedAdd(
            left: product,
            right: (((BigInteger)x) + y)
        ) ?? (CheckNarrow<Int128, long>(
            maximum: long.MaxValue,
            minimum: long.MinValue,
            value: shifted
        ) ?? (CheckNarrow<Int128, long>(
            maximum: long.MaxValue,
            minimum: long.MinValue,
            value: wideLeft
        ) ?? (CheckNarrow<BigInteger, long>(
            maximum: long.MaxValue,
            minimum: long.MinValue,
            value: product
        ) ?? (CheckNarrow<BigInteger, long>(
            maximum: long.MaxValue,
            minimum: long.MinValue,
            value: (((BigInteger)x) + y)
        ) ?? (CheckNarrow<long, int>(
            maximum: int.MaxValue,
            minimum: int.MinValue,
            value: x
        ) ?? (CheckNarrow<long, ulong>(
            maximum: ulong.MaxValue,
            minimum: BigInteger.Zero,
            value: x
        ) ?? CheckNarrow<ulong, long>(
            maximum: long.MaxValue,
            minimum: long.MinValue,
            value: unchecked((ulong)x)
        )))))))))))));
    }

    /// <summary>Sweeps both members over every lane: the add at five fixed widths (signed and unsigned, 8 through 128
    /// bits) and the unbounded carrier, and the narrowing from wider, narrower-signed and unsigned sources.</summary>
    /// <param name="left">The first operand's lanes.</param>
    /// <param name="right">The second operand's lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? AddAndNarrowMatchExactArithmetic(long[] left, long[] right) {
        for (var lane = 0; (lane < left.Length); ++lane) {
            if (CheckLane(
                x: left[lane],
                y: right[lane]
            ) is { } failure) {
                return failure;
            }
        }

        return null;
    }
    /// <summary>Pins the seams both members decide on: each carrier's extremes and their neighbours.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SeamsAreExact() {
        long[] longSeams = [long.MinValue, (long.MinValue + 1), -1L, 0L, 1L, (long.MaxValue - 1), long.MaxValue];

        foreach (var x in longSeams) {
            foreach (var y in longSeams) {
                if (CheckLane(
                    x: x,
                    y: y
                ) is { } failure) {
                    return failure;
                }
            }
        }

        Int128[] wideSeams = [
            Int128.MinValue,
            (((Int128)long.MinValue) - 1),
            ((Int128)long.MinValue),
            ((Int128)long.MaxValue),
            (((Int128)long.MaxValue) + 1),
            Int128.MaxValue,
        ];

        foreach (var value in wideSeams) {
            if (CheckNarrow<Int128, long>(
                maximum: long.MaxValue,
                minimum: long.MinValue,
                value: value
            ) is { } failure) {
                return failure;
            }
        }

        BigInteger[] unboundedSeams = [
            (((BigInteger)long.MinValue) - 1),
            ((BigInteger)long.MinValue),
            ((BigInteger)long.MaxValue),
            (((BigInteger)long.MaxValue) + 1),
            (BigInteger.One << 200),
            -(BigInteger.One << 200),
        ];

        foreach (var value in unboundedSeams) {
            if (CheckNarrow<BigInteger, long>(
                maximum: long.MaxValue,
                minimum: long.MinValue,
                value: value
            ) is { } failure) {
                return failure;
            }
        }

        return CheckAdd(
            left: ulong.MaxValue,
            maximum: ulong.MaxValue,
            minimum: BigInteger.Zero,
            modulus: TwoTo64,
            right: 1UL,
            signed: false
        );
    }
}
