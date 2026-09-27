using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claim bodies for <see cref="NumberTheoryFunctions.ModularPower(ulong, ulong, ulong)"/>,
/// <see cref="NumberTheoryFunctions.ModularInverse(ulong, ulong)"/> and
/// <see cref="NumberTheoryFunctions.ExtendedGreatestCommonDivisor(long, long)"/>. Every expectation is
/// <see cref="BigInteger"/> arithmetic from the runtime library: its own modular exponentiation, its own greatest common
/// divisor, and plain products and remainders. Nothing here calls a Puck.Maths member but the subject.</summary>
internal static class WordModularClaims {
    private static readonly ulong[] ModulusSeams = [
        1UL, 2UL, 3UL, 4UL, 8UL, 9UL, 12UL, 65_536UL, 65_537UL, 4_294_967_295UL, 4_294_967_296UL, 4_294_967_297UL,
        (1UL << 62), ((1UL << 63) - 25UL), (1UL << 63), ((1UL << 63) + 1UL), (ulong.MaxValue - 58UL), (ulong.MaxValue - 1UL), ulong.MaxValue,
    ];

    // A modulus drawn from a lane at every magnitude: the lane's bits shifted down by a lane-chosen amount, so small,
    // mid-sized and full-width moduli, odd and even, all occur.
    private static ulong ModulusOf(long lane) => (unchecked((ulong)lane) >> ((int)(unchecked((ulong)lane) % 61UL)));
    private static string? CheckPower(ulong value, ulong exponent, ulong modulus) {
        if (modulus == 0UL) {
            return null;
        }

        var expected = BigInteger.ModPow(
            exponent: exponent,
            modulus: modulus,
            value: value
        );
        var actual = NumberTheoryFunctions.ModularPower(
            exponent: exponent,
            modulus: modulus,
            value: value
        );

        return ((actual != expected)
            ? $"ModularPower({value}, {exponent}, {modulus}) was {actual}, expected {expected}"
            : null
        );
    }
    private static string? CheckInverse(ulong value, ulong modulus) {
        if (modulus < 2UL) {
            return null;
        }

        var coprime = BigInteger.GreatestCommonDivisor(
            left: value,
            right: modulus
        ).IsOne;

        try {
            var inverse = NumberTheoryFunctions.ModularInverse(
                modulus: modulus,
                value: value
            );

            if (!coprime) {
                return $"ModularInverse({value}, {modulus}) answered {inverse} for a value sharing a factor with the modulus";
            }
            if (
                (inverse == 0UL) ||
                (inverse >= modulus) ||
                !((((BigInteger)value) * inverse) % modulus).IsOne
            ) {
                return $"ModularInverse({value}, {modulus}) was {inverse}, which does not multiply back to one in [1, modulus)";
            }

            return null;
        } catch (ArgumentException exception) when (((exception is not ArgumentOutOfRangeException) && (exception.ParamName == "value"))) {
            return (coprime
                ? $"ModularInverse({value}, {modulus}) refused a value coprime to the modulus"
                : null
            );
        }
    }
    private static string? CheckBezout(long value, long other) {
        if (
            (value == long.MinValue) ||
            (other == long.MinValue)
        ) {
            return null;
        }

        var (divisor, x, y) = NumberTheoryFunctions.ExtendedGreatestCommonDivisor(
            other: other,
            value: value
        );
        var expected = BigInteger.GreatestCommonDivisor(
            left: value,
            right: other
        );

        if (divisor != expected) {
            return $"ExtendedGreatestCommonDivisor({value}, {other}) gave divisor {divisor}, expected {expected}";
        }
        if (((((BigInteger)value) * x) + (((BigInteger)other) * y)) != expected) {
            return $"ExtendedGreatestCommonDivisor({value}, {other}) gave ({x}, {y}), which do not combine to {expected}";
        }
        if (other == 0L) {
            return (((x != Math.Sign(value: value)) || (y != 0L))
                ? $"ExtendedGreatestCommonDivisor({value}, 0) gave ({x}, {y}), expected (sign, 0)"
                : null
            );
        }

        // The least-magnitude coefficient: x lies in (-|b|/2g, |b|/2g].
        var reach = (BigInteger.Abs(value: other) / expected);
        var twiceX = (2 * ((BigInteger)x));

        return (((twiceX > reach) || (twiceX <= -reach))
            ? $"ExtendedGreatestCommonDivisor({value}, {other}) gave x = {x}, outside (-|b|/2g, |b|/2g] for |b|/g = {reach}"
            : null
        );
    }
    private static string? CheckLane(long left, long right) {
        var modulus = ModulusOf(lane: right);

        return (CheckPower(
            exponent: unchecked((ulong)right),
            modulus: modulus,
            value: unchecked((ulong)left)
        ) ?? (CheckPower(
            exponent: (unchecked((ulong)left) >> 48),
            modulus: ModulusOf(lane: left),
            value: unchecked((ulong)right)
        ) ?? (CheckInverse(
            modulus: modulus,
            value: unchecked((ulong)left)
        ) ?? (CheckInverse(
            modulus: ModulusOf(lane: left),
            value: unchecked((ulong)right)
        ) ?? (CheckBezout(
            other: right,
            value: left
        ) ?? CheckBezout(
            other: (right >> ((int)(unchecked((ulong)left) % 63UL))),
            value: left
        ))))));
    }

    /// <summary>Sweeps the modular power against the runtime's exponentiation, the inverse against its defining
    /// identity and against coprimality, and the extended divisor against Bézout's identity and the least-magnitude
    /// bound, over moduli of every magnitude drawn from the lanes.</summary>
    /// <param name="left">The first operand's lanes.</param>
    /// <param name="right">The second operand's lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? WordModularArithmeticMatchesBigInteger(long[] left, long[] right) {
        for (var lane = 0; (lane < left.Length); ++lane) {
            if (CheckLane(
                left: left[lane],
                right: right[lane]
            ) is { } failure) {
                return failure;
            }
        }

        return null;
    }
    /// <summary>Pins every modulus shape at its seams, hand-derived rows, and each refusal by exception type and
    /// parameter name.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? WordModularSeamsAndRefusals() {
        foreach (var modulus in ModulusSeams) {
            foreach (var value in new ulong[] { 0UL, 1UL, 2UL, 3UL, 5UL, 7UL, (modulus - 1UL), modulus, (modulus + 1UL), (ulong.MaxValue - 1UL), ulong.MaxValue }) {
                foreach (var exponent in new ulong[] { 0UL, 1UL, 2UL, 63UL, (ulong.MaxValue - 1UL), ulong.MaxValue }) {
                    if (CheckPower(
                        exponent: exponent,
                        modulus: modulus,
                        value: value
                    ) is { } failure) {
                        return failure;
                    }
                }
                if (CheckInverse(
                    modulus: modulus,
                    value: value
                ) is { } inverseFailure) {
                    return inverseFailure;
                }
            }
        }

        // Hand-derived rows, each multiplied back: 3·5 = 2·7 + 1; 3·171 = 513 = 512 + 1 at a power-of-two modulus; and
        // 2·2^63 = 2^64 = (2^64 − 1) + 1 at the widest odd modulus.
        (ulong Value, ulong Modulus, ulong Inverse)[] rows = [(3UL, 7UL, 5UL), (3UL, 512UL, 171UL), (2UL, ulong.MaxValue, (1UL << 63))];

        foreach (var (value, modulus, inverse) in rows) {
            var actual = NumberTheoryFunctions.ModularInverse(
                modulus: modulus,
                value: value
            );

            if (actual != inverse) {
                return $"ModularInverse({value}, {modulus}) was {actual}, expected the hand-derived {inverse}";
            }
        }

        // 2^10 = 1024 ≡ 24 (mod 1000), and every base to the zeroth power modulo one is the one residue there, zero.
        if (NumberTheoryFunctions.ModularPower(
            exponent: 10UL,
            modulus: 1000UL,
            value: 2UL
        ) != 24UL) {
            return "ModularPower(2, 10, 1000) was not 24";
        }
        if (NumberTheoryFunctions.ModularPower(
            exponent: 0UL,
            modulus: 1UL,
            value: 0UL
        ) != 0UL) {
            return "ModularPower(0, 0, 1) was not zero";
        }

        // 240·(−9) + 46·47 = −2160 + 2162 = 2, and −9 is the least-magnitude inverse class of 120 modulo 23.
        if (NumberTheoryFunctions.ExtendedGreatestCommonDivisor(
            other: 46L,
            value: 240L
        ) != (2L, -9L, 47L)) {
            return "ExtendedGreatestCommonDivisor(240, 46) was not (2, -9, 47)";
        }
        if (NumberTheoryFunctions.ExtendedGreatestCommonDivisor(
            other: 0L,
            value: 0L
        ) != (0L, 0L, 0L)) {
            return "ExtendedGreatestCommonDivisor(0, 0) was not (0, 0, 0)";
        }

        long[] bezoutSeams = [(long.MinValue + 1L), -4_611_686_018_427_387_904L, -12L, -1L, 0L, 1L, 2L, 18L, 4_611_686_018_427_387_903L, (long.MaxValue - 1L), long.MaxValue];

        foreach (var value in bezoutSeams) {
            foreach (var other in bezoutSeams) {
                if (CheckBezout(
                    other: other,
                    value: value
                ) is { } failure) {
                    return failure;
                }
            }
        }

        return (Refuses<ArgumentOutOfRangeException>(
            action: static () => _ = NumberTheoryFunctions.ModularPower(exponent: 1UL, modulus: 0UL, value: 1UL),
            parameter: "modulus"
        ) ?? (Refuses<ArgumentOutOfRangeException>(
            action: static () => _ = NumberTheoryFunctions.ModularInverse(modulus: 1UL, value: 1UL),
            parameter: "modulus"
        ) ?? (Refuses<ArgumentOutOfRangeException>(
            action: static () => _ = NumberTheoryFunctions.ModularInverse(modulus: 0UL, value: 1UL),
            parameter: "modulus"
        ) ?? (Refuses<ArgumentException>(
            action: static () => _ = NumberTheoryFunctions.ModularInverse(modulus: 9UL, value: 6UL),
            parameter: "value"
        ) ?? (Refuses<ArgumentException>(
            action: static () => _ = NumberTheoryFunctions.ModularInverse(modulus: 8UL, value: 4UL),
            parameter: "value"
        ) ?? (Refuses<ArgumentException>(
            action: static () => _ = NumberTheoryFunctions.ModularInverse(modulus: 12UL, value: 9UL),
            parameter: "value"
        ) ?? (Refuses<OverflowException>(
            action: static () => _ = NumberTheoryFunctions.ExtendedGreatestCommonDivisor(other: 3L, value: long.MinValue),
            parameter: null
        ) ?? Refuses<OverflowException>(
            action: static () => _ = NumberTheoryFunctions.ExtendedGreatestCommonDivisor(other: long.MinValue, value: 3L),
            parameter: null
        ))))))));
    }

    private static string? Refuses<TException>(Action action, string? parameter) where TException : Exception {
        try {
            action();
        } catch (TException exception) when (((exception.GetType() == typeof(TException)) && ((parameter is null) || ((exception as ArgumentException)?.ParamName == parameter)))) {
            return null;
        } catch (Exception exception) {
            return $"expected {typeof(TException).Name} naming '{parameter}', got {exception.GetType().Name}: {exception.Message}";
        }

        return $"expected {typeof(TException).Name} naming '{parameter}', but the call returned";
    }
}
