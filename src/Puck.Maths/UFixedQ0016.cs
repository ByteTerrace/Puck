using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// An unsigned binary fixed-point fraction in UQ0.16 format: all 16 bits of the backing <see cref="Value"/> are
/// fractional, so it represents a real number in [0, 1) as Value / 2^16. There is no integer part and therefore no
/// representable 1.0 (so no multiplicative identity and no increment by one). Multiplication is closed over [0, 1) and
/// cannot overflow; addition, division, and left shifts can leave the range and saturate (the explicit saturating
/// helpers) or wrap (the raw operators).
/// </summary>
/// <param name="Value">The raw underlying storage — the represented real number scaled by <c>2¹⁶</c>.</param>
public readonly record struct UFixedQ0016(ushort Value)
    : IComparable,
      IComparable<UFixedQ0016>,
      IComparisonOperators<UFixedQ0016, UFixedQ0016, bool>,
      IAdditionOperators<UFixedQ0016, UFixedQ0016, UFixedQ0016>,
      ISubtractionOperators<UFixedQ0016, UFixedQ0016, UFixedQ0016>,
      IMultiplyOperators<UFixedQ0016, UFixedQ0016, UFixedQ0016>,
      IDivisionOperators<UFixedQ0016, UFixedQ0016, UFixedQ0016>,
      IModulusOperators<UFixedQ0016, UFixedQ0016, UFixedQ0016>,
      IBitwiseOperators<UFixedQ0016, UFixedQ0016, UFixedQ0016>,
      IShiftOperators<UFixedQ0016, int, UFixedQ0016>,
      IMinMaxValue<UFixedQ0016>,
      IAdditiveIdentity<UFixedQ0016, UFixedQ0016>,
      ISpanFormattable,
      ISpanParsable<UFixedQ0016> {
    /// <summary>The number of fractional bits in the UQ0.16 layout (<c>16</c>); every bit is fractional.</summary>
    public const int FractionBitCount = 16;
    /// <summary>The total number of bits in the underlying storage (<c>16</c>).</summary>
    public const int TotalBitCount = (8 * sizeof(ushort));

    private const uint FractionBitMask = (RawOne - 1U);
    // The widest canonical rendering is '0' + '.' + 16 fraction digits.
    private const int MaximumFormattedLength = (2 + FractionBitCount);
    private const ushort RawEpsilon = 1;
    private const uint RawHalf = (1U << (FractionBitCount - 1));
    private const ushort RawMaxValue = ushort.MaxValue;
    // The scale (2^16), i.e. the raw value that *would* represent 1.0 — which is itself out of range.
    private const uint RawOne = (1U << FractionBitCount);
    private const double RawOneInverse = (1d / RawOne);

    private static readonly UInt128 ParsingDenominator = FixedPointText.CreateParsingDenominator(
        fractionBitCount: FractionBitCount
    );

    /// <summary>Converts a <see cref="UFixedQ0016"/> to its exact <see cref="double"/> value.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The real value of <paramref name="value"/>, a number in <c>[0, 1)</c>, as a <see cref="double"/>.</returns>
    public static explicit operator double(UFixedQ0016 value) =>
        (value.Value * RawOneInverse);
    /// <summary>Returns the bitwise complement of the raw storage of <paramref name="value"/>.</summary>
    /// <param name="value">The value to complement.</param>
    /// <returns>A value whose raw bits are the complement of those of <paramref name="value"/>.</returns>
    public static UFixedQ0016 operator ~(UFixedQ0016 value) =>
        new(Value: unchecked((ushort)~value.Value));
    /// <summary>Returns the two's-complement negation of <paramref name="value"/>, wrapping around the unsigned range.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The modular negation of <paramref name="value"/>; provided for completeness, since negating a non-zero value wraps.</returns>
    public static UFixedQ0016 operator -(UFixedQ0016 value) =>
        new(Value: unchecked((ushort)(0U - value.Value)));
    /// <summary>Adds two values, wrapping on overflow.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>. Use <see cref="AddSaturating(UFixedQ0016, UFixedQ0016)"/> to clamp instead of wrap.</returns>
    public static UFixedQ0016 operator +(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: unchecked((ushort)(x.Value + y.Value)));
    /// <summary>Subtracts <paramref name="y"/> from <paramref name="x"/>, wrapping on underflow.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>. Use <see cref="SubtractSaturating(UFixedQ0016, UFixedQ0016)"/> to clamp instead of wrap.</returns>
    public static UFixedQ0016 operator -(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: unchecked((ushort)(x.Value - y.Value)));
    /// <summary>Multiplies two values in fixed point, rounding to nearest with ties to even.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product <c><paramref name="x"/> × <paramref name="y"/></c>, which always remains within <c>[0, 1)</c>.</returns>
    public static UFixedQ0016 operator *(UFixedQ0016 x, UFixedQ0016 y) {
        var product = (((uint)x.Value) * y.Value);
        var truncated = (product >> FractionBitCount);
        var remainder = product & FractionBitMask;
        var equalToHalf = Convert.ToUInt32(value: (remainder == RawHalf));
        var greaterThanHalf = Convert.ToUInt32(value: (remainder > RawHalf));
        var correction = greaterThanHalf | (equalToHalf & truncated & 1U);

        return new(Value: ((ushort)(truncated + correction)));
    }
    /// <summary>Divides <paramref name="x"/> by <paramref name="y"/> in fixed point, rounding to nearest with ties to even and saturating to <see cref="MaxValue"/>.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The rounded quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>, clamped to <see cref="MaxValue"/> when the true quotient reaches or exceeds one.</returns>
    public static UFixedQ0016 operator /(UFixedQ0016 x, UFixedQ0016 y) {
        var dividend = (((uint)x.Value) << FractionBitCount);
        var quotient = (dividend / y.Value);
        var remainder = (dividend - (quotient * y.Value));
        var twiceRemainder = (remainder << 1);
        var equalToValue = Convert.ToUInt32(value: (twiceRemainder == y.Value));
        var greaterThanValue = Convert.ToUInt32(value: (twiceRemainder > y.Value));
        var correction = greaterThanValue | (equalToValue & quotient & 1U);

        return new(Value: ((ushort)Math.Min(
            val1: (quotient + correction),
            val2: ((uint)RawMaxValue)
        )));
    }
    /// <summary>Returns the remainder of dividing the raw storage of <paramref name="x"/> by that of <paramref name="y"/>.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The fixed-point remainder <c><paramref name="x"/> mod <paramref name="y"/></c>.</returns>
    public static UFixedQ0016 operator %(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: ((ushort)(x.Value % y.Value)));
    /// <summary>Returns the bitwise AND of the raw storage of two values.</summary>
    /// <param name="x">The first operand.</param>
    /// <param name="y">The second operand.</param>
    /// <returns>The value whose raw bits are <c><paramref name="x"/> &amp; <paramref name="y"/></c>.</returns>
    public static UFixedQ0016 operator &(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: ((ushort)(x.Value & y.Value)));
    /// <summary>Returns the bitwise OR of the raw storage of two values.</summary>
    /// <param name="x">The first operand.</param>
    /// <param name="y">The second operand.</param>
    /// <returns>The value whose raw bits are <c><paramref name="x"/> | <paramref name="y"/></c>.</returns>
    public static UFixedQ0016 operator |(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: ((ushort)(x.Value | y.Value)));
    /// <summary>Returns the bitwise exclusive OR of the raw storage of two values.</summary>
    /// <param name="x">The first operand.</param>
    /// <param name="y">The second operand.</param>
    /// <returns>The value whose raw bits are <c><paramref name="x"/> ^ <paramref name="y"/></c>.</returns>
    public static UFixedQ0016 operator ^(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: ((ushort)(x.Value ^ y.Value)));
    /// <summary>Shifts the raw storage of <paramref name="value"/> left, multiplying it by <c>2^<paramref name="shiftAmount"/></c>.</summary>
    /// <param name="value">The value to shift.</param>
    /// <param name="shiftAmount">The number of bit positions to shift left.</param>
    /// <returns><paramref name="value"/> with its raw bits shifted left, wrapping on overflow.</returns>
    public static UFixedQ0016 operator <<(UFixedQ0016 value, int shiftAmount) =>
        new(Value: unchecked((ushort)(value.Value << shiftAmount)));
    /// <summary>Shifts the raw storage of <paramref name="value"/> right, dividing it by <c>2^<paramref name="shiftAmount"/></c> and truncating.</summary>
    /// <param name="value">The value to shift.</param>
    /// <param name="shiftAmount">The number of bit positions to shift right.</param>
    /// <returns><paramref name="value"/> with its raw bits shifted right. Because the storage is unsigned, this is equivalent to the unsigned shift <c>&gt;&gt;&gt;</c>.</returns>
    public static UFixedQ0016 operator >>(UFixedQ0016 value, int shiftAmount) =>
        new(Value: ((ushort)(value.Value >> shiftAmount)));
    /// <summary>Shifts the raw storage of <paramref name="value"/> right without sign extension, dividing it by <c>2^<paramref name="shiftAmount"/></c> and truncating.</summary>
    /// <param name="value">The value to shift.</param>
    /// <param name="shiftAmount">The number of bit positions to shift right.</param>
    /// <returns><paramref name="value"/> with its raw bits shifted right.</returns>
    public static UFixedQ0016 operator >>>(UFixedQ0016 value, int shiftAmount) =>
        new(Value: ((ushort)(value.Value >>> shiftAmount)));
    /// <summary>Indicates whether <paramref name="x"/> is less than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <(UFixedQ0016 x, UFixedQ0016 y) =>
        (x.Value < y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is less than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(UFixedQ0016 x, UFixedQ0016 y) =>
        (x.Value <= y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >(UFixedQ0016 x, UFixedQ0016 y) =>
        (x.Value > y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(UFixedQ0016 x, UFixedQ0016 y) =>
        (x.Value >= y.Value);

    /// <summary>Gets the additive identity of the type, zero.</summary>
    public static UFixedQ0016 AdditiveIdentity => default;
    /// <summary>Gets the smallest representable positive value, one unit in the last place (<c>2⁻¹⁶</c>).</summary>
    public static UFixedQ0016 Epsilon => new(Value: RawEpsilon);
    /// <summary>Gets the largest representable value, the fraction just below one (<c>(2¹⁶ − 1) / 2¹⁶</c>).</summary>
    public static UFixedQ0016 MaxValue => new(Value: RawMaxValue);
    /// <summary>Gets the smallest representable value, zero.</summary>
    public static UFixedQ0016 MinValue => default;
    /// <summary>Gets the value zero.</summary>
    public static UFixedQ0016 Zero => default;

    /// <summary>Adds two values, saturating to <see cref="MaxValue"/> instead of wrapping on overflow.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>, clamped to <see cref="MaxValue"/> when it would overflow.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ0016 AddSaturating(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: ((ushort)Math.Min(
        val1: (((uint)x.Value) + y.Value),
        val2: ((uint)RawMaxValue)
    )));
    /// <summary>Restricts <paramref name="value"/> to the inclusive range <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>.</summary>
    /// <param name="value">The value to clamp.</param>
    /// <param name="minimum">The inclusive lower bound.</param>
    /// <param name="maximum">The inclusive upper bound.</param>
    /// <returns><paramref name="minimum"/> when <paramref name="value"/> is below it, <paramref name="maximum"/> when above it, otherwise <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ0016 Clamp(UFixedQ0016 value, UFixedQ0016 minimum, UFixedQ0016 maximum) =>
        new(Value: Math.Clamp(
        value: value.Value,
        min: minimum.Value,
        max: maximum.Value
    ));
    /// <summary>Converts a <see cref="double"/> to a <see cref="UFixedQ0016"/>, rounding to nearest with ties to even.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The nearest representable <see cref="UFixedQ0016"/>, clamped to <c>[0, <see cref="MaxValue"/>]</c>. Inputs at or above one, as well as negative and not-a-number inputs, clamp into range.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ0016 FromDouble(double value) {
        var scaled = double.Round(
            x: (value * RawOne),
            mode: MidpointRounding.ToEven
        );
        var clamped = double.Clamp(
            value: scaled,
            min: 0d,
            max: RawMaxValue
        );

        return new(Value: ((ushort)clamped));
    }
    /// <summary>Constructs a <see cref="UFixedQ0016"/> directly from a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value to wrap, interpreted as the real number <c><paramref name="value"/> / 2¹⁶</c>.</param>
    /// <returns>A <see cref="UFixedQ0016"/> whose <see cref="Value"/> equals <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ0016 FromRawBits(ushort value) =>
        new(Value: value);
    /// <summary>Returns the greater of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is greater.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ0016 Max(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: Math.Max(
        val1: x.Value,
        val2: y.Value
    ));
    /// <summary>Returns the lesser of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is lesser.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ0016 Min(UFixedQ0016 x, UFixedQ0016 y) =>
        new(Value: Math.Min(
        val1: x.Value,
        val2: y.Value
    ));
    /// <summary>Subtracts <paramref name="y"/> from <paramref name="x"/>, saturating to <see cref="MinValue"/> (zero) instead of wrapping on underflow.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>, clamped to zero when <paramref name="y"/> exceeds <paramref name="x"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UFixedQ0016 SubtractSaturating(UFixedQ0016 x, UFixedQ0016 y) {
        var z = (((uint)x.Value) - y.Value) & FractionBitMask;
        var keep = (0U - Convert.ToUInt32(value: (z <= x.Value)));

        return new(Value: ((ushort)(z & keep)));
    }
    /// <summary>Compares this instance with a boxed <see cref="UFixedQ0016"/> and indicates their relative order.</summary>
    /// <param name="obj">The object to compare with this instance, or <see langword="null"/>.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="obj"/>; a <see langword="null"/> <paramref name="obj"/> sorts first.</returns>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is neither <see langword="null"/> nor a <see cref="UFixedQ0016"/>.</exception>
    public int CompareTo(object? obj) {
        if (obj is null) { return 1; }
        if (obj is UFixedQ0016 other) { return CompareTo(other: other); }

        throw new ArgumentException(
            message: $"Object must be of type {nameof(UFixedQ0016)}.",
            paramName: nameof(obj)
        );
    }
    /// <summary>Compares this instance with another <see cref="UFixedQ0016"/> and indicates their relative order.</summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="other"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public int CompareTo(UFixedQ0016 other) =>
        Value.CompareTo(value: other.Value);
    /// <summary>Parses a character span into a <see cref="UFixedQ0016"/>.</summary>
    /// <param name="s">The span of characters to parse, an unsigned decimal fraction in <c>[0, 1)</c> such as <c>"0.5"</c>.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <returns>The value represented by <paramref name="s"/>.</returns>
    /// <exception cref="FormatException"><paramref name="s"/> is not a valid, in-range <see cref="UFixedQ0016"/> literal.</exception>
    public static UFixedQ0016 Parse(ReadOnlySpan<char> s, IFormatProvider? provider) {
        if (!TryParseCore(
            s: s,
            provider: provider,
            result: out var result
        )) {
            throw new FormatException(message: $"The input span was not in a valid {nameof(UFixedQ0016)} format.");
        }

        return result;
    }
    /// <summary>Parses a string into a <see cref="UFixedQ0016"/>.</summary>
    /// <param name="s">The string to parse, an unsigned decimal fraction in <c>[0, 1)</c> such as <c>"0.5"</c>.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <returns>The value represented by <paramref name="s"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> is not a valid, in-range <see cref="UFixedQ0016"/> literal.</exception>
    public static UFixedQ0016 Parse(string s, IFormatProvider? provider) {
        ArgumentNullException.ThrowIfNull(argument: s);

        return Parse(
            s: s.AsSpan(),
            provider: provider
        );
    }
    /// <summary>Tries to parse a character span into a <see cref="UFixedQ0016"/>.</summary>
    /// <param name="s">The span of characters to parse.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <param name="result">When this method returns, the parsed value on success or the default value on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> was parsed successfully; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out UFixedQ0016 result) =>
        TryParseCore(
        s: s,
        provider: provider,
        result: out result
    );
    /// <summary>Tries to parse a string into a <see cref="UFixedQ0016"/>.</summary>
    /// <param name="s">The string to parse, or <see langword="null"/>.</param>
    /// <param name="provider">The format provider that supplies the numeric conventions, or <see langword="null"/> to use the invariant culture.</param>
    /// <param name="result">When this method returns, the parsed value on success or the default value on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> was parsed successfully; otherwise <see langword="false"/>. A <see langword="null"/> <paramref name="s"/> returns <see langword="false"/>.</returns>
    public static bool TryParse(string? s, IFormatProvider? provider, out UFixedQ0016 result) {
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

    // Parses and quantizes directly from the decimal digits. Out-of-range text (including the unrepresentable 1.0) is
    // rejected outright, and arbitrarily long fractions cannot double-round through an intermediate decimal value.
    private static bool TryParseCore(ReadOnlySpan<char> s, IFormatProvider? provider, out UFixedQ0016 result) {
        result = default;

        if (FixedPointParseStatus.Success != FixedPointText.Parse(
            s: s,
            style: NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint,
            provider: provider,
            fractionBitCount: FractionBitCount,
            parsingDenominator: ParsingDenominator,
            maximumPositiveRaw: RawMaxValue,
            maximumNegativeMagnitudeRaw: 0UL,
            rejectExactOutOfRange: true,
            negative: out _,
            rawMagnitude: out var rawValue
        )) {
            return false;
        }

        result = new(Value: ((ushort)rawValue));

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
    // through double. The integer part is always '0'; returns false when destination is too small.
    private bool TryFormatCore(Span<char> destination, out int charsWritten) {
        charsWritten = 0;

        if (destination.IsEmpty) {
            return false;
        }

        destination[charsWritten++] = '0';

        var fraction = ((uint)Value);

        if (0U == fraction) {
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

            fraction *= 10U;
            destination[charsWritten++] = ((char)('0' + ((int)(fraction >> FractionBitCount))));
            fraction &= FractionBitMask;
        } while (0U != fraction);

        return true;
    }
}
