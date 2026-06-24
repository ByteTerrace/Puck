namespace Puck.HumbleGamingBrick;

/// <summary>
/// The per-dot background/window pixel pipeline for the DMG (Phase 1 of the dot-accurate PPU). During mode 3 the
/// fetcher runs one logical step every two dots — read the tile number, the low plane, the high plane, then push
/// eight colour indices into the FIFO — while the FIFO shifts one pixel out per dot. Because each pixel is produced at
/// its own dot, mid-scanline writes to <c>SCX</c>/<c>SCY</c>/<c>LCDC</c>/<c>BGP</c> take effect partway across the
/// line (raster-split effects the all-at-once scanline renderer could not show). Sprites are still overlaid at line
/// end and the closed-form mode-3 length still drives timing; both are folded into the fetcher in later phases.
/// </summary>
public sealed partial class Ppu {
    // Resets the pipeline at the start of mode 3 for the current line.
    private void StartBackgroundFetch() {
        m_bgFifoHead = 0;
        m_bgFifoCount = 0;
        m_fetchStep = 0;
        m_fetchTileX = 0;
        m_pixelX = 0;
        m_scxDiscard = (m_scrollX & 7);
        m_fetchingWindow = false;
        m_windowDrawnThisLine = false;
    }

    // Advances the pipeline by one dot: maybe switch to the window, advance the fetcher, and shift one pixel out.
    private void StepBackgroundFetcher() {
        // The window takes over once it is enabled, this line has reached WY, and output has reached WX-7. Switching
        // clears the background FIFO and restarts the fetcher in window space.
        if (!m_fetchingWindow
            && ((m_lcdControl & 0x20) != 0)
            && (m_line >= m_windowY)
            && (m_pixelX >= (m_windowX - 7))
            && (m_windowX < (ScreenWidth + 7))) {
            m_fetchingWindow = true;
            m_windowDrawnThisLine = true;
            m_bgFifoHead = 0;
            m_bgFifoCount = 0;
            m_fetchStep = 0;
            m_fetchTileX = 0;
        }

        AdvanceFetcher();

        if (m_bgFifoCount > 0) {
            var colorIndex = PopBackgroundFifo();

            if (m_scxDiscard > 0) {
                // Drop the fine-scroll pixels at the line's left edge without emitting them.
                m_scxDiscard -= 1;
            }
            else if (m_pixelX < ScreenWidth) {
                // LCDC bit 0 clear blanks the background/window to colour 0 on the DMG.
                var visibleIndex = (((m_lcdControl & 0x01) != 0) ? colorIndex : 0);

                m_lineColorIndex[m_pixelX] = (byte)visibleIndex;
                m_framebuffer[(m_line * ScreenWidth) + m_pixelX] = BackgroundColor(shade: ((m_backgroundPalette >> (visibleIndex * 2)) & 3));
                m_pixelX += 1;
            }
        }
    }

    // The fetcher's four two-dot phases: tile number, low plane, high plane, then push (which stalls until the FIFO
    // has room for another eight pixels).
    private void AdvanceFetcher() {
        m_fetchStep += 1;

        switch (m_fetchStep) {
            case 2:
                m_fetchTileNumber = FetchTileNumber();

                break;
            case 4:
                m_fetchLow = FetchTilePlane(high: false);

                break;
            case 6:
                m_fetchHigh = FetchTilePlane(high: true);

                break;
            case 8:
                if (m_bgFifoCount <= 8) {
                    PushTileToFifo(low: m_fetchLow, high: m_fetchHigh);
                    m_fetchTileX += 1;
                    m_fetchStep = 0;
                }
                else {
                    // No room yet; hold at the push phase and retry next dot.
                    m_fetchStep = 7;
                }

                break;
            default:
                break;
        }
    }

    private int FetchTileNumber() {
        var mapBase = (m_fetchingWindow
            ? (((m_lcdControl & 0x40) != 0) ? TileMap1 : TileMap0)
            : (((m_lcdControl & 0x08) != 0) ? TileMap1 : TileMap0));
        var pixelY = (m_fetchingWindow ? m_windowLineCounter : ((m_scrollY + m_line) & 0xFF));
        var tileX = (m_fetchingWindow ? m_fetchTileX : (((m_scrollX >> 3) + m_fetchTileX) & 0x1F));

        return ReadVideoRam(address: (ushort)(mapBase + (((pixelY >> 3) & 0x1F) * 32) + tileX));
    }

    private int FetchTilePlane(bool high) {
        var unsignedTiles = ((m_lcdControl & 0x10) != 0);
        var pixelY = (m_fetchingWindow ? m_windowLineCounter : ((m_scrollY + m_line) & 0xFF));
        var tileDataAddress = (unsignedTiles
            ? (0x8000 + (m_fetchTileNumber * 16))
            : (0x9000 + ((sbyte)m_fetchTileNumber * 16)));
        var rowAddress = (ushort)(tileDataAddress + ((pixelY & 7) * 2) + (high ? 1 : 0));

        return ReadVideoRam(address: rowAddress);
    }

    private void PushTileToFifo(int low, int high) {
        for (var pixel = 0; pixel < 8; pixel += 1) {
            var bit = (7 - pixel);
            var colorIndex = ((((high >> bit) & 1) << 1) | ((low >> bit) & 1));

            m_bgFifo[(m_bgFifoHead + m_bgFifoCount) & 0x0F] = (byte)colorIndex;
            m_bgFifoCount += 1;
        }
    }

    private int PopBackgroundFifo() {
        var value = m_bgFifo[m_bgFifoHead];

        m_bgFifoHead = ((m_bgFifoHead + 1) & 0x0F);
        m_bgFifoCount -= 1;

        return value;
    }

    // Called at the mode-3 -> mode-0 transition for the DMG: finalize the background line (advancing the window's
    // internal counter), filling any pixels the fetcher did not reach as a safety net.
    private void FinishBackgroundLine() {
        while (m_pixelX < ScreenWidth) {
            var colorIndex = (((m_lcdControl & 0x01) != 0) ? FetchBackgroundPixel(x: m_pixelX) : 0);

            m_lineColorIndex[m_pixelX] = (byte)colorIndex;
            m_framebuffer[(m_line * ScreenWidth) + m_pixelX] = BackgroundColor(shade: ((m_backgroundPalette >> (colorIndex * 2)) & 3));
            m_pixelX += 1;
        }

        if (m_windowDrawnThisLine) {
            m_windowLineCounter += 1;
        }
    }

    // The safety-net per-pixel background/window fetch (mirrors the fetcher's tile maths for a single screen X).
    private int FetchBackgroundPixel(int x) {
        var window = (((m_lcdControl & 0x20) != 0) && (m_line >= m_windowY) && (x >= (m_windowX - 7)));
        var mapBase = (window
            ? (((m_lcdControl & 0x40) != 0) ? TileMap1 : TileMap0)
            : (((m_lcdControl & 0x08) != 0) ? TileMap1 : TileMap0));
        var pixelX = (window ? (x - (m_windowX - 7)) : ((m_scrollX + x) & 0xFF));
        var pixelY = (window ? m_windowLineCounter : ((m_scrollY + m_line) & 0xFF));

        return FetchTileColor(mapBase: mapBase, pixelX: pixelX, pixelY: pixelY, unsignedTiles: ((m_lcdControl & 0x10) != 0));
    }
}
