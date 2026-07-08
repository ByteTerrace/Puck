using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The Volley self-verify battery: boots the freshly-forged ROM on REAL Humble machines (pure CPU, the same core the
/// demo's cabinets run) and asserts the game's observable work-RAM behaviour through the whole state graph —
/// boot→title, the idle→attract hand-off, D4 seed entropy AND same-frame replay determinism (three machines), the
/// ported court battery (paddle motion + clamp, ball live + on-court, rally scoring by CHASING the ball, conceding by
/// dodging it), pause freezing the simulation, the match-point → game-over → initials-entry → high-score flow, SRAM
/// persistence round-tripped through an INDEPENDENT C# checksum, top-slot insertion, and corruption recovery to the
/// ROM defaults. Throws on any violation.
/// </summary>
internal static class VolleyVerify {
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
        AssertVictoryShareConverge(rom: rom);

        Console.WriteLine(value: $"volley verify | boot→title | attract in+out | seed entropy + same-frame replay | paddle up/down + clamp | rally chase scores | dodge concedes | pause freeze | match to {VolleyProtocol.MatchPoint} → game over | score {score:D6} | entry BCA → slot {slot} | top-slot insert + shift | sram round-trip (independent sum16) | corruption → defaults | meta-victory share converge + boot reset");
    }

    // (1) Boot: the machine reaches the title state within ~8 frames with the VBlank handler alive and the boot's
    // initial-state request consumed (PendingState back to 0xFF, which zeroed RAM never is).
    private static void AssertBootToTitle(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
    }

    // (2) Attract: 620 idle frames fall into the scripted attract, the script visibly plays (the ball serves and moves,
    // the scripted paddle sweeps), and any REAL press hands control back to the title.
    private static void AssertAttract(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 620);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateAttract), message: $"620 idle frames did not start the attract loop (state {driver.Read(address: FrameworkMemoryMap.GameState)})");

        var ballMoved = false;
        var paddleMoved = false;
        var ballX0 = driver.Read(address: VolleyProtocol.BallX);
        var leftY0 = driver.Read(address: VolleyProtocol.LeftY);

        for (var frame = 0; (frame < 160); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            ballMoved |= (driver.Read(address: VolleyProtocol.BallX) != ballX0);
            paddleMoved |= (driver.Read(address: VolleyProtocol.LeftY) != leftY0);
        }

        Assert(condition: ballMoved, message: "the ball never moved during attract (the scripted play tick is dead)");
        Assert(condition: paddleMoved, message: "the attract script never swept the player paddle");

        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateTitle), message: "a real press during attract did not return to the title");
    }

    // (3) D4 seed entropy: pressing START on different frames yields different PRNG states; pressing on the SAME frame
    // yields the identical state AND the identical serve velocities — replay determinism from pure input entropy.
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
        Assert(condition: ((first.Read(address: VolleyProtocol.BallDx) == replay.Read(address: VolleyProtocol.BallDx)) && (first.Read(address: VolleyProtocol.BallDy) == replay.Read(address: VolleyProtocol.BallDy))), message: "same-frame starts drew different serve directions (replay broken)");
    }

    // (4)-(7): one continuous session — the ported court battery, rally scoring by chasing, the pause freeze, a staged
    // match end into the game-over → initials → high-score flow — returning the persisted SRAM for the round-trip steps.
    private static (byte[] Sram, byte[] ExpectedMirror, int Score, int Slot) AssertGameplayThroughEntry(byte[] rom) {
        using var driver = new Driver(rom: rom);

        StartPlay(driver: driver, idleFrames: 40);

        // Past the serve delay, the ball moves and stays on the court over a long idle run.
        driver.RunFrames(buttons: JoypadButtons.None, frames: (VolleyProtocol.ServeFrames + 8));

        var x0 = driver.Read(address: VolleyProtocol.BallX);
        var y0 = driver.Read(address: VolleyProtocol.BallY);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: ((driver.Read(address: VolleyProtocol.BallX) != x0) || (driver.Read(address: VolleyProtocol.BallY) != y0)), message: "the ball did not move after the serve delay");

        // The paddle responds to Up and Down and clamps to its range.
        var beforeUp = driver.Read(address: VolleyProtocol.LeftY);

        driver.RunFrames(buttons: JoypadButtons.Up, frames: 12);

        var afterUp = driver.Read(address: VolleyProtocol.LeftY);

        Assert(condition: (afterUp < beforeUp), message: $"Up did not raise the paddle ({beforeUp} → {afterUp})");
        driver.RunFrames(buttons: JoypadButtons.Down, frames: 24);
        Assert(condition: (driver.Read(address: VolleyProtocol.LeftY) > afterUp), message: "Down did not lower the paddle");
        driver.RunFrames(buttons: JoypadButtons.Up, frames: 200);
        Assert(condition: (driver.Read(address: VolleyProtocol.LeftY) == VolleyProtocol.PaddleMinY), message: $"the paddle did not clamp to {VolleyProtocol.PaddleMinY} (got {driver.Read(address: VolleyProtocol.LeftY)})");

        // CHASE the ball with the paddle: the ball stays on the court, returns land, and each return banks a rally
        // point on the persisted score.
        var scored = false;

        for (var frame = 0; ((frame < 600) && !scored); frame++) {
            driver.RunFrames(buttons: ChaseButtons(driver: driver), frames: 1);

            var bx = driver.Read(address: VolleyProtocol.BallX);
            var by = driver.Read(address: VolleyProtocol.BallY);

            Assert(condition: ((bx <= VolleyProtocol.RightMissX) && (by >= (VolleyProtocol.BallTopY - 2)) && (by <= (VolleyProtocol.BallBottomY + 2))), message: $"the ball left the court at frame {frame} ({bx},{by})");
            scored = (ReadBcd(driver: driver, address: VolleyProtocol.Score, byteCount: 3) > 0);
        }

        Assert(condition: scored, message: "600 frames of chasing the ball never banked a rally point");

        // (5) Pause: START freezes the ball for 90 frames (the frame counter keeps running), START resumes play.
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StatePause), message: "START did not pause");

        var pausedX = driver.Read(address: VolleyProtocol.BallX);
        var pausedY = driver.Read(address: VolleyProtocol.BallY);
        var framesBefore = driver.ReadWide(address: FrameworkMemoryMap.FrameCounter);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 90);
        Assert(condition: ((driver.Read(address: VolleyProtocol.BallX) == pausedX) && (driver.Read(address: VolleyProtocol.BallY) == pausedY)), message: "the ball kept moving while paused");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) != framesBefore), message: "the frame counter froze during pause (the handler died)");

        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StatePlay), message: "START did not unpause");

        var resumed = false;

        for (var frame = 0; ((frame < 90) && !resumed); frame++) {
            driver.RunFrames(buttons: JoypadButtons.None, frames: 1);
            resumed = ((driver.Read(address: VolleyProtocol.BallX) != pausedX) || (driver.Read(address: VolleyProtocol.BallY) != pausedY));
        }

        Assert(condition: resumed, message: "the ball did not resume after unpause");

        // (6) Conceding: DODGE the ball (steer to the opposite half each frame) — the AI must take a point, and the
        // ball must re-serve from centre with the delay armed.
        var aiBefore = driver.Read(address: VolleyProtocol.AiPoints);
        var conceded = false;

        for (var frame = 0; ((frame < 900) && !conceded); frame++) {
            driver.RunFrames(buttons: DodgeButtons(driver: driver), frames: 1);
            conceded = (driver.Read(address: VolleyProtocol.AiPoints) > aiBefore);
        }

        Assert(condition: conceded, message: "the AI never scored even while the paddle actively dodged");
        Assert(condition: (driver.Read(address: VolleyProtocol.BallX) == VolleyProtocol.CentreX), message: $"the ball did not re-centre after a point (BallX = {driver.Read(address: VolleyProtocol.BallX)})");
        Assert(condition: (driver.Read(address: VolleyProtocol.ServeDelay) != 0), message: "the serve delay was not armed after a point");

        // (7) Match end: stage the AI at match point minus one, keep dodging, and the next concession must resolve to
        // the game-over card, then (score > the zero fifth entry) to initials entry.
        driver.Write(address: VolleyProtocol.AiPoints, value: (byte)(VolleyProtocol.MatchPoint - 1));

        var over = false;

        for (var frame = 0; ((frame < 900) && !over); frame++) {
            driver.RunFrames(buttons: DodgeButtons(driver: driver), frames: 1);
            over = (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateGameOver);
        }

        Assert(condition: over, message: "a staged match point never reached the game-over state");

        var finalScore = new[] { driver.Read(address: VolleyProtocol.Score), driver.Read(address: (ushort)(VolleyProtocol.Score + 1)), driver.Read(address: (ushort)(VolleyProtocol.Score + 2)) };

        Assert(condition: driver.RunUntilState(state: VolleyProtocol.StateScoreEntry, buttons: JoypadButtons.None, maxFrames: 120), message: "a qualifying score never reached initials entry after game over");

        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Right); // Slot 1.
        driver.Press(buttons: JoypadButtons.Up);    // A → B.
        driver.Press(buttons: JoypadButtons.Up);    // B → C.
        driver.Press(buttons: JoypadButtons.Right); // Slot 2 stays A.
        Assert(condition: ((driver.Read(address: VolleyProtocol.EntryGlyphs) == 1) && (driver.Read(address: (ushort)(VolleyProtocol.EntryGlyphs + 1)) == 2) && (driver.Read(address: (ushort)(VolleyProtocol.EntryGlyphs + 2)) == 0)), message: "the initials pad did not spell BCA");

        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateHighScores), message: "confirming the initials did not land on the high-score screen");

        // The mirror now holds the BCA entry with the exact final score, and stays sorted.
        var mirror = ReadMirror(driver: driver);
        var slot = FindEntry(mirror: mirror, initials: "BCA");

        Assert(condition: (slot >= 0), message: "the BCA entry is missing from the high-score mirror");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[(slot * VolleyProtocol.HiScoreEntryByteCount) + 3 + index] == finalScore[index]), message: "the persisted entry's score does not match the game's final score");
        }

        for (var entry = 1; (entry < VolleyProtocol.HiScoreEntryCount); entry++) {
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
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateTitle), message: "a machine restored from the save did not boot to the title");
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: expectedMirror), message: "a fresh machine did not load the persisted high-score table");
    }

    // (7b) Top-slot insertion: a C#-AUTHORED save block with an all-zero score table must pass the ROM's own validation
    // (checksum compatibility in the import direction), and a fresh session's score must then insert at SLOT 0 —
    // exercising the table shift a lower landing never runs — pushing the old leader down.
    private static void AssertTopSlotInsertion(byte[] rom) {
        var payload = VolleyTables.BuildDefaultScoreTable();

        for (var entry = 0; (entry < VolleyProtocol.HiScoreEntryCount); entry++) {
            for (var index = 0; (index < 3); index++) {
                payload[(entry * VolleyProtocol.HiScoreEntryByteCount) + 3 + index] = 0x00;
            }
        }

        using var driver = new Driver(rom: rom, externalRam: BuildSaveBlock(payload: payload));

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: payload), message: "the ROM rejected a C#-authored valid save block (import-direction checksum drift)");

        // A quick staged session: chase until a rally point banks, then concede the match from match point minus one.
        StartPlay(driver: driver, idleFrames: 4);
        driver.RunFrames(buttons: JoypadButtons.None, frames: VolleyProtocol.ServeFrames);

        var scored = false;

        for (var frame = 0; ((frame < 600) && !scored); frame++) {
            driver.RunFrames(buttons: ChaseButtons(driver: driver), frames: 1);
            scored = (ReadBcd(driver: driver, address: VolleyProtocol.Score, byteCount: 3) > 0);
        }

        Assert(condition: scored, message: "the top-slot session never banked a rally point");
        driver.Write(address: VolleyProtocol.AiPoints, value: (byte)(VolleyProtocol.MatchPoint - 1));

        var over = false;

        for (var frame = 0; ((frame < 900) && !over); frame++) {
            driver.RunFrames(buttons: DodgeButtons(driver: driver), frames: 1);
            over = (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateGameOver);
        }

        Assert(condition: over, message: "the top-slot session's staged match point never reached game over");

        var finalScore = new[] { driver.Read(address: VolleyProtocol.Score), driver.Read(address: (ushort)(VolleyProtocol.Score + 1)), driver.Read(address: (ushort)(VolleyProtocol.Score + 2)) };

        Assert(condition: driver.RunUntilState(state: VolleyProtocol.StateScoreEntry, buttons: JoypadButtons.None, maxFrames: 120), message: "the top-slot session never reached initials entry");
        driver.Press(buttons: JoypadButtons.Start); // Confirm the default AAA.
        driver.RunFrames(buttons: JoypadButtons.None, frames: 4);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateHighScores), message: "the top-slot confirm did not land on the high-score screen");

        var mirror = ReadMirror(driver: driver);

        Assert(condition: (FindEntry(mirror: mirror, initials: "AAA") == 0), message: "the beating score did not insert at slot 0");

        for (var index = 0; (index < 3); index++) {
            Assert(condition: (mirror[3 + index] == finalScore[index]), message: "the slot-0 entry's score does not match the game's final score");
            Assert(condition: (mirror[VolleyProtocol.HiScoreEntryByteCount + index] == payload[index]), message: "the old leader's initials did not shift down to slot 1");
            Assert(condition: (mirror[VolleyProtocol.HiScoreEntryByteCount + 3 + index] == 0x00), message: "the old leader's score did not shift down intact");
        }
    }

    // (9) Corruption recovery: one flipped payload byte must fail the ROM's own checksum and land the machine on the
    // ROM defaults — never a partially trusted table.
    private static void AssertCorruptionRecovery(byte[] rom, byte[] sram) {
        var corrupted = (byte[])sram.Clone();

        corrupted[SaveModule.HeaderByteCount + 5] ^= 0x5A;

        using var driver = new Driver(rom: rom, externalRam: corrupted);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateTitle), message: "a machine with a corrupt save did not boot cleanly to the title");
        Assert(condition: ReadMirror(driver: driver).AsSpan().SequenceEqual(other: VolleyTables.BuildDefaultScoreTable()), message: "a corrupt save did not fall back to the ROM's default table");
    }

    // (10) The 128-bit META VICTORY share converge (Stage 2 of the self-editing arcade arc). This is the GAME-SIDE
    // proof of the whole meta gate: the host seeds a cabinet's authored share into the framework's victory-share WRAM
    // slot at boot, and a completed game must copy it verbatim into the TOP-16 SRAM region the room XORs across
    // cabinets. Every leg is run on a real machine:
    //   (a) the seeded WRAM slot SURVIVES the framework's boot work-RAM clear (the split-around-the-slot in
    //       FrameworkKernel.EmitBootPrologue) — the seed poked before the first frame is still present after boot;
    //   (b) the top-16 SRAM win region is CLEAR after boot (the boot reset in VictoryModule.EmitBootReset) and stays
    //       clear right up until the win — so a partial state can never look like a share;
    //   (c) a staged match end (game over) copies the EXACT seeded share into the region — whole-region-on-win;
    //   (d) a FRESH boot from a .sav that already carries a share RE-CLEARS the region (no persistence, re-earn each
    //       session) — the stale-save-cannot-auto-fire guarantee the room depends on.
    private static void AssertVictoryShareConverge(byte[] rom) {
        // A recognizable, structured share (a valid v4 GUID's bytes) so a stray zero/garbage region can't pass by luck.
        var share = new byte[] { 0xA5, 0x3C, 0x10, 0x24, 0x77, 0x91, 0x4B, 0xCD, 0x8E, 0x0F, 0xF0, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E };

        using var driver = new Driver(rom: rom);

        // (a) Seed the share into the framework's victory-share WRAM slot BEFORE the game boots — exactly the host's
        // per-cabinet pre-boot poke — then boot. The slot must survive the boot work-RAM clear.
        for (var index = 0; (index < share.Length); index++) {
            driver.Write(address: (ushort)(FrameworkMemoryMap.VictoryShareSource + index), value: share[index]);
        }

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateTitle), message: "the victory-share machine did not reach the title");

        for (var index = 0; (index < share.Length); index++) {
            Assert(condition: (driver.Read(address: (ushort)(FrameworkMemoryMap.VictoryShareSource + index)) == share[index]), message: "the seeded victory-share WRAM slot was wiped by the boot RAM clear (the split-around-the-slot regressed)");
        }

        // (b) The top-16 SRAM win region is CLEAR after boot and stays clear until the win.
        Assert(condition: IsRegionClear(driver: driver), message: "the top-16 SRAM win region was not clear after boot (the boot reset did not run)");

        // Drive a staged match to game over (the win edge that fires the share write). Stage the AI at match point
        // minus one and dodge, exactly like the gameplay battery above.
        StartPlay(driver: driver, idleFrames: 40);
        driver.RunFrames(buttons: JoypadButtons.None, frames: VolleyProtocol.ServeFrames);

        Assert(condition: IsRegionClear(driver: driver), message: "the win region filled BEFORE the game ended (the write hook fired too early)");

        driver.Write(address: VolleyProtocol.AiPoints, value: (byte)(VolleyProtocol.MatchPoint - 1));

        var over = false;

        for (var frame = 0; ((frame < 900) && !over); frame++) {
            driver.RunFrames(buttons: DodgeButtons(driver: driver), frames: 1);
            over = (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StateGameOver);
        }

        Assert(condition: over, message: "the victory-share session never reached game over");

        // (c) The region now holds the EXACT seeded share — whole-region-on-win.
        var region = driver.ReadVictoryRegion();

        Assert(condition: region.AsSpan().SequenceEqual(other: share), message: "the win region did not converge on the seeded 128-bit share after the game ended");

        // (d) A FRESH boot from the just-won .sav must RE-CLEAR the region: no persistence, re-earn each session. Import
        // the won save into a new machine WITHOUT seeding a share this time — the region must come up clear regardless.
        var wonSram = driver.ExportExternalRam();

        Assert(condition: !wonSram.AsSpan(start: (wonSram.Length - FrameworkMemoryMap.VictoryShareByteCount)).SequenceEqual(other: new byte[FrameworkMemoryMap.VictoryShareByteCount]), message: "the exported save's top-16 did not carry the won share (the store never reached SRAM)");

        using var reboot = new Driver(rom: rom, externalRam: wonSram);

        reboot.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: IsRegionClear(driver: reboot), message: "a fresh boot from a save carrying a share did not re-clear the win region (a stale save could auto-fire the gate)");
    }

    private static bool IsRegionClear(Driver driver) {
        var region = driver.ReadVictoryRegion();

        foreach (var value in region) {
            if (value != 0) {
                return false;
            }
        }

        return true;
    }

    // ==== Helpers. =======================================================================================================

    // Boot a fresh machine and press START after `idleFrames` on the title (the D4 entropy sample point).
    private static void StartPlay(Driver driver, int idleFrames) {
        driver.RunFrames(buttons: JoypadButtons.None, frames: idleFrames);
        driver.Press(buttons: JoypadButtons.Start);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 2);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == VolleyProtocol.StatePlay), message: "START on the title did not start a match");
    }

    // The chase policy: steer the paddle's centre toward the ball's centre — reliable returns, so rally points bank.
    private static JoypadButtons ChaseButtons(Driver driver) {
        var paddleCentre = (driver.Read(address: VolleyProtocol.LeftY) + (VolleyProtocol.PaddleHeight / 2));
        var ballCentre = (driver.Read(address: VolleyProtocol.BallY) + 4);

        return ((ballCentre < paddleCentre) ? JoypadButtons.Up : JoypadButtons.Down);
    }

    // The dodge policy: steer to the OPPOSITE half of the court each frame, read from the live ball position — a
    // guaranteed miss on the left, so the AI must eventually score.
    private static JoypadButtons DodgeButtons(Driver driver) =>
        ((driver.Read(address: VolleyProtocol.BallY) < 72) ? JoypadButtons.Down : JoypadButtons.Up);

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

    private static byte[] ReadMirror(Driver driver) {
        var mirror = new byte[VolleyProtocol.HiScoreEntryCount * VolleyProtocol.HiScoreEntryByteCount];

        for (var index = 0; (index < mirror.Length); index++) {
            mirror[index] = driver.Read(address: (ushort)(VolleyProtocol.HiScoreMirror + index));
        }

        return mirror;
    }

    private static int FindEntry(byte[] mirror, string initials) {
        for (var entry = 0; (entry < VolleyProtocol.HiScoreEntryCount); entry++) {
            var matches = true;

            for (var index = 0; (index < 3); index++) {
                if (mirror[(entry * VolleyProtocol.HiScoreEntryByteCount) + index] != TextModule.TileFor(fontTileBase: VolleyTables.FontTileBase, character: initials[index])) {
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
        var offset = ((entry * VolleyProtocol.HiScoreEntryByteCount) + 3);

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
            throw new InvalidOperationException(message: $"volley ROM verification failed: {message}");
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

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "volley");
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

        // The top-16 SRAM win region, read exactly as the host's meta gate does (ReadExternalRam by absolute offset —
        // bank-independent, side-effect-free): the highest 16 bytes of the cartridge's external RAM.
        public byte[] ReadVictoryRegion() {
            var region = new byte[FrameworkMemoryMap.VictoryShareByteCount];

            m_cartridge.ReadExternalRam(offset: (m_cartridge.ExternalRamByteCount - region.Length), destination: region);

            return region;
        }

        public void Dispose() => m_machine.Dispose();
    }
}
