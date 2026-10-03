using System.Numerics;

namespace Puck.SdfVm;

public static partial class SdfVisibility {
    /// <summary>The bits of one stable slot in the visibility record's single K word.</summary>
    public const int ShadowBits = 8;
    /// <summary>The largest code of a shadow visibility: fully visible.</summary>
    public const uint ShadowMask = ((1u << ShadowBits) - 1u);

    /// <summary>Packs four stable visibilities, saturated and rounded to their nearest eight-bit codes, slot zero
    /// in the low byte. Only presentation values are quantized.</summary>
    /// <param name="visibility">The four slots' visibilities.</param>
    /// <returns>The record's K word.</returns>
    public static uint PackShadows(Vector4 visibility) {
        uint Code(float value) => ((uint)MathF.Floor(x: ((Math.Clamp(max: 1f, min: 0f, value: value) * ShadowMask) + 0.5f)));

        return Code(value: visibility.X) | (Code(value: visibility.Y) << ShadowBits) |
            (Code(value: visibility.Z) << (2 * ShadowBits)) | (Code(value: visibility.W) << (3 * ShadowBits));
    }
    /// <summary>Decodes a stable slot's visibility from the K word.</summary>
    /// <param name="word">The packed K word.</param>
    /// <param name="slot">The stable slot, zero through three.</param>
    /// <returns>The exact eight-bit code divided by 255.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside zero through three.</exception>
    public static float ShadowAt(uint word, int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 3);
        return (((word >> (slot * ShadowBits)) & ShadowMask) / ((float)ShadowMask));
    }
}
