using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Module 2 — reference arithmetic that shares nothing with the subject BY CONSTRUCTION: every value is computed in
/// <see cref="BigInteger"/> with no call into any Puck.Maths kernel — or, where the value PROVABLY cannot leave the
/// carrier and the bound is stated at the member, in exact fixed-width integer arithmetic, which
/// <see cref="BigInteger"/> would only slow down: <see cref="PrimeSieve(int)"/>,
/// <see cref="ExactPrimality(ulong)"/>'s trial-division screen and the perfect-square bisection behind
/// <see cref="StrongLucasSelfridge(ulong)"/> are where that licence is taken, and each states its own bound. The single
/// home of oracle logic. Oracles may
/// share the dyadic rounding/wrap primitives with EACH OTHER; none shares code with the subject. Each returned raw is
/// ONE ties-to-even rounding of the exact rational value of the ideal expression at the ideal scale, wrapped to the
/// signed or the unsigned 64-bit carrier — the same discipline the fused fixed-point kernels implement, re-derived here
/// independently. The odd-characteristic references at the foot of the module round NOWHERE and say so; the discipline
/// below is about the fixed-point faces. The tie rule and the carrier reduction each have ONE home here
/// (<see cref="RoundRationalTiesToEven"/> and <see cref="WrapToUnsignedRaw"/>): every dyadic and rational rounding face
/// calls into them, so the signed and unsigned carriers cannot drift apart. The two half-open unit-fraction oracles are
/// the exception and say so at their own declarations — each folds the rule into a clamp or a refusal decision in one
/// pass, which is the contract those subjects actually carry.
/// </summary>
internal static class Oracles {
    private static readonly BigInteger TwoTo64 = (BigInteger.One << 64);

    /// <summary>The shared-nothing contribution-fold reference: every addition and clamp is performed in
    /// <see cref="BigInteger"/>, and terminal quantization is a direct integer comparison.</summary>
    /// <param name="baselineRaw">The baseline raw.</param>
    /// <param name="poolDeltaRaw">The pooled raw delta sum.</param>
    /// <param name="outsidePoolDeltaRaw">The outside-pool raw delta sum.</param>
    /// <param name="poolRadiusRaw">The optional non-negative pool radius raw.</param>
    /// <param name="minimumRaw">The final range minimum raw.</param>
    /// <param name="maximumRaw">The final range maximum raw.</param>
    /// <param name="thresholdRaw">The optional terminal threshold raw.</param>
    /// <returns>The result raw and whether the pool changed its intermediate.</returns>
    /// <remarks>No subject type, subject helper, fixed-width intermediate, or fixed-point operator is used here. The
    /// caller supplies only valid configurations; configuration refusals are a separate structural law.</remarks>
    public static (long ResultRaw, bool PoolClamped) FixedContributionFold(
        long baselineRaw,
        long poolDeltaRaw,
        long outsidePoolDeltaRaw,
        long? poolRadiusRaw,
        long minimumRaw,
        long maximumRaw,
        long? thresholdRaw
    ) {
        var baseline = new BigInteger(value: baselineRaw);
        var rawPooled = (baseline + poolDeltaRaw);
        var pooled = poolRadiusRaw is { } radius
            ? BigInteger.Clamp(value: rawPooled, min: (baseline - radius), max: (baseline + radius))
            : rawPooled;
        var ranged = BigInteger.Clamp(value: (pooled + outsidePoolDeltaRaw), min: minimumRaw, max: maximumRaw);
        var result = thresholdRaw is { } threshold
            ? ((ranged >= threshold) ? new BigInteger(value: maximumRaw) : new BigInteger(value: minimumRaw))
            : ranged;

        return (ResultRaw: ((long)result), PoolClamped: (pooled != rawPooled));
    }

    /// <summary>The direct baseline-zero, no-pool specialization: clamp the exact sum of both raw deltas once, then
    /// optionally quantize it.</summary>
    /// <param name="poolDeltaRaw">The first raw delta sum.</param>
    /// <param name="outsidePoolDeltaRaw">The second raw delta sum.</param>
    /// <param name="minimumRaw">The final range minimum raw.</param>
    /// <param name="maximumRaw">The final range maximum raw.</param>
    /// <param name="thresholdRaw">The optional terminal threshold raw.</param>
    /// <returns>The specialized result raw.</returns>
    /// <remarks>This is intentionally not expressed by calling <see cref="FixedContributionFold"/>: the specialization
    /// law needs a direct formula rather than a second route through its general oracle.</remarks>
    public static long FixedContributionFoldNoPool(long poolDeltaRaw, long outsidePoolDeltaRaw, long minimumRaw, long maximumRaw, long? thresholdRaw) {
        var ranged = BigInteger.Clamp(
            value: (new BigInteger(value: poolDeltaRaw) + outsidePoolDeltaRaw),
            min: minimumRaw,
            max: maximumRaw
        );
        var result = thresholdRaw is { } threshold
            ? ((ranged >= threshold) ? new BigInteger(value: maximumRaw) : new BigInteger(value: minimumRaw))
            : ranged;

        return ((long)result);
    }

    /// <summary>Rounds an exact dyadic value <c>exact / 2^shift</c> to the nearest raw, ties to even, then wraps to the
    /// signed 64-bit carrier. Rounds the magnitude and re-applies the sign (symmetric ties-to-even).</summary>
    /// <param name="exact">The exact numerator.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The rounded, wrapped raw.</returns>
    /// <remarks>The dyadic face of <see cref="RoundRationalTiesToEven"/>, which is the module's ONE ties-to-even body:
    /// stating the rule twice would let the two spellings drift apart while every law stayed green.</remarks>
    public static long RoundDyadic(BigInteger exact, int shift) =>
        WrapToRaw(value: RoundRationalTiesToEven(numerator: exact, denominator: (BigInteger.One << shift)));

    /// <summary>The exact quotient rounded toward negative infinity, in arbitrary width.</summary>
    /// <param name="numerator">The dividend.</param>
    /// <param name="denominator">The divisor, which must be non-zero.</param>
    /// <returns>The floored quotient. Arbitrary width means the signed minimum over minus one is an ordinary case here
    /// rather than the overflow it is in the carrier, so the oracle never shares the subject's edge behaviour.</returns>
    public static BigInteger FloorQuotient(BigInteger numerator, BigInteger denominator) {
        var quotient = BigInteger.Divide(dividend: numerator, divisor: denominator);
        var remainder = (numerator - (quotient * denominator));

        return ((!remainder.IsZero && ((remainder.Sign < 0) != (denominator.Sign < 0))) ? (quotient - BigInteger.One) : quotient);
    }

    /// <summary>Reduces an exact integer to the signed 64-bit carrier, two's complement.</summary>
    /// <param name="value">The exact value.</param>
    /// <returns>The wrapped raw.</returns>
    /// <remarks>The signed reading of <see cref="WrapToUnsignedRaw"/>, which is the module's ONE reduction body — the
    /// two carriers differ only in how the reduced word is read, and writing the reduction twice would let the two
    /// spellings drift.</remarks>
    public static long WrapToRaw(BigInteger value) =>
        unchecked((long)WrapToUnsignedRaw(value: value));

    /// <summary>Reduces an exact integer to the UNSIGNED 64-bit carrier — modulo <c>2⁶⁴</c> into <c>[0, 2⁶⁴)</c>, where
    /// the signed reduction lands the same word in <c>[−2⁶³, 2⁶³)</c>.</summary>
    /// <param name="value">The exact value.</param>
    /// <returns>The wrapped raw.</returns>
    public static ulong WrapToUnsignedRaw(BigInteger value) =>
        ((ulong)((((value % TwoTo64) + TwoTo64) % TwoTo64)));

    /// <summary>Rounds the exact dyadic value <c>magnitude / 2^shift</c> to the nearest integer, ties to even, and
    /// returns it UNWRAPPED — so a caller can decide representability before reducing to a carrier, which is exactly
    /// what a checked operator's contract needs.</summary>
    /// <param name="magnitude">The exact numerator. Every caller in the unsigned family hands this a non-negative
    /// value; the underlying rule is sign-symmetric, so a signed caller gets the magnitude rounding it expects.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The rounded integer, unwrapped.</returns>
    public static BigInteger RoundToEvenUnits(BigInteger magnitude, int shift) =>
        RoundRationalTiesToEven(numerator: magnitude, denominator: (BigInteger.One << shift));

    /// <summary>Rounds an exact NON-NEGATIVE dyadic value <c>exact / 2^shift</c> to the nearest raw, ties to even, then
    /// wraps to the unsigned 64-bit carrier — the unsigned sibling of <see cref="RoundDyadic"/>. Every caller forms a
    /// non-negative ideal value, because the unsigned family has no negative side.</summary>
    /// <param name="exact">The exact non-negative numerator.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The rounded, wrapped raw.</returns>
    public static ulong RoundDyadicUnsigned(BigInteger exact, int shift) =>
        WrapToUnsignedRaw(value: RoundToEvenUnits(magnitude: exact, shift: shift));

    /// <summary>The reference UQ48.16 product — ONE ties-to-even rounding of the exact product at the <c>2⁻¹⁶</c> grid,
    /// reduced to the unsigned 64-bit carrier.</summary>
    /// <param name="x">The multiplicand's raw.</param>
    /// <param name="y">The multiplier's raw.</param>
    /// <returns>The product's raw.</returns>
    /// <remarks>Shares nothing with the subject: this forms the whole product in arbitrary width and rounds it once,
    /// where the subject truncates a <see cref="UInt128"/> to sixty-four bits and then adds a branchless correction
    /// rebuilt from the discarded low word. No sixty-four-bit boundary is observed here at all.</remarks>
    public static ulong UnsignedFixedProduct(ulong x, ulong y) =>
        RoundDyadicUnsigned(exact: ((BigInteger)x * y), shift: 16);

    /// <summary>The reference UQ48.16 quotient — ONE ties-to-even rounding of the exact rational <c>(x·2¹⁶)/y</c>,
    /// reduced to the unsigned 64-bit carrier (the ideal quotient can be eighty bits wide, and the subject wraps).</summary>
    /// <param name="x">The dividend's raw.</param>
    /// <param name="y">The divisor's raw; it must be non-zero.</param>
    /// <returns>The quotient's raw.</returns>
    /// <remarks>Shares nothing with the subject: the tie is decided by <c>2r</c> against <c>d</c>, where the subject
    /// compares <c>r</c> against <c>d − r</c> because <c>2r</c> would leave its carrier — the same predicate reached
    /// from the other side — and the quotient comes from one exact <see cref="BigInteger"/> division rather than from a
    /// 128-by-64 hardware divide under a fits-in-64-bits gate.</remarks>
    public static ulong UnsignedFixedQuotient(ulong x, ulong y) =>
        WrapToUnsignedRaw(value: RoundRationalTiesToEven(numerator: (((BigInteger)x) << 16), denominator: new BigInteger(value: y)));

    /// <summary>The exact rational <c>numerator / denominator</c> rounded to the nearest integer, ties to even,
    /// SIGN-SYMMETRICALLY (the tie rule is invariant under negation, so rounding the magnitude and re-applying the sign
    /// is the same map), returned UNWRAPPED. The home of the house ties-to-even rule: every dyadic and rational
    /// rounding face above calls into this one body, so the rule is stated once for them and cannot drift between
    /// spellings.</summary>
    /// <param name="numerator">The exact numerator.</param>
    /// <param name="denominator">The exact denominator, which must be non-zero.</param>
    /// <returns>The rounded quotient, unwrapped.</returns>
    /// <remarks>The tie comparison is written as <c>2r</c> versus <c>d</c> — the formulation the carrier's divide
    /// operators cannot use, since <c>2r</c> would overflow there; arbitrary width makes it the natural one here.</remarks>
    public static BigInteger RoundRationalTiesToEven(BigInteger numerator, BigInteger denominator) {
        var negative = ((numerator.Sign < 0) != (denominator.Sign < 0));
        var magnitude = BigInteger.Abs(value: numerator);
        var divisor = BigInteger.Abs(value: denominator);
        var quotient = BigInteger.DivRem(dividend: magnitude, divisor: divisor, remainder: out var remainder);
        var twiceRemainder = (remainder << 1);

        if ((twiceRemainder > divisor) || ((twiceRemainder == divisor) && !((quotient & BigInteger.One).IsZero))) {
            quotient += BigInteger.One;
        }

        return (negative ? -quotient : quotient);
    }

    /// <summary>The reference closed-unit product — one ties-to-even rounding of the exact product at the
    /// <c>2⁻³²</c> grid. Both raws lie in <c>[0, 2³²]</c>, so the rounded result does too and nothing wraps.</summary>
    /// <param name="x">The multiplicand's raw.</param>
    /// <param name="y">The multiplier's raw.</param>
    /// <returns>The product's raw.</returns>
    public static ulong ClosedUnitProduct(ulong x, ulong y) =>
        ((ulong)RoundDyadic(exact: ((BigInteger)x * y), shift: 32));

    /// <summary>The reference closed-unit product of THREE raws — one ties-to-even rounding of the exact triple product
    /// at the <c>2⁻³²</c> grid, taken at the tripled scale so that no intermediate is rounded.</summary>
    /// <param name="x">The first factor's raw.</param>
    /// <param name="y">The second factor's raw.</param>
    /// <param name="z">The third factor's raw.</param>
    /// <returns>The product's raw.</returns>
    public static ulong ClosedUnitTripleProduct(ulong x, ulong y, ulong z) =>
        ((ulong)RoundDyadic(exact: (((BigInteger)x * y) * z), shift: 64));

    /// <summary>The reference narrowing of a closed-unit raw onto <see cref="FixedQ4816"/>'s sixteen fraction bits —
    /// one ties-to-even rounding, sixteen bits discarded.</summary>
    /// <param name="value">The closed-unit raw.</param>
    /// <returns>The Q48.16 raw.</returns>
    public static long ClosedUnitNarrow(ulong value) =>
        RoundDyadic(exact: new BigInteger(value: value), shift: 16);

    /// <summary>The exact decimal expansion of the dyadic rational <c>numerator / 2^shift</c>, rendered the way the
    /// fixed-point family renders: the integer part, then a decimal point and the terminating expansion when the
    /// fraction is non-zero.</summary>
    /// <param name="numerator">The exact non-negative numerator.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The exact invariant-culture text.</returns>
    /// <remarks>Derived differently from any digit-at-a-time renderer: <c>n / 2ˢ</c> is <c>(n·5ˢ) / 10ˢ</c>, so the
    /// fraction digits ARE the decimal digits of the scaled fraction numerator, left-padded to <c>s</c> places and
    /// stripped of trailing zeros.</remarks>
    public static string ExactDyadicDecimal(BigInteger numerator, int shift) {
        var integerPart = (numerator >> shift);
        var fraction = (numerator - (integerPart << shift));

        if (fraction.IsZero) {
            return integerPart.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        }

        var digits = (fraction * BigInteger.Pow(value: new BigInteger(value: 5), exponent: shift))
            .ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
            .PadLeft(totalWidth: shift, paddingChar: '0');

        return $"{integerPart.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}.{digits.TrimEnd(trimChar: '0')}";
    }

    /// <summary>The reference half-open unit-fraction product — one ties-to-even rounding of the exact product at the
    /// <c>2⁻ᶠ</c> grid, where <c>f</c> is the width's fraction-bit count. Both raws are below <c>2ᶠ</c>, so the rounded
    /// product is below <c>2ᶠ</c> too and neither saturation nor wrapping arises.</summary>
    /// <param name="x">The multiplicand's raw.</param>
    /// <param name="y">The multiplier's raw.</param>
    /// <param name="fractionBitCount">The width's fraction-bit count.</param>
    /// <returns>The product's raw.</returns>
    public static ulong UnitFractionProduct(ulong x, ulong y, int fractionBitCount) =>
        ((ulong)RoundDyadic(exact: ((BigInteger)x * y), shift: fractionBitCount));

    /// <summary>The reference half-open unit-fraction quotient — one ties-to-even rounding of the exact ratio
    /// <c>(x·2ᶠ) / y</c>, then a clamp onto the largest representable raw. The rounding happens BEFORE the clamp, so a
    /// ratio that rounds up onto <c>2ᶠ</c> reports the endpoint rather than wrapping.</summary>
    /// <param name="x">The dividend's raw.</param>
    /// <param name="y">The divisor's raw, which must be non-zero.</param>
    /// <param name="fractionBitCount">The width's fraction-bit count.</param>
    /// <returns>The quotient's raw.</returns>
    /// <remarks>ENVELOPE: the tie <c>2·(x·2ᶠ mod y) == y</c> is unreachable at every legal operand pair, because the
    /// dividend carries <c>f</c> factors of two while every divisor below <c>2ᶠ</c> carries at most <c>f − 1</c>, so the
    /// remainder is divisible by <c>2^v₂(y)</c> and <c>y/2</c> is not. The rule is spelled out all the same, because it is
    /// the contract the subject's own correction block states.</remarks>
    public static ulong UnitFractionQuotient(ulong x, ulong y, int fractionBitCount) {
        var divisor = new BigInteger(value: y);
        var dividend = (new BigInteger(value: x) << fractionBitCount);
        var quotient = BigInteger.Divide(dividend: dividend, divisor: divisor);
        var twiceRemainder = ((dividend - (quotient * divisor)) << 1);
        var maximum = ((BigInteger.One << fractionBitCount) - BigInteger.One);

        if ((twiceRemainder > divisor) || ((twiceRemainder == divisor) && !((quotient & BigInteger.One).IsZero))) {
            quotient += BigInteger.One;
        }

        return ((ulong)BigInteger.Min(left: quotient, right: maximum));
    }

    /// <summary>The reference IEEE-754 binary64 encoding of the dyadic rational <c>numerator / 2^shift</c>, derived from
    /// the FORMAT rather than from any floating-point arithmetic: sign zero, exponent field
    /// <c>1023 + (bitLength − 1) − shift</c>, and a trailing significand that is the numerator's bits below its leading
    /// one, left-aligned into fifty-two places. Zero encodes as the all-clear pattern.</summary>
    /// <param name="numerator">The exact non-negative numerator; it must carry at most fifty-three significant bits, so
    /// the value is exactly representable and no rounding decision arises.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The bit pattern a <see cref="double"/> holding that value carries.</returns>
    public static ulong ExactBinary64Bits(BigInteger numerator, int shift) {
        if (numerator.IsZero) {
            return 0UL;
        }

        var bitLength = BinaryBitLength(magnitude: numerator);
        var significand = ((numerator << (53 - bitLength)) - (BigInteger.One << 52));
        var exponentField = new BigInteger(value: (((1023 + bitLength) - 1) - shift));

        return ((ulong)((exponentField << 52) + significand));
    }

    /// <summary>The reference IEEE-754 binary64 encoding of the SIGNED dyadic rational <c>value / 2^shift</c> under
    /// round-to-nearest-ties-to-even — the general form of <see cref="ExactBinary64Bits"/>, which demands an exactly
    /// representable magnitude. The magnitude is rounded to fifty-three significant bits by exact integer division with
    /// the tie decided on twice the remainder against the divisor, a carry out of the all-ones significand is absorbed
    /// by widening the exponent, and the encoding is then assembled from the FORMAT: sign bit, exponent field
    /// <c>1023 + 52 + excess − shift</c>, and the rounded significand less its implicit leading one. No floating-point
    /// arithmetic runs anywhere in it.</summary>
    /// <param name="value">The exact signed numerator. Its magnitude must stay inside the binary64 normal range at the
    /// given shift, which every fixed-point carrier in this tree does by a wide margin: no infinity, no subnormal and
    /// no not-a-number can arise.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The bit pattern a <see cref="double"/> nearest that value carries.</returns>
    public static ulong NearestBinary64Bits(BigInteger value, int shift) {
        if (value.IsZero) {
            return 0UL;
        }

        var negative = (value.Sign < 0);
        var magnitude = BigInteger.Abs(value: value);
        var excess = (BinaryBitLength(magnitude: magnitude) - 53);
        BigInteger significand;

        if (excess > 0) {
            var divisor = (BigInteger.One << excess);
            var quotient = BigInteger.Divide(dividend: magnitude, divisor: divisor);
            var twiceRemainder = ((magnitude - (quotient * divisor)) << 1);

            if ((twiceRemainder > divisor) || ((twiceRemainder == divisor) && !((quotient & BigInteger.One).IsZero))) {
                quotient += BigInteger.One;
            }

            // The one carry the rounding can make: an all-ones significand rolls to 2^53, which is one bit too wide.
            if (quotient == (BigInteger.One << 53)) {
                quotient >>= 1;
                ++excess;
            }

            significand = quotient;
        } else {
            significand = (magnitude << (-excess));
        }

        var exponentField = new BigInteger(value: (((1023 + 52) + excess) - shift));
        var bits = ((ulong)((exponentField << 52) + (significand - (BigInteger.One << 52))));

        return (negative ? (bits | (1UL << 63)) : bits);
    }

    // The number of bits in a non-negative magnitude's binary expansion; zero has none.
    private static int BinaryBitLength(BigInteger magnitude) {
        var bitLength = 0;

        for (var remaining = magnitude; !remaining.IsZero; remaining >>= 1) {
            ++bitLength;
        }

        return bitLength;
    }

    /// <summary>The reference decimal-text quantization for the half-open unit fractions: reads the text as ONE exact
    /// rational <c>N / 10ᵈ</c> and rounds it onto the <c>2⁻ᶠ</c> grid, ties to even, refusing anything the type cannot
    /// name. Deliberately unlike the subject, which truncates the fraction to <c>f + 1</c> decimal places, divides by the
    /// reduced denominator <c>2·5^(f+1)</c>, and carries a discarded-digit flag.</summary>
    /// <param name="text">The candidate text. The grammar accepted here is deliberately the SMALL one — optional ASCII
    /// spaces around <c>digits* ('.' digits*)?</c> with at least one digit — a strict subset of the subject's, which is
    /// the platform decimal grammar under AllowLeadingWhite/AllowTrailingWhite/AllowDecimalPoint. Every text a law hands
    /// this oracle lives in the subset; the culture spellings and the exotic-whitespace spellings are pinned structurally
    /// instead.</param>
    /// <param name="fractionBitCount">The width's fraction-bit count.</param>
    /// <param name="raw">The quantized raw on success; zero on refusal.</param>
    /// <returns><see langword="true"/> when the text names a value in <c>[0, (2ᶠ − 1)/2ᶠ]</c>.</returns>
    public static bool TryUnitFractionText(string text, int fractionBitCount, out ulong raw) {
        raw = 0UL;

        var span = text.AsSpan().Trim(trimChar: ' ');
        var digitCount = 0;
        var fractionDigitCount = -1;
        var value = BigInteger.Zero;

        foreach (var character in span) {
            if ('.' == character) {
                if (0 <= fractionDigitCount) { return false; }

                fractionDigitCount = 0;

                continue;
            }

            if (('0' > character) || ('9' < character)) { return false; }

            value = ((value * 10) + (character - '0'));
            ++digitCount;

            if (0 <= fractionDigitCount) { ++fractionDigitCount; }
        }

        if (0 == digitCount) { return false; }

        var denominator = BigInteger.Pow(value: new BigInteger(value: 10), exponent: Math.Max(val1: fractionDigitCount, val2: 0));
        var scaled = (value << fractionBitCount);
        var quotient = BigInteger.Divide(dividend: scaled, divisor: denominator);
        var remainder = (scaled - (quotient * denominator));
        var maximum = ((BigInteger.One << fractionBitCount) - BigInteger.One);

        // Out of range is a REFUSAL, not a clamp: the exact value is tested, not the rounded one, so a text strictly above
        // the top raw is refused even where rounding would carry it back onto the top raw.
        if ((quotient > maximum) || ((quotient == maximum) && !remainder.IsZero)) {
            return false;
        }

        var twiceRemainder = (remainder << 1);

        if ((twiceRemainder > denominator) || ((twiceRemainder == denominator) && !((quotient & BigInteger.One).IsZero))) {
            quotient += BigInteger.One;
        }

        raw = ((ulong)quotient);

        return true;
    }

    /// <summary>The reference product of two elements of the algebra <c>x² = P·x + Q</c> over the fixed-point carrier,
    /// coefficients as raw Q16 longs. <c>U</c> and <c>V</c> are each one Q48→Q16 rounding of the ideal expression.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <param name="u1">The first element's scalar part, raw.</param>
    /// <param name="v1">The first element's root coefficient, raw.</param>
    /// <param name="u2">The second element's scalar part, raw.</param>
    /// <param name="v2">The second element's root coefficient, raw.</param>
    /// <returns>The product components as raws.</returns>
    public static (long U, long V) QuadraticMultiply(long pRaw, long qRaw, long u1, long v1, long u2, long v2) {
        var rootProduct = ((BigInteger)v1 * v2);
        var tU = ((((BigInteger)u1 * u2) << 16) + ((BigInteger)qRaw * rootProduct));
        var tV = (((((BigInteger)u1 * v2) + ((BigInteger)v1 * u2)) << 16) + ((BigInteger)pRaw * rootProduct));

        return (RoundDyadic(exact: tU, shift: 32), RoundDyadic(exact: tV, shift: 32));
    }

    /// <summary>The reference algebra norm <c>U² + P·U·V − Q·V²</c>, one Q48→Q16 rounding.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <param name="u">The scalar part, raw.</param>
    /// <param name="v">The root coefficient, raw.</param>
    /// <returns>The norm as a raw.</returns>
    public static long QuadraticNorm(long pRaw, long qRaw, long u, long v) {
        var exact = (((((BigInteger)u * u) << 16) + ((BigInteger)pRaw * ((BigInteger)u * v))) - ((BigInteger)qRaw * ((BigInteger)v * v)));

        return RoundDyadic(exact: exact, shift: 32);
    }

    /// <summary>The reference Möbius/projective numerator <c>P·n + Q·d</c> at Q32, one Q32→Q16 rounding (exact when the
    /// coefficients are integers — the remainder is then identically zero).</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <param name="n">The projective numerator, raw.</param>
    /// <param name="d">The projective denominator, raw.</param>
    /// <returns>The stepped numerator as a raw.</returns>
    public static long MobiusNumerator(long pRaw, long qRaw, long n, long d) {
        var exact = (((BigInteger)pRaw * n) + ((BigInteger)qRaw * d));

        return RoundDyadic(exact: exact, shift: 16);
    }

    /// <summary>The exact (unrounded) algebra norm numerator as a <see cref="BigInteger"/> over the fixed denominator
    /// <c>2^32</c> — the value <see cref="QuadraticNorm"/> rounds. No law consumes it: the committed
    /// norm-multiplicativity law runs on the bounded sublattice, where the subject's own rounded raws are already
    /// exactly multiplicative, so it compares those directly.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <param name="u">The scalar part, raw.</param>
    /// <param name="v">The root coefficient, raw.</param>
    /// <returns>The exact norm numerator over the fixed denominator <c>2^32</c>.</returns>
    public static BigInteger ExactNormNumerator(long pRaw, long qRaw, long u, long v) =>
        (((((BigInteger)u * u) << 16) + ((BigInteger)pRaw * ((BigInteger)u * v))) - ((BigInteger)qRaw * ((BigInteger)v * v)));

    /// <summary>The reference power of the adjoined root of <c>x² = P·x + Q</c>, by the pinned ascending-bit
    /// square-and-multiply schedule, every step's two components one Q48→Q16 rounding of the exact product in
    /// <see cref="BigInteger"/>.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <param name="exponent">The power; zero yields the unit element.</param>
    /// <returns>The power's components as raws.</returns>
    /// <remarks>The SCHEDULE is faithful carriage, not independent evidence: a power over a rounding carrier is
    /// chain-dependent, so the number and order of the roundings is part of the answer and no single-rounding oracle can
    /// stand beside it. What this reference re-derives independently is the ARITHMETIC of every step — the ideal
    /// expression accumulated exactly and rounded once per component by <see cref="RoundDyadic"/>, sharing no code and no
    /// rounding kernel with either subject. A transcription error in the schedule would therefore hide; an error in a
    /// step's product, its rounding, or its wrap would not. The schedule itself is pinned as a contract on both subjects
    /// (<c>PresentedAlgebra.Power</c>'s remark) and is the statement the twin leg makes.</remarks>
    public static (long U, long V) CompanionRootPower(long pRaw, long qRaw, ulong exponent) {
        var result = ((1L << 16), 0L);
        var power = (0L, (1L << 16));

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) {
                result = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: result.Item1, v1: result.Item2, u2: power.Item1, v2: power.Item2);
            }

            exponent >>>= 1;

            if (0UL != exponent) {
                power = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: power.Item1, v1: power.Item2, u2: power.Item1, v2: power.Item2);
            }
        }

        return result;
    }

    /// <summary>The largest partial quotient of the continued fraction of <c>(p + q·√d) / r</c>, excluding the integer
    /// part — the badly-approximable certificate, walked here in <see cref="BigInteger"/> arithmetic.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <returns>The maximum over <c>a₁, a₂, …</c> of the eventually periodic expansion.</returns>
    /// <remarks>The expansion is <see cref="PartialQuotients"/>, which shares nothing with the subject. The repeating
    /// block recurs at arbitrarily large indices, so the maximum over the pre-period tail and one block is the maximum
    /// over the whole infinite expansion.</remarks>
    /// <exception cref="InvalidOperationException">The expansion did not close within the walk ceiling.</exception>
    public static long MaximumPartialQuotient(long p, long q, long d, long r) {
        var quotients = PartialQuotients(p: p, q: q, d: d, r: r, periodStart: out var periodStart);
        var maximum = BigInteger.One;

        // Only a₀ is dropped — a large integer part shifts the value without clumping its fractional points — so
        // the supremum runs over the pre-period tail a₁.. and over the whole repeating block, which recurs at
        // arbitrarily large indices and therefore contributes every one of its terms even when it starts at a₀.
        for (var index = 1; (index < periodStart); ++index) { maximum = BigInteger.Max(left: maximum, right: quotients[index]); }
        for (var index = periodStart; (index < quotients.Count); ++index) { maximum = BigInteger.Max(left: maximum, right: quotients[index]); }

        return ((long)maximum);
    }

    /// <summary>The continued-fraction expansion of <c>(p + q·√d) / r</c> through the end of its first repeating block,
    /// walked in <see cref="BigInteger"/> arithmetic.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <param name="periodStart">Receives the index of the first term of the repeating block.</param>
    /// <returns>The pre-period followed by exactly one period block.</returns>
    /// <remarks>Shares nothing with the subject: the value is carried as the reduced triple <c>(A + B·√d) / C</c> in the
    /// original field rather than the subject's canonical <c>(P + √N) / Q</c> surd form, the floor comes from a bracketed
    /// integer search whose predicate is one exact squaring comparison rather than an integer square root, and the period
    /// is found by repeating the reduced triple. Because the reduced triple with <c>C &gt; 0</c> is unique, a repeat is
    /// the period and its first occurrence is where the purely periodic tail begins.</remarks>
    /// <exception cref="InvalidOperationException">The expansion did not close within the walk ceiling.</exception>
    public static IReadOnlyList<BigInteger> PartialQuotients(long p, long q, long d, long r, out int periodStart) {
        var radicand = new BigInteger(value: d);
        var (a, b, c) = Reduce(a: new BigInteger(value: p), b: new BigInteger(value: q), c: new BigInteger(value: r));
        var seen = new Dictionary<(BigInteger A, BigInteger B, BigInteger C), int>();
        var quotients = new List<BigInteger>();

        while (quotients.Count < 4096) {
            if (seen.TryGetValue(key: (a, b, c), value: out var repeatAt)) {
                periodStart = repeatAt;

                return quotients;
            }

            seen.Add(key: (a, b, c), value: quotients.Count);

            var quotient = Floor(a: a, b: b, c: c, radicand: radicand);

            quotients.Add(item: quotient);

            // 1 / ((A' + B√d) / C) = (C·A' − C·B√d) / (A'² − B²·d), with A' = A − a·C the fractional part's numerator.
            var shifted = (a - (quotient * c));

            (a, b, c) = Reduce(a: (c * shifted), b: (-c * b), c: ((shifted * shifted) - ((b * b) * radicand)));
        }

        throw new InvalidOperationException(message: "the continued fraction did not close within the walk ceiling");
    }

    /// <summary>The tiling word of the quadratic irrational <c>(p + q·√d) / r</c>, read as the MECHANICAL word of its
    /// slope: no substitution is applied and no letter image is ever formed.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <param name="tiles">Receives the word: <see langword="false"/> is the short tile, <see langword="true"/> the
    /// long. Every element is written.</param>
    /// <remarks>
    /// The subject builds its word by composing the period's factors <c>τ_k: long → long^k short, short → long</c> and
    /// expanding the fixed point. This reaches the same word from the other end of the theory: the fixed point of that
    /// composition is the characteristic Sturmian word of slope <c>β = [0; b₀+1, b₁, …]</c>, whose letters are the
    /// first differences <c>⌊(n+1)·β⌋ − ⌊n·β⌋</c> — one where the short tile sits, zero where the long one does. The
    /// period comes from <see cref="PartialQuotients"/>, so not even the expansion is shared with the subject.
    /// <para><c>β</c> is irrational, so each floor is pinned by BRACKETING it between two consecutive convergents of its
    /// own continued fraction and requiring the two floors to agree; a disagreement throws rather than guesses.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The bracket failed to pin a floor, or the expansion did not close.</exception>
    public static void SturmianMechanicalWord(long p, long q, long d, long r, Span<bool> tiles) {
        var quotients = PartialQuotients(p: p, q: q, d: d, r: r, periodStart: out var periodStart);
        var block = new BigInteger[quotients.Count - periodStart];

        for (var index = 0; (index < block.Length); ++index) { block[index] = quotients[(periodStart + index)]; }

        // β = α / (1 + α) with α = [0; b₀, b₁, …] the purely periodic tail, which is [0; b₀+1, b₁, …] term for term.
        // Its convergents alternate around it, so consecutive ones bracket it; take enough that the bracket is far
        // tighter than the coarsest floor the caller asks for.
        var bound = (new BigInteger(value: (tiles.Length + 2)) << 8);
        BigInteger numeratorPrevious = BigInteger.One, denominatorPrevious = BigInteger.Zero;
        BigInteger numerator = BigInteger.Zero, denominator = BigInteger.One;
        var term = 0;

        while ((denominator <= bound) || (term < block.Length)) {
            var partial = (((0 == term) ? BigInteger.One : BigInteger.Zero) + block[(term % block.Length)]);

            (numeratorPrevious, denominatorPrevious, numerator, denominator) =
                (numerator, denominator, ((partial * numerator) + numeratorPrevious), ((partial * denominator) + denominatorPrevious));

            ++term;
        }

        var scale = (denominator * denominatorPrevious);
        var low = BigInteger.Min(left: (numerator * denominatorPrevious), right: (numeratorPrevious * denominator));
        var high = BigInteger.Max(left: (numerator * denominatorPrevious), right: (numeratorPrevious * denominator));
        var previous = BigInteger.Zero;

        for (var index = 1; (index <= (tiles.Length + 1)); ++index) {
            var multiple = new BigInteger(value: index);
            var floorLow = BigInteger.Divide(dividend: (multiple * low), divisor: scale);
            var floorHigh = BigInteger.Divide(dividend: (multiple * high), divisor: scale);

            if (floorLow != floorHigh) {
                throw new InvalidOperationException(message: $"the convergents do not pin the {index}th multiple of the slope");
            }

            // A step of one crosses an integer, which is where the SHORT tile sits; no step is the long one.
            if (index > 1) { tiles[(index - 2)] = (floorLow == previous); }

            previous = floorLow;
        }
    }

    /// <summary>The incidence matrix of the substitution a continued-fraction period composes: the continuant product
    /// <c>∏ [[bᵢ, 1], [1, 0]]</c>, formed here in <see cref="BigInteger"/> from the period alone.</summary>
    /// <param name="period">The repeating block, in the order the substitution composes it.</param>
    /// <returns>The entries <c>(A, B, C, D)</c> of the product, read row by row.</returns>
    /// <remarks>The abelianization of the composed substitution is this matrix TRANSPOSED — the census of the image of
    /// letter <c>i</c> is its column <c>i</c> — and it is what the shipped inflation lens reads from the same period. It
    /// is authored here as a plain matrix product over the integers, so it stands outside both.</remarks>
    public static (BigInteger A, BigInteger B, BigInteger C, BigInteger D) SubstitutionIncidence(ReadOnlySpan<long> period) {
        BigInteger a = BigInteger.One, b = BigInteger.Zero, c = BigInteger.Zero, e = BigInteger.One;

        foreach (var partial in period) {
            var quotient = new BigInteger(value: partial);

            (a, b, c, e) = (((a * quotient) + b), a, ((c * quotient) + e), c);
        }

        return (a, b, c, e);
    }

    // The reduced representative of (A + B√d) / C: denominator positive, the three coefficients coprime. Unique, which is
    // what lets a repeat of the triple mark the period.
    private static (BigInteger A, BigInteger B, BigInteger C) Reduce(BigInteger a, BigInteger b, BigInteger c) {
        if (c.Sign < 0) {
            (a, b, c) = (-a, -b, -c);
        }

        var divisor = BigInteger.GreatestCommonDivisor(left: BigInteger.GreatestCommonDivisor(left: BigInteger.Abs(value: a), right: BigInteger.Abs(value: b)), right: c);

        return ((a / divisor), (b / divisor), (c / divisor));
    }

    // ⌊(A + B√d) / C⌋ for C > 0, by bracketing and bisecting the exact predicate k·C ≤ A + B√d.
    private static BigInteger Floor(BigInteger a, BigInteger b, BigInteger c, BigInteger radicand) {
        var low = BigInteger.Zero;
        var high = BigInteger.One;

        if (AtMost(candidate: low, a: a, b: b, c: c, radicand: radicand)) {
            while (AtMost(candidate: high, a: a, b: b, c: c, radicand: radicand)) {
                low = high;
                high <<= 1;
            }
        } else {
            high = low;
            low = BigInteger.MinusOne;

            while (!AtMost(candidate: low, a: a, b: b, c: c, radicand: radicand)) {
                high = low;
                low <<= 1;
            }
        }

        // low satisfies the predicate, high does not; bisect down to the boundary.
        while ((high - low) > BigInteger.One) {
            var middle = ((low + high) >> 1);

            if (AtMost(candidate: middle, a: a, b: b, c: c, radicand: radicand)) {
                low = middle;
            } else {
                high = middle;
            }
        }

        return low;
    }

    // candidate·C ≤ A + B√d, exactly: move the rational part across and decide the surd comparison by one squaring, with
    // the sign of each side read off first so squaring never flips the inequality.
    private static bool AtMost(BigInteger candidate, BigInteger a, BigInteger b, BigInteger c, BigInteger radicand) {
        var excess = ((candidate * c) - a);

        if (b.Sign >= 0) {
            return ((excess.Sign <= 0) || ((excess * excess) <= ((b * b) * radicand)));
        }

        return ((excess.Sign <= 0) && ((excess * excess) >= ((b * b) * radicand)));
    }

    /// <summary>The reference charge a Clifford signature puts on one ordered pair of basis blades, computed by writing
    /// both blades out as explicit ascending generator lists, concatenating them, BUBBLE SORTING the concatenation while
    /// counting transpositions, and then cancelling the adjacent equal pairs against the generators' squares.</summary>
    /// <param name="leftBlade">The left blade as a generator bitmask.</param>
    /// <param name="rightBlade">The right blade as a generator bitmask.</param>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of generators squaring to <c>0</c>.</param>
    /// <returns><c>+1</c>, <c>−1</c>, or <c>0</c> when a degenerate generator annihilates the product. The result blade
    /// is the exclusive-or of the two operands and is not returned.</returns>
    /// <remarks>Deliberately the slow, literal construction: no parity-of-inversions popcount identity is used anywhere,
    /// so agreement with a table-driven kernel is evidence rather than a restatement.</remarks>
    public static int CliffordCharge(int leftBlade, int rightBlade, int positiveCount, int negativeCount, int degenerateCount) {
        var generatorCount = ((positiveCount + negativeCount) + degenerateCount);
        var letters = new List<int>();

        for (var generator = 0; (generator < generatorCount); ++generator) {
            if (0 != (leftBlade & (1 << generator))) { letters.Add(item: generator); }
        }

        for (var generator = 0; (generator < generatorCount); ++generator) {
            if (0 != (rightBlade & (1 << generator))) { letters.Add(item: generator); }
        }

        var sign = 1;

        for (var pass = 0; (pass < letters.Count); ++pass) {
            for (var position = 0; ((position + 1) < letters.Count); ++position) {
                if (letters[position] <= letters[(position + 1)]) { continue; }

                (letters[position], letters[(position + 1)]) = (letters[(position + 1)], letters[position]);
                sign = -sign;
            }
        }

        for (var position = 0; ((position + 1) < letters.Count); ) {
            if (letters[position] != letters[(position + 1)]) {
                ++position;

                continue;
            }

            var generator = letters[position];
            var square = ((generator < positiveCount)
                ? 1
                : ((generator < (positiveCount + negativeCount)) ? -1 : 0));

            if (0 == square) { return 0; }

            sign *= square;

            letters.RemoveRange(index: position, count: 2);
            position = ((position > 0) ? (position - 1) : 0);
        }

        return sign;
    }

    /// <summary>The reference charge the Cayley–Dickson tower puts on one ordered pair of basis indices, computed by the
    /// doubling recursion <c>(a, b)·(c, d) = (a·c − d̄·b, d·a + b·c̄)</c> applied to basis vectors.</summary>
    /// <param name="leftIndex">The left basis index, below <c>2^floors</c>.</param>
    /// <param name="rightIndex">The right basis index, below <c>2^floors</c>.</param>
    /// <param name="floors">The number of doublings.</param>
    /// <returns><c>+1</c> or <c>−1</c>; the result index is the exclusive-or of the two operands and is not returned.</returns>
    /// <remarks>
    /// TRANSCRIPTION, labelled (condition (C)). This transcribes the recursion
    /// <see cref="Presentations"/>'s Cayley–Dickson sign rule carries, branch for branch — there is no second definition
    /// of the tower — so agreement proves the presentation carries the recursion FAITHFULLY and never that the recursion
    /// is the right one. A shared error in the sign convention cancels on both sides and this leg stays green.
    /// <para>The independent witness is the shipped nested doubling tower, multiplied out at unit basis elements: the
    /// commutation charges in <c>presented.braiding-derived-vs-doubling</c> at every floor the tower ships, and the
    /// floor-three associator twin in <c>presented.associator-twin-doubling</c>. Those read a kernel that shares no code
    /// with either side.</para>
    /// <para>A basis vector at index <c>i</c> is the pair <c>(e_low, 0)</c> when <c>i</c> is below the half, and
    /// <c>(0, e_low)</c> above it; expanding the four cases of the doubling product on those pairs gives the recursion
    /// below.</para>
    /// </remarks>
    public static int CayleyDicksonCharge(int leftIndex, int rightIndex, int floors) {
        if (0 == floors) { return 1; }

        var half = (1 << (floors - 1));
        var leftHigh = (leftIndex >= half);
        var leftLow = (leftIndex & (half - 1));
        var rightHigh = (rightIndex >= half);
        var rightLow = (rightIndex & (half - 1));

        // (a, 0)·(c, 0) = (a·c, 0).
        if (!leftHigh && !rightHigh) { return CayleyDicksonCharge(leftIndex: leftLow, rightIndex: rightLow, floors: (floors - 1)); }

        // (a, 0)·(0, d) = (0, d·a).
        if (!leftHigh) { return CayleyDicksonCharge(leftIndex: rightLow, rightIndex: leftLow, floors: (floors - 1)); }

        // (0, b)·(c, 0) = (0, b·c̄).
        if (!rightHigh) { return (ConjugationSign(index: rightLow) * CayleyDicksonCharge(leftIndex: leftLow, rightIndex: rightLow, floors: (floors - 1))); }

        // (0, b)·(0, d) = (−d̄·b, 0).
        return (-ConjugationSign(index: rightLow) * CayleyDicksonCharge(leftIndex: rightLow, rightIndex: leftLow, floors: (floors - 1)));
    }

    /// <summary>The reference product of two basis monomials of <c>ℤ[x] / (xᵈ − Σ cᵢ xⁱ)</c>: schoolbook, by carrying
    /// the top coefficient down through the relation until nothing above degree <c>d − 1</c> remains.</summary>
    /// <param name="relation">The relation <c>xᵈ = Σ cᵢ xⁱ</c> as its coefficients <c>c₀ … c_{d−1}</c>, ascending; the
    /// degree is its length. A caller holding a monic MODULUS tail negates it to reach this form.</param>
    /// <param name="leftExponent">The multiplicand's exponent.</param>
    /// <param name="rightExponent">The multiplier's exponent.</param>
    /// <returns>The reduced coefficients <c>a₀ … a_{d−1}</c>, exact.</returns>
    /// <remarks>Shares nothing with any presentation kernel: the product of two monomials is one exponent, and the
    /// reduction is the relation read as a rewriting of <c>xᵈ</c>, applied top down. No normal-form word, no rewrite
    /// rule table and no charge is consulted.</remarks>
    public static BigInteger[] MonogenicMonomialProduct(ReadOnlySpan<BigInteger> relation, int leftExponent, int rightExponent) {
        var degree = relation.Length;
        var coefficients = new BigInteger[Math.Max(val1: degree, val2: ((leftExponent + rightExponent) + 1))];

        coefficients[(leftExponent + rightExponent)] = BigInteger.One;

        for (var exponent = (coefficients.Length - 1); (exponent >= degree); --exponent) {
            var carried = coefficients[exponent];

            if (carried.IsZero) { continue; }

            coefficients[exponent] = BigInteger.Zero;

            for (var index = 0; (index < degree); ++index) {
                coefficients[((exponent - degree) + index)] += (carried * relation[index]);
            }
        }

        var reduced = new BigInteger[degree];

        for (var index = 0; (index < degree); ++index) { reduced[index] = coefficients[index]; }

        return reduced;
    }

    /// <summary>The reference product of a twisted group algebra over <c>(ℤ/2)^k</c> — the shape BOTH the Clifford and
    /// the Cayley–Dickson bases take, the target key always being the exclusive-or of the two operand keys. Each returned
    /// lane is exactly ONE ties-to-even rounding of the whole exact charged sum, wrapped to the carrier.</summary>
    /// <param name="chargeSource">The 2-cochain: the charge on an ordered pair of keys, in <c>{−1, 0, +1}</c>.</param>
    /// <param name="left">The multiplicand's lanes, raw.</param>
    /// <param name="right">The multiplier's lanes, raw.</param>
    /// <param name="shift">The rounding scale; the two raw Q16 factors make the exact sum Q32, so this is 16.</param>
    /// <param name="result">The destination lanes, the same width as the operands.</param>
    public static void TwistedGroupProduct(Func<int, int, int> chargeSource, ReadOnlySpan<long> left, ReadOnlySpan<long> right, int shift, Span<long> result) {
        var width = left.Length;

        for (var target = 0; (target < width); ++target) {
            var exact = BigInteger.Zero;

            for (var first = 0; (first < width); ++first) {
                var second = (first ^ target);
                var charge = chargeSource(first, second);

                if (0 == charge) { continue; }

                var term = ((BigInteger)left[first] * right[second]);

                exact += ((charge > 0) ? term : -term);
            }

            result[target] = RoundDyadic(exact: exact, shift: shift);
        }
    }

    /// <summary>The same twisted group product with the rounding moved: EVERY TERM is rounded on its own and the rounded
    /// terms are then summed. The honest alternative discipline, and the one the fused kernels are claimed to beat.</summary>
    /// <param name="chargeSource">The 2-cochain, as in <see cref="TwistedGroupProduct"/>.</param>
    /// <param name="left">The multiplicand's lanes, raw.</param>
    /// <param name="right">The multiplier's lanes, raw.</param>
    /// <param name="shift">The rounding scale.</param>
    /// <param name="result">The destination lanes.</param>
    public static void TwistedGroupPerProduct(Func<int, int, int> chargeSource, ReadOnlySpan<long> left, ReadOnlySpan<long> right, int shift, Span<long> result) {
        var width = left.Length;

        for (var target = 0; (target < width); ++target) {
            var total = BigInteger.Zero;

            for (var first = 0; (first < width); ++first) {
                var second = (first ^ target);
                var charge = chargeSource(first, second);

                if (0 == charge) { continue; }

                var term = ((BigInteger)left[first] * right[second]);

                total += RoundDyadic(exact: ((charge > 0) ? term : -term), shift: shift);
            }

            result[target] = WrapToRaw(value: total);
        }
    }

    /// <summary>The reference product of <c>GF(2^degree)</c>, by SCHOOLBOOK carryless multiplication into a
    /// double-width polynomial followed by bit-by-bit reduction from the top against the modulus.</summary>
    /// <param name="left">The multiplicand as a coefficient bitmask.</param>
    /// <param name="right">The multiplier as a coefficient bitmask.</param>
    /// <param name="degree">The extension degree.</param>
    /// <param name="reductionTail">The modulus below its leading <c>x^degree</c> term.</param>
    /// <returns>The reduced product as a coefficient bitmask.</returns>
    public static BigInteger BinaryFieldProduct(BigInteger left, BigInteger right, int degree, BigInteger reductionTail) {
        // The unreduced product is the module's ONE carryless multiply: this oracle's callers pack degree-wide lane
        // vectors, so the multiplier carries no coefficient at or above the degree and the whole product is bit-for-bit
        // the bounded one this loop used to form for itself.
        var wide = CarrylessProduct(left: left, right: right);

        for (var bit = ((2 * degree) - 2); (bit >= degree); --bit) {
            if (((wide >> bit) & BigInteger.One).IsZero) { continue; }

            wide ^= (BigInteger.One << bit);
            wide ^= (reductionTail << (bit - degree));
        }

        return wide;
    }

    /// <summary>The reference reduction of an arbitrary packed polynomial modulo <c>t^degree + reductionTail</c>, by
    /// SCHOOLBOOK LONG DIVISION against the modulus MATERIALIZED IN FULL — its implicit leading term included.</summary>
    /// <param name="value">The packed polynomial to reduce, of any degree.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="reductionTail">The modulus below its leading term.</param>
    /// <returns>The unique representative of degree below <paramref name="degree"/>.</returns>
    /// <remarks>The subject never materializes <c>t^degree</c> at all: the representation stores only the tail, and
    /// reduction proceeds by splitting the value at the degree, multiplying the high part BY the tail through a
    /// carryless product, and re-splitting until the remainder clears. This rebuilds the leading term the subject
    /// deliberately elides and divides from the top, so a dropped or mis-shifted leading term, a low mask built as
    /// <c>(1 &lt;&lt; degree) − 1</c> where the shift count reaches the carrier's width, or a split that returned its
    /// value unshifted all diverge on the first operand. Exact on both sides, so the rounding condition does not
    /// arise.</remarks>
    public static BigInteger BinaryFieldReduce(BigInteger value, int degree, BigInteger reductionTail) =>
        ReduceBinary(value: value, modulus: ((BigInteger.One << degree) | reductionTail));

    /// <summary>The reference multiplicative inverse in <c>GF(2^degree)</c>, by the EXTENDED EUCLIDEAN algorithm over
    /// the polynomial ring — the almost-inverse loop that tracks one Bezout coefficient — rather than by any power of
    /// the value.</summary>
    /// <param name="value">The reduced, non-zero element to invert.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="reductionTail">The modulus below its leading term.</param>
    /// <returns>The inverse, or <see cref="BigInteger.MinusOne"/> when the step budget was exhausted, which happens
    /// only for a reducible modulus — where the descent stalls on a common factor, no inverse exists, and the loop
    /// would not otherwise halt.</returns>
    /// <remarks>The subject is the Itoh–Tsujii Frobenius addition chain: it reaches <c>value^(2^degree − 2)</c> as a
    /// POWER, walking the binary expansion of <c>degree − 1</c> with repeated squarings and one final Frobenius step.
    /// This never exponentiates anything — it runs Euclid on the polynomials — so a wrong reach bookkeeping, a missing
    /// final squaring or a degree-one short circuit cannot be reproduced here.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="value"/> is zero.</exception>
    public static BigInteger BinaryFieldInverse(BigInteger value, int degree, BigInteger reductionTail) {
        if (value.IsZero) { throw new DivideByZeroException(); }

        var modulus = ((BigInteger.One << degree) | reductionTail);
        var first = value;
        var second = modulus;
        var firstCoefficient = BigInteger.One;
        var secondCoefficient = BigInteger.Zero;

        // Every step clears the leading term of the higher-degree operand, so the two degrees' sum strictly decreases
        // and an irreducible modulus lands on one within roughly 2·degree steps. Twice that is the ceiling; exhausting
        // it is a NAMED failure — the caller reports the modulus — rather than the spin a stalled descent would
        // otherwise become, per the suite's search-budget rule.
        for (var step = 0; !first.IsOne; ++step) {
            if (step > (4 * degree)) { return BigInteger.MinusOne; }

            var shift = (BinaryPolynomialDegree(value: first) - BinaryPolynomialDegree(value: second));

            if (shift < 0) {
                (first, second) = (second, first);
                (firstCoefficient, secondCoefficient) = (secondCoefficient, firstCoefficient);
                shift = -shift;
            }

            first ^= (second << shift);
            firstCoefficient ^= (secondCoefficient << shift);
        }

        // The loop maintains first ≡ firstCoefficient · value modulo the modulus, and first is now one, so the
        // coefficient's own remainder is the field's unique inverse — reduced here rather than assumed bounded.
        return ReduceBinary(value: firstCoefficient, modulus: modulus);
    }

    /// <summary>The reference power in <c>GF(2^degree)</c>, by the SEQUENTIAL fold — one ordinary product of a running
    /// accumulator against the value per unit of exponent — rather than by square-and-multiply over the exponent's
    /// binary expansion.</summary>
    /// <param name="value">The reduced base.</param>
    /// <param name="exponent">The exponent, small: the fold costs one product per unit.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="reductionTail">The modulus below its leading term.</param>
    /// <returns>The reduced power; one for a zero exponent, including at a zero base.</returns>
    /// <remarks>The two schedules perform different operations in a different order, so a mis-stepped
    /// square-and-multiply cannot agree with this by construction. It reuses <see cref="BinaryFieldProduct"/> —
    /// oracles sharing their primitives with each OTHER is the sanctioned pattern; neither shares anything with the
    /// subject — whose own precondition is met because a running accumulator and a reduced base are both
    /// reduced.</remarks>
    public static BigInteger BinaryFieldRepeatedProduct(BigInteger value, int exponent, int degree, BigInteger reductionTail) {
        var result = BigInteger.One;

        for (var step = 0; (step < exponent); ++step) {
            result = BinaryFieldProduct(left: result, right: value, degree: degree, reductionTail: reductionTail);
        }

        return result;
    }

    /// <summary>The reference value of a polynomial over <c>GF(2^degree)</c> at a point, by the DEFINITION — the sum of
    /// each coefficient times the matching power of the point — rather than by Horner's nested form.</summary>
    /// <param name="coefficients">The coefficients, highest-order first, as reduced field elements.</param>
    /// <param name="point">The reduced element to evaluate at.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="reductionTail">The modulus below its leading term.</param>
    /// <returns>The reduced value.</returns>
    /// <remarks>
    /// The subject evaluates by Horner — one multiply of a running accumulator by the point per coefficient, folding the
    /// next coefficient in as it goes — so the two schedules touch different intermediate values in a different order,
    /// and a Horner loop that seeds wrong, folds the coefficient on the wrong side of the multiply, or walks the
    /// coefficients in the opposite order cannot agree with this by construction. The powers are accumulated forward
    /// from the constant term so the whole evaluation stays linear in the coefficient count rather than quadratic; that
    /// is a cost decision inside ONE derivation and not a step toward the subject's. It reuses
    /// <see cref="BinaryFieldProduct"/>, which is the sanctioned sharing of a primitive between oracles; nothing here
    /// touches the subject.
    /// </remarks>
    public static BigInteger BinaryFieldPolynomialValue(BigInteger[] coefficients, BigInteger point, int degree, BigInteger reductionTail) {
        var power = BigInteger.One;
        var total = BigInteger.Zero;

        for (var index = (coefficients.Length - 1); (index >= 0); --index) {
            total ^= BinaryFieldProduct(left: coefficients[index], right: power, degree: degree, reductionTail: reductionTail);
            power = BinaryFieldProduct(left: power, right: point, degree: degree, reductionTail: reductionTail);
        }

        return total;
    }

    /// <summary>The exact carryless product of two <c>GF(2)</c> coefficient bit-vectors — the schoolbook
    /// shift-and-exclusive-or over the multiplier's set bits, unbounded in width.</summary>
    /// <param name="left">The multiplicand as a coefficient bitmask.</param>
    /// <param name="right">The multiplier as a coefficient bitmask.</param>
    /// <returns>The whole product, with no truncation and no modulus.</returns>
    /// <remarks>The subject side reaches a word-level kernel — one carryless-multiply instruction where the hardware
    /// carries it, four thirty-two-bit comb multiplies where it does not. This carries no word split, no limb, no comb
    /// and no instruction set: it adds shifted copies of the multiplicand under exclusive or, which is the definition.
    /// Exact on both sides, so the rounding condition does not arise.</remarks>
    public static BigInteger CarrylessProduct(BigInteger left, BigInteger right) {
        var product = BigInteger.Zero;

        for (var bit = 0; (bit < ((int)right.GetBitLength())); ++bit) {
            if (!((right >> bit) & BigInteger.One).IsZero) { product ^= (left << bit); }
        }

        return product;
    }

    /// <summary>The degree of a <c>GF(2)</c> coefficient bit-vector, by a downward scan for the highest set bit.</summary>
    /// <param name="value">The coefficient bitmask.</param>
    /// <returns>The largest exponent carrying a non-zero coefficient, or minus one for the zero polynomial.</returns>
    /// <remarks>The subject computes a fixed carrier width minus a hardware leading-zero count. This scan knows nothing
    /// of a carrier width at all, so a wrong width constant or an off-by-one in the complement is visible here.</remarks>
    public static int BinaryPolynomialDegree(BigInteger value) {
        for (var bit = ((int)value.GetBitLength()); (bit > 0); --bit) {
            if (!((value >> (bit - 1)) & BigInteger.One).IsZero) { return (bit - 1); }
        }

        return -1;
    }

    /// <summary>The coefficient-wise sum over <c>GF(2)</c>, formed one bit position at a time from the definition —
    /// each coefficient is the sum of the two inputs' coefficients modulo two.</summary>
    /// <param name="left">The first addend as a coefficient bitmask.</param>
    /// <param name="right">The second addend as a coefficient bitmask.</param>
    /// <param name="width">The number of coefficient positions to form.</param>
    /// <returns>The sum as a coefficient bitmask.</returns>
    /// <remarks>The subject is one exclusive or over the packed carrier. This adds two integers per coefficient and
    /// reduces modulo two — arithmetic rather than a bitwise operator — so a subject that reached for a conjunction, a
    /// disjunction or an ordinary sum is caught.</remarks>
    public static BigInteger BinaryPolynomialSum(BigInteger left, BigInteger right, int width) {
        var sum = BigInteger.Zero;

        for (var bit = 0; (bit < width); ++bit) {
            var total = ((((left >> bit) & BigInteger.One) + ((right >> bit) & BigInteger.One)) % 2);

            if (!total.IsZero) { sum |= (BigInteger.One << bit); }
        }

        return sum;
    }

    /// <summary>The Euclidean quotient and remainder over <c>GF(2)[t]</c>, derived from LINEARITY over the monomial
    /// basis rather than by long division: the pair <c>(t^i div d, t^i mod d)</c> is carried forward one exponent at a
    /// time by a shift and a conditional fold, and the answer is the exclusive or of the pairs at the dividend's set
    /// bits.</summary>
    /// <param name="dividend">The dividend as a coefficient bitmask.</param>
    /// <param name="divisor">The divisor as a coefficient bitmask; must be non-zero.</param>
    /// <returns>The quotient and the remainder.</returns>
    /// <remarks>
    /// From <c>t^i = Q_i·d + R_i</c> and <c>t^(i+1) = (Q_i·t)·d + (R_i·t)</c>, a fold is needed exactly when
    /// <c>R_i·t</c> reaches the divisor's degree, and it moves one into the quotient: <c>Q_(i+1) = Q_i·t + 1</c> and
    /// <c>R_(i+1) = R_i·t + d</c>. Reduction over <c>GF(2)</c> is linear, so summing the pairs at the dividend's set
    /// bits gives its own quotient and remainder; uniqueness of Euclidean division makes that the same answer the
    /// subject's top-down long division reaches by a completely different route — this one never inspects the
    /// dividend's leading term at all.
    /// </remarks>
    public static (BigInteger Quotient, BigInteger Remainder) BinaryPolynomialDivRem(BigInteger dividend, BigInteger divisor) {
        var dividendDegree = BinaryPolynomialDegree(value: dividend);
        var divisorDegree = BinaryPolynomialDegree(value: divisor);
        var quotient = BigInteger.Zero;
        var remainder = BigInteger.Zero;
        // t^0 against the divisor: the constant one is the only degree-zero polynomial over GF(2), and it divides
        // everything exactly, so its own pair is (1, 0) where every higher-degree divisor's is (0, 1).
        var monomialQuotient = ((divisorDegree == 0) ? BigInteger.One : BigInteger.Zero);
        var monomialRemainder = ((divisorDegree == 0) ? BigInteger.Zero : BigInteger.One);

        for (var bit = 0; (bit <= dividendDegree); ++bit) {
            if (!((dividend >> bit) & BigInteger.One).IsZero) {
                quotient ^= monomialQuotient;
                remainder ^= monomialRemainder;
            }

            monomialQuotient <<= 1;
            monomialRemainder <<= 1;

            if (BinaryPolynomialDegree(value: monomialRemainder) == divisorDegree) {
                monomialRemainder ^= divisor;
                monomialQuotient ^= BigInteger.One;
            }
        }

        return (Quotient: quotient, Remainder: remainder);
    }

    /// <summary>The monic greatest common divisor over <c>GF(2)[t]</c>, by the BINARY descent: strip the common power
    /// of <c>t</c>, then repeatedly exclusive-or the lower-degree operand into the higher and strip the <c>t</c>s that
    /// exclusive or creates.</summary>
    /// <param name="left">The first operand as a coefficient bitmask.</param>
    /// <param name="right">The second operand as a coefficient bitmask.</param>
    /// <returns>The greatest common divisor; when one operand is zero, the other.</returns>
    /// <remarks>Every non-zero polynomial over the two-element field is monic, so "monic" costs no normalization step
    /// here. Once the common power of <c>t</c> is stripped both operands have a non-zero constant term, their exclusive
    /// or therefore has a zero one, and <c>t</c> divides neither operand — so stripping the <c>t</c>s from the
    /// difference preserves the common divisor while strictly lowering the total degree, which is what makes the loop
    /// halt. The subject runs the classical Euclidean loop, one long division per step; this performs no division
    /// anywhere.</remarks>
    public static BigInteger BinaryPolynomialGcd(BigInteger left, BigInteger right) {
        if (left.IsZero) { return right; }
        if (right.IsZero) { return left; }

        var a = left;
        var b = right;
        var common = Math.Min(val1: TrailingZeroes(value: a), val2: TrailingZeroes(value: b));

        a >>= TrailingZeroes(value: a);
        b >>= TrailingZeroes(value: b);

        while (a != b) {
            if (BinaryPolynomialDegree(value: a) < BinaryPolynomialDegree(value: b)) { (a, b) = (b, a); }

            a ^= b;
            a >>= TrailingZeroes(value: a);
        }

        return (a << common);
    }

    /// <summary>Whether a <c>GF(2)[t]</c> polynomial is irreducible, by EXHAUSTIVE TRIAL DIVISION against every monic
    /// polynomial of degree one through half its own — the definition.</summary>
    /// <param name="value">The polynomial as a coefficient bitmask.</param>
    /// <returns><see langword="true"/> when no proper divisor exists.</returns>
    /// <remarks>The subject's decision is Ben-Or/Rabin — repeated Frobenius exponentiation and greatest common divisors
    /// inside a binary field — so the two share no procedure at all. The cost here is <c>2^(⌊deg/2⌋+1)</c> remainders,
    /// so callers keep the degree modest.</remarks>
    public static bool BinaryPolynomialIsIrreducible(BigInteger value) {
        var degree = BinaryPolynomialDegree(value: value);

        // A unit and the zero polynomial are not irreducible; the notion begins at degree one.
        if (degree < 1) { return false; }

        for (var divisorDegree = 1; (divisorDegree <= (degree / 2)); ++divisorDegree) {
            var leading = (BigInteger.One << divisorDegree);

            for (var tail = BigInteger.Zero; (tail < leading); ++tail) {
                if (ReduceBinary(value: value, modulus: (leading | tail)).IsZero) { return false; }
            }
        }

        return true;
    }

    /// <summary>Every divisor of <c>2^degree − 1</c> in ascending order, from an independent trial-division
    /// factorization.</summary>
    /// <param name="degree">The extension degree, in <c>[1, 32]</c>.</param>
    /// <returns>The ascending divisors; the last is <c>2^degree − 1</c> itself.</returns>
    /// <remarks>The subject's own primitivity decision factors the same order with the shipped prime-factor
    /// enumerator; nothing of that is reached here. Exact in the machine carrier by construction: <c>2^32 − 1</c> and
    /// every divisor of it fit a <see cref="ulong"/> with room to spare.</remarks>
    public static ulong[] AscendingMersenneDivisors(int degree) {
        var divisors = new List<ulong> { 1UL, };
        var remaining = ((1UL << degree) - 1UL);

        // 2^degree − 1 is odd at every degree, so the even candidates are skipped rather than tested.
        for (var candidate = 3UL; ((candidate * candidate) <= remaining); candidate += 2UL) {
            if (0UL != (remaining % candidate)) { continue; }

            // The products are formed from the divisors of the PRECEDING primes only, so each prime contributes its
            // own powers exactly once.
            var settled = divisors.Count;
            var power = 1UL;

            while (0UL == (remaining % candidate)) {
                remaining /= candidate;
                power *= candidate;

                for (var index = 0; (index < settled); ++index) { divisors.Add(item: (divisors[index] * power)); }
            }
        }

        if (1UL < remaining) {
            var settled = divisors.Count;

            for (var index = 0; (index < settled); ++index) { divisors.Add(item: (divisors[index] * remaining)); }
        }

        divisors.Sort();

        return [.. divisors];
    }

    /// <summary>Whether a <c>GF(2)[t]</c> polynomial is primitive, and the multiplicative order of <c>t</c> modulo it:
    /// the least divisor <c>e</c> of <c>2^degree − 1</c> with <c>t^e ≡ 1</c>, or zero when no divisor works.</summary>
    /// <param name="modulus">The polynomial as a coefficient bitmask.</param>
    /// <param name="ascendingDivisors">Every divisor of <c>2^deg(modulus) − 1</c>, ascending.</param>
    /// <returns>Whether the polynomial is primitive, and the order of <c>t</c>.</returns>
    /// <remarks>
    /// <para>
    /// This is a COMPLETE characterization and it needs no separate irreducibility decision: if <c>t</c> has order
    /// exactly <c>2^d − 1</c> in <c>GF(2)[t]/(f)</c> then its powers are that many distinct elements of a ring with
    /// <c>2^d</c> elements, so every non-zero element is a power of <c>t</c> and therefore a unit, so the ring is a
    /// field and <c>f</c> is irreducible. A zero order covers both a reducible modulus and one <c>t</c> divides.
    /// </para>
    /// <para>
    /// The squaring ladder of <c>t</c> is built once and shared by every divisor's square-and-multiply, which changes
    /// no value — each power is still assembled from the exponent's own binary expansion — and is what keeps an
    /// exhaustive census affordable.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="ascendingDivisors"/> does not end at
    /// <c>2^deg(modulus) − 1</c>, which would mean the caller paired the wrong degree's divisors with this modulus.</exception>
    public static (bool Primitive, ulong Order) BinaryPolynomialIsPrimitive(BigInteger modulus, ReadOnlySpan<ulong> ascendingDivisors) {
        var degree = BinaryPolynomialDegree(value: modulus);

        if (degree < 1) { return (Primitive: false, Order: 0UL); }

        var groupOrder = ((1UL << degree) - 1UL);

        if ((ascendingDivisors.Length == 0) || (ascendingDivisors[^1] != groupOrder)) {
            throw new ArgumentException(message: "The divisor list does not belong to this modulus's degree.", paramName: nameof(ascendingDivisors));
        }

        var ladder = RootSquarings(modulus: modulus, bound: groupOrder);

        foreach (var exponent in ascendingDivisors) {
            if (RootPower(exponent: exponent, modulus: modulus, ladder: ladder).IsOne) {
                return (Primitive: (exponent == groupOrder), Order: exponent);
            }
        }

        return (Primitive: false, Order: 0UL);
    }

    /// <summary>The DEGREES of the distinct monic irreducible factors of <c>t^n + 1</c> over the two-element field,
    /// ascending, from the 2-cyclotomic cosets modulo <paramref name="cycleOrder"/>.</summary>
    /// <param name="cycleOrder">An odd positive order.</param>
    /// <returns>One degree per orbit of <c>x ↦ 2x</c> on <c>Z/nZ</c>, ascending.</returns>
    /// <remarks>The classical structure of <c>t^n − 1</c> over <c>GF(2)</c> for odd <c>n</c>: its roots are the
    /// <c>n</c>-th roots of unity, the Frobenius map permutes them by squaring, and each orbit of that permutation is
    /// the root set of one irreducible factor whose degree is the orbit's size. The computation is purely
    /// combinatorial — no polynomial is multiplied, divided or reduced anywhere in it, where the subject enumerates
    /// monic candidates, tests each for irreducibility and divides.</remarks>
    public static int[] BinaryCyclotomicFactorDegrees(int cycleOrder) {
        var seen = new bool[cycleOrder];
        var degrees = new List<int>();

        for (var start = 0; (start < cycleOrder); ++start) {
            if (seen[start]) { continue; }

            var size = 0;
            var element = start;

            do {
                seen[element] = true;
                element = ((2 * element) % cycleOrder);
                ++size;
            } while (element != start);

            degrees.Add(item: size);
        }

        degrees.Sort();

        return [.. degrees];
    }

    /// <summary>The conventional written form of a <c>GF(2)</c> coefficient bit-vector, such as <c>t^5+t^2+1</c>.</summary>
    /// <param name="value">The coefficient bitmask.</param>
    /// <param name="width">The number of coefficient positions to consider.</param>
    /// <returns>The terms in descending exponent order, or <c>0</c> for the zero polynomial.</returns>
    /// <remarks>The subject walks its own degree downward into a builder, placing a separator before each term after
    /// the first. This collects terms into a list and joins them, and it takes its upper bound from the caller rather
    /// than from any degree reader — so a wrong degree, a misplaced separator or a reversed loop is visible.</remarks>
    public static string BinaryPolynomialText(BigInteger value, int width) {
        var terms = new List<string>();

        for (var exponent = (width - 1); (exponent >= 0); --exponent) {
            if (((value >> exponent) & BigInteger.One).IsZero) { continue; }

            terms.Add(item: (exponent switch {
                0 => "1",
                1 => "t",
                _ => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, $"t^{exponent}"),
            }));
        }

        return ((terms.Count == 0) ? "0" : string.Join(separator: "+", values: terms));
    }

    // The number of leading zero COEFFICIENTS — the multiplicity of t as a factor. Never called on the zero
    // polynomial, whose multiplicity is not finite; both gcd operands are non-zero by the time it is reached.
    private static int TrailingZeroes(BigInteger value) {
        var count = 0;

        while (((value >> count) & BigInteger.One).IsZero) { ++count; }

        return count;
    }

    // Top-down long division against a modulus materialized in full. It backs the irreducibility and order oracles,
    // BinaryFieldReduce and BinaryFieldInverse's final normalization — never the reference for BinaryPolynomial's own
    // division, whose oracle is the bottom-up monomial route above. Two deliberately different reduction routes live
    // in this module so neither statement leans on the other, and the binary FIELD's subject is a third route again:
    // the tail fold, which never materializes the leading term this one divides by.
    private static BigInteger ReduceBinary(BigInteger value, BigInteger modulus) {
        var modulusDegree = BinaryPolynomialDegree(value: modulus);
        var remainder = value;

        for (var degree = BinaryPolynomialDegree(value: remainder); (degree >= modulusDegree); degree = BinaryPolynomialDegree(value: remainder)) {
            remainder ^= (modulus << (degree - modulusDegree));
        }

        return remainder;
    }

    // The reduced squarings of t: entry k is t^(2^k) modulo the polynomial, up to the highest bit any exponent bounded
    // by `bound` can carry.
    private static BigInteger[] RootSquarings(BigInteger modulus, ulong bound) {
        var ladder = new List<BigInteger> { ReduceBinary(value: (BigInteger.One << 1), modulus: modulus), };

        for (var bit = 1; ((bound >> bit) != 0UL); ++bit) {
            ladder.Add(item: ReduceBinary(value: CarrylessProduct(left: ladder[bit - 1], right: ladder[bit - 1]), modulus: modulus));
        }

        return [.. ladder];
    }

    // t raised to an exponent modulo the polynomial, assembled from the exponent's binary expansion against the
    // squaring ladder.
    private static BigInteger RootPower(ulong exponent, BigInteger modulus, BigInteger[] ladder) {
        var power = BigInteger.One;

        for (var bit = 0; ((exponent >> bit) != 0UL); ++bit) {
            if (0UL == ((exponent >> bit) & 1UL)) { continue; }

            power = ReduceBinary(value: CarrylessProduct(left: power, right: ladder[bit]), modulus: modulus);
        }

        return power;
    }

    /// <summary>The reference reflexive-transitive closure of a directed graph, by Warshall's triple loop over an
    /// adjacency matrix of bits.</summary>
    /// <param name="adjacency">The adjacency matrix, row-major, <c>order²</c> entries.</param>
    /// <param name="order">The number of vertices.</param>
    /// <param name="result">The closure, row-major, with the diagonal set.</param>
    public static void BooleanTransitiveClosure(ReadOnlySpan<bool> adjacency, int order, Span<bool> result) {
        for (var entry = 0; (entry < (order * order)); ++entry) { result[entry] = adjacency[entry]; }

        for (var vertex = 0; (vertex < order); ++vertex) { result[((vertex * order) + vertex)] = true; }

        for (var middle = 0; (middle < order); ++middle) {
            for (var source = 0; (source < order); ++source) {
                if (!result[((source * order) + middle)]) { continue; }

                for (var target = 0; (target < order); ++target) {
                    if (result[((middle * order) + target)]) { result[((source * order) + target)] = true; }
                }
            }
        }
    }

    /// <summary>The reference all-pairs shortest path over a weighted directed graph, by the Floyd–Warshall triple loop
    /// in exact <see cref="BigInteger"/> arithmetic.</summary>
    /// <param name="weights">The weight matrix, row-major; <see cref="long.MaxValue"/> marks an absent arc.</param>
    /// <param name="order">The number of vertices.</param>
    /// <param name="result">The distances, row-major, with a zero diagonal and <see cref="long.MaxValue"/> where no path
    /// exists.</param>
    /// <remarks>The caller supplies non-negative weights small enough that no path sum leaves the carrier, so this is an
    /// exact statement about <c>(min, +)</c> and never a statement about wrapping.</remarks>
    public static void TropicalShortestPath(ReadOnlySpan<long> weights, int order, Span<long> result) {
        var distance = new BigInteger[(order * order)];
        var infinite = new bool[(order * order)];

        for (var entry = 0; (entry < (order * order)); ++entry) {
            infinite[entry] = (long.MaxValue == weights[entry]);
            distance[entry] = (infinite[entry] ? BigInteger.Zero : weights[entry]);
        }

        for (var vertex = 0; (vertex < order); ++vertex) {
            var diagonal = ((vertex * order) + vertex);

            if (infinite[diagonal] || (distance[diagonal].Sign > 0)) {
                distance[diagonal] = BigInteger.Zero;
                infinite[diagonal] = false;
            }
        }

        for (var middle = 0; (middle < order); ++middle) {
            for (var source = 0; (source < order); ++source) {
                var viaEntry = ((source * order) + middle);

                if (infinite[viaEntry]) { continue; }

                for (var target = 0; (target < order); ++target) {
                    var tailEntry = ((middle * order) + target);

                    if (infinite[tailEntry]) { continue; }

                    var candidate = (distance[viaEntry] + distance[tailEntry]);
                    var entry = ((source * order) + target);

                    if (infinite[entry] || (candidate < distance[entry])) {
                        distance[entry] = candidate;
                        infinite[entry] = false;
                    }
                }
            }
        }

        for (var entry = 0; (entry < (order * order)); ++entry) {
            result[entry] = (infinite[entry] ? long.MaxValue : ((long)distance[entry]));
        }
    }

    /// <summary>The reference best-likelihood route over a graph whose arc weights are closed-unit raws, by explicit
    /// enumeration of the SIMPLE paths — no matrix, no power, no relaxation — with one ties-to-even rounding per step.</summary>
    /// <param name="weights">The weight matrix, row-major; a zero raw marks an absent arc.</param>
    /// <param name="order">The number of vertices.</param>
    /// <param name="result">The best likelihoods, row-major, with the diagonal at one.</param>
    /// <remarks>
    /// <para>
    /// Enumerating simple paths alone is exhaustive here, and that is a THEOREM about the step rather than a
    /// convenience: a step is <c>x ↦ round(x·w)</c> with <c>w</c> at most one, which is monotone and never increases its
    /// argument, so excising a cycle from a walk cannot lower the walk's value. Every walk is therefore dominated by one
    /// of its own simple sub-paths, and the maximum over all walks is attained on a simple path.
    /// </para>
    /// <para>
    /// The fold is left-nested, which is the composition order the subject's repeated product takes, and it is
    /// load-bearing: over a rounding step the value depends on the order the factors are combined, so an oracle that
    /// multiplied the whole path out exactly and rounded once would be answering a different question. That difference
    /// is the campaign's canary, measured rather than assumed.
    /// </para>
    /// </remarks>
    public static void ClosedUnitBestRoute(ReadOnlySpan<ulong> weights, int order, Span<ulong> result) {
        var arcs = weights.ToArray();
        var best = new ulong[(order * order)];
        var visited = new bool[order];

        void Extend(int source, int vertex, ulong value) {
            var entry = ((source * order) + vertex);

            if (value > best[entry]) { best[entry] = value; }

            visited[vertex] = true;

            for (var next = 0; (next < order); ++next) {
                if (visited[next]) { continue; }

                var stepped = ClosedUnitProduct(x: value, y: arcs[((vertex * order) + next)]);

                if (0UL != stepped) { Extend(source: source, vertex: next, value: stepped); }
            }

            visited[vertex] = false;
        }

        for (var source = 0; (source < order); ++source) {
            Array.Clear(array: visited);
            Extend(source: source, vertex: source, value: ClosedUnitOneRaw);
        }

        for (var entry = 0; (entry < best.Length); ++entry) { result[entry] = best[entry]; }
    }

    /// <summary>The reference widest-bottleneck closure of a graph whose arc weights are closed-unit raws, by the
    /// Floyd–Warshall triple loop with maximum and minimum in place of minimum and plus.</summary>
    /// <param name="weights">The weight matrix, row-major; a zero raw marks an absent arc.</param>
    /// <param name="order">The number of vertices.</param>
    /// <param name="result">The widest bottlenecks, row-major, with the diagonal at one.</param>
    /// <remarks>Exact: both operations select an operand, so nothing here rounds and the answer is a statement about the
    /// lattice rather than about a carrier. The diagonal is seeded at one and cannot leak into an off-diagonal entry,
    /// since a minimum against one is the other operand.</remarks>
    public static void ClosedUnitBottleneckClosure(ReadOnlySpan<ulong> weights, int order, Span<ulong> result) {
        for (var entry = 0; (entry < (order * order)); ++entry) { result[entry] = weights[entry]; }

        for (var vertex = 0; (vertex < order); ++vertex) { result[((vertex * order) + vertex)] = ClosedUnitOneRaw; }

        for (var middle = 0; (middle < order); ++middle) {
            for (var source = 0; (source < order); ++source) {
                var head = result[((source * order) + middle)];

                if (0UL == head) { continue; }

                for (var target = 0; (target < order); ++target) {
                    var candidate = ((head < result[((middle * order) + target)]) ? head : result[((middle * order) + target)]);
                    var entry = ((source * order) + target);

                    if (candidate > result[entry]) { result[entry] = candidate; }
                }
            }
        }
    }

    /// <summary>The reference best route over a graph whose arc weights are closed-unit raws under the bounded sum, by
    /// explicit enumeration of the SIMPLE paths in exact <see cref="BigInteger"/> arithmetic.</summary>
    /// <param name="weights">The weight matrix, row-major; a zero raw marks an absent arc.</param>
    /// <param name="order">The number of vertices.</param>
    /// <param name="result">The best route values, row-major, with the diagonal at one.</param>
    /// <remarks>Exact, and the dominance argument of <see cref="ClosedUnitBestRoute"/> holds for the same reason: the
    /// step <c>x ↦ max(0, x + w − 1)</c> is monotone and never increases its argument, so no walk beats its own simple
    /// sub-paths. A route survives only while the shortfalls of its steps from one sum to less than one, which is why a
    /// long route collapses to zero here where the fuzzy material would keep it.</remarks>
    public static void ClosedUnitBoundedSumRoute(ReadOnlySpan<ulong> weights, int order, Span<ulong> result) {
        var arcs = weights.ToArray();
        var best = new ulong[(order * order)];
        var one = new BigInteger(value: ClosedUnitOneRaw);
        var visited = new bool[order];

        void Extend(int source, int vertex, ulong value) {
            var entry = ((source * order) + vertex);

            if (value > best[entry]) { best[entry] = value; }

            visited[vertex] = true;

            for (var next = 0; (next < order); ++next) {
                if (visited[next]) { continue; }

                var excess = ((new BigInteger(value: value) + arcs[((vertex * order) + next)]) - one);

                if (excess.Sign > 0) { Extend(source: source, vertex: next, value: ((ulong)excess)); }
            }

            visited[vertex] = false;
        }

        for (var source = 0; (source < order); ++source) {
            Array.Clear(array: visited);
            Extend(source: source, vertex: source, value: ClosedUnitOneRaw);
        }

        for (var entry = 0; (entry < best.Length); ++entry) { result[entry] = best[entry]; }
    }

    // The closed unit interval's upper endpoint as a raw, re-derived here rather than read off the carrier: an oracle
    // that borrowed the subject's own constant would agree with it about the grid by construction.
    private const ulong ClosedUnitOneRaw = (1UL << 32);

    /// <summary>The reference count of directed walks of a fixed length between every ordered pair of vertices, by
    /// REPEATED <see cref="BigInteger"/> matrix multiplication.</summary>
    /// <param name="adjacency">The adjacency matrix, row-major, <c>order²</c> entries.</param>
    /// <param name="order">The number of vertices.</param>
    /// <param name="length">The walk length; zero yields the identity matrix.</param>
    /// <param name="result">The walk counts, row-major.</param>
    /// <remarks>Repeated multiplication rather than square-and-multiply, so the schedule differs from the subject's even
    /// though the exact arithmetic makes the values agree.</remarks>
    public static void WalkCount(ReadOnlySpan<BigInteger> adjacency, int order, int length, Span<BigInteger> result) {
        var accumulator = new BigInteger[(order * order)];
        var scratch = new BigInteger[(order * order)];
        var matrix = new BigInteger[(order * order)];

        for (var entry = 0; (entry < (order * order)); ++entry) {
            accumulator[entry] = BigInteger.Zero;
            matrix[entry] = adjacency[entry];
        }

        for (var vertex = 0; (vertex < order); ++vertex) { accumulator[((vertex * order) + vertex)] = BigInteger.One; }

        for (var step = 0; (step < length); ++step) {
            for (var source = 0; (source < order); ++source) {
                for (var target = 0; (target < order); ++target) {
                    var total = BigInteger.Zero;

                    for (var middle = 0; (middle < order); ++middle) {
                        total += (accumulator[((source * order) + middle)] * matrix[((middle * order) + target)]);
                    }

                    scratch[((source * order) + target)] = total;
                }
            }

            scratch.CopyTo(array: accumulator, index: 0);
        }

        for (var entry = 0; (entry < (order * order)); ++entry) { result[entry] = accumulator[entry]; }
    }

    /// <summary>The reference coefficients of <c>det(I − tA)</c>, by PRINCIPAL-MINOR enumeration: the coefficient of
    /// <c>t^k</c> is <c>(−1)^k</c> times the sum of the matrix's <c>k</c>-by-<c>k</c> principal minors.</summary>
    /// <param name="matrix">The matrix, row-major, <c>order²</c> entries.</param>
    /// <param name="order">The matrix order.</param>
    /// <param name="result">The coefficients, low degree first, <c>order + 1</c> entries.</param>
    /// <remarks>It shares no step with the trace recursion it answers for: no power of the matrix is formed, no trace is
    /// taken, and nothing is divided anywhere — each minor is the full permutation expansion signed by counting
    /// inversions, which is why this stays honest at an order where the subject's recursion would need an inverse the
    /// material does not have.</remarks>
    public static void CharacteristicPolynomial(ReadOnlySpan<BigInteger> matrix, int order, Span<BigInteger> result) {
        var choice = new int[order];

        result[0] = BigInteger.One;

        for (var size = 1; (size <= order); ++size) {
            var total = BigInteger.Zero;

            for (var index = 0; (index < size); ++index) { choice[index] = index; }

            while (true) {
                total += PrincipalMinor(matrix: matrix, order: order, rows: choice.AsSpan(start: 0, length: size));

                var cursor = (size - 1);

                while ((cursor >= 0) && (choice[cursor] == ((order - size) + cursor))) { --cursor; }

                if (cursor < 0) { break; }

                ++choice[cursor];

                for (var index = (cursor + 1); (index < size); ++index) { choice[index] = (choice[(index - 1)] + 1); }
            }

            result[size] = (((0 == (size & 1)) ? total : -total));
        }
    }

    // One principal minor by the full permutation expansion, signed by inversion count. Division-free and pivot-free, so
    // it shares nothing with any elimination the subject might run.
    private static BigInteger PrincipalMinor(ReadOnlySpan<BigInteger> matrix, int order, ReadOnlySpan<int> rows) {
        var size = rows.Length;
        var permutation = new int[size];
        var total = BigInteger.Zero;

        for (var index = 0; (index < size); ++index) { permutation[index] = index; }

        while (true) {
            var term = BigInteger.One;
            var inversions = 0;

            for (var index = 0; (index < size); ++index) {
                term *= matrix[((rows[index] * order) + rows[permutation[index]])];

                for (var other = (index + 1); (other < size); ++other) {
                    if (permutation[index] > permutation[other]) { ++inversions; }
                }
            }

            total += ((0 == (inversions & 1)) ? term : -term);

            var pivot = (size - 2);

            while ((pivot >= 0) && (permutation[pivot] >= permutation[(pivot + 1)])) { --pivot; }

            if (pivot < 0) { return total; }

            var swap = (size - 1);

            while (permutation[swap] <= permutation[pivot]) { --swap; }

            (permutation[pivot], permutation[swap]) = (permutation[swap], permutation[pivot]);
            permutation.AsSpan(start: (pivot + 1)).Reverse();
        }
    }

    /// <summary>The reference determinant of a three-by-three integer matrix, by first-row cofactor expansion.</summary>
    /// <param name="rows">The nine entries, row-major.</param>
    /// <returns>The determinant.</returns>
    /// <remarks>Shares nothing with the top-grade blade coefficient it is the oracle for: no generator ordering, no
    /// permutation sign and no algebra, only three two-by-two minors.</remarks>
    public static BigInteger Determinant3(ReadOnlySpan<BigInteger> rows) =>
        ((rows[0] * ((rows[4] * rows[8]) - (rows[5] * rows[7])))
            - (rows[1] * ((rows[3] * rows[8]) - (rows[5] * rows[6])))
            + (rows[2] * ((rows[3] * rows[7]) - (rows[4] * rows[6]))));

    /// <summary>The reference product of two polynomials modulo a monic tail and a prime, by schoolbook multiplication
    /// and top-down reduction.</summary>
    /// <param name="left">The multiplicand's coefficients, low exponent first.</param>
    /// <param name="right">The multiplier's coefficients, low exponent first.</param>
    /// <param name="tail">The modulus tail <c>[m₀ … m_{n−1}]</c> of <c>xⁿ + m_{n−1}x^{n−1} + … + m₀</c>, so
    /// <c>xⁿ ≡ −Σ mⱼxʲ</c>.</param>
    /// <param name="modulus">The prime.</param>
    /// <param name="result">The reduced coefficients, low exponent first, the same length as the tail.</param>
    /// <remarks>Every step is a <see cref="BigInteger"/> remainder: no Montgomery form, no Barrett reduction, no field
    /// descriptor — nothing the subject's prime-field material or monogenic reduction shares.</remarks>
    public static void PrimeFieldPolynomialProduct(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, ReadOnlySpan<ulong> tail, ulong modulus, Span<ulong> result) {
        var degree = tail.Length;
        var wide = new BigInteger[((2 * degree) - 1)];

        for (var index = 0; (index < wide.Length); ++index) { wide[index] = BigInteger.Zero; }

        for (var leftIndex = 0; (leftIndex < degree); ++leftIndex) {
            for (var rightIndex = 0; (rightIndex < degree); ++rightIndex) {
                wide[(leftIndex + rightIndex)] += ((BigInteger)left[leftIndex] * right[rightIndex]);
            }
        }

        for (var exponent = (wide.Length - 1); (exponent >= degree); --exponent) {
            var leading = (wide[exponent] % modulus);

            wide[exponent] = BigInteger.Zero;

            for (var offset = 0; (offset < degree); ++offset) {
                wide[((exponent - degree) + offset)] -= (leading * tail[offset]);
            }
        }

        for (var exponent = 0; (exponent < degree); ++exponent) {
            result[exponent] = ((ulong)(((wide[exponent] % modulus) + modulus) % modulus));
        }
    }

    // ---- the odd-characteristic references ----
    // Nothing below rounds: every answer is an exact integer, so the module's ties-to-even discipline does not arise
    // here at all. What these references are for is the OTHER failure modes — a wrong modulus fold, a Montgomery
    // representation leaking into an answer, a width or carry edge — and each is written to reach its answer by a
    // different ROUTE from the subject it stands against, not merely by different code.

    /// <summary>The primes below 8192, sieved once — the trial-division screen the primality references open
    /// with.</summary>
    private static readonly uint[] SmallPrimeTable = BuildSmallPrimes();

    /// <summary>The primality flags for every value through <paramref name="inclusiveMaximum"/>, by the sieve of
    /// Eratosthenes.</summary>
    /// <param name="inclusiveMaximum">The largest value to decide.</param>
    /// <returns>One flag per value from zero through <paramref name="inclusiveMaximum"/>.</returns>
    /// <remarks>The one reference in this module that carries no notion of a witness, a base or an exponent: it
    /// crosses out multiples and reads the survivors. That is what makes it the strongest anchor the primality laws
    /// have — it shares with <see cref="PrimeField64.IsPrime(ulong)"/> not merely no code but no IDEA.</remarks>
    public static bool[] PrimeSieve(int inclusiveMaximum) {
        var flags = new bool[(inclusiveMaximum + 1)];

        for (var index = 2; (index <= inclusiveMaximum); ++index) { flags[index] = true; }

        for (var candidate = 2; ((candidate * candidate) <= inclusiveMaximum); ++candidate) {
            if (!flags[candidate]) { continue; }

            for (var multiple = (candidate * candidate); (multiple <= inclusiveMaximum); multiple += candidate) { flags[multiple] = false; }
        }

        return flags;
    }

    /// <summary>The primes below 8192, sieved once by <see cref="PrimeSieve(int)"/>.</summary>
    /// <remarks>Trial division by this list decides every value through <c>8191² = 67092481</c> — just under
    /// <c>2²⁶</c> — outright, with no probable-prime reasoning anywhere in the answer. Deliberately 8192 rather than
    /// 65536: the screen costs one remainder per entry on every candidate that reaches the rounds, and that band is
    /// already well past the region the exhaustive-sieve statements cover directly.</remarks>
    public static ReadOnlySpan<uint> SmallPrimes => SmallPrimeTable;

    /// <summary>The first twenty prime bases, a strict SUPERSET of the twelve
    /// <see cref="PrimeField64.IsPrime(ulong)"/> runs.</summary>
    public static ReadOnlySpan<ulong> StrongPrimeWitnessBases => [2UL, 3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 19UL, 23UL, 29UL, 31UL, 37UL, 41UL, 43UL, 47UL, 53UL, 59UL, 61UL, 67UL, 71UL];

    /// <summary>The Jacobi symbol of <paramref name="numerator"/> over an odd positive
    /// <paramref name="denominator"/>, by the binary quadratic-reciprocity descent in
    /// <see cref="BigInteger"/>.</summary>
    /// <param name="numerator">The upper argument, of either sign; it is normalized into the denominator's range
    /// first.</param>
    /// <param name="denominator">The lower argument, which must be odd and positive.</param>
    /// <returns><c>0</c> when the two share a factor, <c>1</c> when the numerator is a residue, <c>-1</c>
    /// otherwise.</returns>
    /// <remarks>Calls no Puck.Maths member — in particular it is neither <c>NumberTheoryFunctions.JacobiSymbol</c>
    /// nor <c>UnsignedNumberFunctions.JacobiSymbol</c>, both of which are SUBJECTS of core.jacobi-symbol-cross-carrier's jacobi
    /// statement. Written against the shipped kernels in two ways a reader can check: those accumulate the sign in bit
    /// zero of a parity word through the <c>((lower &gt;&gt; 1) ^ lower) &gt;&gt; 1</c> bit identities and strip the
    /// whole trailing-zero run in one <c>TrailingZeroCount</c>, where this carries a signed accumulator, tests
    /// <c>% 8</c> and <c>% 4</c> literally, and strips one factor of two at a time. It is also a wholly different
    /// derivation route from <see cref="PrimeField64.LegendreCharacter(ulong)"/>, which decides the character by
    /// Euler's exponentiation criterion and never touches reciprocity at all.</remarks>
    public static int JacobiSymbolReciprocity(BigInteger numerator, BigInteger denominator) {
        var lower = denominator;
        var upper = (((numerator % lower) + lower) % lower);
        var sign = 1;

        while (!upper.IsZero) {
            // One factor of two at a time, each flipping the sign exactly where the (2/n) rule says it does. Stated
            // as a residue test rather than as a parity bit identity, which is the whole point of this spelling.
            while (upper.IsEven) {
                upper >>= 1;

                var residue = (lower % 8);

                if ((3 == residue) || (5 == residue)) { sign = -sign; }
            }

            if ((3 == (upper % 4)) && (3 == (lower % 4))) { sign = -sign; }

            (lower, upper) = (upper, (lower % upper));
        }

        return (lower.IsOne ? sign : 0);
    }

    /// <summary>One strong-probable-prime round of <paramref name="value"/> to base <paramref name="witness"/>,
    /// evaluated entirely in <see cref="BigInteger"/> plain residues.</summary>
    /// <param name="value">The candidate.</param>
    /// <param name="witness">The witness base, reduced modulo the candidate first.</param>
    /// <returns><see langword="true"/> when the candidate is a strong probable prime to that base.</returns>
    /// <remarks>A base that reduces to zero carries no evidence and PASSES, which is the contract
    /// <see cref="PrimeField64.IsStrongProbablePrime(ulong, ulong)"/> states. Plain residues from first to last —
    /// <see cref="BigInteger.ModPow(BigInteger, BigInteger, BigInteger)"/> and <c>%</c>, never a Montgomery encoding
    /// and never a ring's own one or minus one — where the subject makes its whole acceptance test against the
    /// ring's encoded constants and never leaves Montgomery form. The CRITERION is shared and the legs say so; what
    /// disagreement here catches is the arithmetic, the <c>d·2^s</c> split and a representation leak.</remarks>
    public static bool ModularStrongProbablePrime(ulong value, ulong witness) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }

        var residue = (witness % value);

        if (0UL == residue) { return true; }

        var oddPart = (value - 1UL);
        var twoExponent = 0;

        while (0UL == (oddPart & 1UL)) {
            oddPart >>= 1;
            ++twoExponent;
        }

        var wide = new BigInteger(value: value);
        var last = (wide - BigInteger.One);
        var power = BigInteger.ModPow(value: new BigInteger(value: residue), exponent: new BigInteger(value: oddPart), modulus: wide);

        if (power.IsOne || (power == last)) { return true; }

        for (var round = 1; (round < twoExponent); ++round) {
            power = ((power * power) % wide);

            if (power == last) { return true; }
        }

        return false;
    }

    /// <summary>The EXACT primality decision for every <see cref="ulong"/>, in two layers: trial division by
    /// <see cref="SmallPrimes"/>, then twenty strong-probable-prime rounds against
    /// <see cref="StrongPrimeWitnessBases"/>.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns><see langword="true"/> when the candidate is prime.</returns>
    /// <remarks>
    /// <para>
    /// Trial division alone decides every value through <c>8191² = 67092481</c>, with no probable-prime reasoning in
    /// the answer at all. Above that the rounds decide, and the decision is still exact: the first TWELVE prime bases
    /// are a proven complete witness set for every value below Sorenson and Webster's computed
    /// <c>ψ₁₂ = 318665857834031151167461 ≈ 3.19 × 10²³</c>, four orders of magnitude past
    /// <see cref="ulong.MaxValue"/>, and twenty bases are a strict superset — extra bases can only reject more
    /// composites, and every prime passes every base. That provenance is a third-party exhaustive computation, the
    /// same epistemic class as the Baillie–PSW guarantee, which likewise rests on a third-party enumeration —
    /// Feitsma's and Galway's independent lists of the base-2 Fermat pseudoprimes below <c>2⁶⁴</c> — rather than on a
    /// proof from first principles. Neither is stronger than the other; do not describe one that way.
    /// </para>
    /// <para>
    /// Deliberately OUTSIDE Puck.Maths rather than borrowing <see cref="PrimeField64.IsPrime(ulong)"/>, so that a
    /// future re-pointing of that member at the Baillie–PSW composition turns no tier of this family into a tautology.
    /// </para>
    /// </remarks>
    public static bool ExactPrimality(ulong value) {
        if (2UL > value) { return false; }

        // Fixed-width and exact: the screen's operands are the candidate itself and a prime below 8192, whose square
        // is below 2^26, so nothing here can leave the carrier and BigInteger would buy only wall time.
        foreach (var small in SmallPrimeTable) {
            var prime = ((ulong)small);

            if ((prime * prime) > value) { return true; }
            if (0UL == (value % prime)) { return (value == prime); }
        }

        foreach (var witness in StrongPrimeWitnessBases) {
            if (!ModularStrongProbablePrime(value: value, witness: witness)) { return false; }
        }

        return true;
    }

    /// <summary>The strong Lucas probable-prime test with Selfridge's Method A parameters, computed from
    /// COMPANION-MATRIX powers in <see cref="BigInteger"/>.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns><see langword="true"/> when the candidate is a strong Lucas probable prime.</returns>
    /// <remarks>
    /// <para>
    /// Independent of <see cref="PrimeField64.IsStrongLucasProbablePrime(ulong)"/> in three deliberate ways. The
    /// subject walks the <c>U</c>/<c>V</c> doubling identities most-significant-bit first, HALVING on every
    /// index-incrementing step, entirely inside a Montgomery ring; this multiplies two-by-two matrices in plain
    /// residues, halves nothing anywhere, and carries no <c>V</c> recurrence of its own at all — it derives
    /// <c>V_d</c> from two consecutive <c>U</c> terms through <c>V_n = 2·U_(n+1) − P·U_n</c>. Its Selfridge search
    /// carries the discriminant's sign EXPLICITLY, where the subject reads the sign off <c>magnitude &amp; 3</c>, and
    /// takes its symbol from <see cref="JacobiSymbolReciprocity(BigInteger, BigInteger)"/> rather than from any
    /// shipped Jacobi. A wrong index-incrementing pair, a dropped <c>Q^k</c> squaring, or a halving that leaves the
    /// carrier shows here and nowhere else.
    /// </para>
    /// <para>
    /// It is <c>O(log n)</c>, so a law standing on it reaches the whole carrier rather than the small band an
    /// index-by-index recurrence oracle can afford.
    /// </para>
    /// </remarks>
    public static bool StrongLucasSelfridge(ulong value) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }
        if (PerfectSquareByBisection(value: value)) { return false; }

        var wide = new BigInteger(value: value);
        var discriminant = new BigInteger(value: 5);
        var step = 0;

        // Method A: 5, −7, 9, −11, 13, … with the sign carried as a sign, not derived from a magnitude's low bits.
        while (true) {
            var symbol = JacobiSymbolReciprocity(numerator: discriminant, denominator: wide);

            if (-1 == symbol) { break; }
            // A vanishing symbol means the candidate shares a factor with the discriminant. That factor is a proper
            // divisor unless the value divides the discriminant outright, which leaves the search uninformed.
            if ((0 == symbol) && !(discriminant % wide).IsZero) { return false; }

            ++step;

            var magnitude = (BigInteger.Abs(value: discriminant) + 2);

            discriminant = ((1 == (step & 1)) ? (-magnitude) : magnitude);
        }

        // P = 1 and Q = (1 − D)/4, exact in BigInteger in either sign class: no shift, no ring negation.
        var q = (((((BigInteger.One - discriminant) / 4) % wide) + wide) % wide);
        var order = (wide + BigInteger.One);
        var twoExponent = 0;

        while (order.IsEven) {
            order >>= 1;
            ++twoExponent;
        }

        var (atIndex, atNextIndex) = LucasNumeratorPair(q: q, index: ((ulong)order), modulus: wide);
        var v = (((((2 * atNextIndex) - atIndex) % wide) + wide) % wide);

        if (atIndex.IsZero || v.IsZero) { return true; }

        var qPower = BigInteger.ModPow(value: q, exponent: order, modulus: wide);

        for (var round = 1; (round < twoExponent); ++round) {
            v = (((((v * v) - (2 * qPower)) % wide) + wide) % wide);

            if (v.IsZero) { return true; }

            qPower = ((qPower * qPower) % wide);
        }

        return false;
    }

    /// <summary>The reference modular inverse, by the EXTENDED EUCLIDEAN algorithm.</summary>
    /// <param name="value">The value to invert; must be coprime to <paramref name="modulus"/>.</param>
    /// <param name="modulus">The modulus, above one.</param>
    /// <returns>The representative in <c>[0, modulus)</c> whose product with <paramref name="value"/> is one.</returns>
    /// <remarks>Euclid's algorithm is a DIFFERENT THEOREM from the Fermat exponentiation the subject reaches its base
    /// inverse by — <see cref="PrimeField64.Inverse(ulong)"/> evaluates <c>value^(p − 2)</c> — and it runs in plain
    /// residues with no Montgomery form anywhere. Exact on both sides, so the module's rounding discipline does not
    /// arise.</remarks>
    /// <exception cref="ArgumentException">The greatest common divisor is not one, so no inverse exists.</exception>
    public static BigInteger ModularInverse(BigInteger value, BigInteger modulus) {
        var remainder = (((value % modulus) + modulus) % modulus);
        var previousRemainder = modulus;
        var coefficient = BigInteger.One;
        var previousCoefficient = BigInteger.Zero;

        while (!remainder.IsZero) {
            var quotient = BigInteger.Divide(dividend: previousRemainder, divisor: remainder);

            (previousRemainder, remainder) = (remainder, (previousRemainder - (quotient * remainder)));
            (previousCoefficient, coefficient) = (coefficient, (previousCoefficient - (quotient * coefficient)));
        }

        if (!previousRemainder.IsOne) {
            throw new ArgumentException(message: "The value is not invertible modulo the modulus.", paramName: nameof(value));
        }

        return (((previousCoefficient % modulus) + modulus) % modulus);
    }

    /// <summary>The reference quadratic character by EULER'S CRITERION, evaluated with
    /// <see cref="BigInteger"/>'s own modular exponentiation.</summary>
    /// <param name="value">The value whose character is taken, of either sign.</param>
    /// <param name="modulus">The odd prime.</param>
    /// <returns><c>0</c> at a value congruent to zero, <c>1</c> at a non-zero square, and <c>-1</c> at a
    /// non-square.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="BigInteger.ModPow(BigInteger, BigInteger, BigInteger)"/> in PLAIN residues, where the subject's
    /// <see cref="PrimeField64.LegendreCharacter(ulong)"/> runs its exponentiation in <c>ScaledResidueRing64</c>'s
    /// Montgomery form. The argument is reduced BEFORE the test, which is the difference that matters: this reference
    /// answers <c>0</c> for every value congruent to zero, including the unreduced ones the subject answers <c>-1</c>
    /// for.
    /// </para>
    /// <para>
    /// The last line maps every non-one power to <c>-1</c>. For a prime modulus that power is <c>1</c> or
    /// <c>modulus − 1</c> and nothing else, and every caller passes a prime, so no third case exists;
    /// <c>extension-field.construction-and-refusals</c> additionally asserts the <c>modulus − 1</c> half on the
    /// answers it uses, so a composite slipping in would be caught rather than silently classified.
    /// </para>
    /// </remarks>
    public static int PrimeFieldCharacter(BigInteger value, BigInteger modulus) {
        var residue = (((value % modulus) + modulus) % modulus);

        if (residue.IsZero) { return 0; }

        var power = BigInteger.ModPow(value: residue, exponent: ((modulus - BigInteger.One) / 2), modulus: modulus);

        return (power.IsOne ? 1 : -1);
    }

    /// <summary>The least value at or above two whose quadratic character over the modulus is <c>-1</c>.</summary>
    /// <param name="modulus">The odd prime.</param>
    /// <param name="budget">The maximum number of candidates to test; exhausting it is a FAILURE, not an
    /// answer.</param>
    /// <returns>The smallest quadratic non-residue.</returns>
    /// <remarks>Non-residues are half of the non-zero residues, so the smallest is small for every prime and the budget
    /// is never reached in practice. It is declared and enforced anyway: an unbounded search whose predicate has gone
    /// uniformly false must trip a named failure rather than spin.</remarks>
    /// <exception cref="InvalidOperationException">The budget was exhausted.</exception>
    public static BigInteger SmallestQuadraticNonResidue(BigInteger modulus, int budget) {
        var candidate = new BigInteger(value: 2);

        for (var step = 0; (step < budget); ++step) {
            if (-1 == PrimeFieldCharacter(value: candidate, modulus: modulus)) { return candidate; }

            candidate += BigInteger.One;
        }

        throw new InvalidOperationException(message: $"No quadratic non-residue below {candidate} over the modulus {modulus} within a budget of {budget} candidates.");
    }

    /// <summary>The reference product of an element of <c>F_p(sqrt(d))</c> with its own conjugate, by
    /// <see cref="PrimeFieldPolynomialProduct"/> against the tail <c>[p − d, 0]</c>. The first coordinate is the field
    /// NORM; the second must vanish, which the caller asserts.</summary>
    /// <param name="a">The base-field part of the element.</param>
    /// <param name="b">The coefficient of the adjoined root.</param>
    /// <param name="nonSquare">The reduced quadratic non-square the extension adjoins a root of.</param>
    /// <param name="modulus">The odd prime.</param>
    /// <returns>Both reduced coefficients of the conjugate product.</returns>
    /// <remarks>Reaches the norm as a PRODUCT WITH THE CONJUGATE reduced as a polynomial, where the subject evaluates
    /// the closed form <c>A^2 − d·B^2</c> as three separate reduced base products and one conditional-fold subtraction
    /// (QuadraticExtensionField64.cs:166-170). A different derivation, not a transcription; exact on both sides, so the
    /// module's rounding discipline does not arise. That the second coordinate vanishes is a STATEMENT the caller
    /// checks, not an assumption this reference makes.</remarks>
    public static (ulong A, ulong B) QuadraticExtensionConjugateProduct(ulong a, ulong b, ulong nonSquare, ulong modulus) {
        Span<ulong> left = stackalloc ulong[2];
        Span<ulong> right = stackalloc ulong[2];
        Span<ulong> tail = stackalloc ulong[2];
        Span<ulong> result = stackalloc ulong[2];
        var rootPart = (b % modulus);

        left[0] = (a % modulus);
        left[1] = rootPart;
        right[0] = left[0];
        right[1] = ((modulus - rootPart) % modulus);
        tail[0] = ((modulus - (nonSquare % modulus)) % modulus);
        tail[1] = 0UL;

        PrimeFieldPolynomialProduct(left: left, right: right, tail: tail, modulus: modulus, result: result);

        return (result[0], result[1]);
    }

    /// <summary>The reference power of an element of <c>F_p(sqrt(d))</c>, by MOST-significant-bit-first binary
    /// exponentiation whose multiplication step is <see cref="PrimeFieldPolynomialProduct"/> against the tail
    /// <c>[p − d, 0]</c>.</summary>
    /// <param name="a">The base-field part of the element.</param>
    /// <param name="b">The coefficient of the adjoined root.</param>
    /// <param name="exponent">The power; zero yields <c>(1, 0)</c>.</param>
    /// <param name="nonSquare">The reduced quadratic non-square the extension adjoins a root of.</param>
    /// <param name="modulus">The odd prime.</param>
    /// <returns>The reduced pair.</returns>
    /// <remarks>Two independences at once. The SCHEDULE walks the exponent from its top bit down, squaring the
    /// accumulator; the subject walks it from the bottom up, squaring a running power it multiplies in
    /// (QuadraticExtensionField64.cs:176-189), so the two visit different intermediate values in a different order. The
    /// STEP is schoolbook polynomial reduction rather than the closed pair formula, so no line of the product rule is
    /// shared either. Everything is <see cref="BigInteger"/> inside the shared product; no Puck.Maths kernel is
    /// called.</remarks>
    public static (ulong A, ulong B) QuadraticExtensionPower(ulong a, ulong b, ulong exponent, ulong nonSquare, ulong modulus) {
        Span<ulong> tail = stackalloc ulong[2];
        Span<ulong> accumulator = stackalloc ulong[2];
        Span<ulong> baseValue = stackalloc ulong[2];
        Span<ulong> scratch = stackalloc ulong[2];

        tail[0] = ((modulus - (nonSquare % modulus)) % modulus);
        tail[1] = 0UL;
        baseValue[0] = (a % modulus);
        baseValue[1] = (b % modulus);
        accumulator[0] = (1UL % modulus);
        accumulator[1] = 0UL;

        if (0UL == exponent) { return (accumulator[0], accumulator[1]); }

        for (var bit = BitOperations.Log2(value: exponent); (bit >= 0); --bit) {
            PrimeFieldPolynomialProduct(left: accumulator, right: accumulator, tail: tail, modulus: modulus, result: scratch);
            scratch.CopyTo(destination: accumulator);

            if (0UL != ((exponent >>> bit) & 1UL)) {
                PrimeFieldPolynomialProduct(left: accumulator, right: baseValue, tail: tail, modulus: modulus, result: scratch);
                scratch.CopyTo(destination: accumulator);
            }
        }

        return (accumulator[0], accumulator[1]);
    }

    // Whether the value is a perfect square, by EXACT float-free bisection on [0, 2^32] — thirty-two halvings whose
    // only predicate is one exact squaring. Deliberately NOT the subject's ulong.SquareRoot(), whose first estimate
    // comes from hardware floating point and is the single floating-point touch the whole FiniteFields wing admits
    // to: a gate must not read the estimate it gates. Fixed width and exact rather than BigInteger because the
    // bracket bounds the square — a midpoint is strictly below 2^32, so its square is strictly below 2^64 and cannot
    // leave the carrier.
    private static bool PerfectSquareByBisection(ulong value) {
        var low = 0UL;
        var high = (1UL << 32);

        while ((high - low) > 1UL) {
            var middle = (low + ((high - low) >> 1));

            if ((middle * middle) <= value) { low = middle; } else { high = middle; }
        }

        return ((low * low) == value);
    }

    // The Lucas numerators U_index and U_(index+1) modulo the value, from the companion matrix M = [[P, −Q], [1, 0]]
    // with P = 1, by square-and-multiply over two-by-two BigInteger matrices. M^n carries (U_(n+1), U_n) in its first
    // column, which is what makes ONE matrix power answer both terms and lets the V terms be derived rather than
    // recurred.
    private static (BigInteger AtIndex, BigInteger AtNextIndex) LucasNumeratorPair(BigInteger q, ulong index, BigInteger modulus) {
        var resultA = BigInteger.One;
        var resultB = BigInteger.Zero;
        var resultC = BigInteger.Zero;
        var resultD = BigInteger.One;
        var baseA = BigInteger.One;
        var baseB = ((modulus - q) % modulus);
        var baseC = BigInteger.One;
        var baseD = BigInteger.Zero;
        var exponent = index;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) {
                var a = (((resultA * baseA) + (resultB * baseC)) % modulus);
                var b = (((resultA * baseB) + (resultB * baseD)) % modulus);
                var c = (((resultC * baseA) + (resultD * baseC)) % modulus);
                var d = (((resultC * baseB) + (resultD * baseD)) % modulus);

                resultA = a;
                resultB = b;
                resultC = c;
                resultD = d;
            }

            var squareA = (((baseA * baseA) + (baseB * baseC)) % modulus);
            var squareB = (((baseA * baseB) + (baseB * baseD)) % modulus);
            var squareC = (((baseC * baseA) + (baseD * baseC)) % modulus);
            var squareD = (((baseC * baseB) + (baseD * baseD)) % modulus);

            baseA = squareA;
            baseB = squareB;
            baseC = squareC;
            baseD = squareD;
            exponent >>= 1;
        }

        return (AtIndex: resultC, AtNextIndex: resultA);
    }

    private static uint[] BuildSmallPrimes() {
        var flags = PrimeSieve(inclusiveMaximum: 8191);
        var primes = new List<uint>();

        for (var index = 2; (index < flags.Length); ++index) {
            if (flags[index]) { primes.Add(item: ((uint)index)); }
        }

        return [.. primes];
    }

    /// <summary>One planar diagram: its two boundary widths, its balanced-parenthesis code, and its matching.</summary>
    /// <param name="InputWidth">The number of input wires.</param>
    /// <param name="OutputWidth">The number of output wires.</param>
    /// <param name="Code">The parenthesis word packed most significant point first, a set bit where a point closes an
    /// arc — so ascending codes ARE the lexicographic word order the catalogue keys by.</param>
    /// <param name="Partner">The matching: the point each boundary point is joined to. The inputs occupy the first
    /// <paramref name="InputWidth"/> points in order and the outputs the rest IN REVERSE, which is what makes a planar
    /// diagram a non-crossing matching of one circle of points.</param>
    public sealed record PlanarDiagram(int InputWidth, int OutputWidth, int Code, int[] Partner);

    /// <summary>Every planar diagram of both widths at most <paramref name="maximumWidth"/>, in the catalogue's
    /// canonical order.</summary>
    /// <param name="maximumWidth">The width bound.</param>
    /// <returns>The diagrams, ordered by input width, then output width, then code.</returns>
    /// <remarks>Shares no construction with the subject's enumeration: every mask of the boundary length is generated
    /// and then TESTED for balance, where the subject walks the balanced words directly under two prunings and never
    /// forms an unbalanced one. Only the declared order is common, because that order is the key scheme itself.</remarks>
    public static IReadOnlyList<PlanarDiagram> PlanarDiagrams(int maximumWidth) {
        var diagrams = new List<PlanarDiagram>();

        for (var inputs = 0; (inputs <= maximumWidth); ++inputs) {
            for (var outputs = 0; (outputs <= maximumWidth); ++outputs) {
                var points = (inputs + outputs);

                if (0 != (points & 1)) { continue; }

                for (var code = 0; (code < (1 << points)); ++code) {
                    var depth = 0;
                    var openers = new List<int>();
                    var partner = new int[points];

                    for (var position = 0; (position < points); ++position) {
                        if (0 == ((code >> ((points - 1) - position)) & 1)) {
                            ++depth;

                            openers.Add(item: position);

                            continue;
                        }

                        --depth;

                        if (depth < 0) { break; }

                        var opener = openers[^1];

                        openers.RemoveAt(index: (openers.Count - 1));

                        partner[opener] = position;
                        partner[position] = opener;
                    }

                    if ((depth != 0) || (openers.Count != 0)) { continue; }

                    diagrams.Add(item: new PlanarDiagram(InputWidth: inputs, OutputWidth: outputs, Code: code, Partner: partner));
                }
            }
        }

        return diagrams;
    }

    /// <summary>Indexes a planar basis by boundary shape and code, so a composite diagram can be named.</summary>
    /// <param name="basis">The basis, as <see cref="PlanarDiagrams"/> returns it.</param>
    /// <returns>The map from shape and code to the diagram's index in the basis.</returns>
    public static IReadOnlyDictionary<(int InputWidth, int OutputWidth, int Code), int> PlanarSymbols(IReadOnlyList<PlanarDiagram> basis) {
        var symbols = new Dictionary<(int InputWidth, int OutputWidth, int Code), int>();

        for (var index = 0; (index < basis.Count); ++index) {
            symbols[(basis[index].InputWidth, basis[index].OutputWidth, basis[index].Code)] = index;
        }

        return symbols;
    }

    /// <summary>Composes two planar diagrams by tracing their arcs, and counts the closed loops the composition strands
    /// off.</summary>
    /// <param name="basis">The basis.</param>
    /// <param name="left">The left diagram's index.</param>
    /// <param name="right">The right diagram's index, whose input width must be the left one's output width.</param>
    /// <returns>The composite's shape and code, and the loop count.</returns>
    /// <remarks>An explicit edge list and a union-find, which shares nothing with the subject's walk: the components
    /// are found by merging edges rather than by following one point at a time, the composite's arcs are read off the
    /// component a free point lands in, and the loop count is the components with no free point at all — a difference of
    /// two counts rather than an unvisited-node sweep. The count is a <see cref="BigInteger"/> throughout.</remarks>
    public static (int InputWidth, int OutputWidth, int Code, BigInteger Loops) PlanarCompose(IReadOnlyList<PlanarDiagram> basis, int left, int right) {
        var first = basis[left];
        var second = basis[right];
        var leftPoints = (first.InputWidth + first.OutputWidth);
        var rightPoints = (second.InputWidth + second.OutputWidth);
        var total = (leftPoints + rightPoints);
        var edges = new List<(int First, int Second)>();

        for (var point = 0; (point < leftPoints); ++point) {
            if (point < first.Partner[point]) { edges.Add(item: (point, first.Partner[point])); }
        }

        for (var point = 0; (point < rightPoints); ++point) {
            if (point < second.Partner[point]) { edges.Add(item: ((leftPoints + point), (leftPoints + second.Partner[point]))); }
        }

        // The glue: the left diagram's wire w is its output read backwards from the end, the right diagram's is its
        // input read forwards from the start.
        for (var wire = 0; (wire < first.OutputWidth); ++wire) {
            edges.Add(item: (((leftPoints - 1) - wire), (leftPoints + wire)));
        }

        var parent = new int[total];

        for (var node = 0; (node < total); ++node) { parent[node] = node; }

        int Root(int node) {
            while (parent[node] != node) { node = parent[node] = parent[parent[node]]; }

            return node;
        }

        foreach (var (one, other) in edges) { parent[Root(node: one)] = Root(node: other); }

        var free = new List<int>();

        for (var point = 0; (point < first.InputWidth); ++point) { free.Add(item: point); }
        for (var point = 0; (point < second.OutputWidth); ++point) { free.Add(item: ((leftPoints + second.InputWidth) + point)); }

        var components = new HashSet<int>();
        var openComponents = free.Select(selector: Root).ToHashSet();

        for (var node = 0; (node < total); ++node) { _ = components.Add(item: Root(node: node)); }

        var composite = new int[free.Count];

        for (var position = 0; (position < free.Count); ++position) {
            for (var other = 0; (other < free.Count); ++other) {
                if ((position != other) && (Root(node: free[position]) == Root(node: free[other]))) { composite[position] = other; }
            }
        }

        var code = 0;

        for (var position = 0; (position < composite.Length); ++position) {
            code = ((code << 1) | ((composite[position] < position) ? 1 : 0));
        }

        return (first.InputWidth, second.OutputWidth, code, new BigInteger(value: (components.Count - openComponents.Count)));
    }

    /// <summary>Every distinct interleaving of two words, with the number of ways it is reached — the structure
    /// constants of the shuffle at an empty letter product, and of the quasi-shuffle at a non-empty one.</summary>
    /// <param name="left">The left word.</param>
    /// <param name="right">The right word.</param>
    /// <param name="letterProduct">The row-major table of merged letters, one per ordered pair of letters, or empty for
    /// the shuffle, where no two letters collide.</param>
    /// <param name="letterCount">The alphabet size, which indexes that table.</param>
    /// <returns>The interleavings, ascending by length and then lexicographically, each with its multiplicity.</returns>
    /// <remarks>Shares no construction with the subject's recursion on the two heads: every step-kind sequence of every
    /// block count is generated and then TESTED against the two words, so this counts by exhaustive filtering where the
    /// subject counts by reading three shorter cells. The multiplicities are <see cref="BigInteger"/> throughout, and the
    /// enumeration is exponential in the combined length, which is what keeps its callers small.</remarks>
    public static IReadOnlyList<(int[] Word, BigInteger Multiplicity)> Interleavings(int[] left, int[] right, int[] letterProduct, int letterCount) {
        var kinds = ((0 == letterProduct.Length) ? 2L : 3L);
        var multiplicities = new List<BigInteger>();
        var words = new List<int[]>();

        for (var blocks = 0; (blocks <= (left.Length + right.Length)); ++blocks) {
            var sequences = 1L;

            for (var step = 0; (step < blocks); ++step) { sequences *= kinds; }

            for (var sequence = 0L; (sequence < sequences); ++sequence) {
                var scan = sequence;
                var taken = 0;
                var word = new int[blocks];
                var used = 0;

                for (var block = 0; (block < blocks); ++block) {
                    var kind = ((int)(scan % kinds));

                    scan /= kinds;

                    if (((0 == kind) || (2 == kind)) && (taken >= left.Length)) { taken = -1; break; }
                    if (((1 == kind) || (2 == kind)) && (used >= right.Length)) { taken = -1; break; }

                    word[block] = kind switch {
                        0 => left[taken++],
                        1 => right[used++],
                        _ => letterProduct[((left[taken++] * letterCount) + right[used++])],
                    };
                }

                if ((taken != left.Length) || (used != right.Length)) { continue; }

                var low = 0;
                var high = words.Count;

                while (low < high) {
                    var middle = ((low + high) >> 1);
                    var order = CompareWords(left: words[middle], right: word);

                    if (0 == order) {
                        multiplicities[middle] += BigInteger.One;

                        low = -1;

                        break;
                    }

                    if (order < 0) { low = (middle + 1); } else { high = middle; }
                }

                if (low < 0) { continue; }

                multiplicities.Insert(index: low, item: BigInteger.One);
                words.Insert(index: low, item: word);
            }
        }

        var result = new (int[] Word, BigInteger Multiplicity)[words.Count];

        for (var index = 0; (index < result.Length); ++index) { result[index] = (words[index], multiplicities[index]); }

        return result;
    }

    /// <summary>Pascal's triangle down to a given row.</summary>
    /// <param name="rows">The last row.</param>
    /// <returns>The rows, row <c>n</c> carrying <c>n + 1</c> binomial coefficients.</returns>
    /// <remarks>Every entry is the sum of the two above it, so nothing here multiplies, divides or forms a factorial —
    /// which is what makes it an independent reading of a multiplicity some product computed.</remarks>
    public static BigInteger[][] PascalTriangle(int rows) {
        var triangle = new BigInteger[(rows + 1)][];

        for (var row = 0; (row <= rows); ++row) {
            triangle[row] = new BigInteger[(row + 1)];

            for (var column = 0; (column <= row); ++column) {
                triangle[row][column] = (((0 == column) || (row == column))
                    ? BigInteger.One
                    : (triangle[(row - 1)][(column - 1)] + triangle[(row - 1)][column]));
            }
        }

        return triangle;
    }

    /// <summary>The bracket state sum of a plat-closed braid word: every smoothing of every crossing enumerated, each
    /// state's closed curves counted, and the loop charge raised to that count.</summary>
    /// <param name="strandCount">The number of strands, which is even, since the closing cups and caps join adjacent
    /// pairs.</param>
    /// <param name="word">The braid word, one letter per crossing: <c>+i</c> for the crossing at strand <c>i</c> and
    /// <c>-i</c> for its mirror, read bottom to top.</param>
    /// <returns>The bracket as a Laurent polynomial in the crossing charge: the exponent of its first coefficient, and
    /// the coefficients ascending from it.</returns>
    /// <remarks>
    /// Shares no construction with the subject, which composes one planar diagram into another and charges the loops
    /// each composition strands off. Here the WHOLE closed diagram of one state is built at once as a graph over the
    /// boundary points of every layer — the closing arcs, the wire segments and the smoothings are all edges — and its
    /// closed curves are its connected components under a union-find. Nothing is composed, no diagram is named, no key is
    /// formed, and the arithmetic is <see cref="BigInteger"/> throughout. The cost is two to the crossing count, which is
    /// what keeps its callers small.
    /// </remarks>
    public static (int Lowest, BigInteger[] Coefficients) BracketStateSum(int strandCount, ReadOnlySpan<int> word) {
        var levels = (word.Length + 1);
        var nodeCount = (levels * strandCount);
        var states = (1 << word.Length);
        var top = ((levels - 1) * strandCount);
        var total = (0, Array.Empty<BigInteger>());

        for (var state = 0; (state < states); ++state) {
            var parent = new int[nodeCount];

            for (var node = 0; (node < nodeCount); ++node) { parent[node] = node; }

            int Root(int node) {
                while (parent[node] != node) { node = parent[node] = parent[parent[node]]; }

                return node;
            }

            void Join(int left, int right) { parent[Root(node: left)] = Root(node: right); }

            // The closure: the cups below and the caps above, each joining an adjacent pair.
            for (var wire = 0; (wire < strandCount); wire += 2) {
                Join(left: wire, right: (wire + 1));
                Join(left: (top + wire), right: (top + wire + 1));
            }

            var exponent = 0;

            for (var layer = 0; (layer < word.Length); ++layer) {
                var letter = word[layer];
                var strand = Math.Abs(value: letter);
                var lower = (layer * strandCount);
                var upper = ((layer + 1) * strandCount);

                // One crossing smooths two ways. The straight-through smoothing carries the crossing's own charge and
                // the joined one carries its inverse, which is what the mirror crossing swaps.
                if (0 == ((state >> layer) & 1)) {
                    exponent += ((letter > 0) ? 1 : -1);

                    for (var wire = 0; (wire < strandCount); ++wire) { Join(left: (lower + wire), right: (upper + wire)); }
                } else {
                    exponent -= ((letter > 0) ? 1 : -1);

                    Join(left: (lower + strand - 1), right: (lower + strand));
                    Join(left: (upper + strand - 1), right: (upper + strand));

                    for (var wire = 0; (wire < strandCount); ++wire) {
                        if ((wire != (strand - 1)) && (wire != strand)) { Join(left: (lower + wire), right: (upper + wire)); }
                    }
                }
            }

            var curves = new HashSet<int>();

            for (var node = 0; (node < nodeCount); ++node) { _ = curves.Add(item: Root(node: node)); }

            var term = (exponent, new[] { BigInteger.One });

            for (var curve = 0; (curve < curves.Count); ++curve) { term = LaurentMultiply(left: term, right: (-2, LoopCharge)); }

            total = LaurentAdd(left: total, right: term);
        }

        return total;
    }

    /// <summary>The bracket a published reduced bracket and a kink count give: the loop charge times the kink factor
    /// raised to that count times the reduced bracket.</summary>
    /// <param name="kinkExponent">The number of first-move kinks the diagram carries over the standard one, positive or
    /// negative; each multiplies the bracket by the kink factor.</param>
    /// <param name="lowest">The exponent the reduced bracket's first coefficient sits at.</param>
    /// <param name="coefficients">The reduced bracket's coefficients, ascending from that exponent.</param>
    /// <returns>The bracket as a Laurent polynomial in the crossing charge.</returns>
    /// <remarks>The reduced bracket is the published, diagram-independent invariant; the kink count is the declared
    /// diagram's own. Reading them through this is how a table of published numbers becomes a prediction about one
    /// diagram — and the state-sum enumeration, which knows neither, is what says the prediction was right.</remarks>
    public static (int Lowest, BigInteger[] Coefficients) BracketNormalization(int kinkExponent, int lowest, ReadOnlySpan<BigInteger> coefficients) {
        var value = LaurentMultiply(left: (lowest, coefficients.ToArray()), right: (-2, LoopCharge));
        var factor = ((kinkExponent >= 0) ? (3, KinkFactor) : (-3, KinkFactor));

        for (var kink = 0; (kink < Math.Abs(value: kinkExponent)); ++kink) { value = LaurentMultiply(left: value, right: factor); }

        return value;
    }

    /// <summary>Evaluates a Laurent polynomial at one point, exactly, by a Horner fold.</summary>
    /// <param name="lowest">The exponent the first coefficient sits at.</param>
    /// <param name="coefficients">The coefficients, ascending from that exponent.</param>
    /// <param name="point">The point.</param>
    /// <returns>The value as an exact fraction: the fold's numerator, and the power of the point a negative lowest
    /// exponent divides by.</returns>
    /// <remarks>The fold runs over the coefficients alone and never forms a power of the point, so a table of integers
    /// becomes a value in any material that can divide, and the division is the caller's own.</remarks>
    public static (BigInteger Numerator, BigInteger Denominator) BracketHorner(int lowest, ReadOnlySpan<BigInteger> coefficients, BigInteger point) {
        var numerator = BigInteger.Zero;

        for (var index = (coefficients.Length - 1); (index >= 0); --index) { numerator = ((numerator * point) + coefficients[index]); }

        return ((lowest >= 0)
            ? ((numerator * BigInteger.Pow(value: point, exponent: lowest)), BigInteger.One)
            : (numerator, BigInteger.Pow(value: point, exponent: -lowest)));
    }

    // The charge one closed curve carries, as a Laurent polynomial from the exponent minus two: the second move forces
    // it, since a crossing and its mirror compose to the straight-through diagram only at this value.
    //
    // TRANSCRIPTION, labelled (condition (C)). These two constants restate quantities the subject forms for itself —
    // the loop charge the state sum multiplies a closed curve by, and the kink factor the first move charges — so
    // agreement proves the two carriages match and NOT that either value is the one the moves force. The independent
    // witness is `presented.braid-relation-holds`'s loop-charge negative control: the second Reidemeister move holds at
    // this charge and at no other, which is the statement that pins the value from outside both.
    private static readonly BigInteger[] LoopCharge = [BigInteger.MinusOne, BigInteger.Zero, BigInteger.Zero, BigInteger.Zero, BigInteger.MinusOne];

    // The factor one first-move kink multiplies a bracket by, as a Laurent polynomial read at the exponent plus or minus
    // three: minus the crossing charge cubed. Labelled with the loop charge above.
    private static readonly BigInteger[] KinkFactor = [BigInteger.MinusOne];

    private static (int Lowest, BigInteger[] Coefficients) LaurentAdd((int Lowest, BigInteger[] Coefficients) left, (int Lowest, BigInteger[] Coefficients) right) {
        if (0 == left.Coefficients.Length) { return right; }
        if (0 == right.Coefficients.Length) { return left; }

        var lowest = Math.Min(val1: left.Lowest, val2: right.Lowest);
        var highest = Math.Max(val1: (left.Lowest + left.Coefficients.Length - 1), val2: (right.Lowest + right.Coefficients.Length - 1));
        var coefficients = new BigInteger[(highest - lowest + 1)];

        for (var index = 0; (index < left.Coefficients.Length); ++index) { coefficients[(left.Lowest + index - lowest)] += left.Coefficients[index]; }
        for (var index = 0; (index < right.Coefficients.Length); ++index) { coefficients[(right.Lowest + index - lowest)] += right.Coefficients[index]; }

        return LaurentTrim(lowest: lowest, coefficients: coefficients);
    }

    private static (int Lowest, BigInteger[] Coefficients) LaurentMultiply((int Lowest, BigInteger[] Coefficients) left, (int Lowest, BigInteger[] Coefficients) right) {
        if ((0 == left.Coefficients.Length) || (0 == right.Coefficients.Length)) { return (0, []); }

        var coefficients = new BigInteger[(left.Coefficients.Length + right.Coefficients.Length - 1)];

        for (var first = 0; (first < left.Coefficients.Length); ++first) {
            for (var second = 0; (second < right.Coefficients.Length); ++second) {
                coefficients[(first + second)] += (left.Coefficients[first] * right.Coefficients[second]);
            }
        }

        return LaurentTrim(lowest: (left.Lowest + right.Lowest), coefficients: coefficients);
    }

    // A Laurent polynomial is canonical when its first and last coefficients are nonzero, so equality is a span
    // comparison and cancellation is visible in the exponent rather than hidden in a zero.
    private static (int Lowest, BigInteger[] Coefficients) LaurentTrim(int lowest, BigInteger[] coefficients) {
        var high = (coefficients.Length - 1);
        var low = 0;

        while ((low <= high) && coefficients[low].IsZero) { ++low; }
        while ((high >= low) && coefficients[high].IsZero) { --high; }

        if (low > high) { return (0, []); }

        var trimmed = new BigInteger[(high - low + 1)];

        Array.Copy(sourceArray: coefficients, sourceIndex: low, destinationArray: trimmed, destinationIndex: 0, length: trimmed.Length);

        return ((lowest + low), trimmed);
    }

    // The canonical word order the presented algebra keys by: shorter first, then lexicographically. It is shared
    // because it IS the key scheme, exactly as the planar oracle shares the diagram order.
    private static int CompareWords(int[] left, int[] right) {
        if (left.Length != right.Length) { return ((left.Length < right.Length) ? -1 : 1); }

        for (var index = 0; (index < left.Length); ++index) {
            if (left[index] != right[index]) { return ((left[index] < right[index]) ? -1 : 1); }
        }

        return 0;
    }

    /// <summary>The kind of node a <see cref="WordPattern"/> carries.</summary>
    public enum WordPatternKind {
        /// <summary>The empty span, and only it.</summary>
        Empty,
        /// <summary>One letter.</summary>
        Letter,
        /// <summary>Either branch.</summary>
        Union,
        /// <summary>The left branch followed by the right one.</summary>
        Concatenate,
        /// <summary>Any number of repetitions of the branch, the empty one included.</summary>
        Iterate,
    }

    /// <summary>A pattern as a syntax TREE, which is what makes it an oracle: the subject has no tree at all — a pattern
    /// there is an element of a presented algebra and matching is a residual — so counting derivations here shares no
    /// construction with it.</summary>
    /// <param name="Kind">The node kind.</param>
    /// <param name="Symbol">The letter, read only at <see cref="WordPatternKind.Letter"/>.</param>
    /// <param name="Left">The first branch.</param>
    /// <param name="Right">The second branch.</param>
    public sealed record WordPattern(WordPatternKind Kind, int Symbol, WordPattern? Left, WordPattern? Right) {
        /// <summary>The empty-span pattern.</summary>
        public static WordPattern Empty { get; } = new(Kind: WordPatternKind.Empty, Symbol: -1, Left: null, Right: null);

        /// <summary>Builds a one-letter pattern.</summary>
        /// <param name="letter">The letter.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Letter(int letter) =>
            new(Kind: WordPatternKind.Letter, Symbol: letter, Left: null, Right: null);

        /// <summary>Builds a union.</summary>
        /// <param name="left">The first branch.</param>
        /// <param name="right">The second branch.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Union(WordPattern left, WordPattern right) =>
            new(Kind: WordPatternKind.Union, Symbol: -1, Left: left, Right: right);

        /// <summary>Builds a concatenation.</summary>
        /// <param name="left">The first branch.</param>
        /// <param name="right">The second branch.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Concatenate(WordPattern left, WordPattern right) =>
            new(Kind: WordPatternKind.Concatenate, Symbol: -1, Left: left, Right: right);

        /// <summary>Builds an iteration.</summary>
        /// <param name="value">The repeated branch, which must not derive the empty span.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Iterate(WordPattern value) =>
            new(Kind: WordPatternKind.Iterate, Symbol: -1, Left: value, Right: null);
    }

    /// <summary>Counts the derivations of one word under a pattern tree, by backtracking over every split.</summary>
    /// <param name="pattern">The pattern tree.</param>
    /// <param name="word">The word, as letter numbers.</param>
    /// <returns>The number of distinct derivations — the ambiguity degree, which over a counting material IS the
    /// coefficient, and whose being non-zero IS Boolean membership.</returns>
    /// <remarks>An <see cref="WordPatternKind.Iterate"/> node consumes at least one letter per repetition, so the
    /// recursion terminates; a branch that derives the empty span would make the count infinite and is rejected by the
    /// caller rather than silently truncated.</remarks>
    public static BigInteger WordDerivations(WordPattern pattern, ReadOnlySpan<int> word) =>
        Derivations(pattern: pattern, word: word, start: 0, end: word.Length);

    private static BigInteger Derivations(WordPattern pattern, ReadOnlySpan<int> word, int start, int end) {
        switch (pattern.Kind) {
            case WordPatternKind.Empty:
                return ((start == end) ? BigInteger.One : BigInteger.Zero);
            case WordPatternKind.Letter:
                return ((((start + 1) == end) && (word[start] == pattern.Symbol)) ? BigInteger.One : BigInteger.Zero);
            case WordPatternKind.Union:
                return (Derivations(pattern: pattern.Left!, word: word, start: start, end: end)
                    + Derivations(pattern: pattern.Right!, word: word, start: start, end: end));
            case WordPatternKind.Concatenate: {
                var total = BigInteger.Zero;

                for (var split = start; (split <= end); ++split) {
                    var head = Derivations(pattern: pattern.Left!, word: word, start: start, end: split);

                    if (head.IsZero) { continue; }

                    total += (head * Derivations(pattern: pattern.Right!, word: word, start: split, end: end));
                }

                return total;
            }
            default: {
                if (start == end) { return BigInteger.One; }

                var total = BigInteger.Zero;

                // Every repetition consumes at least one letter, which is what bounds the recursion.
                for (var split = (start + 1); (split <= end); ++split) {
                    var head = Derivations(pattern: pattern.Left!, word: word, start: start, end: split);

                    if (head.IsZero) { continue; }

                    total += (head * Derivations(pattern: pattern, word: word, start: split, end: end));
                }

                return total;
            }
        }
    }

    // The doubling conjugation's sign on a basis vector: the real unit is fixed, every imaginary unit is negated.
    private static int ConjugationSign(int index) =>
        ((0 == index) ? 1 : -1);

    // ---- the signed Q48.16 carrier: exact reference arithmetic, and directed-rounding enclosures for the
    // transcendentals, whose kernels are polynomial approximations with no correctly-rounded raw to be compared against ----

    /// <summary>An exact enclosure of a real value on a stated fixed-point grid: the true value's scaled form lies in
    /// <c>[Low, High]</c> BY CONSTRUCTION — every intermediate is truncated toward negative infinity for
    /// <see cref="Low"/> and toward positive infinity for <see cref="High"/>, so the pair is a proof obligation the code
    /// discharges rather than an error estimate. A transcendental has no single correctly-rounded raw to compare
    /// against, so the enclosure is what an oracle can honestly offer: the law states the subject lies within the
    /// DOCUMENTED envelope of it.</summary>
    /// <param name="Low">The greatest scaled integer proved to be at or below the true value.</param>
    /// <param name="High">The least scaled integer proved to be at or above the true value.</param>
    internal readonly record struct Enclosure(BigInteger Low, BigInteger High);

    /// <summary>The guard bits every transcendental enclosure carries below the Q48.16 grid: the returned pair is
    /// scaled by <c>2^(16 + GuardBitCount)</c>, so a sub-ULP envelope is expressible as an integer comparison.</summary>
    public const int GuardBitCount = 32;

    // The working fraction bits the logarithm and exponential oracles carry.
    private const int SeriesBitCount = 160;
    // The working fraction bits the arctangent series carries. Smaller than SeriesBitCount because the arctangent runs
    // per sampled operand pair rather than once at type initialization, and eighty bits of headroom below the guard
    // scale already make the enclosure's own width invisible against a sub-ULP envelope.
    private const int ArcTangentBitCount = 128;
    // The working fraction bits the circular reduction carries. Far larger than the others because a full-range Q48.16
    // angle is reduced against 2π with up to forty-five bits of cancellation before any series runs.
    private const int AngleBitCount = 384;
    // The working fraction bits the sine/cosine Taylor series carries AFTER that reduction, where no cancellation is
    // left to absorb.
    private const int TrigBitCount = 96;
    // The emitted fraction bits of the repeated-squaring logarithm.
    private const int LogFractionBitCount = 56;
    // The depth of the 2^(2^-i) square-root ladder, which bounds the exponent scale EncloseExp2 accepts.
    private const int LadderDepth = 48;
    // The Taylor terms the reduced sine and cosine series carry; the remainder after thirty terms is below 2^-176 while
    // the working scale is 2^-96.
    private const int TrigTermCount = 30;

    // The ladder factors 2^(2^-i) at the working scale, built ONCE by repeated integer square roots of two — floored
    // for the lower ladder and ceilinged for the upper one, so a product over a fraction's set bits is an enclosure by
    // construction. Operand-independent, so building them per call would be pure waste.
    private static readonly BigInteger[] LowLadder = BuildLadder(ceiling: false);
    private static readonly BigInteger[] HighLadder = BuildLadder(ceiling: true);

    // The circle constant at each working scale, derived once by Machin's formula from this module's own arctangent
    // series. Nothing in the trigonometric oracles rests on a transcribed digit string.
    private static readonly Enclosure ArcTangentPi = MachinPi(bitCount: ArcTangentBitCount);
    private static readonly Enclosure AnglePi = MachinPi(bitCount: AngleBitCount);

    /// <summary>An enclosure of <c>π·2^bitCount</c>, derived by Machin's formula <c>π = 16·atan(1/5) − 4·atan(1/239)</c>
    /// evaluated by this module's own alternating arctangent series.</summary>
    /// <param name="bitCount">The scale the enclosure is returned at.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>The published forty-digit expansion is asserted against this once, as a structural leg of
    /// <c>scalar.sincos-vs-series</c>, purely to catch a transposed formula — it is a cross-check on the derivation,
    /// never the source of the value.</remarks>
    public static Enclosure Pi(int bitCount) =>
        ((bitCount == ArcTangentBitCount)
            ? ArcTangentPi
            : ((bitCount == AngleBitCount) ? AnglePi : MachinPi(bitCount: bitCount)));

    /// <summary>Rounds the exact rational <c>numerator·2^shift / denominator</c> to the nearest raw, ties to even, then
    /// wraps to the signed 64-bit carrier — the reference for a fixed-point division.</summary>
    /// <param name="numerator">The dividend.</param>
    /// <param name="denominator">The divisor, which must be non-zero.</param>
    /// <param name="shift">The fixed-point scale the quotient is taken at.</param>
    /// <returns>The rounded, wrapped raw.</returns>
    /// <remarks>Shares nothing with the subject: that one splits the magnitude into a 128-by-64 hardware quotient with
    /// an <c>r</c> versus <c>d − r</c> comparison and re-applies the combined sign to a 64-bit-truncated magnitude;
    /// this takes one exact <see cref="BigInteger.DivRem(BigInteger,BigInteger,out BigInteger)"/>, compares <c>2r</c>
    /// against <c>d</c> — the formulation the carrier cannot use, because <c>2r</c> would overflow it — and wraps the
    /// exact signed value once at the end.</remarks>
    public static long RoundDyadicRatio(BigInteger numerator, BigInteger denominator, int shift) =>
        WrapToRaw(value: RoundRationalTiesToEven(numerator: (numerator << shift), denominator: denominator));

    /// <summary>The exact rounded fixed-point product as an UNWRAPPED integer — what the checked multiplication must
    /// return, and what its range verdict is decided by.</summary>
    /// <param name="a">The multiplicand's raw.</param>
    /// <param name="b">The multiplier's raw.</param>
    /// <returns>The rounded product, unwrapped.</returns>
    public static BigInteger ExactRoundedProduct(long a, long b) =>
        RoundRationalTiesToEven(numerator: (((BigInteger)a) * b), denominator: (BigInteger.One << 16));

    /// <summary>The exact rounded fixed-point quotient as an UNWRAPPED integer — the checked division's counterpart to
    /// <see cref="ExactRoundedProduct"/>.</summary>
    /// <param name="a">The dividend's raw.</param>
    /// <param name="b">The divisor's raw, which must be non-zero.</param>
    /// <returns>The rounded quotient, unwrapped.</returns>
    public static BigInteger ExactRoundedRatio(long a, long b) =>
        RoundRationalTiesToEven(numerator: (((BigInteger)a) << 16), denominator: new BigInteger(value: b));

    /// <summary>The exact integer square root <c>⌊√value⌋</c>, by a bit-length seed and Newton descent in
    /// <see cref="BigInteger"/>, settled by the exact predicate <c>r² ≤ value &lt; (r+1)²</c>.</summary>
    /// <param name="value">The radicand; a non-positive value roots to zero.</param>
    /// <returns>The exact floor of the square root.</returns>
    /// <remarks>Deliberately a different route from the subject, which seeds from a hardware or <see cref="double"/>
    /// square root and settles by trial multiplication in a fixed-width carrier. Puck.Maths' own
    /// <c>BigIntegerFunctions.SquareRoot</c> is NOT used: an oracle that called it would be checking the tree against
    /// itself.</remarks>
    public static BigInteger IntegerSquareRoot(BigInteger value) {
        if (value.Sign <= 0) {
            return BigInteger.Zero;
        }

        var root = (BigInteger.One << ((int)((value.GetBitLength() + 1L) / 2L)));

        while (true) {
            var next = ((root + (value / root)) >> 1);

            if (next >= root) { break; }

            root = next;
        }

        while ((root * root) > value) { root -= BigInteger.One; }

        while (((root + BigInteger.One) * (root + BigInteger.One)) <= value) { root += BigInteger.One; }

        return root;
    }

    /// <summary>The nearest integer to the square root of a non-negative exact value, by a BRACKETED INTEGER SEARCH
    /// whose predicate is one exact squaring — no square root is ever taken.</summary>
    /// <param name="value">The radicand, which must be non-negative.</param>
    /// <returns>The nearest integer root.</returns>
    /// <remarks>The answer is the largest <c>t</c> with <c>(2t − 1)² ≤ 4·value</c> (with zero admitted outright), because
    /// <c>t</c> is nearest exactly when <c>(t − ½)² ≤ value &lt; (t + ½)²</c>. No halfway case exists: <c>4·value</c> is even
    /// and <c>(2t + 1)²</c> odd, so the two can never be equal — which is the same fact the subject's ±1 settle relies on,
    /// reached here from the inequality rather than from an integer square root plus a remainder compare. Deliberately
    /// NOT <see cref="IntegerSquareRoot"/> with a repair on top: that one seeds from a bit length and descends by
    /// Newton's method, and a nearest root built on its floor would inherit the descent.</remarks>
    public static BigInteger NearestIntegerRoot(BigInteger value) {
        var quadrupled = (value << 2);

        bool AtMost(BigInteger candidate) {
            if (candidate.IsZero) { return true; }

            var odd = ((candidate << 1) - BigInteger.One);

            return ((odd * odd) <= quadrupled);
        }

        var low = BigInteger.Zero;
        var high = BigInteger.One;

        while (AtMost(candidate: high)) {
            low = high;
            high <<= 1;
        }

        while ((high - low) > BigInteger.One) {
            var middle = ((low + high) >> 1);

            if (AtMost(candidate: middle)) { low = middle; } else { high = middle; }
        }

        return low;
    }

    /// <summary>The reference complex quotient over the fixed-point carrier — <c>left·conj(right)/|right|²</c>, each
    /// component ONE ties-to-even rounding of the exact rational at Q16, wrapped to the carrier.</summary>
    /// <param name="ar">The dividend's real raw.</param>
    /// <param name="ai">The dividend's imaginary raw.</param>
    /// <param name="br">The divisor's real raw.</param>
    /// <param name="bi">The divisor's imaginary raw; the divisor must not be the additive identity.</param>
    /// <returns>The quotient's components as raws.</returns>
    /// <remarks>PATH-INDEPENDENT by construction: the subject reaches this value two ways — a narrow lane routed through
    /// the carrier's 128-by-64 divide-and-repair and a full-width lane through a bit-by-bit restoring division — and this
    /// forms neither, so agreement is also the proof of the source's "exact-equivalent fast path" claim rather than an
    /// assumption of it.</remarks>
    public static (long Real, long Imaginary) ComplexQuotient(long ar, long ai, long br, long bi) {
        var denominator = (((BigInteger)br * br) + ((BigInteger)bi * bi));

        return (
            RoundDyadicRatio(numerator: (((BigInteger)ar * br) + ((BigInteger)ai * bi)), denominator: denominator, shift: 16),
            RoundDyadicRatio(numerator: (((BigInteger)ai * br) - ((BigInteger)ar * bi)), denominator: denominator, shift: 16)
        );
    }

    /// <summary>The reference split-complex quotient — <c>left·conj(right)/(c² − d²)</c>, each component ONE
    /// ties-to-even rounding at Q16.</summary>
    /// <param name="au">The dividend's scalar raw.</param>
    /// <param name="av">The dividend's split raw.</param>
    /// <param name="bu">The divisor's scalar raw.</param>
    /// <param name="bv">The divisor's split raw; the divisor must lie off the light cone.</param>
    /// <returns>The quotient's components as raws.</returns>
    /// <remarks>The denominator is INDEFINITE and may be negative; the sign of the quotient is the sign of the rational,
    /// which is the statement the subject's signed <c>DivideProductSum</c> overload makes and the definite complex case
    /// never reaches.</remarks>
    public static (long U, long V) SplitQuotient(long au, long av, long bu, long bv) {
        var denominator = (((BigInteger)bu * bu) - ((BigInteger)bv * bv));

        return (
            RoundDyadicRatio(numerator: (((BigInteger)au * bu) - ((BigInteger)av * bv)), denominator: denominator, shift: 16),
            RoundDyadicRatio(numerator: (((BigInteger)av * bu) - ((BigInteger)au * bv)), denominator: denominator, shift: 16)
        );
    }

    /// <summary>The first lane at which a returned unit direction is farther than <paramref name="tolerance"/> raws from
    /// the EXACT Q16 unit direction of <paramref name="components"/>, or <c>−1</c> when every lane is within it.</summary>
    /// <param name="components">The exact input components. They are taken at arbitrary width rather than as raws
    /// because the geometric-product constructions judged here form exact Q32 sums that reach <c>2¹²⁷</c>, and only the
    /// DIRECTION of the tuple matters.</param>
    /// <param name="unit">The returned unit components, raw.</param>
    /// <param name="tolerance">The permitted deviation, in raws.</param>
    /// <returns>The offending lane index, or <c>−1</c>.</returns>
    /// <remarks>The ideal lane is <c>cᵢ·2¹⁶/√S</c> with <c>S = Σ cⱼ²</c>. Rather than form that irrational value, the two
    /// bounds are decided as SURD COMPARISONS <c>a·√S ≤ b</c>, each settled by reading the signs of <c>a</c> and <c>b</c>
    /// first and then squaring once — the same technique <see cref="PartialQuotients"/>' floor uses. Nothing here
    /// preconditions by a power of two, rounds a denominator, or divides: it shares no step with
    /// <c>FixedVectorMath.Normalize</c>, whose answer it judges. An all-zero input is defined to have an all-zero
    /// direction, which is NOT what the algebra types return at zero — each documents its own identity there, and each
    /// case states that pole structurally rather than routing it here.</remarks>
    public static int FirstNonUnitLane(ReadOnlySpan<BigInteger> components, ReadOnlySpan<long> unit, long tolerance) {
        var squaredSum = BigInteger.Zero;

        foreach (var component in components) {
            squaredSum += (component * component);
        }

        for (var lane = 0; (lane < components.Length); ++lane) {
            if (squaredSum.IsZero) {
                if (0L != unit[lane]) { return lane; }

                continue;
            }

            var scaled = (components[lane] << 16);
            var low = (new BigInteger(value: unit[lane]) - tolerance);
            var high = (new BigInteger(value: unit[lane]) + tolerance);

            // low·√S ≤ cᵢ·2¹⁶ ≤ high·√S, the second written as (−high)·√S ≤ −cᵢ·2¹⁶ so one comparison shape serves both.
            if (!SurdAtMost(coefficient: low, radicand: squaredSum, bound: scaled)) { return lane; }
            if (!SurdAtMost(coefficient: -high, radicand: squaredSum, bound: -scaled)) { return lane; }
        }

        return -1;
    }

    /// <summary>The reference product of the Cayley–Dickson tower at a given number of doublings, as a twisted group
    /// algebra over <c>(ℤ/2)^floors</c>: the target key is the exclusive-or of the operand keys and the charge is
    /// <see cref="CayleyDicksonCharge"/>. Each lane is ONE ties-to-even rounding of the whole exact charged sum.</summary>
    /// <param name="left">The multiplicand's lanes, raw, <c>2^floors</c> wide, in basis order <c>e₀ … e_{2^floors−1}</c>.</param>
    /// <param name="right">The multiplier's lanes, same width and order.</param>
    /// <param name="floors">The number of doublings; two for the quaternions.</param>
    /// <param name="shift">The rounding scale; two raw Q16 factors make the exact sum Q32, so this is sixteen.</param>
    /// <param name="result">The destination lanes.</param>
    /// <remarks>A hand-written Hamilton product forms no such basis: this walks the doubling recursion
    /// <c>(a, b)·(c, d) = (a·c − d̄·b, d·a + b·c̄)</c> down to basis vectors and reads the target key off an
    /// exclusive-or. The convention agrees with Hamilton — checked by hand at <c>e₁e₂ = +e₃</c>, <c>e₂e₁ = −e₃</c>,
    /// <c>e₁² = e₃² = −e₀</c> and <c>e₂e₃ = +e₁</c>.</remarks>
    public static void CayleyDicksonProduct(ReadOnlySpan<long> left, ReadOnlySpan<long> right, int floors, int shift, Span<long> result) =>
        TwistedGroupProduct(
            chargeSource: (first, second) => CayleyDicksonCharge(leftIndex: first, rightIndex: second, floors: floors),
            left: left,
            right: right,
            shift: shift,
            result: result
        );

    /// <summary>The reference dual product over a Cayley–Dickson carrier: the real block is the carrier product
    /// <c>a·c</c> and each dual lane is ONE ties-to-even rounding of the WHOLE exact sum <c>a·d + b·c</c> — the two
    /// carrier products fused ACROSS the dual seam rather than rounded separately and added.</summary>
    /// <param name="left">The multiplicand: the real block's lanes then the dual block's, each <c>2^floors</c> wide.</param>
    /// <param name="right">The multiplier, same layout.</param>
    /// <param name="floors">The number of doublings; two for dual quaternions.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">The destination, same layout as the operands.</param>
    public static void DoublingDualProduct(ReadOnlySpan<long> left, ReadOnlySpan<long> right, int floors, int shift, Span<long> result) {
        var width = (1 << floors);

        for (var target = 0; (target < width); ++target) {
            var real = BigInteger.Zero;
            var dual = BigInteger.Zero;

            for (var first = 0; (first < width); ++first) {
                var second = (first ^ target);
                var charge = CayleyDicksonCharge(leftIndex: first, rightIndex: second, floors: floors);
                var realTerm = ((BigInteger)left[first] * right[second]);
                var dualTerm = (((BigInteger)left[first] * right[(width + second)]) + ((BigInteger)left[(width + first)] * right[second]));

                real += ((charge > 0) ? realTerm : -realTerm);
                dual += ((charge > 0) ? dualTerm : -dualTerm);
            }

            result[target] = RoundDyadic(exact: real, shift: shift);
            result[(width + target)] = RoundDyadic(exact: dual, shift: shift);
        }
    }

    /// <summary>The reference dual product over a carrier that is NOT a house type: the generic path forms the carrier
    /// product three times and adds, so the dual part carries TWO roundings and one wrapping add — the honest
    /// alternative discipline the fused seams are claimed to beat.</summary>
    /// <param name="pRaw">The carrier relation's linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The carrier relation's constant coefficient, raw Q16.</param>
    /// <param name="left">The multiplicand: the real carrier's two components then the dual carrier's.</param>
    /// <param name="right">The multiplier, same layout.</param>
    /// <param name="result">The destination, same layout.</param>
    public static void DualOverQuadraticProduct(long pRaw, long qRaw, ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var real = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: left[0], v1: left[1], u2: right[0], v2: right[1]);
        var crossed = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: left[0], v1: left[1], u2: right[2], v2: right[3]);
        var seeded = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: left[2], v1: left[3], u2: right[0], v2: right[1]);

        result[0] = real.U;
        result[1] = real.V;
        result[2] = WrapToRaw(value: ((BigInteger)crossed.U + seeded.U));
        result[3] = WrapToRaw(value: ((BigInteger)crossed.V + seeded.V));
    }

    /// <summary>The reference dot product of two lane vectors — ONE ties-to-even rounding of the exact product sum.</summary>
    /// <param name="left">The first vector's lanes, raw.</param>
    /// <param name="right">The second vector's lanes, raw.</param>
    /// <param name="shift">The rounding scale; two raw Q16 factors make the exact sum Q32, so this is sixteen.</param>
    /// <returns>The dot product as a raw.</returns>
    public static long LaneDotProduct(ReadOnlySpan<long> left, ReadOnlySpan<long> right, int shift) {
        var exact = BigInteger.Zero;

        for (var lane = 0; (lane < left.Length); ++lane) { exact += ((BigInteger)left[lane] * right[lane]); }

        return RoundDyadic(exact: exact, shift: shift);
    }

    /// <summary>The reference rotation sandwich <c>v' = v + 2·u×(u×v + w·v)</c> over the fixed-point carrier: each of the
    /// two stages accumulates its exact product sum and rounds ONCE per component, and the final combination wraps.</summary>
    /// <param name="rotation">The rotor lanes <c>(x, y, z, w)</c>, raw.</param>
    /// <param name="vector">The vector lanes <c>(x, y, z)</c>, raw.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">The three destination lanes.</param>
    /// <remarks>The TWO-STAGE SCHEDULE is the subject's documented contract, carried faithfully here — a rotation over a
    /// rounding carrier is chain-dependent, so the number and order of the roundings is part of the answer and no
    /// single-rounding oracle can stand beside it. What is re-derived independently is the ARITHMETIC of each stage: the
    /// exact cross and scaled sums in <see cref="BigInteger"/>, one <see cref="RoundDyadic"/> per component, sharing no
    /// code and no rounding kernel with the subject.</remarks>
    public static void QuaternionSandwich(ReadOnlySpan<long> rotation, ReadOnlySpan<long> vector, int shift, Span<long> result) {
        var (ux, uy, uz, w) = ((BigInteger)rotation[0], (BigInteger)rotation[1], (BigInteger)rotation[2], (BigInteger)rotation[3]);
        var (vx, vy, vz) = ((BigInteger)vector[0], (BigInteger)vector[1], (BigInteger)vector[2]);
        var tx = new BigInteger(value: RoundDyadic(exact: (((uy * vz) - (uz * vy)) + (w * vx)), shift: shift));
        var ty = new BigInteger(value: RoundDyadic(exact: (((uz * vx) - (ux * vz)) + (w * vy)), shift: shift));
        var tz = new BigInteger(value: RoundDyadic(exact: (((ux * vy) - (uy * vx)) + (w * vz)), shift: shift));
        var dx = new BigInteger(value: RoundDyadic(exact: ((uy * tz) - (uz * ty)), shift: shift));
        var dy = new BigInteger(value: RoundDyadic(exact: ((uz * tx) - (ux * tz)), shift: shift));
        var dz = new BigInteger(value: RoundDyadic(exact: ((ux * ty) - (uy * tx)), shift: shift));

        result[0] = WrapToRaw(value: (vx + (dx << 1)));
        result[1] = WrapToRaw(value: (vy + (dy << 1)));
        result[2] = WrapToRaw(value: (vz + (dz << 1)));
    }

    /// <summary>The reference canonicalization of one world axis: the exact integer split of a cell index and an offset
    /// into the canonical pair whose offset lies in <c>[−2^(cellRawLog2−1), 2^(cellRawLog2−1))</c>.</summary>
    /// <param name="cell">The initial cell index.</param>
    /// <param name="localRaw">The initial offset, in raw units.</param>
    /// <param name="cellRawLog2">The base-2 logarithm of one cell's raw span.</param>
    /// <returns>The canonical cell index and offset, both exact; the cell index is NOT reduced to any carrier, so the
    /// caller decides representability.</returns>
    /// <remarks>Derived from the definition rather than from a shift schedule: the carry is the exact rounded quotient
    /// <c>⌊(localRaw + 2^(cellRawLog2−1)) / 2^cellRawLog2⌋</c> — half-cells carry UP — taken with
    /// <see cref="FloorQuotient"/>, and the offset is the exact residue. Nothing here masks, shifts or wraps, so the
    /// reference never reproduces the subject's <c>carry &lt;&lt; 36</c> two's-complement congruence; it judges it.</remarks>
    public static (BigInteger Cell, BigInteger LocalRaw) CellSplit(BigInteger cell, BigInteger localRaw, int cellRawLog2) {
        var span = (BigInteger.One << cellRawLog2);
        var carry = FloorQuotient(numerator: (localRaw + (span >> 1)), denominator: span);

        return ((cell + carry), (localRaw - (carry * span)));
    }

    /// <summary>The exact displacement between two canonical world axes, in raw units:
    /// <c>(cell − originCell)·2^cellRawLog2 + (localRaw − originLocalRaw)</c>.</summary>
    /// <param name="cell">The target's cell index.</param>
    /// <param name="localRaw">The target's canonical offset, raw.</param>
    /// <param name="originCell">The origin's cell index.</param>
    /// <param name="originLocalRaw">The origin's canonical offset, raw.</param>
    /// <param name="cellRawLog2">The base-2 logarithm of one cell's raw span.</param>
    /// <returns>The exact displacement, arbitrary width, so the value is the mathematical one and the caller decides
    /// whether the carrier can hold it.</returns>
    /// <remarks>One expression, no branch. The subject reaches the same number through two paths selected by a
    /// conservative <c>|cellDelta| ≤ 2²⁶</c> gate and an overflow test, one of them relying on the canonical offsets
    /// differing by less than a cell; this knows nothing of either and therefore judges both.</remarks>
    public static BigInteger CellDelta(BigInteger cell, BigInteger localRaw, BigInteger originCell, BigInteger originLocalRaw, int cellRawLog2) =>
        (((cell - originCell) << cellRawLog2) + (localRaw - originLocalRaw));

    /// <summary>Replays a fixed schedule of rate steps against an exact rational ledger and returns each step's advanced
    /// quantity together with the remainder retained after it.</summary>
    /// <param name="rateRaws">Each step's per-second rate, raw.</param>
    /// <param name="elapsedTicks">Each step's tick count, parallel to <paramref name="rateRaws"/>.</param>
    /// <param name="ticksPerSecond">The positive time base.</param>
    /// <param name="initialRemainder">The remainder the ledger starts from.</param>
    /// <returns>Per step: the advanced raw quantity and the remainder after it.</returns>
    /// <remarks>The division is TRUNCATION TOWARD ZERO, derived from the definition rather than from a carrier's
    /// divide-and-remainder primitive: the quotient is the magnitude quotient with the numerator's sign re-applied, and
    /// the remainder is the numerator less quotient·denominator. Ties do not arise — nothing is rounded here, which is
    /// exactly the point: the discarded part is RETAINED rather than resolved, and the invariant
    /// <c>ticksPerSecond·Σ advanced + finalRemainder == Σ rate·ticks + initialRemainder</c> holds at every prefix. No
    /// value here is reduced to any carrier and no cast is checked, so the subject's overflow refusal is judged from
    /// outside its own boundary.</remarks>
    public static IReadOnlyList<(BigInteger Advanced, BigInteger Remainder)> RateIntegrationLedger(
        ReadOnlySpan<long> rateRaws,
        ReadOnlySpan<ulong> elapsedTicks,
        long ticksPerSecond,
        long initialRemainder
    ) {
        var denominator = new BigInteger(value: ticksPerSecond);
        var remainder = new BigInteger(value: initialRemainder);
        var steps = new List<(BigInteger Advanced, BigInteger Remainder)>(capacity: rateRaws.Length);

        for (var step = 0; (step < rateRaws.Length); ++step) {
            var numerator = ((new BigInteger(value: rateRaws[step]) * new BigInteger(value: elapsedTicks[step])) + remainder);
            var magnitude = BigInteger.Abs(value: numerator);
            var quotient = BigInteger.Divide(dividend: magnitude, divisor: denominator);

            if (numerator.Sign < 0) { quotient = -quotient; }

            remainder = (numerator - (quotient * denominator));

            steps.Add(item: (quotient, remainder));
        }

        return steps;
    }

    /// <summary>The reference translation of a rigid transform: <c>2·dual·conj(real)</c>, the Hamilton product's lanes
    /// each ONE ties-to-even rounding of its exact four-term sum, then doubled with a wrapping add and no second
    /// rounding — plus the scalar lane, which a unit transform leaves at zero and which the caller inspects.</summary>
    /// <param name="value">The eight lanes in doubling order: the real quaternion's <c>e₀…e₃</c> then the dual's.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">Four destination lanes: the doubled vector lanes <c>e₁, e₂, e₃</c> and the UNDOUBLED scalar
    /// residual <c>e₀</c> of <c>dual·conj(real)</c>, which is the orthogonality witness.</param>
    /// <remarks>The product side is the doubling recursion, which shares nothing with a hand-written Hamilton product's
    /// sixteen signed products. The conjugation is an explicit arbitrary-width negation wrapped once, so the
    /// two's-complement fixed point at the signed minimum is reproduced by STATEMENT rather than inherited from an
    /// <c>unchecked</c> negation. The doubling is one exact left shift, where the subject writes a wrapping add.</remarks>
    public static void RigidTranslation(ReadOnlySpan<long> value, int shift, Span<long> result) {
        Span<long> conjugate = stackalloc long[4];
        Span<long> product = stackalloc long[4];

        conjugate[0] = value[0];
        conjugate[1] = WrapToRaw(value: -new BigInteger(value: value[1]));
        conjugate[2] = WrapToRaw(value: -new BigInteger(value: value[2]));
        conjugate[3] = WrapToRaw(value: -new BigInteger(value: value[3]));

        CayleyDicksonProduct(left: value[4..8], right: conjugate, floors: 2, shift: shift, result: product);

        result[0] = WrapToRaw(value: (new BigInteger(value: product[1]) << 1));
        result[1] = WrapToRaw(value: (new BigInteger(value: product[2]) << 1));
        result[2] = WrapToRaw(value: (new BigInteger(value: product[3]) << 1));
        result[3] = product[0];
    }

    /// <summary>The reference point action of a rigid transform: the two-stage rotation sandwich by the real quaternion,
    /// then the componentwise wrapping addition of <see cref="RigidTranslation"/>'s doubled lanes.</summary>
    /// <param name="value">The eight lanes in doubling order.</param>
    /// <param name="point">The three point lanes, raw.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">The three destination lanes.</param>
    /// <remarks>The SCHEDULE — sandwich, then add a freshly formed translation — is the subject's documented
    /// composition and is carried faithfully here; what is re-derived independently is every step's arithmetic.</remarks>
    public static void RigidPointAction(ReadOnlySpan<long> value, ReadOnlySpan<long> point, int shift, Span<long> result) {
        Span<long> rotation = [value[1], value[2], value[3], value[0]];
        Span<long> rotated = stackalloc long[3];
        Span<long> translation = stackalloc long[4];

        QuaternionSandwich(rotation: rotation, vector: point, shift: shift, result: rotated);
        RigidTranslation(value: value, shift: shift, result: translation);

        for (var lane = 0; (lane < 3); ++lane) {
            result[lane] = WrapToRaw(value: (new BigInteger(value: rotated[lane]) + translation[lane]));
        }
    }

    // coefficient·√radicand ≤ bound, exactly: the signs are read off first so the single squaring never flips the
    // inequality. The radicand is non-negative by construction.
    private static bool SurdAtMost(BigInteger coefficient, BigInteger radicand, BigInteger bound) {
        if (coefficient.Sign <= 0) { return ((bound.Sign >= 0) || ((coefficient * coefficient * radicand) >= (bound * bound))); }

        return ((bound.Sign > 0) && ((coefficient * coefficient * radicand) <= (bound * bound)));
    }

    /// <summary>The exact decimal expansion of the SIGNED dyadic rational <c>value / 2^shift</c>, rendered
    /// sign-magnitude the way the signed fixed-point family renders — a leading minus, then the magnitude's
    /// expansion.</summary>
    /// <param name="value">The exact signed numerator.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The exact invariant-culture text.</returns>
    /// <remarks>The magnitude side is <see cref="ExactDyadicDecimal"/>, whose digits come from
    /// <c>n/2ˢ == (n·5ˢ)/10ˢ</c>, deliberately unlike any digit-at-a-time renderer.</remarks>
    public static string ExactDyadicDecimalSigned(BigInteger value, int shift) =>
        ((value.Sign < 0)
            ? ("-" + ExactDyadicDecimal(numerator: -value, shift: shift))
            : ExactDyadicDecimal(numerator: value, shift: shift));

    /// <summary>Quantizes the exact decimal literal <c>numerator / 10^decimalExponent</c> onto the <c>2^shift</c> grid:
    /// ONE ties-to-even rounding of the exact rational <c>numerator·2^shift / 10^decimalExponent</c>, plus the verdict
    /// of the ASYMMETRIC signed range <c>[−2⁶³, 2⁶³ − 1]</c>.</summary>
    /// <param name="numerator">The literal's digits as one exact integer, sign included.</param>
    /// <param name="decimalExponent">The number of decimal fraction digits those digits carry.</param>
    /// <param name="shift">The fixed-point scale.</param>
    /// <returns>Whether the quantized value fits the carrier, and that value when it does.</returns>
    /// <remarks>A deliberately different route from the subject's parser, which never forms the whole rational: that
    /// one keeps a seventeen-digit fraction prefix, divides by the reduced denominator <c>2·5¹⁷</c>, and repairs the tie
    /// with a sticky flag for the digits it discarded. The two agree because <c>2·5¹⁷</c> divides <c>10¹⁷</c>, which is
    /// a theorem about the subject rather than a shared step.</remarks>
    public static (bool InRange, long Raw) DecimalToRaw(BigInteger numerator, int decimalExponent, int shift) {
        var exact = RoundRationalTiesToEven(
            numerator: (numerator << shift),
            denominator: BigInteger.Pow(value: new BigInteger(value: 10), exponent: decimalExponent)
        );
        var inRange = ((exact >= long.MinValue) && (exact <= long.MaxValue));

        return (inRange, (inRange ? ((long)exact) : 0L));
    }

    /// <summary>An enclosure of <c>log₂(raw / 2¹⁶) · 2^(16 + guardBitCount)</c> for a positive raw.</summary>
    /// <param name="raw">The subject's raw, which must be positive.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>Route: the integer part is the bit length (exact). The fraction is produced by REPEATED SQUARING — the
    /// mantissa in <c>[1, 2)</c> is held exactly as <c>raw &lt;&lt; (SeriesBitCount − ⌊log₂ raw⌋)</c>, then squared bit
    /// by bit, emitting a one and halving whenever the square reaches two. The two chains square with opposite
    /// truncation, so the pair is an enclosure; the gap at most quadruples per step from an EXACT start, which at a
    /// hundred and sixty working bits leaves it below the emitted grid throughout. There is no table and no polynomial
    /// anywhere in this route — the subject's 128-entry reciprocal table and quartic residual are reproduced by
    /// nothing here.</remarks>
    public static Enclosure EncloseLog2(long raw, int guardBitCount) {
        var value = new BigInteger(value: raw);
        var bitLength = ((int)value.GetBitLength());
        var integerPart = new BigInteger(value: ((bitLength - 1) - 16));
        var two = (BigInteger.One << (SeriesBitCount + 1));
        var lowState = (value << (SeriesBitCount - (bitLength - 1)));
        var highState = lowState;
        var lowFraction = BigInteger.Zero;
        var highFraction = BigInteger.Zero;

        for (var bit = 1; (bit <= LogFractionBitCount); ++bit) {
            lowState = ((lowState * lowState) >> SeriesBitCount);
            highState = CeilingShiftRight(value: (highState * highState), shift: SeriesBitCount);

            if (lowState >= two) {
                lowFraction += (BigInteger.One << (LogFractionBitCount - bit));
                lowState >>= 1;
            }

            if (highState >= two) {
                highFraction += (BigInteger.One << (LogFractionBitCount - bit));
                highState = CeilingShiftRight(value: highState, shift: 1);
            }
        }

        var scaled = (integerPart << LogFractionBitCount);

        return Rescale(
            value: new Enclosure(Low: (scaled + lowFraction), High: ((scaled + highFraction) + BigInteger.One)),
            fromBitCount: LogFractionBitCount,
            toBitCount: (16 + guardBitCount)
        );
    }

    /// <summary>An enclosure of <c>2^(scaledExponent / 2^exponentBitCount) · 2^(16 + guardBitCount)</c>.</summary>
    /// <param name="scaledExponent">The exponent, scaled by <c>2^exponentBitCount</c>.</param>
    /// <param name="exponentBitCount">The exponent's scale; it may not exceed the ladder's depth.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>Route: split the exponent into <c>k + u</c> with <c>u ∈ [0, 1)</c>, then form <c>2^u</c> as the product
    /// of the ladder factors <c>2^(2⁻ⁱ)</c> over u's set bits. The ladder is built once by REPEATED INTEGER SQUARE
    /// ROOTS of two, floored for the lower chain and ceilinged for the upper one, so the whole construction is
    /// exact-integer and shares nothing with the subject's 128-entry table and quartic residual.</remarks>
    public static Enclosure EncloseExp2(BigInteger scaledExponent, int exponentBitCount, int guardBitCount) {
        var wholePart = (scaledExponent >> exponentBitCount);
        var fraction = (scaledExponent - (wholePart << exponentBitCount));
        var low = (BigInteger.One << SeriesBitCount);
        var high = low;

        for (var level = 1; (level <= exponentBitCount); ++level) {
            if (!(((fraction >> (exponentBitCount - level)) & BigInteger.One)).IsZero) {
                low = ((low * LowLadder[level]) >> SeriesBitCount);
                high = CeilingShiftRight(value: (high * HighLadder[level]), shift: SeriesBitCount);
            }
        }

        return Rescale(
            value: new Enclosure(Low: low, High: high),
            fromBitCount: SeriesBitCount,
            toBitCount: (((int)wholePart) + 16 + guardBitCount)
        );
    }

    /// <summary>An enclosure of <c>atan2(y, x)·2^(16 + guardBitCount)</c> over raw Q48.16 operands. The ratio is
    /// scale-invariant, so the raw operands go straight in.</summary>
    /// <param name="yRaw">The ordinate's raw.</param>
    /// <param name="xRaw">The abscissa's raw.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>The quadrant split is the textbook definition (by the signs of <paramref name="xRaw"/> and
    /// <paramref name="yRaw"/>, with the axes named explicitly), NOT the subject's min/max octant fold; the VALUE comes
    /// from the alternating arctangent series rather than the subject's interval table. What the two sides do share is
    /// the mathematical case analysis itself — there is only one atan2 — so the leg names the series as the independent
    /// part.</remarks>
    public static Enclosure EncloseAtan2(long yRaw, long xRaw, int guardBitCount) {
        if ((0L == yRaw) && (0L == xRaw)) {
            return new(Low: BigInteger.Zero, High: BigInteger.Zero);
        }

        var ordinate = BigInteger.Abs(value: new BigInteger(value: yRaw));
        var abscissa = BigInteger.Abs(value: new BigInteger(value: xRaw));
        var circle = Pi(bitCount: ArcTangentBitCount);
        Enclosure principal;

        if (xRaw > 0L) {
            principal = EncloseArcTangent(numerator: ordinate, denominator: abscissa, bitCount: ArcTangentBitCount);
        } else if (0L == xRaw) {
            principal = new(Low: (circle.Low >> 1), High: CeilingShiftRight(value: circle.High, shift: 1));
        } else {
            var inner = EncloseArcTangent(numerator: ordinate, denominator: abscissa, bitCount: ArcTangentBitCount);

            principal = new(Low: (circle.Low - inner.High), High: (circle.High - inner.Low));
        }

        return Rescale(
            value: ((yRaw < 0L) ? new Enclosure(Low: -principal.High, High: -principal.Low) : principal),
            fromBitCount: ArcTangentBitCount,
            toBitCount: (16 + guardBitCount)
        );
    }

    /// <summary>An enclosure of <c>(sin θ, cos θ)·2^(16 + guardBitCount)</c> for <c>θ = raw / 2¹⁶</c> radians.</summary>
    /// <param name="raw">The angle's raw.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The two enclosures.</returns>
    /// <remarks>Route: reduce IN RADIANS against <see cref="Pi"/> — <c>n = round(θ / 2π)</c>, residual
    /// <c>r = θ − n·2π ∈ [−π, π]</c> carried at three hundred and eighty-four working bits so the up-to-forty-five-bit
    /// cancellation of a full-range angle is absorbed — then the alternating Taylor series for sine and cosine, whose
    /// remainder after thirty terms is bounded by <c>|r|^61/61!</c>. The subject reduces in TURNS against a single Q64
    /// reciprocal constant and evaluates a seven-term Q60 polynomial after a half-turn fold; neither the reduction
    /// domain, the constant, nor the polynomial is shared.</remarks>
    public static (Enclosure Sin, Enclosure Cos) EncloseSinCos(long raw, int guardBitCount) {
        var circle = Pi(bitCount: AngleBitCount);
        var turn = new Enclosure(Low: (circle.Low << 1), High: (circle.High << 1));
        var theta = (new BigInteger(value: raw) << (AngleBitCount - 16));
        var turns = RoundRationalTiesToEven(numerator: theta, denominator: turn.Low);
        var residual = Residual(theta: theta, turn: turn, turns: turns);

        // The reduction constant is known to within a handful of units at this scale, so the rounded turn count is the
        // nearest one except at an unreachable knife edge; the normalisation makes the residual's bound a fact of the
        // code rather than an argument about that edge.
        while (residual.Low > circle.High) {
            turns += BigInteger.One;
            residual = Residual(theta: theta, turn: turn, turns: turns);
        }

        while (residual.High < -circle.High) {
            turns -= BigInteger.One;
            residual = Residual(theta: theta, turn: turn, turns: turns);
        }

        var narrowing = (AngleBitCount - TrigBitCount);
        var angle = (residual.Low >> narrowing);
        var slack = (((residual.High - residual.Low) >> narrowing) + new BigInteger(value: 1026));
        var scale = (BigInteger.One << TrigBitCount);
        var square = ((angle * angle) >> TrigBitCount);
        var sineTerm = angle;
        var sine = angle;
        var cosineTerm = scale;
        var cosine = scale;

        for (var term = 1; (term <= TrigTermCount); ++term) {
            sineTerm = -((sineTerm * square) / (scale * new BigInteger(value: ((2 * term) * ((2 * term) + 1)))));
            cosineTerm = -((cosineTerm * square) / (scale * new BigInteger(value: (((2 * term) - 1) * (2 * term)))));
            sine += sineTerm;
            cosine += cosineTerm;
        }

        return (
            Rescale(value: new Enclosure(Low: (sine - slack), High: (sine + slack)), fromBitCount: TrigBitCount, toBitCount: (16 + guardBitCount)),
            Rescale(value: new Enclosure(Low: (cosine - slack), High: (cosine + slack)), fromBitCount: TrigBitCount, toBitCount: (16 + guardBitCount))
        );
    }

    /// <summary>Rescales an enclosure between two fixed-point scales with DIRECTED rounding, so the widened or narrowed
    /// pair still brackets the same real value.</summary>
    /// <param name="value">The enclosure.</param>
    /// <param name="fromBitCount">The scale it is stated at.</param>
    /// <param name="toBitCount">The scale it is wanted at.</param>
    /// <returns>The rescaled enclosure.</returns>
    public static Enclosure Rescale(Enclosure value, int fromBitCount, int toBitCount) {
        if (fromBitCount == toBitCount) {
            return value;
        }

        if (fromBitCount > toBitCount) {
            var narrowing = (fromBitCount - toBitCount);

            return new(Low: (value.Low >> narrowing), High: CeilingShiftRight(value: value.High, shift: narrowing));
        }

        var widening = (toBitCount - fromBitCount);

        return new(Low: (value.Low << widening), High: (value.High << widening));
    }

    // The quotient rounded toward POSITIVE infinity, which is what an upper bound must use wherever the lower bound
    // shifts right.
    private static BigInteger CeilingShiftRight(BigInteger value, int shift) =>
        (-((-value) >> shift));

    // The residual θ − n·2π as an enclosure, with the turn product taken in the direction each bound needs.
    private static Enclosure Residual(BigInteger theta, Enclosure turn, BigInteger turns) {
        var productLow = ((turns.Sign >= 0) ? (turns * turn.Low) : (turns * turn.High));
        var productHigh = ((turns.Sign >= 0) ? (turns * turn.High) : (turns * turn.Low));

        return new(Low: (theta - productHigh), High: (theta - productLow));
    }

    // π = 16·atan(1/5) − 4·atan(1/239), evaluated by the series alone: the reduction branch of EncloseArcTangent needs
    // π, and this is where that circularity is cut — both Machin arguments are already below a half.
    private static Enclosure MachinPi(int bitCount) {
        var first = ArcTangentSeries(numerator: BigInteger.One, denominator: new BigInteger(value: 5), bitCount: bitCount);
        var second = ArcTangentSeries(numerator: BigInteger.One, denominator: new BigInteger(value: 239), bitCount: bitCount);

        return new(Low: ((16 * first.Low) - (4 * second.High)), High: ((16 * first.High) - (4 * second.Low)));
    }

    // atan(numerator/denominator)·2^bitCount, enclosed, for a non-negative numerator and a positive denominator. The
    // two exact reductions atan(z) = π/2 − atan(1/z) and atan(z) = π/4 + atan((z−1)/(z+1)) bring every argument onto
    // [0, ½], where the alternating series converges at one bit per two terms or better. NO table, NO per-interval
    // cubic, NO fixed-width truncation: a different derivation from the subject in every part.
    private static Enclosure EncloseArcTangent(BigInteger numerator, BigInteger denominator, int bitCount) {
        if (numerator > denominator) {
            var reciprocal = EncloseArcTangent(numerator: denominator, denominator: numerator, bitCount: bitCount);
            var circle = Pi(bitCount: bitCount);

            return new(Low: ((circle.Low >> 1) - reciprocal.High), High: (CeilingShiftRight(value: circle.High, shift: 1) - reciprocal.Low));
        }

        if ((numerator << 1) > denominator) {
            var folded = EncloseArcTangent(numerator: (denominator - numerator), denominator: (denominator + numerator), bitCount: bitCount);
            var circle = Pi(bitCount: bitCount);

            return new(Low: ((circle.Low >> 2) - folded.High), High: (CeilingShiftRight(value: circle.High, shift: 2) - folded.Low));
        }

        return ArcTangentSeries(numerator: numerator, denominator: denominator, bitCount: bitCount);
    }

    // The alternating series atan(z) = Σ (−1)ᵏ z^(2k+1)/(2k+1) at scale 2^bitCount, for 0 ≤ z ≤ ½. The powers are
    // carried at the working scale rather than as exact rationals, so nothing grows past a few hundred bits; every
    // truncation is bounded by a handful of units and the slack below absorbs the lot along with the tail.
    private static Enclosure ArcTangentSeries(BigInteger numerator, BigInteger denominator, int bitCount) {
        if (numerator.IsZero) {
            return new(Low: BigInteger.Zero, High: BigInteger.Zero);
        }

        var count = ArcTangentTermCount(numerator: numerator, denominator: denominator, bitCount: bitCount);
        var argument = ((numerator << bitCount) / denominator);
        var square = ((argument * argument) >> bitCount);
        var power = argument;
        var low = BigInteger.Zero;
        var high = BigInteger.Zero;

        for (var term = 0; (term < count); ++term) {
            var quotient = (power / new BigInteger(value: ((2 * term) + 1)));

            if (0 == (term & 1)) {
                low += quotient;
                high += (quotient + BigInteger.One);
            } else {
                low -= (quotient + BigInteger.One);
                high -= quotient;
            }

            power = ((power * square) >> bitCount);
        }

        var slack = new BigInteger(value: ((8 * count) + 16));

        return new(Low: (low - slack), High: (high + slack));
    }

    // The terms the series needs for a tail below 2^-(bitCount+8): each term costs at least one guaranteed bit per
    // factor of the argument's reciprocal, bounded below by the operands' bit-length difference.
    private static int ArcTangentTermCount(BigInteger numerator, BigInteger denominator, int bitCount) {
        var ratioBits = (((int)(denominator.GetBitLength() - numerator.GetBitLength())) - 1);

        if (ratioBits < 1) {
            ratioBits = 1;
        }

        return (((bitCount + 16) / (2 * ratioBits)) + 2);
    }

    // The quotient rounded toward POSITIVE infinity for a non-negative numerator and a positive denominator —
    // the ceiling counterpart BigInteger's own truncating `/` does not give, needed by the Gaussian-tail enclosure
    // below wherever a bound must round away from the true value rather than toward it.
    private static BigInteger CeilingDivideNonNegative(BigInteger numerator, BigInteger positiveDenominator) {
        var quotient = BigInteger.DivRem(dividend: numerator, divisor: positiveDenominator, remainder: out var remainder);

        return ((remainder > BigInteger.Zero) ? (quotient + BigInteger.One) : quotient);
    }

    // The working precision and term count the Gaussian-tail enclosure's e^4.5 series below carries. 4.5^41/41! is
    // astronomically below any bit this module ever asks for (Stirling puts it under 2^-100), so forty terms at two
    // hundred fifty-six working bits leaves the geometric remainder bound, not term-by-term rounding, as the
    // dominant — and still utterly negligible — source of width.
    private const int GaussianTailWorkingBitCount = 256;
    private const int GaussianTailTermCount = 40;

    /// <summary>An enclosure of <c>P(|Z|&gt;3)·2^(16+guardBitCount)</c> for a standard normal <c>Z</c> — the
    /// two-sided Gaussian tail beyond three sigma.</summary>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>
    /// Route: Gordon's classical inequality, for <c>x &gt; 0</c>: <c>x·φ(x)/(x²+1) &lt; Q(x) &lt; φ(x)/x</c>, where
    /// <c>Q(x) = P(Z&gt;x)</c> and <c>φ(x) = e^(−x²/2)/√(2π)</c> is the standard normal density. At <c>x = 3</c> this
    /// is <c>(3/10)·φ(3) &lt; Q(3) &lt; φ(3)/3</c> — simple enough to state and check by hand, unlike the tighter
    /// continued fraction <c>Q(x)/φ(x) = 1/(x+) 1/(x+) 2/(x+) 3/(x+) …</c> that pins <c>Q</c> arbitrarily closely; the
    /// classical bound's ~10% relative width at <c>x = 3</c> still lands inside the existing empirical tolerance
    /// band, which is what a reference reachable at reasonable effort needs to do, not more.
    /// <para>
    /// <c>φ(3) = e^(−4.5)/√(2π)</c> is built from two independently-derived pieces, neither touching a <c>Puck.Maths</c>
    /// kernel: <c>e^4.5</c> from its OWN Taylor series (every term positive, so the partial sum is a lower bound and
    /// a geometric tail bound — valid because the term ratio <c>4.5/(n+1)</c> is safely below one past this series'
    /// forty terms — gives the upper one), reciprocated for <c>e^(−4.5)</c>; and <c>√(2π)</c> from <see cref="Pi"/>
    /// (itself Machin's formula, not a transcribed digit string) via <see cref="IntegerSquareRoot"/> with directed
    /// rounding at each bound.
    /// </para>
    /// </remarks>
    public static Enclosure EncloseGaussianTailBeyondThreeSigma(int guardBitCount) {
        var scale = (BigInteger.One << GaussianTailWorkingBitCount);
        // 4.5 = 9/2 is an exact dyadic fraction, so xScaled = 4.5·scale is an exact integer for any working bit
        // count ≥ 1.
        var xScaled = (new BigInteger(value: 9) << (GaussianTailWorkingBitCount - 1));
        var termLow = scale;
        var termHigh = scale;
        var sumLow = scale;
        var sumHigh = scale;

        for (var term = 1; (term <= GaussianTailTermCount); ++term) {
            var stepDenominator = (new BigInteger(value: term) * scale);

            termLow = ((termLow * xScaled) / stepDenominator);
            termHigh = CeilingDivideNonNegative(numerator: (termHigh * xScaled), positiveDenominator: stepDenominator);
            sumLow += termLow;
            sumHigh += termHigh;
        }

        // The tail Σ_{k>N} term_k is bounded by term_N · r/(1−r) with r = x/(N+1) — the largest ratio any later term
        // ever carries, since x/k strictly decreases as k grows past N+1. At x=9/2, N=40: r/(1−r) = x/((N+1)−x) =
        // (9/2)/(73/2) = 9/73, an exact rational applied directly to the already-scaled upper term.
        var remainderBound = CeilingDivideNonNegative(numerator: (termHigh * 9), positiveDenominator: 73);
        var eEnclosure = new Enclosure(Low: sumLow, High: (sumHigh + remainderBound));
        var scaleSquared = (BigInteger.One << (2 * GaussianTailWorkingBitCount));
        // e^(−4.5) = 1/e^4.5: reciprocating an enclosure swaps and inverts its bounds, each rounded away from the
        // true value.
        var negativeExponentialLow = (scaleSquared / eEnclosure.High);
        var negativeExponentialHigh = CeilingDivideNonNegative(numerator: scaleSquared, positiveDenominator: eEnclosure.Low);
        var piEnclosure = Pi(bitCount: GaussianTailWorkingBitCount);
        var twoPiEnclosure = new Enclosure(Low: (piEnclosure.Low * 2), High: (piEnclosure.High * 2));
        // √(2π) at the SAME working scale: S = √(2π)·scale satisfies S² = 2π·scale², so the radicand is the 2π
        // enclosure shifted up by one more working-bit-count factor. IntegerSquareRoot floors, which is already a
        // safe LOWER bound for the low radicand; +1 makes the high side a safe upper bound.
        var sqrtTwoPiLow = IntegerSquareRoot(value: (twoPiEnclosure.Low << GaussianTailWorkingBitCount));
        var sqrtTwoPiHigh = (IntegerSquareRoot(value: (twoPiEnclosure.High << GaussianTailWorkingBitCount)) + BigInteger.One);
        // φ(3) = e^(−4.5)/√(2π), both operands already at scale 2^GaussianTailWorkingBitCount, so the scale factor
        // introduced by the division is corrected by one more multiply by `scale`.
        var densityLow = ((negativeExponentialLow * scale) / sqrtTwoPiHigh);
        var densityHigh = CeilingDivideNonNegative(numerator: (negativeExponentialHigh * scale), positiveDenominator: sqrtTwoPiLow);
        // Gordon's bounds, doubled for the two-sided tail: Q(3) ∈ ((3/10)·φ(3), φ(3)/3).
        var tailLow = ((2 * (3 * densityLow)) / 10);
        var tailHigh = CeilingDivideNonNegative(numerator: (2 * densityHigh), positiveDenominator: 3);

        return Rescale(
            value: new Enclosure(Low: tailLow, High: tailHigh),
            fromBitCount: GaussianTailWorkingBitCount,
            toBitCount: (16 + guardBitCount)
        );
    }

    // ---- the fixed-point vectors: the plane and the space ----
    //
    // Every reference below is ONE ties-to-even rounding of the exact expression at the ideal scale, formed in
    // BigInteger. None builds a machine-width accumulator, none observes the subjects' narrow/wide lane gate, and none
    // calls a Puck.Maths kernel. The rounding faces are the module's own — RoundDyadic, RoundToEvenUnits,
    // RoundRationalTiesToEven and NearestIntegerRoot — shared with the other oracles here and with nothing else.

    /// <summary>The reference fused dot product — ONE ties-to-even rounding of the exact sum of raw Q32 products at
    /// shift sixteen, wrapped to the carrier. The lane count is the span length, so the plane and the space share one
    /// derivation.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws, the same width.</param>
    /// <returns>The dot product's raw.</returns>
    public static long FusedDot(ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        var exact = BigInteger.Zero;

        for (var lane = 0; (lane < left.Length); ++lane) {
            exact += ((BigInteger)left[lane] * right[lane]);
        }

        return RoundDyadic(exact: exact, shift: 16);
    }

    /// <summary>The reference fused wedge — ONE ties-to-even rounding of the exact <c>x₁·y₂ − y₁·x₂</c> at shift
    /// sixteen, wrapped to the carrier.</summary>
    /// <param name="leftX">The first vector's first raw.</param>
    /// <param name="leftY">The first vector's second raw.</param>
    /// <param name="rightX">The second vector's first raw.</param>
    /// <param name="rightY">The second vector's second raw.</param>
    /// <returns>The bivector coefficient's raw.</returns>
    public static long FusedWedge(long leftX, long leftY, long rightX, long rightY) =>
        RoundDyadic(exact: (((BigInteger)leftX * rightY) - ((BigInteger)leftY * rightX)), shift: 16);

    /// <summary>The reference fused cross product — each lane ONE ties-to-even rounding of its exact two-product
    /// difference at shift sixteen, wrapped to the carrier.</summary>
    /// <param name="left">The first vector's three raws.</param>
    /// <param name="right">The second vector's three raws.</param>
    /// <param name="result">The destination lanes, three wide.</param>
    /// <remarks>The right-handed cycle is spelled out lane by lane rather than delegated, so a transposed or mis-signed
    /// lane assignment in the subject has an independently authored orientation to fail against.</remarks>
    public static void FusedCross(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        result[0] = RoundDyadic(exact: (((BigInteger)left[1] * right[2]) - ((BigInteger)left[2] * right[1])), shift: 16);
        result[1] = RoundDyadic(exact: (((BigInteger)left[2] * right[0]) - ((BigInteger)left[0] * right[2])), shift: 16);
        result[2] = RoundDyadic(exact: (((BigInteger)left[0] * right[1]) - ((BigInteger)left[1] * right[0])), shift: 16);
    }

    /// <summary>The per-product-rounding discipline for a dot product: EACH raw Q32 product rounded to Q16 on its own,
    /// then summed exactly and wrapped. The alternative a kernel without a fused accumulator is forced into; it exists
    /// only so a canary can require the fused kernel to differ from it.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws, the same width.</param>
    /// <returns>The per-product dot product's raw.</returns>
    public static long PerProductDot(ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        var total = BigInteger.Zero;

        for (var lane = 0; (lane < left.Length); ++lane) {
            total += RoundToEvenUnits(magnitude: ((BigInteger)left[lane] * right[lane]), shift: 16);
        }

        return WrapToRaw(value: total);
    }

    /// <summary>The per-product-rounding discipline for a wedge — both raw Q32 products rounded to Q16 on their own
    /// before the exact difference.</summary>
    /// <param name="leftX">The first vector's first raw.</param>
    /// <param name="leftY">The first vector's second raw.</param>
    /// <param name="rightX">The second vector's first raw.</param>
    /// <param name="rightY">The second vector's second raw.</param>
    /// <returns>The per-product bivector coefficient's raw.</returns>
    public static long PerProductWedge(long leftX, long leftY, long rightX, long rightY) =>
        WrapToRaw(value: (
            RoundToEvenUnits(magnitude: ((BigInteger)leftX * rightY), shift: 16) -
            RoundToEvenUnits(magnitude: ((BigInteger)leftY * rightX), shift: 16)
        ));

    /// <summary>The per-square-rounding discipline for a squared norm: EACH raw Q32 square rounded to Q16 on its own,
    /// then summed exactly and returned UNWRAPPED.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The per-square squared norm, unwrapped.</returns>
    public static BigInteger PerSquareNorm(ReadOnlySpan<long> raws) {
        var total = BigInteger.Zero;

        foreach (var raw in raws) {
            var magnitude = BigInteger.Abs(value: new BigInteger(value: raw));

            total += RoundToEvenUnits(magnitude: (magnitude * magnitude), shift: 16);
        }

        return total;
    }

    /// <summary>The exact raw Q32 sum of squares — the value both norm kernels start from, unrounded and
    /// unwrapped.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The exact sum of squares.</returns>
    public static BigInteger SquaredNorm(ReadOnlySpan<long> raws) {
        var total = BigInteger.Zero;

        foreach (var raw in raws) {
            var exact = new BigInteger(value: raw);

            total += (exact * exact);
        }

        return total;
    }

    /// <summary>The reference squared length — ONE ties-to-even rounding of <see cref="SquaredNorm"/> at shift sixteen,
    /// returned UNWRAPPED so the caller can state the saturation predicate against <see cref="long.MaxValue"/>, which a
    /// wrap would destroy.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The rounded squared length, unwrapped.</returns>
    public static BigInteger RoundedSquaredNorm(ReadOnlySpan<long> raws) =>
        RoundToEvenUnits(magnitude: SquaredNorm(raws: raws), shift: 16);

    /// <summary>The reference length — the NEAREST integer square root of the exact raw Q32 sum of squares, returned
    /// unwrapped. Rooting a raw Q32 quantity yields a raw Q16 one, so the only rounding is that final root.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The rounded length, unwrapped.</returns>
    /// <remarks>The root is <see cref="NearestIntegerRoot"/>, a bracketed integer search whose predicate is one exact
    /// squaring — deliberately a different route from the subject's floor-then-compare-the-remainder-with-the-root
    /// repair, so a transcription error in either repair rule fails the law.</remarks>
    public static BigInteger NormRoot(ReadOnlySpan<long> raws) =>
        NearestIntegerRoot(value: SquaredNorm(raws: raws));

    /// <summary>The reference linear interpolation, spelled as the DOCUMENTED expression
    /// <c>from + (to − from)·amount</c>: the difference wraps to the carrier, the product carries the family's one
    /// ties-to-even rounding at shift sixteen, and the sum wraps again.</summary>
    /// <param name="from">The origin's raw.</param>
    /// <param name="to">The destination's raw.</param>
    /// <param name="amount">The interpolation fraction's raw.</param>
    /// <returns>The interpolated raw.</returns>
    /// <remarks>Classical rather than transcription: the two wraps and the one rounding are the PUBLISHED contract of
    /// the interpolation, re-derived here in <see cref="BigInteger"/>, not an implementation detail read off a kernel.
    /// ENVELOPE: because that contract names an intermediate wrap, a law standing on this pins the wrap policy
    /// alongside the arithmetic.</remarks>
    public static long LerpRaw(long from, long to, long amount) =>
        WrapToRaw(value: (
            new BigInteger(value: from) +
            RoundDyadic(exact: ((BigInteger)WrapToRaw(value: (new BigInteger(value: to) - from)) * amount), shift: 16)
        ));

    /// <summary>The IDEAL Q16 unit direction: each component ONE ties-to-even rounding of the exact ratio
    /// <c>rawᵢ·2¹⁶ / √(Σ rawⱼ²)</c>, with no preconditioning and no intermediate quantization. A zero vector maps to
    /// the zero vector.</summary>
    /// <param name="raws">The direction's raws.</param>
    /// <param name="result">The destination lanes, the same width.</param>
    /// <remarks>Derived without ever forming a square root: the integer part is bracketed by the exact comparison
    /// <c>q²·S ≤ (|rawᵢ|·2¹⁶)²</c> over the closed range <c>[0, 2¹⁶]</c> — closed because every component's square is
    /// one term of <c>S</c>, so the ratio cannot exceed <c>2¹⁶</c> — and the rounding decision by <c>(2q+1)²·S</c>
    /// against <c>4·(|rawᵢ|·2¹⁶)²</c> with equality resolved to even. It shares no shift, no common denominator and no
    /// root with the staged pipeline the subject runs, which is what makes agreement to within one raw evidence rather
    /// than a restatement.</remarks>
    public static void IdealUnitVector(ReadOnlySpan<long> raws, Span<long> result) {
        var squaredNorm = SquaredNorm(raws: raws);

        if (squaredNorm.IsZero) {
            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = 0L; }

            return;
        }

        for (var lane = 0; (lane < raws.Length); ++lane) {
            var numerator = (BigInteger.Abs(value: new BigInteger(value: raws[lane])) << 16);
            var squaredNumerator = (numerator * numerator);
            var low = BigInteger.Zero;
            var high = ((BigInteger.One << 16) + BigInteger.One);

            while ((high - low) > BigInteger.One) {
                var middle = ((low + high) >> 1);

                if (((middle * middle) * squaredNorm) <= squaredNumerator) { low = middle; } else { high = middle; }
            }

            var odd = ((low << 1) + BigInteger.One);
            var comparison = BigInteger.Compare(left: ((odd * odd) * squaredNorm), right: (squaredNumerator << 2));
            var rounded = (((comparison < 0) || ((0 == comparison) && !((low & BigInteger.One).IsZero)))
                ? (low + BigInteger.One)
                : low);

            result[lane] = WrapToRaw(value: ((raws[lane] < 0L) ? -rounded : rounded));
        }
    }

    /// <summary>The STAGED normalization the shipped pipeline performs, re-derived in <see cref="BigInteger"/>: the
    /// common power-of-two precondition at leading bit forty-five (ties to even on a shrinking shift), the Q16-scaled
    /// nearest root as the single common denominator, and one ties-to-even ratio per component.</summary>
    /// <param name="raws">The direction's raws.</param>
    /// <param name="result">The destination lanes, the same width.</param>
    /// <remarks>A TRANSCRIPTION of the subject's own derivation — it shares no code, and it deliberately shares the
    /// STAGING, so a shared staging error would cancel. Any law standing on it declares faithful carriage and names
    /// <see cref="IdealUnitVector"/> beside it as the independent witness.</remarks>
    public static void StagedUnitVector(ReadOnlySpan<long> raws, Span<long> result) {
        var maximum = BigInteger.Zero;

        foreach (var raw in raws) {
            maximum = BigInteger.Max(left: maximum, right: BigInteger.Abs(value: new BigInteger(value: raw)));
        }

        if (maximum.IsZero) {
            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = 0L; }

            return;
        }

        // Stage one: the common power-of-two precondition at leading bit forty-five. A non-negative shift is a pure
        // left shift and is EXACT; a negative one is a ties-to-even right shift, the pipeline's one lossy step.
        var shift = (45 - ((int)(maximum.GetBitLength() - 1L)));
        var scaled = new BigInteger[raws.Length];
        var squaredSum = BigInteger.Zero;

        for (var lane = 0; (lane < raws.Length); ++lane) {
            var magnitude = BigInteger.Abs(value: new BigInteger(value: raws[lane]));
            var preconditioned = ((shift >= 0) ? (magnitude << shift) : RoundToEvenUnits(magnitude: magnitude, shift: -shift));

            scaled[lane] = ((raws[lane] < 0L) ? -preconditioned : preconditioned);
            squaredSum += (preconditioned * preconditioned);
        }

        // Stage two: the Q16-scaled nearest root, the one common denominator every component divides by. Stage three:
        // one ties-to-even ratio per component against it.
        var denominator = NearestIntegerRoot(value: (squaredSum << 32));

        for (var lane = 0; (lane < raws.Length); ++lane) {
            var quotient = RoundRationalTiesToEven(numerator: (BigInteger.Abs(value: scaled[lane]) << 32), denominator: denominator);

            result[lane] = WrapToRaw(value: ((scaled[lane].Sign < 0) ? -quotient : quotient));
        }
    }

    // The 2^(2^-i) ladder by repeated integer square roots of two, in one direction.
    private static BigInteger[] BuildLadder(bool ceiling) {
        var scale = (BigInteger.One << SeriesBitCount);
        var ladder = new BigInteger[(LadderDepth + 1)];

        ladder[0] = (scale << 1);

        for (var level = 1; (level <= LadderDepth); ++level) {
            var squared = (ladder[(level - 1)] * scale);
            var root = IntegerSquareRoot(value: squared);

            ladder[level] = ((ceiling && ((root * root) != squared)) ? (root + BigInteger.One) : root);
        }

        return ladder;
    }
}
