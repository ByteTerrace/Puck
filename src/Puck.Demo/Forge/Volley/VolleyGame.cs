using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The five-star Volley: the original hand-authored court rules (player paddle vs. a tracking AI, wall bounces,
/// paddle reflection, centre serves) rebuilt on the SM83 game framework as a full seven-state game — title, scripted
/// attract, battery-backed high scores, play (seven hardware sprites: two 8×24 paddle bars and the ball), pause, game
/// over, and initials entry. A match runs to <see cref="VolleyProtocol.MatchPoint"/> points; the persisted score
/// rewards play (+1 per rally return, +100 per point won). Serves draw their direction from the framework PRNG, and
/// PRNG seeding is pure input entropy (D4): the frame counter sampled at the title's START edge.
/// </summary>
internal sealed class VolleyGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;
    private const byte GameOverFrames = 90;
    private const byte LetterCount = 26;
    private const byte LeftPaddleScreenX = 16;   // OAM X = screenX(8) + 8.
    private const byte RightPaddleScreenX = 152; // screenX(144) + 8.
    // The idle threshold before the title/high-score screens fall into attract/back to title: 600 frames (0x0258).
    private const byte IdleThresholdLow = 0x58;
    private const byte IdleThresholdHigh = 0x02;

    private readonly GameFramework m_fw;
    private readonly int m_leftSlot;
    private readonly int m_rightSlot;
    private readonly int m_ballSlot;

    private readonly RomTable m_bgPalettes;
    private readonly RomTable m_objPalettes;
    private readonly RomTable m_tiles;
    private readonly RomTable m_titleMap;
    private readonly RomTable m_playMap;
    private readonly RomTable m_attractScript;
    private readonly RomTable m_strPause;
    private readonly RomTable m_strGameOver;
    private readonly RomTable m_strNewHigh;
    private readonly RomTable m_strHiScores;
    private readonly RomTable? m_titleAttributes;

    private readonly int m_subServe;
    private readonly int m_subGameReset;
    private readonly int m_subDrawSprites;
    private readonly int m_subDrawHud;
    private readonly int m_subHideSprites;
    private readonly int m_subHiInsert;
    private readonly int m_subPlayCore;

    // The game's identity as a declarative manifest: tiles + font + palettes into the linker's bank/slots, the two
    // screens (the title art-backed when the SDF bake installed, the banner otherwise — the menu prompts overlay
    // EITHER map), and the attract script and strings. The linker owns all relocation.
    private static GameManifest BuildManifest(PbakBackground? titleArt) {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "game-tiles", tiles2bpp: VolleyTables.BuildGameTiles());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg-gameplay", paletteData: HgbImage.EncodePalette(palette: VolleyTables.Palette));
        manifest.DefineObjectPalettes(name: "obj-gameplay", paletteData: HgbImage.EncodePalette(palette: VolleyTables.Palette));

        if (titleArt is not null) {
            manifest.DefineArtScreen(name: "title", art: titleArt, overlays: VolleyTables.TitleMenuOverlays);
        }
        else {
            manifest.DefineScreen(name: "title", cells: VolleyTables.BuildTitleBannerCells(), overlays: VolleyTables.TitleMenuOverlays);
        }

        manifest.DefineScreen(name: "play", cells: VolleyTables.BuildPlayCells(), overlays: VolleyTables.PlayHudOverlays);
        manifest.DefineInputScript(name: "attract", steps: VolleyTables.BuildAttractScript());
        manifest.DefineText(name: "str-pause", text: "PAUSE");
        manifest.DefineText(name: "str-game-over", text: "GAME OVER");
        manifest.DefineText(name: "str-new-high", text: "NEW HIGH SCORE");
        manifest.DefineText(name: "str-hi-scores", text: "HI SCORES");
        // The shared sound catalog (deal/flip/shuffle/win + cursor/thud/sweep/over + the loop) rides the manifest
        // like every other table; the ApuSoundDriver binds to the linked streams below.
        SoundTables.DefineIn(manifest: manifest);

        return manifest;
    }

    private VolleyGame() {
        var manifest = BuildManifest(titleArt: VolleyTables.TitleArt);

        // The protocol/verify layer references the font base as a constant; the manifest computes it from the
        // declarations — guard the two against drifting apart.
        if (manifest.FontTileBase != VolleyTables.FontTileBase) {
            throw new InvalidOperationException(message: $"The manifest landed the font at tile {manifest.FontTileBase}, not the pinned {VolleyTables.FontTileBase}.");
        }

        var sound = new ApuSoundDriver();

        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: VolleyTables.BuildDefaultScoreTable(), saveVersion: 1, sound: sound);
        m_leftSlot = m_fw.Oam.Reserve(count: 3);
        m_rightSlot = m_fw.Oam.Reserve(count: 3);
        m_ballSlot = m_fw.Oam.Reserve(count: 1);

        var linked = manifest.Link(framework: m_fw);

        sound.Bind(linked: linked);

        var title = linked.Screen(name: "title");

        m_bgPalettes = linked.BackgroundPalettes;
        m_objPalettes = linked.ObjectPalettes;
        m_tiles = linked.TileBank;
        m_titleMap = title.Map;
        m_playMap = linked.Screen(name: "play").Map;
        m_attractScript = linked.InputScript(name: "attract");
        m_strPause = linked.Text(name: "str-pause");
        m_strGameOver = linked.Text(name: "str-game-over");
        m_strNewHigh = linked.Text(name: "str-new-high");
        m_strHiScores = linked.Text(name: "str-hi-scores");
        m_titleAttributes = title.Attributes;

        var emitter = m_fw.Emitter;

        m_subServe = emitter.NewLabel();
        m_subGameReset = emitter.NewLabel();
        m_subDrawSprites = emitter.NewLabel();
        m_subDrawHud = emitter.NewLabel();
        m_subHideSprites = emitter.NewLabel();
        m_subHiInsert = emitter.NewLabel();
        m_subPlayCore = emitter.NewLabel();

        m_fw.States.DefineState(id: VolleyProtocol.StateTitle, emitEnter: EmitTitleEnter, emitTick: EmitTitleTick);
        m_fw.States.DefineState(id: VolleyProtocol.StateAttract, emitEnter: EmitAttractEnter, emitTick: EmitAttractTick);
        m_fw.States.DefineState(id: VolleyProtocol.StateHighScores, emitEnter: EmitHighScoresEnter, emitTick: EmitHighScoresTick);
        m_fw.States.DefineState(id: VolleyProtocol.StatePlay, emitEnter: EmitPlayEnter, emitTick: EmitPlayTick);
        m_fw.States.DefineState(id: VolleyProtocol.StatePause, emitEnter: EmitPauseEnter, emitTick: EmitPauseTick);
        m_fw.States.DefineState(id: VolleyProtocol.StateGameOver, emitEnter: EmitGameOverEnter, emitTick: EmitGameOverTick);
        m_fw.States.DefineState(id: VolleyProtocol.StateScoreEntry, emitEnter: EmitScoreEntryEnter, emitTick: EmitScoreEntryTick);
    }

    /// <summary>Assembles the cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new VolleyGame();
        var spec = new FrameworkBootSpec(
            BgPalettes: game.m_bgPalettes,
            ObjPalettes: game.m_objPalettes,
            Tiles: game.m_tiles,
            TileByteCount: game.m_tiles.Length,
            InitialMap: game.m_titleMap,
            Lcdc: GameLcdc,
            InitialState: VolleyProtocol.StateTitle
        );

        return game.m_fw.BuildRom(title: title, bootSpec: spec, emitGameLibrary: game.EmitGameLibrary);
    }

    // ==== The game library (shared subroutines). ========================================================================

    private void EmitGameLibrary(Sm83Emitter e) {
        EmitServe(e: e);
        EmitGameReset(e: e);
        EmitDrawSprites(e: e);
        EmitDrawHud(e: e);
        EmitHideSprites(e: e);
        EmitHiInsert(e: e);
        EmitPlayCore(e: e);
    }

    // Re-arm the serve: the delay, the ball pinned at centre, and the serve DIRECTION drawn from the framework PRNG
    // (bit 0 = vertical, bit 1 = horizontal) — deterministic from the seed, never from a wall clock.
    private void EmitServe(Sm83Emitter e) {
        var dyDown = e.NewLabel();
        var dyDone = e.NewLabel();
        var dxRight = e.NewLabel();
        var dxDone = e.NewLabel();

        e.MarkLabel(label: m_subServe);
        e.LoadAImmediate(value: VolleyProtocol.ServeFrames);
        e.StoreAToAddress(address: VolleyProtocol.ServeDelay);
        e.LoadAImmediate(value: VolleyProtocol.CentreX);
        e.StoreAToAddress(address: VolleyProtocol.BallX);
        e.LoadAImmediate(value: VolleyProtocol.CentreY);
        e.StoreAToAddress(address: VolleyProtocol.BallY);

        m_fw.Prng.EmitNext();
        e.Load(destination: Reg8.B, source: Reg8.A);

        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: dyDown);
        e.LoadAImmediate(value: VolleyProtocol.NegativeSpeed);
        e.JumpRelative(label: dyDone);
        e.MarkLabel(label: dyDown);
        e.LoadAImmediate(value: VolleyProtocol.BallSpeed);
        e.MarkLabel(label: dyDone);
        e.StoreAToAddress(address: VolleyProtocol.BallDy);

        e.TestBit(bit: 1, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: dxRight);
        e.LoadAImmediate(value: VolleyProtocol.NegativeSpeed);
        e.JumpRelative(label: dxDone);
        e.MarkLabel(label: dxRight);
        e.LoadAImmediate(value: VolleyProtocol.BallSpeed);
        e.MarkLabel(label: dxDone);
        e.StoreAToAddress(address: VolleyProtocol.BallDx);
        e.Return();
    }

    // A fresh match: zeroed points/score/flag, centred paddles, and an armed serve.
    private void EmitGameReset(Sm83Emitter e) {
        e.MarkLabel(label: m_subGameReset);
        e.XorA();
        e.StoreAToAddress(address: VolleyProtocol.PlayerPoints);
        e.StoreAToAddress(address: VolleyProtocol.AiPoints);
        e.StoreAToAddress(address: VolleyProtocol.Score);
        e.StoreAToAddress(address: (ushort)(VolleyProtocol.Score + 1));
        e.StoreAToAddress(address: (ushort)(VolleyProtocol.Score + 2));
        e.StoreAToAddress(address: VolleyProtocol.MatchOverFlag);
        e.LoadAImmediate(value: VolleyProtocol.PaddleStartY);
        e.StoreAToAddress(address: VolleyProtocol.LeftY);
        e.StoreAToAddress(address: VolleyProtocol.RightY);
        e.Call(label: m_subServe);
        e.Return();
    }

    // Write the seven shadow-OAM sprites (two 3-sprite paddle columns + the ball) from the game state.
    private void EmitDrawSprites(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawSprites);

        for (var segment = 0; (segment < 3); segment++) {
            EmitPaddleSprite(e: e, slot: (m_leftSlot + segment), paddleYAddress: VolleyProtocol.LeftY, screenX: LeftPaddleScreenX, segment: segment);
            EmitPaddleSprite(e: e, slot: (m_rightSlot + segment), paddleYAddress: VolleyProtocol.RightY, screenX: RightPaddleScreenX, segment: segment);
        }

        e.LoadAFromAddress(address: VolleyProtocol.BallY);
        e.ArithmeticImmediate(op: AluOp.Add, value: 16);
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_ballSlot, byteIndex: 0));
        e.LoadAFromAddress(address: VolleyProtocol.BallX);
        e.ArithmeticImmediate(op: AluOp.Add, value: 8);
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_ballSlot, byteIndex: 1));
        e.LoadAImmediate(value: VolleyTables.TileBall);
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_ballSlot, byteIndex: 2));
        e.XorA();
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_ballSlot, byteIndex: 3));
        e.Return();
    }

    private static void EmitPaddleSprite(Sm83Emitter e, int slot, ushort paddleYAddress, byte screenX, int segment) {
        e.LoadAFromAddress(address: paddleYAddress);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(16 + (segment * 8)));
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: slot, byteIndex: 0));
        e.LoadAImmediate(value: screenX);
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: slot, byteIndex: 1));
        e.LoadAImmediate(value: VolleyTables.TilePaddle);
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: slot, byteIndex: 2));
        e.XorA();
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: slot, byteIndex: 3));
    }

    // Queue the HUD: the six-digit rally score and both single-digit match points (eight queue entries a frame).
    private void EmitDrawHud(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawHud);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: VolleyProtocol.Score, byteCount: 3, row: VolleyTables.HudRow, column: VolleyTables.HudScoreColumn);
        e.LoadAFromAddress(address: VolleyProtocol.PlayerPoints);
        e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.DigitTileBase);
        m_fw.Bg.EmitQueueCell(row: VolleyTables.HudRow, column: VolleyTables.HudPlayerPointColumn);
        e.LoadAFromAddress(address: VolleyProtocol.AiPoints);
        e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.DigitTileBase);
        m_fw.Bg.EmitQueueCell(row: VolleyTables.HudRow, column: VolleyTables.HudAiPointColumn);
        e.Return();
    }

    private void EmitHideSprites(Sm83Emitter e) {
        e.MarkLabel(label: m_subHideSprites);
        m_fw.Oam.EmitHideRange(baseSlot: m_leftSlot, count: 7);
        e.Return();
    }

    // Insert the current score + entered initials into the mirror table (sorted, ties keep the older entry), shifting
    // the lower entries down and dropping the last. The caller has already checked the score qualifies.
    private void EmitHiInsert(Sm83Emitter e) {
        var insertGo = e.NewLabel();
        var noShift = e.NewLabel();
        var shiftLoop = e.NewLabel();

        e.MarkLabel(label: m_subHiInsert);

        for (var slot = 0; (slot < (VolleyProtocol.HiScoreEntryCount - 1)); slot++) {
            var take = e.NewLabel();
            var skip = e.NewLabel();
            var entryScore = (ushort)(VolleyProtocol.HiScoreMirror + (slot * VolleyProtocol.HiScoreEntryByteCount) + 3);

            for (var index = 0; (index < 3); index++) {
                e.LoadAFromAddress(address: (ushort)(VolleyProtocol.Score + index));
                e.Load(destination: Reg8.B, source: Reg8.A);
                e.LoadAFromAddress(address: (ushort)(entryScore + index));
                e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
                e.JumpRelative(condition: Condition.Carry, label: take); // entry < score → insert here.

                if (index < 2) {
                    e.JumpRelative(condition: Condition.NotZero, label: skip); // entry > score → try the next slot.
                }
                else {
                    e.JumpRelative(label: skip); // equal or greater on the last byte → not strictly greater.
                }
            }

            e.MarkLabel(label: take);
            e.LoadImmediate(destination: Reg8.C, value: (byte)(slot * VolleyProtocol.HiScoreEntryByteCount));
            e.JumpAbsolute(label: insertGo);
            e.MarkLabel(label: skip);
        }

        // The last slot always accepts (the caller verified score > the table's fifth entry).
        e.LoadImmediate(destination: Reg8.C, value: (byte)((VolleyProtocol.HiScoreEntryCount - 1) * VolleyProtocol.HiScoreEntryByteCount));

        e.MarkLabel(label: insertGo);

        // Shift entries [C/6 .. 3] down one slot, copying backwards; count = 24 - C bytes.
        e.LoadAImmediate(value: (byte)((VolleyProtocol.HiScoreEntryCount - 1) * VolleyProtocol.HiScoreEntryByteCount));
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(condition: Condition.Zero, label: noShift);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadImmediate(pair: Reg16.Hl, value: (ushort)(VolleyProtocol.HiScoreMirror + (4 * VolleyProtocol.HiScoreEntryByteCount) - 1));
        e.LoadImmediate(pair: Reg16.De, value: (ushort)(VolleyProtocol.HiScoreMirror + (5 * VolleyProtocol.HiScoreEntryByteCount) - 1));
        e.MarkLabel(label: shiftLoop);
        e.LoadAFromHlDecrement();
        e.StoreAToDe();
        e.Decrement(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: shiftLoop);
        e.MarkLabel(label: noShift);

        // Write the new entry at mirror + C: three initials (letter index → font tile), then the three score bytes.
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(VolleyProtocol.HiScoreMirror & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(VolleyProtocol.HiScoreMirror >> 8));

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(VolleyProtocol.EntryGlyphs + index));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            e.StoreAToHlIncrement();
        }

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(VolleyProtocol.Score + index));
            e.StoreAToHlIncrement();
        }

        e.Return();
    }

    // The shared per-frame court simulation (Play ticks it directly; Attract ticks it under the input script):
    // held-input paddle motion, the AI chase, the serve delay, ball motion + bounces + paddle reflection (a player
    // return scores +1), miss detection (+100 and a point to the winner, then a fresh serve), and match-point
    // resolution — straight to Title when the attract script is playing (attract never writes SRAM).
    private void EmitPlayCore(Sm83Emitter e) {
        var ballLive = e.NewLabel();
        var draw = e.NewLabel();
        var notLeftMiss = e.NewLabel();
        var notRightMiss = e.NewLabel();
        var noMatchOver = e.NewLabel();
        var realOver = e.NewLabel();
        var done = e.NewLabel();

        e.MarkLabel(label: m_subPlayCore);

        // Player paddle: Up/Down held.
        EmitPaddleInput(e: e);
        EmitClamp(e: e, address: VolleyProtocol.LeftY, minimum: VolleyProtocol.PaddleMinY, maximum: VolleyProtocol.PaddleMaxY);

        // AI paddle: chase the ball's centre.
        EmitAiChase(e: e);
        EmitClamp(e: e, address: VolleyProtocol.RightY, minimum: VolleyProtocol.PaddleMinY, maximum: VolleyProtocol.PaddleMaxY);

        // Serve delay: hold the ball at centre until it expires.
        e.LoadAFromAddress(address: VolleyProtocol.ServeDelay);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: ballLive);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: VolleyProtocol.ServeDelay);
        e.LoadAImmediate(value: VolleyProtocol.CentreX);
        e.StoreAToAddress(address: VolleyProtocol.BallX);
        e.LoadAImmediate(value: VolleyProtocol.CentreY);
        e.StoreAToAddress(address: VolleyProtocol.BallY);
        e.JumpAbsolute(label: draw);

        e.MarkLabel(label: ballLive);

        // Move the ball (signed add of the velocity bytes).
        EmitAddSignedToByte(e: e, address: VolleyProtocol.BallX, deltaAddress: VolleyProtocol.BallDx);
        EmitAddSignedToByte(e: e, address: VolleyProtocol.BallY, deltaAddress: VolleyProtocol.BallDy);

        // Wall bounces.
        EmitBounce(e: e, boundary: VolleyProtocol.BallTopY, boundaryIsTop: true);
        EmitBounce(e: e, boundary: VolleyProtocol.BallBottomY, boundaryIsTop: false);

        // Paddle collisions (a player return earns a rally point).
        EmitPaddleCollision(e: e, movingLeft: true, hitX: VolleyProtocol.LeftHitX, paddleYAddress: VolleyProtocol.LeftY);
        EmitPaddleCollision(e: e, movingLeft: false, hitX: VolleyProtocol.RightHitX, paddleYAddress: VolleyProtocol.RightY);

        // Left miss → the AI takes the point.
        e.LoadAFromAddress(address: VolleyProtocol.BallX);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(VolleyProtocol.LeftMissX + 1));
        e.JumpRelative(condition: Condition.NoCarry, label: notLeftMiss); // BallX > 6 → not a left miss.
        EmitAwardPoint(e: e, pointsAddress: VolleyProtocol.AiPoints, playerWon: false);
        e.JumpAbsolute(label: draw);
        e.MarkLabel(label: notLeftMiss);

        // Right miss → the player takes the point (+100 on the persisted score).
        e.LoadAFromAddress(address: VolleyProtocol.BallX);
        e.ArithmeticImmediate(op: AluOp.Compare, value: VolleyProtocol.RightMissX);
        e.JumpRelative(condition: Condition.Carry, label: notRightMiss); // BallX < 150 → not a right miss.
        EmitAwardPoint(e: e, pointsAddress: VolleyProtocol.PlayerPoints, playerWon: true);
        e.MarkLabel(label: notRightMiss);

        e.MarkLabel(label: draw);
        e.Call(label: m_subDrawSprites);
        e.Call(label: m_subDrawHud);

        // Match-point resolution: a set flag ends the match — back to the title from attract, else the game-over card.
        e.LoadAFromAddress(address: VolleyProtocol.MatchOverFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noMatchOver);
        e.XorA();
        e.StoreAToAddress(address: VolleyProtocol.MatchOverFlag);
        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: VolleyProtocol.StateAttract);
        e.JumpRelative(condition: Condition.NotZero, label: realOver);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateTitle);
        e.JumpRelative(label: done);
        e.MarkLabel(label: realOver);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateGameOver);
        e.JumpRelative(label: done);

        e.MarkLabel(label: noMatchOver);
        e.MarkLabel(label: done);
        e.Return();
    }

    // Up/Down (held, active-high) move the player's paddle top.
    private static void EmitPaddleInput(Sm83Emitter e) {
        var noUp = e.NewLabel();
        var noDown = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputHeld);
        e.Load(destination: Reg8.B, source: Reg8.A);

        e.TestBit(bit: 2, register: Reg8.B); // Up.
        e.JumpRelative(condition: Condition.Zero, label: noUp);
        e.LoadAFromAddress(address: VolleyProtocol.LeftY);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: VolleyProtocol.PaddleSpeed);
        e.StoreAToAddress(address: VolleyProtocol.LeftY);
        e.MarkLabel(label: noUp);

        e.TestBit(bit: 3, register: Reg8.B); // Down.
        e.JumpRelative(condition: Condition.Zero, label: noDown);
        e.LoadAFromAddress(address: VolleyProtocol.LeftY);
        e.ArithmeticImmediate(op: AluOp.Add, value: VolleyProtocol.PaddleSpeed);
        e.StoreAToAddress(address: VolleyProtocol.LeftY);
        e.MarkLabel(label: noDown);
    }

    // Move the AI paddle toward the ball: compare the paddle centre (RightY + 12) with the ball centre (BallY + 4).
    private static void EmitAiChase(Sm83Emitter e) {
        var down = e.NewLabel();
        var done = e.NewLabel();

        e.LoadAFromAddress(address: VolleyProtocol.RightY);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(VolleyProtocol.PaddleHeight / 2));
        e.Load(destination: Reg8.B, source: Reg8.A); // B = paddle centre.
        e.LoadAFromAddress(address: VolleyProtocol.BallY);
        e.ArithmeticImmediate(op: AluOp.Add, value: 4); // A = ball centre.
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B); // Carry when ballCentre < paddleCentre.
        e.JumpRelative(condition: Condition.NoCarry, label: down);
        e.LoadAFromAddress(address: VolleyProtocol.RightY);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: VolleyProtocol.AiSpeed);
        e.StoreAToAddress(address: VolleyProtocol.RightY);
        e.JumpRelative(label: done);
        e.MarkLabel(label: down);
        e.LoadAFromAddress(address: VolleyProtocol.RightY);
        e.ArithmeticImmediate(op: AluOp.Add, value: VolleyProtocol.AiSpeed);
        e.StoreAToAddress(address: VolleyProtocol.RightY);
        e.MarkLabel(label: done);
    }

    // Pin [address] into [minimum, maximum] (the paddle range).
    private static void EmitClamp(Sm83Emitter e, ushort address, byte minimum, byte maximum) {
        var checkMax = e.NewLabel();
        var store = e.NewLabel();

        e.LoadAFromAddress(address: address);
        e.ArithmeticImmediate(op: AluOp.Compare, value: minimum);
        e.JumpRelative(condition: Condition.NoCarry, label: checkMax); // A >= minimum → check the ceiling.
        e.LoadAImmediate(value: minimum);
        e.JumpRelative(label: store);
        e.MarkLabel(label: checkMax);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(maximum + 1));
        e.JumpRelative(condition: Condition.Carry, label: store); // A <= maximum → keep it.
        e.LoadAImmediate(value: maximum);
        e.MarkLabel(label: store);
        e.StoreAToAddress(address: address);
    }

    // [address] += (signed)[deltaAddress].
    private static void EmitAddSignedToByte(Sm83Emitter e, ushort address, ushort deltaAddress) {
        e.LoadAFromAddress(address: deltaAddress);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: address);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.StoreAToAddress(address: address);
    }

    // Reflect BallY off a wall: at the top, force velocity positive; at the bottom, negative; and pin the ball onto the
    // wall so it cannot creep past and wrap the byte.
    private static void EmitBounce(Sm83Emitter e, byte boundary, bool boundaryIsTop) {
        var skip = e.NewLabel();

        e.LoadAFromAddress(address: VolleyProtocol.BallY);

        if (boundaryIsTop) {
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(boundary + 1));
            e.JumpRelative(condition: Condition.NoCarry, label: skip); // BallY > top → no bounce.
            e.LoadAImmediate(value: VolleyProtocol.BallSpeed);
        } else {
            e.ArithmeticImmediate(op: AluOp.Compare, value: boundary);
            e.JumpRelative(condition: Condition.Carry, label: skip); // BallY < bottom → no bounce.
            e.LoadAImmediate(value: VolleyProtocol.NegativeSpeed);
        }

        e.StoreAToAddress(address: VolleyProtocol.BallDy);
        e.LoadAImmediate(value: boundary);
        e.StoreAToAddress(address: VolleyProtocol.BallY);
        e.MarkLabel(label: skip);
    }

    // If the ball is moving toward the given paddle and has reached its x-plane, and overlaps it vertically, reflect
    // the ball's x-velocity and pin it to the paddle face. A PLAYER return (the left paddle) banks a rally point.
    private void EmitPaddleCollision(Sm83Emitter e, bool movingLeft, byte hitX, ushort paddleYAddress) {
        var done = e.NewLabel();

        // Direction gate (bit 7 of the x-velocity = moving left).
        e.LoadAFromAddress(address: VolleyProtocol.BallDx);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x80);

        if (movingLeft) {
            e.JumpRelative(condition: Condition.Zero, label: done); // Moving right → skip.
        } else {
            e.JumpRelative(condition: Condition.NotZero, label: done); // Moving left → skip.
        }

        // X-plane gate.
        e.LoadAFromAddress(address: VolleyProtocol.BallX);

        if (movingLeft) {
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(hitX + 1));
            e.JumpRelative(condition: Condition.NoCarry, label: done); // BallX > hitX → not yet.
        } else {
            e.ArithmeticImmediate(op: AluOp.Compare, value: hitX);
            e.JumpRelative(condition: Condition.Carry, label: done); // BallX < hitX → not yet.
        }

        // Vertical overlap: paddleY < ballBottom (BallY + 8) AND ballY < paddleY + height.
        e.LoadAFromAddress(address: VolleyProtocol.BallY);
        e.ArithmeticImmediate(op: AluOp.Add, value: 8);
        e.Load(destination: Reg8.B, source: Reg8.A); // B = ball bottom.
        e.LoadAFromAddress(address: paddleYAddress);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B); // Carry when paddleY < ballBottom.
        e.JumpRelative(condition: Condition.NoCarry, label: done); // Paddle entirely below the ball → miss.

        e.LoadAFromAddress(address: paddleYAddress);
        e.ArithmeticImmediate(op: AluOp.Add, value: VolleyProtocol.PaddleHeight);
        e.Load(destination: Reg8.B, source: Reg8.A); // B = paddle bottom.
        e.LoadAFromAddress(address: VolleyProtocol.BallY);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B); // Carry when ballY < paddleBottom.
        e.JumpRelative(condition: Condition.NoCarry, label: done); // Ball entirely below the paddle → miss.

        // Bounce.
        e.LoadAImmediate(value: (movingLeft ? VolleyProtocol.BallSpeed : VolleyProtocol.NegativeSpeed));
        e.StoreAToAddress(address: VolleyProtocol.BallDx);
        e.LoadAImmediate(value: hitX);
        e.StoreAToAddress(address: VolleyProtocol.BallX);

        if (movingLeft) {
            EmitScoreAdd(e: e, units: 1, hundreds: 0); // The player's return: +1 rally point.
            m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        }

        e.MarkLabel(label: done);
    }

    // One side takes a point: bump its point counter, award +100 to the persisted score on a PLAYER point, re-serve,
    // and raise the match-over flag at match point.
    private void EmitAwardPoint(Sm83Emitter e, ushort pointsAddress, bool playerWon) {
        var noMatch = e.NewLabel();

        e.LoadAFromAddress(address: pointsAddress);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: pointsAddress);

        if (playerWon) {
            EmitScoreAdd(e: e, units: 0, hundreds: 1);
        }

        e.Call(label: m_subServe);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectThud);
        e.LoadAFromAddress(address: pointsAddress);
        e.ArithmeticImmediate(op: AluOp.Compare, value: VolleyProtocol.MatchPoint);
        e.JumpRelative(condition: Condition.Carry, label: noMatch);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: VolleyProtocol.MatchOverFlag);
        e.MarkLabel(label: noMatch);
    }

    // Add a small BCD amount to the three-byte score: `units` to the least significant byte and/or `hundreds` to the
    // middle byte, carry-chained with DAA up to the most significant byte.
    private static void EmitScoreAdd(Sm83Emitter e, byte units, byte hundreds) {
        e.LoadAFromAddress(address: (ushort)(VolleyProtocol.Score + 2));
        e.ArithmeticImmediate(op: AluOp.Add, value: units);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(VolleyProtocol.Score + 2));
        e.LoadAFromAddress(address: (ushort)(VolleyProtocol.Score + 1));
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: hundreds);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(VolleyProtocol.Score + 1));
        e.LoadAFromAddress(address: VolleyProtocol.Score);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: VolleyProtocol.Score);
    }

    // ==== The seven states. ==============================================================================================

    private void EmitTitleEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        // The title is always quiet: an idempotent stop keeps that an invariant whichever state fell back here.
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        m_fw.Input.EmitScriptStop();
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_titleMap.Address);
        EmitTitleAttributesPaint(e: e);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: VolleyProtocol.IdleTimer);
        e.StoreAToAddress(address: VolleyProtocol.IdleTimerHigh);
    }

    private void EmitTitleTick(Sm83Emitter e) {
        var noStart = e.NewLabel();
        var noSelect = e.NewLabel();
        var stay = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noStart);

        // START edge: the D4 input-entropy seed — the frame counter is sampled at THIS press — then a fresh match,
        // with the start chirp and the short loop kicking in as play begins.
        m_fw.Prng.EmitSeedFromFrameCounter();
        e.Call(label: m_subGameReset);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectFlip);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicLoop);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StatePlay);
        e.Return();

        e.MarkLabel(label: noStart);
        e.TestBit(bit: 6, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noSelect);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateHighScores);
        e.Return();

        e.MarkLabel(label: noSelect);
        EmitIdleAdvance(e: e, stayLabel: stay);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateAttract);
        e.MarkLabel(label: stay);
    }

    private void EmitAttractEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        m_fw.Input.EmitScriptStart(script: m_attractScript);
        e.Call(label: m_subGameReset);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitTitleAttributesReset(e: e);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }

    private void EmitAttractTick(Sm83Emitter e) {
        var noReal = e.NewLabel();
        var running = e.NewLabel();

        // Any REAL press hands the machine back to the title.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputRaw);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noReal);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: noReal);
        e.LoadAFromAddress(address: FrameworkMemoryMap.ScriptEnded);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: running);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: running);
        e.Call(label: m_subPlayCore);
    }

    private void EmitHighScoresEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        EmitTitleAttributesReset(e: e);
        m_fw.Text.EmitPrintDirect(text: m_strHiScores, row: 2, column: 5);

        for (var entry = 0; (entry < VolleyProtocol.HiScoreEntryCount); entry++) {
            var row = (5 + (entry * 2));
            var entryBase = (ushort)(VolleyProtocol.HiScoreMirror + (entry * VolleyProtocol.HiScoreEntryByteCount));

            for (var glyph = 0; (glyph < 3); glyph++) {
                e.LoadAFromAddress(address: (ushort)(entryBase + glyph));
                e.StoreAToAddress(address: Hw.MapCell(row: row, column: (4 + glyph)));
            }

            m_fw.Text.EmitPrintBcdDirect(bcdAddress: (ushort)(entryBase + 3), byteCount: 3, row: row, column: 9);
        }

        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: VolleyProtocol.IdleTimer);
        e.StoreAToAddress(address: VolleyProtocol.IdleTimerHigh);
    }

    private void EmitHighScoresTick(Sm83Emitter e) {
        var back = e.NewLabel();
        var stay = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: back);
        e.TestBit(bit: 5, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: back);
        EmitIdleAdvance(e: e, stayLabel: stay);
        e.MarkLabel(label: back);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateTitle);
        e.MarkLabel(label: stay);
    }

    private void EmitPlayEnter(Sm83Emitter e) {
        // Repaint the play screen FROM STATE (never resetting it) so resuming from pause redraws cleanly; the match
        // reset itself happens on the title's START edge / the attract enter.
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitTitleAttributesReset(e: e);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }

    private void EmitPlayTick(Sm83Emitter e) {
        var pause = e.NewLabel();
        var noPause = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: pause);
        e.TestBit(bit: 6, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noPause);
        e.MarkLabel(label: pause);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StatePause);
        e.Return();
        e.MarkLabel(label: noPause);
        e.Call(label: m_subPlayCore);
    }

    private void EmitPauseEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        m_fw.Text.EmitPrintQueued(text: m_strPause, row: 8, column: 7);
    }

    private void EmitPauseTick(Sm83Emitter e) {
        var resume = e.NewLabel();
        var stay = e.NewLabel();

        // The simulation halts BY CONSTRUCTION here: this tick only polls for the unpause edge.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resume);
        e.TestBit(bit: 6, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: stay);
        e.MarkLabel(label: resume);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StatePlay);
        e.MarkLabel(label: stay);
    }

    private void EmitGameOverEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        m_fw.Text.EmitPrintQueued(text: m_strGameOver, row: 8, column: 5);
        e.LoadAImmediate(value: GameOverFrames);
        e.StoreAToAddress(address: VolleyProtocol.GameOverTimer);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectOver);
    }

    private void EmitGameOverTick(Sm83Emitter e) {
        var resolve = e.NewLabel();
        var qualify = e.NewLabel();
        var noQualify = e.NewLabel();
        var entry4Score = (ushort)(VolleyProtocol.HiScoreMirror + ((VolleyProtocol.HiScoreEntryCount - 1) * VolleyProtocol.HiScoreEntryByteCount) + 3);

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.LoadAFromAddress(address: VolleyProtocol.GameOverTimer);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: VolleyProtocol.GameOverTimer);
        e.JumpRelative(condition: Condition.Zero, label: resolve);
        e.Return();

        // Resolve: a score STRICTLY greater than the table's fifth entry earns initials entry; anything else → title.
        // The three-byte BCD compare works most-significant first: entry < score at any significance qualifies,
        // entry > score bails, equal bytes defer to the next.
        e.MarkLabel(label: resolve);

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(VolleyProtocol.Score + index));
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: (ushort)(entry4Score + index));
            e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            e.JumpRelative(condition: Condition.Carry, label: qualify);

            if (index < 2) {
                e.JumpRelative(condition: Condition.NotZero, label: noQualify);
            }
        }

        e.MarkLabel(label: noQualify);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: qualify);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateScoreEntry);
    }

    private void EmitScoreEntryEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        e.XorA();
        e.StoreAToAddress(address: VolleyProtocol.EntryCursor);
        e.StoreAToAddress(address: VolleyProtocol.EntryGlyphs);
        e.StoreAToAddress(address: (ushort)(VolleyProtocol.EntryGlyphs + 1));
        e.StoreAToAddress(address: (ushort)(VolleyProtocol.EntryGlyphs + 2));
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        EmitTitleAttributesReset(e: e);
        m_fw.Text.EmitPrintDirect(text: m_strNewHigh, row: 4, column: 3);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: VolleyProtocol.Score, byteCount: 3, row: 6, column: 7);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }

    private void EmitScoreEntryTick(Sm83Emitter e) {
        var confirm = e.NewLabel();
        var noUp = e.NewLabel();
        var noDown = e.NewLabel();
        var noLeft = e.NewLabel();
        var noRight = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpAbsolute(condition: Condition.NotZero, label: confirm);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpAbsolute(condition: Condition.NotZero, label: confirm);

        // Up: the active initial cycles A → Z with wrap.
        var upStore = e.NewLabel();

        e.TestBit(bit: 2, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noUp);
        EmitEntryGlyphPointer(e: e);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: LetterCount);
        e.JumpRelative(condition: Condition.Carry, label: upStore);
        e.XorA(); // Past Z → wrap to A.
        e.MarkLabel(label: upStore);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.MarkLabel(label: noUp);

        // Down: cycle the other way (A wraps to Z).
        var downDecrement = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 3, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noDown);
        EmitEntryGlyphPointer(e: e);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: downDecrement);
        e.LoadAImmediate(value: LetterCount);
        e.MarkLabel(label: downDecrement);
        e.Decrement(register: Reg8.A);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.MarkLabel(label: noDown);

        // Left/Right: move the cursor among the three slots with wrap.
        var leftDecrement = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 1, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noLeft);
        e.LoadAFromAddress(address: VolleyProtocol.EntryCursor);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: leftDecrement);
        e.LoadAImmediate(value: 3);
        e.MarkLabel(label: leftDecrement);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: VolleyProtocol.EntryCursor);
        e.MarkLabel(label: noLeft);

        var rightStore = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        e.LoadAFromAddress(address: VolleyProtocol.EntryCursor);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.Carry, label: rightStore);
        e.XorA(); // Past the last slot → wrap to the first.
        e.MarkLabel(label: rightStore);
        e.StoreAToAddress(address: VolleyProtocol.EntryCursor);
        e.MarkLabel(label: noRight);

        // Draw the three initials and the cursor markers through the queue (six entries a frame).
        for (var slot = 0; (slot < 3); slot++) {
            e.LoadAFromAddress(address: (ushort)(VolleyProtocol.EntryGlyphs + slot));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            m_fw.Bg.EmitQueueCell(row: 10, column: (8 + slot));
        }

        for (var slot = 0; (slot < 3); slot++) {
            var isCursor = e.NewLabel();
            var push = e.NewLabel();

            e.LoadAFromAddress(address: VolleyProtocol.EntryCursor);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)slot);
            e.JumpRelative(condition: Condition.Zero, label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: ' '));
            e.JumpRelative(label: push);
            e.MarkLabel(label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: '>'));
            e.MarkLabel(label: push);
            m_fw.Bg.EmitQueueCell(row: 12, column: (8 + slot));
        }

        e.Return();

        // Confirm (START or A): insert into the mirror, persist, and show the table.
        e.MarkLabel(label: confirm);
        e.Call(label: m_subHiInsert);
        m_fw.Save.EmitStore();
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectWin);
        m_fw.States.EmitRequestState(id: VolleyProtocol.StateHighScores);
    }

    // ==== Small emission helpers. ========================================================================================

    // HL := EntryGlyphs + EntryCursor (all three glyph bytes share one page).
    private static void EmitEntryGlyphPointer(Sm83Emitter e) {
        e.LoadAFromAddress(address: VolleyProtocol.EntryCursor);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(VolleyProtocol.EntryGlyphs & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(VolleyProtocol.EntryGlyphs >> 8));
    }

    // The shared idle counter: += 1; control FALLS THROUGH when it reaches exactly 600, and jumps to the caller's
    // stay label otherwise (the enters reset it, so the equality fires exactly once).
    private static void EmitIdleAdvance(Sm83Emitter e, int stayLabel) {
        e.LoadAFromAddress(address: VolleyProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.StoreAToAddress(address: VolleyProtocol.IdleTimer);
        e.LoadAFromAddress(address: VolleyProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.StoreAToAddress(address: VolleyProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdHigh);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
        e.LoadAFromAddress(address: VolleyProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdLow);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
    }

    // Emit the title-art attribute paint (VRAM bank 1) inside an LCD-off window, when custom attributes exist.
    private void EmitTitleAttributesPaint(Sm83Emitter e) {
        if (m_titleAttributes is not { } attributes) {
            return;
        }

        e.LoadAImmediate(value: 0x01);
        e.StoreAToHighPage(port: Hw.PortVramBank);
        FrameworkKernel.EmitBlockCopy(emitter: e, sourceAddress: attributes.Address, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x0400);
        e.XorA();
        e.StoreAToHighPage(port: Hw.PortVramBank);
    }

    // Reset the attribute bank to palette 0 on non-title screens — only needed once custom title attributes exist.
    private void EmitTitleAttributesReset(Sm83Emitter e) {
        if (m_titleAttributes is null) {
            return;
        }

        e.LoadAImmediate(value: 0x01);
        e.StoreAToHighPage(port: Hw.PortVramBank);
        FrameworkKernel.EmitBlockFill(emitter: e, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x0400, value: 0x00);
        e.XorA();
        e.StoreAToHighPage(port: Hw.PortVramBank);
    }
}
