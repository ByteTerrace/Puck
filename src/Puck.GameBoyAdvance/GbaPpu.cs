namespace Puck.GameBoyAdvance;

/// <summary>
/// The default PPU: raster timing plus the bitmap background modes (3/4/5). Each scanline spans 1232 master
/// cycles (240 visible dots + 68 H-blank dots) and a frame spans 228 scanlines (160 visible + 68 V-blank). The
/// tiled modes (0–2) and the sprite/window/blend layers are not yet rendered — those scanlines show the
/// backdrop — but the timing, interrupts, and DMA triggers are fully in place.
/// </summary>
public sealed class GbaPpu : IGbaPpu {
    private const int ScreenWidth = 240;
    private const int ScreenHeight = 160;
    private const int DotsPerLine = 1232;
    // The H-Blank flag is raised after the H-draw period — 1008 cycles, not the 960-cycle visible-pixel span —
    // so the flag stays set for 224 cycles per line, matching hardware (and the AGS hblank_status test).
    private const int HDrawLength = 1008;
    private const int TotalLines = 228;

    private const int HBlankLength = DotsPerLine - HDrawLength; // 224

    private readonly GbaScheduler m_scheduler;
    private readonly GbaScheduler.Event m_event;
    private readonly IGbaInterruptController m_interrupts;
    private readonly byte[] m_palette = new byte[0x400];
    private readonly byte[] m_vram = new byte[0x18000];
    private readonly byte[] m_oam = new byte[0x400];
    private readonly ushort[] m_registers = new ushort[0x30];
    private readonly uint[] m_framebuffer = new uint[ScreenWidth * ScreenHeight];

    // Per-layer scanline buffers (15-bit BGR555 colour, or -1 transparent) feeding the window/priority/blend
    // compositor: one per background plus the sprite layer, with the sprite's priority, semi-transparent flag,
    // and object-window mask alongside.
    private readonly int[][] m_backgroundLine = { new int[ScreenWidth], new int[ScreenWidth], new int[ScreenWidth], new int[ScreenWidth] };
    private readonly int[] m_spriteLine = new int[ScreenWidth];
    private readonly int[] m_spritePriority = new int[ScreenWidth];
    private readonly bool[] m_spriteSemiTransparent = new bool[ScreenWidth];
    private readonly bool[] m_spriteWindow = new bool[ScreenWidth];

    // Internal affine reference points (BG2, BG3), latched from BG2X/Y and BG3X/Y at the start of each frame.
    private readonly int[] m_affineRefX = new int[2];
    private readonly int[] m_affineRefY = new int[2];

    private int m_line;
    private bool m_inHBlank;
    private ushort m_dispStatControl;
    private bool m_hblankFlag;
    private bool m_vblankStarted;
    private bool m_hblankStarted;
    private bool m_videoCaptureStarted;
    private bool m_videoCaptureEnded;

    /// <summary>Creates the PPU bound to the interrupt controller it raises display interrupts through.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public GbaPpu(GbaScheduler scheduler, IGbaInterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(interrupts);

        m_scheduler = scheduler;
        m_interrupts = interrupts;

        // The raster runs as scheduled events: from the start of each scanline the next event is H-Blank (after
        // the H-draw period), and from H-Blank the next is the following scanline. The first fires at H-draw end.
        m_event = new GbaScheduler.Event { Callback = RunEvent };
        m_scheduler.Schedule(e: m_event, cyclesFromNow: HDrawLength);
    }

    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer => m_framebuffer;

    private int BackgroundMode => m_registers[0] & 0x7;

    private bool ForcedBlank => (m_registers[0] & 0x80) != 0;

    /// <inheritdoc/>
    // The scheduled raster event: alternately enters H-Blank (at H-draw end) and advances to the next scanline
    // (at line end), rescheduling itself for the next transition. cyclesLate keeps the cadence drift-free.
    private void RunEvent(int cyclesLate) {
        if (!m_inHBlank) {
            EnterHBlank();
            m_inHBlank = true;
            m_scheduler.Schedule(e: m_event, cyclesFromNow: HBlankLength - cyclesLate);
        }
        else {
            NextScanline();
            m_inHBlank = false;
            m_scheduler.Schedule(e: m_event, cyclesFromNow: HDrawLength - cyclesLate);
        }
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        if (offset == 0x04u) {
            // The V-Blank flag is set on lines 160–226 and cleared again on the final line (227), a hardware
            // quirk the AGS vblank_status test checks explicitly.
            return (ushort)((((m_line >= ScreenHeight) && (m_line < (TotalLines - 1))) ? 0x1u : 0u)
                | (m_hblankFlag ? 0x2u : 0u)
                | ((m_line == (m_dispStatControl >> 8)) ? 0x4u : 0u)
                | m_dispStatControl);
        }

        if (offset == 0x06u) {
            return (ushort)m_line;
        }

        return m_registers[offset >> 1];
    }

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        switch (offset) {
            case 0x04u:
                // Only the interrupt-enable bits (3–5) and the V-count target (8–15) are writable.
                m_dispStatControl = (ushort)(value & 0xFF38u);

                break;
            case 0x06u:
                // VCOUNT is read-only.
                break;
            default:
                m_registers[offset >> 1] = value;

                break;
        }
    }

    /// <inheritdoc/>
    public uint ReadVideo(uint address, int width) => (address >> 24) switch {
        0x5u => ReadArray(array: m_palette, index: address & 0x3FFu, width: width),
        0x6u => ReadArray(array: m_vram, index: VramOffset(address: address), width: width),
        _ => ReadArray(array: m_oam, index: address & 0x3FFu, width: width),
    };

    /// <inheritdoc/>
    public void WriteVideo(uint address, int width, uint value) {
        switch (address >> 24) {
            case 0x5u:
                if (width == 1) {
                    WriteDuplicatedByte(array: m_palette, index: (address & 0x3FFu) & ~1u, value: (byte)value);
                }
                else {
                    WriteArray(array: m_palette, index: address & 0x3FFu, width: width, value: value);
                }

                break;
            case 0x6u:
                WriteVram(address: address, width: width, value: value);

                break;
            default:
                // 8-bit writes to OAM are dropped.
                if (width != 1) {
                    WriteArray(array: m_oam, index: address & 0x3FFu, width: width, value: value);
                }

                break;
        }
    }

    /// <inheritdoc/>
    public bool ConsumeVBlankStarted() {
        var started = m_vblankStarted;

        m_vblankStarted = false;

        return started;
    }

    /// <inheritdoc/>
    public bool ConsumeHBlankStarted() {
        var started = m_hblankStarted;

        m_hblankStarted = false;

        return started;
    }

    /// <inheritdoc/>
    public bool ConsumeVideoCaptureStarted() {
        var started = m_videoCaptureStarted;

        m_videoCaptureStarted = false;

        return started;
    }

    /// <inheritdoc/>
    public bool ConsumeVideoCaptureEnded() {
        var ended = m_videoCaptureEnded;

        m_videoCaptureEnded = false;

        return ended;
    }

    private void EnterHBlank() {
        m_hblankFlag = true;

        // The H-Blank interrupt fires on every scanline, including the V-Blank lines. The H-Blank DMA trigger and
        // scanline rendering, by contrast, happen only on the visible lines (no H-Blank DMA during V-Blank).
        if ((m_dispStatControl & 0x10) != 0) {
            m_interrupts.Request(source: InterruptSource.HBlank);
        }

        if (m_line < ScreenHeight) {
            RenderScanline(line: m_line);

            m_hblankStarted = true;
        }

        // DMA3 video-capture timing fires on the H-blank of scanlines 2 through 161 (the visible lines plus the
        // first two V-blank lines), then the window ends at line 162.
        if ((m_line >= 2) && (m_line <= 161)) {
            m_videoCaptureStarted = true;
        }
    }

    private void NextScanline() {
        m_hblankFlag = false;

        if (++m_line >= TotalLines) {
            m_line = 0;

            // Affine reference points are reloaded from their registers at the start of each frame.
            m_affineRefX[0] = ReadReferencePoint(lowIndex: 0x14); // BG2X
            m_affineRefY[0] = ReadReferencePoint(lowIndex: 0x16); // BG2Y
            m_affineRefX[1] = ReadReferencePoint(lowIndex: 0x1C); // BG3X
            m_affineRefY[1] = ReadReferencePoint(lowIndex: 0x1E); // BG3Y
        }

        if (m_line == ScreenHeight) {
            m_vblankStarted = true;

            if ((m_dispStatControl & 0x8) != 0) {
                m_interrupts.Request(source: InterruptSource.VBlank);
            }
        }

        // Reaching line 162 closes the video-capture window: a running video-capture DMA is stopped.
        if (m_line == 162) {
            m_videoCaptureEnded = true;
        }

        if ((m_line == (m_dispStatControl >> 8)) && ((m_dispStatControl & 0x20) != 0)) {
            m_interrupts.Request(source: InterruptSource.VCounter);
        }
    }

    private void RenderScanline(int line) {
        var rowBase = line * ScreenWidth;

        if (ForcedBlank) {
            m_framebuffer.AsSpan(start: rowBase, length: ScreenWidth).Fill(value: 0xFFFFFFFFu);

            return;
        }

        var mode = BackgroundMode;
        var activeBackgrounds = 0;

        switch (mode) {
            case 0:
            case 1:
            case 2:
                for (var background = 0; background < 4; ++background) {
                    if (!BackgroundUsable(mode: mode, background: background) || ((m_registers[0] & (0x100u << background)) == 0u)) {
                        continue;
                    }

                    if (IsAffineBackground(mode: mode, background: background)) {
                        RenderAffineBackground(background: background, line: line);
                    }
                    else {
                        RenderTextBackground(background: background, line: line);
                    }

                    activeBackgrounds |= 1 << background;
                }

                break;
            case 3:
                RenderBitmapMode3(line: line);
                activeBackgrounds = BitmapBackgroundActive();

                break;
            case 4:
                RenderBitmapMode4(line: line);
                activeBackgrounds = BitmapBackgroundActive();

                break;
            case 5:
                RenderBitmapMode5(line: line);
                activeBackgrounds = BitmapBackgroundActive();

                break;
            default:
                m_framebuffer.AsSpan(start: rowBase, length: ScreenWidth).Fill(value: Color(bgr555: PaletteColor(index: 0)));

                return;
        }

        var objectsEnabled = (m_registers[0] & 0x1000u) != 0u;

        if (objectsEnabled) {
            RenderSprites(line: line);
        }
        else {
            Array.Fill(array: m_spriteLine, value: -1);
            Array.Clear(array: m_spriteWindow);
        }

        Composite(line: line, rowBase: rowBase, activeBackgrounds: activeBackgrounds, objectsEnabled: objectsEnabled);
    }

    // Resolves each pixel: window region → which layers are visible, priority → the top two layers, then the
    // BLDCNT special effect (alpha / brighten / darken) between them.
    private void Composite(int line, int rowBase, int activeBackgrounds, bool objectsEnabled) {
        var windowsActive = (m_registers[0] & 0xE000u) != 0u; // any of WIN0/WIN1/OBJ-window enabled
        var blendControl = m_registers[0x28]; // BLDCNT (0x50)
        var effect = (blendControl >> 6) & 0x3;
        var firstTargets = blendControl & 0x3Fu;
        var secondTargets = (blendControl >> 8) & 0x3Fu;

        for (var x = 0; x < ScreenWidth; ++x) {
            var enableMask = windowsActive ? WindowMaskAt(x: x, line: line) : 0x3Fu;

            // Find the top two visible, opaque layers by (priority, then OBJ-before-BG, then BG number); the
            // backdrop sits beneath everything (priority 5, id 5).
            var backdrop = (int)PaletteColor(index: 0);
            var top = new Layer(Color: backdrop, Priority: 5, Order: 6, Id: 5);
            var second = new Layer(Color: backdrop, Priority: 5, Order: 6, Id: 5);

            if (objectsEnabled && (m_spriteLine[x] >= 0) && ((enableMask & 0x10u) != 0u)) {
                Consider(color: m_spriteLine[x], priority: m_spritePriority[x], order: 0, id: 4, top: ref top, second: ref second);
            }

            for (var background = 0; background < 4; ++background) {
                if ((((activeBackgrounds >> background) & 1) == 0) || (((enableMask >> background) & 1u) == 0u)) {
                    continue;
                }

                var color = m_backgroundLine[background][x];

                if (color >= 0) {
                    Consider(color: color, priority: m_registers[4 + background] & 0x3, order: background + 1, id: background, top: ref top, second: ref second);
                }
            }

            var result = top.Color;
            var effectsAllowed = (enableMask & 0x20u) != 0u;
            var semiTransparentTop = (top.Id == 4) && m_spriteSemiTransparent[x];

            if (effectsAllowed) {
                var topIsFirst = (firstTargets & (1u << top.Id)) != 0u;
                var secondIsTarget = (secondTargets & (1u << second.Id)) != 0u;

                if (semiTransparentTop && secondIsTarget) {
                    result = AlphaBlend(first: top.Color, second: second.Color);
                }
                else if (topIsFirst) {
                    if ((effect == 1) && secondIsTarget) {
                        result = AlphaBlend(first: top.Color, second: second.Color);
                    }
                    else if (effect == 2) {
                        result = Brighten(color: top.Color);
                    }
                    else if (effect == 3) {
                        result = Darken(color: top.Color);
                    }
                }
            }

            m_framebuffer[rowBase + x] = Color(bgr555: (ushort)result);
        }
    }

    private static void Consider(int color, int priority, int order, int id, ref Layer top, ref Layer second) {
        if ((priority < top.Priority) || ((priority == top.Priority) && (order < top.Order))) {
            second = top;
            top = new Layer(Color: color, Priority: priority, Order: order, Id: id);
        }
        else if ((priority < second.Priority) || ((priority == second.Priority) && (order < second.Order))) {
            second = new Layer(Color: color, Priority: priority, Order: order, Id: id);
        }
    }

    private readonly record struct Layer(int Color, int Priority, int Order, int Id);

    private uint WindowMaskAt(int x, int line) {
        if (((m_registers[0] & 0x2000u) != 0u) && InWindow(horizontalRegister: 0x20, verticalRegister: 0x22, x: x, line: line)) {
            return (uint)m_registers[0x24] & 0x3Fu; // WININ low byte (WIN0)
        }

        if (((m_registers[0] & 0x4000u) != 0u) && InWindow(horizontalRegister: 0x21, verticalRegister: 0x23, x: x, line: line)) {
            return ((uint)m_registers[0x24] >> 8) & 0x3Fu; // WININ high byte (WIN1)
        }

        if (((m_registers[0] & 0x8000u) != 0u) && m_spriteWindow[x]) {
            return ((uint)m_registers[0x25] >> 8) & 0x3Fu; // WINOUT high byte (object window)
        }

        return (uint)m_registers[0x25] & 0x3Fu; // WINOUT low byte (outside)
    }

    private bool InWindow(int horizontalRegister, int verticalRegister, int x, int line) {
        var horizontal = m_registers[horizontalRegister];
        var vertical = m_registers[verticalRegister];
        var left = (horizontal >> 8) & 0xFF;
        var right = horizontal & 0xFF;
        var top = (vertical >> 8) & 0xFF;
        var bottom = vertical & 0xFF;

        var insideX = (left <= right) ? ((x >= left) && (x < right)) : ((x >= left) || (x < right));
        var insideY = (top <= bottom) ? ((line >= top) && (line < bottom)) : ((line >= top) || (line < bottom));

        return insideX && insideY;
    }

    private int AlphaBlend(int first, int second) {
        var eva = Math.Min(16, m_registers[0x29] & 0x1F);        // BLDALPHA EVA
        var evb = Math.Min(16, (m_registers[0x29] >> 8) & 0x1F); // BLDALPHA EVB

        var r = Math.Min(31, (((first & 0x1F) * eva) + ((second & 0x1F) * evb)) >> 4);
        var g = Math.Min(31, ((((first >> 5) & 0x1F) * eva) + (((second >> 5) & 0x1F) * evb)) >> 4);
        var b = Math.Min(31, ((((first >> 10) & 0x1F) * eva) + (((second >> 10) & 0x1F) * evb)) >> 4);

        return r | (g << 5) | (b << 10);
    }

    private int Brighten(int color) {
        var evy = Math.Min(16, m_registers[0x2A] & 0x1F); // BLDY
        var r = (color & 0x1F) + (((31 - (color & 0x1F)) * evy) >> 4);
        var g = ((color >> 5) & 0x1F) + (((31 - ((color >> 5) & 0x1F)) * evy) >> 4);
        var b = ((color >> 10) & 0x1F) + (((31 - ((color >> 10) & 0x1F)) * evy) >> 4);

        return r | (g << 5) | (b << 10);
    }

    private int Darken(int color) {
        var evy = Math.Min(16, m_registers[0x2A] & 0x1F); // BLDY
        var r = (color & 0x1F) - (((color & 0x1F) * evy) >> 4);
        var g = ((color >> 5) & 0x1F) - ((((color >> 5) & 0x1F) * evy) >> 4);
        var b = ((color >> 10) & 0x1F) - ((((color >> 10) & 0x1F) * evy) >> 4);

        return r | (g << 5) | (b << 10);
    }

    private int BitmapBackgroundActive() => ((m_registers[0] & 0x400u) != 0u) ? (1 << 2) : 0;

    private void RenderSprites(int line) {
        Array.Fill(array: m_spriteLine, value: -1);
        Array.Clear(array: m_spriteWindow);

        var oneDimensional = (m_registers[0] & 0x40u) != 0u;

        for (var sprite = 0; sprite < 128; ++sprite) {
            var attributeBase = (uint)(sprite * 8);
            var attr0 = Oam16(offset: attributeBase);
            var attr1 = Oam16(offset: attributeBase + 2u);
            var attr2 = Oam16(offset: attributeBase + 4u);
            var affine = (attr0 & 0x100) != 0;
            var objectMode = (attr0 >> 10) & 0x3;

            if ((!affine && ((attr0 & 0x200) != 0)) || (objectMode == 3)) {
                // Disabled, or a prohibited mode.
                continue;
            }

            var windowSprite = objectMode == 2;
            var is8Bpp = (attr0 & 0x2000) != 0;
            var (width, height) = SpriteSize(shape: (attr0 >> 14) & 0x3, size: (attr1 >> 14) & 0x3);
            var doubleSize = affine && ((attr0 & 0x200) != 0);
            var boxWidth = doubleSize ? (width * 2) : width;
            var boxHeight = doubleSize ? (height * 2) : height;
            var rowInBox = (line - (attr0 & 0xFF)) & 0xFF;

            if (rowInBox >= boxHeight) {
                continue;
            }

            // OBJ mosaic (attr0 bit 12): the MOSAIC register's bits 8-11 (H) and 12-15 (V) quantise the sprite's
            // sampled coordinates into blocks, snapped to the sprite's origin.
            var objMosaic = (attr0 & 0x1000) != 0;
            var objMosaicX = objMosaic ? (((m_registers[0x26] >> 8) & 0xF) + 1) : 1;
            var objMosaicY = objMosaic ? (((m_registers[0x26] >> 12) & 0xF) + 1) : 1;
            var sampleRow = objMosaic ? (rowInBox - (rowInBox % objMosaicY)) : rowInBox;

            var x = attr1 & 0x1FF;
            var priority = (attr2 >> 10) & 0x3;
            var tileBase = (uint)(attr2 & 0x3FF);
            var paletteBank = (attr2 >> 12) & 0xF;
            var flipX = !affine && ((attr1 & 0x1000) != 0);
            var flipY = !affine && ((attr1 & 0x2000) != 0);

            short pa = 0x100, pb = 0, pc = 0, pd = 0x100;

            if (affine) {
                var group = (uint)((attr1 >> 9) & 0x1F) * 32u;

                pa = (short)Oam16(offset: group + 6u);
                pb = (short)Oam16(offset: group + 14u);
                pc = (short)Oam16(offset: group + 22u);
                pd = (short)Oam16(offset: group + 30u);
            }

            var halfBoxWidth = boxWidth / 2;
            var halfBoxHeight = boxHeight / 2;

            for (var column = 0; column < boxWidth; ++column) {
                var screenX = (x + column) & 0x1FF;

                if (screenX >= ScreenWidth) {
                    continue;
                }

                // A colour pixel already written by a lower-numbered sprite wins; window sprites only OR in.
                if (!windowSprite && (m_spriteLine[screenX] >= 0)) {
                    continue;
                }

                int texelX;
                int texelY;

                // Snap the sampled column to the OBJ mosaic grid (the row was snapped above).
                var sampleColumn = objMosaic ? (column - (column % objMosaicX)) : column;

                if (affine) {
                    var dx = sampleColumn - halfBoxWidth;
                    var dy = sampleRow - halfBoxHeight;

                    texelX = (((pa * dx) + (pb * dy)) >> 8) + (width / 2);
                    texelY = (((pc * dx) + (pd * dy)) >> 8) + (height / 2);
                }
                else {
                    texelX = flipX ? (width - 1 - sampleColumn) : sampleColumn;
                    texelY = flipY ? (height - 1 - sampleRow) : sampleRow;
                }

                if ((texelX < 0) || (texelX >= width) || (texelY < 0) || (texelY >= height)) {
                    continue;
                }

                var colorIndex = FetchSpritePixel(texelX: texelX, texelY: texelY, width: width, tileBase: tileBase, is8Bpp: is8Bpp, paletteBank: paletteBank, oneDimensional: oneDimensional);

                if (colorIndex < 0) {
                    continue;
                }

                if (windowSprite) {
                    m_spriteWindow[screenX] = true;
                }
                else {
                    m_spriteLine[screenX] = PaletteColor(index: 256 + colorIndex);
                    m_spritePriority[screenX] = priority;
                    m_spriteSemiTransparent[screenX] = objectMode == 1;
                }
            }
        }
    }

    private int FetchSpritePixel(int texelX, int texelY, int width, uint tileBase, bool is8Bpp, int paletteBank, bool oneDimensional) {
        var tilesWide = width >> 3;
        var units = is8Bpp ? 2u : 1u;
        var tileNumber = oneDimensional
            ? tileBase + ((uint)(((texelY >> 3) * tilesWide) + (texelX >> 3)) * units)
            : tileBase + (uint)((texelY >> 3) * 32) + ((uint)(texelX >> 3) * units);

        tileNumber &= 0x3FF;

        // Object tiles live in the upper half of VRAM (0x10000+).
        var address = 0x10000u + (tileNumber * 32u);

        if (is8Bpp) {
            address += (uint)(((texelY & 7) * 8) + (texelX & 7));

            if (address >= 0x18000u) {
                return -1;
            }

            int index = m_vram[address];

            return (index == 0) ? -1 : index;
        }

        address += (uint)(((texelY & 7) * 4) + ((texelX & 7) >> 1));

        if (address >= 0x18000u) {
            return -1;
        }

        var packed = m_vram[address];
        var nibble = ((texelX & 1) != 0) ? (packed >> 4) : (packed & 0xF);

        return (nibble == 0) ? -1 : ((paletteBank * 16) + nibble);
    }

    private static (int Width, int Height) SpriteSize(int shape, int size) => shape switch {
        1 => size switch { 0 => (16, 8), 1 => (32, 8), 2 => (32, 16), _ => (64, 32) },
        2 => size switch { 0 => (8, 16), 1 => (8, 32), 2 => (16, 32), _ => (32, 64) },
        _ => size switch { 0 => (8, 8), 1 => (16, 16), 2 => (32, 32), _ => (64, 64) },
    };

    private ushort Oam16(uint offset) => (ushort)(m_oam[offset] | (m_oam[offset + 1u] << 8));

    private void RenderTextBackground(int background, int line) {
        var dest = m_backgroundLine[background];

        Array.Fill(array: dest, value: -1);

        var control = m_registers[4 + background];
        var charBase = (uint)((control >> 2) & 0x3) * 0x4000u;
        var screenBase = (uint)((control >> 8) & 0x1F) * 0x800u;
        var is8Bpp = (control & 0x80) != 0;
        var size = (control >> 14) & 0x3;
        var horizontalOffset = m_registers[8 + (background * 2)] & 0x1FF;
        var verticalOffset = m_registers[9 + (background * 2)] & 0x1FF;
        var widthMask = ((size == 0) || (size == 2)) ? 0xFF : 0x1FF;
        var heightMask = ((size == 0) || (size == 1)) ? 0xFF : 0x1FF;
        var mosaic = (control & 0x40) != 0;
        var mosaicX = mosaic ? ((m_registers[0x26] & 0xF) + 1) : 1;
        var mosaicY = mosaic ? (((m_registers[0x26] >> 4) & 0xF) + 1) : 1;

        var y = ((line - (line % mosaicY)) + verticalOffset) & heightMask;
        var tileY = y >> 3;
        var inTileY = y & 7;

        for (var x = 0; x < ScreenWidth; ++x) {
            var px = ((x - (x % mosaicX)) + horizontalOffset) & widthMask;
            var entry = Vram16(offset: screenBase + MapEntryOffset(tileX: px >> 3, tileY: tileY, size: size));
            var tileNumber = (uint)(entry & 0x3FF);
            var flipX = (entry & 0x400) != 0;
            var flipY = (entry & 0x800) != 0;
            var tx = flipX ? (7 - (px & 7)) : (px & 7);
            var ty = flipY ? (7 - inTileY) : inTileY;
            int colorIndex;

            if (is8Bpp) {
                var address = charBase + (tileNumber * 64u) + (uint)((ty * 8) + tx);

                if (address >= 0x18000u) {
                    continue;
                }

                colorIndex = m_vram[address];
            }
            else {
                var address = charBase + (tileNumber * 32u) + (uint)((ty * 4) + (tx >> 1));

                if (address >= 0x18000u) {
                    continue;
                }

                var packed = m_vram[address];
                var nibble = ((tx & 1) != 0) ? (packed >> 4) : (packed & 0xF);

                if (nibble == 0) {
                    continue;
                }

                colorIndex = (((entry >> 12) & 0xF) * 16) + nibble;
            }

            if (colorIndex == 0) {
                continue;
            }

            dest[x] = PaletteColor(index: colorIndex);
        }
    }

    private void RenderAffineBackground(int background, int line) {
        var dest = m_backgroundLine[background];

        Array.Fill(array: dest, value: -1);

        var index = background - 2;
        var control = m_registers[4 + background];
        var charBase = (uint)((control >> 2) & 0x3) * 0x4000u;
        var screenBase = (uint)((control >> 8) & 0x1F) * 0x800u;
        var wrap = (control & 0x2000) != 0;
        var mapPixels = 128 << ((control >> 14) & 0x3);
        var tilesWide = mapPixels >> 3;
        var registerBase = (background == 2) ? 0x10 : 0x18;
        var pa = (short)m_registers[registerBase];
        var pb = (short)m_registers[registerBase + 1];
        var pc = (short)m_registers[registerBase + 2];
        var pd = (short)m_registers[registerBase + 3];
        var mosaic = (control & 0x40) != 0;
        var mosaicX = mosaic ? ((m_registers[0x26] & 0xF) + 1) : 1;
        var mosaicY = mosaic ? (((m_registers[0x26] >> 4) & 0xF) + 1) : 1;
        var mosaicLine = line - (line % mosaicY);
        var startX = m_affineRefX[index] + (pb * mosaicLine);
        var startY = m_affineRefY[index] + (pd * mosaicLine);

        for (var x = 0; x < ScreenWidth; ++x) {
            var sampleX = x - (x % mosaicX);
            var texX = (startX + (pa * sampleX)) >> 8;
            var texY = (startY + (pc * sampleX)) >> 8;

            if (wrap) {
                texX &= mapPixels - 1;
                texY &= mapPixels - 1;
            }
            else if ((texX < 0) || (texX >= mapPixels) || (texY < 0) || (texY >= mapPixels)) {
                continue;
            }

            var mapAddress = screenBase + (uint)(((texY >> 3) * tilesWide) + (texX >> 3));

            if (mapAddress >= 0x18000u) {
                continue;
            }

            // Affine maps are one byte per tile (tile number only) and the tiles are always 8bpp.
            var pixelAddress = charBase + ((uint)m_vram[mapAddress] * 64u) + (uint)(((texY & 7) * 8) + (texX & 7));

            if (pixelAddress >= 0x18000u) {
                continue;
            }

            var colorIndex = m_vram[pixelAddress];

            if (colorIndex == 0) {
                continue;
            }

            dest[x] = PaletteColor(index: colorIndex);
        }
    }

    private int ReadReferencePoint(int lowIndex) {
        var raw = m_registers[lowIndex] | (m_registers[lowIndex + 1] << 16);

        // The reference point is a signed 28-bit fixed-point value.
        return ((raw & 0x08000000) != 0)
            ? (raw | unchecked((int)0xF0000000))
            : raw;
    }

    private static bool BackgroundUsable(int mode, int background) => mode switch {
        0 => true,
        1 => background <= 2,
        _ => background >= 2,
    };

    private static bool IsAffineBackground(int mode, int background) => mode switch {
        1 => background == 2,
        2 => background >= 2,
        _ => false,
    };

    // Byte offset of a tilemap entry within VRAM, accounting for how the 32×32-tile screenblocks tile the four
    // background sizes (256×256, 512×256, 256×512, 512×512).
    private static uint MapEntryOffset(int tileX, int tileY, int size) {
        var block = size switch {
            1 => (tileX >= 32) ? 1u : 0u,
            2 => (tileY >= 32) ? 1u : 0u,
            3 => ((tileY >= 32) ? 2u : 0u) + ((tileX >= 32) ? 1u : 0u),
            _ => 0u,
        };

        return (block * 0x800u) + (uint)((((tileY & 31) * 32) + (tileX & 31)) * 2);
    }

    private ushort Vram16(uint offset) => (ushort)(m_vram[offset] | (m_vram[offset + 1u] << 8));

    private ushort PaletteColor(int index) => (ushort)(m_palette[index * 2] | (m_palette[(index * 2) + 1] << 8));

    private void RenderBitmapMode3(int line) {
        var dest = m_backgroundLine[2];
        var source = line * ScreenWidth * 2;

        for (var x = 0; x < ScreenWidth; ++x) {
            dest[x] = m_vram[source] | (m_vram[source + 1] << 8);
            source += 2;
        }
    }

    private void RenderBitmapMode4(int line) {
        // The 8-bit paletted bitmap is double-buffered; DISPCNT bit 4 selects the visible page. Index 0 is transparent.
        var dest = m_backgroundLine[2];
        var page = ((m_registers[0] & 0x10) != 0) ? 0xA000 : 0x0000;
        var source = page + (line * ScreenWidth);

        for (var x = 0; x < ScreenWidth; ++x) {
            var index = m_vram[source + x];

            dest[x] = (index == 0)
                ? -1
                : PaletteColor(index: index);
        }
    }

    private void RenderBitmapMode5(int line) {
        // A 160×128 16-bit bitmap, also double-buffered; outside that area is transparent (shows the backdrop).
        var dest = m_backgroundLine[2];

        Array.Fill(array: dest, value: -1);

        if (line >= 128) {
            return;
        }

        var page = ((m_registers[0] & 0x10) != 0) ? 0xA000 : 0x0000;
        var source = page + (line * 160 * 2);

        for (var x = 0; x < 160; ++x) {
            dest[x] = m_vram[source] | (m_vram[source + 1] << 8);
            source += 2;
        }
    }

    private void WriteVram(uint address, int width, uint value) {
        var offset = VramOffset(address: address);

        if (width == 1) {
            // An 8-bit VRAM write duplicates the byte across a halfword in the background region and is dropped
            // in the object region (boundary 0x14000 in bitmap modes, 0x10000 in tiled modes).
            var objectBase = (BackgroundMode >= 3) ? 0x14000u : 0x10000u;

            if (offset >= objectBase) {
                return;
            }

            WriteDuplicatedByte(array: m_vram, index: offset & ~1u, value: (byte)value);

            return;
        }

        WriteArray(array: m_vram, index: offset, width: width, value: value);
    }

    private static uint VramOffset(uint address) {
        var offset = address & 0x1FFFFu;

        return (offset >= 0x18000u)
            ? (offset - 0x8000u)
            : offset;
    }

    private static uint Color(ushort bgr555) {
        var r = bgr555 & 0x1F;
        var g = (bgr555 >> 5) & 0x1F;
        var b = (bgr555 >> 10) & 0x1F;

        // Pack as RGBA byte order in memory (red in the low byte), matching the DMG/CGB PPU and the PNG encoder.
        return 0xFF000000u
            | ((uint)((b << 3) | (b >> 2)) << 16)
            | ((uint)((g << 3) | (g >> 2)) << 8)
            | (uint)((r << 3) | (r >> 2));
    }

    private static void WriteDuplicatedByte(byte[] array, uint index, byte value) {
        array[index] = value;
        array[index + 1u] = value;
    }

    private static uint ReadArray(byte[] array, uint index, int width) {
        return width switch {
            1 => array[index],
            2 => (uint)(array[index] | (array[index + 1u] << 8)),
            _ => (uint)(array[index]
                | (array[index + 1u] << 8)
                | (array[index + 2u] << 16)
                | (array[index + 3u] << 24)),
        };
    }

    private static void WriteArray(byte[] array, uint index, int width, uint value) {
        array[index] = (byte)value;

        if (width >= 2) {
            array[index + 1u] = (byte)(value >> 8);
        }

        if (width == 4) {
            array[index + 2u] = (byte)(value >> 16);
            array[index + 3u] = (byte)(value >> 24);
        }
    }
}
