using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>Assembles each destination bit from its position within the source block, without replication multiplication or division.</summary>
    public static BigInteger RepeatPatternBits(BigInteger pattern, int blockWidth, int bitWidth) {
        var result = BigInteger.Zero;

        for (var bit = 0; bit < bitWidth; ++bit) {
            if (!((pattern >> (bit % blockWidth)) & BigInteger.One).IsZero) {
                result |= (BigInteger.One << bit);
            }
        }

        return result;
    }
}
