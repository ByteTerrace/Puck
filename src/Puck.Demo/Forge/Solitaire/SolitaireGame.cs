using Puck.Demo.Forge.Cards;
using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The five-star Solitaire (classic Klondike, draw one): a full seven-state game — title menu, scripted attract,
/// battery-backed high scores and win streaks, play, pause, the win fanfare, and initials entry — built ON the SM83
/// game framework and the shared card layer. The board is twelve pile arrays over the card layer's 52-card record
/// table; the deal is the card layer's Fisher–Yates over the framework PRNG, seeded from pure input entropy at the
/// title's confirm edge (same press frame → bit-identical game); every board change is an LCD-off repaint (the
/// Brickfall line-clear discipline) while the cursor rides hardware sprites; undo is the card layer's fixed
/// work-RAM ring. Layout, navigation, rules inputs, and art all arrive as manifest data — the game's SM83 is logic
/// over tables.
/// </summary>
internal sealed class SolitaireGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;
    private const byte WinFrames = 180;
    // The idle threshold before the title/high-score screens fall into attract/back to title: 600 frames (0x0258).
    private const byte IdleThresholdLow = 0x58;
    private const byte IdleThresholdHigh = 0x02;
    // The attract loop's constant seed (attract is scripted; its deal must be the same every time).
    private const byte AttractSeedLow = 0x34;
    private const byte AttractSeedHigh = 0x12;

    private readonly GameFramework m_fw;
    private readonly CardMenu m_menu;
    private readonly CardInitialsPad m_pad;
    private readonly CardUndo m_undo;
    private readonly int m_cursorSlot;
    private readonly int m_cursorMaxEntries;
    private readonly LinkedSpriteSet m_cursor;

    private readonly RomTable m_bgPalettes;
    private readonly RomTable m_objPalettes;
    private readonly RomTable m_tiles;
    private readonly RomTable m_titleMap;
    private readonly RomTable? m_titleAttributes;
    private readonly RomTable m_playMap;
    private readonly RomTable? m_playAttributes;
    private readonly RomRecords m_cards;
    private readonly RomRecords m_positions;
    private readonly RomTable m_attractScript;
    private readonly RomTable m_strPause;
    private readonly RomTable m_strYouWin;
    private readonly RomTable m_strNewHigh;
    private readonly RomTable m_strHiScores;
    private readonly RomTable m_strStreak;
    private readonly RomTable m_strBest;

    private readonly int m_subPileAddr;
    private readonly int m_subCountAddr;
    private readonly int m_subGetCount;
    private readonly int m_subCardRecord;
    private readonly int m_subPosRecord;
    private readonly int m_subTailCard;
    private readonly int m_subRowColAddr;
    private readonly int m_subCellOffset;
    private readonly int m_subWriteCell;
    private readonly int m_subWriteFeltCell;
    private readonly int m_subFeltRestore;
    private readonly int m_subDrawCard;
    private readonly int m_subDrawBack;
    private readonly int m_subDrawOutline;
    private readonly int m_subDrawStripUp;
    private readonly int m_subDrawStripDown;
    private readonly int m_subDrawTops;
    private readonly int m_subDrawTableau;
    private readonly int m_subHudPrint;
    private readonly int m_subBoardRepaint;
    private readonly int m_subCursorSprite;
    private readonly int m_subShuffle;
    private readonly int m_subWinCard;
    private readonly int m_winCardSlot;
    private readonly int m_subGameReset;
    private readonly int m_subScoreAdd;
    private readonly int m_subScoreSub;
    private readonly int m_subByteToBcd;
    private readonly int m_subLegalDrop;
    private readonly int m_subMoveBlock;
    private readonly int m_subDoMove;
    private readonly int m_subActionA;
    private readonly int m_subDrawAction;
    private readonly int m_subDropAction;
    private readonly int m_subUndoApply;
    private readonly int m_subWinCheck;
    private readonly int m_subHiInsert;
    private readonly int m_subPlayCore;

    // The game's identity as a declarative manifest: the card layer's tiles/records/palettes, the two screens (each
    // art-backed when its bake installed), the cursor sprite set (baked or the hand-authored fallback), the position
    // table, the attract script, and every string. The linker owns all relocation.
    private static GameManifest BuildManifest(PbakBackground? titleArt, PbakBackground? feltArt, PbakBundle? cursorArt) {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "card-tiles", tiles2bpp: CardTables.BuildCardTiles());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg-gameplay", paletteData: HgbImage.EncodePalette(palette: CardTables.BackgroundPalette));
        manifest.DefineObjectPalettes(name: "obj-gameplay", paletteData: HgbImage.EncodePalette(palette: CardTables.ObjectPalette));

        if (titleArt is not null) {
            manifest.DefineArtScreen(name: "title", art: titleArt, overlays: SolitaireTables.TitleOverlays);
        }
        else {
            manifest.DefineScreen(name: "title", cells: SolitaireTables.BuildTitleBannerCells(), overlays: SolitaireTables.TitleOverlays);
        }

        if (feltArt is not null) {
            manifest.DefineArtScreen(name: "play", art: feltArt, overlays: SolitaireTables.PlayOverlays);
        }
        else {
            manifest.DefineScreen(name: "play", cells: SolitaireTables.BuildFallbackFeltCells(), overlays: SolitaireTables.PlayOverlays);
        }

        manifest.DefineSpriteArt(name: "cursor", bundle: (cursorArt ?? CardTables.BuildFallbackCursorBundle()));
        manifest.DefineRecords(name: "cards", stride: 4, records: CardTables.BuildCardRecords());
        manifest.DefineRecords(name: "positions", stride: SolitaireTables.PositionRecordStride, records: SolitaireTables.BuildPositionRecords());
        manifest.DefineInputScript(name: "attract", steps: SolitaireTables.BuildAttractScript());
        manifest.DefineText(name: "str-new-deal", text: "NEW DEAL");
        manifest.DefineText(name: "str-scores", text: "SCORES");
        manifest.DefineText(name: "str-pause", text: "PAUSE");
        manifest.DefineText(name: "str-you-win", text: "YOU WIN");
        manifest.DefineText(name: "str-new-high", text: "NEW HIGH SCORE");
        manifest.DefineText(name: "str-hi-scores", text: "HI SCORES");
        manifest.DefineText(name: "str-streak", text: "STREAK");
        manifest.DefineText(name: "str-best", text: "BEST");

        // The shared sound catalog (the CardSfx ids alias its streams) rides the manifest like every other table;
        // the ApuSoundDriver binds to the linked streams in the ctor.
        SoundTables.DefineIn(manifest: manifest);

        return manifest;
    }

    private SolitaireGame() {
        var manifest = BuildManifest(titleArt: SolitaireTables.TitleArt, feltArt: SolitaireTables.FeltArt, cursorArt: SolitaireTables.CursorArt);

        // The protocol/verify layer references the font base as a constant; guard the two against drifting apart.
        if (manifest.FontTileBase != SolitaireTables.FontTileBase) {
            throw new InvalidOperationException(message: $"The manifest landed the font at tile {manifest.FontTileBase}, not the pinned {SolitaireTables.FontTileBase}.");
        }

        var sound = new ApuSoundDriver();

        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: SolitaireTables.BuildDefaultSavePayload(), saveVersion: 1, sound: sound);

        var linked = manifest.Link(framework: m_fw);

        sound.Bind(linked: linked);
        var title = linked.Screen(name: "title");
        var play = linked.Screen(name: "play");

        m_bgPalettes = linked.BackgroundPalettes;
        m_objPalettes = linked.ObjectPalettes;
        m_tiles = linked.TileBank;
        m_titleMap = title.Map;
        m_titleAttributes = title.Attributes;
        m_playMap = play.Map;
        m_playAttributes = play.Attributes;
        m_cards = linked.Records(name: "cards");
        m_positions = linked.Records(name: "positions");
        m_attractScript = linked.InputScript(name: "attract");
        m_strPause = linked.Text(name: "str-pause");
        m_strYouWin = linked.Text(name: "str-you-win");
        m_strNewHigh = linked.Text(name: "str-new-high");
        m_strHiScores = linked.Text(name: "str-hi-scores");
        m_strStreak = linked.Text(name: "str-streak");
        m_strBest = linked.Text(name: "str-best");
        m_cursor = linked.SpriteArt(name: "cursor")[0];
        m_cursorMaxEntries = m_cursor.FrameEntryCounts.Max();
        m_cursorSlot = m_fw.Oam.Reserve(count: m_cursorMaxEntries);
        m_winCardSlot = m_fw.Oam.Reserve(count: 4);
        m_menu = new CardMenu(
            cursorAddress: SolitaireProtocol.MenuCursor,
            fw: m_fw,
            items: [
                new CardMenuItem(Column: 7, Row: 11, Text: linked.Text(name: "str-new-deal")),
                new CardMenuItem(Column: 7, Row: 13, Text: linked.Text(name: "str-scores")),
            ]
        );
        m_pad = new CardInitialsPad(column: 8, cursorAddress: SolitaireProtocol.EntryCursor, fw: m_fw, glyphsAddress: SolitaireProtocol.EntryGlyphs, row: 10);
        m_undo = new CardUndo(
            capacity: SolitaireProtocol.UndoCapacity,
            countAddress: SolitaireProtocol.UndoCount,
            emitter: m_fw.Emitter,
            headAddress: SolitaireProtocol.UndoHead,
            ringBase: SolitaireProtocol.UndoRing,
            stagingBase: SolitaireProtocol.UndoStaging,
            stride: SolitaireProtocol.UndoStride
        );

        var e = m_fw.Emitter;

        m_subPileAddr = e.NewLabel();
        m_subCountAddr = e.NewLabel();
        m_subGetCount = e.NewLabel();
        m_subCardRecord = e.NewLabel();
        m_subPosRecord = e.NewLabel();
        m_subTailCard = e.NewLabel();
        m_subRowColAddr = e.NewLabel();
        m_subCellOffset = e.NewLabel();
        m_subWriteCell = e.NewLabel();
        m_subWriteFeltCell = e.NewLabel();
        m_subFeltRestore = e.NewLabel();
        m_subDrawCard = e.NewLabel();
        m_subDrawBack = e.NewLabel();
        m_subDrawOutline = e.NewLabel();
        m_subDrawStripUp = e.NewLabel();
        m_subDrawStripDown = e.NewLabel();
        m_subDrawTops = e.NewLabel();
        m_subDrawTableau = e.NewLabel();
        m_subHudPrint = e.NewLabel();
        m_subBoardRepaint = e.NewLabel();
        m_subCursorSprite = e.NewLabel();
        m_subShuffle = e.NewLabel();
        m_subWinCard = e.NewLabel();
        m_subGameReset = e.NewLabel();
        m_subScoreAdd = e.NewLabel();
        m_subScoreSub = e.NewLabel();
        m_subByteToBcd = e.NewLabel();
        m_subLegalDrop = e.NewLabel();
        m_subMoveBlock = e.NewLabel();
        m_subDoMove = e.NewLabel();
        m_subActionA = e.NewLabel();
        m_subDrawAction = e.NewLabel();
        m_subDropAction = e.NewLabel();
        m_subUndoApply = e.NewLabel();
        m_subWinCheck = e.NewLabel();
        m_subHiInsert = e.NewLabel();
        m_subPlayCore = e.NewLabel();

        m_fw.States.DefineState(id: SolitaireProtocol.StateTitle, emitEnter: EmitTitleEnter, emitTick: EmitTitleTick);
        m_fw.States.DefineState(id: SolitaireProtocol.StateAttract, emitEnter: EmitAttractEnter, emitTick: EmitAttractTick);
        m_fw.States.DefineState(id: SolitaireProtocol.StateHighScores, emitEnter: EmitHighScoresEnter, emitTick: EmitHighScoresTick);
        m_fw.States.DefineState(id: SolitaireProtocol.StatePlay, emitEnter: EmitPlayEnter, emitTick: EmitPlayTick);
        m_fw.States.DefineState(id: SolitaireProtocol.StatePause, emitEnter: EmitPauseEnter, emitTick: EmitPauseTick);
        m_fw.States.DefineState(id: SolitaireProtocol.StateWin, emitEnter: EmitWinEnter, emitTick: EmitWinTick);
        m_fw.States.DefineState(id: SolitaireProtocol.StateScoreEntry, emitEnter: EmitScoreEntryEnter, emitTick: EmitScoreEntryTick);
    }

    /// <summary>Assembles the cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new SolitaireGame();
        var spec = new FrameworkBootSpec(
            BgPalettes: game.m_bgPalettes,
            ObjPalettes: game.m_objPalettes,
            Tiles: game.m_tiles,
            TileByteCount: game.m_tiles.Length,
            InitialMap: game.m_titleMap,
            Lcdc: GameLcdc,
            InitialState: SolitaireProtocol.StateTitle
        );

        return game.m_fw.BuildRom(title: title, bootSpec: spec, emitGameLibrary: game.EmitGameLibrary);
    }

    // ==== The game library (shared subroutines). ========================================================================

    private void EmitGameLibrary(Sm83Emitter e) {
        EmitPileAddr(e: e);
        EmitCountAddr(e: e);
        EmitGetCount(e: e);
        EmitCardRecord(e: e);
        EmitPosRecord(e: e);
        EmitTailCard(e: e);
        EmitRowColAddr(e: e);
        EmitCellOffset(e: e);
        EmitWriteCell(e: e);
        EmitWriteFeltCell(e: e);
        EmitFeltRestore(e: e);
        EmitDrawCard(e: e);
        EmitDrawBack(e: e);
        EmitDrawOutline(e: e);
        EmitDrawStripUp(e: e);
        EmitDrawStripDown(e: e);
        EmitDrawTops(e: e);
        EmitDrawTableau(e: e);
        EmitHudPrint(e: e);
        EmitBoardRepaint(e: e);
        EmitCursorSprite(e: e);
        CardDeck.EmitShuffleSubroutine(e: e, prng: m_fw.Prng, label: m_subShuffle, deckBase: SolitaireProtocol.DeckScratch, indexScratch: SolitaireProtocol.IdxI, drawScratch: SolitaireProtocol.IdxJ);
        EmitWinCard(e: e);
        EmitGameReset(e: e);
        EmitScoreAdd(e: e);
        EmitScoreSub(e: e);
        EmitByteToBcd(e: e);
        EmitLegalDrop(e: e);
        EmitMoveBlock(e: e);
        EmitDoMove(e: e);
        EmitActionA(e: e);
        EmitDrawAction(e: e);
        EmitDropAction(e: e);
        EmitUndoApply(e: e);
        EmitWinCheck(e: e);
        EmitHiInsert(e: e);
        EmitPlayCore(e: e);
        m_undo.EmitLibrary();
    }

    // HL := PileBase + TmpPile × 24 + TmpIdx. Clobbers A, D, E.
    private void EmitPileAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subPileAddr);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpPile);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8 (≤ 88).
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: SolitaireProtocol.PileBase);
        e.AddToHl(pair: Reg16.De);
        e.AddToHl(pair: Reg16.De);
        e.AddToHl(pair: Reg16.De); // +24 × pile.
        e.LoadAFromAddress(address: SolitaireProtocol.TmpIdx);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.AddToHl(pair: Reg16.De);
        e.Return();
    }

    // HL := CountsBase + TmpPile (one page). Clobbers A.
    private void EmitCountAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subCountAddr);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpPile);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(SolitaireProtocol.CountsBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.CountsBase >> 8));
        e.Return();
    }

    // A := counts[TmpPile]. Clobbers H, L.
    private void EmitGetCount(Sm83Emitter e) {
        e.MarkLabel(label: m_subGetCount);
        e.Call(label: m_subCountAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Return();
    }

    // A = card id → TmpRank/TmpSuit/TmpRed/TmpRankTile from the card record table. Clobbers A, D, E, H, L.
    private void EmitCardRecord(Sm83Emitter e) {
        e.MarkLabel(label: m_subCardRecord);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×4 (≤ 204).
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_cards.Table.Address);
        e.AddToHl(pair: Reg16.De);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: SolitaireProtocol.TmpRank);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: SolitaireProtocol.TmpSuit);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: SolitaireProtocol.TmpRed);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: SolitaireProtocol.TmpRankTile);
        e.Return();
    }

    // A = position → HL := the position record's first byte. Clobbers A, C, D, E.
    private void EmitPosRecord(Sm83Emitter e) {
        e.MarkLabel(label: m_subPosRecord);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×2
        e.Arithmetic(op: AluOp.Add, source: Reg8.C); // ×3
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×6 (≤ 72).
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_positions.Table.Address);
        e.AddToHl(pair: Reg16.De);
        e.Return();
    }

    // A := the tail card of pile TmpPile (the caller ensures the count is non-zero). Clobbers A, D, E, H, L, TmpIdx.
    private void EmitTailCard(Sm83Emitter e) {
        e.MarkLabel(label: m_subTailCard);
        e.Call(label: m_subGetCount);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.Call(label: m_subPileAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Return();
    }

    // HL := 0x9800 + TmpRow × 32 + TmpCol. Clobbers A, C.
    private void EmitRowColAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subRowColAddr);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x07);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // (row & 7) × 32.
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Arithmetic(op: AluOp.Add, source: Reg8.C);
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(Hw.VramBackgroundMap >> 8));
        e.Load(destination: Reg8.H, source: Reg8.A);
        e.Return();
    }

    // DE := TmpRow × 32 + TmpCol (a map-table byte offset). Clobbers A, C.
    private void EmitCellOffset(Sm83Emitter e) {
        e.MarkLabel(label: m_subCellOffset);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x07);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Arithmetic(op: AluOp.Add, source: Reg8.C);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftRightLogical, register: Reg8.A);
        e.Load(destination: Reg8.D, source: Reg8.A);
        e.Return();
    }

    // Writes tile B at (TmpRow, TmpCol) and zeroes the cell's attribute (LCD off / VBlank only). Clobbers A, C, H, L.
    private void EmitWriteCell(Sm83Emitter e) {
        e.MarkLabel(label: m_subWriteCell);
        e.Call(label: m_subRowColAddr);
        e.Load(destination: Reg8.Memory, source: Reg8.B);
        e.LoadAImmediate(value: 0x01);
        e.StoreAToHighPage(port: Hw.PortVramBank);
        e.LoadImmediate(destination: Reg8.Memory, value: 0x00);
        e.XorA();
        e.StoreAToHighPage(port: Hw.PortVramBank);
        e.Return();
    }

    // Restores one felt cell at (TmpRow, TmpCol) from the play screen's ROM tables (map, and attributes when the
    // felt is art-backed). LCD off only. Clobbers A, B, C, D, E, H, L.
    private void EmitWriteFeltCell(Sm83Emitter e) {
        e.MarkLabel(label: m_subWriteFeltCell);
        e.Call(label: m_subCellOffset);
        e.Push(pair: StackPair.De);
        e.LoadImmediate(pair: Reg16.Hl, value: m_playMap.Address);
        e.AddToHl(pair: Reg16.De);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subRowColAddr);
        e.Load(destination: Reg8.Memory, source: Reg8.B);
        e.Pop(pair: StackPair.De);

        if (m_playAttributes is { } attributes) {
            e.Push(pair: StackPair.Hl);
            e.LoadImmediate(pair: Reg16.Hl, value: attributes.Address);
            e.AddToHl(pair: Reg16.De);
            e.Load(destination: Reg8.A, source: Reg8.Memory);
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.Pop(pair: StackPair.Hl);
            e.LoadAImmediate(value: 0x01);
            e.StoreAToHighPage(port: Hw.PortVramBank);
            e.Load(destination: Reg8.Memory, source: Reg8.B);
            e.XorA();
            e.StoreAToHighPage(port: Hw.PortVramBank);
        }
        else if (m_titleAttributes is not null) {
            // The felt is flat but the title paints attributes; keep the board's cells pinned to palette 0.
            e.LoadAImmediate(value: 0x01);
            e.StoreAToHighPage(port: Hw.PortVramBank);
            e.LoadImmediate(destination: Reg8.Memory, value: 0x00);
            e.XorA();
            e.StoreAToHighPage(port: Hw.PortVramBank);
        }

        e.Return();
    }

    // Restores the whole board region (rows 0..17, columns 0..15) to the felt. LCD off only.
    private void EmitFeltRestore(Sm83Emitter e) {
        var rowLoop = e.NewLabel();
        var columnLoop = e.NewLabel();

        e.MarkLabel(label: m_subFeltRestore);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.MarkLabel(label: rowLoop);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.MarkLabel(label: columnLoop);
        e.Call(label: m_subWriteFeltCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.BoardColumns);
        e.JumpRelative(condition: Condition.Carry, label: columnLoop);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 18);
        e.JumpRelative(condition: Condition.Carry, label: rowLoop);
        e.Return();
    }

    // Draws the full 2×2 face of TmpCard at (TmpRow, TmpCol); TmpRow/TmpCol are restored on return.
    private void EmitDrawCard(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawCard);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCard);
        e.Call(label: m_subCardRecord);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRankTile);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSuit);
        e.ArithmeticImmediate(op: AluOp.Add, value: CardTables.TileSuitBase);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.LoadImmediate(destination: Reg8.B, value: CardTables.TileCardBottomRight);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadImmediate(destination: Reg8.B, value: CardTables.TileCardBottomLeft);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.Return();
    }

    private void EmitDrawBack(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawBack);
        EmitDrawBlock2x2(e: e, baseTile: CardTables.TileBackBase);
        e.Return();
    }

    private void EmitDrawOutline(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawOutline);
        EmitDrawBlock2x2(e: e, baseTile: CardTables.TileOutlineBase);
        e.Return();
    }

    // Shared 2×2 constant-tile block body (TL, TR, BL, BR); TmpRow/TmpCol restored.
    private void EmitDrawBlock2x2(Sm83Emitter e, byte baseTile) {
        e.LoadImmediate(destination: Reg8.B, value: baseTile);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadImmediate(destination: Reg8.B, value: (byte)(baseTile + 1));
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.LoadImmediate(destination: Reg8.B, value: (byte)(baseTile + 3));
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadImmediate(destination: Reg8.B, value: (byte)(baseTile + 2));
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRow);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
    }

    // A fanned face-up row: rank + suit corners of TmpCard at (TmpRow, TmpCol); TmpCol restored.
    private void EmitDrawStripUp(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawStripUp);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCard);
        e.Call(label: m_subCardRecord);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRankTile);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSuit);
        e.ArithmeticImmediate(op: AluOp.Add, value: CardTables.TileSuitBase);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.Return();
    }

    // A fanned face-down row: the back's top edge; TmpCol restored.
    private void EmitDrawStripDown(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawStripDown);
        e.LoadImmediate(destination: Reg8.B, value: CardTables.TileBackBase);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadImmediate(destination: Reg8.B, value: (byte)(CardTables.TileBackBase + 1));
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCol);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.Return();
    }

    // Draws the top row: stock (back / outline), waste (top card / outline), and the four foundations.
    private void EmitDrawTops(Sm83Emitter e) {
        var stockEmpty = e.NewLabel();
        var stockDone = e.NewLabel();
        var wasteEmpty = e.NewLabel();
        var wasteDone = e.NewLabel();

        e.MarkLabel(label: m_subDrawTops);

        // Stock (column 1): a back while cards remain above the split, else the outline.
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: stockEmpty);
        e.Call(label: m_subDrawBack);
        e.JumpRelative(label: stockDone);
        e.MarkLabel(label: stockEmpty);
        e.Call(label: m_subDrawOutline);
        e.MarkLabel(label: stockDone);

        // Waste (column 4): the card just above the split, else the outline.
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.LoadAImmediate(value: 4);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: wasteEmpty);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subPileAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCard);
        e.Call(label: m_subDrawCard);
        e.JumpRelative(label: wasteDone);
        e.MarkLabel(label: wasteEmpty);
        e.Call(label: m_subDrawOutline);
        e.MarkLabel(label: wasteDone);

        // Foundations (columns 7/9/11/13): the top card, else the outline.
        for (var foundation = 0; (foundation < 4); foundation++) {
            var empty = e.NewLabel();
            var done = e.NewLabel();

            e.XorA();
            e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
            e.LoadAImmediate(value: (byte)(7 + (foundation * 2)));
            e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
            e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.CountsBase + SolitaireProtocol.PileFoundationBase + foundation));
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpRelative(condition: Condition.Zero, label: empty);
            e.LoadAImmediate(value: (byte)(SolitaireProtocol.PileFoundationBase + foundation));
            e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
            e.Call(label: m_subTailCard);
            e.StoreAToAddress(address: SolitaireProtocol.TmpCard);
            e.Call(label: m_subDrawCard);
            e.JumpRelative(label: done);
            e.MarkLabel(label: empty);
            e.Call(label: m_subDrawOutline);
            e.MarkLabel(label: done);
        }

        e.Return();
    }

    // Draws tableau column TabIdx: face-down strips, face-up rank strips, the full tail card, clipping tall
    // columns from the top (the marker tile flags the clip).
    private void EmitDrawTableau(Sm83Emitter e) {
        var notEmpty = e.NewLabel();
        var hasSkip = e.NewLabel();
        var kLoop = e.NewLabel();
        var kDone = e.NewLabel();
        var isLast = e.NewLabel();
        var lastDown = e.NewLabel();
        var stepDown = e.NewLabel();
        var stepUp = e.NewLabel();
        var advanceOne = e.NewLabel();
        var advanceTwo = e.NewLabel();
        var noMarker = e.NewLabel();

        e.MarkLabel(label: m_subDrawTableau);

        // Pile + column from the position record (position = 6 + TabIdx).
        e.LoadAFromAddress(address: SolitaireProtocol.TabIdx);
        e.ArithmeticImmediate(op: AluOp.Add, value: SolitaireProtocol.PositionTableauBase);
        e.Call(label: m_subPosRecord);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Increment(pair: Reg16.Hl);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpColumn);

        e.Call(label: m_subGetCount);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCount);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: notEmpty);

        // Empty column: the outline at the tableau's top row.
        e.LoadAImmediate(value: SolitaireProtocol.TableauTopRow);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpColumn);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.Call(label: m_subDrawOutline);
        e.Return();

        e.MarkLabel(label: notEmpty);

        // down = count − faceUp[TabIdx]; skip = max(0, count − 14).
        e.LoadAFromAddress(address: SolitaireProtocol.TabIdx);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(SolitaireProtocol.FaceUpBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.FaceUpBase >> 8));
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCount);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.StoreAToAddress(address: SolitaireProtocol.TmpDown);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCount);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: (byte)(SolitaireProtocol.TableauRows - 1));
        e.JumpRelative(condition: Condition.NoCarry, label: hasSkip);
        e.XorA();
        e.MarkLabel(label: hasSkip);
        e.StoreAToAddress(address: SolitaireProtocol.TmpSkip);

        // y = the tableau's top row; k = skip.
        e.LoadAImmediate(value: SolitaireProtocol.TableauTopRow);
        e.StoreAToAddress(address: SolitaireProtocol.TmpY);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSkip);
        e.StoreAToAddress(address: SolitaireProtocol.IdxI);

        e.MarkLabel(label: kLoop);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxI);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpAbsolute(condition: Condition.NoCarry, label: kDone);

        // The card at k, drawn at (TmpY, column).
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.Call(label: m_subPileAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCard);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpY);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpColumn);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);

        // Last card? (k + 1 == count)
        e.LoadAFromAddress(address: SolitaireProtocol.IdxI);
        e.Increment(register: Reg8.A);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B); // B still holds the count.
        e.JumpRelative(condition: Condition.Zero, label: isLast);

        // A fanned strip: down (k < down) or up.
        e.LoadAFromAddress(address: SolitaireProtocol.TmpDown);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxI);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Carry, label: stepDown);
        e.JumpRelative(label: stepUp);
        e.MarkLabel(label: stepDown);
        e.Call(label: m_subDrawStripDown);
        e.JumpRelative(label: advanceOne);
        e.MarkLabel(label: stepUp);
        e.Call(label: m_subDrawStripUp);
        e.JumpRelative(label: advanceOne);

        // The tail card: the full 2×2 face (or back, when still face-down).
        e.MarkLabel(label: isLast);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpDown);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxI);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Carry, label: lastDown);
        e.Call(label: m_subDrawCard);
        e.JumpRelative(label: advanceTwo);
        e.MarkLabel(label: lastDown);
        e.Call(label: m_subDrawBack);
        e.JumpRelative(label: advanceTwo);

        e.MarkLabel(label: advanceOne);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpY);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpY);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxI);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.IdxI);
        e.JumpAbsolute(label: kLoop);

        e.MarkLabel(label: advanceTwo);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpY);
        e.Increment(register: Reg8.A);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpY);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxI);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.IdxI);
        e.JumpAbsolute(label: kLoop);

        e.MarkLabel(label: kDone);

        // The clip marker over the column's first visible cell.
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSkip);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noMarker);
        e.LoadAImmediate(value: SolitaireProtocol.TableauTopRow);
        e.StoreAToAddress(address: SolitaireProtocol.TmpRow);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpColumn);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCol);
        e.LoadImmediate(destination: Reg8.B, value: CardTables.TileMarker);
        e.Call(label: m_subWriteCell);
        e.MarkLabel(label: noMarker);
        e.Return();
    }

    // Prints the HUD numbers directly (LCD off): the score, the stock remainder, the streak.
    private void EmitHudPrint(Sm83Emitter e) {
        e.MarkLabel(label: m_subHudPrint);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: SolitaireProtocol.Score, byteCount: 3, row: SolitaireProtocol.HudRow, column: 3);

        // The stock remainder (binary) → packed BCD → one direct BCD byte.
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.Call(label: m_subByteToBcd);
        e.StoreAToAddress(address: SolitaireProtocol.TmpFlag);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: SolitaireProtocol.TmpFlag, byteCount: 1, row: SolitaireProtocol.HudRow, column: 12);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: SolitaireProtocol.StreakMirror, byteCount: 1, row: SolitaireProtocol.HudRow, column: 17);
        e.Return();
    }

    // The whole-board repaint: LCD off, felt restore, the piles, the HUD, LCD on (the Brickfall line-clear
    // discipline — one blink per MOVE, never per cursor step).
    private void EmitBoardRepaint(Sm83Emitter e) {
        var tabLoop = e.NewLabel();

        e.MarkLabel(label: m_subBoardRepaint);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        e.Call(label: m_subFeltRestore);
        e.Call(label: m_subDrawTops);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.TabIdx);
        e.MarkLabel(label: tabLoop);
        e.Call(label: m_subDrawTableau);
        e.LoadAFromAddress(address: SolitaireProtocol.TabIdx);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TabIdx);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 7);
        e.JumpRelative(condition: Condition.Carry, label: tabLoop);
        e.Call(label: m_subHudPrint);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.Return();
    }

    // Positions and draws the cursor metasprite: X from the position's column, Y from the pile's visible fan (the
    // carried depth raises it), the grab frame while carrying.
    private void EmitCursorSprite(Sm83Emitter e) {
        var notTableau = e.NewLabel();
        var haveY = e.NewLabel();
        var emptyPile = e.NewLabel();
        var depthOne = e.NewLabel();
        var haveDepth = e.NewLabel();
        var pixelY = e.NewLabel();
        var grabFrame = e.NewLabel();
        var drawDone = e.NewLabel();

        e.MarkLabel(label: m_subCursorSprite);
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);

        // Y: top-row positions sit over the card row; tableau positions ride the fan.
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PositionTableauBase);
        e.JumpAbsolute(condition: Condition.Carry, label: notTableau);

        // Tableau: pile + count.
        e.Call(label: m_subPosRecord); // A still holds the position.
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subGetCount);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: emptyPile);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCount);

        // d = the carried depth when carrying FROM here, else 1.
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: depthOne);
        e.LoadAFromAddress(address: SolitaireProtocol.CarrySrc);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: depthOne);
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.JumpRelative(label: haveDepth);
        e.MarkLabel(label: depthOne);
        e.LoadAImmediate(value: 1);
        e.MarkLabel(label: haveDepth);
        e.Load(destination: Reg8.B, source: Reg8.A);

        // row = 3 + count − d − skip; skip = max(0, count − 14). (d ≤ count, and skip < count − d + 1.)
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCount);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCount);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: (byte)(SolitaireProtocol.TableauRows - 1));
        e.JumpRelative(condition: Condition.NoCarry, label: haveY); // A = skip.
        e.XorA();
        e.MarkLabel(label: haveY);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.ArithmeticImmediate(op: AluOp.Add, value: SolitaireProtocol.TableauTopRow);
        e.JumpRelative(label: pixelY);

        e.MarkLabel(label: emptyPile);
        e.LoadAImmediate(value: SolitaireProtocol.TableauTopRow);
        e.JumpRelative(label: pixelY);

        e.MarkLabel(label: notTableau);
        e.LoadAImmediate(value: 1);

        // A = map row → TmpSpriteY = row × 8 + 20, falling through to the X computation and the frame draw.
        e.MarkLabel(label: pixelY);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8.
        e.ArithmeticImmediate(op: AluOp.Add, value: 20);
        e.StoreAToAddress(address: SolitaireProtocol.TmpSpriteY);

        // X = column × 8 + 22; B = Y, C = X, then the frame draw.
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Call(label: m_subPosRecord);
        e.Increment(pair: Reg16.Hl);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8.
        e.ArithmeticImmediate(op: AluOp.Add, value: 22);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSpriteY);
        e.Load(destination: Reg8.B, source: Reg8.A);

        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.NotZero, label: grabFrame);
        e.LoadImmediate(pair: Reg16.Hl, value: m_cursor.FrameAddresses[0]);
        m_fw.Oam.EmitDrawMetasprite(baseSlot: m_cursorSlot, spriteCount: m_cursor.FrameEntryCounts[0]);
        e.JumpAbsolute(label: drawDone);
        e.MarkLabel(label: grabFrame);
        e.LoadImmediate(pair: Reg16.Hl, value: m_cursor.FrameAddresses[1]);
        m_fw.Oam.EmitDrawMetasprite(baseSlot: m_cursorSlot, spriteCount: m_cursor.FrameEntryCounts[1]);
        e.MarkLabel(label: drawDone);
        e.Return();
    }

    // A fresh game: cleared state, the deterministic deal (init → shuffle → distribute), and the standard counts.
    private void EmitGameReset(Sm83Emitter e) {
        e.MarkLabel(label: m_subGameReset);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.WastePos);
        e.StoreAToAddress(address: SolitaireProtocol.Score);
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.Score + 1));
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.Score + 2));
        e.StoreAToAddress(address: SolitaireProtocol.CarryDepth);
        e.StoreAToAddress(address: SolitaireProtocol.CarrySrc);
        e.StoreAToAddress(address: SolitaireProtocol.CursorPos);
        m_undo.EmitReset();

        // Counts: stock 24, foundations 0, tableau 1..7; every tableau tail starts face-up.
        e.LoadAImmediate(value: 24);
        e.StoreAToAddress(address: SolitaireProtocol.CountsBase);
        e.XorA();

        for (var foundation = 0; (foundation < 4); foundation++) {
            e.StoreAToAddress(address: (ushort)(SolitaireProtocol.CountsBase + SolitaireProtocol.PileFoundationBase + foundation));
        }

        for (var tableau = 0; (tableau < 7); tableau++) {
            e.LoadAImmediate(value: (byte)(tableau + 1));
            e.StoreAToAddress(address: (ushort)(SolitaireProtocol.CountsBase + SolitaireProtocol.PileTableauBase + tableau));
        }

        e.LoadAImmediate(value: 1);

        for (var tableau = 0; (tableau < 7); tableau++) {
            e.StoreAToAddress(address: (ushort)(SolitaireProtocol.FaceUpBase + tableau));
        }

        // The deal: identity deck → Fisher–Yates (51 PRNG draws) → constant-offset distribution.
        CardDeck.EmitInitDeck(e: e, deckBase: SolitaireProtocol.DeckScratch);
        e.Call(label: m_subShuffle);

        var offset = 0;

        for (var tableau = 0; (tableau < 7); tableau++) {
            FrameworkKernel.EmitBlockCopy(
                emitter: e,
                sourceAddress: (ushort)(SolitaireProtocol.DeckScratch + offset),
                destinationAddress: (ushort)(SolitaireProtocol.PileBase + ((SolitaireProtocol.PileTableauBase + tableau) * SolitaireProtocol.PileStride)),
                byteCount: (ushort)(tableau + 1)
            );
            offset += (tableau + 1);
        }

        FrameworkKernel.EmitBlockCopy(
            emitter: e,
            sourceAddress: (ushort)(SolitaireProtocol.DeckScratch + offset),
            destinationAddress: SolitaireProtocol.PileBase,
            byteCount: 24
        );
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Shuffle);
        e.Return();
    }

    // Adds the packed-BCD delta in A to the three-byte score (carry-chained decimal adds).
    private void EmitScoreAdd(Sm83Emitter e) {
        e.MarkLabel(label: m_subScoreAdd);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.Score + 2));
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.Score + 2));
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.Score + 1));
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.Score + 1));
        e.LoadAFromAddress(address: SolitaireProtocol.Score);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: SolitaireProtocol.Score);
        e.Return();
    }

    // Subtracts the packed-BCD delta in A from the score (the undo path).
    private void EmitScoreSub(Sm83Emitter e) {
        e.MarkLabel(label: m_subScoreSub);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.Score + 2));
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.Score + 2));
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.Score + 1));
        e.ArithmeticImmediate(op: AluOp.SubtractWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.Score + 1));
        e.LoadAFromAddress(address: SolitaireProtocol.Score);
        e.ArithmeticImmediate(op: AluOp.SubtractWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: SolitaireProtocol.Score);
        e.Return();
    }

    // A (binary, ≤ 99) → A (packed BCD).
    private void EmitByteToBcd(Sm83Emitter e) {
        var tens = e.NewLabel();
        var done = e.NewLabel();

        e.MarkLabel(label: m_subByteToBcd);
        e.LoadImmediate(destination: Reg8.B, value: 0);
        e.MarkLabel(label: tens);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 10);
        e.JumpRelative(condition: Condition.Carry, label: done);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: 10);
        e.Increment(register: Reg8.B);
        e.JumpRelative(label: tens);
        e.MarkLabel(label: done);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Arithmetic(op: AluOp.Or, source: Reg8.C);
        e.Return();
    }

    // A := 1 when the carried run may drop on CursorPos, else 0. Reads the card records — rules stay data.
    private void EmitLegalDrop(Sm83Emitter e) {
        var dstEmpty = e.NewLabel();
        var haveDst = e.NewLabel();
        var srcWaste = e.NewLabel();
        var haveSrcIdx = e.NewLabel();
        var checkFoundation = e.NewLabel();
        var checkTableau = e.NewLabel();
        var tableauStack = e.NewLabel();
        var tableauStackCheck = e.NewLabel();
        var legal = e.NewLabel();
        var illegal = e.NewLabel();

        e.MarkLabel(label: m_subLegalDrop);

        // The destination: pile → TmpPile, kind → TmpFlag, top rank → IdxJ (0 = empty), suit → TmpDown, red → TmpSkip.
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Call(label: m_subPosRecord);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpFlag);
        e.Call(label: m_subGetCount);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: dstEmpty);
        e.Call(label: m_subTailCard);
        e.Call(label: m_subCardRecord);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRank);
        e.StoreAToAddress(address: SolitaireProtocol.IdxJ);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSuit);
        e.StoreAToAddress(address: SolitaireProtocol.TmpDown);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRed);
        e.StoreAToAddress(address: SolitaireProtocol.TmpSkip);
        e.JumpRelative(label: haveDst);
        e.MarkLabel(label: dstEmpty);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.IdxJ);
        e.MarkLabel(label: haveDst);

        // The moving card: the BOTTOM of the carried run (the waste's is the card above the split).
        e.LoadAFromAddress(address: SolitaireProtocol.CarrySrc);
        e.Call(label: m_subPosRecord);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireTables.KindWaste);
        e.JumpRelative(condition: Condition.Zero, label: srcWaste);
        e.Call(label: m_subGetCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(label: haveSrcIdx);
        e.MarkLabel(label: srcWaste);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Decrement(register: Reg8.A);
        e.MarkLabel(label: haveSrcIdx);
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.Call(label: m_subPileAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Call(label: m_subCardRecord);

        // Dispatch on the destination kind.
        e.LoadAFromAddress(address: SolitaireProtocol.TmpFlag);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireTables.KindFoundation);
        e.JumpRelative(condition: Condition.Zero, label: checkFoundation);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireTables.KindTableau);
        e.JumpRelative(condition: Condition.Zero, label: checkTableau);
        e.JumpAbsolute(label: illegal); // Stock/waste never accept a drop.

        // Foundation: single card; an ace opens, then same suit ascending.
        e.MarkLabel(label: checkFoundation);
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 1);
        e.JumpAbsolute(condition: Condition.NotZero, label: illegal);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxJ);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: tableauStack); // Reused as "non-empty foundation" check below.
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRank);
        e.ArithmeticImmediate(op: AluOp.Compare, value: CardDeck.RankAce);
        e.JumpRelative(condition: Condition.Zero, label: legal);
        e.JumpRelative(label: illegal);

        // Non-empty foundation: same suit, rank = top + 1.
        e.MarkLabel(label: tableauStack);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSuit);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpDown);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: illegal);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxJ);
        e.Increment(register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRank);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: legal);
        e.JumpRelative(label: illegal);

        // Tableau: a king opens an empty column; otherwise alternating colour, descending rank.
        e.MarkLabel(label: checkTableau);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxJ);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: tableauStackCheck);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRank);
        e.ArithmeticImmediate(op: AluOp.Compare, value: CardDeck.RankKing);
        e.JumpRelative(condition: Condition.Zero, label: legal);
        e.JumpRelative(label: illegal);

        // Non-empty tableau: opposite colour AND top rank = moving rank + 1.
        e.MarkLabel(label: tableauStackCheck);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpSkip);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: illegal);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpRank);
        e.Increment(register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.IdxJ);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: legal);
        e.JumpRelative(label: illegal);

        e.MarkLabel(label: legal);
        e.LoadAImmediate(value: 1);
        e.Return();
        e.MarkLabel(label: illegal);
        e.XorA();
        e.Return();
    }

    // Moves MoveCount cards from the tail of pile MoveSrc to the tail of pile MoveDst (order preserved) and
    // adjusts both counts. No face-up or score logic — the callers own that.
    private void EmitMoveBlock(Sm83Emitter e) {
        var copy = e.NewLabel();

        e.MarkLabel(label: m_subMoveBlock);

        // DE := &dst[dstCount].
        e.LoadAFromAddress(address: SolitaireProtocol.MoveDst);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subGetCount);
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.Call(label: m_subPileAddr);
        e.Push(pair: StackPair.Hl);

        // HL := &src[srcCount − count].
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subGetCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.Call(label: m_subPileAddr);
        e.Pop(pair: StackPair.De);

        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.MarkLabel(label: copy);
        e.LoadAFromHlIncrement();
        e.StoreAToDe();
        e.Increment(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: copy);

        // counts[src] −= count; counts[dst] += count.
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subCountAddr);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveDst);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subCountAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.Return();
    }

    // Performs the validated forward move (MoveSrc → MoveDst, MoveCount cards): the waste's single card moves with
    // a split shift, everything else block-moves; tableau face-up counts adjust, and an exposed face-down tail
    // flips (+5, recorded in MoveFlip/MoveScore for the undo record).
    private void EmitDoMove(Sm83Emitter e) {
        var blockMove = e.NewLabel();
        var shiftLoop = e.NewLabel();
        var noShift = e.NewLabel();
        var dstFace = e.NewLabel();
        var noSrcFace = e.NewLabel();
        var noFlip = e.NewLabel();
        var done = e.NewLabel();

        e.MarkLabel(label: m_subDoMove);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.NotZero, label: blockMove);

        // Waste → dst: append pile0[wastePos − 1] to the destination, then close the split.
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subPileAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCard);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveDst);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subGetCount);
        e.StoreAToAddress(address: SolitaireProtocol.TmpIdx);
        e.Call(label: m_subPileAddr);
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCard);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.Call(label: m_subCountAddr);
        e.Increment(register: Reg8.Memory);

        // Shift the stock half down over the removed card: arr[wastePos..count−1] → arr[wastePos−1..].
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noShift);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.PileBase >> 8));
        e.Decrement(register: Reg8.A);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: (byte)(SolitaireProtocol.PileBase >> 8));
        e.MarkLabel(label: shiftLoop);
        e.LoadAFromHlIncrement();
        e.StoreAToDe();
        e.Increment(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: shiftLoop);
        e.MarkLabel(label: noShift);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.CountsBase);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.WastePos);
        e.JumpAbsolute(label: dstFace);

        e.MarkLabel(label: blockMove);
        e.Call(label: m_subMoveBlock);

        // Source face-up bookkeeping (tableau only): faceUp −= count; a zero with cards left flips the tail (+5).
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PileTableauBase);
        e.JumpRelative(condition: Condition.Carry, label: noSrcFace);
        EmitFaceUpPointer(e: e, pileAddress: SolitaireProtocol.MoveSrc);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: noSrcFace);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subGetCount);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noFlip);
        EmitFaceUpPointer(e: e, pileAddress: SolitaireProtocol.MoveSrc);
        e.LoadImmediate(destination: Reg8.Memory, value: 1);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: SolitaireProtocol.MoveFlip);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveScore);
        e.ArithmeticImmediate(op: AluOp.Add, value: 0x05);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: SolitaireProtocol.MoveScore);
        e.MarkLabel(label: noFlip);
        e.MarkLabel(label: noSrcFace);

        // Destination face-up bookkeeping (tableau only): faceUp += count.
        e.MarkLabel(label: dstFace);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveDst);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PileTableauBase);
        e.JumpRelative(condition: Condition.Carry, label: done);
        EmitFaceUpPointer(e: e, pileAddress: SolitaireProtocol.MoveDst);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.MarkLabel(label: done);
        e.Return();
    }

    // HL := &faceUp[pile − 5], the pile id read from the given address. Clobbers A.
    private static void EmitFaceUpPointer(Sm83Emitter e, ushort pileAddress) {
        e.LoadAFromAddress(address: pileAddress);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: SolitaireProtocol.PileTableauBase);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(SolitaireProtocol.FaceUpBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.FaceUpBase >> 8));
    }

    // The A button: draw at the stock, pick up / cycle the carry depth, or attempt the drop.
    private void EmitActionA(Sm83Emitter e) {
        var carrying = e.NewLabel();
        var pickWaste = e.NewLabel();
        var pickPile = e.NewLabel();
        var carryOne = e.NewLabel();
        var cycle = e.NewLabel();
        var cycleTableau = e.NewLabel();
        var wrapDepth = e.NewLabel();
        var cancel = e.NewLabel();
        var error = e.NewLabel();

        e.MarkLabel(label: m_subActionA);
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.NotZero, label: carrying);

        // Pick up (or draw): dispatch on the position's kind.
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Call(label: m_subPosRecord);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireTables.KindStock);
        e.JumpAbsolute(condition: Condition.Zero, label: m_subDrawAction);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireTables.KindWaste);
        e.JumpRelative(condition: Condition.Zero, label: pickWaste);
        e.JumpRelative(label: pickPile);

        e.MarkLabel(label: pickWaste);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: error);
        e.JumpRelative(label: carryOne);

        e.MarkLabel(label: pickPile);
        e.Call(label: m_subGetCount);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: error);

        e.MarkLabel(label: carryOne);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: SolitaireProtocol.CarryDepth);
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.StoreAToAddress(address: SolitaireProtocol.CarrySrc);
        e.Return();

        // Carrying already: same position cycles the depth (tableau) or cancels; elsewhere attempts the drop.
        e.MarkLabel(label: carrying);
        e.LoadAFromAddress(address: SolitaireProtocol.CarrySrc);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: cycle);
        e.JumpAbsolute(label: m_subDropAction);

        e.MarkLabel(label: cycle);
        e.LoadAFromAddress(address: SolitaireProtocol.CarrySrc);
        e.Call(label: m_subPosRecord);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireTables.KindTableau);
        e.JumpRelative(condition: Condition.Zero, label: cycleTableau);
        e.JumpRelative(label: cancel);

        // Depth cycles 1 → faceUp → back to 1 (all within the always-valid face-up run).
        e.MarkLabel(label: cycleTableau);
        e.LoadAFromAddress(address: SolitaireProtocol.CarrySrc);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: SolitaireProtocol.PositionTableauBase);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(SolitaireProtocol.FaceUpBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.FaceUpBase >> 8));
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: wrapDepth);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.CarryDepth);
        e.Return();
        e.MarkLabel(label: wrapDepth);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: SolitaireProtocol.CarryDepth);
        e.Return();

        e.MarkLabel(label: cancel);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.CarryDepth);
        e.Return();

        e.MarkLabel(label: error);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Error);
        e.Return();
    }

    // The stock draw: advance the split, or recycle it when the stock is spent. Both push undo records.
    private void EmitDrawAction(Sm83Emitter e) {
        var recycle = e.NewLabel();
        var empty = e.NewLabel();

        e.MarkLabel(label: m_subDrawAction);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: empty);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: recycle);

        // Draw one: wastePos++.
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.WastePos);
        EmitStageSimpleUndo(e: e, op: 1);
        m_undo.EmitPush();
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Flip);
        e.Call(label: m_subBoardRepaint);
        e.Return();

        // Recycle: the split returns to zero (the same pass order replays).
        e.MarkLabel(label: recycle);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.WastePos);
        EmitStageSimpleUndo(e: e, op: 2);
        m_undo.EmitPush();
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Shuffle);
        e.Call(label: m_subBoardRepaint);
        e.Return();

        e.MarkLabel(label: empty);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Error);
        e.Return();
    }

    // Stages a draw/recycle undo record (op; every other field zero).
    private static void EmitStageSimpleUndo(Sm83Emitter e, byte op) {
        e.LoadAImmediate(value: op);
        e.StoreAToAddress(address: SolitaireProtocol.UndoStaging);
        e.XorA();

        for (var index = 1; (index < SolitaireProtocol.UndoStride); index++) {
            e.StoreAToAddress(address: (ushort)(SolitaireProtocol.UndoStaging + index));
        }
    }

    // The drop attempt: validate, score, move, record, repaint, and check the win.
    private void EmitDropAction(Sm83Emitter e) {
        var illegal = e.NewLabel();
        var noFoundationScore = e.NewLabel();
        var noWasteScore = e.NewLabel();
        var scored = e.NewLabel();

        e.MarkLabel(label: m_subDropAction);
        e.Call(label: m_subLegalDrop);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: illegal);

        // The move parameters from the carry (position → pile via the records).
        e.LoadAFromAddress(address: SolitaireProtocol.CarrySrc);
        e.Call(label: m_subPosRecord);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.MoveSrc);
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Call(label: m_subPosRecord);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.MoveDst);
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.StoreAToAddress(address: SolitaireProtocol.MoveCount);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.MoveFlip);
        e.StoreAToAddress(address: SolitaireProtocol.MoveScore);

        // The base score: +10 onto a foundation; +5 waste → tableau (the flip's +5 rides EmitDoMove).
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Call(label: m_subPosRecord);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireTables.KindFoundation);
        e.JumpRelative(condition: Condition.NotZero, label: noFoundationScore);
        e.LoadAImmediate(value: 0x10);
        e.StoreAToAddress(address: SolitaireProtocol.MoveScore);
        e.JumpRelative(label: scored);
        e.MarkLabel(label: noFoundationScore);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: noWasteScore);
        e.LoadAImmediate(value: 0x05);
        e.StoreAToAddress(address: SolitaireProtocol.MoveScore);
        e.MarkLabel(label: noWasteScore);
        e.MarkLabel(label: scored);

        e.Call(label: m_subDoMove);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveScore);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.Call(condition: Condition.NotZero, label: m_subScoreAdd);

        // The undo record: op 0 + the move facts.
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.UndoStaging);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc);
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 1));
        e.LoadAFromAddress(address: SolitaireProtocol.MoveDst);
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 2));
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 3));
        e.LoadAFromAddress(address: SolitaireProtocol.MoveFlip);
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 4));
        e.LoadAFromAddress(address: SolitaireProtocol.MoveScore);
        e.StoreAToAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 5));
        m_undo.EmitPush();

        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.CarryDepth);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Place);
        e.Call(label: m_subBoardRepaint);
        e.Call(label: m_subWinCheck);
        e.Return();

        e.MarkLabel(label: illegal);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Error);
        e.Return();
    }

    // The undo: pop the newest record and reverse it (score included), or buzz when the ring is empty.
    private void EmitUndoApply(Sm83Emitter e) {
        var haveRecord = e.NewLabel();
        var undoDraw = e.NewLabel();
        var undoRecycle = e.NewLabel();
        var undoMove = e.NewLabel();
        var wasteReturn = e.NewLabel();
        var shiftUp = e.NewLabel();
        var noShift = e.NewLabel();
        var srcFace = e.NewLabel();
        var flipBack = e.NewLabel();
        var faceDone = e.NewLabel();
        var noDstFace = e.NewLabel();
        var applyScore = e.NewLabel();
        var finish = e.NewLabel();

        e.MarkLabel(label: m_subUndoApply);
        m_undo.EmitPop();
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: haveRecord);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Error);
        e.Return();

        e.MarkLabel(label: haveRecord);
        e.LoadAFromAddress(address: SolitaireProtocol.UndoStaging);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 1);
        e.JumpRelative(condition: Condition.Zero, label: undoDraw);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpRelative(condition: Condition.Zero, label: undoRecycle);
        e.JumpRelative(label: undoMove);

        e.MarkLabel(label: undoDraw);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.WastePos);
        e.JumpAbsolute(label: finish);

        e.MarkLabel(label: undoRecycle);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.StoreAToAddress(address: SolitaireProtocol.WastePos);
        e.JumpAbsolute(label: finish);

        // A move: send the record's cards back from its dst tail to its src tail.
        e.MarkLabel(label: undoMove);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 1));
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: wasteReturn);

        // Reverse block move: MoveSrc := record dst, MoveDst := record src.
        e.StoreAToAddress(address: SolitaireProtocol.MoveDst);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 2));
        e.StoreAToAddress(address: SolitaireProtocol.MoveSrc);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 3));
        e.StoreAToAddress(address: SolitaireProtocol.MoveCount);
        e.Call(label: m_subMoveBlock);

        // Face-up reversal: dst-of-record loses, src-of-record regains (a flip returns face-down).
        e.LoadAFromAddress(address: SolitaireProtocol.MoveSrc); // The record's dst.
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PileTableauBase);
        e.JumpRelative(condition: Condition.Carry, label: srcFace);
        EmitFaceUpPointer(e: e, pileAddress: SolitaireProtocol.MoveSrc);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.MarkLabel(label: srcFace);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveDst); // The record's src.
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PileTableauBase);
        e.JumpRelative(condition: Condition.Carry, label: faceDone);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 4));
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: flipBack);
        EmitFaceUpPointer(e: e, pileAddress: SolitaireProtocol.MoveDst);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.JumpRelative(label: faceDone);
        e.MarkLabel(label: flipBack);
        EmitFaceUpPointer(e: e, pileAddress: SolitaireProtocol.MoveDst);
        e.LoadAFromAddress(address: SolitaireProtocol.MoveCount);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.MarkLabel(label: faceDone);
        e.JumpAbsolute(label: applyScore);

        // A waste move returns: reopen the split and put the card back at it.
        e.MarkLabel(label: wasteReturn);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 2));
        e.StoreAToAddress(address: SolitaireProtocol.TmpPile);
        e.Call(label: m_subTailCard);
        e.StoreAToAddress(address: SolitaireProtocol.TmpCard);
        e.Call(label: m_subCountAddr);
        e.Decrement(register: Reg8.Memory);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 2));
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PileTableauBase);
        e.JumpRelative(condition: Condition.Carry, label: shiftUp);
        EmitFaceUpPointer(e: e, pileAddress: (ushort)(SolitaireProtocol.UndoStaging + 2));
        e.Decrement(register: Reg8.Memory);

        // Shift the stock half up: arr[wastePos..count−1] → arr[wastePos+1..], backwards.
        e.MarkLabel(label: shiftUp);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noShift);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: (byte)(SolitaireProtocol.PileBase >> 8));
        e.Decrement(register: Reg8.A);
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.PileBase >> 8));

        var shiftLoop = e.NewLabel();

        e.MarkLabel(label: shiftLoop);
        e.LoadAFromHlDecrement();
        e.StoreAToDe();
        e.Decrement(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: shiftLoop);
        e.MarkLabel(label: noShift);

        // arr[wastePos] = the returned card; count++ and wastePos++ reopen the split above it.
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.PileBase >> 8));
        e.LoadAFromAddress(address: SolitaireProtocol.TmpCard);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.CountsBase);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.CountsBase);
        e.LoadAFromAddress(address: SolitaireProtocol.WastePos);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.WastePos);

        e.MarkLabel(label: applyScore);
        e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.UndoStaging + 5));
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.Call(condition: Condition.NotZero, label: m_subScoreSub);

        e.MarkLabel(label: finish);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Undo);
        e.Call(label: m_subBoardRepaint);
        e.Return();
    }

    // All four foundations at thirteen → the win.
    private void EmitWinCheck(Sm83Emitter e) {
        var notWon = e.NewLabel();

        e.MarkLabel(label: m_subWinCheck);

        for (var foundation = 0; (foundation < 4); foundation++) {
            e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.CountsBase + SolitaireProtocol.PileFoundationBase + foundation));
            e.ArithmeticImmediate(op: AluOp.Compare, value: 13);
            e.JumpRelative(condition: Condition.NotZero, label: notWon);
        }

        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateWin);
        e.MarkLabel(label: notWon);
        e.Return();
    }

    // Inserts the current score + entered initials into the mirror table (sorted, ties keep the older entry) —
    // the Brickfall insertion, over the same shared score-table shape.
    private void EmitHiInsert(Sm83Emitter e) {
        var insertGo = e.NewLabel();
        var noShift = e.NewLabel();
        var shiftLoop = e.NewLabel();

        e.MarkLabel(label: m_subHiInsert);

        for (var slot = 0; (slot < (SolitaireProtocol.HiScoreEntryCount - 1)); slot++) {
            var take = e.NewLabel();
            var skip = e.NewLabel();
            var entryScore = (ushort)(SolitaireProtocol.HiScoreMirror + (slot * SolitaireProtocol.HiScoreEntryByteCount) + 3);

            for (var index = 0; (index < 3); index++) {
                e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.Score + index));
                e.Load(destination: Reg8.B, source: Reg8.A);
                e.LoadAFromAddress(address: (ushort)(entryScore + index));
                e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
                e.JumpRelative(condition: Condition.Carry, label: take);

                if (index < 2) {
                    e.JumpRelative(condition: Condition.NotZero, label: skip);
                }
                else {
                    e.JumpRelative(label: skip);
                }
            }

            e.MarkLabel(label: take);
            e.LoadImmediate(destination: Reg8.C, value: (byte)(slot * SolitaireProtocol.HiScoreEntryByteCount));
            e.JumpAbsolute(label: insertGo);
            e.MarkLabel(label: skip);
        }

        e.LoadImmediate(destination: Reg8.C, value: (byte)((SolitaireProtocol.HiScoreEntryCount - 1) * SolitaireProtocol.HiScoreEntryByteCount));
        e.MarkLabel(label: insertGo);

        e.LoadAImmediate(value: (byte)((SolitaireProtocol.HiScoreEntryCount - 1) * SolitaireProtocol.HiScoreEntryByteCount));
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(condition: Condition.Zero, label: noShift);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadImmediate(pair: Reg16.Hl, value: (ushort)(SolitaireProtocol.HiScoreMirror + (4 * SolitaireProtocol.HiScoreEntryByteCount) - 1));
        e.LoadImmediate(pair: Reg16.De, value: (ushort)(SolitaireProtocol.HiScoreMirror + (5 * SolitaireProtocol.HiScoreEntryByteCount) - 1));
        e.MarkLabel(label: shiftLoop);
        e.LoadAFromHlDecrement();
        e.StoreAToDe();
        e.Decrement(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: shiftLoop);
        e.MarkLabel(label: noShift);

        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(SolitaireProtocol.HiScoreMirror & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(SolitaireProtocol.HiScoreMirror >> 8));

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.EntryGlyphs + index));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            e.StoreAToHlIncrement();
        }

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.Score + index));
            e.StoreAToHlIncrement();
        }

        e.Return();
    }

    // The shared per-frame play simulation (Play ticks it directly; Attract ticks it under the input script):
    // table-driven cursor navigation, the A action, B (cancel / undo), and the cursor sprite.
    private void EmitPlayCore(Sm83Emitter e) {
        var noLeft = e.NewLabel();
        var noRight = e.NewLabel();
        var noUp = e.NewLabel();
        var noDown = e.NewLabel();
        var noA = e.NewLabel();
        var noB = e.NewLabel();
        var undoPath = e.NewLabel();
        var bDone = e.NewLabel();

        e.MarkLabel(label: m_subPlayCore);

        // Left/Right: always the record's nav fields.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 1, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noLeft);
        EmitNavigate(e: e, field: SolitaireTables.PositionFieldNavLeft);
        e.MarkLabel(label: noLeft);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        EmitNavigate(e: e, field: SolitaireTables.PositionFieldNavRight);
        e.MarkLabel(label: noRight);

        // Up leaves the tableau; Down leaves the top row (the vertical field is one-directional per kind).
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 2, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noUp);
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PositionTableauBase);
        e.JumpRelative(condition: Condition.Carry, label: noUp);
        EmitNavigate(e: e, field: SolitaireTables.PositionFieldNavVertical);
        e.MarkLabel(label: noUp);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 3, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noDown);
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SolitaireProtocol.PositionTableauBase);
        e.JumpRelative(condition: Condition.NoCarry, label: noDown);
        EmitNavigate(e: e, field: SolitaireTables.PositionFieldNavVertical);
        e.MarkLabel(label: noDown);

        // A: draw / pick / cycle / drop.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noA);
        e.Call(label: m_subActionA);
        e.MarkLabel(label: noA);

        // B: cancel the carry, else undo.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 5, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noB);
        e.LoadAFromAddress(address: SolitaireProtocol.CarryDepth);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: undoPath);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.CarryDepth);
        e.JumpRelative(label: bDone);
        e.MarkLabel(label: undoPath);
        e.Call(label: m_subUndoApply);
        e.MarkLabel(label: bDone);
        e.MarkLabel(label: noB);

        e.Call(label: m_subCursorSprite);
        e.Return();
    }

    // CursorPos := the current record's nav field. Clobbers A, C, D, E, H, L.
    private void EmitNavigate(Sm83Emitter e, int field) {
        e.LoadAFromAddress(address: SolitaireProtocol.CursorPos);
        e.Call(label: m_subPosRecord);

        for (var step = 0; (step < field); step++) {
            e.Increment(pair: Reg16.Hl);
        }

        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: SolitaireProtocol.CursorPos);
    }

    // ==== The seven states. ==============================================================================================

    private void EmitTitleEnter(Sm83Emitter e) {
        // The trio's title-always-quiet invariant: whatever brought us here (boot, abandon, score entry), silence.
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_fw.Input.EmitScriptStop();
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_titleMap.Address);
        EmitAttributesPaint(e: e, attributes: m_titleAttributes);
        m_menu.EmitEnterDraw(e: e);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.IdleTimer);
        e.StoreAToAddress(address: SolitaireProtocol.IdleTimerHigh);
    }

    private void EmitTitleTick(Sm83Emitter e) {
        var stay = e.NewLabel();

        m_menu.EmitTick(e: e, emitConfirm: (emitter, index) => {
            if (index == 0) {
                // The D4 input-entropy seed — the frame counter is sampled at THIS confirm edge — then a fresh deal.
                m_fw.Prng.EmitSeedFromFrameCounter();
                emitter.Call(label: m_subGameReset);
                m_fw.States.EmitRequestState(id: SolitaireProtocol.StatePlay);
                emitter.Return();
            }
            else {
                m_fw.States.EmitRequestState(id: SolitaireProtocol.StateHighScores);
                emitter.Return();
            }
        });
        EmitIdleAdvance(e: e, stayLabel: stay);
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateAttract);
        e.MarkLabel(label: stay);
    }

    private void EmitAttractEnter(Sm83Emitter e) {
        // The constant seed makes the scripted deal identical every time; attract never writes SRAM.
        e.LoadAImmediate(value: AttractSeedLow);
        e.StoreAToAddress(address: FrameworkMemoryMap.PrngState);
        e.LoadAImmediate(value: AttractSeedHigh);
        e.StoreAToAddress(address: FrameworkMemoryMap.PrngStateHigh);
        e.Call(label: m_subGameReset);
        m_fw.Input.EmitScriptStart(script: m_attractScript);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitAttributesPaint(e: e, attributes: m_playAttributes);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.Call(label: m_subBoardRepaint);
        e.Call(label: m_subCursorSprite);
    }

    private void EmitAttractTick(Sm83Emitter e) {
        var noReal = e.NewLabel();
        var running = e.NewLabel();

        // Any REAL press hands the machine back to the title.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputRaw);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noReal);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: noReal);
        e.LoadAFromAddress(address: FrameworkMemoryMap.ScriptEnded);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: running);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: running);
        e.Call(label: m_subPlayCore);
    }

    private void EmitHighScoresEnter(Sm83Emitter e) {
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        EmitAttributesPaint(e: e, attributes: null);
        m_fw.Text.EmitPrintDirect(text: m_strHiScores, row: 1, column: 5);

        for (var entry = 0; (entry < SolitaireProtocol.HiScoreEntryCount); entry++) {
            var row = (3 + (entry * 2));
            var entryBase = (ushort)(SolitaireProtocol.HiScoreMirror + (entry * SolitaireProtocol.HiScoreEntryByteCount));

            for (var glyph = 0; (glyph < 3); glyph++) {
                e.LoadAFromAddress(address: (ushort)(entryBase + glyph));
                e.StoreAToAddress(address: Hw.MapCell(row: row, column: (4 + glyph)));
            }

            m_fw.Text.EmitPrintBcdDirect(bcdAddress: (ushort)(entryBase + 3), byteCount: 3, row: row, column: 9);
        }

        m_fw.Text.EmitPrintDirect(text: m_strStreak, row: 14, column: 3);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: SolitaireProtocol.StreakMirror, byteCount: 1, row: 14, column: 11);
        m_fw.Text.EmitPrintDirect(text: m_strBest, row: 16, column: 3);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: SolitaireProtocol.BestStreakMirror, byteCount: 1, row: 16, column: 11);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.IdleTimer);
        e.StoreAToAddress(address: SolitaireProtocol.IdleTimerHigh);
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
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateTitle);
        e.MarkLabel(label: stay);
    }

    private void EmitPlayEnter(Sm83Emitter e) {
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitAttributesPaint(e: e, attributes: m_playAttributes);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.Call(label: m_subBoardRepaint);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicLoop);
        e.Call(label: m_subCursorSprite);
    }

    private void EmitPlayTick(Sm83Emitter e) {
        var noPause = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noPause);
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StatePause);
        e.Return();
        e.MarkLabel(label: noPause);
        e.Call(label: m_subPlayCore);
    }

    private void EmitPauseEnter(Sm83Emitter e) {
        ArgumentNullException.ThrowIfNull(e);
        m_fw.Text.EmitPrintQueued(text: m_strPause, row: 9, column: 7);
    }

    private void EmitPauseTick(Sm83Emitter e) {
        var resume = e.NewLabel();
        var noAbandon = e.NewLabel();

        // The simulation halts BY CONSTRUCTION here; START resumes, SELECT abandons (breaking the streak).
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resume);
        e.TestBit(bit: 6, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noAbandon);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.StreakMirror);
        m_fw.Save.EmitStore();
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateTitle);
        e.Return();
        e.MarkLabel(label: resume);
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StatePlay);
        e.MarkLabel(label: noAbandon);
    }

    private void EmitWinEnter(Sm83Emitter e) {
        var capStreak = e.NewLabel();
        var noBest = e.NewLabel();

        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_fw.Text.EmitPrintQueued(text: m_strYouWin, row: 9, column: 6);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Win);

        // streak++ (BCD, capped at 99); best = max(best, streak); both persist immediately.
        e.LoadAFromAddress(address: SolitaireProtocol.StreakMirror);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.DecimalAdjustA();
        e.JumpRelative(condition: Condition.NoCarry, label: capStreak);
        e.LoadAImmediate(value: 0x99);
        e.MarkLabel(label: capStreak);
        e.StoreAToAddress(address: SolitaireProtocol.StreakMirror);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.BestStreakMirror);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: noBest);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.StoreAToAddress(address: SolitaireProtocol.BestStreakMirror);
        e.MarkLabel(label: noBest);
        m_fw.Save.EmitStore();

        // The 128-bit meta-victory converge: a completed game (all foundations built) writes this cabinet's host-seeded
        // share into the top-16 SRAM win region (the room XORs it across cabinets to drive the editor reveal).
        m_fw.Victory.EmitStoreShare();

        e.LoadAImmediate(value: WinFrames);
        e.StoreAToAddress(address: SolitaireProtocol.WinTimer);

        // The waterfall's opening card: launched leftward off the foundations, exactly where the win happened.
        e.LoadAImmediate(value: 24);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardY);
        e.LoadAImmediate(value: 120);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardX);
        e.LoadAImmediate(value: unchecked((byte)-3));
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVy);
        e.LoadAImmediate(value: unchecked((byte)-2));
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVx);
        e.XorA();
        e.StoreAToAddress(address: SolitaireProtocol.WinCardPhase);
    }

    private void EmitWinTick(Sm83Emitter e) {
        var resolve = e.NewLabel();
        var qualify = e.NewLabel();
        var noQualify = e.NewLabel();
        var entry4Score = (ushort)(SolitaireProtocol.HiScoreMirror + ((SolitaireProtocol.HiScoreEntryCount - 1) * SolitaireProtocol.HiScoreEntryByteCount) + 3);

        e.Call(label: m_subWinCard);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.LoadAFromAddress(address: SolitaireProtocol.WinTimer);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.WinTimer);
        e.JumpRelative(condition: Condition.Zero, label: resolve);
        e.Return();

        // A score STRICTLY greater than the table's fifth entry earns initials entry; anything else → the scores.
        // The waterfall's card rides slots outside the cursor range, so it hides here, on the only exit path.
        e.MarkLabel(label: resolve);
        m_fw.Oam.EmitHideRange(baseSlot: m_winCardSlot, count: 4);

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(SolitaireProtocol.Score + index));
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: (ushort)(entry4Score + index));
            e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            e.JumpRelative(condition: Condition.Carry, label: qualify);

            if (index < 2) {
                e.JumpRelative(condition: Condition.NotZero, label: noQualify);
            }
        }

        e.MarkLabel(label: noQualify);
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateHighScores);
        e.Return();

        e.MarkLabel(label: qualify);
        m_fw.States.EmitRequestState(id: SolitaireProtocol.StateScoreEntry);
    }

    // ==== The win waterfall (the whimsy). ===============================================================================
    //
    // The classic celebration, sized for the brick: one 16×16 card-back metasprite launches off the foundation row,
    // falls under integer gravity (velocity gains a pixel-per-frame every fourth frame), bounces off the floor with
    // decaying energy, rebounds from the side walls, and — when a bounce dies — relaunches from a PRNG-chosen column
    // with a fresh arc. The flip-attribute cycle tumbles it as it flies. Pure output: the PRNG draws happen only
    // inside the win state (after the save landed), so the celebration is replay-deterministic and touches no
    // gameplay state; the verifier reads WinCardY across frames to prove it is alive.
    private void EmitWinCard(Sm83Emitter e) {
        var checkFloor = e.NewLabel();
        var noGravity = e.NewLabel();
        var floorDone = e.NewLabel();
        var relaunch = e.NewLabel();
        var vxNegative = e.NewLabel();
        var vxDone = e.NewLabel();
        var noLeft = e.NewLabel();
        var noRight = e.NewLabel();
        var attr1 = e.NewLabel();
        var attr2 = e.NewLabel();
        var attr3 = e.NewLabel();
        var attrDone = e.NewLabel();

        e.MarkLabel(label: m_subWinCard);

        // phase++; gravity ticks every fourth frame.
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardPhase);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardPhase);
        e.ArithmeticImmediate(op: AluOp.And, value: 3);
        e.JumpRelative(condition: Condition.NotZero, label: noGravity);
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardVy);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVy);
        e.MarkLabel(label: noGravity);

        // y += vy (two's complement), then place the result against the ceiling and the floor.
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardVy);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardY);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardY);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 200);
        e.JumpRelative(condition: Condition.Carry, label: checkFloor);

        // Wrapped past the top: drop the card back into play, falling gently.
        e.LoadAImmediate(value: 8);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardY);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVy);
        e.JumpRelative(label: floorDone);

        e.MarkLabel(label: checkFloor);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 120);
        e.JumpRelative(condition: Condition.Carry, label: floorDone);

        // On the floor, falling: bounce with two pixels of energy lost — or, once the bounce dies, relaunch.
        e.LoadAImmediate(value: 120);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardY);
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardVy);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.Carry, label: relaunch);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAImmediate(value: 2);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVy);
        e.JumpRelative(label: floorDone);

        // A fresh arc: a PRNG column across the board, launched upward at a PRNG strength, drifting either way.
        e.MarkLabel(label: relaunch);
        m_fw.Prng.EmitNext();
        e.ArithmeticImmediate(op: AluOp.And, value: 0x3F);
        e.ArithmeticImmediate(op: AluOp.Add, value: 40);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardX);
        e.LoadAImmediate(value: 24);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardY);
        m_fw.Prng.EmitNext();
        e.ArithmeticImmediate(op: AluOp.And, value: 3);
        e.ArithmeticImmediate(op: AluOp.Add, value: 3);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.XorA();
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVy);
        m_fw.Prng.EmitNext();
        e.ArithmeticImmediate(op: AluOp.And, value: 1);
        e.JumpRelative(condition: Condition.NotZero, label: vxNegative);
        e.LoadAImmediate(value: 2);
        e.JumpRelative(label: vxDone);
        e.MarkLabel(label: vxNegative);
        e.LoadAImmediate(value: unchecked((byte)-2));
        e.MarkLabel(label: vxDone);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVx);
        e.MarkLabel(label: floorDone);

        // x += vx; the side walls pin the card and reverse its drift.
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardVx);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardX);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardX);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 9);
        e.JumpRelative(condition: Condition.NoCarry, label: noLeft);
        e.LoadAImmediate(value: 9);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardX);
        e.LoadAImmediate(value: 2);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVx);
        e.MarkLabel(label: noLeft);
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardX);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 144);
        e.JumpRelative(condition: Condition.Carry, label: noRight);
        e.LoadAImmediate(value: 143);
        e.StoreAToAddress(address: SolitaireProtocol.WinCardX);
        e.LoadAImmediate(value: unchecked((byte)-2));
        e.StoreAToAddress(address: SolitaireProtocol.WinCardVx);
        e.MarkLabel(label: noRight);

        // The tumble: the flip-attribute cycle turns the card over as it flies ((phase & 0x18) → none/X/XY/Y).
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardPhase);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x18);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 0x08);
        e.JumpRelative(condition: Condition.Zero, label: attr1);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 0x10);
        e.JumpRelative(condition: Condition.Zero, label: attr2);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 0x18);
        e.JumpRelative(condition: Condition.Zero, label: attr3);
        e.LoadImmediate(destination: Reg8.E, value: 0x00);
        e.JumpRelative(label: attrDone);
        e.MarkLabel(label: attr1);
        e.LoadImmediate(destination: Reg8.E, value: 0x20);
        e.JumpRelative(label: attrDone);
        e.MarkLabel(label: attr2);
        e.LoadImmediate(destination: Reg8.E, value: 0x60);
        e.JumpRelative(label: attrDone);
        e.MarkLabel(label: attr3);
        e.LoadImmediate(destination: Reg8.E, value: 0x40);
        e.MarkLabel(label: attrDone);

        // The four hardware sprites of the 2×2 card back ((y+16, x+8) is OAM's top-left corner).
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardY);
        e.ArithmeticImmediate(op: AluOp.Add, value: 16);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: SolitaireProtocol.WinCardX);
        e.ArithmeticImmediate(op: AluOp.Add, value: 8);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: CardTables.TileBackBase);
        m_fw.Oam.EmitSetSprite(slot: m_winCardSlot);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Add, value: 8);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: (byte)(CardTables.TileBackBase + 1));
        m_fw.Oam.EmitSetSprite(slot: (m_winCardSlot + 1));
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.ArithmeticImmediate(op: AluOp.Add, value: 8);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Subtract, value: 8);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: (byte)(CardTables.TileBackBase + 2));
        m_fw.Oam.EmitSetSprite(slot: (m_winCardSlot + 2));
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Add, value: 8);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: (byte)(CardTables.TileBackBase + 3));
        m_fw.Oam.EmitSetSprite(slot: (m_winCardSlot + 3));
        e.Return();
    }

    private void EmitScoreEntryEnter(Sm83Emitter e) {
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_pad.EmitEnterReset(e: e);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        EmitAttributesPaint(e: e, attributes: null);
        m_fw.Text.EmitPrintDirect(text: m_strNewHigh, row: 4, column: 3);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: SolitaireProtocol.Score, byteCount: 3, row: 6, column: 7);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }

    private void EmitScoreEntryTick(Sm83Emitter e) {
        m_pad.EmitTick(e: e, emitConfirm: emitter => {
            emitter.Call(label: m_subHiInsert);
            m_fw.Save.EmitStore();
            m_fw.States.EmitRequestState(id: SolitaireProtocol.StateHighScores);
        });
    }

    // ==== Small emission helpers. ========================================================================================

    // Paints the attribute bank (VRAM bank 1) from a linked table, or resets it to palette 0 — emitted only when
    // any screen actually carries attributes (with no baked art anywhere, the boot's clear is never disturbed).
    private void EmitAttributesPaint(Sm83Emitter e, RomTable? attributes) {
        if ((m_titleAttributes is null) && (m_playAttributes is null)) {
            return;
        }

        e.LoadAImmediate(value: 0x01);
        e.StoreAToHighPage(port: Hw.PortVramBank);

        if (attributes is { } table) {
            FrameworkKernel.EmitBlockCopy(emitter: e, sourceAddress: table.Address, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x0400);
        }
        else {
            FrameworkKernel.EmitBlockFill(emitter: e, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x0400, value: 0x00);
        }

        e.XorA();
        e.StoreAToHighPage(port: Hw.PortVramBank);
    }

    // The shared idle counter: += 1; control FALLS THROUGH when it reaches exactly 600, and jumps to the caller's
    // stay label otherwise (the enters reset it, so the equality fires exactly once).
    private static void EmitIdleAdvance(Sm83Emitter e, int stayLabel) {
        e.LoadAFromAddress(address: SolitaireProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.StoreAToAddress(address: SolitaireProtocol.IdleTimer);
        e.LoadAFromAddress(address: SolitaireProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.StoreAToAddress(address: SolitaireProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdHigh);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
        e.LoadAFromAddress(address: SolitaireProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdLow);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
    }
}
