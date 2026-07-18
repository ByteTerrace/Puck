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
    // each other). The palette + OAM tables sit ABOVE the tilemap (0x1C00+, free ROM) rather than just past the header,
    // so the display routine has the whole 0x0150..0x0400 window (688 bytes) — the overworld's walk/animation routine
    // needs far more than the original 0x0150..0x0300 slot. Tiles cap at (TileMapAddress − TileDataAddress) = 0x1400
    // bytes = 320 tiles.
    private const ushort TileDataAddress = 0x0400;
    private const ushort BackgroundPaletteAddress = 0x1C00;
    private const ushort ObjectAttributeAddress = 0x1C10;
    private const ushort ObjectPaletteAddress = 0x1C08;
    private const ushort TileMapAddress = 0x1800;
    private const ushort VramBackgroundMap = 0x9800;
    private const ushort VramTiles = 0x8000;
    private const ushort VramAttributeMapBank1 = 0x9800; // Same address, read with VBK = 1.
    private const ushort ObjectAttributeMemory = 0xFE00;
    private const byte PortJoypad = 0x00; // P1/JOYP: write 0x20 to select the direction keys, then read them (active-low).
    private const byte PortLcdControl = 0x40;
    private const byte PortScrollX = 0x43;
    private const byte PortScrollY = 0x42;
    private const byte PortScanline = 0x44; // LY.
    private const byte PortBackgroundPaletteData = 0x69;
    private const byte PortBackgroundPaletteIndex = 0x68;
    private const byte PortObjectPaletteData = 0x6B;
    private const byte PortObjectPaletteIndex = 0x6A;
    private const byte PortVramBank = 0x4F;

    // LCDC bases: LCD on (0x80) | BG tile data at 0x8000 (0x10) | OBJ on (0x02) | BG on (0x01). The static scene uses
    // 8×8 objects (0x93); the world-lens uses 8×16 (adds 0x04 → 0x97) so one OAM entry is a 2-tile-tall character.
    private const byte LcdControlStatic = 0x93;
    private const byte LcdControlWorldLens = 0x97;
    private const byte PaletteAutoIncrementFromZero = 0x80;
    private const byte VBlankScanline = 144;

    // The OVERWORLD routine's work-RAM state + layout — the single source of truth is OverworldProtocol (shared with
    // the forge's self-verification). Aliased here so the routine below reads locally.
    private const ushort OverworldPlayerX = OverworldProtocol.PlayerXAddress;
    private const byte FacingDown = OverworldProtocol.FacingDown;
    private const byte FacingLeft = OverworldProtocol.FacingLeft;
    private const byte FacingRight = OverworldProtocol.FacingRight;
    private const byte FacingUp = OverworldProtocol.FacingUp;
    private const ushort OverworldAnimTimer = OverworldProtocol.AnimTimerAddress;
    private const ushort OverworldFacing = OverworldProtocol.FacingAddress;
    private const byte OverworldMaxX = OverworldProtocol.MaxX;
    private const byte OverworldMaxY = OverworldProtocol.MaxY;
    private const byte OverworldMinX = OverworldProtocol.MinX;
    private const byte OverworldMinY = OverworldProtocol.MinY;
    private const ushort OverworldMoving = OverworldProtocol.MovingAddress;
    private const ushort OverworldPlayerY = OverworldProtocol.PlayerYAddress;
    private const byte OverworldStartX = OverworldProtocol.StartX;
    private const byte OverworldStartY = OverworldProtocol.StartY;
    private const ushort OverworldTileScratch = OverworldProtocol.TileScratchAddress;
    private const int OverworldTilesPerPose = OverworldProtocol.TilesPerPose;
    private const byte OverworldWalkSpeed = OverworldProtocol.WalkSpeed;

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

    /// <summary>Builds the OVERWORLD cartridge: a forged room the player's own avatar walks around, in the classic
    /// top-down RPG style. The d-pad moves the 16×16 metasprite (four 8×8 sprites) in pixels, sets its facing, and animates a walk
    /// cycle while moving; the sprite tiles are the forged avatar sheet, appended after the room tiles at
    /// <paramref name="spriteTileBase"/> — pose (facing × 3 + frame) occupies tiles <c>spriteTileBase + pose*4</c>.</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <param name="backgroundPalette">The room's 8-byte BG palette.</param>
    /// <param name="objectPalette">The avatar sheet's 8-byte OBJ palette (slot 0 transparent).</param>
    /// <param name="tileData">The room BG tiles followed by all <see cref="AvatarForge.PoseCount"/> poses' tiles.</param>
    /// <param name="tileMap">The room's 32×32 tilemap.</param>
    /// <param name="spriteTileBase">The VRAM tile index the first pose's tiles start at (the room BG tile count).</param>
    /// <param name="movementMode">The d-pad direction lock (default <see cref="MovementMode.FourWay"/> — today's
    /// behaviour, byte-identical to every ROM forged before this parameter existed).</param>
    /// <exception cref="ArgumentOutOfRangeException">The pose tiles do not fit the single-byte VRAM tile budget.</exception>
    public static byte[] BuildOverworld(string title, byte[] backgroundPalette, byte[] objectPalette, byte[] tileData, byte[] tileMap, int spriteTileBase, MovementMode movementMode = MovementMode.FourWay) {
        // The highest tile the routine references is spriteTileBase + (poses-1)*4 + 3; it must fit a byte.
        var highestTile = ((spriteTileBase + ((AvatarForge.PoseCount - 1) * OverworldTilesPerPose)) + (OverworldTilesPerPose - 1));

        if ((spriteTileBase < 0) || (highestTile > 255)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(spriteTileBase), message: $"The avatar sheet (base {spriteTileBase}, top tile {highestTile}) does not fit the 256-tile VRAM budget — the room forged too many tiles.");
        }

        var rom = NewRomWithHeader(title: title);

        PlaceData(rom: rom, backgroundPalette: backgroundPalette, objectPalette: objectPalette, tileData: tileData, tileMap: tileMap);
        PlaceRoutine(rom: rom, routine: BuildOverworldRoutine(tileByteCount: tileData.Length, spriteTileBase: (byte)spriteTileBase, movementMode: movementMode));
        Finalize(rom: rom);

        return rom;
    }

    private static byte[] NewRomWithHeader(string title) {
        ArgumentException.ThrowIfNullOrEmpty(title);

        var rom = new byte[RomSize];

        // Entry point (0x0100): nop; jp MainRoutine.
        rom[EntryPoint] = 0x00;
        rom[(EntryPoint + 1)] = 0xC3;
        rom[(EntryPoint + 2)] = (byte)(MainRoutine & 0xFF);
        rom[(EntryPoint + 3)] = (byte)((MainRoutine >> 8) & 0xFF);

        BootLogo.CopyTo(array: rom, index: 0x0104);

        var titleBytes = Encoding.ASCII.GetBytes(s: title.ToUpperInvariant());

        for (var index = 0; ((index < titleBytes.Length) && (index < 15)); index++) {
            rom[(0x0134 + index)] = titleBytes[index];
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

        if (((int)TileDataAddress + tileData.Length) > TileMapAddress) {
            throw new ArgumentException(message: $"The tile data ({tileData.Length} bytes) overruns the fixed tilemap slot at 0x{TileMapAddress:X4}.", paramName: nameof(tileData));
        }

        backgroundPalette.CopyTo(array: rom, index: BackgroundPaletteAddress);
        objectPalette.CopyTo(array: rom, index: ObjectPaletteAddress);
        tileData.CopyTo(array: rom, index: TileDataAddress);
        tileMap.CopyTo(array: rom, index: TileMapAddress);
    }
    private static void PlaceRoutine(byte[] rom, byte[] routine) {
        if (((int)MainRoutine + routine.Length) > TileDataAddress) {
            throw new InvalidOperationException(message: $"The display routine ({routine.Length} bytes) overran the tile-data region at 0x{TileDataAddress:X4}.");
        }

        routine.CopyTo(array: rom, index: MainRoutine);
    }
    private static void Finalize(byte[] rom) {
        byte headerChecksum = 0;

        for (var address = 0x0134; (address <= 0x014C); address++) {
            headerChecksum = (byte)((headerChecksum - rom[address]) - 1);
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
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: VBlankScanline);
        emitter.JumpRelative(condition: Condition.NoCarry, label: waitRender); // while LY >= 144
        emitter.MarkLabel(label: waitVBlank);
        emitter.LoadAFromHighPage(port: PortScanline);
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: VBlankScanline);
        emitter.JumpRelative(condition: Condition.Carry, label: waitVBlank); // while LY < 144

        // authority != WORLD (non-zero) -> the game drives; else mirror the world sensor position into the game tile.
        emitter.LoadAFromAddress(address: WorldLensProtocol.AuthorityAddress);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        emitter.JumpRelative(condition: Condition.NotZero, label: gameAuthority);

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
        emitter.Increment(register: Reg8.A);
        emitter.StoreAToAddress(address: WorldLensProtocol.MoveCooldownAddress);
        emitter.ArithmeticImmediate(op: AluOp.And, value: (byte)(WorldLensProtocol.MoveCooldownFrames - 1));
        emitter.JumpRelative(condition: Condition.NotZero, label: place);

        emitter.LoadAImmediate(value: 0x20); // select direction keys (P14 low)
        emitter.StoreAToHighPage(port: PortJoypad);
        emitter.LoadAFromHighPage(port: PortJoypad);
        emitter.LoadAFromHighPage(port: PortJoypad); // read twice to let the lines settle
        emitter.Load(destination: Reg8.B, source: Reg8.A);

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
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);
        emitter.ArithmeticImmediate(op: AluOp.Add, value: 16);
        emitter.StoreAToAddress(address: (ObjectAttributeMemory + 0));
        emitter.LoadAFromAddress(address: WorldLensProtocol.GameTileXAddress);
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);
        emitter.ArithmeticImmediate(op: AluOp.Add, value: 8);
        emitter.StoreAToAddress(address: (ObjectAttributeMemory + 1));
        emitter.LoadAImmediate(value: playerTileBase);
        emitter.StoreAToAddress(address: (ObjectAttributeMemory + 2));
        emitter.XorA();
        emitter.StoreAToAddress(address: (ObjectAttributeMemory + 3));

        // Win check on the game tile: reaching the goal from EITHER authority raises the flag.
        emitter.LoadAFromAddress(address: WorldLensProtocol.GameTileXAddress);
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: goalTileX);
        emitter.JumpRelative(condition: Condition.NotZero, label: notWon);
        emitter.LoadAFromAddress(address: WorldLensProtocol.GameTileYAddress);
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: goalTileY);
        emitter.JumpRelative(condition: Condition.NotZero, label: notWon);
        emitter.LoadAImmediate(value: WorldLensProtocol.WinMagic);
        emitter.StoreAToAddress(address: WorldLensProtocol.WinFlagAddress);
        emitter.MarkLabel(label: notWon);

        // The bidirectional body is over 127 bytes, so the loop back-edge is an ABSOLUTE jump (resolved against the
        // routine's load address); the short forward branches inside stay relative.
        emitter.JumpAbsolute(label: loop);

        return emitter.ToArray(baseAddress: MainRoutine);
    }

    // The OVERWORLD walker: the player's own forged avatar roams a forged room. Each frame (two-phase VBlank wait, like
    // the world-lens) it reads the d-pad, moves the 16×16 metasprite in pixels + records the facing it last moved,
    // clamps to the reachable window, then picks the pose — a neutral idle when still, or a two-frame walk cycle
    // (toggled off the animation timer's bit 3, ~8 frames per step) when moving — and rewrites OAM 0..3 to the pose's
    // four tiles. The sprite-sheet pose index is facing × 3 + frame; its tiles start at spriteTileBase + pose*4.
    // movementMode selects the d-pad direction lock (MovementModule); FourWay emits the ORIGINAL four independent
    // per-axis steps unchanged (the byte-identical regression bar), EightWay/Hex dispatch to their own emission.
    private static byte[] BuildOverworldRoutine(int tileByteCount, byte spriteTileBase, MovementMode movementMode = MovementMode.FourWay) {
        var emitter = new Sm83Emitter();

        EmitSetup(emitter: emitter, tileByteCount: tileByteCount);

        // Clear all 40 OAM entries (LCD is still off, so OAM is freely writable) — the routine only drives sprites 0..3,
        // and the seeded post-boot OAM is otherwise indeterminate garbage that would flicker across the screen.
        EmitBlockFill(emitter: emitter, destinationAddress: ObjectAttributeMemory, byteCount: 0xA0);

        emitter.LoadAImmediate(value: LcdControlStatic); // LCD on, 8×8 objects
        emitter.StoreAToHighPage(port: PortLcdControl);

        // Seed the player state (WRAM is indeterminate at the seeded post-boot handoff).
        emitter.LoadAImmediate(value: OverworldStartX);
        emitter.StoreAToAddress(address: OverworldPlayerX);
        emitter.LoadAImmediate(value: OverworldStartY);
        emitter.StoreAToAddress(address: OverworldPlayerY);
        emitter.XorA();
        emitter.StoreAToAddress(address: OverworldFacing);
        emitter.XorA();
        emitter.StoreAToAddress(address: OverworldAnimTimer);

        var loop = emitter.NewLabel();
        var waitRender = emitter.NewLabel();
        var waitVBlank = emitter.NewLabel();
        var moving = emitter.NewLabel();
        var stepB = emitter.NewLabel();
        var frameDone = emitter.NewLabel();

        emitter.MarkLabel(label: loop);

        // Once per frame: wait for active render (LY < 144), then for the VBlank edge (LY >= 144).
        emitter.MarkLabel(label: waitRender);
        emitter.LoadAFromHighPage(port: PortScanline);
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: VBlankScanline);
        emitter.JumpRelative(condition: Condition.NoCarry, label: waitRender); // while LY >= 144
        emitter.MarkLabel(label: waitVBlank);
        emitter.LoadAFromHighPage(port: PortScanline);
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: VBlankScanline);
        emitter.JumpRelative(condition: Condition.Carry, label: waitVBlank); // while LY < 144

        // Advance the animation timer; assume "not moving" until a held direction proves otherwise.
        emitter.LoadAFromAddress(address: OverworldAnimTimer);
        emitter.Increment(register: Reg8.A);
        emitter.StoreAToAddress(address: OverworldAnimTimer);
        emitter.XorA();
        emitter.StoreAToAddress(address: OverworldMoving);

        // Select + read the D-pad (active-low; 0 = pressed) into B.
        emitter.LoadAImmediate(value: 0x20);
        emitter.StoreAToHighPage(port: PortJoypad);
        emitter.LoadAFromHighPage(port: PortJoypad);
        emitter.LoadAFromHighPage(port: PortJoypad); // read twice to let the lines settle
        emitter.Load(destination: Reg8.B, source: Reg8.A);

        switch (movementMode) {
            case MovementMode.EightWay:
                EmitOverworldEightWayMovement(emitter: emitter);
                break;
            case MovementMode.Hex:
                EmitOverworldHexMovement(emitter: emitter);
                break;
            case MovementMode.FourWay:
            default:
                // Unchanged since before MovementMode existed — the byte-identical regression bar.
                EmitOverworldMoveStep(emitter: emitter, bit: 0, address: OverworldPlayerX, increment: true, facing: FacingRight);  // Right
                EmitOverworldMoveStep(emitter: emitter, bit: 1, address: OverworldPlayerX, increment: false, facing: FacingLeft);  // Left
                EmitOverworldMoveStep(emitter: emitter, bit: 2, address: OverworldPlayerY, increment: false, facing: FacingUp);    // Up
                EmitOverworldMoveStep(emitter: emitter, bit: 3, address: OverworldPlayerY, increment: true, facing: FacingDown);   // Down
                break;
        }

        EmitClampTile(emitter: emitter, address: OverworldPlayerX, minimum: OverworldMinX, maximum: OverworldMaxX);
        EmitClampTile(emitter: emitter, address: OverworldPlayerY, minimum: OverworldMinY, maximum: OverworldMaxY);

        // Pick the animation frame (0/1/2) into A: idle when still; the timer's bit 3 toggles step A/B while moving.
        emitter.LoadAFromAddress(address: OverworldMoving);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        emitter.JumpRelative(condition: Condition.NotZero, label: moving);
        emitter.XorA();                       // idle → frame 0
        emitter.JumpRelative(label: frameDone);
        emitter.MarkLabel(label: moving);
        emitter.LoadAFromAddress(address: OverworldAnimTimer);
        emitter.Load(destination: Reg8.B, source: Reg8.A);
        emitter.TestBit(register: Reg8.B, bit: 3);           // Z = 1 when bit 3 is 0
        emitter.JumpRelative(condition: Condition.NotZero, label: stepB); // bit 3 set → step B (frame 2)
        emitter.LoadAImmediate(value: 1);     // bit 3 clear → step A (frame 1)
        emitter.JumpRelative(label: frameDone);
        emitter.MarkLabel(label: stepB);
        emitter.LoadAImmediate(value: 2);
        emitter.MarkLabel(label: frameDone);

        // pose = facing*3 + frame; tile base = spriteTileBase + pose*4. Frame is held in C while facing is multiplied.
        emitter.Load(destination: Reg8.C, source: Reg8.A);
        emitter.LoadAFromAddress(address: OverworldFacing);
        emitter.Load(destination: Reg8.B, source: Reg8.A);
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);                    // 2*facing
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.B);                    // 3*facing
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.C);                    // 3*facing + frame = pose
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);                    // 2*pose
        emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);                    // 4*pose
        emitter.ArithmeticImmediate(op: AluOp.Add, value: spriteTileBase);
        emitter.StoreAToAddress(address: OverworldTileScratch);

        // Rewrite the 2×2 metasprite (OAM 0..3): top-left, top-right, bottom-left, bottom-right. OAM Y = screenY + 16,
        // X = screenX + 8; the tiles are the pose's four ordered tiles (base + 0/1/2/3).
        EmitOverworldOamSprite(emitter: emitter, oamIndex: 0, yOffset: 16, xOffset: 8, tileDelta: 0);
        EmitOverworldOamSprite(emitter: emitter, oamIndex: 1, yOffset: 16, xOffset: 16, tileDelta: 1);
        EmitOverworldOamSprite(emitter: emitter, oamIndex: 2, yOffset: 24, xOffset: 8, tileDelta: 2);
        EmitOverworldOamSprite(emitter: emitter, oamIndex: 3, yOffset: 24, xOffset: 16, tileDelta: 3);

        // The body exceeds a relative jr's range, so the back-edge is an absolute jump (resolved against MainRoutine).
        emitter.JumpAbsolute(label: loop);

        return emitter.ToArray(baseAddress: MainRoutine);
    }

    // if (D-pad bit PRESSED — active-low) { move [address] by OverworldWalkSpeed pixels; facing := facing; moving := 1 }.
    private static void EmitOverworldMoveStep(Sm83Emitter emitter, int bit, ushort address, bool increment, byte facing) {
        var skip = emitter.NewLabel();

        emitter.TestBit(register: Reg8.B, bit: bit);
        emitter.JumpRelative(condition: Condition.NotZero, label: skip); // bit set => not pressed => skip

        emitter.LoadAFromAddress(address: address);

        for (var step = 0; (step < OverworldWalkSpeed); step++) {
            if (increment) {
                emitter.Increment(register: Reg8.A);
            } else {
                emitter.Decrement(register: Reg8.A);
            }
        }

        emitter.StoreAToAddress(address: address);
        emitter.LoadAImmediate(value: facing);
        emitter.StoreAToAddress(address: OverworldFacing);
        emitter.LoadAImmediate(value: 1);
        emitter.StoreAToAddress(address: OverworldMoving);
        emitter.MarkLabel(label: skip);
    }

    // EIGHTWAY: the same per-axis independent stepping as FourWay (so a held diagonal moves both axes at the full
    // cardinal delta — the classic faster-diagonal artifact, authentic brick-era behaviour, not a bug), but facing is
    // resolved SEPARATELY via MovementModule's horizontal-wins-ties rule instead of FourWay's last-bit-wins order.
    private static void EmitOverworldEightWayMovement(Sm83Emitter emitter) {
        MovementModule.EmitConditionalStep(emitter: emitter, bit: 0, address: OverworldPlayerX, delta: OverworldWalkSpeed, negative: false, movingAddress: OverworldMoving); // Right
        MovementModule.EmitConditionalStep(emitter: emitter, bit: 1, address: OverworldPlayerX, delta: OverworldWalkSpeed, negative: true, movingAddress: OverworldMoving);  // Left
        MovementModule.EmitConditionalStep(emitter: emitter, bit: 2, address: OverworldPlayerY, delta: OverworldWalkSpeed, negative: true, movingAddress: OverworldMoving);  // Up
        MovementModule.EmitConditionalStep(emitter: emitter, bit: 3, address: OverworldPlayerY, delta: OverworldWalkSpeed, negative: false, movingAddress: OverworldMoving); // Down

        MovementModule.EmitFacingResolve(emitter: emitter, facingAddress: OverworldFacing, facingRight: FacingRight, facingLeft: FacingLeft, facingUp: FacingUp, facingDown: FacingDown);
    }

    // HEX (pointy-top, matching the 3D side's hex walk grid): Left/Right are the pure west/east neighbors (full
    // OverworldWalkSpeed on X, matching the other modes' cardinal step); Up+Left/Up+Right/Down+Left/Down+Right are the
    // four 60° neighbors (MovementModule.HexDiagonalXStep/HexDiagonalYStep — a rational approximation of the 60° unit
    // vectors); a lone Up or Down (no Left/Right held) has NO neighbor to move to, so it is a no-op — a pointy-top hex
    // cell has no vertical edge. Facing follows the same horizontal-wins-ties rule as EightWay: a diagonal's
    // (X=1, Y=2) step has |dy| > |dx|, so the four diagonals face vertical (up/down), while a pure Left/Right faces
    // horizontal.
    private static void EmitOverworldHexMovement(Sm83Emitter emitter) {
        var left = emitter.NewLabel();
        var vertOnly = emitter.NewLabel();
        var upLeft = emitter.NewLabel();
        var downLeft = emitter.NewLabel();
        var plainLeft = emitter.NewLabel();
        var upRight = emitter.NewLabel();
        var downRight = emitter.NewLabel();
        var plainRight = emitter.NewLabel();
        var done = emitter.NewLabel();

        emitter.TestBit(register: Reg8.B, bit: 0); // Right
        emitter.JumpRelative(condition: Condition.Zero, label: plainRight); // 0 = pressed; Up/Down checked below first
        emitter.TestBit(register: Reg8.B, bit: 1); // Left
        emitter.JumpAbsolute(condition: Condition.Zero, label: left); // the right-side block below is too far for jr
        emitter.JumpAbsolute(label: vertOnly); // neither Left nor Right held; also too far for jr

        emitter.MarkLabel(label: plainRight);
        emitter.TestBit(register: Reg8.B, bit: 2); // Up
        emitter.JumpRelative(condition: Condition.Zero, label: upRight);
        emitter.TestBit(register: Reg8.B, bit: 3); // Down
        emitter.JumpRelative(condition: Condition.Zero, label: downRight);
        EmitHexStep(emitter: emitter, xDelta: MovementModule.HexAxisStep, xNegative: false, yDelta: 0, yNegative: false, facing: FacingRight);
        emitter.JumpAbsolute(label: done);

        emitter.MarkLabel(label: upRight);
        EmitHexStep(emitter: emitter, xDelta: MovementModule.HexDiagonalXStep, xNegative: false, yDelta: MovementModule.HexDiagonalYStep, yNegative: true, facing: FacingUp);
        emitter.JumpAbsolute(label: done);

        emitter.MarkLabel(label: downRight);
        EmitHexStep(emitter: emitter, xDelta: MovementModule.HexDiagonalXStep, xNegative: false, yDelta: MovementModule.HexDiagonalYStep, yNegative: false, facing: FacingDown);
        emitter.JumpAbsolute(label: done);

        emitter.MarkLabel(label: left);
        emitter.TestBit(register: Reg8.B, bit: 2); // Up
        emitter.JumpRelative(condition: Condition.Zero, label: upLeft);
        emitter.TestBit(register: Reg8.B, bit: 3); // Down
        emitter.JumpRelative(condition: Condition.Zero, label: downLeft);
        emitter.JumpRelative(label: plainLeft);

        emitter.MarkLabel(label: upLeft);
        EmitHexStep(emitter: emitter, xDelta: MovementModule.HexDiagonalXStep, xNegative: true, yDelta: MovementModule.HexDiagonalYStep, yNegative: true, facing: FacingUp);
        emitter.JumpAbsolute(label: done);

        emitter.MarkLabel(label: downLeft);
        EmitHexStep(emitter: emitter, xDelta: MovementModule.HexDiagonalXStep, xNegative: true, yDelta: MovementModule.HexDiagonalYStep, yNegative: false, facing: FacingDown);
        emitter.JumpAbsolute(label: done);

        emitter.MarkLabel(label: plainLeft);
        EmitHexStep(emitter: emitter, xDelta: MovementModule.HexAxisStep, xNegative: true, yDelta: 0, yNegative: false, facing: FacingLeft);
        emitter.JumpAbsolute(label: done);

        // vertOnly: neither Left nor Right held — a lone Up or Down (or nothing) never moves in the hex lock.
        emitter.MarkLabel(label: vertOnly);
        emitter.MarkLabel(label: done);
    }

    // Applies one resolved hex step: X += (xNegative ? -xDelta : xDelta), same for Y (skipped when its delta is 0),
    // sets facing, and raises the moving flag. A zero delta emits no arithmetic for that axis at all.
    private static void EmitHexStep(Sm83Emitter emitter, byte xDelta, bool xNegative, byte yDelta, bool yNegative, byte facing) {
        EmitHexAxisStep(emitter: emitter, address: OverworldPlayerX, delta: xDelta, negative: xNegative);
        EmitHexAxisStep(emitter: emitter, address: OverworldPlayerY, delta: yDelta, negative: yNegative);

        emitter.LoadAImmediate(value: facing);
        emitter.StoreAToAddress(address: OverworldFacing);
        emitter.LoadAImmediate(value: 1);
        emitter.StoreAToAddress(address: OverworldMoving);
    }
    private static void EmitHexAxisStep(Sm83Emitter emitter, ushort address, byte delta, bool negative) {
        if (delta == 0) {
            return;
        }

        emitter.LoadAFromAddress(address: address);

        for (var step = 0; (step < delta); step++) {
            if (negative) {
                emitter.Decrement(register: Reg8.A);
            } else {
                emitter.Increment(register: Reg8.A);
            }
        }

        emitter.StoreAToAddress(address: address);
    }

    // Writes one OAM entry of the player metasprite: Y = PlayerY + yOffset, X = PlayerX + xOffset, tile = scratch base +
    // tileDelta, flags = 0.
    private static void EmitOverworldOamSprite(Sm83Emitter emitter, int oamIndex, byte yOffset, byte xOffset, byte tileDelta) {
        var entry = (ushort)(ObjectAttributeMemory + (oamIndex * 4));

        emitter.LoadAFromAddress(address: OverworldPlayerY);
        emitter.ArithmeticImmediate(op: AluOp.Add, value: yOffset);
        emitter.StoreAToAddress(address: (ushort)(entry + 0));

        emitter.LoadAFromAddress(address: OverworldPlayerX);
        emitter.ArithmeticImmediate(op: AluOp.Add, value: xOffset);
        emitter.StoreAToAddress(address: (ushort)(entry + 1));

        emitter.LoadAFromAddress(address: OverworldTileScratch);

        if (tileDelta > 0) {
            emitter.ArithmeticImmediate(op: AluOp.Add, value: tileDelta);
        }

        emitter.StoreAToAddress(address: (ushort)(entry + 2));

        emitter.XorA();
        emitter.StoreAToAddress(address: (ushort)(entry + 3));
    }

    // Clamp the byte at [address] to [minimum, maximum]: if it exceeds the max, snap to max; if below the min, snap to
    // min. `cp n` sets carry when A < n, so `cp max+1` is carry ⇔ A ≤ max, and `cp min` is carry ⇔ A < min.
    private static void EmitClampTile(Sm83Emitter emitter, ushort address, byte minimum, byte maximum) {
        var belowMax = emitter.NewLabel();
        var aboveMin = emitter.NewLabel();

        emitter.LoadAFromAddress(address: address);
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(maximum + 1));
        emitter.JumpRelative(condition: Condition.Carry, label: belowMax); // A <= max → leave it
        emitter.LoadAImmediate(value: maximum);
        emitter.StoreAToAddress(address: address);
        emitter.MarkLabel(label: belowMax);

        emitter.LoadAFromAddress(address: address);
        emitter.ArithmeticImmediate(op: AluOp.Compare, value: minimum);
        emitter.JumpRelative(condition: Condition.NoCarry, label: aboveMin); // A >= min → leave it
        emitter.LoadAImmediate(value: minimum);
        emitter.StoreAToAddress(address: address);
        emitter.MarkLabel(label: aboveMin);
    }

    // if (D-pad bit is PRESSED — active-low, so the bit is 0) { [address] += (increment ? 1 : -1); }
    private static void EmitDpadStep(Sm83Emitter emitter, int bit, ushort address, bool increment) {
        var skip = emitter.NewLabel();

        emitter.TestBit(register: Reg8.B, bit: bit);
        emitter.JumpRelative(condition: Condition.NotZero, label: skip); // bit set => not pressed => skip
        emitter.LoadAFromAddress(address: address);

        if (increment) {
            emitter.Increment(register: Reg8.A);
        } else {
            emitter.Decrement(register: Reg8.A);
        }

        emitter.StoreAToAddress(address: address);
        emitter.MarkLabel(label: skip);
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
    private static void EmitBlockCopy(Sm83Emitter emitter, ushort sourceAddress, ushort destinationAddress, ushort byteCount) {
        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: sourceAddress);
        emitter.LoadImmediate(pair: Reg16.De, value: destinationAddress);
        emitter.LoadImmediate(pair: Reg16.Bc, value: byteCount);
        emitter.MarkLabel(label: loop);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToDe();
        emitter.Increment(pair: Reg16.De);
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(destination: Reg8.A, source: Reg8.B);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.C);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }
    private static void EmitBlockFill(Sm83Emitter emitter, ushort destinationAddress, ushort byteCount) {
        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.De, value: destinationAddress);
        emitter.LoadImmediate(pair: Reg16.Bc, value: byteCount);
        emitter.MarkLabel(label: loop);
        emitter.XorA();
        emitter.StoreAToDe();
        emitter.Increment(pair: Reg16.De);
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(destination: Reg8.A, source: Reg8.B);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.C);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }
}
