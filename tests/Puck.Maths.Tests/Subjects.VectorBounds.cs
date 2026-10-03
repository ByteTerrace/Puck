namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="FixedVector3.IsWithin"/> to the law suite.</summary>
internal static partial class Subjects {
    /// <summary>IsWithin answers exactly what <c>Length &lt;= radius</c> answers, at radii drawn freely, at the
    /// length itself and its two raw neighbours, at the saturation edge and at negative radii.</summary>
    /// <param name="left">The vector's three raws.</param>
    /// <param name="right">Three freely drawn radius raws.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedVectorIsWithinMatchesLength(long[] left, long[] right) {
        var vector = new FixedVector3(
            X: Raw(value: left[0]),
            Y: Raw(value: left[1]),
            Z: Raw(value: left[2])
        );
        var root = Oracles.NormRoot(raws: left);
        var reported = ((root > long.MaxValue)
            ? long.MaxValue
            : ((long)root));
        var length = vector.Length.Value;
        Span<long> radii = [
            right[0], right[1], right[2],
            reported, (reported - ((reported == long.MinValue) ? 0L : 1L)), (reported + ((reported == long.MaxValue) ? 0L : 1L)),
            long.MaxValue, (long.MaxValue - 1L), 0L, -1L, long.MinValue,
        ];

        foreach (var radius in radii) {
            var expected = (reported <= radius);
            var actual = vector.IsWithin(radius: Raw(value: radius));

            if (actual != expected) {
                return $"({left[0]}, {left[1]}, {left[2]}).IsWithin({radius}) = {actual}, but the nearest root {root} (reported {reported}) is {(expected ? "within" : "beyond")} it";
            }

            if (actual != (length <= radius)) {
                return $"({left[0]}, {left[1]}, {left[2]}).IsWithin({radius}) = {actual}, but Length {length} <= {radius} is {(length <= radius)}";
            }
        }

        return null;
    }
    /// <summary>CompareLengthTo orders two vectors by their exact sums of squares, antisymmetrically, and the one it
    /// calls longer never reports the shorter <see cref="FixedVector3.Length"/>; at the equal-sum pairs a lane
    /// permutation and sign flips build, it answers zero.</summary>
    /// <param name="left">Three raws for the first vector.</param>
    /// <param name="right">Three raws for the second vector.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedVectorCompareLengthMatchesTheSquares(long[] left, long[] right) {
        // The drawn pair, the first against its own one-raw neighbours in each lane (sums one 2|x| ± 1 apart), and
        // against a permuted, sign-flipped copy of itself (the same sum by another route). MinValue lanes stay put,
        // having no negation.
        static long Flip(long value) => ((value == long.MinValue)
            ? value
            : -value);
        static long Nudge(long value, long step) => ((((step > 0L) && (value == long.MaxValue)) || ((step < 0L) && (value == long.MinValue)))
            ? value
            : (value + step));
        long[][] partners = [
            right,
            [Flip(value: left[2]), left[0], Flip(value: left[1])],
            [Nudge(value: left[0], step: 1L), left[1], left[2]],
            [left[0], Nudge(value: left[1], step: -1L), left[2]],
            [left[0], left[1], Nudge(value: left[2], step: 1L)],
        ];
        var vector = Vector(raws: left);

        foreach (var partner in partners) {
            var other = Vector(raws: partner);
            var expected = Oracles.SquaredNorm(raws: left).CompareTo(other: Oracles.SquaredNorm(raws: partner));
            var actual = vector.CompareLengthTo(other: other);

            if (actual != expected) {
                return $"({left[0]}, {left[1]}, {left[2]}).CompareLengthTo(({partner[0]}, {partner[1]}, {partner[2]})) = {actual}, the exact sums compare {expected}";
            }

            if (other.CompareLengthTo(other: vector) != -actual) {
                return $"CompareLengthTo is not antisymmetric between ({left[0]}, {left[1]}, {left[2]}) and ({partner[0]}, {partner[1]}, {partner[2]})";
            }

            if (
                ((actual > 0) && (vector.Length < other.Length)) ||
                ((actual < 0) && (vector.Length > other.Length))
            ) {
                return $"({left[0]}, {left[1]}, {left[2]}) compares {actual} against ({partner[0]}, {partner[1]}, {partner[2]}) but reports Length {vector.Length.Value} against {other.Length.Value}";
            }
        }

        return null;
    }

    private static FixedVector3 Vector(long[] raws) =>
        new(
            X: Raw(value: raws[0]),
            Y: Raw(value: raws[1]),
            Z: Raw(value: raws[2])
        );
}
