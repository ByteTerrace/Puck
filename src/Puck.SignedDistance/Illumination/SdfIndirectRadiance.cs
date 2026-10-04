using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The cache's nonnegative R11G11B10 lighting value: five exponent bits per channel, six mantissa bits for
/// red and green and five for blue. Values round through half precision, then ties to even in the stored format.</summary>
public static class SdfIndirectRadiance {
    /// <summary>Packs finite lighting, saturating negative values and non-finite inputs to black or the largest finite
    /// channel value. This is the CPU reference for sdfIndirectPackRadiance.</summary>
    /// <param name="value">Linear nonnegative radiance or normalized irradiance.</param>
    /// <returns>One word, with red in its low eleven bits.</returns>
    public static uint Pack(Vector3 value) => Channel(value.X, 4) | (Channel(value.Y, 4) << 11) | (Channel(value.Z, 5) << 22);

    /// <summary>Reads a packed lighting value.</summary>
    /// <param name="value">The R11G11B10 word.</param>
    /// <returns>The three linear channels.</returns>
    public static Vector3 Unpack(uint value) => new(
        (float)BitConverter.UInt16BitsToHalf((ushort)((value & 0x7FFu) << 4)),
        (float)BitConverter.UInt16BitsToHalf((ushort)(((value >> 11) & 0x7FFu) << 4)),
        (float)BitConverter.UInt16BitsToHalf((ushort)((value >> 22) << 5)));

    private static uint Channel(float value, int shift) {
        var maximum = shift == 4 ? 65024.0f : 64512.0f;
        var half = (uint)BitConverter.HalfToUInt16Bits((Half)(float.IsNaN(value) ? 0.0f : Math.Clamp(value, 0.0f, maximum)));
        return (half + ((1u << (shift - 1)) - 1u) + ((half >> shift) & 1u)) >> shift;
    }
}
