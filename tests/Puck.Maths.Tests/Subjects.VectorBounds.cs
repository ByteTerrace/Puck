namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="FixedVector3.CompareLengthTo"/> to the law suite.</summary>
internal static partial class Subjects {
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
