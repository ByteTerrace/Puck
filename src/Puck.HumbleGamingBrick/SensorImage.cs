namespace Puck.HumbleGamingBrick;

/// <summary>
/// The fixed geometry of the camera cartridge's captured image: the M64282FP delivers a <c>128</c>×<c>112</c> grayscale
/// plane, and the MAC-GBD controller deposits the processed result as native GamingBrick 2bpp tiles — a <c>16</c>×<c>14</c>
/// tile grid, one <c>0x100</c>-byte page per tile row — starting at <c>0xA100</c> in save-RAM bank&#160;0, so the ROM can
/// blit it straight into VRAM. Centralizing the numbers keeps the sensor plane, the tile packer, and the register file
/// reading one set of names.
/// </summary>
public static class SensorImage {
    /// <summary>The sensor plane width in pixels (and the captured image width).</summary>
    public const int Width = 128;
    /// <summary>The sensor plane height in pixels (and the captured image height).</summary>
    public const int Height = 112;
    /// <summary>The number of grayscale bytes in one sensor plane (<see cref="Width"/> × <see cref="Height"/>).</summary>
    public const int PixelCount = (Width * Height);

    /// <summary>The number of <c>8</c>×<c>8</c> tiles across the image (<see cref="Width"/> ÷ 8).</summary>
    public const int TilesWide = (Width / 8);
    /// <summary>The number of <c>8</c>×<c>8</c> tiles down the image (<see cref="Height"/> ÷ 8).</summary>
    public const int TilesTall = (Height / 8);
    /// <summary>The size of one 2bpp tile in bytes (8 rows × 2 bitplane bytes).</summary>
    public const int TileByteCount = 16;

    /// <summary>The save-RAM offset (within bank&#160;0) where the deposited image begins — <c>0xA100</c> minus the
    /// <c>0xA000</c> window base, i.e. one tile-row page in from the start of the bank.</summary>
    public const int RamOffset = 0x0100;
    /// <summary>The total byte length of the deposited tiled image (<see cref="TilesWide"/> × <see cref="TilesTall"/> ×
    /// <see cref="TileByteCount"/>).</summary>
    public const int TiledByteCount = ((TilesWide * TilesTall) * TileByteCount);
}
