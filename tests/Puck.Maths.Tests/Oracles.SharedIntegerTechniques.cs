using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    public static uint ReciprocalDivision(uint value, uint divisor) => ((uint)(((BigInteger)value) / divisor));
    public static int PackedPairHash(long value) {
        var word = ((BigInteger)value) & ((BigInteger.One << 64) - 1);
        var product = (word * BigInteger.Parse(value: "11400714819323198485", provider: System.Globalization.CultureInfo.InvariantCulture)) & ((BigInteger.One << 64) - 1);

        return unchecked((int)((uint)(product >> 32)));
    }
}
