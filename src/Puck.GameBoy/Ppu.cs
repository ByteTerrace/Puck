namespace Puck.GameBoy;

/// <summary>
/// The picture processing unit — timing core. It walks the dot clock through the per-scanline mode sequence
/// (OAM scan → pixel transfer → horizontal blank for visible lines, then vertical blank), drives the
/// <c>LY</c>/<c>LYC</c> comparison and the STAT and vertical-blank interrupts, and exposes the VRAM/OAM
/// accessibility the bus enforces. This stage establishes the timing and the register interface; the pixel
/// pipeline (background, window, sprites) fills the framebuffer in a later stage, so <see cref="Framebuffer"/>
/// is allocated and latched on each vertical blank but not yet painted.
/// </summary>
public sealed class Ppu : IClockedComponent {
    /// <summary>The visible screen width in pixels.</summary>
    public const int ScreenWidth = 160;
    /// <summary>The visible screen height in pixels.</summary>
    public const int ScreenHeight = 144;

    private const int DotsPerLine = 456;
    private const int OamScanDots = 80;
    private const int DrawingDots = 172;
    private const int LastLine = 153;

    private const byte StatHBlankInterrupt = 0x08;
    private const byte StatVBlankInterrupt = 0x10;
    private const byte StatOamInterrupt = 0x20;
    private const byte StatLineCompareInterrupt = 0x40;
    private const byte LcdEnable = 0x80;

    private const ushort VideoRamBase = 0x8000;
    private const ushort TileMap0 = 0x9800;
    private const ushort TileMap1 = 0x9C00;

    // DMG shades 0-3 (lightest to darkest) as packed R8G8B8A8 (0xAABBGGRR in little-endian byte order).
    private static readonly uint[] Shades = [0xFFFFFFFFu, 0xFFAAAAAAu, 0xFF555555u, 0xFF000000u];

    private readonly InterruptController m_interrupts;
    private readonly byte[] m_objectAttributeMemory;
    private readonly byte[] m_videoRam;
    private readonly uint[] m_framebuffer = new uint[ScreenWidth * ScreenHeight];
    // The front buffer: a stable snapshot of the most recently completed frame, latched from the back buffer at
    // vertical blank. Presenting from this (rather than the live back buffer the PPU paints into scanline by
    // scanline) means a host that stops the machine mid-frame never sees a torn, half-drawn picture.
    private readonly uint[] m_presentBuffer = new uint[ScreenWidth * ScreenHeight];
    // The background/window color index (0-3) chosen per pixel on the current line, used for sprite-to-background
    // priority (a sprite with the priority bit set is hidden behind background colors 1-3).
    private readonly byte[] m_lineColorIndex = new byte[ScreenWidth];

    private bool m_coincidence;
    private bool m_enabled;
    private bool m_frameReady;
    private bool m_statInterruptLine;
    private int m_dot;
    private int m_line;
    private int m_mode0StartDot = (OamScanDots + DrawingDots);
    private int m_reportedModeDelay;
    private int m_windowLineCounter;
    private PpuMode m_mode = PpuMode.HorizontalBlank;
    private PpuMode m_reportedMode = PpuMode.HorizontalBlank;
    private byte m_backgroundPalette;
    private byte m_lcdControl;
    private byte m_lineCompare;
    private byte m_objectPalette0;
    private byte m_objectPalette1;
    private byte m_scrollX;
    private byte m_scrollY;
    private byte m_statEnables;
    private byte m_windowX;
    private byte m_windowY;

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Lcd;
    /// <summary>Gets the current mode.</summary>
    public PpuMode Mode =>
        m_mode;
    /// <summary>Gets the current scanline (<c>LY</c>), 0-153.</summary>
    public int Line =>
        m_line;
    /// <summary>Gets the latched frame's pixels (32-bit RGBA, row-major, 160&#215;144): the most recently completed
    /// frame, snapshotted at vertical blank, so the picture is always whole even when read while the next frame is
    /// mid-draw. (Empty — all zero — until the first frame completes.)</summary>
    public ReadOnlySpan<uint> Framebuffer =>
        m_presentBuffer;
    /// <summary>Gets whether VRAM is currently accessible to the CPU — true unless the PPU is drawing. Follows the
    /// reported (machine-cycle-lagged) mode, so the lock tracks what a CPU read observes.</summary>
    public bool IsVideoRamAccessible =>
        (!m_enabled || (m_reportedMode != PpuMode.Drawing));
    /// <summary>Gets whether OAM is currently accessible to the CPU — true outside the OAM-scan and drawing modes,
    /// tracking the reported (lagged) mode.</summary>
    public bool IsObjectMemoryAccessible =>
        (!m_enabled || (m_reportedMode is PpuMode.HorizontalBlank or PpuMode.VerticalBlank));

    /// <summary>Initializes the PPU wired to the interrupt controller and the video RAM it fetches tiles and maps
    /// from. The PPU reads video RAM directly (it is the component that locks it from the CPU), so the access is
    /// not subject to the mode lock.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="videoRam">The video RAM backing store the bus owns.</param>
    /// <param name="objectAttributeMemory">The 160-byte OAM backing store the bus owns, scanned for sprites.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Ppu(InterruptController interrupts, byte[] videoRam, byte[] objectAttributeMemory) {
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(videoRam);
        ArgumentNullException.ThrowIfNull(objectAttributeMemory);

        m_interrupts = interrupts;
        m_objectAttributeMemory = objectAttributeMemory;
        m_videoRam = videoRam;
    }

    /// <summary>Returns whether a fully rendered frame is ready to present, clearing the flag.</summary>
    /// <returns><see langword="true"/> once per frame, at the start of vertical blank.</returns>
    public bool ConsumeFrameReady() {
        var ready = m_frameReady;

        m_frameReady = false;

        return ready;
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        if (!m_enabled) {
            return;
        }

        for (var index = 0; index < tCycles; index += 1) {
            TickDot();
        }
    }

    /// <summary>Reads one of the PPU's registers (<c>0xFF40</c>-<c>0xFF4B</c>, excluding the OAM DMA register).</summary>
    /// <param name="address">The register address.</param>
    /// <returns>The register value with hardware read-as-one bits applied.</returns>
    public byte ReadRegister(ushort address) =>
        address switch {
            MemoryMap.LcdControl => m_lcdControl,
            MemoryMap.LcdStatus => ReadStatus(),
            MemoryMap.ScrollY => m_scrollY,
            MemoryMap.ScrollX => m_scrollX,
            MemoryMap.LcdLine => (byte)m_line,
            MemoryMap.LcdLineCompare => m_lineCompare,
            MemoryMap.BackgroundPalette => m_backgroundPalette,
            MemoryMap.ObjectPalette0 => m_objectPalette0,
            MemoryMap.ObjectPalette1 => m_objectPalette1,
            MemoryMap.WindowY => m_windowY,
            MemoryMap.WindowX => m_windowX,
            _ => 0xFF,
        };
    /// <summary>Writes one of the PPU's registers (<c>0xFF40</c>-<c>0xFF4B</c>, excluding the OAM DMA register).</summary>
    /// <param name="address">The register address.</param>
    /// <param name="value">The value written.</param>
    public void WriteRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.LcdControl:
                SetLcdControl(value: value);

                break;
            case MemoryMap.LcdStatus:
                // Only the four interrupt-source-enable bits (3-6) are writable.
                m_statEnables = (byte)(value & 0x78);

                // While the LCD is off the STAT line is frozen; recompute only when it is running.
                if (m_enabled) {
                    UpdateStatInterrupt();
                }

                break;
            case MemoryMap.ScrollY:
                m_scrollY = value;

                break;
            case MemoryMap.ScrollX:
                m_scrollX = value;

                break;
            case MemoryMap.LcdLine:
                // LY is read-only.
                break;
            case MemoryMap.LcdLineCompare:
                m_lineCompare = value;

                // While the LCD is off the comparison is frozen; recompute only when it is running.
                if (m_enabled) {
                    UpdateStatInterrupt();
                }

                break;
            case MemoryMap.BackgroundPalette:
                m_backgroundPalette = value;

                break;
            case MemoryMap.ObjectPalette0:
                m_objectPalette0 = value;

                break;
            case MemoryMap.ObjectPalette1:
                m_objectPalette1 = value;

                break;
            case MemoryMap.WindowY:
                m_windowY = value;

                break;
            case MemoryMap.WindowX:
                m_windowX = value;

                break;
            default:
                break;
        }
    }

    private void TickDot() {
        // The STAT register's mode bits lag the actual mode transition by one machine cycle: the interrupt fires
        // at the transition, but a CPU read sees the new mode 4 dots later. (The interrupt-to-interrupt deltas
        // stay exact; only the polled view is delayed.) Decremented at the top so a transition later in this dot
        // schedules a full 4-dot delay.
        if (m_reportedModeDelay > 0) {
            m_reportedModeDelay -= 1;

            if (m_reportedModeDelay == 0) {
                m_reportedMode = m_mode;
            }
        }

        m_dot += 1;

        if (m_dot >= DotsPerLine) {
            m_dot = 0;
            AdvanceLine();
        }
        else if (m_line < ScreenHeight) {
            if (m_dot == OamScanDots) {
                // SCX is latched at the mode 2->3 transition; its low three bits lengthen mode 3 (and so push the
                // mode-0 start later), which HBlank absorbs to keep the line 456 dots.
                m_mode0StartDot = (OamScanDots + DrawingDots + ScxPenalty(scrollX: (m_scrollX & 7)));
                SetMode(mode: PpuMode.Drawing);
            }
            else if (m_dot == m_mode0StartDot) {
                RenderScanline();
                SetMode(mode: PpuMode.HorizontalBlank);
            }
        }
    }

    // DMG mode-3 lengthening from the fine X-scroll: +4 dots when SCX&7 is 1-4, +8 when 5-7 (the CGB differs and
    // is intentionally not matched here).
    private static int ScxPenalty(int scrollX) =>
        ((scrollX == 0)
            ? 0
            : ((scrollX <= 4) ? 4 : 8));

    private void AdvanceLine() {
        m_line += 1;

        if (m_line > LastLine) {
            m_line = 0;
        }

        if (m_line == ScreenHeight) {
            // Entering vertical blank: the frame is complete and the vertical-blank interrupt fires. The finished
            // back buffer is latched into the front buffer so it can be presented whole while the next frame draws.
            // The window's internal line counter resets here, ready for the next frame.
            SetMode(mode: PpuMode.VerticalBlank);
            m_interrupts.Request(kind: InterruptKind.VBlank);
            Array.Copy(sourceArray: m_framebuffer, destinationArray: m_presentBuffer, length: m_framebuffer.Length);
            m_frameReady = true;
            m_windowLineCounter = 0;
        }
        else if (m_line < ScreenHeight) {
            SetMode(mode: PpuMode.OamScan);
        }

        // The line number changed even when the mode did not (lines 145-153), so refresh LY=LYC.
        UpdateStatInterrupt();
    }

    private void SetMode(PpuMode mode) {
        m_mode = mode;
        // The interrupt is evaluated now (at the transition); the STAT mode bits update 4 dots later.
        m_reportedModeDelay = 4;
        UpdateStatInterrupt();
    }

    private void RenderScanline() {
        // Scanline-based rendering using the registers as they stand at pixel-transfer end. The dot-accurate
        // fetcher/FIFO (which lets mid-scanline register changes take effect) is a later stage.
        RenderBackgroundAndWindow(line: m_line);
        RenderSprites(line: m_line);
    }

    private void RenderBackgroundAndWindow(int line) {
        var rowBase = (line * ScreenWidth);

        // On the DMG, LCDC bit 0 disables both background and window, clearing the line to the lightest shade.
        if ((m_lcdControl & 0x01) == 0) {
            for (var x = 0; x < ScreenWidth; x += 1) {
                m_framebuffer[rowBase + x] = Shades[0];
                m_lineColorIndex[x] = 0;
            }

            return;
        }

        var backgroundMap = (((m_lcdControl & 0x08) != 0) ? TileMap1 : TileMap0);
        var windowMap = (((m_lcdControl & 0x40) != 0) ? TileMap1 : TileMap0);
        var unsignedTiles = ((m_lcdControl & 0x10) != 0);
        var windowActive = (((m_lcdControl & 0x20) != 0) && (line >= m_windowY));
        var windowStartX = (m_windowX - 7);
        var windowShown = false;

        for (var x = 0; x < ScreenWidth; x += 1) {
            int colorIndex;

            if (windowActive && (x >= windowStartX)) {
                // The window has its own map and an internal line counter (it advances only on lines it appears).
                colorIndex = FetchTileColor(
                    mapBase: windowMap,
                    pixelX: (x - windowStartX),
                    pixelY: m_windowLineCounter,
                    unsignedTiles: unsignedTiles
                );
                windowShown = true;
            }
            else {
                colorIndex = FetchTileColor(
                    mapBase: backgroundMap,
                    pixelX: ((m_scrollX + x) & 0xFF),
                    pixelY: ((m_scrollY + line) & 0xFF),
                    unsignedTiles: unsignedTiles
                );
            }

            m_lineColorIndex[x] = (byte)colorIndex;

            var shade = ((m_backgroundPalette >> (colorIndex * 2)) & 3);

            m_framebuffer[rowBase + x] = Shades[shade];
        }

        // The window's internal line counter advances once per line the window was actually drawn.
        if (windowShown) {
            m_windowLineCounter += 1;
        }
    }

    private int FetchTileColor(ushort mapBase, int pixelX, int pixelY, bool unsignedTiles) {
        var tileNumber = ReadVideoRam(address: (ushort)(mapBase + ((pixelY >> 3) * 32) + (pixelX >> 3)));

        // The 0x8000 method indexes tiles unsigned from 0x8000; the 0x8800 method indexes signed from 0x9000.
        var tileDataAddress = (unsignedTiles
            ? (0x8000 + (tileNumber * 16))
            : (0x9000 + ((sbyte)tileNumber * 16)));
        var rowAddress = (ushort)(tileDataAddress + ((pixelY & 7) * 2));
        var low = ReadVideoRam(address: rowAddress);
        var high = ReadVideoRam(address: (ushort)(rowAddress + 1));
        var bit = (7 - (pixelX & 7));

        return ((((high >> bit) & 1) << 1) | ((low >> bit) & 1));
    }

    private void RenderSprites(int line) {
        // Objects (sprites) are disabled by LCDC bit 1.
        if ((m_lcdControl & 0x02) == 0) {
            return;
        }

        var rowBase = (line * ScreenWidth);
        var spriteHeight = (((m_lcdControl & 0x04) != 0) ? 16 : 8);

        // OAM scan: the first 10 objects (in OAM order) whose vertical span covers this line.
        Span<int> selected = stackalloc int[10];
        var count = 0;

        for (var index = 0; (index < 40) && (count < 10); index += 1) {
            var objectY = (m_objectAttributeMemory[index * 4] - 16);

            if ((line >= objectY) && (line < (objectY + spriteHeight))) {
                selected[count] = index;
                count += 1;
            }
        }

        // DMG object priority: a smaller X draws on top, ties broken by the smaller OAM index. Draw lowest priority
        // first (largest X, then largest index) so the highest-priority object overwrites.
        SortByDrawOrder(
            selected: selected[..count]
        );

        foreach (var index in selected[..count]) {
            DrawSprite(
                line: line,
                oamIndex: index,
                rowBase: rowBase,
                spriteHeight: spriteHeight
            );
        }
    }

    private void SortByDrawOrder(Span<int> selected) {
        // Insertion sort by object X descending, then OAM index descending (small span, at most 10 entries).
        for (var i = 1; i < selected.Length; i += 1) {
            var current = selected[i];
            var currentX = m_objectAttributeMemory[(current * 4) + 1];
            var j = (i - 1);

            while ((j >= 0) && IsHigherDrawPriority(candidate: selected[j], candidateX: m_objectAttributeMemory[(selected[j] * 4) + 1], referenceX: currentX, referenceIndex: current)) {
                selected[j + 1] = selected[j];
                j -= 1;
            }

            selected[j + 1] = current;
        }
    }

    private static bool IsHigherDrawPriority(int candidate, int candidateX, int referenceX, int referenceIndex) =>
        // "Earlier in draw order" means lower priority: larger X, or equal X with larger OAM index.
        ((candidateX < referenceX) || ((candidateX == referenceX) && (candidate < referenceIndex)));

    private void DrawSprite(int line, int oamIndex, int rowBase, int spriteHeight) {
        var oamAddress = (oamIndex * 4);
        var objectY = (m_objectAttributeMemory[oamAddress] - 16);
        var objectX = (m_objectAttributeMemory[oamAddress + 1] - 8);
        var tile = (int)m_objectAttributeMemory[oamAddress + 2];
        var attributes = m_objectAttributeMemory[oamAddress + 3];
        var palette = (((attributes & 0x10) != 0) ? m_objectPalette1 : m_objectPalette0);
        var behindBackground = ((attributes & 0x80) != 0);

        var rowInSprite = (line - objectY);

        if ((attributes & 0x40) != 0) {
            rowInSprite = (spriteHeight - 1 - rowInSprite);
        }

        if (spriteHeight == 16) {
            // In 8x16 mode the low bit of the tile number is ignored; the two stacked tiles are addressed by the row.
            tile &= 0xFE;
        }

        var rowAddress = (ushort)(0x8000 + (tile * 16) + (rowInSprite * 2));
        var low = ReadVideoRam(address: rowAddress);
        var high = ReadVideoRam(address: (ushort)(rowAddress + 1));
        var flipX = ((attributes & 0x20) != 0);

        for (var pixel = 0; pixel < 8; pixel += 1) {
            var screenX = (objectX + pixel);

            if ((screenX < 0) || (screenX >= ScreenWidth)) {
                continue;
            }

            var bit = (flipX ? pixel : (7 - pixel));
            var colorIndex = ((((high >> bit) & 1) << 1) | ((low >> bit) & 1));

            // Color 0 is transparent; a priority object is also hidden behind non-zero background pixels.
            if ((colorIndex == 0) || (behindBackground && (m_lineColorIndex[screenX] != 0))) {
                continue;
            }

            var shade = ((palette >> (colorIndex * 2)) & 3);

            m_framebuffer[rowBase + screenX] = Shades[shade];
        }
    }

    private byte ReadVideoRam(ushort address) =>
        m_videoRam[address - VideoRamBase];

    private void UpdateStatInterrupt() {
        // The coincidence bit is latched here (it is frozen while the LCD is off, since this is not called then),
        // so STAT reads return the held value rather than a fresh LY=LYC compare.
        m_coincidence = (m_line == m_lineCompare);
        // The OAM (mode 2) STAT source asserts during mode 2 and also at the start of vertical blank (line 144,
        // a DMG quirk).
        var oamSource = (((m_mode == PpuMode.OamScan) || (m_line == ScreenHeight)) && ((m_statEnables & StatOamInterrupt) != 0));
        var line = (
            ((m_mode == PpuMode.HorizontalBlank) && ((m_statEnables & StatHBlankInterrupt) != 0)) ||
            ((m_mode == PpuMode.VerticalBlank) && ((m_statEnables & StatVBlankInterrupt) != 0)) ||
            oamSource ||
            (m_coincidence && ((m_statEnables & StatLineCompareInterrupt) != 0))
        );

        // The STAT interrupt fires only on the rising edge of the combined condition (STAT blocking).
        if (line && !m_statInterruptLine) {
            m_interrupts.Request(kind: InterruptKind.LcdStat);
        }

        m_statInterruptLine = line;
    }

    private byte ReadStatus() {
        // The latched coincidence bit (frozen while the LCD is off) and the lagged mode bits, rather than a live
        // compare or the instantaneous mode.
        var coincidence = (m_coincidence ? 0x04 : 0x00);

        return (byte)(0x80 | m_statEnables | coincidence | (int)m_reportedMode);
    }

    private void SetLcdControl(byte value) {
        var enabling = ((value & LcdEnable) != 0);

        m_lcdControl = value;

        if (enabling == m_enabled) {
            return;
        }

        m_enabled = enabling;
        // The first scanline after the LCD is enabled runs ~4 dots short (the documented first-frame lateness), so
        // LY advances slightly earlier than a normal 456-dot line. (Verified against lcdon_timing-GS's LY and
        // STAT-mode sample points; the remaining lcdon coincidence-bit and OAM/VRAM-access edges are still open.)
        m_dot = (enabling ? 4 : 0);
        m_line = 0;

        // The LCD-enable first-frame quirk: line 0 has NO OAM-scan phase — it begins directly in mode 0, then
        // mode 3 at dot 80, then mode 0 (so OAM and VRAM stay accessible until mode 3). Lines 1+ run normally.
        // Turning the LCD off also parks it in mode 0 with LY=0. The reported mode is set immediately (no lag).
        m_mode = PpuMode.HorizontalBlank;
        m_reportedMode = PpuMode.HorizontalBlank;
        m_reportedModeDelay = 0;
        m_windowLineCounter = 0;

        // On re-enable, recompute the STAT line for LY=0 and fire only on a genuine rising edge carried from the
        // value held while the LCD was off — resetting it to false would fabricate a spurious edge. On disable,
        // the STAT line is frozen at its last value (writes skip recomputation while off).
        if (enabling) {
            UpdateStatInterrupt();
        }
    }
}
