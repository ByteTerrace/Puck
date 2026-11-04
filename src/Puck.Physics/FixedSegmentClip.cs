using Puck.Maths;

namespace Puck.Physics;

/// <summary>Closed segment clipping shared by contact and authored aperture queries.</summary>
public static class FixedSegmentClip {
    /// <summary>Narrows a segment parameter interval to one closed axis slab. Endpoints inside the slab leave the
    /// interval unchanged, endpoints separated on the same side reject without division, and only crossed boundaries
    /// require a quotient. Boundary parameters use the fixed carrier's nearest, ties-to-even rounding.</summary>
    /// <param name="start">The axis coordinate at parameter zero.</param>
    /// <param name="end">The axis coordinate at parameter one.</param>
    /// <param name="lower">The slab's inclusive lower coordinate.</param>
    /// <param name="upper">The slab's inclusive upper coordinate.</param>
    /// <param name="enter">The current lower parameter in [0, 1], narrowed on success.</param>
    /// <param name="exit">The current upper parameter in [0, 1], narrowed on success.</param>
    /// <returns>Whether a nonempty parameter interval remains. On rejection its endpoints are unspecified.</returns>
    public static bool Axis(FixedQ4816 start, FixedQ4816 end, FixedQ4816 lower, FixedQ4816 upper,
        ref FixedQ4816 enter, ref FixedQ4816 exit) {
        if (lower > upper) { return false; }
        if (start < lower) {
            if (end < lower) { return false; }
            enter = FixedQ4816.Max(x: enter, y: Parameter(start, end, lower));
            if (end > upper) { exit = FixedQ4816.Min(x: exit, y: Parameter(start, end, upper)); }
        } else if (start > upper) {
            if (end > upper) { return false; }
            enter = FixedQ4816.Max(x: enter, y: Parameter(start, end, upper));
            if (end < lower) { exit = FixedQ4816.Min(x: exit, y: Parameter(start, end, lower)); }
        } else if (end < lower) {
            exit = FixedQ4816.Min(x: exit, y: Parameter(start, end, lower));
        } else if (end > upper) {
            exit = FixedQ4816.Min(x: exit, y: Parameter(start, end, upper));
        }
        return enter <= exit;
    }

    // The crossed boundary lies between the endpoints, hence this ratio is nonnegative and at most one. Widen the
    // subtraction before deciding whether the usual carrier division is enough: extreme endpoints may span 2^64-1
    // raw units even though their parameter fits. The shared Maths rounding primitive also handles that case exactly.
    private static FixedQ4816 Parameter(FixedQ4816 start, FixedQ4816 end, FixedQ4816 boundary) {
        var numerator = (Int128)boundary.Value - start.Value;
        var denominator = (Int128)end.Value - start.Value;
        if (numerator >= long.MinValue && numerator <= long.MaxValue &&
            denominator >= long.MinValue && denominator <= long.MaxValue) {
            return new FixedQ4816((long)numerator) / new FixedQ4816((long)denominator);
        }
        if (!FusedArithmetic.TryDivideMagnitudeRounded(
            numeratorMagnitude: (UInt128)Int128.Abs(numerator),
            denominatorMagnitude: (UInt128)Int128.Abs(denominator),
            fractionBitCount: FixedQ4816.FractionBitCount, quotient: out var quotient)) {
            throw new InvalidOperationException("A segment boundary parameter in [0, 1] must be representable.");
        }
        return new FixedQ4816((long)quotient);
    }
}
