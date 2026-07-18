using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Shared plumbing for driving the cross-gen trade cart through a scripted Cable Club trade: building a Cgb machine with
/// a crafted <see cref="TradeSaveFactory"/> save already in its SRAM, and the handful of HRAM/SRAM peeks the peek-gated
/// phase machine watches (the honest way to gate on in-game progress without the cartridge's cooperation — plain
/// <see cref="ISystemBus.ReadByte"/> reads, exactly as the serial-link stages read their synthetic ROM's verdict RAM).
/// </summary>
internal static class ScriptedTradeHarness {
    // hSerialConnectionStatus — HRAM $FFCD. Reads $01 (USING_EXTERNAL_CLOCK, slave) on exactly one
    // machine and $02 (USING_INTERNAL_CLOCK, master) on the other once WaitForLinkedFriend resolves; $FF
    // (CONNECTION_NOT_ESTABLISHED) before. The symmetry-break assertion reads this canonical HRAM address directly,
    // rather than relying on a derived WRAM offset.
    public const ushort SerialConnectionStatusAddress = 0xFFCD;

    // The LIVE (WRAM) copy of the map-position block for the trade cart (USA) — group / map / Y / X in four contiguous
    // bytes, mirroring the SRAM layout (sav 0x2868..0x286B are contiguous). Empirically located at WRAM 0xDA00 by
    // the --trade-explore scan (a fresh CONTINUE lands this exact 4-byte signature 0x14,0x01,0x03,0x05 there), and
    // cross-checked against that contiguity. This cart's WRAM addresses are specific to it, so this is pinned to it.
    /// <summary>The live WRAM address of wMapGroup (the trade cart, USA); wMapNumber/wYCoord/wXCoord follow contiguously.</summary>
    public const ushort LiveMapGroupAddress = 0xDA00;

    // The following live-WRAM addresses (the trade cart, USA) were resolved by walking the cart's WRAM section
    // chain and cross-checking every one against the cart's own symbol map AND
    // against the running emulator (holding a direction moves wFacingDirection; the map/coord anchors reproduce the
    // externally-verified 0xDA00 signature). This cart keeps SVBK=1 during gameplay, so the $Dxxx (WRAMX bank-1) reads are what
    // a live peek sees. These are the peeks the trade phase machine gates on and the link-lock gate asserts.

    /// <summary>wLinkMode ($D042): LINK_TRADECENTER (2) on both machines once CheckBothSelectedSameRoom succeeds; LINK_NULL
    /// (0) idle / after ExitLinkCommunications.</summary>
    public const ushort LinkModeAddress = 0xD042;

    /// <summary>wPlayerDirection ($D205): the player's facing — 0 (DOWN, the crafted default) until a tap turns it; 4 (UP)
    /// when facing the Trade Center attendant one tile above.</summary>
    public const ushort PlayerDirectionAddress = 0xD205;

    /// <summary>wPlayerState ($D682): the player's overworld action/state byte — 0 (PLAYER_NORMAL) when spawned and idle. A
    /// non-spawned crafted save leaves the whole player object struct (wPlayerStruct $D1FD..) zero.</summary>
    public const ushort PlayerStateAddress = 0xD682;

    /// <summary>wPlayerStruct ($D1FD): the base of the player's object struct (16 bytes of position/direction/sprite); all
    /// zero until the map-load SpawnPlayer populates it — the observable that the overworld actually became interactive.</summary>
    public const ushort PlayerStructAddress = 0xD1FD;

    /// <summary>wSpriteUpdatesEnabled ($C1CD): 1 while the overworld's sprite engine runs; 0 for the whole trade UI —
    /// <c>special TradeCenter</c> opens with <c>DisableSpriteUpdates</c> (which stores FALSE here) before entering
    /// <c>LinkCommunications</c> and re-enables on the way out. The observable that a console A-press actually fired
    /// <c>TradeCenterConsoleScript</c> (wScriptRunning-style flags are nonzero during ordinary overworld processing and
    /// do not discriminate).</summary>
    public const ushort SpriteUpdatesEnabledAddress = 0xC1CD;

    /// <summary>wCurTradePartyMon ($CEED): the 0-based party slot this side offered in the trade menu.</summary>
    public const ushort CurTradePartyMonAddress = 0xCEED;

    /// <summary>wOTPartyCount ($DD55): the partner's party count once the party/OT block round-tripped; wOTPartyMon1
    /// ($DD5D) is the partner's lead struct (its species mirrors the partner's own wPartyMon1 species).</summary>
    public const ushort OtPartyCountAddress = 0xDD55;

    /// <inheritdoc cref="OtPartyCountAddress"/>
    public const ushort OtPartyMon1Address = 0xDD5D;

    /// <summary>hVBlankCounter ($FF8C): a per-VBlank-incremented HRAM counter — a liveness probe (advances every frame while
    /// the game's VBlank routine runs).</summary>
    public const ushort VBlankCounterAddress = 0xFF8C;

    /// <summary>POKECENTER_2F is map group 20 / map 1, the Cable Club floor where the crafted save spawns.</summary>
    public const byte PokecenterFloorGroup = 20;

    /// <inheritdoc cref="PokecenterFloorGroup"/>
    public const byte PokecenterFloorMap = 1;

    /// <summary>The USING_EXTERNAL_CLOCK ($01) / USING_INTERNAL_CLOCK ($02) role values, and the not-established ($FF)
    /// sentinel, that <see cref="SerialConnectionStatusAddress"/> takes.</summary>
    public const byte UsingExternalClock = 0x01;

    /// <inheritdoc cref="UsingExternalClock"/>
    public const byte UsingInternalClock = 0x02;

    /// <inheritdoc cref="UsingExternalClock"/>
    public const byte ConnectionNotEstablished = 0xFF;

    /// <summary>Builds a Cgb trade-cart machine with a crafted trainer's save already imported into SRAM + RTC footer, exactly
    /// as the demo's power-on battery load does (<see cref="ICartridge.ImportExternalRam"/> then
    /// <see cref="ICartridge.ImportPersistentClock"/>). Both trade machines are pinned to CGB.</summary>
    /// <param name="rom">The trade-cart ROM image.</param>
    /// <param name="trainer">The crafted trainer whose save boots into the machine.</param>
    /// <returns>The assembled machine; the caller owns and disposes it.</returns>
    public static MachineInstance Build(byte[] rom, TradeTrainer trainer) {
        var machine = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);
        var cartridge = machine.GetRequiredService<ICartridge>();
        var save = TradeSaveFactory.CreateSaveFile(trainer: trainer);

        cartridge.ImportExternalRam(source: save.AsSpan(start: 0, length: TradeSaveFactory.SramByteCount));
        cartridge.ImportPersistentClock(source: save.AsSpan(start: TradeSaveFactory.SramByteCount, length: TradeSaveFactory.RtcFooterByteCount));

        return machine;
    }

    /// <summary>Builds a Cgb trade-cart machine from a raw crafted save-file image (SRAM + footer) — the debug path the
    /// explorer uses when it needs to patch the save (e.g. an alternate spawn) before import.</summary>
    /// <param name="rom">The trade-cart ROM image.</param>
    /// <param name="save">A crafted <see cref="TradeSaveFactory.SaveFileByteCount"/>-byte save image.</param>
    /// <param name="model">The console model to construct.</param>
    /// <param name="bootRom">An optional boot ROM image.</param>
    /// <returns>The assembled machine; the caller owns and disposes it.</returns>
    public static MachineInstance BuildFromSave(byte[] rom, byte[] save, ConsoleModel model = ConsoleModel.Cgb, byte[]? bootRom = null) {
        var machine = PostMachine.Build(model: model, rom: rom, bootRom: bootRom);
        var cartridge = machine.GetRequiredService<ICartridge>();

        cartridge.ImportExternalRam(source: save.AsSpan(start: 0, length: TradeSaveFactory.SramByteCount));
        cartridge.ImportPersistentClock(source: save.AsSpan(start: TradeSaveFactory.SramByteCount, length: TradeSaveFactory.RtcFooterByteCount));

        return machine;
    }

    /// <summary>Reads a byte from a machine's address space (WRAM/HRAM/SRAM window), the peek the phase machine gates
    /// on.</summary>
    /// <param name="machine">The machine to peek.</param>
    /// <param name="address">The address to read.</param>
    /// <returns>The byte the mapped device returns.</returns>
    public static byte Peek(MachineInstance machine, ushort address) =>
        machine.GetRequiredService<ISystemBus>().ReadByte(address: address);

    /// <summary>Reads a machine's <see cref="SerialConnectionStatusAddress"/> link role.</summary>
    /// <param name="machine">The machine to peek.</param>
    /// <returns>The connection-status byte ($01/$02/$FF).</returns>
    public static byte ConnectionStatus(MachineInstance machine) =>
        Peek(machine: machine, address: SerialConnectionStatusAddress);

    /// <summary>Reads a machine's live <c>wMapGroup</c> (<see cref="LiveMapGroupAddress"/>) — <c>20</c> once the crafted
    /// save has loaded into the POKECENTER_2F Cable Club floor.</summary>
    /// <param name="machine">The machine to peek.</param>
    /// <returns>The live map group.</returns>
    public static byte LiveMapGroup(MachineInstance machine) =>
        Peek(machine: machine, address: LiveMapGroupAddress);

    /// <summary>Reads a machine's live <c>wMapNumber</c> (<see cref="LiveMapGroupAddress"/>+1) — <c>1</c> once the crafted
    /// save has loaded into the POKECENTER_2F Cable Club floor.</summary>
    /// <param name="machine">The machine to peek.</param>
    /// <returns>The live map number.</returns>
    public static byte LiveMapNumber(MachineInstance machine) =>
        Peek(machine: machine, address: (ushort)(LiveMapGroupAddress + 1));

    /// <summary>Whether a machine's live map position is the POKECENTER_2F Cable Club floor — the observable that the
    /// crafted save's CONTINUE was accepted and the overworld loaded.</summary>
    /// <param name="machine">The machine to peek.</param>
    /// <returns><see langword="true"/> when the live map group/number are POKECENTER_2F (20/1).</returns>
    public static bool IsAtCableClubFloor(MachineInstance machine) =>
        ((LiveMapGroup(machine: machine) == PokecenterFloorGroup) && (LiveMapNumber(machine: machine) == PokecenterFloorMap));

    /// <summary>Exports a machine's SRAM (the 32&#160;KiB external RAM, without the RTC footer) — the post-trade save
    /// the harness re-reads to prove each side's lead species became the other's original.</summary>
    /// <param name="machine">The machine to export.</param>
    /// <returns>The SRAM image.</returns>
    public static byte[] ExportSram(MachineInstance machine) =>
        machine.GetRequiredService<ICartridge>().ExportExternalRam();

    /// <summary>The number of frames <see cref="ContinueScript"/> needs to settle the crafted save into the overworld.
    /// The map (<see cref="LiveMapGroupAddress"/> reading <see cref="PokecenterFloorGroup"/>/<see cref="PokecenterFloorMap"/>)
    /// is loaded by ~frame 450 and the standing frame is stable well before 600 (verified with <c>--trade-explore --linked</c>
    /// on both crafted trainers).</summary>
    public const int ContinueSettledFrame = 600;

    /// <summary>The frozen input script that walks the trade cart from power-on (seeded post-boot state, no boot ROM)
    /// through the intro to the title screen and selects CONTINUE, loading the crafted save into the overworld. Verified
    /// frame-by-frame with <c>--trade-explore</c>: three A taps blow through the publisher logo screen and the intro cinematic to
    /// the main menu (cursor defaults to CONTINUE, top), a fourth A selects it, and a fifth confirms the save-info screen
    /// and loads the map — the live map block at <see cref="LiveMapGroupAddress"/> reaches
    /// <see cref="PokecenterFloorGroup"/>/<see cref="PokecenterFloorMap"/> (POKECENTER_2F, the Cable Club floor) at
    /// (Y=3, X=5) by ~frame 450. Each keyframe taps then releases so the cart's edge-triggered menus register a single press.
    /// Both trade sides walk the identical path (the crafted saves differ only in trainer/party, not menu geometry).
    /// <para>
    /// The overworld renders the POKECENTER_2F room and accepts movement only when <see cref="TradeSaveFactory"/>
    /// initializes <c>wScreenSave</c> (the 6×5 saved-screen metatile window, SRAM 0x286C). Gen 2's CONTINUE path restores that window
    /// over the freshly ROM-loaded <c>wOverworldMapBlocks</c> at the player's screen centre (RestoreScreen, home/map.asm) —
    /// it is NOT re-derived on load — so a zeroed <c>wScreenSave</c> painted border block 0 across the play area, which
    /// renders as one uniform tile and reads as impassable collision everywhere. The factory writes the correct window
    /// for the (5,3) spawn, matching what a real in-game save holds. The scripted receptionist walk and the full Cable Club
    /// trade depend on that window.
    /// </para></summary>
    /// <returns>The frozen CONTINUE-selection script.</returns>
    public static LinkInputScript ContinueScript() =>
        new(
            (150, JoypadButtons.A), (158, JoypadButtons.None),
            (220, JoypadButtons.A), (228, JoypadButtons.None),
            (290, JoypadButtons.A), (298, JoypadButtons.None),
            (360, JoypadButtons.A), (368, JoypadButtons.None),
            (430, JoypadButtons.A), (438, JoypadButtons.None)
        );
}
