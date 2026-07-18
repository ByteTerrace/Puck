namespace Puck.Abstractions.Lighting;

/// <summary>
/// A single lamp's color: 8-bit-per-channel RGB plus a separate 8-bit intensity (brightness) channel, matching
/// the HID LampArray per-lamp color model. A device that has no dedicated intensity channel folds
/// <see cref="Intensity"/> into the RGB channels; one that has fewer than 256 levels per channel quantizes.
/// </summary>
/// <param name="Red">The red channel, 0..255.</param>
/// <param name="Green">The green channel, 0..255.</param>
/// <param name="Blue">The blue channel, 0..255.</param>
/// <param name="Intensity">The intensity (brightness) channel, 0..255; defaults to fully lit.</param>
public readonly record struct LampColor(byte Red, byte Green, byte Blue, byte Intensity = 255) {
    /// <summary>Gets a fully-off lamp (all channels and intensity zero).</summary>
    public static LampColor Off => new(Red: 0, Green: 0, Blue: 0, Intensity: 0);

    /// <summary>Creates a color from RGB with full intensity.</summary>
    /// <param name="red">The red channel, 0..255.</param>
    /// <param name="green">The green channel, 0..255.</param>
    /// <param name="blue">The blue channel, 0..255.</param>
    /// <returns>The color at full intensity.</returns>
    public static LampColor Rgb(byte red, byte green, byte blue) {
        return new LampColor(Red: red, Green: green, Blue: blue, Intensity: 255);
    }

    /// <summary>
    /// Returns this color scaled toward black by <paramref name="scale"/> (clamped to 0..1). Brightness is
    /// carried in the RGB channels rather than the intensity byte, because many lamps (e.g. per-key keyboards)
    /// expose only a single intensity level — scaling RGB dims portably on every device.
    /// </summary>
    /// <param name="scale">The brightness multiplier, 0..1.</param>
    /// <returns>The dimmed color.</returns>
    public LampColor Scale(float scale) {
        var clamped = ((scale < 0f) ? 0f : ((scale > 1f) ? 1f : scale));

        return this with {
            Red = (byte)(Red * clamped),
            Green = (byte)(Green * clamped),
            Blue = (byte)(Blue * clamped),
        };
    }
}
