using System.Globalization;
using System.Numerics;
using LeafOctonion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- carrier scalars (UnitFraction16, UnitFraction32), the half-open unit fractions ----

    /// <summary>Maps a sampled signed raw onto a legal UQ0.16 raw: its low sixteen bits. The carrier IS those bits, so the
    /// fold is TOTAL and unbiased — every raw of the type is reachable and none is preferred — unlike the closed interval's,
    /// which must saturate to reach its thirty-third bit. Subject and oracle apply the identical map.</summary>
    private static ushort UnitFraction16Raw(long raw) =>
        unchecked((ushort)raw);
    /// <summary>Maps a sampled signed raw onto a legal UQ0.32 raw: its low thirty-two bits, on the same total fold.</summary>
    private static uint UnitFraction32Raw(long raw) =>
        unchecked((uint)raw);
    private static UnitFraction16 Fraction16(long raw) =>
        UnitFraction16.FromRawBits(value: UnitFraction16Raw(raw: raw));
    private static UnitFraction32 Fraction32(long raw) =>
        UnitFraction32.FromRawBits(value: UnitFraction32Raw(raw: raw));
    /// <summary>Maps a sampled pair onto a dividend and a divisor: the SMALLER raw over the LARGER, with a zero divisor
    /// substituted by one unit. Both draws are uniform over the whole carrier, so an unordered fold would spend half the
    /// sweep at a quotient of one or more, where the answer saturates and the rounding rule never fires; ordering keeps the
    /// saturating corner (equal raws divide to exactly one and still saturate, and the edge square's diagonal reaches it on
    /// every run) and spends the rest of the sweep in the regime the correction lives in. A zero divisor has no quotient at
    /// all — it is the documented-by-code throw site pinned by the refusal ladder rather than a value law's business.
    /// Subject and oracle apply the identical map.</summary>
    private static (ushort Dividend, ushort Divisor) UnitFraction16Ratio(long a, long b) {
        var first = UnitFraction16Raw(raw: a);
        var second = UnitFraction16Raw(raw: b);
        var divisor = Math.Max(
            val1: first,
            val2: second
        );

        return (Math.Min(
            val1: first,
            val2: second
        ), ((0 == divisor)
            ? ((ushort)1)
            : divisor));
    }
    /// <summary>The UQ0.32 sibling of <see cref="UnitFraction16Ratio"/>.</summary>
    private static (uint Dividend, uint Divisor) UnitFraction32Ratio(long a, long b) {
        var first = UnitFraction32Raw(raw: a);
        var second = UnitFraction32Raw(raw: b);
        var divisor = Math.Max(
            val1: first,
            val2: second
        );

        return (Math.Min(
            val1: first,
            val2: second
        ), ((0U == divisor)
            ? 1U
            : divisor));
    }
    /// <summary>Folds a sampled raw onto a shift amount in <c>[−32, 63]</c>, so the sweep visits the negative amounts and
    /// the amounts at and beyond the thirty-two-bit shift-count mask as well as the ordinary ones.</summary>
    private static int UnitFractionShiftAmount(long raw) =>
        (((int)(unchecked((ulong)raw) % 96UL)) - 32);
    /// <summary>The two's-complement wrap of an exact value onto a width's raw space, for the inline expectations the
    /// fraction claims compute.</summary>
    private static BigInteger WrapToFraction(BigInteger value, int fractionBitCount) {
        var scale = (BigInteger.One << fractionBitCount);

        return (((value % scale) + scale) % scale);
    }
    /// <summary>Builds the two derived texts a text sweep parses beside the exact rendering: the rendering truncated after
    /// a sample-selected number of fraction digits, and the rendering with one more digit appended. Neither lands on the
    /// grid in general, so both exercise the parser's rounding rather than its round trip, and the appended form reaches
    /// the out-of-range refusal near the top of the interval.</summary>
    private static string[] UnitFractionDerivedTexts(string rendering, ulong raw) {
        var point = rendering.IndexOf(value: '.');
        var digit = ((char)('0' + ((int)(raw % 10UL))));

        if (0 > point) {
            return [$"0.{digit}", $"00{rendering}"];
        }

        var fractionLength = ((rendering.Length - point) - 1);
        var keep = ((point + 2) + ((int)(raw % ((ulong)fractionLength))));

        return [rendering[..keep], (rendering + digit)];
    }

    /// <summary>The subject <see cref="UnitFraction16"/> multiply, sampled raw in and raw out.</summary>
    public static long UnitFraction16Multiply(long a, long b) =>
        ((long)(Fraction16(raw: a) * Fraction16(raw: b)).Value);
    /// <summary>The oracle for the <see cref="UnitFraction16"/> multiply — one ties-to-even rounding of the exact product
    /// at the <c>2⁻¹⁶</c> grid.</summary>
    public static long UnitFraction16MultiplyOracle(long a, long b) =>
        ((long)Oracles.UnitFractionProduct(
            x: UnitFraction16Raw(raw: a),
            y: UnitFraction16Raw(raw: b),
            fractionBitCount: UnitFraction16.FractionBitCount
        ));
    /// <summary>The subject <see cref="UnitFraction32"/> multiply, sampled raw in and raw out.</summary>
    public static long UnitFraction32Multiply(long a, long b) =>
        ((long)(Fraction32(raw: a) * Fraction32(raw: b)).Value);
    /// <summary>The oracle for the <see cref="UnitFraction32"/> multiply — one ties-to-even rounding of the exact product
    /// at the <c>2⁻³²</c> grid.</summary>
    public static long UnitFraction32MultiplyOracle(long a, long b) =>
        ((long)Oracles.UnitFractionProduct(
            x: UnitFraction32Raw(raw: a),
            y: UnitFraction32Raw(raw: b),
            fractionBitCount: UnitFraction32.FractionBitCount
        ));
    /// <summary>The subject <see cref="UnitFraction16"/> divide, on the ordered non-zero-divisor fold.</summary>
    public static long UnitFraction16Divide(long a, long b) {
        var (dividend, divisor) = UnitFraction16Ratio(
            a: a,
            b: b
        );

        return ((long)(UnitFraction16.FromRawBits(value: dividend) / UnitFraction16.FromRawBits(value: divisor)).Value);
    }
    /// <summary>The oracle for the <see cref="UnitFraction16"/> divide — one ties-to-even rounding of the exact ratio,
    /// clamped at <see cref="UnitFraction16.MaxValue"/>.</summary>
    public static long UnitFraction16DivideOracle(long a, long b) {
        var (dividend, divisor) = UnitFraction16Ratio(
            a: a,
            b: b
        );

        return ((long)Oracles.UnitFractionQuotient(
            fractionBitCount: UnitFraction16.FractionBitCount,
            x: dividend,
            y: divisor
        ));
    }
    /// <summary>The subject <see cref="UnitFraction32"/> divide, on the ordered non-zero-divisor fold.</summary>
    public static long UnitFraction32Divide(long a, long b) {
        var (dividend, divisor) = UnitFraction32Ratio(
            a: a,
            b: b
        );

        return ((long)(UnitFraction32.FromRawBits(value: dividend) / UnitFraction32.FromRawBits(value: divisor)).Value);
    }
    /// <summary>The oracle for the <see cref="UnitFraction32"/> divide — one ties-to-even rounding of the exact ratio,
    /// clamped at <see cref="UnitFraction32.MaxValue"/>.</summary>
    public static long UnitFraction32DivideOracle(long a, long b) {
        var (dividend, divisor) = UnitFraction32Ratio(
            a: a,
            b: b
        );

        return ((long)Oracles.UnitFractionQuotient(
            fractionBitCount: UnitFraction32.FractionBitCount,
            x: dividend,
            y: divisor
        ));
    }

    /// <summary>The UQ0.32 divisor fold WITHOUT the min/max ordering: the operands are taken as sampled and only a zero
    /// divisor is substituted, so the quotient is free to exceed one and the saturating clamp is reached on live
    /// operands rather than on a hand ladder alone. Subject and oracle apply the identical map.</summary>
    private static (uint Dividend, uint Divisor) UnitFraction32UnorderedRatio(long a, long b) {
        var divisor = UnitFraction32Raw(raw: b);

        return (UnitFraction32Raw(raw: a), ((0U == divisor)
            ? 1U
            : divisor));
    }

    /// <summary>The subject <see cref="UnitFraction32"/> divide on the unordered fold.</summary>
    public static long UnitFraction32DivideUnordered(long a, long b) {
        var (dividend, divisor) = UnitFraction32UnorderedRatio(
            a: a,
            b: b
        );

        return ((long)(UnitFraction32.FromRawBits(value: dividend) / UnitFraction32.FromRawBits(value: divisor)).Value);
    }
    /// <summary>The oracle for the unordered fold — one ties-to-even rounding of the exact ratio, then the clamp.</summary>
    public static long UnitFraction32DivideUnorderedOracle(long a, long b) {
        var (dividend, divisor) = UnitFraction32UnorderedRatio(
            a: a,
            b: b
        );

        return ((long)Oracles.UnitFractionQuotient(
            fractionBitCount: UnitFraction32.FractionBitCount,
            x: dividend,
            y: divisor
        ));
    }
    /// <summary>Proves the projection to <see cref="double"/> is EXACT at every swept raw: the bit pattern the conversion
    /// produces is the one the IEEE-754 layout demands for <c>raw / 2¹⁶</c>, which is representable because the raw carries
    /// at most sixteen significant bits. The comparison is on the encodings, so no floating-point arithmetic enters the
    /// law.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction16DoubleProjectionExact(long[] left, long[] right) {
        foreach (var sample in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var raw = UnitFraction16Raw(raw: sample);
            var projected = BitConverter.DoubleToUInt64Bits(value: ((double)UnitFraction16.FromRawBits(value: raw)));
            var expected = Oracles.ExactBinary64Bits(
                numerator: new BigInteger(value: raw),
                shift: UnitFraction16.FractionBitCount
            );

            if (projected != expected) { return $"the projection of raw {raw} encoded as {projected:X16}, expected {expected:X16}"; }

            // The projection is exact, so the double seam is a round trip in this direction at every raw — which separates
            // FromDouble's saturation at the top of the interval from an error inside it.
            if (UnitFraction16.FromDouble(value: ((double)UnitFraction16.FromRawBits(value: raw))) != UnitFraction16.FromRawBits(value: raw)) { return $"the double round trip failed at raw {raw}"; }
        }

        return null;
    }
    /// <summary>Proves the projection to <see cref="double"/> is EXACT at every swept raw: the bit pattern the conversion
    /// produces is the one the IEEE-754 layout demands for <c>raw / 2³²</c>, which is representable because the raw carries
    /// at most thirty-two significant bits. The comparison is on the encodings, so no floating-point arithmetic enters the
    /// law.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32DoubleProjectionExact(long[] left, long[] right) {
        foreach (var sample in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var raw = UnitFraction32Raw(raw: sample);
            var projected = BitConverter.DoubleToUInt64Bits(value: ((double)UnitFraction32.FromRawBits(value: raw)));
            var expected = Oracles.ExactBinary64Bits(
                numerator: new BigInteger(value: raw),
                shift: UnitFraction32.FractionBitCount
            );

            if (projected != expected) { return $"the projection of raw {raw} encoded as {projected:X16}, expected {expected:X16}"; }

            if (UnitFraction32.FromDouble(value: ((double)UnitFraction32.FromRawBits(value: raw))) != UnitFraction32.FromRawBits(value: raw)) { return $"the double round trip failed at raw {raw}"; }
        }

        return null;
    }
    /// <summary>Proves every EXACT operation of the UQ0.16 surface at every swept pair — the wrapping ring, the bitwise
    /// lattice, the raw remainder, the two saturating operations, the two order selections and the clamp — against
    /// arbitrary-width arithmetic, and that the order the comparisons report is the order of the raws. The complement's De
    /// Morgan pair against the two selections is pinned here too, at the carrier.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction16ExactOpsAndOrder(long[] left, long[] right) {
        const int bits = UnitFraction16.FractionBitCount;

        var rawA = UnitFraction16Raw(raw: left[0]);
        var rawB = UnitFraction16Raw(raw: right[0]);
        var rawC = UnitFraction16Raw(raw: (left[0] >> bits));
        var a = new UnitFraction16(Value: rawA);
        var b = UnitFraction16.FromRawBits(value: rawB);
        var c = UnitFraction16.FromRawBits(value: rawC);
        var divisor = ((0 == rawB)
            ? ((ushort)1)
            : rawB
        );
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var exactC = new BigInteger(value: rawC);
        var maximum = ((BigInteger.One << bits) - BigInteger.One);

        // The primary constructor and the raw factory are one fact, and the raw reads back unmoved.
        if (a.Value != rawA) { return $"the constructor moved raw {rawA} to {a.Value}"; }
        if (a != UnitFraction16.FromRawBits(value: rawA)) { return $"the raw factory disagrees with the constructor at {rawA}"; }

        // The wrapping ring.
        if ((a + b).Value != WrapToFraction(
            fractionBitCount: bits,
            value: (exactA + exactB)
        )) { return $"the wrapping sum of {rawA} and {rawB} is wrong"; }
        if ((a - b).Value != WrapToFraction(
            fractionBitCount: bits,
            value: (exactA - exactB)
        )) { return $"the wrapping difference of {rawA} and {rawB} is wrong"; }
        if ((-a).Value != WrapToFraction(
            fractionBitCount: bits,
            value: -exactA
        )) { return $"the modular negation of {rawA} is wrong"; }
        if ((~a).Value != (maximum - exactA)) { return $"the bitwise complement of {rawA} is wrong"; }

        // The bitwise lattice and the raw remainder, which IS the fixed-point remainder.
        if ((a & b).Value != (exactA & exactB)) { return $"the bitwise and of {rawA} and {rawB} is wrong"; }
        if ((a | b).Value != (exactA | exactB)) { return $"the bitwise or of {rawA} and {rawB} is wrong"; }
        if ((a ^ b).Value != (exactA ^ exactB)) { return $"the bitwise xor of {rawA} and {rawB} is wrong"; }
        if ((a % UnitFraction16.FromRawBits(value: divisor)).Value != (exactA % divisor)) { return $"the remainder of {rawA} by {divisor} is wrong"; }

        // The saturating pair, the order selections, and the clamp against the sampled pair as bounds.
        if (UnitFraction16.AddSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: (exactA + exactB),
            right: maximum
        )) { return $"the saturating sum of {rawA} and {rawB} is wrong"; }
        if (UnitFraction16.SubtractSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: (exactA - exactB),
            right: BigInteger.Zero
        )) { return $"the saturating difference of {rawA} and {rawB} is wrong"; }
        if (UnitFraction16.Max(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: exactA,
            right: exactB
        )) { return $"the maximum of {rawA} and {rawB} is wrong"; }
        if (UnitFraction16.Min(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: exactA,
            right: exactB
        )) { return $"the minimum of {rawA} and {rawB} is wrong"; }
        if (UnitFraction16.Clamp(
            value: c,
            minimum: UnitFraction16.Min(
                x: a,
                y: b
            ),
            maximum: UnitFraction16.Max(
                x: a,
                y: b
            )
        ).Value
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
        if ((a + UnitFraction16.Zero) != a) { return $"zero is not neutral for the wrapping sum at {rawA}"; }
        if ((a + UnitFraction16.AdditiveIdentity) != a) { return $"the additive identity is not neutral at {rawA}"; }
        if ((a - a) != UnitFraction16.Zero) { return $"a value is not its own additive inverse at {rawA}"; }
        if ((a ^ a) != UnitFraction16.Zero) { return $"the exclusive or is not self-annihilating at {rawA}"; }
        if (~(~a) != a) { return $"the bitwise complement is not an involution at {rawA}"; }
        if (-(-a) != a) { return $"the modular negation is not an involution at {rawA}"; }
        if (UnitFraction16.AddSaturating(
            x: a,
            y: UnitFraction16.Zero
        ) != a) { return $"zero is not neutral for the saturating sum at {rawA}"; }
        if (UnitFraction16.SubtractSaturating(
            x: a,
            y: UnitFraction16.Zero
        ) != a) { return $"zero is not neutral for the saturating difference at {rawA}"; }
        if (UnitFraction16.AddSaturating(
            x: a,
            y: UnitFraction16.MaxValue
        ) != UnitFraction16.MaxValue) { return $"the saturating sum does not stop at the top at {rawA}"; }
        if (UnitFraction16.SubtractSaturating(
            x: UnitFraction16.MinValue,
            y: a
        ) != UnitFraction16.MinValue) { return $"the saturating difference does not stop at zero at {rawA}"; }
        if (UnitFraction16.AddSaturating(
            x: a,
            y: UnitFraction16.Epsilon
        ).Value != BigInteger.Min(
            left: (exactA + BigInteger.One),
            right: maximum
        )) { return $"one unit in the last place is not one raw at {rawA}"; }
        if (UnitFraction16.Max(
            x: a,
            y: UnitFraction16.MinValue
        ) != a) { return $"the minimum value is not neutral for the maximum at {rawA}"; }
        if (UnitFraction16.Min(
            x: a,
            y: UnitFraction16.MaxValue
        ) != a) { return $"the maximum value is not neutral for the minimum at {rawA}"; }

        // De Morgan between the bitwise complement and the two order selections, pinned at the carrier.
        if (~UnitFraction16.Max(
            x: a,
            y: b
        ) != UnitFraction16.Min(
            x: ~a,
            y: ~b
        )) { return $"De Morgan fails on the maximum at ({rawA}, {rawB})"; }
        if (~UnitFraction16.Min(
            x: a,
            y: b
        ) != UnitFraction16.Max(
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
    /// <summary>The UQ0.32 sibling of <see cref="UnitFraction16ExactOpsAndOrder"/>, statement for statement.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32ExactOpsAndOrder(long[] left, long[] right) {
        const int bits = UnitFraction32.FractionBitCount;

        var rawA = UnitFraction32Raw(raw: left[0]);
        var rawB = UnitFraction32Raw(raw: right[0]);
        var rawC = UnitFraction32Raw(raw: (left[0] >> bits));
        var a = new UnitFraction32(Value: rawA);
        var b = UnitFraction32.FromRawBits(value: rawB);
        var c = UnitFraction32.FromRawBits(value: rawC);
        var divisor = ((0U == rawB)
            ? 1U
            : rawB
        );
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);
        var exactC = new BigInteger(value: rawC);
        var maximum = ((BigInteger.One << bits) - BigInteger.One);

        // The primary constructor and the raw factory are one fact, and the raw reads back unmoved.
        if (a.Value != rawA) { return $"the constructor moved raw {rawA} to {a.Value}"; }
        if (a != UnitFraction32.FromRawBits(value: rawA)) { return $"the raw factory disagrees with the constructor at {rawA}"; }

        // The wrapping ring.
        if ((a + b).Value != WrapToFraction(
            fractionBitCount: bits,
            value: (exactA + exactB)
        )) { return $"the wrapping sum of {rawA} and {rawB} is wrong"; }
        if ((a - b).Value != WrapToFraction(
            fractionBitCount: bits,
            value: (exactA - exactB)
        )) { return $"the wrapping difference of {rawA} and {rawB} is wrong"; }
        if ((-a).Value != WrapToFraction(
            fractionBitCount: bits,
            value: -exactA
        )) { return $"the modular negation of {rawA} is wrong"; }
        if ((~a).Value != (maximum - exactA)) { return $"the bitwise complement of {rawA} is wrong"; }

        // The bitwise lattice and the raw remainder, which IS the fixed-point remainder.
        if ((a & b).Value != (exactA & exactB)) { return $"the bitwise and of {rawA} and {rawB} is wrong"; }
        if ((a | b).Value != (exactA | exactB)) { return $"the bitwise or of {rawA} and {rawB} is wrong"; }
        if ((a ^ b).Value != (exactA ^ exactB)) { return $"the bitwise xor of {rawA} and {rawB} is wrong"; }
        if ((a % UnitFraction32.FromRawBits(value: divisor)).Value != (exactA % divisor)) { return $"the remainder of {rawA} by {divisor} is wrong"; }

        // The saturating pair, the order selections, and the clamp against the sampled pair as bounds.
        if (UnitFraction32.AddSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: (exactA + exactB),
            right: maximum
        )) { return $"the saturating sum of {rawA} and {rawB} is wrong"; }
        if (UnitFraction32.SubtractSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: (exactA - exactB),
            right: BigInteger.Zero
        )) { return $"the saturating difference of {rawA} and {rawB} is wrong"; }
        if (UnitFraction32.Max(
            x: a,
            y: b
        ).Value != BigInteger.Max(
            left: exactA,
            right: exactB
        )) { return $"the maximum of {rawA} and {rawB} is wrong"; }
        if (UnitFraction32.Min(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: exactA,
            right: exactB
        )) { return $"the minimum of {rawA} and {rawB} is wrong"; }
        if (UnitFraction32.Clamp(
            value: c,
            minimum: UnitFraction32.Min(
                x: a,
                y: b
            ),
            maximum: UnitFraction32.Max(
                x: a,
                y: b
            )
        ).Value
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
        if ((a + UnitFraction32.Zero) != a) { return $"zero is not neutral for the wrapping sum at {rawA}"; }
        if ((a + UnitFraction32.AdditiveIdentity) != a) { return $"the additive identity is not neutral at {rawA}"; }
        if ((a - a) != UnitFraction32.Zero) { return $"a value is not its own additive inverse at {rawA}"; }
        if ((a ^ a) != UnitFraction32.Zero) { return $"the exclusive or is not self-annihilating at {rawA}"; }
        if (~(~a) != a) { return $"the bitwise complement is not an involution at {rawA}"; }
        if (-(-a) != a) { return $"the modular negation is not an involution at {rawA}"; }
        if (UnitFraction32.AddSaturating(
            x: a,
            y: UnitFraction32.Zero
        ) != a) { return $"zero is not neutral for the saturating sum at {rawA}"; }
        if (UnitFraction32.SubtractSaturating(
            x: a,
            y: UnitFraction32.Zero
        ) != a) { return $"zero is not neutral for the saturating difference at {rawA}"; }
        if (UnitFraction32.AddSaturating(
            x: a,
            y: UnitFraction32.MaxValue
        ) != UnitFraction32.MaxValue) { return $"the saturating sum does not stop at the top at {rawA}"; }
        if (UnitFraction32.SubtractSaturating(
            x: UnitFraction32.MinValue,
            y: a
        ) != UnitFraction32.MinValue) { return $"the saturating difference does not stop at zero at {rawA}"; }
        if (UnitFraction32.AddSaturating(
            x: a,
            y: UnitFraction32.Epsilon
        ).Value != BigInteger.Min(
            left: (exactA + BigInteger.One),
            right: maximum
        )) { return $"one unit in the last place is not one raw at {rawA}"; }
        if (UnitFraction32.Max(
            x: a,
            y: UnitFraction32.MinValue
        ) != a) { return $"the minimum value is not neutral for the maximum at {rawA}"; }
        if (UnitFraction32.Min(
            x: a,
            y: UnitFraction32.MaxValue
        ) != a) { return $"the maximum value is not neutral for the minimum at {rawA}"; }

        // De Morgan between the bitwise complement and the two order selections, pinned at the carrier.
        if (~UnitFraction32.Max(
            x: a,
            y: b
        ) != UnitFraction32.Min(
            x: ~a,
            y: ~b
        )) { return $"De Morgan fails on the maximum at ({rawA}, {rawB})"; }
        if (~UnitFraction32.Min(
            x: a,
            y: b
        ) != UnitFraction32.Max(
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
    public static string? UnitFraction16ShiftsMatchOracle(long[] left, long[] right) {
        const int bits = UnitFraction16.FractionBitCount;

        var raw = UnitFraction16Raw(raw: left[0]);
        var value = UnitFraction16.FromRawBits(value: raw);
        var exact = new BigInteger(value: raw);
        var scale = (BigInteger.One << bits);

        foreach (var amount in ((ReadOnlySpan<int>)[UnitFractionShiftAmount(raw: right[0]), 0, 1, (bits - 1), bits, (bits + 1), 31, 32, -1])) {
            var masked = amount & 31;

            if ((value << amount).Value != ((exact << masked) % scale)) { return $"the left shift of raw {raw} by {amount} is wrong"; }
            if ((value >> amount).Value != (exact >> masked)) { return $"the right shift of raw {raw} by {amount} is wrong"; }
            if ((value >>> amount) != (value >> amount)) { return $"the unsigned right shift of raw {raw} by {amount} differs from the signed one"; }
        }

        return null;
    }
    /// <summary>The UQ0.32 sibling of <see cref="UnitFraction16ShiftsMatchOracle"/>, statement for statement.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32ShiftsMatchOracle(long[] left, long[] right) {
        const int bits = UnitFraction32.FractionBitCount;

        var raw = UnitFraction32Raw(raw: left[0]);
        var value = UnitFraction32.FromRawBits(value: raw);
        var exact = new BigInteger(value: raw);
        var scale = (BigInteger.One << bits);

        foreach (var amount in ((ReadOnlySpan<int>)[UnitFractionShiftAmount(raw: right[0]), 0, 1, (bits - 1), bits, (bits + 1), 31, 32, -1])) {
            var masked = amount & 31;

            if ((value << amount).Value != ((exact << masked) % scale)) { return $"the left shift of raw {raw} by {amount} is wrong"; }
            if ((value >> amount).Value != (exact >> masked)) { return $"the right shift of raw {raw} by {amount} is wrong"; }
            if ((value >>> amount) != (value >> amount)) { return $"the unsigned right shift of raw {raw} by {amount} differs from the signed one"; }
        }

        return null;
    }
    /// <summary>Proves the UQ0.16 construction contract on its own raw ladder: the declared grid is one fact, both
    /// construction routes preserve every ladder raw, the rendering is the exact decimal expansion (and ignores the format
    /// and the provider it is handed), the boxed comparison refuses a foreign type, the double seam saturates at both ends
    /// and rounds ties to even, and the three documented-by-code refusals — an inverted clamp range and a zero divisor at
    /// both division members — throw rather than answer.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction16ConstructionAndRefusals() {
        // Both declared counts are read into locals, so the width statements below are comparisons the run makes rather
        // than ones the compiler folds away (two compile-time constants would make the counterexample unreachable code).
        var bits = UnitFraction16.FractionBitCount;
        var totalBits = UnitFraction16.TotalBitCount;

        // The declared grid: every bit is fractional, so the widths coincide and the top is one unit below one.
        if (bits != 16) { return $"the fraction-bit count is {bits}"; }
        if (totalBits != bits) { return $"the total bit count is {totalBits}"; }
        if (UnitFraction16.Zero.Value != 0) { return $"zero has raw {UnitFraction16.Zero.Value}"; }
        if (UnitFraction16.MinValue != UnitFraction16.Zero) { return "the minimum value is not zero"; }
        if (UnitFraction16.AdditiveIdentity != UnitFraction16.Zero) { return "the additive identity is not zero"; }
        if (default(UnitFraction16) != UnitFraction16.Zero) { return "the default value is not zero"; }
        if (UnitFraction16.Epsilon.Value != 1) { return $"the epsilon has raw {UnitFraction16.Epsilon.Value}"; }
        if (UnitFraction16.MaxValue.Value != ((1 << bits) - 1)) { return $"the maximum value has raw {UnitFraction16.MaxValue.Value}"; }

        // The half-open contract itself: one is unrepresentable, so the top plus a unit WRAPS to zero and the saturating
        // sum stops one unit below where the closed interval's would.
        if ((UnitFraction16.MaxValue + UnitFraction16.Epsilon) != UnitFraction16.Zero) { return "the top plus one unit does not wrap to zero"; }
        if (UnitFraction16.AddSaturating(
            x: UnitFraction16.MaxValue,
            y: UnitFraction16.Epsilon
        ) != UnitFraction16.MaxValue) { return "the saturating sum passes the top"; }

        // The ladder: zero and its neighbourhood, the byte seam, the exact half either side, and the top either side.
        var comma = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", };

        foreach (var raw in UnitFraction16Ladder) {
            var value = UnitFraction16.FromRawBits(value: raw);
            var reference = Oracles.ExactDyadicDecimal(
                numerator: new BigInteger(value: raw),
                shift: bits
            );

            if (value.Value != raw) { return $"the raw factory moved the ladder raw {raw}"; }
            if (new UnitFraction16(Value: raw).Value != raw) { return $"the constructor moved the ladder raw {raw}"; }
            if (value.ToString() != reference) { return $"the ladder raw {raw} rendered as '{value.ToString()}', expected '{reference}'"; }
            if (value.ToString(
                format: "G17",
                formatProvider: comma
            ) != reference) { return $"the ladder raw {raw} honoured a format or a provider it documents as ignored"; }
        }

        // The boxed comparison contract.
        if (UnitFraction16.MaxValue.CompareTo(obj: null) != 1) { return "a null comparand does not sort first"; }
        if (UnitFraction16.MaxValue.CompareTo(obj: ((object)UnitFraction16.MaxValue)) != 0) { return "the boxed comparison of the top against itself is not zero"; }

        try {
            _ = UnitFraction16.MaxValue.CompareTo(obj: "not a unit fraction");

            return "the boxed comparison accepted a foreign type";
        } catch (ArgumentException exception) {
            if (exception.ParamName != "obj") { return $"the boxed-comparison refusal named '{exception.ParamName}'"; }
        }

        // The double seam inward.
        foreach (var (value, expected) in UnitFraction16DoubleLadder) {
            if (UnitFraction16.FromDouble(value: value).Value != expected) { return $"the double {value} converted to raw {UnitFraction16.FromDouble(value: value).Value}, expected {expected}"; }
        }

        // The division ladder: at or above one the quotient SATURATES rather than wrapping, and it saturates after the
        // rounding rather than before.
        foreach (var (dividend, divisor, expected) in UnitFraction16QuotientLadder) {
            if ((UnitFraction16.FromRawBits(value: dividend) / UnitFraction16.FromRawBits(value: divisor)).Value != expected) { return $"the quotient of {dividend} by {divisor} is wrong"; }
        }

        // The three documented-by-code refusals. An inverted clamp range and a zero divisor have no answer, so the members
        // throw; nothing else in this suite or the tools reaches them.
        try {
            _ = UnitFraction16.Clamp(
                value: UnitFraction16.Epsilon,
                minimum: UnitFraction16.MaxValue,
                maximum: UnitFraction16.Zero
            );

            return "the clamp accepted an inverted range";
        } catch (ArgumentException) { }

        try {
            _ = (UnitFraction16.MaxValue / UnitFraction16.Zero);

            return "the divide accepted a zero divisor";
        } catch (DivideByZeroException) { }

        try {
            _ = (UnitFraction16.MaxValue % UnitFraction16.Zero);

            return "the remainder accepted a zero divisor";
        } catch (DivideByZeroException) { }

        return null;
    }
    /// <summary>The UQ0.32 sibling of <see cref="UnitFraction16ConstructionAndRefusals"/>, statement for statement.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32ConstructionAndRefusals() {
        // Both declared counts are read into locals, so the width statements below are comparisons the run makes rather
        // than ones the compiler folds away (two compile-time constants would make the counterexample unreachable code).
        var bits = UnitFraction32.FractionBitCount;
        var totalBits = UnitFraction32.TotalBitCount;

        // The declared grid: every bit is fractional, so the widths coincide and the top is one unit below one.
        if (bits != 32) { return $"the fraction-bit count is {bits}"; }
        if (totalBits != bits) { return $"the total bit count is {totalBits}"; }
        if (UnitFraction32.Zero.Value != 0U) { return $"zero has raw {UnitFraction32.Zero.Value}"; }
        if (UnitFraction32.MinValue != UnitFraction32.Zero) { return "the minimum value is not zero"; }
        if (UnitFraction32.AdditiveIdentity != UnitFraction32.Zero) { return "the additive identity is not zero"; }
        if (default(UnitFraction32) != UnitFraction32.Zero) { return "the default value is not zero"; }
        if (UnitFraction32.Epsilon.Value != 1U) { return $"the epsilon has raw {UnitFraction32.Epsilon.Value}"; }
        if (UnitFraction32.MaxValue.Value != ((1UL << bits) - 1UL)) { return $"the maximum value has raw {UnitFraction32.MaxValue.Value}"; }

        // The half-open contract itself: one is unrepresentable, so the top plus a unit WRAPS to zero and the saturating
        // sum stops one unit below where the closed interval's would.
        if ((UnitFraction32.MaxValue + UnitFraction32.Epsilon) != UnitFraction32.Zero) { return "the top plus one unit does not wrap to zero"; }
        if (UnitFraction32.AddSaturating(
            x: UnitFraction32.MaxValue,
            y: UnitFraction32.Epsilon
        ) != UnitFraction32.MaxValue) { return "the saturating sum passes the top"; }

        // The ladder: zero and its neighbourhood, the sixteen-bit seam, the exact half either side, and the top either side.
        var comma = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", };

        foreach (var raw in UnitFraction32Ladder) {
            var value = UnitFraction32.FromRawBits(value: raw);
            var reference = Oracles.ExactDyadicDecimal(
                numerator: new BigInteger(value: raw),
                shift: bits
            );

            if (value.Value != raw) { return $"the raw factory moved the ladder raw {raw}"; }
            if (new UnitFraction32(Value: raw).Value != raw) { return $"the constructor moved the ladder raw {raw}"; }
            if (value.ToString() != reference) { return $"the ladder raw {raw} rendered as '{value.ToString()}', expected '{reference}'"; }
            if (value.ToString(
                format: "G17",
                formatProvider: comma
            ) != reference) { return $"the ladder raw {raw} honoured a format or a provider it documents as ignored"; }
        }

        // The boxed comparison contract.
        if (UnitFraction32.MaxValue.CompareTo(obj: null) != 1) { return "a null comparand does not sort first"; }
        if (UnitFraction32.MaxValue.CompareTo(obj: ((object)UnitFraction32.MaxValue)) != 0) { return "the boxed comparison of the top against itself is not zero"; }

        try {
            _ = UnitFraction32.MaxValue.CompareTo(obj: "not a unit fraction");

            return "the boxed comparison accepted a foreign type";
        } catch (ArgumentException exception) {
            if (exception.ParamName != "obj") { return $"the boxed-comparison refusal named '{exception.ParamName}'"; }
        }

        // The double seam inward.
        foreach (var (value, expected) in UnitFraction32DoubleLadder) {
            if (UnitFraction32.FromDouble(value: value).Value != expected) { return $"the double {value} converted to raw {UnitFraction32.FromDouble(value: value).Value}, expected {expected}"; }
        }

        // The division ladder: at or above one the quotient SATURATES rather than wrapping, and it saturates after the
        // rounding rather than before.
        foreach (var (dividend, divisor, expected) in UnitFraction32QuotientLadder) {
            if ((UnitFraction32.FromRawBits(value: dividend) / UnitFraction32.FromRawBits(value: divisor)).Value != expected) { return $"the quotient of {dividend} by {divisor} is wrong"; }
        }

        // The three documented-by-code refusals. An inverted clamp range and a zero divisor have no answer, so the members
        // throw; nothing else in this suite or the tools reaches them.
        try {
            _ = UnitFraction32.Clamp(
                value: UnitFraction32.Epsilon,
                minimum: UnitFraction32.MaxValue,
                maximum: UnitFraction32.Zero
            );

            return "the clamp accepted an inverted range";
        } catch (ArgumentException) { }

        try {
            _ = (UnitFraction32.MaxValue / UnitFraction32.Zero);

            return "the divide accepted a zero divisor";
        } catch (DivideByZeroException) { }

        try {
            _ = (UnitFraction32.MaxValue % UnitFraction32.Zero);

            return "the remainder accepted a zero divisor";
        } catch (DivideByZeroException) { }

        return null;
    }
    /// <summary>Proves the UQ0.16 text seam at every swept raw: the renderer reproduces the oracle's exact decimal
    /// expansion, the span formatter writes exactly that and REFUSES every destination one character short with nothing
    /// reported written, all four parse entry points read the exact rendering back to the raw it came from, and two
    /// derived texts that do not land on the grid — the rendering truncated, and the rendering with a digit appended —
    /// reach the same accept-or-refuse decision and the same raw as the shared-nothing text oracle.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction16TextMatchesOracle(long[] left, long[] right) {
        const int bits = UnitFraction16.FractionBitCount;

        var raw = UnitFraction16Raw(raw: left[0]);
        var value = UnitFraction16.FromRawBits(value: raw);
        var reference = Oracles.ExactDyadicDecimal(
            numerator: new BigInteger(value: raw),
            shift: bits
        );
        Span<char> buffer = stackalloc char[(UnitFraction16.TotalBitCount + 4)];

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

        if (UnitFraction16.Parse(
            provider: null,
            s: reference
        ) != value) { return $"the string parse of '{reference}' did not return raw {raw}"; }
        if (UnitFraction16.Parse(
            s: reference.AsSpan(),
            provider: null
        ) != value) { return $"the span parse of '{reference}' did not return raw {raw}"; }
        if (
            !UnitFraction16.TryParse(
            provider: null,
            result: out var parsed,
            s: reference
        ) ||
            (parsed != value)
        ) { return $"the string try-parse of '{reference}' did not return raw {raw}"; }
        if (
            !UnitFraction16.TryParse(
            s: reference.AsSpan(),
            provider: null,
            result: out var spanParsed
        ) ||
            (spanParsed != value)
        ) { return $"the span try-parse of '{reference}' did not return raw {raw}"; }

        foreach (var text in UnitFractionDerivedTexts(
            raw: raw,
            rendering: reference
        )) {
            var accepted = UnitFraction16.TryParse(
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
                (actual.Value != expected)
            ) { return $"'{text}' parsed to raw {actual.Value}, expected {expected}"; }
            if (
                !accepted &&
                (actual != default)
            ) { return $"the refusal of '{text}' left raw {actual.Value} behind"; }
        }

        return null;
    }
    /// <summary>The UQ0.32 sibling of <see cref="UnitFraction16TextMatchesOracle"/>, statement for statement.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32TextMatchesOracle(long[] left, long[] right) {
        const int bits = UnitFraction32.FractionBitCount;

        var raw = UnitFraction32Raw(raw: left[0]);
        var value = UnitFraction32.FromRawBits(value: raw);
        var reference = Oracles.ExactDyadicDecimal(
            numerator: new BigInteger(value: raw),
            shift: bits
        );
        Span<char> buffer = stackalloc char[(UnitFraction32.TotalBitCount + 4)];

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

        if (UnitFraction32.Parse(
            provider: null,
            s: reference
        ) != value) { return $"the string parse of '{reference}' did not return raw {raw}"; }
        if (UnitFraction32.Parse(
            s: reference.AsSpan(),
            provider: null
        ) != value) { return $"the span parse of '{reference}' did not return raw {raw}"; }
        if (
            !UnitFraction32.TryParse(
            provider: null,
            result: out var parsed,
            s: reference
        ) ||
            (parsed != value)
        ) { return $"the string try-parse of '{reference}' did not return raw {raw}"; }
        if (
            !UnitFraction32.TryParse(
            s: reference.AsSpan(),
            provider: null,
            result: out var spanParsed
        ) ||
            (spanParsed != value)
        ) { return $"the span try-parse of '{reference}' did not return raw {raw}"; }

        foreach (var text in UnitFractionDerivedTexts(
            raw: raw,
            rendering: reference
        )) {
            var accepted = UnitFraction32.TryParse(
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
                (actual.Value != expected)
            ) { return $"'{text}' parsed to raw {actual.Value}, expected {expected}"; }
            if (
                !accepted &&
                (actual != default)
            ) { return $"the refusal of '{text}' left raw {actual.Value} behind"; }
        }

        return null;
    }
    /// <summary>Proves the UQ0.16 text contract on its own committed ladder: every accepted spelling reaches the
    /// hand-derived raw AND the oracle's, through both parse routes; every refused spelling is refused by both, leaves the
    /// default behind, and makes the throwing route throw; the null string is a refusal on one route and an
    /// ArgumentNullException on the other; and the provider is honoured, so a comma-separator culture moves the decimal
    /// point and the invariant spelling stops parsing under it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction16ParseLadderHolds() {
        const int bits = UnitFraction16.FractionBitCount;

        foreach (var (text, expected) in UnitFraction16ParseLadder) {
            if (
                !UnitFraction16.TryParse(
                provider: null,
                result: out var parsed,
                s: text
            ) ||
                (parsed.Value != expected)
            ) { return $"'{text}' parsed to raw {parsed.Value}, expected {expected}"; }
            if (UnitFraction16.Parse(
                provider: null,
                s: text
            ).Value != expected) { return $"the throwing string parse of '{text}' is wrong"; }
            if (UnitFraction16.Parse(
                s: text.AsSpan(),
                provider: null
            ).Value != expected) { return $"the throwing span parse of '{text}' is wrong"; }
            if (
                !Oracles.TryUnitFractionText(
                fractionBitCount: bits,
                raw: out var admitted,
                text: text
            ) ||
                (admitted != expected)
            ) { return $"the oracle reads '{text}' as raw {admitted}, expected {expected}"; }
        }

        foreach (var text in UnitFraction16RefusedTexts) {
            if (
                UnitFraction16.TryParse(
                provider: null,
                result: out var refused,
                s: text
            ) ||
                (refused != default)
            ) { return $"'{text}' was accepted, or left raw {refused.Value} behind"; }
            if (Oracles.TryUnitFractionText(
                fractionBitCount: bits,
                raw: out _,
                text: text
            )) { return $"the oracle accepted '{text}'"; }

            try {
                _ = UnitFraction16.Parse(
                    provider: null,
                    s: text
                );

                return $"the throwing string parse accepted '{text}'";
            } catch (FormatException) { }

            try {
                _ = UnitFraction16.Parse(
                    s: text.AsSpan(),
                    provider: null
                );

                return $"the throwing span parse accepted '{text}'";
            } catch (FormatException) { }
        }

        if (
            UnitFraction16.TryParse(
            provider: null,
            result: out var fromNull,
            s: ((string?)null)
        ) ||
            (fromNull != default)
        ) { return "a null string was accepted"; }

        try {
            _ = UnitFraction16.Parse(
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

        if (UnitFraction16.Parse(
            provider: comma,
            s: "0,5"
        ).Value != 32768) { return "the comma spelling did not parse under a comma-separator provider"; }
        if (UnitFraction16.TryParse(
            provider: comma,
            result: out _,
            s: "0.5"
        )) { return "the point spelling parsed under a comma-separator provider"; }
        if (UnitFraction16.Parse(
            provider: null,
            s: "0.5"
        ).Value != 32768) { return "a null provider is not the invariant culture"; }

        return null;
    }
    /// <summary>The UQ0.32 sibling of <see cref="UnitFraction16ParseLadderHolds"/>, statement for statement.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32ParseLadderHolds() {
        const int bits = UnitFraction32.FractionBitCount;

        foreach (var (text, expected) in UnitFraction32ParseLadder) {
            if (
                !UnitFraction32.TryParse(
                provider: null,
                result: out var parsed,
                s: text
            ) ||
                (parsed.Value != expected)
            ) { return $"'{text}' parsed to raw {parsed.Value}, expected {expected}"; }
            if (UnitFraction32.Parse(
                provider: null,
                s: text
            ).Value != expected) { return $"the throwing string parse of '{text}' is wrong"; }
            if (UnitFraction32.Parse(
                s: text.AsSpan(),
                provider: null
            ).Value != expected) { return $"the throwing span parse of '{text}' is wrong"; }
            if (
                !Oracles.TryUnitFractionText(
                fractionBitCount: bits,
                raw: out var admitted,
                text: text
            ) ||
                (admitted != expected)
            ) { return $"the oracle reads '{text}' as raw {admitted}, expected {expected}"; }
        }

        foreach (var text in UnitFraction32RefusedTexts) {
            if (
                UnitFraction32.TryParse(
                provider: null,
                result: out var refused,
                s: text
            ) ||
                (refused != default)
            ) { return $"'{text}' was accepted, or left raw {refused.Value} behind"; }
            if (Oracles.TryUnitFractionText(
                fractionBitCount: bits,
                raw: out _,
                text: text
            )) { return $"the oracle accepted '{text}'"; }

            try {
                _ = UnitFraction32.Parse(
                    provider: null,
                    s: text
                );

                return $"the throwing string parse accepted '{text}'";
            } catch (FormatException) { }

            try {
                _ = UnitFraction32.Parse(
                    s: text.AsSpan(),
                    provider: null
                );

                return $"the throwing span parse accepted '{text}'";
            } catch (FormatException) { }
        }

        if (
            UnitFraction32.TryParse(
            provider: null,
            result: out var fromNull,
            s: ((string?)null)
        ) ||
            (fromNull != default)
        ) { return "a null string was accepted"; }

        try {
            _ = UnitFraction32.Parse(
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

        if (UnitFraction32.Parse(
            provider: comma,
            s: "0,5"
        ).Value != 2147483648U) { return "the comma spelling did not parse under a comma-separator provider"; }
        if (UnitFraction32.TryParse(
            provider: comma,
            result: out _,
            s: "0.5"
        )) { return "the point spelling parsed under a comma-separator provider"; }
        if (UnitFraction32.Parse(
            provider: null,
            s: "0.5"
        ).Value != 2147483648U) { return "a null provider is not the invariant culture"; }

        return null;
    }
    /// <summary>Proves the seam between the half-open fraction and the closed interval FROM THE FRACTION SIDE, which is
    /// the side the interval's own remarks describe in prose: the two share the grid and the fraction stops exactly one
    /// unit short of the interval's one; the bitwise complement and the interval's ARITHMETIC complement differ by exactly
    /// that one unit; the fraction's sum WRAPS where the interval's saturating sum CLAMPS, and the two agree exactly while
    /// the exact sum stays below one; and the fraction's own saturating sum stops one unit lower still. The embedding's
    /// exactness and its refusal at one are pinned from the interval side by closed-unit.kinship-exact and are NOT
    /// restated here.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32KinshipExact(long[] left, long[] right) {
        var rawA = UnitFraction32Raw(raw: left[0]);
        var rawB = UnitFraction32Raw(raw: right[0]);
        var a = UnitFraction32.FromRawBits(value: rawA);
        var b = UnitFraction32.FromRawBits(value: rawB);
        var embeddedA = UnitInterval32.FromUnitFraction32(value: a);
        var embeddedB = UnitInterval32.FromUnitFraction32(value: b);
        var exactSum = (new BigInteger(value: rawA) + rawB);
        // Read into a local so the shared-grid statement is a comparison the run makes rather than one the compiler folds
        // away — both counts are declared constants, and a folded comparison would make the counterexample unreachable.
        var bits = UnitFraction32.FractionBitCount;
        var one = (BigInteger.One << bits);

        // One fact, two types: the same grid, and the fraction stops exactly one unit short of the interval's one.
        if (bits != UnitInterval32.FractionBitCount) { return "the two types do not share the grid"; }
        if (UnitInterval32.One.Value != (((ulong)UnitFraction32.MaxValue.Value) + 1UL)) { return "the fraction's top is not one unit below the interval's one"; }

        // The order is preserved across the embedding, at every relation the two types both offer.
        if ((a < b) != (embeddedA < embeddedB)) { return $"the embedding does not preserve less-than at ({rawA}, {rawB})"; }
        if ((a <= b) != (embeddedA <= embeddedB)) { return $"the embedding does not preserve less-or-equal at ({rawA}, {rawB})"; }
        if ((a > b) != (embeddedA > embeddedB)) { return $"the embedding does not preserve greater-than at ({rawA}, {rawB})"; }
        if ((a >= b) != (embeddedA >= embeddedB)) { return $"the embedding does not preserve greater-or-equal at ({rawA}, {rawB})"; }
        if (Math.Sign(value: a.CompareTo(other: b)) != Math.Sign(value: embeddedA.CompareTo(other: embeddedB))) { return $"the embedding does not preserve the comparison at ({rawA}, {rawB})"; }

        // The one-unit offset the interval's remark states in prose: bitwise complement versus arithmetic complement.
        if (UnitInterval32.Complement(value: embeddedA).Value != (((ulong)(~a).Value) + 1UL)) { return $"the two complements are not one unit apart at {rawA}"; }

        // Wrap versus clamp, and the exact condition under which they agree.
        if ((a + b).Value != WrapToFraction(
            fractionBitCount: bits,
            value: exactSum
        )) { return $"the fraction's sum does not wrap at ({rawA}, {rawB})"; }
        if (UnitInterval32.AddSaturating(
            x: embeddedA,
            y: embeddedB
        ).Value != BigInteger.Min(
            left: exactSum,
            right: one
        )) { return $"the interval's sum does not clamp at ({rawA}, {rawB})"; }
        if ((((ulong)(a + b).Value) == UnitInterval32.AddSaturating(
            x: embeddedA,
            y: embeddedB
        ).Value) != (exactSum < one)) { return $"the wrap and the clamp agree outside the sub-one regime at ({rawA}, {rawB})"; }
        if (UnitFraction32.AddSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: exactSum,
            right: (one - BigInteger.One)
        )) { return $"the fraction's saturating sum does not stop one unit lower at ({rawA}, {rawB})"; }

        return null;
    }
    /// <summary>Proves the UQ0.16 surface EXHAUSTIVELY, which the width makes possible: every one of the 65 536 raws is
    /// rendered, parsed back, projected to double and complemented, and every raw is multiplied and divided against a
    /// committed band of divisors. At this width the sampled laws' envelope disappears — there is no operand the claim
    /// does not reach on one side of every binary statement.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction16Exhaustive() {
        const int bits = UnitFraction16.FractionBitCount;

        for (var raw = 0; (raw <= 65535); ++raw) {
            var value = UnitFraction16.FromRawBits(value: ((ushort)raw));
            var exact = new BigInteger(value: raw);
            var reference = Oracles.ExactDyadicDecimal(
                numerator: exact,
                shift: bits
            );

            if (value.ToString() != reference) { return $"raw {raw} rendered as '{value.ToString()}'"; }
            if (UnitFraction16.Parse(
                provider: null,
                s: reference
            ) != value) { return $"raw {raw} did not parse back from '{reference}'"; }
            if (BitConverter.DoubleToUInt64Bits(value: ((double)value)) != Oracles.ExactBinary64Bits(
                numerator: exact,
                shift: bits
            )) { return $"the projection of raw {raw} is wrong"; }
            if (UnitFraction16.FromDouble(value: ((double)value)) != value) { return $"the double round trip failed at raw {raw}"; }
            if ((~value).Value != (65535 - raw)) { return $"the complement of raw {raw} is wrong"; }
            if (-(-value) != value) { return $"the negation is not an involution at raw {raw}"; }

            foreach (var divisor in UnitFraction16Band) {
                var product = ((long)(value * UnitFraction16.FromRawBits(value: divisor)).Value);
                var quotient = ((long)(value / UnitFraction16.FromRawBits(value: divisor)).Value);

                if (product != ((long)Oracles.UnitFractionProduct(
                    fractionBitCount: bits,
                    x: ((ulong)raw),
                    y: divisor
                ))) { return $"the product of {raw} and {divisor} is wrong"; }
                if (quotient != ((long)Oracles.UnitFractionQuotient(
                    fractionBitCount: bits,
                    x: ((ulong)raw),
                    y: divisor
                ))) { return $"the quotient of {raw} by {divisor} is wrong"; }
            }
        }

        return null;
    }

    // The construction ladder: zero and its neighbourhood, the byte seam, the exact half either side, and the top either
    // side — the raws every branch of the renderer, the parser and the saturating pair splits on.
    private static readonly ushort[] UnitFraction16Ladder = [
        0, 1, 2, 3,
        255, 256, 257,
        32767, 32768, 32769,
        65533, 65534, 65535,
    ];
    // The 32-bit construction ladder: zero and its neighbourhood, the SIXTEEN-bit seam where the coarse Q48.16 grid and
    // the closed interval's narrowing split, the exact half either side, and the top either side.
    private static readonly uint[] UnitFraction32Ladder = [
        0U, 1U, 2U, 3U,
        65535U, 65536U, 65537U,
        2147483647U, 2147483648U, 2147483649U,
        4294967293U, 4294967294U, 4294967295U,
    ];
    // The double seam, each expectation derived from the definition rather than from the kernel: the two saturations
    // (which land on MaxValue, not on one, because one is unrepresentable), not-a-number, both infinities, negative zero,
    // the exactly-representable interior points, the three half-ULP ties whose ties-to-even resolution is the house
    // rounding discipline (0.5 down to 0, 1.5 up to 2, 2.5 down to 2), the tie AT the top that rounds up and then
    // saturates, and the one input that is not exactly representable: 0.1 scaled by 2^16 is exact in double and its value
    // 6553.6 rounds up.
    private static readonly (double Value, ushort Expected)[] UnitFraction16DoubleLadder = [
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
    // The 32-bit double seam, re-derived at the finer grid: 0.1 scaled by 2^32 is 429496729.6 and rounds up.
    private static readonly (double Value, uint Expected)[] UnitFraction32DoubleLadder = [
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
    // The quotient ladder, hand-derived from (dividend·2^16)/divisor: an exact power-of-two ratio, a ratio that rounds up,
    // the equal-operand case whose true quotient is exactly one, and two ratios above one — all four of the last kind
    // saturate onto MaxValue rather than wrapping.
    private static readonly (ushort Dividend, ushort Divisor, ushort Expected)[] UnitFraction16QuotientLadder = [
        (0, 1, 0),
        (16384, 32768, 32768),
        (1, 3, 21845),
        (2, 3, 43691),
        (40000, 65535, 40001),
        (65535, 65535, 65535),
        (65535, 1, 65535),
        (32768, 16384, 65535),
    ];
    // The same ladder at the 32-bit width, re-derived from (dividend·2^32)/divisor.
    private static readonly (uint Dividend, uint Divisor, uint Expected)[] UnitFraction32QuotientLadder = [
        (0U, 1U, 0U),
        (1073741824U, 2147483648U, 2147483648U),
        (1U, 3U, 1431655765U),
        (2U, 3U, 2863311531U),
        (4294967295U, 4294967295U, 4294967295U),
        (4294967295U, 1U, 4294967295U),
        (2147483648U, 1073741824U, 4294967295U),
    ];
    // The accepted spellings: the zero forms, the redundant and omitted digits either side of the point, surrounding
    // whitespace, the exactly-representable points, the three half-ULP ties (which resolve to even), the tie WITH a
    // discarded nonzero digit far beyond the seventeen the parser keeps (which therefore rounds up), the value that
    // rounds up onto the top, and the top itself.
    private static readonly (string Text, ushort Expected)[] UnitFraction16ParseLadder = [
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
    // The 32-bit accepted spellings, re-derived at the finer grid: the exact ULP and its three half-ULP ties, the tie one
    // unit of the thirty-third decimal place ABOVE the boundary, the value that rounds up onto the top, and the top
    // itself.
    private static readonly (string Text, uint Expected)[] UnitFraction32ParseLadder = [
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
    // The refused spellings: the empty and blank texts, a non-number, both signs (no AllowLeadingSign), one and above
    // (unrepresentable), an exponent (no AllowExponent), the group separator (no AllowThousands), a trailing non-digit,
    // a value ABOVE the top that would round back onto it (the parser rejects the exact value, never clamps it), and an
    // integer too wide to be a fraction at all.
    private static readonly string[] UnitFraction16RefusedTexts = [
        "", " ", "abc", "-0.5", "+0.5", "1", "1.0", "2", "1e-1", "0,5", "0.5x",
        "0.99998474121093751", "12345678901234567890123",
    ];
    // The same refusals at the 32-bit width, with the out-of-range vector re-derived: thirty-three decimal places, one
    // unit above the top.
    private static readonly string[] UnitFraction32RefusedTexts = [
        "", " ", "abc", "-0.5", "+0.5", "1", "1.0", "2", "1e-1", "0,5", "0.5x",
        "0.999999999767169356346130371093751", "12345678901234567890123",
    ];
    // The divisor band the exhaustive sweep runs every raw against: the smallest units, the odd divisors whose remainders
    // never vanish, the byte seam, the exact half either side, and the top either side. Twelve values, so the sweep is
    // 65 536 × 12 pairs on each of the two rounding kernels.
    private static readonly ushort[] UnitFraction16Band = [
        1, 2, 3, 5, 255, 256, 257, 32767, 32768, 32769, 65534, 65535,
    ];

    // ---- integer floored division (BinaryIntegerFunctions, over the raw carrier) ----

    /// <summary>Maps a sampled divisor onto one the operation defines: zero divides by nothing, and the signed minimum
    /// over minus one has no representable quotient. Both are substituted identically in subject and oracle, so every
    /// sampled pair reaches a defined comparison rather than being skipped asymmetrically. The two excluded pairs are
    /// the documented throw sites and belong to a probe, not to a value law.</summary>
    private static long Divisor(long a, long b) =>
        (((0L == b) || ((long.MinValue == a) && (-1L == b)))
            ? 1L
            : b
        );

    /// <summary>Proves the two operand pairs the value laws substitute are REFUSED rather than answered wrongly:
    /// division by zero and the signed minimum over minus one, at all three floored members.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Without this the block's envelope reads "every operand pair except the two substituted ones", since
    /// <see cref="Divisor"/> maps both away and nothing else in the suite or the tools reaches them (worklist O1).</remarks>
    public static string? IntegerDivisionLimitsRefuse() {
        foreach (var (name, divide) in (((string Name, Action<long, long> Divide)[])[
            ("FloorDivide", static (a, b) => _ = a.FloorDivide(divisor: b)),
            ("CeilingDivide", static (a, b) => _ = a.CeilingDivide(divisor: b)),
            ("FloorDivRem", static (a, b) => _ = a.FloorDivRem(divisor: b)),
        ])) {
            if (!Throws<DivideByZeroException>(action: () => divide(
                7L,
                0L
            ))) {
                return $"{name} answered a division by zero instead of refusing it";
            }

            if (!Throws<OverflowException>(action: () => divide(
                long.MinValue,
                -1L
            ))) {
                return $"{name} answered the signed minimum over minus one, whose quotient is unrepresentable, instead of refusing it";
            }
        }

        return null;
    }
    /// <summary>The subject floored quotient.</summary>
    public static long FloorDivide(long a, long b) =>
        a.FloorDivide(divisor: Divisor(
            a: a,
            b: b
        ));
    /// <summary>The subject ceiling quotient.</summary>
    public static long CeilingDivide(long a, long b) =>
        a.CeilingDivide(divisor: Divisor(
            a: a,
            b: b
        ));
    /// <summary>The subject floored quotient read from the quotient-and-remainder pair.</summary>
    public static long FloorDivRemQuotient(long a, long b) =>
        a.FloorDivRem(divisor: Divisor(
            a: a,
            b: b
        )).Quotient;
    /// <summary>The subject floored remainder read from the quotient-and-remainder pair.</summary>
    public static long FloorDivRemRemainder(long a, long b) =>
        a.FloorDivRem(divisor: Divisor(
            a: a,
            b: b
        )).Remainder;
    /// <summary>The exact floored quotient, taken in arbitrary width so no carrier edge is a special case.</summary>
    public static long FloorDivideOracle(long a, long b) =>
        ((long)Oracles.FloorQuotient(
            numerator: a,
            denominator: Divisor(
                a: a,
                b: b
            )
        ));
    /// <summary>The exact ceiling quotient — the floored quotient, raised by one exactly when the division is inexact.</summary>
    public static long CeilingDivideOracle(long a, long b) {
        var divisor = Divisor(
            a: a,
            b: b
        );
        var quotient = Oracles.FloorQuotient(
            denominator: divisor,
            numerator: a
        );

        return ((long)(((quotient * divisor) == a)
            ? quotient
            : (quotient + System.Numerics.BigInteger.One)));
    }
    /// <summary>The exact floored remainder — what the value less the floored product leaves.</summary>
    public static long FloorDivRemRemainderOracle(long a, long b) {
        var divisor = Divisor(
            a: a,
            b: b
        );

        return ((long)(a - (Oracles.FloorQuotient(
            denominator: divisor,
            numerator: a
        ) * divisor)));
    }
    // ---- FixedComplex (the (0, −1) relation) ----

    /// <summary>The subject <see cref="FixedComplex"/> multiply.</summary>
    public static (long U, long V) ComplexMultiply(long u1, long v1, long u2, long v2) {
        var product = (new FixedComplex(
            Real: Raw(value: u1),
            Imaginary: Raw(value: v1)
        ) * new FixedComplex(
            Real: Raw(value: u2),
            Imaginary: Raw(value: v2)
        ));

        return (product.Real.Value, product.Imaginary.Value);
    }
    /// <summary>The subject <see cref="FixedComplex"/> conjugate.</summary>
    public static (long U, long V) ComplexConjugate(long u, long v) {
        var conjugate = new FixedComplex(
            Real: Raw(value: u),
            Imaginary: Raw(value: v)
        ).Conjugate();

        return (conjugate.Real.Value, conjugate.Imaginary.Value);
    }
    /// <summary>The subject <see cref="FixedComplex"/> negation.</summary>
    public static (long U, long V) ComplexNegate(long u, long v) {
        var negated = -new FixedComplex(
            Real: Raw(value: u),
            Imaginary: Raw(value: v)
        );

        return (negated.Real.Value, negated.Imaginary.Value);
    }
    // ---- FixedSplit (the (0, +1) relation) ----

    /// <summary>The subject <see cref="FixedComplex"/> multiply as a two-lane vector operation.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void ComplexMultiplyLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var (u, v) = ComplexMultiply(
            u1: left[0],
            v1: left[1],
            u2: right[0],
            v2: right[1]
        );

        result[0] = u;
        result[1] = v;
    }
    /// <summary>The subject <see cref="FixedSplit"/> multiply.</summary>
    public static (long U, long V) SplitMultiply(long u1, long v1, long u2, long v2) {
        var product = (new FixedSplit(
            U: Raw(value: u1),
            V: Raw(value: v1)
        ) * new FixedSplit(
            U: Raw(value: u2),
            V: Raw(value: v2)
        ));

        return (product.U.Value, product.V.Value);
    }
    /// <summary>The subject <see cref="FixedSplit"/> norm.</summary>
    public static long SplitNorm(long u, long v) =>
        new FixedSplit(
            U: Raw(value: u),
            V: Raw(value: v)
        ).Norm.Value;
    /// <summary>The subject <see cref="FixedSplit"/> conjugate.</summary>
    public static (long U, long V) SplitConjugate(long u, long v) {
        var conjugate = new FixedSplit(
            U: Raw(value: u),
            V: Raw(value: v)
        ).Conjugate();

        return (conjugate.U.Value, conjugate.V.Value);
    }
    // ---- FixedDual<FixedQ4816> (the (0, 0) relation) ----

    /// <summary>The subject <see cref="FixedDual{TValue}"/> multiply over <see cref="FixedQ4816"/>.</summary>
    public static (long U, long V) DualMultiply(long u1, long v1, long u2, long v2) {
        var product = (new FixedDual<FixedQ4816>(
            Real: Raw(value: u1),
            Dual: Raw(value: v1)
        ) * new FixedDual<FixedQ4816>(
            Real: Raw(value: u2),
            Dual: Raw(value: v2)
        ));

        return (product.Real.Value, product.Dual.Value);
    }
    // ---- QuadraticAlgebra<FixedQ4816> lanes ----

    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> multiply for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static BinaryElemOp AlgebraMultiply(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (u1, v1, u2, v2) => {
            var product = algebra.Multiply(
                left: new(
                    U: Raw(value: u1),
                    V: Raw(value: v1)
                ),
                right: new(
                    U: Raw(value: u2),
                    V: Raw(value: v2)
                )
            );

            return (product.U.Value, product.V.Value);
        };
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> norm for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static ScalarElemOp AlgebraNorm(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (u, v) => algebra.Norm(value: new(
            U: Raw(value: u),
            V: Raw(value: v)
        )).Value;
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> Möbius step for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static UnaryElemOp AlgebraMobius(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (n, d) => {
            var step = algebra.MobiusStep(pair: new(
                Numerator: Raw(value: n),
                Denominator: Raw(value: d)
            ));

            return (step.Numerator.Value, step.Denominator.Value);
        };
    }
    // ---- oracle closures for the planar relations ----

    /// <summary>The oracle multiply for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static BinaryElemOp MultiplyOracle(long pRaw, long qRaw) =>
        (u1, v1, u2, v2) => Oracles.QuadraticMultiply(
            pRaw: pRaw,
            qRaw: qRaw,
            u1: u1,
            u2: u2,
            v1: v1,
            v2: v2
        );
    /// <summary>The oracle norm for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static ScalarElemOp NormOracle(long pRaw, long qRaw) =>
        (u, v) => Oracles.QuadraticNorm(
            pRaw: pRaw,
            qRaw: qRaw,
            u: u,
            v: v
        );
    /// <summary>The oracle Möbius numerator for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static ScalarBinaryOp MobiusNumeratorOracle(long pRaw, long qRaw) =>
        (n, d) => Oracles.MobiusNumerator(
            d: d,
            n: n,
            pRaw: pRaw,
            qRaw: qRaw
        );
    /// <summary>The oracle multiply for the relation <c>(pRaw, qRaw)</c> in the two-lane shape the presented quadratic
    /// twins take — the THIRD LEG for every fixed-point twin whose two sides both round through
    /// <c>FixedQ4816.RoundProductSum</c> or <c>FusedArithmetic.RoundQ48SumToRaw</c>.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp QuadraticMultiplyLanesOracle(long pRaw, long qRaw) =>
        (left, right, result) => {
            var product = Oracles.QuadraticMultiply(
                pRaw: pRaw,
                qRaw: qRaw,
                u1: left[0],
                v1: left[1],
                u2: right[0],
                v2: right[1]
            );

            result[0] = product.U;
            result[1] = product.V;
        };
    /// <summary>The oracle power of the adjoined root of <c>x² = P·x + Q</c>, by the pinned ascending-bit schedule with
    /// every step's arithmetic re-derived in <see cref="BigInteger"/>.</summary>
    /// <param name="p">The linear coefficient, raw Q16.</param>
    /// <param name="q">The constant coefficient, raw Q16.</param>
    /// <param name="exponent">The power.</param>
    /// <returns>The power's components as raws.</returns>
    public static (long U, long V) CompanionRootPowerOracle(long p, long q, ulong exponent) =>
        Oracles.CompanionRootPower(
            exponent: exponent,
            pRaw: p,
            qRaw: q
        );
    /// <summary>The oracle dual part of a <c>(0, 0)</c> product — ONE ties-to-even rounding of the exact
    /// <c>a·d + b·c</c> at shift sixteen — in the shape both the jet residual and <see cref="FixedDual{TScalar}"/>
    /// return.</summary>
    /// <param name="u1">The multiplicand's real part, raw.</param>
    /// <param name="v1">The multiplicand's dual part, raw.</param>
    /// <param name="u2">The multiplier's real part, raw.</param>
    /// <param name="v2">The multiplier's dual part, raw.</param>
    /// <returns>The residual's components as raws; the second is identically zero, as both subjects return.</returns>
    public static (long U, long V) JetResidualOracle(long u1, long v1, long u2, long v2) =>
        (Oracles.RoundDyadic(
            exact: ((((BigInteger)u1) * v2) + (((BigInteger)v1) * u2)),
            shift: 16
        ), 0L);
    // ---- PresentedAlgebra: the derived multi-lane products ----
    //
    // Every binding below is built LAZILY, on the delegate's first call, so a filtered run pays only for the
    // presentations its own tier actually drives. Each closure owns its algebra outright; the kernel's working buffers
    // are per-instance mutable state, so nothing here is shared between cases.

    /// <summary>The subject product of the presented Clifford signature <c>(p, q, r)</c> over the house scalar, with
    /// lanes indexed by BLADE BITMASK so the vector agrees with <see cref="Multivector"/> lane for lane.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedCliffordMultiply(int positiveCount, int negativeCount, int degenerateCount) {
        FixedLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= CliffordBinding(
                degenerateCount: degenerateCount,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            );

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The subject <see cref="GeometricAlgebra"/> product of the signature <c>(p, q, r)</c>.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp GeometricMultiply(int positiveCount, int negativeCount, int degenerateCount) {
        var algebra = GeometricAlgebra.Create(
            degenerateCount: degenerateCount,
            negativeCount: negativeCount,
            positiveCount: positiveCount
        );

        return (left, right, result) => {
            var a = new Multivector();
            var b = new Multivector();

            for (var lane = 0; (lane < left.Length); ++lane) {
                a[lane] = Raw(value: left[lane]);
                b[lane] = Raw(value: right[lane]);
            }

            var product = algebra.GeometricProduct(
                left: a,
                right: b
            );

            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = product[lane].Value; }
        };
    }
    /// <summary>The shared-nothing twisted-group oracle at a Clifford signature: one rounding per blade of the whole
    /// charged sum, with the charges from <see cref="Oracles.CliffordCharge"/>.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp CliffordProductOracle(int positiveCount, int negativeCount, int degenerateCount) {
        var charge = CliffordChargeSource(
            degenerateCount: degenerateCount,
            negativeCount: negativeCount,
            positiveCount: positiveCount
        );

        return (left, right, result) => Oracles.TwistedGroupProduct(
            chargeSource: charge,
            left: left,
            result: result,
            right: right,
            shift: 16
        );
    }
    /// <summary>The shared-nothing twisted-group oracle at a Cayley–Dickson floor: one rounding per lane of the whole
    /// charged sum, with the charges from <see cref="Oracles.CayleyDicksonCharge"/>.</summary>
    /// <param name="floors">The number of doublings.</param>
    /// <returns>The bound oracle.</returns>
    /// <remarks>This leg pins the ROUNDING DISCIPLINE, not the sign structure: the charge table it reads is a labelled
    /// faithful-carriage transcription of the presentation's own recursion (<see cref="Oracles.CayleyDicksonCharge"/>'s
    /// remark), so a shared error in the tower's signs would hide. The signs answer to the doubling-algebra comparison in
    /// the same case. What no other statement in the tree covers, and what this one does, is that the presented product
    /// at this floor is ONE ties-to-even rounding of the exact charged sum at full raw range.</remarks>
    public static VectorBinaryOp CayleyDicksonProductOracle(int floors) {
        var count = (1 << floors);
        var table = new int[(count * count)];

        for (var left = 0; (left < count); ++left) {
            for (var right = 0; (right < count); ++right) {
                table[((left * count) + right)] = Oracles.CayleyDicksonCharge(
                    floors: floors,
                    leftIndex: left,
                    rightIndex: right
                );
            }
        }

        var charge = ((Func<int, int, int>)((first, second) => table[((first * count) + second)]));

        return (left, right, result) => Oracles.TwistedGroupProduct(
            chargeSource: charge,
            left: left,
            result: result,
            right: right,
            shift: 16
        );
    }
    /// <summary>The per-term-rounding sibling of <see cref="CliffordProductOracle"/> — the discipline the fused kernel
    /// is claimed to differ from.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp CliffordPerProductOracle(int positiveCount, int negativeCount, int degenerateCount) {
        var charge = CliffordChargeSource(
            degenerateCount: degenerateCount,
            negativeCount: negativeCount,
            positiveCount: positiveCount
        );

        return (left, right, result) => Oracles.TwistedGroupPerProduct(
            chargeSource: charge,
            left: left,
            result: result,
            right: right,
            shift: 16
        );
    }
    /// <summary>The subject product of the presented Cayley–Dickson tower over the house scalar; a lane IS a tower
    /// index, which is also the normal-form key.</summary>
    /// <param name="floors">The number of doublings.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedCayleyDicksonMultiply(int floors) {
        FixedLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= CayleyDicksonBinding(floors: floors);

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The subject <see cref="DoublingAlgebra{TInner}"/> octonion product, lanes in tower-index order.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DoublingOctonionMultiply(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        WriteOctonion(
            value: LeafOctonion.Multiply(
                left: ReadOctonion(lanes: left),
                right: ReadOctonion(lanes: right)
            ),
            lanes: result
        );
    /// <summary>The subject <see cref="DoublingAlgebra{TInner}"/> octonion associator, lanes in tower-index order.</summary>
    /// <param name="a">The first operand's lanes.</param>
    /// <param name="b">The second operand's lanes.</param>
    /// <param name="c">The third operand's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DoublingOctonionAssociator(ReadOnlySpan<long> a, ReadOnlySpan<long> b, ReadOnlySpan<long> c, Span<long> result) =>
        WriteOctonion(
            value: LeafOctonion.Associator(
                left: ReadOctonion(lanes: a),
                middle: ReadOctonion(lanes: b),
                right: ReadOctonion(lanes: c)
            ),
            lanes: result
        );
    /// <summary>The subject associator of the presented Cayley–Dickson tower, formed as
    /// <c>(a·b)·c + −(a·(b·c))</c> through the algebra's own add and the material's negation.</summary>
    /// <param name="floors">The number of doublings.</param>
    /// <returns>The bound operation.</returns>
    public static VectorTernaryOp PresentedCayleyDicksonAssociator(int floors) {
        FixedLaneAlgebra? binding = null;

        return (a, b, c, result) => {
            binding ??= CayleyDicksonBinding(floors: floors);

            binding.Associator(
                a: a,
                b: b,
                c: c,
                result: result
            );
        };
    }
    /// <summary>The subject product of the presented monogenic algebra <c>xⁿ ≡ tail</c> over <see cref="ParityMaterial"/>
    /// — the binary field of that degree; a lane is a coefficient bit and IS the normal-form key.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <param name="reductionTail">The modulus below its leading term, as a coefficient bitmask.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedBinaryFieldMultiply(int degree, ulong reductionTail) {
        ParityLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= new ParityLaneAlgebra(
                degree: degree,
                reductionTail: reductionTail
            );

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The shared-nothing <c>GF(2^degree)</c> oracle, by schoolbook carryless multiply and bit-by-bit
    /// reduction.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <param name="reductionTail">The modulus below its leading term.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp BinaryFieldProductOracle(int degree, ulong reductionTail) =>
        (left, right, result) => {
            var product = Oracles.BinaryFieldProduct(
                left: PackBits(lanes: left),
                right: PackBits(lanes: right),
                degree: degree,
                reductionTail: reductionTail
            );

            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = ((long)((product >> lane) & BigInteger.One)); }
        };
    /// <summary>The subject <see cref="BinaryField{T}"/> product at degree eight.</summary>
    /// <param name="left">The multiplicand's coefficient bits.</param>
    /// <param name="right">The multiplier's coefficient bits.</param>
    /// <param name="result">The destination coefficient bits.</param>
    public static void BinaryFieldMultiply8(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        UnpackBits(
            value: BinaryFields.Degree8.Multiply(
                left: ((byte)PackBits(lanes: left)),
                right: ((byte)PackBits(lanes: right))
            ),
            lanes: result
        );
    /// <summary>The subject <see cref="BinaryField{T}"/> product at degree sixteen.</summary>
    /// <param name="left">The multiplicand's coefficient bits.</param>
    /// <param name="right">The multiplier's coefficient bits.</param>
    /// <param name="result">The destination coefficient bits.</param>
    public static void BinaryFieldMultiply16(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        UnpackBits(
            value: BinaryFields.Degree16.Multiply(
                left: ((ushort)PackBits(lanes: left)),
                right: ((ushort)PackBits(lanes: right))
            ),
            lanes: result
        );
    /// <summary>The subject product of the presented monogenic algebra of the relation <c>x² = P·x + Q</c> over the house
    /// scalar — the derived form of <see cref="QuadraticAlgebra{TScalar}"/>, whose key IS the exponent.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedQuadraticMultiply(long pRaw, long qRaw) {
        FixedLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= QuadraticBinding(
                pRaw: pRaw,
                qRaw: qRaw
            );

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> product as a two-lane vector operation.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp QuadraticMultiplyLanes(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (left, right, result) => {
            var product = algebra.Multiply(
                left: new(
                    U: Raw(value: left[0]),
                    V: Raw(value: left[1])
                ),
                right: new(
                    U: Raw(value: right[0]),
                    V: Raw(value: right[1])
                )
            );

            result[0] = product.U.Value;
            result[1] = product.V.Value;
        };
    }
    /// <summary>The subject presented power of the adjoined root, by the pinned ascending-bit schedule.</summary>
    /// <returns>The bound operation. The presentation is rebuilt only when the relation moves, so a ladder of exponents
    /// over one relation constructs it once.</returns>
    public static PowerOp PresentedRootPower() {
        FixedLaneAlgebra? binding = null;
        var boundP = 0L;
        var boundQ = 0L;

        return (p, q, exponent) => {
            if (
                (binding is null) ||
                (boundP != p) ||
                (boundQ != q)
            ) {
                binding = QuadraticBinding(
                    pRaw: p,
                    qRaw: q
                );
                boundP = p;
                boundQ = q;
            }

            var power = binding.Algebra.Power(
                value: binding.Algebra.Generator(symbol: 0),
                exponent: exponent
            );

            return (power[0L].Value, power[1L].Value);
        };
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}.CompanionPower"/>.</summary>
    /// <param name="p">The linear coefficient, raw Q16.</param>
    /// <param name="q">The constant coefficient, raw Q16.</param>
    /// <param name="exponent">The power.</param>
    /// <returns>The result components.</returns>
    public static (long U, long V) CompanionRootPower(long p, long q, ulong exponent) {
        var element = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: p),
            q: Raw(value: q)
        ).CompanionPower(exponent: exponent);

        return (element.U.Value, element.V.Value);
    }

    // ---- the algebraic path problem: ONE quiver presentation at three materials ----
    //
    // The operand pair encodes a weighted digraph on GraphOrder vertices: lane i·n + j is the arc i → j, present when
    // the right operand's low bit is set, and carrying the left operand's low sixteen raw bits as its weight. A
    // non-negative weight below one keeps every path sum of a four-vertex graph exactly representable, so the tropical
    // statement is about (min, +) and never about wrapping.

    /// <summary>The number of vertices every graph law runs on; the lane count is its square.</summary>
    public const int GraphOrder = 4;

    /// <summary>The subject reflexive-transitive closure: the guarded sum over all lengths of a Boolean quiver
    /// element.</summary>
    /// <returns>The bound operation. The presentation is built on first use and owned by this closure alone, because
    /// the kernel's working buffers are per-instance mutable state.</returns>
    public static VectorBinaryOp PresentedBooleanStar() {
        PresentedAlgebra<bool, BooleanMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<bool, BooleanMaterial>.Create(presentation: CodiscreteQuiver<bool, BooleanMaterial>(
                material: default,
                order: GraphOrder
            ));

            var count = right.Length;
            var coefficients = new bool[count];
            var keys = new long[count];
            var support = 0;

            for (var lane = 0; (lane < count); ++lane) {
                if (0L == (right[lane] & 1L)) { continue; }

                coefficients[support] = true;
                keys[support] = lane;
                ++support;
            }

            var element = algebra.FromSupport(
                keys: keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );

            result.Clear();

            if (!algebra.TrySumOverAllLengths(
                obstruction: out _,
                total: out var total,
                value: element
            )) {
                for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = -1L; }

                return;
            }

            for (var index = 0; (index < total.SupportCount); ++index) {
                result[((int)total.Keys[index])] = (total.Coefficients[index]
                    ? 1L
                    : 0L
                );
            }
        };
    }
    /// <summary>The shared-nothing reflexive-transitive closure oracle.</summary>
    /// <param name="left">The weight lanes, unread.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <param name="result">The closure lanes.</param>
    public static void BooleanStarOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var count = right.Length;
        var adjacency = new bool[count];
        var closure = new bool[count];

        for (var lane = 0; (lane < count); ++lane) { adjacency[lane] = (0L != (right[lane] & 1L)); }

        Oracles.BooleanTransitiveClosure(
            adjacency: adjacency,
            order: GraphOrder,
            result: closure
        );

        for (var lane = 0; (lane < count); ++lane) {
            result[lane] = (closure[lane]
            ? 1L
            : 0L
        );
        }
    }
    /// <summary>The subject all-pairs shortest path: the guarded sum over all lengths of a tropical quiver element.</summary>
    /// <returns>The bound operation, owning its own presentation.</returns>
    public static VectorBinaryOp PresentedTropicalStar() {
        PresentedAlgebra<FixedQ4816, TropicalMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<FixedQ4816, TropicalMaterial>.Create(presentation: CodiscreteQuiver<FixedQ4816, TropicalMaterial>(
                material: default,
                order: GraphOrder
            ));

            var count = right.Length;
            var coefficients = new FixedQ4816[count];
            var keys = new long[count];
            var support = 0;

            for (var lane = 0; (lane < count); ++lane) {
                if (0L == (right[lane] & 1L)) { continue; }

                coefficients[support] = Raw(value: GraphWeight(raw: left[lane]));
                keys[support] = lane;
                ++support;
            }

            var element = algebra.FromSupport(
                keys: keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );

            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = long.MaxValue; }

            if (!algebra.TrySumOverAllLengths(
                obstruction: out _,
                total: out var total,
                value: element
            )) {
                for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = -1L; }

                return;
            }

            for (var index = 0; (index < total.SupportCount); ++index) {
                result[((int)total.Keys[index])] = total.Coefficients[index].Value;
            }
        };
    }
    /// <summary>The shared-nothing all-pairs shortest path oracle.</summary>
    /// <param name="left">The weight lanes.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <param name="result">The distance lanes.</param>
    public static void TropicalStarOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var count = right.Length;
        var weights = new long[count];

        for (var lane = 0; (lane < count); ++lane) {
            weights[lane] = ((0L == (right[lane] & 1L))
                ? long.MaxValue
                : GraphWeight(raw: left[lane])
            );
        }

        Oracles.TropicalShortestPath(
            order: GraphOrder,
            result: result,
            weights: weights
        );
    }
    /// <summary>The subject walk count: a power of a counting quiver element, by the pinned ascending-bit schedule, with
    /// the sequential schedule pinned against it in the same pass (the counting material is exact, so the two agree).</summary>
    /// <param name="length">The walk length.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedWalkCount(int length) {
        PresentedAlgebra<BigInteger, CountingMaterial>? owned = null;

        return (left, right, result) => {
            var algebra = (owned ??= PresentedAlgebra<BigInteger, CountingMaterial>.Create(presentation: CodiscreteQuiver<BigInteger, CountingMaterial>(
                material: default,
                order: GraphOrder
            )));
            var element = CountingAdjacency(
                algebra: algebra,
                left: left,
                right: right
            );
            var power = algebra.Power(
                exponent: ((ulong)length),
                value: element
            );
            var sequential = algebra.PowerSequential(
                exponent: ((ulong)length),
                value: element
            );

            result.Clear();

            for (var index = 0; (index < power.SupportCount); ++index) {
                result[((int)power.Keys[index])] = ((long)power.Coefficients[index]);
            }

            // The sequential schedule is a distinct public member with its own contract; over an exact material the two
            // must land on the same value, and a divergence is reported through the same lane comparison as everything
            // else by poisoning the result rather than by asserting here.
            for (var index = 0; (index < sequential.SupportCount); ++index) {
                if (sequential.Coefficients[index] != power[sequential.Keys[index]]) { result[((int)sequential.Keys[index])] = long.MinValue; }
            }

            if (sequential.SupportCount != power.SupportCount) { result[0] = long.MinValue; }
        };
    }
    /// <summary>The shared-nothing walk-count oracle, by repeated <see cref="BigInteger"/> matrix multiplication.</summary>
    /// <param name="length">The walk length.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp WalkCountOracle(int length) =>
        (left, right, result) => {
            var count = right.Length;
            var adjacency = new BigInteger[count];
            var counts = new BigInteger[count];

            for (var lane = 0; (lane < count); ++lane) {
                adjacency[lane] = GraphMultiplicity(
                left: left[lane],
                right: right[lane]
            );
            }

            Oracles.WalkCount(
                adjacency: adjacency,
                length: length,
                order: GraphOrder,
                result: counts
            );

            for (var lane = 0; (lane < count); ++lane) { result[lane] = ((long)counts[lane]); }
        };

    /// <summary>Builds the counting quiver's adjacency element from an operand pair.</summary>
    /// <param name="algebra">The counting quiver algebra.</param>
    /// <param name="left">The multiplicity lanes.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <returns>The adjacency element.</returns>
    private static PresentedAlgebra<BigInteger, CountingMaterial>.Element CountingAdjacency(PresentedAlgebra<BigInteger, CountingMaterial> algebra, ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        var count = right.Length;
        var coefficients = new BigInteger[count];
        var keys = new long[count];
        var support = 0;

        for (var lane = 0; (lane < count); ++lane) {
            var multiplicity = GraphMultiplicity(
                left: left[lane],
                right: right[lane]
            );

            if (multiplicity.IsZero) { continue; }

            coefficients[support] = multiplicity;
            keys[support] = lane;
            ++support;
        }

        return algebra.FromSupport(
            keys: keys.AsSpan(
                length: support,
                start: 0
            ),
            coefficients: coefficients.AsSpan(
                length: support,
                start: 0
            )
        );
    }
    /// <summary>Builds the codiscrete quiver on a given number of objects at any material: every ordered pair is an
    /// arrow, so the algebra IS the matrix algebra of that order.</summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <param name="order">The number of objects.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation.</returns>
    private static ChargedPresentation<TValue, TOps> CodiscreteQuiver<TValue, TOps>(int order, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var arrows = new (int Source, int Target, TValue Weight)[(order * order)];

        for (var source = 0; (source < order); ++source) {
            for (var target = 0; (target < order); ++target) {
                arrows[((source * order) + target)] = (source, target, material.One);
            }
        }

        return Presentations.Quiver<TValue, TOps>(
            arrows: arrows,
            material: material,
            objectCount: order
        );
    }

}
