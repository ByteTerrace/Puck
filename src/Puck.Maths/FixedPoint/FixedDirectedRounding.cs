namespace Puck.Maths;

/// <summary>
/// The directed-UP arithmetic a conservative bound is built from: a product, a quotient, a product-plus-addend, a
/// square root and a vector norm, each the LEAST representable value at or above the exact one. Every operand is a
/// non-negative raw at whatever scale the caller declares, and every member refuses rather than wrapping or
/// saturating — the same policy <see cref="FixedSymmetricSolve"/> carries.
/// </summary>
/// <remarks>
/// <para><b>Why these exist rather than a unit in the last place at the call site.</b> A speculative contact or a
/// time-of-impact test is correct only while its bound is an OVER-estimate; a round-to-nearest result is an
/// under-estimate about half the time. Adding one unit in the last place to a nearest result does not repair that: it
/// overshoots by a whole unit whenever the nearest result was already above the exact value, and it still misses the
/// case where the exact value sits more than half a unit above the truncation. Directing the ONE rounding these
/// kernels already perform costs nothing and is exact — the returned value <c>r</c> satisfies
/// <c>exact ≤ r &lt; exact + 1</c> in units of the output scale, with equality on the left exactly when the exact
/// value is representable.</para>
/// <para><b>Non-negative only.</b> Every member refuses a negative operand rather than choosing between "up" meaning
/// toward positive infinity and "up" meaning away from zero — a bound whose direction depends on a sign the caller did
/// not think about is the defect this family exists to remove. The quantities it serves (a separation, a radius, a
/// speed bound, a norm) are non-negative by construction.</para>
/// <para><b>The round-to-nearest siblings, deliberately left alone.</b> <see cref="FixedQ4816.Sqrt"/> is the nearest
/// square root and <see cref="FixedVectorMath.TryMagnitude(long, long, long, out FixedQ4816)"/> the round-to-nearest
/// norm; neither changes behaviour to serve this family. <see cref="TryCeilingSquareRoot"/> at
/// <see cref="FixedQ4816.FractionBitCount"/> is the directed sibling of the first, and
/// <see cref="TryCeilingMagnitude(long, long, long, out long)"/> of the second.</para>
/// </remarks>
public static class FixedDirectedRounding {
    // 2^63 as a double, the one binary64 bound both carrier edges are tested against: a floor below −2^63 or at or
    // above 2^63 has no raw.
    private const double RawCarrierLimit = 9223372036854775808d;

    private static bool TryCeilingRoot(UInt128 radicand, out long result) =>
        FusedArithmetic.TryNarrowSignedMagnitude(
            magnitude: FusedArithmetic.CeilingSquareRoot(radicand: radicand),
            negative: false,
            result: out result
        );

    /// <summary>Returns the greatest integer at or below the exact quotient of two signed wide integers.</summary>
    /// <param name="numerator">The dividend.</param>
    /// <param name="denominator">The divisor, which must be non-zero.</param>
    /// <returns><c>⌊numerator / denominator⌋</c>, exact.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    /// <exception cref="OverflowException"><paramref name="numerator"/> is <see cref="Int128.MinValue"/> and
    /// <paramref name="denominator"/> is minus one, the one quotient outside the carrier.</exception>
    /// <remarks>A positive power-of-two divisor is an arithmetic shift, a numerator inside the 64-bit range divides in
    /// 64 bits, and the rest take <see cref="Int128"/>'s own division; the floor is the truncated quotient less one
    /// exactly when a non-zero remainder's sign differs from the divisor's.</remarks>
    public static Int128 FloorQuotient(Int128 numerator, Int128 denominator) =>
        DirectedQuotients(
            denominator: denominator,
            numerator: numerator
        ).Floor;
    /// <summary>Returns the least integer at or above the exact quotient of two signed wide integers.</summary>
    /// <param name="numerator">The dividend.</param>
    /// <param name="denominator">The divisor, which must be non-zero.</param>
    /// <returns><c>⌈numerator / denominator⌉</c>, exact.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    /// <exception cref="OverflowException"><paramref name="numerator"/> is <see cref="Int128.MinValue"/> and
    /// <paramref name="denominator"/> is minus one, the one quotient outside the carrier.</exception>
    /// <remarks>The ceiling is the negated floor of the negated quotient: the truncated quotient plus one exactly when
    /// a non-zero remainder's sign matches the divisor's. The same paths as <see cref="FloorQuotient"/>.</remarks>
    public static Int128 CeilingQuotient(Int128 numerator, Int128 denominator) =>
        DirectedQuotients(
            denominator: denominator,
            numerator: numerator
        ).Ceiling;
    /// <summary>Returns the greatest raw at or below the exact real value of a double, the directed-down inbound seam
    /// of a conservative enclosure.</summary>
    /// <param name="value">The double whose exact value is bounded.</param>
    /// <param name="result">The floor on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when <paramref name="value"/> is not finite or when its floor lies outside the
    /// raw carrier.</returns>
    /// <remarks>A double times <c>2¹⁶</c> is exact (a power of two only moves the exponent, and an overflow reads as
    /// infinity and is refused), and <see cref="Math.Floor(double)"/> of a finite double is exact, so the result is the
    /// true floor rather than the nearest value a tiny positive operand would round to zero under.</remarks>
    public static bool TryFloorFromDouble(double value, out FixedQ4816 result) {
        var scaled = (value * (1L << FixedQ4816.FractionBitCount));

        if (!double.IsFinite(d: scaled)) {
            result = default;
            return false;
        }

        var floor = Math.Floor(d: scaled);

        if (
            (floor < -RawCarrierLimit) ||
            (floor >= RawCarrierLimit)
        ) {
            result = default;
            return false;
        }

        result = FixedQ4816.FromRawBits(value: ((long)floor));
        return true;
    }
    /// <summary>Returns the least raw at or above the exact real value of a double, the directed-up inbound seam of a
    /// conservative enclosure.</summary>
    /// <param name="value">The double whose exact value is bounded.</param>
    /// <param name="result">The ceiling on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when <paramref name="value"/> is not finite or when its ceiling lies outside
    /// the raw carrier.</returns>
    /// <remarks>The directed-up sibling of <see cref="TryFloorFromDouble"/>, exact for the same reason.</remarks>
    public static bool TryCeilingFromDouble(double value, out FixedQ4816 result) {
        var scaled = (value * (1L << FixedQ4816.FractionBitCount));

        if (!double.IsFinite(d: scaled)) {
            result = default;
            return false;
        }

        var ceiling = Math.Ceiling(a: scaled);

        if (
            (ceiling < -RawCarrierLimit) ||
            (ceiling >= RawCarrierLimit)
        ) {
            result = default;
            return false;
        }

        result = FixedQ4816.FromRawBits(value: ((long)ceiling));
        return true;
    }

    // The floor and ceiling of numerator/denominator for a non-zero denominator, from one division. A positive
    // power-of-two denominator inside the long range is an arithmetic shift, which floors, and its ceiling the
    // negated floor of the negation; a numerator and denominator both inside the 64-bit range divide in 64 bits,
    // whose truncated quotient and remainder are Int128's (long.MinValue is left out, since its quotient by −1 is
    // not a long); everything else takes Int128's own division.
    internal static (Int128 Floor, Int128 Ceiling) DirectedQuotients(Int128 numerator, Int128 denominator) {
        if (
            (denominator > Int128.Zero) &&
            (denominator <= long.MaxValue) &&
            long.IsPow2(value: ((long)denominator))
        ) {
            var shift = ((int)long.TrailingZeroCount(value: ((long)denominator)));
            var shifted = (numerator >> shift);

            // The ceiling is the floor plus one exactly when a discarded bit is set; negating the numerator to reuse
            // the floor would wrap at Int128.MinValue.
            return (shifted, (((numerator & ((Int128.One << shift) - Int128.One)) == Int128.Zero)
                ? shifted
                : (shifted + Int128.One)));
        }

        if (
            (numerator > long.MinValue) &&
            (numerator <= long.MaxValue) &&
            (denominator >= long.MinValue) &&
            (denominator <= long.MaxValue)
        ) {
            var (narrowQuotient, narrowRemainder) = Math.DivRem(
                left: ((long)numerator),
                right: ((long)denominator)
            );

            if (narrowRemainder == 0L) {
                return (narrowQuotient, narrowQuotient);
            }

            return (((narrowRemainder < 0L) != (denominator < Int128.Zero))
                ? ((((Int128)narrowQuotient) - 1), narrowQuotient)
                : (narrowQuotient, (((Int128)narrowQuotient) + 1)));
        }

        var quotient = Int128.DivRem(
            left: numerator,
            right: denominator
        );
        var floor = quotient.Quotient;
        var ceiling = quotient.Quotient;

        if (quotient.Remainder != Int128.Zero) {
            // Truncation rounds toward zero: a positive exact quotient's floor is the truncation, a negative one's the
            // truncation less one, and the ceiling the other way about.
            if ((quotient.Remainder < Int128.Zero) != (denominator < Int128.Zero)) {
                --floor;
            } else {
                ++ceiling;
            }
        }

        return (floor, ceiling);
    }

    /// <summary>Returns the least raw at or above the exact planar magnitude <c>√(x² + y²)</c>, at the components' own
    /// scale.</summary>
    /// <param name="x">The first component's raw.</param>
    /// <param name="y">The second component's raw.</param>
    /// <param name="result">The rounded-up magnitude on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the rounded-up magnitude does not fit the signed 64-bit raw.</returns>
    /// <remarks>No fraction bit count appears because none is needed: a norm is degree one in its components, so a
    /// common scale passes straight through it. Both components may be negative — a magnitude is non-negative
    /// whatever their signs, which is why this member alone in the family admits them.</remarks>
    public static bool TryCeilingMagnitude(long x, long y, out long result) =>
        TryCeilingRoot(
            radicand: (FusedArithmetic.SquareMagnitude(value: x) + FusedArithmetic.SquareMagnitude(value: y)),
            result: out result
        );
    /// <summary>Returns the least raw at or above the exact magnitude <c>√(x² + y² + z²)</c>, at the components' own
    /// scale — the directed-up sibling of
    /// <see cref="FixedVectorMath.TryMagnitude(long, long, long, out FixedQ4816)"/>.</summary>
    /// <param name="x">The first component's raw.</param>
    /// <param name="y">The second component's raw.</param>
    /// <param name="z">The third component's raw.</param>
    /// <param name="result">The rounded-up magnitude on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the rounded-up magnitude does not fit the signed 64-bit raw.</returns>
    /// <remarks>Three squared 64-bit raws total at most <c>3·2^126</c>, inside <see cref="UInt128"/>, so the sum is
    /// exact and no preconditioning is needed. The root itself can still reach past the raw carrier, which is refused.</remarks>
    public static bool TryCeilingMagnitude(long x, long y, long z, out long result) =>
        TryCeilingRoot(
            radicand: ((FusedArithmetic.SquareMagnitude(value: x) + FusedArithmetic.SquareMagnitude(value: y)) + FusedArithmetic.SquareMagnitude(value: z)),
            result: out result
        );
    /// <summary>Multiplies two non-negative raws at independent scales and rounds the product UP to a third scale
    /// exactly once.</summary>
    /// <param name="a">The first factor's non-negative raw.</param>
    /// <param name="fractionBitsA">The first factor's fraction bit count.</param>
    /// <param name="b">The second factor's non-negative raw.</param>
    /// <param name="fractionBitsB">The second factor's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The rounded-up product on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when either factor is negative or the rounded product does not fit the signed
    /// 64-bit raw.</returns>
    public static bool TryCeilingProduct(long a, int fractionBitsA, long b, int fractionBitsB, int fractionBitsOut, out long result) {
        if (
            (a < 0L) ||
            (b < 0L)
        ) {
            result = 0L;
            return false;
        }

        var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(
            magnitude: (((UInt128)((ulong)a)) * ((ulong)b)),
            shift: FusedArithmetic.MixedScaleShift(
                first: fractionBitsA,
                fractionBitsOut: fractionBitsOut,
                second: fractionBitsB
            )
        );

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(
            magnitude: scaled.Magnitude,
            negative: false,
            result: out result
        );
    }
    /// <summary>Rounds <c>a·b + addend</c> UP to a chosen scale exactly once, over non-negative raws at three
    /// independent scales.</summary>
    /// <param name="a">The first factor's non-negative raw.</param>
    /// <param name="fractionBitsA">The first factor's fraction bit count.</param>
    /// <param name="b">The second factor's non-negative raw.</param>
    /// <param name="fractionBitsB">The second factor's fraction bit count.</param>
    /// <param name="addend">The addend's non-negative raw.</param>
    /// <param name="fractionBitsAddend">The addend's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The rounded-up sum on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when any operand is negative, when either term needs more than 127 bits once
    /// lifted onto the common scale, or when the rounded sum does not fit the signed 64-bit raw.</returns>
    /// <remarks>Both terms are lifted by a LOSSLESS left shift onto the finer of the two scales they arrive at —
    /// preconditioning by one common power of two — so the addition is exact and only the final narrowing rounds. The
    /// per-term budget is 127 bits rather than <see cref="UInt128"/>'s full 128 BECAUSE the two are then added: two
    /// 128-bit terms need a 129th bit the accumulator does not have, and a wrapped sum would be answered as an
    /// ordinary result. That is the whole refusal envelope, and it is a declined answer rather than a wrong one.</remarks>
    public static bool TryCeilingProductSum(
        long a,
        int fractionBitsA,
        long b,
        int fractionBitsB,
        long addend,
        int fractionBitsAddend,
        int fractionBitsOut,
        out long result
    ) {
        if (
            (a < 0L) ||
            (b < 0L) ||
            (addend < 0L)
        ) {
            result = 0L;
            return false;
        }

        var productScale = (((long)fractionBitsA) + fractionBitsB);
        var addendScale = ((long)fractionBitsAddend);
        var commonScale = Math.Max(
            val1: productScale,
            val2: addendScale
        );
        var product = FusedArithmetic.ScaleMagnitudeToCeiling(
            magnitude: (((UInt128)((ulong)a)) * ((ulong)b)),
            shift: (commonScale - productScale)
        );
        var lifted = FusedArithmetic.ScaleMagnitudeToCeiling(
            magnitude: ((UInt128)((ulong)addend)),
            shift: (commonScale - addendScale)
        );

        var budget = (UInt128.MaxValue >> 1);

        if (
            product.Overflowed ||
            lifted.Overflowed ||
            (product.Magnitude > budget) ||
            (lifted.Magnitude > budget)
        ) {
            result = 0L;
            return false;
        }

        var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(
            magnitude: (product.Magnitude + lifted.Magnitude),
            shift: (fractionBitsOut - commonScale)
        );

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(
            magnitude: scaled.Magnitude,
            negative: false,
            result: out result
        );
    }
    /// <summary>Divides one non-negative raw by another at independent scales and rounds the quotient UP to a third
    /// scale exactly once.</summary>
    /// <param name="numerator">The dividend's non-negative raw.</param>
    /// <param name="fractionBitsNumerator">The dividend's fraction bit count.</param>
    /// <param name="denominator">The divisor's raw, which must be strictly positive.</param>
    /// <param name="fractionBitsDenominator">The divisor's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The rounded-up quotient on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when <paramref name="numerator"/> is negative, when
    /// <paramref name="denominator"/> is not strictly positive, or when the rounded quotient does not fit the signed
    /// 64-bit raw.</returns>
    /// <remarks>The exact value is <c>numerator · 2^(fractionBitsOut + fractionBitsDenominator −
    /// fractionBitsNumerator) / denominator</c>. A positive exponent scales the dividend and a negative one the
    /// divisor, so neither side is rounded before the single division. Both extremes are answered exactly rather than
    /// declined: an oversized dividend forces a quotient of at least <c>2^65</c>, which cannot fit the raw and is
    /// refused, and an oversized divisor puts the exact ratio strictly inside <c>(0, 1)</c>, whose ceiling is one.</remarks>
    public static bool TryCeilingQuotient(
        long numerator,
        int fractionBitsNumerator,
        long denominator,
        int fractionBitsDenominator,
        int fractionBitsOut,
        out long result
    ) {
        if (
            (numerator < 0L) ||
            (denominator <= 0L)
        ) {
            result = 0L;
            return false;
        }

        if (numerator == 0L) {
            result = 0L;
            return true;
        }

        var exponent = ((((long)fractionBitsOut) + fractionBitsDenominator) - fractionBitsNumerator);
        var dividend = ((UInt128)((ulong)numerator));
        var divisor = ((UInt128)((ulong)denominator));

        if (exponent >= 0L) {
            var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(
                magnitude: dividend,
                shift: exponent
            );

            if (scaled.Overflowed) {
                // The dividend alone passed 2^128 while the divisor is below 2^63, so the exact quotient exceeds
                // 2^65 — past the raw carrier for any divisor this signature admits.
                result = 0L;
                return false;
            }

            dividend = scaled.Magnitude;
        } else {
            var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(
                magnitude: divisor,
                shift: -exponent
            );

            if (scaled.Overflowed) {
                // The divisor passed 2^128 while the dividend is below 2^63, so the exact ratio lies strictly between
                // zero and one and its ceiling is exactly one.
                result = 1L;
                return true;
            }

            divisor = scaled.Magnitude;
        }

        var quotient = (dividend / divisor);

        if ((dividend - (quotient * divisor)) != UInt128.Zero) {
            ++quotient;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(
            magnitude: quotient,
            negative: false,
            result: out result
        );
    }
    /// <summary>Returns the least raw at or above <c>√(value · 2^fractionBitCount)</c> — the directed-up sibling of
    /// <see cref="FixedQ4816.Sqrt"/>'s floor, generalized to any output resolution.</summary>
    /// <param name="value">The non-negative radicand's raw.</param>
    /// <param name="fractionBitCount">The number of fraction bits the root is produced at; passing the radicand's own
    /// count reproduces a same-scale square root.</param>
    /// <param name="result">The rounded-up root on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when <paramref name="value"/> is negative, when
    /// <paramref name="fractionBitCount"/> is negative, when the scaled radicand leaves <see cref="UInt128"/>, or when
    /// the root does not fit the signed 64-bit raw.</returns>
    /// <remarks>The floor root is settled by one exact squaring, then carried up by one whenever the radicand is not a
    /// perfect square — so the result is the exact ceiling, never a floor with a unit added blindly.</remarks>
    public static bool TryCeilingSquareRoot(long value, int fractionBitCount, out long result) {
        if (
            (value < 0L) ||
            (fractionBitCount < 0)
        ) {
            result = 0L;
            return false;
        }

        var magnitude = ((UInt128)((ulong)value));
        var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(
            magnitude: magnitude,
            shift: fractionBitCount
        );

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return TryCeilingRoot(
            radicand: scaled.Magnitude,
            result: out result
        );
    }
}
