using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The single internal substrate for the fused one-rounding discipline over <see cref="FixedQ4816"/>: the
/// sign-magnitude product accumulation, exact restoring division, power-of-two scaling, and Q48 rounding shared by the
/// hand-written planar and quaternion types and by the generic algebra descriptors. Every helper is exact integer
/// arithmetic on raw carrier bits, so the callers that once carried private copies of these kernels now round each
/// returned component identically. Not part of the public API.
/// </summary>
internal static class FusedArithmetic {
    /// <summary>Returns the unsigned magnitude of a raw carrier value by the branchless sign trick.</summary>
    /// <param name="value">The signed raw value.</param>
    /// <returns><c>|value|</c> as an unsigned magnitude (<c>long.MinValue</c> maps exactly to <c>2^63</c>).</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static ulong RawMagnitude(long value) {
        var sign = (value >> 63);

        return unchecked((ulong)((value ^ sign) - sign));
    }

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
        var first = ((Int128)firstLeft * firstRight);
        var second = ((Int128)secondLeft * secondRight);
        var firstNegative = (first < Int128.Zero);
        var secondProductNegative = (second < Int128.Zero);
        var secondNegative = secondProductNegative ^ subtractSecond;
        var firstMagnitude = (UInt128)(firstNegative ? -first : first);
        var secondMagnitude = (UInt128)(secondProductNegative ? -second : second);

        if (firstNegative == secondNegative) {
            return (firstNegative, (firstMagnitude + secondMagnitude));
        }

        return ((firstMagnitude >= secondMagnitude)
            ? (firstNegative, (firstMagnitude - secondMagnitude))
            : (secondNegative, (secondMagnitude - firstMagnitude)));
    }

    /// <summary>Forms a single raw Q32 product as sign plus <see cref="UInt128"/> magnitude.</summary>
    /// <param name="left">The left factor.</param>
    /// <param name="right">The right factor.</param>
    /// <returns>The signed product.</returns>
    internal static (bool Negative, UInt128 Magnitude) Product(long left, long right) {
        var product = ((Int128)left * right);

        return ((product < Int128.Zero), (UInt128)((product < Int128.Zero) ? -product : product));
    }

    /// <summary>Returns the exact square of a raw carrier value as a <see cref="UInt128"/>.</summary>
    /// <param name="value">The raw value to square.</param>
    /// <returns><c>value²</c>.</returns>
    internal static UInt128 SquareMagnitude(long value) {
        var magnitude = RawMagnitude(value: value);

        return ((UInt128)magnitude * magnitude);
    }

    /// <summary>Rounds <c>numerator / denominator · 2^16</c> to raw Q16 over an unsigned denominator, once, ties to even.</summary>
    /// <param name="numerator">The signed numerator as sign plus magnitude.</param>
    /// <param name="denominator">The unsigned denominator magnitude; must be non-zero.</param>
    /// <returns>The raw Q16 quotient, wrapped to the signed 64-bit carrier.</returns>
    internal static long DivideProductSum((bool Negative, UInt128 Magnitude) numerator, UInt128 denominator) =>
        DivideProductSumCore(numerator: numerator, denominatorMagnitude: denominator, resultNegative: numerator.Negative);

    /// <summary>Rounds <c>numerator / denominator · 2^16</c> to raw Q16 over a signed denominator, once, ties to even.</summary>
    /// <param name="numerator">The signed numerator as sign plus magnitude.</param>
    /// <param name="denominator">The signed denominator as sign plus magnitude; its magnitude must be non-zero.</param>
    /// <returns>The raw Q16 quotient with the combined sign, wrapped to the signed 64-bit carrier.</returns>
    internal static long DivideProductSum((bool Negative, UInt128 Magnitude) numerator, (bool Negative, UInt128 Magnitude) denominator) =>
        DivideProductSumCore(numerator: numerator, denominatorMagnitude: denominator.Magnitude, resultNegative: numerator.Negative ^ denominator.Negative);

    // round((numerator / denominator) · 2^16), ties to even, retaining the carrier's wrapping result semantics.
    // Splitting off the integer quotient avoids a potentially oversized numerator; the remaining sixteen fractional
    // bits are generated by overflow-safe restoring division (denominator − remainder replaces the unsafe 2·r test).
    private static long DivideProductSumCore((bool Negative, UInt128 Magnitude) numerator, UInt128 denominatorMagnitude, bool resultNegative) {
        var integer = (numerator.Magnitude / denominatorMagnitude);
        var remainder = (numerator.Magnitude - (integer * denominatorMagnitude));
        var quotient = unchecked(((ulong)integer << FixedQ4816.FractionBitCount));

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

        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && ((quotient & 1UL) != 0UL))) {
            ++quotient;
        }

        var result = unchecked((long)quotient);

        return (resultNegative
            ? unchecked(-result)
            : result);
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

            if ((remainder > half) || ((remainder == half) && ((magnitude & UInt128.One) != UInt128.Zero))) {
                ++magnitude;
            }
        }

        var raw = unchecked((long)magnitude);

        return (value.Negative
            ? unchecked(-raw)
            : raw);
    }

    /// <summary>Returns the position of the most significant set bit of a <see cref="UInt128"/>, or zero when it is zero.</summary>
    /// <param name="value">The value whose bit length is taken.</param>
    /// <returns>The number of bits needed to represent <paramref name="value"/>.</returns>
    internal static int BitLength(UInt128 value) {
        var high = ((ulong)(value >> 64));

        return ((high != 0UL)
            ? (128 - BitOperations.LeadingZeroCount(value: high))
            : (64 - BitOperations.LeadingZeroCount(value: ((ulong)value))));
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
    // The number of significant (non-zero) low limbs of a magnitude.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static int SignificantLength(ReadOnlySpan<ulong> magnitude) {
        for (var index = (magnitude.Length - 1); (index >= 0); --index) {
            if (0UL != magnitude[index]) { return (index + 1); }
        }

        return 0;
    }

    // Sets the magnitude to |value| and returns the sign, from a 64-bit input (long.MinValue's magnitude 2^63 casts
    // exactly through the unchecked two's-complement negation).
    internal static sbyte SetFromInt64(Span<ulong> magnitude, long value) {
        magnitude.Clear();

        if (0L == value) { return 0; }

        magnitude[0] = unchecked((ulong)((value < 0L) ? -value : value));

        return (sbyte)((value < 0L) ? -1 : 1);
    }

    // Sets the magnitude to |value| and returns the sign, from a 128-bit input (a raw coordinate product, |value| < 2^127).
    internal static sbyte SetFromInt128(Span<ulong> magnitude, Int128 value) {
        magnitude.Clear();

        if (Int128.Zero == value) { return 0; }

        var absolute = unchecked((UInt128)((value < Int128.Zero) ? -value : value));

        magnitude[0] = unchecked((ulong)absolute);
        magnitude[1] = unchecked((ulong)(absolute >> 64));

        return (sbyte)((value < Int128.Zero) ? -1 : 1);
    }

    // Copies a magnitude into a cleared destination of equal or greater width.
    internal static void CopyMagnitude(Span<ulong> destination, ReadOnlySpan<ulong> source) {
        destination.Clear();
        source.Slice(start: 0, length: Math.Min(val1: destination.Length, val2: source.Length)).CopyTo(destination: destination);
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

                magnitude[index] = ((source >= 0) ? magnitude[source] : 0UL);
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

                magnitude[index] = ((source < length) ? magnitude[source] : 0UL);
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

    // Sets destination = |source·multiplier| and returns the signed result's sign.
    internal static sbyte MultiplyByInt64(Span<ulong> destination, ReadOnlySpan<ulong> source, sbyte sourceSign, long multiplier) {
        destination.Clear();

        if ((0 == sourceSign) || (0L == multiplier)) { return 0; }

        var factor = unchecked((ulong)((multiplier < 0L) ? -multiplier : multiplier));
        var length = SignificantLength(magnitude: source);
        UInt128 carry = 0;

        for (var index = 0; (index < length); ++index) {
            var product = (((UInt128)source[index] * factor) + carry);

            destination[index] = unchecked((ulong)product);
            carry = (product >> 64);
        }

        if (0 != (ulong)carry) { destination[length] = unchecked((ulong)carry); }

        return (sbyte)(sourceSign * ((multiplier < 0L) ? -1 : 1));
    }

    // Sets destination = left·right (schoolbook) and returns the product sign; destination must not alias either input.
    internal static sbyte MultiplyFull(Span<ulong> destination, ReadOnlySpan<ulong> left, sbyte leftSign, ReadOnlySpan<ulong> right, sbyte rightSign) {
        destination.Clear();

        if ((0 == leftSign) || (0 == rightSign)) { return 0; }

        var leftLength = SignificantLength(magnitude: left);
        var rightLength = SignificantLength(magnitude: right);

        for (var i = 0; (i < leftLength); ++i) {
            if (0UL == left[i]) { continue; }

            var leftLimb = left[i];
            UInt128 carry = 0;

            for (var j = 0; (j < rightLength); ++j) {
                var slot = (i + j);
                var product = ((((UInt128)leftLimb * right[j]) + destination[slot]) + carry);

                destination[slot] = unchecked((ulong)product);
                carry = (product >> 64);
            }

            var carrySlot = (i + rightLength);

            while (0UL != (ulong)carry) {
                var sum = ((UInt128)destination[carrySlot] + carry);

                destination[carrySlot] = unchecked((ulong)sum);
                carry = (sum >> 64);
                ++carrySlot;
            }
        }

        return (sbyte)(leftSign * rightSign);
    }

    // Adds a signed addend into a signed destination, in place, returning the new sign.
    internal static sbyte AddInto(Span<ulong> destination, sbyte destinationSign, ReadOnlySpan<ulong> addend, sbyte addendSign) {
        if (0 == addendSign) { return destinationSign; }
        if (0 == destinationSign) { CopyMagnitude(destination: destination, source: addend); return addendSign; }
        if (destinationSign == addendSign) { AddMagnitudeInto(destination: destination, addend: addend); return destinationSign; }

        var comparison = CompareMagnitude(left: destination, right: addend);

        if (0 == comparison) { destination.Clear(); return 0; }

        if (comparison > 0) {
            SubtractMagnitudeInto(destination: destination, subtrahend: addend);

            return destinationSign;
        }

        SubtractMagnitudeReverse(destination: destination, minuend: addend);

        return addendSign;
    }

    // Rounds the signed value (magnitude, sign) divided by 2^shift to nearest, ties to even, wrapping to 64 bits. The
    // shift is any non-negative bit count; zero discards nothing, so the magnitude's low 64 bits pass through with only
    // the sign applied and the half-bit inspection — which would read the bit below position zero — is skipped.
    internal static long RoundAtShift(ReadOnlySpan<ulong> magnitude, sbyte sign, int shift) {
        if (0 == sign) { return 0L; }

        var truncatedLow = ExtractLow64(magnitude: magnitude, shift: shift);
        var roundUp = false;

        if (0 != shift) {
            var halfBit = TestBit(magnitude: magnitude, position: (shift - 1));
            var lowerAny = AnyBitBelow(magnitude: magnitude, position: (shift - 1));

            roundUp = (halfBit && (lowerAny || (0UL != (truncatedLow & 1UL))));
        }

        var result = unchecked((long)(truncatedLow + (roundUp ? 1UL : 0UL)));

        return ((sign < 0) ? unchecked(-result) : result);
    }

    private static int CompareMagnitude(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right) {
        var length = Math.Max(val1: left.Length, val2: right.Length);

        for (var index = (length - 1); (index >= 0); --index) {
            var leftValue = ((index < left.Length) ? left[index] : 0UL);
            var rightValue = ((index < right.Length) ? right[index] : 0UL);

            if (leftValue != rightValue) { return ((leftValue < rightValue) ? -1 : 1); }
        }

        return 0;
    }
    private static void AddMagnitudeInto(Span<ulong> destination, ReadOnlySpan<ulong> addend) {
        UInt128 carry = 0;

        for (var index = 0; (index < destination.Length); ++index) {
            var sum = (((UInt128)destination[index] + ((index < addend.Length) ? addend[index] : 0UL)) + carry);

            destination[index] = unchecked((ulong)sum);
            carry = (sum >> 64);
        }
    }

    // destination = destination − subtrahend, with |destination| ≥ |subtrahend|.
    private static void SubtractMagnitudeInto(Span<ulong> destination, ReadOnlySpan<ulong> subtrahend) {
        UInt128 borrow = 0;

        for (var index = 0; (index < destination.Length); ++index) {
            var minuendLimb = (UInt128)destination[index];
            var subtractor = ((UInt128)((index < subtrahend.Length) ? subtrahend[index] : 0UL) + borrow);

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
            var minuendLimb = (UInt128)((index < minuend.Length) ? minuend[index] : 0UL);
            var subtractor = ((UInt128)destination[index] + borrow);

            if (minuendLimb >= subtractor) {
                destination[index] = unchecked((ulong)(minuendLimb - subtractor));
                borrow = 0;
            } else {
                destination[index] = unchecked((ulong)((minuendLimb + (UInt128.One << 64)) - subtractor));
                borrow = 1;
            }
        }
    }
    private static ulong ExtractLow64(ReadOnlySpan<ulong> magnitude, int shift) {
        var limb = (shift >> 6);
        var bit = shift & 63;
        var low = ((limb < magnitude.Length) ? magnitude[limb] : 0UL);

        if (0 == bit) { return low; }

        var high = (((limb + 1) < magnitude.Length) ? magnitude[(limb + 1)] : 0UL);

        return (low >> bit) | (high << (64 - bit));
    }
    private static bool TestBit(ReadOnlySpan<ulong> magnitude, int position) {
        var limb = (position >> 6);
        var bit = position & 63;

        return ((limb < magnitude.Length) && (0UL != ((magnitude[limb] >> bit) & 1UL)));
    }
    private static bool AnyBitBelow(ReadOnlySpan<ulong> magnitude, int position) {
        var limb = (position >> 6);
        var bit = position & 63;

        for (var index = 0; ((index < limb) && (index < magnitude.Length)); ++index) {
            if (0UL != magnitude[index]) { return true; }
        }

        return ((0 != bit) && (limb < magnitude.Length) && (0UL != (magnitude[limb] & ((1UL << bit) - 1UL))));
    }
}
