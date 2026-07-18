using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The Chroma self-verify battery: boots the freshly-forged ROM on REAL Humble machines (pure CPU, the same core the
/// demo's cabinets run) and asserts the game's observable work-RAM behaviour through the whole state graph —
/// boot→title, the idle→attract hand-off, D4 seed entropy AND same-frame replay determinism (three machines, the
/// seeded WELL compared byte-for-byte), the ported well battery (seeded gravity settle, cursor moves + clamps, the
/// swap rearranging the grid, the drip feed, a staged three-run clearing and scoring), pause freezing the simulation,
/// a staged top-out into the game-over → initials-entry → high-score flow, SRAM persistence round-tripped through an
/// INDEPENDENT C# checksum, top-slot insertion, and corruption recovery to the ROM defaults. Throws on any violation.
/// </summary>
internal static class ChromaVerify {
    private const int CellCount = (ChromaProtocol.Cols * ChromaProtocol.Rows);
    private const ulong TCyclesPerFrame = 70224UL;
    // The window for a STAGED (forced-drip) top-out to reach the game-over card — a few frames' headroom over the one
    // drip StageTopOut arms. Comfortable rather than tight, but the outcome no longer depends on the PRNG spawn phase.
    private const int TopOutWindowFrames = (ChromaProtocol.DropInterval * 3);

    /// <summary>Runs the whole battery.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        AssertBootToTitle(rom: rom);
        AssertAttract(rom: rom);
        AssertSeedEntropy(rom: rom);

        var (sram, expectedMirror, score, slot) = AssertGameplayThroughEntry(rom: rom);

        AssertSramPersistence(rom: rom, sram: sram, expectedMirror: expectedMirror);
        AssertTopSlotInsertion(rom: rom);
        AssertCorruptionRecovery(rom: rom, sram: sram);

        Console.WriteLine(value: $"chroma verify | boot→title | attract in+out | seed entropy + same-frame well replay | seeded settle | cursor moves + clamps | swap rearranges | staged run clears + scores | pause freeze | top-out → game over | score {score:D6} | entry BCA → slot {slot} | top-slot insert + shift | sram round-trip (independent sum16) | corruption → defaults");
    }

    // (1) Boot: the machine reaches the title state within ~8 frames with the VBlank handler alive and the boot's
    // initial-state request consumed (PendingState back to 0xFF, which zeroed RAM never is).
    private static void AssertBootToTitle(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
    }

    // (2) Attract: 620 idle frames fall into the scripted attract with a seeded well, the script visibly plays (the
    // cursor walks), and any REAL press hands control back to the title.
    private static void AssertAttract(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 620);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateAttract), message: $"620 idle frames did not start the attract loop (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (GridCount(driver: driver) > 0), message: "the attract well is empty after seeding");

        var cursorMoved = false;
        var col0 = driver.Read(address: ChromaProtocol.CursorCol);
        var row0 = driver.Read(address: ChromaProtocol.CursorRow);

        for (var frame = 0; (frame < 200); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            cursorMoved |= ((driver.Read(address: ChromaProtocol.CursorCol) != col0) || (driver.Read(address: ChromaProtocol.CursorRow) != row0));
        }

        Assert(condition: cursorMoved, message: "the attract script never moved the cursor (scripted play is dead)");

        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateTitle), message: "a real press during attract did not return to the title");
    }

    // (3) D4 seed entropy: pressing START on different frames yields different PRNG states; pressing on the SAME frame
    // yields the identical state AND a byte-identical seeded well — replay determinism from pure input entropy.
    private static void AssertSeedEntropy(byte[] rom) {
        using var first = new Driver(rom: rom);
        using var second = new Driver(rom: rom);
        using var replay = new Driver(rom: rom);

        StartPlay(driver: first, idleFrames: 40);
        StartPlay(driver: second, idleFrames: 47);
        StartPlay(driver: replay, idleFrames: 40);

        var firstState = first.ReadWide(address: FrameworkMemoryMap.PrngState);
        var secondState = second.ReadWide(address: FrameworkMemoryMap.PrngState);
        var replayState = replay.ReadWide(address: FrameworkMemoryMap.PrngState);

        Assert(condition: (firstState != secondState), message: $"START on different frames produced the SAME PRNG state 0x{firstState:X4} (no input entropy)");
        Assert(condition: (firstState == replayState), message: $"START on the same frame produced different PRNG states (0x{firstState:X4} vs 0x{replayState:X4} — replay broken)");
        Assert(condition: ReadGrid(driver: first).AsSpan().SequenceEqual(other: ReadGrid(driver: replay)), message: "same-frame starts seeded different wells (replay broken)");
    }

    // (4)-(7): one continuous session — the ported well battery, a STAGED three-run clear (bottom-row cells poked so
    // the next resolve ignites them), the pause freeze, a staged top-out into the game-over → initials → high-score
    // flow — returning the persisted SRAM for the round-trip steps.
    private static (byte[] Sram, byte[] ExpectedMirror, int Score, int Slot) AssertGameplayThroughEntry(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 40);

        // The seeded well holds settled material: blocks in range, and no empty cell below a block in any column.
        Assert(condition: (GridCount(driver: driver) > 0), message: "the well is empty after seeding");
        AssertSettled(driver: driver);

        // The cursor responds to all four directions and clamps. Moves are edge-triggered, so we TAP (press + release).
        var col0 = driver.Read(address: ChromaProtocol.CursorCol);

        driver.Press(buttons: JoypadButtons.Right);
        Assert(condition: (driver.Read(address: ChromaProtocol.CursorCol) > col0), message: "Right did not move the cursor");

        for (var tap = 0; (tap < 8); tap++) {
            driver.Press(buttons: JoypadButtons.Left); // Walk to the left wall.
        }

        Assert(condition: (driver.Read(address: ChromaProtocol.CursorCol) == 0), message: $"the cursor column did not clamp to 0 (got {driver.Read(address: ChromaProtocol.CursorCol)})");

        var rowStart = driver.Read(address: ChromaProtocol.CursorRow);

        driver.Press(buttons: JoypadButtons.Up);
        Assert(condition: (driver.Read(address: ChromaProtocol.CursorRow) < rowStart), message: "Up did not move the cursor");

        for (var tap = 0; (tap < 14); tap++) {
            driver.Press(buttons: JoypadButtons.Down); // Walk to the floor.
        }

        Assert(condition: (driver.Read(address: ChromaProtocol.CursorRow) == (ChromaProtocol.Rows - 1)), message: $"the cursor row did not clamp to {(ChromaProtocol.Rows - 1)} (got {driver.Read(address: ChromaProtocol.CursorRow)})");

        // A swap actually rearranges the grid (some cell changes across the press).
        var changed = false;
        var snapshot = ReadGrid(driver: driver);

        for (var attempt = 0; ((attempt < 20) && !changed); attempt++) {
            driver.Press(buttons: JoypadButtons.A);
            driver.Press(buttons: JoypadButtons.Up);
            changed = !ReadGrid(driver: driver).AsSpan().SequenceEqual(other: snapshot);
        }

        Assert(condition: changed, message: "no swap ever changed the grid");

        // (6) A staged clear: poke a same-colour horizontal run into the bottom row — the next drip's resolve (at most
        // DropInterval frames away) must ignite it and bank +3 or more on the BCD score.
        var scoreBefore = ReadBcd(driver: driver, address: ChromaProtocol.Score, byteCount: 3);

        for (var column = 0; (column < 3); column++) {
            driver.Write(address: (ushort)((ChromaProtocol.GridBase + ((ChromaProtocol.Rows - 1) * ChromaProtocol.Cols)) + column), value: ChromaTables.TileBlockBase);
        }

        var scored = false;

        for (var frame = 0; ((frame < (ChromaProtocol.DropInterval * 3)) && !scored); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            scored = (ReadBcd(driver: driver, address: ChromaProtocol.Score, byteCount: 3) >= (scoreBefore + 3));
        }

        Assert(condition: scored, message: "a staged three-run never cleared and scored");

        // (5) Pause: START freezes the well and the drip timer for 90 frames (the frame counter keeps running), START
        // resumes the drip feed.
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StatePause), message: "START did not pause");

        var pausedGrid = ReadGrid(driver: driver);
        var pausedTimer = driver.Read(address: ChromaProtocol.DropTimer);
        var framesBefore = driver.ReadWide(address: FrameworkMemoryMap.FrameCounter);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 90);
        Assert(condition: ReadGrid(driver: driver).AsSpan().SequenceEqual(other: pausedGrid), message: "the well kept changing while paused");
        Assert(condition: (driver.Read(address: ChromaProtocol.DropTimer) == pausedTimer), message: "the drip timer kept running while paused");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) != framesBefore), message: "the frame counter froze during pause (the handler died)");

        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StatePlay), message: "START did not unpause");

        var resumed = false;

        for (var frame = 0; ((frame < 40) && !resumed); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            resumed = (driver.Read(address: ChromaProtocol.DropTimer) != pausedTimer);
        }

        Assert(condition: resumed, message: "the drip timer did not resume after unpause");

        // (7) A staged top-out: fill the whole well and force the imminent drip, so the next spawn finds row 0 occupied
        // and tops the game out (see DriveStagedTopOut for why this is done robustly, not by waiting on a lucky PRNG spawn).
        Assert(condition: DriveStagedTopOut(driver: driver), message: "a staged full well never reached the game-over state");

        var finalScore = new[] { driver.Read(address: ChromaProtocol.Score), driver.Read(address: (ushort)(ChromaProtocol.Score + 1)), driver.Read(address: (ushort)(ChromaProtocol.Score + 2)) };

        Assert(condition: driver.RunUntilState(state: ChromaProtocol.StateScoreEntry, buttons: JoypadButtons.None, maxFrames: 120), message: "a qualifying score never reached initials entry after game over");
        // The entry screen's LCD-off enter outlasts the state flip; settle so the input pipeline is live again.
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);

        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Right); // Slot 1.
        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Up);    // B → C.
        driver.Press(buttons: JoypadButtons.Right); // Slot 2 stays A.
        Assert(condition: ((driver.Read(address: ChromaProtocol.EntryGlyphs) == 1) && (driver.Read(address: (ushort)(ChromaProtocol.EntryGlyphs + 1)) == 2) && (driver.Read(address: (ushort)(ChromaProtocol.EntryGlyphs + 2)) == 0)), message: "the initials pad did not spell BCA");

        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateHighScores), message: "confirming the initials did not land on the high-score screen");

        // The mirror now holds the BCA entry with the exact final score, and stays sorted.
        var mirror = ReadMirror(driver: driver);
        var slot = FindEntry(mirror: mirror, initials: "BCA");

        Assert(condition: (slot >= 0), message: "the BCA entry is missing from the high-score mirror");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[(((slot * ChromaProtocol.HiScoreEntryByteCount) + 3) + index)] == finalScore[index]), message: "the persisted entry's score does not match the game's final score");
        }

        for (var entry = 1; (entry < ChromaProtocol.HiScoreEntryCount); entry++) {
            Assert(condition: (EntryScore(mirror: mirror, entry: (entry - 1)) >= EntryScore(mirror: mirror, entry: entry)), message: $"the high-score table is not sorted (entry {(entry - 1)} < entry {entry})");
        }

        return (driver.ExportExternalRam(), mirror, ReadBcdValue(bytes: finalScore), slot);
    }

    // (8) SRAM persistence: validate the exported block with an INDEPENDENT checksum implementation, then import it
    // into a FRESH machine and confirm the boot load lands the persisted table in the mirror.
    private static void AssertSramPersistence(byte[] rom, byte[] sram, byte[] expectedMirror) {
        var payloadLength = expectedMirror.Length;

        Assert(condition: (sram.Length == 0x2000), message: $"the exported SRAM is {sram.Length} bytes (expected the MBC1 header's 8 KiB)");
        Assert(condition: ((sram[0] == SaveModule.MagicLow) && (sram[1] == SaveModule.MagicHigh)), message: "the persisted block's magic is wrong");
        Assert(condition: (sram[2] == 1), message: $"the persisted block's version is {sram[2]} (expected 1)");

        var payload = sram.AsSpan(start: SaveModule.HeaderByteCount, length: payloadLength);
        var sum = 0;

        foreach (var value in payload) {
            sum = (sum + value) & 0xFFFF;
        }

        var stored = sram[(SaveModule.HeaderByteCount + payloadLength)] | (sram[((SaveModule.HeaderByteCount + payloadLength) + 1)] << 8);

        Assert(condition: (sum == stored), message: $"the stored checksum 0x{stored:X4} does not match the independently computed 0x{sum:X4}");
        Assert(condition: payload.SequenceEqual(other: expectedMirror), message: "the persisted payload does not match the in-game mirror");

        using var driver = new Driver(rom: rom, externalRam: sram);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateTitle), message: "a machine restored from the save did not boot to the title");
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: expectedMirror), message: "a fresh machine did not load the persisted high-score table");
    }

    // (7b) Top-slot insertion: a C#-AUTHORED save block with an all-zero score table must pass the ROM's own validation
    // (checksum compatibility in the import direction), and a fresh session's score must then insert at SLOT 0 —
    // exercising the table shift a lower landing never runs — pushing the old leader down.
    private static void AssertTopSlotInsertion(byte[] rom) {
        var payload = ChromaTables.BuildDefaultScoreTable();

        for (var entry = 0; (entry < ChromaProtocol.HiScoreEntryCount); entry++) {
            for (var index = 0; (index < 3); index++) {
                payload[(((entry * ChromaProtocol.HiScoreEntryByteCount) + 3) + index)] = 0x00;
            }
        }

        using var driver = new Driver(rom: rom, externalRam: BuildSaveBlock(payload: payload));

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: payload), message: "the ROM rejected a C#-authored valid save block (import-direction checksum drift)");

        // A quick staged session: a poked three-run banks a score, then the checkerboard top-out ends the game.
        StartPlay(driver: driver, idleFrames: 4);

        for (var column = 0; (column < 3); column++) {
            driver.Write(address: (ushort)((ChromaProtocol.GridBase + ((ChromaProtocol.Rows - 1) * ChromaProtocol.Cols)) + column), value: ChromaTables.TileBlockBase);
        }

        var scored = false;

        for (var frame = 0; ((frame < (ChromaProtocol.DropInterval * 3)) && !scored); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            scored = (ReadBcd(driver: driver, address: ChromaProtocol.Score, byteCount: 3) >= 3);
        }

        Assert(condition: scored, message: "the top-slot session's staged run never scored");
        Assert(condition: DriveStagedTopOut(driver: driver), message: "the top-slot session's staged top-out never reached game over");

        var finalScore = new[] { driver.Read(address: ChromaProtocol.Score), driver.Read(address: (ushort)(ChromaProtocol.Score + 1)), driver.Read(address: (ushort)(ChromaProtocol.Score + 2)) };

        Assert(condition: driver.RunUntilState(state: ChromaProtocol.StateScoreEntry, buttons: JoypadButtons.None, maxFrames: 120), message: "the top-slot session never reached initials entry");
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4); // Let the LCD-off enter finish (see above).
        driver.Press(buttons: JoypadButtons.Start); // Confirm the default AAA.
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateHighScores), message: "the top-slot confirm did not land on the high-score screen");

        var mirror = ReadMirror(driver: driver);

        Assert(condition: (FindEntry(mirror: mirror, initials: "AAA") == 0), message: "the beating score did not insert at slot 0");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[(3 + index)] == finalScore[index]), message: "the slot-0 entry's score does not match the game's final score");
            Assert(condition: (mirror[(ChromaProtocol.HiScoreEntryByteCount + index)] == payload[index]), message: "the old leader's initials did not shift down to slot 1");
            Assert(condition: (mirror[((ChromaProtocol.HiScoreEntryByteCount + 3) + index)] == 0x00), message: "the old leader's score did not shift down intact");
        }
    }

    // (9) Corruption recovery: one flipped payload byte must fail the ROM's own checksum and land the machine on the
    // ROM defaults — never a partially trusted table.
    private static void AssertCorruptionRecovery(byte[] rom, byte[] sram) {
        var corrupted = (byte[])sram.Clone();

        corrupted[(SaveModule.HeaderByteCount + 5)] ^= 0x5A;

        using var driver = new Driver(rom: rom, externalRam: corrupted);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateTitle), message: "a machine with a corrupt save did not boot cleanly to the title");
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: ChromaTables.BuildDefaultScoreTable()), message: "a corrupt save did not fall back to the ROM's default table");
    }

    // ==== Helpers. =======================================================================================================

    // Boot a fresh machine and press START after `idleFrames` on the title (the D4 entropy sample point). The START
    // tick seeds the whole well in place (twelve drips + a settle resolve — several frames of SM83 time), so the Play
    // state lands a few frames after the edge; the seed itself was sampled AT the edge, so replay is unaffected.
    private static void StartPlay(Driver driver, int idleFrames) {
        driver.RunFrames(buttons: JoypadButtons.None, frames: idleFrames);
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: driver.RunUntilState(state: ChromaProtocol.StatePlay, buttons: JoypadButtons.None, maxFrames: 30), message: "START on the title did not start a game");
    }

    // Drive a guaranteed, LAYOUT-INDEPENDENT top-out to the game-over card, and return whether it reached it. Rather
    // than fill the well once and hope a drip lands before the in-flight cascade (from the caller's staged run) empties
    // the top row — a phase that shifts with any ROM-layout change and made the old "wait a few drips" byte-layout
    // brittle — this REFILLS the whole well every frame and re-arms the drip timer, so row 0 is occupied in every
    // column at the instant the next spawn checks it. EmitSpawn tops out immediately when grid[col] (row 0) is occupied
    // BEFORE placing, so the game-over card is reached deterministically within a couple of drips. Bounded.
    private static bool DriveStagedTopOut(Driver driver) {
        for (var frame = 0; (frame < TopOutWindowFrames); frame++) {
            PokeCheckerboard(driver: driver);
            driver.Write(address: ChromaProtocol.DropTimer, value: (byte)(ChromaProtocol.DropInterval - 1));
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);

            if (driver.Read(address: FrameworkMemoryMap.GameState) == ChromaProtocol.StateGameOver) {
                return true;
            }
        }

        return false;
    }

    // Fill the whole well with a matchless 1/2 checkerboard (no two adjacent cells share a colour, so no run can
    // ignite) — the next drip finds its column full and tops the game out.
    private static void PokeCheckerboard(Driver driver) {
        for (var row = 0; (row < ChromaProtocol.Rows); row++) {
            for (var column = 0; (column < ChromaProtocol.Cols); column++) {
                driver.Write(address: (ushort)((ChromaProtocol.GridBase + (row * ChromaProtocol.Cols)) + column), value: (byte)(ChromaTables.TileBlockBase + ((((row + column) % 2) == 0) ? 0 : 1)));
            }
        }
    }

    // Blocks settle to the bottom, so no empty cell may sit BELOW a block in any column.
    private static void AssertSettled(Driver driver) {
        for (var column = 0; (column < ChromaProtocol.Cols); column++) {
            var sawBlock = false;

            for (var row = 0; (row < ChromaProtocol.Rows); row++) {
                var value = driver.Read(address: (ushort)((ChromaProtocol.GridBase + (row * ChromaProtocol.Cols)) + column));

                Assert(condition: (value <= 3), message: $"grid cell ({row},{column}) holds an out-of-range colour {value}");

                if (value != 0) {
                    sawBlock = true;
                } else {
                    Assert(condition: !sawBlock, message: $"column {column} has an empty cell below a block (gravity broken)");
                }
            }
        }
    }
    private static int GridCount(Driver driver) {
        var count = 0;

        for (var cell = 0; (cell < CellCount); cell++) {
            if (driver.Read(address: (ushort)(ChromaProtocol.GridBase + cell)) != 0) {
                count++;
            }
        }

        return count;
    }
    private static byte[] ReadGrid(Driver driver) {
        var grid = new byte[CellCount];

        for (var cell = 0; (cell < CellCount); cell++) {
            grid[cell] = driver.Read(address: (ushort)(ChromaProtocol.GridBase + cell));
        }

        return grid;
    }

    // Builds a complete, VALID 8 KiB save image around a payload — the independent writer-side implementation of the
    // block format (magic | version | payload | sum16 LE).
    private static byte[] BuildSaveBlock(byte[] payload) {
        var sram = new byte[0x2000];
        var sum = 0;

        sram[0] = SaveModule.MagicLow;
        sram[1] = SaveModule.MagicHigh;
        sram[2] = 1;
        payload.CopyTo(array: sram, index: SaveModule.HeaderByteCount);

        foreach (var value in payload) {
            sum = (sum + value) & 0xFFFF;
        }

        sram[(SaveModule.HeaderByteCount + payload.Length)] = (byte)(sum & 0xFF);
        sram[((SaveModule.HeaderByteCount + payload.Length) + 1)] = (byte)((sum >> 8) & 0xFF);

        return sram;
    }
    private static byte[] ReadMirror(Driver driver) {
        var mirror = new byte[(ChromaProtocol.HiScoreEntryCount * ChromaProtocol.HiScoreEntryByteCount)];

        for (var index = 0; (index < mirror.Length); index++) {
            mirror[index] = driver.Read(address: (ushort)(ChromaProtocol.HiScoreMirror + index));
        }

        return mirror;
    }
    private static int FindEntry(byte[] mirror, string initials) {
        for (var entry = 0; (entry < ChromaProtocol.HiScoreEntryCount); entry++) {
            var matches = true;

            for (var index = 0; (index < 3); index++) {
                if (mirror[((entry * ChromaProtocol.HiScoreEntryByteCount) + index)] != TextModule.TileFor(fontTileBase: ChromaTables.FontTileBase, character: initials[index])) {
                    matches = false;

                    break;
                }
            }

            if (matches) {
                return entry;
            }
        }

        return -1;
    }
    private static int EntryScore(byte[] mirror, int entry) {
        var offset = ((entry * ChromaProtocol.HiScoreEntryByteCount) + 3);

        return ReadBcdValue(bytes: [mirror[offset], mirror[(offset + 1)], mirror[(offset + 2)]]);
    }
    private static int ReadBcd(Driver driver, ushort address, int byteCount) {
        var bytes = new byte[byteCount];

        for (var index = 0; (index < byteCount); index++) {
            bytes[index] = driver.Read(address: (ushort)(address + index));
        }

        return ReadBcdValue(bytes: bytes);
    }
    private static int ReadBcdValue(byte[] bytes) {
        var value = 0;

        foreach (var packed in bytes) {
            value = (((value * 100) + (((packed >> 4) & 0x0F) * 10)) + (packed & 0x0F));
        }

        return value;
    }
    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"chroma ROM verification failed: {message}");
        }
    }

    // One real Humble CGB machine: frame stepping, joypad edges, work-RAM peeks/pokes, and the battery-save seam.
    private sealed class Driver : IDisposable {
        private readonly ICartridge m_cartridge;
        private readonly ICpu m_cpu;
        private readonly IJoypad m_joypad;
        private readonly MachineInstance m_machine;
        private readonly ISystemBus m_bus;

        public Driver(byte[] rom, byte[]? externalRam = null) {
            m_machine = MachineFactory.Create(
                configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
                compose: static services => services.AddHumbleGamingBrickComponents()
            );
            m_bus = m_machine.GetRequiredService<ISystemBus>();
            m_cartridge = m_machine.GetRequiredService<ICartridge>();
            m_cpu = m_machine.GetRequiredService<ICpu>();
            m_joypad = m_machine.GetRequiredService<IJoypad>();

            if (externalRam is not null) {
                m_cartridge.ImportExternalRam(source: externalRam);
            }
        }

        public byte Read(ushort address) => m_bus.ReadByte(address: address);
        public int ReadWide(ushort address) => Read(address: address) | (Read(address: (ushort)(address + 1)) << 8);
        public void Write(ushort address, byte value) => m_bus.WriteByte(address: address, value: value);
        public void RunFrames(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                m_joypad.SetButtons(pressed: buttons);
                m_machine.Machine.Run(tCycles: TCyclesPerFrame);
            }

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "chroma");
        }
        public void Press(JoypadButtons buttons) {
            RunFrames(buttons: buttons, frames: 2);
            RunFrames(buttons: JoypadButtons.None, frames: 2);
        }
        public bool RunUntilState(byte state, JoypadButtons buttons, int maxFrames) {
            for (var frame = 0; (frame < maxFrames); frame++) {
                RunFrames(buttons: buttons, frames: 1);

                if (Read(address: FrameworkMemoryMap.GameState) == state) {
                    return true;
                }
            }

            return false;
        }
        public byte[] ExportExternalRam() => m_cartridge.ExportExternalRam();
        public void Dispose() => m_machine.Dispose();
    }
}
