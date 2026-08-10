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

        _ = decimal.GetBits(d: value, destination: bits);

        var mantissa = ((((BigInteger)(uint)bits[2]) << 64) |
                        (((BigInteger)(uint)bits[1]) << 32) |
                        ((BigInteger)(uint)bits[0]));
        var numerator = (mantissa << fractionBitCount);
        var denominator = BigInteger.Pow(value: 10, exponent: ((bits[3] >> 16) & 0xFF));
        var quotient = BigInteger.DivRem(dividend: numerator, divisor: denominator, remainder: out var remainder);
        var twiceRemainder = (remainder << 1);

        if (
            (twiceRemainder > denominator) ||
            ((twiceRemainder == denominator) && !quotient.IsEven)
        ) {
            ++quotient;
        }

        return ((bits[3] < 0) ? -quotient : quotient);
    }

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

        _ = decimal.GetBits(d: value, destination: bits);

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

        return ((bits[3] < 0) ? -magnitude : magnitude);
    }
}
