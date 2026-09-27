using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claim bodies for <see cref="BinaryIntegerFunctions.TryAdd{T}"/>,
/// <see cref="BinaryIntegerFunctions.TryMultiply{T}"/>, <see cref="BinaryIntegerFunctions.TryExponentiate{T}"/> and
/// <see cref="BinaryIntegerFunctions.TryNarrow{TWide, TNarrow}"/>. Every expectation is exact
/// <see cref="BigInteger"/> arithmetic compared against the destination's range; nothing here shares the subject's
/// sign-bit tests, its division check, its squaring schedule or its saturating round trip.</summary>
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
    private static string? CheckMultiply<T>(T left, T right, BigInteger minimum, BigInteger maximum, BigInteger modulus, bool signed) where T : IBinaryInteger<T> {
        var exact = (BigInteger.CreateTruncating(value: left) * BigInteger.CreateTruncating(value: right));
        var representable = ((exact >= minimum) && (exact <= maximum));
        var accepted = left.TryMultiply(
            product: out var product,
            right: right
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
            return $"TryMultiply<{typeof(T).Name}>({left}, {right}) returned {accepted}; the exact product {exact} is {(representable ? "" : "not ")}representable";
        }

        return ((BigInteger.CreateTruncating(value: product) != expected)
            ? $"TryMultiply<{typeof(T).Name}>({left}, {right}) left {product}, expected {expected}"
            : null
        );
    }
    // The power by repeated multiplication in arbitrary width, never by squaring, so the claim shares no schedule
    // with the subject.
    private static string? CheckExponentiate<T>(T value, int exponent, BigInteger minimum, BigInteger maximum) where T : IBinaryInteger<T> {
        var exact = BigInteger.One;
        var factor = BigInteger.CreateTruncating(value: value);

        for (var step = 0; ((step < exponent) && (BigInteger.Abs(value: exact) <= (maximum - minimum))); ++step) { exact *= factor; }

        var representable = ((exact >= minimum) && (exact <= maximum));
        var accepted = value.TryExponentiate(
            exponent: T.CreateChecked(value: exponent),
            power: out var power
        );
        var expected = (representable
            ? exact
            : BigInteger.Zero
        );

        if (accepted != representable) {
            return $"TryExponentiate<{typeof(T).Name}>({value}, {exponent}) returned {accepted}; the exact power is {(representable ? "" : "not ")}representable";
        }

        return ((BigInteger.CreateTruncating(value: power) != expected)
            ? $"TryExponentiate<{typeof(T).Name}>({value}, {exponent}) left {power}, expected {expected}"
            : null
        );
    }
    private static string? CheckMultiplyLane(long x, long y) {
        var wideLeft = ((((Int128)x) << 32) + y);
        var wideRight = ((((Int128)y) << 40) - x);
        // A small base and exponent drawn from the lane, so the sweep crosses the overflow edge for every base size
        // rather than overflowing at once.
        var smallBase = (x % 70L);
        var exponent = ((int)((((ulong)y) >> 1) % 130UL));

        return (CheckMultiply(
            left: x,
            maximum: long.MaxValue,
            minimum: long.MinValue,
            modulus: TwoTo64,
            right: y,
            signed: true
        ) ?? (CheckMultiply(
            left: unchecked((int)x),
            maximum: int.MaxValue,
            minimum: int.MinValue,
            modulus: (BigInteger.One << 32),
            right: unchecked((int)y),
            signed: true
        ) ?? (CheckMultiply(
            left: unchecked((ulong)x),
            maximum: ulong.MaxValue,
            minimum: BigInteger.Zero,
            modulus: TwoTo64,
            right: unchecked((ulong)y),
            signed: false
        ) ?? (CheckMultiply(
            left: unchecked((byte)x),
            maximum: byte.MaxValue,
            minimum: BigInteger.Zero,
            modulus: (BigInteger.One << 8),
            right: unchecked((byte)y),
            signed: false
        ) ?? (CheckMultiply(
            left: wideLeft,
            maximum: ((BigInteger)Int128.MaxValue),
            minimum: ((BigInteger)Int128.MinValue),
            modulus: TwoTo128,
            right: wideRight,
            signed: true
        ) ?? (CheckExponentiate(
            exponent: exponent,
            maximum: long.MaxValue,
            minimum: long.MinValue,
            value: smallBase
        ) ?? (CheckExponentiate(
            exponent: (exponent % 40),
            maximum: int.MaxValue,
            minimum: int.MinValue,
            value: ((int)smallBase)
        ) ?? CheckExponentiate(
            exponent: exponent,
            maximum: ulong.MaxValue,
            minimum: BigInteger.Zero,
            value: ((ulong)Math.Abs(value: smallBase))
        ))))))));
    }

    /// <summary>Sweeps the checked multiply at five fixed widths and the power at three, each against exact arithmetic.</summary>
    /// <param name="left">The first operand's lanes.</param>
    /// <param name="right">The second operand's lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MultiplyAndExponentiateMatchExactArithmetic(long[] left, long[] right) {
        for (var lane = 0; (lane < left.Length); ++lane) {
            if (CheckMultiplyLane(
                x: left[lane],
                y: right[lane]
            ) is { } failure) {
                return failure;
            }
        }

        return null;
    }
    /// <summary>Pins the multiply's minus-one and extreme seams, the powers at the edge of the signed carrier, the
    /// unbounded carrier, and the negative-exponent refusal.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MultiplyAndExponentiateSeamsAreExact() {
        long[] longSeams = [long.MinValue, (long.MinValue + 1), -3037000500L, -3037000499L, -2L, -1L, 0L, 1L, 2L, 3037000499L, 3037000500L, (long.MaxValue - 1), long.MaxValue];

        foreach (var x in longSeams) {
            foreach (var y in longSeams) {
                if (CheckMultiplyLane(
                    x: x,
                    y: y
                ) is { } failure) {
                    return failure;
                }
            }
        }

        (long Base, int Exponent)[] powerSeams = [(-2L, 63), (2L, 63), (2L, 62), (-2L, 64), (3037000499L, 2), (3037000500L, 2), (-3037000499L, 2), (-1L, 127), (-1L, 128), (0L, 0), (0L, 126), (1L, 127), (7L, 22), (7L, 23)];

        foreach (var (value, exponent) in powerSeams) {
            if (CheckExponentiate(
                exponent: exponent,
                maximum: long.MaxValue,
                minimum: long.MinValue,
                value: value
            ) is { } failure) {
                return failure;
            }
        }

        var hugeAccepted = BigInteger.MinusOne.TryExponentiate(
            exponent: (BigInteger.One << 70),
            power: out var hugePower
        );

        if (!hugeAccepted || !hugePower.IsOne) {
            return $"TryExponentiate<BigInteger>(-1, 2^70) returned {hugeAccepted} with {hugePower}, expected true with one";
        }
        if (!(BigInteger.One << 70).TryMultiply(
            product: out var unbounded,
            right: -(BigInteger.One << 70)
        ) || (unbounded != -(BigInteger.One << 140))) {
            return "TryMultiply<BigInteger>(2^70, -2^70) did not accept with the exact product";
        }

        try {
            _ = 2L.TryExponentiate(
                exponent: -1L,
                power: out _
            );

            return "TryExponentiate<long>(2, -1) did not refuse the negative exponent";
        } catch (ArgumentOutOfRangeException exception) when ((exception.ParamName == "exponent")) {
            return null;
        }
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
