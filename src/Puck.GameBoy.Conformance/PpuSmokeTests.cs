namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained PPU timing checks driving a <see cref="Ppu"/> directly: the per-scanline mode sequence and
/// its dot durations, the <c>LY</c> progression and frame wrap, the vertical-blank and <c>LYC</c>-coincidence
/// STAT interrupts, and the LCD-disable reset. These cover the timing skeleton (no pixels yet) ahead of the
/// mooneye PPU suite.
/// </summary>
internal static class PpuSmokeTests {
    private const ushort LcdControl = 0xFF40;
    private const ushort LcdStatus = 0xFF41;
    private const ushort ScrollY = 0xFF42;
    private const ushort ScrollX = 0xFF43;
    private const ushort LcdLine = 0xFF44;
    private const ushort LcdLineCompare = 0xFF45;
    private const ushort BackgroundPalette = 0xFF47;
    private const ushort ObjectPalette0 = 0xFF48;
    private const ushort WindowY = 0xFF4A;
    private const ushort WindowX = 0xFF4B;
    private const int DotsPerLine = 456;
    private const int FrameDots = 70224;
    private const uint White = 0xFFFFFFFFu;
    private const uint LightGray = 0xFFAAAAAAu;
    private const uint Black = 0xFF000000u;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("line 0 after LCD enable starts in mode 0 (no OAM scan)", static () => {
                var (ppu, _) = Enabled();

                return (CurrentMode(ppu: ppu) == 0)
                    ? null
                    : $"mode={CurrentMode(ppu: ppu)} (expected 0 on the first line after enable)";
            }),
            ("line 1 starts in OAM scan (mode 2)", static () => {
                var (ppu, _) = Enabled();

                ppu.Step(tCycles: DotsPerLine);

                return (CurrentMode(ppu: ppu) == 2)
                    ? null
                    : $"mode={CurrentMode(ppu: ppu)} (expected 2 at line 1 start)";
            }),
            ("enters drawing (mode 3) after 80 dots", static () => {
                var (ppu, _) = Enabled();

                ppu.Step(tCycles: 80);

                return (CurrentMode(ppu: ppu) == 3)
                    ? null
                    : $"mode={CurrentMode(ppu: ppu)} (expected 3)";
            }),
            ("enters HBlank (mode 0) after 252 dots", static () => {
                var (ppu, _) = Enabled();

                ppu.Step(tCycles: 252);

                return (CurrentMode(ppu: ppu) == 0)
                    ? null
                    : $"mode={CurrentMode(ppu: ppu)} (expected 0)";
            }),
            ("LY increments after one full scanline", static () => {
                var (ppu, _) = Enabled();

                ppu.Step(tCycles: DotsPerLine);

                return (ppu.ReadRegister(address: LcdLine) == 1)
                    ? null
                    : $"LY={ppu.ReadRegister(address: LcdLine)} (expected 1)";
            }),
            ("vertical blank + interrupt at line 144", static () => {
                var (ppu, interrupts) = Enabled();

                ppu.Step(tCycles: (DotsPerLine * 144));

                var atVBlank = ((ppu.ReadRegister(address: LcdLine) == 144) && (CurrentMode(ppu: ppu) == 1));
                var raised = ((interrupts.InterruptFlag & (byte)InterruptKind.VBlank) != 0);

                return (atVBlank && raised)
                    ? null
                    : $"LY={ppu.ReadRegister(address: LcdLine)} mode={CurrentMode(ppu: ppu)} vblankIF={raised}";
            }),
            ("frame wraps to line 0 after 154 lines", static () => {
                var (ppu, _) = Enabled();

                ppu.Step(tCycles: (DotsPerLine * 154));

                return (ppu.ReadRegister(address: LcdLine) == 0)
                    ? null
                    : $"LY={ppu.ReadRegister(address: LcdLine)} (expected 0)";
            }),
            ("LYC coincidence raises a STAT interrupt", static () => {
                var (ppu, interrupts) = Enabled();

                ppu.WriteRegister(address: LcdLineCompare, value: 5);
                ppu.WriteRegister(address: LcdStatus, value: 0x40);
                ppu.Step(tCycles: (DotsPerLine * 5));

                return (((interrupts.InterruptFlag & (byte)InterruptKind.LcdStat) != 0) && (ppu.ReadRegister(address: LcdLine) == 5))
                    ? null
                    : $"LY={ppu.ReadRegister(address: LcdLine)} statIF set={(interrupts.InterruptFlag & (byte)InterruptKind.LcdStat) != 0}";
            }),
            ("disabling the LCD resets LY to 0", static () => {
                var (ppu, _) = Enabled();

                ppu.Step(tCycles: (DotsPerLine * 10));
                ppu.WriteRegister(address: LcdControl, value: 0x00);

                return (ppu.ReadRegister(address: LcdLine) == 0)
                    ? null
                    : $"LY={ppu.ReadRegister(address: LcdLine)} (expected 0 after LCD off)";
            }),
            ("background renders a solid tile across the screen", static () => {
                var videoRam = new byte[0x2000];

                // Tile 0: every pixel color index 1 (low plane all set, high plane clear).
                for (var row = 0; row < 8; row += 1) {
                    videoRam[(row * 2)] = 0xFF;
                }
                // Tile map at 0x9800 is left all-zero, so every cell uses tile 0.

                var ppu = RenderFrame(videoRam: videoRam, palette: 0xE4);

                // BGP 0xE4 maps color index 1 -> shade 1 (light gray) everywhere.
                var first = ppu.Framebuffer[0];
                var last = ppu.Framebuffer[(144 * 160) - 1];

                return ((first == LightGray) && (last == LightGray))
                    ? null
                    : $"first=0x{first:X8} last=0x{last:X8} (expected 0x{LightGray:X8})";
            }),
            ("background disabled clears to the lightest shade", static () => {
                var ppu = new Ppu(interrupts: new InterruptController(), videoRam: new byte[0x2000], objectAttributeMemory: new byte[0xA0]);

                ppu.WriteRegister(address: BackgroundPalette, value: 0xE4);
                // LCD on, but BG disabled (bit 0 clear).
                ppu.WriteRegister(address: LcdControl, value: 0x90);
                ppu.Step(tCycles: FrameDots);

                return (ppu.Framebuffer[0] == White)
                    ? null
                    : $"framebuffer[0]=0x{ppu.Framebuffer[0]:X8} (expected white 0x{White:X8})";
            }),
            ("tile pixels decode most-significant-bit first", static () => {
                var videoRam = new byte[0x2000];

                // Tile 0, row 0: both planes have only bit 7 set -> leftmost pixel is color index 3, rest 0.
                videoRam[0] = 0x80;
                videoRam[1] = 0x80;

                var ppu = RenderFrame(videoRam: videoRam, palette: 0xE4);

                // Color 3 -> shade 3 (black) at x=0; color 0 -> shade 0 (white) at x=1.
                return ((ppu.Framebuffer[0] == Black) && (ppu.Framebuffer[1] == White))
                    ? null
                    : $"px0=0x{ppu.Framebuffer[0]:X8} px1=0x{ppu.Framebuffer[1]:X8} (expected black, white)";
            }),
            ("window overlays the background when enabled", static () => {
                var videoRam = new byte[0x2000];

                FillSolidTile(videoRam: videoRam, tileIndex: 0, colorIndex: 1); // background tile -> color 1
                FillSolidTile(videoRam: videoRam, tileIndex: 1, colorIndex: 3); // window tile -> color 3

                // Window map at 0x9C00 selects tile 1 everywhere; background map at 0x9800 stays tile 0.
                for (var cell = 0; cell < (32 * 32); cell += 1) {
                    videoRam[0x1C00 + cell] = 1;
                }

                var ppu = new Ppu(interrupts: new InterruptController(), videoRam: videoRam, objectAttributeMemory: new byte[0xA0]);

                ppu.WriteRegister(address: BackgroundPalette, value: 0xE4);
                ppu.WriteRegister(address: WindowY, value: 0x00);
                ppu.WriteRegister(address: WindowX, value: 0x07); // window covers the whole screen from x=0
                // LCD on + window map 0x9C00 + window on + 0x8000 tiles + BG on.
                ppu.WriteRegister(address: LcdControl, value: 0xF1);
                ppu.Step(tCycles: FrameDots);

                var first = ppu.Framebuffer[0];
                var mid = ppu.Framebuffer[(100 * 160) + 50];

                return ((first == Black) && (mid == Black))
                    ? null
                    : $"first=0x{first:X8} mid=0x{mid:X8} (expected window color 0x{Black:X8})";
            }),
            ("a sprite draws over the background", static () => {
                var videoRam = new byte[0x2000];

                FillSolidTile(videoRam: videoRam, tileIndex: 0, colorIndex: 1); // background -> color 1 (light gray)
                FillSolidTile(videoRam: videoRam, tileIndex: 2, colorIndex: 3); // sprite tile -> color 3

                var oam = new byte[0xA0];

                oam[0] = 16;   // Y = 16 -> top row at screen y=0
                oam[1] = 8;    // X = 8 -> left column at screen x=0
                oam[2] = 2;    // tile 2
                oam[3] = 0x00; // OBP0, no flip, in front of background

                var ppu = new Ppu(interrupts: new InterruptController(), videoRam: videoRam, objectAttributeMemory: oam);

                ppu.WriteRegister(address: BackgroundPalette, value: 0xE4);
                ppu.WriteRegister(address: ObjectPalette0, value: 0xE4);
                // LCD on + 0x8000 tiles + sprites on (0x02) + BG on (0x01).
                ppu.WriteRegister(address: LcdControl, value: 0x93);
                ppu.Step(tCycles: FrameDots);

                var spritePixel = ppu.Framebuffer[0];                 // covered by the sprite
                var backgroundPixel = ppu.Framebuffer[(50 * 160) + 80]; // outside the sprite

                return ((spritePixel == Black) && (backgroundPixel == LightGray))
                    ? null
                    : $"sprite=0x{spritePixel:X8} bg=0x{backgroundPixel:X8} (expected sprite black, bg light gray)";
            }),
            ("the presented frame is latched at vblank, never torn mid-draw", static () => {
                var videoRam = new byte[0x2000];

                FillSolidTile(videoRam: videoRam, tileIndex: 0, colorIndex: 1); // frame 1 paints light gray

                var ppu = new Ppu(interrupts: new InterruptController(), videoRam: videoRam, objectAttributeMemory: new byte[0xA0]);

                ppu.WriteRegister(address: BackgroundPalette, value: 0xE4);
                ppu.WriteRegister(address: LcdControl, value: 0x91); // LCD on, 0x8000 tiles, 0x9800 map, BG on
                ppu.Step(tCycles: FrameDots); // render frame 1 and latch it at its vertical blank

                var presentedAfterFrame1 = ppu.Framebuffer[0];

                // Repaint the tile black, then advance only a few scanlines into frame 2 — not far enough to reach
                // the next vertical blank. Those lines land in the back buffer; the presented (front) buffer must
                // not change until the frame completes.
                FillSolidTile(videoRam: videoRam, tileIndex: 0, colorIndex: 3); // frame 2 would paint black
                ppu.Step(tCycles: (DotsPerLine * 5));

                var presentedMidFrame2 = ppu.Framebuffer[0];

                return ((presentedAfterFrame1 == LightGray) && (presentedMidFrame2 == LightGray))
                    ? null
                    : $"afterFrame1=0x{presentedAfterFrame1:X8} midFrame2=0x{presentedMidFrame2:X8} (expected both light gray 0x{LightGray:X8})";
            }),
            ("a transparent sprite pixel (color 0) shows the background", static () => {
                var videoRam = new byte[0x2000];

                FillSolidTile(videoRam: videoRam, tileIndex: 0, colorIndex: 1); // background -> color 1
                FillSolidTile(videoRam: videoRam, tileIndex: 2, colorIndex: 0); // sprite tile -> color 0 (transparent)

                var oam = new byte[0xA0];

                oam[0] = 16;
                oam[1] = 8;
                oam[2] = 2;
                oam[3] = 0x00;

                var ppu = new Ppu(interrupts: new InterruptController(), videoRam: videoRam, objectAttributeMemory: oam);

                ppu.WriteRegister(address: BackgroundPalette, value: 0xE4);
                ppu.WriteRegister(address: ObjectPalette0, value: 0xE4);
                ppu.WriteRegister(address: LcdControl, value: 0x93);
                ppu.Step(tCycles: FrameDots);

                // Under the (fully transparent) sprite, the background still shows.
                return (ppu.Framebuffer[0] == LightGray)
                    ? null
                    : $"px0=0x{ppu.Framebuffer[0]:X8} (expected background light gray through transparent sprite)";
            }),
        ];

    private static Ppu RenderFrame(byte[] videoRam, byte palette, byte[]? oam = null) {
        var ppu = new Ppu(
            interrupts: new InterruptController(),
            objectAttributeMemory: (oam ?? new byte[0xA0]),
            videoRam: videoRam
        );

        ppu.WriteRegister(address: BackgroundPalette, value: palette);
        ppu.WriteRegister(address: ScrollX, value: 0x00);
        ppu.WriteRegister(address: ScrollY, value: 0x00);
        // LCD on (0x80) + BG on (0x01) + 0x8000 tile-data method (0x10) + 0x9800 map (bit 3 clear).
        ppu.WriteRegister(address: LcdControl, value: 0x91);
        ppu.Step(tCycles: FrameDots);

        return ppu;
    }

    // Fills the given tile (16 bytes at tileIndex*16 in video RAM) so every pixel is the given 2-bit color index.
    private static void FillSolidTile(byte[] videoRam, int tileIndex, int colorIndex) {
        var baseAddress = (tileIndex * 16);

        for (var row = 0; row < 8; row += 1) {
            videoRam[baseAddress + (row * 2)] = (byte)(((colorIndex & 1) != 0) ? 0xFF : 0x00);
            videoRam[baseAddress + (row * 2) + 1] = (byte)(((colorIndex & 2) != 0) ? 0xFF : 0x00);
        }
    }

    private static (Ppu Ppu, InterruptController Interrupts) Enabled() {
        var interrupts = new InterruptController();
        var ppu = new Ppu(interrupts: interrupts, videoRam: new byte[0x2000], objectAttributeMemory: new byte[0xA0]);

        ppu.WriteRegister(address: LcdControl, value: 0x80);

        return (ppu, interrupts);
    }

    // The actual PPU mode (the STAT register's mode bits lag this by a machine cycle, which is verified against
    // the mooneye suite rather than here).
    private static int CurrentMode(Ppu ppu) =>
        (int)ppu.Mode;
}
