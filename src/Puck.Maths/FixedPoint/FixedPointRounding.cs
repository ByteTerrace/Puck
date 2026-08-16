using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The round-half-to-even correction every fixed-point carrier applies after a truncating narrowing.
/// </summary>
public static class FixedPointRounding {
    /// <summary>Corrects a truncated result to the nearest representable value, with ties resolved to even.</summary>
    /// <typeparam name="T">The binary integer the truncated result and the discarded remainder are carried in.</typeparam>
    /// <param name="truncated">The truncated result; its low bit carries the tie parity.</param>
    /// <param name="remainder">The discarded remainder, in whatever domain the caller measures it.</param>
    /// <param name="threshold">The half-unit <paramref name="remainder"/> ties against, in that same domain.</param>
    /// <returns><paramref name="truncated"/> incremented when <paramref name="remainder"/> exceeds <paramref name="threshold"/>, or ties it with an odd <paramref name="truncated"/>; otherwise <paramref name="truncated"/> unchanged. The increment wraps rather than throwing.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static T RoundHalfToEven<T>(T truncated, T remainder, T threshold) where T : IBinaryInteger<T> =>
        unchecked((truncated + ((remainder > threshold).As<T>() | ((remainder == threshold).As<T>() & truncated & T.One))));

    /// <summary>Chooses the nearest of two adjacent integer results, resolving an exact tie to the even result.</summary>
    /// <typeparam name="T">The binary integer carrying the result and both non-negative distances.</typeparam>
    /// <param name="truncated">The lower-magnitude result; its low bit carries the tie parity.</param>
    /// <param name="distanceToTruncated">The exact distance from the source value to
    /// <paramref name="truncated"/>.</param>
    /// <param name="distanceToNext">The exact distance from the source value to the next higher-magnitude result.</param>
    /// <returns><paramref name="truncated"/> incremented when the next result is closer, or when the two distances
    /// tie and <paramref name="truncated"/> is odd; otherwise <paramref name="truncated"/> unchanged. The increment
    /// wraps rather than throwing.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static T RoundToNearestTiesToEven<T>(T truncated, T distanceToTruncated, T distanceToNext)
        where T : IBinaryInteger<T> =>
        unchecked((truncated + ((distanceToTruncated > distanceToNext).As<T>() |
            ((distanceToTruncated == distanceToNext).As<T>() & truncated & T.One))));
    /// <summary>Rounds the exact rational <c>numerator · 2^fractionBitCount / denominator</c> to a raw carrier, once,
    /// to nearest with ties to even.</summary>
    /// <param name="numerator">The exact numerator.</param>
    /// <param name="denominator">The exact denominator, which must be non-zero.</param>
    /// <param name="fractionBitCount">The result's fraction bit count, which must be non-negative. Zero is the case
    /// where the numerator already sits at the result's scale.</param>
    /// <param name="result">The rounded raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the denominator is zero, the fraction bit count is negative, or the
    /// rounded value leaves the signed 64-bit raw.</returns>
    /// <remarks>The scale shift is folded onto the numerator ahead of the one division, so nothing is rounded before
    /// it, and the tie is decided against the exact distance to the next multiple rather than by doubling the
    /// remainder — the formulation every sibling kernel in this family uses. Both the exact-rational mass-property
    /// chain here and <c>Puck.Physics</c>'s softness chain round through this body, so the two cannot drift onto
    /// different tie rules.</remarks>
    public static bool TryRoundRational(BigInteger numerator, BigInteger denominator, int fractionBitCount, out long result) {
        if (
            denominator.IsZero ||
            (fractionBitCount < 0)
        ) {
            result = 0L;

            return false;
        }

        var negative = ((numerator.Sign < 0) != (denominator.Sign < 0));
        var magnitude = BigInteger.Abs(value: numerator);
        var divisor = BigInteger.Abs(value: denominator);

        if (magnitude.IsZero) {
            result = 0L;

            return true;
        }

        // Refuse an obviously over-wide quotient before materializing the shifted numerator. Without this guard a
        // public call such as (1, 1, int.MaxValue) asks BigInteger for hundreds of megabytes only to discover after
        // division that the result cannot fit a long. If this coarse bound does not prove overflow, the shifted
        // numerator is at most 64 bits wider than the denominator the caller already supplied.
        if (((magnitude.GetBitLength() + fractionBitCount) - divisor.GetBitLength()) > 64L) {
            result = 0L;

            return false;
        }

        magnitude <<= fractionBitCount;
        var quotient = BigInteger.DivRem(
            dividend: magnitude,
            divisor: divisor,
            remainder: out var remainder
        );
        var distanceToNext = (divisor - remainder);

        quotient = RoundToNearestTiesToEven(
            distanceToNext: distanceToNext,
            distanceToTruncated: remainder,
            truncated: quotient
        );

        if (negative) {
            quotient = -quotient;
        }

        if (
            (quotient < long.MinValue) ||
            (quotient > long.MaxValue)
        ) {
            result = 0L;

            return false;
        }

        result = ((long)quotient);

        return true;
    }
}
