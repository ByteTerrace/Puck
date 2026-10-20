namespace Puck.Machines.Post;

/// <summary>Repacks a machine framebuffer's packed <c>0x00RRGGBB</c> pixels, the layout every machine core hands its host
/// (<see cref="Puck.Machines.ITimeTravelMachineCore{TInput}.Framebuffer"/>), into the R, G, B, A byte layout a PNG and a framebuffer hash
/// read, with opaque alpha.</summary>
public static class FramebufferRgba {
    /// <summary>Packs framebuffer pixels into RGBA bytes.</summary>
    /// <param name="pixels">The framebuffer's pixels, each a packed <c>0x00RRGGBB</c> value; the high byte is
    /// ignored.</param>
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
