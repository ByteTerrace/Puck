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
internal static partial class Oracles {
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
        var pooled = ((poolRadiusRaw is { } radius)
            ? BigInteger.Clamp(max: (baseline + radius), min: (baseline - radius), value: rawPooled)
            : rawPooled);
        var ranged = BigInteger.Clamp(max: maximumRaw, min: minimumRaw, value: (pooled + outsidePoolDeltaRaw));
        var result = ((thresholdRaw is { } threshold)
            ? ((ranged >= threshold) ? new BigInteger(value: maximumRaw) : new BigInteger(value: minimumRaw))
            : ranged);

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
        var result = ((thresholdRaw is { } threshold)
            ? ((ranged >= threshold) ? new BigInteger(value: maximumRaw) : new BigInteger(value: minimumRaw))
            : ranged);

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
    /// <summary>The round-to-nearest, exact-ties-UP rational rounding <c>SecondOrderExactMath.RoundToGuardScale</c>
    /// used before it was routed through <see cref="RoundRationalTiesToEven"/> (this module's own tie rule, and now
    /// the subject's): <c>floor((2·numerator·2^fractionBitCount + denominator) / (2·denominator))</c>, for a
    /// non-negative rational.</summary>
    /// <param name="numerator">The exact non-negative numerator.</param>
    /// <param name="denominator">The exact positive denominator.</param>
    /// <param name="fractionBitCount">The result's fraction bit count.</param>
    /// <returns>The rounded, unwrapped raw.</returns>
    public static BigInteger RoundHalfUp(BigInteger numerator, BigInteger denominator, int fractionBitCount) =>
        (((numerator << (fractionBitCount + 1)) + denominator) / (denominator * 2));
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
        RoundDyadicUnsigned(exact: (((BigInteger)x) * y), shift: 16);
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
        ((ulong)RoundDyadic(exact: (((BigInteger)x) * y), shift: 32));
    /// <summary>The reference closed-unit product of THREE raws — one ties-to-even rounding of the exact triple product
    /// at the <c>2⁻³²</c> grid, taken at the tripled scale so that no intermediate is rounded.</summary>
    /// <param name="x">The first factor's raw.</param>
    /// <param name="y">The second factor's raw.</param>
    /// <param name="z">The third factor's raw.</param>
    /// <returns>The product's raw.</returns>
    public static ulong ClosedUnitTripleProduct(ulong x, ulong y, ulong z) =>
        ((ulong)RoundDyadic(exact: ((((BigInteger)x) * y) * z), shift: 64));
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
            .PadLeft(paddingChar: '0', totalWidth: shift);

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
        ((ulong)RoundDyadic(exact: (((BigInteger)x) * y), shift: fractionBitCount));
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

        return (negative ? bits | (1UL << 63) : bits);
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
        var rootProduct = (((BigInteger)v1) * v2);
        var tU = (((((BigInteger)u1) * u2) << 16) + (((BigInteger)qRaw) * rootProduct));
        var tV = ((((((BigInteger)u1) * v2) + (((BigInteger)v1) * u2)) << 16) + (((BigInteger)pRaw) * rootProduct));

        return (RoundDyadic(exact: tU, shift: 32), RoundDyadic(exact: tV, shift: 32));
    }
    /// <summary>The reference algebra norm <c>U² + P·U·V − Q·V²</c>, one Q48→Q16 rounding.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <param name="u">The scalar part, raw.</param>
    /// <param name="v">The root coefficient, raw.</param>
    /// <returns>The norm as a raw.</returns>
    public static long QuadraticNorm(long pRaw, long qRaw, long u, long v) {
        var exact = ((((((BigInteger)u) * u) << 16) + (((BigInteger)pRaw) * (((BigInteger)u) * v))) - (((BigInteger)qRaw) * (((BigInteger)v) * v)));

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
        var exact = ((((BigInteger)pRaw) * n) + (((BigInteger)qRaw) * d));

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
        ((((((BigInteger)u) * u) << 16) + (((BigInteger)pRaw) * (((BigInteger)u) * v))) - (((BigInteger)qRaw) * (((BigInteger)v) * v)));
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
                result = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: result.Item1, u2: power.Item1, v1: result.Item2, v2: power.Item2);
            }

            exponent >>>= 1;

            if (0UL != exponent) {
                power = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: power.Item1, u2: power.Item1, v1: power.Item2, v2: power.Item2);
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
        var quotients = PartialQuotients(d: d, p: p, periodStart: out var periodStart, q: q, r: r);
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
        var quotients = PartialQuotients(d: d, p: p, periodStart: out var periodStart, q: q, r: r);
        var block = new BigInteger[(quotients.Count - periodStart)];

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

        if (AtMost(a: a, b: b, c: c, candidate: low, radicand: radicand)) {
            while (AtMost(a: a, b: b, c: c, candidate: high, radicand: radicand)) {
                low = high;
                high <<= 1;
            }
        } else {
            high = low;
            low = BigInteger.MinusOne;

            while (!AtMost(a: a, b: b, c: c, candidate: low, radicand: radicand)) {
                high = low;
                low <<= 1;
            }
        }

        // low satisfies the predicate, high does not; bisect down to the boundary.
        while ((high - low) > BigInteger.One) {
            var middle = ((low + high) >> 1);

            if (AtMost(a: a, b: b, c: c, candidate: middle, radicand: radicand)) {
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

        for (var position = 0; ((position + 1) < letters.Count);) {
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

            letters.RemoveRange(count: 2, index: position);
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
        var leftLow = leftIndex & (half - 1);
        var rightHigh = (rightIndex >= half);
        var rightLow = rightIndex & (half - 1);

        // (a, 0)·(c, 0) = (a·c, 0).
        if (!leftHigh && !rightHigh) { return CayleyDicksonCharge(floors: (floors - 1), leftIndex: leftLow, rightIndex: rightLow); }

        // (a, 0)·(0, d) = (0, d·a).
        if (!leftHigh) { return CayleyDicksonCharge(floors: (floors - 1), leftIndex: rightLow, rightIndex: leftLow); }

        // (0, b)·(c, 0) = (0, b·c̄).
        if (!rightHigh) { return (ConjugationSign(index: rightLow) * CayleyDicksonCharge(floors: (floors - 1), leftIndex: leftLow, rightIndex: rightLow)); }

        // (0, b)·(0, d) = (−d̄·b, 0).
        return (-ConjugationSign(index: rightLow) * CayleyDicksonCharge(floors: (floors - 1), leftIndex: rightLow, rightIndex: leftLow));
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
                var second = first ^ target;
                var charge = chargeSource(first, second);

                if (0 == charge) { continue; }

                var term = (((BigInteger)left[first]) * right[second]);

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
                var second = first ^ target;
                var charge = chargeSource(first, second);

                if (0 == charge) { continue; }

                var term = (((BigInteger)left[first]) * right[second]);

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
        ReduceBinary(value: value, modulus: (BigInteger.One << degree) | reductionTail);
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

        var modulus = (BigInteger.One << degree) | reductionTail;
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
        return ReduceBinary(modulus: modulus, value: firstCoefficient);
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
            result = BinaryFieldProduct(degree: degree, left: result, reductionTail: reductionTail, right: value);
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
            power = BinaryFieldProduct(degree: degree, left: power, reductionTail: reductionTail, right: point);
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
                if (ReduceBinary(modulus: leading | tail, value: value).IsZero) { return false; }
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

        var ladder = RootSquarings(bound: groupOrder, modulus: modulus);

        foreach (var exponent in ascendingDivisors) {
            if (RootPower(exponent: exponent, ladder: ladder, modulus: modulus).IsOne) {
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
            ladder.Add(item: ReduceBinary(value: CarrylessProduct(left: ladder[(bit - 1)], right: ladder[(bit - 1)]), modulus: modulus));
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

                if (0UL != stepped) { Extend(source: source, value: stepped, vertex: next); }
            }

            visited[vertex] = false;
        }

        for (var source = 0; (source < order); ++source) {
            Array.Clear(array: visited);
            Extend(source: source, value: ClosedUnitOneRaw, vertex: source);
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

                if (excess.Sign > 0) { Extend(source: source, value: ((ulong)excess), vertex: next); }
            }

            visited[vertex] = false;
        }

        for (var source = 0; (source < order); ++source) {
            Array.Clear(array: visited);
            Extend(source: source, value: ClosedUnitOneRaw, vertex: source);
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
                total += PrincipalMinor(matrix: matrix, order: order, rows: choice.AsSpan(length: size, start: 0));

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
        (((rows[0] * ((rows[4] * rows[8]) - (rows[5] * rows[7])))
            - (rows[1] * ((rows[3] * rows[8]) - (rows[5] * rows[6]))))
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
                wide[(leftIndex + rightIndex)] += (((BigInteger)left[leftIndex]) * right[rightIndex]);
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

}
