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
    private readonly byte[] m_videoRam;
    private readonly uint[] m_framebuffer = new uint[ScreenWidth * ScreenHeight];

    private bool m_coincidence;
    private bool m_enabled;
    private bool m_frameReady;
    private bool m_statInterruptLine;
    private int m_dot;
    private int m_line;
    private int m_mode0StartDot = (OamScanDots + DrawingDots);
    private PpuMode m_mode = PpuMode.HorizontalBlank;
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
    /// <summary>Gets the latched frame's pixels (32-bit RGBA, row-major, 160&#215;144). Painted by a later stage.</summary>
    public ReadOnlySpan<uint> Framebuffer =>
        m_framebuffer;
    /// <summary>Gets whether VRAM is currently accessible to the CPU — true unless the PPU is drawing.</summary>
    public bool IsVideoRamAccessible =>
        (!m_enabled || (m_mode != PpuMode.Drawing));
    /// <summary>Gets whether OAM is currently accessible to the CPU — true outside the OAM-scan and drawing modes.</summary>
    public bool IsObjectMemoryAccessible =>
        (!m_enabled || (m_mode is PpuMode.HorizontalBlank or PpuMode.VerticalBlank));

    /// <summary>Initializes the PPU wired to the interrupt controller and the video RAM it fetches tiles and maps
    /// from. The PPU reads video RAM directly (it is the component that locks it from the CPU), so the access is
    /// not subject to the mode lock.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="videoRam">The video RAM backing store the bus owns.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Ppu(InterruptController interrupts, byte[] videoRam) {
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(videoRam);

        m_interrupts = interrupts;
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
            // Entering vertical blank: the frame is complete and the vertical-blank interrupt fires.
            SetMode(mode: PpuMode.VerticalBlank);
            m_interrupts.Request(kind: InterruptKind.VBlank);
            m_frameReady = true;
        }
        else if (m_line < ScreenHeight) {
            SetMode(mode: PpuMode.OamScan);
        }

        // The line number changed even when the mode did not (lines 145-153), so refresh LY=LYC.
        UpdateStatInterrupt();
    }

    private void SetMode(PpuMode mode) {
        m_mode = mode;
        UpdateStatInterrupt();
    }

    private void RenderScanline() {
        // Scanline-based background rendering using the registers as they stand at pixel-transfer end. The
        // dot-accurate fetcher/FIFO (which lets mid-scanline register changes take effect) is a later stage;
        // window and sprites are also still to come.
        RenderBackground(line: m_line);
    }

    private void RenderBackground(int line) {
        var rowBase = (line * ScreenWidth);

        // On the DMG, LCDC bit 0 clears the background to the lightest shade when zero.
        if ((m_lcdControl & 0x01) == 0) {
            for (var x = 0; x < ScreenWidth; x += 1) {
                m_framebuffer[rowBase + x] = Shades[0];
            }

            return;
        }

        var mapBase = (((m_lcdControl & 0x08) != 0) ? TileMap1 : TileMap0);
        var unsignedTiles = ((m_lcdControl & 0x10) != 0);
        var backgroundY = ((m_scrollY + line) & 0xFF);
        var tileRow = (backgroundY >> 3);
        var tileLine = (backgroundY & 7);

        for (var x = 0; x < ScreenWidth; x += 1) {
            var backgroundX = ((m_scrollX + x) & 0xFF);
            var tileColumn = (backgroundX >> 3);
            var tileNumber = ReadVideoRam(address: (ushort)(mapBase + (tileRow * 32) + tileColumn));

            // The 0x8000 method indexes tiles unsigned from 0x8000; the 0x8800 method indexes signed from 0x9000.
            var tileDataAddress = (unsignedTiles
                ? (0x8000 + (tileNumber * 16))
                : (0x9000 + ((sbyte)tileNumber * 16)));
            var rowAddress = (ushort)(tileDataAddress + (tileLine * 2));
            var low = ReadVideoRam(address: rowAddress);
            var high = ReadVideoRam(address: (ushort)(rowAddress + 1));
            var bit = (7 - (backgroundX & 7));
            var colorIndex = ((((high >> bit) & 1) << 1) | ((low >> bit) & 1));
            var shade = ((m_backgroundPalette >> (colorIndex * 2)) & 3);

            m_framebuffer[rowBase + x] = Shades[shade];
        }
    }

    private byte ReadVideoRam(ushort address) =>
        m_videoRam[address - VideoRamBase];

    private void UpdateStatInterrupt() {
        // The coincidence bit is latched here (it is frozen while the LCD is off, since this is not called then),
        // so STAT reads return the held value rather than a fresh LY=LYC compare.
        m_coincidence = (m_line == m_lineCompare);
        // The OAM (mode 2) STAT source also asserts at the start of vertical blank (line 144) on the DMG, even
        // though the PPU enters mode 1 there.
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
        // The latched coincidence bit (frozen while the LCD is off) rather than a live LY=LYC compare.
        var coincidence = (m_coincidence ? 0x04 : 0x00);

        return (byte)(0x80 | m_statEnables | coincidence | (int)m_mode);
    }

    private void SetLcdControl(byte value) {
        var enabling = ((value & LcdEnable) != 0);

        m_lcdControl = value;

        if (enabling == m_enabled) {
            return;
        }

        m_enabled = enabling;
        m_dot = 0;
        m_line = 0;

        // The LCD-enable first-frame quirk: line 0 has NO OAM-scan phase — it begins directly in mode 0, then
        // mode 3 at dot 80, then mode 0 (so OAM and VRAM stay accessible until mode 3). Lines 1+ run normally.
        // Turning the LCD off also parks it in mode 0 with LY=0.
        m_mode = PpuMode.HorizontalBlank;

        // On re-enable, recompute the STAT line for LY=0 and fire only on a genuine rising edge carried from the
        // value held while the LCD was off — resetting it to false would fabricate a spurious edge. On disable,
        // the STAT line is frozen at its last value (writes skip recomputation while off).
        if (enabling) {
            UpdateStatInterrupt();
        }
    }
}
