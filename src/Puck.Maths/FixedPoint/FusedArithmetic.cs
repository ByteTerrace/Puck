using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The single internal substrate for the fused one-rounding discipline over <see cref="FixedQ4816"/>: the
/// sign-magnitude product accumulation, exact restoring division, power-of-two scaling, and Q48 rounding shared by the
/// hand-written planar and quaternion types and by the generic algebra descriptors. Every helper is exact integer
/// arithmetic on raw carrier bits, so the callers that once carried private copies of these kernels now round each
/// returned component identically. Public refusing faces expose complete mixed-scale operations to other assemblies;
/// the sign-magnitude building blocks and wrapping kernels remain internal to the fixed-point family.
/// </summary>
public static class FusedArithmetic {
    /// <summary>Accumulates the exact signed sum <c>firstLeft·firstRight ± secondLeft·secondRight</c> of two raw Q32 products as sign plus <see cref="UInt128"/> magnitude.</summary>
    /// <param name="firstLeft">The first product's left factor.</param>
    /// <param name="firstRight">The first product's right factor.</param>
    /// <param name="secondLeft">The second product's left factor.</param>
    /// <param name="secondRight">The second product's right factor.</param>
    /// <param name="subtractSecond">When <see langword="true"/>, the second product is subtracted rather than added.</param>
    /// <returns>The signed sum; the magnitude is tracked separately because the Q32 sum is one bit too wide for signed <see cref="Int128"/> at the extremes.</returns>
    internal static (bool Negative, UInt128 Magnitude) AddProducts(
        long firstLeft,
        long firstRight,
        long secondLeft,
        long secondRight,
        bool subtractSecond = false
    ) {
        var first = Product(
            left: firstLeft,
            right: firstRight
        );
        var second = Product(
            left: secondLeft,
            right: secondRight
        );

        return CombineSigned(
            firstMagnitude: first.Magnitude,
            firstNegative: first.Negative,
            secondMagnitude: second.Magnitude,
            secondNegative: second.Negative ^ subtractSecond
        );
    }
    /// <summary>Returns the position of the most significant set bit of a <see cref="UInt128"/>, or zero when it is zero.</summary>
    /// <param name="value">The value whose bit length is taken.</param>
    /// <returns>The number of bits needed to represent <paramref name="value"/>.</returns>
    internal static int BitLength(UInt128 value) {
        var high = ((ulong)(value >> 64));

        return ((high != 0UL)
            ? (128 - BitOperations.LeadingZeroCount(value: high))
            : (64 - BitOperations.LeadingZeroCount(value: ((ulong)value)))
        );
    }
    /// <summary>Combines two already-formed sign-plus-magnitude terms into their exact signed sum, at whatever scale the caller's two terms already share. The one sign-magnitude addition/subtraction tail every multi-term fused kernel in this file funnels through — <see cref="AddProducts"/> forms its two terms as fresh products and calls straight through; a caller whose terms are not both bare products (a shift, a pre-combined sub-sum) calls this directly rather than re-deriving the same tail.</summary>
    /// <param name="firstNegative">Whether the first term is negative.</param>
    /// <param name="firstMagnitude">The first term's magnitude.</param>
    /// <param name="secondNegative">Whether the second term is negative.</param>
    /// <param name="secondMagnitude">The second term's magnitude.</param>
    /// <returns>The signed sum as sign plus magnitude. The caller is responsible for the two magnitudes not overflowing <see cref="UInt128"/> when added.</returns>
    internal static (bool Negative, UInt128 Magnitude) CombineSigned(
        bool firstNegative,
        UInt128 firstMagnitude,
        bool secondNegative,
        UInt128 secondMagnitude
    ) {
        if (firstNegative == secondNegative) {
            return (firstNegative, (firstMagnitude + secondMagnitude));
        }

        return ((firstMagnitude >= secondMagnitude)
            ? (firstNegative, (firstMagnitude - secondMagnitude))
            : (secondNegative, (secondMagnitude - firstMagnitude))
        );
    }
    /// <summary>Rounds <c>numerator / denominator · 2^16</c> to raw Q16 over an unsigned denominator, once, ties to even.</summary>
    /// <param name="numerator">The signed numerator as sign plus magnitude.</param>
    /// <param name="denominator">The unsigned denominator magnitude; must be non-zero.</param>
    /// <returns>The raw Q16 quotient, wrapped to the signed 64-bit carrier.</returns>
    internal static long DivideProductSum((bool Negative, UInt128 Magnitude) numerator, UInt128 denominator) =>
        DivideProductSumCore(
            denominatorMagnitude: denominator,
            numerator: numerator,
            resultNegative: numerator.Negative
        );
    /// <summary>Rounds <c>numerator / denominator · 2^16</c> to raw Q16 over a signed denominator, once, ties to even.</summary>
    /// <param name="numerator">The signed numerator as sign plus magnitude.</param>
    /// <param name="denominator">The signed denominator as sign plus magnitude; its magnitude must be non-zero.</param>
    /// <returns>The raw Q16 quotient with the combined sign, wrapped to the signed 64-bit carrier.</returns>
    internal static long DivideProductSum((bool Negative, UInt128 Magnitude) numerator, (bool Negative, UInt128 Magnitude) denominator) =>
        DivideProductSumCore(
            denominatorMagnitude: denominator.Magnitude,
            numerator: numerator,
            resultNegative: numerator.Negative ^ denominator.Negative
        );
    /// <summary>Multiplies two raws carried at DIFFERENT fixed-point scales and rounds the result to a third scale
    /// exactly once, to nearest with ties to even, wrapping on overflow.</summary>
    /// <param name="a">The first factor's raw.</param>
    /// <param name="fractionBitsA">The first factor's fraction bit count.</param>
    /// <param name="b">The second factor's raw.</param>
    /// <param name="fractionBitsB">The second factor's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <returns>The raw product at <paramref name="fractionBitsOut"/>.</returns>
    /// <remarks>The kernel an inverse mass at one scale and an impulse at another need: rounding either operand onto a
    /// shared scale first would cost a rounding the mixed-scale caller does not have to pay. The whole product is
    /// formed exactly (two 64-bit factors reach at most <c>2^126</c>, inside <see cref="UInt128"/>) and the single
    /// rounding happens at <c>2^(fractionBitsOut − fractionBitsA − fractionBitsB)</c>. The three counts are combined in
    /// <see cref="long"/> so no combination of <see cref="int"/> extremes can wrap the exponent.</remarks>
    internal static long MixedScaleProduct(long a, int fractionBitsA, long b, int fractionBitsB, int fractionBitsOut) {
        var product = Product(
            left: a,
            right: b
        );
        var scaled = ScaleMagnitudeToNearest(
            magnitude: product.Magnitude,
            shift: MixedScaleShift(
                first: fractionBitsA,
                fractionBitsOut: fractionBitsOut,
                second: fractionBitsB
            )
        );

        return WrapSignedMagnitude(
            magnitude: scaled.Magnitude,
            negative: product.Negative
        );
    }
    // The exponent every mixed-scale kernel rounds at, formed in long so no combination of int extremes wraps it.
    internal static long MixedScaleShift(int fractionBitsOut, int first, int second) =>
        ((((long)fractionBitsOut) - first) - second);
    /// <summary>Forms a single raw Q32 product as sign plus <see cref="UInt128"/> magnitude.</summary>
    /// <param name="left">The left factor.</param>
    /// <param name="right">The right factor.</param>
    /// <returns>The signed product.</returns>
    internal static (bool Negative, UInt128 Magnitude) Product(long left, long right) {
        var product = (((Int128)left) * right);

        return ((product < Int128.Zero), ((UInt128)((product < Int128.Zero)
            ? -product
            : product)));
    }
    /// <summary>Returns the unsigned magnitude of a raw carrier value by the branchless sign trick.</summary>
    /// <param name="value">The signed raw value.</param>
    /// <returns><c>|value|</c> as an unsigned magnitude (<c>long.MinValue</c> maps exactly to <c>2^63</c>).</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static ulong RawMagnitude(long value) {
        var sign = (value >> 63);

        return unchecked((ulong)((value ^ sign) - sign));
    }
    /// <summary>Rounds a Q48-scaled product sum to raw Q16, once, to nearest with ties to even, wrapping to the signed 64-bit carrier.</summary>
    /// <param name="productSum">The exact (or unchecked-<see cref="Int128"/>-congruent) Q48 sum.</param>
    /// <returns>The raw Q16 result.</returns>
    /// <remarks>A shift of 32 (Q48 → Q16) turns an <see cref="Int128"/> wrap of <c>k·2^128</c> into <c>k·2^96</c> on the
    /// rounded result, which the final 64-bit wrap erases (<c>2^96 ≡ 0 mod 2^64</c>) without changing tie parity.</remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long RoundQ48SumToRaw(Int128 productSum) => FixedQ4816.RoundProduct(
        product: productSum,
        fractionBitCount: (2 * FixedQ4816.FractionBitCount) // Q48 → Q16.
    );
    /// <summary>Rounds the exact magnitude <c>magnitude · 2^shift</c> to an integer magnitude, once, AWAY FROM ZERO —
    /// the directed-up sibling of <see cref="ScaleMagnitudeToNearest"/>, for the conservative bounds a speculative
    /// contact or time-of-impact test needs. A caller wanting an upper bound calls this rather than adding a unit in
    /// the last place to a nearest result, which overshoots whenever the nearest result was already an upper bound and
    /// undershoots nothing it would have caught.</summary>
    /// <param name="magnitude">The exact non-negative magnitude to scale.</param>
    /// <param name="shift">The signed power-of-two exponent; a negative value rounds the discarded low bits up.</param>
    /// <returns>The scaled magnitude and the same overflow flag <see cref="ScaleMagnitudeToNearest"/> reports.</returns>
    internal static (UInt128 Magnitude, bool Overflowed) ScaleMagnitudeToCeiling(UInt128 magnitude, long shift) {
        if (shift >= 0L) {
            return ShiftLeftCongruent(
                magnitude: magnitude,
                shift: shift
            );
        }

        var split = SplitAtRightShift(
            magnitude: magnitude,
            rightShift: -shift
        );

        return ((split.AnyDiscarded
            ? (split.Quotient + UInt128.One)
            : split.Quotient), false);
    }
    /// <summary>Rounds the exact magnitude <c>magnitude · 2^shift</c> to an integer magnitude, once, to nearest with
    /// ties to even, for a <paramref name="shift"/> of either sign and any width.</summary>
    /// <param name="magnitude">The exact non-negative magnitude to scale.</param>
    /// <param name="shift">The signed power-of-two exponent; a negative value rounds the discarded low bits.</param>
    /// <returns>The scaled magnitude — congruent to the true value modulo <c>2^128</c> — and whether a non-negative
    /// shift carried that true value past <see cref="UInt128"/>'s width. A wrapping caller ignores the flag; a
    /// refusing caller reads it before trusting the magnitude.</returns>
    internal static (UInt128 Magnitude, bool Overflowed) ScaleMagnitudeToNearest(UInt128 magnitude, long shift) {
        if (shift >= 0L) {
            return ShiftLeftCongruent(
                magnitude: magnitude,
                shift: shift
            );
        }

        var split = SplitAtRightShift(
            magnitude: magnitude,
            rightShift: -shift
        );
        var quotient = split.Quotient;

        if (
            split.AboveHalf ||
            (split.AtHalf && ((quotient & UInt128.One) != UInt128.Zero))
        ) {
            ++quotient;
        }

        return (quotient, false);
    }
    /// <summary>Scales a signed sign-magnitude value by a power of two, rounding a negative shift to even.</summary>
    /// <param name="value">The signed value as sign plus magnitude.</param>
    /// <param name="shift">The signed power-of-two exponent; a negative value rounds the discarded low bits to nearest, ties to even.</param>
    /// <returns>The scaled raw value, wrapping to the signed 64-bit carrier the way every sibling kernel's narrowing
    /// does.</returns>
    internal static long ScaleProductSum((bool Negative, UInt128 Magnitude) value, int shift) {
        UInt128 magnitude;

        if (shift >= 0) {
            magnitude = (value.Magnitude << shift);
        } else {
            var rightShift = -shift;

            magnitude = (value.Magnitude >> rightShift);
            var remainder = value.Magnitude & ((((UInt128)1) << rightShift) - UInt128.One);
            var half = (((UInt128)1) << (rightShift - 1));

            if (
                (remainder > half) ||
                ((remainder == half) && ((magnitude & UInt128.One) != UInt128.Zero))
            ) {
                ++magnitude;
            }
        }

        var raw = unchecked((long)magnitude);

        return (value.Negative
            ? unchecked(-raw)
            : raw
        );
    }
    /// <summary>Returns the exact square of a raw carrier value as a <see cref="UInt128"/>.</summary>
    /// <param name="value">The raw value to square.</param>
    /// <returns><c>value²</c>.</returns>
    internal static UInt128 SquareMagnitude(long value) {
        var magnitude = RawMagnitude(value: value);

        return (((UInt128)magnitude) * magnitude);
    }
    /// <summary>Reads a sign-plus-magnitude value back as a raw <see cref="long"/>, REFUSING rather than wrapping when
    /// it does not fit — the one narrowing tail every checked kernel in the fixed-point family crosses back through.</summary>
    /// <param name="negative">Whether the value is negative.</param>
    /// <param name="magnitude">The value's magnitude.</param>
    /// <param name="result">The raw value on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the magnitude exceeds what the signed 64-bit carrier holds for that sign
    /// (<c>2^63</c> for a negative value, <c>2^63 − 1</c> for a non-negative one).</returns>
    internal static bool TryNarrowSignedMagnitude(bool negative, UInt128 magnitude, out long result) {
        var limit = (negative
            ? (((UInt128)long.MaxValue) + UInt128.One)
            : (UInt128)long.MaxValue
        );

        if (magnitude > limit) {
            result = 0L;
            return false;
        }

        var raw = unchecked((long)((ulong)magnitude));

        result = (negative
            ? unchecked(-raw)
            : raw
        );
        return true;
    }
    /// <summary>Reads a sign-plus-magnitude value back as a raw <see cref="long"/>, wrapping the way every narrowing
    /// in this file does.</summary>
    /// <param name="negative">Whether the value is negative.</param>
    /// <param name="magnitude">The value's magnitude.</param>
    /// <returns>The raw value, wrapped to the signed 64-bit carrier.</returns>
    internal static long WrapSignedMagnitude(bool negative, UInt128 magnitude) {
        var raw = unchecked((long)magnitude);

        return (negative
            ? unchecked(-raw)
            : raw
        );
    }

    // round((numerator / denominator) · 2^16), ties to even, retaining the carrier's wrapping result semantics.
    // Splitting off the integer quotient avoids a potentially oversized numerator; the remaining sixteen fractional
    // bits are generated by overflow-safe restoring division (denominator − remainder replaces the unsafe 2·r test).
    private static long DivideProductSumCore((bool Negative, UInt128 Magnitude) numerator, UInt128 denominatorMagnitude, bool resultNegative) {
        var integer = (numerator.Magnitude / denominatorMagnitude);
        var remainder = (numerator.Magnitude - (integer * denominatorMagnitude));
        var quotient = unchecked((((ulong)integer) << FixedQ4816.FractionBitCount));

        for (var bit = (FixedQ4816.FractionBitCount - 1); (bit >= 0); --bit) {
            var complement = (denominatorMagnitude - remainder);

            if (remainder >= complement) {
                remainder -= complement;
                quotient |= (1UL << bit);
            } else {
                remainder <<= 1;
            }
        }

        var distanceToNext = (denominatorMagnitude - remainder);

        if (
            (remainder > distanceToNext) ||
            ((remainder == distanceToNext) && ((quotient & 1UL) != 0UL))
        ) {
            ++quotient;
        }

        var result = unchecked((long)quotient);

        return (resultNegative
            ? unchecked(-result)
            : result
        );
    }
    // A left shift that keeps the result congruent to the true value modulo 2^128 and reports whether the true value
    // left that width. A count at or past 128 is answered directly: the CLR masks a UInt128 shift count modulo 128,
    // which would fold a high bit back onto a low one instead of clearing the word.
    private static (UInt128 Magnitude, bool Overflowed) ShiftLeftCongruent(UInt128 magnitude, long shift) {
        if (magnitude == UInt128.Zero) { return (UInt128.Zero, false); }

        if (shift >= 128L) { return (UInt128.Zero, true); }

        return ((magnitude << ((int)shift)), ((BitLength(value: magnitude) + shift) > 128L));
    }
    // The one right-shift split both directed faces read: the truncated quotient, whether anything was discarded (the
    // ceiling's whole decision), and whether the discarded part is above or exactly at half a discarded unit (the
    // nearest face's). A count at or past 129 puts the half-unit strictly above every 128-bit magnitude, so it is
    // answered without forming a half that would not fit.
    private static (UInt128 Quotient, bool AnyDiscarded, bool AboveHalf, bool AtHalf) SplitAtRightShift(UInt128 magnitude, long rightShift) {
        if (rightShift >= 129L) {
            return (UInt128.Zero, (magnitude != UInt128.Zero), false, false);
        }

        var wide = (rightShift >= 128L);
        var quotient = (wide
            ? UInt128.Zero
            : (magnitude >> ((int)rightShift))
        );
        var remainder = (wide
            ? magnitude
            : (magnitude - (quotient << ((int)rightShift)))
        );
        var half = (UInt128.One << ((int)(rightShift - 1L)));

        return (quotient, (remainder != UInt128.Zero), (remainder > half), (remainder == half));
    }

    /// <summary>Rounds <c>numeratorMagnitude / denominatorMagnitude · 2^fractionBitCount</c> to an exact unsigned
    /// magnitude, once, ties to even — the same integer-quotient-plus-restoring-division shape
    /// <see cref="DivideProductSumCore"/> runs at its own fixed count, generalized to an arbitrary
    /// non-negative <paramref name="fractionBitCount"/> and returned WITHOUT narrowing to any carrier, so a caller
    /// whose fraction count is not always 16 (<see cref="FixedSymmetricSolve"/>'s caller-supplied output scale) shares
    /// this one loop instead of copying it.</summary>
    /// <param name="numeratorMagnitude">The exact non-negative numerator.</param>
    /// <param name="denominatorMagnitude">The exact non-negative denominator.</param>
    /// <param name="fractionBitCount">The number of extra bits to generate below the integer quotient; zero rounds a
    /// plain division to the nearest integer.</param>
    /// <param name="quotient">The rounded magnitude on success; default on refusal.</param>
    /// <returns><see langword="false"/> when <paramref name="fractionBitCount"/> is negative — checked FIRST, before
    /// any shift or loop is set up, so a negative count can never reach the starting <see cref="UInt128"/> shift (which
    /// would alias it modulo 128) or the restoring loop (which would count down from it, billions of iterations for a
    /// large-magnitude negative count such as <see cref="int.MinValue"/>) — when <paramref name="denominatorMagnitude"/>
    /// is zero (the caller's singularity signal), or when the integer quotient's bit length plus
    /// <paramref name="fractionBitCount"/> would leave fewer than one clear bit below <see cref="UInt128"/>'s width —
    /// the shift that builds the starting quotient would silently lose bits rather than compute the true value, so
    /// this refuses instead of answering a wrapped one. A caller narrowing further (to a signed 64-bit raw) must
    /// still check its own, tighter range.</returns>
    public static bool TryDivideMagnitudeRounded(UInt128 numeratorMagnitude, UInt128 denominatorMagnitude, int fractionBitCount, out UInt128 quotient) {
        // Checked before anything else touches fractionBitCount: a negative count must never reach the starting
        // shift or the loop below. The prior guard here was Debug.Assert alone, which is compiled out in Release and
        // therefore enforced nothing outside Debug — int.MinValue reached the loop and counted down 2,147,483,648
        // times before returning a wrapped "success". This is real refusal, not a debugging aid.
        if (fractionBitCount < 0) {
            quotient = default;
            return false;
        }

        if (denominatorMagnitude == UInt128.Zero) {
            quotient = default;
            return false;
        }

        var integer = (numeratorMagnitude / denominatorMagnitude);

        // The width check must run even when the integer quotient is zero: a zero integer still has a starting
        // bit length of zero (BitLength's own zero case), and a fractionBitCount alone at or past 128 would still
        // build a bit index the loop below cannot address without a CLR shift-count alias (count masked modulo
        // 128) silently wrapping a high bit back onto a low one instead of refusing. The sum is widened to `long`
        // BEFORE adding: `fractionBitCount` alone can be as large as `int.MaxValue`, and `BitLength(...) +
        // fractionBitCount` computed in `int` wraps past `int.MaxValue` (e.g. `1 + int.MaxValue` becomes
        // `int.MinValue`), which would slip under the `> 127` test and let the loop below run on an aliased shift
        // count for billions of iterations before returning garbage as success. `long` has no such ceiling for any
        // combination of a 0-128 bit length and an `int` fraction count, so this comparison can never wrap.
        if ((((long)BitLength(value: integer)) + fractionBitCount) > 127L) {
            quotient = default;
            return false;
        }

        var remainder = (numeratorMagnitude - (integer * denominatorMagnitude));
        var result = (integer << fractionBitCount);

        // Overflow-safe restoring division: the compare is against `denominatorMagnitude - remainder`, never
        // `2 * remainder`, so the remainder is only ever doubled when that doubling is already known to stay
        // below the denominator (the invariant `remainder < denominatorMagnitude` holding on entry to each
        // iteration). Doubling unconditionally first — the shape this loop replaces — can wrap `remainder` past
        // UInt128's own ceiling when it is already in the top half of its range, corrupting the quotient silently.
        for (var bit = (fractionBitCount - 1); (bit >= 0); --bit) {
            var complement = (denominatorMagnitude - remainder);

            if (remainder >= complement) {
                remainder -= complement;
                result |= (UInt128.One << bit);
            } else {
                remainder <<= 1;
            }
        }

        var distanceToNext = (denominatorMagnitude - remainder);

        if (
            (remainder > distanceToNext) ||
            ((remainder == distanceToNext) && ((result & UInt128.One) != UInt128.Zero))
        ) {
            ++result;
        }

        quotient = result;
        return true;
    }
    /// <summary>Rounds the dot product of two three-component raw vectors carried at independent fixed-point scales
    /// onto a third scale exactly once, to nearest with ties to even, refusing rather than wrapping.</summary>
    /// <param name="ax">The first vector's X raw.</param>
    /// <param name="ay">The first vector's Y raw.</param>
    /// <param name="az">The first vector's Z raw.</param>
    /// <param name="fractionBitsA">The first vector's fraction bit count.</param>
    /// <param name="bx">The second vector's X raw.</param>
    /// <param name="by">The second vector's Y raw.</param>
    /// <param name="bz">The second vector's Z raw.</param>
    /// <param name="fractionBitsB">The second vector's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The rounded dot product on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when scaling the exact sum leaves <see cref="UInt128"/> or when the rounded
    /// result leaves the signed 64-bit raw.</returns>
    /// <remarks>All three products accumulate exactly in sign-plus-<see cref="UInt128"/> magnitude before the one
    /// rounding, so reassociating the terms cannot change the answer.</remarks>
    public static bool TryMixedScaleDotProduct(
        long ax,
        long ay,
        long az,
        int fractionBitsA,
        long bx,
        long by,
        long bz,
        int fractionBitsB,
        int fractionBitsOut,
        out long result
    ) {
        var accumulator = AddProducts(
            firstLeft: ax,
            firstRight: bx,
            secondLeft: ay,
            secondRight: by
        );
        var third = Product(
            left: az,
            right: bz
        );

        accumulator = CombineSigned(
            firstMagnitude: accumulator.Magnitude,
            firstNegative: accumulator.Negative,
            secondMagnitude: third.Magnitude,
            secondNegative: third.Negative
        );

        var scaled = ScaleMagnitudeToNearest(
            magnitude: accumulator.Magnitude,
            shift: MixedScaleShift(
                first: fractionBitsA,
                fractionBitsOut: fractionBitsOut,
                second: fractionBitsB
            )
        );

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return TryNarrowSignedMagnitude(
            magnitude: scaled.Magnitude,
            negative: accumulator.Negative,
            result: out result
        );
    }
    /// <summary>The refusing face of <see cref="MixedScaleProduct(long, int, long, int, int)"/>: the same single
    /// rounding, declining rather than wrapping when the rounded result leaves the signed 64-bit raw.</summary>
    /// <param name="a">The first factor's raw.</param>
    /// <param name="fractionBitsA">The first factor's fraction bit count.</param>
    /// <param name="b">The second factor's raw.</param>
    /// <param name="fractionBitsB">The second factor's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The raw product on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the correctly rounded product does not fit the signed 64-bit raw;
    /// <paramref name="result"/> is zero in that case.</returns>
    public static bool TryMixedScaleProduct(long a, int fractionBitsA, long b, int fractionBitsB, int fractionBitsOut, out long result) {
        var product = Product(
            left: a,
            right: b
        );
        var scaled = ScaleMagnitudeToNearest(
            magnitude: product.Magnitude,
            shift: MixedScaleShift(
                first: fractionBitsA,
                fractionBitsOut: fractionBitsOut,
                second: fractionBitsB
            )
        );

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return TryNarrowSignedMagnitude(
            magnitude: scaled.Magnitude,
            negative: product.Negative,
            result: out result
        );
    }
    /// <summary>Multiplies THREE raws carried at independent fixed-point scales and rounds the result to a fourth
    /// scale exactly once, to nearest with ties to even, refusing rather than wrapping.</summary>
    /// <param name="a">The first factor's raw.</param>
    /// <param name="fractionBitsA">The first factor's fraction bit count.</param>
    /// <param name="b">The second factor's raw.</param>
    /// <param name="fractionBitsB">The second factor's fraction bit count.</param>
    /// <param name="c">The third factor's raw.</param>
    /// <param name="fractionBitsC">The third factor's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The raw product on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the exact triple product leaves <see cref="UInt128"/> — three 64-bit
    /// factors reach <c>2^189</c>, far past what this width holds, and the check is the exact one (the third factor
    /// against <see cref="UInt128.MaxValue"/> divided by the running pair) rather than a conservative bit-length
    /// estimate — or when the correctly rounded result does not fit the signed 64-bit raw. There is no wrapping face:
    /// a wrapped triple product is congruent to nothing a caller can use.</returns>
    public static bool TryMixedScaleProduct(long a, int fractionBitsA, long b, int fractionBitsB, long c, int fractionBitsC, int fractionBitsOut, out long result) {
        var pair = Product(
            left: a,
            right: b
        );
        var third = ((UInt128)RawMagnitude(value: c));
        var negative = pair.Negative ^ (c < 0L);

        if (
            (pair.Magnitude != UInt128.Zero) &&
            (third > (UInt128.MaxValue / pair.Magnitude))
        ) {
            result = 0L;
            return false;
        }

        var shift = (MixedScaleShift(
            first: fractionBitsA,
            fractionBitsOut: fractionBitsOut,
            second: fractionBitsB
        ) - fractionBitsC);
        var scaled = ScaleMagnitudeToNearest(
            magnitude: (pair.Magnitude * third),
            shift: shift
        );

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return TryNarrowSignedMagnitude(
            magnitude: scaled.Magnitude,
            negative: negative,
            result: out result
        );
    }
    /// <summary>Rounds the reciprocal of a strictly positive raw carried at one fixed-point scale onto another scale
    /// exactly once, to nearest with ties to even, refusing rather than wrapping.</summary>
    /// <param name="value">The positive raw to invert.</param>
    /// <param name="fractionBitsIn">The operand's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <param name="result">The rounded reciprocal on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when <paramref name="value"/> is not positive, when the sum of the fraction
    /// bit counts is negative or exceeds <see cref="int.MaxValue"/>, when the exact division leaves the
    /// <see cref="UInt128"/> restoring divider's envelope, or when the rounded reciprocal leaves the signed 64-bit
    /// raw.</returns>
    public static bool TryScaledReciprocal(long value, int fractionBitsIn, int fractionBitsOut, out long result) {
        var fractionBitCount = (((long)fractionBitsIn) + fractionBitsOut);

        if (
            (value <= 0L) ||
            (fractionBitCount < 0L) ||
            (fractionBitCount > int.MaxValue)
        ) {
            result = 0L;
            return false;
        }

        if (!TryDivideMagnitudeRounded(
            numeratorMagnitude: UInt128.One,
            denominatorMagnitude: ((UInt128)((ulong)value)),
            fractionBitCount: ((int)fractionBitCount),
            quotient: out var quotient
        )) {
            result = 0L;
            return false;
        }

        return TryNarrowSignedMagnitude(
            magnitude: quotient,
            negative: false,
            result: out result
        );
    }
}

/// <summary>
/// The exact signed multi-limb accumulator serving <c>Algebra/MonogenicAlgebra</c>'s higher-degree lanes — no
/// <see cref="FusedArithmetic"/> kernel calls it. Numbers are sign-magnitude: a fixed-width little-endian
/// <see cref="ulong"/> span holds the magnitude and an <see cref="sbyte"/> (−1, 0, +1) the sign. Every operation is
/// schoolbook and exact ONLY under the caller's obligation to size the width to bound the largest value, and the
/// failure shapes on an undersized destination differ: <c>MultiplyByInt64</c> and <c>MultiplyFull</c> throw
/// <see cref="IndexOutOfRangeException"/>, while <c>AddMagnitudeInto</c> drops the carry out of the top limb and
/// <c>CopyMagnitude</c> and <c>ShiftLeft</c> clamp, all silently. Magnitude ops scan for the significant limb count, so cost tracks the
/// actual magnitude rather than the buffer width. Not part of the public API.
/// </summary>
internal static class LimbBig {
    // Adds a signed addend into a signed destination, in place, returning the new sign.
    internal static sbyte AddInto(Span<ulong> destination, sbyte destinationSign, ReadOnlySpan<ulong> addend, sbyte addendSign) {
        if (0 == addendSign) { return destinationSign; }
        if (0 == destinationSign) {
            CopyMagnitude(
                destination: destination,
                source: addend
            ); return addendSign;
        }
        if (destinationSign == addendSign) {
            AddMagnitudeInto(
                addend: addend,
                destination: destination
            ); return destinationSign;
        }

        var comparison = CompareMagnitude(
            left: destination,
            right: addend
        );

        if (0 == comparison) { destination.Clear(); return 0; }

        if (comparison > 0) {
            SubtractMagnitudeInto(
                destination: destination,
                subtrahend: addend
            );

            return destinationSign;
        }

        SubtractMagnitudeReverse(
            destination: destination,
            minuend: addend
        );

        return addendSign;
    }
    // Copies a magnitude into a cleared destination of equal or greater width.
    internal static void CopyMagnitude(Span<ulong> destination, ReadOnlySpan<ulong> source) {
        destination.Clear();
        source.Slice(
            start: 0,
            length: Math.Min(
                val1: destination.Length,
                val2: source.Length
            )
        ).CopyTo(destination: destination);
    }
    // Sets destination = |source·multiplier| and returns the signed result's sign.
    internal static sbyte MultiplyByInt64(Span<ulong> destination, ReadOnlySpan<ulong> source, sbyte sourceSign, long multiplier) {
        destination.Clear();

        if (
            (0 == sourceSign) ||
            (0L == multiplier)
        ) { return 0; }

        var factor = unchecked((ulong)((multiplier < 0L)
            ? -multiplier
            : multiplier));
        var length = SignificantLength(magnitude: source);
        UInt128 carry = 0;

        for (var index = 0; (index < length); ++index) {
            var product = ((((UInt128)source[index]) * factor) + carry);

            destination[index] = unchecked((ulong)product);
            carry = (product >> 64);
        }

        if (0 != ((ulong)carry)) { destination[length] = unchecked((ulong)carry); }

        return ((sbyte)(sourceSign * ((multiplier < 0L)
            ? -1
            : 1)));
    }
    // Sets destination = left·right (schoolbook) and returns the product sign; destination must not alias either input.
    internal static sbyte MultiplyFull(Span<ulong> destination, ReadOnlySpan<ulong> left, sbyte leftSign, ReadOnlySpan<ulong> right, sbyte rightSign) {
        destination.Clear();

        if (
            (0 == leftSign) ||
            (0 == rightSign)
        ) { return 0; }

        var leftLength = SignificantLength(magnitude: left);
        var rightLength = SignificantLength(magnitude: right);

        for (var i = 0; (i < leftLength); ++i) {
            if (0UL == left[i]) { continue; }

            var leftLimb = left[i];
            UInt128 carry = 0;

            for (var j = 0; (j < rightLength); ++j) {
                var slot = (i + j);
                var product = (((((UInt128)leftLimb) * right[j]) + destination[slot]) + carry);

                destination[slot] = unchecked((ulong)product);
                carry = (product >> 64);
            }

            var carrySlot = (i + rightLength);

            while (0UL != ((ulong)carry)) {
                var sum = (((UInt128)destination[carrySlot]) + carry);

                destination[carrySlot] = unchecked((ulong)sum);
                carry = (sum >> 64);
                ++carrySlot;
            }
        }

        return ((sbyte)(leftSign * rightSign));
    }
    // Rounds the signed value (magnitude, sign) divided by 2^shift to nearest, ties to even, wrapping to 64 bits. The
    // shift is any non-negative bit count; zero discards nothing, so the magnitude's low 64 bits pass through with only
    // the sign applied and the half-bit inspection — which would read the bit below position zero — is skipped.
    internal static long RoundAtShift(ReadOnlySpan<ulong> magnitude, sbyte sign, int shift) {
        if (0 == sign) { return 0L; }

        var truncatedLow = ExtractLow64(
            magnitude: magnitude,
            shift: shift
        );
        var roundUp = false;

        if (0 != shift) {
            var halfBit = TestBit(
                magnitude: magnitude,
                position: (shift - 1)
            );
            var lowerAny = AnyBitBelow(
                magnitude: magnitude,
                position: (shift - 1)
            );

            roundUp = (halfBit && (lowerAny || (0UL != (truncatedLow & 1UL))));
        }

        var result = unchecked((long)(truncatedLow + (roundUp
            ? 1UL
            : 0UL)));

        return ((sign < 0)
            ? unchecked(-result)
            : result
        );
    }
    // Sets the magnitude to |value| and returns the sign, from a 128-bit input (a raw coordinate product, |value| < 2^127).
    internal static sbyte SetFromInt128(Span<ulong> magnitude, Int128 value) {
        magnitude.Clear();

        if (Int128.Zero == value) { return 0; }

        var absolute = unchecked((UInt128)((value < Int128.Zero)
            ? -value
            : value));

        magnitude[0] = unchecked((ulong)absolute);
        magnitude[1] = unchecked((ulong)(absolute >> 64));

        return ((sbyte)((value < Int128.Zero)
            ? -1
            : 1));
    }
    // Sets the magnitude to |value| and returns the sign, from a 64-bit input (long.MinValue's magnitude 2^63 casts
    // exactly through the unchecked two's-complement negation).
    internal static sbyte SetFromInt64(Span<ulong> magnitude, long value) {
        magnitude.Clear();

        if (0L == value) { return 0; }

        magnitude[0] = unchecked((ulong)((value < 0L)
            ? -value
            : value));

        return ((sbyte)((value < 0L)
            ? -1
            : 1));
    }
    // Shifts a magnitude left by the given bit count, in place.
    internal static void ShiftLeft(Span<ulong> magnitude, int bits) {
        if (0 == bits) { return; }

        var limbShift = (bits >> 6);
        var bitShift = bits & 63;
        var length = magnitude.Length;

        if (0 == bitShift) {
            for (var index = (length - 1); (index >= 0); --index) {
                var source = (index - limbShift);

                magnitude[index] = ((source >= 0)
                    ? magnitude[source]
                    : 0UL
                );
            }

            return;
        }

        for (var index = (length - 1); (index >= 0); --index) {
            var source = (index - limbShift);
            var value = 0UL;

            if (source >= 0) {
                value = (magnitude[source] << bitShift);

                if (source >= 1) { value |= (magnitude[(source - 1)] >> (64 - bitShift)); }
            }

            magnitude[index] = value;
        }
    }
    // Shifts a magnitude right by the given bit count, in place; the caller guarantees the discarded low bits are zero.
    internal static void ShiftRightExact(Span<ulong> magnitude, int bits) {
        if (0 == bits) { return; }

        var limbShift = (bits >> 6);
        var bitShift = bits & 63;
        var length = magnitude.Length;

        if (0 == bitShift) {
            for (var index = 0; (index < length); ++index) {
                var source = (index + limbShift);

                magnitude[index] = ((source < length)
                    ? magnitude[source]
                    : 0UL
                );
            }

            return;
        }

        for (var index = 0; (index < length); ++index) {
            var source = (index + limbShift);
            var value = 0UL;

            if (source < length) {
                value = (magnitude[source] >> bitShift);

                if ((source + 1) < length) { value |= (magnitude[(source + 1)] << (64 - bitShift)); }
            }

            magnitude[index] = value;
        }
    }
    // The number of significant (non-zero) low limbs of a magnitude.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static int SignificantLength(ReadOnlySpan<ulong> magnitude) {
        for (var index = (magnitude.Length - 1); (index >= 0); --index) {
            if (0UL != magnitude[index]) { return (index + 1); }
        }

        return 0;
    }

    private static void AddMagnitudeInto(Span<ulong> destination, ReadOnlySpan<ulong> addend) {
        UInt128 carry = 0;

        for (var index = 0; (index < destination.Length); ++index) {
            var sum = ((((UInt128)destination[index]) + ((index < addend.Length)
                ? addend[index]
                : 0UL)) + carry);

            destination[index] = unchecked((ulong)sum);
            carry = (sum >> 64);
        }
    }
    private static bool AnyBitBelow(ReadOnlySpan<ulong> magnitude, int position) {
        var limb = (position >> 6);
        var bit = position & 63;

        for (var index = 0; ((index < limb) && (index < magnitude.Length)); ++index) {
            if (0UL != magnitude[index]) { return true; }
        }

        return (
            (0 != bit) &&
            (limb < magnitude.Length) &&
            (0UL != (magnitude[limb] & ((1UL << bit) - 1UL)))
        );
    }
    private static int CompareMagnitude(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right) {
        var length = Math.Max(
            val1: left.Length,
            val2: right.Length
        );

        for (var index = (length - 1); (index >= 0); --index) {
            var leftValue = ((index < left.Length)
                ? left[index]
                : 0UL
            );
            var rightValue = ((index < right.Length)
                ? right[index]
                : 0UL
            );

            if (leftValue != rightValue) {
                return ((leftValue < rightValue)
                    ? -1
                    : 1
                );
            }
        }

        return 0;
    }
    private static ulong ExtractLow64(ReadOnlySpan<ulong> magnitude, int shift) {
        var limb = (shift >> 6);
        var bit = shift & 63;
        var low = ((limb < magnitude.Length)
            ? magnitude[limb]
            : 0UL
        );

        if (0 == bit) { return low; }

        var high = (((limb + 1) < magnitude.Length)
            ? magnitude[(limb + 1)]
            : 0UL
        );

        return (low >> bit) | (high << (64 - bit));
    }
    // destination = destination − subtrahend, with |destination| ≥ |subtrahend|.
    private static void SubtractMagnitudeInto(Span<ulong> destination, ReadOnlySpan<ulong> subtrahend) {
        UInt128 borrow = 0;

        for (var index = 0; (index < destination.Length); ++index) {
            var minuendLimb = ((UInt128)destination[index]);
            var subtractor = (((UInt128)((index < subtrahend.Length)
                ? subtrahend[index]
                : 0UL)) + borrow);

            if (minuendLimb >= subtractor) {
                destination[index] = unchecked((ulong)(minuendLimb - subtractor));
                borrow = 0;
            } else {
                destination[index] = unchecked((ulong)((minuendLimb + (UInt128.One << 64)) - subtractor));
                borrow = 1;
            }
        }
    }
    // destination = minuend − destination, with |minuend| ≥ |destination|.
    private static void SubtractMagnitudeReverse(Span<ulong> destination, ReadOnlySpan<ulong> minuend) {
        UInt128 borrow = 0;

        for (var index = 0; (index < destination.Length); ++index) {
            var minuendLimb = ((UInt128)((index < minuend.Length)
                ? minuend[index]
                : 0UL));
            var subtractor = (((UInt128)destination[index]) + borrow);

            if (minuendLimb >= subtractor) {
                destination[index] = unchecked((ulong)(minuendLimb - subtractor));
                borrow = 0;
            } else {
                destination[index] = unchecked((ulong)((minuendLimb + (UInt128.One << 64)) - subtractor));
                borrow = 1;
            }
        }
    }
    private static bool TestBit(ReadOnlySpan<ulong> magnitude, int position) {
        var limb = (position >> 6);
        var bit = position & 63;

        return (
            (limb < magnitude.Length) &&
            (0UL != ((magnitude[limb] >> bit) & 1UL))
        );
    }
}
