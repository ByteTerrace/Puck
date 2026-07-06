using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The five-star Chroma: the original hand-authored colour-match rules (three colours dripping into a 6×12 well,
/// cursor swap-with-below, three-plus runs igniting and cascading under gravity, top-out) rebuilt on the SM83 game
/// framework as a full seven-state game — title, scripted attract, battery-backed high scores, play, pause, game over,
/// and initials entry. The cursor is one hardware sprite; the well repaints through a per-frame GRID-vs-SCREEN diff
/// over the background write queue, so cascades resolve without an LCD-off flash. Drip columns and colours draw from
/// the framework PRNG, and PRNG seeding is pure input entropy (D4): the frame counter sampled at the title's START
/// edge — the same press frame replays the same seeded well.
/// </summary>
internal sealed class ChromaGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;
    private const byte GameOverFrames = 90;
    private const byte LetterCount = 26;
    private const byte ResolveGuardLimit = 40;
    private const byte DiffPushBudget = 16;
    private const ushort MapWellBase = (ushort)(Hw.VramBackgroundMap + (ChromaProtocol.WellScreenRow * 32) + ChromaProtocol.WellScreenColumn);
    // The idle threshold before the title/high-score screens fall into attract/back to title: 600 frames (0x0258).
    private const byte IdleThresholdLow = 0x58;
    private const byte IdleThresholdHigh = 0x02;

    private readonly GameFramework m_fw;
    private readonly int m_cursorSlot;

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

    private readonly int m_subGridPtr;
    private readonly int m_subMapAddr;
    private readonly int m_subScan;
    private readonly int m_subApply;
    private readonly int m_subGravity;
    private readonly int m_subResolve;
    private readonly int m_subSpawn;
    private readonly int m_subSwap;
    private readonly int m_subDrawCursor;
    private readonly int m_subDrawHud;
    private readonly int m_subDiffPush;
    private readonly int m_subWellPaint;
    private readonly int m_subGameReset;
    private readonly int m_subHideSprites;
    private readonly int m_subHiInsert;
    private readonly int m_subPlayCore;

    // The game's identity as a declarative manifest: tiles + font + palettes into the linker's bank/slots, the two
    // screens (the title art-backed when the SDF bake installed, the banner otherwise — the menu prompts overlay
    // EITHER map), and the attract script and strings. The linker owns all relocation.
    private static GameManifest BuildManifest(PbakBackground? titleArt) {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "game-tiles", tiles2bpp: ChromaTables.BuildGameTiles());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg-gameplay", paletteData: HgbImage.EncodePalette(palette: ChromaTables.Palette));
        manifest.DefineObjectPalettes(name: "obj-gameplay", paletteData: HgbImage.EncodePalette(palette: ChromaTables.ObjectPalette));

        if (titleArt is not null) {
            manifest.DefineArtScreen(name: "title", art: titleArt, overlays: ChromaTables.TitleMenuOverlays);
        }
        else {
            manifest.DefineScreen(name: "title", cells: ChromaTables.BuildTitleBannerCells(), overlays: ChromaTables.TitleMenuOverlays);
        }

        manifest.DefineScreen(name: "play", cells: ChromaTables.BuildPlayCells(), overlays: ChromaTables.PlayHudOverlays);
        manifest.DefineInputScript(name: "attract", steps: ChromaTables.BuildAttractScript());
        manifest.DefineText(name: "str-pause", text: "PAUSE");
        manifest.DefineText(name: "str-game-over", text: "GAME OVER");
        manifest.DefineText(name: "str-new-high", text: "NEW HIGH SCORE");
        manifest.DefineText(name: "str-hi-scores", text: "HI SCORES");
        // The shared sound catalog (deal/flip/shuffle/win + cursor/thud/sweep/over + the loop) rides the manifest
        // like every other table; the ApuSoundDriver binds to the linked streams below.
        SoundTables.DefineIn(manifest: manifest);

        return manifest;
    }

    private ChromaGame() {
        var manifest = BuildManifest(titleArt: ChromaTables.TitleArt);

        // The protocol/verify layer references the font base as a constant; the manifest computes it from the
        // declarations — guard the two against drifting apart.
        if (manifest.FontTileBase != ChromaTables.FontTileBase) {
            throw new InvalidOperationException(message: $"The manifest landed the font at tile {manifest.FontTileBase}, not the pinned {ChromaTables.FontTileBase}.");
        }

        var sound = new ApuSoundDriver();

        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: ChromaTables.BuildDefaultScoreTable(), saveVersion: 1, sound: sound);
        m_cursorSlot = m_fw.Oam.Reserve(count: 1);

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

        m_subGridPtr = emitter.NewLabel();
        m_subMapAddr = emitter.NewLabel();
        m_subScan = emitter.NewLabel();
        m_subApply = emitter.NewLabel();
        m_subGravity = emitter.NewLabel();
        m_subResolve = emitter.NewLabel();
        m_subSpawn = emitter.NewLabel();
        m_subSwap = emitter.NewLabel();
        m_subDrawCursor = emitter.NewLabel();
        m_subDrawHud = emitter.NewLabel();
        m_subDiffPush = emitter.NewLabel();
        m_subWellPaint = emitter.NewLabel();
        m_subGameReset = emitter.NewLabel();
        m_subHideSprites = emitter.NewLabel();
        m_subHiInsert = emitter.NewLabel();
        m_subPlayCore = emitter.NewLabel();

        m_fw.States.DefineState(id: ChromaProtocol.StateTitle, emitEnter: EmitTitleEnter, emitTick: EmitTitleTick);
        m_fw.States.DefineState(id: ChromaProtocol.StateAttract, emitEnter: EmitAttractEnter, emitTick: EmitAttractTick);
        m_fw.States.DefineState(id: ChromaProtocol.StateHighScores, emitEnter: EmitHighScoresEnter, emitTick: EmitHighScoresTick);
        m_fw.States.DefineState(id: ChromaProtocol.StatePlay, emitEnter: EmitPlayEnter, emitTick: EmitPlayTick);
        m_fw.States.DefineState(id: ChromaProtocol.StatePause, emitEnter: EmitPauseEnter, emitTick: EmitPauseTick);
        m_fw.States.DefineState(id: ChromaProtocol.StateGameOver, emitEnter: EmitGameOverEnter, emitTick: EmitGameOverTick);
        m_fw.States.DefineState(id: ChromaProtocol.StateScoreEntry, emitEnter: EmitScoreEntryEnter, emitTick: EmitScoreEntryTick);
    }

    /// <summary>Assembles the cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new ChromaGame();
        var spec = new FrameworkBootSpec(
            BgPalettes: game.m_bgPalettes,
            ObjPalettes: game.m_objPalettes,
            Tiles: game.m_tiles,
            TileByteCount: game.m_tiles.Length,
            InitialMap: game.m_titleMap,
            Lcdc: GameLcdc,
            InitialState: ChromaProtocol.StateTitle
        );

        return game.m_fw.BuildRom(title: title, bootSpec: spec, emitGameLibrary: game.EmitGameLibrary);
    }

    // ==== The game library (shared subroutines). ========================================================================

    private void EmitGameLibrary(Sm83Emitter e) {
        EmitGridPtr(e: e);
        EmitMapAddr(e: e);
        EmitScan(e: e);
        EmitApply(e: e);
        EmitGravity(e: e);
        EmitResolve(e: e);
        EmitSpawn(e: e);
        EmitSwap(e: e);
        EmitDrawCursor(e: e);
        EmitDrawHud(e: e);
        EmitDiffPush(e: e);
        EmitWellPaint(e: e);
        EmitGameReset(e: e);
        EmitHideSprites(e: e);
        EmitHiInsert(e: e);
        EmitPlayCore(e: e);
    }

    // HL := GridBase + Rr*6 + Cc; also writes the linear index to Idx. Clobbers A, D, E.
    private void EmitGridPtr(Sm83Emitter e) {
        e.MarkLabel(label: m_subGridPtr);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // row×2
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // row×4
        e.Arithmetic(op: AluOp.Add, source: Reg8.D);                // row×6
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.LoadAFromAddress(address: ChromaProtocol.Cc);
        e.Arithmetic(op: AluOp.Add, source: Reg8.D);                // The linear index.
        e.StoreAToAddress(address: ChromaProtocol.Idx);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: ChromaProtocol.GridBase);
        e.AddToHl(pair: Reg16.De);
        e.Return();
    }

    // HL := MapWellBase + Rr*32 + Cc (the background-map cell of a well cell; 16-bit safe). Clobbers A, D, E.
    private void EmitMapAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subMapAddr);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x07);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);                // (row&7) << 4
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // << 5
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.LoadAFromAddress(address: ChromaProtocol.Cc);
        e.Arithmetic(op: AluOp.Add, source: Reg8.D);
        e.Load(destination: Reg8.E, source: Reg8.A);                // E = (row&7)*32 + col.
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Load(destination: Reg8.D, source: Reg8.A);                // D = row >> 3.
        e.LoadImmediate(pair: Reg16.Hl, value: MapWellBase);
        e.AddToHl(pair: Reg16.De);
        e.Return();
    }

    // Mark every cell that is part of a horizontal or vertical run of three-plus same-colour blocks.
    private void EmitScan(Sm83Emitter e) {
        var hRow = e.NewLabel();
        var hCol = e.NewLabel();
        var hNext = e.NewLabel();
        var vCol = e.NewLabel();
        var vRow = e.NewLabel();
        var vNext = e.NewLabel();

        e.MarkLabel(label: m_subScan);
        FrameworkKernel.EmitBlockFill(emitter: e, destinationAddress: ChromaProtocol.MarkBase, byteCount: (ChromaProtocol.Cols * ChromaProtocol.Rows), value: 0x00);

        // Horizontal windows: rows 0..11, cols 0..3.
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.MarkLabel(label: hRow);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.MarkLabel(label: hCol);
        e.Call(label: m_subGridPtr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: hNext);
        e.Load(destination: Reg8.B, source: Reg8.A); // The run colour.
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: hNext);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: hNext);
        EmitMarkRun(e: e, stride: 1);
        e.MarkLabel(label: hNext);
        e.LoadAFromAddress(address: ChromaProtocol.Cc);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(ChromaProtocol.Cols - 2)); // Cols 0..3.
        e.JumpAbsolute(condition: Condition.Carry, label: hCol);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)ChromaProtocol.Rows);
        e.JumpAbsolute(condition: Condition.Carry, label: hRow);

        // Vertical windows: cols 0..5, rows 0..9.
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.MarkLabel(label: vCol);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.MarkLabel(label: vRow);
        e.Call(label: m_subGridPtr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: vNext);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadImmediate(pair: Reg16.De, value: (ushort)ChromaProtocol.Cols);
        e.AddToHl(pair: Reg16.De);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: vNext);
        e.AddToHl(pair: Reg16.De);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: vNext);
        EmitMarkRun(e: e, stride: ChromaProtocol.Cols);
        e.MarkLabel(label: vNext);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(ChromaProtocol.Rows - 2)); // Rows 0..9.
        e.JumpAbsolute(condition: Condition.Carry, label: vRow);
        e.LoadAFromAddress(address: ChromaProtocol.Cc);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)ChromaProtocol.Cols);
        e.JumpAbsolute(condition: Condition.Carry, label: vCol);

        e.Return();
    }

    // Mark the three cells at Idx, Idx+stride, Idx+2*stride.
    private static void EmitMarkRun(Sm83Emitter e, int stride) {
        e.LoadAFromAddress(address: ChromaProtocol.Idx);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: ChromaProtocol.MarkBase);
        e.AddToHl(pair: Reg16.De);
        e.LoadImmediate(destination: Reg8.Memory, value: 1);
        e.LoadImmediate(pair: Reg16.De, value: (ushort)stride);
        e.AddToHl(pair: Reg16.De);
        e.LoadImmediate(destination: Reg8.Memory, value: 1);
        e.AddToHl(pair: Reg16.De);
        e.LoadImmediate(destination: Reg8.Memory, value: 1);
    }

    // Clear every marked cell (AnyMarked := whether any), banking +1 BCD on the score per cleared block.
    private void EmitApply(Sm83Emitter e) {
        var loop = e.NewLabel();
        var noMark = e.NewLabel();

        e.MarkLabel(label: m_subApply);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.AnyMarked);
        e.LoadImmediate(pair: Reg16.Hl, value: ChromaProtocol.MarkBase);
        e.LoadImmediate(pair: Reg16.De, value: ChromaProtocol.GridBase);
        e.LoadImmediate(destination: Reg8.B, value: (byte)(ChromaProtocol.Cols * ChromaProtocol.Rows));

        e.MarkLabel(label: loop);
        e.Load(destination: Reg8.A, source: Reg8.Memory); // mark[i]; HL not advanced yet.
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noMark);
        e.XorA();
        e.StoreAToDe(); // grid[i] = 0.

        // Score += 1 (BCD, carry-chained; absolute A-only ops, so HL/DE/B survive).
        e.LoadAFromAddress(address: (ushort)(ChromaProtocol.Score + 2));
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(ChromaProtocol.Score + 2));
        e.LoadAFromAddress(address: (ushort)(ChromaProtocol.Score + 1));
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(ChromaProtocol.Score + 1));
        e.LoadAFromAddress(address: ChromaProtocol.Score);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: ChromaProtocol.Score);

        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: ChromaProtocol.AnyMarked);
        e.StoreAToAddress(address: ChromaProtocol.ClearedFlag);
        e.MarkLabel(label: noMark);
        e.Increment(pair: Reg16.Hl);
        e.Increment(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: loop);
        e.Return();
    }

    // Settle every column: collect its non-zero cells top-to-bottom, then rewrite the column with empties on top and
    // the collected blocks packed at the bottom.
    private void EmitGravity(Sm83Emitter e) {
        var colLoop = e.NewLabel();
        var gather = e.NewLabel();
        var gatherSkip = e.NewLabel();
        var writeBack = e.NewLabel();
        var placeEmpty = e.NewLabel();
        var placeDone = e.NewLabel();

        e.MarkLabel(label: m_subGravity);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Cc);

        e.MarkLabel(label: colLoop);

        // Gather this column's non-zeros into TempBase, count into Ki.
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Ki);
        e.StoreAToAddress(address: ChromaProtocol.Rr);

        e.MarkLabel(label: gather);
        e.Call(label: m_subGridPtr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: gatherSkip);
        e.Load(destination: Reg8.C, source: Reg8.A); // The colour to stash.
        e.LoadAFromAddress(address: ChromaProtocol.Ki);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: ChromaProtocol.TempBase);
        e.AddToHl(pair: Reg16.De);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.Load(destination: Reg8.Memory, source: Reg8.A); // Temp[Ki] = colour.
        e.LoadAFromAddress(address: ChromaProtocol.Ki);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Ki);
        e.MarkLabel(label: gatherSkip);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)ChromaProtocol.Rows);
        e.JumpAbsolute(condition: Condition.Carry, label: gather);

        // Rewrite the column: row r gets empty while r < Rows-Ki, else Temp[r-(Rows-Ki)].
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.MarkLabel(label: writeBack);
        e.Call(label: m_subGridPtr); // HL = &grid[r,c].
        e.LoadAImmediate(value: (byte)ChromaProtocol.Rows);
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.LoadAFromAddress(address: ChromaProtocol.Ki);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.D);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.E); // The threshold in A.
        e.Load(destination: Reg8.D, source: Reg8.A);      // D = threshold.
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.D);  // Carry when Rr < threshold.
        e.JumpRelative(condition: Condition.Carry, label: placeEmpty);
        // Temp index = Rr - threshold.
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.D);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.Push(pair: StackPair.Hl);
        e.LoadImmediate(pair: Reg16.Hl, value: ChromaProtocol.TempBase);
        e.AddToHl(pair: Reg16.De);
        e.Load(destination: Reg8.A, source: Reg8.Memory); // A = Temp[index].
        e.Pop(pair: StackPair.Hl);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.JumpRelative(label: placeDone);
        e.MarkLabel(label: placeEmpty);
        e.LoadImmediate(destination: Reg8.Memory, value: 0);
        e.MarkLabel(label: placeDone);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)ChromaProtocol.Rows);
        e.JumpAbsolute(condition: Condition.Carry, label: writeBack);

        e.LoadAFromAddress(address: ChromaProtocol.Cc);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)ChromaProtocol.Cols);
        e.JumpAbsolute(condition: Condition.Carry, label: colLoop);
        e.Return();
    }

    // Cascade: settle, find matches, clear — repeat until nothing more clears. A guard caps the iterations (a correct
    // cascade converges in far fewer than a grid's worth of clears; the cap is a hard backstop against a stuck loop).
    private void EmitResolve(Sm83Emitter e) {
        var loop = e.NewLabel();
        var done = e.NewLabel();

        e.MarkLabel(label: m_subResolve);
        e.LoadAImmediate(value: ResolveGuardLimit);
        e.StoreAToAddress(address: ChromaProtocol.ResolveGuard);
        e.MarkLabel(label: loop);
        e.Call(label: m_subGravity);
        e.Call(label: m_subScan);
        e.Call(label: m_subApply);
        e.LoadAFromAddress(address: ChromaProtocol.AnyMarked);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: done);
        e.LoadAFromAddress(address: ChromaProtocol.ResolveGuard);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.ResolveGuard);
        e.JumpRelative(condition: Condition.NotZero, label: loop);
        e.MarkLabel(label: done);
        e.Return();
    }

    // Drip a random colour into the top of a random column (both from the framework PRNG); a full column raises the
    // top-out flag for the play core to resolve.
    private void EmitSpawn(Sm83Emitter e) {
        var alive = e.NewLabel();

        e.MarkLabel(label: m_subSpawn);
        m_fw.Prng.EmitNextInRange(modulus: (byte)ChromaProtocol.Cols);
        e.StoreAToAddress(address: ChromaProtocol.SpawnCol);
        m_fw.Prng.EmitNextInRange(modulus: 3);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1); // Colour 1..3.
        e.StoreAToAddress(address: ChromaProtocol.SpawnColour);

        // The top cell of the column is grid[col] (row 0). Occupied → top-out; else place the colour.
        e.LoadAFromAddress(address: ChromaProtocol.SpawnCol);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: ChromaProtocol.GridBase);
        e.AddToHl(pair: Reg16.De); // HL = &grid[col] (row 0).
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: alive);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: ChromaProtocol.TopOutFlag);
        e.Return();
        e.MarkLabel(label: alive);
        e.LoadAFromAddress(address: ChromaProtocol.SpawnColour);
        e.Load(destination: Reg8.Memory, source: Reg8.A); // grid[col] := colour.
        e.Return();
    }

    // Swap the cursor cell with the one directly below it (if there is one), the Chroma vertical shuffle.
    private void EmitSwap(Sm83Emitter e) {
        var done = e.NewLabel();

        e.MarkLabel(label: m_subSwap);
        e.LoadAFromAddress(address: ChromaProtocol.CursorRow);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(ChromaProtocol.Rows - 1));
        e.JumpRelative(condition: Condition.NoCarry, label: done); // The bottom row → nothing below.

        e.LoadAFromAddress(address: ChromaProtocol.CursorCol);
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.LoadAFromAddress(address: ChromaProtocol.CursorRow);
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.Call(label: m_subGridPtr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Load(destination: Reg8.B, source: Reg8.A);   // B = the upper value.
        e.LoadImmediate(pair: Reg16.De, value: (ushort)ChromaProtocol.Cols);
        e.AddToHl(pair: Reg16.De);                     // HL = the cell below.
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Load(destination: Reg8.C, source: Reg8.A);   // C = the lower value.
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.Load(destination: Reg8.Memory, source: Reg8.A); // Lower := upper.
        // Recompute the upper cell address (HL now points at the lower cell).
        e.LoadAFromAddress(address: ChromaProtocol.CursorCol);
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.LoadAFromAddress(address: ChromaProtocol.CursorRow);
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.Call(label: m_subGridPtr); // HL = the upper cell again.
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.Load(destination: Reg8.Memory, source: Reg8.A); // Upper := lower.
        e.MarkLabel(label: done);
        e.Return();
    }

    // The cursor sprite (one shadow-OAM slot): screen (WellScreenColumn + col, WellScreenRow + row) tiles → pixels
    // with the OAM +8/+16 bias.
    private void EmitDrawCursor(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawCursor);
        e.LoadAFromAddress(address: ChromaProtocol.CursorRow);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // row×8
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)((ChromaProtocol.WellScreenRow * 8) + 16));
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_cursorSlot, byteIndex: 0));
        e.LoadAFromAddress(address: ChromaProtocol.CursorCol);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // col×8
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)((ChromaProtocol.WellScreenColumn * 8) + 8));
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_cursorSlot, byteIndex: 1));
        e.LoadAImmediate(value: ChromaTables.TileCursor);
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_cursorSlot, byteIndex: 2));
        e.XorA();
        e.StoreAToAddress(address: OamManager.SpriteAddress(slot: m_cursorSlot, byteIndex: 3));
        e.Return();
    }

    // Queue the HUD: the six-digit BCD score (six queue entries a frame).
    private void EmitDrawHud(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawHud);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: ChromaProtocol.Score, byteCount: 3, row: ChromaTables.HudScoreRow, column: ChromaTables.HudColumn);
        e.Return();
    }

    // The per-frame repaint: walk all 72 cells comparing the grid against the on-screen shadow, queueing changed
    // cells (and updating the shadow) up to a per-frame push budget — the queue's remaining headroom after the HUD.
    // Any backlog (a big cascade) lands over the next few frames, so the well never needs an LCD-off flash mid-play.
    private void EmitDiffPush(Sm83Emitter e) {
        var rowLoop = e.NewLabel();
        var colLoop = e.NewLabel();
        var next = e.NewLabel();
        var doneAll = e.NewLabel();

        e.MarkLabel(label: m_subDiffPush);
        e.LoadAImmediate(value: DiffPushBudget);
        e.StoreAToAddress(address: ChromaProtocol.DiffBudget);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Rr);

        e.MarkLabel(label: rowLoop);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Cc);

        e.MarkLabel(label: colLoop);
        e.Call(label: m_subGridPtr); // HL = &grid[r,c], Idx set.
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Load(destination: Reg8.C, source: Reg8.A); // C = the grid value.
        e.LoadAFromAddress(address: ChromaProtocol.Idx);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: ChromaProtocol.ScreenBase);
        e.AddToHl(pair: Reg16.De); // HL = &screen[idx].
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.C);
        e.JumpRelative(condition: Condition.Zero, label: next);

        // The cell changed: sync the shadow, queue the map write, spend budget.
        e.Load(destination: Reg8.Memory, source: Reg8.C);
        e.Call(label: m_subMapAddr); // HL = the map cell (clobbers A, D, E; C survives).
        e.Load(destination: Reg8.D, source: Reg8.H);
        e.Load(destination: Reg8.E, source: Reg8.L);
        e.Load(destination: Reg8.A, source: Reg8.C);
        m_fw.Bg.EmitQueuePush();
        e.LoadAFromAddress(address: ChromaProtocol.DiffBudget);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.DiffBudget);
        e.JumpAbsolute(condition: Condition.Zero, label: doneAll);

        e.MarkLabel(label: next);
        e.LoadAFromAddress(address: ChromaProtocol.Cc);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Cc);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)ChromaProtocol.Cols);
        e.JumpAbsolute(condition: Condition.Carry, label: colLoop);
        e.LoadAFromAddress(address: ChromaProtocol.Rr);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.Rr);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)ChromaProtocol.Rows);
        e.JumpAbsolute(condition: Condition.Carry, label: rowLoop);

        e.MarkLabel(label: doneAll);
        e.Return();
    }

    // The LCD-off/enter-time full well paint: copy every grid row straight into the map (a cell's colour IS its tile
    // id) and sync the on-screen shadow, so the diff pass starts from truth.
    private void EmitWellPaint(Sm83Emitter e) {
        e.MarkLabel(label: m_subWellPaint);

        for (var row = 0; (row < ChromaProtocol.Rows); row++) {
            FrameworkKernel.EmitBlockCopy(
                emitter: e,
                sourceAddress: (ushort)(ChromaProtocol.GridBase + (row * ChromaProtocol.Cols)),
                destinationAddress: (ushort)(MapWellBase + (row * 32)),
                byteCount: (ushort)ChromaProtocol.Cols
            );
        }

        FrameworkKernel.EmitBlockCopy(emitter: e, sourceAddress: ChromaProtocol.GridBase, destinationAddress: ChromaProtocol.ScreenBase, byteCount: (ChromaProtocol.Cols * ChromaProtocol.Rows));
        e.Return();
    }

    // A fresh well: cleared grid, centred cursor, twelve seeded drips settled under gravity (a resolve then clears any
    // matches the seeding happened to form), and the score zeroed AFTER the seed resolve so every game starts at zero.
    private void EmitGameReset(Sm83Emitter e) {
        e.MarkLabel(label: m_subGameReset);
        FrameworkKernel.EmitBlockFill(emitter: e, destinationAddress: ChromaProtocol.GridBase, byteCount: (ChromaProtocol.Cols * ChromaProtocol.Rows), value: 0x00);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.DropTimer);
        e.StoreAToAddress(address: ChromaProtocol.TopOutFlag);
        e.LoadAImmediate(value: 2);
        e.StoreAToAddress(address: ChromaProtocol.CursorCol);
        e.LoadAImmediate(value: 9);
        e.StoreAToAddress(address: ChromaProtocol.CursorRow);

        // Seed the well with settled material. Gravity (not a full resolve) runs after each drip so every block
        // reaches the floor and the next one enters a clear row 0; a single resolve at the end clears any matches the
        // seeding happened to form.
        for (var drip = 0; (drip < ChromaProtocol.SeedDrips); drip++) {
            e.Call(label: m_subSpawn);
            e.Call(label: m_subGravity);
        }

        e.Call(label: m_subResolve);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.Score);
        e.StoreAToAddress(address: (ushort)(ChromaProtocol.Score + 1));
        e.StoreAToAddress(address: (ushort)(ChromaProtocol.Score + 2));
        e.StoreAToAddress(address: ChromaProtocol.TopOutFlag);
        e.StoreAToAddress(address: ChromaProtocol.ClearedFlag);
        e.Return();
    }

    private void EmitHideSprites(Sm83Emitter e) {
        e.MarkLabel(label: m_subHideSprites);
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: 1);
        e.Return();
    }

    // Insert the current score + entered initials into the mirror table (sorted, ties keep the older entry), shifting
    // the lower entries down and dropping the last. The caller has already checked the score qualifies.
    private void EmitHiInsert(Sm83Emitter e) {
        var insertGo = e.NewLabel();
        var noShift = e.NewLabel();
        var shiftLoop = e.NewLabel();

        e.MarkLabel(label: m_subHiInsert);

        for (var slot = 0; (slot < (ChromaProtocol.HiScoreEntryCount - 1)); slot++) {
            var take = e.NewLabel();
            var skip = e.NewLabel();
            var entryScore = (ushort)(ChromaProtocol.HiScoreMirror + (slot * ChromaProtocol.HiScoreEntryByteCount) + 3);

            for (var index = 0; (index < 3); index++) {
                e.LoadAFromAddress(address: (ushort)(ChromaProtocol.Score + index));
                e.Load(destination: Reg8.B, source: Reg8.A);
                e.LoadAFromAddress(address: (ushort)(entryScore + index));
                e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
                e.JumpRelative(condition: Condition.Carry, label: take); // entry < score → insert here.

                if (index < 2) {
                    e.JumpRelative(condition: Condition.NotZero, label: skip); // entry > score → try the next slot.
                }
                else {
                    e.JumpRelative(label: skip); // Equal or greater on the last byte → not strictly greater.
                }
            }

            e.MarkLabel(label: take);
            e.LoadImmediate(destination: Reg8.C, value: (byte)(slot * ChromaProtocol.HiScoreEntryByteCount));
            e.JumpAbsolute(label: insertGo);
            e.MarkLabel(label: skip);
        }

        // The last slot always accepts (the caller verified score > the table's fifth entry).
        e.LoadImmediate(destination: Reg8.C, value: (byte)((ChromaProtocol.HiScoreEntryCount - 1) * ChromaProtocol.HiScoreEntryByteCount));

        e.MarkLabel(label: insertGo);

        // Shift entries [C/6 .. 3] down one slot, copying backwards; count = 24 - C bytes.
        e.LoadAImmediate(value: (byte)((ChromaProtocol.HiScoreEntryCount - 1) * ChromaProtocol.HiScoreEntryByteCount));
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(condition: Condition.Zero, label: noShift);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadImmediate(pair: Reg16.Hl, value: (ushort)(ChromaProtocol.HiScoreMirror + (4 * ChromaProtocol.HiScoreEntryByteCount) - 1));
        e.LoadImmediate(pair: Reg16.De, value: (ushort)(ChromaProtocol.HiScoreMirror + (5 * ChromaProtocol.HiScoreEntryByteCount) - 1));
        e.MarkLabel(label: shiftLoop);
        e.LoadAFromHlDecrement();
        e.StoreAToDe();
        e.Decrement(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: shiftLoop);
        e.MarkLabel(label: noShift);

        // Write the new entry at mirror + C: three initials (letter index → font tile), then the three score bytes.
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(ChromaProtocol.HiScoreMirror & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(ChromaProtocol.HiScoreMirror >> 8));

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(ChromaProtocol.EntryGlyphs + index));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            e.StoreAToHlIncrement();
        }

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(ChromaProtocol.Score + index));
            e.StoreAToHlIncrement();
        }

        e.Return();
    }

    // The shared per-frame well simulation (Play ticks it directly; Attract ticks it under the input script):
    // edge-triggered cursor moves + the swap, the drip timer, top-out resolution — straight to Title when the attract
    // script is playing (attract never writes SRAM) — then the cursor sprite, the HUD, and the well diff repaint.
    private void EmitPlayCore(Sm83Emitter e) {
        var noLeft = e.NewLabel();
        var noRight = e.NewLabel();
        var noUp = e.NewLabel();
        var noDown = e.NewLabel();
        var noSwap = e.NewLabel();
        var noSpawn = e.NewLabel();
        var alive = e.NewLabel();
        var realOver = e.NewLabel();
        var afterOut = e.NewLabel();

        e.MarkLabel(label: m_subPlayCore);

        // Cursor moves (edge-triggered), each clamped to the well.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);

        e.TestBit(bit: 1, register: Reg8.B); // Left.
        e.JumpRelative(condition: Condition.Zero, label: noLeft);
        EmitDecClamp(e: e, address: ChromaProtocol.CursorCol, minimum: 0);
        e.MarkLabel(label: noLeft);

        e.TestBit(bit: 0, register: Reg8.B); // Right.
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        EmitIncClamp(e: e, address: ChromaProtocol.CursorCol, maximum: (byte)(ChromaProtocol.Cols - 1));
        e.MarkLabel(label: noRight);

        e.TestBit(bit: 2, register: Reg8.B); // Up.
        e.JumpRelative(condition: Condition.Zero, label: noUp);
        EmitDecClamp(e: e, address: ChromaProtocol.CursorRow, minimum: 0);
        e.MarkLabel(label: noUp);

        e.TestBit(bit: 3, register: Reg8.B); // Down.
        e.JumpRelative(condition: Condition.Zero, label: noDown);
        EmitIncClamp(e: e, address: ChromaProtocol.CursorRow, maximum: (byte)(ChromaProtocol.Rows - 1));
        e.MarkLabel(label: noDown);

        // A (edge): swap the cursor cell with the one below, then resolve.
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noSwap);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.Call(label: m_subSwap);
        e.Call(label: m_subResolve);
        e.MarkLabel(label: noSwap);

        // Drip a new block on the timer, then resolve.
        e.LoadAFromAddress(address: ChromaProtocol.DropTimer);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.DropTimer);
        e.ArithmeticImmediate(op: AluOp.Compare, value: ChromaProtocol.DropInterval);
        e.JumpRelative(condition: Condition.Carry, label: noSpawn);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.DropTimer);
        e.Call(label: m_subSpawn);
        e.Call(label: m_subResolve);
        e.MarkLabel(label: noSpawn);

        // Any cleared cascade this frame rides the rising sweep (the apply pass raised the flag; consume it here).
        var noSweep = e.NewLabel();

        e.LoadAFromAddress(address: ChromaProtocol.ClearedFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noSweep);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.ClearedFlag);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectSweep);
        e.MarkLabel(label: noSweep);

        // Top-out resolution: back to the title from attract, else the game-over card.
        e.LoadAFromAddress(address: ChromaProtocol.TopOutFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: alive);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.TopOutFlag);
        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: ChromaProtocol.StateAttract);
        e.JumpRelative(condition: Condition.NotZero, label: realOver);
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateTitle);
        e.JumpRelative(label: afterOut);
        e.MarkLabel(label: realOver);
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateGameOver);
        e.MarkLabel(label: afterOut);
        e.MarkLabel(label: alive);

        e.Call(label: m_subDrawCursor);
        e.Call(label: m_subDrawHud);
        e.Call(label: m_subDiffPush);
        e.Return();
    }

    private static void EmitDecClamp(Sm83Emitter e, ushort address, byte minimum) {
        var skip = e.NewLabel();

        e.LoadAFromAddress(address: address);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(minimum + 1));
        e.JumpRelative(condition: Condition.Carry, label: skip); // At/below the minimum → leave.
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: address);
        e.MarkLabel(label: skip);
    }

    private static void EmitIncClamp(Sm83Emitter e, ushort address, byte maximum) {
        var skip = e.NewLabel();

        e.LoadAFromAddress(address: address);
        e.ArithmeticImmediate(op: AluOp.Compare, value: maximum);
        e.JumpRelative(condition: Condition.NoCarry, label: skip); // At/above the maximum → leave.
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: address);
        e.MarkLabel(label: skip);
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
        e.StoreAToAddress(address: ChromaProtocol.IdleTimer);
        e.StoreAToAddress(address: ChromaProtocol.IdleTimerHigh);
    }

    private void EmitTitleTick(Sm83Emitter e) {
        var noStart = e.NewLabel();
        var noSelect = e.NewLabel();
        var stay = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noStart);

        // START edge: the D4 input-entropy seed — the frame counter is sampled at THIS press — then a fresh well,
        // with the start chirp and the short loop kicking in as play begins.
        m_fw.Prng.EmitSeedFromFrameCounter();
        e.Call(label: m_subGameReset);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectFlip);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicLoop);
        m_fw.States.EmitRequestState(id: ChromaProtocol.StatePlay);
        e.Return();

        e.MarkLabel(label: noStart);
        e.TestBit(bit: 6, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noSelect);
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateHighScores);
        e.Return();

        e.MarkLabel(label: noSelect);
        EmitIdleAdvance(e: e, stayLabel: stay);
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateAttract);
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
        e.Call(label: m_subWellPaint);
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
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: noReal);
        e.LoadAFromAddress(address: FrameworkMemoryMap.ScriptEnded);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: running);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateTitle);
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

        for (var entry = 0; (entry < ChromaProtocol.HiScoreEntryCount); entry++) {
            var row = (5 + (entry * 2));
            var entryBase = (ushort)(ChromaProtocol.HiScoreMirror + (entry * ChromaProtocol.HiScoreEntryByteCount));

            for (var glyph = 0; (glyph < 3); glyph++) {
                e.LoadAFromAddress(address: (ushort)(entryBase + glyph));
                e.StoreAToAddress(address: Hw.MapCell(row: row, column: (4 + glyph)));
            }

            m_fw.Text.EmitPrintBcdDirect(bcdAddress: (ushort)(entryBase + 3), byteCount: 3, row: row, column: 9);
        }

        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.IdleTimer);
        e.StoreAToAddress(address: ChromaProtocol.IdleTimerHigh);
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
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateTitle);
        e.MarkLabel(label: stay);
    }

    private void EmitPlayEnter(Sm83Emitter e) {
        // Repaint the play screen FROM STATE (never resetting it) so resuming from pause redraws cleanly; the well
        // reset itself happens on the title's START edge / the attract enter.
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitTitleAttributesReset(e: e);
        e.Call(label: m_subWellPaint);
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
        m_fw.States.EmitRequestState(id: ChromaProtocol.StatePause);
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
        m_fw.States.EmitRequestState(id: ChromaProtocol.StatePlay);
        e.MarkLabel(label: stay);
    }

    private void EmitGameOverEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        m_fw.Text.EmitPrintQueued(text: m_strGameOver, row: 8, column: 5);
        e.LoadAImmediate(value: GameOverFrames);
        e.StoreAToAddress(address: ChromaProtocol.GameOverTimer);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectOver);
    }

    private void EmitGameOverTick(Sm83Emitter e) {
        var resolve = e.NewLabel();
        var qualify = e.NewLabel();
        var noQualify = e.NewLabel();
        var entry4Score = (ushort)(ChromaProtocol.HiScoreMirror + ((ChromaProtocol.HiScoreEntryCount - 1) * ChromaProtocol.HiScoreEntryByteCount) + 3);

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.LoadAFromAddress(address: ChromaProtocol.GameOverTimer);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.GameOverTimer);
        e.JumpRelative(condition: Condition.Zero, label: resolve);
        e.Return();

        // Resolve: a score STRICTLY greater than the table's fifth entry earns initials entry; anything else → title.
        // The three-byte BCD compare works most-significant first: entry < score at any significance qualifies,
        // entry > score bails, equal bytes defer to the next.
        e.MarkLabel(label: resolve);

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(ChromaProtocol.Score + index));
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: (ushort)(entry4Score + index));
            e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            e.JumpRelative(condition: Condition.Carry, label: qualify);

            if (index < 2) {
                e.JumpRelative(condition: Condition.NotZero, label: noQualify);
            }
        }

        e.MarkLabel(label: noQualify);
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: qualify);
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateScoreEntry);
    }

    private void EmitScoreEntryEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        e.XorA();
        e.StoreAToAddress(address: ChromaProtocol.EntryCursor);
        e.StoreAToAddress(address: ChromaProtocol.EntryGlyphs);
        e.StoreAToAddress(address: (ushort)(ChromaProtocol.EntryGlyphs + 1));
        e.StoreAToAddress(address: (ushort)(ChromaProtocol.EntryGlyphs + 2));
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        EmitTitleAttributesReset(e: e);
        m_fw.Text.EmitPrintDirect(text: m_strNewHigh, row: 4, column: 3);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: ChromaProtocol.Score, byteCount: 3, row: 6, column: 7);
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
        e.LoadAFromAddress(address: ChromaProtocol.EntryCursor);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: leftDecrement);
        e.LoadAImmediate(value: 3);
        e.MarkLabel(label: leftDecrement);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: ChromaProtocol.EntryCursor);
        e.MarkLabel(label: noLeft);

        var rightStore = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        e.LoadAFromAddress(address: ChromaProtocol.EntryCursor);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.Carry, label: rightStore);
        e.XorA(); // Past the last slot → wrap to the first.
        e.MarkLabel(label: rightStore);
        e.StoreAToAddress(address: ChromaProtocol.EntryCursor);
        e.MarkLabel(label: noRight);

        // Draw the three initials and the cursor markers through the queue (six entries a frame).
        for (var slot = 0; (slot < 3); slot++) {
            e.LoadAFromAddress(address: (ushort)(ChromaProtocol.EntryGlyphs + slot));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            m_fw.Bg.EmitQueueCell(row: 10, column: (8 + slot));
        }

        for (var slot = 0; (slot < 3); slot++) {
            var isCursor = e.NewLabel();
            var push = e.NewLabel();

            e.LoadAFromAddress(address: ChromaProtocol.EntryCursor);
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
        m_fw.States.EmitRequestState(id: ChromaProtocol.StateHighScores);
    }

    // ==== Small emission helpers. ========================================================================================

    // HL := EntryGlyphs + EntryCursor (all three glyph bytes share one page).
    private static void EmitEntryGlyphPointer(Sm83Emitter e) {
        e.LoadAFromAddress(address: ChromaProtocol.EntryCursor);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(ChromaProtocol.EntryGlyphs & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(ChromaProtocol.EntryGlyphs >> 8));
    }

    // The shared idle counter: += 1; control FALLS THROUGH when it reaches exactly 600, and jumps to the caller's
    // stay label otherwise (the enters reset it, so the equality fires exactly once).
    private static void EmitIdleAdvance(Sm83Emitter e, int stayLabel) {
        e.LoadAFromAddress(address: ChromaProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.StoreAToAddress(address: ChromaProtocol.IdleTimer);
        e.LoadAFromAddress(address: ChromaProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.StoreAToAddress(address: ChromaProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdHigh);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
        e.LoadAFromAddress(address: ChromaProtocol.IdleTimer);
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
