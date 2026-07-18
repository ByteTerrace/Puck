namespace Puck.HumbleGamingBrick;

/// <summary>
/// The minimal, deterministic payload of one completed link-cable printer job — the machine-to-host event a
/// <see cref="GamePrinterDevice"/> raises when it finishes a PRINT command. It is a plain immutable value (never emulated
/// state, never serialized): a host receives it to turn a printed image into a texture, a creation, or a save, exactly
/// the "cartridge-to-host events" seam the overworld plan sketches.
/// <para>
/// The pixels are the assembled band buffer with the PRINT command's palette applied — one byte per pixel, row-major,
/// each byte a 0-3 printer shade (0 = lightest .. 3 = darkest) obtained by looking the 2-bit source dot up in the
/// palette. A host maps those four shades to whatever colors it presents. The dimensions, margins, palette, and exposure
/// are carried verbatim so a host can reproduce the exact sheet the game asked for.
/// </para>
/// </summary>
public sealed class GamePrintout {
    private readonly byte[] m_pixels;

    /// <summary>Creates a printout from an assembled, palette-applied band buffer.</summary>
    /// <param name="width">The image width in pixels (always 160 for the link-cable printer).</param>
    /// <param name="height">The image height in pixels (a multiple of 16 — two 8-pixel tile rows per DATA band).</param>
    /// <param name="topMargin">The feed-before margin (PRINT command byte 1, high nibble), in 8-pixel units.</param>
    /// <param name="bottomMargin">The feed-after margin (PRINT command byte 1, low nibble), in 8-pixel units.</param>
    /// <param name="palette">The PRINT command's palette byte (byte 2): each 2-bit source dot indexes it for its shade.</param>
    /// <param name="exposure">The PRINT command's exposure/density byte (byte 3, 7 bits).</param>
    /// <param name="pixels">The palette-applied shade buffer, <paramref name="width"/>×<paramref name="height"/> bytes,
    /// each 0-3; the printout takes ownership of the array.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pixels"/> is <see langword="null"/>.</exception>
    public GamePrintout(int width, int height, byte topMargin, byte bottomMargin, byte palette, byte exposure, byte[] pixels) {
        ArgumentNullException.ThrowIfNull(argument: pixels);

        BottomMargin = bottomMargin;
        Exposure = exposure;
        Height = height;
        m_pixels = pixels;
        Palette = palette;
        TopMargin = topMargin;
        Width = width;
    }

    /// <summary>Gets the image width in pixels (160).</summary>
    public int Width { get; }
    /// <summary>Gets the image height in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets the feed-before margin (8-pixel units).</summary>
    public byte TopMargin { get; }
    /// <summary>Gets the feed-after margin (8-pixel units).</summary>
    public byte BottomMargin { get; }
    /// <summary>Gets the palette byte the print was rendered with.</summary>
    public byte Palette { get; }
    /// <summary>Gets the exposure/density byte (7 bits).</summary>
    public byte Exposure { get; }
    /// <summary>Gets the palette-applied shade buffer (one 0-3 byte per pixel, row-major).</summary>
    public ReadOnlySpan<byte> Pixels =>
        m_pixels;

    /// <summary>Computes a stable 64-bit FNV-1a fingerprint over the dimensions, margins, palette, exposure, and every
    /// pixel — the deterministic identity a gate compares across two runs to prove the print is reproducible.</summary>
    /// <returns>The fingerprint.</returns>
    public ulong Fingerprint() {
        const ulong OffsetBasis = 0xCBF29CE484222325ul;
        const ulong Prime = 0x100000001B3ul;

        var hash = OffsetBasis;

        void Fold(byte value) =>
            hash = ((hash ^ value) * Prime);

        Fold(value: (byte)Width);
        Fold(value: (byte)(Width >> 8));
        Fold(value: (byte)Height);
        Fold(value: (byte)(Height >> 8));
        Fold(value: TopMargin);
        Fold(value: BottomMargin);
        Fold(value: Palette);
        Fold(value: Exposure);

        foreach (var pixel in m_pixels) {
            Fold(value: pixel);
        }

        return hash;
    }
}
