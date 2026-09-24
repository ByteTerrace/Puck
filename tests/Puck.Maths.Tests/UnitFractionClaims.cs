using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>The width-specific surface of a half-open unit fraction that no generic-math interface the carriers implement
/// names: the raw representation, the two construction routes, the saturating and selecting members, the double seam, and
/// the hand-derived ladders each width's statements run. The generic claims in
/// <see cref="UnitFractionClaims{TWidth, TFraction}"/> reach every other member through the carrier's own interfaces, so
/// each member a claim exercises is the carrier's one public implementation whichever width it runs at.</summary>
/// <typeparam name="TFraction">The unit-fraction carrier.</typeparam>
internal interface IUnitFractionWidth<TFraction> {
    /// <summary>Gets the carrier's declared fraction-bit count, read from its constant.</summary>
    static abstract int FractionBitCount { get; }
    /// <summary>Gets the fraction-bit count the width's definition demands, written out rather than read from the
    /// carrier.</summary>
    static abstract int ExpectedFractionBitCount { get; }
    /// <summary>Gets the carrier's declared total bit count, read from its constant.</summary>
    static abstract int TotalBitCount { get; }
    /// <summary>Gets the carrier's one-unit value.</summary>
    static abstract TFraction Epsilon { get; }
    /// <summary>Gets the carrier's zero.</summary>
    static abstract TFraction Zero { get; }
    /// <summary>Gets the construction ladder: zero and its neighbourhood, the seam below the width, the exact half either
    /// side, and the top either side.</summary>
    static abstract ulong[] Ladder { get; }
    /// <summary>Gets the double-seam ladder, each expectation derived from the definition rather than from the
    /// kernel.</summary>
    static abstract (double Value, ulong Expected)[] DoubleLadder { get; }
    /// <summary>Gets the quotient ladder, hand-derived from <c>(dividend·2^bits)/divisor</c>.</summary>
    static abstract (ulong Dividend, ulong Divisor, ulong Expected)[] QuotientLadder { get; }
    /// <summary>Gets the accepted spellings and the raws they name.</summary>
    static abstract (string Text, ulong Expected)[] ParseLadder { get; }
    /// <summary>Gets the refused spellings.</summary>
    static abstract string[] RefusedTexts { get; }
    /// <summary>Gets the raw one half names, written out rather than derived from the bit count.</summary>
    static abstract ulong HalfRaw { get; }

    /// <summary>Saturates the sum at the top.</summary>
    static abstract TFraction AddSaturating(TFraction x, TFraction y);
    /// <summary>Clamps into an inclusive range.</summary>
    static abstract TFraction Clamp(TFraction value, TFraction minimum, TFraction maximum);
    /// <summary>Constructs through the primary constructor, from a raw already on the width.</summary>
    static abstract TFraction Construct(ulong raw);
    /// <summary>Folds a sampled signed raw onto a legal raw of the width: its low bits, a total and unbiased map.</summary>
    static abstract ulong Fold(long sampled);
    /// <summary>Converts inward across the double seam.</summary>
    static abstract TFraction FromDouble(double value);
    /// <summary>Constructs through the raw factory, from a raw already on the width.</summary>
    static abstract TFraction FromRawBits(ulong raw);
    /// <summary>Selects the larger operand.</summary>
    static abstract TFraction Max(TFraction x, TFraction y);
    /// <summary>Selects the smaller operand.</summary>
    static abstract TFraction Min(TFraction x, TFraction y);
    /// <summary>Negates modulo one.</summary>
    static abstract TFraction Negate(TFraction value);
    /// <summary>Reads the raw back.</summary>
    static abstract ulong Raw(TFraction value);
    /// <summary>Saturates the difference at zero.</summary>
    static abstract TFraction SubtractSaturating(TFraction x, TFraction y);
    /// <summary>Converts outward across the double seam.</summary>
    static abstract double ToDouble(TFraction value);
}
/// <summary>The UQ0.16 width.</summary>
internal readonly struct UnitFraction16Width : IUnitFractionWidth<UnitFraction16> {
    public static int ExpectedFractionBitCount => 16;
    public static int FractionBitCount => UnitFraction16.FractionBitCount;
    public static int TotalBitCount => UnitFraction16.TotalBitCount;
    public static UnitFraction16 Epsilon => UnitFraction16.Epsilon;
    public static UnitFraction16 Zero => UnitFraction16.Zero;
    public static ulong HalfRaw => 32768UL;

    public static ulong[] Ladder { get; } = [
        0, 1, 2, 3,
        255, 256, 257,
        32767, 32768, 32769,
        65533, 65534, 65535,
    ];
    // The two saturations (which land on MaxValue, not on one, because one is unrepresentable), not-a-number, both
    // infinities, negative zero, the exactly-representable interior points, the three half-ULP ties whose ties-to-even
    // resolution is the house rounding discipline (0.5 down to 0, 1.5 up to 2, 2.5 down to 2), the tie AT the top that
    // rounds up and then saturates, and the one input that is not exactly representable: 0.1 scaled by 2^16 is exact in
    // double and its value 6553.6 rounds up.
    public static (double Value, ulong Expected)[] DoubleLadder { get; } = [
        (double.NaN, 0),
        (double.NegativeInfinity, 0),
        (double.PositiveInfinity, 65535),
        (-1d, 0),
        (-0d, 0),
        (0d, 0),
        (1d, 65535),
        (2d, 65535),
        (0.5d, 32768),
        (0.25d, 16384),
        ((1d - (1d / 65536d)), 65535),
        ((1d - (1d / 131072d)), 65535),
        ((1d / 65536d), 1),
        ((1d / 131072d), 0),
        ((3d / 131072d), 2),
        ((5d / 131072d), 2),
        (0.1d, 6554),
    ];
    // An exact power-of-two ratio, a ratio that rounds up, the equal-operand case whose true quotient is exactly one, and
    // two ratios above one — all four of the last kind saturate onto MaxValue rather than wrapping.
    public static (ulong Dividend, ulong Divisor, ulong Expected)[] QuotientLadder { get; } = [
        (0, 1, 0),
        (16384, 32768, 32768),
        (1, 3, 21845),
        (2, 3, 43691),
        (40000, 65535, 40001),
        (65535, 65535, 65535),
        (65535, 1, 65535),
        (32768, 16384, 65535),
    ];
    // The zero forms, the redundant and omitted digits either side of the point, surrounding whitespace, the
    // exactly-representable points, the three half-ULP ties (which resolve to even), the tie WITH a discarded nonzero
    // digit far beyond the seventeen the parser keeps (which therefore rounds up), the value that rounds up onto the top,
    // and the top itself.
    public static (string Text, ulong Expected)[] ParseLadder { get; } = [
        ("0", 0),
        ("0.0", 0),
        ("0.", 0),
        ("000", 0),
        (".5", 32768),
        ("00.5", 32768),
        (" 0.5 ", 32768),
        ("0.5", 32768),
        ("0.25", 16384),
        ("0.1", 6554),
        ("0.0000152587890625", 1),
        ("0.00000762939453125", 0),
        ("0.00002288818359375", 2),
        ("0.00003814697265625", 2),
        ("0.00000762939453125000000000000001", 1),
        ("0.99998474121093749", 65535),
        ("0.9999847412109375", 65535),
    ];
    // The empty and blank texts, a non-number, both signs (no AllowLeadingSign), one and above (unrepresentable), an
    // exponent (no AllowExponent), the group separator (no AllowThousands), a trailing non-digit, a value ABOVE the top
    // that would round back onto it (the parser rejects the exact value, never clamps it), and an integer too wide to be
    // a fraction at all.
    public static string[] RefusedTexts { get; } = [
        "", " ", "abc", "-0.5", "+0.5", "1", "1.0", "2", "1e-1", "0,5", "0.5x",
        "0.99998474121093751", "12345678901234567890123",
    ];

    public static UnitFraction16 AddSaturating(UnitFraction16 x, UnitFraction16 y) =>
        UnitFraction16.AddSaturating(
            x: x,
            y: y
        );
    public static UnitFraction16 Clamp(UnitFraction16 value, UnitFraction16 minimum, UnitFraction16 maximum) =>
        UnitFraction16.Clamp(
            maximum: maximum,
            minimum: minimum,
            value: value
        );
    public static UnitFraction16 Construct(ulong raw) =>
        new(Value: ((ushort)raw));
    public static ulong Fold(long sampled) =>
        unchecked((ushort)sampled);
    public static UnitFraction16 FromDouble(double value) =>
        UnitFraction16.FromDouble(value: value);
    public static UnitFraction16 FromRawBits(ulong raw) =>
        UnitFraction16.FromRawBits(value: ((ushort)raw));
    public static UnitFraction16 Max(UnitFraction16 x, UnitFraction16 y) =>
        UnitFraction16.Max(
            x: x,
            y: y
        );
    public static UnitFraction16 Min(UnitFraction16 x, UnitFraction16 y) =>
        UnitFraction16.Min(
            x: x,
            y: y
        );
    public static UnitFraction16 Negate(UnitFraction16 value) =>
        -value;
    public static ulong Raw(UnitFraction16 value) =>
        value.Value;
    public static UnitFraction16 SubtractSaturating(UnitFraction16 x, UnitFraction16 y) =>
        UnitFraction16.SubtractSaturating(
            x: x,
            y: y
        );
    public static double ToDouble(UnitFraction16 value) =>
        ((double)value);
}
/// <summary>The UQ0.32 width, its ladders re-derived at the finer grid.</summary>
internal readonly struct UnitFraction32Width : IUnitFractionWidth<UnitFraction32> {
    public static int ExpectedFractionBitCount => 32;
    public static int FractionBitCount => UnitFraction32.FractionBitCount;
    public static int TotalBitCount => UnitFraction32.TotalBitCount;
    public static UnitFraction32 Epsilon => UnitFraction32.Epsilon;
    public static UnitFraction32 Zero => UnitFraction32.Zero;
    public static ulong HalfRaw => 2147483648UL;

    // The SIXTEEN-bit seam here is where the coarse Q48.16 grid and the closed interval's narrowing split.
    public static ulong[] Ladder { get; } = [
        0U, 1U, 2U, 3U,
        65535U, 65536U, 65537U,
        2147483647U, 2147483648U, 2147483649U,
        4294967293U, 4294967294U, 4294967295U,
    ];
    // 0.1 scaled by 2^32 is 429496729.6 and rounds up.
    public static (double Value, ulong Expected)[] DoubleLadder { get; } = [
        (double.NaN, 0U),
        (double.NegativeInfinity, 0U),
        (double.PositiveInfinity, 4294967295U),
        (-1d, 0U),
        (-0d, 0U),
        (0d, 0U),
        (1d, 4294967295U),
        (2d, 4294967295U),
        (0.5d, 2147483648U),
        (0.25d, 1073741824U),
        ((1d - (1d / 4294967296d)), 4294967295U),
        ((1d - (1d / 8589934592d)), 4294967295U),
        ((1d / 4294967296d), 1U),
        ((1d / 8589934592d), 0U),
        ((3d / 8589934592d), 2U),
        ((5d / 8589934592d), 2U),
        (0.1d, 429496730U),
    ];
    public static (ulong Dividend, ulong Divisor, ulong Expected)[] QuotientLadder { get; } = [
        (0U, 1U, 0U),
        (1073741824U, 2147483648U, 2147483648U),
        (1U, 3U, 1431655765U),
        (2U, 3U, 2863311531U),
        (4294967295U, 4294967295U, 4294967295U),
        (4294967295U, 1U, 4294967295U),
        (2147483648U, 1073741824U, 4294967295U),
    ];
    // The exact ULP and its three half-ULP ties, the tie one unit of the thirty-third decimal place ABOVE the boundary,
    // the value that rounds up onto the top, and the top itself.
    public static (string Text, ulong Expected)[] ParseLadder { get; } = [
        ("0", 0U),
        ("0.0", 0U),
        ("0.", 0U),
        ("000", 0U),
        (".5", 2147483648U),
        ("00.5", 2147483648U),
        (" 0.5 ", 2147483648U),
        ("0.5", 2147483648U),
        ("0.25", 1073741824U),
        ("0.1", 429496730U),
        ("0.00000000023283064365386962890625", 1U),
        ("0.000000000116415321826934814453125", 0U),
        ("0.000000000349245965480804443359375", 2U),
        ("0.000000000582076609134674072265625", 2U),
        ("0.000000000116415321826934814453126", 1U),
        ("0.99999999976716935634613037109374", 4294967295U),
        ("0.99999999976716935634613037109375", 4294967295U),
    ];
    // The out-of-range vector re-derived: thirty-three decimal places, one unit above the top.
    public static string[] RefusedTexts { get; } = [
        "", " ", "abc", "-0.5", "+0.5", "1", "1.0", "2", "1e-1", "0,5", "0.5x",
        "0.999999999767169356346130371093751", "12345678901234567890123",
    ];

    public static UnitFraction32 AddSaturating(UnitFraction32 x, UnitFraction32 y) =>
        UnitFraction32.AddSaturating(
            x: x,
            y: y
        );
    public static UnitFraction32 Clamp(UnitFraction32 value, UnitFraction32 minimum, UnitFraction32 maximum) =>
        UnitFraction32.Clamp(
            maximum: maximum,
            minimum: minimum,
            value: value
        );
    public static UnitFraction32 Construct(ulong raw) =>
        new(Value: ((uint)raw));
    public static ulong Fold(long sampled) =>
        unchecked((uint)sampled);
    public static UnitFraction32 FromDouble(double value) =>
        UnitFraction32.FromDouble(value: value);
    public static UnitFraction32 FromRawBits(ulong raw) =>
        UnitFraction32.FromRawBits(value: ((uint)raw));
    public static UnitFraction32 Max(UnitFraction32 x, UnitFraction32 y) =>
        UnitFraction32.Max(
            x: x,
            y: y
        );
    public static UnitFraction32 Min(UnitFraction32 x, UnitFraction32 y) =>
        UnitFraction32.Min(
            x: x,
            y: y
        );
    public static UnitFraction32 Negate(UnitFraction32 value) =>
        -value;
    public static ulong Raw(UnitFraction32 value) =>
        value.Value;
    public static UnitFraction32 SubtractSaturating(UnitFraction32 x, UnitFraction32 y) =>
        UnitFraction32.SubtractSaturating(
            x: x,
            y: y
        );
    public static double ToDouble(UnitFraction32 value) =>
        ((double)value);
}
/// <summary>The unit-fraction subjects and claims, written once and instantiated per width: UQ0.16 through
/// <see cref="UnitFraction16Width"/> and UQ0.32 through <see cref="UnitFraction32Width"/>. Every expectation is either a
/// width's hand-derived ladder or shared-nothing <see cref="Oracles"/> arithmetic at the width's bit count; the only thing
/// the two widths share is the statement.</summary>
/// <typeparam name="TWidth">The width.</typeparam>
/// <typeparam name="TFraction">The width's carrier.</typeparam>
internal static class UnitFractionClaims<TWidth, TFraction>
    where TWidth : IUnitFractionWidth<TFraction>
    where TFraction : struct,
        IComparable,
        IComparable<TFraction>,
        IComparisonOperators<TFraction, TFraction, bool>,
        IAdditionOperators<TFraction, TFraction, TFraction>,
        ISubtractionOperators<TFraction, TFraction, TFraction>,
        IMultiplyOperators<TFraction, TFraction, TFraction>,
        IDivisionOperators<TFraction, TFraction, TFraction>,
        IModulusOperators<TFraction, TFraction, TFraction>,
        IBitwiseOperators<TFraction, TFraction, TFraction>,
        IShiftOperators<TFraction, int, TFraction>,
        IMinMaxValue<TFraction>,
        IAdditiveIdentity<TFraction, TFraction>,
        ISpanFormattable,
        ISpanParsable<TFraction> {
    // The string parse routes, reached through IParsable alone. Through the full constraint set a string argument binds
    // to ISpanParsable's span overload by implicit conversion — member lookup drops the base interface's string
    // overload — so the string members would go unexercised and a null string would parse as an empty span.
    private static T ParseText<T>(string s, IFormatProvider? provider)
        where T : IParsable<T> =>
        T.Parse(
            provider: provider,
            s: s
        );
    private static bool TryParseText<T>(string? s, IFormatProvider? provider, [MaybeNullWhen(returnValue: false)] out T result)
        where T : IParsable<T> =>
        T.TryParse(
            provider: provider,
            result: out result,
            s: s
        );
    private static TFraction Sampled(long raw) =>
        TWidth.FromRawBits(raw: TWidth.Fold(sampled: raw));
    /// <summary>Maps a sampled pair onto a dividend and a divisor: the SMALLER raw over the LARGER, with a zero divisor
    /// substituted by one unit. Both draws are uniform over the whole carrier, so an unordered fold would spend half the
    /// sweep at a quotient of one or more, where the answer saturates and the rounding rule never fires; ordering keeps the
    /// saturating corner (equal raws divide to exactly one and still saturate, and the edge square's diagonal reaches it
    /// on every run) and spends the rest of the sweep in the regime the correction lives in. A zero divisor has no quotient
    /// at all — it is the documented-by-code throw site pinned by the refusal ladder rather than a value law's business.
    /// Subject and oracle apply the identical map.</summary>
    private static (ulong Dividend, ulong Divisor) OrderedRatio(long a, long b) {
        var first = TWidth.Fold(sampled: a);
        var second = TWidth.Fold(sampled: b);
        var divisor = Math.Max(
            val1: first,
            val2: second
        );

        return (Math.Min(
            val1: first,
            val2: second
        ), ((0UL == divisor)
            ? 1UL
            : divisor));
    }
    /// <summary>The divisor fold WITHOUT the min/max ordering: the operands are taken as sampled and only a zero divisor is
    /// substituted, so the quotient is free to exceed one and the saturating clamp is reached on live operands rather
    /// than on a hand ladder alone. Subject and oracle apply the identical map.</summary>
    private static (ulong Dividend, ulong Divisor) UnorderedRatio(long a, long b) {
        var divisor = TWidth.Fold(sampled: b);

        return (TWidth.Fold(sampled: a), ((0UL == divisor)
            ? 1UL
            : divisor));
    }
    /// <summary>Folds a sampled raw onto a shift amount in <c>[−32, 63]</c>, so the sweep visits the negative amounts and
    /// the amounts at and beyond the thirty-two-bit shift-count mask as well as the ordinary ones.</summary>
    private static int ShiftAmount(long raw) =>
        (((int)(unchecked((ulong)raw) % 96UL)) - 32);

    /// <summary>The two's-complement wrap of an exact value onto the width's raw space, for the inline expectations the
    /// fraction claims compute.</summary>
    /// <param name="value">The exact value.</param>
    /// <returns>The value reduced into <c>[0, 2^bits)</c>.</returns>
    public static BigInteger Wrap(BigInteger value) {
        var scale = (BigInteger.One << TWidth.FractionBitCount);

        return (((value % scale) + scale) % scale);
    }

    /// <summary>Builds the two derived texts a text sweep parses beside the exact rendering: the rendering truncated after
    /// a sample-selected number of fraction digits, and the rendering with one more digit appended. Neither lands on the
    /// grid in general, so both exercise the parser's rounding rather than its round trip, and the appended form reaches
    /// the out-of-range refusal near the top of the interval.</summary>
    private static string[] DerivedTexts(string rendering, ulong raw) {
        var point = rendering.IndexOf(value: '.');
        var digit = ((char)('0' + ((int)(raw % 10UL))));

        if (0 > point) {
            return [$"0.{digit}", $"00{rendering}"];
        }

        var fractionLength = ((rendering.Length - point) - 1);
        var keep = ((point + 2) + ((int)(raw % ((ulong)fractionLength))));

        return [rendering[..keep], (rendering + digit)];
    }

    /// <summary>The subject multiply, sampled raw in and raw out.</summary>
    public static long Multiply(long a, long b) =>
        ((long)TWidth.Raw(value: (Sampled(raw: a) * Sampled(raw: b))));
    /// <summary>The oracle for the multiply — one ties-to-even rounding of the exact product at the width's grid.</summary>
    public static long MultiplyOracle(long a, long b) =>
        ((long)Oracles.UnitFractionProduct(
            fractionBitCount: TWidth.FractionBitCount,
            x: TWidth.Fold(sampled: a),
            y: TWidth.Fold(sampled: b)
        ));
    /// <summary>The subject divide, on the ordered non-zero-divisor fold.</summary>
    public static long Divide(long a, long b) =>
        SubjectQuotient(ratio: OrderedRatio(
            a: a,
            b: b
        ));
    /// <summary>The oracle for the divide — one ties-to-even rounding of the exact ratio, clamped at the top.</summary>
    public static long DivideOracle(long a, long b) =>
        OracleQuotient(ratio: OrderedRatio(
            a: a,
            b: b
        ));
    /// <summary>The subject divide on the unordered fold.</summary>
    public static long DivideUnordered(long a, long b) =>
        SubjectQuotient(ratio: UnorderedRatio(
            a: a,
            b: b
        ));
    /// <summary>The oracle for the unordered fold — one ties-to-even rounding of the exact ratio, then the clamp.</summary>
    public static long DivideUnorderedOracle(long a, long b) =>
        OracleQuotient(ratio: UnorderedRatio(
            a: a,
            b: b
        ));

    private static long OracleQuotient((ulong Dividend, ulong Divisor) ratio) =>
        ((long)Oracles.UnitFractionQuotient(
            fractionBitCount: TWidth.FractionBitCount,
            x: ratio.Dividend,
            y: ratio.Divisor
        ));
    private static long SubjectQuotient((ulong Dividend, ulong Divisor) ratio) =>
        ((long)TWidth.Raw(value: (TWidth.FromRawBits(raw: ratio.Dividend) / TWidth.FromRawBits(raw: ratio.Divisor))));

    /// <summary>Proves the projection to <see cref="double"/> is EXACT at every swept raw: the bit pattern the conversion
    /// produces is the one the IEEE-754 layout demands for <c>raw / 2^bits</c>, which is representable because the raw
    /// carries at most thirty-two significant bits. The comparison is on the encodings, so no floating-point arithmetic
    /// enters the law. The projection being exact, the double seam is a round trip in this direction at every raw — which
    /// separates FromDouble's saturation at the top of the interval from an error inside it.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? DoubleProjectionExact(long[] left, long[] right) {
        foreach (var sample in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var raw = TWidth.Fold(sampled: sample);
            var value = TWidth.FromRawBits(raw: raw);
            var projected = BitConverter.DoubleToUInt64Bits(value: TWidth.ToDouble(value: value));
            var expected = Oracles.ExactBinary64Bits(
                numerator: new BigInteger(value: raw),
                shift: TWidth.FractionBitCount
            );

            if (projected != expected) { return $"the projection of raw {raw} encoded as {projected:X16}, expected {expected:X16}"; }
            if (TWidth.FromDouble(value: TWidth.ToDouble(value: value)) != value) { return $"the double round trip failed at raw {raw}"; }
        }

        return null;
    }
    /// <summary>Proves every EXACT operation of the surface at every swept pair — the wrapping ring, the bitwise lattice,
    /// the raw remainder, the two saturating operations, the two order selections and the clamp — against arbitrary-width
    /// arithmetic, and that the order the comparisons report is the order of the raws. The complement's De Morgan pair
    /// against the two selections is pinned here too, at the carrier.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ExactOpsAndOrder(long[] left, long[] right) {
        var bits = TWidth.FractionBitCount;
        var rawA = TWidth.Fold(sampled: left[0]);
        var rawB = TWidth.Fold(sampled: right[0]);
        var rawC = TWidth.Fold(sampled: (left[0] >> bits));
        var a = TWidth.Construct(raw: rawA);
        var b = TWidth.FromRawBits(raw: rawB);
        var c = TWidth.FromRawBits(raw: rawC);
        var divisor = ((0UL == rawB)
            ? 1UL
            : rawB
        );
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var exactC = new BigInteger(value: rawC);
        var maximum = ((BigInteger.One << bits) - BigInteger.One);
        var zero = TWidth.Zero;
        var top = TFraction.MaxValue;
        var bottom = TFraction.MinValue;

        // The primary constructor and the raw factory are one fact, and the raw reads back unmoved.
        if (TWidth.Raw(value: a) != rawA) { return $"the constructor moved raw {rawA} to {TWidth.Raw(value: a)}"; }
        if (a != TWidth.FromRawBits(raw: rawA)) { return $"the raw factory disagrees with the constructor at {rawA}"; }

        // The wrapping ring.
        if (TWidth.Raw(value: (a + b)) != Wrap(value: (exactA + exactB))) { return $"the wrapping sum of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: (a - b)) != Wrap(value: (exactA - exactB))) { return $"the wrapping difference of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: TWidth.Negate(value: a)) != Wrap(value: -exactA)) { return $"the modular negation of {rawA} is wrong"; }
        if (TWidth.Raw(value: ~a) != (maximum - exactA)) { return $"the bitwise complement of {rawA} is wrong"; }

        // The bitwise lattice and the raw remainder, which IS the fixed-point remainder.
        if (TWidth.Raw(value: a & b) != (exactA & exactB)) { return $"the bitwise and of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: a | b) != (exactA | exactB)) { return $"the bitwise or of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: a ^ b) != (exactA ^ exactB)) { return $"the bitwise xor of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: (a % TWidth.FromRawBits(raw: divisor))) != (exactA % divisor)) { return $"the remainder of {rawA} by {divisor} is wrong"; }

        // The saturating pair, the order selections, and the clamp against the sampled pair as bounds.
        if (TWidth.Raw(value: TWidth.AddSaturating(
            x: a,
            y: b
        )) != BigInteger.Min(
            left: (exactA + exactB),
            right: maximum
        )) { return $"the saturating sum of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: TWidth.SubtractSaturating(
            x: a,
            y: b
        )) != BigInteger.Max(
            left: (exactA - exactB),
            right: BigInteger.Zero
        )) { return $"the saturating difference of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: TWidth.Max(
            x: a,
            y: b
        )) != BigInteger.Max(
            left: exactA,
            right: exactB
        )) { return $"the maximum of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: TWidth.Min(
            x: a,
            y: b
        )) != BigInteger.Min(
            left: exactA,
            right: exactB
        )) { return $"the minimum of {rawA} and {rawB} is wrong"; }
        if (TWidth.Raw(value: TWidth.Clamp(
            value: c,
            minimum: TWidth.Min(
                x: a,
                y: b
            ),
            maximum: TWidth.Max(
                x: a,
                y: b
            )
        ))
            != BigInteger.Min(
            left: BigInteger.Max(
                left: exactC,
                right: BigInteger.Min(
                    left: exactA,
                    right: exactB
                )
            ),
            right: BigInteger.Max(
                left: exactA,
                right: exactB
            )
        )) { return $"the clamp of {rawC} into [{rawA}, {rawB}] is wrong"; }

        // Identities, annihilators and involutions — the structure the constants have to have.
        if ((a + zero) != a) { return $"zero is not neutral for the wrapping sum at {rawA}"; }
        if ((a + TFraction.AdditiveIdentity) != a) { return $"the additive identity is not neutral at {rawA}"; }
        if ((a - a) != zero) { return $"a value is not its own additive inverse at {rawA}"; }
        if ((a ^ a) != zero) { return $"the exclusive or is not self-annihilating at {rawA}"; }
        if (~(~a) != a) { return $"the bitwise complement is not an involution at {rawA}"; }
        if (TWidth.Negate(value: TWidth.Negate(value: a)) != a) { return $"the modular negation is not an involution at {rawA}"; }
        if (TWidth.AddSaturating(
            x: a,
            y: zero
        ) != a) { return $"zero is not neutral for the saturating sum at {rawA}"; }
        if (TWidth.SubtractSaturating(
            x: a,
            y: zero
        ) != a) { return $"zero is not neutral for the saturating difference at {rawA}"; }
        if (TWidth.AddSaturating(
            x: a,
            y: top
        ) != top) { return $"the saturating sum does not stop at the top at {rawA}"; }
        if (TWidth.SubtractSaturating(
            x: bottom,
            y: a
        ) != bottom) { return $"the saturating difference does not stop at zero at {rawA}"; }
        if (TWidth.Raw(value: TWidth.AddSaturating(
            x: a,
            y: TWidth.Epsilon
        )) != BigInteger.Min(
            left: (exactA + BigInteger.One),
            right: maximum
        )) { return $"one unit in the last place is not one raw at {rawA}"; }
        if (TWidth.Max(
            x: a,
            y: bottom
        ) != a) { return $"the minimum value is not neutral for the maximum at {rawA}"; }
        if (TWidth.Min(
            x: a,
            y: top
        ) != a) { return $"the maximum value is not neutral for the minimum at {rawA}"; }

        // De Morgan between the bitwise complement and the two order selections, pinned at the carrier.
        if (~TWidth.Max(
            x: a,
            y: b
        ) != TWidth.Min(
            x: ~a,
            y: ~b
        )) { return $"De Morgan fails on the maximum at ({rawA}, {rawB})"; }
        if (~TWidth.Min(
            x: a,
            y: b
        ) != TWidth.Max(
            x: ~a,
            y: ~b
        )) { return $"De Morgan fails on the minimum at ({rawA}, {rawB})"; }

        // The order every comparison reports.
        var order = BigInteger.Compare(
            left: exactA,
            right: exactB
        );

        if (Math.Sign(value: a.CompareTo(other: b)) != order) { return $"the comparison of {rawA} and {rawB} reports the wrong order"; }
        if ((a < b) != (order < 0)) { return $"the less-than operator disagrees at ({rawA}, {rawB})"; }
        if ((a <= b) != (order <= 0)) { return $"the less-or-equal operator disagrees at ({rawA}, {rawB})"; }
        if ((a > b) != (order > 0)) { return $"the greater-than operator disagrees at ({rawA}, {rawB})"; }
        if ((a >= b) != (order >= 0)) { return $"the greater-or-equal operator disagrees at ({rawA}, {rawB})"; }

        return null;
    }
    /// <summary>Proves the three shift operators at every swept raw and amount, against the exact arbitrary-width
    /// expression, INCLUDING the amounts the C# shift-count mask reinterprets: the operand promotes to a thirty-two-bit
    /// word on both widths, so the amount acts modulo thirty-two and an amount of thirty-two is the identity rather than
    /// the wrap the documentation implies. The unsigned right shift is required to be the arithmetic one, which is what the
    /// unsigned storage makes it.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ShiftsMatchOracle(long[] left, long[] right) {
        var bits = TWidth.FractionBitCount;
        var raw = TWidth.Fold(sampled: left[0]);
        var value = TWidth.FromRawBits(raw: raw);
        var exact = new BigInteger(value: raw);
        var scale = (BigInteger.One << bits);
        Span<int> amounts = [ShiftAmount(raw: right[0]), 0, 1, (bits - 1), bits, (bits + 1), 31, 32, -1];

        foreach (var amount in amounts) {
            var masked = amount & 31;

            if (TWidth.Raw(value: (value << amount)) != ((exact << masked) % scale)) { return $"the left shift of raw {raw} by {amount} is wrong"; }
            if (TWidth.Raw(value: (value >> amount)) != (exact >> masked)) { return $"the right shift of raw {raw} by {amount} is wrong"; }
            if ((value >>> amount) != (value >> amount)) { return $"the unsigned right shift of raw {raw} by {amount} differs from the signed one"; }
        }

        return null;
    }
    /// <summary>Proves the construction contract on the width's own raw ladder: the declared grid is one fact, both
    /// construction routes preserve every ladder raw, the rendering is the exact decimal expansion (and ignores the format
    /// and the provider it is handed), the boxed comparison refuses a foreign type, the double seam saturates at both ends
    /// and rounds ties to even, and the three documented-by-code refusals — an inverted clamp range and a zero divisor at
    /// both division members — throw rather than answer.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ConstructionAndRefusals() {
        // Both declared counts are read into locals, so the width statements below are comparisons the run makes rather
        // than ones the compiler folds away (two compile-time constants would make the counterexample unreachable code).
        var bits = TWidth.FractionBitCount;
        var totalBits = TWidth.TotalBitCount;
        var zero = TWidth.Zero;
        var top = TFraction.MaxValue;

        // The declared grid: every bit is fractional, so the widths coincide and the top is one unit below one.
        if (bits != TWidth.ExpectedFractionBitCount) { return $"the fraction-bit count is {bits}"; }
        if (totalBits != bits) { return $"the total bit count is {totalBits}"; }
        if (TWidth.Raw(value: zero) != 0UL) { return $"zero has raw {TWidth.Raw(value: zero)}"; }
        if (TFraction.MinValue != zero) { return "the minimum value is not zero"; }
        if (TFraction.AdditiveIdentity != zero) { return "the additive identity is not zero"; }
        if (default(TFraction) != zero) { return "the default value is not zero"; }
        if (TWidth.Raw(value: TWidth.Epsilon) != 1UL) { return $"the epsilon has raw {TWidth.Raw(value: TWidth.Epsilon)}"; }
        if (TWidth.Raw(value: top) != ((1UL << bits) - 1UL)) { return $"the maximum value has raw {TWidth.Raw(value: top)}"; }

        // The half-open contract itself: one is unrepresentable, so the top plus a unit WRAPS to zero and the saturating
        // sum stops one unit below where the closed interval's would.
        if ((top + TWidth.Epsilon) != zero) { return "the top plus one unit does not wrap to zero"; }
        if (TWidth.AddSaturating(
            x: top,
            y: TWidth.Epsilon
        ) != top) { return "the saturating sum passes the top"; }

        var comma = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", };

        foreach (var raw in TWidth.Ladder) {
            var value = TWidth.FromRawBits(raw: raw);
            var reference = Oracles.ExactDyadicDecimal(
                numerator: new BigInteger(value: raw),
                shift: bits
            );

            if (TWidth.Raw(value: value) != raw) { return $"the raw factory moved the ladder raw {raw}"; }
            if (TWidth.Raw(value: TWidth.Construct(raw: raw)) != raw) { return $"the constructor moved the ladder raw {raw}"; }
            if (value.ToString() != reference) { return $"the ladder raw {raw} rendered as '{value.ToString()}', expected '{reference}'"; }
            if (value.ToString(
                format: "G17",
                formatProvider: comma
            ) != reference) { return $"the ladder raw {raw} honoured a format or a provider it documents as ignored"; }
        }

        // The boxed comparison contract.
        if (top.CompareTo(obj: null) != 1) { return "a null comparand does not sort first"; }
        if (top.CompareTo(obj: ((object)top)) != 0) { return "the boxed comparison of the top against itself is not zero"; }

        try {
            _ = top.CompareTo(obj: "not a unit fraction");

            return "the boxed comparison accepted a foreign type";
        } catch (ArgumentException exception) {
            if (exception.ParamName != "obj") { return $"the boxed-comparison refusal named '{exception.ParamName}'"; }
        }

        // The double seam inward.
        foreach (var (value, expected) in TWidth.DoubleLadder) {
            if (TWidth.Raw(value: TWidth.FromDouble(value: value)) != expected) { return $"the double {value} converted to raw {TWidth.Raw(value: TWidth.FromDouble(value: value))}, expected {expected}"; }
        }

        // The division ladder: at or above one the quotient SATURATES rather than wrapping, and it saturates after the
        // rounding rather than before.
        foreach (var (dividend, divisor, expected) in TWidth.QuotientLadder) {
            if (TWidth.Raw(value: (TWidth.FromRawBits(raw: dividend) / TWidth.FromRawBits(raw: divisor))) != expected) { return $"the quotient of {dividend} by {divisor} is wrong"; }
        }

        // The three documented-by-code refusals. An inverted clamp range and a zero divisor have no answer, so the members
        // throw; nothing else in this suite or the tools reaches them.
        try {
            _ = TWidth.Clamp(
                value: TWidth.Epsilon,
                minimum: top,
                maximum: zero
            );

            return "the clamp accepted an inverted range";
        } catch (ArgumentException) { }

        try {
            _ = (top / zero);

            return "the divide accepted a zero divisor";
        } catch (DivideByZeroException) { }

        try {
            _ = (top % zero);

            return "the remainder accepted a zero divisor";
        } catch (DivideByZeroException) { }

        return null;
    }
    /// <summary>Proves the text seam at every swept raw: the renderer reproduces the oracle's exact decimal expansion, the
    /// span formatter writes exactly that and REFUSES every destination one character short with nothing reported
    /// written, all four parse entry points read the exact rendering back to the raw it came from, and two derived texts
    /// that do not land on the grid — the rendering truncated, and the rendering with a digit appended — reach the same
    /// accept-or-refuse decision and the same raw as the shared-nothing text oracle.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? TextMatchesOracle(long[] left, long[] right) {
        var bits = TWidth.FractionBitCount;
        var raw = TWidth.Fold(sampled: left[0]);
        var value = TWidth.FromRawBits(raw: raw);
        var reference = Oracles.ExactDyadicDecimal(
            numerator: new BigInteger(value: raw),
            shift: bits
        );
        Span<char> buffer = stackalloc char[(TWidth.TotalBitCount + 4)];

        if (value.ToString() != reference) { return $"raw {raw} rendered as '{value.ToString()}', expected '{reference}'"; }

        if (
            !value.TryFormat(
            destination: buffer[..reference.Length],
            charsWritten: out var written,
            format: default,
            provider: null
        ) ||
            (written != reference.Length) ||
            (new string(value: buffer[..written]) != reference)
        ) { return $"the span format of raw {raw} did not write '{reference}'"; }

        if (
            !value.TryFormat(
            charsWritten: out var spare,
            destination: buffer,
            format: default,
            provider: null
        ) ||
            (spare != reference.Length)
        ) { return $"the span format of raw {raw} into a longer destination reported {spare}"; }

        for (var length = 0; (length < reference.Length); ++length) {
            if (
                value.TryFormat(
                destination: buffer[..length],
                charsWritten: out var refused,
                format: default,
                provider: null
            ) ||
                (0 != refused)
            ) { return $"the span format of raw {raw} accepted a destination of {length} characters"; }
        }

        if (ParseText<TFraction>(
            provider: null,
            s: reference
        ) != value) { return $"the string parse of '{reference}' did not return raw {raw}"; }
        if (TFraction.Parse(
            s: reference.AsSpan(),
            provider: null
        ) != value) { return $"the span parse of '{reference}' did not return raw {raw}"; }
        if (
            !TryParseText<TFraction>(
            provider: null,
            result: out var parsed,
            s: reference
        ) ||
            (parsed != value)
        ) { return $"the string try-parse of '{reference}' did not return raw {raw}"; }
        if (
            !TFraction.TryParse(
            s: reference.AsSpan(),
            provider: null,
            result: out var spanParsed
        ) ||
            (spanParsed != value)
        ) { return $"the span try-parse of '{reference}' did not return raw {raw}"; }

        foreach (var text in DerivedTexts(
            raw: raw,
            rendering: reference
        )) {
            var accepted = TryParseText<TFraction>(
                provider: null,
                result: out var actual,
                s: text
            );
            var admitted = Oracles.TryUnitFractionText(
                fractionBitCount: bits,
                raw: out var expected,
                text: text
            );

            if (accepted != admitted) {
                return $"'{text}' was {(accepted
                    ? "accepted"
                    : "refused")} by the subject and {(admitted
                    ? "accepted"
                    : "refused")} by the oracle";
            }
            if (
                accepted &&
                (TWidth.Raw(value: actual) != expected)
            ) { return $"'{text}' parsed to raw {TWidth.Raw(value: actual)}, expected {expected}"; }
            if (
                !accepted &&
                (actual != default)
            ) { return $"the refusal of '{text}' left raw {TWidth.Raw(value: actual)} behind"; }
        }

        return null;
    }
    /// <summary>Proves the text contract on the width's own committed ladder: every accepted spelling reaches the
    /// hand-derived raw AND the oracle's, through both parse routes; every refused spelling is refused by both, leaves the
    /// default behind, and makes the throwing route throw; the null string is a refusal on one route and an
    /// ArgumentNullException on the other; and the provider is honoured, so a comma-separator culture moves the decimal
    /// point and the invariant spelling stops parsing under it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ParseLadderHolds() {
        var bits = TWidth.FractionBitCount;

        foreach (var (text, expected) in TWidth.ParseLadder) {
            if (
                !TryParseText<TFraction>(
                provider: null,
                result: out var parsed,
                s: text
            ) ||
                (TWidth.Raw(value: parsed) != expected)
            ) { return $"'{text}' parsed to raw {TWidth.Raw(value: parsed)}, expected {expected}"; }
            if (TWidth.Raw(value: ParseText<TFraction>(
                provider: null,
                s: text
            )) != expected) { return $"the throwing string parse of '{text}' is wrong"; }
            if (TWidth.Raw(value: TFraction.Parse(
                s: text.AsSpan(),
                provider: null
            )) != expected) { return $"the throwing span parse of '{text}' is wrong"; }
            if (
                !Oracles.TryUnitFractionText(
                fractionBitCount: bits,
                raw: out var admitted,
                text: text
            ) ||
                (admitted != expected)
            ) { return $"the oracle reads '{text}' as raw {admitted}, expected {expected}"; }
        }

        foreach (var text in TWidth.RefusedTexts) {
            if (
                TryParseText<TFraction>(
                provider: null,
                result: out var refused,
                s: text
            ) ||
                (refused != default)
            ) { return $"'{text}' was accepted, or left raw {TWidth.Raw(value: refused)} behind"; }
            if (Oracles.TryUnitFractionText(
                fractionBitCount: bits,
                raw: out _,
                text: text
            )) { return $"the oracle accepted '{text}'"; }

            try {
                _ = ParseText<TFraction>(
                    provider: null,
                    s: text
                );

                return $"the throwing string parse accepted '{text}'";
            } catch (FormatException) { }

            try {
                _ = TFraction.Parse(
                    s: text.AsSpan(),
                    provider: null
                );

                return $"the throwing span parse accepted '{text}'";
            } catch (FormatException) { }
        }

        if (
            TryParseText<TFraction>(
            provider: null,
            result: out var fromNull,
            s: ((string?)null)
        ) ||
            (fromNull != default)
        ) { return "a null string was accepted"; }

        try {
            _ = ParseText<TFraction>(
                provider: null,
                s: ((string)null!)
            );

            return "the throwing parse accepted a null string";
        } catch (ArgumentNullException exception) {
            if (exception.ParamName != "s") { return $"the null refusal named '{exception.ParamName}'"; }
        }

        // The provider is honoured on both routes: a comma-separator culture moves the point, and the invariant spelling
        // stops naming a number under it.
        var comma = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", };

        if (TWidth.Raw(value: ParseText<TFraction>(
            provider: comma,
            s: "0,5"
        )) != TWidth.HalfRaw) { return "the comma spelling did not parse under a comma-separator provider"; }
        if (TryParseText<TFraction>(
            provider: comma,
            result: out _,
            s: "0.5"
        )) { return "the point spelling parsed under a comma-separator provider"; }
        if (TWidth.Raw(value: ParseText<TFraction>(
            provider: null,
            s: "0.5"
        )) != TWidth.HalfRaw) { return "a null provider is not the invariant culture"; }

        return null;
    }
}
