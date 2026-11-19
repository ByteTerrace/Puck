namespace Puck.Maths;

/// <summary>
/// The sRGB transfer function (IEC 61966-2-1), in its extended form: the curve continues past one and mirrors below
/// zero, so headroom and negative excursions encode and decode on the curve and the two directions are inverses over
/// every real value. A caller that wants the standard's clamped range clamps its input. <c>Shared/transfer.hlsli</c>
/// (<c>puckSrgbToLinearExtended</c>, <c>puckLinearToSrgbExtended</c>) is the shaders' twin.
/// </summary>
public static class SrgbTransfer {
    /// <summary>The linear value below which the encode is the linear segment.</summary>
    public const double LinearLimit = 0.0031308;
    /// <summary>The encoded value below which the decode is the linear segment.</summary>
    public const double EncodedLimit = 0.04045;

    /// <summary>Decodes an sRGB-encoded value to linear light.</summary>
    /// <param name="encoded">The encoded value; one is SDR white.</param>
    /// <returns>The linear value, mirrored below zero and extended past one.</returns>
    public static double ToLinear(double encoded) {
        var magnitude = Math.Abs(value: encoded);
        var linear = ((magnitude <= EncodedLimit)
            ? (magnitude / 12.92)
            : Math.Pow(x: ((magnitude + 0.055) / 1.055), y: 2.4));

        return ((encoded < 0.0) ? -linear : linear);
    }
    /// <summary>Encodes linear light on the sRGB curve.</summary>
    /// <param name="linear">The linear value; one is SDR white.</param>
    /// <returns>The encoded value, mirrored below zero and extended past one.</returns>
    public static double ToEncoded(double linear) {
        var magnitude = Math.Abs(value: linear);
        var encoded = ((magnitude <= LinearLimit)
            ? (magnitude * 12.92)
            : ((1.055 * Math.Pow(x: magnitude, y: (1.0 / 2.4))) - 0.055));

        return ((linear < 0.0) ? -encoded : encoded);
    }
    /// <summary>Decodes an sRGB-encoded value to linear light, in single precision.</summary>
    /// <param name="encoded">The encoded value.</param>
    /// <returns>The linear value.</returns>
    public static float ToLinear(float encoded) => ((float)ToLinear(encoded: (double)encoded));
    /// <summary>Encodes linear light on the sRGB curve, in single precision.</summary>
    /// <param name="linear">The linear value.</param>
    /// <returns>The encoded value.</returns>
    public static float ToEncoded(float linear) => ((float)ToEncoded(linear: (double)linear));
}
