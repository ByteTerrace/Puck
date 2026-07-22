using System.Numerics;
using System.Runtime.Intrinsics.X86;
using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. Proves the binary-field contract that a determinism gate cannot see: that every accelerated path in
/// <see cref="BinaryField{T}"/> produces exactly what its portable fallback produces. The published carryless-multiply
/// control-byte vectors are asserted first, because a mis-wired operand half is the classic silent-wrong-answer bug at
/// that seam and every later check inherits it. The five canonical moduli are then re-proved irreducible, the whole
/// degree-8 multiplication and inversion tables are compared against an independent <see cref="BigInteger"/> oracle
/// written from the definitions, and every bulk region rung this machine supports is executed against the
/// element-at-a-time rung over the full byte cross-product and every short length. Finally the stage relaunches itself
/// as feature-suppressed children and requires an identical workload digest from each — and requires each child to
/// report a genuinely weakened instruction-set vector, so a renamed runtime knob fails the stage instead of quietly
/// turning every child into a second copy of the parent.
/// </summary>
internal sealed class BinaryFieldStage : IPostStage {
    /// <summary>The degree-8 moduli the region rungs are swept under: the canonical tail and two further irreducibles, so a rung that has quietly bound itself to one modulus cannot pass.</summary>
    private static ReadOnlySpan<byte> ByteRegionTails =>
        [0x1B, 0x2D, 0x9F];
    /// <summary>The region rungs that operate on byte-wide fields, widest first.</summary>
    private static ReadOnlySpan<BinaryFieldRegionTier> ByteRegionTiers =>
        [
            BinaryFieldRegionTier.Affine512,
            BinaryFieldRegionTier.Split512,
            BinaryFieldRegionTier.Affine256,
            BinaryFieldRegionTier.Split256,
            BinaryFieldRegionTier.Affine128,
            BinaryFieldRegionTier.Split128,
        ];
    /// <summary>The longest region the length sweep covers: four whole 512-bit vectors and a partial one.</summary>
    private const int MaximumSweptLength = ((4 * 64) + 3);
    /// <summary>The degree-16 moduli the sixteen-bit region rungs are swept under.</summary>
    private static ReadOnlySpan<ushort> WideRegionTails =>
        [0x2B, 0x47];
    /// <summary>The region rungs that operate on sixteen-bit fields; there is deliberately no nibble-split rung at this width.</summary>
    private static ReadOnlySpan<BinaryFieldRegionTier> WideRegionTiers =>
        [BinaryFieldRegionTier.Affine512, BinaryFieldRegionTier.Affine256, BinaryFieldRegionTier.Affine128];

    /// <inheritdoc/>
    public string Name => "binary-field";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var failure = (CheckCarrylessVectors() ?? CheckModuli()) ?? (CheckSmallDegrees() ?? CheckDegree8());

        if (failure is not null) {
            return PostStageOutcome.Fail(detail: failure);
        }

        failure = (CheckPublishedByteVectors() ?? CheckByteRegionTiers()) ?? (CheckWideRegionTiers() ?? CheckRegionLengths());

        if (failure is not null) {
            return PostStageOutcome.Fail(detail: failure);
        }

        var notes = new List<string>();

        failure = RunChildren(notes: notes);

        if (failure is not null) {
            return PostStageOutcome.Fail(detail: failure);
        }

        return PostStageOutcome.Pass(detail: $"published carryless and byte-field vectors, five irreducible moduli, the exhaustive degree-8 and degree-4 tables against a BigInteger oracle, and every region rung ({DescribeSupportedTiers()}) against the scalar rung all agree; {string.Join(separator: "; ", values: notes)}");
    }

    /// <summary>Compares every supported byte-wide region rung against the element-at-a-time rung over the whole scalar-by-element cross-product.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every rung agrees.</returns>
    private static string? CheckByteRegionTiers() {
        Span<byte> actual = stackalloc byte[256];
        Span<byte> expected = stackalloc byte[256];
        Span<byte> source = stackalloc byte[256];

        for (var index = 0; (index < 256); ++index) {
            source[index] = ((byte)index);
        }

        foreach (var tier in ByteRegionTiers) {
            if (!BinaryFieldKernels.IsRegionTierSupported(tier: tier)) {
                continue;
            }

            foreach (var tail in ByteRegionTails) {
                for (var scalar = 0; (scalar < 256); ++scalar) {
                    for (var accumulate = 0; (accumulate < 2); ++accumulate) {
                        source.CopyTo(destination: actual);
                        source.CopyTo(destination: expected);
                        BinaryFieldKernels.MultiplyAccumulateRegionScalar(destination: expected, source: source, scalar: ((byte)scalar), accumulate: (1 == accumulate), degree: 8, tail: tail);
                        RunByteRegionTier(tier: tier, destination: actual, source: source, scalar: ((byte)scalar), accumulate: (1 == accumulate), degree: 8, tail: tail);

                        if (!actual.SequenceEqual(other: expected)) {
                            return $"region rung {tier} disagreed with the scalar rung at degree 8, tail 0x{tail:X2}, scalar 0x{scalar:X2}, accumulate {1 == accumulate}";
                        }
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Asserts the published carryless-multiply reference vectors against both scalar tiers.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when both tiers reproduce all four vectors.</returns>
    /// <remarks>The four vectors are the products of the two published operands' halves, so together they pin which operand each selected half came from — the mapping the hardware instruction takes as a control byte and the one place a transposition would be silent everywhere else.</remarks>
    private static string? CheckCarrylessVectors() {
        var vectors = new (ulong Left, ulong Right, ulong Low, ulong High)[] {
            (Left: 0x63746F725D53475DUL, Right: 0x5B477565726F6E5DUL, Low: 0x929633D5D36F0451UL, High: 0x1D4D84C85C3440C0UL),
            (Left: 0x7B5B546573745665UL, Right: 0x5B477565726F6E5DUL, Low: 0xBABF262DF4B7D5C9UL, High: 0x1A2BF6DB3A30862FUL),
            (Left: 0x63746F725D53475DUL, Right: 0x4869285368617929UL, Low: 0x7FA540AC2A281315UL, High: 0x1BD17C8D556AB5A1UL),
            (Left: 0x7B5B546573745665UL, Right: 0x4869285368617929UL, Low: 0xD66EE03E410FD4EDUL, High: 0x1D1E1F2C592E7C45UL),
        };

        foreach (var vector in vectors) {
            var portable = BinaryFieldKernels.CarrylessMultiply64Portable(left: vector.Left, right: vector.Right);

            if ((portable.Low != vector.Low) || (portable.High != vector.High)) {
                return $"the portable carryless multiply of 0x{vector.Left:X16} and 0x{vector.Right:X16} gave 0x{portable.High:X16}_{portable.Low:X16}, the published vector is 0x{vector.High:X16}_{vector.Low:X16}";
            }

            if (!Pclmulqdq.IsSupported) {
                continue;
            }

            var hardware = BinaryFieldKernels.CarrylessMultiply64Carryless(left: vector.Left, right: vector.Right);

            if ((hardware.Low != vector.Low) || (hardware.High != vector.High)) {
                return $"the hardware carryless multiply of 0x{vector.Left:X16} and 0x{vector.Right:X16} gave 0x{hardware.High:X16}_{hardware.Low:X16}, the published vector is 0x{vector.High:X16}_{vector.Low:X16}";
            }
        }

        return null;
    }
    /// <summary>Compares the whole degree-8 field against the oracle and pins its multiplicative group.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when the field agrees with the oracle.</returns>
    private static string? CheckDegree8() {
        var field = BinaryFields.Degree8;
        var fullOrder = 0;

        for (var left = 0; (left < 256); ++left) {
            for (var right = 0; (right < 256); ++right) {
                var product = field.Multiply(left: ((byte)left), right: ((byte)right));
                var expected = OracleFieldMultiply(left: ((ulong)left), right: ((ulong)right), degree: 8, tail: 0x1BUL);

                if (product != expected) {
                    return $"degree-8 multiply of 0x{left:X2} and 0x{right:X2} gave 0x{product:X2}, the oracle gives 0x{expected:X2}";
                }
            }

            if (0 == left) {
                continue;
            }

            var inverse = field.Inverse(value: ((byte)left));
            var oracleInverse = OracleInverse(value: ((ulong)left), degree: 8, tail: 0x1BUL);

            if ((inverse != oracleInverse) || (1 != field.Multiply(left: ((byte)left), right: inverse))) {
                return $"degree-8 inverse of 0x{left:X2} gave 0x{inverse:X2}, the oracle gives 0x{oracleInverse:X2}";
            }

            if (1 != field.Exponentiate(value: ((byte)left), exponent: 255UL)) {
                return $"degree-8 element 0x{left:X2} raised to the group order is not one";
            }

            if (IsGenerator(field: field, value: ((byte)left))) {
                ++fullOrder;
            }
        }

        if (128 != fullOrder) {
            return $"the degree-8 multiplicative group has {fullOrder} elements of order 255, the group has 128";
        }

        return null;
    }
    /// <summary>Re-proves every modulus the stage and the library rely on irreducible.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every modulus is irreducible.</returns>
    /// <remarks>The constants are never trusted on their word: an irreducibility decision at run time removes the whole "the table has a typo" failure class, and it is what makes an inverse table meaningful at all.</remarks>
    private static string? CheckModuli() {
        if (!BinaryFields.Degree8.IsIrreducible() || !BinaryFields.Degree16.IsIrreducible() ||
            !BinaryFields.Degree32.IsIrreducible() || !BinaryFields.Degree64.IsIrreducible() ||
            !BinaryFields.Degree128.IsIrreducible()) {
            return "one of the five canonical binary-field moduli is not irreducible";
        }

        foreach (var tail in ByteRegionTails) {
            if (!BinaryField<byte>.Create(degree: 8, reductionTail: tail).IsIrreducible()) {
                return $"the degree-8 sweep modulus with tail 0x{tail:X2} is not irreducible";
            }
        }

        foreach (var tail in WideRegionTails) {
            if (!BinaryField<ushort>.Create(degree: 16, reductionTail: tail).IsIrreducible()) {
                return $"the degree-16 sweep modulus with tail 0x{tail:X4} is not irreducible";
            }
        }

        return null;
    }
    /// <summary>Asserts the published byte-field product vectors on the scalar path and through every supported region rung.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every path reproduces them.</returns>
    private static string? CheckPublishedByteVectors() {
        var field = BinaryFields.Degree8;
        var vectors = new (byte Left, byte Right, byte Product)[] {
            (Left: 0x53, Right: 0xCA, Product: 0x01),
            (Left: 0x57, Right: 0x83, Product: 0xC1),
            (Left: 0x57, Right: 0x13, Product: 0xFE),
        };

        Span<byte> actual = stackalloc byte[64];
        Span<byte> source = stackalloc byte[64];

        foreach (var vector in vectors) {
            var product = field.Multiply(left: vector.Left, right: vector.Right);

            if (product != vector.Product) {
                return $"the published byte-field product 0x{vector.Left:X2} times 0x{vector.Right:X2} gave 0x{product:X2}, the published value is 0x{vector.Product:X2}";
            }

            source.Fill(value: vector.Right);

            foreach (var tier in ByteRegionTiers) {
                if (!BinaryFieldKernels.IsRegionTierSupported(tier: tier)) {
                    continue;
                }

                actual.Clear();
                RunByteRegionTier(tier: tier, destination: actual, source: source, scalar: vector.Left, accumulate: false, degree: 8, tail: 0x1B);

                foreach (var element in actual) {
                    if (element != vector.Product) {
                        return $"region rung {tier} gave 0x{element:X2} for the published byte-field product 0x{vector.Left:X2} times 0x{vector.Right:X2}, the published value is 0x{vector.Product:X2}";
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Compares every supported region rung against the scalar rung at every length through four whole widest vectors and a partial one.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every rung agrees at every length.</returns>
    /// <remarks>The tail past the last whole vector is where a region kernel fails, so the short lengths are covered exhaustively rather than sampled.</remarks>
    private static string? CheckRegionLengths() {
        var actual = new byte[MaximumSweptLength];
        var expected = new byte[MaximumSweptLength];
        var source = new byte[MaximumSweptLength];
        var wideActual = new ushort[MaximumSweptLength];
        var wideExpected = new ushort[MaximumSweptLength];
        var wideSeed = new ushort[MaximumSweptLength];
        var wideSource = new ushort[MaximumSweptLength];

        for (var index = 0; (index < MaximumSweptLength); ++index) {
            source[index] = ((byte)((index * 31) + 7));
            wideSeed[index] = ((ushort)((index * 3_119) + 11));
            wideSource[index] = ((ushort)((index * 7_919) + 13));
        }

        foreach (var scalar in (byte[])[0x00, 0x01, 0x1D, 0xFF]) {
            for (var length = 0; (length <= MaximumSweptLength); ++length) {
                foreach (var tier in ByteRegionTiers) {
                    if (!BinaryFieldKernels.IsRegionTierSupported(tier: tier)) {
                        continue;
                    }

                    for (var accumulate = 0; (accumulate < 2); ++accumulate) {
                        source.AsSpan(start: 0, length: length).CopyTo(destination: actual);
                        source.AsSpan(start: 0, length: length).CopyTo(destination: expected);
                        BinaryFieldKernels.MultiplyAccumulateRegionScalar(destination: expected.AsSpan(start: 0, length: length), source: source.AsSpan(start: 0, length: length), scalar: scalar, accumulate: (1 == accumulate), degree: 8, tail: ((byte)0x1BU));
                        RunByteRegionTier(tier: tier, destination: actual.AsSpan(start: 0, length: length), source: source.AsSpan(start: 0, length: length), scalar: scalar, accumulate: (1 == accumulate), degree: 8, tail: 0x1B);

                        if (!actual.AsSpan(start: 0, length: length).SequenceEqual(other: expected.AsSpan(start: 0, length: length))) {
                            return $"region rung {tier} disagreed with the scalar rung at length {length}, scalar 0x{scalar:X2}, accumulate {1 == accumulate}";
                        }
                    }
                }

                foreach (var tier in WideRegionTiers) {
                    if (!BinaryFieldKernels.IsRegionTierSupported(tier: tier)) {
                        continue;
                    }

                    for (var accumulate = 0; (accumulate < 2); ++accumulate) {
                        var wideScalar = ((ushort)((scalar * 259) + 1));

                        wideSeed.AsSpan(start: 0, length: length).CopyTo(destination: wideActual);
                        wideSeed.AsSpan(start: 0, length: length).CopyTo(destination: wideExpected);
                        BinaryFieldKernels.MultiplyAccumulateRegionScalar(destination: wideExpected.AsSpan(start: 0, length: length), source: wideSource.AsSpan(start: 0, length: length), scalar: wideScalar, accumulate: (1 == accumulate), degree: 16, tail: ((ushort)0x2BU));
                        RunWideRegionTier(tier: tier, destination: wideActual.AsSpan(start: 0, length: length), source: wideSource.AsSpan(start: 0, length: length), scalar: wideScalar, accumulate: (1 == accumulate), degree: 16, tail: 0x2B);

                        if (!wideActual.AsSpan(start: 0, length: length).SequenceEqual(other: wideExpected.AsSpan(start: 0, length: length))) {
                            return $"sixteen-bit region rung {tier} disagreed with the scalar rung at length {length}, scalar 0x{wideScalar:X4}, accumulate {1 == accumulate}";
                        }
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Compares the small-degree fields against the oracle, where a degree below the carrier's width is what a masked-split bug shows up in.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every small field agrees with the oracle.</returns>
    private static string? CheckSmallDegrees() {
        foreach (var tail in (byte[])[0x3, 0x9, 0xF]) {
            var field = BinaryField<byte>.Create(degree: 4, reductionTail: tail);

            if (!field.IsIrreducible()) {
                return $"the degree-4 modulus with tail 0x{tail:X1} is not irreducible";
            }

            for (var left = 0; (left < 16); ++left) {
                for (var right = 0; (right < 16); ++right) {
                    var product = field.Multiply(left: ((byte)left), right: ((byte)right));
                    var expected = OracleFieldMultiply(left: ((ulong)left), right: ((ulong)right), degree: 4, tail: tail);

                    if (product != expected) {
                        return $"degree-4 multiply of 0x{left:X1} and 0x{right:X1} under tail 0x{tail:X1} gave 0x{product:X1}, the oracle gives 0x{expected:X1}";
                    }
                }

                if (0 == left) {
                    continue;
                }

                var inverse = field.Inverse(value: ((byte)left));
                var oracleInverse = OracleInverse(value: ((ulong)left), degree: 4, tail: tail);

                if (inverse != oracleInverse) {
                    return $"degree-4 inverse of 0x{left:X1} under tail 0x{tail:X1} gave 0x{inverse:X1}, the oracle gives 0x{oracleInverse:X1}";
                }
            }
        }

        // GF(2) itself is a real instantiation, not a corner case: t + 1 divides every t^n + 1, so cyclic-incidence
        // analysis builds this field on every run.
        var binary = BinaryField<byte>.Create(degree: 1, reductionTail: 0x1);

        if (!binary.IsIrreducible() || (1 != binary.Inverse(value: 1)) || (1 != binary.Multiply(left: 1, right: 1)) ||
            (0 != binary.Multiply(left: 1, right: 0)) || (1 != binary.Exponentiate(value: 0, exponent: 0UL))) {
            return "the two-element field does not behave as GF(2)";
        }

        try {
            _ = binary.Inverse(value: 0);

            return "inverting zero did not throw";
        } catch (DivideByZeroException) {
            return null;
        }
    }
    /// <summary>Compares every supported sixteen-bit region rung against the element-at-a-time rung over the whole element range.</summary>
    /// <returns>The failure detail, or <see langword="null"/> when every rung agrees.</returns>
    /// <remarks>The sixteen-bit rungs split the operation's bit matrix into four byte-wide blocks, so the sweep crosses a spread of scalars with every element the carrier holds rather than sampling both.</remarks>
    private static string? CheckWideRegionTiers() {
        var actual = new ushort[65_536];
        var expected = new ushort[65_536];
        var source = new ushort[65_536];

        for (var index = 0; (index < 65_536); ++index) {
            source[index] = ((ushort)index);
        }

        foreach (var tier in WideRegionTiers) {
            if (!BinaryFieldKernels.IsRegionTierSupported(tier: tier)) {
                continue;
            }

            foreach (var tail in WideRegionTails) {
                foreach (var scalar in (ushort[])[0x0000, 0x0001, 0x0002, 0x00FF, 0x0100, 0x1234, 0x8000, 0xFFFF]) {
                    for (var accumulate = 0; (accumulate < 2); ++accumulate) {
                        source.CopyTo(array: actual, index: 0);
                        source.CopyTo(array: expected, index: 0);
                        BinaryFieldKernels.MultiplyAccumulateRegionScalar(destination: expected, source: source, scalar: scalar, accumulate: (1 == accumulate), degree: 16, tail: tail);
                        RunWideRegionTier(tier: tier, destination: actual, source: source, scalar: scalar, accumulate: (1 == accumulate), degree: 16, tail: tail);

                        if (!actual.AsSpan().SequenceEqual(other: expected)) {
                            return $"sixteen-bit region rung {tier} disagreed with the scalar rung at degree 16, tail 0x{tail:X4}, scalar 0x{scalar:X4}, accumulate {1 == accumulate}";
                        }
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Describes the region rungs this machine can execute.</summary>
    /// <returns>The supported rungs, comma separated.</returns>
    private static string DescribeSupportedTiers() {
        var supported = new List<string>();

        foreach (var tier in ByteRegionTiers) {
            if (BinaryFieldKernels.IsRegionTierSupported(tier: tier)) {
                supported.Add(item: tier.ToString());
            }
        }

        supported.Add(item: BinaryFieldRegionTier.Scalar.ToString());

        return string.Join(separator: ", ", values: supported);
    }
    /// <summary>Gets whether an element generates the whole multiplicative group of the degree-8 field.</summary>
    /// <param name="field">The degree-8 field.</param>
    /// <param name="value">The non-zero element to test.</param>
    /// <returns><see langword="true"/> when the element has order 255; otherwise <see langword="false"/>.</returns>
    private static bool IsGenerator(BinaryField<byte> field, byte value) {
        foreach (var divisor in (ulong[])[1UL, 3UL, 5UL, 15UL, 17UL, 51UL, 85UL]) {
            if (1 == field.Exponentiate(value: value, exponent: divisor)) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Multiplies two packed polynomials over the two-element field, from the definition.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The exact product.</returns>
    /// <remarks>The oracle carries coefficients in an unbounded integer, so it has no limbs, no reduction tail, no combs, and no lanes to get wrong in the same way the implementation could.</remarks>
    private static BigInteger OracleMultiply(BigInteger left, BigInteger right) {
        var product = BigInteger.Zero;
        var remaining = right;
        var shifted = left;

        while (BigInteger.Zero != remaining) {
            if (!remaining.IsEven) {
                product ^= shifted;
            }

            remaining >>= 1;
            shifted <<= 1;
        }

        return product;
    }
    /// <summary>Divides one packed polynomial by another over the two-element field, by schoolbook long division.</summary>
    /// <param name="dividend">The dividend.</param>
    /// <param name="divisor">The non-zero divisor.</param>
    /// <returns>The quotient and the remainder.</returns>
    private static (BigInteger Quotient, BigInteger Remainder) OracleDivRem(BigInteger dividend, BigInteger divisor) {
        var divisorDegree = (((int)divisor.GetBitLength()) - 1);
        var quotient = BigInteger.Zero;
        var remainder = dividend;

        for (var degree = (((int)remainder.GetBitLength()) - 1); (degree >= divisorDegree); degree = (((int)remainder.GetBitLength()) - 1)) {
            quotient ^= (BigInteger.One << (degree - divisorDegree));
            remainder ^= (divisor << (degree - divisorDegree));
        }

        return (Quotient: quotient, Remainder: remainder);
    }
    /// <summary>Multiplies two field elements from the definition, reconstructing the whole modulus from its degree and tail.</summary>
    /// <param name="left">The reduced first factor.</param>
    /// <param name="right">The reduced second factor.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The reduced product.</returns>
    /// <remarks>The oracle rebuilds the modulus as <c>t^degree + tail</c>, so an implementation that ever dropped the implicit leading term would disagree here immediately.</remarks>
    private static ulong OracleFieldMultiply(ulong left, ulong right, int degree, ulong tail) {
        return ((ulong)OracleDivRem(
            dividend: OracleMultiply(left: left, right: right),
            divisor: ((BigInteger.One << degree) | tail)
        ).Remainder);
    }
    /// <summary>Inverts a field element by the extended Euclidean algorithm, which shares no structure with the shipped Frobenius chain.</summary>
    /// <param name="value">The reduced, non-zero element to invert.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    /// <returns>The element's inverse.</returns>
    private static ulong OracleInverse(ulong value, int degree, ulong tail) {
        var coefficient = BigInteger.One;
        var previousCoefficient = BigInteger.Zero;
        var previousRemainder = ((BigInteger.One << degree) | tail);
        var remainder = ((BigInteger)value);

        while (BigInteger.Zero != remainder) {
            var division = OracleDivRem(dividend: previousRemainder, divisor: remainder);

            (previousRemainder, remainder) = (remainder, division.Remainder);
            (previousCoefficient, coefficient) = (coefficient, (previousCoefficient ^ OracleMultiply(left: division.Quotient, right: coefficient)));
        }

        return ((ulong)previousCoefficient);
    }
    /// <summary>Runs one named byte-wide region rung directly, bypassing the ladder's own support and length gates.</summary>
    /// <param name="tier">The rung to run.</param>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    private static void RunByteRegionTier(BinaryFieldRegionTier tier, Span<byte> destination, ReadOnlySpan<byte> source, byte scalar, bool accumulate, int degree, byte tail) {
        switch (tier) {
            case BinaryFieldRegionTier.Affine512:
                BinaryFieldKernels.MultiplyAccumulateRegionAffine512(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            case BinaryFieldRegionTier.Split512:
                BinaryFieldKernels.MultiplyAccumulateRegionSplit512(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            case BinaryFieldRegionTier.Affine256:
                BinaryFieldKernels.MultiplyAccumulateRegionAffine256(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            case BinaryFieldRegionTier.Split256:
                BinaryFieldKernels.MultiplyAccumulateRegionSplit256(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            case BinaryFieldRegionTier.Affine128:
                BinaryFieldKernels.MultiplyAccumulateRegionAffine128(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            case BinaryFieldRegionTier.Split128:
                BinaryFieldKernels.MultiplyAccumulateRegionSplit128(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            default:
                BinaryFieldKernels.MultiplyAccumulateRegionScalar(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
        }
    }
    /// <summary>Relaunches this executable under each instruction-set suppression configuration and requires an identical digest and a genuinely weakened support vector.</summary>
    /// <param name="notes">Receives one summary line per child.</param>
    /// <returns>The failure detail, or <see langword="null"/> when every child agreed.</returns>
    /// <remarks>
    /// The knob names are the measured ones. Several plausible-looking alternatives are silent no-ops, and a verifier
    /// spelling one of them would report a passing fallback comparison while running the hardware path on both sides —
    /// which is exactly why a child that fails to weaken the gates it was launched to weaken fails the stage rather
    /// than being counted as agreement.
    /// </remarks>
    private static string? RunChildren(List<string> notes) {
        var parentDigest = BinaryFieldProbe.ComputeDigest();
        var parentSets = BinaryFieldProbe.InstructionSets();
        var children = new (string Name, string[] Knobs, string[] Suppressed)[] {
            (Name: "hardware-intrinsics", Knobs: ["DOTNET_EnableHWIntrinsic"], Suppressed: [.. parentSets.Keys]),
            (Name: "carryless-multiply", Knobs: ["DOTNET_EnableAES"], Suppressed: ["Pclmulqdq", "Pclmulqdq.V256", "Pclmulqdq.V512"]),
            (Name: "galois-field-affine", Knobs: ["DOTNET_EnableGFNI"], Suppressed: ["Gfni", "Gfni.V256", "Gfni.V512"]),
            (Name: "galois-field-affine-and-512-bit", Knobs: ["DOTNET_EnableGFNI", "DOTNET_EnableAVX2"], Suppressed: ["Bmi2", "Gfni", "Gfni.V256", "Gfni.V512", "Pclmulqdq.V512"]),
            (Name: "512-bit", Knobs: ["DOTNET_EnableAVX2"], Suppressed: ["Bmi2", "Gfni.V512", "Pclmulqdq.V512"]),
        };
        var index = 0;

        foreach (var child in children) {
            ++index;

            var required = child.Suppressed.Where(predicate: name => parentSets[name]).ToArray();

            if (0 == required.Length) {
                notes.Add(item: $"child {index} ({child.Name}) skipped: this machine already reports {string.Join(separator: ", ", values: child.Suppressed)} unsupported");
                continue;
            }

            var result = PostProbeProcess.Run(
                arguments: ["--binary-field-probe"],
                environment: child.Knobs.ToDictionary(keySelector: static knob => knob, elementSelector: static _ => "0", comparer: StringComparer.Ordinal)
            );

            if (result.TimedOut) {
                return $"binary-field probe child {index} ({child.Name}) hung and was killed after {PostProbeProcess.TimeoutSeconds}s";
            }

            if (0 != result.ExitCode) {
                return $"binary-field probe child {index} ({child.Name}) exited {result.ExitCode} ({result.OutputTail})";
            }

            var childSets = BinaryFieldProbe.ParseInstructionSets(output: result.Output);
            var childDigest = BinaryFieldProbe.ParseDigest(output: result.Output);

            if ((childSets is null) || (childDigest is null)) {
                return $"binary-field probe child {index} ({child.Name}) printed no digest or instruction-set vector ({result.OutputTail})";
            }

            foreach (var name in required) {
                if (!childSets.TryGetValue(key: name, value: out var supported) || supported) {
                    return $"binary-field probe child {index} did not suppress {name}";
                }
            }

            if (parentDigest != childDigest.Value) {
                return $"binary-field probe child {index} ({child.Name}) produced digest 0x{childDigest.Value:X16}, this process produced 0x{parentDigest:X16}";
            }

            notes.Add(item: $"child {index} ({child.Name}) matched digest 0x{parentDigest:X16} with {string.Join(separator: ", ", values: required)} suppressed");
        }

        return null;
    }
    /// <summary>Runs one named sixteen-bit region rung directly, bypassing the ladder's own support gate.</summary>
    /// <param name="tier">The rung to run.</param>
    /// <param name="destination">The region to write, whose length matches <paramref name="source"/>.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <param name="accumulate"><see langword="true"/> to add the scaled region into the destination; <see langword="false"/> to overwrite it.</param>
    /// <param name="degree">The field's degree.</param>
    /// <param name="tail">The modulus tail.</param>
    private static void RunWideRegionTier(BinaryFieldRegionTier tier, Span<ushort> destination, ReadOnlySpan<ushort> source, ushort scalar, bool accumulate, int degree, ushort tail) {
        switch (tier) {
            case BinaryFieldRegionTier.Affine512:
                BinaryFieldKernels.MultiplyAccumulateRegionWideAffine512(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            case BinaryFieldRegionTier.Affine256:
                BinaryFieldKernels.MultiplyAccumulateRegionWideAffine256(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            case BinaryFieldRegionTier.Affine128:
                BinaryFieldKernels.MultiplyAccumulateRegionWideAffine128(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
            default:
                BinaryFieldKernels.MultiplyAccumulateRegionScalar(destination: destination, source: source, scalar: scalar, accumulate: accumulate, degree: degree, tail: tail);
                break;
        }
    }
}
