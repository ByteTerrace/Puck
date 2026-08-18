namespace Puck.HumbleGamingBrick.Post;

// Repacks a framebuffer's packed 0x00RRGGBB pixels into the R,G,B,A byte layout PngEncoder wants, opaque alpha.
internal static class FramebufferRgba {
    /// <summary>Packs framebuffer pixels into RGBA bytes.</summary>
    /// <param name="pixels">The framebuffer's pixels, each a packed <c>0x00RRGGBB</c> value.</param>
    /// <returns>The RGBA bytes, four per pixel.</returns>
    public static byte[] Pack(ReadOnlySpan<uint> pixels) {
        var rgba = new byte[(pixels.Length * 4)];

        for (var index = 0; (index < pixels.Length); ++index) {
            var offset = (index * 4);
            var pixel = pixels[index];

            rgba[offset] = ((byte)(pixel >> 16));
            rgba[(offset + 1)] = ((byte)(pixel >> 8));
            rgba[(offset + 2)] = ((byte)pixel);
            rgba[(offset + 3)] = 0xFF;
        }

        return rgba;
    }
}
