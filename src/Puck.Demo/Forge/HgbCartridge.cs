using System.Text;

namespace Puck.Demo.Forge;

/// <summary>
/// Assembles forged assets into a genuine 32&#160;KiB Humble GamingBrick cartridge image — header (CGB-required flag, the
/// boot-header logo, valid checksums), entry trampoline, a hand-authored display routine, and the asset data tables the
/// routine reads. Two cartridge shapes share the same setup + header machinery:
/// <list type="bullet">
/// <item><see cref="Build"/> — a STATIC scene (a forged room + a forged metasprite), the display's first artifact.</item>
/// <item><see cref="BuildWorldLens"/> — a live WORLD-LENS: same setup, but its main loop reads a work-RAM sensor page
/// each frame and moves the player sprite to mirror the world the cartridge sits inside (see <see cref="WorldLensProtocol"/>).</item>
/// </list>
///
/// <para>Puck's <c>--rom</c> path runs no boot ROM (the machine starts at the seeded post-boot handoff, A = 0x11), so
/// the logo/checksums are not required to boot HERE; they are written anyway so the <c>.gbc</c> is valid on real
/// hardware — the "a stranger could play it" (and "we could press physical carts") property.</para>
/// </summary>
internal static class HgbCartridge {
    private const int RomSize = 0x8000; // 32 KiB, ROM-only (header size code 0x00).
    private const ushort EntryPoint = 0x0100;
    private const ushort MainRoutine = 0x0150; // Just past the 0x0100–0x014F header block.

    // Fixed in-ROM addresses the display routine reads its data from (all within bank 0, clear of the routine and of
    // each other; tiles cap at 256 * 16 = 0x1000 bytes).
    private const ushort BackgroundPaletteAddress = 0x0300;
    private const ushort ObjectPaletteAddress = 0x0308;
    private const ushort ObjectAttributeAddress = 0x0310;
    private const ushort TileDataAddress = 0x0400;
    private const ushort TileMapAddress = 0x1800;

    private const ushort VramTiles = 0x8000;
    private const ushort VramBackgroundMap = 0x9800;
    private const ushort VramAttributeMapBank1 = 0x9800; // Same address, read with VBK = 1.
    private const ushort ObjectAttributeMemory = 0xFE00;

    private const byte PortJoypad = 0x00; // P1/JOYP: write 0x20 to select the direction keys, then read them (active-low).
    private const byte PortLcdControl = 0x40;
    private const byte PortScrollY = 0x42;
    private const byte PortScrollX = 0x43;
    private const byte PortScanline = 0x44; // LY.
    private const byte PortVramBank = 0x4F;
    private const byte PortBackgroundPaletteIndex = 0x68;
    private const byte PortBackgroundPaletteData = 0x69;
    private const byte PortObjectPaletteIndex = 0x6A;
    private const byte PortObjectPaletteData = 0x6B;

    // LCDC bases: LCD on (0x80) | BG tile data at 0x8000 (0x10) | OBJ on (0x02) | BG on (0x01). The static scene uses
    // 8×8 objects (0x93); the world-lens uses 8×16 (adds 0x04 → 0x97) so one OAM entry is a 2-tile-tall character.
    private const byte LcdControlStatic = 0x93;
    private const byte LcdControlWorldLens = 0x97;
    private const byte PaletteAutoIncrementFromZero = 0x80;
    private const byte VBlankScanline = 144;

    private static readonly byte[] BootLogo = [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    /// <summary>Builds the STATIC-scene cartridge (a forged room BG + a forged metasprite placed by OAM).</summary>
    public static byte[] Build(string title, byte[] backgroundPalette, byte[] objectPalette, byte[] tileData, byte[] tileMap, byte[] objectAttributes) {
        ArgumentNullException.ThrowIfNull(objectAttributes);

        var rom = NewRomWithHeader(title: title);

        PlaceData(rom: rom, backgroundPalette: backgroundPalette, objectPalette: objectPalette, tileData: tileData, tileMap: tileMap);
        objectAttributes.CopyTo(array: rom, index: ObjectAttributeAddress);
        PlaceRoutine(rom: rom, routine: BuildStaticRoutine(tileByteCount: tileData.Length, objectByteCount: objectAttributes.Length));
        Finalize(rom: rom);

        return rom;
    }

    /// <summary>Builds the WORLD-LENS cartridge: the room BG plus a player sprite the ROM repositions every frame from
    /// the work-RAM sensor page a host peripheral writes. <paramref name="playerTileBase"/> is the (even) VRAM tile
    /// index of the player's 8×16 sprite (top tile; bottom is the next one). When the sprite's tile coordinates reach
    /// (<paramref name="goalTileX"/>, <paramref name="goalTileY"/>) the ROM writes <see cref="WorldLensProtocol.WinMagic"/>
    /// to the win flag — the fourth-wall trigger the host polls.</summary>
    public static byte[] BuildWorldLens(string title, byte[] backgroundPalette, byte[] objectPalette, byte[] tileData, byte[] tileMap, int playerTileBase, byte goalTileX, byte goalTileY) {
        if ((playerTileBase < 0) || (playerTileBase > 254)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(playerTileBase), message: "The player tile base must fit a byte (and leave room for the paired bottom tile).");
        }

        if ((playerTileBase & 1) != 0) {
            throw new ArgumentException(message: "An 8×16 sprite's tile base must be EVEN (hardware pairs tile N with N+1).", paramName: nameof(playerTileBase));
        }

        var rom = NewRomWithHeader(title: title);

        PlaceData(rom: rom, backgroundPalette: backgroundPalette, objectPalette: objectPalette, tileData: tileData, tileMap: tileMap);
        PlaceRoutine(rom: rom, routine: BuildWorldLensRoutine(tileByteCount: tileData.Length, playerTileBase: (byte)playerTileBase, goalTileX: goalTileX, goalTileY: goalTileY));
        Finalize(rom: rom);

        return rom;
    }

    private static byte[] NewRomWithHeader(string title) {
        ArgumentException.ThrowIfNullOrEmpty(title);

        var rom = new byte[RomSize];

        // Entry point (0x0100): nop; jp MainRoutine.
        rom[EntryPoint] = 0x00;
        rom[EntryPoint + 1] = 0xC3;
        rom[EntryPoint + 2] = (byte)(MainRoutine & 0xFF);
        rom[EntryPoint + 3] = (byte)((MainRoutine >> 8) & 0xFF);

        BootLogo.CopyTo(array: rom, index: 0x0104);

        var titleBytes = Encoding.ASCII.GetBytes(s: title.ToUpperInvariant());

        for (var index = 0; ((index < titleBytes.Length) && (index < 15)); index++) {
            rom[0x0134 + index] = titleBytes[index];
        }

        rom[0x0143] = 0xC0; // CGB flag: Color REQUIRED.
        rom[0x0147] = 0x00; // Cartridge type: ROM only.
        rom[0x0148] = 0x00; // ROM size: 32 KiB.
        rom[0x0149] = 0x00; // RAM size: none.
        rom[0x014A] = 0x01; // Destination: non-Japanese.
        rom[0x014B] = 0x33; // Old licensee 0x33 = "see new licensee code".

        return rom;
    }

    private static void PlaceData(byte[] rom, byte[] backgroundPalette, byte[] objectPalette, byte[] tileData, byte[] tileMap) {
        ArgumentNullException.ThrowIfNull(backgroundPalette);
        ArgumentNullException.ThrowIfNull(objectPalette);
        ArgumentNullException.ThrowIfNull(tileData);
        ArgumentNullException.ThrowIfNull(tileMap);

        if ((backgroundPalette.Length != 8) || (objectPalette.Length != 8)) {
            throw new ArgumentException(message: "A CGB palette is 8 bytes (4 RGB555 colours).");
        }

        if (tileMap.Length != 0x400) {
            throw new ArgumentException(message: "The tilemap is a full 32x32 = 1024 bytes.", paramName: nameof(tileMap));
        }

        if ((int)TileDataAddress + tileData.Length > TileMapAddress) {
            throw new ArgumentException(message: $"The tile data ({tileData.Length} bytes) overruns the fixed tilemap slot at 0x{TileMapAddress:X4}.", paramName: nameof(tileData));
        }

        backgroundPalette.CopyTo(array: rom, index: BackgroundPaletteAddress);
        objectPalette.CopyTo(array: rom, index: ObjectPaletteAddress);
        tileData.CopyTo(array: rom, index: TileDataAddress);
        tileMap.CopyTo(array: rom, index: TileMapAddress);
    }

    private static void PlaceRoutine(byte[] rom, byte[] routine) {
        if ((int)MainRoutine + routine.Length > BackgroundPaletteAddress) {
            throw new InvalidOperationException(message: $"The display routine ({routine.Length} bytes) overran the data region at 0x{BackgroundPaletteAddress:X4}.");
        }

        routine.CopyTo(array: rom, index: MainRoutine);
    }

    private static void Finalize(byte[] rom) {
        byte headerChecksum = 0;

        for (var address = 0x0134; (address <= 0x014C); address++) {
            headerChecksum = (byte)(headerChecksum - rom[address] - 1);
        }

        rom[0x014D] = headerChecksum;

        var globalSum = 0;

        for (var address = 0; (address < rom.Length); address++) {
            if ((address == 0x014E) || (address == 0x014F)) {
                continue;
            }

            globalSum += rom[address];
        }

        rom[0x014E] = (byte)((globalSum >> 8) & 0xFF);
        rom[0x014F] = (byte)(globalSum & 0xFF);
    }

    // The shared prologue: stack, scroll to origin, LCD off, then load CGB palettes / tiles / tilemap and zero the
    // bank-1 attribute map. Leaves the LCD OFF and VBK = 0; the caller turns the LCD on and installs its own loop.
    private static void EmitSetup(Sm83Emitter emitter, int tileByteCount) {
        emitter.DisableInterrupts();
        emitter.LoadStackPointer(value: 0xFFFE);

        emitter.XorA();
        emitter.StoreAToHighPage(port: PortScrollY);
        emitter.StoreAToHighPage(port: PortScrollX);

        emitter.XorA();
        emitter.StoreAToHighPage(port: PortLcdControl); // LCD off (VRAM writes unrestricted).

        emitter.XorA();
        emitter.StoreAToHighPage(port: PortVramBank); // Bank 0 for tiles + map.

        emitter.LoadAImmediate(value: PaletteAutoIncrementFromZero);
        emitter.StoreAToHighPage(port: PortBackgroundPaletteIndex);
        EmitPaletteCopy(emitter: emitter, sourceAddress: BackgroundPaletteAddress, dataPort: PortBackgroundPaletteData);

        emitter.LoadAImmediate(value: PaletteAutoIncrementFromZero);
        emitter.StoreAToHighPage(port: PortObjectPaletteIndex);
        EmitPaletteCopy(emitter: emitter, sourceAddress: ObjectPaletteAddress, dataPort: PortObjectPaletteData);

        EmitBlockCopy(emitter: emitter, sourceAddress: TileDataAddress, destinationAddress: VramTiles, byteCount: (ushort)tileByteCount);
        EmitBlockCopy(emitter: emitter, sourceAddress: TileMapAddress, destinationAddress: VramBackgroundMap, byteCount: 0x0400);

        emitter.LoadAImmediate(value: 0x01);
        emitter.StoreAToHighPage(port: PortVramBank); // Bank 1: attributes.
        EmitBlockFill(emitter: emitter, destinationAddress: VramAttributeMapBank1, byteCount: 0x0400);
        emitter.XorA();
        emitter.StoreAToHighPage(port: PortVramBank); // Back to bank 0.
    }

    private static byte[] BuildStaticRoutine(int tileByteCount, int objectByteCount) {
        var emitter = new Sm83Emitter();

        EmitSetup(emitter: emitter, tileByteCount: tileByteCount);
        EmitBlockCopy(emitter: emitter, sourceAddress: ObjectAttributeAddress, destinationAddress: ObjectAttributeMemory, byteCount: (ushort)objectByteCount);

        emitter.LoadAImmediate(value: LcdControlStatic);
        emitter.StoreAToHighPage(port: PortLcdControl);

        var hang = emitter.NewLabel();

        emitter.MarkLabel(label: hang);
        emitter.JumpRelative(label: hang);

        return emitter.ToArray();
    }

    // The BIDIRECTIONAL world-lens/membrane loop. Each frame it reads the authority baton and drives the single 8×16
    // sprite (OAM 0) from ONE "game tile": in WORLD authority the game tile mirrors the room sensor position (walking —
    // and a seamless hand-off, since the game tile is kept current); in GAME authority the ROM reads the joypad and
    // integrates the game tile itself. Either way it places the sprite from the game tile, writes the game tile back out
    // for the host to follow (machine→world), and checks the win goal. Runs its body ONCE per frame (two-phase wait:
    // active render THEN VBlank) — a single VBlank wait would spin many times per frame and integrate the joypad dozens
    // of times, flinging the sprite.
    private static byte[] BuildWorldLensRoutine(int tileByteCount, byte playerTileBase, byte goalTileX, byte goalTileY) {
        var emitter = new Sm83Emitter();

        EmitSetup(emitter: emitter, tileByteCount: tileByteCount);

        emitter.LoadAImmediate(value: LcdControlWorldLens);
        emitter.StoreAToHighPage(port: PortLcdControl);

        var loop = emitter.NewLabel();
        var waitRender = emitter.NewLabel();
        var waitVBlank = emitter.NewLabel();
        var gameAuthority = emitter.NewLabel();
        var place = emitter.NewLabel();
        var notWon = emitter.NewLabel();

        emitter.MarkLabel(label: loop);

        // Once per frame: wait for active render (LY < 144), then for the VBlank edge (LY >= 144).
        emitter.MarkLabel(label: waitRender);
        emitter.LoadAFromHighPage(port: PortScanline);
        emitter.CompareImmediate(value: VBlankScanline);
        emitter.JumpRelativeIfNoCarry(label: waitRender); // while LY >= 144
        emitter.MarkLabel(label: waitVBlank);
        emitter.LoadAFromHighPage(port: PortScanline);
        emitter.CompareImmediate(value: VBlankScanline);
        emitter.JumpRelativeIfCarry(label: waitVBlank); // while LY < 144

        // authority != WORLD (non-zero) -> the game drives; else mirror the world sensor position into the game tile.
        emitter.LoadAFromAddress(address: WorldLensProtocol.AuthorityAddress);
        emitter.OrA();
        emitter.JumpRelativeIfNotZero(label: gameAuthority);

        // WORLD: game tile := sensor position (mirror + reconcile, so a hand-off starts where the world left off).
        emitter.LoadAFromAddress(address: WorldLensProtocol.PlayerTileXAddress);
        emitter.StoreAToAddress(address: WorldLensProtocol.GameTileXAddress);
        emitter.LoadAFromAddress(address: WorldLensProtocol.PlayerTileYAddress);
        emitter.StoreAToAddress(address: WorldLensProtocol.GameTileYAddress);
        emitter.JumpAbsolute(label: place); // absolute: the game-auth block below can exceed a relative jr's range

        // GAME: select the D-pad, read it (active-low; 0 = pressed), integrate the game tile by held direction.
        emitter.MarkLabel(label: gameAuthority);

        // Rate-limit: advance the sprite ONE tile every MoveCooldownFrames frames (gated on a cheap AND against the
        // power-of-two-minus-one). Without this the sprite steps every frame (~60 tiles/s) and the followed avatar
        // blurs across the room. On the off frames, fall straight through to placing the sprite where it already is.
        emitter.LoadAFromAddress(address: WorldLensProtocol.MoveCooldownAddress);
        emitter.IncrementA();
        emitter.StoreAToAddress(address: WorldLensProtocol.MoveCooldownAddress);
        emitter.AndImmediate(value: (byte)(WorldLensProtocol.MoveCooldownFrames - 1));
        emitter.JumpRelativeIfNotZero(label: place);

        emitter.LoadAImmediate(value: 0x20); // select direction keys (P14 low)
        emitter.StoreAToHighPage(port: PortJoypad);
        emitter.LoadAFromHighPage(port: PortJoypad);
        emitter.LoadAFromHighPage(port: PortJoypad); // read twice to let the lines settle
        emitter.LoadBFromA();

        EmitDpadStep(emitter: emitter, bit: 0, address: WorldLensProtocol.GameTileXAddress, increment: true);  // Right -> X+
        EmitDpadStep(emitter: emitter, bit: 1, address: WorldLensProtocol.GameTileXAddress, increment: false); // Left  -> X-
        EmitDpadStep(emitter: emitter, bit: 2, address: WorldLensProtocol.GameTileYAddress, increment: false); // Up    -> Y-
        EmitDpadStep(emitter: emitter, bit: 3, address: WorldLensProtocol.GameTileYAddress, increment: true);  // Down  -> Y+

        // Clamp the integrated game tile to the reachable window so a held direction stops at the edge instead of
        // running the byte past the mapped range and WRAPPING (which teleports the followed avatar edge to edge).
        EmitClampTile(emitter: emitter, address: WorldLensProtocol.GameTileXAddress, minimum: WorldLensProtocol.MinTile, maximum: WorldLensProtocol.MaxTileX);
        EmitClampTile(emitter: emitter, address: WorldLensProtocol.GameTileYAddress, minimum: WorldLensProtocol.MinTile, maximum: WorldLensProtocol.MaxTileY);

        // Place the sprite (OAM 0) from the game tile: Y = tile*8 + 16, X = tile*8 + 8.
        emitter.MarkLabel(label: place);
        emitter.LoadAFromAddress(address: WorldLensProtocol.GameTileYAddress);
        emitter.AddAToA();
        emitter.AddAToA();
        emitter.AddAToA();
        emitter.AddImmediate(value: 16);
        emitter.StoreAToAddress(address: ObjectAttributeMemory + 0);
        emitter.LoadAFromAddress(address: WorldLensProtocol.GameTileXAddress);
        emitter.AddAToA();
        emitter.AddAToA();
        emitter.AddAToA();
        emitter.AddImmediate(value: 8);
        emitter.StoreAToAddress(address: ObjectAttributeMemory + 1);
        emitter.LoadAImmediate(value: playerTileBase);
        emitter.StoreAToAddress(address: ObjectAttributeMemory + 2);
        emitter.XorA();
        emitter.StoreAToAddress(address: ObjectAttributeMemory + 3);

        // Win check on the game tile: reaching the goal from EITHER authority raises the flag.
        emitter.LoadAFromAddress(address: WorldLensProtocol.GameTileXAddress);
        emitter.CompareImmediate(value: goalTileX);
        emitter.JumpRelativeIfNotZero(label: notWon);
        emitter.LoadAFromAddress(address: WorldLensProtocol.GameTileYAddress);
        emitter.CompareImmediate(value: goalTileY);
        emitter.JumpRelativeIfNotZero(label: notWon);
        emitter.LoadAImmediate(value: WorldLensProtocol.WinMagic);
        emitter.StoreAToAddress(address: WorldLensProtocol.WinFlagAddress);
        emitter.MarkLabel(label: notWon);

        // The bidirectional body is over 127 bytes, so the loop back-edge is an ABSOLUTE jump (resolved against the
        // routine's load address); the short forward branches inside stay relative.
        emitter.JumpAbsolute(label: loop);

        return emitter.ToArray(baseAddress: MainRoutine);
    }

    // Clamp the byte at [address] to [minimum, maximum]: if it exceeds the max, snap to max; if below the min, snap to
    // min. `cp n` sets carry when A < n, so `cp max+1` is carry ⇔ A ≤ max, and `cp min` is carry ⇔ A < min.
    private static void EmitClampTile(Sm83Emitter emitter, ushort address, byte minimum, byte maximum) {
        var belowMax = emitter.NewLabel();
        var aboveMin = emitter.NewLabel();

        emitter.LoadAFromAddress(address: address);
        emitter.CompareImmediate(value: (byte)(maximum + 1));
        emitter.JumpRelativeIfCarry(label: belowMax); // A <= max → leave it
        emitter.LoadAImmediate(value: maximum);
        emitter.StoreAToAddress(address: address);
        emitter.MarkLabel(label: belowMax);

        emitter.LoadAFromAddress(address: address);
        emitter.CompareImmediate(value: minimum);
        emitter.JumpRelativeIfNoCarry(label: aboveMin); // A >= min → leave it
        emitter.LoadAImmediate(value: minimum);
        emitter.StoreAToAddress(address: address);
        emitter.MarkLabel(label: aboveMin);
    }

    // if (D-pad bit is PRESSED — active-low, so the bit is 0) { [address] += (increment ? 1 : -1); }
    private static void EmitDpadStep(Sm83Emitter emitter, int bit, ushort address, bool increment) {
        var skip = emitter.NewLabel();

        emitter.TestBitOfB(bit: bit);
        emitter.JumpRelativeIfNotZero(label: skip); // bit set => not pressed => skip
        emitter.LoadAFromAddress(address: address);

        if (increment) {
            emitter.IncrementA();
        } else {
            emitter.DecrementA();
        }

        emitter.StoreAToAddress(address: address);
        emitter.MarkLabel(label: skip);
    }

    private static void EmitPaletteCopy(Sm83Emitter emitter, ushort sourceAddress, byte dataPort) {
        var loop = emitter.NewLabel();

        emitter.LoadHlImmediate(value: sourceAddress);
        emitter.LoadBImmediate(value: 8);
        emitter.MarkLabel(label: loop);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToHighPage(port: dataPort);
        emitter.DecrementB();
        emitter.JumpRelativeIfNotZero(label: loop);
    }

    private static void EmitBlockCopy(Sm83Emitter emitter, ushort sourceAddress, ushort destinationAddress, ushort byteCount) {
        var loop = emitter.NewLabel();

        emitter.LoadHlImmediate(value: sourceAddress);
        emitter.LoadDeImmediate(value: destinationAddress);
        emitter.LoadBcImmediate(value: byteCount);
        emitter.MarkLabel(label: loop);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToDe();
        emitter.IncrementDe();
        emitter.DecrementBc();
        emitter.LoadAFromB();
        emitter.OrC();
        emitter.JumpRelativeIfNotZero(label: loop);
    }

    private static void EmitBlockFill(Sm83Emitter emitter, ushort destinationAddress, ushort byteCount) {
        var loop = emitter.NewLabel();

        emitter.LoadDeImmediate(value: destinationAddress);
        emitter.LoadBcImmediate(value: byteCount);
        emitter.MarkLabel(label: loop);
        emitter.XorA();
        emitter.StoreAToDe();
        emitter.IncrementDe();
        emitter.DecrementBc();
        emitter.LoadAFromB();
        emitter.OrC();
        emitter.JumpRelativeIfNotZero(label: loop);
    }
}
