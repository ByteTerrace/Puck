namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The picture-processing unit: it owns the video memories (palette, VRAM, OAM), walks the 240×160 raster on
/// the master clock, drives the display registers (0x00–0x56), and raises the V-blank / H-blank / V-count
/// interrupts. To keep the wiring acyclic it requests interrupts through the controller it is given and merely
/// <em>flags</em> the H/V-blank moments — the bus polls those flags after stepping it and fires the timed DMAs,
/// so the PPU needs no reference back to the bus or DMA.
/// </summary>
public interface IAgbPpu {
    /// <summary>Gets the completed 240×160 frame as packed 0xAARRGGBB pixels, row-major.</summary>
    ReadOnlySpan<uint> Framebuffer { get; }

    /// <summary>Reads a display register (DISPCNT, DISPSTAT, VCOUNT, BG/window/blend control).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page (0x00–0x56).</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes a display register.</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);

    /// <summary>Gets whether the PPU is contending for the palette bus this cycle, so a CPU/DMA palette access must
    /// stall (the PPU's palette-RAM contention). True only during a rendered visible scanline, on the specific dot
    /// phase the renderer reads palette RAM.</summary>
    bool PramContention => false;

    /// <summary>Reads palette/VRAM/OAM, selected by the region nibble of <paramref name="address"/>.</summary>
    /// <param name="address">The CPU address (0x05/0x06/0x07 region).</param>
    /// <param name="width">The access width in bytes (1, 2, or 4).</param>
    /// <returns>The value read.</returns>
    uint ReadVideo(uint address, int width);

    /// <summary>Writes palette/VRAM/OAM, applying the hardware 8-bit-write rules.</summary>
    /// <param name="address">The CPU address (0x05/0x06/0x07 region).</param>
    /// <param name="width">The access width in bytes (1, 2, or 4).</param>
    /// <param name="value">The value to write.</param>
    void WriteVideo(uint address, int width, uint value);

    /// <summary>Returns and clears the flag marking that the most recent step entered V-blank (line 160).</summary>
    /// <returns><see langword="true"/> if V-blank just started.</returns>
    bool ConsumeVBlankStarted();

    /// <summary>Returns and clears the flag marking that the most recent step entered a visible H-blank.</summary>
    /// <returns><see langword="true"/> if an H-blank just started on a visible scanline.</returns>
    bool ConsumeHBlankStarted();

    /// <summary>Returns and clears the flag marking an H-blank on a video-capture scanline (2–161), which fires
    /// DMA3's special (video-capture) timing.</summary>
    /// <returns><see langword="true"/> if a video-capture H-blank just started.</returns>
    bool ConsumeVideoCaptureStarted();

    /// <summary>Returns and clears the flag marking the end of the video-capture window (entering line 162), which
    /// disables a running video-capture DMA.</summary>
    /// <returns><see langword="true"/> if the video-capture window just ended.</returns>
    bool ConsumeVideoCaptureEnded();
}
