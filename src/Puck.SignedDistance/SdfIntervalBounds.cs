using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>Directed fixed-point enclosures of packed float parameters, which the query evaluator's primitive interval
/// bounds (<c>SdfFieldEvaluator.PrimitiveBounds</c>) are built from.</summary>
public static class SdfIntervalBounds {
    /// <summary>Bounds the rational lower factor relating a three-dimensional Lp norm to its Euclidean norm,
    /// for packed exponents from two through three. Convexity puts <c>3^(1/p-1/2)</c> above its tangent at two;
    /// <c>ln(3)/4 &lt; 7/25</c> puts <c>1-(7/25)*(p-2)</c> below that tangent. The upper norm factor is one.</summary>
    /// <param name="exponent">The packed superellipsoid exponent.</param>
    /// <returns>The outward rational factor, or the entire interval outside the modeled range.</returns>
    public static FixedInterval SuperellipsoidNormFactor(float exponent) {
        if (!float.IsFinite(f: exponent) || (exponent < 2f) || (exponent > 3f)) { return FixedInterval.Entire; }
        return (Integer(value: 1) - ((Integer(value: 7) / Integer(value: 25)) * (EncloseFloat(value: exponent) - Integer(value: 2))));
    }
    // A float is exact in double; multiplication by 2^16 is exact too. Floor/ceiling therefore give the actual
    // enclosing Q48.16 values, including tiny positive operands whose nearest fixed value would be zero.
    /// <summary>Encloses the exact real value of a packed float between outward Q48.16 endpoints.</summary>
    /// <param name="value">The packed parameter or coordinate.</param>
    /// <returns>The directed enclosure, or the entire interval for a nonfinite or out-of-carrier value.</returns>
    public static FixedInterval EncloseFloat(float value) {
        var scaled = (((double)value) * 65536d);

        if (!double.IsFinite(d: scaled) || (scaled <= long.MinValue) || (scaled >= long.MaxValue)) {
            return FixedInterval.Entire;
        }
        return new FixedInterval(
            lower: new FixedQ4816(Value: ((long)Math.Floor(d: scaled))),
            upper: new FixedQ4816(Value: ((long)Math.Ceiling(a: scaled)))
        );
    }
    /// <summary>Rounds a nonnegative interval's upper endpoint outward to a float after an exact binary scale.</summary>
    /// <param name="value">The nonnegative interval to bound.</param>
    /// <param name="binaryScale">The power of two applied to the endpoint.</param>
    /// <returns>An outward float upper bound, or positive infinity when the input cannot certify one.</returns>
    public static float UpperFloat(FixedInterval value, int binaryScale = 0) {
        if (value.IsUnbounded || (value.Lower < FixedQ4816.Zero)) { return float.PositiveInfinity; }
        var exact = Math.ScaleB(x: (((double)value.Upper.Value) / 65536d), n: binaryScale);
        var result = ((float)exact);

        if (!float.IsFinite(f: result)) { return float.PositiveInfinity; }
        // Compare integers, not a rounded double copy of Upper: above 2^53 raws that copy can erase the fraction
        // immediately above a representable float. A float rescaled back to raws is exact in double.
        var representedRaw = ((Int128)Math.ScaleB(n: (16 - binaryScale), x: result));

        return ((representedRaw < value.Upper.Value) ? float.BitIncrement(x: result) : result);
    }

    private static FixedInterval Integer(int value) => FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: value));
}
