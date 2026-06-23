namespace Puck.GameBoy;

/// <summary>
/// The picture processing unit — timing core. It walks the dot clock through the per-scanline mode sequence
/// (OAM scan → pixel transfer → horizontal blank for visible lines, then vertical blank), drives the
/// <c>LY</c>/<c>LYC</c> comparison and the STAT and vertical-blank interrupts, and exposes the VRAM/OAM
/// accessibility the bus enforces. This stage establishes the timing and the register interface; the pixel
/// pipeline (background, window, sprites) fills the framebuffer in a later stage, so <see cref="Framebuffer"/>
/// is allocated and latched on each vertical blank but not yet painted.
/// </summary>
public sealed partial class Ppu : IPpu {
    /// <summary>The visible screen width in pixels.</summary>
    public const int ScreenWidth = 160;
    /// <summary>The visible screen height in pixels.</summary>
    public const int ScreenHeight = 144;

    private const int DotsPerLine = 456;
    private const int OamScanDots = 80;
    private const int DrawingDots = 172;
    private const int LastLine = 153;
    // T-cycles the LY=LYC comparison stays invalid after LY increments (the polled coincidence bit reads 0 in this
    // window, then tracks the new line) — the documented LCD-enable coincidence timing.
    private const int LyComparisonDelayDots = 4;
    // T-cycles the polled STAT mode bits lag the actual mode transition. Small under this bus's deferred-cycle read
    // model: a CPU I/O read latches at the START of its access machine cycle (the access's four T-cycles are charged
    // afterward), so the read already observes the PPU a machine cycle "ahead" — the mode bits need only a single
    // dot of lag on top to land on the right machine cycle. (This is the hardware's sub-cycle STAT-mode and
    // sprite-timing behavior.)
    private const int StatBitReportDelay = 1;
    // T-cycles the mode-driven STAT *interrupt* lags the actual transition, on its own clock independent of the
    // polled STAT bits — the mode the interrupt logic sees lags the STAT-register mode by one machine cycle, which is
    // the hardware's interrupt-to-interrupt timing.
    private const int InterruptReportDelay = 4;

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

    private readonly IInterruptController m_interrupts;
    private readonly byte[] m_objectAttributeMemory;
    private readonly byte[] m_videoRam;
    private readonly uint[] m_framebuffer = new uint[ScreenWidth * ScreenHeight];
    // The front buffer: a stable snapshot of the most recently completed frame, latched from the back buffer at
    // vertical blank. Presenting from this (rather than the live back buffer the PPU paints into scanline by
    // scanline) means a host that stops the machine mid-frame never sees a torn, half-drawn picture.
    private readonly uint[] m_presentBuffer = new uint[ScreenWidth * ScreenHeight];
    // The raw previous frame, kept for frame blending (the LCD's slow pixel response visually merges consecutive
    // frames, so games that flicker rapidly to fake transparency or extra shades appear stable on hardware).
    private readonly uint[] m_previousFramebuffer = new uint[ScreenWidth * ScreenHeight];
    // The background/window color index (0-3) chosen per pixel on the current line, used for sprite-to-background
    // priority (a sprite with the priority bit set is hidden behind background colors 1-3).
    private readonly byte[] m_lineColorIndex = new byte[ScreenWidth];

    private bool m_coincidence;
    private bool m_enabled;
    private bool m_frameReady;
    private bool m_frameBlending = true;
    private bool m_hasPreviousFrame;
    // The OAM/VRAM CPU-access locks, split by direction because reads block a machine cycle before writes do.
    // The READ locks lead the polled STAT mode bits: they engage at the *actual* mode transition (OAM at the
    // mode-2 onset, VRAM at the mode-3 onset), one machine cycle before the STAT bits report the new mode. The
    // WRITE locks engage a machine cycle later, as the reported mode settles. All release together when the
    // reported mode settles back to horizontal blank. The OAM write lock additionally opens for a single cycle at
    // the mode-2/3 boundary. (OAM and VRAM each have separate read and write locks, with distinct engage/release
    // timing.)
    private bool m_oamReadBlocked;
    private bool m_oamWriteBlocked;
    private bool m_vramReadBlocked;
    private bool m_vramWriteBlocked;
    // The first line after the LCD is enabled skips OAM scan (mode 0 straight to mode 3), so it has no mode-2
    // onset to lead from: its access locks engage *with* the reported mode-3 bits rather than a machine cycle ahead.
    private bool m_firstLineAfterEnable;
    private bool m_statInterruptLine;
    private int m_dot;
    private int m_line;
    // The LY value the LY=LYC comparison actually uses. It goes briefly invalid (-1)
    // right after LY increments, so the coincidence reads "not equal" for a machine cycle before tracking the new
    // line — both for the polled STAT bit and for the STAT interrupt.
    private int m_lyForComparison = -1;
    private int m_lyForComparisonDelay;
    private int m_mode0StartDot = (OamScanDots + DrawingDots);
    private int m_reportedModeDelay;
    private int m_interruptModeDelay;
    private int m_windowLineCounter;

    // Per-dot mode-3 background/window pixel pipeline (Phase 1: the DMG background fetcher + FIFO; sprites are still
    // overlaid at line end, and the closed-form mode-3 length still drives timing). The fetcher runs one logical step
    // per two dots — fetch tile number, low byte, high byte, then push eight colour indices — and the FIFO shifts one
    // pixel out per dot, so mid-scanline writes to SCX/SCY/LCDC/BGP take effect partway across the line.
    private readonly byte[] m_bgFifo = new byte[16];
    private int m_bgFifoHead;
    private int m_bgFifoCount;
    private int m_fetchStep;        // 0-7: two dots each for tile number / low / high / push
    private int m_fetchTileX;       // tile column fetched so far (background or window space)
    private int m_fetchTileNumber;
    private int m_fetchLow;
    private int m_fetchHigh;
    private int m_pixelX;           // next screen X to output (0-160)
    private int m_scxDiscard;       // fine-scroll pixels still to drop from the FIFO front
    private bool m_fetchingWindow;
    private bool m_windowDrawnThisLine;
    private PpuMode m_mode = PpuMode.HorizontalBlank;
    private PpuMode m_reportedMode = PpuMode.HorizontalBlank;
    // The mode that drives the STAT interrupt, settled independently of the polled STAT bits (m_reportedMode) so
    // the mode-2 interrupt and the mode-0 transition have the right sub-cycle vernier.
    private PpuMode m_interruptMode = PpuMode.HorizontalBlank;
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

    /// <summary>Gets or sets whether consecutive frames are blended when latched, simulating the LCD's slow pixel
    /// response. Enabled by default: static images are unaffected (identical frames blend to themselves), but content
    /// that flickers every frame to fake transparency or extra shades resolves to the stable image hardware shows.</summary>
    public bool FrameBlendingEnabled {
        get => m_frameBlending;
        set => m_frameBlending = value;
    }

    /// <summary>Gets whether VRAM is readable by the CPU — true unless the PPU is drawing. The read lock engages at
    /// the actual mode-3 onset (a machine cycle ahead of the STAT mode-3 bits) and releases when the reported mode
    /// settles back to horizontal blank.</summary>
    public bool IsVideoRamAccessible =>
        (!m_enabled || !m_vramReadBlocked);
    /// <summary>Gets whether VRAM is writable by the CPU. The write lock engages a machine cycle after the read
    /// lock (as the reported mode settles to drawing) and releases with it.</summary>
    public bool IsVideoRamWritable =>
        (!m_enabled || !m_vramWriteBlocked);
    /// <summary>Gets whether OAM is readable by the CPU — true outside OAM scan and drawing. The read lock engages
    /// at the actual mode-2 (or, on the first line after LCD-on, mode-3) onset, a machine cycle ahead of the STAT
    /// bits, and releases when the reported mode settles back to horizontal blank.</summary>
    public bool IsObjectMemoryAccessible =>
        (!m_enabled || !m_oamReadBlocked);
    /// <summary>Gets whether OAM is writable by the CPU. The write lock engages a machine cycle after the read lock
    /// (as the reported mode settles), and briefly opens for one cycle at the mode-2/3 boundary.</summary>
    public bool IsObjectMemoryWritable =>
        (!m_enabled || !m_oamWriteBlocked);

    /// <summary>Initializes the PPU wired to the interrupt controller and the video RAM it fetches tiles and maps
    /// from. The PPU reads video RAM directly (it is the component that locks it from the CPU), so the access is
    /// not subject to the mode lock.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="memory">The shared system memory whose video RAM and object attribute memory the PPU scans
    /// (one 8&#160;KiB VRAM bank on the monochrome models, two on color).</param>
    /// <param name="configuration">The machine configuration; the color model renders with tile attributes and palette RAM.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Ppu(IInterruptController interrupts, SystemMemory memory, MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(configuration);

        m_interrupts = interrupts;
        m_objectAttributeMemory = memory.ObjectAttributeMemory;
        m_videoRam = memory.VideoRam;
        m_isColor = (configuration.Model == ConsoleModel.Cgb);
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

                switch (m_reportedMode) {
                    case PpuMode.HorizontalBlank:
                        // Every lock releases as the reported mode settles to horizontal blank — a machine cycle
                        // after the actual mode-0 transition, matching when the STAT bits report it.
                        m_oamReadBlocked = false;
                        m_oamWriteBlocked = false;
                        m_vramReadBlocked = false;
                        m_vramWriteBlocked = false;

                        break;
                    case PpuMode.OamScan:
                        // The OAM write lock trails the read lock by a machine cycle, engaging as mode 2 reports.
                        m_oamWriteBlocked = true;

                        break;
                    case PpuMode.Drawing:
                        // The write locks engage as mode 3 reports (a machine cycle behind the read locks).
                        m_oamWriteBlocked = true;
                        m_vramWriteBlocked = true;

                        // The first line after LCD-on has no mode-2 onset to lead from, so its read locks engage
                        // here, with the reported mode-3 bits, rather than at the actual transition.
                        if (m_firstLineAfterEnable) {
                            m_oamReadBlocked = true;
                            m_vramReadBlocked = true;
                        }

                        break;
                    default:
                        break;
                }

                // Re-evaluate the STAT line as the polled bits settle (the coincidence bit and the access edges may
                // shift it).
                UpdateStatInterrupt();
            }
        }

        // The mode-driven STAT interrupt settles on its own clock, independent of the polled STAT bits.
        if (m_interruptModeDelay > 0) {
            m_interruptModeDelay -= 1;

            if (m_interruptModeDelay == 0) {
                m_interruptMode = m_mode;
                UpdateStatInterrupt();
            }
        }

        // The LY=LYC comparison value tracks the new line a machine cycle after LY changed; recompute the latched
        // coincidence (and STAT line) when it settles.
        if (m_lyForComparisonDelay > 0) {
            m_lyForComparisonDelay -= 1;

            if (m_lyForComparisonDelay == 0) {
                m_lyForComparison = m_line;
                UpdateStatInterrupt();
            }
        }

        m_dot += 1;

        if (m_dot >= DotsPerLine) {
            m_dot = 0;
            AdvanceLine();
        }
        else if (m_line < ScreenHeight) {
            if (m_dot == OamScanDots) {
                // Mode 3 length is latched at the mode 2->3 transition: the base 172 dots, plus the SCX fine-scroll
                // discard, plus the per-object fetch penalty for the sprites on this line. HBlank absorbs it all to
                // keep the line 456 dots.
                m_mode0StartDot = (OamScanDots + DrawingDots + ScxPenalty(scrollX: (m_scrollX & 7)) + ObjectPenalty(line: m_line));
                SetMode(mode: PpuMode.Drawing);

                if (!m_isColor) {
                    StartBackgroundFetch();
                }
            }
            else if (m_dot == m_mode0StartDot) {
                RenderScanline();
                SetMode(mode: PpuMode.HorizontalBlank);
            }
            else if (!m_isColor && (m_mode == PpuMode.Drawing)) {
                // DMG background/window is produced one pixel per dot by the fetcher (sprites are overlaid at line end).
                StepBackgroundFetcher();
            }
        }
    }

    // DMG mode-3 lengthening from the fine X-scroll: +4 dots when SCX&7 is 1-4, +8 when 5-7 (the CGB differs and
    // is intentionally not matched here).
    private static int ScxPenalty(int scrollX) =>
        ((scrollX == 0)
            ? 0
            : ((scrollX <= 4) ? 4 : 8));

    // The per-object mode-3 penalty (the OBJ penalty algorithm). Each object the fetcher stalls for costs a
    // flat 6 dots; the first object whose leftmost pixel falls in a not-yet-considered background tile adds up to 5
    // more depending on where in the tile it lands (so overlapping objects in one tile pay the tile cost once). An
    // object parked at OAM X=0 always costs 11. Only matters while objects are enabled.
    private int ObjectPenalty(int line) {
        if ((m_lcdControl & 0x02) == 0) {
            return 0;
        }

        var spriteHeight = (((m_lcdControl & 0x04) != 0) ? 16 : 8);

        // Up to 10 objects covering this line, in OAM order.
        Span<int> selected = stackalloc int[10];
        var count = 0;

        for (var index = 0; (index < 40) && (count < 10); index += 1) {
            var objectY = (m_objectAttributeMemory[index * 4] - 16);

            if ((line >= objectY) && (line < (objectY + spriteHeight))) {
                selected[count] = index;
                count += 1;
            }
        }

        // The fetcher meets objects left to right, so cost them by ascending X (ties keep OAM order).
        for (var i = 1; i < count; i += 1) {
            var current = selected[i];
            var currentX = m_objectAttributeMemory[(current * 4) + 1];
            var j = (i - 1);

            while ((j >= 0) && (m_objectAttributeMemory[(selected[j] * 4) + 1] > currentX)) {
                selected[j + 1] = selected[j];
                j -= 1;
            }

            selected[j + 1] = current;
        }

        var scroll = (m_scrollX & 7);
        var penalty = 0;
        Span<int> consideredTiles = stackalloc int[10];
        var consideredCount = 0;

        for (var s = 0; s < count; s += 1) {
            var objectX = m_objectAttributeMemory[(selected[s] * 4) + 1];

            // Objects off the right edge (X >= 168, i.e. screen X >= 160) are never reached by the fetcher and cost
            // nothing. Off the left edge (X in 0..7, including the X=0 stall) they still are, so only the right side
            // is excluded. (On hardware, X=167 is the last X that lengthens mode 3; X=168 is the first that does not.)
            if (objectX >= 168) {
                continue;
            }

            var pixel = ((objectX - 8) + scroll);
            var tile = (pixel >> 3);
            var alreadyConsidered = false;

            for (var t = 0; t < consideredCount; t += 1) {
                if (consideredTiles[t] == tile) {
                    alreadyConsidered = true;

                    break;
                }
            }

            // The first object whose leftmost pixel lands in a not-yet-considered tile pays the tile-fetch cost:
            // the pixels of that tile strictly to its right, minus 2. An object at OAM X=0 always pays the full 5
            // here regardless of SCX. Overlapping objects in the same tile skip this and only pay the flat fetch.
            if (!alreadyConsidered) {
                consideredTiles[consideredCount] = tile;
                consideredCount += 1;
                penalty += ((objectX == 0) ? 5 : Math.Max(0, (5 - (pixel & 7))));
            }

            penalty += 6;
        }

        return penalty;
    }

    private void AdvanceLine() {
        m_line += 1;

        if (m_line > LastLine) {
            m_line = 0;
        }

        // Line 0's special post-LCD-on timing applies only to that first line; subsequent lines lead normally.
        m_firstLineAfterEnable = false;

        if (m_line == ScreenHeight) {
            // Entering vertical blank: the frame is complete and the vertical-blank interrupt fires. The finished
            // back buffer is latched into the front buffer so it can be presented whole while the next frame draws.
            // The window's internal line counter resets here, ready for the next frame.
            SetMode(mode: PpuMode.VerticalBlank);
            m_interrupts.Request(kind: InterruptKind.VBlank);
            PresentFrame();
            m_frameReady = true;
            m_windowLineCounter = 0;
        }
        else if (m_line < ScreenHeight) {
            SetMode(mode: PpuMode.OamScan);
        }

        // LY just changed: the comparison value goes invalid for a machine cycle, so the coincidence reads "not
        // equal" until it tracks the new line (the LCD-enable coincidence quirk).
        m_lyForComparison = -1;
        m_lyForComparisonDelay = LyComparisonDelayDots;

        // The line number changed even when the mode did not (lines 145-153), so refresh LY=LYC.
        UpdateStatInterrupt();
    }

    private void SetMode(PpuMode mode) {
        m_mode = mode;

        // The READ locks engage at the *actual* transition, one machine cycle ahead of the polled STAT bits: OAM at
        // the mode-2 onset, OAM+VRAM at the mode-3 onset. The first post-LCD-on line skips OAM scan, so it defers
        // its read locks to the reported settle (handled in TickDot). The WRITE locks trail by a machine cycle and
        // engage on the reported settle. Vertical blank frees everything.
        switch (mode) {
            case PpuMode.OamScan:
                m_oamReadBlocked = true;

                break;
            case PpuMode.Drawing:
                if (!m_firstLineAfterEnable) {
                    m_oamReadBlocked = true;
                    m_vramReadBlocked = true;
                }

                // The mode-2 OAM write lock releases at the actual mode-3 transition and the mode-3 OAM write lock
                // re-engages a machine cycle later (the reported settle), leaving a one-cycle OAM write window at
                // the boundary. VRAM is freely writable through mode 2, so only its mode-3 write lock matters.
                m_oamWriteBlocked = false;

                break;
            case PpuMode.VerticalBlank:
                m_oamReadBlocked = false;
                m_oamWriteBlocked = false;
                m_vramReadBlocked = false;
                m_vramWriteBlocked = false;

                break;
            default:
                break;
        }

        // The polled STAT mode bits and the mode-driven STAT interrupt each lag the raw transition, on independent
        // clocks (see the delay constants).
        m_reportedModeDelay = StatBitReportDelay;
        m_interruptModeDelay = InterruptReportDelay;
        UpdateStatInterrupt();
    }

    // Latches the finished back buffer into the front buffer, blending it with the previous frame when enabled.
    private void PresentFrame() {
        if (m_frameBlending && m_hasPreviousFrame) {
            for (var i = 0; i < m_framebuffer.Length; i += 1) {
                m_presentBuffer[i] = BlendPixels(a: m_framebuffer[i], b: m_previousFramebuffer[i]);
            }
        }
        else {
            Array.Copy(sourceArray: m_framebuffer, destinationArray: m_presentBuffer, length: m_framebuffer.Length);
        }

        if (m_frameBlending) {
            // Keep the raw (unblended) frame so the next blend mixes the two real frames, not a feedback of blends.
            Array.Copy(sourceArray: m_framebuffer, destinationArray: m_previousFramebuffer, length: m_framebuffer.Length);
            m_hasPreviousFrame = true;
        }
    }

    // A per-channel 50/50 average of two opaque RGBA pixels. Done per channel (not a masked bit-halve) so that two
    // identical frames average to themselves exactly — a static picture is left untouched, only flicker is smoothed.
    private static uint BlendPixels(uint a, uint b) {
        var r = (((a & 0xFFu) + (b & 0xFFu)) >> 1);
        var g = ((((a >> 8) & 0xFFu) + ((b >> 8) & 0xFFu)) >> 1);
        var blue = ((((a >> 16) & 0xFFu) + ((b >> 16) & 0xFFu)) >> 1);

        return (0xFF000000u | (blue << 16) | (g << 8) | r);
    }

    private void RenderScanline() {
        if (m_isColor) {
            // The CGB background/window is still drawn all at once (its per-dot fetcher is a later phase).
            RenderBackgroundAndWindowColor(line: m_line);
            RenderSpritesColor(line: m_line);
        }
        else {
            // The DMG background/window was already drawn per-dot by the fetcher; finalize it and overlay sprites.
            FinishBackgroundLine();
            RenderSprites(line: m_line);
        }
    }

    private void RenderBackgroundAndWindow(int line) {
        var rowBase = (line * ScreenWidth);

        // On the DMG, LCDC bit 0 disables both background and window, clearing the line to the lightest shade.
        if ((m_lcdControl & 0x01) == 0) {
            for (var x = 0; x < ScreenWidth; x += 1) {
                m_framebuffer[rowBase + x] = BackgroundColor(shade: 0);
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

            m_framebuffer[rowBase + x] = BackgroundColor(shade: shade);
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
        var usingObjectPalette1 = ((attributes & 0x10) != 0);
        var palette = (usingObjectPalette1 ? m_objectPalette1 : m_objectPalette0);
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

            m_framebuffer[rowBase + screenX] = ObjectColor(palette1: usingObjectPalette1, shade: shade);
        }
    }

    private byte ReadVideoRam(ushort address) =>
        m_videoRam[address - VideoRamBase];

    private void UpdateStatInterrupt() {
        // The coincidence bit is latched here (it is frozen while the LCD is off, since this is not called then),
        // so STAT reads return the held value rather than a fresh LY=LYC compare. The comparison uses the lagged
        // ly_for_comparison, so it reads "not equal" for a machine cycle right after LY increments.
        m_coincidence = (m_lyForComparison == m_lineCompare);
        // The STAT mode-select sources track the interrupt mode, settled on its own clock (independent of the
        // polled STAT bits). The OAM (mode 2) source also asserts at the start of vertical blank (line 144, a DMG
        // quirk).
        var oamSource = (((m_interruptMode == PpuMode.OamScan) || (m_line == ScreenHeight)) && ((m_statEnables & StatOamInterrupt) != 0));
        var line = (
            ((m_interruptMode == PpuMode.HorizontalBlank) && ((m_statEnables & StatHBlankInterrupt) != 0)) ||
            ((m_interruptMode == PpuMode.VerticalBlank) && ((m_statEnables & StatVBlankInterrupt) != 0)) ||
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
        // LY advances slightly earlier than a normal 456-dot line. (This matches the hardware's first-frame LY and
        // STAT-mode timing after enable; the exact coincidence-bit and OAM/VRAM-access edges of that first line
        // remain an approximation.)
        m_dot = (enabling ? 4 : 0);
        m_line = 0;

        // The LCD-enable first-frame quirk: line 0 has NO OAM-scan phase — it begins directly in mode 0, then
        // mode 3 at dot 80, then mode 0 (so OAM and VRAM stay accessible until mode 3). Lines 1+ run normally.
        // Turning the LCD off also parks it in mode 0 with LY=0. The reported mode is set immediately (no lag).
        m_mode = PpuMode.HorizontalBlank;
        m_reportedMode = PpuMode.HorizontalBlank;
        m_reportedModeDelay = 0;
        m_interruptMode = PpuMode.HorizontalBlank;
        m_interruptModeDelay = 0;
        m_windowLineCounter = 0;

        // The first post-LCD-on line begins in horizontal blank, so OAM and VRAM start accessible; the locks engage
        // when that line reaches mode 3. Turning the LCD off likewise leaves everything accessible.
        m_oamReadBlocked = false;
        m_oamWriteBlocked = false;
        m_vramReadBlocked = false;
        m_vramWriteBlocked = false;
        m_firstLineAfterEnable = enabling;

        // On re-enable, recompute the STAT line for LY=0 and fire only on a genuine rising edge carried from the
        // value held while the LCD was off — resetting it to false would fabricate a spurious edge. On disable,
        // the STAT line is frozen at its last value (writes skip recomputation while off).
        if (enabling) {
            // Enabling sets LY directly rather than incrementing it, so the comparison is valid immediately.
            m_lyForComparison = m_line;
            m_lyForComparisonDelay = 0;
            UpdateStatInterrupt();
        }
    }
}
