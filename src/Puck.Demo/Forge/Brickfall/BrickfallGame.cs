using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The five-star Brickfall: the original hand-authored falling-blocks rules (the 224-byte piece table, the LCG piece
/// draw, edge-triggered shifts, rotation, gravity, lock → clear → spawn) rebuilt on the SM83 game framework as a full
/// seven-state game — title, scripted attract, battery-backed high scores, play (hardware-sprite piece + preview,
/// queued HUD), pause, game over, and initials entry. The falling piece and the NEXT preview are eight hardware
/// sprites (under the 10-per-line limit); locked cells go through the background write queue; a line clear repaints
/// the well LCD-off. PRNG seeding is pure input entropy (D4): the frame counter sampled at the title's START edge.
/// </summary>
internal sealed class BrickfallGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;
    private const ushort MapWellBase = (ushort)(Hw.VramBackgroundMap + 1); // Well column 0 → screen column 1, row 0.
    private const byte SpawnColumn = 3;
    private const byte PreviewBaseY = 104; // Screen row 11 → sprite Y (88 + 16).
    private const byte PreviewBaseX = 120; // Screen column 14 → sprite X (112 + 8).
    private const byte GameOverFrames = 90;
    private const byte LetterCount = 26;
    // The idle threshold before the title/high-score screens fall into attract/back to title: 600 frames (0x0258).
    private const byte IdleThresholdLow = 0x58;
    private const byte IdleThresholdHigh = 0x02;

    private readonly GameFramework m_fw;
    private readonly int m_pieceSlot;
    private readonly int m_previewSlot;

    private readonly RomTable m_bgPalettes;
    private readonly RomTable m_objPalettes;
    private readonly RomTable m_tiles;
    private readonly RomTable m_titleMap;
    private readonly RomTable m_playMap;
    private readonly RomTable m_pieceTable;
    private readonly RomTable m_previewTable;
    private readonly RomTable m_speedTable;
    private readonly RomTable m_scoreBases;
    private readonly RomTable m_attractScript;
    private readonly RomTable m_strPause;
    private readonly RomTable m_strGameOver;
    private readonly RomTable m_strNewHigh;
    private readonly RomTable m_strHiScores;
    private readonly RomTable? m_titleAttributes;

    // The game's identity as a declarative manifest: tiles + font + palettes into the linker's bank/slots, the two
    // screens (the title art-backed when the SDF bake installed, the banner otherwise — the menu prompts overlay
    // EITHER map), and every rule table, string, and script. The linker owns all relocation.
    private static GameManifest BuildManifest(PbakBackground? titleArt) {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "game-tiles", tiles2bpp: BrickfallTables.BuildGameTiles());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg-gameplay", paletteData: HgbImage.EncodePalette(palette: BrickfallTables.Palette));
        manifest.DefineObjectPalettes(name: "obj-gameplay", paletteData: HgbImage.EncodePalette(palette: BrickfallTables.Palette));

        if (titleArt is not null) {
            manifest.DefineArtScreen(name: "title", art: titleArt, overlays: BrickfallTables.TitleMenuOverlays);
        }
        else {
            manifest.DefineScreen(name: "title", cells: BrickfallTables.BuildTitleBannerCells(), overlays: BrickfallTables.TitleMenuOverlays);
        }

        manifest.DefineScreen(name: "play", cells: BrickfallTables.BuildPlayCells(), overlays: BrickfallTables.PlayHudOverlays);
        manifest.DefineTable(name: "piece-table", bytes: BrickfallTables.BuildPieceTable());
        manifest.DefineRecords(name: "preview-table", stride: 16, records: BrickfallTables.BuildPreviewRecords());
        manifest.DefineTable(name: "speed-table", bytes: BrickfallTables.BuildSpeedTable());
        manifest.DefineTable(name: "score-bases", bytes: BrickfallTables.BuildScoreBases());
        manifest.DefineInputScript(name: "attract", steps: BrickfallTables.BuildAttractScript());
        manifest.DefineText(name: "str-pause", text: "PAUSE");
        manifest.DefineText(name: "str-game-over", text: "GAME OVER");
        manifest.DefineText(name: "str-new-high", text: "NEW HIGH SCORE");
        manifest.DefineText(name: "str-hi-scores", text: "HI SCORES");
        // The shared sound catalog (deal/flip/shuffle/win + cursor/thud/sweep/over + the loop) rides the manifest
        // like every other table; the ApuSoundDriver binds to the linked streams below. The music loop itself comes
        // from the checked-in puck.audio.v1 document, compiled through AudioDocumentCompiler — never a hand array.
        SoundTables.DefineIn(manifest: manifest, musicLoop: LoadMusicLoop());

        return manifest;
    }

    // The music-as-data workstream: Brickfall's loop is an authored document (docs/examples/tunes/
    // brickfall.audio.json — the tunes/ family subdirectory keeps the run-document contract corpus at the top
    // level), compiled to the exact same pulse-2 stream format SoundTables.BuildMusicLoop used to hand-author.
    private static byte[] LoadMusicLoop() =>
        AudioDocumentCompiler.CompileMusicLoop(document: AudioDocumentStore.Load(path: "docs/examples/tunes/brickfall.audio.json"));

    private readonly int m_subSetTest;
    private readonly int m_subPiecePtr;
    private readonly int m_subWellAddr;
    private readonly int m_subMapAddr;
    private readonly int m_subCollide;
    private readonly int m_subLock;
    private readonly int m_subClear;
    private readonly int m_subShiftDown;
    private readonly int m_subSpawn;
    private readonly int m_subPreviewDraw;
    private readonly int m_subGameReset;
    private readonly int m_subWellBlit;
    private readonly int m_subWellRepaint;
    private readonly int m_subPieceSprites;
    private readonly int m_subHudPrint;
    private readonly int m_subHideSprites;
    private readonly int m_subAwardScore;
    private readonly int m_subHiInsert;
    private readonly int m_subPlayCore;

    private BrickfallGame() {
        var manifest = BuildManifest(titleArt: BrickfallTables.TitleArt);

        // The protocol/verify layer references the font base as a constant; the manifest computes it from the
        // declarations — guard the two against drifting apart.
        if (manifest.FontTileBase != BrickfallTables.FontTileBase) {
            throw new InvalidOperationException(message: $"The manifest landed the font at tile {manifest.FontTileBase}, not the pinned {BrickfallTables.FontTileBase}.");
        }

        var sound = new ApuSoundDriver();

        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: BrickfallTables.BuildDefaultScoreTable(), saveVersion: 1, sound: sound);
        m_pieceSlot = m_fw.Oam.Reserve(count: 4);
        m_previewSlot = m_fw.Oam.Reserve(count: 4);

        var linked = manifest.Link(framework: m_fw);

        sound.Bind(linked: linked);

        var title = linked.Screen(name: "title");

        m_bgPalettes = linked.BackgroundPalettes;
        m_objPalettes = linked.ObjectPalettes;
        m_tiles = linked.TileBank;
        m_titleMap = title.Map;
        m_playMap = linked.Screen(name: "play").Map;
        m_pieceTable = linked.Table(name: "piece-table");
        m_previewTable = linked.Records(name: "preview-table").Table;
        m_speedTable = linked.Table(name: "speed-table");
        m_scoreBases = linked.Table(name: "score-bases");
        m_attractScript = linked.InputScript(name: "attract");
        m_strPause = linked.Text(name: "str-pause");
        m_strGameOver = linked.Text(name: "str-game-over");
        m_strNewHigh = linked.Text(name: "str-new-high");
        m_strHiScores = linked.Text(name: "str-hi-scores");
        m_titleAttributes = title.Attributes;

        var emitter = m_fw.Emitter;

        m_subSetTest = emitter.NewLabel();
        m_subPiecePtr = emitter.NewLabel();
        m_subWellAddr = emitter.NewLabel();
        m_subMapAddr = emitter.NewLabel();
        m_subCollide = emitter.NewLabel();
        m_subLock = emitter.NewLabel();
        m_subClear = emitter.NewLabel();
        m_subShiftDown = emitter.NewLabel();
        m_subSpawn = emitter.NewLabel();
        m_subPreviewDraw = emitter.NewLabel();
        m_subGameReset = emitter.NewLabel();
        m_subWellBlit = emitter.NewLabel();
        m_subWellRepaint = emitter.NewLabel();
        m_subPieceSprites = emitter.NewLabel();
        m_subHudPrint = emitter.NewLabel();
        m_subHideSprites = emitter.NewLabel();
        m_subAwardScore = emitter.NewLabel();
        m_subHiInsert = emitter.NewLabel();
        m_subPlayCore = emitter.NewLabel();

        m_fw.States.DefineState(id: BrickfallProtocol.StateTitle, emitEnter: EmitTitleEnter, emitTick: EmitTitleTick);
        m_fw.States.DefineState(id: BrickfallProtocol.StateAttract, emitEnter: EmitAttractEnter, emitTick: EmitAttractTick);
        m_fw.States.DefineState(id: BrickfallProtocol.StateHighScores, emitEnter: EmitHighScoresEnter, emitTick: EmitHighScoresTick);
        m_fw.States.DefineState(id: BrickfallProtocol.StatePlay, emitEnter: EmitPlayEnter, emitTick: EmitPlayTick);
        m_fw.States.DefineState(id: BrickfallProtocol.StatePause, emitEnter: EmitPauseEnter, emitTick: EmitPauseTick);
        m_fw.States.DefineState(id: BrickfallProtocol.StateGameOver, emitEnter: EmitGameOverEnter, emitTick: EmitGameOverTick);
        m_fw.States.DefineState(id: BrickfallProtocol.StateScoreEntry, emitEnter: EmitScoreEntryEnter, emitTick: EmitScoreEntryTick);
    }

    /// <summary>Assembles the cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new BrickfallGame();
        var spec = new FrameworkBootSpec(
            BgPalettes: game.m_bgPalettes,
            ObjPalettes: game.m_objPalettes,
            Tiles: game.m_tiles,
            TileByteCount: game.m_tiles.Length,
            InitialMap: game.m_titleMap,
            Lcdc: GameLcdc,
            InitialState: BrickfallProtocol.StateTitle
        );

        return game.m_fw.BuildRom(title: title, bootSpec: spec, emitGameLibrary: game.EmitGameLibrary);
    }

    // ==== The game library (shared subroutines). ========================================================================

    private void EmitGameLibrary(Sm83Emitter e) {
        EmitSetTest(e: e);
        EmitPiecePtr(e: e);
        EmitWellAddr(e: e);
        EmitMapAddr(e: e);
        EmitCollide(e: e);
        EmitLock(e: e);
        EmitClear(e: e);
        EmitShiftDown(e: e);
        EmitSpawn(e: e);
        EmitPreviewDraw(e: e);
        EmitGameReset(e: e);
        EmitWellBlit(e: e);
        EmitWellRepaint(e: e);
        EmitPieceSprites(e: e);
        EmitHudPrint(e: e);
        EmitHideSprites(e: e);
        EmitAwardScore(e: e);
        EmitHiInsert(e: e);
        EmitPlayCore(e: e);
    }

    // Test* := the falling piece's current position/rotation (the collision probe's baseline).
    private void EmitSetTest(Sm83Emitter e) {
        e.MarkLabel(label: m_subSetTest);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceX);
        e.StoreAToAddress(address: BrickfallProtocol.TestX);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceY);
        e.StoreAToAddress(address: BrickfallProtocol.TestY);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceRot);
        e.StoreAToAddress(address: BrickfallProtocol.TestRot);
        e.Return();
    }

    // HL := pieceTable + ((PieceType*4 + TestRot) * 8). Clobbers A, C, D, E.
    private void EmitPiecePtr(Sm83Emitter e) {
        e.MarkLabel(label: m_subPiecePtr);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceType);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×2
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×4
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: BrickfallProtocol.TestRot);
        e.Arithmetic(op: AluOp.Add, source: Reg8.C); // type*4 + rot
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×2
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×4
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_pieceTable.Address);
        e.AddToHl(pair: Reg16.De);
        e.Return();
    }

    // HL := WellBase + RowT*10 + ColT. Clobbers A, D, E.
    private void EmitWellAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subWellAddr);
        e.LoadAFromAddress(address: BrickfallProtocol.RowT);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // row×2
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.LoadAFromAddress(address: BrickfallProtocol.RowT);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // row×8
        e.Arithmetic(op: AluOp.Add, source: Reg8.D);                // row×10
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.LoadAFromAddress(address: BrickfallProtocol.ColT);
        e.Arithmetic(op: AluOp.Add, source: Reg8.D);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: BrickfallProtocol.WellBase);
        e.AddToHl(pair: Reg16.De);
        e.Return();
    }

    // HL := MapWellBase + RowT*32 + ColT (the background-map cell of a well cell). Clobbers A, D, E.
    private void EmitMapAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subMapAddr);
        e.LoadAFromAddress(address: BrickfallProtocol.RowT);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x07);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);              // (row&7) << 4
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // << 5
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.LoadAFromAddress(address: BrickfallProtocol.ColT);
        e.Arithmetic(op: AluOp.Add, source: Reg8.D);
        e.Load(destination: Reg8.E, source: Reg8.A);              // E = (row&7)*32 + col
        e.LoadAFromAddress(address: BrickfallProtocol.RowT);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Load(destination: Reg8.D, source: Reg8.A);              // D = row >> 3
        e.LoadImmediate(pair: Reg16.Hl, value: MapWellBase);
        e.AddToHl(pair: Reg16.De);
        e.Return();
    }

    // CollideFlag := 1 if the piece (PieceType at Test*) overlaps a wall/floor/occupied cell, else 0.
    private void EmitCollide(Sm83Emitter e) {
        var loop = e.NewLabel();
        var hit = e.NewLabel();

        e.MarkLabel(label: m_subCollide);
        e.Call(label: m_subPiecePtr);
        e.LoadImmediate(destination: Reg8.B, value: 4);

        e.MarkLabel(label: loop);
        e.LoadAFromHlIncrement();
        e.Load(destination: Reg8.E, source: Reg8.A); // dx
        e.LoadAFromHlIncrement();
        e.Load(destination: Reg8.D, source: Reg8.A); // dy
        e.LoadAFromAddress(address: BrickfallProtocol.TestX);
        e.Arithmetic(op: AluOp.Add, source: Reg8.E);
        e.ArithmeticImmediate(op: AluOp.Compare, value: BrickfallProtocol.WellColumns);
        e.JumpRelative(condition: Condition.NoCarry, label: hit); // col >= 10 (or wrapped past 0) → wall.
        e.StoreAToAddress(address: BrickfallProtocol.ColT);
        e.LoadAFromAddress(address: BrickfallProtocol.TestY);
        e.Arithmetic(op: AluOp.Add, source: Reg8.D);
        e.ArithmeticImmediate(op: AluOp.Compare, value: BrickfallProtocol.WellRows);
        e.JumpRelative(condition: Condition.NoCarry, label: hit); // row >= 18 → floor.
        e.StoreAToAddress(address: BrickfallProtocol.RowT);
        e.Push(pair: StackPair.Bc);
        e.Push(pair: StackPair.Hl);
        e.Call(label: m_subWellAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Pop(pair: StackPair.Hl);
        e.Pop(pair: StackPair.Bc);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: hit); // occupied → collide.
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: loop);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.CollideFlag);
        e.Return();

        e.MarkLabel(label: hit);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: BrickfallProtocol.CollideFlag);
        e.Return();
    }

    // Stamp BlockTile into the well at the piece's (Test*) cells AND queue the matching background-map writes.
    private void EmitLock(Sm83Emitter e) {
        var loop = e.NewLabel();

        e.MarkLabel(label: m_subLock);
        e.Call(label: m_subPiecePtr);
        e.LoadImmediate(destination: Reg8.B, value: 4);

        e.MarkLabel(label: loop);
        e.LoadAFromHlIncrement();
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadAFromAddress(address: BrickfallProtocol.TestX);
        e.Arithmetic(op: AluOp.Add, source: Reg8.E);
        e.StoreAToAddress(address: BrickfallProtocol.ColT);
        e.LoadAFromHlIncrement();
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadAFromAddress(address: BrickfallProtocol.TestY);
        e.Arithmetic(op: AluOp.Add, source: Reg8.E);
        e.StoreAToAddress(address: BrickfallProtocol.RowT);
        e.Push(pair: StackPair.Bc);
        e.Push(pair: StackPair.Hl);
        e.Call(label: m_subWellAddr);
        e.LoadAFromAddress(address: BrickfallProtocol.BlockTile);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.Call(label: m_subMapAddr);
        e.Load(destination: Reg8.D, source: Reg8.H);
        e.Load(destination: Reg8.E, source: Reg8.L);
        e.LoadAFromAddress(address: BrickfallProtocol.BlockTile);
        m_fw.Bg.EmitQueuePush();
        e.Pop(pair: StackPair.Hl);
        e.Pop(pair: StackPair.Bc);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: loop);
        e.Return();
    }

    // Scan rows bottom-up; a full row is shifted out and re-examined, bumping ClearedCount.
    private void EmitClear(Sm83Emitter e) {
        var rowLoop = e.NewLabel();
        var colLoop = e.NewLabel();
        var notFull = e.NewLabel();
        var done = e.NewLabel();

        e.MarkLabel(label: m_subClear);
        e.LoadAImmediate(value: (byte)(BrickfallProtocol.WellRows - 1));
        e.StoreAToAddress(address: BrickfallProtocol.RowScan);

        e.MarkLabel(label: rowLoop);
        e.LoadAFromAddress(address: BrickfallProtocol.RowScan);
        e.StoreAToAddress(address: BrickfallProtocol.RowT);
        e.LoadImmediate(destination: Reg8.C, value: 0);

        e.MarkLabel(label: colLoop);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.StoreAToAddress(address: BrickfallProtocol.ColT);
        e.Push(pair: StackPair.Bc);
        e.Call(label: m_subWellAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Pop(pair: StackPair.Bc);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: notFull); // an empty cell → row not full.
        e.Increment(register: Reg8.C);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Compare, value: BrickfallProtocol.WellColumns);
        e.JumpRelative(condition: Condition.Carry, label: colLoop);

        // Row full: shift down, count it, and re-check the same row (the shifted-in row may be full too).
        e.Call(label: m_subShiftDown);
        e.LoadAFromAddress(address: BrickfallProtocol.ClearedCount);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.ClearedCount);
        e.JumpRelative(label: rowLoop);

        e.MarkLabel(label: notFull);
        e.LoadAFromAddress(address: BrickfallProtocol.RowScan);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: done);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.RowScan);
        e.JumpRelative(label: rowLoop);

        e.MarkLabel(label: done);
        e.Return();
    }

    // Shift well rows [0 .. RowScan-1] down into [1 .. RowScan], then clear row 0.
    private void EmitShiftDown(Sm83Emitter e) {
        var shiftLoop = e.NewLabel();
        var clearTop = e.NewLabel();
        var copyRow = e.NewLabel();
        var clearRow = e.NewLabel();

        e.MarkLabel(label: m_subShiftDown);
        e.LoadAFromAddress(address: BrickfallProtocol.RowScan);
        e.StoreAToAddress(address: BrickfallProtocol.ShiftRow);

        e.MarkLabel(label: shiftLoop);
        e.LoadAFromAddress(address: BrickfallProtocol.ShiftRow);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: clearTop);

        // dst = well row ShiftRow; src = dst - 10.
        e.LoadAFromAddress(address: BrickfallProtocol.ShiftRow);
        e.StoreAToAddress(address: BrickfallProtocol.RowT);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.ColT);
        e.Call(label: m_subWellAddr);
        e.Push(pair: StackPair.Hl);
        e.LoadImmediate(pair: Reg16.De, value: 0xFFF6); // -10.
        e.AddToHl(pair: Reg16.De);
        e.Pop(pair: StackPair.De);
        e.LoadImmediate(destination: Reg8.B, value: BrickfallProtocol.WellColumns);
        e.MarkLabel(label: copyRow);
        e.LoadAFromHlIncrement();
        e.StoreAToDe();
        e.Increment(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: copyRow);
        e.LoadAFromAddress(address: BrickfallProtocol.ShiftRow);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.ShiftRow);
        e.JumpRelative(label: shiftLoop);

        e.MarkLabel(label: clearTop);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.RowT);
        e.StoreAToAddress(address: BrickfallProtocol.ColT);
        e.Call(label: m_subWellAddr);
        e.LoadImmediate(destination: Reg8.B, value: BrickfallProtocol.WellColumns);
        e.MarkLabel(label: clearRow);
        e.XorA();
        e.StoreAToHlIncrement();
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: clearRow);
        e.Return();
    }

    // Promote the preview to the falling piece, draw a fresh preview, reset the position, and probe the spawn cell
    // (CollideFlag = 1 means the stack reached the top — the caller decides between GameOver and Title).
    private void EmitSpawn(Sm83Emitter e) {
        var mod3 = e.NewLabel();
        var mod3Done = e.NewLabel();

        e.MarkLabel(label: m_subSpawn);
        e.LoadAFromAddress(address: BrickfallProtocol.NextPiece);
        e.StoreAToAddress(address: BrickfallProtocol.PieceType);

        // BlockTile = TileBlockBase + (type mod 3); stamp it on the four piece sprites.
        e.MarkLabel(label: mod3);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.Carry, label: mod3Done);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: 3);
        e.JumpRelative(label: mod3);
        e.MarkLabel(label: mod3Done);
        e.ArithmeticImmediate(op: AluOp.Add, value: BrickfallTables.TileBlockBase);
        e.StoreAToAddress(address: BrickfallProtocol.BlockTile);

        for (var sprite = 0; (sprite < 4); sprite++) {
            e.StoreAToAddress(address: OamManager.SpriteAddress(slot: (m_pieceSlot + sprite), byteIndex: 2));
        }

        // Draw the next preview piece.
        m_fw.Prng.EmitNextInRange(modulus: 7);
        e.StoreAToAddress(address: BrickfallProtocol.NextPiece);
        e.Call(label: m_subPreviewDraw);

        // Reset the position and probe the spawn cell.
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.PieceRot);
        e.StoreAToAddress(address: BrickfallProtocol.PieceY);
        e.StoreAToAddress(address: BrickfallProtocol.DropTimer);
        e.LoadAImmediate(value: SpawnColumn);
        e.StoreAToAddress(address: BrickfallProtocol.PieceX);
        e.Call(label: m_subSetTest);
        e.Call(label: m_subCollide);
        e.Return();
    }

    // Draw the NEXT preview metasprite (slots 4..7) for NextPiece at the HUD's preview box.
    private void EmitPreviewDraw(Sm83Emitter e) {
        e.MarkLabel(label: m_subPreviewDraw);
        e.LoadAFromAddress(address: BrickfallProtocol.NextPiece);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A); // ×16 (four 4-byte rows per piece).
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_previewTable.Address);
        e.AddToHl(pair: Reg16.De);
        e.LoadImmediate(destination: Reg8.B, value: PreviewBaseY);
        e.LoadImmediate(destination: Reg8.C, value: PreviewBaseX);
        m_fw.Oam.EmitDrawMetasprite(baseSlot: m_previewSlot, spriteCount: 4);
        e.Return();
    }

    // A fresh game: empty well, zeroed score/lines/level, a drawn preview, and a spawned first piece.
    private void EmitGameReset(Sm83Emitter e) {
        e.MarkLabel(label: m_subGameReset);
        FrameworkKernel.EmitBlockFill(emitter: e, destinationAddress: BrickfallProtocol.WellBase, byteCount: (BrickfallProtocol.WellColumns * BrickfallProtocol.WellRows), value: 0x00);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.Score);
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Score + 1));
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Score + 2));
        e.StoreAToAddress(address: BrickfallProtocol.Lines);
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Lines + 1));
        e.StoreAToAddress(address: BrickfallProtocol.LevelBcd);
        e.StoreAToAddress(address: BrickfallProtocol.LevelBin);
        e.StoreAToAddress(address: BrickfallProtocol.LinesUnits);
        e.StoreAToAddress(address: BrickfallProtocol.DropTimer);
        e.StoreAToAddress(address: BrickfallProtocol.ClearedCount);
        e.StoreAToAddress(address: BrickfallProtocol.SoftDropFlag);
        m_fw.Prng.EmitNextInRange(modulus: 7);
        e.StoreAToAddress(address: BrickfallProtocol.NextPiece);
        e.Call(label: m_subSpawn);
        e.Return();
    }

    // Blit the whole well shadow into the background map (LCD off / VBlank only).
    private void EmitWellBlit(Sm83Emitter e) {
        var rowLoop = e.NewLabel();
        var colLoop = e.NewLabel();
        var noCarry = e.NewLabel();

        e.MarkLabel(label: m_subWellBlit);
        e.LoadImmediate(pair: Reg16.Hl, value: MapWellBase);
        e.LoadImmediate(pair: Reg16.De, value: BrickfallProtocol.WellBase);
        e.LoadImmediate(destination: Reg8.B, value: BrickfallProtocol.WellRows);
        e.MarkLabel(label: rowLoop);
        e.LoadImmediate(destination: Reg8.C, value: BrickfallProtocol.WellColumns);
        e.MarkLabel(label: colLoop);
        e.LoadAFromDe();
        e.Increment(pair: Reg16.De);
        e.StoreAToHlIncrement();
        e.Decrement(register: Reg8.C);
        e.JumpRelative(condition: Condition.NotZero, label: colLoop);
        e.Load(destination: Reg8.A, source: Reg8.L);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(32 - BrickfallProtocol.WellColumns));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.JumpRelative(condition: Condition.NoCarry, label: noCarry);
        e.Increment(register: Reg8.H);
        e.MarkLabel(label: noCarry);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: rowLoop);
        e.Return();
    }

    // Repaint the whole well from its shadow with the LCD off — the line-clear path, where the incremental queue
    // writes are not enough. Also clears the queue so stale cell writes never land on the fresh paint.
    private void EmitWellRepaint(Sm83Emitter e) {
        e.MarkLabel(label: m_subWellRepaint);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        e.Call(label: m_subWellBlit);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.Return();
    }

    // Position the four falling-piece sprites from the piece table (X = (col)*8 + 16, Y = row*8 + 16 — the well sits
    // one screen column right of the map origin, folded into the +16).
    private void EmitPieceSprites(Sm83Emitter e) {
        e.MarkLabel(label: m_subPieceSprites);
        e.Call(label: m_subSetTest);
        e.Call(label: m_subPiecePtr);

        for (var sprite = 0; (sprite < 4); sprite++) {
            e.LoadAFromHlIncrement(); // dx
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: BrickfallProtocol.PieceX);
            e.Arithmetic(op: AluOp.Add, source: Reg8.B);
            e.Arithmetic(op: AluOp.Add, source: Reg8.A);
            e.Arithmetic(op: AluOp.Add, source: Reg8.A);
            e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8
            e.ArithmeticImmediate(op: AluOp.Add, value: 16);
            e.StoreAToAddress(address: OamManager.SpriteAddress(slot: (m_pieceSlot + sprite), byteIndex: 1));
            e.LoadAFromHlIncrement(); // dy
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: BrickfallProtocol.PieceY);
            e.Arithmetic(op: AluOp.Add, source: Reg8.B);
            e.Arithmetic(op: AluOp.Add, source: Reg8.A);
            e.Arithmetic(op: AluOp.Add, source: Reg8.A);
            e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8
            e.ArithmeticImmediate(op: AluOp.Add, value: 16);
            e.StoreAToAddress(address: OamManager.SpriteAddress(slot: (m_pieceSlot + sprite), byteIndex: 0));
        }

        e.Return();
    }

    // Queue the HUD numbers (score, lines, level) — twelve queue entries, drained next VBlank.
    private void EmitHudPrint(Sm83Emitter e) {
        e.MarkLabel(label: m_subHudPrint);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: BrickfallProtocol.Score, byteCount: 3, row: 2, column: 13);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: BrickfallProtocol.Lines, byteCount: 2, row: 5, column: 13);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: BrickfallProtocol.LevelBcd, byteCount: 1, row: 8, column: 13);
        e.Return();
    }

    private void EmitHideSprites(Sm83Emitter e) {
        e.MarkLabel(label: m_subHideSprites);
        m_fw.Oam.EmitHideRange(baseSlot: m_pieceSlot, count: 8);
        e.Return();
    }

    // Award a lock's cleared lines: bump the BCD line counter and the level bookkeeping per line, then add
    // base[cleared] × (level + 1) to the score with carry-chained BCD adds.
    private void EmitAwardScore(Sm83Emitter e) {
        var lineLoop = e.NewLabel();
        var noLevel = e.NewLabel();
        var scoreLoop = e.NewLabel();

        e.MarkLabel(label: m_subAwardScore);
        e.LoadAFromAddress(address: BrickfallProtocol.ClearedCount);
        e.Load(destination: Reg8.B, source: Reg8.A);

        e.MarkLabel(label: lineLoop);
        e.Push(pair: StackPair.Bc);
        e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Lines + 1));
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Lines + 1));
        e.LoadAFromAddress(address: BrickfallProtocol.Lines);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: BrickfallProtocol.Lines);
        e.LoadAFromAddress(address: BrickfallProtocol.LinesUnits);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.LinesUnits);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 10);
        e.JumpRelative(condition: Condition.Carry, label: noLevel);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.LinesUnits);
        e.LoadAFromAddress(address: BrickfallProtocol.LevelBin);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.LevelBin);
        e.LoadAFromAddress(address: BrickfallProtocol.LevelBcd);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: BrickfallProtocol.LevelBcd);
        e.MarkLabel(label: noLevel);
        e.Pop(pair: StackPair.Bc);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: lineLoop);

        // HL := scoreBases + (cleared - 1) × 3; add it (level + 1) times, least significant byte first.
        e.LoadAFromAddress(address: BrickfallProtocol.ClearedCount);
        e.Decrement(register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×2
        e.Arithmetic(op: AluOp.Add, source: Reg8.B); // ×3
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_scoreBases.Address);
        e.AddToHl(pair: Reg16.De);
        e.LoadAFromAddress(address: BrickfallProtocol.LevelBin);
        e.Increment(register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);

        e.MarkLabel(label: scoreLoop);
        e.Push(pair: StackPair.Bc);
        e.Push(pair: StackPair.Hl);
        e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Score + 2));
        e.Arithmetic(op: AluOp.Add, source: Reg8.Memory);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Score + 2));
        e.Increment(pair: Reg16.Hl);
        e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Score + 1));
        e.Arithmetic(op: AluOp.AddWithCarry, source: Reg8.Memory);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Score + 1));
        e.Increment(pair: Reg16.Hl);
        e.LoadAFromAddress(address: BrickfallProtocol.Score);
        e.Arithmetic(op: AluOp.AddWithCarry, source: Reg8.Memory);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: BrickfallProtocol.Score);
        e.Pop(pair: StackPair.Hl);
        e.Pop(pair: StackPair.Bc);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: scoreLoop);
        e.Return();
    }

    // Insert the current score + entered initials into the mirror table (sorted, ties keep the older entry), shifting
    // the lower entries down and dropping the last. The caller has already checked the score qualifies.
    private void EmitHiInsert(Sm83Emitter e) {
        var insertGo = e.NewLabel();
        var noShift = e.NewLabel();
        var shiftLoop = e.NewLabel();

        e.MarkLabel(label: m_subHiInsert);

        for (var slot = 0; (slot < (BrickfallProtocol.HiScoreEntryCount - 1)); slot++) {
            var take = e.NewLabel();
            var skip = e.NewLabel();
            var entryScore = (ushort)(BrickfallProtocol.HiScoreMirror + (slot * BrickfallProtocol.HiScoreEntryByteCount) + 3);

            for (var index = 0; (index < 3); index++) {
                e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Score + index));
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
            e.LoadImmediate(destination: Reg8.C, value: (byte)(slot * BrickfallProtocol.HiScoreEntryByteCount));
            e.JumpAbsolute(label: insertGo);
            e.MarkLabel(label: skip);
        }

        // The last slot always accepts (the caller verified score > the table's fifth entry).
        e.LoadImmediate(destination: Reg8.C, value: (byte)((BrickfallProtocol.HiScoreEntryCount - 1) * BrickfallProtocol.HiScoreEntryByteCount));

        e.MarkLabel(label: insertGo);

        // Shift entries [C/6 .. 3] down one slot, copying backwards; count = 24 - C bytes.
        e.LoadAImmediate(value: (byte)((BrickfallProtocol.HiScoreEntryCount - 1) * BrickfallProtocol.HiScoreEntryByteCount));
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(condition: Condition.Zero, label: noShift);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadImmediate(pair: Reg16.Hl, value: (ushort)(BrickfallProtocol.HiScoreMirror + (4 * BrickfallProtocol.HiScoreEntryByteCount) - 1));
        e.LoadImmediate(pair: Reg16.De, value: (ushort)(BrickfallProtocol.HiScoreMirror + (5 * BrickfallProtocol.HiScoreEntryByteCount) - 1));
        e.MarkLabel(label: shiftLoop);
        e.LoadAFromHlDecrement();
        e.StoreAToDe();
        e.Decrement(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: shiftLoop);
        e.MarkLabel(label: noShift);

        // Write the new entry at mirror + C: three initials (letter index → font tile), then the three score bytes.
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(BrickfallProtocol.HiScoreMirror & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(BrickfallProtocol.HiScoreMirror >> 8));

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.EntryGlyphs + index));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            e.StoreAToHlIncrement();
        }

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Score + index));
            e.StoreAToHlIncrement();
        }

        e.Return();
    }

    // The shared per-frame play simulation (Play ticks it directly; Attract ticks it under the input script):
    // edge-triggered shifts and rotation, gravity/soft drop, lock → clear → award → spawn, top-out resolution,
    // sprite positioning, and the queued HUD prints.
    private void EmitPlayCore(Sm83Emitter e) {
        var noLeft = e.NewLabel();
        var noRight = e.NewLabel();
        var doRotate = e.NewLabel();
        var noRotate = e.NewLabel();
        var levelOk = e.NewLabel();
        var forceDrop = e.NewLabel();
        var tryDrop = e.NewLabel();
        var landed = e.NewLabel();
        var skipSoft = e.NewLabel();
        var noRepaint = e.NewLabel();
        var realOver = e.NewLabel();
        var afterGravity = e.NewLabel();

        e.MarkLabel(label: m_subPlayCore);

        // Left (edge): try column - 1.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 1, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noLeft);
        e.Call(label: m_subSetTest);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceX);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.TestX);
        e.Call(label: m_subCollide);
        e.LoadAFromAddress(address: BrickfallProtocol.CollideFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: noLeft);
        e.LoadAFromAddress(address: BrickfallProtocol.TestX);
        e.StoreAToAddress(address: BrickfallProtocol.PieceX);
        e.MarkLabel(label: noLeft);

        // Right (edge): try column + 1.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        e.Call(label: m_subSetTest);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceX);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.TestX);
        e.Call(label: m_subCollide);
        e.LoadAFromAddress(address: BrickfallProtocol.CollideFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: noRight);
        e.LoadAFromAddress(address: BrickfallProtocol.TestX);
        e.StoreAToAddress(address: BrickfallProtocol.PieceX);
        e.MarkLabel(label: noRight);

        // Rotate (edge, Up or A): try (rotation + 1) & 3.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 2, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: doRotate);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRotate);
        e.MarkLabel(label: doRotate);
        e.Call(label: m_subSetTest);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceRot);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x03);
        e.StoreAToAddress(address: BrickfallProtocol.TestRot);
        e.Call(label: m_subCollide);
        e.LoadAFromAddress(address: BrickfallProtocol.CollideFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: noRotate);
        e.LoadAFromAddress(address: BrickfallProtocol.TestRot);
        e.StoreAToAddress(address: BrickfallProtocol.PieceRot);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.MarkLabel(label: noRotate);

        // Gravity: advance the drop timer; drop when it reaches the level's speed OR Down is held (a soft drop).
        e.LoadAFromAddress(address: BrickfallProtocol.DropTimer);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.DropTimer);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputHeld);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 3, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: forceDrop);
        e.LoadAFromAddress(address: BrickfallProtocol.LevelBin);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(m_speedTable.Length - 1));
        e.JumpRelative(condition: Condition.Carry, label: levelOk);
        e.LoadAImmediate(value: (byte)(m_speedTable.Length - 1));
        e.MarkLabel(label: levelOk);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_speedTable.Address);
        e.AddToHl(pair: Reg16.De);
        e.LoadAFromAddress(address: BrickfallProtocol.DropTimer);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.Memory);
        e.JumpAbsolute(condition: Condition.Carry, label: afterGravity); // timer < speed → wait.
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.SoftDropFlag);
        e.JumpRelative(label: tryDrop);
        e.MarkLabel(label: forceDrop);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: BrickfallProtocol.SoftDropFlag);
        e.MarkLabel(label: tryDrop);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.DropTimer);
        e.Call(label: m_subSetTest);
        e.LoadAFromAddress(address: BrickfallProtocol.PieceY);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.TestY);
        e.Call(label: m_subCollide);
        e.LoadAFromAddress(address: BrickfallProtocol.CollideFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: landed);
        e.LoadAFromAddress(address: BrickfallProtocol.TestY);
        e.StoreAToAddress(address: BrickfallProtocol.PieceY);

        // A moved soft drop scores +1 (BCD, carry-chained).
        e.LoadAFromAddress(address: BrickfallProtocol.SoftDropFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: skipSoft);
        e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Score + 2));
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Score + 2));
        e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Score + 1));
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.Score + 1));
        e.LoadAFromAddress(address: BrickfallProtocol.Score);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: BrickfallProtocol.Score);
        e.MarkLabel(label: skipSoft);
        e.JumpAbsolute(label: afterGravity);

        // Landed: lock, clear (with an LCD-off repaint + award on any clear), spawn — a blocked spawn resolves to
        // GameOver, or straight back to Title when the attract script is playing (attract never writes SRAM).
        e.MarkLabel(label: landed);
        e.Call(label: m_subSetTest);
        e.Call(label: m_subLock);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectThud);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.ClearedCount);
        e.Call(label: m_subClear);
        e.LoadAFromAddress(address: BrickfallProtocol.ClearedCount);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noRepaint);
        e.Call(label: m_subWellRepaint);
        e.Call(label: m_subAwardScore);
        // The clear's rising zip rides pulse 1 while the lock's thud rides noise — both land on a line clear.
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectSweep);
        e.MarkLabel(label: noRepaint);
        e.Call(label: m_subSpawn);
        e.LoadAFromAddress(address: BrickfallProtocol.CollideFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: afterGravity);
        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: BrickfallProtocol.StateAttract);
        e.JumpRelative(condition: Condition.NotZero, label: realOver);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateTitle);
        e.JumpRelative(label: afterGravity);
        e.MarkLabel(label: realOver);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateGameOver);

        e.MarkLabel(label: afterGravity);
        e.Call(label: m_subPieceSprites);
        e.Call(label: m_subHudPrint);
        e.Return();
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
        e.StoreAToAddress(address: BrickfallProtocol.IdleTimer);
        e.StoreAToAddress(address: BrickfallProtocol.IdleTimerHigh);
    }

    private void EmitTitleTick(Sm83Emitter e) {
        var noStart = e.NewLabel();
        var noSelect = e.NewLabel();
        var stay = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noStart);

        // START edge: the D4 input-entropy seed — the frame counter is sampled at THIS press — then a fresh game,
        // with the start chirp and the short loop kicking in as play begins.
        m_fw.Prng.EmitSeedFromFrameCounter();
        e.Call(label: m_subGameReset);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectFlip);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicLoop);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StatePlay);
        e.Return();

        e.MarkLabel(label: noStart);
        e.TestBit(bit: 6, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noSelect);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateHighScores);
        e.Return();

        e.MarkLabel(label: noSelect);
        EmitIdleAdvance(e: e, stayLabel: stay);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateAttract);
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
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: noReal);
        e.LoadAFromAddress(address: FrameworkMemoryMap.ScriptEnded);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: running);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateTitle);
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

        for (var entry = 0; (entry < BrickfallProtocol.HiScoreEntryCount); entry++) {
            var row = (5 + (entry * 2));
            var entryBase = (ushort)(BrickfallProtocol.HiScoreMirror + (entry * BrickfallProtocol.HiScoreEntryByteCount));

            for (var glyph = 0; (glyph < 3); glyph++) {
                e.LoadAFromAddress(address: (ushort)(entryBase + glyph));
                e.StoreAToAddress(address: Hw.MapCell(row: row, column: (4 + glyph)));
            }

            m_fw.Text.EmitPrintBcdDirect(bcdAddress: (ushort)(entryBase + 3), byteCount: 3, row: row, column: 9);
        }

        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.IdleTimer);
        e.StoreAToAddress(address: BrickfallProtocol.IdleTimerHigh);
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
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateTitle);
        e.MarkLabel(label: stay);
    }

    private void EmitPlayEnter(Sm83Emitter e) {
        // Repaint the play screen FROM STATE (never resetting it) so resuming from pause redraws cleanly; the game
        // reset itself happens on the title's START edge / the attract enter.
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitTitleAttributesReset(e: e);
        e.Call(label: m_subWellBlit);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);

        // Restore the piece sprites' tiles (positions land on the first tick) and the preview.
        e.LoadAFromAddress(address: BrickfallProtocol.BlockTile);

        for (var sprite = 0; (sprite < 4); sprite++) {
            e.StoreAToAddress(address: OamManager.SpriteAddress(slot: (m_pieceSlot + sprite), byteIndex: 2));
        }

        e.Call(label: m_subPreviewDraw);
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
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StatePause);
        e.Return();
        e.MarkLabel(label: noPause);
        e.Call(label: m_subPlayCore);
    }

    private void EmitPauseEnter(Sm83Emitter e) {
        m_fw.Oam.EmitHideRange(baseSlot: m_pieceSlot, count: 4);
        m_fw.Text.EmitPrintQueued(text: m_strPause, row: 8, column: 3);
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
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StatePlay);
        e.MarkLabel(label: stay);
    }

    private void EmitGameOverEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        m_fw.Text.EmitPrintQueued(text: m_strGameOver, row: 8, column: 1);
        e.LoadAImmediate(value: GameOverFrames);
        e.StoreAToAddress(address: BrickfallProtocol.GameOverTimer);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectOver);
        // The 128-bit meta-victory converge: a completed run writes this cabinet's host-seeded share into the top-16
        // SRAM win region (the room XORs it across cabinets to drive the editor reveal).
        m_fw.Victory.EmitStoreShare();
    }

    private void EmitGameOverTick(Sm83Emitter e) {
        var resolve = e.NewLabel();
        var qualify = e.NewLabel();
        var noQualify = e.NewLabel();
        var entry4Score = (ushort)(BrickfallProtocol.HiScoreMirror + ((BrickfallProtocol.HiScoreEntryCount - 1) * BrickfallProtocol.HiScoreEntryByteCount) + 3);

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.LoadAFromAddress(address: BrickfallProtocol.GameOverTimer);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.GameOverTimer);
        e.JumpRelative(condition: Condition.Zero, label: resolve);
        e.Return();

        // Resolve: a score STRICTLY greater than the table's fifth entry earns initials entry; anything else → title.
        // The three-byte BCD compare works most-significant first: entry < score at any significance qualifies,
        // entry > score bails, equal bytes defer to the next.
        e.MarkLabel(label: resolve);

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.Score + index));
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: (ushort)(entry4Score + index));
            e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            e.JumpRelative(condition: Condition.Carry, label: qualify);

            if (index < 2) {
                e.JumpRelative(condition: Condition.NotZero, label: noQualify);
            }
        }

        e.MarkLabel(label: noQualify);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: qualify);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateScoreEntry);
    }

    private void EmitScoreEntryEnter(Sm83Emitter e) {
        e.Call(label: m_subHideSprites);
        e.XorA();
        e.StoreAToAddress(address: BrickfallProtocol.EntryCursor);
        e.StoreAToAddress(address: BrickfallProtocol.EntryGlyphs);
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.EntryGlyphs + 1));
        e.StoreAToAddress(address: (ushort)(BrickfallProtocol.EntryGlyphs + 2));
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        EmitTitleAttributesReset(e: e);
        m_fw.Text.EmitPrintDirect(text: m_strNewHigh, row: 4, column: 3);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: BrickfallProtocol.Score, byteCount: 3, row: 6, column: 7);
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
        e.LoadAFromAddress(address: BrickfallProtocol.EntryCursor);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: leftDecrement);
        e.LoadAImmediate(value: 3);
        e.MarkLabel(label: leftDecrement);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: BrickfallProtocol.EntryCursor);
        e.MarkLabel(label: noLeft);

        var rightStore = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        e.LoadAFromAddress(address: BrickfallProtocol.EntryCursor);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.Carry, label: rightStore);
        e.XorA(); // Past the last slot → wrap to the first.
        e.MarkLabel(label: rightStore);
        e.StoreAToAddress(address: BrickfallProtocol.EntryCursor);
        e.MarkLabel(label: noRight);

        // Draw the three initials and the cursor markers through the queue (six entries a frame).
        for (var slot = 0; (slot < 3); slot++) {
            e.LoadAFromAddress(address: (ushort)(BrickfallProtocol.EntryGlyphs + slot));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            m_fw.Bg.EmitQueueCell(row: 10, column: (8 + slot));
        }

        for (var slot = 0; (slot < 3); slot++) {
            var isCursor = e.NewLabel();
            var push = e.NewLabel();

            e.LoadAFromAddress(address: BrickfallProtocol.EntryCursor);
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

        // Confirm (START or A): insert into the mirror, persist, celebrate, and show the table.
        e.MarkLabel(label: confirm);
        e.Call(label: m_subHiInsert);
        m_fw.Save.EmitStore();
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectWin);
        m_fw.States.EmitRequestState(id: BrickfallProtocol.StateHighScores);
    }

    // ==== Small emission helpers. ========================================================================================

    // HL := EntryGlyphs + EntryCursor (all three glyph bytes share one page).
    private static void EmitEntryGlyphPointer(Sm83Emitter e) {
        e.LoadAFromAddress(address: BrickfallProtocol.EntryCursor);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(BrickfallProtocol.EntryGlyphs & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(BrickfallProtocol.EntryGlyphs >> 8));
    }

    // The shared idle counter: += 1; control FALLS THROUGH when it reaches exactly 600, and jumps to the caller's
    // stay label otherwise (the enters reset it, so the equality fires exactly once).
    private static void EmitIdleAdvance(Sm83Emitter e, int stayLabel) {
        e.LoadAFromAddress(address: BrickfallProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.StoreAToAddress(address: BrickfallProtocol.IdleTimer);
        e.LoadAFromAddress(address: BrickfallProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.StoreAToAddress(address: BrickfallProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdHigh);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
        e.LoadAFromAddress(address: BrickfallProtocol.IdleTimer);
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
