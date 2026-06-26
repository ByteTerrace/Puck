namespace Puck.HumbleGamingBrick;

/// <summary>
/// The DMG per-pixel renderer, ported from ares' <c>gb/ppu/dmg.cpp</c>. During mode 3 it produces one pixel per dot,
/// reading SCX/SCY/LCDC/BGP/WX/WY and the tile and sprite data fresh for each pixel — so a mid-mode-3 register write
/// takes effect on exactly the pixel the hardware shows. With the bus landing register writes at their sub-machine-
/// cycle access point, this reproduces the mealybug pixel-timing behaviour without any FIFO warmup or latch heuristic.
/// Sprites are searched once per line and composited per pixel. The CGB path still uses the whole-line renderer.
/// </summary>
public sealed partial class Ppu {
    // Called at the mode 2->3 transition: latch the window state for the line, advance the window's frame-top reset
    // and left-edge counter (ares), search OAM for this line's objects, and arm the per-pixel renderer.
    private void StartLineDmg() {
        m_px = 0;
        m_renderActive = true;
        m_renderWarmup = RenderWarmupDots;
        m_bgpRender = m_backgroundPalette;
        m_bgpRenderPendingPops = 0;

        if (m_line == 0) {
            m_latchWy = 0;
        }

        m_latchWindowEnable = ((m_lcdControl & 0x20) != 0);
        m_latchWx = m_windowX;

        if ((m_line >= m_windowY) && (m_windowX < 7)) {
            m_latchWy += 1;
        }

        ScanlineDmg();
    }

    // Renders the next pixel (m_px) and advances; finishes the line once all 160 are output. A warmup phases the
    // pixel pipeline so register writes land on the hardware-faithful pixel given Puck's access timing.
    private void StepRenderDmg() {
        if (m_renderWarmup > 0) {
            m_renderWarmup -= 1;

            return;
        }

        RunDmg();

        if (m_px >= ScreenWidth) {
            m_renderActive = false;
        }
    }

    // ares scanlineDMG: the first ten objects covering this line, in OAM order, then stably sorted by X.
    private void ScanlineDmg() {
        m_spriteCount = 0;

        var height = (((m_lcdControl & 0x04) != 0) ? 16 : 8);

        for (var n = 0; (n < (40 * 4)) && (m_spriteCount < 10); n += 4) {
            var spriteY = (m_objectAttributeMemory[n] - 16);

            if (m_line < spriteY) {
                continue;
            }

            if (m_line >= (spriteY + height)) {
                continue;
            }

            m_spriteY[m_spriteCount] = spriteY;
            m_spriteX[m_spriteCount] = (m_objectAttributeMemory[n + 1] - 8);
            m_spriteTile[m_spriteCount] = m_objectAttributeMemory[n + 2];
            m_spriteAttributes[m_spriteCount] = m_objectAttributeMemory[n + 3];
            m_spriteCount += 1;
        }

        // Stable insertion sort by ascending X (ties keep OAM order, the DMG priority rule).
        for (var i = 1; i < m_spriteCount; i += 1) {
            var x = m_spriteX[i];
            var y = m_spriteY[i];
            var tile = m_spriteTile[i];
            var attributes = m_spriteAttributes[i];
            var j = (i - 1);

            while ((j >= 0) && (m_spriteX[j] > x)) {
                m_spriteX[j + 1] = m_spriteX[j];
                m_spriteY[j + 1] = m_spriteY[j];
                m_spriteTile[j + 1] = m_spriteTile[j];
                m_spriteAttributes[j + 1] = m_spriteAttributes[j];
                j -= 1;
            }

            m_spriteX[j + 1] = x;
            m_spriteY[j + 1] = y;
            m_spriteTile[j + 1] = tile;
            m_spriteAttributes[j + 1] = attributes;
        }
    }

    // ares runDMG: composite the background/window and object pixels for m_px under the DMG priority rule.
    private void RunDmg() {
        var backgroundIndex = 0;

        if ((m_lcdControl & 0x01) != 0) {
            backgroundIndex = RunBackgroundDmg();
        }

        if (m_latchWindowEnable) {
            var windowIndex = RunWindowDmg();

            if (windowIndex >= 0) {
                backgroundIndex = windowIndex;
            }
        }

        var backgroundShade = ((m_bgpRender >> (backgroundIndex * 2)) & 3);

        var objectIndex = 0;
        var objectShade = 0;
        var objectPalette1 = false;
        var objectPriority = false;

        if ((m_lcdControl & 0x02) != 0) {
            RunObjectsDmg(backgroundIndex: backgroundIndex, objectIndex: out objectIndex, objectShade: out objectShade, objectPalette1: out objectPalette1, objectPriority: out objectPriority);
        }

        var useObject = (objectIndex != 0) && ((backgroundIndex == 0) || objectPriority);
        var color = (useObject ? ObjectColor(palette1: objectPalette1, shade: objectShade) : BackgroundColor(shade: backgroundShade));

        m_lineColorIndex[m_px] = (byte)backgroundIndex;
        m_framebuffer[(m_line * ScreenWidth) + m_px] = color;
        m_px += 1;

        // A deferred push-phase BGP write reaches the render palette one pixel after it was issued.
        if (m_bgpRenderPendingPops > 0) {
            m_bgpRenderPendingPops -= 1;

            if (m_bgpRenderPendingPops == 0) {
                m_bgpRender = m_bgpRenderPending;
            }
        }
    }

    // ares runBackgroundDMG: the background colour index (0-3) for m_px, re-reading the tile at each tile boundary.
    private int RunBackgroundDmg() {
        var scrollY = (byte)(m_line + m_scrollY);
        var scrollX = (byte)(m_px + m_scrollX);
        var tileX = (scrollX & 7);

        if ((tileX == 0) || (m_px == 0)) {
            m_backgroundTiledata = ReadTileRowDmg(select: ((m_lcdControl & 0x08) != 0), x: scrollX, y: scrollY);
        }

        return TileIndex(tiledata: m_backgroundTiledata, tileX: tileX);
    }

    // ares runWindowDMG: returns the window colour index for m_px, or -1 when the window does not cover it.
    private int RunWindowDmg() {
        if (m_line < m_windowY) {
            return -1;
        }

        if ((m_px + 7) < m_latchWx) {
            return -1;
        }

        if ((m_px + 7) == m_latchWx) {
            m_latchWy += 1;
        }

        if ((m_lcdControl & 0x01) == 0) {
            return -1;
        }

        var scrollY = (byte)(m_latchWy - 1);
        var scrollX = (byte)(m_px + 7 - m_latchWx);
        var tileX = (scrollX & 7);

        if ((tileX == 0) || (m_px == 0)) {
            m_windowTiledata = ReadTileRowDmg(select: ((m_lcdControl & 0x40) != 0), x: scrollX, y: scrollY);
        }

        return TileIndex(tiledata: m_windowTiledata, tileX: tileX);
    }

    // ares runObjectsDMG: render objects back-to-front so the first (after the X-sort) wins; an opaque sprite pixel
    // sets the object colour, palette, and BG-priority flag.
    private void RunObjectsDmg(int backgroundIndex, out int objectIndex, out int objectShade, out bool objectPalette1, out bool objectPriority) {
        _ = backgroundIndex;

        objectIndex = 0;
        objectShade = 0;
        objectPalette1 = false;
        objectPriority = false;

        for (var n = (m_spriteCount - 1); n >= 0; n -= 1) {
            var tileX = (m_px - m_spriteX[n]);

            if ((tileX < 0) || (tileX > 7)) {
                continue;
            }

            var tiledata = ReadObjectRowDmg(spriteIndex: n);
            var index = TileIndex(tiledata: tiledata, tileX: tileX);

            if (index == 0) {
                continue;
            }

            var palette1 = ((m_spriteAttributes[n] & 0x10) != 0);

            objectIndex = index;
            objectPalette1 = palette1;
            objectShade = ((((palette1 ? m_objectPalette1 : m_objectPalette0)) >> (index * 2)) & 3);
            objectPriority = ((m_spriteAttributes[n] & 0x80) == 0);
        }
    }

    // Reads a background/window tile's 2bpp row into a 16-bit value (low plane in the low byte, high plane in the
    // high byte), addressed by the LCDC tile-data select.
    private ushort ReadTileRowDmg(bool select, int x, int y) {
        var mapBase = (select ? TileMap1 : TileMap0);
        var tile = ReadVideoRam(address: (ushort)(mapBase + (((y >> 3) & 0x1F) * 32) + ((x >> 3) & 0x1F)));

        var tileDataAddress = (((m_lcdControl & 0x10) != 0)
            ? (0x8000 + (tile * 16))
            : (0x9000 + ((sbyte)tile * 16)));
        var rowAddress = (ushort)(tileDataAddress + ((y & 7) * 2));
        var low = ReadVideoRam(address: rowAddress);
        var high = ReadVideoRam(address: (ushort)(rowAddress + 1));

        return (ushort)(low | (high << 8));
    }

    // Reads the current line's row of an object's tile (handling the 8x16 tile pairing and vertical/horizontal flip).
    private ushort ReadObjectRowDmg(int spriteIndex) {
        var height = (((m_lcdControl & 0x04) != 0) ? 16 : 8);
        var tile = m_spriteTile[spriteIndex];
        var attributes = m_spriteAttributes[spriteIndex];

        if (height == 16) {
            tile &= 0xFE;
        }

        var row = (m_line - m_spriteY[spriteIndex]);

        if ((attributes & 0x40) != 0) {
            row = (height - 1 - row);
        }

        var rowAddress = (ushort)(0x8000 + (tile * 16) + (row * 2));
        var low = ReadVideoRam(address: rowAddress);
        var high = ReadVideoRam(address: (ushort)(rowAddress + 1));
        var tiledata = (ushort)(low | (high << 8));

        return (((attributes & 0x20) != 0) ? HorizontalFlip(tiledata: tiledata) : tiledata);
    }

    // Extracts the 2bpp colour index for column tileX (0-7) from a 16-bit tile row (low plane low byte, high plane high).
    private static int TileIndex(ushort tiledata, int tileX) {
        var bit = (7 - tileX);

        return ((((tiledata >> (8 + bit)) & 1) << 1) | ((tiledata >> bit) & 1));
    }

    // Mirrors a 2bpp tile row horizontally within each plane (ares hflip).
    private static ushort HorizontalFlip(ushort tiledata) {
        var result = 0;

        for (var bit = 0; bit < 8; bit += 1) {
            if (((tiledata >> bit) & 1) != 0) {
                result |= (1 << (7 - bit));
            }

            if (((tiledata >> (8 + bit)) & 1) != 0) {
                result |= (1 << (8 + (7 - bit)));
            }
        }

        return (ushort)result;
    }
}
