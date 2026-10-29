using System.Numerics;

namespace Puck.SdfVm;

public static partial class SdfVisibility {
    /// <summary>The bits of one stable slot in the visibility record's single K word.</summary>
    public const int ShadowBits = 8;
    /// <summary>The mask of one slot's code within the K word.</summary>
    public const uint ShadowMask = ((1u << ShadowBits) - 1u);
    /// <summary>The code of a slot the surface pass did not query: the surface turns from the slot's light so far that its
    /// wrapped Lambert term is zero. It decodes as fully visible, and the views pass's shadow filter leaves it out of every
    /// average, since it measures nothing.</summary>
    public const uint ShadowUnqueried = ShadowMask;
    /// <summary>The code of a fully visible queried slot: queried visibilities take the codes zero through this one.</summary>
    public const int ShadowScale = ((int)ShadowMask - 1);

    /// <summary>Packs four stable visibilities, slot zero in the low byte: a negative visibility as
    /// <see cref="ShadowUnqueried"/>, any other saturated and rounded to its nearest code of zero through
    /// <see cref="ShadowScale"/>. Only presentation values are quantized.</summary>
    /// <param name="visibility">The four slots' visibilities, negative for a slot not queried.</param>
    /// <returns>The record's K word.</returns>
    public static uint PackShadows(Vector4 visibility) {
        static uint Code(float value) => ((value < 0f)
            ? ShadowUnqueried
            : ((uint)MathF.Floor(x: ((Math.Clamp(max: 1f, min: 0f, value: value) * ShadowScale) + 0.5f))));

        return Code(value: visibility.X) | (Code(value: visibility.Y) << ShadowBits) |
            (Code(value: visibility.Z) << (2 * ShadowBits)) | (Code(value: visibility.W) << (3 * ShadowBits));
    }
    /// <summary>Decodes a stable slot's visibility from the K word.</summary>
    /// <param name="word">The packed K word.</param>
    /// <param name="slot">The stable slot, zero through three.</param>
    /// <returns>One for an unqueried slot, otherwise the exact code divided by <see cref="ShadowScale"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside zero through three.</exception>
    public static float ShadowAt(uint word, int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 3);
        var code = ((word >> (slot * ShadowBits)) & ShadowMask);

        return ((code == ShadowUnqueried) ? 1f : (code / ((float)ShadowScale)));
    }
}
