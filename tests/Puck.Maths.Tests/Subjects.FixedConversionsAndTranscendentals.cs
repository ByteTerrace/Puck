using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- the generic-conversion seam: three modes, two carriers, and the BCL numerics either side ----

    // INumberBase's conversion hooks are EXPLICIT static interface implementations, so a direct static call cannot
    // reach them. These three constrained generics are the only route, and they are the route a real caller takes.
    private static TTarget ConvertChecked<TTarget, TSource>(TSource value)
        where TTarget : INumberBase<TTarget>
        where TSource : INumberBase<TSource> =>
        TTarget.CreateChecked(value: value);
    private static TTarget ConvertSaturating<TTarget, TSource>(TSource value)
        where TTarget : INumberBase<TTarget>
        where TSource : INumberBase<TSource> =>
        TTarget.CreateSaturating(value: value);
    private static TTarget ConvertTruncating<TTarget, TSource>(TSource value)
        where TTarget : INumberBase<TTarget>
        where TSource : INumberBase<TSource> =>
        TTarget.CreateTruncating(value: value);

    private static readonly BigInteger SignedRawMinimum = new(value: long.MinValue);
    private static readonly BigInteger SignedRawMaximum = new(value: long.MaxValue);
    private static readonly BigInteger UnsignedRawMaximum = new(value: ulong.MaxValue);

    // The source's exact value at the target's Q16 scale, from the decimal's own bits: mantissa over a power of ten,
    // scaled and rounded ONCE, in arbitrary width.
    private static BigInteger ScaledDecimal(decimal value) {
        var (numerator, decimalExponent) = DecimalParts(value: value);

        return Oracles.RoundRationalTiesToEven(
            numerator: (numerator << FixedQ4816.FractionBitCount),
            denominator: BigInteger.Pow(
                value: new BigInteger(value: 10),
                exponent: decimalExponent
            )
        );
    }
    // One source against all three modes on the signed carrier. `scaled` is the source's exact value at Q16, derived
    // in BigInteger without asking either carrier anything.
    private static string? SignedConversionModes<TSource>(string label, TSource value, BigInteger scaled, ref int divergences)
        where TSource : INumberBase<TSource> {
        var inRange = ((scaled >= SignedRawMinimum) && (scaled <= SignedRawMaximum));
        var saturating = ConvertSaturating<FixedQ4816, TSource>(value: value).Value;
        var truncating = ConvertTruncating<FixedQ4816, TSource>(value: value).Value;
        var expectedSaturating = (inRange
            ? ((long)scaled)
            : ((scaled < SignedRawMinimum)
                ? long.MinValue
                : long.MaxValue
        ));
        var expectedTruncating = Oracles.WrapToRaw(value: scaled);

        if (saturating != expectedSaturating) { return $"{label}: CreateSaturating is {saturating}, expected {expectedSaturating}"; }
        if (truncating != expectedTruncating) { return $"{label}: CreateTruncating is {truncating}, expected {expectedTruncating}"; }

        if (inRange) {
            var exact = ConvertChecked<FixedQ4816, TSource>(value: value).Value;

            if (exact != ((long)scaled)) { return $"{label}: CreateChecked is {exact}, expected {((long)scaled)}"; }
            if (
                (saturating != exact) ||
                (truncating != exact)
            ) { return $"{label}: the three modes disagree on an IN-RANGE source"; }

            return null;
        }

        if (!Throws<OverflowException>(action: () => _ = ConvertChecked<FixedQ4816, TSource>(value: value))) { return $"{label}: CreateChecked did not report an overflow"; }

        if (saturating != truncating) { ++divergences; }

        return null;
    }
    // The unsigned mirror, against the unsigned carrier's own range.
    private static string? UnsignedConversionModes<TSource>(string label, TSource value, BigInteger scaled, ref int divergences)
        where TSource : INumberBase<TSource> {
        var inRange = ((scaled >= BigInteger.Zero) && (scaled <= UnsignedRawMaximum));
        var saturating = ConvertSaturating<UFixedQ4816, TSource>(value: value).Value;
        var truncating = ConvertTruncating<UFixedQ4816, TSource>(value: value).Value;
        var expectedSaturating = (inRange
            ? ((ulong)scaled)
            : ((scaled < BigInteger.Zero)
                ? ulong.MinValue
                : ulong.MaxValue
        ));
        var expectedTruncating = Oracles.WrapToUnsignedRaw(value: scaled);

        if (saturating != expectedSaturating) { return $"{label}: CreateSaturating is {saturating}, expected {expectedSaturating}"; }
        if (truncating != expectedTruncating) { return $"{label}: CreateTruncating is {truncating}, expected {expectedTruncating}"; }

        if (inRange) {
            var exact = ConvertChecked<UFixedQ4816, TSource>(value: value).Value;

            if (exact != ((ulong)scaled)) { return $"{label}: CreateChecked is {exact}, expected {((ulong)scaled)}"; }
            if (
                (saturating != exact) ||
                (truncating != exact)
            ) { return $"{label}: the three modes disagree on an IN-RANGE source"; }

            return null;
        }

        if (!Throws<OverflowException>(action: () => _ = ConvertChecked<UFixedQ4816, TSource>(value: value))) { return $"{label}: CreateChecked did not report an overflow"; }

        if (saturating != truncating) { ++divergences; }

        return null;
    }
    // The source's exact value at an arbitrary fixed-point scale, from the decimal's own bits — the same route
    // ScaledDecimal uses at FixedQ4816's fixed Q16, generalized to any fraction bit count so FixedQ1648's Q48 and
    // FixedQ3232's Q32 can both reuse it without touching FixedPointConvert.ScaleDecimalWide itself.
    private static BigInteger ScaledDecimalAt(decimal value, int fractionBitCount) {
        var (numerator, decimalExponent) = DecimalParts(value: value);

        return Oracles.RoundRationalTiesToEven(
            numerator: (numerator << fractionBitCount),
            denominator: BigInteger.Pow(
                value: new BigInteger(value: 10),
                exponent: decimalExponent
            )
        );
    }

    /// <summary>Proves the three <c>INumberBase</c> conversion modes through a <c>decimal</c> source into
    /// <see cref="FixedQ1648"/> — one of the two public routes to <c>FixedPointConvert.ScaleDecimalWide</c>, the
    /// other being <see cref="Q3232DecimalConversionModes"/>: <see cref="GenericConversionModes"/> exercises the
    /// same three modes but only ever targets <see cref="FixedQ4816"/>/<see cref="UFixedQ4816"/>, both of which
    /// route decimal through the narrower <c>ScaleDecimal</c> instead. <c>decimal.MaxValue</c>'s exact scaled value
    /// at Q16.48 is independently derived
    /// here from its own documented bit layout (a scale-zero, <c>2⁹⁶ − 1</c> mantissa) rather than through
    /// <c>ScaleDecimalWide</c> itself: checked must refuse it, saturating must clamp to <see cref="FixedQ1648.MaxValue"/>,
    /// and truncating must keep exactly its low sixty-four bits — raw <c>0xFFFF000000000000</c>, signed
    /// <c>−281474976710656</c>. <c>decimal.MinValue</c> mirrors it by sign, and an in-range decimal exercises the
    /// branch where all three modes must agree.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648DecimalConversionModes() {
        const long expectedTruncatingAtMax = unchecked((long)0xFFFF000000000000UL);
        var scaledMax = ScaledDecimalAt(
            fractionBitCount: FixedQ1648.FractionBitCount,
            value: decimal.MaxValue
        );

        if (scaledMax != (((BigInteger.One << 96) - BigInteger.One) << FixedQ1648.FractionBitCount)) { return "the independently-derived scaled value of decimal.MaxValue does not match its hand-derived bit layout"; }

        if (!Throws<OverflowException>(action: () => _ = ConvertChecked<FixedQ1648, decimal>(value: decimal.MaxValue))) { return "CreateChecked accepted decimal.MaxValue instead of refusing it"; }

        var saturatedMax = ConvertSaturating<FixedQ1648, decimal>(value: decimal.MaxValue);

        if (saturatedMax != FixedQ1648.MaxValue) { return $"CreateSaturating of decimal.MaxValue is {saturatedMax}, expected MaxValue"; }

        var truncatedMax = ConvertTruncating<FixedQ1648, decimal>(value: decimal.MaxValue).Value;

        if (truncatedMax != expectedTruncatingAtMax) { return $"CreateTruncating of decimal.MaxValue is raw {truncatedMax}, expected raw {expectedTruncatingAtMax}"; }

        if (!Throws<OverflowException>(action: () => _ = ConvertChecked<FixedQ1648, decimal>(value: decimal.MinValue))) { return "CreateChecked accepted decimal.MinValue instead of refusing it"; }

        var saturatedMin = ConvertSaturating<FixedQ1648, decimal>(value: decimal.MinValue);

        if (saturatedMin != FixedQ1648.MinValue) { return $"CreateSaturating of decimal.MinValue is {saturatedMin}, expected MinValue"; }

        var truncatedMin = ConvertTruncating<FixedQ1648, decimal>(value: decimal.MinValue).Value;

        if (truncatedMin != unchecked(-expectedTruncatingAtMax)) { return $"CreateTruncating of decimal.MinValue is raw {truncatedMin}, expected raw {unchecked(-expectedTruncatingAtMax)}"; }

        // An in-range decimal: all three modes must agree with each other and with the independent oracle.
        var inRangeExpected = ((long)ScaledDecimalAt(
            fractionBitCount: FixedQ1648.FractionBitCount,
            value: 1.5m
        ));
        var exact = ConvertChecked<FixedQ1648, decimal>(value: 1.5m).Value;
        var saturatedInRange = ConvertSaturating<FixedQ1648, decimal>(value: 1.5m).Value;
        var truncatedInRange = ConvertTruncating<FixedQ1648, decimal>(value: 1.5m).Value;

        if (
            (exact != inRangeExpected) ||
            (saturatedInRange != inRangeExpected) ||
            (truncatedInRange != inRangeExpected)
        ) { return $"the three modes disagree on the in-range decimal 1.5: checked={exact}, saturating={saturatedInRange}, truncating={truncatedInRange}, expected {inRangeExpected}"; }

        return null;
    }
    /// <summary>Proves the three INumberBase conversion modes are three DIFFERENT operations at both fixed-point
    /// carriers: checked refuses what will not fit, saturating clamps to the carrier's ends, and truncating reduces
    /// the scaled value modulo the carrier's width — including across the signed/unsigned peer seam, whose identical
    /// width and identical Q16 scale make truncation the low sixty-four bits verbatim.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? GenericConversionModes() {
        var divergences = 0;
        var shift = FixedQ4816.FractionBitCount;

        // The peer seam, both directions. A peer's raw IS its value at Q16, so the scaled column is the raw itself.
        if (SignedConversionModes(
            label: "FixedQ4816 ← UFixedQ4816.MaxValue",
            value: UFixedQ4816.MaxValue,
            scaled: UnsignedRawMaximum,
            divergences: ref divergences
        ) is { } a) { return a; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← UFixedQ4816 one",
            value: UFixedQ4816.One,
            scaled: new BigInteger(value: 65536L),
            divergences: ref divergences
        ) is { } b) { return b; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← UFixedQ4816 raw 2⁶³",
            value: UFixedQ4816.FromRawBits(value: (1UL << 63)),
            scaled: (BigInteger.One << 63),
            divergences: ref divergences
        ) is { } c) { return c; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← UFixedQ4816.MinValue",
            value: UFixedQ4816.MinValue,
            scaled: BigInteger.Zero,
            divergences: ref divergences
        ) is { } d) { return d; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← FixedQ4816 raw −1",
            value: FixedQ4816.FromRawBits(value: -1L),
            scaled: BigInteger.MinusOne,
            divergences: ref divergences
        ) is { } e) { return e; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← FixedQ4816.One",
            value: FixedQ4816.One,
            scaled: new BigInteger(value: 65536L),
            divergences: ref divergences
        ) is { } f) { return f; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← FixedQ4816.MinValue",
            value: FixedQ4816.MinValue,
            scaled: SignedRawMinimum,
            divergences: ref divergences
        ) is { } g) { return g; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← FixedQ4816.MaxValue",
            value: FixedQ4816.MaxValue,
            scaled: SignedRawMaximum,
            divergences: ref divergences
        ) is { } h) { return h; }

        // The BCL numerics: both signednesses at sixty-four bits, both at a hundred and twenty-eight, the unbounded
        // integer, and the fractional one — each in range and each past both carriers' ends.
        if (SignedConversionModes(
            label: "FixedQ4816 ← long 1",
            value: 1L,
            scaled: (BigInteger.One << shift),
            divergences: ref divergences
        ) is { } i) { return i; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← long −1",
            value: -1L,
            scaled: (BigInteger.MinusOne << shift),
            divergences: ref divergences
        ) is { } j) { return j; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← long 2⁴⁷",
            value: 140737488355328L,
            scaled: (new BigInteger(value: 140737488355328L) << shift),
            divergences: ref divergences
        ) is { } k) { return k; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← long −2⁴⁷",
            value: -140737488355328L,
            scaled: (new BigInteger(value: -140737488355328L) << shift),
            divergences: ref divergences
        ) is { } l) { return l; }
        if (SignedConversionModes(
            divergences: ref divergences,
            label: "FixedQ4816 ← long.MaxValue",
            scaled: (SignedRawMaximum << shift),
            value: long.MaxValue
        ) is { } m) { return m; }
        if (SignedConversionModes(
            divergences: ref divergences,
            label: "FixedQ4816 ← ulong.MaxValue",
            scaled: (UnsignedRawMaximum << shift),
            value: ulong.MaxValue
        ) is { } n) { return n; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← Int128.MaxValue",
            value: Int128.MaxValue,
            scaled: (((BigInteger.One << 127) - BigInteger.One) << shift),
            divergences: ref divergences
        ) is { } o) { return o; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← UInt128.MaxValue",
            value: UInt128.MaxValue,
            scaled: (((BigInteger.One << 128) - BigInteger.One) << shift),
            divergences: ref divergences
        ) is { } p) { return p; }
        if (SignedConversionModes(
            divergences: ref divergences,
            label: "FixedQ4816 ← BigInteger 10³⁰",
            scaled: (BigIntegerTenToThirty << shift),
            value: BigIntegerTenToThirty
        ) is { } q) { return q; }
        if (SignedConversionModes(
            divergences: ref divergences,
            label: "FixedQ4816 ← BigInteger −10³⁰",
            scaled: ((-BigIntegerTenToThirty) << shift),
            value: -BigIntegerTenToThirty
        ) is { } r) { return r; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← decimal 1.5",
            value: 1.5m,
            scaled: ScaledDecimal(value: 1.5m),
            divergences: ref divergences
        ) is { } s) { return s; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← decimal −1.5",
            value: -1.5m,
            scaled: ScaledDecimal(value: -1.5m),
            divergences: ref divergences
        ) is { } t) { return t; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← decimal 10²⁰",
            value: 100000000000000000000m,
            scaled: ScaledDecimal(value: 100000000000000000000m),
            divergences: ref divergences
        ) is { } u) { return u; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← long −1",
            value: -1L,
            scaled: (BigInteger.MinusOne << shift),
            divergences: ref divergences
        ) is { } v) { return v; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← long 2⁴⁸",
            value: 281474976710656L,
            scaled: (new BigInteger(value: 281474976710656L) << shift),
            divergences: ref divergences
        ) is { } w) { return w; }
        if (UnsignedConversionModes(
            divergences: ref divergences,
            label: "UFixedQ4816 ← ulong.MaxValue",
            scaled: (UnsignedRawMaximum << shift),
            value: ulong.MaxValue
        ) is { } x) { return x; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← Int128.MinValue",
            value: Int128.MinValue,
            scaled: ((-(BigInteger.One << 127)) << shift),
            divergences: ref divergences
        ) is { } y) { return y; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← UInt128.MaxValue",
            value: UInt128.MaxValue,
            scaled: (((BigInteger.One << 128) - BigInteger.One) << shift),
            divergences: ref divergences
        ) is { } z) { return z; }
        if (UnsignedConversionModes(
            divergences: ref divergences,
            label: "UFixedQ4816 ← BigInteger 10³⁰",
            scaled: (BigIntegerTenToThirty << shift),
            value: BigIntegerTenToThirty
        ) is { } aa) { return aa; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← decimal −1.5",
            value: -1.5m,
            scaled: ScaledDecimal(value: -1.5m),
            divergences: ref divergences
        ) is { } ab) { return ab; }

        // The decimal witnesses whose exact scaled product needs more than decimal's own ninety-six-bit mantissa: a
        // decimal multiply rescales — and ROUNDS — first, landing the intermediate exactly on a half that a second
        // rounding then resolves by parity, off the true value's side. Read from the decimal's own bits there is one
        // rounding, and the hand-derived raw pins the oracle itself. RULED, and the correction is the point.
        if (ScaledDecimal(value: 30517578125000.000022888183593m) != new BigInteger(value: 2000000000000000001L)) { return "the decimal double-rounding witness's hand-derived raw does not match the oracle"; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← decimal 30517578125000.000022888183593 (just below the half)",
            value: 30517578125000.000022888183593m,
            scaled: ScaledDecimal(value: 30517578125000.000022888183593m),
            divergences: ref divergences
        ) is { } ac) { return ac; }
        if (SignedConversionModes(
            label: "FixedQ4816 ← decimal 30517578125000.000007629394532 (just above the half)",
            value: 30517578125000.000007629394532m,
            scaled: ScaledDecimal(value: 30517578125000.000007629394532m),
            divergences: ref divergences
        ) is { } ad) { return ad; }

        // The unsigned twins live ABOVE the signed carrier's top, so only UFixedQ4816 can hold them. At the first
        // witness the exact d·2¹⁶ is N + 0.4999999999508… with N = 1310720000000000001 odd — strictly below the
        // half, so one exact rounding answers N, while the decimal multiply sheds the deciding low digits onto
        // exactly N + 0.5 and ties-to-even carries the odd N up. The second witness diverges the same way at
        // 4103496782671996199. RULED, and the correction is the point.
        if (ScaledDecimal(value: 20000000000000.000022888183593m) != new BigInteger(value: 1310720000000000001L)) { return "the unsigned decimal double-rounding witness's hand-derived raw does not match the oracle"; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← decimal 20000000000000.000022888183593 (just below the half above an odd raw)",
            value: 20000000000000.000022888183593m,
            scaled: ScaledDecimal(value: 20000000000000.000022888183593m),
            divergences: ref divergences
        ) is { } ae) { return ae; }
        if (UnsignedConversionModes(
            label: "UFixedQ4816 ← decimal 62614391825439.395133972167968",
            value: 62614391825439.395133972167968m,
            scaled: ScaledDecimal(value: 62614391825439.395133972167968m),
            divergences: ref divergences
        ) is { } af) { return af; }

        if (divergences < 8) { return $"only {divergences} out-of-range sources made saturating and truncating differ; the two modes are not being told apart"; }

        // The TO hooks, where the TARGET's own truncation must decide the range rather than an intermediate's. The
        // reference is the BCL's own answer for the same integer, which is the contract being conformed to.
        if (ConvertTruncating<long, FixedQ4816>(value: FixedQ4816.FromInteger(value: 5L)) != 5L) { return "the long target did not receive five"; }
        if (ConvertTruncating<byte, FixedQ4816>(value: FixedQ4816.FromInteger(value: 300L)) != byte.CreateTruncating(value: 300)) { return "the byte target saturated instead of truncating three hundred"; }
        if (ConvertTruncating<ulong, FixedQ4816>(value: FixedQ4816.FromInteger(value: -1L)) != ulong.CreateTruncating(value: -1L)) { return "the ulong target clamped instead of truncating minus one"; }
        if (ConvertTruncating<sbyte, UFixedQ4816>(value: UFixedQ4816.FromRawBits(value: (200UL << shift))) != sbyte.CreateTruncating(value: 200)) { return "the sbyte target saturated instead of truncating two hundred"; }
        if (ConvertTruncating<Int128, UFixedQ4816>(value: UFixedQ4816.MaxValue) != ((Int128)281474976710655L)) { return "the Int128 target did not receive the whole unsigned range"; }
        if (ConvertTruncating<BigInteger, FixedQ4816>(value: FixedQ4816.MinValue) != new BigInteger(value: -140737488355328L)) { return "the BigInteger target did not receive the signed minimum"; }
        if (ConvertSaturating<byte, FixedQ4816>(value: FixedQ4816.FromInteger(value: 300L)) != byte.MaxValue) { return "the byte target's SATURATING conversion stopped clamping"; }
        if (ConvertSaturating<ulong, FixedQ4816>(value: FixedQ4816.FromInteger(value: -1L)) != ulong.MinValue) { return "the ulong target's SATURATING conversion stopped clamping"; }

        if (DoubleCheckedVsFromDouble() is { } doubleDetail) { return doubleDetail; }
        if (FloatTargetSingleRounding() is { } floatDetail) { return floatDetail; }

        return null;
    }

    // The outbound float seam, on BOTH carriers: the raw must round ONCE onto the 24-bit mantissa. A double
    // intermediate absorbs the sub-double-ULP tail of a raw wider than fifty-three significant bits, can land exactly
    // on a float midpoint, and float's ties-to-even then answers by parity instead of by the true value's side — one
    // full ULP off. Every expectation is a hand-derived bit pattern, so no floating-point arithmetic runs inside the
    // law. RULED on both carriers, and the correction is the point.
    private static string? FloatTargetSingleRounding() {
        foreach (var (label, raw, expectedBits) in FixedToSingleLadder) {
            var value = Raw(value: raw);
            var checkedBits = BitConverter.SingleToUInt32Bits(value: ConvertChecked<float, FixedQ4816>(value: value));
            var saturatingBits = BitConverter.SingleToUInt32Bits(value: ConvertSaturating<float, FixedQ4816>(value: value));
            var truncatingBits = BitConverter.SingleToUInt32Bits(value: ConvertTruncating<float, FixedQ4816>(value: value));

            if (checkedBits != expectedBits) { return $"{label}: CreateChecked<float> is 0x{checkedBits:X8}, expected 0x{expectedBits:X8}"; }
            if (
                (saturatingBits != expectedBits) ||
                (truncatingBits != expectedBits)
            ) { return $"{label}: the three float conversion modes do not share the correctly rounded single"; }
        }

        foreach (var (label, raw, expectedBits) in UnsignedFixedToSingleLadder) {
            var value = UFixedQ4816.FromRawBits(value: raw);
            var checkedBits = BitConverter.SingleToUInt32Bits(value: ConvertChecked<float, UFixedQ4816>(value: value));
            var saturatingBits = BitConverter.SingleToUInt32Bits(value: ConvertSaturating<float, UFixedQ4816>(value: value));
            var truncatingBits = BitConverter.SingleToUInt32Bits(value: ConvertTruncating<float, UFixedQ4816>(value: value));

            if (checkedBits != expectedBits) { return $"{label}: CreateChecked<float> is 0x{checkedBits:X8}, expected 0x{expectedBits:X8}"; }
            if (
                (saturatingBits != expectedBits) ||
                (truncatingBits != expectedBits)
            ) { return $"{label}: the three float conversion modes do not share the correctly rounded single"; }
        }

        return null;
    }

    // Hand-derived from the binary32 format: at magnitude 2⁴⁶ a float ULP is 2²³, so the midpoint above 2⁴⁶ is
    // 2⁴⁶ + 2²² — raw 2⁶² + 2³⁸ — and the exact midpoint ties DOWN to the even mantissa while one raw above it must
    // round UP to 2⁴⁶ + 2²³; through a double that raw's 2⁻¹⁶ tail is absorbed first and the answer comes back one
    // ULP low. The exponent fields: 2⁻¹⁶ → 111, 1 → 127, 2⁴⁶ → 173, 2⁴⁷ → 174, each shifted twenty-three bits.
    private static readonly (string Label, long Raw, uint ExpectedBits)[] FixedToSingleLadder = [
        ("one", 65536L, 0x3F800000U),
        ("epsilon, 2⁻¹⁶ — fully normal in binary32", 1L, 0x37800000U),
        ("the exact float midpoint 2⁴⁶ + 2²², a true tie to even", 4611686293305294848L, 0x56800000U),
        ("one raw above the float midpoint", 4611686293305294849L, 0x56800001U),
        ("one raw above the float midpoint, negated", -4611686293305294849L, 0xD6800001U),
        ("MaxValue, which rounds up onto 2⁴⁷", long.MaxValue, 0x57000000U),
        ("MinValue, exactly −2⁴⁷", long.MinValue, 0xD7000000U),
    ];
    // The unsigned mirror, hand-derived from the same binary32 facts at the exponent fields 111, 127, 164, 173 and
    // 175. The two witness raws straddle the float midpoint above 2³⁷ by exactly 2⁻¹⁶ — the tail a double
    // intermediate absorbs onto the midpoint, where ties-to-even then answers by parity: raw 2⁵³ + 2²⁹ + 1 sits one
    // raw ABOVE the midpoint (2³⁷ + 2¹³) and must round UP to 2³⁷ + 2¹⁴, and raw 2⁵³ + 2³⁰ + 2²⁹ − 1 sits one raw
    // BELOW the next midpoint and must round DOWN to that same float — one double rounding misses each from the
    // opposite side.
    private static readonly (string Label, ulong Raw, uint ExpectedBits)[] UnsignedFixedToSingleLadder = [
        ("unsigned one", 65536UL, 0x3F800000U),
        ("unsigned epsilon, 2⁻¹⁶ — fully normal in binary32", 1UL, 0x37800000U),
        ("the exact float midpoint 2⁴⁶ + 2²², a true tie to even, unsigned", 4611686293305294848UL, 0x56800000U),
        ("one raw above the float midpoint, unsigned", 4611686293305294849UL, 0x56800001U),
        ("the double-rounding witness raw 2⁵³ + 2²⁹ + 1, one raw above the midpoint at 2³⁷", 9007199791611905UL, 0x52000001U),
        ("the witness's mirror raw 2⁵³ + 2³⁰ + 2²⁹ − 1, one raw below the next midpoint", 9007200865353727UL, 0x52000001U),
        ("unsigned MaxValue, which rounds up onto 2⁴⁸", ulong.MaxValue, 0x57800000U),
    ];

    // The FromDoubleChecked-vs-FromDouble sub-probe: CreateChecked<double> is reached through the SAME ConvertChecked
    // helper the rest of this case uses, and it shares FromDouble's own rounding rule (double.Round(value * 2^16,
    // MidpointRounding.ToEven)) verbatim -- the two diverge ONLY in what happens outside the representable range:
    // FromDouble saturates (and maps NaN to zero), CreateChecked refuses with an OverflowException (NaN included).
    private static string? DoubleCheckedVsFromDouble() {
        // In range: the two must agree exactly, rounding rule and all.
        foreach (var value in new[] { 0.0, 1.5, -1.5, 12345.6789, -98765.4321, 100000000000.0, -100000000000.0, -140737488355328.0 }) {
            var checkedResult = ConvertChecked<FixedQ4816, double>(value: value);
            var direct = FixedQ4816.FromDouble(value: value);

            if (checkedResult != direct) { return $"FixedQ4816: CreateChecked<double>({value}) = {checkedResult.Value}, FromDouble = {direct.Value}, expected agreement in range"; }
        }

        // Out of range (huge magnitude, both infinities, and NaN): CreateChecked refuses while FromDouble still
        // returns a value, saturated at the extreme or zeroed for NaN.
        foreach (var value in new[] { 1e30, -1e30, double.PositiveInfinity, double.NegativeInfinity, double.NaN }) {
            if (!Throws<OverflowException>(action: () => _ = ConvertChecked<FixedQ4816, double>(value: value))) { return $"FixedQ4816: CreateChecked<double>({value}) did not refuse an out-of-range source"; }

            var direct = FixedQ4816.FromDouble(value: value);
            var expected = (double.IsNaN(d: value)
                ? FixedQ4816.Zero
                : ((value > 0d)
                    ? FixedQ4816.MaxValue
                    : FixedQ4816.MinValue
            ));

            if (direct != expected) { return $"FixedQ4816: FromDouble({value}) = {direct.Value}, expected the saturated/zeroed {expected.Value}"; }
        }

        // The unsigned mirror: zero and every positive interior value are in range, but a NEGATIVE value is out of
        // range too, not merely a huge one.
        foreach (var value in new[] { 0.0, 1.5, 12345.6789, 100000000000.0, 281474976710000.0 }) {
            var checkedResult = ConvertChecked<UFixedQ4816, double>(value: value);
            var direct = UFixedQ4816.FromDouble(value: value);

            if (checkedResult != direct) { return $"UFixedQ4816: CreateChecked<double>({value}) = {checkedResult.Value}, FromDouble = {direct.Value}, expected agreement in range"; }
        }

        foreach (var value in new[] { -1.5, -100000000000.0, 1e30, double.PositiveInfinity, double.NegativeInfinity, double.NaN }) {
            if (!Throws<OverflowException>(action: () => _ = ConvertChecked<UFixedQ4816, double>(value: value))) { return $"UFixedQ4816: CreateChecked<double>({value}) did not refuse an out-of-range source"; }

            var direct = UFixedQ4816.FromDouble(value: value);
            var expected = ((double.IsNaN(d: value) || (value <= 0d))
                ? UFixedQ4816.Zero
                : UFixedQ4816.MaxValue
            );

            if (direct != expected) { return $"UFixedQ4816: FromDouble({value}) = {direct.Value}, expected the saturated/zeroed {expected.Value}"; }
        }

        return null;
    }

    private static readonly BigInteger BigIntegerTenToThirty = BigInteger.Pow(
        value: new BigInteger(value: 10),
        exponent: 30
    );

    // ---- the culture-token seam: configurations on which the two passes could read different numbers ----

    // A NumberFormatInfo whose sign, currency and separator tokens are set explicitly, so no law reads an ambient
    // culture and every alias below is deliberate.
    private static NumberFormatInfo CultureTokens(
        string negativeSign,
        string positiveSign,
        string currencySymbol,
        string numberDecimalSeparator,
        string currencyDecimalSeparator,
        string numberGroupSeparator = " ",
        string currencyGroupSeparator = " "
    ) => new() {
        CurrencyDecimalSeparator = currencyDecimalSeparator,
        CurrencyGroupSeparator = currencyGroupSeparator,
        CurrencySymbol = currencySymbol,
        NegativeSign = negativeSign,
        NumberDecimalSeparator = numberDecimalSeparator,
        NumberGroupSeparator = numberGroupSeparator,
        PositiveSign = positiveSign,
    };

    // Hand-built configurations that alias one enabled token with another, plus the disjoint control that must still
    // parse. The expectation column is the REFUSAL, not a value: where the two passes could read different numbers the
    // contract is that nothing is returned at all.
    private static readonly (string Label, string Text, NumberFormatInfo Provider, bool Accepted, long Expected)[] CultureTokenLadder = [
        (
            "the negative sign aliased with the currency symbol, separator families split",
            "$1.5",
            CultureTokens(
            negativeSign: "$",
            positiveSign: "+",
            currencySymbol: "$",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: ","
        ),
            false,
            0L
        ),
        (
            "the positive sign aliased with the currency symbol",
            "$1.5",
            CultureTokens(
            negativeSign: "-",
            positiveSign: "$",
            currencySymbol: "$",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            false,
            0L
        ),
        (
            "a negative sign the currency symbol is a prefix of",
            "$-1.5",
            CultureTokens(
            negativeSign: "$-",
            positiveSign: "+",
            currencySymbol: "$",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            false,
            0L
        ),
        (
            "a currency symbol the negative sign is a prefix of",
            "$1.5",
            CultureTokens(
            negativeSign: "$",
            positiveSign: "+",
            currencySymbol: "$X",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            false,
            0L
        ),
        (
            "disjoint tokens but split separator families, on an input the platform reads unambiguously",
            "USD1,5",
            CultureTokens(
            negativeSign: "~",
            positiveSign: "^",
            currencySymbol: "USD",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: ","
        ),
            false,
            0L
        ),
        (
            "a decimal separator aliasing the positive sign, spelled as the separator's own token",
            "+15",
            CultureTokens(
            negativeSign: "-",
            positiveSign: "+",
            currencySymbol: "USD",
            numberDecimalSeparator: "+",
            currencyDecimalSeparator: "+"
        ),
            false,
            0L
        ),
        (
            "a positive sign aliasing the decimal separator, spelled as the sign's own token",
            ".5",
            CultureTokens(
            negativeSign: "-",
            positiveSign: ".",
            currencySymbol: "USD",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            false,
            0L
        ),
        (
            "a negative sign aliasing the decimal separator — the platform reads a sign, the scanner a radix point",
            ".5",
            CultureTokens(
            negativeSign: ".",
            positiveSign: "^",
            currencySymbol: "USD",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            false,
            0L
        ),
        (
            "a currency symbol CONTAINING the active decimal separator, consumed whole by the platform",
            "S/.5",
            CultureTokens(
            negativeSign: "-",
            positiveSign: "^",
            currencySymbol: "S/.",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            false,
            0L
        ),
        (
            "a decimal separator whose text is parser white space, consumed in the platform's white-space phase",
            " 15",
            CultureTokens(
            negativeSign: "-",
            positiveSign: "^",
            currencySymbol: "USD",
            numberDecimalSeparator: " ",
            currencyDecimalSeparator: " "
        ),
            false,
            0L
        ),
        (
            "a decimal separator carrying the exponent marker, which exponent discovery would skip",
            "1e2e1",
            CultureTokens(
            negativeSign: "-",
            positiveSign: "^",
            currencySymbol: "USD",
            numberDecimalSeparator: "e",
            currencyDecimalSeparator: "e"
        ),
            false,
            0L
        ),
        (
            "a group separator carrying the exponent marker, illegal past the point for the platform",
            "1.2e3",
            CultureTokens(
            currencyDecimalSeparator: ".",
            currencyGroupSeparator: "e",
            currencySymbol: "USD",
            negativeSign: "-",
            numberDecimalSeparator: ".",
            numberGroupSeparator: "e",
            positiveSign: "^"
        ),
            false,
            0L
        ),
        (
            "the disjoint control, number syntax",
            "~1.5",
            CultureTokens(
            negativeSign: "~",
            positiveSign: "^",
            currencySymbol: "USD",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            true,
            -98304L
        ),
        (
            "the disjoint control, currency syntax",
            "USD1.5",
            CultureTokens(
            negativeSign: "~",
            positiveSign: "^",
            currencySymbol: "USD",
            numberDecimalSeparator: ".",
            currencyDecimalSeparator: "."
        ),
            true,
            98304L
        ),
    ];

    /// <summary>Proves that a hand-built provider whose enabled tokens alias one another is REFUSED rather than
    /// quantized: on every aliased configuration the platform validator accepts a number the exact scanner would read
    /// differently, and all four entry points refuse it, while the disjoint control with the same custom tokens still
    /// parses to its hand-derived raw.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CultureTokenAmbiguityRefused() {
        foreach (var (label, text, provider, accepted, expected) in CultureTokenLadder) {
            if (!decimal.TryParse(
                provider: provider,
                result: out _,
                s: text,
                style: NumberStyles.Any
            )) { return $"{label}: the platform validator refused '{text}', so this row states nothing about the exact pass"; }

            if (!accepted) {
                if (
                    FixedQ4816.TryParse(
                    provider: provider,
                    result: out var refusedString,
                    s: text,
                    style: NumberStyles.Any
                ) ||
                    (refusedString != default)
                ) { return $"{label}: the string try-parse of '{text}' returned {refusedString.Value} instead of refusing"; }
                if (
                    FixedQ4816.TryParse(
                    s: text.AsSpan(),
                    style: NumberStyles.Any,
                    provider: provider,
                    result: out var refusedSpan
                ) ||
                    (refusedSpan != default)
                ) { return $"{label}: the span try-parse of '{text}' returned {refusedSpan.Value} instead of refusing"; }
                if (!Throws<FormatException>(action: () => _ = FixedQ4816.Parse(
                    provider: provider,
                    s: text,
                    style: NumberStyles.Any
                ))) { return $"{label}: the throwing string parse of '{text}' did not report a format failure"; }
                if (!Throws<FormatException>(action: () => _ = FixedQ4816.Parse(
                    s: text.AsSpan(),
                    style: NumberStyles.Any,
                    provider: provider
                ))) { return $"{label}: the throwing span parse of '{text}' did not report a format failure"; }

                continue;
            }

            if (FixedQ4816.Parse(
                provider: provider,
                s: text,
                style: NumberStyles.Any
            ).Value != expected) { return $"{label}: the string parse of '{text}' did not return {expected}"; }
            if (FixedQ4816.Parse(
                s: text.AsSpan(),
                style: NumberStyles.Any,
                provider: provider
            ).Value != expected) { return $"{label}: the span parse of '{text}' did not return {expected}"; }
            if (
                !FixedQ4816.TryParse(
                provider: provider,
                result: out var fromString,
                s: text,
                style: NumberStyles.Any
            ) ||
                (fromString.Value != expected)
            ) { return $"{label}: the string try-parse of '{text}' did not return {expected}"; }
            if (
                !FixedQ4816.TryParse(
                s: text.AsSpan(),
                style: NumberStyles.Any,
                provider: provider,
                result: out var fromSpan
            ) ||
                (fromSpan.Value != expected)
            ) { return $"{label}: the span try-parse of '{text}' did not return {expected}"; }
        }

        return null;
    }

    // ---- the signed Q48.16 transcendentals: exact poles, and directed-rounding envelopes where no exact answer exists ----

    // The tolerance a transcendental envelope allows, in guard units: numerator/denominator raw ULP.
    private static BigInteger UlpUnits(int numerator, int denominator) =>
        (((BigInteger.One << Oracles.GuardBitCount) * numerator) / denominator);
    // The envelope statement itself: the subject's raw, lifted to the guard scale, lies inside the enclosure widened by
    // the stated tolerance. A transcendental kernel has no correctly-rounded answer to be compared against, so this is
    // the strongest statement an oracle can make — and it is a strong one, because the enclosure's own width is a
    // handful of guard units while the tolerance is a fraction of one ULP.
    private static string? WithinEnvelope(string name, long subjectRaw, Oracles.Enclosure enclosure, BigInteger toleranceUnits) {
        var scaled = (new BigInteger(value: subjectRaw) << Oracles.GuardBitCount);

        if (scaled < (enclosure.Low - toleranceUnits)) { return $"{name} is {subjectRaw}, below the envelope [{enclosure.Low}, {enclosure.High}] at guard scale by more than the allowed {toleranceUnits} units"; }
        if (scaled > (enclosure.High + toleranceUnits)) { return $"{name} is {subjectRaw}, above the envelope [{enclosure.Low}, {enclosure.High}] at guard scale by more than the allowed {toleranceUnits} units"; }

        return null;
    }
    // The logarithm's domain is the positive raws; a non-positive raw is the documented MinValue refusal, stated
    // structurally rather than swept. Clearing the sign bit maps the whole sampled space onto it, and the zero raw —
    // the one value that would still be refused — onto Epsilon.
    private static long PositiveRaw(long raw) {
        var magnitude = unchecked((ulong)raw) & ((ulong)long.MaxValue);

        return ((0UL == magnitude)
            ? 1L
            : ((long)magnitude)
        );
    }
    // Folds a sampled raw onto the exponent band [−20, 47) at full 2⁻¹⁶ resolution: the whole NON-SATURATING interior
    // plus the underflow gate, so the sweep spends every draw where the 128-interval table and the quartic residual are
    // actually consulted. The saturating half-open tail from 47 upward is excluded on purpose — there the kernel
    // deliberately answers MaxValue, which no envelope around the true value could contain. Subject and oracle apply
    // the identical map.
    private static long Exp2ExponentRaw(long raw) =>
        (((long)(unchecked((ulong)raw) % (67UL << FixedQ4816.FractionBitCount))) - (20L << FixedQ4816.FractionBitCount));
    // Exp2's envelope, DERIVED from the kernel's own two error terms rather than declared as a step: half a raw ULP for
    // the closing round-half-UP narrowing, plus the mantissa's own relative error carried up to the result's scale.
    // That relative error is dominated by the quartic's truncation of the exponential series — the omitted tail
    // Σ_{n≥5} (ln2·r)ⁿ/n! is at most 3.85·10⁻¹⁴ at the largest residual r = 511/2¹⁶ < 2⁻⁷ — with the six Q62
    // truncations and the table's own rounding together under 10⁻¹⁸, so 2⁻⁴⁴ ≈ 5.68·10⁻¹⁴ covers the whole of it.
    // CONTINUOUS by construction, where the two-regime step this replaces was not: below 2²⁷ it allowed 0.75 raw ULP
    // absolute and above it 2⁻⁴⁰ relative, which at the switch is eight raw ULP — and the kernel's own shape crosses
    // 0.75 raw ULP at a result near 2²⁶·⁸, INSIDE the absolute arm, so the step asserted a bound over an octave where
    // the kernel cannot deliver one. This form is strictly tighter than the step everywhere below 2²⁶ (half a ULP
    // against three quarters) and sixteen times tighter above 2²⁷.
    private static BigInteger Exp2ToleranceUnits(BigInteger high) =>
        (UlpUnits(
            denominator: 2,
            numerator: 1
        ) + (high >> 44));
    // The three committed circular regimes: 0.75 raw ULP over |θ| ≤ 2π, 1.0 below 2⁴⁸ raw, 2.5 over the full carrier.
    // The bands follow the reduction constant's own error, which grows linearly in |raw|: |C − 2⁶⁴/2π| ≤ ½ admits up to
    // 2π·|raw|/2⁶⁵ ≈ 1.57 raw ULP of argument error at |raw| = 2⁶³, on top of the kernel's own ≤ 0.5.
    private static BigInteger SinCosToleranceUnits(long raw) {
        var magnitude = ((long.MinValue == raw)
            ? (BigInteger.One << 63)
            : BigInteger.Abs(value: new BigInteger(value: raw))
        );

        if (magnitude <= 411775) {
            return UlpUnits(
            denominator: 4,
            numerator: 3
        );
        }
        if (magnitude < (BigInteger.One << 48)) {
            return UlpUnits(
            denominator: 1,
            numerator: 1
        );
        }

        return UlpUnits(
            denominator: 2,
            numerator: 5
        );
    }

    /// <summary>Proves the square root is BIT-EXACTLY the documented floor at every swept raw, against a Newton descent
    /// in arbitrary width settled by the exact predicate; states that predicate directly on the returned raw, so the
    /// answer is pinned without reference to how either side computed it; and pins the non-positive policy and the seam
    /// between the subject's two lanes.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedSqrtExact(long[] left, long[] right) {
        if (FixedSqrtAt(raw: left[0]) is { } sampled) { return sampled; }

        // The non-positive policy and the two-lane seam, always: the raws either side of 2⁴⁸ take different code paths.
        foreach (var raw in ((ReadOnlySpan<long>)[long.MinValue, -1L, 0L, 1L, ((1L << 48) - 1L), (1L << 48), long.MaxValue])) {
            if (FixedSqrtAt(raw: raw) is { } rung) { return rung; }
        }

        return null;
    }

    private static string? FixedSqrtAt(long raw) {
        var actual = FixedQ4816.Sqrt(value: Raw(value: raw)).Value;

        if (raw <= 0L) {
            return ((0L == actual)
                ? null
                : $"the square root of the non-positive raw {raw} is {actual}"
            );
        }

        var radicand = (new BigInteger(value: raw) << FixedQ4816.FractionBitCount);
        var expected = Oracles.IntegerSquareRoot(value: radicand);
        var exact = new BigInteger(value: actual);

        if (exact != expected) { return $"the square root of {raw} is {actual}, expected {expected}"; }
        if ((exact * exact) > radicand) { return $"the square root of {raw} squares above the radicand"; }
        if (((exact + BigInteger.One) * (exact + BigInteger.One)) <= radicand) { return $"the square root of {raw} is not the greatest integer whose square fits"; }

        return null;
    }

    /// <summary>Proves the logarithm lies inside the repeated-squaring enclosure widened by the committed 0.75 raw ULP
    /// envelope, that it is EXACT at every power of two, that the documented refusal answers MinValue for every
    /// non-positive raw, that both ends of the attained range are reached exactly, and that it does not step backwards
    /// across an interval-table boundary.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedLog2WithinEnvelope(long[] left, long[] right) {
        var raw = PositiveRaw(raw: left[0]);
        var actual = FixedQ4816.Log2(value: Raw(value: raw)).Value;

        if (WithinEnvelope(
            name: $"Log2({raw})",
            subjectRaw: actual,
            enclosure: Oracles.EncloseLog2(
                guardBitCount: Oracles.GuardBitCount,
                raw: raw
            ),
            toleranceUnits: UlpUnits(
                denominator: 4,
                numerator: 3
            )
        ) is { } detail) { return detail; }

        if (FixedQ4816.Log2(value: FixedQ4816.Zero).Value != long.MinValue) { return "the logarithm of zero is not MinValue"; }
        if (FixedQ4816.Log2(value: FixedQ4816.MinValue).Value != long.MinValue) { return "the logarithm of MinValue is not MinValue"; }
        if (FixedQ4816.Log2(value: Raw(value: -raw)).Value != long.MinValue) { return $"the logarithm of the negated raw {raw} is not MinValue"; }

        // The attained range, both ends. The upper one is the value the doc's half-open spelling excludes.
        if (FixedQ4816.Log2(value: FixedQ4816.Epsilon).Value != (-16L << FixedQ4816.FractionBitCount)) { return "the logarithm of epsilon is not minus sixteen"; }
        if (FixedQ4816.Log2(value: FixedQ4816.MaxValue).Value != (47L << FixedQ4816.FractionBitCount)) { return "the logarithm of MaxValue is not forty-seven"; }

        for (var exponent = -16; (exponent <= 46); ++exponent) {
            var pole = (1L << (FixedQ4816.FractionBitCount + exponent));

            if (FixedQ4816.Log2(value: Raw(value: pole)).Value != (((long)exponent) << FixedQ4816.FractionBitCount)) { return $"the logarithm is not exact at the power of two 2^{exponent}"; }
        }

        // One interval-table boundary per draw: the Q62 mantissa's top seven bits select the interval, so the raws
        // either side of a boundary are where a mis-transcribed entry shows as a step in the wrong direction.
        var index = ((long)(unchecked((ulong)left[0]) & 127UL));
        var boundary = ((1L << 62) + (index << 55));

        if (FixedQ4816.Log2(value: Raw(value: (boundary - 1L))) > FixedQ4816.Log2(value: Raw(value: boundary))) { return $"the logarithm steps backwards entering interval {index}"; }
        if (FixedQ4816.Log2(value: Raw(value: boundary)) > FixedQ4816.Log2(value: Raw(value: (boundary + 1L)))) { return $"the logarithm steps backwards leaving interval {index}"; }

        return null;
    }
    /// <summary>Proves the exponential lies inside the square-root-ladder enclosure widened by the committed two-regime
    /// envelope, that it is EXACT at every whole exponent, and that both documented gates fire exactly where the code
    /// puts them — the underflow one at −17, not at the −17.5 the doc names.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedExp2WithinEnvelope(long[] left, long[] right) {
        var raw = Exp2ExponentRaw(raw: left[0]);
        var actual = FixedQ4816.Exp2(value: Raw(value: raw)).Value;
        var enclosure = Oracles.EncloseExp2(
            scaledExponent: new BigInteger(value: raw),
            exponentBitCount: FixedQ4816.FractionBitCount,
            guardBitCount: Oracles.GuardBitCount
        );

        if (WithinEnvelope(
            name: $"Exp2({raw})",
            subjectRaw: actual,
            enclosure: enclosure,
            toleranceUnits: Exp2ToleranceUnits(high: enclosure.High)
        ) is { } detail) { return detail; }

        for (var exponent = -16; (exponent <= 46); ++exponent) {
            var expected = (1L << (FixedQ4816.FractionBitCount + exponent));

            if (FixedQ4816.Exp2(value: Raw(value: (((long)exponent) << FixedQ4816.FractionBitCount))).Value != expected) { return $"the exponential is not exact at the whole exponent {exponent}"; }
        }

        // The saturation gate, and the last exponent below it that does not take it.
        if (FixedQ4816.Exp2(value: Raw(value: (47L << FixedQ4816.FractionBitCount))) != FixedQ4816.MaxValue) { return "the exponential does not saturate at forty-seven"; }
        if (FixedQ4816.Exp2(value: FixedQ4816.MaxValue) != FixedQ4816.MaxValue) { return "the exponential does not saturate at MaxValue"; }
        if (FixedQ4816.Exp2(value: Raw(value: ((47L << FixedQ4816.FractionBitCount) - 1L))) == FixedQ4816.MaxValue) { return "the exponential saturates one epsilon below forty-seven"; }

        // The underflow gate, which is at −17 and not the documented −17.5: the true 2⁻¹⁷ is exactly half a ULP and
        // this kernel rounds half UP, so the threshold lands on Epsilon rather than on Zero.
        if (FixedQ4816.Exp2(value: Raw(value: (-17L << FixedQ4816.FractionBitCount))) != FixedQ4816.Epsilon) { return "the exponential at minus seventeen is not epsilon"; }

        foreach (var underflow in ((ReadOnlySpan<long>)[((-17L << FixedQ4816.FractionBitCount) - 1L), (-18L << FixedQ4816.FractionBitCount), (-19L << FixedQ4816.FractionBitCount), (-1L << 31), long.MinValue])) {
            if (FixedQ4816.Exp2(value: Raw(value: underflow)) != FixedQ4816.Zero) { return $"the exponential at the raw {underflow} is not zero"; }
        }

        return null;
    }
    /// <summary>Proves both circular outputs lie inside the radian-domain series enclosure widened by the committed
    /// per-regime envelope, that the two one-line projections are the pair's own components, that both outputs are
    /// clamped into the unit interval, that the Pythagorean identity holds inside the regime's own budget, that the
    /// origin is exact, and that the module's derived circle constant agrees with the published expansion.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedSinCosWithinEnvelope(long[] left, long[] right) {
        var raw = left[0];

        var (sin, cos) = FixedQ4816.SinCos(angle: Raw(value: raw));
        var enclosure = Oracles.EncloseSinCos(
            guardBitCount: Oracles.GuardBitCount,
            raw: raw
        );
        var tolerance = SinCosToleranceUnits(raw: raw);
        var one = (1L << FixedQ4816.FractionBitCount);

        if (WithinEnvelope(
            name: $"Sin({raw})",
            subjectRaw: sin.Value,
            enclosure: enclosure.Sin,
            toleranceUnits: tolerance
        ) is { } sine) { return sine; }
        if (WithinEnvelope(
            name: $"Cos({raw})",
            subjectRaw: cos.Value,
            enclosure: enclosure.Cos,
            toleranceUnits: tolerance
        ) is { } cosine) { return cosine; }
        if (
            (sin.Value < -one) ||
            (sin.Value > one)
        ) { return $"the sine at raw {raw} left the unit interval"; }
        if (
            (cos.Value < -one) ||
            (cos.Value > one)
        ) { return $"the cosine at raw {raw} left the unit interval"; }
        if (FixedQ4816.Sin(angle: Raw(value: raw)) != sin) { return $"the sine projection diverges from the pair at raw {raw}"; }
        if (FixedQ4816.Cos(angle: Raw(value: raw)) != cos) { return $"the cosine projection diverges from the pair at raw {raw}"; }

        // The Pythagorean identity, inside the regime's own budget, entirely in arbitrary width.
        var ulp = ((tolerance >> Oracles.GuardBitCount) + BigInteger.One);
        var residual = BigInteger.Abs(value: (((((BigInteger)sin.Value) * sin.Value) + (((BigInteger)cos.Value) * cos.Value)) - (BigInteger.One << 32)));

        if (residual > (((4 * one) * ulp) + ((4 * ulp) * ulp))) { return $"the Pythagorean identity fails at raw {raw} by {residual}"; }

        var (originSin, originCos) = FixedQ4816.SinCos(angle: FixedQ4816.Zero);

        if (
            (originSin != FixedQ4816.Zero) ||
            (originCos != FixedQ4816.One)
        ) { return "the pair is not exact at the origin"; }

        // The cross-check on the derived constant: a transposed Machin formula would shift every angle by a constant
        // and could otherwise hide inside a self-consistent oracle.
        var published = ((BigInteger.Parse(
            value: "3141592653589793238462643383279502884197",
            provider: CultureInfo.InvariantCulture
        ) << 128) / BigInteger.Pow(
            value: new BigInteger(value: 10),
            exponent: 39
        ));
        var circle = Oracles.Pi(bitCount: 128);

        if (
            (published < (circle.Low - 4)) ||
            (published > (circle.High + 4))
        ) { return $"the derived circle constant [{circle.Low}, {circle.High}] does not bracket the published expansion {published}"; }

        return null;
    }
    /// <summary>Proves the two-argument arctangent lies inside the alternating-series enclosure widened by the committed
    /// 0.75 raw ULP envelope, that the two exact poles are exact, that the documented half-open range is respected, that
    /// the odd symmetry and the quarter-turn corner hold, and that the both-zero gate is the only place the fold could
    /// have divided by zero.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedAtan2WithinEnvelope(long[] left, long[] right) {
        const long HalfTurnRaw = 205887L;
        const long QuarterTurnRaw = 102944L;

        var yRaw = left[0];
        var xRaw = right[0];
        var actual = FixedQ4816.Atan2(
            y: Raw(value: yRaw),
            x: Raw(value: xRaw)
        ).Value;

        if (WithinEnvelope(
            name: $"Atan2({yRaw}, {xRaw})",
            subjectRaw: actual,
            enclosure: Oracles.EncloseAtan2(
                guardBitCount: Oracles.GuardBitCount,
                xRaw: xRaw,
                yRaw: yRaw
            ),
            toleranceUnits: UlpUnits(
                denominator: 4,
                numerator: 3
            )
        ) is { } detail) { return detail; }

        // The documented range is (−π, π]. In RAW terms both endpoints are ±205887, because round(π·2¹⁶) = 205887 names
        // an angle strictly BELOW π — so the negative endpoint is attained as a raw while the value it names is still
        // greater than −π, and the range statement is the symmetric magnitude bound.
        if (
            (actual < -HalfTurnRaw) ||
            (actual > HalfTurnRaw)
        ) { return $"the angle at ({yRaw}, {xRaw}) left the documented range"; }

        // The odd symmetry, wherever the negation actually names a different ordinate. It does not at the zero ordinate,
        // where this carrier has ONE zero rather than the two an IEEE atan2 splits the negative real axis with, so the
        // whole axis maps to +π; nor at the signed minimum, whose negation is its own fixed point.
        if (
            (0L != yRaw) &&
            (long.MinValue != yRaw)
        ) {
            if (FixedQ4816.Atan2(
                y: Raw(value: -yRaw),
                x: Raw(value: xRaw)
            ).Value != -actual) { return $"the angle is not odd in the ordinate at ({yRaw}, {xRaw})"; }
        }

        if (0L != yRaw) {
            var quarter = FixedQ4816.Atan2(
                y: Raw(value: yRaw),
                x: FixedQ4816.Zero
            ).Value;

            if (quarter != ((yRaw < 0L)
                ? -QuarterTurnRaw
                : QuarterTurnRaw)) { return $"the quarter-turn corner at the ordinate {yRaw} is {quarter}"; }
        }

        if (FixedQ4816.Atan2(
            y: FixedQ4816.Zero,
            x: FixedQ4816.Zero
        ) != FixedQ4816.Zero) { return "the both-zero gate does not answer zero"; }
        if (FixedQ4816.Atan2(
            y: FixedQ4816.Zero,
            x: FixedQ4816.NegativeOne
        ).Value != HalfTurnRaw) { return "the half-turn pole is not the documented upper endpoint"; }

        return null;
    }
    /// <summary>Proves the power on the two families where its answer is EXACT rather than approximate — the
    /// power-of-two lattice, where every intermediate of the squaring schedule is exactly representable, and the small
    /// whole-exponent ladder, where a plain sequential fold in arbitrary width reaches the same value by a different
    /// schedule — and pins the four documented edge policies and the two saturation gates.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedPowExactLattice() {
        for (var scale = -16; (scale <= 46); ++scale) {
            for (var exponent = -32; (exponent <= 32); ++exponent) {
                var product = (scale * exponent);

                if (
                    (product < -16) ||
                    (product > 46)
                ) { continue; }

                var actual = FixedQ4816.Pow(
                    x: Raw(value: (1L << (FixedQ4816.FractionBitCount + scale))),
                    y: FixedQ4816.FromInteger(value: exponent)
                ).Value;

                if (actual != (1L << (FixedQ4816.FractionBitCount + product))) { return $"the power of two 2^{scale} raised to {exponent} is {actual}, expected 2^{product}"; }
            }
        }

        foreach (var start in ((ReadOnlySpan<long>)[1L, 2L, 3L, 5L, 7L, 10L, 17L, 99L, 100L])) {
            for (var exponent = 0; (exponent <= 4); ++exponent) {
                var expected = (BigInteger.Pow(
                    value: new BigInteger(value: start),
                    exponent: exponent
                ) << FixedQ4816.FractionBitCount);
                var actual = FixedQ4816.Pow(
                    x: FixedQ4816.FromInteger(value: start),
                    y: FixedQ4816.FromInteger(value: exponent)
                ).Value;

                if (actual != expected) { return $"the whole power {start}^{exponent} is {actual}, expected {expected}"; }
            }
        }

        if (FixedQ4816.Pow(
            x: FixedQ4816.Zero,
            y: FixedQ4816.Zero
        ) != FixedQ4816.One) { return "the zero base at the zero exponent is not one"; }
        if (FixedQ4816.Pow(
            x: FixedQ4816.Zero,
            y: FixedQ4816.One
        ) != FixedQ4816.Zero) { return "the zero base at a positive exponent is not zero"; }
        if (FixedQ4816.Pow(
            x: FixedQ4816.Zero,
            y: FixedQ4816.NegativeOne
        ) != FixedQ4816.MaxValue) { return "the zero base at a negative exponent is not MaxValue"; }

        foreach (var (label, baseRaw, exponentRaw, expected) in FixedNegativeBasePowers) {
            var direct = FixedQ4816.Pow(
                x: Raw(value: baseRaw),
                y: Raw(value: exponentRaw)
            ).Value;

            if (direct != expected) { return $"{label} is {direct}, expected {expected}"; }

            // The same statement through the interface the type advertises, not only through the static call: Pow is
            // IPowerFunctions<FixedQ4816>'s member, and a constrained generic call is how a caller reaches it.
            var generic = PowThrough(
                x: Raw(value: baseRaw),
                y: Raw(value: exponentRaw)
            ).Value;

            if (generic != expected) { return $"{label} through IPowerFunctions is {generic}, expected {expected}"; }
        }

        // The overflow verdict on the whole-exponent path, the underflow shortcut, and the Int128 exponent product on
        // the fractional path, which is what keeps a huge exponent from wrapping into range.
        if (FixedQ4816.Pow(
            x: Raw(value: (1L << 56)),
            y: FixedQ4816.FromInteger(value: 2L)
        ) != FixedQ4816.MaxValue) { return "a product past the top of the range did not saturate"; }
        if (FixedQ4816.Pow(
            x: FixedQ4816.Epsilon,
            y: FixedQ4816.FromInteger(value: 2L)
        ) != FixedQ4816.Zero) { return "a product below the bottom of the range did not vanish"; }
        if (FixedQ4816.Pow(
            x: FixedQ4816.FromInteger(value: 4L),
            y: Raw(value: (1L << 46))
        ) != FixedQ4816.MaxValue) { return "an exponent past the public multiplication's range did not saturate"; }

        // The exponent-one identity holds over the WHOLE range, including the top band a log-estimated overflow gate
        // once wrongly saturated — the first raw that gate answered MaxValue for was 9220202148413599469. RULED, and
        // the correction is the point.
        foreach (var identityRaw in ((ReadOnlySpan<long>)[1L, 9220202148413599469L, (long.MaxValue - 1L), long.MaxValue])) {
            if (FixedQ4816.Pow(
                x: Raw(value: identityRaw),
                y: FixedQ4816.One
            ).Value != identityRaw) { return $"the base raw {identityRaw} is not its own power at exponent one"; }
            if (FixedQ4816.Pow(
                x: Raw(value: -identityRaw),
                y: FixedQ4816.One
            ).Value != -identityRaw) { return $"the base raw {-identityRaw} is not its own power at exponent one"; }
        }

        // Overflow on the squaring path is the ladder's OWN verdict rather than a log-derived estimate: at exponent
        // two the single rounded squaring either fits — and then Pow answers exactly the multiply operator's product,
        // matched here against an independent BigInteger rounding — or leaves the carrier and saturates. The last two
        // rows bracket the exact threshold floor(√(2⁷⁹ − 2¹⁵)).
        foreach (var squareRaw in ((ReadOnlySpan<long>)[777336460312L, 777472127993L, 777472127994L])) {
            var exactSquare = Oracles.RoundRationalTiesToEven(
                numerator: (new BigInteger(value: squareRaw) * squareRaw),
                denominator: (BigInteger.One << FixedQ4816.FractionBitCount)
            );
            var expected = ((exactSquare <= long.MaxValue)
                ? ((long)exactSquare)
                : long.MaxValue
            );
            var actual = FixedQ4816.Pow(
                x: Raw(value: squareRaw),
                y: FixedQ4816.FromInteger(value: 2L)
            ).Value;

            if (actual != expected) { return $"the near-top square of raw {squareRaw} is {actual}, expected {expected}"; }
        }

        return null;
    }

    // The interface route: IPowerFunctions<T>.Pow reached through a constrained generic, so the law states the .NET
    // contract the type advertises rather than only the shape of its own static member.
    private static T PowThrough<T>(T x, T y)
        where T : IPowerFunctions<T> =>
        T.Pow(
            x: x,
            y: y
        );

    // The negative-base table, every expectation hand-derived from x^y and the 2⁻¹⁶ grid. Parity carries the sign, the
    // two saturations carry it too, and the one genuinely unsupported case — a non-whole exponent, whose real power is
    // not a real number — keeps answering Zero.
    private static readonly (string Label, long BaseRaw, long ExponentRaw, long Expected)[] FixedNegativeBasePowers = [
        ("(−2)^0", -131072L, 0L, 65536L),
        ("(−2)^1", -131072L, 65536L, -131072L),
        ("(−2)^2", -131072L, 131072L, 262144L),
        ("(−2)^3", -131072L, 196608L, -524288L),
        ("(−2)^4", -131072L, 262144L, 1048576L),
        ("(−2)^−1", -131072L, -65536L, -32768L),
        ("(−2)^−2", -131072L, -131072L, 16384L),
        ("(−2)^−3", -131072L, -196608L, -8192L),
        ("(−1)^5", -65536L, 327680L, -65536L),
        ("(−1)^6", -65536L, 393216L, 65536L),
        ("(−0.5)^3", -32768L, 196608L, -8192L),
        ("(−0.5)^−3", -32768L, -196608L, -524288L),
        ("(−2)^1.5, the unsupported non-whole exponent", -131072L, 98304L, 0L),
        ("(−4)^0.5, the unsupported non-whole exponent", -262144L, 32768L, 0L),
        ("(−2)^33, past the squaring band and onto the exponential path", -131072L, 2162688L, -562949953421312L),
        ("(−2)^34, past the squaring band, even parity", -131072L, 2228224L, 1125899906842624L),
        ("(−2)^−33, past the squaring band, underflowing", -131072L, -2162688L, 0L),
        ("(−2^40)^2, a positive overflow", -72057594037927936L, 131072L, long.MaxValue),
        ("(−2^40)^3, a NEGATIVE overflow saturating to MinValue", -72057594037927936L, 196608L, long.MinValue),
        ("(−Epsilon)^2, an underflow", -1L, 131072L, 0L),
        ("(−Epsilon)^3, an underflow whose sign the zero cannot carry", -1L, 196608L, 0L),
        ("MinValue^0", long.MinValue, 0L, 65536L),
        ("MinValue^1", long.MinValue, 65536L, long.MinValue),
        ("MinValue^2", long.MinValue, 131072L, long.MaxValue),
        ("MinValue^3", long.MinValue, 196608L, long.MinValue),
        ("MinValue^−1", long.MinValue, -65536L, 0L),
    ];

    /// <summary>Proves the power's FRACTIONAL path — the exponential of the Q16-rounded product of the exponent and the
    /// subject's own logarithm — lies inside the enclosure of the true value widened by the DERIVED envelope: the
    /// exponent carries at most (3·|yRaw| + 2¹⁷)/2³⁴ of error from the logarithm's 0.75 ULP and the Q16 quantization,
    /// which scales the result by at most that factor, and the exponential contributes its own documented
    /// envelope.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedPowWithinEnvelope(long[] left, long[] right) {
        var baseRaw = PositiveRaw(raw: left[0]);
        var logarithm = Oracles.EncloseLog2(
            guardBitCount: Oracles.GuardBitCount,
            raw: baseRaw
        );
        var magnitude = (BigInteger.Max(
            left: BigInteger.Abs(value: logarithm.Low),
            right: BigInteger.Abs(value: logarithm.High)
        ) >> (FixedQ4816.FractionBitCount + Oracles.GuardBitCount));
        var limit = ((15L << FixedQ4816.FractionBitCount) / (((long)magnitude) + 1L));
        var exponentRaw = (((long)(unchecked((ulong)right[0]) % ((ulong)(2L * limit)))) - limit);

        // The band keeps |y·log₂ x| under sixteen, so neither saturation gate fires and the derived error factor stays
        // far below the half at which the bound 2^ε − 1 ≤ ε would stop holding. The fraction bits are forced non-zero
        // so the subject takes the exponential path rather than the whole-exponent squaring one, which
        // scalar.pow-exact-lattice owns.
        if (0L == (exponentRaw & 0xFFFFL)) { exponentRaw += 1L; }

        var actual = FixedQ4816.Pow(
            x: Raw(value: baseRaw),
            y: Raw(value: exponentRaw)
        ).Value;
        var first = (logarithm.Low * exponentRaw);
        var second = (logarithm.High * exponentRaw);
        var low = Oracles.EncloseExp2(
            scaledExponent: (BigInteger.Min(
                left: first,
                right: second
            ) >> 32),
            exponentBitCount: 32,
            guardBitCount: Oracles.GuardBitCount
        ).Low;
        var high = Oracles.EncloseExp2(
            scaledExponent: (-((-BigInteger.Max(
                left: first,
                right: second
            )) >> 32)),
            exponentBitCount: 32,
            guardBitCount: Oracles.GuardBitCount
        ).High;
        var quantization = ((high * ((3 * BigInteger.Abs(value: new BigInteger(value: exponentRaw))) + (BigInteger.One << 17))) >> 34);

        return WithinEnvelope(
            name: $"Pow({baseRaw}, {exponentRaw})",
            subjectRaw: actual,
            enclosure: new Oracles.Enclosure(
                High: high,
                Low: low
            ),
            toleranceUnits: (quantization + Exp2ToleranceUnits(high: high))
        );
    }
    /// <summary>The Deep mirror of the five transcendental envelopes on one operand stream: the square root's exact
    /// floor, the logarithm's and the exponential's enclosures, the circular pair's three-band envelope, and the
    /// arctangent's — each the same statement its Default case makes, at the exhaustive edge cross product and the deep
    /// tier's sixteen-fold random batch.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedTranscendentalDeepSweep(long[] left, long[] right) {
        var raw = left[0];
        var logarithmRaw = PositiveRaw(raw: raw);
        var exponentRaw = Exp2ExponentRaw(raw: raw);
        var exponential = Oracles.EncloseExp2(
            scaledExponent: new BigInteger(value: exponentRaw),
            exponentBitCount: FixedQ4816.FractionBitCount,
            guardBitCount: Oracles.GuardBitCount
        );
        var circular = Oracles.EncloseSinCos(
            guardBitCount: Oracles.GuardBitCount,
            raw: raw
        );
        var tolerance = SinCosToleranceUnits(raw: raw);

        var (sin, cos) = FixedQ4816.SinCos(angle: Raw(value: raw));

        if (FixedSqrtAt(raw: raw) is { } root) { return root; }

        if (WithinEnvelope(
            name: $"Log2({logarithmRaw})",
            subjectRaw: FixedQ4816.Log2(value: Raw(value: logarithmRaw)).Value,
            enclosure: Oracles.EncloseLog2(
                guardBitCount: Oracles.GuardBitCount,
                raw: logarithmRaw
            ),
            toleranceUnits: UlpUnits(
                denominator: 4,
                numerator: 3
            )
        ) is { } logarithm) { return logarithm; }

        if (WithinEnvelope(
            name: $"Exp2({exponentRaw})",
            subjectRaw: FixedQ4816.Exp2(value: Raw(value: exponentRaw)).Value,
            enclosure: exponential,
            toleranceUnits: Exp2ToleranceUnits(high: exponential.High)
        ) is { } exponentiation) { return exponentiation; }

        if (WithinEnvelope(
            name: $"Sin({raw})",
            subjectRaw: sin.Value,
            enclosure: circular.Sin,
            toleranceUnits: tolerance
        ) is { } sine) { return sine; }
        if (WithinEnvelope(
            name: $"Cos({raw})",
            subjectRaw: cos.Value,
            enclosure: circular.Cos,
            toleranceUnits: tolerance
        ) is { } cosine) { return cosine; }
        if (FixedQ4816.Sin(angle: Raw(value: raw)) != sin) { return $"the sine projection diverges from the pair at raw {raw}"; }
        if (FixedQ4816.Cos(angle: Raw(value: raw)) != cos) { return $"the cosine projection diverges from the pair at raw {raw}"; }

        return WithinEnvelope(
            name: $"Atan2({raw}, {right[0]})",
            subjectRaw: FixedQ4816.Atan2(
                y: Raw(value: raw),
                x: Raw(value: right[0])
            ).Value,
            enclosure: Oracles.EncloseAtan2(
                yRaw: raw,
                xRaw: right[0],
                guardBitCount: Oracles.GuardBitCount
            ),
            toleranceUnits: UlpUnits(
                denominator: 4,
                numerator: 3
            )
        );
    }

}
