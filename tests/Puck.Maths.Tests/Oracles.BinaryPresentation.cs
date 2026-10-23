using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // Search the ordered IEEE encodings, then compare exact distances to the two neighbours in units of the
    // smallest subnormal. This deliberately shares neither a leading-bit calculation nor a rounding shift with
    // the conversion kernels. The encoding above MaxValue denotes the next power of two for the overflow tie.
    public static uint NearestBinaryPresentationBits(BigInteger raw, int fractionBits, int mantissaBits, int exponentBits) {
        var sign = raw.Sign < 0 ? 1U << (mantissaBits + exponentBits) : 0U;
        var bias = (1 << (exponentBits - 1)) - 1;
        var infinity = ((1U << exponentBits) - 1U) << mantissaBits;
        var exact = BigInteger.Abs(raw) << (bias + mantissaBits - 1);
        var denominator = BigInteger.One << fractionBits;

        BigInteger Units(uint bits) {
            var exponent = (int)(bits >> mantissaBits);
            var fraction = bits & ((1U << mantissaBits) - 1U);
            return exponent == 0 ? fraction : ((BigInteger)((1U << mantissaBits) + fraction)) << (exponent - 1);
        }

        uint lower = 0, upper = infinity;
        while (upper - lower > 1) {
            var middle = lower + ((upper - lower) >> 1);
            if (Units(middle) * denominator <= exact) { lower = middle; } else { upper = middle; }
        }

        var below = exact - Units(lower) * denominator;
        var above = Units(upper) * denominator - exact;
        return sign | (below < above || (below == above && (lower & 1U) == 0) ? lower : upper);
    }
}
