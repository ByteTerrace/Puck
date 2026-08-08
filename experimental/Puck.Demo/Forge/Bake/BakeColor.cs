namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The bake's colour currency: packed RGB555 (<c>bbbbbgggggrrrrr</c>, the CGB palette-RAM bit layout) with ROUNDING
/// conversions — <c>(v·31+127)/255</c> up-conversion parity. `HgbImage.EncodePalette` uses truncation for its separate
/// wire-compatibility contract. All palette fitting and
/// error math happens in this space so the fit optimizes exactly what the hardware will display.
/// </summary>
internal static class BakeColor {
    /// <summary>Rounds an 8-bit channel to 5 bits: <c>(v·31+127)/255</c>.</summary>
    /// <param name="value">The 8-bit channel.</param>
    /// <returns>The rounded 5-bit channel (0..31).</returns>
    public static int Round5(int value) =>
        (((Math.Clamp(value: value, max: 255, min: 0) * 31) + 127) / 255);

    /// <summary>Expands a 5-bit channel back to 8 bits with rounding: <c>(v·255+15)/31</c>.</summary>
    /// <param name="value5">The 5-bit channel (0..31).</param>
    /// <returns>The 8-bit channel.</returns>
    public static byte Expand8(int value5) =>
        (byte)(((value5 * 255) + 15) / 31);

    /// <summary>Packs three 5-bit channels into the CGB wire layout (<c>r | g&lt;&lt;5 | b&lt;&lt;10</c>).</summary>
    /// <param name="r5">The red channel (0..31).</param>
    /// <param name="g5">The green channel (0..31).</param>
    /// <param name="b5">The blue channel (0..31).</param>
    /// <returns>The packed RGB555 colour.</returns>
    public static ushort Pack(int r5, int g5, int b5) =>
        (ushort)(r5 | (g5 << 5) | (b5 << 10));

    /// <summary>Packs an 8-bit RGB triple straight to rounded RGB555.</summary>
    /// <param name="r">The red channel (0..255).</param>
    /// <param name="g">The green channel (0..255).</param>
    /// <param name="b">The blue channel (0..255).</param>
    /// <returns>The packed RGB555 colour.</returns>
    public static ushort PackFrom8(int r, int g, int b) =>
        Pack(b5: Round5(value: b), g5: Round5(value: g), r5: Round5(value: r));

    /// <summary>Unpacks one 5-bit channel from a packed colour.</summary>
    /// <param name="colour">The packed RGB555 colour.</param>
    /// <param name="channel">The channel index (0 = red, 1 = green, 2 = blue).</param>
    /// <returns>The 5-bit channel value.</returns>
    public static int Channel(ushort colour, int channel) =>
        (colour >> (channel * 5)) & 0x1F;

    /// <summary>Squared Euclidean distance between two packed colours, in 5-bit channel units.</summary>
    /// <param name="a">The first colour.</param>
    /// <param name="b">The second colour.</param>
    /// <returns>The squared distance (0..2883).</returns>
    public static int DistanceSquared(ushort a, ushort b) {
        var dr = (Channel(channel: 0, colour: a) - Channel(channel: 0, colour: b));
        var dg = (Channel(channel: 1, colour: a) - Channel(channel: 1, colour: b));
        var db = (Channel(channel: 2, colour: a) - Channel(channel: 2, colour: b));

        return (((dr * dr) + (dg * dg)) + (db * db));
    }

    /// <summary>An integer Rec.709-weighted luminance for ORDERING palette colours (lightest first) — scaled so
    /// ties break on the packed value, never on float noise.</summary>
    /// <param name="colour">The packed RGB555 colour.</param>
    /// <returns>The weighted luminance (larger = lighter).</returns>
    public static int Luminance(ushort colour) =>
        (((2126 * Channel(channel: 0, colour: colour)) + (7152 * Channel(channel: 1, colour: colour))) + (722 * Channel(channel: 2, colour: colour)));

    /// <summary>Expands a packed colour to packed <c>0xRRGGBB</c> (the diagnostics/preview form).</summary>
    /// <param name="colour">The packed RGB555 colour.</param>
    /// <returns>The 24-bit RGB value.</returns>
    public static uint ToRgb24(ushort colour) =>
        ((uint)Expand8(value5: Channel(channel: 0, colour: colour)) << 16)
            | ((uint)Expand8(value5: Channel(channel: 1, colour: colour)) << 8)
            | Expand8(value5: Channel(channel: 2, colour: colour));

    /// <summary>Writes a palette table into CGB palette-RAM wire bytes (little-endian RGB555 pairs), padding every
    /// palette to exactly four colours by repeating its last (or black when empty).</summary>
    /// <param name="palettes">The palettes (each up to four colours, display order).</param>
    /// <param name="reserveTransparentSlot">Whether slot 0 of each palette is the transparent OBJ slot — the
    /// fitted colours then occupy slots 1..3 and slot 0 writes as black.</param>
    /// <returns>The wire-form palette set.</returns>
    public static BakedPaletteSet EncodePalettes(IReadOnlyList<ushort[]> palettes, bool reserveTransparentSlot) {
        var bytes = new byte[(palettes.Count * 8)];

        for (var palette = 0; (palette < palettes.Count); palette++) {
            var colours = palettes[palette];
            var offset = (reserveTransparentSlot ? 1 : 0);

            for (var slot = 0; (slot < 4); slot++) {
                var sourceIndex = (slot - offset);
                var value = (((sourceIndex >= 0) && (colours.Length > 0))
                    ? colours[Math.Min(val1: sourceIndex, val2: (colours.Length - 1))]
                    : (ushort)0);
                var byteOffset = ((palette * 8) + (slot * 2));

                bytes[byteOffset] = (byte)(value & 0xFF);
                bytes[(byteOffset + 1)] = (byte)((value >> 8) & 0xFF);
            }
        }

        return new BakedPaletteSet(Count: palettes.Count, Rgb555Data: bytes);
    }
}
