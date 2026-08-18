using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>A reference all-pairs walk over a graph whose arc weights are closed-unit raws — the shape the three
/// unit-interval oracles share, so the subject binding is written once and named per material.</summary>
/// <param name="weights">The weight matrix, row-major; a zero raw marks an absent arc.</param>
/// <param name="order">The number of vertices.</param>
/// <param name="result">The per-pair answers, row-major.</param>
internal delegate void RouteOracle(ReadOnlySpan<ulong> weights, int order, Span<ulong> result);

/// <summary>
/// Binds the abstract law delegates to concrete Puck.Maths kernels and to the shared-nothing <see cref="Oracles"/>.
/// Every member a <see cref="LawRegistry"/> case declares is exercised by that case's subject closure, and every
/// algebra operation a closure exercises is declared — an algebra closure builds its descriptor with <c>Create</c>, so
/// its case declares that too. The one deliberate exception is the plumbing every closure here shares: constructing
/// values from raw bits and reading the components back is not enumerated per case. Oracle closures route to
/// <see cref="Oracles"/> only.
/// </summary>
internal static partial class Subjects {
    private static FixedQ4816 Raw(long value) =>
        FixedQ4816.FromRawBits(value: value);

    // ---- carrier scalar (FixedQ4816) ----

    /// <summary>The subject <see cref="FixedQ4816"/> multiply.</summary>
    public static long FixedMultiply(long a, long b) =>
        (Raw(value: a) * Raw(value: b)).Value;
    /// <summary>The subject <see cref="FixedQ4816"/> add.</summary>
    public static long FixedAdd(long a, long b) =>
        (Raw(value: a) + Raw(value: b)).Value;
    /// <summary>The dyadic oracle for <see cref="FixedQ4816"/> multiply — one Q16 rounding, ties to even.</summary>
    public static long FixedMultiplyOracle(long a, long b) =>
        Oracles.RoundDyadic(
            exact: (((System.Numerics.BigInteger)a) * b),
            shift: 16
        );
    /// <summary>The oracle for <see cref="FixedQ4816"/> add — exact, wrapped to the carrier.</summary>
    public static long FixedAddOracle(long a, long b) =>
        Oracles.WrapToRaw(value: (((System.Numerics.BigInteger)a) + b));

    // A zero divisor has no quotient. Epsilon is the substitute rather than One, because dividing by the smallest
    // representable value is the operand that MAXIMIZES the quotient and so drives the wrapping path the substitution
    // would otherwise cost the sweep. The identical substitution runs on both sides, so every sampled pair reaches a
    // defined comparison rather than being skipped asymmetrically.
    private static long NonZeroDivisor(long b) =>
        ((0L == b)
            ? 1L
            : b
        );

    /// <summary>The subject <see cref="FixedQ4816"/> divide.</summary>
    public static long FixedDivide(long a, long b) =>
        (Raw(value: a) / Raw(value: NonZeroDivisor(b: b))).Value;
    /// <summary>The dyadic oracle for <see cref="FixedQ4816"/> divide — one Q16 rounding of the exact ratio, ties to
    /// even.</summary>
    public static long FixedDivideOracle(long a, long b) =>
        Oracles.RoundDyadicRatio(
            numerator: new BigInteger(value: a),
            denominator: new BigInteger(value: NonZeroDivisor(b: b)),
            shift: FixedQ4816.FractionBitCount
        );
    /// <summary>Proves the signed Q48.16 grid on its own ladder: the declared constants are one consistent fact, the raw
    /// survives every construction route, the whole-number seam admits exactly the representable integers and refuses
    /// the first value beyond on each side, and the double seam saturates at both extremes and rounds ties to
    /// even.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedGridAndConstruction() =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.GridAndConstruction(
            fractionBitCount: 16,
            rawLadder: FixedRawLadder,
            integerLadder: FixedIntegerLadder,
            refusedIntegers: [(1L << 47), -((1L << 47) + 1L), long.MaxValue, long.MinValue],
            doubleLadder: FixedDoubleLadder,
            // The outward seam is FixedDoubleProjectionExact's statement on this carrier; it sweeps operands and pins
            // the round trip and the oddness besides, so a ladder sweep here would add nothing.
            includeDoubleProjection: false
        );

    // The construction ladder: both carrier extremes and their neighbourhoods, zero and both units, the fraction and
    // integer seam either side, and the narrow/wide product boundary at 2^31.
    private static readonly long[] FixedRawLadder = [
        long.MinValue, (long.MinValue + 1L), -(1L << 47), -65537L, -65536L, -65535L, -32768L, -1L,
        0L, 1L, 32768L, 65535L, 65536L, 65537L, (1L << 31), ((1L << 47) - 1L), (long.MaxValue - 1L), long.MaxValue,
    ];
    // The whole-number seam: both admissible extremes and the ordinary interior.
    private static readonly long[] FixedIntegerLadder = [
        -140737488355328L, -140737488355327L, -65536L, -2L, -1L, 0L, 1L, 2L, 65536L, 140737488355326L, 140737488355327L,
    ];
    // The double seam, each expectation derived from the IEEE-754 layout and the 2⁻¹⁶ grid rather than from the kernel:
    // not-a-number and both infinities, the two saturations, the exactly-representable interior points, the four
    // half-ULP ties whose ties-to-even resolution is the house rounding discipline (±0.5 → 0, ±1.5 → ±2, ±2.5 → ±2 raw),
    // the 2⁴⁷ seam — the largest binary64 below 2⁶³ is a whole raw ULP short of MaxValue, which is why the kernel
    // saturates explicitly rather than casting a clamp — and 0.1, which is not exactly representable.
    private static readonly (double Value, long Expected)[] FixedDoubleLadder = [
        (double.NaN, 0L),
        (double.NegativeInfinity, long.MinValue),
        (double.PositiveInfinity, long.MaxValue),
        (0d, 0L), (-0d, 0L), (1d, 65536L), (-1d, -65536L), (0.5d, 32768L), (-0.5d, -32768L),
        ((1d / 65536d), 1L), (((-1d) / 65536d), -1L),
        ((1d / 131072d), 0L), (((-1d) / 131072d), 0L),
        ((3d / 131072d), 2L), (((-3d) / 131072d), -2L),
        ((5d / 131072d), 2L), (((-5d) / 131072d), -2L),
        (140737488355328d, long.MaxValue),
        (-140737488355328d, long.MinValue),
        (1e300d, long.MaxValue), (-1e300d, long.MinValue),
        (0.1d, 6554L),
    ];

    /// <summary>Proves the OUTWARD double seam at every swept raw: the explicit conversion is one
    /// round-to-nearest-ties-to-even of the signed sixty-four-bit raw followed by an EXACT scale by <c>2⁻¹⁶</c>, read as
    /// an exact bit pattern against an oracle that assembles the IEEE-754 encoding from the format in
    /// <see cref="BigInteger"/>. The projection is odd, it composes with <see cref="FixedQ4816.FromDouble"/> into a round
    /// trip below <c>2⁵³</c>, and a hand-derived ladder pins the two ties and both saturations.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedDoubleProjectionExact(long[] left, long[] right) {
        static string? Check(long raw) {
            var projected = ((double)Raw(value: raw));
            var bits = BitConverter.DoubleToUInt64Bits(value: projected);
            var expected = Oracles.NearestBinary64Bits(
                value: new BigInteger(value: raw),
                shift: FixedQ4816.FractionBitCount
            );

            if (bits != expected) { return $"the raw {raw} projected to {bits:X16}, expected {expected:X16}"; }

            // Below 2⁵³ the projection is EXACT, so the inward conversion recovers the raw bit for bit. That boundary is
            // what separates the documented precision loss at large magnitudes from an error.
            if (
                (raw > -(1L << 53)) &&
                (raw < (1L << 53)) &&
                (FixedQ4816.FromDouble(value: projected).Value != raw)
            ) {
                return $"the double round trip failed at raw {raw}";
            }

            // The map is ODD wherever the negation names another raw, which pins the sign bit independently of the
            // magnitude. Zero has one representation on this carrier and two in binary64, and MinValue's negation is its
            // own fixed point, so neither has a mirror to compare.
            if (
                (0L != raw) &&
                (long.MinValue != raw) &&
                (BitConverter.DoubleToUInt64Bits(value: ((double)Raw(value: -raw))) != (bits ^ (1UL << 63)))
            ) {
                return $"the projection is not odd at raw {raw}";
            }

            return null;
        }

        if (Check(raw: left[0]) is { } first) { return first; }
        if (Check(raw: right[0]) is { } second) { return second; }

        foreach (var (raw, expected) in FixedToDoubleLadder) {
            var projected = ((double)Raw(value: raw));

            if (BitConverter.DoubleToUInt64Bits(value: projected) != BitConverter.DoubleToUInt64Bits(value: expected)) { return $"the ladder raw {raw} projected to {projected}, expected {expected}"; }
        }

        return null;
    }

    // The double seam OUTWARD. The conversion is round-to-nearest-ties-to-even of the signed raw followed by an EXACT
    // scale by 2^-16, so every magnitude below 2^53 converts exactly and the rows at and above it exhibit the
    // long-to-double tie rule on BOTH signs: 2^53 + 1 is a tie resolved DOWN to 2^53, 2^53 + 3 a tie resolved UP to
    // 2^53 + 4. Both carrier extremes land on ±2^47 — MinValue exactly, because −2^63 is a power of two, and MaxValue by
    // rounding UP across the 2048-wide double gap below 2^63, which is the asymmetry the ladder exists to show. Every
    // expectation is a compile-time constant, so no floating-point arithmetic runs inside the law.
    private static readonly (long Raw, double Expected)[] FixedToDoubleLadder = [
        (0L, 0d),
        (1L, (1d / 65536d)), (-1L, (-1d / 65536d)),
        (32768L, 0.5d), (-32768L, -0.5d),
        (65536L, 1d), (-65536L, -1d),
        (98304L, 1.5d), (-98304L, -1.5d),
        (((1L << 53) - 1L), (9007199254740991d / 65536d)),
        (-((1L << 53) - 1L), (-9007199254740991d / 65536d)),
        (((1L << 53) + 1L), 137438953472d),
        (-((1L << 53) + 1L), -137438953472d),
        (((1L << 53) + 3L), (9007199254740996d / 65536d)),
        (-((1L << 53) + 3L), (-9007199254740996d / 65536d)),
        (long.MaxValue, 140737488355328d),
        (long.MinValue, -140737488355328d),
    ];

    /// <summary>Proves the wrapping additive surface is exact integer arithmetic on the raw, wrapped to the carrier:
    /// subtraction, negation, unary plus, and the two translations agree with arbitrary-width arithmetic, negation is an
    /// involution with <see cref="FixedQ4816.MinValue"/> as its own fixed point, and increment and decrement are
    /// mutually inverse and equal to adding and subtracting one.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedAdditiveOpsExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.AdditiveOpsExact(
            left: left,
            right: right
        );
    /// <summary>Proves all seven checked operators against the EXACT value of their operation, which decides both halves
    /// of the statement: the operator must return that value where it lands inside the carrier and must throw where it
    /// does not, and must agree bit-for-bit with its wrapping sibling wherever it answers. The two division refusals no
    /// swept operand can reach — a zero divisor on both routes, and the signed minimum over negative one — are stated
    /// alongside.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedCheckedOpsRefuse(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.CheckedOpsRefuse(
            left: left,
            right: right
        );
    // One checked operator against the exact value of its operation: the value where it is representable, the refusal
    // where it is not, and bit-for-bit agreement with the wrapping sibling in the first case.

    /// <summary>Proves the remainder is the exact truncated remainder of the raws, that the operator's two short-circuit
    /// divisors return the answer the oracle independently confirms, that the division identity and the magnitude bound
    /// hold, and that the signed minimum over negative epsilon returns zero rather than raising the platform's
    /// signed-remainder trap while a zero divisor still refuses.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedModulusExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.ModulusExact(
            left: left,
            right: right
        );
    /// <summary>Proves the order the carrier reports is the exact order of the raws, read through every operator the
    /// comparison contract names, that the two selections and the clamp agree with arbitrary-width formulations of the
    /// same rules, and that the boxed comparison and the inverted clamp range refuse as documented — the clamp's refusal
    /// naming no parameter at all, which is the platform's own spelling.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedOrderExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.OrderExact(
            left: left,
            right: right
        );
    /// <summary>Proves the two magnitude selections are the IEEE-754 <c>maximumMagnitude</c>/<c>minimumMagnitude</c>
    /// rules re-derived in arbitrary width, that the two <c>*Number</c> members do not diverge from them, that the pair
    /// is partitioned between the two selections, and that the absolute value and the sign transplant answer or refuse
    /// exactly where the carrier's asymmetric magnitude says they must.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedMagnitudeSelectionExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.MagnitudeSelectionExact(
            left: left,
            right: right
        );
    /// <summary>Proves the five integral parts against arbitrary-width derivations that each reach the answer by a
    /// DIFFERENT route from the subject's bit masking — a floored quotient, a quotient toward zero, a reflection of the
    /// floor, and an independently re-derived ties-to-even step — that they are mutually consistent, and that the two
    /// that can leave the carrier refuse exactly inside the top integer bucket while the two that cannot never
    /// throw.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedIntegralPartsExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.IntegralPartsExact(
            fixedRaws: [long.MinValue, (long.MinValue + 1L), 0x7FFF_FFFF_FFFF_0000L, 0x7FFF_FFFF_FFFF_7FFFL, 0x7FFF_FFFF_FFFF_8000L, long.MaxValue],
            left: left
        );
    /// <summary>Proves the seventeen classifiers on the carrier: integrality and its two parities read from the value's
    /// exact divisibility rather than from a bit mask, the sign predicates from the exact sign, the eleven constant ones
    /// holding their value at every raw, and the partitions the set must satisfy.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedPredicatesClassify(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.PredicatesClassify(left: left);
    /// <summary>Proves the interpolation carries ONE rounding over the TRUE mathematical result — the exact rational
    /// <c>from + (to − from)·amount</c>, formed in <see cref="BigInteger"/> with NO intermediate wrap at any width and
    /// only ONE final rounding — and that both endpoints and the degenerate segment are exact. The oracle never
    /// evaluates <c>to − from</c> as a standalone raw and never wraps it: it forms <c>fromRaw·2¹⁶ + (toRaw − fromRaw)·amountRaw</c>
    /// as one arbitrary-width integer (exact; a <see cref="BigInteger"/> difference cannot leave any carrier) and rounds
    /// that once, so it would catch a subject that wraps <c>to − from</c> before multiplying even where every operand
    /// and the true result are representable.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedLerpEndpointsAndOracle(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.LerpEndpointsAndOracle(
            left: left,
            right: right
        );

    // The styles the two-argument parse overloads apply, spelled out so the four style-taking entry points are driven
    // with the same grammar the four short ones use.
    private const NumberStyles FixedParseStyle = NumberStyles.AllowLeadingWhite |
                                                   NumberStyles.AllowTrailingWhite |
                                                   NumberStyles.AllowLeadingSign |
                                                   NumberStyles.AllowDecimalPoint;

    /// <summary>Proves the signed text seam at every swept raw: the rendering is the exact decimal expansion the
    /// arbitrary-width oracle derives by a different route, the span formatter fills an exact destination and refuses a
    /// short one, the text names a rational the oracle quantizes back onto the same raw, and all eight parse entry
    /// points round-trip it.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedTextRoundTrip(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.TextRoundTrip(
            formatBufferLength: 40,
            includeStyledOverloads: true,
            left: left,
            parseStyle: FixedParseStyle
        );

    // All eight parse entry points on one text: the four throwing routes and the four trying ones, string and span,
    // with and without an explicit style.
    private static string? FixedParseAll(string text, IFormatProvider provider, long expected) =>
        SignedFixedPointSubject<FixedQ4816, Q4816Adapter>.ParseAll(
            expected: expected,
            includeStyledOverloads: true,
            parseStyle: FixedParseStyle,
            provider: provider,
            text: text
        );

    /// <summary>Proves the signed text contract on its own committed ladder: every accepted spelling reaches the
    /// hand-derived raw through all eight entry points AND through the arbitrary-width quantizer, the asymmetric
    /// negative extreme is admitted while one epsilon beyond it is refused, the refusal ladder throws the documented
    /// exception on each route, and a non-invariant decimal separator is spliced rather than re-rendered.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedTextLadderAndRefusals() {
        foreach (var (text, inRange, expected) in FixedParseLadder) {
            var point = text.IndexOf(value: '.');
            var digits = ((point < 0)
                ? text
                : string.Concat(
                    str0: text.AsSpan(
                        length: point,
                        start: 0
                    ),
                    str1: text.AsSpan(start: (point + 1))
                )
            );
            var fractionDigitCount = ((point < 0)
                ? 0
                : ((text.Length - point) - 1)
            );

            var (oracleInRange, oracleRaw) = Oracles.DecimalToRaw(
                numerator: BigInteger.Parse(
                    value: digits,
                    provider: CultureInfo.InvariantCulture
                ),
                decimalExponent: fractionDigitCount,
                shift: FixedQ4816.FractionBitCount
            );

            if (oracleInRange != inRange) {
                return $"the oracle {(oracleInRange
                ? "admitted"
                : "refused")} '{text}', expected the opposite";
            }
            if (
                inRange &&
                (oracleRaw != expected)
            ) { return $"the oracle read '{text}' as {oracleRaw}, expected {expected}"; }

            if (!inRange) {
                if (
                    FixedQ4816.TryParse(
                    s: text,
                    provider: CultureInfo.InvariantCulture,
                    result: out var overflowed
                ) ||
                    (overflowed != default)
                ) { return $"'{text}' was accepted, or left raw {overflowed.Value} behind"; }
                if (!Throws<OverflowException>(action: () => _ = FixedQ4816.Parse(
                    s: text,
                    provider: CultureInfo.InvariantCulture
                ))) { return $"the throwing parse of '{text}' did not report an overflow"; }

                continue;
            }

            if (FixedParseAll(
                text: text,
                provider: CultureInfo.InvariantCulture,
                expected: expected
            ) is { } detail) { return detail; }
        }

        foreach (var text in FixedRefusedTexts) {
            if (
                FixedQ4816.TryParse(
                s: text,
                provider: CultureInfo.InvariantCulture,
                result: out var refused
            ) ||
                (refused != default)
            ) { return $"'{text}' was accepted, or left raw {refused.Value} behind"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ4816.Parse(
                s: text,
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing string parse accepted '{text}'"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ4816.Parse(
                s: text.AsSpan(),
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing span parse accepted '{text}'"; }
        }

        if (
            FixedQ4816.TryParse(
            provider: null,
            result: out var fromNull,
            s: ((string?)null)
        ) ||
            (fromNull != default)
        ) { return "a null string was accepted"; }
        if (!Throws<ArgumentNullException>(
            action: () => _ = FixedQ4816.Parse(
                provider: null,
                s: ((string)null!)
            ),
            paramName: "s"
        )) { return "the throwing parse accepted a null string"; }

        // A null string does NOT bypass argument validation: an invalid style throws the platform's ArgumentException
        // naming 'style' before the input is consulted — the BCL's own order — while a valid style keeps the false
        // verdict with the default left behind. RULED, and the correction is the point: a null short-circuit used to
        // swallow the style error the same call surfaces for every non-null string.
        if (!Throws<ArgumentException>(
            action: () => _ = FixedQ4816.TryParse(
                s: ((string?)null),
                style: NumberStyles.HexNumber,
                provider: CultureInfo.InvariantCulture,
                result: out _
            ),
            paramName: "style"
        )) { return "a null string under an invalid style skipped the style validation"; }
        if (
            FixedQ4816.TryParse(
            s: ((string?)null),
            style: FixedParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var nullStyled
        ) ||
            (nullStyled != default)
        ) { return "a null string under a valid style was accepted, or left a raw behind"; }

        // A non-invariant decimal separator and a non-invariant negative sign: the rendering is the invariant one with
        // exactly those two positions respliced, the span formatter sizes itself from each token's OWN width, and the
        // eight entry points read the result back under the same provider.
        Span<char> destination = stackalloc char[64];

        foreach (var provider in FixedSpliceProviders) {
            var separator = provider.NumberDecimalSeparator;
            var negativeSign = provider.NegativeSign;

            foreach (var raw in FixedSpliceRaws) {
                var value = Raw(value: raw);
                var expected = value.ToString().Replace(
                    comparisonType: StringComparison.Ordinal,
                    newValue: separator,
                    oldValue: "."
                );

                if (expected.StartsWith(value: '-')) {
                    expected = string.Concat(
                        str0: negativeSign,
                        str1: expected.AsSpan(start: 1)
                    );
                }

                var spliced = value.ToString(
                    format: "G",
                    formatProvider: provider
                );

                if (spliced != expected) { return $"the rendering of {raw} under ('{separator}', '{negativeSign}') is '{spliced}', not the invariant text respliced as '{expected}'"; }
                if (
                    !value.TryFormat(
                    destination: destination[..expected.Length],
                    charsWritten: out var written,
                    format: "G",
                    provider: provider
                ) ||
                    (written != expected.Length) ||
                    !destination[..written].SequenceEqual(other: expected)
                ) { return $"the span format did not fill an exactly sized destination for {raw} under ('{separator}', '{negativeSign}')"; }
                if (
                    !value.TryFormat(
                    charsWritten: out var oversized,
                    destination: destination,
                    format: default,
                    provider: provider
                ) ||
                    (oversized != expected.Length) ||
                    !destination[..oversized].SequenceEqual(other: expected)
                ) { return $"the span format did not write the same prefix into an oversized destination for {raw} under ('{separator}', '{negativeSign}')"; }
                if (
                    value.TryFormat(
                    destination: destination[..(expected.Length - 1)],
                    charsWritten: out var refused,
                    format: "g",
                    provider: provider
                ) ||
                    (0 != refused)
                ) { return $"the span format claimed a destination one character short for {raw} under ('{separator}', '{negativeSign}')"; }
                if (FixedParseAll(
                    expected: raw,
                    provider: provider,
                    text: spliced
                ) is { } detail) { return detail; }
            }
        }

        return null;
    }

    // Six provider shapes for the splice: the invariant control, a wider separator alone, a custom sign alone, both at
    // once, a multi-character sign, and both multi-character — so the reported length has to adjust for each token's
    // OWN width rather than merely for its spelling. Every provider is built here, so no law reads an ambient culture.
    private static readonly NumberFormatInfo[] FixedSpliceProviders = [
        new() { NegativeSign = "-", NumberDecimalSeparator = ".", NumberGroupSeparator = "_", },
        new() { NegativeSign = "-", NumberDecimalSeparator = ",", NumberGroupSeparator = "_", },
        new() { NegativeSign = "~", NumberDecimalSeparator = ".", NumberGroupSeparator = "_", },
        new() { NegativeSign = "~", NumberDecimalSeparator = ",", NumberGroupSeparator = "_", },
        new() { NegativeSign = "MINUS", NumberDecimalSeparator = ".", NumberGroupSeparator = "_", },
        new() { NegativeSign = "~~", NumberDecimalSeparator = "<>", NumberGroupSeparator = "_", },
    ];
    // The raws the splice is exercised at: a negative with a fraction, a positive whole, both carrier extremes — the
    // asymmetric minimum being the longest rendering there is — and zero.
    private static readonly long[] FixedSpliceRaws = [-65537L, 1L, long.MinValue, long.MaxValue, 0L];
    // The parse ladder: each expectation hand-derived from the exact decimal value and the ties-to-even rule.
    private static readonly (string Text, bool InRange, long Expected)[] FixedParseLadder = [
        ("0", true, 0L), ("-0", true, 0L), ("-0.000001", true, 0L),                     // no negative zero
        ("1", true, 65536L), ("-1", true, -65536L),
        ("0.00000762939453125", true, 0L),                                              // exactly ½ ULP → 0 (even)
        ("0.00002288818359375", true, 2L),                                              // exactly 1½ ULP → 2
        ("0.00003814697265625", true, 2L),                                              // exactly 2½ ULP → 2
        ("-0.00002288818359375", true, -2L),
        ("0.00000762939453125000000000000001", true, 1L),                               // the sticky bit past digit 17
        ("100000000000000.00000762939453125000000000000001", true, 6553600000000000001L),
        ("140737488355327.9999847412109375", true, long.MaxValue),
        ("-140737488355328", true, long.MinValue),                                      // the asymmetric negative extreme
        ("-140737488355328.0000152587890625", false, 0L),                               // one epsilon beyond it
        ("140737488355328", false, 0L),
    ];
    // Spellings the grammar does not name: empty, blank, a bare sign, a bare point, two points, an exponent the style
    // does not admit, hexadecimal, and a word.
    private static readonly string[] FixedRefusedTexts = ["", "   ", "-", "+", ".", "1.2.3", "1e3", "0x10", "one"];

    // ---- carrier scalar (FixedQ1648, Q16.48) ----
    //
    // A range-for-resolution scalar leaning toward resolution: a signed Q16.48 sibling of FixedQ4816 sharing the
    // same sixty-four-bit long carrier and the same fused-arithmetic substrate (FusedArithmetic, FixedPointRounding),
    // so its non-transcendental surface mirrors FixedQ4816's own scalar laws member for member, retargeted at forty-
    // eight fraction bits and a sixteen-bit (not forty-eight-bit) integer range — well suited to a quantity, such as
    // a reciprocal, whose useful values sit close to zero but span many decades of magnitude. It carries no
    // transcendentals (Sqrt/Log2/Exp2/SinCos/Atan2/Pow), so those FixedQ4816 law shapes have no counterpart here.
    // Every oracle below routes through the SAME shared-nothing Oracles primitives FixedQ4816's own laws use —
    // RoundDyadic, RoundDyadicRatio, WrapToRaw, RoundRationalTiesToEven, NearestBinary64Bits, FloorQuotient,
    // ExactDyadicDecimalSigned, DecimalToRaw — all already parameterized by shift, so nothing here re-implements a
    // rounding or wrapping rule; only the shift argument changes from sixteen to forty-eight.

    private static FixedQ1648 RawQ1648(long value) =>
        FixedQ1648.FromRawBits(value: value);

    /// <summary>The subject <see cref="FixedQ1648"/> multiply.</summary>
    public static long Q1648Multiply(long a, long b) =>
        (RawQ1648(value: a) * RawQ1648(value: b)).Value;
    /// <summary>The subject <see cref="FixedQ1648"/> add.</summary>
    public static long Q1648Add(long a, long b) =>
        (RawQ1648(value: a) + RawQ1648(value: b)).Value;
    /// <summary>The dyadic oracle for <see cref="FixedQ1648"/> multiply — one Q48 rounding, ties to even.</summary>
    public static long Q1648MultiplyOracle(long a, long b) =>
        Oracles.RoundDyadic(
            exact: (((BigInteger)a) * b),
            shift: FixedQ1648.FractionBitCount
        );
    /// <summary>The oracle for <see cref="FixedQ1648"/> add — exact, wrapped to the carrier.</summary>
    public static long Q1648AddOracle(long a, long b) =>
        Oracles.WrapToRaw(value: (((BigInteger)a) + b));
    /// <summary>The subject <see cref="FixedQ1648"/> divide.</summary>
    public static long Q1648Divide(long a, long b) =>
        (RawQ1648(value: a) / RawQ1648(value: NonZeroDivisor(b: b))).Value;
    /// <summary>The dyadic oracle for <see cref="FixedQ1648"/> divide — one Q48 rounding of the exact ratio, ties to
    /// even.</summary>
    public static long Q1648DivideOracle(long a, long b) =>
        Oracles.RoundDyadicRatio(
            numerator: new BigInteger(value: a),
            denominator: new BigInteger(value: NonZeroDivisor(b: b)),
            shift: FixedQ1648.FractionBitCount
        );
    /// <summary>Proves the signed Q16.48 grid on its own ladder: the declared constants are one consistent fact, the
    /// raw survives every construction route, the whole-number seam admits exactly the sixteen-bit integer range and
    /// refuses the first value beyond on each side, and the double seam saturates at both extremes and rounds ties
    /// to even.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648GridAndConstruction() =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.GridAndConstruction(
            fractionBitCount: 48,
            rawLadder: Q1648RawLadder,
            integerLadder: Q1648IntegerLadder,
            refusedIntegers: [32768L, -32769L, long.MaxValue, long.MinValue],
            doubleLadder: Q1648DoubleLadder,
            // This carrier has no separate outward-projection law, so its own raw ladder carries that seam here.
            includeDoubleProjection: true
        );

    // The construction ladder: both carrier extremes and their neighbourhoods, zero and both units, and the
    // fraction/integer seam at 2^48 either side.
    private static readonly long[] Q1648RawLadder = [
        long.MinValue, (long.MinValue + 1L), -562949953421313L, -562949953421312L, -562949953421311L,
        -281474976710657L, -281474976710656L, -1L, 0L, 1L, 281474976710656L, 281474976710657L,
        562949953421311L, 562949953421312L, (long.MaxValue - 1L), long.MaxValue,
    ];
    // The whole-number seam: both admissible extremes ([-32768, 32767]) and the ordinary interior.
    private static readonly long[] Q1648IntegerLadder = [-32768L, -32767L, -2L, -1L, 0L, 1L, 2L, 32766L, 32767L];
    // The double seam, each expectation derived from the IEEE-754 layout and the 2⁻⁴⁸ grid rather than from the
    // kernel: not-a-number and both infinities, the two saturations, the exactly-representable interior points, the
    // four half-ULP ties whose ties-to-even resolution is the house rounding discipline, and the 2^15 seam — 32768
    // scales to exactly 2⁶³ (one raw past MaxValue) while −32768 scales to exactly −2⁶³ (MinValue itself, exact
    // rather than saturating).
    private static readonly (double Value, long Expected)[] Q1648DoubleLadder = [
        (double.NaN, 0L),
        (double.NegativeInfinity, long.MinValue),
        (double.PositiveInfinity, long.MaxValue),
        (0d, 0L), (-0d, 0L),
        (1d, 281474976710656L), (-1d, -281474976710656L),
        (0.5d, 140737488355328L), (-0.5d, -140737488355328L),
        ((1d / 281474976710656d), 1L), (((-1d) / 281474976710656d), -1L),
        ((1d / 562949953421312d), 0L), (((-1d) / 562949953421312d), 0L),
        ((3d / 562949953421312d), 2L), (((-3d) / 562949953421312d), -2L),
        ((5d / 562949953421312d), 2L), (((-5d) / 562949953421312d), -2L),
        (32768d, long.MaxValue),
        (-32768d, long.MinValue),
        (1e300d, long.MaxValue), (-1e300d, long.MinValue),
    ];

    /// <summary>Proves the wrapping additive surface is exact integer arithmetic on the raw, wrapped to the carrier:
    /// subtraction, negation, unary plus, and the two translations agree with arbitrary-width arithmetic, negation is
    /// an involution with <see cref="FixedQ1648.MinValue"/> as its own fixed point, and increment and decrement are
    /// mutually inverse and equal to adding and subtracting one.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648AdditiveOpsExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.AdditiveOpsExact(
            left: left,
            right: right
        );
    /// <summary>Proves all seven checked operators against the EXACT value of their operation, which decides both
    /// halves of the statement: the operator must return that value where it lands inside the carrier and must throw
    /// where it does not, and must agree bit-for-bit with its wrapping sibling wherever it answers. The two division
    /// refusals no swept operand can reach — a zero divisor on both routes, and the signed minimum over negative one —
    /// are stated alongside.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648CheckedOpsRefuse(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.CheckedOpsRefuse(
            left: left,
            right: right
        );
    /// <summary>Proves the remainder is the exact truncated remainder of the raws, that the operator's two
    /// short-circuit divisors return the answer the oracle independently confirms, that the division identity and the
    /// magnitude bound hold, and that the signed minimum over negative epsilon returns zero rather than raising the
    /// platform's signed-remainder trap while a zero divisor still refuses.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648ModulusExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.ModulusExact(
            left: left,
            right: right
        );
    /// <summary>Proves the order the carrier reports is the exact order of the raws, read through every operator the
    /// comparison contract names, that the two selections and the clamp agree with arbitrary-width formulations of the
    /// same rules, and that the boxed comparison and the inverted clamp range refuse as documented.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648OrderExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.OrderExact(
            left: left,
            right: right
        );
    /// <summary>Proves the two magnitude selections are the IEEE-754 <c>maximumMagnitude</c>/<c>minimumMagnitude</c>
    /// rules re-derived in arbitrary width, that the two <c>*Number</c> members do not diverge from them, that the
    /// pair is partitioned between the two selections, and that the absolute value and the sign transplant answer or
    /// refuse exactly where the carrier's asymmetric magnitude says they must.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648MagnitudeSelectionExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.MagnitudeSelectionExact(
            left: left,
            right: right
        );
    /// <summary>Proves the five integral parts against arbitrary-width derivations that each reach the answer by a
    /// DIFFERENT route from the subject's bit masking, that they are mutually consistent, and that the two that can
    /// leave the carrier refuse exactly inside the top integer bucket while the two that cannot never throw.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648IntegralPartsExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.IntegralPartsExact(
            fixedRaws: [long.MinValue, (long.MinValue + 1L), (32767L << 48), ((32767L << 48) | ((1L << 47) - 1L)), ((32767L << 48) | (1L << 47)), long.MaxValue],
            left: left
        );
    /// <summary>Proves the seventeen classifiers on the carrier: integrality and its two parities read from the
    /// value's exact divisibility rather than from a bit mask, the sign predicates from the exact sign, the eleven
    /// constant ones holding their value at every raw, and the partitions the set must satisfy.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648PredicatesClassify(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.PredicatesClassify(left: left);
    /// <summary>Proves the interpolation carries ONE rounding over the TRUE mathematical result — the exact rational
    /// <c>from + (to − from)·amount</c>, formed in <see cref="BigInteger"/> with NO intermediate wrap at any width and
    /// only ONE final rounding — and that both endpoints and the degenerate segment are exact. The oracle never
    /// evaluates <c>to − from</c> as a standalone raw and never wraps it: it forms <c>fromRaw·2⁴⁸ + (toRaw − fromRaw)·amountRaw</c>
    /// as one arbitrary-width integer (exact; a <see cref="BigInteger"/> difference cannot leave any carrier) and rounds
    /// that once, so it would catch a subject that wraps <c>to − from</c> before multiplying even where every operand
    /// and the true result are representable.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648LerpEndpointsAndOracle(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.LerpEndpointsAndOracle(
            left: left,
            right: right
        );
    /// <summary>Proves the signed text seam at every swept raw: the rendering is the exact decimal expansion the
    /// arbitrary-width oracle derives by a different route, the span formatter fills an exact destination and refuses
    /// a short one, the text names a rational the oracle quantizes back onto the same raw, and all eight parse entry
    /// points round-trip it.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648TextRoundTrip(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.TextRoundTrip(
            formatBufferLength: 80,
            includeStyledOverloads: false,
            left: left,
            parseStyle: FixedParseStyle
        );

    private static string? Q1648ParseAll(string text, IFormatProvider provider, long expected) =>
        SignedFixedPointSubject<FixedQ1648, Q1648Adapter>.ParseAll(
            expected: expected,
            includeStyledOverloads: false,
            parseStyle: FixedParseStyle,
            provider: provider,
            text: text
        );

    /// <summary>Proves the signed text contract refuses malformed spellings on every entry point, that the null
    /// string is refused (rather than accepted or defaulted) on both the throwing and the trying routes, and that the
    /// asymmetric integer extremes — <c>-32768</c> round-trips exactly, <c>32768</c> is refused as an overflow rather
    /// than accepted or mis-parsed — hold. Unlike <see cref="Q1648TextRoundTrip"/>, which sweeps sampled operands
    /// through the oracle, this is the type's own fixed refusal ladder and needs no domain.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648TextRefusals() {
        foreach (var text in Q1648RefusedTexts) {
            if (
                FixedQ1648.TryParse(
                s: text,
                provider: CultureInfo.InvariantCulture,
                result: out var refused
            ) ||
                (refused != default)
            ) { return $"'{text}' was accepted, or left raw {refused.Value} behind"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ1648.Parse(
                s: text,
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing string parse accepted '{text}'"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ1648.Parse(
                s: text.AsSpan(),
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing span parse accepted '{text}'"; }
        }

        if (
            !FixedQ1648.TryParse(
            s: "-32768",
            provider: CultureInfo.InvariantCulture,
            result: out var minText
        ) ||
            (minText != FixedQ1648.MinValue)
        ) { return "the text '-32768' did not parse to MinValue"; }
        if (
            FixedQ1648.TryParse(
            s: "32768",
            provider: CultureInfo.InvariantCulture,
            result: out var overflowed
        ) ||
            (overflowed != default)
        ) { return "'32768' was accepted, or left a raw behind"; }
        if (!Throws<OverflowException>(action: () => _ = FixedQ1648.Parse(
            s: "32768",
            provider: CultureInfo.InvariantCulture
        ))) { return "the throwing parse of '32768' did not report an overflow"; }

        if (
            FixedQ1648.TryParse(
            provider: null,
            result: out var fromNull,
            s: ((string?)null)
        ) ||
            (fromNull != default)
        ) { return "a null string was accepted"; }
        if (!Throws<ArgumentNullException>(
            action: () => _ = FixedQ1648.Parse(
                provider: null,
                s: ((string)null!)
            ),
            paramName: "s"
        )) { return "the throwing parse accepted a null string"; }

        return null;
    }

    // Spellings the grammar does not name: empty, blank, a bare sign, a bare point, two points, an exponent the
    // style does not admit, hexadecimal, and a word.
    private static readonly string[] Q1648RefusedTexts = ["", "   ", "-", "+", ".", "1.2.3", "1e3", "0x10", "one"];

    /// <summary>Proves the NumberStyles-taking Parse/TryParse overloads genuinely depend on their style argument for
    /// FixedQ1648: <c>"1e3"</c> is refused under the default style (no <c>AllowExponent</c> — see
    /// <see cref="Q1648RefusedTexts"/>) but accepted and quantized to exactly <c>1000</c> under
    /// <see cref="NumberStyles.Float"/>, which adds <c>AllowExponent</c>. Every other FixedQ1648 text law
    /// (<see cref="Q1648TextRoundTrip"/>, <see cref="Q1648TextRefusals"/>, <see cref="Q1648TextParseTies"/>) calls
    /// only the two-argument provider-only overloads, which forward a FIXED style internally — the manifest's
    /// "covered" mark on the four-argument overloads was earned by that forwarding alone, never by a caller-supplied
    /// style actually changing the outcome, until this law.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648StyledParseIsGenuine() {
        const string text = "1e3";
        const long expected = (1000L << FixedQ1648.FractionBitCount);

        if (
            !FixedQ1648.TryParse(
            s: text,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out var fromString
        ) ||
            (fromString.Value != expected)
        ) { return $"the styled string try-parse of '{text}' under NumberStyles.Float did not return {expected}"; }
        if (
            !FixedQ1648.TryParse(
            s: text.AsSpan(),
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out var fromSpan
        ) ||
            (fromSpan.Value != expected)
        ) { return $"the styled span try-parse of '{text}' under NumberStyles.Float did not return {expected}"; }
        if (FixedQ1648.Parse(
            s: text,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"the styled string parse of '{text}' under NumberStyles.Float did not return {expected}"; }
        if (FixedQ1648.Parse(
            s: text.AsSpan(),
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"the styled span parse of '{text}' under NumberStyles.Float did not return {expected}"; }

        // The same text, through the same four-argument entry point, refused at a style lacking AllowExponent —
        // Q1648TextRefusals already pins the provider-only route to this refusal; this pins the styled route too, so
        // both ends of the discriminating pair are reached directly rather than through internal forwarding. The
        // style spelled here matches FixedQ1648's own (private) DefaultParseStyle field-for-field.
        const NumberStyles noExponentStyle = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                                               NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

        if (
            FixedQ1648.TryParse(
            s: text,
            style: noExponentStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var refused
        ) ||
            (refused != default)
        ) { return $"'{text}' was accepted under a style without AllowExponent"; }

        return null;
    }
    /// <summary>Proves the half-ULP tie-break at FixedQ1648's own forty-nine-fraction-digit limit: a decimal string
    /// exactly at the tie between two raws rounds to the EVEN one, one just below rounds down, and one just above
    /// rounds up. <see cref="Q1648TextRoundTrip"/> alone never reaches this: every string it feeds Parse is the exact
    /// terminating expansion of an already-representable raw, so the division remainder there is always zero and the
    /// tie-break arithmetic never runs. <c>3·2⁻⁴⁹</c> is exactly forty-nine fraction decimal digits — the type's own
    /// <c>fractionBitCount + 1</c> — so no digit is discarded and the remainder lands exactly on the tie at raw
    /// <c>1.5</c>; perturbing only the forty-ninth digit moves the remainder one part in <c>2·5⁴⁹</c> off the tie
    /// without touching the quotient.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648TextParseTies() {
        const string below = "0.0000000000000053290705182007513940334320068359374";
        const string exact = "0.0000000000000053290705182007513940334320068359375";
        const string above = "0.0000000000000053290705182007513940334320068359376";

        return
            (Q1648CheckTie(
            expectedRaw: 1,
            text: below
        ) ??
            (Q1648CheckTie(
            expectedRaw: 2,
            text: exact
        ) ??
            Q1648CheckTie(
            expectedRaw: 2,
            text: above
        )));
    }

    // Re-derives the expected raw from the string with the same shared-nothing oracle Q1648TextRoundTrip uses (never
    // through FixedQ1648.Parse), then proves every parse entry point agrees with the CALLER's expected raw — so a
    // wrong expectedRaw in the test itself, not just a wrong subject, would still be caught.
    private static string? Q1648CheckTie(string text, long expectedRaw) {
        var point = text.IndexOf(value: '.');
        var digits = string.Concat(
            str0: text.AsSpan(
                length: point,
                start: 0
            ),
            str1: text.AsSpan(start: (point + 1))
        );
        var fractionDigitCount = ((text.Length - point) - 1);

        var (inRange, quantized) = Oracles.DecimalToRaw(
            numerator: BigInteger.Parse(
                value: digits,
                provider: CultureInfo.InvariantCulture
            ),
            decimalExponent: fractionDigitCount,
            shift: FixedQ1648.FractionBitCount
        );

        if (
            !inRange ||
            (quantized != expectedRaw)
        ) { return $"the oracle quantized '{text}' as {quantized} (in range: {inRange}), not the expected {expectedRaw}"; }

        return Q1648ParseAll(
            text: text,
            provider: CultureInfo.InvariantCulture,
            expected: expectedRaw
        );
    }

    /// <summary>Proves this carrier's FixedQ4816 (Q48.16) peer conversion. Narrowing
    /// (<see cref="FixedQ1648.ToFixedQ4816"/>) is a single ties-to-even rounding at the thirty-two-bit fraction
    /// difference and NEVER overflows, because Q16.48's whole range fits inside Q48.16's; widening
    /// (<see cref="FixedQ1648.FromFixedQ4816"/> / <see cref="FixedQ1648.TryFromFixedQ4816"/>) is EXACT — it only
    /// appends zero bits — but is gated by whether the source's integer part fits the sixteen-bit range, and the
    /// widen-then-narrow round trip recovers the original Q48.16 value exactly wherever it succeeded.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q1648PeerConversionExact() {
        foreach (var raw in Q1648RawLadder) {
            var value = RawQ1648(value: raw);
            var narrowed = value.ToFixedQ4816();
            var expected = Oracles.RoundDyadic(
                exact: new BigInteger(value: raw),
                shift: (FixedQ1648.FractionBitCount - FixedQ4816.FractionBitCount)
            );

            if (narrowed.Value != expected) { return $"ToFixedQ4816 of raw {raw} is {narrowed.Value}, expected {expected}"; }
        }

        foreach (var raw in FixedQ4816RawLadderForPeer) {
            var source = FixedQ4816.FromRawBits(value: raw);
            var widened = (new BigInteger(value: raw) << (FixedQ1648.FractionBitCount - FixedQ4816.FractionBitCount));
            var inRange = ((widened >= long.MinValue) && (widened <= long.MaxValue));
            var succeeded = FixedQ1648.TryFromFixedQ4816(
                result: out var tried,
                value: source
            );

            if (succeeded != inRange) { return $"TryFromFixedQ4816 of raw {raw} reported {succeeded}, expected {inRange}"; }

            if (inRange) {
                if (tried.Value != widened) { return $"TryFromFixedQ4816 of raw {raw} is {tried.Value}, expected {widened}"; }
                if (FixedQ1648.FromFixedQ4816(value: source).Value != widened) { return $"FromFixedQ4816 of raw {raw} is wrong"; }
                if (tried.ToFixedQ4816() != source) { return $"the widen-then-narrow round trip of raw {raw} did not recover the original Q48.16 value"; }
            } else {
                if (tried != default) { return $"TryFromFixedQ4816 of raw {raw} left a non-default result behind on failure"; }
                if (!Throws<OverflowException>(action: () => _ = FixedQ1648.FromFixedQ4816(value: source))) { return $"FromFixedQ4816 of raw {raw} did not throw on overflow"; }
            }
        }

        return null;
    }

    // FixedQ4816 raws spanning the Q16.48 integer boundary: comfortably inside, exactly at both edges, one raw past
    // both edges, and a scattering of ordinary and wildly out-of-range values.
    private static readonly long[] FixedQ4816RawLadderForPeer = [
        0L, 65536L, -65536L,
        (32767L << 16), ((32767L << 16) | 0xFFFFL),
        (-32768L << 16),
        (((-32768L) << 16) - 1L),
        (32768L << 16),
        long.MaxValue, long.MinValue,
        (1L << 47), -(1L << 47),
    ];

    // ---- carrier scalar (FixedQ3232, Q32.32) ----
    //
    // A balanced scalar splitting integer and fraction bits evenly: a signed Q32.32 sibling of FixedQ4816 sharing
    // the same sixty-four-bit long carrier and the same fused-arithmetic substrate (FusedArithmetic,
    // FixedPointRounding), so its non-transcendental surface mirrors FixedQ4816's own scalar laws member for member,
    // retargeted at thirty-two fraction bits and a thirty-two-bit (not forty-eight-bit) integer range — the balanced
    // point between FixedQ4816's range-leaning Q48.16 split and FixedQ1648's resolution-leaning Q16.48 split. It
    // carries no transcendentals (Sqrt/Log2/Exp2/SinCos/Atan2/Pow), so those FixedQ4816 law shapes have no
    // counterpart here. Every oracle below routes through the SAME shared-nothing Oracles primitives FixedQ4816's
    // own laws use — RoundDyadic, RoundDyadicRatio, WrapToRaw, RoundRationalTiesToEven, NearestBinary64Bits,
    // FloorQuotient, ExactDyadicDecimalSigned, DecimalToRaw — all already parameterized by shift, so nothing here
    // re-implements a rounding or wrapping rule; only the shift argument changes from sixteen to thirty-two.

    private static FixedQ3232 RawQ3232(long value) =>
        FixedQ3232.FromRawBits(value: value);

    // FixedQ3232's private default parse style, restated so the laws can reach the styled overloads through the same
    // grammar as the provider-only forwards and prove those overloads rather than receiving coverage by name alone.
    private const NumberStyles Q3232DefaultParseStyle = NumberStyles.AllowLeadingWhite |
                                                          NumberStyles.AllowTrailingWhite |
                                                          NumberStyles.AllowLeadingSign |
                                                          NumberStyles.AllowDecimalPoint;

    /// <summary>The subject <see cref="FixedQ3232"/> multiply.</summary>
    public static long Q3232Multiply(long a, long b) =>
        (RawQ3232(value: a) * RawQ3232(value: b)).Value;
    /// <summary>The subject <see cref="FixedQ3232"/> add.</summary>
    public static long Q3232Add(long a, long b) =>
        (RawQ3232(value: a) + RawQ3232(value: b)).Value;
    /// <summary>The dyadic oracle for <see cref="FixedQ3232"/> multiply — one Q32 rounding, ties to even.</summary>
    public static long Q3232MultiplyOracle(long a, long b) =>
        Oracles.RoundDyadic(
            exact: (((BigInteger)a) * b),
            shift: FixedQ3232.FractionBitCount
        );
    /// <summary>The oracle for <see cref="FixedQ3232"/> add — exact, wrapped to the carrier.</summary>
    public static long Q3232AddOracle(long a, long b) =>
        Oracles.WrapToRaw(value: (((BigInteger)a) + b));
    /// <summary>The subject <see cref="FixedQ3232"/> divide.</summary>
    public static long Q3232Divide(long a, long b) =>
        (RawQ3232(value: a) / RawQ3232(value: NonZeroDivisor(b: b))).Value;
    /// <summary>The dyadic oracle for <see cref="FixedQ3232"/> divide — one Q32 rounding of the exact ratio, ties to
    /// even.</summary>
    public static long Q3232DivideOracle(long a, long b) =>
        Oracles.RoundDyadicRatio(
            numerator: new BigInteger(value: a),
            denominator: new BigInteger(value: NonZeroDivisor(b: b)),
            shift: FixedQ3232.FractionBitCount
        );
    /// <summary>Proves the signed Q32.32 grid on its own ladder: the declared constants are one consistent fact, the
    /// raw survives every construction route, the whole-number seam admits exactly the thirty-two-bit integer range
    /// and refuses the first value beyond on each side, and the double seam saturates at both extremes and rounds
    /// ties to even.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232GridAndConstruction() =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.GridAndConstruction(
            fractionBitCount: 32,
            rawLadder: Q3232RawLadder,
            integerLadder: Q3232IntegerLadder,
            refusedIntegers: [2147483648L, -2147483649L, long.MaxValue, long.MinValue],
            doubleLadder: Q3232DoubleLadder,
            // This carrier has no separate outward-projection law, so its own raw ladder carries that seam here.
            includeDoubleProjection: true
        );

    // The construction ladder: both carrier extremes and their neighbourhoods, zero and both units, and the
    // fraction/integer seam at 2^32 either side.
    private static readonly long[] Q3232RawLadder = [
        long.MinValue, (long.MinValue + 1L), -8589934593L, -8589934592L, -8589934591L,
        -4294967297L, -4294967296L, -1L, 0L, 1L, 4294967296L, 4294967297L,
        8589934591L, 8589934592L, (long.MaxValue - 1L), long.MaxValue,
    ];
    // The whole-number seam: both admissible extremes ([-2147483648, 2147483647]) and the ordinary interior.
    private static readonly long[] Q3232IntegerLadder = [-2147483648L, -2147483647L, -2L, -1L, 0L, 1L, 2L, 2147483646L, 2147483647L];
    // The double seam, each expectation derived from the IEEE-754 layout and the 2⁻³² grid rather than from the
    // kernel: not-a-number and both infinities, both ends of the subnormal band, the two saturations, the last double
    // grid point inside each carrier edge, the exactly-representable interior points, the four half-ULP ties whose
    // ties-to-even resolution is the house rounding discipline, and the 2^31 seam —
    // 2147483648 scales to exactly 2⁶³ (one raw past MaxValue) while −2147483648 scales to exactly −2⁶³ (MinValue
    // itself, exact rather than saturating).
    private static readonly (double Value, long Expected)[] Q3232DoubleLadder = [
        (double.NaN, 0L),
        (double.NegativeInfinity, long.MinValue),
        (double.PositiveInfinity, long.MaxValue),
        (0d, 0L), (-0d, 0L),
        (double.Epsilon, 0L), (-double.Epsilon, 0L),
        (BitConverter.Int64BitsToDouble(value: 0x000FFFFFFFFFFFFF), 0L),
        (BitConverter.Int64BitsToDouble(value: unchecked((long)0x800FFFFFFFFFFFFFUL)), 0L),
        (1d, 4294967296L), (-1d, -4294967296L),
        (0.5d, 2147483648L), (-0.5d, -2147483648L),
        ((1d / 4294967296d), 1L), (((-1d) / 4294967296d), -1L),
        ((1d / 8589934592d), 0L), (((-1d) / 8589934592d), 0L),
        ((3d / 8589934592d), 2L), (((-3d) / 8589934592d), -2L),
        ((5d / 8589934592d), 2L), (((-5d) / 8589934592d), -2L),
        (BitConverter.Int64BitsToDouble(value: 0x41DFFFFFFFFFFFFF), 9223372036854774784L),
        (BitConverter.Int64BitsToDouble(value: unchecked((long)0xC1DFFFFFFFFFFFFFUL)), (long.MinValue + 1024L)),
        (2147483648d, long.MaxValue),
        (-2147483648d, long.MinValue),
        (1e300d, long.MaxValue), (-1e300d, long.MinValue),
    ];

    /// <summary>Proves the wrapping additive surface is exact integer arithmetic on the raw, wrapped to the carrier:
    /// subtraction, negation, unary plus, and the two translations agree with arbitrary-width arithmetic, negation is
    /// an involution with <see cref="FixedQ3232.MinValue"/> as its own fixed point, and increment and decrement are
    /// mutually inverse and equal to adding and subtracting one.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232AdditiveOpsExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.AdditiveOpsExact(
            left: left,
            right: right
        );
    /// <summary>Proves all seven checked operators against the EXACT value of their operation, which decides both
    /// halves of the statement: the operator must return that value where it lands inside the carrier and must throw
    /// where it does not, and must agree bit-for-bit with its wrapping sibling wherever it answers. The two division
    /// refusals no swept operand can reach — a zero divisor on both routes, and the signed minimum over negative one —
    /// are stated alongside.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232CheckedOpsRefuse(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.CheckedOpsRefuse(
            left: left,
            right: right
        );
    /// <summary>Proves the remainder is the exact truncated remainder of the raws, that the operator's two
    /// short-circuit divisors return the answer the oracle independently confirms, that the division identity and the
    /// magnitude bound hold, and that the signed minimum over negative epsilon returns zero rather than raising the
    /// platform's signed-remainder trap while a zero divisor still refuses.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232ModulusExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.ModulusExact(
            left: left,
            right: right
        );
    /// <summary>Proves the order the carrier reports is the exact order of the raws, read through every operator the
    /// comparison contract names, that the two selections and the clamp agree with arbitrary-width formulations of the
    /// same rules, and that the boxed comparison and the inverted clamp range refuse as documented.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232OrderExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.OrderExact(
            left: left,
            right: right
        );
    /// <summary>Proves the two magnitude selections are the IEEE-754 <c>maximumMagnitude</c>/<c>minimumMagnitude</c>
    /// rules re-derived in arbitrary width, that the two <c>*Number</c> members do not diverge from them, that the
    /// pair is partitioned between the two selections, and that the absolute value and the sign transplant answer or
    /// refuse exactly where the carrier's asymmetric magnitude says they must.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232MagnitudeSelectionExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.MagnitudeSelectionExact(
            left: left,
            right: right
        );
    /// <summary>Proves the five integral parts against arbitrary-width derivations that each reach the answer by a
    /// DIFFERENT route from the subject's bit masking, that they are mutually consistent, and that the two that can
    /// leave the carrier refuse exactly inside the top integer bucket while the two that cannot never throw.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232IntegralPartsExact(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.IntegralPartsExact(
            fixedRaws: [long.MinValue, (long.MinValue + 1L), (2147483647L << 32), ((2147483647L << 32) | ((1L << 31) - 1L)), ((2147483647L << 32) | (1L << 31)), long.MaxValue],
            left: left
        );
    /// <summary>Proves the seventeen classifiers on the carrier: integrality and its two parities read from the
    /// value's exact divisibility rather than from a bit mask, the sign predicates from the exact sign, the eleven
    /// constant ones holding their value at every raw, and the partitions the set must satisfy.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232PredicatesClassify(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.PredicatesClassify(left: left);
    /// <summary>Proves the interpolation carries ONE rounding over the TRUE mathematical result — the exact rational
    /// <c>from + (to − from)·amount</c>, formed in <see cref="BigInteger"/> with NO intermediate wrap at any width and
    /// only ONE final rounding — and that both endpoints and the degenerate segment are exact. The oracle never
    /// evaluates <c>to − from</c> as a standalone raw and never wraps it: it forms <c>fromRaw·2³² + (toRaw − fromRaw)·amountRaw</c>
    /// as one arbitrary-width integer (exact; a <see cref="BigInteger"/> difference cannot leave any carrier) and rounds
    /// that once, so it would catch a subject that wraps <c>to − from</c> before multiplying even where every operand
    /// and the true result are representable.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232LerpEndpointsAndOracle(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.LerpEndpointsAndOracle(
            left: left,
            right: right
        );
    /// <summary>Proves the signed text seam at every swept raw: the rendering is the exact decimal expansion the
    /// arbitrary-width oracle derives by a different route, the span formatter fills an exact destination and refuses
    /// a short one, the text names a rational the oracle quantizes back onto the same raw, and all eight parse entry
    /// points round-trip it.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232TextRoundTrip(long[] left, long[] right) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.TextRoundTrip(
            formatBufferLength: 80,
            includeStyledOverloads: true,
            left: left,
            parseStyle: Q3232DefaultParseStyle
        );

    private static string? Q3232ParseAll(string text, IFormatProvider provider, long expected) =>
        SignedFixedPointSubject<FixedQ3232, Q3232Adapter>.ParseAll(
            expected: expected,
            includeStyledOverloads: true,
            parseStyle: Q3232DefaultParseStyle,
            provider: provider,
            text: text
        );

    /// <summary>Proves the signed text contract refuses malformed spellings on every entry point, that the null
    /// string is refused (rather than accepted or defaulted) on both the throwing and the trying routes, and that the
    /// asymmetric integer extremes — <c>-2147483648</c> round-trips exactly, <c>2147483648</c> is refused as an
    /// overflow rather than accepted or mis-parsed — hold. Unlike <see cref="Q3232TextRoundTrip"/>, which sweeps
    /// sampled operands through the oracle, this is the type's own fixed refusal ladder and needs no domain.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232TextRefusals() {
        foreach (var text in Q3232RefusedTexts) {
            if (
                FixedQ3232.TryParse(
                s: text,
                provider: CultureInfo.InvariantCulture,
                result: out var refused
            ) ||
                (refused != default)
            ) { return $"'{text}' was accepted, or left raw {refused.Value} behind"; }
            if (
                FixedQ3232.TryParse(
                s: text.AsSpan(),
                provider: CultureInfo.InvariantCulture,
                result: out var refusedSpan
            ) ||
                (refusedSpan != default)
            ) { return $"the span '{text}' was accepted, or left raw {refusedSpan.Value} behind"; }
            if (
                FixedQ3232.TryParse(
                s: text,
                style: Q3232DefaultParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refusedStyled
            ) ||
                (refusedStyled != default)
            ) { return $"the styled string '{text}' was accepted, or left raw {refusedStyled.Value} behind"; }
            if (
                FixedQ3232.TryParse(
                s: text.AsSpan(),
                style: Q3232DefaultParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refusedStyledSpan
            ) ||
                (refusedStyledSpan != default)
            ) { return $"the styled span '{text}' was accepted, or left raw {refusedStyledSpan.Value} behind"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ3232.Parse(
                s: text,
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing string parse accepted '{text}'"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ3232.Parse(
                s: text.AsSpan(),
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing span parse accepted '{text}'"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ3232.Parse(
                s: text,
                style: Q3232DefaultParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing styled string parse accepted '{text}'"; }
            if (!Throws<FormatException>(action: () => _ = FixedQ3232.Parse(
                s: text.AsSpan(),
                style: Q3232DefaultParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"the throwing styled span parse accepted '{text}'"; }
        }

        if (Q3232ParseAll(
            text: "-2147483648",
            provider: CultureInfo.InvariantCulture,
            expected: long.MinValue
        ) is { } minimumFailure) { return minimumFailure; }
        if (
            FixedQ3232.TryParse(
            s: "2147483648",
            provider: CultureInfo.InvariantCulture,
            result: out var overflowed
        ) ||
            (overflowed != default)
        ) { return "'2147483648' was accepted, or left a raw behind"; }
        if (
            FixedQ3232.TryParse(
            s: "2147483648".AsSpan(),
            provider: CultureInfo.InvariantCulture,
            result: out var overflowedSpan
        ) ||
            (overflowedSpan != default)
        ) { return "the span '2147483648' was accepted, or left a raw behind"; }
        if (
            FixedQ3232.TryParse(
            s: "2147483648",
            style: Q3232DefaultParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var overflowedStyled
        ) ||
            (overflowedStyled != default)
        ) { return "the styled string '2147483648' was accepted, or left a raw behind"; }
        if (
            FixedQ3232.TryParse(
            s: "2147483648".AsSpan(),
            style: Q3232DefaultParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var overflowedStyledSpan
        ) ||
            (overflowedStyledSpan != default)
        ) { return "the styled span '2147483648' was accepted, or left a raw behind"; }
        if (!Throws<OverflowException>(action: () => _ = FixedQ3232.Parse(
            s: "2147483648",
            provider: CultureInfo.InvariantCulture
        ))) { return "the throwing parse of '2147483648' did not report an overflow"; }
        if (!Throws<OverflowException>(action: () => _ = FixedQ3232.Parse(
            s: "2147483648".AsSpan(),
            provider: CultureInfo.InvariantCulture
        ))) { return "the throwing span parse of '2147483648' did not report an overflow"; }
        if (!Throws<OverflowException>(action: () => _ = FixedQ3232.Parse(
            s: "2147483648",
            style: Q3232DefaultParseStyle,
            provider: CultureInfo.InvariantCulture
        ))) { return "the throwing styled string parse of '2147483648' did not report an overflow"; }
        if (!Throws<OverflowException>(action: () => _ = FixedQ3232.Parse(
            s: "2147483648".AsSpan(),
            style: Q3232DefaultParseStyle,
            provider: CultureInfo.InvariantCulture
        ))) { return "the throwing styled span parse of '2147483648' did not report an overflow"; }

        if (
            FixedQ3232.TryParse(
            provider: null,
            result: out var fromNull,
            s: ((string?)null)
        ) ||
            (fromNull != default)
        ) { return "a null string was accepted"; }
        if (!Throws<ArgumentNullException>(
            action: () => _ = FixedQ3232.Parse(
                provider: null,
                s: ((string)null!)
            ),
            paramName: "s"
        )) { return "the throwing parse accepted a null string"; }

        return null;
    }

    // Spellings the grammar does not name: empty, blank, a bare sign, a bare point, two points, an exponent the
    // style does not admit, hexadecimal, and a word.
    private static readonly string[] Q3232RefusedTexts = ["", "   ", "-", "+", ".", "1.2.3", "1e3", "0x10", "one"];

    /// <summary>Proves the NumberStyles-taking Parse/TryParse overloads genuinely depend on their style argument for
    /// FixedQ3232: <c>"1e3"</c> is refused under the default style (no <c>AllowExponent</c> — see
    /// <see cref="Q3232RefusedTexts"/>) but accepted and quantized to exactly <c>1000</c> under
    /// <see cref="NumberStyles.Float"/>, which adds <c>AllowExponent</c>. Every other FixedQ3232 text law
    /// (<see cref="Q3232TextRoundTrip"/>, <see cref="Q3232TextRefusals"/>, <see cref="Q3232TextParseTies"/>) calls
    /// only the two-argument provider-only overloads, which forward a FIXED style internally — the manifest's
    /// "covered" mark on the four-argument overloads was earned by that forwarding alone, never by a caller-supplied
    /// style actually changing the outcome, until this law.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232StyledParseIsGenuine() {
        const string text = "1e3";
        const long expected = (1000L << FixedQ3232.FractionBitCount);

        if (
            !FixedQ3232.TryParse(
            s: text,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out var fromString
        ) ||
            (fromString.Value != expected)
        ) { return $"the styled string try-parse of '{text}' under NumberStyles.Float did not return {expected}"; }
        if (
            !FixedQ3232.TryParse(
            s: text.AsSpan(),
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out var fromSpan
        ) ||
            (fromSpan.Value != expected)
        ) { return $"the styled span try-parse of '{text}' under NumberStyles.Float did not return {expected}"; }
        if (FixedQ3232.Parse(
            s: text,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"the styled string parse of '{text}' under NumberStyles.Float did not return {expected}"; }
        if (FixedQ3232.Parse(
            s: text.AsSpan(),
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"the styled span parse of '{text}' under NumberStyles.Float did not return {expected}"; }

        // The same text is refused both through a caller-supplied style lacking AllowExponent and through all four
        // provider-only overloads, whose fixed style has the same grammar. Calling both families here keeps this law's
        // name-based Parse/TryParse declaration honest for every overload while preserving the discriminating pair.
        if (
            FixedQ3232.TryParse(
            s: text,
            style: Q3232DefaultParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var refused
        ) ||
            (refused != default)
        ) { return $"'{text}' was accepted under a style without AllowExponent"; }
        if (
            FixedQ3232.TryParse(
            s: text.AsSpan(),
            style: Q3232DefaultParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var refusedStyledSpan
        ) ||
            (refusedStyledSpan != default)
        ) { return $"the span '{text}' was accepted under a style without AllowExponent"; }
        if (
            FixedQ3232.TryParse(
            s: text,
            provider: CultureInfo.InvariantCulture,
            result: out var refusedProvider
        ) ||
            (refusedProvider != default)
        ) { return $"the provider-only string '{text}' was accepted"; }
        if (
            FixedQ3232.TryParse(
            s: text.AsSpan(),
            provider: CultureInfo.InvariantCulture,
            result: out var refusedProviderSpan
        ) ||
            (refusedProviderSpan != default)
        ) { return $"the provider-only span '{text}' was accepted"; }
        if (!Throws<FormatException>(action: () => _ = FixedQ3232.Parse(
            s: text,
            provider: CultureInfo.InvariantCulture
        ))) { return $"the provider-only throwing string parse accepted '{text}'"; }
        if (!Throws<FormatException>(action: () => _ = FixedQ3232.Parse(
            s: text.AsSpan(),
            provider: CultureInfo.InvariantCulture
        ))) { return $"the provider-only throwing span parse accepted '{text}'"; }

        return null;
    }
    /// <summary>Proves the half-ULP tie-break at FixedQ3232's own thirty-three-fraction-digit limit: a decimal string
    /// exactly at the tie between two raws rounds to the EVEN one, one just below rounds down, and one just above
    /// rounds up. <see cref="Q3232TextRoundTrip"/> alone never reaches this: every string it feeds Parse is the exact
    /// terminating expansion of an already-representable raw, so the division remainder there is always zero and the
    /// tie-break arithmetic never runs. <c>3·2⁻³³</c> is exactly thirty-three fraction decimal digits — the type's own
    /// <c>fractionBitCount + 1</c> — so no digit is discarded and the remainder lands exactly on the tie at raw
    /// <c>1.5</c>; perturbing only the thirty-third digit moves the remainder one part in <c>2·5³³</c> off the tie
    /// without touching the quotient.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232TextParseTies() {
        const string below = "0.000000000349245965480804443359374";
        const string exact = "0.000000000349245965480804443359375";
        const string above = "0.000000000349245965480804443359376";

        return
            (Q3232CheckTie(
            expectedRaw: 1,
            text: below
        ) ??
            (Q3232CheckTie(
            expectedRaw: 2,
            text: exact
        ) ??
            Q3232CheckTie(
            expectedRaw: 2,
            text: above
        )));
    }

    // Re-derives the expected raw from the string with the same shared-nothing oracle Q3232TextRoundTrip uses (never
    // through FixedQ3232.Parse), then proves every parse entry point agrees with the CALLER's expected raw — so a
    // wrong expectedRaw in the test itself, not just a wrong subject, would still be caught.
    private static string? Q3232CheckTie(string text, long expectedRaw) {
        var point = text.IndexOf(value: '.');
        var digits = string.Concat(
            str0: text.AsSpan(
                length: point,
                start: 0
            ),
            str1: text.AsSpan(start: (point + 1))
        );
        var fractionDigitCount = ((text.Length - point) - 1);

        var (inRange, quantized) = Oracles.DecimalToRaw(
            numerator: BigInteger.Parse(
                value: digits,
                provider: CultureInfo.InvariantCulture
            ),
            decimalExponent: fractionDigitCount,
            shift: FixedQ3232.FractionBitCount
        );

        if (
            !inRange ||
            (quantized != expectedRaw)
        ) { return $"the oracle quantized '{text}' as {quantized} (in range: {inRange}), not the expected {expectedRaw}"; }

        return Q3232ParseAll(
            text: text,
            provider: CultureInfo.InvariantCulture,
            expected: expectedRaw
        );
    }

    /// <summary>Proves this carrier's FixedQ4816 (Q48.16) peer conversion. Narrowing
    /// (<see cref="FixedQ3232.ToFixedQ4816"/>) is a single ties-to-even rounding at the sixteen-bit fraction
    /// difference and NEVER overflows, because Q32.32's whole range fits inside Q48.16's; widening
    /// (<see cref="FixedQ3232.FromFixedQ4816"/> / <see cref="FixedQ3232.TryFromFixedQ4816"/>) is EXACT — it only
    /// appends zero bits — but is gated by whether the source's integer part fits the thirty-two-bit range, and the
    /// widen-then-narrow round trip recovers the original Q48.16 value exactly wherever it succeeded.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232PeerConversionExact() {
        foreach (var raw in Q3232RawLadder) {
            var value = RawQ3232(value: raw);
            var narrowed = value.ToFixedQ4816();
            var expected = Oracles.RoundDyadic(
                exact: new BigInteger(value: raw),
                shift: (FixedQ3232.FractionBitCount - FixedQ4816.FractionBitCount)
            );

            if (narrowed.Value != expected) { return $"ToFixedQ4816 of raw {raw} is {narrowed.Value}, expected {expected}"; }
            if (ConvertChecked<FixedQ4816, FixedQ3232>(value: value) != narrowed) { return $"CreateChecked<FixedQ4816> disagrees with ToFixedQ4816 at raw {raw}"; }
            if (ConvertSaturating<FixedQ4816, FixedQ3232>(value: value) != narrowed) { return $"CreateSaturating<FixedQ4816> disagrees with ToFixedQ4816 at raw {raw}"; }
            if (ConvertTruncating<FixedQ4816, FixedQ3232>(value: value) != narrowed) { return $"CreateTruncating<FixedQ4816> disagrees with ToFixedQ4816 at raw {raw}"; }
        }

        // The general ladder has no exact half after the sixteen-bit shift, so it cannot distinguish ties-to-even
        // from either half-up or half-down. These six rows pin both even and odd ties in both sign directions.
        foreach (var (raw, expected) in Q3232PeerNarrowingTieLadder) {
            var narrowed = RawQ3232(value: raw).ToFixedQ4816();

            if (narrowed.Value != expected) { return $"ToFixedQ4816 tie raw {raw} is {narrowed.Value}, expected {expected}"; }
        }

        foreach (var raw in FixedQ4816RawLadderForQ3232Peer) {
            var source = FixedQ4816.FromRawBits(value: raw);
            var widened = (new BigInteger(value: raw) << (FixedQ3232.FractionBitCount - FixedQ4816.FractionBitCount));
            var inRange = ((widened >= long.MinValue) && (widened <= long.MaxValue));
            var succeeded = FixedQ3232.TryFromFixedQ4816(
                result: out var tried,
                value: source
            );

            if (succeeded != inRange) { return $"TryFromFixedQ4816 of raw {raw} reported {succeeded}, expected {inRange}"; }

            var saturatedExpected = ((widened < long.MinValue)
                ? long.MinValue
                : ((widened > long.MaxValue)
                    ? long.MaxValue
                    : ((long)widened)
            ));
            var truncatingExpected = unchecked((long)((ulong)(widened & ulong.MaxValue)));

            if (ConvertSaturating<FixedQ3232, FixedQ4816>(value: source).Value != saturatedExpected) { return $"CreateSaturating<FixedQ3232> of raw {raw} did not clamp the exact widened value"; }
            if (ConvertTruncating<FixedQ3232, FixedQ4816>(value: source).Value != truncatingExpected) { return $"CreateTruncating<FixedQ3232> of raw {raw} did not keep the widened value's low sixty-four bits"; }

            if (inRange) {
                if (tried.Value != widened) { return $"TryFromFixedQ4816 of raw {raw} is {tried.Value}, expected {widened}"; }
                if (FixedQ3232.FromFixedQ4816(value: source).Value != widened) { return $"FromFixedQ4816 of raw {raw} is wrong"; }
                if (ConvertChecked<FixedQ3232, FixedQ4816>(value: source).Value != widened) { return $"CreateChecked<FixedQ3232> of raw {raw} is wrong"; }
                if (tried.ToFixedQ4816() != source) { return $"the widen-then-narrow round trip of raw {raw} did not recover the original Q48.16 value"; }
            } else {
                if (tried != default) { return $"TryFromFixedQ4816 of raw {raw} left a non-default result behind on failure"; }
                if (!Throws<OverflowException>(action: () => _ = FixedQ3232.FromFixedQ4816(value: source))) { return $"FromFixedQ4816 of raw {raw} did not throw on overflow"; }
                if (!Throws<OverflowException>(action: () => _ = ConvertChecked<FixedQ3232, FixedQ4816>(value: source))) { return $"CreateChecked<FixedQ3232> of raw {raw} did not throw on overflow"; }
            }
        }

        return null;
    }

    // FixedQ4816 raws spanning the Q32.32 integer boundary: comfortably inside, exactly at both edges, one raw past
    // both edges, and a scattering of ordinary and wildly out-of-range values.
    private static readonly long[] FixedQ4816RawLadderForQ3232Peer = [
        0L, 65536L, -65536L,
        (2147483647L << 16), ((2147483647L << 16) | 0xFFFFL),
        (-2147483648L << 16),
        ((-2147483648L << 16) - 1L),
        (2147483648L << 16),
        long.MaxValue, long.MinValue,
        (1L << 47), -(1L << 47),
    ];
    private static readonly (long Raw, long Expected)[] Q3232PeerNarrowingTieLadder = [
        (0x0000000000008000L, 0L),
        (0x0000000000018000L, 2L),
        (0x0000000000028000L, 2L),
        (-0x0000000000008000L, 0L),
        (-0x0000000000018000L, -2L),
        (-0x0000000000028000L, -2L),
    ];

    /// <summary>Proves the three <c>INumberBase</c> conversion modes through a <c>decimal</c> source into
    /// <see cref="FixedQ3232"/> — a second public route to <c>FixedPointConvert.ScaleDecimalWide</c>, alongside
    /// <see cref="Q1648DecimalConversionModes"/>: <see cref="GenericConversionModes"/> exercises the same three
    /// modes but only ever targets <see cref="FixedQ4816"/>/<see cref="UFixedQ4816"/>, both of which route decimal
    /// through the narrower <c>ScaleDecimal</c> instead, since their sixteen fraction bits sit under its
    /// thirty-one-bit cap where FixedQ3232's thirty-two do not. <c>decimal.MaxValue</c>'s exact scaled value at
    /// Q32.32 is independently derived here from its own documented bit layout (a scale-zero, <c>2⁹⁶ − 1</c>
    /// mantissa) rather than through <c>ScaleDecimalWide</c> itself: checked must refuse it, saturating must clamp
    /// to <see cref="FixedQ3232.MaxValue"/>, and truncating must keep exactly its low sixty-four bits — raw
    /// <c>0xFFFFFFFF00000000</c>, signed <c>−4294967296</c>. <c>decimal.MinValue</c> mirrors it by sign, and an
    /// in-range decimals exercise exact scaling and both directions of non-tie rounding where all three modes must
    /// agree. Decimal cannot name a half-raw tie here: its scale is at most twenty-eight, so the Q32 multiplication
    /// cancels every factor of two in its denominator and leaves an odd denominator.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Q3232DecimalConversionModes() {
        const long expectedTruncatingAtMax = unchecked((long)0xFFFFFFFF00000000UL);
        var scaledMax = ScaledDecimalAt(
            fractionBitCount: FixedQ3232.FractionBitCount,
            value: decimal.MaxValue
        );

        if (scaledMax != (((BigInteger.One << 96) - BigInteger.One) << FixedQ3232.FractionBitCount)) { return "the independently-derived scaled value of decimal.MaxValue does not match its hand-derived bit layout"; }

        if (!Throws<OverflowException>(action: () => _ = ConvertChecked<FixedQ3232, decimal>(value: decimal.MaxValue))) { return "CreateChecked accepted decimal.MaxValue instead of refusing it"; }

        var saturatedMax = ConvertSaturating<FixedQ3232, decimal>(value: decimal.MaxValue);

        if (saturatedMax != FixedQ3232.MaxValue) { return $"CreateSaturating of decimal.MaxValue is {saturatedMax}, expected MaxValue"; }

        var truncatedMax = ConvertTruncating<FixedQ3232, decimal>(value: decimal.MaxValue).Value;

        if (truncatedMax != expectedTruncatingAtMax) { return $"CreateTruncating of decimal.MaxValue is raw {truncatedMax}, expected raw {expectedTruncatingAtMax}"; }

        if (!Throws<OverflowException>(action: () => _ = ConvertChecked<FixedQ3232, decimal>(value: decimal.MinValue))) { return "CreateChecked accepted decimal.MinValue instead of refusing it"; }

        var saturatedMin = ConvertSaturating<FixedQ3232, decimal>(value: decimal.MinValue);

        if (saturatedMin != FixedQ3232.MinValue) { return $"CreateSaturating of decimal.MinValue is {saturatedMin}, expected MinValue"; }

        var truncatedMin = ConvertTruncating<FixedQ3232, decimal>(value: decimal.MinValue).Value;

        if (truncatedMin != unchecked(-expectedTruncatingAtMax)) { return $"CreateTruncating of decimal.MinValue is raw {truncatedMin}, expected raw {unchecked(-expectedTruncatingAtMax)}"; }

        // An in-range decimal: all three modes must agree with each other and with the independent oracle.
        var inRangeExpected = ((long)ScaledDecimalAt(
            fractionBitCount: FixedQ3232.FractionBitCount,
            value: 1.5m
        ));
        var exact = ConvertChecked<FixedQ3232, decimal>(value: 1.5m).Value;
        var saturatedInRange = ConvertSaturating<FixedQ3232, decimal>(value: 1.5m).Value;
        var truncatedInRange = ConvertTruncating<FixedQ3232, decimal>(value: 1.5m).Value;

        if (
            (exact != inRangeExpected) ||
            (saturatedInRange != inRangeExpected) ||
            (truncatedInRange != inRangeExpected)
        ) { return $"the three modes disagree on the in-range decimal 1.5: checked={exact}, saturating={saturatedInRange}, truncating={truncatedInRange}, expected {inRangeExpected}"; }

        foreach (var value in ((ReadOnlySpan<decimal>)[0.1m, -0.1m, 0.2m, -0.2m, 0.3m, -0.3m])) {
            var expected = ((long)ScaledDecimalAt(
                fractionBitCount: FixedQ3232.FractionBitCount,
                value: value
            ));
            var checkedValue = ConvertChecked<FixedQ3232, decimal>(value: value).Value;
            var saturatedValue = ConvertSaturating<FixedQ3232, decimal>(value: value).Value;
            var truncatedValue = ConvertTruncating<FixedQ3232, decimal>(value: value).Value;

            if (
                (checkedValue != expected) ||
                (saturatedValue != expected) ||
                (truncatedValue != expected)
            ) { return $"the three modes disagree on the rounded in-range decimal {value}: checked={checkedValue}, saturating={saturatedValue}, truncating={truncatedValue}, expected {expected}"; }
        }

        return null;
    }

    // ---- the exponent-compensation seam: an exponent no fixed cap can bound ----

    // The exponent grammar the default overloads do not admit, plus the leading sign the negative mirror needs.
    private const NumberStyles ExponentParseStyle = NumberStyles.AllowLeadingSign |
                                                      NumberStyles.AllowDecimalPoint |
                                                      NumberStyles.AllowExponent;
    // Long enough that a fixed one-million cap saturates: the leading fractional zeros compensate the exponent exactly.
    private const int CompensationZeroCount = 1_000_000;

    // The platform's accepted value, decomposed EXACTLY from its own bits. The invariant under test is that a
    // successful parse names the number the validation pass accepted, so the validator's value is the reference; the
    // ninety-six-bit mantissa and the scale byte are read straight out of decimal.GetBits and never re-rendered.
    private static (BigInteger Numerator, int DecimalExponent) DecimalParts(decimal value) {
        Span<int> bits = stackalloc int[4];

        _ = decimal.GetBits(
            d: value,
            destination: bits
        );

        var magnitude = new BigInteger(value: ((uint)bits[0])) |
                         (new BigInteger(value: ((uint)bits[1])) << 32) |
                         (new BigInteger(value: ((uint)bits[2])) << 64);

        return (((bits[3] < 0)
            ? -magnitude
            : magnitude), (bits[3] >> 16) & 0xFF);
    }
    // The unsigned face of Oracles.DecimalToRaw: one ties-to-even rounding of the exact rational, then the unsigned
    // carrier's own range verdict.
    private static (bool InRange, ulong Raw) UnsignedDecimalToRaw(BigInteger numerator, int decimalExponent) {
        var exact = Oracles.RoundRationalTiesToEven(
            numerator: (numerator << UFixedQ4816.FractionBitCount),
            denominator: BigInteger.Pow(
                value: new BigInteger(value: 10),
                exponent: decimalExponent
            )
        );
        var inRange = ((exact >= BigInteger.Zero) && (exact <= new BigInteger(value: ulong.MaxValue)));

        return (inRange, (inRange
            ? ((ulong)exact)
            : 0UL));
    }
    // All four styled signed entry points on one text: string and span, throwing and trying.
    private static string? FixedStyledParseAll(string label, string text, bool inRange, long expected) {
        if (!inRange) {
            if (
                FixedQ4816.TryParse(
                s: text,
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refusedString
            ) ||
                (refusedString != default)
            ) { return $"{label}: the signed string try-parse accepted it, or left raw {refusedString.Value} behind"; }
            if (
                FixedQ4816.TryParse(
                s: text.AsSpan(),
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refusedSpan
            ) ||
                (refusedSpan != default)
            ) { return $"{label}: the signed span try-parse accepted it, or left raw {refusedSpan.Value} behind"; }
            if (!Throws<OverflowException>(action: () => _ = FixedQ4816.Parse(
                s: text,
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"{label}: the signed throwing string parse did not report an overflow"; }
            if (!Throws<OverflowException>(action: () => _ = FixedQ4816.Parse(
                s: text.AsSpan(),
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"{label}: the signed throwing span parse did not report an overflow"; }

            return null;
        }

        if (FixedQ4816.Parse(
            s: text,
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"{label}: the signed string parse did not return {expected}"; }
        if (FixedQ4816.Parse(
            s: text.AsSpan(),
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"{label}: the signed span parse did not return {expected}"; }
        if (
            !FixedQ4816.TryParse(
            s: text,
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var fromString
        ) ||
            (fromString.Value != expected)
        ) { return $"{label}: the signed string try-parse did not return {expected}"; }
        if (
            !FixedQ4816.TryParse(
            s: text.AsSpan(),
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var fromSpan
        ) ||
            (fromSpan.Value != expected)
        ) { return $"{label}: the signed span try-parse did not return {expected}"; }

        return null;
    }
    // The unsigned mirror of FixedStyledParseAll.
    private static string? UnsignedStyledParseAll(string label, string text, bool inRange, ulong expected) {
        if (!inRange) {
            if (
                UFixedQ4816.TryParse(
                s: text,
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refusedString
            ) ||
                (refusedString != default)
            ) { return $"{label}: the unsigned string try-parse accepted it, or left raw {refusedString.Value} behind"; }
            if (
                UFixedQ4816.TryParse(
                s: text.AsSpan(),
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var refusedSpan
            ) ||
                (refusedSpan != default)
            ) { return $"{label}: the unsigned span try-parse accepted it, or left raw {refusedSpan.Value} behind"; }
            if (!Throws<OverflowException>(action: () => _ = UFixedQ4816.Parse(
                s: text,
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"{label}: the unsigned throwing string parse did not report an overflow"; }
            if (!Throws<OverflowException>(action: () => _ = UFixedQ4816.Parse(
                s: text.AsSpan(),
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture
            ))) { return $"{label}: the unsigned throwing span parse did not report an overflow"; }

            return null;
        }

        if (UFixedQ4816.Parse(
            s: text,
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"{label}: the unsigned string parse did not return {expected}"; }
        if (UFixedQ4816.Parse(
            s: text.AsSpan(),
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture
        ).Value != expected) { return $"{label}: the unsigned span parse did not return {expected}"; }
        if (
            !UFixedQ4816.TryParse(
            s: text,
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var fromString
        ) ||
            (fromString.Value != expected)
        ) { return $"{label}: the unsigned string try-parse did not return {expected}"; }
        if (
            !UFixedQ4816.TryParse(
            s: text.AsSpan(),
            style: ExponentParseStyle,
            provider: CultureInfo.InvariantCulture,
            result: out var fromSpan
        ) ||
            (fromSpan.Value != expected)
        ) { return $"{label}: the unsigned span try-parse did not return {expected}"; }

        return null;
    }

    /// <summary>Proves that an exponent whose magnitude exceeds any fixed cap never changes the value of syntax the
    /// validation pass already accepted: a million leading fractional zeros compensate a million-and-one exponent back
    /// onto exactly one, the mirrors and the ranges resolve as the platform's own accepted value does, and the
    /// exponents that cannot be compensated saturate into the overflow and underflow verdicts they would reach
    /// unsaturated.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? StyledExponentCompensation() {
        var zeros = new string(
            c: '0',
            count: CompensationZeroCount
        );
        var compensatedOne = $"0.{zeros}1e1000001";
        (string Label, string Text, bool SignedInRange, long SignedRaw, bool UnsignedInRange, ulong UnsignedRaw)[] ladder = [
            ("the compensated one", compensatedOne, true, 65536L, true, 65536UL),
            ("the compensated negative one", $"-{compensatedOne}", true, -65536L, false, 0UL),
            ("the compensated one half", $"0.{zeros}5e1000000", true, 32768L, true, 32768UL),
            ("the compensated 10¹⁵, just past the range", $"0.{zeros}1e1000016", false, 0L, false, 0UL),
            ("the uncompensated positive exponent", "1e1000001", false, 0L, false, 0UL),
            ("the uncompensated negative exponent", "1e-1000001", true, 0L, true, 0UL),
            ("an exponent longer than the carrier", "1e-9999999999999999999999999", true, 0L, true, 0UL),
            ("a zero significand at a huge exponent", "0e1000001", true, 0L, true, 0UL),
        ];

        foreach (var (label, text, signedInRange, signedRaw, unsignedInRange, unsignedRaw) in ladder) {
            var validated = decimal.TryParse(
                s: text,
                style: ExponentParseStyle,
                provider: CultureInfo.InvariantCulture,
                result: out var platform
            );

            if (validated) {
                var (numerator, decimalExponent) = DecimalParts(value: platform);
                var (oracleInRange, oracleRaw) = Oracles.DecimalToRaw(
                    decimalExponent: decimalExponent,
                    numerator: numerator,
                    shift: FixedQ4816.FractionBitCount
                );

                if (oracleInRange != signedInRange) {
                    return $"{label}: the oracle {(oracleInRange
                    ? "admitted"
                    : "refused")} the validated value, expected the opposite";
                }
                if (
                    signedInRange &&
                    (oracleRaw != signedRaw)
                ) { return $"{label}: the oracle quantized the validated value to {oracleRaw}, expected {signedRaw}"; }

                var (unsignedOracleInRange, unsignedOracleRaw) = UnsignedDecimalToRaw(
                    decimalExponent: decimalExponent,
                    numerator: numerator
                );

                if (unsignedOracleInRange != unsignedInRange) {
                    return $"{label}: the unsigned oracle {(unsignedOracleInRange
                    ? "admitted"
                    : "refused")} the validated value, expected the opposite";
                }
                if (
                    unsignedInRange &&
                    (unsignedOracleRaw != unsignedRaw)
                ) { return $"{label}: the unsigned oracle quantized the validated value to {unsignedOracleRaw}, expected {unsignedRaw}"; }
            } else if (
                signedInRange ||
                unsignedInRange
            ) {
                return $"{label}: the platform validator refused syntax this ladder expects to be accepted";
            }

            if (FixedStyledParseAll(
                expected: signedRaw,
                inRange: signedInRange,
                label: label,
                text: text
            ) is { } signedDetail) { return signedDetail; }
            if (UnsignedStyledParseAll(
                expected: unsignedRaw,
                inRange: unsignedInRange,
                label: label,
                text: text
            ) is { } unsignedDetail) { return unsignedDetail; }
        }

        return null;
    }
    // ---- CyclicRotation: pinning PlaneCount itself, not merely looping up to it ----

    /// <summary>Pins <see cref="CyclicRotation.PlaneCount"/> at exactly four by an independent derivation from the
    /// E8 Coxeter element's decomposition, rather than merely looping planes up to whatever the constant says (the
    /// gap scalar.cyclic-rotation-closes-its-loop's own waiver named: that law sweeps <c>plane in [0, PlaneCount)</c>,
    /// so a wrong count would shorten its sweep and still pass). <see cref="CyclicRotation.Period"/> is the order of
    /// the Coxeter element; its REDUCED RESIDUE SYSTEM — the residues in <c>[1, Period)</c> coprime to it, found here
    /// by a from-scratch Euclidean gcd that calls no <c>Puck.Maths</c> member — splits into conjugate pairs
    /// <c>{m, Period − m}</c> (a complex eigenvalue and its conjugate collapsing to one REAL rotation plane), and the
    /// number of those pairs is exactly what fixes the plane count: thirty's reduced residue system is the
    /// eight-element set <c>{1, 7, 11, 13, 17, 19, 23, 29}</c>, splitting into exactly four pairs. The claim reads
    /// each plane's speed off the PUBLIC <see cref="CyclicRotation.Step(int, long)"/> at tick one (phase one, so the
    /// step IS the plane's speed, no table lookup involved) and checks that the declared planes name exactly one
    /// representative of every conjugate pair, with none left over and none repeated: a <c>PlaneCount</c> of three
    /// would leave a pair unaccounted for, and a <c>PlaneCount</c> of five would index a step past the private
    /// four-entry speed table and throw.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CyclicRotationPlaneCountIsCoxeterConjugacyPairCount() {
        var period = CyclicRotation.Period;
        var reducedResidues = new List<int>();

        for (var candidate = 1; (candidate < period); ++candidate) {
            if (1 == Gcd(
                a: candidate,
                b: period
            )) { reducedResidues.Add(item: candidate); }
        }

        if ((reducedResidues.Count % 2) != 0) {
            return $"the reduced residue system of {period} has {reducedResidues.Count} elements, which is odd and cannot split into conjugate pairs";
        }

        var planeCount = CyclicRotation.PlaneCount;
        var accountedFor = new HashSet<int>();

        for (var plane = 0; (plane < planeCount); ++plane) {
            var speed = CyclicRotation.Step(
                plane: plane,
                tick: 1L
            );

            if (!reducedResidues.Contains(item: speed)) { return $"plane {plane}'s speed {speed} is not coprime to the period {period}"; }

            var conjugate = (period - speed);

            if (
                !accountedFor.Add(item: speed) ||
                !accountedFor.Add(item: conjugate)
            ) {
                return $"plane {plane}'s speed {speed} (or its conjugate {conjugate}) was already claimed by another plane — the declared planes do not name DISTINCT conjugate pairs";
            }
        }

        if (accountedFor.Count != reducedResidues.Count) {
            return $"the {planeCount} declared planes account for {accountedFor.Count} of the {reducedResidues.Count} residues coprime to {period}; PlaneCount should be {(reducedResidues.Count / 2)}";
        }

        return null;
    }

    // The Euclidean algorithm, written from scratch for the claim above: no Puck.Maths call, and no line shared with
    // CyclicRotation's own PlaneSpeeds table or its Step/FloorModulo path.
    private static int Gcd(int a, int b) {
        while (0 != b) {
            (a, b) = (b, (a % b));
        }

        return a;
    }
}
