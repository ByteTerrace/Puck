using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// An unsigned binary fixed-point number in UQ48.16 format: a 64-bit magnitude where the high 48 bits are the integer
/// part and the low 16 bits are the fraction. The stored <see cref="Value"/> equals the represented real number scaled
/// by 2^16; the most-significant bit is an ordinary magnitude bit, never a sign bit.
/// </summary>
/// <param name="Value">The raw underlying storage — the represented real number scaled by <c>2¹⁶</c>.</param>
public readonly record struct UFixedQ4816(ulong Value)
    : IComparable,
      IComparable<UFixedQ4816>,
      IComparisonOperators<UFixedQ4816, UFixedQ4816, bool>,
      IAdditionOperators<UFixedQ4816, UFixedQ4816, UFixedQ4816>,
      ISubtractionOperators<UFixedQ4816, UFixedQ4816, UFixedQ4816>,
      IMultiplyOperators<UFixedQ4816, UFixedQ4816, UFixedQ4816>,
      IDivisionOperators<UFixedQ4816, UFixedQ4816, UFixedQ4816>,
      IModulusOperators<UFixedQ4816, UFixedQ4816, UFixedQ4816>,
      IIncrementOperators<UFixedQ4816>,
      IDecrementOperators<UFixedQ4816>,
      IBitwiseOperators<UFixedQ4816, UFixedQ4816, UFixedQ4816>,
      IShiftOperators<UFixedQ4816, int, UFixedQ4816>,
      IMinMaxValue<UFixedQ4816>,
      IAdditiveIdentity<UFixedQ4816, UFixedQ4816>,
      IMultiplicativeIdentity<UFixedQ4816, UFixedQ4816>,
      ISpanFormattable,
      ISpanParsable<UFixedQ4816> {
    /// <summary>The number of fractional bits in the UQ48.16 layout (<c>16</c>).</summary>
    public const int FractionBitCount = 16;
    /// <summary>The number of integer bits in the UQ48.16 layout (<c>48</c>).</summary>
    public const int IntegerBitCount = (TotalBitCount - FractionBitCount);
    /// <summary>The total number of bits in the underlying storage (<c>64</c>).</summary>
    public const int TotalBitCount = (8 * sizeof(ulong));

    private const ulong FractionBitMask = (RawOne - 1UL);
    private const ulong IntegerBitMask = ~FractionBitMask;
    // The widest canonical rendering is 15 integer digits + '.' + 16 fraction digits.
    private const int MaximumFormattedLength = (32 + 2);
    private const ulong MaxIntegerValue = (ulong.MaxValue >> FractionBitCount);
    private const ulong RawEpsilon = 1UL;
    private const ulong RawHalf = (1UL << (FractionBitCount - 1));
    private const ulong RawOne = (1UL << FractionBitCount);
    private const double RawOneInverse = (1d / RawOne);
    // The largest power-of-two-grid double strictly below 2^64; clamping here keeps (ulong) casts from wrapping.
    private const double ScaledMaximum = 18446744073709547520d;

    // The exact upper bound of the representable real range (ulong.MaxValue / 2^16), used to reject out-of-range text.
    private static readonly decimal MaxValueAsDecimal = (ulong.MaxValue / ((decimal)RawOne));

    /// <summary>Converts a <see cref="UFixedQ4816"/> to the nearest <see cref="double"/>.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The real value of <paramref name="value"/> as a <see cref="double"/>; precision may be lost for large magnitudes.</returns>
    public static explicit operator double(UFixedQ4816 value) =>
        (value.Value * RawOneInverse);
    /// <summary>Returns the bitwise complement of the raw storage of <paramref name="value"/>.</summary>
    /// <param name="value">The value to complement.</param>
    /// <returns>A value whose raw bits are the complement of those of <paramref name="value"/>.</returns>
    public static UFixedQ4816 operator ~(UFixedQ4816 value) =>
        new(Value: ~value.Value);
    /// <summary>Returns the two's-complement negation of <paramref name="value"/>, wrapping around the unsigned range.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The modular negation of <paramref name="value"/>; provided for completeness, since negating a non-zero value wraps.</returns>
    public static UFixedQ4816 operator -(UFixedQ4816 value) =>
        new(Value: unchecked((0UL - value.Value)));
    /// <summary>Returns <paramref name="value"/> increased by one, wrapping on overflow.</summary>
    /// <param name="value">The value to increment.</param>
    /// <returns><paramref name="value"/> plus <c>1.0</c>.</returns>
    public static UFixedQ4816 operator ++(UFixedQ4816 value) =>
        new(Value: unchecked((value.Value + RawOne)));
    /// <summary>Returns <paramref name="value"/> decreased by one, wrapping on underflow.</summary>
    /// <param name="value">The value to decrement.</param>
    /// <returns><paramref name="value"/> minus <c>1.0</c>.</returns>
    public static UFixedQ4816 operator --(UFixedQ4816 value) =>
        new(Value: unchecked((value.Value - RawOne)));
    /// <summary>Adds two values, wrapping on overflow.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>. Use <see cref="AddSaturating(UFixedQ4816, UFixedQ4816)"/> to clamp instead of wrap.</returns>
    public static UFixedQ4816 operator +(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: unchecked((x.Value + y.Value)));
    /// <summary>Subtracts <paramref name="y"/> from <paramref name="x"/>, wrapping on underflow.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>. Use <see cref="SubtractSaturating(UFixedQ4816, UFixedQ4816)"/> to clamp instead of wrap.</returns>
    public static UFixedQ4816 operator -(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: unchecked((x.Value - y.Value)));
    /// <summary>Multiplies two values in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    public static UFixedQ4816 operator *(UFixedQ4816 x, UFixedQ4816 y) {
        var truncated = MultiplyUnchecked(
            remainder: out var remainder,
            x: x,
            y: y
        ).Value;
        var equalToHalf = Convert.ToUInt64(value: (remainder == RawHalf));
        var greaterThanHalf = Convert.ToUInt64(value: (remainder > RawHalf));
        var correction = greaterThanHalf | (equalToHalf & truncated & 1UL);

        return new UFixedQ4816(Value: unchecked((truncated + correction)));
    }
    /// <summary>Divides <paramref name="x"/> by <paramref name="y"/> in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The rounded quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    public static UFixedQ4816 operator /(UFixedQ4816 x, UFixedQ4816 y) {
        var truncated = DivideUnchecked(
            remainder: out var remainder,
            x: x,
            y: y
        ).Value;
        var twiceRemainder = (((UInt128)remainder) << 1);
        var equalToValue = Convert.ToUInt64(value: (twiceRemainder == y.Value));
        var greaterThanValue = Convert.ToUInt64(value: (twiceRemainder > y.Value));
        var correction = greaterThanValue | (equalToValue & truncated & 1UL);

        return new UFixedQ4816(Value: unchecked((truncated + correction)));
    }
    /// <summary>Returns the remainder of dividing the raw storage of <paramref name="x"/> by that of <paramref name="y"/>.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The fixed-point remainder <c><paramref name="x"/> mod <paramref name="y"/></c>.</returns>
    public static UFixedQ4816 operator %(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: (x.Value % y.Value));
    /// <summary>Returns the bitwise AND of the raw storage of two values.</summary>
    /// <param name="x">The first operand.</param>
    /// <param name="y">The second operand.</param>
    /// <returns>The value whose raw bits are <c><paramref name="x"/> &amp; <paramref name="y"/></c>.</returns>
    public static UFixedQ4816 operator &(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: x.Value & y.Value);
    /// <summary>Returns the bitwise OR of the raw storage of two values.</summary>
    /// <param name="x">The first operand.</param>
    /// <param name="y">The second operand.</param>
    /// <returns>The value whose raw bits are <c><paramref name="x"/> | <paramref name="y"/></c>.</returns>
    public static UFixedQ4816 operator |(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: x.Value | y.Value);
    /// <summary>Returns the bitwise exclusive OR of the raw storage of two values.</summary>
    /// <param name="x">The first operand.</param>
    /// <param name="y">The second operand.</param>
    /// <returns>The value whose raw bits are <c><paramref name="x"/> ^ <paramref name="y"/></c>.</returns>
    public static UFixedQ4816 operator ^(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: x.Value ^ y.Value);
    /// <summary>Shifts the raw storage of <paramref name="value"/> left, multiplying it by <c>2^<paramref name="shiftAmount"/></c>.</summary>
    /// <param name="value">The value to shift.</param>
    /// <param name="shiftAmount">The number of bit positions to shift left.</param>
    /// <returns><paramref name="value"/> with its raw bits shifted left, wrapping on overflow.</returns>
    public static UFixedQ4816 operator <<(UFixedQ4816 value, int shiftAmount) =>
        new(Value: (value.Value << shiftAmount));
    /// <summary>Shifts the raw storage of <paramref name="value"/> right, dividing it by <c>2^<paramref name="shiftAmount"/></c> and truncating.</summary>
    /// <param name="value">The value to shift.</param>
    /// <param name="shiftAmount">The number of bit positions to shift right.</param>
    /// <returns><paramref name="value"/> with its raw bits shifted right. Because the storage is unsigned, this is equivalent to the unsigned shift <c>&gt;&gt;&gt;</c>.</returns>
    public static UFixedQ4816 operator >>(UFixedQ4816 value, int shiftAmount) =>
        new(Value: (value.Value >> shiftAmount));
    /// <summary>Shifts the raw storage of <paramref name="value"/> right without sign extension, dividing it by <c>2^<paramref name="shiftAmount"/></c> and truncating.</summary>
    /// <param name="value">The value to shift.</param>
    /// <param name="shiftAmount">The number of bit positions to shift right.</param>
    /// <returns><paramref name="value"/> with its raw bits shifted right.</returns>
    public static UFixedQ4816 operator >>>(UFixedQ4816 value, int shiftAmount) =>
        new(Value: (value.Value >>> shiftAmount));
    /// <summary>Indicates whether <paramref name="x"/> is less than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <(UFixedQ4816 x, UFixedQ4816 y) =>
        (x.Value < y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is less than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(UFixedQ4816 x, UFixedQ4816 y) =>
        (x.Value <= y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >(UFixedQ4816 x, UFixedQ4816 y) =>
        (x.Value > y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(UFixedQ4816 x, UFixedQ4816 y) =>
        (x.Value >= y.Value);

    /// <summary>Gets the additive identity of the type, zero.</summary>
    public static UFixedQ4816 AdditiveIdentity => default;
    /// <summary>Gets the smallest representable positive value, one unit in the last place (<c>2⁻¹⁶</c>).</summary>
    public static UFixedQ4816 Epsilon => new(Value: RawEpsilon);
    /// <summary>Gets the largest representable value.</summary>
    public static UFixedQ4816 MaxValue => new(Value: ulong.MaxValue);
    /// <summary>Gets the smallest representable value, zero.</summary>
    public static UFixedQ4816 MinValue => default;
    /// <summary>Gets the multiplicative identity of the type, one.</summary>
    public static UFixedQ4816 MultiplicativeIdentity => new(Value: RawOne);
    /// <summary>Gets the value one.</summary>
    public static UFixedQ4816 One => new(Value: RawOne);
    /// <summary>Gets the value zero.</summary>
    public static UFixedQ4816 Zero => default;

    /// <summary>Returns the absolute value of <paramref name="value"/>, which is <paramref name="value"/> itself because the type is unsigned.</summary>
    /// <param name="value">The value whose absolute value is returned.</param>
    /// <returns><paramref name="value"/> unchanged.</returns>
    /// <remarks>Provided so the type can stand in for patterns that expect a numeric <c>Abs</c> operation.</remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Abs(UFixedQ4816 value) =>
        value;
    /// <summary>Adds two values, saturating to <see cref="MaxValue"/> instead of wrapping on overflow.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>, clamped to <see cref="MaxValue"/> when it would overflow.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 AddSaturating(UFixedQ4816 x, UFixedQ4816 y) {
        var z = (x + y).Value;
        var s = unchecked((ulong)-(long)Convert.ToUInt64(value: (z < x.Value)));

        return new(Value: z | s);
    }
    /// <summary>Returns the smallest integral value greater than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round up.</param>
    /// <returns><paramref name="value"/> rounded toward positive infinity to a whole number. The result wraps when <paramref name="value"/> rounds past <see cref="MaxValue"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Ceiling(UFixedQ4816 value) =>
        new(Value: unchecked((value.Value + FractionBitMask) & IntegerBitMask));
    /// <summary>Restricts <paramref name="value"/> to the inclusive range <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>.</summary>
    /// <param name="value">The value to clamp.</param>
    /// <param name="minimum">The inclusive lower bound.</param>
    /// <param name="maximum">The inclusive upper bound.</param>
    /// <returns><paramref name="minimum"/> when <paramref name="value"/> is below it, <paramref name="maximum"/> when above it, otherwise <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Clamp(UFixedQ4816 value, UFixedQ4816 minimum, UFixedQ4816 maximum) =>
        new(Value: Math.Clamp(
            value: value.Value,
            min: minimum.Value,
            max: maximum.Value
        ));
    /// <summary>Divides <paramref name="x"/> by <paramref name="y"/> in fixed point without rounding, also returning the division remainder.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <param name="remainder">When this method returns, the remainder of the fixed-point division expressed in raw storage units.</param>
    /// <returns>The truncated quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    /// <remarks>
    /// The result is not rounded and the division is not range-checked. A hardware 128-by-64-bit division is used when
    /// the x64 instruction set is available; otherwise the computation falls back to <see cref="UInt128"/> arithmetic.
    /// The rounding <c>/</c> operator builds on the remainder reported here.
    /// </remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 DivideUnchecked(UFixedQ4816 x, UFixedQ4816 y, out ulong remainder) {
        if (X86Base.X64.IsSupported) {
            var high = Math.BigMul(
                a: x.Value,
                b: RawOne,
                low: out var low
            );

#pragma warning disable SYSLIB5004
            (var quotient, remainder) = X86Base.X64.DivRem(
                lower: low,
                upper: high,
                divisor: y.Value
            );
#pragma warning restore SYSLIB5004

            return new(Value: quotient);
        } else {
            var dividend = (((UInt128)x.Value) << FractionBitCount);
            var divisor = ((UInt128)y.Value);
            var quotient = (dividend / divisor);

            remainder = ((ulong)(dividend - (quotient * divisor)));

            return new(Value: ((ulong)quotient));
        }
    }
    /// <summary>Divides <paramref name="x"/> by <paramref name="y"/> in fixed point without rounding, discarding the remainder.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The truncated quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 DivideUnchecked(UFixedQ4816 x, UFixedQ4816 y) =>
        DivideUnchecked(
            remainder: out var _,
            x: x,
            y: y
        );
    /// <summary>Returns the largest integral value less than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round down.</param>
    /// <returns><paramref name="value"/> with its fractional bits cleared.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Floor(UFixedQ4816 value) =>
        new(Value: value.Value & IntegerBitMask);
    /// <summary>Returns the fractional part of <paramref name="value"/> — the portion below the radix point.</summary>
    /// <param name="value">The value whose fractional part is returned.</param>
    /// <returns>A value in <c>[0, 1)</c> equal to <c><paramref name="value"/> − Floor(<paramref name="value"/>)</c>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Fractional(UFixedQ4816 value) =>
        new(Value: value.Value & FractionBitMask);
    /// <summary>Converts a <see cref="double"/> to a <see cref="UFixedQ4816"/>, rounding to nearest with ties to even.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The nearest representable <see cref="UFixedQ4816"/>, clamped to the range <c>[0, <see cref="MaxValue"/>]</c>. Negative and not-a-number inputs clamp to zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 FromDouble(double value) {
        var scaled = double.Round(
            x: (value * RawOne),
            mode: MidpointRounding.ToEven
        );
        var clamped = double.Clamp(
            value: scaled,
            min: 0d,
            max: ScaledMaximum
        );

        return new(Value: unchecked((ulong)clamped));
    }
    /// <summary>Constructs a <see cref="UFixedQ4816"/> from a whole number.</summary>
    /// <param name="value">The integer to represent. Must not exceed the largest representable integer (<c>2⁴⁸ − 1</c>).</param>
    /// <returns>The fixed-point value equal to <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is larger than the integer range of the format.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 FromInteger(ulong value) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: value,
            other: MaxIntegerValue
        );

        return new(Value: (value << FractionBitCount));
    }
    /// <summary>Constructs a <see cref="UFixedQ4816"/> directly from a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value to wrap, interpreted as the real number <c><paramref name="value"/> / 2¹⁶</c>.</param>
    /// <returns>A <see cref="UFixedQ4816"/> whose <see cref="Value"/> equals <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 FromRawBits(ulong value) =>
        new(Value: value);
    /// <summary>Returns the greater of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is greater.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Max(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: Math.Max(
            val1: x.Value,
            val2: y.Value
        ));
    /// <summary>Returns the lesser of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is lesser.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Min(UFixedQ4816 x, UFixedQ4816 y) =>
        new(Value: Math.Min(
            val1: x.Value,
            val2: y.Value
        ));
    /// <summary>Multiplies two values in fixed point without rounding, also returning the discarded low-order bits as the remainder.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <param name="remainder">When this method returns, the fractional bits shifted off below the radix point, in raw storage units.</param>
    /// <returns>The truncated product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    /// <remarks>The product is computed at full 128-bit width; the result is truncated rather than rounded and is not range-checked. The rounding <c>*</c> operator builds on the remainder reported here.</remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 MultiplyUnchecked(UFixedQ4816 x, UFixedQ4816 y, out ulong remainder) {
        var high = Math.BigMul(
            a: x.Value,
            b: y.Value,
            low: out var low
        );

        remainder = low & FractionBitMask;

        return new UFixedQ4816(Value: (high << IntegerBitCount) | (low >> FractionBitCount));
    }
    /// <summary>Multiplies two values in fixed point without rounding, discarding the remainder.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The truncated product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 MultiplyUnchecked(UFixedQ4816 x, UFixedQ4816 y) =>
        MultiplyUnchecked(
            remainder: out var _,
            x: x,
            y: y
        );
    /// <summary>Rounds <paramref name="value"/> to the nearest integral value, with ties rounded to the nearest even integer.</summary>
    /// <param name="value">The value to round.</param>
    /// <returns><paramref name="value"/> rounded to a whole number using banker's rounding.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Round(UFixedQ4816 value) {
        var integerPart = value.Value & IntegerBitMask;
        var fraction = value.Value & FractionBitMask;
        var equalToHalf = Convert.ToUInt64(value: (fraction == RawHalf));
        var greaterThanHalf = Convert.ToUInt64(value: (fraction > RawHalf));
        var isOdd = (integerPart >> FractionBitCount) & 1UL;
        var correction = ((greaterThanHalf | (equalToHalf & isOdd)) * RawOne);

        return new(Value: unchecked((integerPart + correction)));
    }
    /// <summary>Subtracts <paramref name="y"/> from <paramref name="x"/>, saturating to <see cref="MinValue"/> (zero) instead of wrapping on underflow.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>, clamped to zero when <paramref name="y"/> exceeds <paramref name="x"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 SubtractSaturating(UFixedQ4816 x, UFixedQ4816 y) {
        var z = (x - y).Value;
        var s = unchecked((ulong)-(long)Convert.ToUInt64(value: (z <= x.Value)));

        return new(Value: z & s);
    }
    /// <summary>Returns the integral part of <paramref name="value"/>, discarding the fraction.</summary>
    /// <param name="value">The value to truncate.</param>
    /// <returns><paramref name="value"/> with its fractional bits cleared. Because the type is unsigned, this matches <see cref="Floor(UFixedQ4816)"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ4816 Truncate(UFixedQ4816 value) =>
        new(Value: value.Value & IntegerBitMask);
    /// <summary>Compares this instance with a boxed <see cref="UFixedQ4816"/> and indicates their relative order.</summary>
    /// <param name="obj">The object to compare with this instance, or <see langword="null"/>.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="obj"/>; a <see langword="null"/> <paramref name="obj"/> sorts first.</returns>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is neither <see langword="null"/> nor a <see cref="UFixedQ4816"/>.</exception>
    public int CompareTo(object? obj) {
        if (obj is null) { return 1; }
        if (obj is UFixedQ4816 other) { return CompareTo(other: other); }

        throw new ArgumentException(
            message: $"Object must be of type {nameof(UFixedQ4816)}.",
            paramName: nameof(obj)
        );
    }
    /// <summary>Compares this instance with another <see cref="UFixedQ4816"/> and indicates their relative order.</summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="other"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public int CompareTo(UFixedQ4816 other) =>
        Value.CompareTo(value: other.Value);
    /// <summary>Parses a character span into a <see cref="UFixedQ4816"/>.</summary>
    /// <param name="s">The span of characters to parse, an unsigned decimal number such as <c>"123.5"</c>.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <returns>The value represented by <paramref name="s"/>.</returns>
    /// <exception cref="FormatException"><paramref name="s"/> is not a valid, in-range <see cref="UFixedQ4816"/> literal.</exception>
    public static UFixedQ4816 Parse(ReadOnlySpan<char> s, IFormatProvider? provider) {
        if (!TryParseCore(
            s: s,
            provider: provider,
            result: out var result
        )) {
            throw new FormatException(message: $"The input span was not in a valid {nameof(UFixedQ4816)} format.");
        }

        return result;
    }
    /// <summary>Parses a string into a <see cref="UFixedQ4816"/>.</summary>
    /// <param name="s">The string to parse, an unsigned decimal number such as <c>"123.5"</c>.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <returns>The value represented by <paramref name="s"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> is not a valid, in-range <see cref="UFixedQ4816"/> literal.</exception>
    public static UFixedQ4816 Parse(string s, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(
            s: s.AsSpan(),
            provider: provider
        );
    }
    /// <summary>Tries to parse a character span into a <see cref="UFixedQ4816"/>.</summary>
    /// <param name="s">The span of characters to parse.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <param name="result">When this method returns, the parsed value on success or the default value on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> was parsed successfully; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out UFixedQ4816 result) =>
        TryParseCore(
            s: s,
            provider: provider,
            result: out result
        );
    /// <summary>Tries to parse a string into a <see cref="UFixedQ4816"/>.</summary>
    /// <param name="s">The string to parse, or <see langword="null"/>.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <param name="result">When this method returns, the parsed value on success or the default value on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> was parsed successfully; otherwise <see langword="false"/>. A <see langword="null"/> <paramref name="s"/> returns <see langword="false"/>.</returns>
    public static bool TryParse(string? s, IFormatProvider? provider, out UFixedQ4816 result) {
        if (s is null) {
            result = default;

            return false;
        }

        return TryParseCore(
            s: s.AsSpan(),
            provider: provider,
            result: out result
        );
    }

    // Parses an unsigned decimal in invariant style. decimal arithmetic is exact for all practical magnitudes; only at
    // the very top of the range (a 15-digit integer with a full 16-digit fraction) can the (parsed * 2^16) product
    // exceed decimal's significand and incur a one-ULP rounding before the final round-half-to-even quantization.
    private static bool TryParseCore(ReadOnlySpan<char> s, IFormatProvider? provider, out UFixedQ4816 result) {
        result = default;

        if (!decimal.TryParse(
            s: s,
            style: NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint,
            provider: (provider ?? CultureInfo.InvariantCulture),
            result: out var parsed
        )) {
            return false;
        }

        if (
            (0m > parsed) ||
            (parsed > MaxValueAsDecimal)
        ) {
            return false;
        }

        var scaled = decimal.Round(
            d: (parsed * RawOne),
            decimals: 0,
            mode: MidpointRounding.ToEven
        );

        result = new(Value: ((ulong)scaled));

        return true;
    }

    /// <summary>Tries to format this value into the provided character span.</summary>
    /// <param name="destination">The span to write the formatted characters into.</param>
    /// <param name="charsWritten">When this method returns, the number of characters written to <paramref name="destination"/>.</param>
    /// <param name="format">Ignored; the value is always rendered as its exact decimal expansion.</param>
    /// <param name="provider">Ignored; formatting always uses the invariant culture.</param>
    /// <returns><see langword="true"/> when <paramref name="destination"/> was large enough; otherwise <see langword="false"/>.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        TryFormatCore(
            destination: destination,
            charsWritten: out charsWritten
        );
    /// <summary>Returns the exact decimal string representation of this value.</summary>
    /// <returns>The exact, invariant-culture decimal expansion of this value (a <c>/2¹⁶</c> fraction always terminates within sixteen digits).</returns>
    public override string ToString() {
        Span<char> buffer = stackalloc char[MaximumFormattedLength];

        _ = TryFormatCore(
            destination: buffer,
            charsWritten: out var charsWritten
        );

        return new string(value: buffer[..charsWritten]);
    }
    /// <summary>Returns the exact decimal string representation of this value.</summary>
    /// <param name="format">Ignored; the value is always rendered as its exact decimal expansion.</param>
    /// <param name="formatProvider">Ignored; formatting always uses the invariant culture.</param>
    /// <returns>The exact, invariant-culture decimal expansion of this value.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        ToString();

    // Renders the exact decimal expansion (a /2^16 fraction always terminates within 16 digits) without routing
    // through double; returns false and writes nothing meaningful when destination is too small.
    private bool TryFormatCore(Span<char> destination, out int charsWritten) {
        var integerPart = (Value >> FractionBitCount);

        if (!integerPart.TryFormat(
            destination: destination,
            charsWritten: out charsWritten,
            format: default,
            provider: CultureInfo.InvariantCulture
        )) {
            charsWritten = 0;

            return false;
        }

        var fraction = Value & FractionBitMask;

        if (0UL == fraction) {
            return true;
        }

        if (destination.Length <= charsWritten) {
            charsWritten = 0;

            return false;
        }

        destination[charsWritten++] = '.';

        do {
            if (destination.Length <= charsWritten) {
                charsWritten = 0;

                return false;
            }

            fraction *= 10UL;
            destination[charsWritten++] = ((char)('0' + ((int)(fraction >> FractionBitCount))));
            fraction &= FractionBitMask;
        } while (0UL != fraction);

        return true;
    }
}
