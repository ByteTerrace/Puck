using Puck.Maths;

namespace Puck.World.Qr;

/// <summary>
/// The QR spec's Reed–Solomon parameters, bound to <see cref="ReedSolomon"/> — the field ISO/IEC 18004 Annex A
/// names and the generator polynomials every supported version+level block plan can ask for. The generators are
/// built once at type load for each degree in 1..<see cref="MaxEccCodewordsPerBlock"/>, so encoding allocates only
/// the block's own error-correction codewords: no per-call polynomial construction, no cache, no lock.
/// </summary>
/// <remarks>
/// The field is <c>GF(256)</c> under <c>t⁸ + t⁴ + t³ + t² + 1</c> (<c>0x11D</c>) with the generator element
/// <c>α = 2</c> and the root run starting at <c>α⁰</c>. It is deliberately not <see cref="BinaryFields.Degree8"/>,
/// whose modulus is <c>t⁸ + t⁴ + t³ + t + 1</c> (<c>0x11B</c>): both are degree-8 pentanomials and both are
/// irreducible, but they are different fields, and a code computed in one does not decode in the other. The catalog
/// carries the canonical minimum-weight modulus at each accelerated width, a statement about the width rather than
/// about any standard, so this modulus is named here, where the standard that chose it is cited, rather than added
/// to the catalog as a second degree-8 entry. Naming a field costs nothing — <see cref="BinaryField{T}"/> precomputes
/// nothing — so there is no saving to chase by hoisting it.
/// </remarks>
public static class QrReedSolomon {
    private const byte GeneratorElement = 2;
    private const byte ReductionTail = 0x1D;

    /// <summary>The largest EC codeword count any version 1..10 block plan asks for (version 9, level L) — the highest
    /// generator degree the prebuilt table carries.</summary>
    public const int MaxEccCodewordsPerBlock = 30;

    private static readonly BinaryField<byte> Field = BinaryField<byte>.Create(
        degree: 8,
        reductionTail: ReductionTail
    );
    // Indexed by (degree - 1): each entry is that degree's generator coefficients, highest-order first, length degree+1.
    private static readonly byte[][] Generators = BuildGenerators();

    private static byte[][] BuildGenerators() {
        var generators = new byte[MaxEccCodewordsPerBlock][];

        for (var degree = 1; (degree <= MaxEccCodewordsPerBlock); degree++) {
            var generator = new byte[(degree + 1)];

            ReedSolomon.BuildGenerator(
                field: Field,
                firstRootExponent: 0,
                generator: generator,
                rootBase: GeneratorElement
            );

            generators[(degree - 1)] = generator;
        }

        return generators;
    }

    /// <summary>Computes <paramref name="eccCount"/> error-correction codewords for one data block.</summary>
    /// <param name="data">The block's data codewords, highest-order coefficient first (the spec's message polynomial).</param>
    /// <param name="eccCount">The EC codeword count for this block (<see cref="QrBlockPlan.EccCodewordsPerBlock"/>),
    /// within 1..<see cref="MaxEccCodewordsPerBlock"/>.</param>
    /// <returns>The block's EC codewords, in the order the final interleaved message appends them.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="eccCount"/> is outside
    /// 1..<see cref="MaxEccCodewordsPerBlock"/>.</exception>
    public static byte[] ComputeCodewords(ReadOnlySpan<byte> data, int eccCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: eccCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: eccCount,
            other: MaxEccCodewordsPerBlock
        );

        var codewords = new byte[eccCount];

        ReedSolomon.ComputeCheckSymbols(
            field: Field,
            generator: Generators[(eccCount - 1)],
            message: data,
            checkSymbols: codewords
        );

        return codewords;
    }
}
