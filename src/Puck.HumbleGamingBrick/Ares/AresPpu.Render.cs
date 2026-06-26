namespace Puck.HumbleGamingBrick.Ares;

/// <summary>The DMG per-pixel renderer, ported from ares (<c>gb/ppu/dmg.cpp</c>).</summary>
public sealed partial class AresPpu {
    // ares scanlineDMG: find the first ten objects covering this line in OAM order, then stably sort by X.
    private void ScanlineDmg() {
        m_px = 0;
        m_sprites = 0;

        var height = (m_obSize ? 16 : 8);

        for (var n = 0; n < (40 * 4); n += 4) {
            var y = (m_oam[n + 0] - 16);
            var x = (m_oam[n + 1] - 8);

            if (m_ly < y) {
                continue;
            }

            if (m_ly >= (y + height)) {
                continue;
            }

            m_spriteY[m_sprites] = y;
            m_spriteX[m_sprites] = x;
            m_spriteTile[m_sprites] = m_oam[n + 2];
            m_spriteAttributes[m_sprites] = m_oam[n + 3];

            if (++m_sprites == 10) {
                break;
            }
        }

        // Stable insertion sort by ascending X (ties keep OAM order — the DMG priority rule).
        for (var i = 1; i < m_sprites; i += 1) {
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

    // ares runDMG: composite background/window and object pixels for m_px under the DMG priority rule.
    private void RunDmg() {
        m_bgColor = 0;
        m_bgPalette = 0;
        m_obColor = 0;
        m_obPalette = 0;
        m_obPriority = false;

        var color = 0;

        if (m_bgEnable) {
            RunBackgroundDmg();
        }

        if (m_latchWindowDisplayEnable) {
            RunWindowDmg();
        }

        if (m_obEnable) {
            RunObjectsDmg();
        }

        if (m_obPalette == 0) {
            color = m_bgColor;
        }
        else if (m_bgPalette == 0) {
            color = m_obColor;
        }
        else if (m_obPriority) {
            color = m_obColor;
        }
        else {
            color = m_bgColor;
        }

        // The LCD is blank during the first frame after the display is enabled (ares: write only when not latched).
        if (!m_latchDisplayEnable) {
            m_framebuffer[(m_ly * ScreenWidth) + m_px] = Shades[color];
        }

        m_px += 1;
    }

    // ares runBackgroundDMG: the background colour for m_px, re-reading the tile at each 8-pixel boundary.
    private void RunBackgroundDmg() {
        var scrollY = (byte)(m_ly + m_scy);
        var scrollX = (byte)(m_px + m_scx);
        var tileX = (scrollX & 7);

        if ((tileX == 0) || (m_px == 0)) {
            m_backgroundTiledata = ReadTileDmg(select: m_bgTilemapSelect, x: scrollX, y: scrollY);
        }

        var index = TileIndex(tiledata: m_backgroundTiledata, tileX: tileX);

        m_bgColor = m_bgp[index];
        m_bgPalette = index;
    }

    // ares runWindowDMG: overlays the window onto the background layer when it covers m_px.
    private void RunWindowDmg() {
        if (m_ly < m_wy) {
            return;
        }

        if ((m_px + 7) < m_wx) {
            return;
        }

        if ((m_px + 7) == m_wx) {
            m_latchWy += 1;
        }

        if (!m_bgEnable) {
            return;
        }

        var scrollY = (byte)(m_latchWy - 1);
        var scrollX = (byte)(m_px + 7 - m_latchWx);
        var tileX = (scrollX & 7);

        if ((tileX == 0) || (m_px == 0)) {
            m_windowTiledata = ReadTileDmg(select: m_windowTilemapSelect, x: scrollX, y: scrollY);
        }

        var index = TileIndex(tiledata: m_windowTiledata, tileX: tileX);

        m_bgColor = m_bgp[index];
        m_bgPalette = index;
    }

    // ares runObjectsDMG: render back-to-front so the first object (after the X-sort) wins.
    private void RunObjectsDmg() {
        for (var n = (m_sprites - 1); n >= 0; n -= 1) {
            var tileX = (m_px - m_spriteX[n]);

            if ((tileX < 0) || (tileX > 7)) {
                continue;
            }

            if ((tileX == 0) || (m_px == 0)) {
                m_spriteTiledata[n] = ReadObjectDmg(spriteY: m_spriteY[n], tile: m_spriteTile[n], attributes: m_spriteAttributes[n]);
            }

            var index = TileIndex(tiledata: m_spriteTiledata[n], tileX: tileX);

            if (index == 0) {
                continue;
            }

            var attributes = m_spriteAttributes[n];
            var palette = (((attributes & 0x10) != 0 ? 4 : 0) | index);

            m_obColor = m_obp[palette];
            m_obPalette = index;
            m_obPriority = ((attributes & 0x80) == 0);
        }
    }

    // ares readTileDMG: read a background/window tile's 2bpp row (low plane low byte, high plane high byte).
    private ushort ReadTileDmg(bool select, int x, int y) {
        var tilemapAddress = (0x1800 + (select ? 0x400 : 0));

        tilemapAddress += ((((y >> 3) << 5) + (x >> 3)) & 0x03FF);

        var tile = m_vram[tilemapAddress];
        int tiledataAddress;

        if (!m_bgTiledataSelect) {
            tiledataAddress = (0x1000 + ((sbyte)tile << 4));
        }
        else {
            tiledataAddress = (tile << 4);
        }

        tiledataAddress += ((y & 7) << 1);

        return (ushort)(m_vram[tiledataAddress] | (m_vram[tiledataAddress + 1] << 8));
    }

    // ares readObjectDMG: read the current line's row of an object's tile (8x16 pairing + vertical/horizontal flip).
    private ushort ReadObjectDmg(int spriteY, byte tile, byte attributes) {
        var height = (m_obSize ? 16 : 8);

        if (m_obSize) {
            tile &= 0xFE;
        }

        var row = (m_ly - spriteY);

        if ((attributes & 0x40) != 0) {
            row ^= (height - 1);
        }

        var tiledataAddress = ((tile << 4) + (row << 1));
        var tiledata = (ushort)(m_vram[tiledataAddress] | (m_vram[tiledataAddress + 1] << 8));

        return (((attributes & 0x20) != 0) ? Hflip(tiledata: tiledata) : tiledata);
    }

    // Extracts the 2bpp colour index for column tileX (0-7) from a 16-bit tile row.
    private static int TileIndex(ushort tiledata, int tileX) {
        var bit = (7 - tileX);

        return ((((tiledata >> (8 + bit)) & 1) << 1) | ((tiledata >> bit) & 1));
    }

    // ares hflip: mirror a 2bpp tile row within each plane.
    private static ushort Hflip(ushort tiledata) {
        return (ushort)(((tiledata >> 7) & 0x0101)
            | ((tiledata >> 5) & 0x0202)
            | ((tiledata >> 3) & 0x0404)
            | ((tiledata >> 1) & 0x0808)
            | ((tiledata << 1) & 0x1010)
            | ((tiledata << 3) & 0x2020)
            | ((tiledata << 5) & 0x4040)
            | ((tiledata << 7) & 0x8080));
    }
}
