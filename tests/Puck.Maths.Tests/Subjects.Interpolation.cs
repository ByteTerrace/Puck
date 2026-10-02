using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="FixedQ4816.Smoothstep"/> to the law suite.</summary>
internal static partial class Subjects {
    // The subject's own bound beyond the half ULP is 1.5·2⁻⁴⁶ ULP; a value whose exact curve lies within 2⁻⁴⁴ ULP of a
    // rounding midpoint may round either way, and only there.
    private const int SmoothstepTieBandBits = 44;

    /// <summary>Smoothstep is the correct rounding of the exact curve outside a 2⁻⁴⁴-ULP band about each midpoint, a
    /// true tie goes to even, and it is monotone in the value between its edges.</summary>
    /// <param name="left">The two edges' raws.</param>
    /// <param name="right">Two raws: a value taken as is, and the bits of a fraction placing a second value inside
    /// the edges.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedSmoothstepCorrectlyRounded(long[] left, long[] right) {
        var edge0 = left[0];
        var edge1 = left[1];

        if (SmoothstepAgainstOracle(edge0: edge0, edge1: edge1, value: right[0]) is { } outside) {
            return outside;
        }

        // A value placed inside the edges at the fraction right[1]/2⁶⁴, so the interior is swept even when the edges
        // lie across the whole carrier, and its two raw neighbours, which must not reverse the curve's direction.
        var span = (((BigInteger)edge1) - edge0);
        var inside = ((long)(edge0 + ((span * unchecked((ulong)right[1])) >> 64)));

        if (SmoothstepAgainstOracle(edge0: edge0, edge1: edge1, value: inside) is { } interior) {
            return interior;
        }

        if (
            (inside != long.MinValue) &&
            (inside != long.MaxValue)
        ) {
            var below = Smoothstep(edge0: edge0, edge1: edge1, value: (inside - 1L));
            var at = Smoothstep(edge0: edge0, edge1: edge1, value: inside);
            var above = Smoothstep(edge0: edge0, edge1: edge1, value: (inside + 1L));
            var increasing = (edge1 >= edge0);

            if (increasing
                ? ((below > at) || (at > above))
                : ((below < at) || (at < above))) {
                return $"Smoothstep({edge0}, {edge1}, ·) is not monotone at {inside}: {below}, {at}, {above}";
            }
        }

        // A true tie: t = o/64 for an odd o, the only ratios whose curve is a Q16 midpoint, in both orientations; and
        // one raw either side of it, where the curve sits between 2⁻⁴⁰ and 2⁻¹⁴ ULP off the midpoint (the unit ranges
        // up to 2⁵¹) — outside the tie band, so a ratio carried coarser than about Q55 rounds those the wrong way.
        var tieEdge = (edge0 >> 20);
        var unitShift = ((int)(13UL + ((unchecked((ulong)right[0]) >> 8) % 48UL)));
        var unit = (1L + unchecked((long)(((ulong)right[1]) >> unitShift)));
        var odd = ((2L * (right[0] & 31L)) + 1L);
        var tieValue = (tieEdge + (odd * unit));
        var tieFar = (tieEdge + (64L * unit));

        foreach (var nudge in ((long[])[0L, -1L, 1L])) {
            if (
                (SmoothstepAgainstOracle(edge0: tieEdge, edge1: tieFar, value: (tieValue + nudge)) ??
                SmoothstepAgainstOracle(edge0: tieFar, edge1: tieEdge, value: (tieValue + nudge))) is { } tie
            ) {
                return tie;
            }
        }

        return null;
    }
    /// <summary>Smoothstep's fixed points: exact zero and one at and beyond the edges in both orientations, including
    /// edges at the two carrier extremes whose distance apart leaves 64 bits; the step at equal edges; and the exact
    /// one half at the midpoint.</summary>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedSmoothstepEdges() {
        var one = FixedQ4816.One.Value;
        long[][] edges = [
            [0L, one],
            [one, 0L],
            [long.MinValue, long.MaxValue],
            [long.MaxValue, long.MinValue],
            [-5L, 7L],
            [(1L << 47), -(1L << 47)],
        ];

        foreach (var pair in edges) {
            var edge0 = pair[0];
            var edge1 = pair[1];
            var outward = ((edge1 > edge0)
                ? -1L
                : 1L);

            if (Smoothstep(edge0: edge0, edge1: edge1, value: edge0) != 0L) { return $"Smoothstep({edge0}, {edge1}) is not zero at edge0"; }
            if (Smoothstep(edge0: edge0, edge1: edge1, value: edge1) != one) { return $"Smoothstep({edge0}, {edge1}) is not one at edge1"; }

            if (
                (edge0 != long.MinValue) &&
                (edge0 != long.MaxValue) &&
                (Smoothstep(edge0: edge0, edge1: edge1, value: (edge0 + outward)) != 0L)
            ) {
                return $"Smoothstep({edge0}, {edge1}) is not zero beyond edge0";
            }

            if (
                (edge1 != long.MinValue) &&
                (edge1 != long.MaxValue) &&
                (Smoothstep(edge0: edge0, edge1: edge1, value: (edge1 - outward)) != one)
            ) {
                return $"Smoothstep({edge0}, {edge1}) is not one beyond edge1";
            }

            // The midpoint of an even span is t = 1/2 exactly, where the curve is exactly one half.
            var span = (((BigInteger)edge1) - edge0);

            if (span.IsEven) {
                var middle = ((long)(edge0 + (span / 2)));

                if (Smoothstep(edge0: edge0, edge1: edge1, value: middle) != (one / 2L)) {
                    return $"Smoothstep({edge0}, {edge1}) is not one half at the midpoint {middle}";
                }
            }
        }

        foreach (var edge in ((long[])[long.MinValue, -1L, 0L, one, long.MaxValue])) {
            if (
                (edge != long.MinValue) &&
                (Smoothstep(edge0: edge, edge1: edge, value: (edge - 1L)) != 0L)
            ) {
                return $"the step at equal edges {edge} is not zero below the edge";
            }

            if (Smoothstep(edge0: edge, edge1: edge, value: edge) != one) {
                return $"the step at equal edges {edge} is not one at the edge";
            }
        }

        return null;
    }

    private static long Smoothstep(long edge0, long edge1, long value) =>
        FixedQ4816.Smoothstep(
            edge0: FixedQ4816.FromRawBits(value: edge0),
            edge1: FixedQ4816.FromRawBits(value: edge1),
            value: FixedQ4816.FromRawBits(value: value)
        ).Value;
    // The subject against the oracle: equal to the correct rounding, or, where the exact curve lies within the tie
    // band of a midpoint, one of the midpoint's two neighbours.
    private static string? SmoothstepAgainstOracle(long edge0, long edge1, long value) {
        var actual = Smoothstep(edge0: edge0, edge1: edge1, value: value);
        var expected = Oracles.Smoothstep(
            denominator: out var denominator,
            edge0: edge0,
            edge1: edge1,
            numerator: out var numerator,
            value: value
        );

        if (actual == expected) {
            return null;
        }

        // |2·(numerator mod denominator) − denominator| measures, in units of the denominator, twice the distance of
        // the exact curve from the midpoint between its two neighbours.
        var floor = BigInteger.Divide(dividend: numerator, divisor: denominator);
        var distance = BigInteger.Abs(value: ((2 * (numerator - (floor * denominator))) - denominator));
        // A true tie (distance zero) is held to the correct rounding exactly; only a near-miss may round either way.
        var withinBand = (
            !distance.IsZero &&
            ((distance << SmoothstepTieBandBits) <= (2 * denominator))
        );

        if (
            withinBand &&
            ((actual == ((long)floor)) || (actual == (((long)floor) + 1L)))
        ) {
            return null;
        }

        return $"Smoothstep({edge0}, {edge1}, {value}) = {actual}, correctly rounded {expected}";
    }
}
