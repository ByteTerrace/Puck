using Puck.Demo.Forge.Cards;
using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The Poker self-verify battery: boots the freshly-forged ROM on REAL Humble machines (pure CPU, the same core
/// the demo's cabinets run) and asserts the game's observable work-RAM behaviour through the whole state graph —
/// boot→title, the idle→attract hand-off (the constant-seed deal matched against the C# oracle, the attract table
/// playing hands with SRAM provably untouched), D4 seed entropy AND same-frame replay determinism, the
/// deal-from-seed proof (the post-deal PRNG state walked BACK 51 LCG steps to the seed), evaluator equivalence
/// against a byte-for-byte C# oracle (random deals plus staged straight-flush/quads/boat/flush/wheel/tiebreak
/// showdowns), a staged full hand — deal → bet → draw → showdown with a known winner, every AI action matched
/// against the personality-table decision oracle and every chip movement re-derived — pause freezing the
/// simulation, the session end → initials → save flow both ways (table cleared and busted), SRAM persistence
/// round-tripped through an INDEPENDENT C# checksum, and corruption recovery. Throws on any violation.
/// </summary>
internal static class PokerVerify {
    private const ulong TCyclesPerFrame = 70224UL;
    private const ushort AttractSeed = 0x1234;
    private const int TotalChips = 800;

    /// <summary>Runs the whole battery.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        AssertBootToTitle(rom: rom);
        AssertAttract(rom: rom);
        AssertSeedEntropyAndOracle(rom: rom);
        AssertStagedHand(rom: rom);
        AssertShowdownCategories(rom: rom);
        AssertPersonalitySweep(rom: rom);
        AssertPause(rom: rom);

        var (sram, expectedMirror) = AssertGameOverWinAndEntry(rom: rom);

        AssertGameOverLose(rom: rom);
        AssertSramPersistence(rom: rom, sram: sram, expectedMirror: expectedMirror);
        AssertCorruptionRecovery(rom: rom, sram: sram);

        Console.WriteLine(value: "poker verify | boot→title | attract in+out + oracle deal + sram untouched | seed entropy + same-frame replay | deal-from-seed (LCG inverted 51 steps) | evaluator == oracle (random + SF/quads/boat/flush/wheel/kicker showdowns) | staged hand: bet→draw→showdown, known winner, AI == personality oracle, chips re-derived + conserved | pause freeze | cleared→entry BCA→save | busted→no entry | sram round-trip (independent sum16) | corruption → defaults");
    }

    // ==== The C# oracles (byte-for-byte mirrors of the SM83). ============================================================

    /// <summary>The evaluator oracle: mirrors PokerGame's EmitEvalSeat exactly — the ace-high poker ranks (with the
    /// wheel mirror at index 1), the flush/straight scans, the shape byte through the SAME category table, the
    /// grouped tiebreak pass, and the strength-base table.</summary>
    /// <param name="cards">The five card ids.</param>
    /// <returns>The eight eval bytes: category, five tiebreaks, strength, zero.</returns>
    internal static byte[] EvaluateOracle(IReadOnlyList<byte> cards) {
        ArgumentNullException.ThrowIfNull(cards);

        var rankCounts = new int[16];
        var suitCounts = new int[4];

        foreach (var card in cards) {
            var rank = CardDeck.RankOf(id: card);

            suitCounts[CardDeck.SuitOf(id: card)]++;

            if (rank == CardDeck.RankAce) {
                rankCounts[1]++;
                rankCounts[14]++;
            }
            else {
                rankCounts[rank]++;
            }
        }

        var flush = false;

        for (var suit = 0; (suit < 4); suit++) {
            flush |= (suitCounts[suit] == PokerProtocol.HandSize);
        }

        var straightHigh = 0;

        for (var high = 14; (high >= 5); high--) {
            var run = true;

            for (var step = 0; (step < 5); step++) {
                run &= (rankCounts[high - step] != 0);
            }

            if (run) {
                straightHigh = high;

                break;
            }
        }

        var quadRank = 0;
        var tripRank = 0;
        var pairCount = 0;
        var pairHigh = 0;
        var pairLow = 0;

        for (var rank = 14; (rank >= 2); rank--) {
            var count = rankCounts[rank];

            if (count >= 4) {
                quadRank = rank;
            }
            else if (count == 3) {
                tripRank = rank;
            }
            else if (count == 2) {
                pairCount++;

                if (pairHigh == 0) {
                    pairHigh = rank;
                }
                else {
                    pairLow = rank;
                }
            }
        }

        var shape = ((flush ? 1 : 0) | ((straightHigh != 0) ? 2 : 0) | (pairCount << 2) | ((tripRank != 0) ? 0x10 : 0) | ((quadRank != 0) ? 0x20 : 0));
        var category = PokerTables.BuildCategoryTable()[shape];
        var tiebreaks = new byte[5];

        if (straightHigh != 0) {
            tiebreaks[0] = (byte)straightHigh;
        }
        else {
            var index = 0;

            for (var target = 4; (target >= 1); target--) {
                for (var rank = 14; (rank >= 2); rank--) {
                    if (rankCounts[rank] == target) {
                        tiebreaks[index++] = (byte)rank;
                    }
                }
            }
        }

        var strength = (byte)(PokerTables.BuildStrengthBaseTable()[category] + tiebreaks[0]);

        return [category, tiebreaks[0], tiebreaks[1], tiebreaks[2], tiebreaks[3], tiebreaks[4], strength, 0];
    }

    /// <summary>The decision-seam oracle: mirrors PokerGame's EmitDecide — the personality row (seat 0 borrows
    /// index 2 in attract auto-play), exactly one LCG draw for the bluff roll, then the threshold ladder.</summary>
    /// <param name="seat">The deciding seat.</param>
    /// <param name="strength">The seat's evaluated strength.</param>
    /// <param name="facing">Whether an outstanding bet must be answered.</param>
    /// <param name="raises">Bets/raises already used this round.</param>
    /// <param name="prngState">The PRNG state going in.</param>
    /// <returns>The intended action and the PRNG state after the roll.</returns>
    internal static (int Action, ushort NextState) DecideOracle(int seat, byte strength, bool facing, int raises, ushort prngState) {
        var row = PokerTables.BuildPersonalityRecords()[((seat == 0) ? 2 : (seat - 1))];
        var state = CardDeck.NextState(state: prngState);
        var roll = (byte)(state >> 8);
        var bluff = (roll < row[PokerTables.PersonalityFieldBluff]);
        int action;

        if (!facing) {
            action = (((strength >= row[PokerTables.PersonalityFieldBet]) || bluff) ? PokerProtocol.ActionBetRaise : PokerProtocol.ActionCheckCall);
        }
        else if ((raises < PokerProtocol.RaiseCap) && (strength >= row[PokerTables.PersonalityFieldRaise])) {
            action = PokerProtocol.ActionBetRaise;
        }
        else if ((strength >= row[PokerTables.PersonalityFieldCall]) || bluff) {
            action = PokerProtocol.ActionCheckCall;
        }
        else {
            action = PokerProtocol.ActionFold;
        }

        return (action, state);
    }

    // The table's legality downgrades, mirrored from EmitBettingTick: an over-cap or unaffordable raise becomes a
    // call, an unaffordable call becomes a fold.
    private static int FilterLegality(int intent, int betLevel, int roundBet, int raises, int bankrollChips) {
        var action = intent;

        if (action == PokerProtocol.ActionBetRaise) {
            if ((raises >= PokerProtocol.RaiseCap) || (bankrollChips < ((betLevel + 1 - roundBet) * 10))) {
                action = PokerProtocol.ActionCheckCall;
            }
        }

        if (action == PokerProtocol.ActionCheckCall) {
            var needed = (betLevel - roundBet);

            if ((needed > 0) && (bankrollChips < (needed * 10))) {
                action = PokerProtocol.ActionFold;
            }
        }

        return action;
    }

    // ==== The staged-flow plumbing. ======================================================================================

    private enum TableEvent { Action, Menu, DrawSelect, HandEnd, GameOver }

    private sealed record TableSnap(ushort Prng, byte BetLevel, byte RaiseCount, byte[] RoundBet, byte[] Strength, byte[] Folded, int[] Bankrolls, int Pot, byte InHand, byte Phase);

    private static TableSnap Snapshot(Driver driver) {
        var roundBet = new byte[PokerProtocol.SeatCount];
        var strength = new byte[PokerProtocol.SeatCount];
        var folded = new byte[PokerProtocol.SeatCount];
        var bankrolls = new int[PokerProtocol.SeatCount];

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            roundBet[seat] = driver.Read(address: (ushort)(PokerProtocol.RoundBetBase + seat));
            strength[seat] = driver.Read(address: (ushort)(PokerProtocol.StrengthBase + seat));
            folded[seat] = driver.Read(address: (ushort)(PokerProtocol.FoldedBase + seat));
            bankrolls[seat] = ReadBankroll(driver: driver, seat: seat);
        }

        return new TableSnap(
            BetLevel: driver.Read(address: PokerProtocol.BetLevel),
            Bankrolls: bankrolls,
            Folded: folded,
            InHand: driver.Read(address: PokerProtocol.InHand),
            Phase: driver.Read(address: PokerProtocol.Phase),
            Pot: ReadPot(driver: driver),
            Prng: (ushort)driver.ReadWide(address: FrameworkMemoryMap.PrngState),
            RaiseCount: driver.Read(address: PokerProtocol.RaiseCount),
            RoundBet: roundBet,
            Strength: strength
        );
    }

    // Steps single frames until the next observable table event, returning the PRE-event snapshot for Action
    // events (the oracle's inputs are the state the actor decided against).
    private static (TableEvent Event, TableSnap Before) WaitEvent(Driver driver, ref byte serial, int boundFrames) {
        var before = Snapshot(driver: driver);

        for (var frame = 0; (frame < boundFrames); frame++) {
            if (driver.Read(address: PokerProtocol.TurnSerial) != serial) {
                serial = driver.Read(address: PokerProtocol.TurnSerial);

                return (TableEvent.Action, before);
            }

            if (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateGameOver) {
                return (TableEvent.GameOver, before);
            }

            if (driver.Read(address: PokerProtocol.AwaitInput) == 1) {
                return (TableEvent.Menu, before);
            }

            if (driver.Read(address: PokerProtocol.AwaitInput) == 2) {
                return (TableEvent.DrawSelect, before);
            }

            if (driver.Read(address: PokerProtocol.Phase) == PokerProtocol.PhaseHandEnd) {
                return (TableEvent.HandEnd, before);
            }

            before = Snapshot(driver: driver);
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
        }

        throw new InvalidOperationException(message: "poker ROM verification failed: the table produced no observable event within the frame bound.");
    }

    // Asserts one just-applied AI action against the decision oracle + legality filter + chip arithmetic, all
    // derived from the pre-event snapshot.
    private static void AssertAiActionMatchesOracle(Driver driver, TableSnap before, string context) {
        var actor = driver.Read(address: PokerProtocol.LastActor);

        if (actor == 0) {
            return; // The player's own action — the legs assert its effects explicitly.
        }

        var facing = (before.BetLevel != before.RoundBet[actor]);
        var (intent, _) = DecideOracle(seat: actor, strength: before.Strength[actor], facing: facing, raises: before.RaiseCount, prngState: before.Prng);
        var action = FilterLegality(intent: intent, betLevel: before.BetLevel, roundBet: before.RoundBet[actor], raises: before.RaiseCount, bankrollChips: before.Bankrolls[actor]);
        var observed = driver.Read(address: (ushort)(PokerProtocol.LastActionBase + actor));
        byte expectedDisplay;
        var expectedPaid = 0;
        var expectedLevel = (int)before.BetLevel;

        if (action == PokerProtocol.ActionFold) {
            expectedDisplay = PokerProtocol.ActedFold;
        }
        else if (action == PokerProtocol.ActionCheckCall) {
            var needed = (before.BetLevel - before.RoundBet[actor]);

            expectedDisplay = ((needed > 0) ? PokerProtocol.ActedCall : PokerProtocol.ActedCheck);
            expectedPaid = (needed * 10);
        }
        else {
            expectedDisplay = ((before.BetLevel > 0) ? PokerProtocol.ActedRaise : PokerProtocol.ActedBet);
            expectedLevel = (before.BetLevel + 1);
            expectedPaid = ((expectedLevel - before.RoundBet[actor]) * 10);
        }

        Assert(condition: (observed == expectedDisplay), message: $"{context}: seat {actor} acted {observed}, the personality oracle expected {expectedDisplay} (intent {intent}, filtered {action})");
        Assert(condition: (ReadBankroll(driver: driver, seat: actor) == (before.Bankrolls[actor] - expectedPaid)), message: $"{context}: seat {actor}'s bankroll moved to {ReadBankroll(driver: driver, seat: actor)} (expected {before.Bankrolls[actor] - expectedPaid})");
        Assert(condition: (driver.Read(address: PokerProtocol.BetLevel) == expectedLevel), message: $"{context}: the bet level is {driver.Read(address: PokerProtocol.BetLevel)} (expected {expectedLevel})");

        if (driver.Read(address: PokerProtocol.Phase) != PokerProtocol.PhaseHandEnd) {
            // A pot award (an uncontested fold-out) empties the pot in the same event; only assert mid-round.
            Assert(condition: (ReadPot(driver: driver) == (before.Pot + expectedPaid)), message: $"{context}: the pot is {ReadPot(driver: driver)} (expected {before.Pot + expectedPaid})");
        }
    }

    // Finds the first PRNG seed whose bluff rolls keep DOT honest twice and IVY honest once (consumption order:
    // DOT, REX, IVY, DOT) — so a staged weak table folds to the one staged bettor, deterministically.
    private static ushort FindHonestFoldSeed() {
        var personalities = PokerTables.BuildPersonalityRecords();
        var dotBluff = personalities[0][PokerTables.PersonalityFieldBluff];
        var ivyBluff = personalities[2][PokerTables.PersonalityFieldBluff];

        for (var seed = 1; (seed < 0x10000); seed++) {
            var state = (ushort)seed;
            var rolls = new byte[4];

            for (var index = 0; (index < 4); index++) {
                state = CardDeck.NextState(state: state);
                rolls[index] = (byte)(state >> 8);
            }

            if ((rolls[0] >= dotBluff) && (rolls[2] >= ivyBluff) && (rolls[3] >= dotBluff)) {
                return (ushort)seed;
            }
        }

        throw new InvalidOperationException(message: "poker ROM verification failed: no honest-fold seed exists (the personality tables changed shape).");
    }

    // ==== Read helpers. ==================================================================================================

    private static int BcdToInt(byte high, byte low) =>
        (((((high >> 4) & 0x0F) * 10) + (high & 0x0F)) * 100) + ((((low >> 4) & 0x0F) * 10) + (low & 0x0F));

    private static int ReadBankroll(Driver driver, int seat) =>
        BcdToInt(high: driver.Read(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2))), low: driver.Read(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2) + 1)));

    private static int ReadPot(Driver driver) =>
        BcdToInt(high: driver.Read(address: PokerProtocol.Pot), low: driver.Read(address: (ushort)(PokerProtocol.Pot + 1)));

    private static int ChipTotal(Driver driver) {
        var total = ReadPot(driver: driver);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            total += ReadBankroll(driver: driver, seat: seat);
        }

        return total;
    }

    private static byte[] ReadHand(Driver driver, int seat) {
        var hand = new byte[PokerProtocol.HandSize];

        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            hand[slot] = driver.Read(address: (ushort)(PokerProtocol.HandBase + (seat * PokerProtocol.HandStride) + slot));
        }

        return hand;
    }

    private static byte[] ReadEval(Driver driver, int seat) {
        var eval = new byte[8];

        for (var index = 0; (index < 8); index++) {
            eval[index] = driver.Read(address: (ushort)(PokerProtocol.EvalBase + (seat * 8) + index));
        }

        return eval;
    }

    private static void WriteHand(Driver driver, int seat, byte[] cards) {
        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            driver.Write(address: (ushort)(PokerProtocol.HandBase + (seat * PokerProtocol.HandStride) + slot), value: cards[slot]);
        }
    }

    private static void WriteStrength(Driver driver, int seat, byte strength) =>
        driver.Write(address: (ushort)(PokerProtocol.StrengthBase + seat), value: strength);

    private static void WriteBankroll(Driver driver, int seat, byte high, byte low) {
        driver.Write(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2)), value: high);
        driver.Write(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2) + 1), value: low);
    }

    private static byte[] ReadSavePayload(Driver driver) {
        var payload = new byte[PokerProtocol.SavePayloadByteCount];

        for (var index = 0; (index < payload.Length); index++) {
            payload[index] = driver.Read(address: (ushort)(PokerProtocol.HiScoreMirror + index));
        }

        return payload;
    }

    // Confirms DEAL on the title after `idleFrames` (the D4 entropy sample point).
    private static void StartPlay(Driver driver, int idleFrames) {
        driver.RunFrames(buttons: JoypadButtons.None, frames: idleFrames);
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 2);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StatePlay), message: "confirming DEAL did not start a session");
    }

    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"poker ROM verification failed: {message}");
        }
    }

    // ==== The battery legs. ==============================================================================================

    // (1) Boot: the machine reaches the title within ~8 frames with the VBlank handler alive and the boot's
    // initial-state request consumed.
    private static void AssertBootToTitle(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
    }

    // (2) Attract: 620 idle frames fall into the scripted four-AI table, its constant-seed deal matches the C#
    // oracle, the antes were taken, the table demonstrably plays hands, SRAM stays untouched throughout, and any
    // REAL press hands control back to the title.
    private static void AssertAttract(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 630);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateAttract), message: $"620 idle frames did not start the attract loop (state {driver.Read(address: FrameworkMemoryMap.GameState)})");

        var deck = CardDeck.ShuffleOracle(seed: AttractSeed, finalState: out _);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var hand = ReadHand(driver: driver, seat: seat);

            for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
                Assert(condition: (hand[slot] == deck[(seat * PokerProtocol.HandSize) + slot]), message: $"the attract deal: seat {seat} card {slot} is {hand[slot]} (the oracle dealt {deck[(seat * PokerProtocol.HandSize) + slot]})");
            }

            Assert(condition: (ReadBankroll(driver: driver, seat: seat) == 195), message: $"the attract ante: seat {seat} holds {ReadBankroll(driver: driver, seat: seat)} chips (expected 195)");
        }

        Assert(condition: (ReadPot(driver: driver) == 20), message: $"the attract ante left the pot at {ReadPot(driver: driver)} (expected 20)");
        driver.RunFrames(buttons: JoypadButtons.None, frames: 1300);
        Assert(condition: (driver.Read(address: PokerProtocol.TurnSerial) > 0), message: "the attract table never applied an action (the auto-played table is dead)");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateAttract), message: "the attract loop ended early");

        foreach (var value in driver.ExportExternalRam()) {
            Assert(condition: (value == 0), message: "the attract table wrote SRAM (attract must never save)");
        }

        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateTitle), message: "a real press during attract did not return to the title");
    }

    // (3) D4 seed entropy + the deal/evaluator oracles: different confirm frames yield different PRNG states AND
    // deals; the same frame replays the identical table; the C# Fisher–Yates from the RECOVERED seed reproduces
    // every hand and the undealt deck; and every dealt hand's evaluation matches the C# evaluator byte for byte.
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

        var firstHands = ReadAllHands(driver: first);

        Assert(condition: firstHands.SequenceEqual(second: ReadAllHands(driver: replay)), message: "same-frame confirms dealt different tables (replay broken)");
        Assert(condition: !firstHands.SequenceEqual(second: ReadAllHands(driver: second)), message: "different-frame confirms dealt the same table (the deal ignores the seed)");

        // Walk the observed state back exactly 51 draws to the seed the machine sampled, then predict the deal.
        var seed = (ushort)firstState;

        for (var step = 0; (step < 51); step++) {
            seed = CardDeck.StepBack(state: seed);
        }

        var deck = CardDeck.ShuffleOracle(seed: seed, finalState: out _);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var hand = ReadHand(driver: first, seat: seat);

            for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
                Assert(condition: (hand[slot] == deck[(seat * PokerProtocol.HandSize) + slot]), message: $"the recovered-seed deal: seat {seat} card {slot} is {hand[slot]} (the oracle dealt {deck[(seat * PokerProtocol.HandSize) + slot]})");
            }
        }

        var seen = new HashSet<byte>();

        for (var index = 0; (index < CardDeck.CardCount); index++) {
            var actual = first.Read(address: (ushort)(PokerProtocol.DeckScratch + index));

            _ = seen.Add(item: actual);

            if (index >= (PokerProtocol.SeatCount * PokerProtocol.HandSize)) {
                Assert(condition: (actual == deck[index]), message: $"the recovered-seed deal: deck[{index}] is {actual} (the oracle holds {deck[index]})");
            }
        }

        Assert(condition: (seen.Count == CardDeck.CardCount), message: $"the deal is not a 52-card permutation ({seen.Count} distinct ids)");
        Assert(condition: (first.Read(address: PokerProtocol.NextCard) == (PokerProtocol.SeatCount * PokerProtocol.HandSize)), message: "the draw pointer did not start past the dealt hands");
        Assert(condition: (ChipTotal(driver: first) == TotalChips), message: $"the fresh table holds {ChipTotal(driver: first)} chips (expected {TotalChips})");
        Assert(condition: (ReadPot(driver: first) == 20), message: $"the ante left the pot at {ReadPot(driver: first)} (expected 20)");

        // Evaluator equivalence over the random deals (plus two extra sessions for breadth).
        using var third = new Driver(rom: rom);
        using var fourth = new Driver(rom: rom);

        StartPlay(driver: third, idleFrames: 52);
        StartPlay(driver: fourth, idleFrames: 61);

        foreach (var driver in new[] { first, second, third, fourth }) {
            for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
                var expected = EvaluateOracle(cards: ReadHand(driver: driver, seat: seat));
                var actual = ReadEval(driver: driver, seat: seat);

                Assert(condition: actual.SequenceEqual(second: expected), message: $"seat {seat}'s dealt evaluation [{string.Join(separator: ",", values: actual)}] disagrees with the oracle [{string.Join(separator: ",", values: expected)}]");
                Assert(condition: (driver.Read(address: (ushort)(PokerProtocol.StrengthBase + seat)) == expected[6]), message: $"seat {seat}'s cached strength disagrees with the oracle");
            }
        }
    }

    // (4) The staged full hand — the flagship leg: a known table (player quads, one caller, two honest folders),
    // every AI action matched against the personality oracle with its chips re-derived, the draw phase replacing
    // exactly the marked cards from the deck, the second round's re-evaluations, the showdown's evaluations and
    // winner, the pot award, the records, and chip conservation.
    private static void AssertStagedHand(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 40);

        byte[][] hands = [
            [0, 13, 26, 39, 1],   // YOU: A♠ A♥ A♦ A♣ 2♠ — quad aces.
            [12, 23, 34, 45, 4],  // DOT: K-J-9-7-5 offsuit — air.
            [5, 18, 25, 47, 28],  // REX: 6♠ 6♥ K♥ 9♣ 3♦ — a pair of sixes (calls, draws three).
            [37, 9, 20, 42, 14],  // IVY: Q-10-8-4-2 offsuit — air.
        ];

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            WriteHand(driver: driver, seat: seat, cards: hands[seat]);
            WriteStrength(driver: driver, seat: seat, strength: EvaluateOracle(cards: hands[seat])[6]);
        }

        var seedValue = FindHonestFoldSeed();

        driver.Write(address: FrameworkMemoryMap.PrngState, value: (byte)(seedValue & 0xFF));
        driver.Write(address: (ushort)(FrameworkMemoryMap.PrngState + 1), value: (byte)(seedValue >> 8));

        var deck = new byte[CardDeck.CardCount];

        for (var index = 0; (index < deck.Length); index++) {
            deck[index] = driver.Read(address: (ushort)(PokerProtocol.DeckScratch + index));
        }

        var serial = driver.Read(address: PokerProtocol.TurnSerial);
        var playerActed = false;
        var reachedDraw = false;
        var expectedFinalPot = 0;
        var guard = 0;

        while (true) {
            Assert(condition: (++guard < 64), message: "the staged hand never resolved (the table is looping)");

            var (tableEvent, before) = WaitEvent(driver: driver, serial: ref serial, boundFrames: 600);

            if (tableEvent == TableEvent.Action) {
                AssertAiActionMatchesOracle(driver: driver, before: before, context: "the staged hand");

                continue;
            }

            if (tableEvent == TableEvent.Menu) {
                var phase = driver.Read(address: PokerProtocol.Phase);

                if ((phase == PokerProtocol.PhaseBet1) && !playerActed) {
                    // REX has bet: the menu must face it, and the call must cost exactly one unit.
                    Assert(condition: (driver.Read(address: PokerProtocol.Facing) == 1), message: "the player's first menu is not facing REX's bet");
                    Assert(condition: (driver.Read(address: PokerProtocol.BetLevel) == 1), message: $"the bet level is {driver.Read(address: PokerProtocol.BetLevel)} at the player's first turn (expected 1)");

                    var chipsBefore = ReadBankroll(driver: driver, seat: 0);

                    driver.Press(buttons: JoypadButtons.A); // Cursor 0 = CALL.
                    serial = driver.Read(address: PokerProtocol.TurnSerial);
                    Assert(condition: (ReadBankroll(driver: driver, seat: 0) == (chipsBefore - 10)), message: "the player's call did not cost ten chips");
                    playerActed = true;

                    continue;
                }

                // The second round: the pot peaks with this final check/call, then the showdown fires.
                var needed = (driver.Read(address: PokerProtocol.BetLevel) - driver.Read(address: PokerProtocol.RoundBetBase));

                expectedFinalPot = (ReadPot(driver: driver) + (needed * 10));
                driver.Press(buttons: JoypadButtons.A);
                serial = driver.Read(address: PokerProtocol.TurnSerial);

                continue;
            }

            if (tableEvent == TableEvent.DrawSelect) {
                reachedDraw = true;

                // REX (the only surviving AI) has already drawn: a pair keeps two, so slots 2..4 came off the deck.
                Assert(condition: (driver.Read(address: (ushort)(PokerProtocol.FoldedBase + 1)) == PokerProtocol.SeatFolded), message: "DOT did not fold to the staged bet");
                Assert(condition: (driver.Read(address: (ushort)(PokerProtocol.FoldedBase + 3)) == PokerProtocol.SeatFolded), message: "IVY did not fold to the staged bet");
                Assert(condition: (driver.Read(address: (ushort)(PokerProtocol.DrawCountBase + 2)) == 3), message: $"REX drew {driver.Read(address: (ushort)(PokerProtocol.DrawCountBase + 2))} cards (the rules say a pair draws 3)");

                var rexHand = ReadHand(driver: driver, seat: 2);

                Assert(condition: ((rexHand[0] == 5) && (rexHand[1] == 18)), message: "REX did not keep his pair of sixes");
                Assert(condition: ((rexHand[2] == deck[20]) && (rexHand[3] == deck[21]) && (rexHand[4] == deck[22])), message: "REX's replacements did not come off the deck in order");

                // The player discards the kicker (slot 4): Right ×4, mark, confirm.
                for (var step = 0; (step < 4); step++) {
                    driver.Press(buttons: JoypadButtons.Right);
                }

                Assert(condition: (driver.Read(address: PokerProtocol.DrawCursor) == 4), message: "the draw cursor did not reach the fifth card");
                driver.Press(buttons: JoypadButtons.A);
                Assert(condition: (driver.Read(address: PokerProtocol.DiscardMask) == 0x10), message: $"marking the fifth card set mask 0x{driver.Read(address: PokerProtocol.DiscardMask):X2} (expected 0x10)");
                driver.Press(buttons: JoypadButtons.Start);

                var playerHand = ReadHand(driver: driver, seat: 0);

                Assert(condition: (playerHand[4] == deck[23]), message: "the player's replacement did not come off the deck");
                Assert(condition: (driver.Read(address: (ushort)PokerProtocol.DrawCountBase) == 1), message: "the player's draw count is wrong");

                continue;
            }

            if (tableEvent == TableEvent.HandEnd) {
                break;
            }

            Assert(condition: false, message: $"the staged hand hit an unexpected {tableEvent} event");
        }

        Assert(condition: reachedDraw, message: "the staged hand never reached the draw phase");

        // Strengths were re-evaluated for the second round; the showdown must agree with the oracle end to end.
        var playerEval = EvaluateOracle(cards: ReadHand(driver: driver, seat: 0));
        var rexEval = EvaluateOracle(cards: ReadHand(driver: driver, seat: 2));

        Assert(condition: ReadEval(driver: driver, seat: 0).SequenceEqual(second: playerEval), message: "the showdown's player evaluation disagrees with the oracle");
        Assert(condition: ReadEval(driver: driver, seat: 2).SequenceEqual(second: rexEval), message: "the showdown's REX evaluation disagrees with the oracle");
        Assert(condition: (playerEval[0] == 7), message: $"the staged quads evaluated as category {playerEval[0]}");
        Assert(condition: (driver.Read(address: PokerProtocol.WinnerSeat) == 0), message: $"seat {driver.Read(address: PokerProtocol.WinnerSeat)} won the staged hand (the quads must)");
        Assert(condition: (ReadPot(driver: driver) == 0), message: "the pot was not paid out");
        Assert(condition: (ChipTotal(driver: driver) == TotalChips), message: $"the table leaked chips ({ChipTotal(driver: driver)} != {TotalChips})");
        Assert(condition: (BcdToInt(high: driver.Read(address: PokerProtocol.HandsWonMirror), low: driver.Read(address: (ushort)(PokerProtocol.HandsWonMirror + 1))) == 1), message: "the hands-won record did not count the player's win");
        Assert(condition: (BcdToInt(high: driver.Read(address: PokerProtocol.BiggestPotMirror), low: driver.Read(address: (ushort)(PokerProtocol.BiggestPotMirror + 1))) == expectedFinalPot), message: $"the biggest-pot record is {BcdToInt(high: driver.Read(address: PokerProtocol.BiggestPotMirror), low: driver.Read(address: (ushort)(PokerProtocol.BiggestPotMirror + 1)))} (expected {expectedFinalPot})");
    }

    // (5) Staged showdown categories: heads-up hands poked in right before the final call, covering the wheel,
    // the straight flush (an AI win), the full boat over a flush, and a two-pair kicker fight.
    private static void AssertShowdownCategories(byte[] rom) {
        AssertOneShowdown(rom: rom, idleFrames: 42, playerHand: [0, 14, 28, 42, 4], rexHand: [6, 19, 32, 51, 27], expectedWinner: 0, context: "the wheel vs trips", expectedPlayerTb0: 5);
        AssertOneShowdown(rom: rom, idleFrames: 43, playerHand: [12, 25, 38, 51, 29], rexHand: [17, 18, 19, 20, 21], expectedWinner: 2, context: "quads vs the straight flush", expectedPlayerTb0: 13);
        AssertOneShowdown(rom: rom, idleFrames: 44, playerHand: [2, 15, 28, 1, 14], rexHand: [40, 43, 45, 47, 49], expectedWinner: 0, context: "the full boat vs the flush", expectedPlayerTb0: 3);
        AssertOneShowdown(rom: rom, idleFrames: 45, playerHand: [12, 25, 28, 41, 11], rexHand: [38, 51, 2, 15, 36], expectedWinner: 0, context: "the two-pair kicker fight", expectedPlayerTb0: 13);
    }

    private static void AssertOneShowdown(byte[] rom, int idleFrames, byte[] playerHand, byte[] rexHand, byte expectedWinner, string context, byte expectedPlayerTb0) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: idleFrames);
        WriteStrength(driver: driver, seat: 0, strength: 50);
        WriteStrength(driver: driver, seat: 1, strength: 5);
        WriteStrength(driver: driver, seat: 2, strength: 80);
        WriteStrength(driver: driver, seat: 3, strength: 5);

        var seedValue = FindHonestFoldSeed();

        driver.Write(address: FrameworkMemoryMap.PrngState, value: (byte)(seedValue & 0xFF));
        driver.Write(address: (ushort)(FrameworkMemoryMap.PrngState + 1), value: (byte)(seedValue >> 8));

        var serial = driver.Read(address: PokerProtocol.TurnSerial);
        var guard = 0;

        while (true) {
            Assert(condition: (++guard < 64), message: $"{context}: the hand never resolved");

            var (tableEvent, before) = WaitEvent(driver: driver, serial: ref serial, boundFrames: 600);

            if (tableEvent == TableEvent.Action) {
                AssertAiActionMatchesOracle(driver: driver, before: before, context: context);

                continue;
            }

            if (tableEvent == TableEvent.DrawSelect) {
                driver.Press(buttons: JoypadButtons.Start); // Keep everything.

                continue;
            }

            if (tableEvent == TableEvent.Menu) {
                if (driver.Read(address: PokerProtocol.Phase) == PokerProtocol.PhaseBet2) {
                    // The last action before the showdown: stage the exotic hands now — the showdown re-evaluates
                    // from these bytes.
                    WriteHand(driver: driver, seat: 0, cards: playerHand);
                    WriteHand(driver: driver, seat: 2, cards: rexHand);
                }

                driver.Press(buttons: JoypadButtons.A);
                serial = driver.Read(address: PokerProtocol.TurnSerial);

                continue;
            }

            if (tableEvent == TableEvent.HandEnd) {
                break;
            }

            Assert(condition: false, message: $"{context}: unexpected {tableEvent} event");
        }

        var playerEval = EvaluateOracle(cards: playerHand);
        var rexEval = EvaluateOracle(cards: rexHand);

        Assert(condition: ReadEval(driver: driver, seat: 0).SequenceEqual(second: playerEval), message: $"{context}: the player's evaluation disagrees with the oracle");
        Assert(condition: ReadEval(driver: driver, seat: 2).SequenceEqual(second: rexEval), message: $"{context}: REX's evaluation disagrees with the oracle");
        Assert(condition: (playerEval[1] == expectedPlayerTb0), message: $"{context}: the player's top tiebreak is {playerEval[1]} (expected {expectedPlayerTb0})");
        Assert(condition: (driver.Read(address: PokerProtocol.WinnerSeat) == expectedWinner), message: $"{context}: seat {driver.Read(address: PokerProtocol.WinnerSeat)} won (expected {expectedWinner})");
        Assert(condition: (ChipTotal(driver: driver) == TotalChips), message: $"{context}: the table leaked chips");
    }

    // (6) The personality sweep: staged strengths rotated across the three opponents, three sessions, every
    // first-round decision matched against the oracle over the SAME personality records the manifest links.
    private static void AssertPersonalitySweep(byte[] rom) {
        byte[] strengths = [5, 40, 150];

        for (var trial = 0; (trial < 3); trial++) {
            using var driver = new Driver(rom: rom);

            StartPlay(driver: driver, idleFrames: (40 + trial));

            for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
                WriteStrength(driver: driver, seat: seat, strength: strengths[((seat - 1) + trial) % 3]);
            }

            var seedValue = (ushort)(0x4321 + (trial * 7));

            driver.Write(address: FrameworkMemoryMap.PrngState, value: (byte)(seedValue & 0xFF));
            driver.Write(address: (ushort)(FrameworkMemoryMap.PrngState + 1), value: (byte)(seedValue >> 8));

            var serial = driver.Read(address: PokerProtocol.TurnSerial);

            // The three opponents act before the player's first turn; every one must match the oracle.
            for (var action = 0; (action < 3); action++) {
                var (tableEvent, before) = WaitEvent(driver: driver, serial: ref serial, boundFrames: 600);

                Assert(condition: (tableEvent == TableEvent.Action), message: $"sweep {trial}: expected an AI action, saw {tableEvent}");
                AssertAiActionMatchesOracle(driver: driver, before: before, context: $"sweep {trial}");
            }
        }
    }

    // (7) Pause freezes the simulation (the frame counter keeps running); SELECT abandons to the title and saves.
    private static void AssertPause(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 45);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 5);
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StatePause), message: "START did not pause");

        var serialBefore = driver.Read(address: PokerProtocol.TurnSerial);
        var potBefore = ReadPot(driver: driver);
        var framesBefore = driver.ReadWide(address: FrameworkMemoryMap.FrameCounter);

        driver.RunFrames(buttons: JoypadButtons.A, frames: 90);
        Assert(condition: (driver.Read(address: PokerProtocol.TurnSerial) == serialBefore), message: "the table advanced while paused");
        Assert(condition: (ReadPot(driver: driver) == potBefore), message: "the pot changed while paused");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) != framesBefore), message: "the frame counter froze during pause (the handler died)");
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StatePlay), message: "START did not unpause");

        driver.Press(buttons: JoypadButtons.Start);
        driver.Press(buttons: JoypadButtons.Select);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateTitle), message: "SELECT in pause did not abandon to the title");

        var sram = driver.ExportExternalRam();

        Assert(condition: ((sram[0] == SaveModule.MagicLow) && (sram[1] == SaveModule.MagicHigh)), message: "abandoning did not persist the table");
    }

    // Auto-plays the player's seat (call/check every menu, stand pat on every draw) until the hand resolves.
    private static void AutoPlayUntilHandEnd(Driver driver, int boundFrames) {
        for (var frame = 0; (frame < boundFrames); frame++) {
            if (driver.Read(address: PokerProtocol.Phase) == PokerProtocol.PhaseHandEnd) {
                return;
            }

            if (driver.Read(address: PokerProtocol.AwaitInput) == 1) {
                driver.Press(buttons: JoypadButtons.A);
            }
            else if (driver.Read(address: PokerProtocol.AwaitInput) == 2) {
                driver.Press(buttons: JoypadButtons.Start);
            }
            else {
                driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            }
        }

        throw new InvalidOperationException(message: "poker ROM verification failed: the auto-played hand never resolved.");
    }

    // (8) The table cleared: every opponent busted at a hand's end wins the session — the final stack qualifies,
    // initials entry persists it, and the table's chips reset for the next session. Returns the SRAM for the
    // round-trip legs.
    private static (byte[] Sram, byte[] ExpectedMirror) AssertGameOverWinAndEntry(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 40);
        AutoPlayUntilHandEnd(driver: driver, boundFrames: 6000);

        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            WriteBankroll(driver: driver, seat: seat, high: 0x00, low: 0x00);
        }

        driver.Press(buttons: JoypadButtons.A);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateGameOver), message: "busting every opponent did not end the session");
        Assert(condition: (driver.Read(address: PokerProtocol.GameOverKind) == 1), message: "clearing the table was not scored as a win");

        var scoreHigh = driver.Read(address: PokerProtocol.BankrollMirror);
        var scoreLow = driver.Read(address: (ushort)(PokerProtocol.BankrollMirror + 1));

        Assert(condition: (BcdToInt(high: scoreHigh, low: scoreLow) > 100), message: "the winning stack does not clear the table's fifth entry (the leg's premise broke)");
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateScoreEntry), message: "a qualifying stack never reached initials entry");

        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Right); // Slot 1.
        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Up);    // B → C.
        driver.Press(buttons: JoypadButtons.Right); // Slot 2 stays A.
        Assert(condition: ((driver.Read(address: PokerProtocol.EntryGlyphs) == 1) && (driver.Read(address: (ushort)(PokerProtocol.EntryGlyphs + 1)) == 2) && (driver.Read(address: (ushort)(PokerProtocol.EntryGlyphs + 2)) == 0)), message: "the initials pad did not spell BCA");
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateHighScores), message: "confirming the initials did not land on the records screen");

        var mirror = ReadSavePayload(driver: driver);
        var slot = FindEntry(mirror: mirror, initials: "BCA");

        Assert(condition: (slot >= 0), message: "the BCA entry is missing from the records mirror");
        Assert(condition: ((mirror[(slot * PokerProtocol.HiScoreEntryByteCount) + 3] == 0x00) && (mirror[(slot * PokerProtocol.HiScoreEntryByteCount) + 4] == scoreHigh) && (mirror[(slot * PokerProtocol.HiScoreEntryByteCount) + 5] == scoreLow)), message: "the persisted entry's score does not match the final stack");

        for (var entry = 1; (entry < PokerProtocol.HiScoreEntryCount); entry++) {
            Assert(condition: (EntryScore(mirror: mirror, entry: (entry - 1)) >= EntryScore(mirror: mirror, entry: entry)), message: $"the records table is not sorted (entry {entry - 1} < entry {entry})");
        }

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            Assert(condition: (ReadBankroll(driver: driver, seat: seat) == 200), message: $"seat {seat}'s chips did not reset after the session (holds {ReadBankroll(driver: driver, seat: seat)})");
        }

        return (driver.ExportExternalRam(), ReadSavePayload(driver: driver));
    }

    // (9) Busting out: the player unable to ante loses the session, a zero stack never qualifies, and the table
    // still resets for the next session.
    private static void AssertGameOverLose(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 41);
        AutoPlayUntilHandEnd(driver: driver, boundFrames: 6000);
        WriteBankroll(driver: driver, seat: 0, high: 0x00, low: 0x00);
        driver.Press(buttons: JoypadButtons.A);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateGameOver), message: "busting the player did not end the session");
        Assert(condition: (driver.Read(address: PokerProtocol.GameOverKind) == 0), message: "busting out was scored as a win");
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateHighScores), message: "a busted session's zero stack reached initials entry");

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            Assert(condition: (ReadBankroll(driver: driver, seat: seat) == 200), message: $"seat {seat}'s chips did not reset after busting out");
        }
    }

    // (10) SRAM persistence: validate the exported block with an INDEPENDENT checksum, then import into a FRESH
    // machine and confirm the boot load lands the whole persisted payload in the mirror.
    private static void AssertSramPersistence(byte[] rom, byte[] sram, byte[] expectedMirror) {
        Assert(condition: (sram.Length == 0x2000), message: $"the exported SRAM is {sram.Length} bytes (expected the MBC1 header's 8 KiB)");
        Assert(condition: ((sram[0] == SaveModule.MagicLow) && (sram[1] == SaveModule.MagicHigh)), message: "the persisted block's magic is wrong");
        Assert(condition: (sram[2] == 1), message: $"the persisted block's version is {sram[2]} (expected 1)");

        var payload = sram.AsSpan(start: SaveModule.HeaderByteCount, length: PokerProtocol.SavePayloadByteCount);
        var sum = 0;

        foreach (var value in payload) {
            sum = ((sum + value) & 0xFFFF);
        }

        var stored = (sram[SaveModule.HeaderByteCount + PokerProtocol.SavePayloadByteCount] | (sram[SaveModule.HeaderByteCount + PokerProtocol.SavePayloadByteCount + 1] << 8));

        Assert(condition: (sum == stored), message: $"the stored checksum 0x{stored:X4} does not match the independently computed 0x{sum:X4}");
        Assert(condition: payload.SequenceEqual(other: expectedMirror), message: "the persisted payload does not match the in-game mirror");

        using var driver = new Driver(rom: rom, externalRam: sram);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateTitle), message: "a machine restored from the save did not boot to the title");
        Assert(condition: ReadSavePayload(driver: driver).AsSpan().SequenceEqual(other: expectedMirror), message: "a fresh machine did not load the persisted payload");
    }

    // (11) Corruption recovery: one flipped payload byte must fail the ROM's own checksum and land on the defaults.
    private static void AssertCorruptionRecovery(byte[] rom, byte[] sram) {
        var corrupted = (byte[])sram.Clone();

        corrupted[SaveModule.HeaderByteCount + 5] ^= 0x5A;

        using var driver = new Driver(rom: rom, externalRam: corrupted);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == PokerProtocol.StateTitle), message: "a machine with a corrupt save did not boot cleanly to the title");
        Assert(condition: ReadSavePayload(driver: driver).AsSpan().SequenceEqual(other: PokerTables.BuildDefaultSavePayload()), message: "a corrupt save did not fall back to the ROM's default payload");
    }

    private static byte[] ReadAllHands(Driver driver) {
        var hands = new byte[PokerProtocol.SeatCount * PokerProtocol.HandSize];

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            ReadHand(driver: driver, seat: seat).CopyTo(array: hands, index: (seat * PokerProtocol.HandSize));
        }

        return hands;
    }

    private static int FindEntry(byte[] mirror, string initials) {
        for (var entry = 0; (entry < PokerProtocol.HiScoreEntryCount); entry++) {
            var matches = true;

            for (var index = 0; (index < 3); index++) {
                if (mirror[(entry * PokerProtocol.HiScoreEntryByteCount) + index] != TextModule.TileFor(fontTileBase: PokerTables.FontTileBase, character: initials[index])) {
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
        var offset = ((entry * PokerProtocol.HiScoreEntryByteCount) + 3);
        var value = 0;

        for (var index = 0; (index < 3); index++) {
            var packed = mirror[offset + index];

            value = ((value * 100) + (((packed >> 4) & 0x0F) * 10) + (packed & 0x0F));
        }

        return value;
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

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "poker");
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
