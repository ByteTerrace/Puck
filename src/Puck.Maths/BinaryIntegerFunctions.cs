using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// Provides extension methods that implement bit-level and base-10 digit manipulations over arbitrary binary integer
/// types.
/// </summary>
/// <remarks>
/// The routines are written against <see cref="IBinaryInteger{TSelf}"/>, so a single implementation serves every
/// width from <see cref="byte"/> through <see cref="System.Int128"/>. Bit-twiddling operations favor branchless,
/// width-agnostic formulations (and hardware bit-manipulation instructions where available), so a closed generic
/// compiles down to a compact, value-independent instruction sequence.
/// </remarks>
public static class BinaryIntegerFunctions {
    /// <summary>Reinterprets a Boolean as a <typeparamref name="T"/> without branching, yielding <c>1</c> for <see langword="true"/> and <c>0</c> for <see langword="false"/>.</summary>
    /// <typeparam name="T">The binary integer type to produce.</typeparam>
    /// <param name="value">The Boolean to convert.</param>
    /// <returns><c>1</c> when <paramref name="value"/> is <see langword="true"/>; otherwise <c>0</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T As<T>(this bool value) where T : IBinaryInteger<T> =>
        T.CreateTruncating(value: Unsafe.As<bool, byte>(source: ref value));
    /// <summary>Returns <c>1</c> when <paramref name="value"/> is non-zero and <c>0</c> otherwise, without branching.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to test.</param>
    /// <returns><c>1</c> when <paramref name="value"/> differs from zero; otherwise <c>0</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T IsNonZero<T>(this T value) where T : IBinaryInteger<T> =>
        (T.Zero != value).As<T>();
    /// <summary>
    /// Builds the periodic bit mask whose set bits form blocks of <c>2^<paramref name="value"/></c> ones alternating
    /// with equally sized blocks of zeros (for example <c>0x5555…</c>, <c>0x3333…</c>, and <c>0x0F0F…</c> for inputs
    /// <c>0</c>, <c>1</c>, and <c>2</c>).
    /// </summary>
    /// <typeparam name="T">The binary integer type the mask is produced in.</typeparam>
    /// <param name="value">The block exponent; block width is <c>2^<paramref name="value"/></c> bits.</param>
    /// <returns>The repeating mask for the requested block width.</returns>
    /// <remarks>
    /// The mask is derived by dividing an all-ones word by the corresponding Fermat number (see
    /// <see cref="NthFermatNumber{T}(int)"/>); these are the constants that drive the SWAR bit-permutation routines
    /// such as <see cref="BitwisePair{TInput, TResult}(TInput, TInput)"/> and <see cref="ReverseBits{T}(T)"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T NthFermatMask<T>(this int value) where T : IBinaryInteger<T> {
        var x = T.AllBitsSet;
        var y = T.IsNegative(value: x).As<int>();

        return (((x >>> y) / value.NthFermatNumber<T>()) << y) | T.One;
    }
    /// <summary>Computes the <paramref name="value"/>-th Fermat number, <c>F(n) = 2^(2^n) + 1</c>.</summary>
    /// <typeparam name="T">The binary integer type the result is produced in.</typeparam>
    /// <param name="value">The Fermat number index <c>n</c>.</param>
    /// <returns>The Fermat number <c>2^(2^<paramref name="value"/>) + 1</c>, truncated to the width of <typeparamref name="T"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T NthFermatNumber<T>(this int value) where T : IBinaryInteger<T> =>
        ((T.One << (1 << value)) + T.One);
    /// <summary>Computes two raised to the power <paramref name="value"/> (that is, <c>1 &lt;&lt; <paramref name="value"/></c>).</summary>
    /// <typeparam name="T">The binary integer type the result is produced in.</typeparam>
    /// <param name="value">The exponent.</param>
    /// <returns>The value <c>2^<paramref name="value"/></c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T NthPowerOfTwo<T>(this int value) where T : IBinaryInteger<T> =>
        (T.One << value);
    /// <summary>Cyclically rotates the base-10 digits of <paramref name="value"/> by <paramref name="count"/> places, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are rotated.</param>
    /// <param name="count">The number of digit positions to rotate; positive rotates toward the most significant digit (left) and negative toward the least significant (right). The count is reduced modulo the digit count.</param>
    /// <returns><paramref name="value"/> with its decimal digits rotated, carrying the original sign.</returns>
    /// <remarks>
    /// <paramref name="count"/> is widened to <see cref="long"/> so that <see cref="RotateDigitsRight{T}(T, int)"/> can
    /// negate <see cref="int.MinValue"/> without it wrapping back to itself; the reduction modulo the digit count then
    /// always narrows back to a representable <typeparamref name="T"/>.
    /// </remarks>
    internal static T RotateDigits<T>(this T value, long count) where T : IBinaryInteger<T> {
        var digitCount = value.LogarithmBase10();

        count %= long.CreateTruncating(value: digitCount);

        var countAsT = T.CreateTruncating(value: count);

        if (0 > count) { countAsT += digitCount; }
        if (T.Zero == countAsT) { return value; }

        // Split and recombine on the SIGNED value: the rotation is linear, so the sign factors straight through the two
        // halves and out of the result — no CopySign, and no unrepresentable T.Abs(T.MinValue) up front.
        var factor = BinaryIntegerConstants<T>.Ten.Exponentiate(exponent: (digitCount - countAsT));
        var endDigits = (value / factor);
        var startDigits = (value - (endDigits * factor));

        return ((startDigits * BinaryIntegerConstants<T>.Ten.Exponentiate(exponent: countAsT)) + endDigits);
    }

    /// <summary>
    /// Interleaves the bits of two integers into a single value (a Morton, or Z-order, code), placing the bits of
    /// <paramref name="value"/> in the even-indexed positions and the bits of <paramref name="other"/> in the
    /// odd-indexed positions.
    /// </summary>
    /// <typeparam name="TInput">The binary integer type of the operands.</typeparam>
    /// <typeparam name="TResult">The binary integer type of the interleaved result; it must be wide enough to hold the combined bits of both operands.</typeparam>
    /// <param name="value">The operand whose bits occupy the even-indexed positions of the result.</param>
    /// <param name="other">The operand whose bits occupy the odd-indexed positions of the result.</param>
    /// <returns>The Morton code that interleaves the bits of <paramref name="value"/> and <paramref name="other"/>.</returns>
    /// <remarks>
    /// The hardware <c>PDEP</c> instruction is used when the BMI2 instruction set is available; otherwise a
    /// width-agnostic SWAR fallback performs the interleave. <see cref="BitwiseUnpair{TInput, TResult}(TInput)"/> is the
    /// inverse operation.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="TInput"/> or <typeparamref name="TResult"/> is <see cref="BigInteger"/>. Interleaving requires a fixed carrier width for both operand and result.</exception>
    public static TResult BitwisePair<TInput, TResult>(this TInput value, TInput other) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        BinaryIntegerConstants<TInput>.ThrowIfUnbounded(operationName: nameof(BitwisePair));
        BinaryIntegerConstants<TResult>.ThrowIfUnbounded(operationName: nameof(BitwisePair));

        switch (value) {
            case short:
            case ushort:
                if (Bmi2.IsSupported) {
                    return TResult.CreateTruncating(value: Bmi2.ParallelBitDeposit(
                        mask: 0.NthFermatMask<uint>(),
                        value: uint.CreateTruncating(value: value)
                    )) |
                        TResult.CreateTruncating(value: Bmi2.ParallelBitDeposit(
                        mask: (0.NthFermatMask<uint>() << 1),
                        value: uint.CreateTruncating(value: other)
                    ));
                }
                break;
            case int:
            case uint:
                if (Bmi2.X64.IsSupported) {
                    return TResult.CreateTruncating(value: Bmi2.X64.ParallelBitDeposit(
                        mask: 0.NthFermatMask<ulong>(),
                        value: ulong.CreateTruncating(value: value)
                    )) |
                        TResult.CreateTruncating(value: Bmi2.X64.ParallelBitDeposit(
                        mask: (0.NthFermatMask<ulong>() << 1),
                        value: ulong.CreateTruncating(value: other)
                    ));
                }
                break;
            default:
                break;
        }

        const int LoopOffset = 7;

        int offset;
        int shift;

        var resultBitCount = int.CreateChecked(value: BinaryIntegerConstants<TResult>.Size);
        var bitCountDividedByTwo = (resultBitCount >> 1);
        var inputBitCount = int.CreateChecked(value: BinaryIntegerConstants<TInput>.Size);
        var inputMask = TResult.AllBitsSet >>> (resultBitCount - inputBitCount);
        var evenBits = TResult.CreateTruncating(value: other) & inputMask;
        var oddBits = TResult.CreateTruncating(value: value) & inputMask;

        if (LoopOffset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            var i = ((int.CreateChecked(value: BinaryIntegerConstants<TResult>.Log2Size) - LoopOffset) - 1);

            do {
                offset = (i + (LoopOffset - 1));
                shift = offset.NthPowerOfTwo<int>();

                DistributeBits(
                    evenBits: ref evenBits,
                    oddBits: ref oddBits,
                    offset: offset,
                    shift: shift
                );
            } while (0 < --i);
        }

        offset = 6; if ((shift = offset.NthPowerOfTwo<int>()) < bitCountDividedByTwo) {
            DistributeBits(
                evenBits: ref evenBits,
                oddBits: ref oddBits,
                offset: offset,
                shift: shift
            );
        }
        offset = 5; if ((shift = offset.NthPowerOfTwo<int>()) < bitCountDividedByTwo) {
            DistributeBits(
                evenBits: ref evenBits,
                oddBits: ref oddBits,
                offset: offset,
                shift: shift
            );
        }
        offset = 4; if ((shift = offset.NthPowerOfTwo<int>()) < bitCountDividedByTwo) {
            DistributeBits(
                evenBits: ref evenBits,
                oddBits: ref oddBits,
                offset: offset,
                shift: shift
            );
        }
        offset = 3; if ((shift = offset.NthPowerOfTwo<int>()) < bitCountDividedByTwo) {
            DistributeBits(
                evenBits: ref evenBits,
                oddBits: ref oddBits,
                offset: offset,
                shift: shift
            );
        }
        offset = 2; if ((shift = offset.NthPowerOfTwo<int>()) < bitCountDividedByTwo) {
            DistributeBits(
                evenBits: ref evenBits,
                oddBits: ref oddBits,
                offset: offset,
                shift: shift
            );
        }
        offset = 1; if ((shift = offset.NthPowerOfTwo<int>()) < bitCountDividedByTwo) {
            DistributeBits(
                evenBits: ref evenBits,
                oddBits: ref oddBits,
                offset: offset,
                shift: shift
            );
        }
        offset = 0; if ((shift = offset.NthPowerOfTwo<int>()) < bitCountDividedByTwo) {
            DistributeBits(
                evenBits: ref evenBits,
                oddBits: ref oddBits,
                offset: offset,
                shift: shift
            );
        }

        return oddBits | (evenBits << shift);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void DistributeBits(int offset, int shift, ref TResult evenBits, ref TResult oddBits) {
            var mask = offset.NthFermatMask<TResult>();

            evenBits = (evenBits | (evenBits << shift)) & mask;
            oddBits = (oddBits | (oddBits << shift)) & mask;
        }
    }
    /// <summary>
    /// Separates the interleaved bits of a Morton (Z-order) code into its two components, returning the even-indexed
    /// bits and the odd-indexed bits as a pair.
    /// </summary>
    /// <typeparam name="TInput">The binary integer type of the interleaved input.</typeparam>
    /// <typeparam name="TResult">The binary integer type of each extracted component.</typeparam>
    /// <param name="value">The Morton code to de-interleave.</param>
    /// <returns>A pair whose first element is gathered from the even-indexed bits of <paramref name="value"/> and whose second element is gathered from the odd-indexed bits.</returns>
    /// <remarks>
    /// This is the inverse of <see cref="BitwisePair{TInput, TResult}(TInput, TInput)"/>. The hardware <c>PEXT</c>
    /// instruction is used when the BMI2 instruction set is available; otherwise a width-agnostic SWAR fallback
    /// performs the extraction.
    /// </remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="TInput"/> or <typeparamref name="TResult"/> is <see cref="BigInteger"/>. De-interleaving requires a fixed carrier width for both operand and result.</exception>
    public static (TResult, TResult) BitwiseUnpair<TInput, TResult>(this TInput value) where TInput : IBinaryInteger<TInput> where TResult : IBinaryInteger<TResult> {
        BinaryIntegerConstants<TInput>.ThrowIfUnbounded(operationName: nameof(BitwiseUnpair));
        BinaryIntegerConstants<TResult>.ThrowIfUnbounded(operationName: nameof(BitwiseUnpair));

        switch (value) {
            case int:
            case uint:
                if (Bmi2.IsSupported) {
                    return (
                        TResult.CreateTruncating(value: Bmi2.ParallelBitExtract(
                        mask: 0.NthFermatMask<uint>(),
                        value: uint.CreateTruncating(value: value)
                    )),
                        TResult.CreateTruncating(value: Bmi2.ParallelBitExtract(
                        mask: (0.NthFermatMask<uint>() << 1),
                        value: uint.CreateTruncating(value: value)
                    ))
                    );
                }
                break;
            case long:
            case ulong:
                if (Bmi2.X64.IsSupported) {
                    return (
                        TResult.CreateTruncating(value: Bmi2.X64.ParallelBitExtract(
                        mask: 0.NthFermatMask<ulong>(),
                        value: ulong.CreateTruncating(value: value)
                    )),
                        TResult.CreateTruncating(value: Bmi2.X64.ParallelBitExtract(
                        mask: (0.NthFermatMask<ulong>() << 1),
                        value: ulong.CreateTruncating(value: value)
                    ))
                    );
                }
                break;
            default:
                break;
        }

        return (UnpairCore(value: value), UnpairCore(value: (value >> 1)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void AggregateBits(int offset, int shift, ref TInput value) {
            value = (((value | (value >> shift)) & offset.NthFermatMask<TInput>()));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TResult UnpairCore(TInput value) {
            const int LoopOffset = 7;

            int offset;
            int shift;

            var bitCount = int.CreateChecked(value: BinaryIntegerConstants<TResult>.Size);

            value &= 0.NthFermatMask<TInput>();

            offset = 0; if ((shift = offset.NthPowerOfTwo<int>()) < bitCount) {
                AggregateBits(
                    offset: (offset + 1),
                    shift: shift,
                    value: ref value
                );
            }
            offset = 1; if ((shift = offset.NthPowerOfTwo<int>()) < bitCount) {
                AggregateBits(
                    offset: (offset + 1),
                    shift: shift,
                    value: ref value
                );
            }
            offset = 2; if ((shift = offset.NthPowerOfTwo<int>()) < bitCount) {
                AggregateBits(
                    offset: (offset + 1),
                    shift: shift,
                    value: ref value
                );
            }
            offset = 3; if ((shift = offset.NthPowerOfTwo<int>()) < bitCount) {
                AggregateBits(
                    offset: (offset + 1),
                    shift: shift,
                    value: ref value
                );
            }
            offset = 4; if ((shift = offset.NthPowerOfTwo<int>()) < bitCount) {
                AggregateBits(
                    offset: (offset + 1),
                    shift: shift,
                    value: ref value
                );
            }
            offset = 5; if ((shift = offset.NthPowerOfTwo<int>()) < bitCount) {
                AggregateBits(
                    offset: (offset + 1),
                    shift: shift,
                    value: ref value
                );
            }
            offset = 6; if ((shift = offset.NthPowerOfTwo<int>()) < bitCount) {
                AggregateBits(
                    offset: (offset + 1),
                    shift: shift,
                    value: ref value
                );
            }

            if (LoopOffset.NthPowerOfTwo<int>() < bitCount) {
                var i = (int.CreateChecked(value: BinaryIntegerConstants<TResult>.Log2Size) - LoopOffset);

                do {
                    shift = (++offset).NthPowerOfTwo<int>();

                    AggregateBits(
                        offset: (offset + 1),
                        shift: shift,
                        value: ref value
                    );
                } while (0 < --i);
            }

            return TResult.CreateTruncating(value: value);
        }
    }
    /// <summary>Returns <paramref name="value"/> with its lowest set bit cleared.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns><paramref name="value"/> with its least significant set bit turned off, or zero when <paramref name="value"/> is zero.</returns>
    public static T ClearLowestSetBit<T>(this T value) where T : IBinaryInteger<T> =>
        value & (value - T.One);
    /// <summary>Computes the digital root of <paramref name="value"/> — the single base-10 digit reached by repeatedly summing its decimal digits.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose digital root is computed; its sign is ignored.</param>
    /// <returns>Zero when <paramref name="value"/> is zero; otherwise a digit from <c>1</c> through <c>9</c>.</returns>
    /// <remarks>The result is obtained in constant time through the modulo-nine congruence rather than by iterating over the digits.</remarks>
    public static T DigitalRoot<T>(this T value) where T : IBinaryInteger<T> {
        if (T.Zero == value) { return T.Zero; }

        // The magnitude's residue modulo nine equals the magnitude of the SIGNED remainder, so this never materializes
        // |value| — which is unrepresentable at T.MinValue. A zero residue means the magnitude is a non-zero multiple
        // of nine, whose digital root is nine.
        var residue = T.Abs(value: (value % BinaryIntegerConstants<T>.Nine));

        return ((T.Zero == residue)
            ? BinaryIntegerConstants<T>.Nine
            : residue);
    }
    /// <summary>Enumerates the base-10 digits of <paramref name="value"/>, from least significant to most significant.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are enumerated; its sign is ignored.</param>
    /// <returns>A lazily evaluated sequence of the decimal digits of <paramref name="value"/>, in least-significant-first order; a single zero is yielded when <paramref name="value"/> is zero.</returns>
    public static IEnumerable<T> EnumerateDigits<T>(this T value) where T : IBinaryInteger<T> {
        // Divide the SIGNED value down and take each remainder's magnitude, so T.MinValue enumerates cleanly rather than
        // throwing on an unrepresentable T.Abs(value). A single-digit remainder's magnitude is always representable.
        var quotient = value;

        do {
            (quotient, var remainder) = T.DivRem(
                left: quotient,
                right: BinaryIntegerConstants<T>.Ten
            );

            yield return T.Abs(value: remainder);
        } while (T.Zero != quotient);
    }
    /// <summary>Raises <paramref name="value"/> to the power <paramref name="exponent"/> using exponentiation by squaring.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The base.</param>
    /// <param name="exponent">The exponent; must be non-negative, since a negative integer power is not representable.</param>
    /// <returns><paramref name="value"/> raised to the power <paramref name="exponent"/>. The result wraps on overflow rather than throwing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exponent"/> is negative.</exception>
    public static T Exponentiate<T>(this T value, T exponent) where T : IBinaryInteger<T> {
        // A negative exponent has no integer result; reject it rather than let the squaring loop fall through to a
        // meaningless value (unsigned T is never negative, so this guard costs a folded-away branch there).
        ArgumentOutOfRangeException.ThrowIfNegative(value: exponent);

        var result = T.One;

        while (true) {
            if (T.IsOddInteger(value: exponent)) {
                result *= value;
            }

            exponent >>= 1;

            if (T.Zero == exponent) {
                return result;
            }

            value *= value;
        }
    }
    /// <summary>Returns a value containing only the lowest set bit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns>A value in which only the least significant set bit of <paramref name="value"/> is set, or zero when <paramref name="value"/> is zero.</returns>
    public static T ExtractLowestSetBit<T>(this T value) where T : IBinaryInteger<T> =>
        value & (-value);
    /// <summary>Clears the contiguous run of set bits at and below the lowest clear (zero) bit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns><paramref name="value"/> with its trailing run of set bits turned off. This is the bitwise dual of <see cref="FillFromLowestSetBit{T}(T)"/>.</returns>
    public static T FillFromLowestClearBit<T>(this T value) where T : IBinaryInteger<T> =>
        value & (value + T.One);
    /// <summary>Sets every bit below the lowest set bit of <paramref name="value"/>, filling its trailing zeros with ones.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to operate on.</param>
    /// <returns><paramref name="value"/> with all bits below its least significant set bit turned on; an all-ones value when <paramref name="value"/> is zero.</returns>
    public static T FillFromLowestSetBit<T>(this T value) where T : IBinaryInteger<T> =>
        value | (value - T.One);
    /// <summary>Reduces <paramref name="value"/> modulo <paramref name="modulus"/> with a floored quotient, so the result carries the sign of <paramref name="modulus"/> rather than of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The dividend.</param>
    /// <param name="modulus">The divisor whose sign the result follows; for a positive <paramref name="modulus"/> the result is the non-negative remainder in <c>[0, <paramref name="modulus"/>)</c>, which is what makes this the right tool for wrapping a signed index or offset back into range.</param>
    /// <returns>The floored remainder — the unique value congruent to <paramref name="value"/> modulo <paramref name="modulus"/> that lies on the same side of zero as <paramref name="modulus"/>. This differs from the built-in <c>%</c> only when the operands carry opposing signs.</returns>
    /// <remarks>The correction is branchless: the built-in truncated remainder is nudged by a single conditional addition of <paramref name="modulus"/>, never the <c>((value % modulus) + modulus) % modulus</c> double reduction — which both spends a second division and can overflow its intermediate sum (for a large positive <paramref name="modulus"/> that double reduction is not merely slower but wrong). An unsigned <typeparamref name="T"/> is already non-negative, so the correction folds away to nothing there.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="modulus"/> is zero.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is the signed minimum and <paramref name="modulus"/> is <c>-1</c>; the underlying remainder rejects that one pair because its quotient is unrepresentable.</exception>
    public static T FloorModulo<T>(this T value, T modulus) where T : IBinaryInteger<T> {
        var remainder = (value % modulus);

        // The truncated remainder carries the sign of `value`; add `modulus` back exactly when the remainder is
        // non-zero and signed opposite the modulus, landing it on the divisor's side. The addend never overflows:
        // it only fires when the two have opposing signs, so their sum shrinks toward zero.
        var wrap = (remainder.IsNonZero() & T.IsNegative(value: (remainder ^ modulus)).As<T>());

        return (remainder + (modulus & (-wrap)));
    }
    /// <summary>Computes the greatest common divisor of <paramref name="value"/> and <paramref name="other"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The first operand; its magnitude is used.</param>
    /// <param name="other">The second operand; its magnitude is used.</param>
    /// <returns>The largest value that divides both operands. When one operand is zero, the magnitude of the other is returned.</returns>
    /// <exception cref="OverflowException">The result is the magnitude of the minimum signed value and is not representable by <typeparamref name="T"/>.</exception>
    /// <remarks>Implemented with the binary GCD (Stein's) algorithm, whose inner loop is branchless.</remarks>
    public static T GreatestCommonDivisor<T>(this T value, T other) where T : IBinaryInteger<T> {
        // The zero fast paths return the OTHER operand's magnitude, matching the general path below (which abs-es both):
        // returning it verbatim would leak a negative sign the contract promises to drop.
        if (T.Zero == other) { return T.Abs(value: value); } else if (T.Zero == value) { return T.Abs(value: other); }

        // A signed minimum has no positive magnitude in T. If exactly one operand is the minimum, its magnitude is a
        // power of two, so the GCD is simply the lowest set bit of the other operand's magnitude and is representable.
        // If both operands are the minimum, T.Abs below deliberately reports the unrepresentable result.
        var valueIsMinimum = (T.IsNegative(value: value) && (value == -value));
        var otherIsMinimum = (T.IsNegative(value: other) && (other == -other));

        if (valueIsMinimum != otherIsMinimum) {
            var magnitude = T.Abs(value: (valueIsMinimum
                ? other
                : value));

            return (T.One << int.CreateChecked(value: T.TrailingZeroCount(value: magnitude)));
        }

        other = T.Abs(value: other);
        value = T.Abs(value: value);

        var shift = int.CreateTruncating(value: T.TrailingZeroCount(value: other | value));

        other >>= int.CreateTruncating(value: T.TrailingZeroCount(value: other));
        value >>= int.CreateTruncating(value: T.TrailingZeroCount(value: value));

        if (other != value) {
            do {
                var swap = (other ^ value) & (-(value < other).As<T>());

                other ^= swap;
                value ^= swap;
                value -= other;
                value >>= int.CreateTruncating(value: T.TrailingZeroCount(value: value));
            } while (other != value);
        }

        return (other << shift);
    }
    /// <summary>Computes the least common multiple of <paramref name="value"/> and <paramref name="other"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <returns>The smallest value that is a multiple of both operands, or zero when either operand is zero. The result wraps on overflow.</returns>
    /// <remarks>The quotient is divided out before multiplying (<c>(value / gcd) * other</c>) to reduce the chance of overflow.</remarks>
    public static T LeastCommonMultiple<T>(this T value, T other) where T : IBinaryInteger<T> {
        var divisor = value.GreatestCommonDivisor(other: other);

        if (T.Zero == divisor) { return T.Zero; }

        var quotient = (value / divisor);
        var result = (quotient * other);

        // Remove only the MATHEMATICAL sign, based on the operands before multiplication. Looking at the wrapped
        // product's sign would mistake an overflowed positive result for a negative input and change its modular bits.
        return ((T.IsNegative(value: quotient) != T.IsNegative(value: other))
            ? -result
            : result);
    }
    /// <summary>Returns the one-based position of the lowest set bit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine.</param>
    /// <returns>The position of the least significant set bit, counting from <c>1</c>, or <c>0</c> when <paramref name="value"/> is zero.</returns>
    public static T LeastSignificantBit<T>(this T value) where T : IBinaryInteger<T> =>
        (value.IsNonZero() * (T.TrailingZeroCount(value: value) + T.One));
    /// <summary>Returns the least significant base-10 digit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine; its sign is ignored.</param>
    /// <returns>The ones digit of <paramref name="value"/>, a value from <c>0</c> through <c>9</c>.</returns>
    public static T LeastSignificantDigit<T>(this T value) where T : IBinaryInteger<T> =>
        // Abs the single-digit remainder, not the whole value, so T.MinValue yields its ones digit instead of throwing.
        T.Abs(value: (value % BinaryIntegerConstants<T>.Ten));
    /// <summary>Returns the number of base-10 digits required to represent the magnitude of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to measure; its sign is ignored.</param>
    /// <returns>The decimal digit count of <paramref name="value"/>. For a non-zero magnitude this equals <c>⌊log₁₀(|value|)⌋ + 1</c>; the magnitude zero yields <c>1</c>.</returns>
    public static T LogarithmBase10<T>(this T value) where T : IBinaryInteger<T> {
        // Count divisions of the SIGNED value toward zero — the digit count of the magnitude, without the unrepresentable
        // T.Abs(T.MinValue). Truncating division reaches zero from either sign, so the loop terminates all the same.
        var quotient = value;
        var result = T.Zero;

        do {
            quotient /= BinaryIntegerConstants<T>.Ten;
            ++result;
        } while (T.Zero != quotient);

        return result;
    }
    /// <summary>Returns the one-based position of the highest set bit of <paramref name="value"/>, equivalently its bit length.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine.</param>
    /// <returns>The position of the most significant set bit, counting from <c>1</c>, or <c>0</c> when <paramref name="value"/> is zero.</returns>
    /// <remarks>
    /// "Bit length" has a well-defined meaning even for an unbounded <typeparamref name="T"/> such as <see cref="BigInteger"/>,
    /// so this is computed from the value itself (<see cref="IBinaryInteger{TSelf}.GetShortestBitLength"/>) rather than
    /// from a fixed carrier width in that case. Every fixed-width instantiation keeps the branchless width-minus-leading-
    /// zeros fast path — see <see cref="BinaryIntegerConstants{T}.IsUnbounded"/> for why the guard costs nothing there.
    /// </remarks>
    public static T MostSignificantBit<T>(this T value) where T : IBinaryInteger<T> {
        if (typeof(T) == typeof(BigInteger)) {
            return T.CreateTruncating(value: value.GetShortestBitLength());
        }

        return (BinaryIntegerConstants<T>.Size - T.LeadingZeroCount(value: value));
    }
    /// <summary>Returns the most significant (leading) base-10 digit of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine; its sign is ignored.</param>
    /// <returns>The leading decimal digit of <paramref name="value"/>, a value from <c>0</c> through <c>9</c>.</returns>
    public static T MostSignificantDigit<T>(this T value) where T : IBinaryInteger<T> =>
        // Divide the signed value by the leading power of ten, then abs the single-digit result: |value / p| equals
        // |value| / p (p is positive), so this avoids the unrepresentable T.Abs(T.MinValue).
        T.Abs(value: (value / BinaryIntegerConstants<T>.Ten.Exponentiate(exponent: (value.LogarithmBase10() - T.One))));
    /// <summary>Returns the next integer greater than <paramref name="value"/> that has the same number of set bits, in lexicographic order.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The current bit permutation.</param>
    /// <returns>The smallest value larger than <paramref name="value"/> with an identical population count.</returns>
    /// <remarks>This is the classic next-bit-permutation (Gosper's hack); iterating it visits every value of a given population count in ascending order.</remarks>
    public static T PermuteBitsLexicographically<T>(this T value) where T : IBinaryInteger<T> {
        var x = value.FillFromLowestSetBit();
        var y = int.CreateTruncating(value: (T.TrailingZeroCount(value: value) + T.One));
        var z = (((~x).ExtractLowestSetBit() - T.One) >> y);

        return (x + T.One) | z;
    }
    /// <summary>Returns the parity of the population count of <paramref name="value"/>.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to examine.</param>
    /// <returns><c>1</c> when <paramref name="value"/> has an odd number of set bits; otherwise <c>0</c>.</returns>
    public static T PopulationParity<T>(this T value) where T : IBinaryInteger<T> =>
        T.PopCount(value: value) & T.One;
    /// <summary>Converts a reflected binary (Gray) code back to its standard binary representation.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The Gray-coded value to decode.</param>
    /// <returns>The standard binary value corresponding to the Gray code <paramref name="value"/>.</returns>
    /// <remarks>This is the inverse of <see cref="ReflectedBinaryEncode{T}(T)"/>.</remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>. Decoding is width-bounded — it XOR-folds against the carrier's own bit width.</exception>
    public static T ReflectedBinaryDecode<T>(this T value) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(ReflectedBinaryDecode));

        const int LoopOffset = 8;

        var bitCount = int.CreateChecked(value: BinaryIntegerConstants<T>.Size);

        if (0.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 0.NthPowerOfTwo<int>()); }
        if (1.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 1.NthPowerOfTwo<int>()); }
        if (2.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 2.NthPowerOfTwo<int>()); }
        if (3.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 3.NthPowerOfTwo<int>()); }
        if (4.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 4.NthPowerOfTwo<int>()); }
        if (5.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 5.NthPowerOfTwo<int>()); }
        if (6.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 6.NthPowerOfTwo<int>()); }
        if (7.NthPowerOfTwo<int>() < bitCount) { value ^= (value >>> 7.NthPowerOfTwo<int>()); }

        if (LoopOffset.NthPowerOfTwo<int>() < bitCount) {
            var i = (int.CreateChecked(value: BinaryIntegerConstants<T>.Log2Size) - LoopOffset);

            do {
                value ^= (value >>> (bitCount >> i));
            } while (0 < --i);
        }

        return value;
    }
    /// <summary>Converts a standard binary value to its reflected binary (Gray) code.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value to encode.</param>
    /// <returns>The Gray code of <paramref name="value"/>, in which successive integers differ by exactly one bit.</returns>
    /// <remarks><see cref="ReflectedBinaryDecode{T}(T)"/> recovers the original value.</remarks>
    public static T ReflectedBinaryEncode<T>(this T value) where T : IBinaryInteger<T> =>
        value ^ (value >>> 1);
    /// <summary>Returns <paramref name="value"/> with the order of all of its bits reversed.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose bits are reversed.</param>
    /// <returns>A value whose bit at position <c>i</c> equals the bit of <paramref name="value"/> at position <c>(width − 1 − i)</c>.</returns>
    /// <remarks>Implemented as a width-agnostic SWAR butterfly that swaps progressively larger bit groups.</remarks>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>. Bit reversal requires a fixed carrier width to define which bit is "first".</exception>
    public static T ReverseBits<T>(this T value) where T : IBinaryInteger<T> {
        BinaryIntegerConstants<T>.ThrowIfUnbounded(operationName: nameof(ReverseBits));

        const int LoopOffset = 7;

        int offset;

        var bitCountDividedByTwo = (int.CreateChecked(value: BinaryIntegerConstants<T>.Size) >> 1);

        offset = 0; if (offset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            SwapBitPairs(
                offset: offset,
                value: ref value
            );
        }
        offset = 1; if (offset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            SwapBitPairs(
                offset: offset,
                value: ref value
            );
        }
        offset = 2; if (offset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            SwapBitPairs(
                offset: offset,
                value: ref value
            );
        }
        offset = 3; if (offset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            SwapBitPairs(
                offset: offset,
                value: ref value
            );
        }
        offset = 4; if (offset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            SwapBitPairs(
                offset: offset,
                value: ref value
            );
        }
        offset = 5; if (offset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            SwapBitPairs(
                offset: offset,
                value: ref value
            );
        }
        offset = 6; if (offset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            SwapBitPairs(
                offset: offset,
                value: ref value
            );
        }

        if (LoopOffset.NthPowerOfTwo<int>() < bitCountDividedByTwo) {
            var i = ((int.CreateChecked(value: BinaryIntegerConstants<T>.Log2Size) - LoopOffset) - 1);

            do {
                SwapBitPairs(
                    offset: ++offset,
                    value: ref value
                );
            } while (0 < --i);
        }

        return (value >>> bitCountDividedByTwo) | (value << bitCountDividedByTwo);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SwapBitPairs(int offset, ref T value) {
            var mask = offset.NthFermatMask<T>();
            var shift = offset.NthPowerOfTwo<int>();

            value = ((value >>> shift) & mask) | ((value & mask) << shift);
        }
    }
    /// <summary>Returns <paramref name="value"/> with the order of its base-10 digits reversed, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are reversed.</param>
    /// <returns><paramref name="value"/> with its decimal digits reversed (for example <c>1230</c> becomes <c>321</c>), carrying the original sign. The result wraps on overflow.</returns>
    public static T ReverseDigits<T>(this T value) where T : IBinaryInteger<T> {
        // Reverse the SIGNED value directly: signed DivRem yields sign-carrying remainders, so the accumulated result
        // already bears the original sign (no CopySign, no unrepresentable T.Abs(T.MinValue)).
        var quotient = value;
        var result = T.Zero;

        do {
            (quotient, var remainder) = T.DivRem(
                left: quotient,
                right: BinaryIntegerConstants<T>.Ten
            );

            result = ((result * BinaryIntegerConstants<T>.Ten) + remainder);
        } while (T.Zero != quotient);

        return result;
    }
    /// <summary>Cyclically rotates the base-10 digits of <paramref name="value"/> toward the most significant end, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are rotated.</param>
    /// <param name="count">The number of digit positions to rotate left; the count is reduced modulo the digit count.</param>
    /// <returns><paramref name="value"/> with its decimal digits rotated left by <paramref name="count"/> places, carrying the original sign.</returns>
    public static T RotateDigitsLeft<T>(this T value, int count) where T : IBinaryInteger<T> =>
        value.RotateDigits(count: count);
    /// <summary>Cyclically rotates the base-10 digits of <paramref name="value"/> toward the least significant end, preserving the sign.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The value whose decimal digits are rotated.</param>
    /// <param name="count">The number of digit positions to rotate right; the count is reduced modulo the digit count.</param>
    /// <returns><paramref name="value"/> with its decimal digits rotated right by <paramref name="count"/> places, carrying the original sign.</returns>
    /// <remarks>
    /// <paramref name="count"/> is widened to <see cref="long"/> before negating: negating <see cref="int.MinValue"/> as
    /// an <see cref="int"/> wraps back to itself (its magnitude, <c>2147483648</c>, is not representable in <see cref="int"/>),
    /// which would silently rotate by the original, un-negated count instead of by its true magnitude in the opposite
    /// direction.
    /// </remarks>
    public static T RotateDigitsRight<T>(this T value, int count) where T : IBinaryInteger<T> =>
        value.RotateDigits(count: -(long)count);
}
