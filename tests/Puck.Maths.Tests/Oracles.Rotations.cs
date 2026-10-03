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
    /// <summary>The reference screw exponential of a dual vector given as raw lanes, each output lane an exact numerator
    /// over its own positive denominator: the rotation <c>(û·sin θ, cos θ)</c> and the dual part
    /// <c>dual·sin θ/θ + û·(û·dual)·(cos θ − sin θ/θ)</c> with scalar <c>−(û·dual)·sin θ</c>, for <c>θ = |real|</c>.
    /// Null for a zero rotation, whose exponential is exact.</summary>
    /// <param name="real">The rotation part's three raw lanes.</param>
    /// <param name="dual">The dual part's three raw lanes.</param>
    /// <returns>The eight lane rationals (rotation x, y, z, w, then dual x, y, z, w), or <see langword="null"/>.</returns>
    /// <remarks>θ is the integer root of the exact sum of squares at Q60, and its sine and cosine come from
    /// <see cref="EncloseSinCosScaled"/>, 40 guard bits below Q16: the series, never the subject's Q60 table, and θ
    /// never rounded to the Q16 grid the subject's angle sits on.</remarks>
    public static (BigInteger Numerator, BigInteger Denominator)[]? RigidExp(ReadOnlySpan<long> real, ReadOnlySpan<long> dual) {
        const int Guard = RotationGuardBitCount;
        BigInteger rx = real[0], ry = real[1], rz = real[2], dx = dual[0], dy = dual[1], dz = dual[2];
        var sum = (((rx * rx) + (ry * ry)) + (rz * rz));

        if (sum.IsZero) {
            return null;
        }

        // T = θ·2⁶⁰; sin and cos at 2^(16 + Guard); θ_raw = T/2⁴⁴.
        var t = IntegerSquareRoot(value: (sum << 88));

        var (sin, cos) = EncloseSinCosScaled(
            fractionBitCount: 60,
            guardBitCount: Guard,
            raw: t
        );
        var sinS = sin.Low;
        var cosS = cos.Low;
        var slide = (((rx * dx) + (ry * dy)) + (rz * dz));
        var realDenominator = (t << Guard);
        var dualDenominator = (((t * t) * t) << (16 + Guard));
        var bend = ((cosS * t) - (sinS << 60));
        BigInteger[] r = [rx, ry, rz];
        BigInteger[] d = [dx, dy, dz];
        var lanes = new (BigInteger, BigInteger)[8];

        for (var lane = 0; (lane < 3); lane++) {
            lanes[lane] = (((r[lane] * sinS) << 44), realDenominator);
            lanes[(4 + lane)] = ((((((d[lane] * sinS) * t) * t) << 60) + (((r[lane] * slide) * bend) << 88)), dualDenominator);
        }

        lanes[3] = (cosS, (BigInteger.One << Guard));
        lanes[7] = (-((slide * sinS) << 44), (t << (16 + Guard)));

        return lanes;
    }
    /// <summary>The reference screw logarithm of a transform given as raw lanes, each output lane an exact numerator
    /// over a positive denominator: the rotation bivector <c>rᵢ·h/s</c> and the dual part
    /// <c>dᵢ·h/s − rᵢ·d_w·(s − w·h)/s³</c>, for <c>s = |vector part|</c> and <c>h = atan2(s, w)</c>. Null for a
    /// vector-free rotation, where the subject answers the dual part as given.</summary>
    /// <param name="real">The rotation quaternion's four raw lanes (x, y, z, w).</param>
    /// <param name="dual">The dual quaternion's four raw lanes.</param>
    /// <param name="sineSensitivity">Per dual lane, <c>|dᵢ·h/s| + 3·|rᵢ·d_w·(s − w·h)/s³|</c> over the dual lanes'
    /// denominator: the first-order change of the lane per unit relative error in s, the two terms' magnitudes
    /// weighted by their powers of s, which a near-half-turn rotation makes large and nearly cancelling.</param>
    /// <returns>The six lane rationals (rotation x, y, z, then dual x, y, z), or <see langword="null"/>.</returns>
    /// <remarks>s is the integer root of the exact sum of squares at 2⁻⁶⁰ and h comes from
    /// <see cref="EncloseAtan2"/>, the arctangent series, 40 guard bits below Q16. The subject carries both at Q20 and
    /// folds the lanes into one fraction each; this oracle shares neither its precision nor its arctangent.</remarks>
    public static (BigInteger Numerator, BigInteger Denominator)[]? RigidLog(ReadOnlySpan<long> real, ReadOnlySpan<long> dual, out BigInteger[] sineSensitivity) {
        const int Guard = RotationGuardBitCount;
        BigInteger rx = real[0], ry = real[1], rz = real[2], w = real[3], dw = dual[3];
        var sum = (((rx * rx) + (ry * ry)) + (rz * rz));

        sineSensitivity = new BigInteger[3];

        if (sum.IsZero) {
            return null;
        }

        // Ssc = s·2⁶⁰ (s in real units); h·2^(16 + Guard) from operands at a shared scale below 2⁶³.
        var ssc = IntegerSquareRoot(value: (sum << 88));
        var h = EncloseAtan2(
            guardBitCount: Guard,
            xRaw: ((long)(w << 30)),
            yRaw: ((long)IntegerSquareRoot(value: (sum << 60)))
        ).Low;
        var tilt = (((ssc << (32 + Guard)) - ((w * h) << 60)) << (72 - Guard));
        BigInteger[] r = [rx, ry, rz];
        BigInteger[] d = [dual[0], dual[1], dual[2]];
        var cube = ((ssc * ssc) * ssc);
        var lanes = new (BigInteger, BigInteger)[6];

        for (var lane = 0; (lane < 3); lane++) {
            lanes[lane] = (((r[lane] * h) << (44 - Guard)), ssc);
            var screw = ((((d[lane] * h) << (44 - Guard)) * ssc) * ssc);
            var slide = ((r[lane] * dw) * tilt);

            lanes[(3 + lane)] = ((screw - slide), cube);
            sineSensitivity[lane] = (BigInteger.Abs(value: screw) + (3 * BigInteger.Abs(value: slide)));
        }

        return lanes;
    }
}
