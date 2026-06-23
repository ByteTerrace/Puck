namespace Puck.GameBoy;

/// <summary>
/// The Game Boy Color additions to the PPU: background and object palette RAM (eight four-color palettes each, in
/// 15-bit BGR555), the per-tile attributes held in VRAM bank&#160;1 (palette, tile bank, flips, and BG-over-OBJ
/// priority), and the color render path. On the CGB <c>LCDC</c> bit&#160;0 is a BG/OBJ master-priority bit rather
/// than a BG-enable, and object priority defaults to OAM order. The DMG render path in <see cref="Ppu"/> is used
/// unchanged for monochrome models.
/// </summary>
public sealed partial class Ppu {
    private const int PaletteRamSize = 64; // 8 palettes * 4 colors * 2 bytes
    private const int VideoRamBankStride = 0x2000;

    private bool m_isColor;
    private readonly byte[] m_backgroundPaletteRam = new byte[PaletteRamSize];
    private readonly byte[] m_objectPaletteRam = new byte[PaletteRamSize];
    // DMG-on-CGB compatibility colorization: when a CGB console runs a game with no CGB flag it renders DMG-style but
    // maps the shades through the boot ROM's assigned palettes, in RGBA, instead of the grayscale ramp.
    private bool m_dmgCompatColorization;
    private readonly uint[] m_compatBackground = new uint[4];
    private readonly uint[] m_compatObject0 = new uint[4];
    private readonly uint[] m_compatObject1 = new uint[4];
    // Per-pixel BG-over-OBJ priority for the current line (the tile attribute's bit 7), used to resolve object
    // priority against the background.
    private readonly bool[] m_lineBackgroundPriority = new bool[ScreenWidth];

    private byte m_backgroundPaletteIndex; // BGPI: bit 7 auto-increment, bits 0-5 index
    private byte m_objectPaletteIndex; // OBPI
    private byte m_objectPriorityMode; // OPRI (bit 0: 1 = X-coordinate priority, 0 = OAM-order priority)
    private CgbColorCorrection m_colorCorrection = CgbColorCorrection.ModernBalanced;

    // Measured CGB LCD per-channel response curve (5-bit input -> 8-bit output).
    private static readonly byte[] s_responseCurve = [
        0, 6, 12, 20, 28, 36, 45, 56, 66, 76, 88, 100, 113, 125, 137, 149,
        161, 172, 182, 192, 202, 210, 218, 225, 232, 238, 243, 247, 250, 252, 254, 255,
    ];

    // The ModernBalanced green output, blended from the curved green and blue (gamma 1.6), precomputed per (g, b).
    private static readonly byte[] s_balancedGreen = BuildBalancedGreen();

    /// <summary>Gets whether this is a Game Boy Color PPU (color render path, palette RAM, tile attributes).</summary>
    public bool IsColor =>
        m_isColor;

    /// <summary>Gets or sets how CGB palette colors are corrected for display. Defaults to
    /// <see cref="CgbColorCorrection.ModernBalanced"/>, the closest match to the physical CGB screen.</summary>
    public CgbColorCorrection ColorCorrection {
        get => m_colorCorrection;
        set => m_colorCorrection = value;
    }

    /// <summary>Gets or sets whether color correction is applied, as a convenience over <see cref="ColorCorrection"/>:
    /// enabling selects <see cref="CgbColorCorrection.ModernBalanced"/>, disabling selects
    /// <see cref="CgbColorCorrection.Disabled"/>.</summary>
    public bool ColorCorrectionEnabled {
        get => (m_colorCorrection != CgbColorCorrection.Disabled);
        set => m_colorCorrection = (value ? CgbColorCorrection.ModernBalanced : CgbColorCorrection.Disabled);
    }

    /// <summary>Reads the background palette index register (<c>BGPI</c>, <c>0xFF68</c>).</summary>
    public byte ReadBackgroundPaletteIndex() =>
        (byte)(m_backgroundPaletteIndex | 0x40);
    /// <summary>Writes the background palette index register (<c>BGPI</c>).</summary>
    public void WriteBackgroundPaletteIndex(byte value) =>
        m_backgroundPaletteIndex = (byte)(value & 0xBF);
    /// <summary>Reads the background palette data register (<c>BGPD</c>, <c>0xFF69</c>) at the current index.</summary>
    public byte ReadBackgroundPaletteData() =>
        (m_isColor && IsPaletteAccessible)
            ? m_backgroundPaletteRam[m_backgroundPaletteIndex & 0x3F]
            : (byte)0xFF;
    /// <summary>Writes the background palette data register (<c>BGPD</c>), advancing the index when auto-increment is set.</summary>
    public void WriteBackgroundPaletteData(byte value) {
        if (IsPaletteAccessible) {
            m_backgroundPaletteRam[m_backgroundPaletteIndex & 0x3F] = value;
        }

        m_backgroundPaletteIndex = AdvancePaletteIndex(index: m_backgroundPaletteIndex);
    }

    /// <summary>Reads the object palette index register (<c>OBPI</c>, <c>0xFF6A</c>).</summary>
    public byte ReadObjectPaletteIndex() =>
        (byte)(m_objectPaletteIndex | 0x40);
    /// <summary>Writes the object palette index register (<c>OBPI</c>).</summary>
    public void WriteObjectPaletteIndex(byte value) =>
        m_objectPaletteIndex = (byte)(value & 0xBF);
    /// <summary>Reads the object palette data register (<c>OBPD</c>, <c>0xFF6B</c>) at the current index.</summary>
    public byte ReadObjectPaletteData() =>
        (m_isColor && IsPaletteAccessible)
            ? m_objectPaletteRam[m_objectPaletteIndex & 0x3F]
            : (byte)0xFF;
    /// <summary>Writes the object palette data register (<c>OBPD</c>), advancing the index when auto-increment is set.</summary>
    public void WriteObjectPaletteData(byte value) {
        if (IsPaletteAccessible) {
            m_objectPaletteRam[m_objectPaletteIndex & 0x3F] = value;
        }

        m_objectPaletteIndex = AdvancePaletteIndex(index: m_objectPaletteIndex);
    }

    /// <summary>Reads the object priority mode register (<c>OPRI</c>, <c>0xFF6C</c>).</summary>
    public byte ReadObjectPriorityMode() =>
        (byte)(m_objectPriorityMode | 0xFE);
    /// <summary>Writes the object priority mode register (<c>OPRI</c>).</summary>
    public void WriteObjectPriorityMode(byte value) =>
        m_objectPriorityMode = (byte)(value & 0x01);

    // Palette RAM is locked from the CPU while the PPU is reading it in mode 3, like VRAM.
    private bool IsPaletteAccessible =>
        (!m_enabled || (m_reportedMode != PpuMode.Drawing));

    private static byte AdvancePaletteIndex(byte index) =>
        // Bit 7 (auto-increment) is preserved; the 6-bit index wraps within 0-63.
        (((index & 0x80) != 0)
            ? (byte)((index & 0x80) | ((index + 1) & 0x3F))
            : index);

    private byte ReadVideoRamBank(int address, int bank) =>
        m_videoRam[(bank * VideoRamBankStride) + (address - VideoRamBase)];

    /// <summary>Enables DMG-on-CGB compatibility colorization with the boot ROM's assigned palettes (four BGR555 colors
    /// each), forcing the DMG render path so the game's shades resolve through these instead of grayscale.</summary>
    /// <param name="background">The four background-palette colors.</param>
    /// <param name="object0">The four object-palette-0 colors.</param>
    /// <param name="object1">The four object-palette-1 colors.</param>
    public void EnableDmgCompatibilityColorization(ReadOnlySpan<ushort> background, ReadOnlySpan<ushort> object0, ReadOnlySpan<ushort> object1) {
        for (var i = 0; i < 4; i += 1) {
            m_compatBackground[i] = Bgr555ToRgba(value: background[i]);
            m_compatObject0[i] = Bgr555ToRgba(value: object0[i]);
            m_compatObject1[i] = Bgr555ToRgba(value: object1[i]);
        }

        m_isColor = false; // DMG-style rendering, colorized through the assigned palettes
        m_dmgCompatColorization = true;
    }

    // The background color for a resolved DMG shade: the assigned compatibility color, or the grayscale ramp otherwise.
    private uint BackgroundColor(int shade) =>
        (m_dmgCompatColorization ? m_compatBackground[shade] : Shades[shade]);

    // The object color for a resolved DMG shade, selecting object palette 0 or 1.
    private uint ObjectColor(bool palette1, int shade) =>
        (m_dmgCompatColorization
            ? (palette1 ? m_compatObject1[shade] : m_compatObject0[shade])
            : Shades[shade]);

    // A 15-bit BGR555 palette entry converted to opaque R8G8B8A8 (0xAABBGGRR), either through the CGB color-correction
    // curve or a direct bit-expansion, per ColorCorrectionEnabled.
    private uint ColorFromPaletteRam(byte[] paletteRam, int palette, int colorIndex) {
        var offset = ((palette * 4) + colorIndex) * 2;

        return Bgr555ToRgba(value: (paletteRam[offset] | (paletteRam[offset + 1] << 8)));
    }
    private uint Bgr555ToRgba(int value) {
        var r5 = (value & 0x1F);
        var g5 = ((value >> 5) & 0x1F);
        var b5 = ((value >> 10) & 0x1F);

        uint r;
        uint g;
        uint b;

        switch (m_colorCorrection) {
            case CgbColorCorrection.Disabled:
                // Each 5-bit channel widened to 8 bits via (c << 3) | (c >> 2).
                r = (uint)((r5 << 3) | (r5 >> 2));
                g = (uint)((g5 << 3) | (g5 >> 2));
                b = (uint)((b5 << 3) | (b5 >> 2));

                break;
            case CgbColorCorrection.CorrectCurves:
                r = s_responseCurve[r5];
                g = s_responseCurve[g5];
                b = s_responseCurve[b5];

                break;
            default:
                // ModernBalanced: the response curve, with green blended toward blue (precomputed in s_balancedGreen).
                r = s_responseCurve[r5];
                g = s_balancedGreen[(g5 * 32) + b5];
                b = s_responseCurve[b5];

                break;
        }

        return (0xFF000000u | (b << 16) | (g << 8) | r);
    }

    // Precomputes the ModernBalanced green: the curved green and blue mixed in gamma-1.6 space, 3 parts green to
    // one part blue, indexed by the raw 5-bit green and blue.
    private static byte[] BuildBalancedGreen() {
        const double Gamma = 1.6;

        var table = new byte[32 * 32];

        for (var g5 = 0; g5 < 32; g5 += 1) {
            for (var b5 = 0; b5 < 32; b5 += 1) {
                var curvedGreen = (s_responseCurve[g5] / 255.0);
                var curvedBlue = (s_responseCurve[b5] / 255.0);
                var mixed = Math.Pow(x: (((Math.Pow(x: curvedGreen, y: Gamma) * 3) + Math.Pow(x: curvedBlue, y: Gamma)) / 4), y: (1 / Gamma));

                table[(g5 * 32) + b5] = (byte)Math.Round(a: (mixed * 255));
            }
        }

        return table;
    }

    private void RenderBackgroundAndWindowColor(int line) {
        var rowBase = (line * ScreenWidth);
        var backgroundMap = (((m_lcdControl & 0x08) != 0) ? TileMap1 : TileMap0);
        var windowMap = (((m_lcdControl & 0x40) != 0) ? TileMap1 : TileMap0);
        var unsignedTiles = ((m_lcdControl & 0x10) != 0);
        var windowActive = (((m_lcdControl & 0x20) != 0) && (line >= m_windowY));
        var windowStartX = (m_windowX - 7);
        var windowShown = false;

        for (var x = 0; x < ScreenWidth; x += 1) {
            int mapBase;
            int pixelX;
            int pixelY;

            if (windowActive && (x >= windowStartX)) {
                mapBase = windowMap;
                pixelX = (x - windowStartX);
                pixelY = m_windowLineCounter;
                windowShown = true;
            }
            else {
                mapBase = backgroundMap;
                pixelX = ((m_scrollX + x) & 0xFF);
                pixelY = ((m_scrollY + line) & 0xFF);
            }

            var mapAddress = (mapBase + ((pixelY >> 3) * 32) + (pixelX >> 3));
            var tileNumber = ReadVideoRamBank(address: mapAddress, bank: 0);
            var attributes = ReadVideoRamBank(address: mapAddress, bank: 1);

            var palette = (attributes & 0x07);
            var tileBank = ((attributes >> 3) & 0x01);
            var flipX = ((attributes & 0x20) != 0);
            var flipY = ((attributes & 0x40) != 0);

            var tileDataAddress = (unsignedTiles
                ? (0x8000 + (tileNumber * 16))
                : (0x9000 + ((sbyte)tileNumber * 16)));
            var rowInTile = (flipY ? (7 - (pixelY & 7)) : (pixelY & 7));
            var rowAddress = (tileDataAddress + (rowInTile * 2));
            var low = ReadVideoRamBank(address: rowAddress, bank: tileBank);
            var high = ReadVideoRamBank(address: (rowAddress + 1), bank: tileBank);
            var bit = (flipX ? (pixelX & 7) : (7 - (pixelX & 7)));
            var colorIndex = ((((high >> bit) & 1) << 1) | ((low >> bit) & 1));

            m_lineColorIndex[x] = (byte)colorIndex;
            m_lineBackgroundPriority[x] = ((attributes & 0x80) != 0);
            m_framebuffer[rowBase + x] = ColorFromPaletteRam(paletteRam: m_backgroundPaletteRam, palette: palette, colorIndex: colorIndex);
        }

        if (windowShown) {
            m_windowLineCounter += 1;
        }
    }

    private void RenderSpritesColor(int line) {
        if ((m_lcdControl & 0x02) == 0) {
            return;
        }

        var rowBase = (line * ScreenWidth);
        var spriteHeight = (((m_lcdControl & 0x04) != 0) ? 16 : 8);

        Span<int> selected = stackalloc int[10];
        var count = 0;

        for (var index = 0; (index < 40) && (count < 10); index += 1) {
            var objectY = (m_objectAttributeMemory[index * 4] - 16);

            if ((line >= objectY) && (line < (objectY + spriteHeight))) {
                selected[count] = index;
                count += 1;
            }
        }

        SortSpritesColor(selected: selected[..count]);

        // LCDC bit 0 is the BG/OBJ master priority on the CGB: when clear, objects always draw over the background.
        var backgroundHasPriority = ((m_lcdControl & 0x01) != 0);

        foreach (var index in selected[..count]) {
            DrawSpriteColor(
                line: line,
                oamIndex: index,
                rowBase: rowBase,
                spriteHeight: spriteHeight,
                backgroundHasPriority: backgroundHasPriority
            );
        }
    }

    private void SortSpritesColor(Span<int> selected) {
        // Default CGB priority is by OAM index (lower index wins); OPRI bit 0 set selects DMG-style X-coordinate
        // priority. Draw lowest priority first so the highest-priority object overwrites it.
        var byCoordinate = ((m_objectPriorityMode & 0x01) != 0);

        for (var i = 1; i < selected.Length; i += 1) {
            var current = selected[i];
            var j = (i - 1);

            while ((j >= 0) && IsLowerSpritePriority(reference: current, candidate: selected[j], byCoordinate: byCoordinate)) {
                selected[j + 1] = selected[j];
                j -= 1;
            }

            selected[j + 1] = current;
        }
    }

    private bool IsLowerSpritePriority(int reference, int candidate, bool byCoordinate) {
        // "Lower priority" sorts earlier (drawn first, overwritten later). By coordinate: larger X is lower; ties by
        // larger OAM index. By OAM index: larger index is lower.
        if (byCoordinate) {
            var referenceX = m_objectAttributeMemory[(reference * 4) + 1];
            var candidateX = m_objectAttributeMemory[(candidate * 4) + 1];

            return ((candidateX > referenceX) || ((candidateX == referenceX) && (candidate > reference)));
        }

        return (candidate > reference);
    }

    private void DrawSpriteColor(int line, int oamIndex, int rowBase, int spriteHeight, bool backgroundHasPriority) {
        var oamAddress = (oamIndex * 4);
        var objectY = (m_objectAttributeMemory[oamAddress] - 16);
        var objectX = (m_objectAttributeMemory[oamAddress + 1] - 8);
        var tile = (int)m_objectAttributeMemory[oamAddress + 2];
        var attributes = m_objectAttributeMemory[oamAddress + 3];

        var palette = (attributes & 0x07);
        var tileBank = ((attributes >> 3) & 0x01);
        var flipX = ((attributes & 0x20) != 0);
        var flipY = ((attributes & 0x40) != 0);
        var objectBehindBackground = ((attributes & 0x80) != 0);

        var rowInSprite = (line - objectY);

        if (flipY) {
            rowInSprite = (spriteHeight - 1 - rowInSprite);
        }

        if (spriteHeight == 16) {
            tile &= 0xFE;
        }

        var rowAddress = (0x8000 + (tile * 16) + (rowInSprite * 2));
        var low = ReadVideoRamBank(address: rowAddress, bank: tileBank);
        var high = ReadVideoRamBank(address: (rowAddress + 1), bank: tileBank);

        for (var pixel = 0; pixel < 8; pixel += 1) {
            var screenX = (objectX + pixel);

            if ((screenX < 0) || (screenX >= ScreenWidth)) {
                continue;
            }

            var bit = (flipX ? pixel : (7 - pixel));
            var colorIndex = ((((high >> bit) & 1) << 1) | ((low >> bit) & 1));

            if (colorIndex == 0) {
                continue;
            }

            // With BG master priority on, the background covers the object where its own pixel is non-zero and either
            // the tile attribute or the object marks the background as in front. Master priority off forces the object on top.
            if (backgroundHasPriority
                && (m_lineColorIndex[screenX] != 0)
                && (objectBehindBackground || m_lineBackgroundPriority[screenX])) {
                continue;
            }

            m_framebuffer[rowBase + screenX] = ColorFromPaletteRam(paletteRam: m_objectPaletteRam, palette: palette, colorIndex: colorIndex);
        }
    }
}
