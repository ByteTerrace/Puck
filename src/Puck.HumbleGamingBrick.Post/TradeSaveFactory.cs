using System.Buffers.Binary;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Crafts a byte-exact battery save for the cross-gen trade cart — a 32&#160;KiB SRAM image plus the 48-byte MBC3 RTC
/// footer (<see cref="Mbc3Cartridge"/>'s de-facto save-file convention) — that boots straight to a CONTINUE-able game
/// standing one tile below the Cable Club trade receptionist, ready to trade. Every field, offset, and invariant is a
/// fixed byte in this cart's era-II save format; the party-mon stats are computed from the era's stat formula so current
/// HP == max HP &gt; 0 holds by construction rather than by a hand-poked constant.
/// <para>
/// The two canned trainers (<see cref="SideA"/>, <see cref="SideB"/>) carry <b>distinct lead species</b> so a completed
/// trade is observable as each side's lead species becoming the other's original. The factory is the harness input for
/// the scripted two-machine trade and the exported <c>.sav</c> pair the demo's per-cabinet saves consume.
/// </para>
/// <para>
/// This cart's link code never writes rKEY1/SC_SPEED, so its same-model trade always
/// runs the normal (~8192&#160;Hz) serial clock — but that is a property of the GAME, not a licence to pin the emulator's
/// serial to a real-time rate. KEY1 legitimately doubles the serial shift clock on hardware and in
/// <see cref="SerialComponent"/> (the fast-clock bit taps DIV bit 3 instead of bit 8); this cart simply never arms it.
/// Both machines are pinned to <see cref="ConsoleModel.Cgb"/> here.
/// </para>
/// </summary>
internal static class TradeSaveFactory {
    // --- File geometry (§2.1, §2.4) ---
    /// <summary>The SRAM image size: 4 banks × 8&#160;KiB (plain MBC3, not the 64&#160;KiB MBC30 variant).</summary>
    public const int SramByteCount = 0x8000;
    /// <summary>The MBC3 RTC footer size appended after the SRAM (five live + five latched registers + 8-byte timestamp).</summary>
    public const int RtcFooterByteCount = 48;
    /// <summary>The full <c>.sav</c> file size: SRAM concatenated with the RTC footer (32&#160;816 bytes for this cart).</summary>
    public const int SaveFileByteCount = (SramByteCount + RtcFooterByteCount);

    // --- Absolute .sav (bank-1) offsets in this cart's era-II save format ---
    private const int OffsetOptions = 0x2000;        // sOptions, 8 bytes — cosmetic.
    private const int OffsetCheckValue1 = 0x2008;    // sCheckValue1 — must be 0x63.
    private const int OffsetPlayerId = 0x2009;       // wPlayerID — big-endian u16; also the checksum range start.
    private const int OffsetPlayerName = 0x200B;     // wPlayerName — 11, $50-terminated.
    private const int OffsetMomsName = 0x2016;       // wMomsName — 11.
    private const int OffsetRivalName = 0x2021;      // wRivalName — 11.
    private const int OffsetEventFlags = 0x2622;     // wEventFlags+3 — bit 0x80 = EVENT_GAVE_MYSTERY_EGG_TO_ELM (§2.3 gate).
    private const int OffsetMoney = 0x23DB;          // wMoney — big-endian u24, plain binary.
    private const int OffsetMapGroup = 0x2868;       // wMapGroup — 20 (POKECENTER_2F).
    private const int OffsetMapNumber = 0x2869;      // wMapNumber — 1.
    private const int OffsetYCoord = 0x286A;         // wYCoord — 3 (one tile below the trade attendant at (5,2)).
    private const int OffsetXCoord = 0x286B;         // wXCoord — 5.
    private const int OffsetScreenSave = 0x286C;     // wScreenSave — the 6×5 saved metatile screen (§2.3.1 below).
    private const int OffsetPartyCount = 0x288A;     // wPartyCount — 1..6.
    private const int OffsetPartySpecies = 0x288B;   // wPartySpecies[6] — species preview, $FF-padded.
    private const int OffsetPartyEnd = 0x2891;       // wPartyEnd — 0xFF list terminator.
    private const int OffsetPartyMons = 0x2892;      // wPartyMon1.. — 48 bytes each.
    private const int OffsetPartyOtNames = 0x29B2;   // wPartyMonOTs — 11×6.
    private const int OffsetPartyNicknames = 0x29F4; // wPartyMonNicknames — 11×6.
    private const int OffsetChecksum = 0x2D69;       // sChecksum — 16-bit LE over [0x2009, 0x2D68].
    private const int OffsetCheckValue2 = 0x2D6B;    // sCheckValue2 — must be 0x7F.

    // The live overworld OBJECTS are part of the saved+restored block, NOT regenerated on CONTINUE (the crux a naive
    // "object structs safe to zero — regenerated on map load" assumption gets WRONG for the continue path).
    // The cart's MapSetupScript_Continue uses LoadMapAttributes_SkipObjects (ReadMapEvents skip=TRUE, so wMapObjects
    // is NOT re-read from ROM) and HandleContinueMap (no SpawnPlayer / LoadMapObjects) — it TRUSTS the saved wObjectStructs
    // + wMapObjects. A zeroed object region therefore boots into a live-but-empty overworld: no player object (every tile
    // "bumps" because GetMovementPermissions samples collision at the player struct's stale wPlayerMapX/Y, and the object
    // is inactive with sprite 0) and no NPCs (nothing to talk to). The MapSetupScript_Warp path DOES spawn from ROM, but
    // CONTINUE only reaches it post-E4. So the crafted save must carry valid object structs, exactly as a real save does.
    // The object-follow globals sit immediately BEFORE wObjectStructs and are saved+restored with it. A real save holds
    // $FF/$FF (no follower); zero-filling them arms "object 0 follows object 0", which makes ApplyMovementToFollower
    // record every player STEP into the 5-byte wFollowMovementQueue ($D1F8) with no consumer — the unbounded queue index
    // then writes straight into wPlayerStruct ($D1FD), corrupting the object structs. The corruption fires on the 5th
    // recorded step, which is exactly side A's TRADE_CENTER seat approach (Up, Up, Right, Right, Up): DoMovementFunction
    // then dispatches a corrupted movement byte, overruns MovementPointers, and jumps into ROM padding ($7709) — the
    // NOP-slide into video RAM that presented as an RST-38 storm (and as a mode-3 VRAM-lock crash before the PPU
    // read-unlock fix, which is why the seat crash survived that fix).
    private const int OffsetObjectFollowLeader = 0x205C;    // wObjectFollow_Leader ($D1F4) — $FF = none.
    private const int OffsetObjectFollowFollower = 0x205D;  // wObjectFollow_Follower ($D1F5) — $FF = none.
    private const int OffsetPlayerObjectStruct = 0x2065; // wObjectStructs / wPlayerStruct ($D1FD, sPlayerData1).
    private const int OffsetObject1Struct = 0x208D;      // wObject1Struct ($D225) — the trade receptionist's live struct.
    private const int OffsetPlayerMapObject = 0x22AD;    // wMapObjects / wPlayerObject ($D445, sPlayerData2).
    private const int OffsetMap1Object = 0x22BD;         // wMap1Object ($D455) — the trade receptionist's map object.
    private const int ObjectStructLength = 0x28;         // OBJECT_LENGTH (40 bytes).
    private const int MapObjectLength = 0x10;            // MAPOBJECT_LENGTH (16 bytes).

    // The additive-checksum range (§2.1): sGameData..sGameDataEnd, .sav 0x2009..0x2D68 inclusive (3361 bytes).
    private const int ChecksumRangeStart = 0x2009;
    private const byte CheckValue1 = 0x63;
    private const byte CheckValue2 = 0x7F;
    private const int ChecksumRangeEndInclusive = 0x2D68;
    private const byte EventGaveMysteryEggBit = 0x80;
    private const int NameLength = 11;
    private const int PartyMonStructLength = 48;
    private const byte SpeciesTerminator = 0xFF;

    // --- Party-mon struct field offsets within the 48-byte struct (§2.2.1) ---
    private const int MonSpecies = 0x00;
    private const int MonHeldItem = 0x01;
    private const int MonMoves = 0x02;      // 4 bytes
    private const int MonOtId = 0x06;       // big-endian u16
    private const int MonExperience = 0x08; // big-endian u24
    private const int MonStatExp = 0x0B;    // 10 bytes (5× u16 BE) — EVs, all zero
    private const int MonDvs = 0x15;        // 2 bytes packed nibbles
    private const int MonPp = 0x17;         // 4 bytes
    private const int MonHappiness = 0x1B;
    private const int MonLevel = 0x1F;
    private const int MonStatusCondition = 0x20;
    private const int MonCurrentHp = 0x22;  // big-endian u16
    private const int MonMaxHp = 0x24;      // big-endian u16
    private const int MonBattleStats = 0x26;      // 10 bytes (Atk,Def,Spd,SpAtk,SpDef; 5× u16 BE)
    private const byte BaseHappiness = 70;
    private const ushort StartMoney = 3000;

    /// <summary>The length of the saved on-screen metatile window (SCREEN_META_WIDTH 6 × SCREEN_META_HEIGHT 5).</summary>
    private const int ScreenSaveLength = 30;

    // The 6×5 saved metatile-block window for a POKECENTER_2F (group 20 / map 1) save standing at (X=5, Y=3), row-major
    // (row 0 = the border strip above the room, rows 1–4 = the room's block rows, columns 0–5 from the screen's left
    // edge) — the shipping map's own block indices. NOTE: this is INERT on the actual CONTINUE path (traced against
    // the cart's map code: MapSetupScript_Continue rebuilds the on-screen blocks via LoadBlockData + BufferScreen from
    // wOverworldMapBlocks; wScreenSave/RestoreScreen is used only by MapSetupScript_Connection). The prior "blank
    // overworld" symptom was NOT wScreenSave — it was the missing player object struct (see WriteObjects). This window is
    // kept because a real save at (5,3) holds it and writing the true values is harmless; it is not load-bearing.
    private static ReadOnlySpan<byte> s_pokecenter2FScreenSaveAt5x3 =>
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x13, 0x31, 0x2B, 0x0B, 0x0A, 0x0B,
        0x05, 0x32, 0x0E, 0x28, 0x14, 0x0F,
        0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
        0x10, 0x04, 0x04, 0x2D, 0x29, 0x2D,
    ];

    /// <summary>The trade harness's first trainer: a Cgb player named GOLD standing at the Trade Center attendant with a
    /// single level-5 RATTATA (distinct from side B's lead).</summary>
    public static TradeTrainer SideA =>
        new(
            Name: "GOLD",
            TrainerId: 0x1234,
            Party: [TradePartyMember.Level5(species: TradeSpecies.Rattata)]
        );

    /// <summary>The trade harness's second trainer: a Cgb player named SILVER standing at the Trade Center attendant with
    /// a single level-5 PIDGEY (distinct from side A's lead).</summary>
    public static TradeTrainer SideB =>
        new(
            Name: "SILVER",
            TrainerId: 0x5678,
            Party: [TradePartyMember.Level5(species: TradeSpecies.Pidgey)]
        );

    /// <summary>Builds the full <c>.sav</c> file (SRAM + RTC footer) for a trainer, ready to write to disk or import via
    /// <see cref="CartridgeBase.ImportExternalRam"/> + <see cref="Mbc3Cartridge.ImportPersistentClock"/>.</summary>
    /// <param name="trainer">The trainer to craft a save for.</param>
    /// <returns>A <see cref="SaveFileByteCount"/>-byte save-file image.</returns>
    public static byte[] CreateSaveFile(TradeTrainer trainer) {
        var file = new byte[SaveFileByteCount];
        var sram = file.AsSpan(start: 0, length: SramByteCount);

        WriteSram(trainer: trainer, sram: sram);

        // The RTC footer is all-zero (halt bit 6 = 0, day-carry bit 7 = 0 in both the live and latched Day-High words,
        // every counter zero, the trailing UNIX timestamp ignored on import). Per §2.3/§2.4 this boots clean — no
        // "the internal clock has been reset" prompt — which the zero-fill already gives us; nothing to write.
        return file;
    }

    /// <summary>Overwrites the spawn map/coordinates in a crafted <c>.sav</c> and re-derives the primary checksum — a
    /// debug affordance for the explorer to isolate whether a map-entry soft-lock is specific to POKECENTER_2F.</summary>
    /// <param name="saveFile">A crafted save file (mutated in place).</param>
    /// <param name="group">The map group.</param>
    /// <param name="map">The map number.</param>
    /// <param name="y">The player Y coordinate.</param>
    /// <param name="x">The player X coordinate.</param>
    /// <returns>The same array, with a valid checksum.</returns>
    public static byte[] PatchSpawn(byte[] saveFile, byte group, byte map, byte y, byte x) {
        var sram = saveFile.AsSpan(start: 0, length: SramByteCount);

        sram[OffsetMapGroup] = group;
        sram[OffsetMapNumber] = map;
        sram[OffsetYCoord] = y;
        sram[OffsetXCoord] = x;

        BinaryPrimitives.WriteUInt16LittleEndian(destination: sram[OffsetChecksum..], value: ComputeChecksum(sram: sram));

        return saveFile;
    }

    /// <summary>Recomputes and rewrites the primary checksum over a crafted <c>.sav</c> — the fix-up after any debug
    /// patch to the checksummed region (e.g. the explorer's <c>wSpawnAfterChampion</c> capture route).</summary>
    /// <param name="saveFile">A crafted save file (mutated in place).</param>
    public static void RewriteChecksum(byte[] saveFile) {
        var sram = saveFile.AsSpan(start: 0, length: SramByteCount);

        BinaryPrimitives.WriteUInt16LittleEndian(destination: sram[OffsetChecksum..], value: ComputeChecksum(sram: sram));
    }

    /// <summary>Builds just the 32&#160;KiB SRAM image (no RTC footer) — the slice a checksum re-verification or a WRAM
    /// mirror comparison works against.</summary>
    /// <param name="trainer">The trainer to craft a save for.</param>
    /// <returns>A <see cref="SramByteCount"/>-byte SRAM image.</returns>
    public static byte[] CreateSram(TradeTrainer trainer) {
        var sram = new byte[SramByteCount];

        WriteSram(trainer: trainer, sram: sram);

        return sram;
    }

    /// <summary>Recomputes the primary checksum the way the game's <c>Checksum</c> routine does (a plain 16-bit
    /// little-endian additive sum over <c>[0x2009, 0x2D68]</c>) and returns whether the SRAM's stored checksum and both
    /// check bytes are internally consistent — the strongest end-to-end proof that a crafted or post-trade save is
    /// acceptable.</summary>
    /// <param name="sram">A 32&#160;KiB SRAM image (the first <see cref="SramByteCount"/> bytes of a <c>.sav</c>).</param>
    /// <returns><see langword="true"/> when the stored checksum and check bytes match the crafted contract.</returns>
    public static bool VerifyChecksum(ReadOnlySpan<byte> sram) {
        if (sram.Length < SramByteCount) {
            return false;
        }

        var expected = ComputeChecksum(sram: sram);
        var stored = BinaryPrimitives.ReadUInt16LittleEndian(source: sram[OffsetChecksum..]);

        return ((stored == expected)
            && (sram[OffsetCheckValue1] == CheckValue1)
            && (sram[OffsetCheckValue2] == CheckValue2));
    }

    /// <summary>Reads the lead party mon's species from a 32&#160;KiB SRAM image — the observable a completed trade moves
    /// (each side's lead species becomes the other's original).</summary>
    /// <param name="sram">A 32&#160;KiB SRAM image.</param>
    /// <returns>The species byte at party slot 0.</returns>
    public static byte ReadLeadSpecies(ReadOnlySpan<byte> sram) =>
        sram[(OffsetPartyMons + MonSpecies)];

    private static void WriteSram(TradeTrainer trainer, Span<byte> sram) {
        ArgumentNullException.ThrowIfNull(argument: trainer);

        if ((trainer.Party.Count < 1) || (trainer.Party.Count > 6)) {
            throw new ArgumentException(message: "a party must hold 1..6 mons.", paramName: nameof(trainer));
        }

        // sOptions is cosmetic; a zero byte set is a legal, fastest-text-speed configuration.
        sram[OffsetCheckValue1] = CheckValue1;
        sram[OffsetCheckValue2] = CheckValue2;

        BinaryPrimitives.WriteUInt16BigEndian(destination: sram[OffsetPlayerId..], value: trainer.TrainerId);
        WriteName(destination: sram[OffsetPlayerName..(OffsetPlayerName + NameLength)], text: trainer.Name);
        WriteName(destination: sram[OffsetMomsName..(OffsetMomsName + NameLength)], text: "MOM");
        WriteName(destination: sram[OffsetRivalName..(OffsetRivalName + NameLength)], text: "RIVAL");

        // The single Cable Club gate (§2.3): force EVENT_GAVE_MYSTERY_EGG_TO_ELM (bit 31 → byte 3 of wEventFlags, bit
        // 0x80). No badges, starter, or further story flags are checked for trade access.
        sram[OffsetEventFlags] = EventGaveMysteryEggBit;

        WriteUInt24BigEndian(destination: sram[OffsetMoney..], value: StartMoney);

        // Spawn directly inside the shared POKECENTER_2F Cable Club floor (group 20, map 1), one tile below the Trade
        // Center receptionist at (5,2) — §2.5. Facing is NOT craftable (object structs regenerate to default DOWN); the
        // harness's first act is tap UP to turn-and-bump toward the attendant, then A.
        sram[OffsetMapGroup] = 20;
        sram[OffsetMapNumber] = 1;
        sram[OffsetYCoord] = 3;
        sram[OffsetXCoord] = 5;

        // wScreenSave is the 6×5 metatile-block window Gen 2 saves so CONTINUE can restore the exact on-screen view. The
        // CONTINUE path RESTORES it (RestoreScreen, home/map.asm) OVER the freshly ROM-loaded wOverworldMapBlocks at the
        // player's screen center — it is NOT redrawn on load. A zero-filled
        // wScreenSave therefore overwrites the visible map with border block 0, which renders as a uniform tile and reads
        // as impassable collision everywhere — the overworld looks blank and the player cannot move. These 30 bytes are
        // the exact window a real save at POKECENTER_2F (5,3) holds: the map's own block indices for the 6-wide × 5-tall
        // metatile region centered on the spawn (row 0 is the border strip above the room). Verified against the shipping
        // ROM's POKECENTER_2F block data (group 20 / map 1, tileset 0x25) — restoring it is a no-op that leaves the loaded
        // map intact, so the overworld is navigable.
        s_pokecenter2FScreenSaveAt5x3.CopyTo(destination: sram[OffsetScreenSave..(OffsetScreenSave + ScreenSaveLength)]);

        WriteObjects(sram: sram);
        WriteParty(trainer: trainer, sram: sram);

        var checksum = ComputeChecksum(sram: sram);

        BinaryPrimitives.WriteUInt16LittleEndian(destination: sram[OffsetChecksum..], value: checksum);

        Validate(trainer: trainer, sram: sram);
    }

    // The player (object 0) spawn coordinates in tile units, and the +4 map-coordinate border offset the object system
    // adds (PlayerSpawn_ConvertCoords: wPlayerMapX = wXCoord + 4).
    private const byte SpawnX = 5;
    private const byte AttendantX = 5;
    private const byte AttendantY = 2;
    private const byte MapCoordBorder = 4;
    private const byte SpawnY = 3;

    // A real, active standing-player object struct captured from a WARP spawn (--trade-capture), which the CONTINUE path
    // does NOT build. Bytes are the object_struct layout (OBJECT_LENGTH 40): [0]=SPRITE_CHRIS, [3]=SPRITEMOVEDATA_
    // PLAYER, [7]=Walking $FF (standing), [11]=Action $01 (OBJECT_ACTION_STAND), [16..21]=MapX/MapY/LastMapX/LastMapY/
    // InitX/InitY (patched from the spawn), [23..24]=screen sprite X/Y (player is always screen-centred). Restoring this
    // makes the player a live, movable object; without it GetMovementPermissions reads collision at a stale (0,0) map
    // coord and every tile bumps.
    private static ReadOnlySpan<byte> s_playerObjectStructTemplate =>
    [
        0x01, 0x00, 0x00, 0x0B, 0x02, 0x00, 0x00, 0xFF, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    // The player's map object (map_object layout, MAPOBJECT_LENGTH 16): structId 0, SPRITE_CHRIS, Y/X coords (map
    // units), SPRITEMOVEDATA_PLAYER, then radius/hours/type/sight $FF/0 and no script/event. wMapObjects is likewise not
    // re-read on CONTINUE, so the player's map object must be present too (the movement code writes coords back to it).
    private static ReadOnlySpan<byte> s_playerMapObjectTemplate =>
    [
        0x00, 0x01, 0x00, 0x00, 0x0B, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
    ];

    // The trade receptionist's live object struct (object 1), built from a real standing NPC captured with --trade-capture
    // in the object_struct field order: [0]=Sprite SPRITE_LINK_RECEPTIONIST 0x38, [1]=MapObjectIndex 1 (-> wMap1Object,
    // the map object whose script A runs — getting THIS wrong indexes wMapObjects out of bounds and crashes on interaction),
    // [2]=SpriteTile 0x24, [3]=MovementType SPRITEMOVEDATA_STANDING_DOWN 0x06, [6]=Palette, [7]=Walking $FF (standing),
    // [8]=Direction DOWN 0, [9]=StepType 0 (RESET, as a fresh spawn has), [11]=Action OBJECT_ACTION_STAND 1, [16..21]=
    // Map/Last/Init X/Y (patched to the (5,2) attendant tile). SpriteTile/palette/screen X/Y self-correct via
    // RefreshMapSprites, which DOES run on CONTINUE. This makes the attendant a real, interactable object at (5,2).
    private static ReadOnlySpan<byte> s_receptionistObjectStructTemplate =>
    [
        0x38, 0x01, 0x24, 0x06, 0x00, 0x00, 0x02, 0xFF, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x21, 0x30, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    // The trade receptionist's map object (wMap1Object), byte-for-byte the shipping POKECENTER_2F object_event
    // as ReadObjectEvents would lay it into wMapObjects: [0]=StructID 1, [1]=SPRITE_LINK_
    // RECEPTIONIST 0x38, [2]=Y+4 (2+4=6), [3]=X+4 (5+4=9), [4]=SPRITEMOVEDATA_STANDING_DOWN 0x06, [5]=radius 0,
    // [6..7]=hours -1/-1, [8]=(PAL_NPC_GREEN<<4)|OBJECTTYPE_SCRIPT = 0xA0, [9]=sight 0, [10..11]=script pointer
    // (LinkReceptionistScript_Trade = bank 0x5C : 0x4D6F, stored LE; the bank is the map-scripts bank the CONTINUE path
    // loads), [12..13]=event flag -1 (always appears). This is what A-on-the-attendant jumps to.
    private static ReadOnlySpan<byte> s_receptionistMapObjectTemplate =>
    [
        0x01, 0x38, 0x06, 0x09, 0x06, 0x00, 0xFF, 0xFF, 0xA0, 0x00, 0x6F, 0x4D, 0xFF, 0xFF, 0x00, 0x00,
    ];

    // Writes the live object structs + map objects the CONTINUE path restores rather than regenerates: the player object
    // (so the overworld is walkable) and the trade receptionist (so the Cable Club script is reachable). Both the live
    // struct (position/sprite/state) and the map object (script pointer) are needed for each — the CONTINUE setup skips
    // both SpawnPlayer and LoadMapObjects.
    private static void WriteObjects(Span<byte> sram) {
        // No follower: a real save carries $FF/$FF here (see the OffsetObjectFollowLeader note — zeros arm a phantom
        // follower whose movement queue overflows into wPlayerStruct on the 5th step).
        sram[OffsetObjectFollowLeader] = 0xFF;
        sram[OffsetObjectFollowFollower] = 0xFF;

        var playerMapX = (byte)(SpawnX + MapCoordBorder);
        var playerMapY = (byte)(SpawnY + MapCoordBorder);

        var playerStruct = sram[OffsetPlayerObjectStruct..(OffsetPlayerObjectStruct + ObjectStructLength)];

        s_playerObjectStructTemplate.CopyTo(destination: playerStruct);
        playerStruct[16] = playerMapX; // MapX
        playerStruct[17] = playerMapY; // MapY
        playerStruct[18] = playerMapX; // LastMapX
        playerStruct[19] = playerMapY; // LastMapY
        playerStruct[20] = playerMapX; // InitX
        playerStruct[21] = playerMapY; // InitY

        var playerMapObject = sram[OffsetPlayerMapObject..(OffsetPlayerMapObject + MapObjectLength)];

        s_playerMapObjectTemplate.CopyTo(destination: playerMapObject);
        playerMapObject[2] = playerMapY; // ObjectYCoord
        playerMapObject[3] = playerMapX; // ObjectXCoord

        // The trade receptionist stands one tile above the player at (X=5, Y=2) -> map coords (9, 6).
        var recMapX = (byte)(AttendantX + MapCoordBorder);
        var recMapY = (byte)(AttendantY + MapCoordBorder);

        var receptionistStruct = sram[OffsetObject1Struct..(OffsetObject1Struct + ObjectStructLength)];

        s_receptionistObjectStructTemplate.CopyTo(destination: receptionistStruct);
        receptionistStruct[16] = recMapX; // MapX
        receptionistStruct[17] = recMapY; // MapY
        receptionistStruct[18] = recMapX; // LastMapX
        receptionistStruct[19] = recMapY; // LastMapY
        receptionistStruct[20] = recMapX; // InitX
        receptionistStruct[21] = recMapY; // InitY

        s_receptionistMapObjectTemplate.CopyTo(destination: sram[OffsetMap1Object..(OffsetMap1Object + MapObjectLength)]);
    }
    private static void WriteParty(TradeTrainer trainer, Span<byte> sram) {
        sram[OffsetPartyCount] = (byte)trainer.Party.Count;

        // wPartySpecies mirrors each mon's species and is $FF-terminated after the count, and wPartyEnd is a fixed $FF.
        for (var slot = 0; (slot < 6); ++slot) {
            sram[(OffsetPartySpecies + slot)] = ((slot < trainer.Party.Count) ? (byte)trainer.Party[slot].Species : SpeciesTerminator);
        }

        sram[OffsetPartyEnd] = SpeciesTerminator;

        for (var slot = 0; (slot < trainer.Party.Count); ++slot) {
            var mon = trainer.Party[slot];
            var structOffset = (OffsetPartyMons + (slot * PartyMonStructLength));

            WriteMon(destination: sram[structOffset..(structOffset + PartyMonStructLength)], mon: mon, trainerId: trainer.TrainerId);

            // OT name = the trainer's own name (a caught/starter mon), nickname = the species' default (left blank here
            // — a $50-terminated empty name renders as the species name in-game, which is legal).
            var otOffset = (OffsetPartyOtNames + (slot * NameLength));
            var nickOffset = (OffsetPartyNicknames + (slot * NameLength));

            WriteName(destination: sram[otOffset..(otOffset + NameLength)], text: trainer.Name);
            WriteName(destination: sram[nickOffset..(nickOffset + NameLength)], text: mon.Species.ToString().ToUpperInvariant());
        }
    }
    private static void WriteMon(Span<byte> destination, TradePartyMember mon, ushort trainerId) {
        var stats = ComputeStats(species: mon.Species, level: mon.Level);

        destination[MonSpecies] = (byte)mon.Species;
        destination[MonHeldItem] = 0;
        destination[MonMoves] = mon.Move;            // move 1; the remaining three move slots stay empty (0).
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[MonOtId..], value: trainerId);
        WriteUInt24BigEndian(destination: destination[MonExperience..], value: (uint)((mon.Level * mon.Level) * mon.Level));
        // Stat exp (EVs) all zero — already zero-filled.
        destination[MonDvs] = 0xFF;                  // DVs: Atk=15,Def=15 → 0xFF.
        destination[(MonDvs + 1)] = 0xFF;              // DVs: Spd=15,Spc=15 → 0xFF (HP DV = 15). Max, legal, non-corrupt.
        destination[MonPp] = mon.Pp;                 // move 1 current PP; PP-Up count 0.
        destination[MonHappiness] = BaseHappiness;
        destination[MonLevel] = mon.Level;
        destination[MonStatusCondition] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[MonCurrentHp..], value: stats.Hp);
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[MonMaxHp..], value: stats.Hp);
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[(MonBattleStats + 0)..], value: stats.Attack);
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[(MonBattleStats + 2)..], value: stats.Defense);
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[(MonBattleStats + 4)..], value: stats.Speed);
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[(MonBattleStats + 6)..], value: stats.SpecialAttack);
        BinaryPrimitives.WriteUInt16BigEndian(destination: destination[(MonBattleStats + 8)..], value: stats.SpecialDefense);
    }

    // The era's stat formula with DV = 15 and stat-exp = 0 (so the sqrt term drops out): a stat is
    // floor((2*(Base+DV)) * Level / 100) + 5, and HP additionally adds Level + 10. Computing it here guarantees the
    // invariant "current HP == max HP > 0" holds for any species/level rather than trusting a hand-poked number.
    private static MonStats ComputeStats(TradeSpecies species, byte level) {
        var @base = TradeSpecies_Base(species: species);

        static ushort Regular(int baseStat, int dv, int level) =>
            (ushort)((((2 * (baseStat + dv)) * level) / 100) + 5);

        static ushort Health(int baseStat, int dv, int level) =>
            (ushort)(((((2 * (baseStat + dv)) * level) / 100) + level) + 10);

        const int dv = 15;

        return new MonStats(
            Hp: Health(baseStat: @base.Hp, dv: dv, level: level),
            Attack: Regular(baseStat: @base.Attack, dv: dv, level: level),
            Defense: Regular(baseStat: @base.Defense, dv: dv, level: level),
            Speed: Regular(baseStat: @base.Speed, dv: dv, level: level),
            SpecialAttack: Regular(baseStat: @base.SpecialAttack, dv: dv, level: level),
            SpecialDefense: Regular(baseStat: @base.SpecialDefense, dv: dv, level: level)
        );
    }

    // Base stats for the handful of species the harness uses (index = the cart's internal species index).
    private static MonStats TradeSpecies_Base(TradeSpecies species) =>
        species switch {
            TradeSpecies.Rattata => new MonStats(Hp: 30, Attack: 56, Defense: 35, Speed: 72, SpecialAttack: 25, SpecialDefense: 35),
            TradeSpecies.Pidgey => new MonStats(Hp: 40, Attack: 45, Defense: 40, Speed: 56, SpecialAttack: 35, SpecialDefense: 35),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(species), actualValue: species, message: "no base-stat entry for this species."),
        };
    private static ushort ComputeChecksum(ReadOnlySpan<byte> sram) {
        var sum = 0;

        for (var offset = ChecksumRangeStart; (offset <= ChecksumRangeEndInclusive); ++offset) {
            sum += sram[offset];
        }

        return (ushort)sum;
    }

    // Asserts every mandated crafted-save invariant on the finished SRAM; a violation is a factory bug, not a runtime
    // condition, so it throws rather than returning a verdict.
    private static void Validate(TradeTrainer trainer, ReadOnlySpan<byte> sram) {
        Require(condition: (sram[OffsetCheckValue1] == CheckValue1), message: "sCheckValue1 must be 0x63.");
        Require(condition: (sram[OffsetCheckValue2] == CheckValue2), message: "sCheckValue2 must be 0x7F.");
        Require(condition: ((sram[OffsetEventFlags] & EventGaveMysteryEggBit) != 0), message: "the Mystery-Egg Cable Club gate flag must be set.");
        Require(condition: ((sram[OffsetMapGroup] == 20) && (sram[OffsetMapNumber] == 1)), message: "spawn must be POKECENTER_2F (group 20, map 1).");
        Require(condition: ((sram[OffsetXCoord] == 5) && (sram[OffsetYCoord] == 3)), message: "spawn must be (X=5, Y=3), one tile below the attendant.");
        Require(condition: VerifyChecksum(sram: sram), message: "the crafted save's checksum/check-byte contract is inconsistent.");

        var count = sram[OffsetPartyCount];

        Require(condition: (count == trainer.Party.Count), message: "wPartyCount must equal the crafted party size.");
        Require(condition: (sram[OffsetPartyEnd] == SpeciesTerminator), message: "wPartyEnd must be the 0xFF terminator.");

        for (var slot = 0; (slot < count); ++slot) {
            var mon = trainer.Party[slot];
            var structOffset = (OffsetPartyMons + (slot * PartyMonStructLength));
            var species = sram[(structOffset + MonSpecies)];
            var level = sram[(structOffset + MonLevel)];
            var currentHp = BinaryPrimitives.ReadUInt16BigEndian(source: sram[(structOffset + MonCurrentHp)..]);
            var maxHp = BinaryPrimitives.ReadUInt16BigEndian(source: sram[(structOffset + MonMaxHp)..]);

            Require(condition: (species == (byte)mon.Species), message: $"party slot {slot} species must match the struct.");
            Require(condition: (sram[(OffsetPartySpecies + slot)] == species), message: $"wPartySpecies[{slot}] must mirror the struct species.");
            Require(condition: ((level >= 1) && (level <= 100)), message: $"party slot {slot} level must be 1..100.");
            Require(condition: ((currentHp == maxHp) && (maxHp > 0)), message: $"party slot {slot} current HP must equal max HP > 0.");
        }

        // Distinct lead species per side is a two-save property; asserted here only that the lead is a real species byte.
        Require(condition: ((sram[(OffsetPartySpecies + count)] == SpeciesTerminator) || (count == 6)), message: "wPartySpecies must be 0xFF-terminated after the party count.");
    }
    private static void Require(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"TradeSaveFactory invariant violated: {message}");
        }
    }

    // Gen-2 name text encoding (§2.2.2): a $50 terminator, then $50 padding to the fixed 11-byte field.
    private static void WriteName(Span<byte> destination, string text) {
        destination.Fill(value: 0x50);

        var limit = Math.Min(val1: text.Length, val2: (destination.Length - 1));

        for (var index = 0; (index < limit); ++index) {
            destination[index] = EncodeChar(character: text[index]);
        }

        destination[limit] = 0x50;
    }
    private static byte EncodeChar(char character) =>
        character switch {
            ' ' => 0x7F,
            >= 'A' and <= 'Z' => (byte)(0x80 + (character - 'A')),
            >= 'a' and <= 'z' => (byte)(0xA0 + (character - 'a')),
            >= '0' and <= '9' => (byte)(0xF6 + (character - '0')),
            _ => 0x50,
        };
    private static void WriteUInt24BigEndian(Span<byte> destination, uint value) {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }

    private readonly record struct MonStats(
        ushort Hp,
        ushort Attack,
        ushort Defense,
        ushort Speed,
        ushort SpecialAttack,
        ushort SpecialDefense
    );
}

/// <summary>A species the trade harness uses, whose enum value is the cart's internal era-II species index.</summary>
internal enum TradeSpecies : byte {
    /// <summary>RATTATA — species index 19 (0x13); side A's lead.</summary>
    Rattata = 19,
    /// <summary>PIDGEY — species index 16 (0x10); side B's lead.</summary>
    Pidgey = 16,
}

/// <summary>One crafted party mon: species, level, its single move, and that move's PP. Stats and DVs are derived by the
/// factory so the "current HP == max HP" invariant is structural.</summary>
/// <param name="Species">The species.</param>
/// <param name="Level">The level (1..100).</param>
/// <param name="Move">The single move ID in slot 0 (TACKLE by default).</param>
/// <param name="Pp">The move's current PP (TACKLE base PP is 35).</param>
internal readonly record struct TradePartyMember(TradeSpecies Species, byte Level, byte Move, byte Pp) {
    private const byte TackleMoveId = 0x21; // move 33.
    private const byte TackleBasePp = 35;

    /// <summary>A trivially-legal level-5 mon of a species: a single TACKLE at full PP.</summary>
    /// <param name="species">The species.</param>
    /// <returns>The party mon.</returns>
    public static TradePartyMember Level5(TradeSpecies species) =>
        new(Species: species, Level: 5, Move: TackleMoveId, Pp: TackleBasePp);
}

/// <summary>A crafted trade-cart trainer: name, trainer ID, and party — the input to
/// <see cref="TradeSaveFactory.CreateSaveFile"/>.</summary>
/// <param name="Name">The player name (ASCII; Gen-2-encoded by the factory, max 7 visible chars).</param>
/// <param name="TrainerId">The 16-bit trainer ID (also each caught mon's OT ID).</param>
/// <param name="Party">The party, 1..6 mons.</param>
internal sealed record TradeTrainer(string Name, ushort TrainerId, IReadOnlyList<TradePartyMember> Party);
