using System.Numerics;
using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>A certificate for the existing GPU candidate evaluator. The exact field has the stated Lipschitz bound
/// in the certificate frame: world space unless <see cref="DynamicFrame"/> selects a runtime pose frame.
/// On the admitted coordinate domain its float evaluation differs by at most
/// <c>ErrorSlope * max(abs(point)) + ErrorOffset</c>, unless <see cref="CenteredNormEnvelope"/> selects the
/// centre-dependent norm enclosure applied by <see cref="Enclose"/>. Zero flags certify nothing.</summary>
/// <param name="Lipschitz">An outward upper bound, including the point transform and distance scaling.</param>
/// <param name="ErrorSlope">The coordinate-dependent coefficient in the paired float enclosure.</param>
/// <param name="ErrorOffset">The coordinate-independent coefficient in the paired float enclosure.</param>
/// <param name="Flags">The generated tape certificate flags.</param>
public readonly record struct SdfTapeCertificate(float Lipschitz, float ErrorSlope, float ErrorOffset, uint Flags) {
    /// <summary>A finite candidate certificate is present.</summary>
    public const uint Certified = 1u;
    /// <summary>The candidate belongs to a segment whose point state is dead at its end and which edits no field.</summary>
    public const uint OmissibleSegment = 2u;
    /// <summary>The certificate starts in the frame of the chain's one leading dynamic transform. A consumer first
    /// transforms its ball with <see cref="TransformBall"/>; an unbounded or absent pose leaves the candidate live.</summary>
    public const uint DynamicFrame = 4u;
    /// <summary>The coefficients include the superellipsoid norm envelope: arithmetic error is multiplied by
    /// 1/alpha and the offset includes gap*scaledMinRadius/(2*alpha). Enclose also needs the packed power exponent
    /// to add gap/(2*alpha) times the absolute centre sample before doubling the paired endpoint allowance.</summary>
    public const uint CenteredNormEnvelope = 8u;
    /// <summary>The largest admitted absolute world coordinate. Outside this box the candidate stays live.</summary>
    public const float CoordinateLimit = 65536f;
    /// <summary>The maximum candidate magnitude admitted by the host arithmetic model, keeping squared lengths finite.</summary>
    public const float IntermediateLimit = 1.0e12f;
    /// <summary>Sixty-four single-precision unit roundoffs. The smooth blend's h calculation and polynomial use
    /// fewer than sixteen dependent arithmetic operations; this covers their directed error, division's extra ulps
    /// and the guarded ball-membership reduction. It is a derived operation budget, not a geometric tolerance.</summary>
    public const float BlendRoundoffMargin = (64f / 16777216f);

    /// <summary>Bounds the rational lower factor relating a three-dimensional Lp norm to its Euclidean norm,
    /// for packed exponents from two through three. Convexity puts <c>3^(1/p-1/2)</c> above its tangent at two;
    /// <c>ln(3)/4 &lt; 7/25</c> puts <c>1-(7/25)*(p-2)</c> below that tangent. The upper norm factor is one.
    /// The GPU clamps its power approximation to this band, so its certificate needs no intrinsic power-error claim.</summary>
    /// <param name="exponent">The packed superellipsoid exponent.</param>
    /// <returns>The outward rational factor, or the entire interval outside the modeled range.</returns>
    public static FixedInterval SuperellipsoidNormFactor(float exponent) {
        if (!float.IsFinite(f: exponent) || (exponent < 2f) || (exponent > 3f)) { return FixedInterval.Entire; }
        return (Integer(value: 1) - ((Integer(value: 7) / Integer(value: 25)) * (EncloseFloat(value: exponent) - Integer(value: 2))));
    }
    /// <summary>Encloses the centre-sample coefficient gap/(2*alpha) in the paired superellipsoid norm bound.
    /// The full ball allowance is L*radius + 2*(ErrorSlope*magnitude + ErrorOffset + gain*abs(centreValue)).</summary>
    /// <param name="exponent">The packed superellipsoid exponent.</param>
    /// <returns>The directed nonnegative gain, or the entire interval outside the modeled exponent range.</returns>
    public static FixedInterval SuperellipsoidCenterGain(float exponent) {
        var alpha = SuperellipsoidNormFactor(exponent: exponent);

        return ((Integer(value: 1) - alpha) / (Integer(value: 2) * alpha));
    }
    /// <summary>Bounds a ball after the GPU's translate-and-inverse-quaternion point polynomial, including rounding
    /// at both its centre and sampled points. The pose need not have an exactly unit quaternion. Its norm uses
    /// <c>max(1, 2*|q|²-1)</c> and the coordinate norm uses the rational upper bound <c>7/4 &gt; sqrt(3)</c>,
    /// so the runtime certificate needs no floating-point square root.</summary>
    /// <param name="centreMagnitude">The maximum absolute coordinate of the world-space centre.</param>
    /// <param name="radius">The outward world-space radius.</param>
    /// <param name="position">The pose's packed position.</param>
    /// <param name="orientation">The pose's packed quaternion.</param>
    /// <returns>The dynamic-frame radius and centre magnitude, or a ball with no certificate.</returns>
    public static SdfTapeBall TransformBall(float centreMagnitude, float radius, Vector3 position, Quaternion orientation) =>
        SdfProgram.TransformTapeBall(centreMagnitude: centreMagnitude, orientation: orientation, position: position, radius: radius);
    /// <summary>Encloses the float candidate at every point in a ball from one float sample at its centre. The error
    /// appears twice because both that centre and the later ray sample are float evaluations of the certified field.</summary>
    /// <param name="centreValue">The existing GPU evaluator's centre sample.</param>
    /// <param name="centreMagnitude">The largest absolute coordinate of the ball centre.</param>
    /// <param name="radius">The ball radius, already rounded outward.</param>
    /// <param name="powerExponent">The packed exponent for a centered norm envelope. Missing or unsupported values
    /// leave that candidate live; ordinary certificates do not require this argument.</param>
    /// <returns>A certified interval, or the entire interval when a certificate or admitted domain is absent.</returns>
    public FixedInterval Enclose(float centreValue, float centreMagnitude, float radius, float powerExponent = float.NaN) {
        if (((Flags & Certified) == 0u) || !float.IsFinite(f: centreValue) || !float.IsFinite(f: radius)
            || !float.IsFinite(f: centreMagnitude) || (radius < 0f) || (centreMagnitude < 0f)
            || !float.IsFinite(f: Lipschitz) || !float.IsFinite(f: ErrorSlope) || !float.IsFinite(f: ErrorOffset)
            || (Lipschitz < 0f) || (ErrorSlope < 0f) || (ErrorOffset < 0f)
            || ((((double)centreMagnitude) + radius) > CoordinateLimit)) {
            return FixedInterval.Entire;
        }
        var r = EncloseFloat(value: radius);
        var magnitude = (EncloseFloat(value: centreMagnitude) + r);
        var error = ((EncloseFloat(value: ErrorSlope) * magnitude) + EncloseFloat(value: ErrorOffset));
        var center = EncloseFloat(value: centreValue);

        if ((Flags & CenteredNormEnvelope) != 0u) {
            if (!(powerExponent > 2f) || !(powerExponent <= 3f)) { return FixedInterval.Entire; }
            var gain = SuperellipsoidCenterGain(exponent: powerExponent);

            error += (gain * FixedInterval.Abs(value: center));
        }
        var expansion = ((EncloseFloat(value: Lipschitz) * r) + (error * Integer(value: 2)));

        return FixedInterval.Union(first: (center - expansion), second: (center + expansion));
    }
    /// <summary>Proves a candidate inert under a hard or smooth minimum/maximum. An unbounded operand or unsupported
    /// blend never proves a deletion. The strict separation retains equal-value material and gradient decisions.</summary>
    /// <param name="current">The accumulated field's interval.</param>
    /// <param name="candidate">The candidate's interval.</param>
    /// <param name="blend">The composition operation.</param>
    /// <param name="radius">The nonnegative smooth radius.</param>
    /// <returns>Whether omitting the candidate preserves the current value throughout the region.</returns>
    public static bool CandidateLoses(FixedInterval current, FixedInterval candidate, SdfBlendOp blend, FixedQ4816 radius) {
        if (current.IsUnbounded || candidate.IsUnbounded || (radius < FixedQ4816.Zero)) {
            return false;
        }
        if (blend is SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction) { candidate = -candidate; }
        if (candidate.IsUnbounded) { return false; }
        var band = ((blend is SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction)
            ? FixedInterval.Max(first: FixedInterval.FromPoint(value: radius), second: EncloseFloat(value: 0.0001f))
            : Integer(value: 0));
        var magnitude = FixedInterval.Max(first: FixedInterval.Abs(value: current), second: FixedInterval.Abs(value: candidate));
        var margin = (((magnitude + band) * Integer(value: 64)) / Integer(value: 16777216));
        var gap = (band + margin);
        var padded = (current + gap);
        var lowered = (current - gap);

        if (padded.IsUnbounded || lowered.IsUnbounded) { return false; }

        return blend switch {
            SdfBlendOp.Union or SdfBlendOp.SmoothUnion => (candidate.Lower > padded.Upper),
            SdfBlendOp.Intersection or SdfBlendOp.SmoothIntersection or SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction => (candidate.Upper < lowered.Lower),
            _ => false,
        };
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

    internal static FixedInterval Integer(int value) => FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: value));

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
}
/// <summary>An outward ball descriptor in a candidate certificate's frame.</summary>
/// <param name="Radius">The radius, including both endpoint pose errors.</param>
/// <param name="Magnitude">An upper bound on the centre's maximum absolute coordinate.</param>
/// <param name="IsCertified">Whether the pose arithmetic and complete ball stay in the admitted domain.</param>
public readonly record struct SdfTapeBall(float Radius, float Magnitude, bool IsCertified);
