using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // The extra binary scale the rotation oracles carry their square roots and trigonometry at.
    private const int RotationGuardBitCount = 40;

    /// <summary>The reference spherical interpolation of two quaternions given as raw lanes, as an exact numerator per
    /// lane over one common denominator: the point at angle <c>t·θ</c> along the great circle from <c>A/|A|</c>
    /// toward <c>B/|B|</c> (B negated first when <c>A·B &lt; 0</c>), scaled to Q16. Null when the inputs are parallel,
    /// where the arc is undefined.</summary>
    /// <param name="from">The four raw lanes of A.</param>
    /// <param name="to">The four raw lanes of B.</param>
    /// <param name="amountRaw">The raw Q16 interpolation parameter t.</param>
    /// <returns>Four lane numerators and their shared positive denominator, or <see langword="null"/>.</returns>
    /// <remarks>Lane i is <c>cos φ·Aᵢ/|A| + sin φ·(Bᵢ|A|² − (A·B)Aᵢ)/(|A|·√(|A|²|B|² − (A·B)²))</c> for
    /// <c>φ = t·θ</c>, <c>θ = atan2(√(|A|²|B|² − (A·B)²), A·B)</c>. Every product and sum is exact; the two norms are
    /// integer square roots at 2⁴⁰ extra scale, θ comes from <see cref="EncloseAtan2"/> (the arctangent series, not a
    /// table) and the sine and cosine of φ from <see cref="EncloseSinCosScaled"/>, each 40 guard bits below Q16. The
    /// subject's route shares none of it: it takes θ from FixedQ4816.Atan2 at Q16, forms the weights by one SinCos
    /// and two Q16 divides, and normalizes the blend.</remarks>
    public static (BigInteger[] Numerators, BigInteger Denominator)? Slerp(ReadOnlySpan<long> from, ReadOnlySpan<long> to, long amountRaw) {
        const int Guard = RotationGuardBitCount;
        var a = new BigInteger[4];
        var b = new BigInteger[4];

        for (var lane = 0; (lane < 4); lane++) {
            a[lane] = from[lane];
            b[lane] = to[lane];
        }

        var dot = (((a[0] * b[0]) + (a[1] * b[1])) + ((a[2] * b[2]) + (a[3] * b[3])));

        if (dot.Sign < 0) {
            dot = -dot;

            for (var lane = 0; (lane < 4); lane++) {
                b[lane] = -b[lane];
            }
        }

        var normA = (((a[0] * a[0]) + (a[1] * a[1])) + ((a[2] * a[2]) + (a[3] * a[3])));
        var normB = (((b[0] * b[0]) + (b[1] * b[1])) + ((b[2] * b[2]) + (b[3] * b[3])));
        var crossSquared = ((normA * normB) - (dot * dot));

        if (crossSquared.Sign <= 0) {
            return null;
        }

        // atan2 is scale-invariant: both operands at Q32·2³⁰, a little under 2⁶³, keep the ratio to 2⁻⁶⁰.
        var ordinate = IntegerSquareRoot(value: (crossSquared << 60));
        var abscissa = (dot << 30);
        var excess = Math.Max(
            val1: 0,
            val2: (((int)Math.Max(
                val1: ordinate.GetBitLength(),
                val2: abscissa.GetBitLength()
            )) - 62)
        );
        var theta = EncloseAtan2(
            guardBitCount: Guard,
            xRaw: ((long)(abscissa >> excess)),
            yRaw: ((long)(ordinate >> excess))
        );
        var phi = (amountRaw * theta.Low);

        var (sin, cos) = EncloseSinCosScaled(
            fractionBitCount: (32 + Guard),
            guardBitCount: Guard,
            raw: phi
        );
        var rootA = IntegerSquareRoot(value: (normA << (2 * Guard)));
        var rootCross = IntegerSquareRoot(value: (crossSquared << (2 * Guard)));
        var numerators = new BigInteger[4];

        for (var lane = 0; (lane < 4); lane++) {
            numerators[lane] = ((((cos.Low * a[lane]) * rootCross) << Guard) +
                ((sin.Low * ((b[lane] * normA) - (dot * a[lane]))) << (2 * Guard)));
        }

        return (numerators, ((rootA * rootCross) << Guard));
    }
}
