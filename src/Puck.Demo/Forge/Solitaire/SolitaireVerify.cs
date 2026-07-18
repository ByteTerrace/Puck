using Puck.Demo.Forge.Cards;
using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The Solitaire self-verify battery: boots the freshly-forged ROM on REAL Humble machines (pure CPU, the same core
/// the demo's cabinets run) and asserts the game's observable work-RAM behaviour through the whole state graph —
/// boot→title, the idle→attract hand-off (with the attract deal matched against the C# oracle), D4 seed entropy AND
/// same-frame replay determinism, the deal-from-seed proof (the post-deal PRNG state is walked BACK 51 LCG steps to
/// the seed and the C# Fisher–Yates must reproduce every pile byte), draw/recycle/undo round trips, staged legal and
/// illegal moves with exact-snapshot undo, the tableau flip (+5), foundation building (+10), pause freezing the
/// simulation, the four-king win → streak → initials → high-score flow, SRAM persistence round-tripped through an
/// INDEPENDENT C# checksum, top-slot insertion, and corruption recovery. Throws on any violation.
/// </summary>
internal static class SolitaireVerify {
    private const ushort AttractSeed = 0x1234;
    private const ulong TCyclesPerFrame = 70224UL;

    /// <summary>Runs the whole battery.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        AssertBootToTitle(rom: rom);
        AssertAttract(rom: rom);
        AssertSeedEntropyAndOracle(rom: rom);
        AssertDrawRecycleUndo(rom: rom);
        AssertMovesAndUndo(rom: rom);
        AssertPause(rom: rom);

        var (sram, expectedMirror, streak) = AssertWinAndEntry(rom: rom);

        AssertSramPersistence(rom: rom, sram: sram, expectedMirror: expectedMirror);
        AssertTopSlotInsertion(rom: rom);
        AssertCorruptionRecovery(rom: rom, sram: sram);

        Console.WriteLine(value: $"solitaire verify | boot→title | attract in+out + oracle deal | seed entropy + same-frame replay | deal-from-seed (LCG inverted 51 steps) | draw/recycle/undo | legal+illegal moves | flip +5 | foundation +10 | exact-snapshot undo | pause freeze | four-king win → streak {streak:X2} | entry BCA | sram round-trip (independent sum16) | top-slot insert + shift | corruption → defaults");
    }

    // (1) Boot: the machine reaches the title within ~8 frames with the VBlank handler alive and the boot's
    // initial-state request consumed.
    private static void AssertBootToTitle(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
    }

    // (2) Attract: 620 idle frames fall into the scripted attract, its constant-seed deal matches the C# oracle,
    // the script visibly draws from the stock, and any REAL press hands control back to the title.
    private static void AssertAttract(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 630);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateAttract), message: $"620 idle frames did not start the attract loop (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        AssertDealMatchesOracle(driver: driver, seed: AttractSeed, context: "the attract deal");

        var drew = false;

        for (var frame = 0; (frame < 400); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);

            if (driver.Read(address: SolitaireProtocol.WastePos) > 0) {
                drew = true;

                break;
            }
        }

        Assert(condition: drew, message: "the attract script never drew from the stock (scripted play is dead)");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateTitle), message: "a real press during attract did not return to the title");
    }

    // (3) D4 seed entropy + the oracle: different confirm frames yield different PRNG states AND decks; the same
    // frame replays the identical board; and the C# Fisher–Yates from the RECOVERED seed (the post-deal state walked
    // back 51 LCG steps) reproduces every pile byte — deal-from-seed, proven end to end.
    private static void AssertSeedEntropyAndOracle(byte[] rom) {
        using var first = new Driver(rom: rom);
        using var second = new Driver(rom: rom);
        using var replay = new Driver(rom: rom);

        StartPlay(driver: first, idleFrames: 40);
        StartPlay(driver: second, idleFrames: 47);
        StartPlay(driver: replay, idleFrames: 40);

        var firstState = first.ReadWide(address: FrameworkMemoryMap.PrngState);
        var secondState = second.ReadWide(address: FrameworkMemoryMap.PrngState);
        var replayState = replay.ReadWide(address: FrameworkMemoryMap.PrngState);

        Assert(condition: (firstState != secondState), message: $"confirming on different frames produced the SAME PRNG state 0x{firstState:X4} (no input entropy)");
        Assert(condition: (firstState == replayState), message: $"confirming on the same frame produced different PRNG states (0x{firstState:X4} vs 0x{replayState:X4} — replay broken)");
        Assert(condition: ReadBoardSnapshot(driver: first).SequenceEqual(second: ReadBoardSnapshot(driver: replay)), message: "same-frame confirms dealt different boards (replay broken)");
        Assert(condition: !ReadBoardSnapshot(driver: first).SequenceEqual(second: ReadBoardSnapshot(driver: second)), message: "different-frame confirms dealt the same board (the deal ignores the seed)");

        // Walk the observed state back exactly 51 draws to the seed the machine sampled, then predict the deal.
        var seed = (ushort)firstState;

        for (var step = 0; (step < 51); step++) {
            seed = CardDeck.StepBack(state: seed);
        }

        AssertDealMatchesOracle(driver: first, seed: seed, context: "the recovered-seed deal");
        AssertDealShape(driver: first);
    }

    // (4) Draw, recycle, and the undo ring on a live deal.
    private static void AssertDrawRecycleUndo(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 42);
        Assert(condition: (driver.Read(address: SolitaireProtocol.CursorPos) == SolitaireProtocol.PositionStock), message: "a fresh deal did not start the cursor on the stock");

        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 1), message: "A at the stock did not draw a card");
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 2), message: "a second draw did not advance the waste");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 1), message: "undo did not take back a draw");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 0), message: "undo did not take back the first draw");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 0), message: "an empty undo ring changed the board");

        for (var draw = 0; (draw < 24); draw++) {
            driver.Press(buttons: JoypadButtons.A);
        }

        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 24), message: $"24 draws left the waste at {driver.Read(address: SolitaireProtocol.WastePos)}");
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 0), message: "drawing past the stock's end did not recycle");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 24), message: "undo did not take back the recycle");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == 23), message: "undo after the recycle did not take back a draw");
    }

    // (5) Staged moves: a legal tableau build, an exact-snapshot undo, an illegal drop that changes nothing, the
    // flip (+5), and foundation building (+10, with the wrong suit rejected and a king onto an empty column).
    private static void AssertMovesAndUndo(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 44);

        // A red five onto a black six (tableau → tableau, no score).
        ClearBoard(driver: driver);
        SetTableau(driver: driver, column: 0, faceUp: 1, cards: [CardDeck.CardId(suit: 1, rank: 5)]);
        SetTableau(driver: driver, column: 1, faceUp: 1, cards: [CardDeck.CardId(suit: 0, rank: 6)]);
        SetTableau(driver: driver, column: 2, faceUp: 1, cards: [CardDeck.CardId(suit: 1, rank: 7)]);

        var before = ReadBoardSnapshot(driver: driver);

        MoveCursorTo(driver: driver, target: 6);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: SolitaireProtocol.CarryDepth) == 1), message: "A on a tableau card did not pick it up");
        MoveCursorTo(driver: driver, target: 7);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: SolitaireProtocol.CarryDepth) == 0), message: "a legal drop did not land");
        Assert(condition: (ReadCount(driver: driver, pile: 6) == 2), message: "the legal drop did not grow the target pile");
        Assert(condition: (ReadCount(driver: driver, pile: 5) == 0), message: "the legal drop did not shrink the source pile");

        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: ReadBoardSnapshot(driver: driver).SequenceEqual(second: before), message: "undo did not restore the exact pre-move board");

        // The illegal drop: a red five onto a red seven — nothing may change, and the carry survives.
        MoveCursorTo(driver: driver, target: 6);
        driver.Press(buttons: JoypadButtons.A);
        MoveCursorTo(driver: driver, target: 8);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: SolitaireProtocol.CarryDepth) == 1), message: "an illegal drop cleared the carry");
        Assert(condition: ReadBoardSnapshot(driver: driver).SequenceEqual(second: before), message: "an illegal drop changed the board");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: SolitaireProtocol.CarryDepth) == 0), message: "B did not cancel the carry");

        // The flip: moving the five off a face-down king flips it for +5.
        ClearBoard(driver: driver);
        SetTableau(driver: driver, column: 0, faceUp: 1, cards: [CardDeck.CardId(suit: 0, rank: 13), CardDeck.CardId(suit: 1, rank: 5)]);
        SetTableau(driver: driver, column: 1, faceUp: 1, cards: [CardDeck.CardId(suit: 0, rank: 6)]);

        var beforeFlip = ReadBoardSnapshot(driver: driver);

        MoveCursorTo(driver: driver, target: 6);
        driver.Press(buttons: JoypadButtons.A);
        MoveCursorTo(driver: driver, target: 7);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (ReadFaceUp(driver: driver, column: 0) == 1), message: "the exposed face-down card did not flip");
        Assert(condition: (ReadScore(driver: driver) == 5), message: $"the flip scored {ReadScore(driver: driver)} (expected 5)");
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: ReadBoardSnapshot(driver: driver).SequenceEqual(second: beforeFlip), message: "undo did not reverse the flip move exactly");

        // Foundations: the ace opens (+10), the wrong suit is rejected, the right suit builds, a king opens an
        // empty column.
        ClearBoard(driver: driver);
        SetWaste(driver: driver, cards: [CardDeck.CardId(suit: 0, rank: 1)]);
        MoveCursorTo(driver: driver, target: SolitaireProtocol.PositionWaste);
        driver.Press(buttons: JoypadButtons.A);
        MoveCursorTo(driver: driver, target: 2);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (ReadCount(driver: driver, pile: 1) == 1), message: "the ace did not open a foundation");
        Assert(condition: (ReadScore(driver: driver) == 10), message: $"the foundation ace scored {ReadScore(driver: driver)} (expected 10)");

        SetWaste(driver: driver, cards: [CardDeck.CardId(suit: 1, rank: 2)]);
        MoveCursorTo(driver: driver, target: SolitaireProtocol.PositionWaste);
        driver.Press(buttons: JoypadButtons.A);
        MoveCursorTo(driver: driver, target: 2);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (ReadCount(driver: driver, pile: 1) == 1), message: "a wrong-suit two landed on the foundation");
        driver.Press(buttons: JoypadButtons.B); // Cancel the still-held carry.

        SetWaste(driver: driver, cards: [CardDeck.CardId(suit: 0, rank: 2)]);
        MoveCursorTo(driver: driver, target: SolitaireProtocol.PositionWaste);
        driver.Press(buttons: JoypadButtons.A);
        MoveCursorTo(driver: driver, target: 2);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (ReadCount(driver: driver, pile: 1) == 2), message: "the same-suit two did not build the foundation");

        SetWaste(driver: driver, cards: [CardDeck.CardId(suit: 2, rank: 13)]);
        MoveCursorTo(driver: driver, target: SolitaireProtocol.PositionWaste);
        driver.Press(buttons: JoypadButtons.A);
        MoveCursorTo(driver: driver, target: 6);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (ReadCount(driver: driver, pile: 5) == 1), message: "a king did not open an empty column");

        SetWaste(driver: driver, cards: [CardDeck.CardId(suit: 2, rank: 4)]);
        MoveCursorTo(driver: driver, target: SolitaireProtocol.PositionWaste);
        driver.Press(buttons: JoypadButtons.A);
        MoveCursorTo(driver: driver, target: 7);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (ReadCount(driver: driver, pile: 6) == 0), message: "a non-king opened an empty column");
    }

    // (6) Pause freezes the simulation (the frame counter keeps running); SELECT abandons and breaks the streak.
    private static void AssertPause(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 45);
        driver.Press(buttons: JoypadButtons.A);

        var wasteBefore = driver.Read(address: SolitaireProtocol.WastePos);

        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StatePause), message: "START did not pause");

        var framesBefore = driver.ReadWide(address: FrameworkMemoryMap.FrameCounter);

        driver.RunFrames(buttons: JoypadButtons.A, frames: 90);
        Assert(condition: (driver.Read(address: SolitaireProtocol.WastePos) == wasteBefore), message: "the board changed while paused");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) != framesBefore), message: "the frame counter froze during pause (the handler died)");
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StatePlay), message: "START did not unpause");

        driver.Press(buttons: JoypadButtons.Start);
        driver.Press(buttons: JoypadButtons.Select);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateTitle), message: "SELECT in pause did not abandon to the title");
        Assert(condition: (driver.Read(address: SolitaireProtocol.StreakMirror) == 0), message: "abandoning did not break the streak");
    }

    // (7) The four-king win: staged foundations at queen-high, the kings played through the pad, the win state,
    // the streak, initials entry (BCA), and the sorted mirror — returning the SRAM for the round-trip steps.
    private static (byte[] Sram, byte[] ExpectedMirror, byte Streak) AssertWinAndEntry(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 46);
        StageFourKingWin(driver: driver);

        for (var suit = 0; (suit < 4); suit++) {
            MoveCursorTo(driver: driver, target: (byte)(SolitaireProtocol.PositionTableauBase + suit));
            driver.Press(buttons: JoypadButtons.A);
            MoveCursorTo(driver: driver, target: (byte)(SolitaireProtocol.PositionFoundationBase + suit));
            driver.Press(buttons: JoypadButtons.A);
            Assert(condition: (ReadCount(driver: driver, pile: (byte)(SolitaireProtocol.PileFoundationBase + suit)) == 13), message: $"the {suit} king did not complete its foundation");
        }

        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateWin), message: "completing all four foundations did not win");

        var streak = driver.Read(address: SolitaireProtocol.StreakMirror);

        Assert(condition: (streak == 0x01), message: $"the first win left the streak at 0x{streak:X2} (expected 0x01)");
        Assert(condition: (driver.Read(address: SolitaireProtocol.BestStreakMirror) == 0x01), message: "the best streak did not follow the first win");

        // The win waterfall: the tumbling card is alive (its Y moves across frames) and inside the play area.
        var cardY1 = driver.Read(address: SolitaireProtocol.WinCardY);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);

        var cardY2 = driver.Read(address: SolitaireProtocol.WinCardY);

        Assert(condition: (cardY1 != cardY2), message: $"the win waterfall's card never moved (Y {cardY1} → {cardY2})");
        Assert(condition: ((cardY1 <= 130) && (cardY2 <= 130)), message: $"the win waterfall's card left the play area (Y {cardY1} → {cardY2})");

        var finalScore = new[] { driver.Read(address: SolitaireProtocol.Score), driver.Read(address: (ushort)(SolitaireProtocol.Score + 1)), driver.Read(address: (ushort)(SolitaireProtocol.Score + 2)) };

        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateScoreEntry), message: "a qualifying win never reached initials entry");

        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Right); // Slot 1.
        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Up);    // B → C.
        driver.Press(buttons: JoypadButtons.Right); // Slot 2 stays A.
        Assert(condition: ((driver.Read(address: SolitaireProtocol.EntryGlyphs) == 1) && (driver.Read(address: (ushort)(SolitaireProtocol.EntryGlyphs + 1)) == 2) && (driver.Read(address: (ushort)(SolitaireProtocol.EntryGlyphs + 2)) == 0)), message: "the initials pad did not spell BCA");

        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateHighScores), message: "confirming the initials did not land on the high-score screen");

        var mirror = ReadMirror(driver: driver);
        var slot = FindEntry(mirror: mirror, initials: "BCA");

        Assert(condition: (slot >= 0), message: "the BCA entry is missing from the high-score mirror");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[(((slot * SolitaireProtocol.HiScoreEntryByteCount) + 3) + index)] == finalScore[index]), message: "the persisted entry's score does not match the game's final score");
        }

        for (var entry = 1; (entry < SolitaireProtocol.HiScoreEntryCount); entry++) {
            Assert(condition: (EntryScore(mirror: mirror, entry: (entry - 1)) >= EntryScore(mirror: mirror, entry: entry)), message: $"the high-score table is not sorted (entry {(entry - 1)} < entry {entry})");
        }

        return (driver.ExportExternalRam(), ReadSavePayload(driver: driver), streak);
    }

    // (8) SRAM persistence: validate the exported block with an INDEPENDENT checksum, then import into a FRESH
    // machine and confirm the boot load lands the whole persisted payload (table + streaks) in the mirror.
    private static void AssertSramPersistence(byte[] rom, byte[] sram, byte[] expectedMirror) {
        Assert(condition: (sram.Length == 0x2000), message: $"the exported SRAM is {sram.Length} bytes (expected the MBC1 header's 8 KiB)");
        Assert(condition: ((sram[0] == SaveModule.MagicLow) && (sram[1] == SaveModule.MagicHigh)), message: "the persisted block's magic is wrong");
        Assert(condition: (sram[2] == 1), message: $"the persisted block's version is {sram[2]} (expected 1)");

        var payload = sram.AsSpan(start: SaveModule.HeaderByteCount, length: SolitaireProtocol.SavePayloadByteCount);
        var sum = 0;

        foreach (var value in payload) {
            sum = (sum + value) & 0xFFFF;
        }

        var stored = sram[(SaveModule.HeaderByteCount + SolitaireProtocol.SavePayloadByteCount)] | (sram[((SaveModule.HeaderByteCount + SolitaireProtocol.SavePayloadByteCount) + 1)] << 8);

        Assert(condition: (sum == stored), message: $"the stored checksum 0x{stored:X4} does not match the independently computed 0x{sum:X4}");
        Assert(condition: payload.SequenceEqual(other: expectedMirror), message: "the persisted payload does not match the in-game mirror");

        using var driver = new Driver(rom: rom, externalRam: sram);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateTitle), message: "a machine restored from the save did not boot to the title");
        Assert(condition: ReadSavePayload(driver: driver).AsSpan().SequenceEqual(other: expectedMirror), message: "a fresh machine did not load the persisted payload");
    }

    // (9) Top-slot insertion: a C#-AUTHORED save block with an all-zero score table must pass the ROM's own
    // validation, and a staged win's score must then insert at SLOT 0 — exercising the table shift.
    private static void AssertTopSlotInsertion(byte[] rom) {
        var payload = SolitaireTables.BuildDefaultSavePayload();

        for (var entry = 0; (entry < SolitaireProtocol.HiScoreEntryCount); entry++) {
            for (var index = 0; (index < 3); index++) {
                payload[(((entry * SolitaireProtocol.HiScoreEntryByteCount) + 3) + index)] = 0x00;
            }
        }

        using var driver = new Driver(rom: rom, externalRam: BuildSaveBlock(payload: payload));

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: ReadSavePayload(driver: driver).AsSpan().SequenceEqual(other: payload), message: "the ROM rejected a C#-authored valid save block (import-direction checksum drift)");

        StartPlay(driver: driver, idleFrames: 43);
        StageFourKingWin(driver: driver);

        for (var suit = 0; (suit < 4); suit++) {
            MoveCursorTo(driver: driver, target: (byte)(SolitaireProtocol.PositionTableauBase + suit));
            driver.Press(buttons: JoypadButtons.A);
            MoveCursorTo(driver: driver, target: (byte)(SolitaireProtocol.PositionFoundationBase + suit));
            driver.Press(buttons: JoypadButtons.A);
        }

        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateWin), message: "the top-slot session's staged win never fired");

        var finalScore = new[] { driver.Read(address: SolitaireProtocol.Score), driver.Read(address: (ushort)(SolitaireProtocol.Score + 1)), driver.Read(address: (ushort)(SolitaireProtocol.Score + 2)) };

        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateScoreEntry), message: "the top-slot session never reached initials entry");
        driver.Press(buttons: JoypadButtons.Start); // Confirm the default AAA.
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);

        var mirror = ReadMirror(driver: driver);

        Assert(condition: (FindEntry(mirror: mirror, initials: "AAA") == 0), message: "the beating score did not insert at slot 0");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[(3 + index)] == finalScore[index]), message: "the slot-0 entry's score does not match the game's final score");
            Assert(condition: (mirror[(SolitaireProtocol.HiScoreEntryByteCount + index)] == payload[index]), message: "the old leader's initials did not shift down to slot 1");
            Assert(condition: (mirror[((SolitaireProtocol.HiScoreEntryByteCount + 3) + index)] == 0x00), message: "the old leader's score did not shift down intact");
        }
    }

    // (10) Corruption recovery: one flipped payload byte must fail the ROM's own checksum and land on the defaults.
    private static void AssertCorruptionRecovery(byte[] rom, byte[] sram) {
        var corrupted = (byte[])sram.Clone();

        corrupted[(SaveModule.HeaderByteCount + 5)] ^= 0x5A;

        using var driver = new Driver(rom: rom, externalRam: corrupted);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StateTitle), message: "a machine with a corrupt save did not boot cleanly to the title");
        Assert(condition: ReadSavePayload(driver: driver).AsSpan().SequenceEqual(other: SolitaireTables.BuildDefaultSavePayload()), message: "a corrupt save did not fall back to the ROM's default payload");
    }

    // ==== Staging + oracle helpers. ======================================================================================

    // Confirms NEW DEAL on the title after `idleFrames` (the D4 entropy sample point).
    private static void StartPlay(Driver driver, int idleFrames) {
        driver.RunFrames(buttons: JoypadButtons.None, frames: idleFrames);
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 2);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == SolitaireProtocol.StatePlay), message: "confirming NEW DEAL did not start a game");
    }

    // Predicts the deal from a seed with the C# oracle and asserts every pile byte, count, and face-up.
    private static void AssertDealMatchesOracle(Driver driver, ushort seed, string context) {
        var deck = CardDeck.ShuffleOracle(seed: seed, finalState: out _);
        var offset = 0;

        for (var tableau = 0; (tableau < 7); tableau++) {
            var pile = (byte)(SolitaireProtocol.PileTableauBase + tableau);

            Assert(condition: (ReadCount(driver: driver, pile: pile) == (tableau + 1)), message: $"{context}: tableau {tableau} holds {ReadCount(driver: driver, pile: pile)} cards (expected {(tableau + 1)})");
            Assert(condition: (ReadFaceUp(driver: driver, column: tableau) == 1), message: $"{context}: tableau {tableau} face-up is {ReadFaceUp(driver: driver, column: tableau)} (expected 1)");

            for (var index = 0; (index <= tableau); index++) {
                var actual = driver.Read(address: (ushort)((SolitaireProtocol.PileBase + (pile * SolitaireProtocol.PileStride)) + index));

                Assert(condition: (actual == deck[offset]), message: $"{context}: tableau {tableau}[{index}] is card {actual} (the oracle dealt {deck[offset]})");
                offset++;
            }
        }

        Assert(condition: (ReadCount(driver: driver, pile: SolitaireProtocol.PileStock) == 24), message: $"{context}: the stock holds {ReadCount(driver: driver, pile: SolitaireProtocol.PileStock)} cards (expected 24)");

        for (var index = 0; (index < 24); index++) {
            var actual = driver.Read(address: (ushort)(SolitaireProtocol.PileBase + index));

            Assert(condition: (actual == deck[offset]), message: $"{context}: stock[{index}] is card {actual} (the oracle dealt {deck[offset]})");
            offset++;
        }
    }

    // The dealt board is a permutation of the 52 card ids.
    private static void AssertDealShape(Driver driver) {
        var seen = new HashSet<byte>();

        for (var index = 0; (index < 24); index++) {
            _ = seen.Add(item: driver.Read(address: (ushort)(SolitaireProtocol.PileBase + index)));
        }

        for (var tableau = 0; (tableau < 7); tableau++) {
            var pile = (SolitaireProtocol.PileTableauBase + tableau);

            for (var index = 0; (index <= tableau); index++) {
                _ = seen.Add(item: driver.Read(address: (ushort)((SolitaireProtocol.PileBase + (pile * SolitaireProtocol.PileStride)) + index)));
            }
        }

        Assert(condition: (seen.Count == CardDeck.CardCount), message: $"the deal is not a 52-card permutation ({seen.Count} distinct ids)");
    }
    private static void ClearBoard(Driver driver) {
        for (var pile = 0; (pile < SolitaireProtocol.PileCount); pile++) {
            driver.Write(address: (ushort)(SolitaireProtocol.CountsBase + pile), value: 0);
        }

        for (var column = 0; (column < 7); column++) {
            driver.Write(address: (ushort)(SolitaireProtocol.FaceUpBase + column), value: 0);
        }

        driver.Write(address: SolitaireProtocol.WastePos, value: 0);
        driver.Write(address: SolitaireProtocol.CarryDepth, value: 0);
        driver.Write(address: SolitaireProtocol.Score, value: 0);
        driver.Write(address: (ushort)(SolitaireProtocol.Score + 1), value: 0);
        driver.Write(address: (ushort)(SolitaireProtocol.Score + 2), value: 0);
        driver.Write(address: SolitaireProtocol.UndoHead, value: 0);
        driver.Write(address: SolitaireProtocol.UndoCount, value: 0);
    }
    private static void SetTableau(Driver driver, int column, byte faceUp, byte[] cards) {
        var pile = (SolitaireProtocol.PileTableauBase + column);

        for (var index = 0; (index < cards.Length); index++) {
            driver.Write(address: (ushort)((SolitaireProtocol.PileBase + (pile * SolitaireProtocol.PileStride)) + index), value: cards[index]);
        }

        driver.Write(address: (ushort)(SolitaireProtocol.CountsBase + pile), value: (byte)cards.Length);
        driver.Write(address: (ushort)(SolitaireProtocol.FaceUpBase + column), value: faceUp);
    }
    private static void SetFoundation(Driver driver, int foundation, byte[] cards) {
        var pile = (SolitaireProtocol.PileFoundationBase + foundation);

        for (var index = 0; (index < cards.Length); index++) {
            driver.Write(address: (ushort)((SolitaireProtocol.PileBase + (pile * SolitaireProtocol.PileStride)) + index), value: cards[index]);
        }

        driver.Write(address: (ushort)(SolitaireProtocol.CountsBase + pile), value: (byte)cards.Length);
    }

    // The waste holds these cards, all drawn (the split sits above them).
    private static void SetWaste(Driver driver, byte[] cards) {
        for (var index = 0; (index < cards.Length); index++) {
            driver.Write(address: (ushort)(SolitaireProtocol.PileBase + index), value: cards[index]);
        }

        driver.Write(address: SolitaireProtocol.CountsBase, value: (byte)cards.Length);
        driver.Write(address: SolitaireProtocol.WastePos, value: (byte)cards.Length);
        driver.Write(address: SolitaireProtocol.CarryDepth, value: 0);
    }

    // Foundations at queen-high, the four kings waiting face-up on the first four tableau columns.
    private static void StageFourKingWin(Driver driver) {
        ClearBoard(driver: driver);

        for (var suit = 0; (suit < 4); suit++) {
            var cards = new byte[12];

            for (var rank = 1; (rank <= 12); rank++) {
                cards[(rank - 1)] = CardDeck.CardId(suit: suit, rank: rank);
            }

            SetFoundation(driver: driver, foundation: suit, cards: cards);
            SetTableau(driver: driver, column: suit, faceUp: 1, cards: [CardDeck.CardId(suit: suit, rank: 13)]);
        }
    }

    // Routes the cursor with the SAME navigation table the ROM links (BFS over Left/Right/Up/Down edges).
    private static void MoveCursorTo(Driver driver, byte target) {
        var records = SolitaireTables.BuildPositionRecords();

        for (var guard = 0; (guard < 24); guard++) {
            var current = driver.Read(address: SolitaireProtocol.CursorPos);

            if (current == target) {
                return;
            }

            driver.Press(buttons: NextButton(records: records, current: current, target: target));
        }

        throw new InvalidOperationException(message: $"solitaire ROM verification failed: the cursor never reached position {target}.");
    }

    // The first press of the shortest path through the navigation records.
    private static JoypadButtons NextButton(IReadOnlyList<byte[]> records, byte current, byte target) {
        var previous = new int[records.Count];
        var moves = new JoypadButtons[records.Count];
        var queue = new Queue<int>();

        Array.Fill(array: previous, value: -1);
        previous[current] = current;
        queue.Enqueue(item: current);

        while (queue.Count > 0) {
            var node = queue.Dequeue();
            var record = records[node];
            var isTableau = (record[SolitaireTables.PositionFieldKind] == SolitaireTables.KindTableau);
            (int Next, JoypadButtons Button)[] edges = [
                (record[SolitaireTables.PositionFieldNavLeft], JoypadButtons.Left),
                (record[SolitaireTables.PositionFieldNavRight], JoypadButtons.Right),
                (record[SolitaireTables.PositionFieldNavVertical], (isTableau ? JoypadButtons.Up : JoypadButtons.Down)),
            ];

            foreach (var (next, button) in edges) {
                if (previous[next] >= 0) {
                    continue;
                }

                previous[next] = node;
                moves[next] = button;
                queue.Enqueue(item: next);

                if (next == target) {
                    // Walk back to the first hop.
                    var hop = next;

                    while (previous[hop] != current) {
                        hop = previous[hop];
                    }

                    return moves[hop];
                }
            }
        }

        throw new InvalidOperationException(message: $"solitaire ROM verification failed: no navigation path from {current} to {target}.");
    }

    // ==== Read helpers. ==================================================================================================

    private static byte ReadCount(Driver driver, byte pile) => driver.Read(address: (ushort)(SolitaireProtocol.CountsBase + pile));
    private static byte ReadFaceUp(Driver driver, int column) => driver.Read(address: (ushort)(SolitaireProtocol.FaceUpBase + column));
    private static int ReadScore(Driver driver) {
        var value = 0;

        for (var index = 0; (index < 3); index++) {
            var packed = driver.Read(address: (ushort)(SolitaireProtocol.Score + index));

            value = (((value * 100) + (((packed >> 4) & 0x0F) * 10)) + (packed & 0x0F));
        }

        return value;
    }

    // The full logical board: counts, face-ups, the split, the score, and every pile byte in use.
    private static byte[] ReadBoardSnapshot(Driver driver) {
        var snapshot = new List<byte>();

        for (var pile = 0; (pile < SolitaireProtocol.PileCount); pile++) {
            var count = ReadCount(driver: driver, pile: (byte)pile);

            snapshot.Add(item: count);

            for (var index = 0; (index < count); index++) {
                snapshot.Add(item: driver.Read(address: (ushort)((SolitaireProtocol.PileBase + (pile * SolitaireProtocol.PileStride)) + index)));
            }
        }

        for (var column = 0; (column < 7); column++) {
            snapshot.Add(item: ReadFaceUp(driver: driver, column: column));
        }

        snapshot.Add(item: driver.Read(address: SolitaireProtocol.WastePos));
        snapshot.Add(item: driver.Read(address: SolitaireProtocol.Score));
        snapshot.Add(item: driver.Read(address: (ushort)(SolitaireProtocol.Score + 1)));
        snapshot.Add(item: driver.Read(address: (ushort)(SolitaireProtocol.Score + 2)));

        return [.. snapshot];
    }
    private static byte[] ReadSavePayload(Driver driver) {
        var payload = new byte[SolitaireProtocol.SavePayloadByteCount];

        for (var index = 0; (index < payload.Length); index++) {
            payload[index] = driver.Read(address: (ushort)(SolitaireProtocol.HiScoreMirror + index));
        }

        return payload;
    }
    private static byte[] ReadMirror(Driver driver) {
        var mirror = new byte[(SolitaireProtocol.HiScoreEntryCount * SolitaireProtocol.HiScoreEntryByteCount)];

        for (var index = 0; (index < mirror.Length); index++) {
            mirror[index] = driver.Read(address: (ushort)(SolitaireProtocol.HiScoreMirror + index));
        }

        return mirror;
    }
    private static int FindEntry(byte[] mirror, string initials) {
        for (var entry = 0; (entry < SolitaireProtocol.HiScoreEntryCount); entry++) {
            var matches = true;

            for (var index = 0; (index < 3); index++) {
                if (mirror[((entry * SolitaireProtocol.HiScoreEntryByteCount) + index)] != TextModule.TileFor(fontTileBase: SolitaireTables.FontTileBase, character: initials[index])) {
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
        var offset = ((entry * SolitaireProtocol.HiScoreEntryByteCount) + 3);
        var value = 0;

        for (var index = 0; (index < 3); index++) {
            var packed = mirror[(offset + index)];

            value = (((value * 100) + (((packed >> 4) & 0x0F) * 10)) + (packed & 0x0F));
        }

        return value;
    }

    // Builds a complete, VALID 8 KiB save image around a payload — the independent writer-side implementation of
    // the block format (magic | version | payload | sum16 LE).
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
    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"solitaire ROM verification failed: {message}");
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

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "solitaire");
        }

        // Long enough that a multi-frame LCD-off repaint inside one tick never swallows the edge or the release
        // (every game input is edge-triggered, so a long hold still acts exactly once).
        public void Press(JoypadButtons buttons) {
            RunFrames(buttons: buttons, frames: 8);
            RunFrames(buttons: JoypadButtons.None, frames: 6);
        }
        public byte[] ExportExternalRam() => m_cartridge.ExportExternalRam();
        public void Dispose() => m_machine.Dispose();
    }
}
