using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The reference Hermite smoothstep at Q16: for <c>t = (value − edge0)/(edge1 − edge0)</c>, clamped to
    /// <c>[0, 1]</c>, the exact rational <c>t²(3 − 2t)</c> scaled by <c>2¹⁶</c> and rounded ONCE, ties to even, through
    /// <see cref="RoundRationalTiesToEven"/>. Equal edges are the step <c>value &lt; edge0 ? 0 : 1</c>.</summary>
    /// <param name="edge0">The raw edge where the curve is zero.</param>
    /// <param name="edge1">The raw edge where the curve is one.</param>
    /// <param name="value">The raw value mapped.</param>
    /// <param name="numerator">The exact numerator of the curve at Q16 (zero or <c>2¹⁶</c> where it clamps).</param>
    /// <param name="denominator">The exact denominator (one where it clamps).</param>
    /// <returns>The correctly rounded raw, in <c>[0, 2¹⁶]</c>.</returns>
    /// <remarks>Formed straight from the rational <c>N²(3D − 2N)/D³</c> with <c>N = value − edge0</c> and
    /// <c>D = edge1 − edge0</c> in arbitrary width: no ratio is ever quantized, where the subject floors the ratio to
    /// Q62 before its cubic.</remarks>
    public static long Smoothstep(long edge0, long edge1, long value, out BigInteger numerator, out BigInteger denominator) {
        denominator = BigInteger.One;

        if (edge0 == edge1) {
            numerator = ((value < edge0)
                ? BigInteger.Zero
                : (BigInteger.One << 16)
            );

            return ((long)numerator);
        }

        var offset = (((BigInteger)value) - edge0);
        var span = (((BigInteger)edge1) - edge0);

        if (span.Sign < 0) {
            offset = -offset;
            span = -span;
        }

        if (offset.Sign <= 0) {
            numerator = BigInteger.Zero;

            return 0L;
        }

        if (offset >= span) {
            numerator = (BigInteger.One << 16);

            return (1L << 16);
        }

        numerator = (((offset * offset) * ((3 * span) - (2 * offset))) << 16);
        denominator = ((span * span) * span);

        return ((long)RoundRationalTiesToEven(
            denominator: denominator,
            numerator: numerator
        ));
    }
}
