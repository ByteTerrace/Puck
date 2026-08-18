using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- carrier scalar (UFixedQ4816), the unsigned Q48.16 companion ----

    // The carrier's modulus and its top raw as exact integers. MinValue IS zero here, so none of the signed sibling's
    // asymmetric-minimum envelopes has a counterpart anywhere in this family.
    private static readonly BigInteger UnsignedModulus = (BigInteger.One << UFixedQ4816.TotalBitCount);
    private static readonly BigInteger UnsignedMaximumRaw = (UnsignedModulus - BigInteger.One);

    private const ulong UnsignedOneRaw = (1UL << UFixedQ4816.FractionBitCount);
    private const ulong UnsignedHalfRaw = (UnsignedOneRaw >> 1);
    private const ulong UnsignedFractionMask = (UnsignedOneRaw - 1UL);
    // The largest integral raw — the band Ceiling answers in and refuses one raw above — and its whole-unit count.
    private const ulong UnsignedTopIntegerRaw = ulong.MaxValue & ~UnsignedFractionMask;
    private const ulong UnsignedTopIntegerUnits = (ulong.MaxValue >> UFixedQ4816.FractionBitCount);
    // The widest rendering is 15 integer digits + '.' + 16 fraction digits; the slack keeps one destination span wide
    // enough for a multi-character decimal separator too, so no claim body stackallocs inside a loop.
    private const int UnsignedFormattedCeiling = 48;
    // The style the two-argument parse overloads apply. A strict SUPERSET of the default surface's grammar, which is
    // what makes "the two surfaces agree on well-formed unsigned text" a statement rather than a tautology.
    private const NumberStyles UnsignedParseStyle = NumberStyles.Number;

    /// <summary>Reinterprets a sampled signed raw as the unsigned carrier's raw. The map is a BIJECTION on the
    /// sixty-four-bit word — nothing saturates and nothing is skipped, unlike <see cref="ClosedUnitRaw"/>, which puts
    /// half its draws on one endpoint — so every sampled operand reaches a defined comparison and the committed edge
    /// set lands squarely on the unsigned seams: MaxValue at −1, the bit that splits unsigned order from signed at
    /// long.MinValue, the largest integral raw at −65536, and the top rounding tie at −32768.</summary>
    /// <param name="raw">The sampled raw.</param>
    /// <returns>The unsigned carrier's raw.</returns>
    private static ulong UnsignedRaw(long raw) =>
        unchecked((ulong)raw);
    private static UFixedQ4816 Unsigned(long raw) =>
        UFixedQ4816.FromRawBits(value: UnsignedRaw(raw: raw));
    // A zero divisor divides nothing. One is the substitute, applied identically in subject and oracle, so every
    // sampled pair reaches a defined comparison rather than being skipped asymmetrically; the substituted divisor's own
    // refusal is pinned by unsigned-scalar.construction-and-refusals at all five division entry points.
    private static ulong UnsignedDivisor(ulong raw) =>
        ((0UL == raw)
            ? 1UL
            : raw
        );

    /// <summary>The subject <see cref="UFixedQ4816"/> multiply, sampled raw in and raw out.</summary>
    /// <param name="a">The multiplicand's sampled raw.</param>
    /// <param name="b">The multiplier's sampled raw.</param>
    /// <returns>The product's raw, reinterpreted back onto the sampled space.</returns>
    public static long UnsignedFixedMultiply(long a, long b) =>
        unchecked((long)(Unsigned(raw: a) * Unsigned(raw: b)).Value);
    /// <summary>The oracle for the <see cref="UFixedQ4816"/> multiply — one ties-to-even rounding of the exact product
    /// at the <c>2⁻¹⁶</c> grid, reduced once to the unsigned carrier.</summary>
    /// <param name="a">The multiplicand's sampled raw.</param>
    /// <param name="b">The multiplier's sampled raw.</param>
    /// <returns>The product's raw, reinterpreted back onto the sampled space.</returns>
    public static long UnsignedFixedMultiplyOracle(long a, long b) =>
        unchecked((long)Oracles.UnsignedFixedProduct(
            x: UnsignedRaw(raw: a),
            y: UnsignedRaw(raw: b)
        ));
    /// <summary>The subject <see cref="UFixedQ4816"/> divide, on a substituted non-zero divisor.</summary>
    /// <param name="a">The dividend's sampled raw.</param>
    /// <param name="b">The divisor's sampled raw.</param>
    /// <returns>The quotient's raw, reinterpreted back onto the sampled space.</returns>
    public static long UnsignedFixedDivide(long a, long b) =>
        unchecked((long)(Unsigned(raw: a) / UFixedQ4816.FromRawBits(value: UnsignedDivisor(raw: UnsignedRaw(raw: b)))).Value);
    /// <summary>The oracle for the <see cref="UFixedQ4816"/> divide — one ties-to-even rounding of the exact rational
    /// quotient, reduced once to the unsigned carrier.</summary>
    /// <param name="a">The dividend's sampled raw.</param>
    /// <param name="b">The divisor's sampled raw.</param>
    /// <returns>The quotient's raw, reinterpreted back onto the sampled space.</returns>
    public static long UnsignedFixedDivideOracle(long a, long b) =>
        unchecked((long)Oracles.UnsignedFixedQuotient(
            x: UnsignedRaw(raw: a),
            y: UnsignedDivisor(raw: UnsignedRaw(raw: b))
        ));
    /// <summary>Proves the two truncating kernels against exact arbitrary-width arithmetic — quotient and remainder on
    /// each, the quotient reduced to the carrier and the remainder exact — that the discarding overloads are their
    /// out-parameter siblings, that both reported remainders are in range, and that each rounding operator IS its own
    /// unchecked kernel plus the ties-to-even correction re-derived from the remainder that kernel reports.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedUncheckedKernelsExact(long[] left, long[] right) {
        var rawX = UnsignedRaw(raw: left[0]);
        var rawY = UnsignedDivisor(raw: UnsignedRaw(raw: right[0]));
        var x = UFixedQ4816.FromRawBits(value: rawX);
        var y = UFixedQ4816.FromRawBits(value: rawY);
        var exactProduct = (((BigInteger)rawX) * rawY);
        var exactDividend = (((BigInteger)rawX) << UFixedQ4816.FractionBitCount);
        var truncatedProduct = UFixedQ4816.MultiplyUnchecked(
            remainder: out var productRemainder,
            x: x,
            y: y
        );
        var expectedProduct = Oracles.WrapToUnsignedRaw(value: (exactProduct >> UFixedQ4816.FractionBitCount));
        var expectedProductRemainder = ((ulong)(exactProduct & UnsignedFractionMask));

        if (truncatedProduct.Value != expectedProduct) { return $"the truncated product of ({rawX}, {rawY}) is {truncatedProduct.Value}, expected {expectedProduct}"; }
        if (productRemainder != expectedProductRemainder) { return $"the product remainder of ({rawX}, {rawY}) is {productRemainder}, expected {expectedProductRemainder}"; }
        if (UFixedQ4816.MultiplyUnchecked(
            x: x,
            y: y
        ) != truncatedProduct) { return $"the discarding multiply overload moved ({rawX}, {rawY})"; }

        var truncatedQuotient = UFixedQ4816.DivideUnchecked(
            remainder: out var quotientRemainder,
            x: x,
            y: y
        );
        var exactQuotient = BigInteger.Divide(
            dividend: exactDividend,
            divisor: rawY
        );
        var expectedQuotient = Oracles.WrapToUnsignedRaw(value: exactQuotient);
        var expectedQuotientRemainder = ((ulong)(exactDividend - (exactQuotient * rawY)));

        if (truncatedQuotient.Value != expectedQuotient) { return $"the truncated quotient of ({rawX}, {rawY}) is {truncatedQuotient.Value}, expected {expectedQuotient}"; }
        if (quotientRemainder != expectedQuotientRemainder) { return $"the quotient remainder of ({rawX}, {rawY}) is {quotientRemainder}, expected {expectedQuotientRemainder}"; }
        if (UFixedQ4816.DivideUnchecked(
            x: x,
            y: y
        ) != truncatedQuotient) { return $"the discarding divide overload moved ({rawX}, {rawY})"; }

        // Neither reported remainder can carry a bit the correction below would misread.
        if (productRemainder > UnsignedFractionMask) { return $"the product remainder {productRemainder} is not below one whole unit"; }
        if (quotientRemainder >= rawY) { return $"the quotient remainder {quotientRemainder} is not below the divisor {rawY}"; }

        // The documented contract: each rounding operator IS its unchecked kernel plus the ties-to-even correction of
        // the remainder that kernel reports, both corrections re-derived here from the reported remainder alone.
        var twiceQuotientRemainder = (((BigInteger)quotientRemainder) << 1);
        var productCorrection = ((((productRemainder > UnsignedHalfRaw) || ((productRemainder == UnsignedHalfRaw) && (0UL != (truncatedProduct.Value & 1UL)))))
            ? 1UL
            : 0UL
        );
        var quotientCorrection = ((((twiceQuotientRemainder > rawY) || ((twiceQuotientRemainder == rawY) && (0UL != (truncatedQuotient.Value & 1UL)))))
            ? 1UL
            : 0UL
        );

        if ((x * y).Value != unchecked((truncatedProduct.Value + productCorrection))) { return $"the rounding multiply is not its kernel plus the correction at ({rawX}, {rawY})"; }
        if ((x / y).Value != unchecked((truncatedQuotient.Value + quotientCorrection))) { return $"the rounding divide is not its kernel plus the correction at ({rawX}, {rawY})"; }

        return null;
    }
    /// <summary>Proves the whole wrapping ring EXACTLY at every swept pair — sum, difference, negation, unary plus,
    /// complement, increment, decrement, the three bit operations, the three shifts and the remainder — against
    /// arbitrary-width arithmetic reduced modulo <c>2⁶⁴</c>, and pins four structural facts the XML docs leave
    /// unstated: the two right shifts are one map, the shift count is taken modulo sixty-four, De Morgan holds at the
    /// carrier, and the wrapping sum and difference are exact inverses.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedWrappingAlgebraExact(long[] left, long[] right) {
        var rawA = UnsignedRaw(raw: left[0]);
        var rawB = UnsignedRaw(raw: right[0]);
        var a = UFixedQ4816.FromRawBits(value: rawA);
        var b = UFixedQ4816.FromRawBits(value: rawB);
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var shift = ((int)(rawB & 63UL));

        if ((a + b).Value != Oracles.WrapToUnsignedRaw(value: (exactA + exactB))) { return $"the sum of {rawA} and {rawB} is wrong"; }
        if ((a - b).Value != Oracles.WrapToUnsignedRaw(value: (exactA - exactB))) { return $"the difference of {rawA} and {rawB} is wrong"; }
        if ((-a).Value != Oracles.WrapToUnsignedRaw(value: -exactA)) { return $"the negation of {rawA} is wrong"; }
        if ((+a) != a) { return $"unary plus moved {rawA}"; }
        if ((~a).Value != (UnsignedMaximumRaw - exactA)) { return $"the complement of {rawA} is wrong"; }

        var incremented = a;
        var decremented = a;

        ++incremented;
        --decremented;

        if (incremented.Value != Oracles.WrapToUnsignedRaw(value: (exactA + UnsignedOneRaw))) { return $"the increment of {rawA} is wrong"; }
        if (decremented.Value != Oracles.WrapToUnsignedRaw(value: (exactA - UnsignedOneRaw))) { return $"the decrement of {rawA} is wrong"; }

        if ((a & b).Value != ((ulong)(exactA & exactB))) { return $"the conjunction of {rawA} and {rawB} is wrong"; }
        if ((a | b).Value != ((ulong)(exactA | exactB))) { return $"the disjunction of {rawA} and {rawB} is wrong"; }
        if ((a ^ b).Value != ((ulong)(exactA ^ exactB))) { return $"the exclusive disjunction of {rawA} and {rawB} is wrong"; }

        if ((a << shift).Value != Oracles.WrapToUnsignedRaw(value: (exactA << shift))) { return $"the left shift of {rawA} by {shift} is wrong"; }
        if ((a >> shift).Value != ((ulong)(exactA >> shift))) { return $"the right shift of {rawA} by {shift} is wrong"; }
        if ((a >>> shift) != (a >> shift)) { return $"the two right shifts of {rawA} by {shift} disagree"; }
        if ((a << (shift + 64)) != (a << shift)) { return $"the left shift count is not taken modulo sixty-four at {rawA}"; }
        if ((a >> (shift + 64)) != (a >> shift)) { return $"the right shift count is not taken modulo sixty-four at {rawA}"; }
        if ((a >>> (shift + 64)) != (a >>> shift)) { return $"the unsigned right shift count is not taken modulo sixty-four at {rawA}"; }

        var divisorRaw = UnsignedDivisor(raw: rawB);

        if ((a % UFixedQ4816.FromRawBits(value: divisorRaw)).Value != BigInteger.Remainder(
            dividend: exactA,
            divisor: divisorRaw
        )) { return $"the remainder of {rawA} over {divisorRaw} is wrong"; }

        if (~(a & b) != ((~a) | (~b))) { return $"De Morgan fails on the conjunction at ({rawA}, {rawB})"; }
        if (~(a | b) != ((~a) & (~b))) { return $"De Morgan fails on the disjunction at ({rawA}, {rawB})"; }
        if (((a + b) - b) != a) { return $"the wrapping sum and difference are not inverses at ({rawA}, {rawB})"; }

        return null;
    }
    /// <summary>Proves all seven checked operators against the EXACT value of their operation, which decides both
    /// halves of the statement: the operator must answer that value where it lands inside <c>[0, 2⁶⁴)</c> and must
    /// refuse with an overflow where it does not, and must agree bit-for-bit with its wrapping sibling wherever it
    /// answers. On this carrier the checked negation admits exactly one operand — zero — which is the whole of its
    /// contract.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedCheckedOperatorsRefuse(long[] left, long[] right) {
        var rawA = UnsignedRaw(raw: left[0]);
        var rawB = UnsignedDivisor(raw: UnsignedRaw(raw: right[0]));
        var a = UFixedQ4816.FromRawBits(value: rawA);
        var b = UFixedQ4816.FromRawBits(value: rawB);
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var exactSum = (exactA + exactB);
        var exactDifference = (exactA - exactB);
        var exactProduct = Oracles.RoundToEvenUnits(
            magnitude: (exactA * exactB),
            shift: UFixedQ4816.FractionBitCount
        );
        var exactQuotient = Oracles.RoundRationalTiesToEven(
            denominator: exactB,
            numerator: (exactA << UFixedQ4816.FractionBitCount)
        );

        if (UnsignedCheckedAgrees(
            expected: exactSum,
            name: "sum",
            run: () => checked((a + b))
        ) is { } sum) { return $"{sum} at ({rawA}, {rawB})"; }
        if (UnsignedCheckedAgrees(
            expected: exactDifference,
            name: "difference",
            run: () => checked((a - b))
        ) is { } difference) { return $"{difference} at ({rawA}, {rawB})"; }
        if (UnsignedCheckedAgrees(
            expected: exactProduct,
            name: "product",
            run: () => checked((a * b))
        ) is { } product) { return $"{product} at ({rawA}, {rawB})"; }
        if (UnsignedCheckedAgrees(
            expected: exactQuotient,
            name: "quotient",
            run: () => checked((a / b))
        ) is { } quotient) { return $"{quotient} at ({rawA}, {rawB})"; }
        if (UnsignedCheckedAgrees(
            name: "increment",
            expected: (exactA + UnsignedOneRaw),
            run: () => { var value = a; return checked(++value); }
        ) is { } increment) { return $"{increment} at {rawA}"; }
        if (UnsignedCheckedAgrees(
            name: "decrement",
            expected: (exactA - UnsignedOneRaw),
            run: () => { var value = a; return checked(--value); }
        ) is { } decrement) { return $"{decrement} at {rawA}"; }
        if (UnsignedCheckedAgrees(
            expected: -exactA,
            name: "negation",
            run: () => checked(-a)
        ) is { } negation) { return $"{negation} at {rawA}"; }

        // Where the checked form answers, it agrees with its wrapping sibling bit for bit: the two differ only in the
        // refusal, never in the answer.
        if (
            (exactSum <= UnsignedMaximumRaw) &&
            (checked((a + b)) != (a + b))
        ) { return $"the checked and wrapping sums disagree at ({rawA}, {rawB})"; }
        if (
            (exactDifference.Sign >= 0) &&
            (checked((a - b)) != (a - b))
        ) { return $"the checked and wrapping differences disagree at ({rawA}, {rawB})"; }
        if (
            (exactProduct <= UnsignedMaximumRaw) &&
            (checked((a * b)) != (a * b))
        ) { return $"the checked and wrapping products disagree at ({rawA}, {rawB})"; }
        if (
            (exactQuotient <= UnsignedMaximumRaw) &&
            (checked((a / b)) != (a / b))
        ) { return $"the checked and wrapping quotients disagree at ({rawA}, {rawB})"; }

        return null;
    }

    // One checked operator against the exact ideal value of its operation: the answer where that value is
    // representable, the OverflowException refusal where it is not. The two failure directions are reported apart, so a
    // wrong refusal never reads as a wrong answer.
    private static string? UnsignedCheckedAgrees(string name, BigInteger expected, Func<UFixedQ4816> run) {
        var representable = ((expected.Sign >= 0) && (expected <= UnsignedMaximumRaw));

        try {
            var actual = run();

            return (representable
                ? ((actual.Value == expected)
                    ? null
                    : $"the checked {name} answered {actual.Value}, expected {expected}")
                : $"the checked {name} answered {actual.Value} where {expected} is not representable"
            );
        } catch (OverflowException) {
            return (representable
                ? $"the checked {name} refused where {expected} is representable"
                : null
            );
        }
    }

    /// <summary>Proves the saturating pair, the two order selections, the four magnitude selectors, the clamp and the
    /// absolute value against arbitrary-width arithmetic — every one of them EXACT on both sides — and pins the
    /// endpoints they stop at: MaxValue above and MinValue, which on this carrier is zero, below.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedSaturatingAndSelectionExact(long[] left, long[] right) {
        var rawA = UnsignedRaw(raw: left[0]);
        var rawB = UnsignedRaw(raw: right[0]);
        var rawC = UnsignedRaw(raw: left[1]);
        var a = UFixedQ4816.FromRawBits(value: rawA);
        var b = UFixedQ4816.FromRawBits(value: rawB);
        var c = UFixedQ4816.FromRawBits(value: rawC);
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var exactC = new BigInteger(value: rawC);

        if (UFixedQ4816.AddSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: (exactA + exactB),
            right: UnsignedMaximumRaw
        )) { return $"the saturating sum of {rawA} and {rawB} is wrong"; }
        if (UFixedQ4816.SubtractSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: (exactA - exactB),
            right: BigInteger.Zero
        )) { return $"the saturating difference of {rawA} and {rawB} is wrong"; }
        if (UFixedQ4816.Max(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: exactA,
            right: exactB
        )) { return $"the maximum of {rawA} and {rawB} is wrong"; }
        if (UFixedQ4816.Min(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: exactA,
            right: exactB
        )) { return $"the minimum of {rawA} and {rawB} is wrong"; }

        // Magnitude IS value on an unsigned carrier, so all four magnitude selectors are the two order selections.
        if (UFixedQ4816.MaxMagnitude(
            x: a,
            y: b
        ) != UFixedQ4816.Max(
            x: a,
            y: b
        )) { return $"MaxMagnitude is not Max at ({rawA}, {rawB})"; }
        if (UFixedQ4816.MaxMagnitudeNumber(
            x: a,
            y: b
        ) != UFixedQ4816.Max(
            x: a,
            y: b
        )) { return $"MaxMagnitudeNumber is not Max at ({rawA}, {rawB})"; }
        if (UFixedQ4816.MinMagnitude(
            x: a,
            y: b
        ) != UFixedQ4816.Min(
            x: a,
            y: b
        )) { return $"MinMagnitude is not Min at ({rawA}, {rawB})"; }
        if (UFixedQ4816.MinMagnitudeNumber(
            x: a,
            y: b
        ) != UFixedQ4816.Min(
            x: a,
            y: b
        )) { return $"MinMagnitudeNumber is not Min at ({rawA}, {rawB})"; }
        if (UFixedQ4816.Abs(value: a) != a) { return $"Abs moved {rawA}"; }

        // The clamp: the third sampled raw widens or narrows the window, and the bounds are ordered before the call so
        // the value statement never collides with the inverted-range refusal below.
        var low = UFixedQ4816.Min(
            x: b,
            y: c
        );
        var high = UFixedQ4816.Max(
            x: b,
            y: c
        );
        var clamped = UFixedQ4816.Clamp(
            maximum: high,
            minimum: low,
            value: a
        );
        var expectedClamp = BigInteger.Min(
            left: BigInteger.Max(
                left: exactA,
                right: BigInteger.Min(
                    left: exactB,
                    right: exactC
                )
            ),
            right: BigInteger.Max(
                left: exactB,
                right: exactC
            )
        );

        if (clamped.Value != expectedClamp) { return $"the clamp of {rawA} into [{low.Value}, {high.Value}] is {clamped.Value}, expected {expectedClamp}"; }
        if (UFixedQ4816.Clamp(
            maximum: a,
            minimum: a,
            value: a
        ) != a) { return $"the clamp is not the identity inside its own bounds at {rawA}"; }
        if (
            (low != high) &&
            !Throws<ArgumentException>(action: () => _ = UFixedQ4816.Clamp(
            maximum: low,
            minimum: high,
            value: a
        ))
        ) { return $"the clamp accepted the inverted range [{high.Value}, {low.Value}]"; }

        if (UFixedQ4816.AddSaturating(
            x: a,
            y: UFixedQ4816.Zero
        ) != a) { return $"zero is not neutral for the saturating sum at {rawA}"; }
        if (UFixedQ4816.SubtractSaturating(
            x: a,
            y: UFixedQ4816.Zero
        ) != a) { return $"zero is not neutral for the saturating difference at {rawA}"; }
        if (UFixedQ4816.AddSaturating(
            x: UFixedQ4816.MaxValue,
            y: UFixedQ4816.Epsilon
        ) != UFixedQ4816.MaxValue) { return "the saturating sum did not absorb at the maximum"; }
        if (UFixedQ4816.SubtractSaturating(
            x: UFixedQ4816.MinValue,
            y: UFixedQ4816.Epsilon
        ) != UFixedQ4816.Zero) { return "the saturating difference did not absorb at the minimum"; }
        if (UFixedQ4816.AddSaturating(
            x: a,
            y: b
        ) < a) { return $"the saturating sum wrapped below its addend at ({rawA}, {rawB})"; }
        if (UFixedQ4816.SubtractSaturating(
            x: a,
            y: b
        ) > a) { return $"the saturating difference wrapped above its minuend at ({rawA}, {rawB})"; }

        return null;
    }
    /// <summary>Proves the five integer maps at the sampled raw and then, whatever the domain drew, over the top
    /// integer band where the two that can leave the carrier actually do.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedIntegerDecompositionExact(long[] left, long[] right) {
        if (UnsignedIntegerDecompositionAt(
            raw: UnsignedRaw(raw: left[0]),
            ceilingRefused: out _,
            roundingRefused: out _
        ) is { } sampled) { return sampled; }

        foreach (var raw in UnsignedIntegerBandRungs) {
            if (UnsignedIntegerDecompositionAt(
                ceilingRefused: out _,
                raw: raw,
                roundingRefused: out _
            ) is { } rung) { return rung; }
        }

        return null;
    }

    // The five integer maps at one raw, against arbitrary-width derivations: the floor and the truncation as the raw
    // less its remainder modulo one whole unit, the fractional part as that remainder, the ceiling as the floor plus a
    // whole unit where the fraction is non-zero, and the rounding as an independently re-derived ties-to-even step. The
    // two that can leave the carrier must ANSWER below 2⁶⁴ and REFUSE at or above it; the caller learns which happened,
    // so an exhaustive sweep can count the refusals rather than only assert them.
    private static string? UnsignedIntegerDecompositionAt(ulong raw, out bool ceilingRefused, out bool roundingRefused) {
        var value = UFixedQ4816.FromRawBits(value: raw);
        var exact = new BigInteger(value: raw);
        var fraction = (exact % UnsignedOneRaw);
        var floor = (exact - fraction);
        var expectedCeiling = (fraction.IsZero
            ? floor
            : (floor + UnsignedOneRaw)
        );
        var expectedRounding = (Oracles.RoundToEvenUnits(
            magnitude: exact,
            shift: UFixedQ4816.FractionBitCount
        ) << UFixedQ4816.FractionBitCount);

        ceilingRefused = (expectedCeiling > UnsignedMaximumRaw);
        roundingRefused = (expectedRounding > UnsignedMaximumRaw);

        if (UFixedQ4816.Floor(value: value).Value != floor) { return $"the floor of {raw} is wrong"; }
        if (UFixedQ4816.Truncate(value: value).Value != floor) { return $"the truncation of {raw} is wrong"; }
        if (UFixedQ4816.Fractional(value: value).Value != fraction) { return $"the fractional part of {raw} is wrong"; }
        if (UFixedQ4816.Truncate(value: value) != UFixedQ4816.Floor(value: value)) { return $"truncation and floor disagree at {raw}"; }
        if ((UFixedQ4816.Floor(value: value).Value + UFixedQ4816.Fractional(value: value).Value) != raw) { return $"the decomposition of {raw} does not recover it"; }
        if (fraction >= UnsignedOneRaw) { return $"the fractional part of {raw} left the unit interval"; }
        if ((expectedCeiling - floor) != (fraction.IsZero
            ? BigInteger.Zero
            : UnsignedOneRaw)) { return $"the ceiling is not the floor plus one whole unit at {raw}"; }

        if (ceilingRefused) {
            if (!Throws<OverflowException>(action: () => _ = UFixedQ4816.Ceiling(value: value))) { return $"the ceiling of {raw} was answered past the carrier"; }
        } else if (UFixedQ4816.Ceiling(value: value).Value != expectedCeiling) {
            return $"the ceiling of {raw} is wrong";
        }

        if (roundingRefused) {
            if (!Throws<OverflowException>(action: () => _ = UFixedQ4816.Round(value: value))) { return $"the rounding of {raw} was answered past the carrier"; }
        } else if (UFixedQ4816.Round(value: value).Value != expectedRounding) {
            return $"the rounding of {raw} is wrong";
        }

        if (fraction.IsZero) {
            if (UFixedQ4816.Ceiling(value: value) != value) { return $"the ceiling of the integral raw {raw} moved it"; }
            if (UFixedQ4816.Round(value: value) != value) { return $"the rounding of the integral raw {raw} moved it"; }
        }

        if (
            !ceilingRefused &&
            !roundingRefused
        ) {
            var subjectFloor = UFixedQ4816.Floor(value: value);
            var subjectCeiling = UFixedQ4816.Ceiling(value: value);
            var subjectRounding = UFixedQ4816.Round(value: value);

            if (subjectFloor > subjectCeiling) { return $"the floor exceeds the ceiling at {raw}"; }
            if (
                (subjectRounding < subjectFloor) ||
                (subjectRounding > subjectCeiling)
            ) { return $"the rounding of {raw} is outside its floor and ceiling"; }
            if ((subjectCeiling.Value - subjectFloor.Value) is not (0UL or UnsignedOneRaw)) { return $"the ceiling and floor of {raw} differ by other than a whole unit"; }
        }

        return null;
    }

    /// <summary>Proves the seventeen classifiers at the sampled raw and at the four raws the format's own structure
    /// names, whatever the domain drew.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedNumberPredicatesExact(long[] left, long[] right) {
        if (UnsignedNumberPredicatesAt(raw: UnsignedRaw(raw: left[0])) is { } sampled) { return sampled; }

        foreach (var raw in ((ReadOnlySpan<ulong>)[0UL, UnsignedOneRaw, UnsignedTopIntegerRaw, ulong.MaxValue])) {
            if (UnsignedNumberPredicatesAt(raw: raw) is { } rung) { return rung; }
        }

        return null;
    }

    // The seventeen classifiers at one raw: integrality and its two parities read from the value's exact divisibility
    // rather than from a bit mask, zero and normality from the exact value, and the twelve constant ones asserted at
    // the value the format admits. IsPositive holding AT ZERO is the platform's own convention for an unsigned carrier
    // — byte.IsPositive and its siblings return true unconditionally — and the XML docs are silent, so the fact is
    // derived from the format and pinned here.
    private static string? UnsignedNumberPredicatesAt(ulong raw) {
        var value = UFixedQ4816.FromRawBits(value: raw);
        var exact = new BigInteger(value: raw);
        var isInteger = (exact % UnsignedOneRaw).IsZero;
        var wholeUnits = (exact >> UFixedQ4816.FractionBitCount);
        var isEven = (isInteger && (wholeUnits % 2).IsZero);
        var isOdd = (isInteger && !(wholeUnits % 2).IsZero);

        if (UFixedQ4816.IsZero(value: value) != exact.IsZero) { return $"the zero classification of {raw} is wrong"; }
        if (UFixedQ4816.IsNormal(value: value) != !exact.IsZero) { return $"the normal classification of {raw} is wrong"; }
        if (UFixedQ4816.IsInteger(value: value) != isInteger) { return $"the integrality of {raw} is wrong"; }
        if (UFixedQ4816.IsEvenInteger(value: value) != isEven) { return $"the even-integer classification of {raw} is wrong"; }
        if (UFixedQ4816.IsOddInteger(value: value) != isOdd) { return $"the odd-integer classification of {raw} is wrong"; }

        if (!UFixedQ4816.IsCanonical(value: value)) { return $"{raw} is not canonical"; }
        if (!UFixedQ4816.IsFinite(value: value)) { return $"{raw} is not finite"; }
        if (!UFixedQ4816.IsRealNumber(value: value)) { return $"{raw} is not a real number"; }
        if (!UFixedQ4816.IsPositive(value: value)) { return $"{raw} is not positive on a carrier that has no negative side"; }
        if (UFixedQ4816.IsNegative(value: value)) { return $"{raw} claims to be negative"; }
        if (UFixedQ4816.IsNaN(value: value)) { return $"{raw} claims not to be a number"; }
        if (UFixedQ4816.IsInfinity(value: value)) { return $"{raw} claims to be infinite"; }
        if (UFixedQ4816.IsPositiveInfinity(value: value)) { return $"{raw} claims to be positive infinity"; }
        if (UFixedQ4816.IsNegativeInfinity(value: value)) { return $"{raw} claims to be negative infinity"; }
        if (UFixedQ4816.IsComplexNumber(value: value)) { return $"{raw} claims to be a complex number"; }
        if (UFixedQ4816.IsImaginaryNumber(value: value)) { return $"{raw} claims to be an imaginary number"; }
        if (UFixedQ4816.IsSubnormal(value: value)) { return $"{raw} claims to be subnormal"; }

        if (
            UFixedQ4816.IsEvenInteger(value: value) &&
            UFixedQ4816.IsOddInteger(value: value)
        ) { return $"{raw} is both even and odd"; }
        if ((UFixedQ4816.IsEvenInteger(value: value) || UFixedQ4816.IsOddInteger(value: value)) != UFixedQ4816.IsInteger(value: value)) { return $"the parities do not cover integrality at {raw}"; }
        if (UFixedQ4816.IsZero(value: value) == UFixedQ4816.IsNormal(value: value)) { return $"the zero and normal classifiers agree at {raw}"; }
        if (UFixedQ4816.IsZero(value: value) != (value == UFixedQ4816.Zero)) { return $"the zero classifier disagrees with equality against zero at {raw}"; }

        return null;
    }

    /// <summary>Proves the order the carrier reports is the exact UNSIGNED order of the raws, read through every
    /// operator the comparison contract names and through the boxed route as well — which is precisely where a signed
    /// reading would answer the other way, and the reason this family exists beside its signed sibling.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedOrderAndComparisonExact(long[] left, long[] right) {
        var rawA = UnsignedRaw(raw: left[0]);
        var rawB = UnsignedRaw(raw: right[0]);
        var a = UFixedQ4816.FromRawBits(value: rawA);
        var b = UFixedQ4816.FromRawBits(value: rawB);
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var order = BigInteger.Compare(
            left: exactA,
            right: exactB
        );

        if (Math.Sign(value: a.CompareTo(other: b)) != order) { return $"the comparison of {rawA} and {rawB} reports the wrong order"; }
        if (Math.Sign(value: a.CompareTo(obj: ((object)b))) != order) { return $"the boxed comparison of {rawA} and {rawB} reports the wrong order"; }
        if ((a < b) != (order < 0)) { return $"the less-than operator disagrees at ({rawA}, {rawB})"; }
        if ((a <= b) != (order <= 0)) { return $"the less-or-equal operator disagrees at ({rawA}, {rawB})"; }
        if ((a > b) != (order > 0)) { return $"the greater-than operator disagrees at ({rawA}, {rawB})"; }
        if ((a >= b) != (order >= 0)) { return $"the greater-or-equal operator disagrees at ({rawA}, {rawB})"; }

        // The order is UNSIGNED: the top bit is an ordinary magnitude bit, so every raw carrying it sorts above every
        // raw that does not.
        if (
            (0UL != (rawA >> 63)) &&
            (0UL == (rawB >> 63)) &&
            (a <= b)
        ) { return $"the top-bit raw {rawA} did not sort above {rawB}"; }
        if (
            (0UL == (rawA >> 63)) &&
            (0UL != (rawB >> 63)) &&
            (a >= b)
        ) { return $"the raw {rawA} did not sort below the top-bit raw {rawB}"; }

        if (Math.Sign(value: a.CompareTo(other: b)) != -Math.Sign(value: b.CompareTo(other: a))) { return $"the comparison is not antisymmetric at ({rawA}, {rawB})"; }
        if (0 != a.CompareTo(other: a)) { return $"the raw {rawA} does not compare equal to itself"; }
        if ((a == b) != (0 == order)) { return $"equality disagrees with a zero comparison at ({rawA}, {rawB})"; }
        if (
            (UFixedQ4816.MinValue > a) ||
            (UFixedQ4816.MaxValue < a)
        ) { return $"the raw {rawA} falls outside the declared extremes"; }

        return null;
    }
    /// <summary>Proves the unsigned Q48.16 grid on its own ladder: the declared layout and the declared constants are
    /// one consistent fact, the raw survives both construction routes, the whole-number seam admits exactly the
    /// representable integers and refuses the first value beyond, the boxed comparison refuses a foreign type, a zero
    /// divisor is refused at all five division entry points, and the two integer maps that can leave the type answer
    /// and refuse exactly where the top of the range says they must.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedConstructionAndRefusals() {
        // Read the declared counts into locals so the layout statement is a comparison the RUN makes rather than one
        // the compiler folds away; a folded comparison would make the counterexample unreachable.
        var fractionBits = UFixedQ4816.FractionBitCount;
        var integerBits = UFixedQ4816.IntegerBitCount;
        var totalBits = UFixedQ4816.TotalBitCount;
        var radix = new BigInteger(value: UFixedQ4816.Radix);

        if (16 != fractionBits) { return $"the fraction bit count is {fractionBits}"; }
        if (48 != integerBits) { return $"the integer bit count is {integerBits}"; }
        if (64 != totalBits) { return $"the total bit count is {totalBits}"; }
        if ((fractionBits + integerBits) != totalBits) { return "the two bit counts do not sum to the total"; }
        if (2 != UFixedQ4816.Radix) { return $"the radix is {UFixedQ4816.Radix}"; }
        if (UFixedQ4816.One.Value != BigInteger.Pow(
            exponent: fractionBits,
            value: radix
        )) { return $"one has raw {UFixedQ4816.One.Value}, not the radix to the fraction bit count"; }
        if (UFixedQ4816.MaxValue.Value != (BigInteger.Pow(
            exponent: totalBits,
            value: radix
        ) - BigInteger.One)) { return $"the maximum has raw {UFixedQ4816.MaxValue.Value}"; }
        if (UFixedQ4816.Epsilon.Value != 1UL) { return $"epsilon has raw {UFixedQ4816.Epsilon.Value}"; }
        if (UFixedQ4816.Zero.Value != 0UL) { return $"zero has raw {UFixedQ4816.Zero.Value}"; }
        if (UFixedQ4816.MinValue != UFixedQ4816.Zero) { return $"the minimum is raw {UFixedQ4816.MinValue.Value} rather than zero"; }
        if (UFixedQ4816.AdditiveIdentity != UFixedQ4816.Zero) { return "the additive identity is not zero"; }
        if (UFixedQ4816.MultiplicativeIdentity != UFixedQ4816.One) { return "the multiplicative identity is not one"; }
        if (default(UFixedQ4816) != UFixedQ4816.Zero) { return "the default value is not zero"; }

        foreach (var raw in UnsignedLadder) {
            if (UFixedQ4816.FromRawBits(value: raw).Value != raw) { return $"the ladder raw {raw} did not survive FromRawBits"; }
            if (new UFixedQ4816(Value: raw).Value != raw) { return $"the ladder raw {raw} did not survive the constructor"; }
            if (UFixedQ4816.FromRawBits(value: raw) != new UFixedQ4816(Value: raw)) { return $"the two construction routes disagree at the ladder raw {raw}"; }
        }

        foreach (var whole in ((ReadOnlySpan<ulong>)[0UL, 1UL, 2UL, ((1UL << 47) - 1UL), (1UL << 47), UnsignedTopIntegerUnits])) {
            if (UFixedQ4816.FromInteger(value: whole).Value != (new BigInteger(value: whole) << UFixedQ4816.FractionBitCount)) { return $"the whole number {whole} did not scale onto the grid"; }
        }

        foreach (var whole in ((ReadOnlySpan<ulong>)[(UnsignedTopIntegerUnits + 1UL), (UnsignedTopIntegerUnits + 2UL), ulong.MaxValue])) {
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = UFixedQ4816.FromInteger(value: whole),
                paramName: "value"
            )) { return $"the whole number {whole} was admitted past the integer range"; }
        }

        if (UFixedQ4816.One.CompareTo(obj: null) != 1) { return "a null comparand does not sort first"; }
        if (UFixedQ4816.One.CompareTo(obj: ((object)UFixedQ4816.One)) != 0) { return "the boxed comparison of one against itself is not zero"; }
        if (!Throws<ArgumentException>(
            action: () => _ = UFixedQ4816.One.CompareTo(obj: "not an unsigned fixed-point value"),
            paramName: "obj"
        )) { return "the boxed comparison accepted a foreign type"; }

        // A zero divisor at all five division entry points. None of the four unchecked ones carries an <exception> tag,
        // so the refusal is derived from the code and pinned here rather than assumed.
        var dividend = UFixedQ4816.FromRawBits(value: 0x1234_5678_9ABC_DEF0UL);

        if (!Throws<DivideByZeroException>(action: () => _ = (dividend / UFixedQ4816.Zero))) { return "the wrapping division answered a zero divisor"; }
        if (!Throws<DivideByZeroException>(action: () => _ = checked((dividend / UFixedQ4816.Zero)))) { return "the checked division answered a zero divisor"; }
        if (!Throws<DivideByZeroException>(action: () => _ = (dividend % UFixedQ4816.Zero))) { return "the remainder answered a zero divisor"; }
        if (!Throws<DivideByZeroException>(action: () => _ = UFixedQ4816.DivideUnchecked(
            x: dividend,
            y: UFixedQ4816.Zero
        ))) { return "the discarding unchecked division answered a zero divisor"; }
        if (!Throws<DivideByZeroException>(action: () => _ = UFixedQ4816.DivideUnchecked(
            x: dividend,
            y: UFixedQ4816.Zero,
            remainder: out _
        ))) { return "the reporting unchecked division answered a zero divisor"; }

        // The Ceiling and Round seam at the top of the range: the tie at the largest integer part rounds UP, because
        // 2⁴⁸ − 1 is odd, and takes the value out of the type.
        if (UFixedQ4816.Ceiling(value: UFixedQ4816.FromRawBits(value: UnsignedTopIntegerRaw)).Value != UnsignedTopIntegerRaw) { return "the ceiling moved the largest integral raw"; }
        if (!Throws<OverflowException>(action: () => _ = UFixedQ4816.Ceiling(value: UFixedQ4816.FromRawBits(value: (UnsignedTopIntegerRaw + 1UL))))) { return "the ceiling answered one raw above the largest integral one"; }
        if (UFixedQ4816.Round(value: UFixedQ4816.FromRawBits(value: ((UnsignedTopIntegerRaw + UnsignedHalfRaw) - 1UL))).Value != UnsignedTopIntegerRaw) { return "the rounding did not fall back to the largest integral raw below the top half"; }
        if (!Throws<OverflowException>(action: () => _ = UFixedQ4816.Round(value: UFixedQ4816.FromRawBits(value: (UnsignedTopIntegerRaw + UnsignedHalfRaw))))) { return "the rounding answered the top tie, whose odd integer part carries it out of the type"; }

        // Every checked operator at its named corner, and the one negation this carrier admits.
        if (checked(-UFixedQ4816.Zero) != UFixedQ4816.Zero) { return "the checked negation of zero is not zero"; }
        if (!Throws<OverflowException>(action: () => _ = checked((UFixedQ4816.MaxValue + UFixedQ4816.Epsilon)))) { return "the checked sum answered past the maximum"; }
        if (!Throws<OverflowException>(action: () => _ = checked((UFixedQ4816.Zero - UFixedQ4816.Epsilon)))) { return "the checked difference answered below zero"; }
        if (!Throws<OverflowException>(action: () => { var value = UFixedQ4816.MaxValue; _ = checked(++value); })) { return "the checked increment answered past the maximum"; }
        if (!Throws<OverflowException>(action: () => { var value = UFixedQ4816.Zero; _ = checked(--value); })) { return "the checked decrement answered below zero"; }
        if (!Throws<OverflowException>(action: () => _ = checked(-UFixedQ4816.Epsilon))) { return "the checked negation answered a non-zero value"; }
        if (!Throws<OverflowException>(action: () => _ = checked((UFixedQ4816.MaxValue * UFixedQ4816.FromInteger(value: 2UL))))) { return "the checked product answered past the maximum"; }
        if (!Throws<OverflowException>(action: () => _ = checked((UFixedQ4816.MaxValue / UFixedQ4816.Epsilon)))) { return "the checked quotient answered past the maximum"; }

        return null;
    }
    /// <summary>Proves both directions of the double seam against constant tables hand-derived from the IEEE-754
    /// binary64 layout and the <c>2⁻¹⁶</c> grid: the conversion inward saturates at both ends and rounds ties to even,
    /// the conversion outward is one round-to-nearest-even of the raw followed by an EXACT scale, the two compose to a
    /// round trip below <c>2⁵³</c>, and the upper saturation reaches MaxValue itself — which no double clamp could,
    /// since the largest double below <c>2⁶⁴</c> is 2048 raw units short of it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedDoubleSeam() {
        foreach (var (value, expected) in UnsignedFromDoubleLadder) {
            var converted = UFixedQ4816.FromDouble(value: value);

            if (converted.Value != expected) { return $"the double {value} converted to raw {converted.Value}, expected {expected}"; }
        }

        foreach (var (raw, expected) in UnsignedToDoubleLadder) {
            var converted = ((double)UFixedQ4816.FromRawBits(value: raw));

            if (BitConverter.DoubleToUInt64Bits(value: converted) != BitConverter.DoubleToUInt64Bits(value: expected)) { return $"the raw {raw} converted to {converted}, expected {expected}"; }

            if (raw < (1UL << 53)) {
                if (UFixedQ4816.FromDouble(value: converted) != UFixedQ4816.FromRawBits(value: raw)) { return $"the double round trip failed at raw {raw}"; }
            }
        }

        if (UFixedQ4816.FromDouble(value: double.PositiveInfinity) != UFixedQ4816.MaxValue) { return "the upper saturation did not reach the maximum"; }
        if (UFixedQ4816.FromDouble(value: double.NegativeInfinity) != UFixedQ4816.Zero) { return "the lower saturation did not reach zero"; }

        return null;
    }
    /// <summary>Proves the unsigned text seam at every swept raw: the rendering is the exact decimal expansion an
    /// arbitrary-width oracle derives by a different route, the span formatter fills an exact destination, an oversized
    /// one and neither a short one nor an unsupported specifier, a non-'.' decimal separator is spliced in place with
    /// the reported length adjusted for its own width, and all eight parse entry points — both surfaces — recover the
    /// original raw bit for bit.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedTextRoundTrip(long[] left, long[] right) {
        var raw = UnsignedRaw(raw: left[0]);
        var value = UFixedQ4816.FromRawBits(value: raw);
        var rendered = value.ToString();
        var reference = Oracles.ExactDyadicDecimal(
            numerator: new BigInteger(value: raw),
            shift: UFixedQ4816.FractionBitCount
        );
        Span<char> destination = stackalloc char[UnsignedFormattedCeiling];

        if (rendered != reference) { return $"the raw {raw} rendered as '{rendered}', expected '{reference}'"; }

        if (
            !value.TryFormat(
            destination: destination[..reference.Length],
            charsWritten: out var exact,
            format: default,
            provider: null
        ) ||
            (exact != reference.Length) ||
            !destination[..exact].SequenceEqual(other: reference)
        ) { return $"the raw {raw} did not format into an exactly sized span"; }

        if (
            !value.TryFormat(
            charsWritten: out var wide,
            destination: destination,
            format: "G",
            provider: null
        ) ||
            (wide != reference.Length) ||
            !destination[..wide].SequenceEqual(other: reference)
        ) { return $"the raw {raw} did not format into an oversized span"; }

        if (
            !value.TryFormat(
            charsWritten: out var lower,
            destination: destination,
            format: "g",
            provider: null
        ) ||
            (lower != reference.Length)
        ) { return $"the raw {raw} refused the lower-case general format"; }
        if (
            value.TryFormat(
            destination: destination[..(reference.Length - 1)],
            charsWritten: out var refused,
            format: default,
            provider: null
        ) ||
            (0 != refused)
        ) { return $"the raw {raw} formatted into a span one character short"; }
        if (!Throws<FormatException>(action: () => {
            Span<char> local = stackalloc char[UnsignedFormattedCeiling]; _ = value.TryFormat(
            charsWritten: out _,
            destination: local,
            format: "N2",
            provider: null
        );
        })) { return $"the raw {raw} accepted an unsupported format specifier"; }

        foreach (var (separator, provider) in UnsignedSeparators) {
            var localized = reference.Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: separator,
                oldValue: "."
            );

            if (
                !value.TryFormat(
                charsWritten: out var written,
                destination: destination,
                format: default,
                provider: provider
            ) ||
                (written != localized.Length) ||
                !destination[..written].SequenceEqual(other: localized)
            ) { return $"the raw {raw} did not localize with the separator '{separator}'"; }

            if (value.ToString(
                format: null,
                formatProvider: provider
            ) != localized) { return $"the raw {raw} did not localize through ToString with the separator '{separator}'"; }
        }

        return UnsignedParseAll(
            expected: raw,
            text: reference
        );
    }

    // All eight parse entry points on one text: the four throwing routes and the four trying ones, string and span,
    // over BOTH surfaces — the default one, which rejects out-of-range text before rounding, and the NumberStyles
    // one, which rounds first.
    private static string? UnsignedParseAll(string text, ulong expected) {
        if (UFixedQ4816.Parse(
            provider: null,
            s: text
        ).Value != expected) { return $"the string parse of '{text}' did not return {expected}"; }
        if (UFixedQ4816.Parse(
            s: text.AsSpan(),
            provider: null
        ).Value != expected) { return $"the span parse of '{text}' did not return {expected}"; }
        if (UFixedQ4816.Parse(
            s: text,
            style: UnsignedParseStyle,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"the styled string parse of '{text}' did not return {expected}"; }
        if (UFixedQ4816.Parse(
            s: text.AsSpan(),
            style: UnsignedParseStyle,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"the styled span parse of '{text}' did not return {expected}"; }
        if (
            !UFixedQ4816.TryParse(
            provider: null,
            result: out var fromString,
            s: text
        ) ||
            (fromString.Value != expected)
        ) { return $"the string try-parse of '{text}' did not return {expected}"; }
        if (
            !UFixedQ4816.TryParse(
            s: text.AsSpan(),
            provider: null,
            result: out var fromSpan
        ) ||
            (fromSpan.Value != expected)
        ) { return $"the span try-parse of '{text}' did not return {expected}"; }
        if (
            !UFixedQ4816.TryParse(
            s: text,
            style: UnsignedParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var styledString
        ) ||
            (styledString.Value != expected)
        ) { return $"the styled string try-parse of '{text}' did not return {expected}"; }
        if (
            !UFixedQ4816.TryParse(
            s: text.AsSpan(),
            style: UnsignedParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var styledSpan
        ) ||
            (styledSpan.Value != expected)
        ) { return $"the styled span try-parse of '{text}' did not return {expected}"; }

        return null;
    }
    // The exact rational a decimal literal names, quantized onto the 2⁻¹⁶ grid by ONE ties-to-even rounding of
    // numerator·2¹⁶ / 10^fractionDigits — a route that never forms the subject's seventeen-digit fraction prefix, its
    // reduced denominator 2·5¹⁷, or its sticky discarded-digit flag.
    private static BigInteger UnsignedQuantizedLiteral(string text) {
        var trimmed = text.Trim();
        var point = trimmed.IndexOf(value: '.');
        var digits = ((point < 0)
            ? trimmed
            : string.Concat(
                str0: trimmed.AsSpan(
                    length: point,
                    start: 0
                ),
                str1: trimmed.AsSpan(start: (point + 1))
            )
        );
        var fractionDigitCount = ((point < 0)
            ? 0
            : ((trimmed.Length - point) - 1)
        );

        return Oracles.RoundRationalTiesToEven(
            numerator: (BigInteger.Parse(
                value: digits,
                provider: CultureInfo.InvariantCulture
            ) << UFixedQ4816.FractionBitCount),
            denominator: BigInteger.Pow(
                value: new BigInteger(value: 10),
                exponent: fractionDigitCount
            )
        );
    }

    /// <summary>Proves the unsigned text contract on its own committed ladders: every accepted spelling — the two
    /// admitted signed spellings included — reaches the hand-derived raw through all eight entry points AND through
    /// an arbitrary-width quantizer, the default surface refuses every malformed, negative-magnitude and out-of-range
    /// spelling as a FORMAT failure, and the styles surface diverges from it exactly where the rejectExactOutOfRange
    /// flag says it must — answering the exact-maximum literal and reporting the rest as overflows or format failures
    /// apart.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedTextLadderAndRefusals() {
        foreach (var (text, expected) in UnsignedParseLadder) {
            if (UnsignedQuantizedLiteral(text: text) != expected) { return $"the oracle read '{text}' as {UnsignedQuantizedLiteral(text: text)}, expected {expected}"; }
            if (UnsignedParseAll(
                expected: expected,
                text: text
            ) is { } detail) { return detail; }
        }

        // The default surface folds every refusal into ONE verdict: a negative magnitude, an out-of-range value, and
        // unparseable text all raise a FormatException.
        foreach (var text in UnsignedDefaultRefusedTexts) {
            if (
                UFixedQ4816.TryParse(
                provider: null,
                result: out var refused,
                s: text
            ) ||
                (refused != default)
            ) { return $"the default surface accepted '{text}', or left raw {refused.Value} behind"; }
            if (!Throws<FormatException>(action: () => _ = UFixedQ4816.Parse(
                provider: null,
                s: text
            ))) { return $"the default string parse accepted '{text}'"; }
            if (!Throws<FormatException>(action: () => _ = UFixedQ4816.Parse(
                s: text.AsSpan(),
                provider: null
            ))) { return $"the default span parse accepted '{text}'"; }
        }

        // …and the styles surface ANSWERS several of them, which is the divergence the two surfaces genuinely have —
        // at the top of the range and across the whole bottom band.
        foreach (var (text, expected) in UnsignedStyledAnswers) {
            if (UFixedQ4816.Parse(
                s: text,
                style: UnsignedParseStyle,
                provider: CultureInfo.InvariantCulture
            ).Value != expected) { return $"the styled parse of '{text}' did not return {expected}"; }
            if (
                !UFixedQ4816.TryParse(
                s: text,
                style: UnsignedParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var parsed
            ) ||
                (parsed.Value != expected)
            ) { return $"the styled try-parse of '{text}' did not return {expected}"; }
        }

        // The same bottom-band rule under an exponent-admitting style: a negative magnitude far below half an ULP
        // rounds to zero FIRST on the styles surface and succeeds as Zero, rather than reporting the
        // OverflowException a nonzero rounded magnitude earns.
        if (UFixedQ4816.Parse(
            s: "-1e-30",
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture
        ) != UFixedQ4816.Zero) { return "the styled Float parse of '-1e-30' did not answer Zero"; }
        if (
            !UFixedQ4816.TryParse(
            s: "-1e-30",
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out var subHalfUlp
        ) ||
            (subHalfUlp != UFixedQ4816.Zero)
        ) { return "the styled Float try-parse of '-1e-30' did not answer Zero"; }

        foreach (var text in UnsignedStyledOverflowTexts) {
            if (
                UFixedQ4816.TryParse(
                s: text,
                style: UnsignedParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refused
            ) ||
                (refused != default)
            ) { return $"the styles surface accepted '{text}', or left raw {refused.Value} behind"; }
            if (!Throws<OverflowException>(action: () => _ = UFixedQ4816.Parse(
                s: text,
                style: UnsignedParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"the styled parse of '{text}' did not report an overflow"; }
        }

        foreach (var text in UnsignedStyledFormatTexts) {
            if (
                UFixedQ4816.TryParse(
                s: text,
                style: UnsignedParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refused
            ) ||
                (refused != default)
            ) { return $"the styles surface accepted '{text}', or left raw {refused.Value} behind"; }
            if (!Throws<FormatException>(action: () => _ = UFixedQ4816.Parse(
                s: text,
                style: UnsignedParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"the styled parse of '{text}' did not report a format failure"; }
        }

        if (
            UFixedQ4816.TryParse(
            provider: null,
            result: out var fromNull,
            s: ((string?)null)
        ) ||
            (fromNull != default)
        ) { return "the default surface accepted a null string"; }
        if (
            UFixedQ4816.TryParse(
            s: ((string?)null),
            style: UnsignedParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var fromStyledNull
        ) ||
            (fromStyledNull != default)
        ) { return "the styles surface accepted a null string"; }
        if (!Throws<ArgumentNullException>(
            action: () => _ = UFixedQ4816.Parse(
                provider: null,
                s: ((string)null!)
            ),
            paramName: "s"
        )) { return "the default string parse accepted a null string"; }
        if (!Throws<ArgumentNullException>(
            action: () => _ = UFixedQ4816.Parse(
                s: ((string)null!),
                style: UnsignedParseStyle,
                provider: CultureInfo.InvariantCulture
            ),
            paramName: "s"
        )) { return "the styled string parse accepted a null string"; }

        return null;
    }
    /// <summary>Proves the five integer maps and the three integrality predicates over EVERY one of the 2¹⁶ fraction
    /// words, crossed with seven integer parts — which is the entire branch space of all eight members, since each is
    /// decided by the fraction word and the parity of the integer part — and locates the two refusals exactly: at the
    /// top integer part the ceiling refuses at its 65535 non-zero fractions and the rounding at its 32768 fractions
    /// from the half upward, while no other integer part in the sweep refuses anything.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedFractionSweep() {
        foreach (var integerPart in UnsignedSweepIntegerParts) {
            var refusedCeilings = 0;
            var refusedRoundings = 0;

            for (var fraction = 0UL; (fraction <= UnsignedFractionMask); ++fraction) {
                var raw = (integerPart << UFixedQ4816.FractionBitCount) | fraction;

                if (UnsignedIntegerDecompositionAt(
                    ceilingRefused: out var ceilingRefused,
                    raw: raw,
                    roundingRefused: out var roundingRefused
                ) is { } decomposition) { return decomposition; }
                if (UnsignedNumberPredicatesAt(raw: raw) is { } predicates) { return predicates; }

                if (ceilingRefused) { ++refusedCeilings; }
                if (roundingRefused) { ++refusedRoundings; }
            }

            // At the top integer part 2⁴⁸ − 1 the ceiling's checked add overflows for every non-zero fraction, and the
            // rounding's correction fires from the half upward — the tie included, because that integer part is ODD.
            var expectedCeilingRefusals = ((UnsignedTopIntegerUnits == integerPart)
                ? 65535
                : 0
            );
            var expectedRoundingRefusals = ((UnsignedTopIntegerUnits == integerPart)
                ? 32768
                : 0
            );

            if (refusedCeilings != expectedCeilingRefusals) { return $"the ceiling refused {refusedCeilings} times at integer part {integerPart}, expected {expectedCeilingRefusals}"; }
            if (refusedRoundings != expectedRoundingRefusals) { return $"the rounding refused {refusedRoundings} times at integer part {integerPart}, expected {expectedRoundingRefusals}"; }
        }

        return null;
    }

    // The construction ladder: both extremes, the fraction seams either side of the half and of the sixteen-bit
    // boundary, the top of the integer range, the bit that separates unsigned order from signed, and the three raws at
    // the Ceiling and Round seam.
    private static readonly ulong[] UnsignedLadder = [
        0UL, 1UL, 2UL,
        ((1UL << 15) - 1UL), (1UL << 15), ((1UL << 15) + 1UL),
        ((1UL << 16) - 1UL), (1UL << 16), ((1UL << 16) + 1UL),
        ((1UL << 47) - 1UL), (1UL << 47),
        ((1UL << 63) - 1UL), (1UL << 63),
        UnsignedTopIntegerRaw, (UnsignedTopIntegerRaw + 0x7FFFUL), (UnsignedTopIntegerRaw + 0x8000UL),
        (UnsignedTopIntegerRaw + 1UL), ulong.MaxValue,
    ];
    // The rungs the integer-decomposition claim visits whatever the domain drew: both extremes, one whole unit, the
    // largest integral raw, the first raw whose ceiling leaves the type, the last raw whose rounding does not, and the
    // top tie whose rounding does.
    private static readonly ulong[] UnsignedIntegerBandRungs = [
        0UL, UnsignedOneRaw, UnsignedTopIntegerRaw, (UnsignedTopIntegerRaw + 1UL),
        ((UnsignedTopIntegerRaw + UnsignedHalfRaw) - 1UL), (UnsignedTopIntegerRaw + UnsignedHalfRaw), ulong.MaxValue,
    ];
    // The seven integer parts the exhaustive fraction sweep crosses: zero, both parities at the bottom, both at the
    // middle of the integer range, and both at the top — where 2⁴⁸ − 1 is the band Ceiling and Round can leave the type
    // from, and its ODD integer part is what carries the top tie upward.
    private static readonly ulong[] UnsignedSweepIntegerParts = [
        0UL, 1UL, 2UL, ((1UL << 47) - 1UL), (1UL << 47), (UnsignedTopIntegerUnits - 1UL), UnsignedTopIntegerUnits,
    ];
    // Two non-invariant decimal separators, one single-character and one wider, so the reported length has to adjust
    // for the separator's own width rather than merely for its spelling. Both providers are built here, so no law reads
    // an ambient culture.
    private static readonly (string Separator, NumberFormatInfo Provider)[] UnsignedSeparators = [
        (",", new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", }),
        ("<>", new NumberFormatInfo { NumberDecimalSeparator = "<>", NumberGroupSeparator = ".", }),
    ];
    // The double seam INWARD, each expectation derived from the IEEE-754 binary64 layout and the 2^-16 grid rather than
    // from the kernel: the two saturations, not-a-number, both infinities, the negatives (including the one whose
    // scaled value rounds to negative zero and still clamps), the smallest positive double, the exactly-representable
    // interior points, the three half-ULP ties whose ties-to-even resolution is the house discipline (0.5 down to 0,
    // 1.5 up to 2, 2.5 down to 2), the exact saturation boundary 2^48 − 2^-5 whose scaled value IS the declared
    // maximum, its successor 2^48 which is not, and the one input that is not exactly representable: 0.1 scaled by the
    // power of two 2^16 is exact in double, and its exact value 6553.60000000000036379788070917129516 rounds up.
    private static readonly (double Value, ulong Expected)[] UnsignedFromDoubleLadder = [
        (double.NaN, 0UL),
        (double.NegativeInfinity, 0UL),
        (double.PositiveInfinity, ulong.MaxValue),
        (-1d, 0UL),
        (-0d, 0UL),
        (0d, 0UL),
        (-0.000001d, 0UL),
        (double.Epsilon, 0UL),
        ((1d / 131072d), 0UL),
        ((1d / 65536d), 1UL),
        ((3d / 131072d), 2UL),
        ((5d / 131072d), 2UL),
        (0.5d, 32768UL),
        (1d, 65536UL),
        (1.5d, 98304UL),
        (0.1d, 6554UL),
        (140737488355328d, (1UL << 63)),
        (281474976710655.96875d, 18446744073709549568UL),
        (281474976710656d, ulong.MaxValue),
    ];
    // The double seam OUTWARD. The conversion is round-to-nearest-even of the ulong followed by an EXACT scale by
    // 2^-16, so every raw below 2^53 converts exactly and the three raws at and above it exhibit the ulong-to-double
    // tie rule: 2^53 + 1 is a tie resolved DOWN to 2^53, 2^53 + 3 a tie resolved UP to 2^53 + 4, and 2^64 − 1 rounds up
    // to 2^64, where the spacing is 2048. Every expectation is an exact power-of-two-scaled constant.
    private static readonly (ulong Raw, double Expected)[] UnsignedToDoubleLadder = [
        (0UL, 0d),
        (1UL, (1d / 65536d)),
        (32768UL, 0.5d),
        (65535UL, (65535d / 65536d)),
        (65536UL, 1d),
        (98304UL, 1.5d),
        (((1UL << 53) - 1UL), (9007199254740991d / 65536d)),
        (((1UL << 53) + 1UL), 137438953472d),
        (((1UL << 53) + 3UL), (9007199254740996d / 65536d)),
        ((1UL << 63), 140737488355328d),
        (ulong.MaxValue, 281474976710656d),
    ];
    // The accepted parse ladder. Every raw here is round(decimal value · 2¹⁶) under the house ties-to-even rule; the
    // three tie rows are the exact half-ULP 2⁻¹⁷ (down to 0, the even neighbour), 3·2⁻¹⁷ (up to 2) and 5·2⁻¹⁷ (down to
    // 2), and the fourth is that same half-ULP carrying one non-zero digit past the seventeen-digit fraction prefix,
    // which the subject's sticky rule breaks UPWARD to 1. The MaxValue row is its exact decimal expansion,
    // 281474976710655 + 65535/2¹⁶; the two signed rows are the spellings the unsigned grammar admits — an explicit
    // plus, and a negative zero whose all-zero significand short-circuits before any range check sees a magnitude.
    private static readonly (string Text, ulong Expected)[] UnsignedParseLadder = [
        ("0", 0UL),
        ("0.0", 0UL),
        ("1", 65536UL),
        ("0.5", 32768UL),
        ("1.5", 98304UL),
        ("  1.5  ", 98304UL),
        ("0.0000152587890625", 1UL),
        ("0.00000762939453125", 0UL),
        ("0.00002288818359375", 2UL),
        ("0.00003814697265625", 2UL),
        ("0.000007629394531251", 1UL),
        ("281474976710655.9999847412109375", ulong.MaxValue),
        ("+1", 65536UL),
        ("-0", 0UL),
    ];
    // Every spelling the DEFAULT surface refuses, and it refuses all of them as format failures: the negative
    // magnitudes the unsigned range cannot hold BEFORE rounding — including the two in the bottom band (−2⁻¹⁷, 0)
    // whose magnitude would round to zero, since this surface's exact-range check runs at both ends — the two
    // out-of-range values, the literal one digit past the exact maximum, and the four unparseable spellings.
    private static readonly string[] UnsignedDefaultRefusedTexts = [
        "-1", "-0.00001", "-0.000007", "-0.00000762939453125",
        "281474976710656", "281474976710655.99998474121093750001",
        "", "abc", "1.5.5", "79228162514264337593543950336",
    ];
    // The literals the styles surface ANSWERS where the default one refused, one from each end of the range: the
    // literal whose prefix numerator equals the maximum exactly — refused by the default surface only because
    // rejectExactOutOfRange tests that prefix against the maximum BEFORE rounding, where the rounding pass finds a
    // zero remainder and lands on MaxValue — and the two bottom-band negatives whose magnitude rounds to zero (the
    // second is the exact half-ULP −2⁻¹⁷, which ties to even), answered as Zero with no OverflowException at all.
    private static readonly (string Text, ulong Expected)[] UnsignedStyledAnswers = [
        ("281474976710655.99998474121093750001", ulong.MaxValue),
        ("-0.000007", 0UL),
        ("-0.00000762939453125", 0UL),
    ];
    // Out of range for the styles surface: a negative significand against a zero negative maximum, and two values above
    // MaxValue — the last of which is above decimal.MaxValue too, so it surfaces from the RE-ENTERED platform parser
    // rather than from the exact pass, which is the distinction the styled Parse's own comment claims.
    private static readonly string[] UnsignedStyledOverflowTexts = [
        "-1", "-0.00001", "281474976710656", "79228162514264337593543950336",
    ];
    // Unparseable for the styles surface, which reports these apart from the overflows above.
    private static readonly string[] UnsignedStyledFormatTexts = ["", "abc", "1.5.5"];

    // ---- carrier scalar (UnitInterval32), the closed unit interval on the sampler's grid ----

    private const ulong ClosedUnitOneRaw = (1UL << UnitInterval32.FractionBitCount);
    private const ulong ClosedUnitFoldMask = ((ClosedUnitOneRaw << 1) - 1UL);
    private const long ClosedUnitNarrowOneRaw = (1L << FixedQ4816.FractionBitCount);

    /// <summary>Maps a sampled signed raw onto a legal closed-unit raw: the low thirty-three bits, saturated at one.
    /// The sampled space is sixty-four bits wide and the carrier's is thirty-three, so an unbiased fold would visit the
    /// upper endpoint once in <c>2³²</c> draws; saturating puts HALF the draws on it while leaving the whole interval,
    /// both neighbourhoods, and the exact half reachable. Subject and oracle apply the identical map, so every sampled
    /// operand reaches a defined comparison rather than being skipped asymmetrically.</summary>
    private static ulong ClosedUnitRaw(long raw) =>
        Math.Min(
            val1: unchecked((ulong)raw) & ClosedUnitFoldMask,
            val2: ClosedUnitOneRaw
        );
    private static UnitInterval32 ClosedUnit(long raw) =>
        UnitInterval32.Create(value: ClosedUnitRaw(raw: raw));

    /// <summary>The subject <see cref="UnitInterval32"/> multiply, sampled raw in and raw out.</summary>
    public static long ClosedUnitMultiply(long a, long b) =>
        ((long)UnitInterval32.Multiply(
            x: ClosedUnit(raw: a),
            y: ClosedUnit(raw: b)
        ).Value);
    /// <summary>The oracle for the <see cref="UnitInterval32"/> multiply — one ties-to-even rounding of the exact
    /// product at the <c>2⁻³²</c> grid.</summary>
    public static long ClosedUnitMultiplyOracle(long a, long b) =>
        ((long)Oracles.ClosedUnitProduct(
            x: ClosedUnitRaw(raw: a),
            y: ClosedUnitRaw(raw: b)
        ));
    /// <summary>Proves the two absorbing elements act exactly at every swept raw, both endpoints included: one is a
    /// two-sided multiplicative identity and zero a two-sided annihilator, with no rounding anywhere. The upper
    /// endpoint is the whole reason the type spends a thirty-third bit, so the corner where BOTH operands are one — the
    /// only product that leaves sixty-four bits — is checked on every case as well.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ClosedUnitUnitAndZeroExact(long[] left, long[] right) {
        foreach (var raw in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var value = ClosedUnit(raw: raw);

            if (UnitInterval32.Multiply(
                x: UnitInterval32.One,
                y: value
            ) != value) { return $"one is not a left identity at raw {value.Value}"; }
            if (UnitInterval32.Multiply(
                x: value,
                y: UnitInterval32.One
            ) != value) { return $"one is not a right identity at raw {value.Value}"; }
            if (UnitInterval32.Multiply(
                x: UnitInterval32.Zero,
                y: value
            ) != UnitInterval32.Zero) { return $"zero is not a left annihilator at raw {value.Value}"; }
            if (UnitInterval32.Multiply(
                x: value,
                y: UnitInterval32.Zero
            ) != UnitInterval32.Zero) { return $"zero is not a right annihilator at raw {value.Value}"; }
        }

        if (UnitInterval32.Multiply(
            x: UnitInterval32.One,
            y: UnitInterval32.One
        ) != UnitInterval32.One) { return "one times one is not one"; }

        return null;
    }
    /// <summary>Proves the bounded operations are EXACT at every swept pair — the saturating sum, the excess of the sum
    /// over one, the complement, and the two order selections all agree with arbitrary-width arithmetic — and that the
    /// order the comparisons report is the order of the raws. The complement's De Morgan pair against the two order
    /// selections is pinned here too, at the carrier, so a material built on it inherits the fact rather than
    /// re-deriving it.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ClosedUnitBoundedOpsExact(long[] left, long[] right) {
        var rawA = ClosedUnitRaw(raw: left[0]);
        var rawB = ClosedUnitRaw(raw: right[0]);
        var a = UnitInterval32.Create(value: rawA);
        var b = UnitInterval32.Create(value: rawB);
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var exactOne = new BigInteger(value: ClosedUnitOneRaw);
        var exactSum = (exactA + exactB);

        if (UnitInterval32.AddSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: exactSum,
            right: exactOne
        )) { return $"the saturating sum of {rawA} and {rawB} is wrong"; }
        if (UnitInterval32.SumExcess(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: (exactSum - exactOne),
            right: BigInteger.Zero
        )) { return $"the sum excess of {rawA} and {rawB} is wrong"; }
        if (UnitInterval32.Max(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: exactA,
            right: exactB
        )) { return $"the maximum of {rawA} and {rawB} is wrong"; }
        if (UnitInterval32.Min(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: exactA,
            right: exactB
        )) { return $"the minimum of {rawA} and {rawB} is wrong"; }
        if (UnitInterval32.Complement(value: a).Value != (exactOne - exactA)) { return $"the complement of {rawA} is wrong"; }
        if (UnitInterval32.Complement(value: UnitInterval32.Complement(value: a)) != a) { return $"the complement is not an involution at {rawA}"; }

        // The saturating sum's neutral element and the excess's, which the two bounded operations must have exactly.
        if (UnitInterval32.AddSaturating(
            x: a,
            y: UnitInterval32.Zero
        ) != a) { return $"zero is not neutral for the saturating sum at {rawA}"; }
        if (UnitInterval32.SumExcess(
            x: a,
            y: UnitInterval32.One
        ) != a) { return $"one is not neutral for the sum excess at {rawA}"; }

        var complementOfMaximum = UnitInterval32.Complement(value: UnitInterval32.Max(
            x: a,
            y: b
        ));
        var minimumOfComplements = UnitInterval32.Min(
            x: UnitInterval32.Complement(value: a),
            y: UnitInterval32.Complement(value: b)
        );
        var complementOfMinimum = UnitInterval32.Complement(value: UnitInterval32.Min(
            x: a,
            y: b
        ));
        var maximumOfComplements = UnitInterval32.Max(
            x: UnitInterval32.Complement(value: a),
            y: UnitInterval32.Complement(value: b)
        );

        if (complementOfMaximum != minimumOfComplements) { return $"De Morgan fails on the maximum at ({rawA}, {rawB})"; }
        if (complementOfMinimum != maximumOfComplements) { return $"De Morgan fails on the minimum at ({rawA}, {rawB})"; }

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
    /// <summary>Proves the kinship contract at every swept draw: <see cref="UnitFraction32"/> embeds EXACTLY and narrows
    /// back exactly below one while REFUSING at one, and the <see cref="FixedQ4816"/> seam clamps on the way in and
    /// carries exactly one ties-to-even rounding on the way out. A value already on the coarse sixteen-bit grid makes
    /// that seam a round trip, which is where the narrowing's non-injectivity is separated from an error.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ClosedUnitKinshipExact(long[] left, long[] right) {
        // The fold saturates, so it alone never sweeps the half-open grid uniformly; the sampled low word does.
        var fractionRaw = unchecked((uint)left[0]);
        var embedded = UnitInterval32.FromUnitFraction32(value: UnitFraction32.FromRawBits(value: fractionRaw));

        if (embedded.Value != fractionRaw) { return $"the embedding of the fraction raw {fractionRaw} moved it to {embedded.Value}"; }
        if (
            !embedded.TryToUnitFraction32(result: out var narrowed) ||
            (narrowed.Value != fractionRaw)
        ) { return $"the embedding of {fractionRaw} did not narrow back"; }

        var rawA = ClosedUnitRaw(raw: left[0]);
        var a = UnitInterval32.Create(value: rawA);
        var narrowable = a.TryToUnitFraction32(result: out var fromA);

        if (narrowable != (rawA != ClosedUnitOneRaw)) { return $"the narrowing of raw {rawA} reported {narrowable}"; }
        if (
            narrowable &&
            (fromA.Value != rawA)
        ) { return $"the narrowing of raw {rawA} produced {fromA.Value}"; }
        if (
            !narrowable &&
            (fromA != default)
        ) { return $"the refused narrowing of raw {rawA} produced {fromA.Value}"; }
        if (a.ToFixedQ4816().Value != Oracles.ClosedUnitNarrow(value: rawA)) { return $"the Q48.16 narrowing of raw {rawA} is wrong"; }

        // The Q48.16 seam inward: a full-range signed raw, clamped to the interval and widened exactly.
        var wideRaw = right[0];
        var clamped = UnitInterval32.FromFixedQ4816(value: FixedQ4816.FromRawBits(value: wideRaw));
        var expected = ((wideRaw <= 0L)
            ? BigInteger.Zero
            : ((wideRaw >= ClosedUnitNarrowOneRaw)
                ? new BigInteger(value: ClosedUnitOneRaw)
                : (new BigInteger(value: wideRaw) << FixedQ4816.FractionBitCount)
        ));

        if (clamped.Value != expected) { return $"the Q48.16 raw {wideRaw} converted to {clamped.Value}, expected {expected}"; }

        // On the coarse grid the seam is a round trip in both directions, so the narrowing's non-injectivity above is a
        // documented loss rather than an error.
        var coarse = UnitInterval32.Create(value: ((rawA >> FixedQ4816.FractionBitCount) << FixedQ4816.FractionBitCount));

        if (UnitInterval32.FromFixedQ4816(value: coarse.ToFixedQ4816()) != coarse) { return $"the coarse-grid round trip failed at raw {coarse.Value}"; }

        return null;
    }
    /// <summary>Proves the closed unit interval's construction contract on its own raw ladder: the invariant admits
    /// exactly <c>[0, 2³²]</c> and refuses everything above it by both routes, the boxed comparison refuses a foreign
    /// type, the double seam saturates at both endpoints and rounds ties to even, and the rendering is the exact
    /// decimal expansion — including the point one, which the half-open fraction types cannot even name.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ClosedUnitConstructionAndRefusals() {
        // The declared grid and the upper endpoint have to be the same fact; the rendering check below reads the same
        // constant as its reference scale, so a wrong grid fails there too.
        if (UnitInterval32.One.Value != (1UL << UnitInterval32.FractionBitCount)) { return $"one has raw {UnitInterval32.One.Value}, not two to the {UnitInterval32.FractionBitCount}"; }
        if (UnitInterval32.Zero.Value != 0UL) { return $"zero has raw {UnitInterval32.Zero.Value}"; }
        if (default(UnitInterval32) != UnitInterval32.Zero) { return "the default value is not zero"; }

        // The ladder: both endpoints, both neighbourhoods, the exact half, and the sixteen-bit seam either side.
        foreach (var raw in ClosedUnitLadder) {
            if (
                !UnitInterval32.TryCreate(
                result: out var created,
                value: raw
            ) ||
                (created.Value != raw)
            ) { return $"the ladder raw {raw} was refused"; }
            if (UnitInterval32.Create(value: raw).Value != raw) { return $"the throwing construction moved the ladder raw {raw}"; }

            var rendered = created.ToString();
            var reference = Oracles.ExactDyadicDecimal(
                numerator: new BigInteger(value: raw),
                shift: UnitInterval32.FractionBitCount
            );

            if (rendered != reference) { return $"the ladder raw {raw} rendered as '{rendered}', expected '{reference}'"; }
        }

        foreach (var raw in ((ReadOnlySpan<ulong>)[(ClosedUnitOneRaw + 1UL), (ClosedUnitOneRaw << 1), ulong.MaxValue])) {
            if (UnitInterval32.TryCreate(
                result: out var refused,
                value: raw
            )) { return $"the out-of-range raw {raw} was accepted"; }
            if (refused != UnitInterval32.Zero) { return $"the refused raw {raw} left {refused.Value} behind"; }

            try {
                _ = UnitInterval32.Create(value: raw);

                return $"the throwing construction accepted the out-of-range raw {raw}";
            } catch (ArgumentOutOfRangeException exception) {
                if (exception.ParamName != "value") { return $"the refusal of raw {raw} named '{exception.ParamName}'"; }
            }
        }

        if (UnitInterval32.One.CompareTo(obj: null) != 1) { return "a null comparand does not sort first"; }
        if (UnitInterval32.One.CompareTo(obj: ((object)UnitInterval32.One)) != 0) { return "the boxed comparison of one against itself is not zero"; }

        try {
            _ = UnitInterval32.One.CompareTo(obj: "not a closed unit value");

            return "the boxed comparison accepted a foreign type";
        } catch (ArgumentException exception) {
            if (exception.ParamName != "obj") { return $"the boxed-comparison refusal named '{exception.ParamName}'"; }
        }

        foreach (var (value, expected) in ClosedUnitDoubleLadder) {
            var converted = UnitInterval32.FromDouble(value: value);

            if (converted.Value != expected) { return $"the double {value} converted to raw {converted.Value}, expected {expected}"; }
        }

        return null;
    }

    // The construction ladder: zero and one, their neighbourhoods, the exact half either side, and the sixteen-bit seam
    // where the FixedQ4816 narrowing splits.
    private static readonly ulong[] ClosedUnitLadder = [
        0UL, 1UL, 2UL, 3UL,
        ((1UL << 15) - 1UL), (1UL << 15), ((1UL << 15) + 1UL),
        ((1UL << 16) - 1UL), (1UL << 16), ((1UL << 16) + 1UL),
        ((1UL << 31) - 1UL), (1UL << 31), ((1UL << 31) + 1UL),
        (ClosedUnitOneRaw - 2UL), (ClosedUnitOneRaw - 1UL), ClosedUnitOneRaw,
    ];
    // The double seam, each expectation derived from the definition rather than from the kernel: the two saturations,
    // not-a-number, both infinities, the exactly-representable interior points, and the three half-ULP ties whose
    // ties-to-even resolution is the house rounding discipline (0.5 down to 0, 1.5 up to 2, 2.5 down to 2). The last row
    // is the one input that is NOT exactly representable: 0.1 scaled by the power of two 2^32 is exact in double, and
    // its exact value 429496729.6 rounds up.
    private static readonly (double Value, ulong Expected)[] ClosedUnitDoubleLadder = [
        (double.NaN, 0UL),
        (double.NegativeInfinity, 0UL),
        (double.PositiveInfinity, ClosedUnitOneRaw),
        (-1d, 0UL),
        (-0d, 0UL),
        (0d, 0UL),
        (1d, ClosedUnitOneRaw),
        (2d, ClosedUnitOneRaw),
        (0.5d, (1UL << 31)),
        (0.25d, (1UL << 30)),
        ((1d - (1d / 4294967296d)), (ClosedUnitOneRaw - 1UL)),
        ((1d / 4294967296d), 1UL),
        ((1d / 8589934592d), 0UL),
        ((3d / 8589934592d), 2UL),
        ((5d / 8589934592d), 2UL),
        (0.1d, 429496730UL),
    ];

}
