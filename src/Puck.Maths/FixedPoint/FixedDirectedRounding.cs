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
/// <para><b>The round-to-nearest siblings, deliberately left alone.</b> <see cref="FixedQ4816.Sqrt"/> is the floor
/// square root and <see cref="FixedVectorMath.TryMagnitude(long, long, long, out FixedQ4816)"/> the round-to-nearest
/// norm; neither changes behaviour to serve this family. <see cref="TryCeilingSquareRoot"/> at
/// <see cref="FixedQ4816.FractionBitCount"/> is the directed sibling of the first, and
/// <see cref="TryCeilingMagnitude(long, long, long, out long)"/> of the second.</para>
/// </remarks>
public static class FixedDirectedRounding {
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
        if ((value < 0L) || (fractionBitCount < 0)) {
            result = 0L;
            return false;
        }

        var magnitude = ((UInt128)(ulong)value);
        var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(magnitude: magnitude, shift: fractionBitCount);

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return TryCeilingRoot(radicand: scaled.Magnitude, result: out result);
    }

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
        if ((a < 0L) || (b < 0L)) {
            result = 0L;
            return false;
        }

        var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(
            magnitude: (((UInt128)(ulong)a) * ((ulong)b)),
            shift: FusedArithmetic.MixedScaleShift(fractionBitsOut: fractionBitsOut, first: fractionBitsA, second: fractionBitsB)
        );

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: false, magnitude: scaled.Magnitude, result: out result);
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
        if ((numerator < 0L) || (denominator <= 0L)) {
            result = 0L;
            return false;
        }

        if (numerator == 0L) {
            result = 0L;
            return true;
        }

        var exponent = (((long)fractionBitsOut + fractionBitsDenominator) - fractionBitsNumerator);
        var dividend = ((UInt128)(ulong)numerator);
        var divisor = ((UInt128)(ulong)denominator);

        if (exponent >= 0L) {
            var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(magnitude: dividend, shift: exponent);

            if (scaled.Overflowed) {
                // The dividend alone passed 2^128 while the divisor is below 2^63, so the exact quotient exceeds
                // 2^65 — past the raw carrier for any divisor this signature admits.
                result = 0L;
                return false;
            }

            dividend = scaled.Magnitude;
        } else {
            var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(magnitude: divisor, shift: -exponent);

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

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: false, magnitude: quotient, result: out result);
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
        if ((a < 0L) || (b < 0L) || (addend < 0L)) {
            result = 0L;
            return false;
        }

        var productScale = ((long)fractionBitsA + fractionBitsB);
        var addendScale = ((long)fractionBitsAddend);
        var commonScale = Math.Max(val1: productScale, val2: addendScale);
        var product = FusedArithmetic.ScaleMagnitudeToCeiling(magnitude: (((UInt128)(ulong)a) * ((ulong)b)), shift: (commonScale - productScale));
        var lifted = FusedArithmetic.ScaleMagnitudeToCeiling(magnitude: ((UInt128)(ulong)addend), shift: (commonScale - addendScale));

        var budget = (UInt128.MaxValue >> 1);

        if (product.Overflowed || lifted.Overflowed || (product.Magnitude > budget) || (lifted.Magnitude > budget)) {
            result = 0L;
            return false;
        }

        var scaled = FusedArithmetic.ScaleMagnitudeToCeiling(magnitude: (product.Magnitude + lifted.Magnitude), shift: (fractionBitsOut - commonScale));

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: false, magnitude: scaled.Magnitude, result: out result);
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
        TryCeilingRoot(radicand: (FusedArithmetic.SquareMagnitude(value: x) + FusedArithmetic.SquareMagnitude(value: y)), result: out result);

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

    // The floor root, carried up by one whenever the radicand is not a perfect square, then narrowed with refusal.
    // The floor never reaches 2^64 for a radicand below 2^128, so the settling square cannot itself overflow.
    private static bool TryCeilingRoot(UInt128 radicand, out long result) {
        var root = radicand.SquareRoot();

        if ((root * root) < radicand) {
            ++root;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: false, magnitude: root, result: out result);
    }
}
