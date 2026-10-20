using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // The working scale the degree conversion's reference π is enclosed at: 128 bits past the widest input the
    // subject accepts (2^63 raw over 180), so the two rounding candidates the enclosure yields agree everywhere a
    // half-raw is not within 2^-100 of the exact value.
    private const int DegreePiBitCount = 256;

    private static readonly Enclosure DegreePi = Pi(bitCount: DegreePiBitCount);

    /// <summary>The Q16 radians nearest to <c>numerator / 2^denominatorExponent</c> degrees, decided from the
    /// Machin-derived enclosure of <c>π</c> at 256 bits: the rounding of the exact rational against each enclosure
    /// endpoint, which agree unless the exact value sits within the enclosure's width of a half-raw.</summary>
    /// <param name="numerator">The exact degree numerator.</param>
    /// <param name="denominatorExponent">The power of two the numerator is scaled by.</param>
    /// <param name="raw">The rounded raw when the enclosure decides it; zero otherwise.</param>
    /// <returns><see langword="false"/> when the two endpoints round apart, so the value is undecidable at this width.</returns>
    public static bool TryDegreesToRadiansRaw(BigInteger numerator, int denominatorExponent, out BigInteger raw) {
        // raw = round(n · π · 2^16 / (180 · 2^e)) = round(n · (π·2^256) / (180 · 2^(256 + e − 16))).
        var denominator = (new BigInteger(value: 180) << ((DegreePiBitCount + denominatorExponent) - 16));
        var low = RoundRationalTiesToEven(
            denominator: denominator,
            numerator: (numerator * DegreePi.Low)
        );
        var high = RoundRationalTiesToEven(
            denominator: denominator,
            numerator: (numerator * DegreePi.High)
        );

        raw = low;

        return (low == high);
    }
    /// <summary>The exact rational a finite double denotes, assembled from the IEEE 754 binary64 fields read off its
    /// bits: a sign, an eleven-bit exponent biased by 1023, a fifty-two-bit fraction with the implicit bit set for a
    /// normal value.</summary>
    /// <param name="value">The finite double.</param>
    /// <returns>The numerator and the power-of-two exponent the numerator is scaled by (non-negative).</returns>
    public static (BigInteger Numerator, int DenominatorExponent) Binary64Rational(double value) {
        var bits = BitConverter.DoubleToInt64Bits(value: value);
        var exponentField = ((int)((bits >> 52) & 0x7FFL));
        var fraction = (bits & 0xFFFFFFFFFFFFFL);
        var significand = new BigInteger(value: ((exponentField == 0)
            ? fraction
            : (fraction | (1L << 52))));
        var exponent = (((exponentField == 0)
            ? 1
            : exponentField
        ) - 1075);

        if (bits < 0L) {
            significand = -significand;
        }

        return ((exponent >= 0)
            ? ((significand << exponent), 0)
            : (significand, -exponent)
        );
    }
    /// <summary>The exact rational a decimal denotes, read off <see cref="decimal.GetBits(decimal)"/>: the sign bit,
    /// the base-10 scale in bits 16–23 of the flags word, and the 96-bit unscaled magnitude.</summary>
    /// <param name="value">The decimal.</param>
    /// <returns>The signed numerator and the power of ten it is scaled by.</returns>
    public static (BigInteger Numerator, BigInteger Denominator) DecimalRational(decimal value) {
        var bits = decimal.GetBits(d: value);
        var scale = ((bits[3] >> 16) & 0xFF);
        var unscaled = ((((BigInteger)((uint)bits[2])) << 64) + (((BigInteger)((uint)bits[1])) << 32) + ((uint)bits[0]));

        return (((bits[3] < 0)
            ? -unscaled
            : unscaled), BigInteger.Pow(
            value: 10,
            exponent: scale
        ));
    }
    /// <summary>The least integer at or above an exact rational.</summary>
    /// <param name="numerator">The numerator.</param>
    /// <param name="denominator">The non-zero denominator.</param>
    /// <returns><c>⌈numerator / denominator⌉</c>.</returns>
    public static BigInteger CeilingQuotient(BigInteger numerator, BigInteger denominator) =>
        -FloorQuotient(
            denominator: denominator,
            numerator: -numerator
        );
    /// <summary>The SplitMix64 output finalizer formed in arbitrary width with the modulus <c>2⁶⁴</c> taken explicitly
    /// at every multiply, where the subject relies on the carrier's wrap.</summary>
    /// <param name="value">The word to mix.</param>
    /// <returns>The mixed word.</returns>
    /// <remarks>Transcribed from the published algorithm (Steele, Lea and Flood, "Fast splittable pseudorandom number
    /// generators"): <c>z ^= z &gt;&gt; 30; z *= 0xBF58476D1CE4E5B9; z ^= z &gt;&gt; 27; z *= 0x94D049BB133111EB;
    /// z ^= z &gt;&gt; 31</c>.</remarks>
    public static ulong SplitMix64Finalizer(ulong value) {
        var wide = ((BigInteger)value);

        wide ^= (wide >> 30);
        wide = ((wide * ((BigInteger)0xBF58476D1CE4E5B9UL)) % TwoTo64);
        wide ^= (wide >> 27);
        wide = ((wide * ((BigInteger)0x94D049BB133111EBUL)) % TwoTo64);
        wide ^= (wide >> 31);

        return ((ulong)wide);
    }
    /// <summary>The jittered point of one scatter block, formed over <see cref="Pcg3dReference"/> with the block
    /// arithmetic in arbitrary width.</summary>
    /// <param name="blockX">The block index along X.</param>
    /// <param name="blockZ">The block index along Z.</param>
    /// <param name="seed">The hash seed.</param>
    /// <param name="spacing">The block edge in cells.</param>
    /// <param name="radius">The radius inset in cells.</param>
    /// <returns>The point's cell coordinates.</returns>
    public static (BigInteger X, BigInteger Z) ScatterPointReference(int blockX, int blockZ, uint seed, int spacing, int radius) {
        var hash = Pcg3dReference(
            x: WrapToUInt32(value: blockX),
            y: WrapToUInt32(value: blockZ),
            z: seed
        );
        var inset = BigInteger.Max(
            left: BigInteger.Zero,
            right: (((BigInteger)spacing) - (2 * ((BigInteger)radius)))
        );
        var jitterX = (inset.IsZero
            ? BigInteger.Zero
            : (((BigInteger)hash.X) % inset));
        var jitterZ = (inset.IsZero
            ? BigInteger.Zero
            : (((BigInteger)hash.Y) % inset));

        return (
            (((((BigInteger)blockX) * spacing) + radius) + jitterX),
            (((((BigInteger)blockZ) * spacing) + radius) + jitterZ)
        );
    }
    /// <summary>Whether an offset lies in the closed cone about a forward with half-angle cosine <c>c/2¹⁶</c>, decided
    /// exactly: <c>(o·f)·2¹⁶ ≥ c·√(|o|²|f|²)</c> by squaring both sides in arbitrary width once their signs are
    /// settled.</summary>
    /// <param name="offset">The offset's three raws.</param>
    /// <param name="forward">The forward's three raws.</param>
    /// <param name="cosineRaw">The half-angle cosine's raw, in <c>[−2¹⁶, 2¹⁶]</c>.</param>
    /// <returns>The exact verdict; a zero forward is outside every cone.</returns>
    public static bool ConeContains(ReadOnlySpan<long> offset, ReadOnlySpan<long> forward, long cosineRaw) {
        if ((forward[0] | forward[1] | forward[2]) == 0L) {
            return false;
        }

        var dot = BigInteger.Zero;

        for (var lane = 0; (lane < 3); ++lane) {
            dot += (((BigInteger)offset[lane]) * forward[lane]);
        }

        var scaledDot = (dot << 16);
        var squaredNorms = (SquaredNorm(raws: offset) * SquaredNorm(raws: forward));
        var cosine = ((BigInteger)cosineRaw);
        var rightSquared = ((cosine * cosine) * squaredNorms);

        if (cosine.Sign >= 0) {
            return ((scaledDot.Sign >= 0) && ((scaledDot * scaledDot) >= rightSquared));
        }

        return ((scaledDot.Sign >= 0) || ((scaledDot * scaledDot) <= rightSquared));
    }
}
