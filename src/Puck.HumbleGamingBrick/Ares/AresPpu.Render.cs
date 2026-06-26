// The mode-3 path is a cycle-exact pixel-FIFO renderer (BG/window/OBJ FIFOs + a 5-state fetcher) replacing ares'
// per-pixel composite. The per-dot routine is deliberately one long method; the analyzers' complexity heuristics do
// not apply to a fixed hardware state machine.
#pragma warning disable CA1502 // Avoid excessive complexity
#pragma warning disable CA1505 // Avoid unmaintainable code

namespace Puck.HumbleGamingBrick.Ares;

/// <summary>The DMG mode-3 pixel-FIFO renderer (BG/window/OBJ fetcher + FIFOs), calibrated to the mealybug suite.</summary>
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

    // Seeds the fetcher/FIFO/discard/sprite-line state at the mode-2 -> mode-3 boundary.
    private void StartLineMode3() {
        m_bgFifoCount = 0;
        Array.Clear(m_objFifoIndex);
        Array.Clear(m_objFifoPalette1);
        Array.Clear(m_objFifoPriority);

        m_fetcherState = FetchState.GetTile;
        m_fetcherStepDots = 2;
        m_fetcherX = 0;
        m_fetcherIsWindow = false;
        m_firstFetchDiscarded = false;

        m_scxFineDiscard = (m_scx & 7);
        m_mode3Warmup = Mode3WarmupExtra;

        // Seed the BGP/bg-enable render snapshots from the live state; no deferral pending at line start.
        Array.Copy(m_bgp, m_bgpRender, 4);
        m_bgpPrevPacked = (m_bgp[0] | (m_bgp[1] << 2) | (m_bgp[2] << 4) | (m_bgp[3] << 6));
        m_bgpDeferPops = 0;

        // Seed the OBP render snapshot identically (same cycle-2 FIFO-phase deferral as BGP).
        Array.Copy(m_obp, m_obpRender, 8);
        m_obpPrevPacked = PackObp();
        m_obpDeferPops = 0;

        m_px = 0;
        m_windowTriggeredThisLine = false;
        m_windowDrawing = false;
        m_consideredTiles = 0;

        // Window arm = the mode-2-start latch; the mode-3-entry reference is the live LCDC.5 (which already includes any
        // late-mode-2 write — that write is intentionally excluded from this line by being baked into the reference).
        m_winArmed = m_latchWindowDisplayEnable;
        m_winEnableRef = m_windowDisplayEnable;

        Array.Clear(m_spriteFetched);
        m_spriteFetchActive = false;
        m_spriteFetchDotsRemaining = 0;

        // WX<7 pre-bump: the window starts already mid-tile from px 0 (the m_latchWy++ already happened in Run).
        // The window's leftmost on-screen column is (m_latchWx - 7), so its first (7 - m_latchWx) tile pixels are
        // off-screen to the left and are discarded from the front of the window FIFO (reusing the fine-discard).
        var winEnabled = m_winArmed;

        if (winEnabled && (m_latchWx < 7) && (m_ly >= m_wy)) {
            TriggerWindowRestart();
            m_windowDrawing = true;
            m_windowTriggeredThisLine = true;
            m_scxFineDiscard = ((7 - m_latchWx) & 7);
        }
    }

    // The single authoritative per-dot mode-3 routine. Intra-dot order (CPU write already committed by AdvanceTo's
    // Step): (1) sprite-stall progression, (2) sprite-stall start, (3) POP+mix+output, (4) advance fetcher one dot.
    // POP runs BEFORE the fetcher advance — this is the discriminator that yields 13px (not 12px) palette bands.
    private void StepMode3Dot() {
        // (0a) Detect a mid-mode-3 BGP write (the live m_bgp changed since the last dot) and route it into the render
        // snapshot. During warmup or at the tile-index/push fetch phase the new value is applied immediately; a write
        // landing during the data-fetch phase (GetLow/GetHigh of the group feeding the shifter) is deferred one pop —
        // the FIFO-phase effect that yields 13px-wide palette bands (mealybug m3_bgp_change).
        var packed = (m_bgp[0] | (m_bgp[1] << 2) | (m_bgp[2] << 4) | (m_bgp[3] << 6));

        var dataFetchPhase = (m_mode3Warmup == 0)
            && ((m_fetcherState == FetchState.GetLow) || (m_fetcherState == FetchState.GetHigh));

        if (packed != m_bgpPrevPacked) {
            m_bgpPrevPacked = packed;

            if (dataFetchPhase) {
                m_bgpDeferPops = 1; // applied after the next pop.
            }
            else {
                Array.Copy(m_bgp, m_bgpRender, 4);
                m_bgpDeferPops = 0;
            }
        }

        // Same detection for OBP0/OBP1 (cycle-2 writes; same FIFO-phase deferral as BGP).
        var obpPacked = PackObp();

        if (obpPacked != m_obpPrevPacked) {
            m_obpPrevPacked = obpPacked;

            if (dataFetchPhase) {
                m_obpDeferPops = 1; // applied after the next pop.
            }
            else {
                Array.Copy(m_obp, m_obpRender, 8);
                m_obpDeferPops = 0;
            }
        }

        // (0b) Calibrated mode-3 warmup: the FIFO/fetcher idles for the first few dots so the first BG pixel pops at
        // the hardware-correct mode-3 dot for this core's CPU<->PPU write phase.
        if (m_mode3Warmup > 0) {
            m_mode3Warmup -= 1;

            return;
        }

        // (0c) Mid-mode-3 LCDC.5 change: a live change measured against the mode-3-entry reference re-arms (or
        // disarms) the window for the rest of the line. The late-mode-2 write is baked into the reference, so it is
        // ignored; only changes occurring after mode-3 entry move the arm.
        if (m_windowDisplayEnable != m_winEnableRef) {
            m_winArmed = m_windowDisplayEnable;
            m_winEnableRef = m_windowDisplayEnable;
        }

        // (1) An in-progress sprite fetch stalls the shifter; just burn a dot.
        if (m_spriteFetchActive) {
            m_spriteFetchDotsRemaining -= 1;

            if (m_spriteFetchDotsRemaining == 0) {
                m_spriteFetchActive = false;
            }

            return;
        }

        // (2) Sprite-stall start: only if OBJ enabled LIVE and a not-yet-fetched sprite's leftmost on-screen column
        // == the current shifter column (objX<0 sprites match at col 0). Requires the BG FIFO be primed and the
        // fine-discard already consumed (sprites stall against real output columns, not discarded ones).
        if (m_obEnable && (m_bgFifoCount > 0) && (m_scxFineDiscard == 0)) {
            for (var i = 0; i < m_sprites; i += 1) {
                if (m_spriteFetched[i]) {
                    continue;
                }

                var leftCol = (m_spriteX[i] < 0) ? 0 : m_spriteX[i];

                if (leftCol != m_px) {
                    continue;
                }

                var pen = 6;
                var rawZero = ((m_spriteX[i] + 8) == 0);
                var tilePix = ((m_spriteX[i] - 8) + (m_scx & 7));
                var tileCol = rawZero ? 31 : ((tilePix >> 3) & 0x1F);

                if ((m_consideredTiles & (1 << tileCol)) == 0) {
                    m_consideredTiles |= (1 << tileCol);
                    pen += rawZero ? 5 : Math.Max(0, 5 - (tilePix & 7));
                }

                FetchSpriteRow(i);
                m_spriteFetched[i] = true;
                m_spriteFetchActive = true;
                m_spriteFetchDotsRemaining = pen;

                return;
            }
        }

        // (3) POP + mix + output (reads palette/enable LIVE as of dot start).
        if (m_bgFifoCount > 0) {
            var bgIndex = ShiftBgFifo();

            // Window trigger comparator uses the running arm (mode-2-start latch + in-mode-3 live deltas).
            var winEnabled = m_winArmed;

            if (winEnabled && (m_ly >= m_wy) && ((m_px + 7) == m_wx) && !m_windowDrawing) {
                if (!m_windowTriggeredThisLine) {
                    m_latchWy += 1;
                }

                m_windowTriggeredThisLine = true;
                TriggerWindowRestart();
                m_windowDrawing = true;

                return;
            }

            // Mid-line window disable (LCDC.5 cleared while drawing the window).
            if (m_windowDrawing && !winEnabled) {
                RestartBgFetch(m_px);
                m_windowDrawing = false;

                return;
            }

            if (m_scxFineDiscard > 0) {
                m_scxFineDiscard -= 1;

                StepFetcher();

                return;
            }

            // BG shade (BGP snapshot + LCDC.0 bg-en LIVE).
            var bi = m_bgEnable ? bgIndex : 0;
            var color = (int)m_bgpRender[bi];

            // OBJ mix (OBP + LCDC.1 obj-en LIVE).
            var oi = ShiftObjFifo(out var pal1, out var bgPrio);

            if (!m_obEnable) {
                oi = 0;
            }

            if (oi != 0) {
                if ((bi == 0) || !bgPrio) {
                    color = m_obpRender[(pal1 ? 4 : 0) + oi];
                }
            }

            if (!m_latchDisplayEnable) {
                m_framebuffer[(m_ly * ScreenWidth) + m_px] = Shades[color];
            }

            PxTrace(px: m_px, shade: color, bgIndex: bi, objIndex: oi);

            m_px += 1;

            // Apply a deferred BGP write one pop after it landed in the data-fetch phase.
            if (m_bgpDeferPops > 0) {
                m_bgpDeferPops -= 1;

                if (m_bgpDeferPops == 0) {
                    Array.Copy(m_bgp, m_bgpRender, 4);
                }
            }

            // Apply a deferred OBP write one pop after it landed in the data-fetch phase.
            if (m_obpDeferPops > 0) {
                m_obpDeferPops -= 1;

                if (m_obpDeferPops == 0) {
                    Array.Copy(m_obp, m_obpRender, 8);
                }
            }

        }

        // (4) Advance the fetcher one dot (reads SCX-high/SCY/LCDC.3/4/6 LIVE at its substeps).
        StepFetcher();
    }

    // Advances the BG/window fetcher one dot.
    private void StepFetcher() {
        switch (m_fetcherState) {
            case FetchState.GetTile:
                m_fetcherStepDots -= 1;

                if (m_fetcherStepDots == 0) {
                    int mapX;
                    int mapY;
                    bool mapSelect;

                    if (m_fetcherIsWindow) {
                        mapX = m_fetcherX;
                        mapY = (m_latchWy - 1);
                        mapSelect = m_windowTilemapSelect; // LCDC.6 LIVE.
                    }
                    else {
                        mapX = (((m_scx >> 3) + m_fetcherX) & 0x1F);   // SCX-high LIVE.
                        mapY = (byte)(m_ly + m_scy);                   // SCY LIVE.
                        mapSelect = m_bgTilemapSelect;                 // LCDC.3 LIVE.
                    }

                    // Freeze the fetch row at GetTile (the tile-index read). A mid-fetch SCY write must NOT shift the
                    // row-within-tile read at GetLow/GetHigh: hardware (and SameBoy) sample SCY once per tile fetch, so
                    // tilemap-row and tiledata-row stay consistent (mealybug m3_scy_change).
                    m_fetchRowY = m_fetcherIsWindow ? (m_latchWy - 1) : (byte)(m_ly + m_scy);

                    var tilemapAddress = (0x1800 + (mapSelect ? 0x400 : 0));

                    tilemapAddress += ((((mapY >> 3) << 5) + (mapX & 0x1F)) & 0x03FF);
                    m_fetchTileNumber = m_vram[tilemapAddress];
                    m_fetcherState = FetchState.GetLow;
                    m_fetcherStepDots = 2;
                }

                break;
            case FetchState.GetLow:
                m_fetcherStepDots -= 1;

                if (m_fetcherStepDots == 0) {
                    m_fetchTileLow = m_vram[BgRowAddress(plane: 0)]; // LCDC.4 LIVE.
                    m_fetcherState = FetchState.GetHigh;
                    m_fetcherStepDots = 2;
                }

                break;
            case FetchState.GetHigh:
                m_fetcherStepDots -= 1;

                if (m_fetcherStepDots == 0) {
                    m_fetchTileHigh = m_vram[BgRowAddress(plane: 1)]; // LCDC.4 LIVE.

                    if (!m_firstFetchDiscarded) {
                        m_firstFetchDiscarded = true;
                        m_fetcherState = FetchState.GetTile;
                        m_fetcherStepDots = 2; // throwaway: refetch col 0 without pushing.
                    }
                    else {
                        m_fetcherState = FetchState.Push;
                    }
                }

                break;
            case FetchState.Push:
            default:
                if (m_bgFifoCount == 0) {
                    for (var col = 0; col < 8; col += 1) {
                        var bit = (7 - col);

                        m_bgFifo[col] = (byte)((((m_fetchTileHigh >> bit) & 1) << 1) | ((m_fetchTileLow >> bit) & 1));
                    }

                    m_bgFifoCount = 8;
                    m_fetcherX += 1;
                    m_fetcherState = FetchState.GetTile;
                    m_fetcherStepDots = 2;
                }

                break;
        }
    }

    // The tiledata address of the current BG/window fetch row's low (plane 0) or high (plane 1) byte. LCDC.4 LIVE.
    // The fetch row (y) was frozen at GetTile so a mid-fetch SCY write cannot desync the row-within-tile.
    private int BgRowAddress(int plane) {
        var y = m_fetchRowY;
        int address;

        if (m_bgTiledataSelect) {
            address = (m_fetchTileNumber << 4);
        }
        else {
            address = (0x1000 + ((sbyte)m_fetchTileNumber << 4));
        }

        return (address + ((y & 7) << 1) + plane);
    }

    // Fetches one sprite's row into the OBJ FIFO under the no-overwrite rule (first opaque writer wins). OBJ-size is
    // read LIVE; the data/flip math is frozen here at fetch time.
    private void FetchSpriteRow(int i) {
        var height = (m_obSize ? 16 : 8);
        var tile = (m_obSize ? (m_spriteTile[i] & 0xFE) : m_spriteTile[i]);
        var row = (m_ly - m_spriteY[i]);

        if ((m_spriteAttributes[i] & 0x40) != 0) {
            row ^= (height - 1);
        }

        var tiledataAddress = ((tile << 4) + (row << 1));
        var tiledata = (ushort)(m_vram[tiledataAddress] | (m_vram[tiledataAddress + 1] << 8));

        if ((m_spriteAttributes[i] & 0x20) != 0) {
            tiledata = Hflip(tiledata: tiledata);
        }

        var pal1 = ((m_spriteAttributes[i] & 0x10) != 0);
        var bgPrio = ((m_spriteAttributes[i] & 0x80) != 0);
        var startCol = (m_spriteX[i] < 0) ? -m_spriteX[i] : 0;

        for (var c = startCol; c < 8; c += 1) {
            var index = TileIndex(tiledata: tiledata, tileX: c);
            var slot = (c - startCol);

            if ((index != 0) && (m_objFifoIndex[slot] == 0)) {
                m_objFifoIndex[slot] = (byte)index;
                m_objFifoPalette1[slot] = pal1;
                m_objFifoPriority[slot] = bgPrio;
            }
        }
    }

    // Packs both object palettes into one 16-bit key (for write detection).
    private int PackObp() =>
        m_obp[0] | (m_obp[1] << 2) | (m_obp[2] << 4) | (m_obp[3] << 6)
        | (m_obp[4] << 8) | (m_obp[5] << 10) | (m_obp[6] << 12) | (m_obp[7] << 14);

    // Pops one BG/window index (shifts the FIFO down by one).
    private byte ShiftBgFifo() {
        var index = m_bgFifo[0];

        for (var i = 1; i < 8; i += 1) {
            m_bgFifo[i - 1] = m_bgFifo[i];
        }

        m_bgFifoCount -= 1;

        return index;
    }

    // Pops one OBJ slot (shifts the FIFO down by one, refilling transparent at the tail). Pops in lockstep with the
    // BG FIFO so alignment never drifts; returns 0 (transparent) when no sprite covers this column.
    private byte ShiftObjFifo(out bool palette1, out bool priority) {
        var index = m_objFifoIndex[0];

        palette1 = m_objFifoPalette1[0];
        priority = m_objFifoPriority[0];

        for (var i = 1; i < 8; i += 1) {
            m_objFifoIndex[i - 1] = m_objFifoIndex[i];
            m_objFifoPalette1[i - 1] = m_objFifoPalette1[i];
            m_objFifoPriority[i - 1] = m_objFifoPriority[i];
        }

        m_objFifoIndex[7] = 0;
        m_objFifoPalette1[7] = false;
        m_objFifoPriority[7] = false;

        return index;
    }

    // Clears the BG FIFO and restarts the fetcher in window mode at window tile-col 0 (+6-dot prime via throwaway).
    private void TriggerWindowRestart() {
        m_bgFifoCount = 0;
        m_fetcherIsWindow = true;
        m_fetcherX = 0;
        m_fetcherState = FetchState.GetTile;
        m_fetcherStepDots = 2;
        m_firstFetchDiscarded = false;
    }

    // Clears the BG FIFO and restarts the BG fetcher at the column matching screen-x px (+6-dot prime via throwaway).
    private void RestartBgFetch(int px) {
        m_bgFifoCount = 0;
        m_fetcherIsWindow = false;
        m_fetcherX = (((m_scx >> 3) + ((px + (m_scx & 7)) >> 3)) & 0x1F);
        m_fetcherState = FetchState.GetTile;
        m_fetcherStepDots = 2;
        m_firstFetchDiscarded = false;
    }

    // ares readTileDMG addressing, retained for reference parity (the fetcher inlines the same math).
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

    // Extracts the 2bpp colour index for column tileX (0-7) from a 16-bit tile row.
    private static int TileIndex(ushort tiledata, int tileX) {
        var bit = (7 - tileX);

        return ((((tiledata >> (8 + bit)) & 1) << 1) | ((tiledata >> bit) & 1));
    }

    // === Per-pixel diagnostic trace (env-gated, parity with SameBoy's SAMEBOY_PX_TRACE) ===
    private static System.IO.TextWriter? s_pxTraceWriter;
    private static int s_pxTraceState; // -1 off, 0 unknown, 1 on
    private static int s_pxTraceLine = -1;

    private void PxTrace(int px, int shade, int bgIndex, int objIndex) {
        if (s_pxTraceState == 0) {
            var path = Environment.GetEnvironmentVariable("PUCK_PX_TRACE");

            if (!string.IsNullOrEmpty(path)) {
                s_pxTraceWriter = new System.IO.StreamWriter(path, append: false);
                s_pxTraceState = 1;

                var ln = Environment.GetEnvironmentVariable("PUCK_PX_LINE");

                s_pxTraceLine = string.IsNullOrEmpty(ln) ? -1 : int.Parse(ln);
            }
            else {
                s_pxTraceState = -1;
            }
        }

        if (s_pxTraceState != 1) {
            return;
        }

        if ((s_pxTraceLine >= 0) && (m_ly != s_pxTraceLine)) {
            return;
        }

        if (px == 0) {
            var sb = new System.Text.StringBuilder($"  >SPRITES LY={m_ly} count={m_sprites}:");

            for (var i = 0; i < m_sprites; i += 1) {
                sb.Append($" [x={m_spriteX[i]} y={m_spriteY[i]} t={m_spriteTile[i]} a=0x{m_spriteAttributes[i]:X2}]");
            }

            s_pxTraceWriter!.WriteLine(sb.ToString());
        }

        var lcdc = (byte)((m_bgEnable ? 0x01 : 0)
            | (m_obEnable ? 0x02 : 0)
            | (m_obSize ? 0x04 : 0)
            | (m_bgTilemapSelect ? 0x08 : 0)
            | (m_bgTiledataSelect ? 0x10 : 0)
            | (m_windowDisplayEnable ? 0x20 : 0)
            | (m_windowTilemapSelect ? 0x40 : 0)
            | (m_displayEnable ? 0x80 : 0));
        var bgp = (byte)(m_bgp[0] | (m_bgp[1] << 2) | (m_bgp[2] << 4) | (m_bgp[3] << 6));
        var mode3dot = Mode3WarmupExtra - m_mode3Warmup + px; // approximate

        s_pxTraceWriter!.WriteLine(
            $"LY={m_ly} px={px} shade={shade} bg_idx={bgIndex} oam_idx={objIndex} win={(m_windowDrawing ? 1 : 0)} "
            + $"LCDC=0x{lcdc:X2} BGP=0x{bgp:X2} SCX={m_scx} SCY={m_scy} WX={m_wx} WY={m_wy} fx={m_fetcherX} fs={m_fetcherState} fwin={(m_fetcherIsWindow ? 1 : 0)}");
        s_pxTraceWriter.Flush();
    }

    private void LcdcWriteTrace(byte data) {
        if (s_pxTraceState != 1) {
            return;
        }

        var old = (byte)((m_bgEnable ? 0x01 : 0)
            | (m_obEnable ? 0x02 : 0)
            | (m_obSize ? 0x04 : 0)
            | (m_bgTilemapSelect ? 0x08 : 0)
            | (m_bgTiledataSelect ? 0x10 : 0)
            | (m_windowDisplayEnable ? 0x20 : 0)
            | (m_windowTilemapSelect ? 0x40 : 0)
            | (m_displayEnable ? 0x80 : 0));

        s_pxTraceWriter!.WriteLine($"  >LCDC_WRITE LY={m_ly} lx={m_lx} mode={m_mode} px={m_px} old=0x{old:X2} new=0x{data:X2}");
        s_pxTraceWriter.Flush();
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
