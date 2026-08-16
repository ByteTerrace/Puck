using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>The exact scaling step the fixed-point carriers' generic conversions are defined on: the truncating mode
/// for every recognized source, and all three modes for a <see cref="decimal"/> source.</summary>
/// <remarks>
/// <see cref="INumberBase{TSelf}.CreateTruncating{TOther}(TOther)"/> is width truncation, not range clamping: the
/// source's value is expressed at the target's scale and then reduced to the target's own width. Routing a source
/// through <see cref="decimal"/> and clamping the result collapses that into saturation, which is what this helper
/// exists to avoid. It produces the scaled value as an <see cref="Int128"/> — 128 bits is always enough, because bits
/// above <c>2¹²⁸</c> cannot reach a sixty-four-bit raw — and the caller reduces to its own carrier with an unchecked
/// cast. The rounding of a fractional source is unchanged: one ties-to-even rounding, here taken exactly on the
/// rational the source's own bits name rather than through a decimal multiply that can round twice. The checked and
/// saturating decimal lanes share <see cref="ScaleDecimal"/> for the same reason, applying their own range policy to
/// its single rounding.
/// </remarks>
internal static class FixedPointConvert {
    /// <summary>Determines whether <typeparamref name="TOther"/> is a BCL integer this helper converts without a
    /// decimal.</summary>
    /// <typeparam name="TOther">The type to test.</typeparam>
    /// <returns>Whether the type is one of the recognized integers.</returns>
    /// <remarks>The TO side needs the same test: handing an integer target its value through
    /// <see cref="decimal"/> would let decimal's own conversion — which saturates even in truncating mode, because
    /// decimal is not a fixed-width binary type — decide a range question that belongs to the target.</remarks>
    internal static bool IsKnownBclInteger<TOther>()
        where TOther : INumberBase<TOther> =>
        ((typeof(TOther) == typeof(byte)) || (typeof(TOther) == typeof(sbyte)) ||
        (typeof(TOther) == typeof(short)) || (typeof(TOther) == typeof(ushort)) ||
        (typeof(TOther) == typeof(int)) || (typeof(TOther) == typeof(uint)) ||
        (typeof(TOther) == typeof(long)) || (typeof(TOther) == typeof(ulong)) ||
        (typeof(TOther) == typeof(nint)) || (typeof(TOther) == typeof(nuint)) ||
        (typeof(TOther) == typeof(Int128)) || (typeof(TOther) == typeof(UInt128)) ||
        (typeof(TOther) == typeof(char)) || (typeof(TOther) == typeof(BigInteger)));
    /// <summary>Determines whether <typeparamref name="TOther"/> is a BCL numeric the carriers' conversion hooks
    /// recognize.</summary>
    /// <typeparam name="TOther">The type to test.</typeparam>
    /// <returns>Whether the type is a recognized integer, <see cref="decimal"/>, or <see cref="Half"/>.</returns>
    /// <remarks>The hooks route everything this admits through <see cref="decimal"/>, so a type it rejects is one the
    /// hook must refuse rather than convert.</remarks>
    internal static bool IsKnownBclNumeric<TOther>()
        where TOther : INumberBase<TOther> =>
        (IsKnownBclInteger<TOther>() ||
        (typeof(TOther) == typeof(decimal)) || (typeof(TOther) == typeof(Half)));
    /// <summary>Scales a <see cref="decimal"/> to a fixed-point raw, exactly and without a range clamp.</summary>
    /// <param name="value">The value to scale.</param>
    /// <param name="fractionBitCount">The target carrier's fraction bit count. Must be at most <c>31</c>: the worst
    /// case — a scale-zero mantissa of <c>2⁹⁶ − 1</c> over a denominator of one — puts the quotient at
    /// <c>2¹²⁷ − 2³¹</c>, the widest value the unchecked <see cref="Int128"/> narrowing below can carry (the ties
    /// increment cannot fire at denominator one). At thirty-two that same mantissa wraps the narrowing into a
    /// sign-flipped magnitude. Every current caller passes sixteen.</param>
    /// <returns>One ties-to-even rounding of <c>value·2^fractionBitCount</c>.</returns>
    /// <remarks>The decimal's own bits name an exact rational: a ninety-six-bit mantissa over a power of ten. Scaling
    /// it by <c>2^fractionBitCount</c> keeps the numerator under <c>2^(96 + fractionBitCount)</c> and the denominator
    /// under <c>2⁹⁴</c>, so within the stated fraction-bit bound the whole division and its ties-to-even repair run in
    /// <see cref="UInt128"/> with nothing to overflow and nothing rounded twice. All three conversion modes rest on
    /// this one rounding: a <c>decimal</c> multiply would rescale — and round — first whenever the product needs more
    /// than ninety-six mantissa bits, and a second rounding can then resolve a manufactured tie off the true value's
    /// side. The caller applies its own range policy: refusal, clamp, or width reduction.</remarks>
    internal static Int128 ScaleDecimal(decimal value, int fractionBitCount) {
        Span<int> bits = stackalloc int[4];

        _ = decimal.GetBits(
            d: value,
            destination: bits
        );

        var numerator = (((((UInt128)((uint)bits[2])) << 64) |
                          (((UInt128)((uint)bits[1])) << 32) |
                          ((UInt128)((uint)bits[0]))) << fractionBitCount);
        var denominator = UInt128.One;

        for (var index = (bits[3] >> 16) & 0xFF; (index > 0); --index) {
            denominator *= 10U;
        }

        var quotient = (numerator / denominator);
        var twiceRemainder = ((numerator - (quotient * denominator)) << 1);

        if (
            (twiceRemainder > denominator) ||
            (
                (twiceRemainder == denominator) &&
                (UInt128.Zero != (quotient & UInt128.One))
            )
        ) {
            ++quotient;
        }

        var magnitude = ((Int128)quotient);

        return ((bits[3] < 0)
            ? -magnitude
            : magnitude
        );
    }
    /// <summary>Scales a <see cref="decimal"/> to a fixed-point raw at an arbitrary fraction bit count, exactly and
    /// without a range clamp, using <see cref="BigInteger"/> for the wide intermediate.</summary>
    /// <param name="value">The value to scale.</param>
    /// <param name="fractionBitCount">The target carrier's fraction bit count. Unlike <see cref="ScaleDecimal"/>,
    /// there is no upper bound on this parameter: the worst case (a scale-zero, ninety-six-bit mantissa scaled by a
    /// forty-eight-bit shift) needs up to a hundred and forty-four bits, past what <see cref="UInt128"/> can hold
    /// exactly, so this overload trades <see cref="UInt128"/>'s allocation-free width for
    /// <see cref="BigInteger"/>'s unbounded one. Reach for <see cref="ScaleDecimal"/> instead whenever
    /// <paramref name="fractionBitCount"/> is known to be at most thirty-one — its hot, allocation-free path is
    /// exactly this same algorithm at that narrower width.</param>
    /// <returns>One ties-to-even rounding of <c>value·2^fractionBitCount</c>, exactly — <see cref="BigInteger"/>
    /// carries the full width, so nothing here can wrap the way a fixed-width intermediate would.</returns>
    /// <remarks>This is the decimal boundary for a carrier whose fraction bit count exceeds
    /// <see cref="ScaleDecimal"/>'s thirty-one-bit cap — <see cref="FixedQ1648"/> (forty-eight) and
    /// <see cref="FixedQ3232"/> (thirty-two) today: past that cap <see cref="ScaleDecimal"/>'s <see cref="UInt128"/>
    /// intermediate is not wide enough to stay exact, and reusing it anyway would silently wrap instead of throwing
    /// — a correctness bug, not a documented policy. This overload is not on either Q48.16 carrier's hot path and is
    /// not called by either of them.</remarks>
    internal static BigInteger ScaleDecimalWide(decimal value, int fractionBitCount) {
        Span<int> bits = stackalloc int[4];

        _ = decimal.GetBits(
            d: value,
            destination: bits
        );

        var mantissa = (((BigInteger)((uint)bits[2])) << 64) |
                        (((BigInteger)((uint)bits[1])) << 32) |
                        ((BigInteger)((uint)bits[0]));
        var numerator = (mantissa << fractionBitCount);
        var denominator = BigInteger.Pow(
            value: 10,
            exponent: (bits[3] >> 16) & 0xFF
        );
        var quotient = BigInteger.DivRem(
            dividend: numerator,
            divisor: denominator,
            remainder: out var remainder
        );
        var twiceRemainder = (remainder << 1);

        if (
            (twiceRemainder > denominator) ||
            ((twiceRemainder == denominator) && !quotient.IsEven)
        ) {
            ++quotient;
        }

        return ((bits[3] < 0)
            ? -quotient
            : quotient
        );
    }
    /// <summary>Implements the checked inbound generic-math conversion shared by the signed fixed-point carriers.</summary>
    internal static bool TryConvertFromChecked<TSelf, TOther>(TOther value, int fractionBitCount, out TSelf result)
        where TSelf : struct, INumberBase<TSelf>
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(TSelf)) {
            result = Unsafe.As<TOther, TSelf>(source: ref value);

            return true;
        }

        if (TryConvertPeerFromChecked(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (TryGetFloating(
            result: out var floating,
            value: value
        )) {
            result = FromDoubleChecked<TSelf>(
                fractionBitCount: fractionBitCount,
                value: floating
            );

            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default;

            return false;
        }

        try {
            result = FromDecimalChecked<TSelf>(
                value: decimal.CreateChecked(value: value),
                fractionBitCount: fractionBitCount
            );

            return true;
        } catch (NotSupportedException) {
            result = default;

            return false;
        }
    }
    /// <summary>Implements the saturating inbound generic-math conversion shared by the signed fixed-point carriers.</summary>
    internal static bool TryConvertFromSaturating<TSelf, TOther>(TOther value, int fractionBitCount, out TSelf result)
        where TSelf : struct, INumberBase<TSelf>
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(TSelf)) {
            result = Unsafe.As<TOther, TSelf>(source: ref value);

            return true;
        }

        if (TryConvertPeerFromSaturating(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (TryGetFloating(
            result: out var floating,
            value: value
        )) {
            result = FromDouble<TSelf>(value: floating);

            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default;

            return false;
        }

        try {
            result = FromDecimalSaturating<TSelf>(
                value: decimal.CreateSaturating(value: value),
                fractionBitCount: fractionBitCount
            );

            return true;
        } catch (NotSupportedException) {
            result = default;

            return false;
        }
    }
    /// <summary>Implements the truncating inbound generic-math conversion shared by the signed fixed-point carriers.</summary>
    internal static bool TryConvertFromTruncating<TSelf, TOther>(TOther value, int fractionBitCount, out TSelf result)
        where TSelf : struct, INumberBase<TSelf>
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(TSelf)) {
            result = Unsafe.As<TOther, TSelf>(source: ref value);

            return true;
        }

        if (TryConvertPeerFromTruncating(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (TryGetFloating(
            result: out var floating,
            value: value
        )) {
            result = FromDouble<TSelf>(value: floating);

            return true;
        }

        if (typeof(TOther) == typeof(decimal)) {
            result = FromDecimalTruncating<TSelf>(
                value: Unsafe.As<TOther, decimal>(source: ref value),
                fractionBitCount: fractionBitCount
            );

            return true;
        }

        if (TryScaleTruncating(
            value: value,
            fractionBitCount: fractionBitCount,
            out var scaled
        )) {
            result = FromRaw<TSelf>(raw: unchecked((long)scaled));

            return true;
        }

        result = default;

        return false;
    }
    /// <summary>Implements the checked outbound generic-math conversion shared by the signed fixed-point carriers.</summary>
    internal static bool TryConvertToChecked<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct, INumberBase<TSelf>
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(TSelf)) {
            result = Unsafe.As<TSelf, TOther>(source: ref value);

            return true;
        }

        if (TryConvertPeerToChecked(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (TrySetFloating(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default!;

            return false;
        }

        try {
            result = TOther.CreateChecked(value: ToDecimal(
                fractionBitCount: fractionBitCount,
                value: value
            ));

            return true;
        } catch (NotSupportedException) {
            result = default!;

            return false;
        }
    }
    /// <summary>Implements the saturating outbound generic-math conversion shared by the signed fixed-point carriers.</summary>
    internal static bool TryConvertToSaturating<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct, INumberBase<TSelf>
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(TSelf)) {
            result = Unsafe.As<TSelf, TOther>(source: ref value);

            return true;
        }

        if (TryConvertPeerToSaturating(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (TrySetFloating(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default!;

            return false;
        }

        try {
            result = TOther.CreateSaturating(value: ToDecimal(
                fractionBitCount: fractionBitCount,
                value: value
            ));

            return true;
        } catch (NotSupportedException) {
            result = default!;

            return false;
        }
    }
    /// <summary>Implements the truncating outbound generic-math conversion shared by the signed fixed-point carriers.</summary>
    internal static bool TryConvertToTruncating<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct, INumberBase<TSelf>
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(TSelf)) {
            result = Unsafe.As<TSelf, TOther>(source: ref value);

            return true;
        }

        if (TryConvertPeerToTruncating(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (IsKnownBclInteger<TOther>()) {
            result = TOther.CreateTruncating(value: ((Int128)(Raw(value: value) / (1L << fractionBitCount))));

            return true;
        }

        if (TrySetFloating(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        )) {
            return true;
        }

        if (!IsKnownBclNumeric<TOther>()) {
            result = default!;

            return false;
        }

        try {
            result = TOther.CreateTruncating(value: ToDecimal(
                fractionBitCount: fractionBitCount,
                value: value
            ));

            return true;
        } catch (NotSupportedException) {
            result = default!;

            return false;
        }
    }
    /// <summary>Widens a BCL binary floating-point source to <see cref="double"/>.</summary>
    /// <typeparam name="TOther">The source type.</typeparam>
    /// <param name="value">The value to widen.</param>
    /// <param name="result">The widened value, or zero when the source is not a binary floating-point type.</param>
    /// <returns>Whether the source is <see cref="double"/>, <see cref="float"/>, or <see cref="Half"/>.</returns>
    internal static bool TryGetFloating<TOther>(TOther value, out double result)
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(double)) {
            result = Unsafe.As<TOther, double>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(float)) {
            result = Unsafe.As<TOther, float>(source: ref value);

            return true;
        }

        if (typeof(TOther) == typeof(Half)) {
            result = ((double)Unsafe.As<TOther, Half>(source: ref value));

            return true;
        }

        result = default;

        return false;
    }
    /// <summary>Scales a known BCL numeric to a fixed-point raw, exactly and without a range clamp.</summary>
    /// <typeparam name="TOther">The source type.</typeparam>
    /// <param name="value">The value to scale.</param>
    /// <param name="fractionBitCount">The target carrier's fraction bit count. Must be at most <c>31</c> for a
    /// <see cref="decimal"/> source; see <see cref="ScaleDecimal"/>.</param>
    /// <param name="scaled">One ties-to-even rounding of <c>value·2^fractionBitCount</c>, reduced to 128 bits.</param>
    /// <returns>Whether the source is a type this helper recognizes.</returns>
    internal static bool TryScaleTruncating<TOther>(TOther value, int fractionBitCount, out Int128 scaled)
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(decimal)) {
            scaled = ScaleDecimal(
                value: Unsafe.As<TOther, decimal>(source: ref value),
                fractionBitCount: fractionBitCount
            );

            return true;
        }

        if (IsKnownBclInteger<TOther>()) {
            // Int128.CreateTruncating keeps the low 128 bits of an arbitrarily wide source, and the shift keeps the
            // low 128 bits of the scaled value. Neither discards a bit the target's sixty-four-bit raw could see.
            scaled = (Int128.CreateTruncating(value: value) << fractionBitCount);

            return true;
        }

        scaled = Int128.Zero;

        return false;
    }

    private static TSelf FromDecimalChecked<TSelf>(decimal value, int fractionBitCount)
        where TSelf : struct {
        var scaled = ScaleDecimalForSignedCarrier(
            fractionBitCount: fractionBitCount,
            value: value
        );

        if (
            (scaled < long.MinValue) ||
            (scaled > long.MaxValue)
        ) {
            throw new OverflowException(message: $"Value is outside the representable {typeof(TSelf).Name} range.");
        }

        return FromRaw<TSelf>(raw: ((long)scaled));
    }
    private static TSelf FromDecimalSaturating<TSelf>(decimal value, int fractionBitCount)
        where TSelf : struct {
        var scaled = ScaleDecimalForSignedCarrier(
            fractionBitCount: fractionBitCount,
            value: value
        );

        if (scaled < long.MinValue) { return FromRaw<TSelf>(raw: long.MinValue); }
        if (scaled > long.MaxValue) { return FromRaw<TSelf>(raw: long.MaxValue); }

        return FromRaw<TSelf>(raw: ((long)scaled));
    }
    private static TSelf FromDecimalTruncating<TSelf>(decimal value, int fractionBitCount)
        where TSelf : struct {
        var scaled = ScaleDecimalForSignedCarrier(
            fractionBitCount: fractionBitCount,
            value: value
        );
        var wrapped = scaled & ulong.MaxValue;

        return FromRaw<TSelf>(raw: unchecked((long)((ulong)wrapped)));
    }
    private static TSelf FromDouble<TSelf>(double value)
        where TSelf : struct {
        if (typeof(TSelf) == typeof(FixedQ4816)) {
            var converted = FixedQ4816.FromDouble(value: value);

            return Unsafe.As<FixedQ4816, TSelf>(source: ref converted);
        }

        if (typeof(TSelf) == typeof(FixedQ1648)) {
            var converted = FixedQ1648.FromDouble(value: value);

            return Unsafe.As<FixedQ1648, TSelf>(source: ref converted);
        }

        var q3232 = FixedQ3232.FromDouble(value: value);

        return Unsafe.As<FixedQ3232, TSelf>(source: ref q3232);
    }
    private static TSelf FromDoubleChecked<TSelf>(double value, int fractionBitCount)
        where TSelf : struct {
        var scaled = double.Round(
            mode: MidpointRounding.ToEven,
            x: (value * (1L << fractionBitCount))
        );

        if (
            double.IsNaN(d: scaled) ||
            (scaled < -9223372036854775808d) ||
            (scaled > 9223372036854774784d)
        ) {
            throw new OverflowException(message: $"Value is outside the representable {typeof(TSelf).Name} range.");
        }

        return FromRaw<TSelf>(raw: ((scaled <= -9223372036854775808d)
            ? long.MinValue
            : ((long)scaled)));
    }
    private static TSelf FromRaw<TSelf>(long raw)
        where TSelf : struct =>
        Unsafe.As<long, TSelf>(source: ref raw);
    private static long Raw<TSelf>(TSelf value)
        where TSelf : struct =>
        Unsafe.As<TSelf, long>(source: ref value);
    private static BigInteger ScaleDecimalForSignedCarrier(decimal value, int fractionBitCount) =>
        ((fractionBitCount <= 31)
            ? ScaleDecimal(
                fractionBitCount: fractionBitCount,
                value: value
            )
            : ScaleDecimalWide(
                fractionBitCount: fractionBitCount,
                value: value
            )
        );
    private static decimal ToDecimal<TSelf>(TSelf value, int fractionBitCount)
        where TSelf : struct =>
        (Raw(value: value) / ((decimal)(1L << fractionBitCount)));
    private static bool TryConvertPeerFromChecked<TSelf, TOther>(TOther value, int fractionBitCount, out TSelf result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (
            (typeof(TSelf) == typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(UFixedQ4816))
        ) {
            var other = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            result = FromRaw<TSelf>(raw: checked((long)other.Value));

            return true;
        }

        if (
            (typeof(TSelf) != typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(FixedQ4816))
        ) {
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);
            var widened = (((Int128)other.Value) << (fractionBitCount - FixedQ4816.FractionBitCount));

            if (
                (widened < long.MinValue) ||
                (widened > long.MaxValue)
            ) {
                throw new OverflowException(message: $"The {nameof(FixedQ4816)} value {other} is outside the representable {typeof(TSelf).Name} range.");
            }

            result = FromRaw<TSelf>(raw: ((long)widened));

            return true;
        }

        result = default;

        return false;
    }
    private static bool TryConvertPeerFromSaturating<TSelf, TOther>(TOther value, int fractionBitCount, out TSelf result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (
            (typeof(TSelf) == typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(UFixedQ4816))
        ) {
            var other = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            result = FromRaw<TSelf>(raw: ((other.Value > long.MaxValue)
                ? long.MaxValue
                : ((long)other.Value)));

            return true;
        }

        if (
            (typeof(TSelf) != typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(FixedQ4816))
        ) {
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);
            var widened = (((Int128)other.Value) << (fractionBitCount - FixedQ4816.FractionBitCount));

            result = FromRaw<TSelf>(raw: ((widened < long.MinValue)
                ? long.MinValue
                : ((widened > long.MaxValue)
                    ? long.MaxValue
                    : ((long)widened))));

            return true;
        }

        result = default;

        return false;
    }
    private static bool TryConvertPeerFromTruncating<TSelf, TOther>(TOther value, int fractionBitCount, out TSelf result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (
            (typeof(TSelf) == typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(UFixedQ4816))
        ) {
            var other = Unsafe.As<TOther, UFixedQ4816>(source: ref value);

            result = FromRaw<TSelf>(raw: unchecked((long)other.Value));

            return true;
        }

        if (
            (typeof(TSelf) != typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(FixedQ4816))
        ) {
            var other = Unsafe.As<TOther, FixedQ4816>(source: ref value);
            var widened = (((Int128)other.Value) << (fractionBitCount - FixedQ4816.FractionBitCount));

            result = FromRaw<TSelf>(raw: unchecked((long)widened));

            return true;
        }

        result = default;

        return false;
    }
    private static bool TryConvertPeerToChecked<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (
            (typeof(TSelf) == typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(UFixedQ4816))
        ) {
            var converted = new UFixedQ4816(Value: checked((ulong)Raw(value: value)));

            result = Unsafe.As<UFixedQ4816, TOther>(source: ref converted);

            return true;
        }

        return TryConvertWidePeerToQ4816(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        );
    }
    private static bool TryConvertPeerToSaturating<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (
            (typeof(TSelf) == typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(UFixedQ4816))
        ) {
            var raw = Raw(value: value);
            var converted = new UFixedQ4816(Value: ((raw < 0L)
                ? 0UL
                : ((ulong)raw)));

            result = Unsafe.As<UFixedQ4816, TOther>(source: ref converted);

            return true;
        }

        return TryConvertWidePeerToQ4816(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        );
    }
    private static bool TryConvertPeerToTruncating<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (
            (typeof(TSelf) == typeof(FixedQ4816)) &&
            (typeof(TOther) == typeof(UFixedQ4816))
        ) {
            var converted = new UFixedQ4816(Value: unchecked((ulong)Raw(value: value)));

            result = Unsafe.As<UFixedQ4816, TOther>(source: ref converted);

            return true;
        }

        return TryConvertWidePeerToQ4816(
            fractionBitCount: fractionBitCount,
            result: out result,
            value: value
        );
    }
    private static bool TryConvertWidePeerToQ4816<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (
            (typeof(TSelf) == typeof(FixedQ4816)) ||
            (typeof(TOther) != typeof(FixedQ4816))
        ) {
            result = default!;

            return false;
        }

        var shift = (fractionBitCount - FixedQ4816.FractionBitCount);
        var sign = (Raw(value: value) >> 63);
        var magnitude = unchecked((ulong)((Raw(value: value) ^ sign) - sign));
        var truncated = (magnitude >> shift);
        var remainder = magnitude & ((1UL << shift) - 1UL);
        var rounded = FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: (1UL << (shift - 1)),
            truncated: truncated
        );
        var narrowedRaw = unchecked((long)rounded);
        var converted = FixedQ4816.FromRawBits(value: ((sign != 0L)
            ? unchecked(-narrowedRaw)
            : narrowedRaw));

        result = Unsafe.As<FixedQ4816, TOther>(source: ref converted);

        return true;
    }
    private static bool TrySetFloating<TSelf, TOther>(TSelf value, int fractionBitCount, out TOther result)
        where TSelf : struct
        where TOther : INumberBase<TOther> {
        if (typeof(TOther) == typeof(double)) {
            var wide = (Raw(value: value) * (1d / (1L << fractionBitCount)));

            result = Unsafe.As<double, TOther>(source: ref wide);

            return true;
        }

        if (typeof(TOther) == typeof(float)) {
            var single = MathF.ScaleB(
                x: ((float)Raw(value: value)),
                n: -fractionBitCount
            );

            result = Unsafe.As<float, TOther>(source: ref single);

            return true;
        }

        result = default!;

        return false;
    }
}
