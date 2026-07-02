using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// A signed binary fixed-point number in Q48.16 format: a 64-bit two's-complement value where the high 48 bits
/// (a sign bit plus 47 integer bits) are the integer part and the low 16 bits are the fraction. The stored
/// <see cref="Value"/> equals the represented real number scaled by 2^16. It is the signed companion to
/// <see cref="UFixedQ4816"/>, built for cross-machine bit-identical simulation: every operation is integer-only
/// and deterministic — including <see cref="Sqrt"/> and <see cref="Atan2"/>, which use no hardware floating point
/// — so the same inputs yield the same bits on every machine.
/// </summary>
/// <param name="Value">The raw underlying storage — the represented real number scaled by <c>2¹⁶</c>.</param>
public readonly record struct FixedQ4816(long Value)
    : IComparable,
      IComparable<FixedQ4816>,
      IComparisonOperators<FixedQ4816, FixedQ4816, bool>,
      IAdditionOperators<FixedQ4816, FixedQ4816, FixedQ4816>,
      ISubtractionOperators<FixedQ4816, FixedQ4816, FixedQ4816>,
      IMultiplyOperators<FixedQ4816, FixedQ4816, FixedQ4816>,
      IDivisionOperators<FixedQ4816, FixedQ4816, FixedQ4816>,
      IModulusOperators<FixedQ4816, FixedQ4816, FixedQ4816>,
      IUnaryNegationOperators<FixedQ4816, FixedQ4816>,
      IIncrementOperators<FixedQ4816>,
      IDecrementOperators<FixedQ4816>,
      IMinMaxValue<FixedQ4816>,
      IAdditiveIdentity<FixedQ4816, FixedQ4816>,
      IMultiplicativeIdentity<FixedQ4816, FixedQ4816> {
    /// <summary>The number of fractional bits in the Q48.16 layout (<c>16</c>).</summary>
    public const int FractionBitCount = 16;
    /// <summary>The number of integer bits in the Q48.16 layout, including the sign bit (<c>48</c>).</summary>
    public const int IntegerBitCount = (TotalBitCount - FractionBitCount);
    /// <summary>The total number of bits in the underlying storage (<c>64</c>).</summary>
    public const int TotalBitCount = (8 * sizeof(long));

    private const ulong FractionBitMask = ((1UL << FractionBitCount) - 1UL);
    private const long IntegerBitMask = unchecked((long)~FractionBitMask);
    private const long MaxIntegerValue = (long.MaxValue >> FractionBitCount);
    private const long MinIntegerValue = (long.MinValue >> FractionBitCount);
    private const long RawEpsilon = 1L;
    private const ulong RawHalf = (1UL << (FractionBitCount - 1)); // the half-ULP threshold, in the fraction domain
    private const long RawOne = (1L << FractionBitCount);          // the raw representation of 1.0, in the value domain
    private const double RawOneInverse = (1d / RawOne);
    // The largest power-of-two-grid double strictly below 2^63 and the exactly-representable -2^63; clamping here
    // keeps (long) casts from wrapping.
    private const double ScaledMaximum = 9223372036854774784d;
    private const double ScaledMinimum = -9223372036854775808d;

    // CORDIC vectoring constants for Atan2: a half turn's quarter (π/2) and atan(2^-i) for i = 0..15, all in Q16
    // radians. Sixteen iterations resolve the angle to within ~2 raw units, well under the fraction's precision.
    private const long HalfPiRaw = 102944L;
    private const int CordicIterations = 16;
    private static readonly long[] CordicAtanTable = [51472, 30386, 16055, 8150, 4091, 2048, 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2,];

    /// <summary>Converts a <see cref="FixedQ4816"/> to the nearest <see cref="double"/>.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The real value of <paramref name="value"/> as a <see cref="double"/>; precision may be lost for large magnitudes.</returns>
    public static explicit operator double(FixedQ4816 value) =>
        (value.Value * RawOneInverse);
    /// <summary>Returns the negation of <paramref name="value"/>, wrapping only at <see cref="MinValue"/>.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The arithmetic negation of <paramref name="value"/>.</returns>
    public static FixedQ4816 operator -(FixedQ4816 value) =>
        new(Value: unchecked(-value.Value));
    /// <summary>Returns <paramref name="value"/> increased by one, wrapping on overflow.</summary>
    /// <param name="value">The value to increment.</param>
    /// <returns><paramref name="value"/> plus <c>1.0</c>.</returns>
    public static FixedQ4816 operator ++(FixedQ4816 value) =>
        new(Value: unchecked((value.Value + RawOne)));
    /// <summary>Returns <paramref name="value"/> decreased by one, wrapping on underflow.</summary>
    /// <param name="value">The value to decrement.</param>
    /// <returns><paramref name="value"/> minus <c>1.0</c>.</returns>
    public static FixedQ4816 operator --(FixedQ4816 value) =>
        new(Value: unchecked((value.Value - RawOne)));
    /// <summary>Adds two values, wrapping on overflow.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>.</returns>
    public static FixedQ4816 operator +(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: unchecked((x.Value + y.Value)));
    /// <summary>Subtracts <paramref name="y"/> from <paramref name="x"/>, wrapping on underflow.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>.</returns>
    public static FixedQ4816 operator -(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: unchecked((x.Value - y.Value)));
    /// <summary>Multiplies two values in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    public static FixedQ4816 operator *(FixedQ4816 x, FixedQ4816 y) {
        // The product is X·Y·2^32; the result we want is X·Y·2^16, i.e. the product divided by 2^16 and rounded.
        // Round the non-negative magnitude (ties to even) then re-apply the sign — equivalent to rounding the
        // signed value, since the integer neighbors share parity — so both signs round identically.
        var product = ((Int128)x.Value * y.Value);
        var negative = (product < Int128.Zero);
        var magnitude = (UInt128)(negative ? -product : product);
        var truncated = ((ulong)(magnitude >> FractionBitCount));
        var remainder = ((ulong)magnitude & FractionBitMask);

        if ((remainder > RawHalf) || ((remainder == RawHalf) && ((truncated & 1UL) != 0UL))) {
            ++truncated;
        }

        var result = ((long)truncated);

        return new(Value: (negative ? unchecked(-result) : result));
    }
    /// <summary>Divides <paramref name="x"/> by <paramref name="y"/> in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The rounded quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    public static FixedQ4816 operator /(FixedQ4816 x, FixedQ4816 y) {
        // result = round((x.Value << 16) / y.Value). Normalize both operands to non-negative magnitudes, round the
        // magnitude quotient to nearest with ties to even, then re-apply the combined sign (parity-symmetric).
        var dividend = (((Int128)x.Value) << FractionBitCount);
        var divisor = ((Int128)y.Value);
        var negative = ((dividend < Int128.Zero) ^ (divisor < Int128.Zero));
        var absDividend = ((UInt128)(dividend < Int128.Zero ? -dividend : dividend));
        var absDivisor = ((UInt128)(divisor < Int128.Zero ? -divisor : divisor));
        var quotient = (absDividend / absDivisor);
        var twiceRemainder = ((absDividend - (quotient * absDivisor)) << 1);

        if ((twiceRemainder > absDivisor) || ((twiceRemainder == absDivisor) && (((ulong)quotient & 1UL) != 0UL))) {
            ++quotient;
        }

        var result = ((long)quotient);

        return new(Value: (negative ? unchecked(-result) : result));
    }
    /// <summary>Returns the remainder of dividing the raw storage of <paramref name="x"/> by that of <paramref name="y"/>.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The fixed-point remainder <c><paramref name="x"/> mod <paramref name="y"/></c>, with the sign of <paramref name="x"/>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    public static FixedQ4816 operator %(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: (x.Value % y.Value));
    /// <summary>Indicates whether <paramref name="x"/> is less than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value < y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is less than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value <= y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value > y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value >= y.Value);

    /// <summary>Gets the additive identity of the type, zero.</summary>
    public static FixedQ4816 AdditiveIdentity => default;
    /// <summary>Gets the smallest representable positive value, one unit in the last place (<c>2⁻¹⁶</c>).</summary>
    public static FixedQ4816 Epsilon => new(Value: RawEpsilon);
    /// <summary>Gets the largest representable value.</summary>
    public static FixedQ4816 MaxValue => new(Value: long.MaxValue);
    /// <summary>Gets the smallest (most negative) representable value.</summary>
    public static FixedQ4816 MinValue => new(Value: long.MinValue);
    /// <summary>Gets the multiplicative identity of the type, one.</summary>
    public static FixedQ4816 MultiplicativeIdentity => new(Value: RawOne);
    /// <summary>Gets the value negative one.</summary>
    public static FixedQ4816 NegativeOne => new(Value: -RawOne);
    /// <summary>Gets the value one.</summary>
    public static FixedQ4816 One => new(Value: RawOne);
    /// <summary>Gets the value zero.</summary>
    public static FixedQ4816 Zero => default;

    /// <summary>Returns the absolute value of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose absolute value is returned.</param>
    /// <returns>The non-negative magnitude of <paramref name="value"/>.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/>, whose magnitude is unrepresentable.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Abs(FixedQ4816 value) =>
        new(Value: Math.Abs(value: value.Value));
    /// <summary>Returns the smallest integral value greater than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round up.</param>
    /// <returns><paramref name="value"/> rounded toward positive infinity to a whole number.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Ceiling(FixedQ4816 value) {
        var floor = (value.Value & IntegerBitMask);

        return new(Value: (((value.Value & (long)FractionBitMask) != 0L) ? unchecked(floor + RawOne) : floor));
    }
    /// <summary>Restricts <paramref name="value"/> to the inclusive range <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>.</summary>
    /// <param name="value">The value to clamp.</param>
    /// <param name="minimum">The inclusive lower bound.</param>
    /// <param name="maximum">The inclusive upper bound.</param>
    /// <returns><paramref name="minimum"/> when <paramref name="value"/> is below it, <paramref name="maximum"/> when above it, otherwise <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Clamp(FixedQ4816 value, FixedQ4816 minimum, FixedQ4816 maximum) =>
        new(Value: Math.Clamp(
            value: value.Value,
            max: maximum.Value,
            min: minimum.Value
        ));
    /// <summary>Returns the largest integral value less than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round down.</param>
    /// <returns><paramref name="value"/> with its fractional bits cleared (rounded toward negative infinity).</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Floor(FixedQ4816 value) =>
        new(Value: (value.Value & IntegerBitMask));
    /// <summary>Returns the fractional part of <paramref name="value"/> — the non-negative portion above its floor.</summary>
    /// <param name="value">The value whose fractional part is returned.</param>
    /// <returns>A value in <c>[0, 1)</c> equal to <c><paramref name="value"/> − Floor(<paramref name="value"/>)</c>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Fractional(FixedQ4816 value) =>
        new(Value: (value.Value & (long)FractionBitMask));
    /// <summary>Converts a <see cref="double"/> to a <see cref="FixedQ4816"/>, rounding to nearest with ties to even.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The nearest representable <see cref="FixedQ4816"/>, clamped to <c>[<see cref="MinValue"/>, <see cref="MaxValue"/>]</c>. Not-a-number clamps to zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 FromDouble(double value) {
        var scaled = double.Round(
            x: (value * RawOne),
            mode: MidpointRounding.ToEven
        );
        var clamped = double.Clamp(
            value: (double.IsNaN(d: scaled) ? 0d : scaled),
            max: ScaledMaximum,
            min: ScaledMinimum
        );

        return new(Value: unchecked((long)clamped));
    }
    /// <summary>Constructs a <see cref="FixedQ4816"/> from a whole number.</summary>
    /// <param name="value">The integer to represent. Its magnitude must fit the integer range of the format.</param>
    /// <returns>The fixed-point value equal to <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside the integer range of the format.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 FromInteger(long value) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: value, other: MaxIntegerValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: value, other: MinIntegerValue);

        return new(Value: (value << FractionBitCount));
    }
    /// <summary>Constructs a <see cref="FixedQ4816"/> directly from a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value to wrap, interpreted as the real number <c><paramref name="value"/> / 2¹⁶</c>.</param>
    /// <returns>A <see cref="FixedQ4816"/> whose <see cref="Value"/> equals <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 FromRawBits(long value) =>
        new(Value: value);
    /// <summary>Returns the greater of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is greater.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Max(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: Math.Max(val1: x.Value, val2: y.Value));
    /// <summary>Returns the lesser of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is lesser.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Min(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: Math.Min(val1: x.Value, val2: y.Value));
    /// <summary>Rounds <paramref name="value"/> to the nearest integral value, with ties rounded to the nearest even integer.</summary>
    /// <param name="value">The value to round.</param>
    /// <returns><paramref name="value"/> rounded to a whole number using banker's rounding.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Round(FixedQ4816 value) {
        // Floor + round the [0,1) fraction (ties to even); for two's-complement the low 16 bits are the fraction
        // above the floor for both signs, so a single path handles negatives correctly.
        var integerPart = (value.Value & IntegerBitMask);
        var fraction = ((ulong)value.Value & FractionBitMask);
        var roundUp = ((fraction > RawHalf) || ((fraction == RawHalf) && (((integerPart >> FractionBitCount) & 1L) != 0L)));

        return new(Value: (roundUp ? unchecked(integerPart + RawOne) : integerPart));
    }
    /// <summary>Returns the non-negative square root of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose square root is returned; non-positive inputs yield zero.</param>
    /// <returns>The floor of the square root of <paramref name="value"/>, in fixed point.</returns>
    /// <remarks>Integer-only (no hardware floating point): the result is <c>⌊√(raw · 2¹⁶)⌋</c> via a width-fixed integer square root, so it is bit-identical across machines.</remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Sqrt(FixedQ4816 value) {
        if (value.Value <= 0L) {
            return Zero;
        }

        // √(raw/2^16)·2^16 = √(raw·2^16); compute at 128-bit width so the shift can't overflow, then floor.
        var scaled = (((UInt128)(ulong)value.Value) << FractionBitCount);

        return new(Value: ((long)scaled.SquareRoot()));
    }
    /// <summary>Computes the angle, in radians, from the positive X axis to the point <c>(<paramref name="x"/>, <paramref name="y"/>)</c>.</summary>
    /// <param name="y">The ordinate (the Y component).</param>
    /// <param name="x">The abscissa (the X component).</param>
    /// <returns>The angle in <c>(−π, π]</c>, in fixed-point radians; zero when both arguments are zero.</returns>
    /// <remarks>A CORDIC vectoring iteration over integer shifts and adds — no hardware floating point — so it is bit-identical across machines.</remarks>
    public static FixedQ4816 Atan2(FixedQ4816 y, FixedQ4816 x) {
        var xi = x.Value;
        var yi = y.Value;

        if ((xi == 0L) && (yi == 0L)) {
            return Zero;
        }

        // Fold into the right half-plane (xi >= 0) so the vectoring iteration converges, tracking the quarter turn
        // added by the fold: a left-half point is rotated ∓90° and the angle restored by ±π/2.
        var z = 0L;

        if (xi < 0L) {
            if (yi >= 0L) {
                (xi, yi) = (yi, -xi);
                z = HalfPiRaw;
            } else {
                (xi, yi) = (-yi, xi);
                z = -HalfPiRaw;
            }
        }

        for (var i = 0; (i < CordicIterations); ++i) {
            var direction = ((yi >= 0L) ? 1L : -1L);
            var nextX = (xi + (direction * (yi >> i)));
            var nextY = (yi - (direction * (xi >> i)));

            z += (direction * CordicAtanTable[i]);
            xi = nextX;
            yi = nextY;
        }

        return new(Value: z);
    }
    /// <summary>Returns the integral part of <paramref name="value"/>, discarding the fraction (rounding toward zero).</summary>
    /// <param name="value">The value to truncate.</param>
    /// <returns><paramref name="value"/> with its fractional part removed toward zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Truncate(FixedQ4816 value) {
        var floor = (value.Value & IntegerBitMask);

        // Floor rounds toward −∞; for a negative value with a fraction, truncation toward zero is one step higher.
        return new(Value: (((value.Value < 0L) && ((value.Value & (long)FractionBitMask) != 0L)) ? unchecked(floor + RawOne) : floor));
    }
    /// <summary>Compares this instance with a boxed <see cref="FixedQ4816"/> and indicates their relative order.</summary>
    /// <param name="obj">The object to compare with this instance, or <see langword="null"/>.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="obj"/>; a <see langword="null"/> <paramref name="obj"/> sorts first.</returns>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is neither <see langword="null"/> nor a <see cref="FixedQ4816"/>.</exception>
    public int CompareTo(object? obj) {
        if (obj is null) { return 1; }
        if (obj is FixedQ4816 other) { return CompareTo(other: other); }

        throw new ArgumentException(
            message: $"Object must be of type {nameof(FixedQ4816)}.",
            paramName: nameof(obj)
        );
    }
    /// <summary>Compares this instance with another <see cref="FixedQ4816"/> and indicates their relative order.</summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="other"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public int CompareTo(FixedQ4816 other) =>
        Value.CompareTo(value: other.Value);
    /// <summary>Returns the exact decimal string representation of this value.</summary>
    /// <returns>The exact, invariant-culture decimal expansion of this value (a <c>/2¹⁶</c> fraction always terminates within sixteen digits).</returns>
    public override string ToString() {
        // A /2^16 fraction terminates in at most 16 digits; render the integer part (signed) then the exact fraction.
        var negative = (Value < 0L);
        var magnitude = (negative ? (ulong)(-(Int128)Value) : (ulong)Value);
        var integerPart = (magnitude >> FractionBitCount);
        var fraction = (magnitude & FractionBitMask);
        var builder = new System.Text.StringBuilder();

        if (negative && ((integerPart != 0UL) || (fraction != 0UL))) {
            _ = builder.Append(value: '-');
        }

        _ = builder.Append(value: integerPart.ToString(provider: CultureInfo.InvariantCulture));

        if (fraction != 0UL) {
            _ = builder.Append(value: '.');

            do {
                fraction *= 10UL;
                _ = builder.Append(value: ((char)('0' + ((int)(fraction >> FractionBitCount)))));
                fraction &= FractionBitMask;
            } while (fraction != 0UL);
        }

        return builder.ToString();
    }
}
