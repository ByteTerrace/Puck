using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// A signed binary fixed-point number in Q32.32 format: a 64-bit two's-complement value whose high 32 bits are the
/// integer part (including the sign) and whose low 32 bits are the fraction. The stored <see cref="Value"/> equals
/// the represented real number scaled by 2^32. Range approximately ±2,147,483,648; resolution 2⁻³² ≈ 2.33×10⁻¹⁰.
/// </summary>
/// <remarks>
/// This splits its sixty-four bits evenly, between the two unequal leans its <see cref="long"/>-carrier siblings
/// take: <see cref="FixedQ4816"/> (Q48.16) spends forty-eight bits on range and sixteen on resolution, and
/// <see cref="FixedQ1648"/> (Q16.48) spends the opposite — sixteen on range and forty-eight on resolution. This
/// format is the balanced point between them: a whole-number range of roughly ±2.1 billion at a resolution of
/// <c>2⁻³²</c>, suited to a quantity that needs both meaningfully wide range AND finer-than-Q48.16 resolution at
/// once — neither lean alone is enough there. It is a distinct carrier from both — not a convention on
/// <see cref="long"/> — because a Q32.32 raw, a Q48.16 raw and a Q16.48 raw are all <see langword="long"/>, and
/// confusing any two of them is a silent scale error wherever more than one appears. Every operation is
/// deterministic: identical inputs produce identical bits on every machine.
/// </remarks>
/// <param name="Value">The raw underlying storage — the represented real number scaled by <c>2³²</c>.</param>
public readonly partial record struct FixedQ3232(long Value)
    : INumber<FixedQ3232>,
      ISignedNumber<FixedQ3232>,
      IMinMaxValue<FixedQ3232> {
    /// <summary>The number of fractional bits in the Q32.32 layout (<c>32</c>).</summary>
    public const int FractionBitCount = 32;
    /// <summary>The number of integer bits in the Q32.32 layout, including the sign bit (<c>32</c>).</summary>
    public const int IntegerBitCount = (TotalBitCount - FractionBitCount);
    /// <summary>The total number of bits in the underlying storage (<c>64</c>).</summary>
    public const int TotalBitCount = (8 * sizeof(long));

    // The shift and half-ULP between this type's thirty-two fraction bits and FixedQ4816's sixteen.
    private const int PeerNarrowShift = (FractionBitCount - FixedQ4816.FractionBitCount);
    private const ulong PeerNarrowBitMask = ((1UL << PeerNarrowShift) - 1UL);
    private const ulong PeerNarrowHalf = (1UL << (PeerNarrowShift - 1));
    private const ulong FractionBitMask = ((1UL << FractionBitCount) - 1UL);
    private const long IntegerBitMask = unchecked((long)~FractionBitMask);
    private const long MaxIntegerValue = (long.MaxValue >> FractionBitCount);
    private const long MinIntegerValue = (long.MinValue >> FractionBitCount);
    private const long RawEpsilon = 1L;
    private const ulong RawHalf = (1UL << (FractionBitCount - 1)); // the half-ULP threshold, in the fraction domain
    private const long RawOne = (1L << FractionBitCount);          // the raw representation of 1.0, in the value domain
    private const double RawOneInverse = (1d / RawOne);
    // The largest power-of-two-grid double strictly below 2^63 and the exactly-representable -2^63; clamping here
    // keeps (long) casts from wrapping. Carrier-width facts, identical to FixedQ4816's — independent of the
    // fraction/integer split.
    private const double ScaledMaximum = 9223372036854774784d;
    private const double ScaledMinimum = -9223372036854775808d;

    /// <summary>Converts a <see cref="FixedQ3232"/> to the nearest <see cref="double"/>.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The real value of <paramref name="value"/> as a <see cref="double"/>; precision may be lost for large magnitudes.</returns>
    public static explicit operator double(FixedQ3232 value) =>
        (value.Value * RawOneInverse);
    /// <summary>Returns the negation of <paramref name="value"/>, wrapping only at <see cref="MinValue"/>.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The arithmetic negation of <paramref name="value"/>.</returns>
    public static FixedQ3232 operator -(FixedQ3232 value) =>
        new(Value: unchecked(-value.Value));
    /// <summary>Returns the negation of <paramref name="value"/>, throwing when the result is not representable.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The arithmetic negation of <paramref name="value"/>.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/>.</exception>
    public static FixedQ3232 operator checked -(FixedQ3232 value) =>
        new(Value: checked(-value.Value));
    /// <summary>Returns <paramref name="value"/> increased by one, wrapping on overflow.</summary>
    /// <param name="value">The value to increment.</param>
    /// <returns><paramref name="value"/> plus <c>1.0</c>.</returns>
    public static FixedQ3232 operator ++(FixedQ3232 value) =>
        new(Value: unchecked((value.Value + RawOne)));
    /// <summary>Returns <paramref name="value"/> increased by one, throwing when the result is not representable.</summary>
    /// <param name="value">The value to increment.</param>
    /// <returns><paramref name="value"/> plus <c>1.0</c>.</returns>
    /// <exception cref="OverflowException">The result exceeds <see cref="MaxValue"/>.</exception>
    public static FixedQ3232 operator checked ++(FixedQ3232 value) =>
        new(Value: checked((value.Value + RawOne)));
    /// <summary>Returns <paramref name="value"/> decreased by one, wrapping on underflow.</summary>
    /// <param name="value">The value to decrement.</param>
    /// <returns><paramref name="value"/> minus <c>1.0</c>.</returns>
    public static FixedQ3232 operator --(FixedQ3232 value) =>
        new(Value: unchecked((value.Value - RawOne)));
    /// <summary>Returns <paramref name="value"/> decreased by one, throwing when the result is not representable.</summary>
    /// <param name="value">The value to decrement.</param>
    /// <returns><paramref name="value"/> minus <c>1.0</c>.</returns>
    /// <exception cref="OverflowException">The result is less than <see cref="MinValue"/>.</exception>
    public static FixedQ3232 operator checked --(FixedQ3232 value) =>
        new(Value: checked((value.Value - RawOne)));
    /// <summary>Adds two values, wrapping on overflow.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>.</returns>
    public static FixedQ3232 operator +(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: unchecked((x.Value + y.Value)));
    /// <summary>Adds two values, throwing when the result is not representable.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>.</returns>
    /// <exception cref="OverflowException">The sum is outside the representable range.</exception>
    public static FixedQ3232 operator checked +(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: checked((x.Value + y.Value)));
    /// <summary>Subtracts <paramref name="y"/> from <paramref name="x"/>, wrapping on underflow.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>.</returns>
    public static FixedQ3232 operator -(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: unchecked((x.Value - y.Value)));
    /// <summary>Subtracts two values, throwing when the result is not representable.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>.</returns>
    /// <exception cref="OverflowException">The difference is outside the representable range.</exception>
    public static FixedQ3232 operator checked -(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: checked((x.Value - y.Value)));
    /// <summary>Multiplies two values in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    /// <remarks>
    /// A same-scale Q32.32 product is a general arithmetic capability — scaling one small factor by another, for
    /// instance. A MIXED-scale product — one operand at this format's Q32.32 scale, the other at a different split
    /// sharing the sixty-four-bit carrier, such as Q48.16 or Q16.48 — belongs in <see cref="FusedArithmetic"/> as a
    /// fused, one-rounding kernel over the two scales, not here: rounding each operand onto a shared scale before
    /// multiplying costs a rounding the mixed-scale caller does not have to pay. This operator is the ordinary
    /// same-scale case, useful on its own terms and with the same stated overflow policy as every other operator in
    /// this format.
    /// </remarks>
    public static FixedQ3232 operator *(FixedQ3232 x, FixedQ3232 y) {
        // The raw product is X·Y·2^64; divide by 2^32 and round to nearest, ties to even. Rounding the magnitude
        // and re-applying the sign equals rounding the signed value (the integer neighbors share parity).
        var product = (((Int128)x.Value) * y.Value);
        var negative = (product < Int128.Zero);
        var magnitude = ((UInt128)(negative
            ? -product
            : product));
        var truncated = (magnitude >> FractionBitCount);
        var remainder = magnitude & FractionBitMask;
        var rounded = FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: ((UInt128)RawHalf),
            truncated: truncated
        );
        var result = unchecked((long)rounded);

        return new(Value: (negative
            ? unchecked(-result)
            : result));
    }
    /// <summary>Multiplies two values in fixed point, rounding to nearest with ties to even and throwing when the rounded result is not representable.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    /// <exception cref="OverflowException">The rounded product is outside the representable range.</exception>
    public static FixedQ3232 operator checked *(FixedQ3232 x, FixedQ3232 y) {
        var product = (((Int128)x.Value) * y.Value);
        var negative = (product < Int128.Zero);
        var magnitude = ((UInt128)(negative
            ? -product
            : product));
        var truncated = (magnitude >> FractionBitCount);
        var remainder = magnitude & FractionBitMask;
        var rounded = FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: ((UInt128)RawHalf),
            truncated: truncated
        );

        return new(Value: SignedFixedPointArithmetic.FromCheckedMagnitude(
            magnitude: rounded,
            negative: negative
        ));
    }
    /// <summary>Divides <paramref name="x"/> by <paramref name="y"/> in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The rounded quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    public static FixedQ3232 operator /(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: SignedFixedPointArithmetic.Divide(
            x: x.Value,
            y: y.Value,
            fractionBitCount: FractionBitCount,
            integerBitCount: IntegerBitCount
        ));
    /// <summary>Divides two values in fixed point, rounding to nearest with ties to even and throwing when the rounded result is not representable.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The rounded quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    /// <exception cref="OverflowException">The rounded quotient is outside the representable range.</exception>
    public static FixedQ3232 operator checked /(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: SignedFixedPointArithmetic.DivideChecked(
            x: x.Value,
            y: y.Value,
            fractionBitCount: FractionBitCount
        ));
    /// <summary>Returns the remainder of dividing the raw storage of <paramref name="x"/> by that of <paramref name="y"/>.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The fixed-point remainder <c><paramref name="x"/> mod <paramref name="y"/></c>, with the sign of <paramref name="x"/>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    public static FixedQ3232 operator %(FixedQ3232 x, FixedQ3232 y) {
        // Every integer is exactly divisible by ±1. Bypass the CLR's signed-division overflow trap for
        // long.MinValue % -1 while preserving the ordinary divide-by-zero exception for a zero divisor.
        if (
            (y.Value == 1L) ||
            (y.Value == -1L)
        ) {
            return Zero;
        }

        return new(Value: (x.Value % y.Value));
    }
    /// <summary>Indicates whether <paramref name="x"/> is less than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <(FixedQ3232 x, FixedQ3232 y) =>
        (x.Value < y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is less than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(FixedQ3232 x, FixedQ3232 y) =>
        (x.Value <= y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >(FixedQ3232 x, FixedQ3232 y) =>
        (x.Value > y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(FixedQ3232 x, FixedQ3232 y) =>
        (x.Value >= y.Value);

    /// <summary>Gets the additive identity of the type, zero.</summary>
    public static FixedQ3232 AdditiveIdentity => default;
    /// <summary>Gets the smallest representable positive value, one unit in the last place (<c>2⁻³²</c>).</summary>
    public static FixedQ3232 Epsilon => new(Value: RawEpsilon);
    /// <summary>Gets the largest representable value.</summary>
    public static FixedQ3232 MaxValue => new(Value: long.MaxValue);
    /// <summary>Gets the smallest (most negative) representable value.</summary>
    public static FixedQ3232 MinValue => new(Value: long.MinValue);
    /// <summary>Gets the multiplicative identity of the type, one.</summary>
    public static FixedQ3232 MultiplicativeIdentity => new(Value: RawOne);
    /// <summary>Gets the value negative one.</summary>
    public static FixedQ3232 NegativeOne => new(Value: -RawOne);
    /// <summary>Gets the value one.</summary>
    public static FixedQ3232 One => new(Value: RawOne);
    /// <summary>Gets the value zero.</summary>
    public static FixedQ3232 Zero => default;

    /// <summary>Returns the absolute value of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose absolute value is returned.</param>
    /// <returns>The non-negative magnitude of <paramref name="value"/>.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/>, whose magnitude is unrepresentable.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Abs(FixedQ3232 value) =>
        new(Value: Math.Abs(value: value.Value));
    /// <summary>Returns the smallest integral value greater than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round up.</param>
    /// <returns><paramref name="value"/> rounded toward positive infinity to a whole number.</returns>
    /// <exception cref="OverflowException">The ceiling exceeds <see cref="MaxValue"/>.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Ceiling(FixedQ3232 value) {
        var floor = value.Value & IntegerBitMask;

        return new(Value: (((value.Value & ((long)FractionBitMask)) != 0L)
            ? checked((floor + RawOne))
            : floor));
    }
    /// <summary>Restricts <paramref name="value"/> to the inclusive range <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>.</summary>
    /// <param name="value">The value to clamp.</param>
    /// <param name="minimum">The inclusive lower bound.</param>
    /// <param name="maximum">The inclusive upper bound.</param>
    /// <returns><paramref name="minimum"/> when <paramref name="value"/> is below it, <paramref name="maximum"/> when above it, otherwise <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="minimum"/> is greater than <paramref name="maximum"/>. The diagnosis is the platform's own <c>Math.Clamp</c> surfacing through the forward, so it names NO parameter.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Clamp(FixedQ3232 value, FixedQ3232 minimum, FixedQ3232 maximum) =>
        new(Value: Math.Clamp(
            value: value.Value,
            max: maximum.Value,
            min: minimum.Value
        ));
    /// <summary>Returns the magnitude of <paramref name="value"/> carrying the sign of <paramref name="sign"/>.</summary>
    /// <param name="value">The value whose magnitude is taken.</param>
    /// <param name="sign">The value whose sign is applied; a zero <paramref name="sign"/> counts as non-negative.</param>
    /// <returns><paramref name="value"/> made negative when <paramref name="sign"/> is negative and non-negative otherwise.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/> and <paramref name="sign"/> is non-negative, so the requested positive magnitude is unrepresentable.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 CopySign(FixedQ3232 value, FixedQ3232 sign) {
        if (
            (value.Value == long.MinValue) &&
            (sign.Value >= 0L)
        ) {
            throw new OverflowException(message: $"The positive magnitude of {nameof(FixedQ3232)}.{nameof(MinValue)} is not representable.");
        }

        var magnitudeSign = (value.Value >> 63);
        var magnitude = unchecked(((value.Value ^ magnitudeSign) - magnitudeSign));
        var targetSign = (sign.Value >> 63);

        return new(Value: unchecked(((magnitude ^ targetSign) - targetSign)));
    }
    /// <summary>Returns the largest integral value less than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round down.</param>
    /// <returns><paramref name="value"/> with its fractional bits cleared (rounded toward negative infinity).</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Floor(FixedQ3232 value) =>
        new(Value: value.Value & IntegerBitMask);
    /// <summary>Returns the fractional part of <paramref name="value"/> — the non-negative portion above its floor.</summary>
    /// <param name="value">The value whose fractional part is returned.</param>
    /// <returns>A value in <c>[0, 1)</c> equal to <c><paramref name="value"/> − Floor(<paramref name="value"/>)</c>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Fractional(FixedQ3232 value) =>
        new(Value: value.Value & ((long)FractionBitMask));
    /// <summary>Converts a <see cref="double"/> to a <see cref="FixedQ3232"/>, rounding to nearest with ties to even.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The nearest representable <see cref="FixedQ3232"/>, clamped to <c>[<see cref="MinValue"/>, <see cref="MaxValue"/>]</c>. Not-a-number clamps to zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 FromDouble(double value) {
        var scaled = double.Round(
            mode: MidpointRounding.ToEven,
            x: (value * RawOne)
        );

        if (double.IsNaN(d: scaled)) { return Zero; }
        if (scaled > ScaledMaximum) { return MaxValue; }
        if (scaled <= ScaledMinimum) { return MinValue; }

        return new(Value: unchecked((long)scaled));
    }
    /// <summary>Constructs a <see cref="FixedQ3232"/> from a whole number.</summary>
    /// <param name="value">The integer to represent. Its magnitude must fit the integer range of the format
    /// (<c>[-2147483648, 2147483647]</c>).</param>
    /// <returns>The fixed-point value equal to <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside the integer range of the format.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 FromInteger(long value) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: value,
            other: MaxIntegerValue
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: value,
            other: MinIntegerValue
        );

        return new(Value: (value << FractionBitCount));
    }
    /// <summary>Constructs a <see cref="FixedQ3232"/> directly from a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value to wrap, interpreted as the real number <c><paramref name="value"/> / 2³²</c>.</param>
    /// <returns>A <see cref="FixedQ3232"/> whose <see cref="Value"/> equals <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 FromRawBits(long value) =>
        new(Value: value);
    /// <summary>Widens a <see cref="FixedQ4816"/> (Q48.16) into this format, throwing when its integer part does not fit.</summary>
    /// <param name="value">The Q48.16 value to widen.</param>
    /// <returns>The same real number, exactly — going from sixteen fraction bits to thirty-two is always exact,
    /// since it only appends zero bits. The overflow risk is entirely in the INTEGER part: Q48.16's integer range
    /// (roughly ±2⁴⁷) vastly exceeds Q32.32's (±2,147,483,648), so a Q48.16 value with a large integer part does not
    /// fit here.</returns>
    /// <exception cref="OverflowException">The widened value is outside <c>[<see cref="MinValue"/>, <see cref="MaxValue"/>]</c>.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 FromFixedQ4816(FixedQ4816 value) {
        if (!TryFromFixedQ4816(
            result: out var result,
            value: value
        )) {
            throw new OverflowException(message: $"The {nameof(FixedQ4816)} value {value} is outside the representable {nameof(FixedQ3232)} range.");
        }

        return result;
    }
    /// <summary>Widens a <see cref="FixedQ4816"/> (Q48.16) into this format, reporting instead of throwing when its integer part does not fit.</summary>
    /// <param name="value">The Q48.16 value to widen.</param>
    /// <param name="result">The widened value, exact, when this call returns <see langword="true"/>; otherwise <see langword="default"/>.</param>
    /// <returns>Whether <paramref name="value"/>'s integer part fits Q32.32's <c>[-2147483648, 2147483647]</c> range.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static bool TryFromFixedQ4816(FixedQ4816 value, out FixedQ3232 result) {
        var widened = (((Int128)value.Value) << PeerNarrowShift);

        if (
            (widened < long.MinValue) ||
            (widened > long.MaxValue)
        ) {
            result = default;

            return false;
        }

        result = new(Value: ((long)widened));

        return true;
    }
    /// <summary>Narrows this value into a <see cref="FixedQ4816"/> (Q48.16), rounding to nearest with ties to even.</summary>
    /// <returns>The value on Q48.16's coarser sixteen-bit fraction grid, losing the low sixteen fraction bits.
    /// This direction never overflows: Q32.32's whole integer range (±2,147,483,648) fits comfortably inside
    /// Q48.16's (roughly ±2⁴⁷), so the single rounding is the only thing that can move the value at all.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public FixedQ4816 ToFixedQ4816() {
        var sign = (Value >> 63);
        var magnitude = unchecked((ulong)((Value ^ sign) - sign));
        var truncated = (magnitude >> PeerNarrowShift);
        var remainder = magnitude & PeerNarrowBitMask;
        var rounded = FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: PeerNarrowHalf,
            truncated: truncated
        );
        var result = unchecked((long)rounded);

        return FixedQ4816.FromRawBits(value: ((sign != 0L)
            ? unchecked(-result)
            : result));
    }
    /// <summary>Linearly interpolates from <paramref name="from"/> to <paramref name="to"/> by <paramref name="amount"/>.</summary>
    /// <param name="from">The value returned when <paramref name="amount"/> is zero.</param>
    /// <param name="to">The value returned when <paramref name="amount"/> is one.</param>
    /// <param name="amount">The interpolation fraction; values outside <c>[0, 1]</c> extrapolate.</param>
    /// <returns><c><paramref name="from"/> + (<paramref name="to"/> − <paramref name="from"/>)·<paramref name="amount"/></c>, formed as ONE exact wide
    /// intermediate — <c>from·2³² + (to·amount − from·amount)</c>, at raw scale 2⁶⁴ — and rounded to the Q32.32 grid exactly once, to nearest with
    /// ties to even. The standalone difference <c>to − from</c> is never computed (and therefore never wrapped or saturated) as its own
    /// <see cref="FixedQ3232"/> value, so this is exact whenever the true mathematical result is representable, even where <c>to − from</c> alone
    /// would not be. Exactly <paramref name="from"/> at zero and exactly <paramref name="to"/> at one. When the true result itself is not
    /// representable, the final sum wraps to the signed 64-bit carrier — the same policy every other unchecked operator on this type states; there is
    /// no checked or saturating sibling.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Lerp(FixedQ3232 from, FixedQ3232 to, FixedQ3232 amount) =>
        new(Value: SignedFixedPointArithmetic.Lerp(
            from: from.Value,
            to: to.Value,
            amount: amount.Value,
            fractionBitCount: FractionBitCount
        ));
    /// <summary>Returns the greater of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is greater.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Max(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: Math.Max(
            val1: x.Value,
            val2: y.Value
        ));
    /// <summary>Returns the lesser of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is lesser.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Min(FixedQ3232 x, FixedQ3232 y) =>
        new(Value: Math.Min(
            val1: x.Value,
            val2: y.Value
        ));
    /// <summary>Rounds <paramref name="value"/> to the nearest integral value, with ties rounded to the nearest even integer.</summary>
    /// <param name="value">The value to round.</param>
    /// <returns><paramref name="value"/> rounded to a whole number using banker's rounding.</returns>
    /// <exception cref="OverflowException">The rounded result exceeds <see cref="MaxValue"/>.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Round(FixedQ3232 value) {
        var integerPart = value.Value & IntegerBitMask;
        var fraction = ((ulong)value.Value) & FractionBitMask;
        var roundUp = ((fraction > RawHalf) || ((fraction == RawHalf) && (((integerPart >> FractionBitCount) & 1L) != 0L)));

        return new(Value: (roundUp
            ? checked((integerPart + RawOne))
            : integerPart));
    }

    /// <summary>Returns a value indicating whether a raw storage bit pattern denotes an exact integer — a multiple of <c>2³²</c>, at any magnitude the format holds.</summary>
    /// <param name="raw">The raw storage bit pattern to classify.</param>
    /// <returns><see langword="true"/> when <paramref name="raw"/> has no fractional bits set.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static bool IsExactInteger(long raw) =>
        (raw == ((raw >> FractionBitCount) << FractionBitCount));

    /// <summary>Returns an integer that indicates the sign of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose sign is returned.</param>
    /// <returns><c>-1</c>, <c>0</c>, or <c>1</c> according to whether <paramref name="value"/> is negative, zero, or positive — the sign of the raw storage, which the <c>2³²</c> scale preserves.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static int Sign(FixedQ3232 value) =>
        Math.Sign(value: value.Value);
    /// <summary>Returns the integral part of <paramref name="value"/>, discarding the fraction (rounding toward zero).</summary>
    /// <param name="value">The value to truncate.</param>
    /// <returns><paramref name="value"/> with its fractional part removed toward zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ3232 Truncate(FixedQ3232 value) {
        var floor = value.Value & IntegerBitMask;

        return new(Value: (((value.Value < 0L) && ((value.Value & ((long)FractionBitMask)) != 0L))
            ? unchecked((floor + RawOne))
            : floor));
    }
    /// <summary>Compares this instance with a boxed <see cref="FixedQ3232"/> and indicates their relative order.</summary>
    /// <param name="obj">The object to compare with this instance, or <see langword="null"/>.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="obj"/>; a <see langword="null"/> <paramref name="obj"/> sorts first.</returns>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is neither <see langword="null"/> nor a <see cref="FixedQ3232"/>.</exception>
    public int CompareTo(object? obj) {
        if (obj is null) { return 1; }
        if (obj is FixedQ3232 other) { return CompareTo(other: other); }

        throw new ArgumentException(
            message: $"Object must be of type {nameof(FixedQ3232)}.",
            paramName: nameof(obj)
        );
    }
    /// <summary>Compares this instance with another <see cref="FixedQ3232"/> and indicates their relative order.</summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="other"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public int CompareTo(FixedQ3232 other) =>
        Value.CompareTo(value: other.Value);
    /// <summary>Returns the exact decimal string representation of this value.</summary>
    /// <returns>The exact, invariant-culture decimal expansion of this value (a <c>/2³²</c> fraction always terminates within thirty-two digits).</returns>
    public override string ToString() =>
        FixedPointText.FormatSignedRaw(
            rawValue: Value,
            fractionBitCount: FractionBitCount
        );
}
