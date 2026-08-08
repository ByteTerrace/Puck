using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Region-tier claims for the GF(2) binary fields.
/// The overlap analysis that preceded this port found the stage's exhaustive degree-8 table, its generator count, its
/// five-carrier field axioms, its catalog-modulus irreducibility, its published carryless-multiply vectors and its
/// region-vs-scalar-rung comparisons THROUGH THE PUBLIC SURFACE ALL already pinned by <c>laws/binary-field.json</c>,
/// <c>laws/polynomial.json</c> and <c>laws/deep.json</c> — most of them more strongly, several exhaustively. Two gaps
/// survived that analysis. The first is the one operand class those laws do not reach: a modulus whose degree sits
/// BELOW its carrier's own width, which is where a masked-split reduction bug shows up and which the five catalog
/// fields — every one of which has degree equal to its carrier's width — cannot exercise no matter how they are
/// swept (<see cref="NarrowDegreeInverseSurface"/>, <see cref="NarrowDegreeRegionsSurface"/>). The second is the one
/// the stage's own per-rung instrumentation existed for: <see cref="BinaryField{T}"/>'s public region members
/// dispatch to whichever accelerated rung this machine's hardware ranks widest, so a rung that is merely SUPPORTED but
/// not the widest available is never reached through the public surface at all. That gap could not be closed until
/// <c>BinaryFieldKernels</c> and <c>BinaryFieldRegionTier</c> — both <see langword="internal"/> to Puck.Maths — opened
/// to this project (<c>src/Puck.Maths/Properties/AssemblyInfo.cs</c>'s <c>InternalsVisibleTo</c>); with that in place,
/// <see cref="RegionTiersVsScalarRungSurface"/>, <see cref="WideRegionTiersVsScalarRungSurface"/> and
/// <see cref="RegionLengthsVsScalarRungSurface"/> invoke every named rung directly, bypassing dispatch, exactly as the
/// byte and wide region-tier walks below do. What is NOT covered is the
/// process relaunch under instruction-set-suppression environment knobs, which forced one host through every
/// fallback regardless of its own hardware; this suite has no relaunch primitive, so a rung this host's hardware does
/// not support is skipped rather than forced — see each new law's ENVELOPE legs. <see cref="LawRegistry"/> invokes
/// each claim below as a Default-tier law.
/// </summary>
internal static class BinaryFieldRegionClaims {
    /// <summary>The stage's three degree-four sweep moduli, over the byte carrier.</summary>
    private static readonly byte[] NarrowDegreeTails = [0x3, 0x9, 0xF];
    /// <summary>The region lengths the narrow-degree region sweep runs at: both sides of the ceiling the stage's own
    /// length sweep used, and the empty region.</summary>
    private static readonly int[] NarrowDegreeRegionLengths = [0, 1, 7, 8, 9, 31, 32, 65, 259];
    /// <summary>The byte-wide region rungs the region-tier laws invoke by name, widest first — every member
    /// <see cref="BinaryFieldRegionTier"/> declares for the byte carrier.</summary>
    private static readonly BinaryFieldRegionTier[] ByteRegionTiers = [
        BinaryFieldRegionTier.Affine512,
        BinaryFieldRegionTier.Split512,
        BinaryFieldRegionTier.Affine256,
        BinaryFieldRegionTier.Split256,
        BinaryFieldRegionTier.Affine128,
        BinaryFieldRegionTier.Split128,
    ];
    /// <summary>The degree-8 sweep moduli the byte-region laws run under: the canonical tail and two further
    /// irreducibles, the catalog tails.</summary>
    private static readonly byte[] ByteRegionTails = [0x1B, 0x2D, 0x9F];
    /// <summary>The sixteen-bit region rungs the wide-region laws invoke by name; there is deliberately no
    /// nibble-split rung at this width.</summary>
    private static readonly BinaryFieldRegionTier[] WideRegionTiers = [BinaryFieldRegionTier.Affine512, BinaryFieldRegionTier.Affine256, BinaryFieldRegionTier.Affine128];
    /// <summary>The degree-16 sweep moduli the wide-region laws run under.</summary>
    private static readonly ushort[] WideRegionTails = [0x2B, 0x47];
    /// <summary>The scalars the wide-region-tier law scales by.</summary>
    private static readonly ushort[] WideRegionScalars = [0x0000, 0x0001, 0x0002, 0x00FF, 0x0100, 0x1234, 0x8000, 0xFFFF];
    /// <summary>The longest region the length-sweep law covers: four whole 512-bit vectors and a partial one,
    /// the ceiling this family admits.</summary>
    private const int RegionLengthCeiling = ((4 * 64) + 3);

    /// <summary>Proves <see cref="BinaryField{T}.Multiply"/> and <see cref="BinaryField{T}.Inverse"/> at every element
    /// of GF(2^4) under three distinct degree-four moduli, against <see cref="Oracles.BinaryFieldProduct"/> and
    /// <see cref="Oracles.BinaryFieldInverse"/> — the same shared-nothing oracles the catalog-field laws already use
    /// (<c>binary-field.product-and-reduction-vs-oracle</c>, <c>binary-field.multiplicative-group-vs-oracle</c>),
    /// reached here at a degree none of the five catalog fields ever exercises, and the inverse's own certificate that
    /// the product with its operand is one.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? NarrowDegreeInverseSurface() {
        foreach (var tail in NarrowDegreeTails) {
            var field = BinaryField<byte>.Create(degree: 4, reductionTail: tail);

            for (var left = 0; (left < 16); ++left) {
                for (var right = 0; (right < 16); ++right) {
                    var expected = Oracles.BinaryFieldProduct(left: left, right: right, degree: 4, reductionTail: tail);
                    var actual = field.Multiply(left: ((byte)left), right: ((byte)right));

                    if (expected != actual) {
                        return $"degree-4 multiply of 0x{left:X1} and 0x{right:X1} under tail 0x{tail:X1} gave 0x{actual:X2}, the oracle gives 0x{expected:X2}";
                    }
                }

                if (0 == left) { continue; }

                var inverse = field.Inverse(value: ((byte)left));
                var oracleInverse = Oracles.BinaryFieldInverse(value: left, degree: 4, reductionTail: tail);

                if (oracleInverse != inverse) {
                    return $"degree-4 inverse of 0x{left:X1} under tail 0x{tail:X1} gave 0x{inverse:X2}, the oracle gives 0x{oracleInverse:X2}";
                }

                var certificate = Oracles.BinaryFieldProduct(left: left, right: inverse, degree: 4, reductionTail: tail);

                if (BigInteger.One != certificate) {
                    return $"degree-4 inverse of 0x{left:X1} under tail 0x{tail:X1} does not multiply back to one (got 0x{certificate:X1})";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="BinaryField{T}.ScaleRegion"/>, <see cref="BinaryField{T}.MultiplyAccumulateRegion"/>
    /// and <see cref="BinaryField{T}.ScaleRegionInPlace"/> at a degree-five byte field and a degree-twelve
    /// sixteen-bit field — the reduction's masked-split path for the bulk
    /// region kernels — against <see cref="Oracles.BinaryFieldProduct"/> element by element. The five catalog fields
    /// <c>binary-field.regions-vs-oracle</c> sweeps all have degree equal to their carrier's width, where this path is
    /// unreachable; this claim is the one operand class that law's exhaustive and delegated legs cannot reach.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? NarrowDegreeRegionsSurface() =>
        (NarrowDegreeRegionField(field: BinaryField<byte>.Create(degree: 5, reductionTail: 0x05), degree: 5, tail: 0x05UL) ??
         NarrowDegreeRegionField(field: BinaryField<ushort>.Create(degree: 12, reductionTail: 0x09), degree: 12, tail: 0x09UL));

    /// <summary>Sweeps one narrow-degree field's region-scaling ladder against the oracle at every declared length.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="field">The narrow-degree field under test.</param>
    /// <param name="degree">The field's degree, strictly below the carrier's own width.</param>
    /// <param name="tail">The field's reduction tail, as the oracle's own operand type.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when every length agrees.</returns>
    private static string? NarrowDegreeRegionField<T>(BinaryField<T> field, int degree, ulong tail) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        const int Ceiling = 259;
        var source = new T[Ceiling];
        var seed = new T[Ceiling];
        var destination = new T[Ceiling];
        var expectedScale = new T[Ceiling];
        var expectedAccumulate = new T[Ceiling];

        for (var index = 0; (index < Ceiling); ++index) {
            // A deterministic, operand-free affine walk spread across the element space by two odd mixing constants,
            // salted so the source and the seed region never coincide, then reduced into the narrow-degree field —
            // unlike a catalog field, where degree equals the carrier's width and every value is already reduced.
            source[index] = field.Reduce(value: NarrowDegreeRegionWalk<T>(index: index, salt: 0UL));
            seed[index] = field.Reduce(value: NarrowDegreeRegionWalk<T>(index: index, salt: 0x5DEECE66DUL));
        }

        var scalar = field.Reduce(value: NarrowDegreeRegionWalk<T>(index: Ceiling, salt: 0x2545F4914F6CDD1DUL));
        var scalarValue = BigInteger.CreateTruncating(value: scalar);

        for (var index = 0; (index < Ceiling); ++index) {
            var element = BigInteger.CreateTruncating(value: source[index]);
            var product = Oracles.BinaryFieldProduct(left: scalarValue, right: element, degree: degree, reductionTail: tail);

            expectedScale[index] = T.CreateTruncating(value: product);
            expectedAccumulate[index] = T.CreateTruncating(value: (BigInteger.CreateTruncating(value: seed[index]) ^ product));
        }

        foreach (var length in NarrowDegreeRegionLengths) {
            Array.Copy(sourceArray: seed, destinationArray: destination, length: Ceiling);
            field.ScaleRegion(destination: destination.AsSpan(start: 0, length: length), source: source.AsSpan(start: 0, length: length), scalar: scalar);

            for (var index = 0; (index < length); ++index) {
                if (destination[index] != expectedScale[index]) {
                    return $"ScaleRegion at degree {degree} length {length} disagreed with the oracle at index {index}";
                }
            }

            Array.Copy(sourceArray: seed, destinationArray: destination, length: Ceiling);
            field.MultiplyAccumulateRegion(destination: destination.AsSpan(start: 0, length: length), source: source.AsSpan(start: 0, length: length), scalar: scalar);

            for (var index = 0; (index < length); ++index) {
                if (destination[index] != expectedAccumulate[index]) {
                    return $"MultiplyAccumulateRegion at degree {degree} length {length} disagreed with the oracle at index {index}";
                }
            }

            Array.Copy(sourceArray: source, destinationArray: destination, length: Ceiling);
            field.ScaleRegionInPlace(values: destination.AsSpan(start: 0, length: length), scalar: scalar);

            for (var index = 0; (index < length); ++index) {
                if (destination[index] != expectedScale[index]) {
                    return $"ScaleRegionInPlace at degree {degree} length {length} disagreed with the oracle at index {index}";
                }
            }
        }

        return null;
    }
    /// <summary>The fixed region content: a deterministic, operand-free affine walk spread across the element space
    /// by two odd mixing constants, salted so a source region and a destination region never coincide. No wall clock
    /// and no randomness — the walk is a pure function of the index and the salt.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="index">The region index to derive a value for.</param>
    /// <param name="salt">The per-region salt.</param>
    /// <returns>The derived carrier value, not yet reduced into any field.</returns>
    private static T NarrowDegreeRegionWalk<T>(int index, ulong salt) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var seed = unchecked(((ulong)index) + salt);
        var low = unchecked(seed * 0x9E3779B97F4A7C15UL);
        var high = unchecked((seed ^ 0xD1B54A32D192ED03UL) * 0xBF58476D1CE4E5B9UL);

        return T.CreateTruncating(value: ((((UInt128)high) << 64) | low));
    }
    /// <summary>Proves every byte-wide region rung <see cref="BinaryFieldKernels"/> ships — the 128-, 256- and
    /// 512-bit Galois-field affine transform and the 128-, 256- and 512-bit nibble-split table shuffle — against the
    /// element-at-a-time scalar rung, over the WHOLE 256-by-256 scalar-by-element byte cross product at three
    /// degree-8 moduli and both accumulate modes. 
    /// <c>CheckByteRegionTiers</c>: every rung is invoked BY NAME here, bypassing <see cref="BinaryField{T}"/>'s own
    /// widest-first dispatch, which this suite could not do before <c>BinaryFieldKernels</c> and
    /// <see cref="BinaryFieldRegionTier"/> opened to <c>Puck.Maths.Tests</c>. A rung
    /// <see cref="BinaryFieldKernels.IsRegionTierSupported"/> reports unsupported on the host running the suite is
    /// SKIPPED, never forced.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every rung this host supports agrees with the
    /// scalar rung at every operand.</returns>
    public static string? RegionTiersVsScalarRungSurface() {
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
    /// <summary>Proves every sixteen-bit region rung <see cref="BinaryFieldKernels"/> ships — the 128-, 256- and
    /// 512-bit Galois-field affine transform; there is deliberately no nibble-split rung at this width — against the
    /// element-at-a-time scalar rung, over the WHOLE 65536-element range at eight scalars, two degree-16 moduli and
    /// both accumulate modes. <c>CheckWideRegionTiers</c>, invoking each rung BY NAME
    /// exactly as <see cref="RegionTiersVsScalarRungSurface"/> does for the byte carrier. A rung
    /// <see cref="BinaryFieldKernels.IsRegionTierSupported"/> reports unsupported on this host is SKIPPED, never
    /// forced.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every rung this host supports agrees with the
    /// scalar rung at every operand.</returns>
    public static string? WideRegionTiersVsScalarRungSurface() {
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
                foreach (var scalar in WideRegionScalars) {
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
    /// <summary>Proves every supported byte-wide and sixteen-bit region rung against the scalar rung at every length
    /// from zero through <see cref="RegionLengthCeiling"/> — four whole 512-bit vectors and a partial one — crossed
    /// with four byte scalars (and four derived sixteen-bit scalars) and both accumulate modes.     /// The tail past the last whole vector, where a masked partial store
    /// or a missed length guard is the classic region-kernel bug, is covered EXHAUSTIVELY here rather than sampled. A
    /// rung <see cref="BinaryFieldKernels.IsRegionTierSupported"/> reports unsupported on this host is SKIPPED, never
    /// forced.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every rung this host supports agrees with the
    /// scalar rung at every length.</returns>
    public static string? RegionLengthsVsScalarRungSurface() {
        var actual = new byte[RegionLengthCeiling];
        var expected = new byte[RegionLengthCeiling];
        var source = new byte[RegionLengthCeiling];
        var wideActual = new ushort[RegionLengthCeiling];
        var wideExpected = new ushort[RegionLengthCeiling];
        var wideSeed = new ushort[RegionLengthCeiling];
        var wideSource = new ushort[RegionLengthCeiling];

        for (var index = 0; (index < RegionLengthCeiling); ++index) {
            // A deterministic, operand-free pattern derived from the index alone — no wall clock and no randomness —
            // with the sixteen-bit seed and source kept apart by different odd multipliers so they never coincide.
            source[index] = ((byte)((index * 31) + 7));
            wideSeed[index] = ((ushort)((index * 3_119) + 11));
            wideSource[index] = ((ushort)((index * 7_919) + 13));
        }

        foreach (var scalar in (byte[])[0x00, 0x01, 0x1D, 0xFF]) {
            for (var length = 0; (length <= RegionLengthCeiling); ++length) {
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
    /// <summary>Runs one named byte-wide region rung directly, bypassing <see cref="BinaryFieldKernels"/>'s own
    /// dispatch and support gate — the byte region-tier seam.</summary>
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
    /// <summary>Runs one named sixteen-bit region rung directly, bypassing <see cref="BinaryFieldKernels"/>'s own
    /// dispatch and support gate — the wide region-tier seam.</summary>
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
