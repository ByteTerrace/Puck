using System.Text;

namespace Puck.Demo.Forge;

/// <summary>
/// Forges a genuine <b>camera</b> cartridge (header type <c>0xFC</c>, 128&#160;KiB save RAM) whose hand-authored
/// ROM drives the real M64282FP protocol: it programs the sensor's gain, exposure, and 4×4 dither matrix,
/// then each frame triggers a capture, <b>polls register&#160;0's busy bit until the shoot completes</b>, and blits the
/// processed <c>128</c>×<c>112</c> image from save-RAM (<c>0xA100</c>) into VRAM with two CGB general-purpose DMAs — a
/// live viewfinder. This is the "our ROM" half of the camera feature: it talks to the authentic mapper, so whatever
/// feeds the sensor — the deterministic gradient, or the PC webcam — appears on the brick screen. The ROM carries a
/// valid logo and checksums so it is a real <c>.gbc</c> a stranger (or a flashcart) could boot.
/// </summary>
internal static class CameraRom {
    private const ushort EntryPoint = 0x0100;
    private const ushort MainRoutine = 0x0150;
    private const int RomSize = 0x8000;

    // In-ROM data tables the routine reads (all in bank 0, clear of the routine and each other).
    private const ushort PaletteAddress = 0x0300;   // 8 bytes: CGB BG palette 0 (four greys)
    private const ushort DitherAddress = 0x0310;    // 48 bytes: the M64282FP dither/threshold matrix
    private const ushort TileMapAddress = 0x1800;   // 0x400 bytes: the 32×32 BG map

    // High-page (0xFF00+) ports.
    private const byte PortLcdControl = 0x40;
    private const byte PortBgPaletteData = 0x69;
    private const byte PortBgPaletteIndex = 0x68;
    private const byte PortHdmaControl = 0x55;
    private const byte PortHdmaDestHigh = 0x53;
    private const byte PortHdmaDestLow = 0x54;
    private const byte PortHdmaSourceHigh = 0x51;
    private const byte PortHdmaSourceLow = 0x52;
    private const byte PortScrollX = 0x43;
    private const byte PortScrollY = 0x42;
    private const byte PortVramBank = 0x4F;

    // Cartridge control/registers (absolute stores through the bus).
    private const ushort RamEnableRegister = 0x0000;    // 0x0A enables the save-RAM window
    private const ushort BankSelectRegister = 0x4000;   // bit 4 maps the camera registers; bits 3-0 = RAM bank
    private const ushort CameraShootRegister = 0xA000;  // register 0: bit 0 triggers / reads busy
    private const ushort CameraDitherStart = 0xA006;
    private const ushort CameraEdgeRegister = 0xA004;
    private const ushort CameraExposureHigh = 0xA002;
    private const ushort CameraExposureLow = 0xA003;
    private const ushort CameraGainRegister = 0xA001;
    private const ushort VramBackgroundMap = 0x9800;
    private const ushort VramTiles = 0x8000;
    private const int BlankTile = (SensorImageGeometry.TilesWide * SensorImageGeometry.TilesTall); // 224: a zeroed margin tile

    // The sensor config: gain index 4 is exactly ×1.0 and exposure 0x1000 divides out, so the processed colour equals the
    // raw sensor value — the dither matrix then spreads 0..255 across four shades. Edge enhancement off (bit 7 clear).
    private const byte GainAndEdge = 0x04;
    private const byte EdgeRatio = 0x00;
    private const byte ExposureHigh = 0x10;
    private const byte ExposureLow = 0x00;

    // LCDC: LCD on (0x80) | BG tiles at 0x8000 (0x10) | BG on (0x01). No sprites; the whole screen is the viewfinder.
    private const byte LcdOn = 0x91;

    private static readonly byte[] BootLogo = [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    /// <summary>Builds the camera viewfinder cartridge image.</summary>
    /// <param name="title">The cartridge title (uppercased into the header).</param>
    /// <returns>A 32&#160;KiB <c>.gbc</c> image with header type <c>0xFC</c>.</returns>
    public static byte[] Build(string title) {
        ArgumentException.ThrowIfNullOrEmpty(argument: title);

        var rom = new byte[RomSize];

        // Entry (0x0100): nop; jp MainRoutine.
        rom[EntryPoint] = 0x00;
        rom[(EntryPoint + 1)] = 0xC3;
        rom[(EntryPoint + 2)] = (byte)(MainRoutine & 0xFF);
        rom[(EntryPoint + 3)] = (byte)((MainRoutine >> 8) & 0xFF);

        BootLogo.CopyTo(array: rom, index: 0x0104);

        var titleBytes = Encoding.ASCII.GetBytes(s: title.ToUpperInvariant());

        for (var index = 0; ((index < titleBytes.Length) && (index < 15)); ++index) {
            rom[(0x0134 + index)] = titleBytes[index];
        }

        rom[0x0143] = 0xC0; // CGB flag: Color required (the four-grey palette lives in CGB palette RAM).
        rom[0x0147] = 0xFC; // Cartridge type 0xFC (camera) — this is what makes the mapper an M64282FP sensor.
        rom[0x0148] = 0x00; // ROM size: 32 KiB.
        rom[0x0149] = 0x04; // RAM size: 128 KiB (the camera's banked image RAM).
        rom[0x014A] = 0x01; // Destination: non-Japanese.
        rom[0x014B] = 0x33; // Old licensee: see new licensee code.

        BuildPalette().CopyTo(array: rom, index: PaletteAddress);
        BuildDither().CopyTo(array: rom, index: DitherAddress);
        BuildTileMap().CopyTo(array: rom, index: TileMapAddress);

        var routine = BuildRoutine();

        if ((MainRoutine + routine.Length) > PaletteAddress) {
            throw new InvalidOperationException(message: $"The camera routine ({routine.Length} bytes) overran the data region at 0x{PaletteAddress:X4}.");
        }

        routine.CopyTo(array: rom, index: MainRoutine);

        Finalize(rom: rom);

        return rom;
    }

    private static byte[] BuildRoutine() {
        var emitter = new Sm83Emitter();

        // --- Setup (once) ---
        emitter.DisableInterrupts();
        emitter.LoadStackPointer(value: 0xFFFE);

        emitter.XorA();
        emitter.StoreAToHighPage(port: PortScrollY);
        emitter.StoreAToHighPage(port: PortScrollX);
        emitter.StoreAToHighPage(port: PortLcdControl); // LCD off: unrestricted VRAM setup below.
        emitter.StoreAToHighPage(port: PortVramBank);   // VBK = 0.

        // CGB BG palette 0 <- the four greys.
        emitter.LoadAImmediate(value: 0x80); // auto-increment from index 0
        emitter.StoreAToHighPage(port: PortBgPaletteIndex);
        EmitPaletteCopy(emitter: emitter, sourceAddress: PaletteAddress, dataPort: PortBgPaletteData);

        // BG tilemap.
        EmitBlockCopy(emitter: emitter, source: TileMapAddress, destination: VramBackgroundMap, count: 0x0400);

        // Bank-1 attribute map = 0 (palette 0, tile bank 0), then back to bank 0.
        emitter.LoadAImmediate(value: 0x01);
        emitter.StoreAToHighPage(port: PortVramBank);
        EmitBlockFill(emitter: emitter, destination: VramBackgroundMap, count: 0x0400);
        emitter.XorA();
        emitter.StoreAToHighPage(port: PortVramBank);

        // Zero the blank margin tile (index 224) so the screen edges outside the 16×14 image read white.
        EmitBlockFill(emitter: emitter, destination: (ushort)(VramTiles + (BlankTile * 16)), count: 16);

        // Program the M64282FP registers (camera block selected).
        emitter.LoadAImmediate(value: 0x10);
        emitter.StoreAToAddress(address: BankSelectRegister);
        emitter.LoadAImmediate(value: GainAndEdge);
        emitter.StoreAToAddress(address: CameraGainRegister);
        emitter.LoadAImmediate(value: ExposureHigh);
        emitter.StoreAToAddress(address: CameraExposureHigh);
        emitter.LoadAImmediate(value: ExposureLow);
        emitter.StoreAToAddress(address: CameraExposureLow);
        emitter.LoadAImmediate(value: EdgeRatio);
        emitter.StoreAToAddress(address: CameraEdgeRegister);
        EmitBlockCopy(emitter: emitter, source: DitherAddress, destination: CameraDitherStart, count: 48);

        emitter.LoadAImmediate(value: LcdOn);
        emitter.StoreAToHighPage(port: PortLcdControl);

        // --- Viewfinder loop ---
        var loop = emitter.NewLabel();
        var wait = emitter.NewLabel();

        emitter.MarkLabel(label: loop);

        // Trigger a capture (camera block selected, register 0 bit 0).
        emitter.LoadAImmediate(value: 0x10);
        emitter.StoreAToAddress(address: BankSelectRegister);
        emitter.LoadAImmediate(value: 0x01);
        emitter.StoreAToAddress(address: CameraShootRegister);

        // Poll the busy bit until the shoot completes — the authentic exposure-dependent wait.
        emitter.MarkLabel(label: wait);
        emitter.LoadAFromAddress(address: CameraShootRegister);
        emitter.ArithmeticImmediate(op: AluOp.And, value: 0x01);
        emitter.JumpRelative(condition: Condition.NotZero, label: wait);

        // Expose the image RAM: deselect the camera block (bank 0), enable the RAM window.
        emitter.XorA();
        emitter.StoreAToAddress(address: BankSelectRegister);
        emitter.LoadAImmediate(value: 0x0A);
        emitter.StoreAToAddress(address: RamEnableRegister);

        // Blit 0xA100 -> 0x8000 (3584 bytes) via two GDMAs; a single GDMA's 7-bit length caps at 128 16-byte chunks.
        EmitGeneralDma(emitter: emitter, sourceHigh: 0xA1, destOffset: 0x0000, chunks: 112);
        EmitGeneralDma(emitter: emitter, sourceHigh: 0xA8, destOffset: 0x0700, chunks: 112);

        emitter.JumpRelative(label: loop);

        return emitter.ToArray();
    }

    // A CGB general-purpose DMA (bit 7 of HDMA5 clear): copies chunks*16 bytes from (sourceHigh<<8) to 0x8000+destOffset,
    // stalling the CPU until done — so it runs with the LCD on and the viewfinder never blanks. Source low is 0x00 (the
    // low nibble is ignored by the hardware anyway).
    private static void EmitGeneralDma(Sm83Emitter emitter, byte sourceHigh, int destOffset, int chunks) {
        emitter.LoadAImmediate(value: sourceHigh);
        emitter.StoreAToHighPage(port: PortHdmaSourceHigh);
        emitter.XorA();
        emitter.StoreAToHighPage(port: PortHdmaSourceLow);
        emitter.LoadAImmediate(value: (byte)((destOffset >> 8) & 0x1F));
        emitter.StoreAToHighPage(port: PortHdmaDestHigh);
        emitter.LoadAImmediate(value: (byte)(destOffset & 0xF0));
        emitter.StoreAToHighPage(port: PortHdmaDestLow);
        emitter.LoadAImmediate(value: (byte)((chunks - 1) & 0x7F)); // bit 7 clear = GDMA
        emitter.StoreAToHighPage(port: PortHdmaControl);
    }
    private static void EmitPaletteCopy(Sm83Emitter emitter, ushort sourceAddress, byte dataPort) {
        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: sourceAddress);
        emitter.LoadImmediate(destination: Reg8.B, value: 8);
        emitter.MarkLabel(label: loop);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToHighPage(port: dataPort);
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }
    private static void EmitBlockCopy(Sm83Emitter emitter, ushort source, ushort destination, ushort count) {
        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: source);
        emitter.LoadImmediate(pair: Reg16.De, value: destination);
        emitter.LoadImmediate(pair: Reg16.Bc, value: count);
        emitter.MarkLabel(label: loop);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToDe();
        emitter.Increment(pair: Reg16.De);
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(destination: Reg8.A, source: Reg8.B);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.C);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }
    private static void EmitBlockFill(Sm83Emitter emitter, ushort destination, ushort count) {
        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.De, value: destination);
        emitter.LoadImmediate(pair: Reg16.Bc, value: count);
        emitter.MarkLabel(label: loop);
        emitter.XorA();
        emitter.StoreAToDe();
        emitter.Increment(pair: Reg16.De);
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(destination: Reg8.A, source: Reg8.B);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.C);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }

    // CGB BG palette 0: four greys, white -> black, as RGB555 little-endian pairs.
    private static byte[] BuildPalette() {
        ushort[] colours = [0x7FFF, 0x56B5, 0x294A, 0x0000];
        var palette = new byte[8];

        for (var index = 0; (index < 4); ++index) {
            palette[(index * 2)] = (byte)(colours[index] & 0xFF);
            palette[((index * 2) + 1)] = (byte)((colours[index] >> 8) & 0xFF);
        }

        return palette;
    }

    // The M64282FP dither/threshold matrix: 16 cells (indexed by x&3, y&3), each three ascending thresholds. An ordered
    // (Bayer) offset around the even split {64,128,192} turns the four hard shades into a dithered grey ramp.
    private static byte[] BuildDither() {
        int[,] bayer = {
            { 0, 8, 2, 10 },
            { 12, 4, 14, 6 },
            { 3, 11, 1, 9 },
            { 15, 7, 13, 5 },
        };
        int[] baseThresholds = [64, 128, 192];
        var dither = new byte[48];

        for (var cellY = 0; (cellY < 4); ++cellY) {
            for (var cellX = 0; (cellX < 4); ++cellX) {
                var offset = ((bayer[cellY, cellX] - 8) * 6); // -48..+42
                var cellBase = ((cellX + (cellY * 4)) * 3);

                for (var level = 0; (level < 3); ++level) {
                    var threshold = (baseThresholds[level] + offset);

                    dither[(cellBase + level)] = (byte)((threshold < 0) ? 0 : ((threshold > 255) ? 255 : threshold));
                }
            }
        }

        return dither;
    }

    // A 32×32 BG map showing the 16×14 sensor image (tiles 0..223, row-major, matching the cartridge's deposit order)
    // CENTERED in the 20×18 visible screen — like a commercial cartridge camera, whose viewfinder sits centered with a
    // border. Every other cell is the blank margin tile. The brick presents this like any other (full-frame resample).
    private const int ScreenTilesWide = 20;
    private const int ScreenTilesTall = 18;

    private static byte[] BuildTileMap() {
        var map = new byte[0x400];

        for (var index = 0; (index < map.Length); ++index) {
            map[index] = (byte)BlankTile;
        }

        const int offsetX = ((ScreenTilesWide - SensorImageGeometry.TilesWide) / 2);
        const int offsetY = ((ScreenTilesTall - SensorImageGeometry.TilesTall) / 2);

        for (var tileY = 0; (tileY < SensorImageGeometry.TilesTall); ++tileY) {
            for (var tileX = 0; (tileX < SensorImageGeometry.TilesWide); ++tileX) {
                map[(((tileY + offsetY) * 32) + (tileX + offsetX))] = (byte)((tileY * SensorImageGeometry.TilesWide) + tileX);
            }
        }

        return map;
    }
    private static void Finalize(byte[] rom) {
        byte headerChecksum = 0;

        for (var address = 0x0134; (address <= 0x014C); ++address) {
            headerChecksum = (byte)((headerChecksum - rom[address]) - 1);
        }

        rom[0x014D] = headerChecksum;

        var globalSum = 0;

        for (var address = 0; (address < rom.Length); ++address) {
            if ((address == 0x014E) || (address == 0x014F)) {
                continue;
            }

            globalSum += rom[address];
        }

        rom[0x014E] = (byte)((globalSum >> 8) & 0xFF);
        rom[0x014F] = (byte)(globalSum & 0xFF);
    }
}

/// <summary>The camera image geometry, mirrored from the emulator's <c>SensorImage</c> so the forged ROM's tilemap
/// and the mapper's deposited tiles agree without the demo taking a hard dependency on an emulator-internal constant.</summary>
internal static class SensorImageGeometry {
    /// <summary>Tiles across the captured image (128 px ÷ 8).</summary>
    public const int TilesWide = 16;
    /// <summary>Tiles down the captured image (112 px ÷ 8).</summary>
    public const int TilesTall = 14;
}
