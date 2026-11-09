using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    private static readonly BigInteger TwoTo32 = (BigInteger.One << 32);
    // The two constants Pcg3dLatticeNoise.Pcg3d's LCG step multiplies and adds by.
    private static readonly BigInteger Pcg3dLcgMultiplier = 1664525;
    private static readonly BigInteger Pcg3dLcgIncrement = 1013904223;

    /// <summary>The fractal value noise <see cref="Pcg3dLatticeNoise.Fbm01"/> sums, formed in closed form per octave in
    /// <see cref="BigInteger"/>: octave <c>k</c> reads the noise-cell edge <c>max(1, frequency &gt;&gt; k)</c> (the
    /// subject halves it iteratively), the seed <c>seed + k·0x9E3779B9</c> reduced modulo <c>2³²</c>, and the amplitude
    /// <c>2¹⁶ &gt;&gt; k</c>; each weighed term and the final quotient round to nearest, ties to even, once.</summary>
    /// <param name="frequency">The first octave's noise-cell edge.</param>
    /// <param name="octaves">The octave count, at least one.</param>
    /// <param name="seed">The first octave's seed.</param>
    /// <param name="octaveRaw">One octave's <see cref="Pcg3dLatticeNoise.ValueNoise01"/> raw at a noise-cell edge and a
    /// seed: the one kernel the oracle shares with the subject, pinned by its own law.</param>
    /// <returns>The weighted mean's raw.</returns>
    public static BigInteger FractalValueNoise01Raw(int frequency, int octaves, uint seed, Func<int, uint, long> octaveRaw) {
        var total = BigInteger.Zero;
        var weight = BigInteger.Zero;

        for (var octave = 0; (octave < octaves); octave++) {
            var cells = Math.Max(
                val1: 1,
                val2: (frequency >> Math.Min(val1: octave, val2: 31))
            );
            var octaveSeed = WrapToUInt32(value: (((BigInteger)seed) + (((BigInteger)octave) * 0x9E3779B9)));
            var amplitude = (((BigInteger)65536) >> octave);

            total += RoundRationalTiesToEven(
                numerator: (amplitude * octaveRaw(cells, octaveSeed)),
                denominator: 65536
            );
            weight += amplitude;
        }

        return RoundRationalTiesToEven(
            numerator: (total << 16),
            denominator: weight
        );
    }
    /// <summary>Reduces an exact integer to the UNSIGNED 32-bit carrier — modulo <c>2³²</c> into <c>[0, 2³²)</c>.</summary>
    /// <param name="value">The exact value.</param>
    /// <returns>The wrapped word.</returns>
    public static uint WrapToUInt32(BigInteger value) =>
        ((uint)(((value % TwoTo32) + TwoTo32) % TwoTo32));
    /// <summary>The Jarzynski &amp; Olano PCG3D mix, formed in arbitrary-width <see cref="BigInteger"/> with the
    /// carrier reduction taken EXPLICITLY at every step, where <see cref="Pcg3dLatticeNoise.Pcg3d"/> relies on the
    /// carrier's own unchecked wrap. Different reduction route, exact on both sides.</summary>
    /// <param name="x">The X lane input.</param>
    /// <param name="y">The Y lane input.</param>
    /// <param name="z">The Z lane input.</param>
    /// <returns>The three mixed lanes.</returns>
    public static (uint X, uint Y, uint Z) Pcg3dReference(uint x, uint y, uint z) {
        var wideX = ((BigInteger)x);
        var wideY = ((BigInteger)y);
        var wideZ = ((BigInteger)z);

        wideX = WrapToUInt32(value: ((wideX * Pcg3dLcgMultiplier) + Pcg3dLcgIncrement));
        wideY = WrapToUInt32(value: ((wideY * Pcg3dLcgMultiplier) + Pcg3dLcgIncrement));
        wideZ = WrapToUInt32(value: ((wideZ * Pcg3dLcgMultiplier) + Pcg3dLcgIncrement));

        wideX = WrapToUInt32(value: (wideX + (wideY * wideZ)));
        wideY = WrapToUInt32(value: (wideY + (wideZ * wideX)));
        wideZ = WrapToUInt32(value: (wideZ + (wideX * wideY)));

        wideX ^= (wideX >> 16);
        wideY ^= (wideY >> 16);
        wideZ ^= (wideZ >> 16);

        wideX = WrapToUInt32(value: (wideX + (wideY * wideZ)));
        wideY = WrapToUInt32(value: (wideY + (wideZ * wideX)));
        wideZ = WrapToUInt32(value: (wideZ + (wideX * wideY)));

        return (((uint)wideX), ((uint)wideY), ((uint)wideZ));
    }
    /// <summary>The quintic fade <c>6t⁵ − 15t⁴ + 10t³</c> at a Q16 fraction, formed exactly in <see cref="BigInteger"/>
    /// and rounded once to nearest, ties to even.</summary>
    /// <param name="raw">The fraction's raw, <c>t·2¹⁶</c>.</param>
    /// <returns>The rounded fade's raw.</returns>
    public static long QuinticFadeReference(long raw) {
        BigInteger t = raw;
        var exact = (((6 * BigInteger.Pow(value: t, exponent: 5)) - ((15 * BigInteger.Pow(value: t, exponent: 4)) << 16)) + ((10 * BigInteger.Pow(value: t, exponent: 3)) << 32));
        var quotient = BigInteger.DivRem(dividend: exact, divisor: (BigInteger.One << 64), remainder: out var remainder);
        var twice = (remainder << 1);
        var divisor = (BigInteger.One << 64);

        if ((twice > divisor) || ((twice == divisor) && !quotient.IsEven)) {
            quotient += 1;
        }

        return ((long)quotient);
    }
    /// <summary>The exact trilinear blend <see cref="Pcg3dLatticeNoise.ValueNoise3"/> rounds: the eight corners' fractions
    /// from <see cref="Pcg3dReference"/> weighed by <see cref="QuinticFadeReference"/>'s fades at the point's fractions,
    /// in <see cref="BigInteger"/>, without rounding.</summary>
    /// <param name="q">The point in lattice cells.</param>
    /// <param name="seedX">The seed each corner's X lane is keyed with.</param>
    /// <param name="seedY">The seed each corner's Y lane is keyed with.</param>
    /// <param name="seedZ">The seed each corner's Z lane is keyed with.</param>
    /// <returns>The blend in raws times <c>2⁴⁸</c>.</returns>
    public static BigInteger ValueNoise3ExactBlend(FixedVector3 q, uint seedX, uint seedY, uint seedZ) {
        var cells = new[] { (q.X.Value >> 16), (q.Y.Value >> 16), (q.Z.Value >> 16) };
        var fades = new[] {
            ((BigInteger)QuinticFadeReference(raw: (q.X.Value - (cells[0] << 16)))),
            ((BigInteger)QuinticFadeReference(raw: (q.Y.Value - (cells[1] << 16)))),
            ((BigInteger)QuinticFadeReference(raw: (q.Z.Value - (cells[2] << 16)))),
        };
        var one = (BigInteger.One << 16);
        var blend = BigInteger.Zero;

        for (var corner = 0; (corner < 8); corner++) {
            var weight = BigInteger.One;
            var offsets = new long[3];

            for (var axis = 0; (axis < 3); axis++) {
                offsets[axis] = ((corner >> axis) & 1);
                weight *= ((offsets[axis] == 0) ? (one - fades[axis]) : fades[axis]);
            }

            var hash = Pcg3dReference(
                x: (unchecked((uint)(cells[0] + offsets[0])) ^ seedX),
                y: (unchecked((uint)(cells[1] + offsets[1])) ^ seedY),
                z: (unchecked((uint)(cells[2] + offsets[2])) ^ seedZ)
            );

            blend += (weight * (hash.X >> 16));
        }

        return blend;
    }
}
