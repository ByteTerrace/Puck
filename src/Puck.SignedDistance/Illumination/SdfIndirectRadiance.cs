using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The cache's nonnegative R11G11B10 lighting value: five exponent bits per channel, six mantissa bits for
/// red and green and five for blue. Values round through half precision, then ties to even in the stored format.</summary>
public static class SdfIndirectRadiance {
    /// <summary>Packs finite lighting, saturating negative values and non-finite inputs to black or the largest finite
    /// channel value. Both signed zeros encode as black. This is the CPU reference for sdfIndirectPackRadiance.</summary>
    /// <param name="value">Linear nonnegative radiance or normalized irradiance.</param>
    /// <returns>One word, with red in its low eleven bits.</returns>
    public static uint Pack(Vector3 value) => Channel(shift: 4, value: value.X) | (Channel(shift: 4, value: value.Y) << 11) | (Channel(shift: 5, value: value.Z) << 22);
    /// <summary>Reads a packed lighting value.</summary>
    /// <param name="value">The R11G11B10 word.</param>
    /// <returns>The three linear channels.</returns>
    public static Vector3 Unpack(uint value) => new(
        x: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)((value & 0x7FFu) << 4)))),
        y: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(((value >> 11) & 0x7FFu) << 4)))),
        z: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)((value >> 22) << 5)))));

    private static uint Channel(float value, int shift) {
        // The sign bit has no place in an unsigned channel and would spill into its neighbor.
        if (!(value > 0.0f)) { return 0; }
        var maximum = ((shift == 4) ? 65024.0f : 64512.0f);
        var half = ((uint)BitConverter.HalfToUInt16Bits(value: ((Half)Math.Min(val1: value, val2: maximum))));

        return (((half + ((1u << (shift - 1)) - 1u)) + ((half >> shift) & 1u)) >> shift);
    }
}
