using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    private static readonly BigInteger TwoTo32 = (BigInteger.One << 32);
    // The two constants Pcg3dLatticeNoise.Pcg3d's LCG step multiplies and adds by.
    private static readonly BigInteger Pcg3dLcgMultiplier = 1664525;
    private static readonly BigInteger Pcg3dLcgIncrement = 1013904223;

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
}
