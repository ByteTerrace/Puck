using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The Brickfall self-verify battery: boots the freshly-forged ROM on REAL Humble machines (pure CPU, the same core
/// the demo's cabinets run) and asserts the game's observable work-RAM behaviour through the whole state graph —
/// boot→title, the idle→attract hand-off, D4 seed entropy AND same-frame replay determinism (three machines), the
/// ported gameplay battery, pause freezing the simulation, BCD scoring and a forced line clear, the game-over →
/// initials-entry → high-score flow, SRAM persistence round-tripped through an INDEPENDENT C# checksum, and
/// corruption recovery to the ROM defaults. Throws on any violation.
/// </summary>
internal static class BrickfallVerify {
    private const ulong TCyclesPerFrame = 70224UL;

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

        Console.WriteLine(value: $"brickfall verify | boot→title | attract in+out | seed entropy + same-frame replay | shift/rotate/gravity/lock | forced line clear | pause freeze | score {score:D6} | entry BCA → slot {slot} | top-slot insert + shift | sram round-trip (independent sum16) | corruption → defaults");
    }

    // (1) Boot: the machine reaches the title state within ~8 frames with the VBlank handler alive (the frame counter
    // advances) and the boot's initial-state request consumed (PendingState back to 0xFF, which zeroed RAM never is).
    private static void AssertBootToTitle(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
    }

    // (2) Attract: 620 idle frames fall into the scripted attract, the script visibly plays (gravity moves the piece),
    // and any REAL press hands control back to the title.
    private static void AssertAttract(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 620);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StateAttract), message: $"620 idle frames did not start the attract loop (state {driver.Read(address: FrameworkMemoryMap.GameState)})");

        var maxY = 0;

        for (var frame = 0; (frame < 100); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            maxY = Math.Max(maxY, driver.Read(address: BrickfallProtocol.PieceY));
        }

        Assert(condition: (maxY > 0), message: "the attract script never moved the piece (scripted play is dead)");

        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StateTitle), message: "a real press during attract did not return to the title");
    }

    // (3) D4 seed entropy: pressing START on different frames yields different PRNG states; pressing on the SAME
    // frame yields the identical state AND the identical first piece — replay determinism from pure input entropy.
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
        Assert(condition: (first.Read(address: BrickfallProtocol.PieceType) == replay.Read(address: BrickfallProtocol.PieceType)), message: "same-frame starts drew different first pieces (replay broken)");
    }

    // (4)-(7): one continuous session — the ported gameplay battery, a FORCED line clear (the well's bottom row is
    // staged so the current flat-bottomed piece completes it), the pause freeze, a staged top-out into the game-over →
    // initials → high-score flow — returning the persisted SRAM for the round-trip steps.
    private static (byte[] Sram, byte[] ExpectedMirror, int Score, int Slot) AssertGameplayThroughEntry(byte[] rom) {
        using var driver = StartFlatBottomGame(rom: rom);

        // The ported battery: a valid piece, gravity, edge-triggered shifts, rotation (wound back to rotation 0).
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceType) < 7), message: $"spawned piece type {driver.Read(address: BrickfallProtocol.PieceType)} is out of range");

        var startY = driver.Read(address: BrickfallProtocol.PieceY);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 50);
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceY) > startY), message: $"gravity did not lower the piece ({startY} → {driver.Read(address: BrickfallProtocol.PieceY)})");

        var beforeMove = driver.Read(address: BrickfallProtocol.PieceX);

        driver.Press(buttons: JoypadButtons.Left);
        driver.Press(buttons: JoypadButtons.Left);
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceX) < beforeMove), message: "Left did not shift the piece");

        var beforeRight = driver.Read(address: BrickfallProtocol.PieceX);

        driver.Press(buttons: JoypadButtons.Right);
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceX) > beforeRight), message: "Right did not shift the piece");

        var beforeRotation = driver.Read(address: BrickfallProtocol.PieceRot);

        driver.Press(buttons: JoypadButtons.Up);
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceRot) != beforeRotation), message: "Up did not rotate the piece");
        driver.Press(buttons: JoypadButtons.Up);
        driver.Press(buttons: JoypadButtons.Up);
        driver.Press(buttons: JoypadButtons.Up);
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceRot) == 0), message: "four rotations did not return to rotation 0");

        // (6) Forced line clear: stage the bottom row so the flat-bottomed piece completes it on a soft drop.
        var cells = PieceCells(type: driver.Read(address: BrickfallProtocol.PieceType));
        var pieceColumns = cells.Select(selector: cell => (driver.Read(address: BrickfallProtocol.PieceX) + cell.X)).ToHashSet();
        var maxDy = cells.Max(selector: static cell => cell.Y);
        var bottomCellCount = cells.Count(predicate: cell => (cell.Y == maxDy));

        for (var column = 0; (column < BrickfallProtocol.WellColumns); column++) {
            if (!pieceColumns.Contains(item: column)) {
                driver.Write(address: (ushort)(BrickfallProtocol.WellBase + ((BrickfallProtocol.WellRows - 1) * BrickfallProtocol.WellColumns) + column), value: BrickfallTables.TileBlockBase);
            }
        }

        var cleared = false;

        for (var frame = 0; (frame < 90); frame++) {
            driver.RunFrames(buttons: JoypadButtons.Down, frames: 1);

            if (driver.Read(address: (ushort)(BrickfallProtocol.Lines + 1)) == 0x01) {
                cleared = true;

                break;
            }
        }

        Assert(condition: cleared, message: "a staged full bottom row never cleared under a soft drop");

        var scoreAfterClear = ReadBcd(driver: driver, address: BrickfallProtocol.Score, byteCount: 3);

        Assert(condition: (scoreAfterClear >= 40), message: $"a single line clear scored {scoreAfterClear} (< the 40-point base)");
        Assert(condition: (WellBlockCount(driver: driver) == (4 - bottomCellCount)), message: $"the well holds {WellBlockCount(driver: driver)} cells after the clear (expected {4 - bottomCellCount})");

        // (5) Pause: START freezes the piece for 90 frames (the frame counter keeps running), START resumes gravity.
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StatePause), message: "START did not pause");

        var pausedY = driver.Read(address: BrickfallProtocol.PieceY);
        var framesBefore = driver.ReadWide(address: FrameworkMemoryMap.FrameCounter);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 90);
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceY) == pausedY), message: "the piece kept falling while paused");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) != framesBefore), message: "the frame counter froze during pause (the handler died)");

        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StatePlay), message: "START did not unpause");
        driver.RunFrames(buttons: JoypadButtons.None, frames: 60);
        Assert(condition: (driver.Read(address: BrickfallProtocol.PieceY) > pausedY), message: "gravity did not resume after unpause");

        // (7) Staged top-out: fill the well's middle (column 0 left open so no row ever completes), then soft-drop
        // into GameOver → ScoreEntry → drive the initials to BCA → confirm → HighScores.
        for (var row = 2; (row <= 15); row++) {
            for (var column = 1; (column < BrickfallProtocol.WellColumns); column++) {
                driver.Write(address: (ushort)(BrickfallProtocol.WellBase + (row * BrickfallProtocol.WellColumns) + column), value: BrickfallTables.TileBlockBase);
            }
        }

        Assert(condition: driver.RunUntilState(state: BrickfallProtocol.StateGameOver, buttons: JoypadButtons.Down, maxFrames: 600), message: "a staged top-out never reached the game-over state");

        var finalScore = new[] { driver.Read(address: BrickfallProtocol.Score), driver.Read(address: (ushort)(BrickfallProtocol.Score + 1)), driver.Read(address: (ushort)(BrickfallProtocol.Score + 2)) };

        Assert(condition: driver.RunUntilState(state: BrickfallProtocol.StateScoreEntry, buttons: JoypadButtons.None, maxFrames: 120), message: "a qualifying score never reached initials entry after game over");

        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Right); // Slot 1.
        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Up);    // B → C.
        driver.Press(buttons: JoypadButtons.Right); // Slot 2 stays A.
        Assert(condition: ((driver.Read(address: BrickfallProtocol.EntryGlyphs) == 1) && (driver.Read(address: (ushort)(BrickfallProtocol.EntryGlyphs + 1)) == 2) && (driver.Read(address: (ushort)(BrickfallProtocol.EntryGlyphs + 2)) == 0)), message: "the initials pad did not spell BCA");

        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StateHighScores), message: "confirming the initials did not land on the high-score screen");

        // The mirror now holds the BCA entry with the exact final score, and stays sorted.
        var mirror = ReadMirror(driver: driver);
        var slot = FindEntry(mirror: mirror, initials: "BCA");

        Assert(condition: (slot >= 0), message: "the BCA entry is missing from the high-score mirror");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[(slot * BrickfallProtocol.HiScoreEntryByteCount) + 3 + index] == finalScore[index]), message: "the persisted entry's score does not match the game's final score");
        }

        for (var entry = 1; (entry < BrickfallProtocol.HiScoreEntryCount); entry++) {
            Assert(condition: (EntryScore(mirror: mirror, entry: (entry - 1)) >= EntryScore(mirror: mirror, entry: entry)), message: $"the high-score table is not sorted (entry {entry - 1} < entry {entry})");
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
            sum = ((sum + value) & 0xFFFF);
        }

        var stored = (sram[SaveModule.HeaderByteCount + payloadLength] | (sram[SaveModule.HeaderByteCount + payloadLength + 1] << 8));

        Assert(condition: (sum == stored), message: $"the stored checksum 0x{stored:X4} does not match the independently computed 0x{sum:X4}");
        Assert(condition: payload.SequenceEqual(other: expectedMirror), message: "the persisted payload does not match the in-game mirror");

        using var driver = new Driver(rom: rom, externalRam: sram);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StateTitle), message: "a machine restored from the save did not boot to the title");
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: expectedMirror), message: "a fresh machine did not load the persisted high-score table");
    }

    // (7b) Top-slot insertion: a C#-AUTHORED save block with an all-zero score table must pass the ROM's own
    // validation (checksum compatibility in the import direction), and a fresh game's score must then insert at
    // SLOT 0 — exercising the table shift the main session's slot-4 landing never runs — pushing the old leader down.
    private static void AssertTopSlotInsertion(byte[] rom) {
        var payload = BrickfallTables.BuildDefaultScoreTable();

        for (var entry = 0; (entry < BrickfallProtocol.HiScoreEntryCount); entry++) {
            for (var index = 0; (index < 3); index++) {
                payload[(entry * BrickfallProtocol.HiScoreEntryByteCount) + 3 + index] = 0x00;
            }
        }

        using var driver = new Driver(rom: rom, externalRam: BuildSaveBlock(payload: payload));

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: payload), message: "the ROM rejected a C#-authored valid save block (import-direction checksum drift)");

        // A quick staged game: a few scoring soft drops, then a top-out (column 0 stays open, so no row ever clears).
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.Down, frames: 6);
        Assert(condition: (ReadBcd(driver: driver, address: BrickfallProtocol.Score, byteCount: 3) > 0), message: "soft drops scored nothing in the top-slot session");

        for (var row = 9; (row <= 15); row++) {
            for (var column = 1; (column < BrickfallProtocol.WellColumns); column++) {
                driver.Write(address: (ushort)(BrickfallProtocol.WellBase + (row * BrickfallProtocol.WellColumns) + column), value: BrickfallTables.TileBlockBase);
            }
        }

        Assert(condition: driver.RunUntilState(state: BrickfallProtocol.StateGameOver, buttons: JoypadButtons.Down, maxFrames: 600), message: "the top-slot session's staged top-out never reached game over");

        var finalScore = new[] { driver.Read(address: BrickfallProtocol.Score), driver.Read(address: (ushort)(BrickfallProtocol.Score + 1)), driver.Read(address: (ushort)(BrickfallProtocol.Score + 2)) };

        Assert(condition: driver.RunUntilState(state: BrickfallProtocol.StateScoreEntry, buttons: JoypadButtons.None, maxFrames: 120), message: "the top-slot session never reached initials entry");
        driver.Press(buttons: JoypadButtons.Start); // Confirm the default AAA.
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StateHighScores), message: "the top-slot confirm did not land on the high-score screen");

        var mirror = ReadMirror(driver: driver);

        Assert(condition: (FindEntry(mirror: mirror, initials: "AAA") == 0), message: "the beating score did not insert at slot 0");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[3 + index] == finalScore[index]), message: "the slot-0 entry's score does not match the game's final score");
            Assert(condition: (mirror[BrickfallProtocol.HiScoreEntryByteCount + index] == payload[index]), message: "the old leader's initials did not shift down to slot 1");
            Assert(condition: (mirror[BrickfallProtocol.HiScoreEntryByteCount + 3 + index] == 0x00), message: "the old leader's score did not shift down intact");
        }
    }

    // (9) Corruption recovery: one flipped payload byte must fail the ROM's own checksum and land the machine on the
    // ROM defaults — never a partially trusted table.
    private static void AssertCorruptionRecovery(byte[] rom, byte[] sram) {
        var corrupted = (byte[])sram.Clone();

        corrupted[SaveModule.HeaderByteCount + 5] ^= 0x5A;

        using var driver = new Driver(rom: rom, externalRam: corrupted);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StateTitle), message: "a machine with a corrupt save did not boot cleanly to the title");
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: BrickfallTables.BuildDefaultScoreTable()), message: "a corrupt save did not fall back to the ROM's default table");
    }

    // ==== Helpers. =======================================================================================================

    // Boot a fresh machine and press START after `idleFrames` on the title (the D4 entropy sample point).
    private static void StartPlay(Driver driver, int idleFrames) {
        driver.RunFrames(buttons: JoypadButtons.None, frames: idleFrames);
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 2);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == BrickfallProtocol.StatePlay), message: "START on the title did not start a game");
    }

    // Boot fresh machines, varying the START frame (and so the seed), until the first piece has a flat bottom —
    // the staged line clear needs a piece whose bottom cells cover every column it occupies (S and Z never do).
    private static Driver StartFlatBottomGame(byte[] rom) {
        for (var idleFrames = 40; (idleFrames < 90); idleFrames++) {
            var driver = new Driver(rom: rom);

            StartPlay(driver: driver, idleFrames: idleFrames);

            var type = driver.Read(address: BrickfallProtocol.PieceType);

            if ((type != 3) && (type != 4)) {
                return driver;
            }

            driver.Dispose();
        }

        throw new InvalidOperationException(message: "brickfall ROM verification failed: fifty different seeds never drew a flat-bottomed first piece (the PRNG is broken).");
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
            sum = ((sum + value) & 0xFFFF);
        }

        sram[SaveModule.HeaderByteCount + payload.Length] = (byte)(sum & 0xFF);
        sram[SaveModule.HeaderByteCount + payload.Length + 1] = (byte)((sum >> 8) & 0xFF);

        return sram;
    }

    private static (int X, int Y)[] PieceCells(byte type) {
        var table = BrickfallTables.BuildPieceTable();
        var cells = new (int X, int Y)[4];

        for (var index = 0; (index < 4); index++) {
            cells[index] = (table[(type * 32) + (index * 2)], table[(type * 32) + (index * 2) + 1]);
        }

        return cells;
    }

    private static int WellBlockCount(Driver driver) {
        var count = 0;

        for (var cell = 0; (cell < (BrickfallProtocol.WellColumns * BrickfallProtocol.WellRows)); cell++) {
            if (driver.Read(address: (ushort)(BrickfallProtocol.WellBase + cell)) != 0) {
                count++;
            }
        }

        return count;
    }

    private static byte[] ReadMirror(Driver driver) {
        var mirror = new byte[BrickfallProtocol.HiScoreEntryCount * BrickfallProtocol.HiScoreEntryByteCount];

        for (var index = 0; (index < mirror.Length); index++) {
            mirror[index] = driver.Read(address: (ushort)(BrickfallProtocol.HiScoreMirror + index));
        }

        return mirror;
    }

    private static int FindEntry(byte[] mirror, string initials) {
        for (var entry = 0; (entry < BrickfallProtocol.HiScoreEntryCount); entry++) {
            var matches = true;

            for (var index = 0; (index < 3); index++) {
                if (mirror[(entry * BrickfallProtocol.HiScoreEntryByteCount) + index] != TextModule.TileFor(fontTileBase: BrickfallTables.FontTileBase, character: initials[index])) {
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
        var offset = ((entry * BrickfallProtocol.HiScoreEntryByteCount) + 3);

        return ReadBcdValue(bytes: [mirror[offset], mirror[offset + 1], mirror[offset + 2]]);
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
            value = ((value * 100) + (((packed >> 4) & 0x0F) * 10) + (packed & 0x0F));
        }

        return value;
    }

    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"brickfall ROM verification failed: {message}");
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

        public int ReadWide(ushort address) => (Read(address: address) | (Read(address: (ushort)(address + 1)) << 8));

        public void Write(ushort address, byte value) => m_bus.WriteByte(address: address, value: value);

        public void RunFrames(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                m_joypad.SetButtons(pressed: buttons);
                m_machine.Machine.Run(tCycles: TCyclesPerFrame);
            }

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "brickfall");
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
