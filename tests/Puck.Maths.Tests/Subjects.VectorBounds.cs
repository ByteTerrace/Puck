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
}
