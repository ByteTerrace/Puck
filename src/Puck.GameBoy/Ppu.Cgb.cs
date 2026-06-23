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

    private readonly bool m_isColor;
    private readonly byte[] m_backgroundPaletteRam = new byte[PaletteRamSize];
    private readonly byte[] m_objectPaletteRam = new byte[PaletteRamSize];
    // Per-pixel BG-over-OBJ priority for the current line (the tile attribute's bit 7), used to resolve object
    // priority against the background.
    private readonly bool[] m_lineBackgroundPriority = new bool[ScreenWidth];

    private byte m_backgroundPaletteIndex; // BGPI: bit 7 auto-increment, bits 0-5 index
    private byte m_objectPaletteIndex; // OBPI
    private byte m_objectPriorityMode; // OPRI (bit 0: 1 = X-coordinate priority, 0 = OAM-order priority)

    /// <summary>Gets whether this is a Game Boy Color PPU (color render path, palette RAM, tile attributes).</summary>
    public bool IsColor =>
        m_isColor;

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

    // A 15-bit BGR555 palette entry expanded to opaque R8G8B8A8 (0xAABBGGRR). The channel expansion mirrors the
    // common (c << 3) | (c >> 2) widening; no further color correction is applied.
    private static uint ColorFromPaletteRam(byte[] paletteRam, int palette, int colorIndex) {
        var offset = ((palette * 4) + colorIndex) * 2;
        var value = (paletteRam[offset] | (paletteRam[offset + 1] << 8));
        var r = (uint)Expand5To8(channel: (value & 0x1F));
        var g = (uint)Expand5To8(channel: ((value >> 5) & 0x1F));
        var b = (uint)Expand5To8(channel: ((value >> 10) & 0x1F));

        return (0xFF000000u | (b << 16) | (g << 8) | r);
    }
    private static int Expand5To8(int channel) =>
        ((channel << 3) | (channel >> 2));

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
