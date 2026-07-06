using Puck.Demo.Forge.Cards;
using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The five-star five-card-draw Poker: a full seven-state game — title menu, scripted attract (the AI plays all
/// four seats over a constant seed), battery-backed best-stacks records, play, pause, the session-end card, and
/// initials entry — built ON the SM83 game framework and the shared card layer. The table seats the player and
/// three data-table AI opponents; each hand runs ante → the card layer's Fisher–Yates deal (seeded from pure input
/// entropy at the title's confirm edge — same press frame, bit-identical session) → fixed-limit betting → a draw
/// phase → a second betting round → showdown with full hand evaluation, the rankings and personalities all manifest
/// data. Chips are packed BCD; every AI choice flows through the one <see cref="PokerProtocol.DecisionAction"/>
/// seam so the follow-on link arc can substitute remote actions without restructuring the table.
/// </summary>
internal sealed class PokerGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;
    // The idle threshold before the title/high-score screens fall into attract/back to title: 600 frames (0x0258).
    private const byte IdleThresholdLow = 0x58;
    private const byte IdleThresholdHigh = 0x02;
    // The attract loop's constant seed (attract is scripted; its table must play out the same every time).
    private const byte AttractSeedLow = 0x34;
    private const byte AttractSeedHigh = 0x12;

    private readonly GameFramework m_fw;
    private readonly CardMenu m_menu;
    private readonly CardInitialsPad m_pad;
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
    private readonly RomRecords m_personalities;
    private readonly RomTable m_categoryTable;
    private readonly RomTable m_strengthBases;
    private readonly RomTable m_attractScript;
    private readonly RomTable m_strPause;
    private readonly RomTable m_strFold;
    private readonly RomTable m_strOut;
    private readonly RomTable m_strMenuClear;
    private readonly RomTable m_strAmountClear;
    private readonly RomTable[] m_strNames;
    private readonly RomTable[] m_strVerbs;
    private readonly RomTable m_strWins;
    private readonly RomTable m_strDraws;
    private readonly RomTable[] m_strMenuOpen;
    private readonly RomTable[] m_strMenuFacing;
    private readonly RomTable[] m_strCategories;
    private readonly RomTable m_strBusted;
    private readonly RomTable m_strCleared;
    private readonly RomTable m_strNewHigh;
    private readonly RomTable m_strHiScores;
    private readonly RomTable m_strHandsWon;
    private readonly RomTable m_strTopPot;

    private readonly int m_subCardRecord;
    private readonly int m_subRowColAddr;
    private readonly int m_subCellOffset;
    private readonly int m_subWriteCell;
    private readonly int m_subWriteFeltCell;
    private readonly int m_subFeltRestore;
    private readonly int m_subDrawCardAt;
    private readonly int m_subDrawStrip;
    private readonly int m_subHandAddr;
    private readonly int m_subEvalAddr;
    private readonly int m_subBankrollAddr;
    private readonly int m_subSeatBroke;
    private readonly int m_subEvalSeat;
    private readonly int m_subDecide;
    private readonly int m_subChipPay;
    private readonly int m_subPotToWinner;
    private readonly int m_subShuffle;
    private readonly int m_subDealHand;
    private readonly int m_subSessionReset;
    private readonly int m_subStartBetRound;
    private readonly int m_subBeginTurn;
    private readonly int m_subBettingTick;
    private readonly int m_subPlayerMenuTick;
    private readonly int m_subApplyAction;
    private readonly int m_subAdvanceTurn;
    private readonly int m_subDrawTick;
    private readonly int m_subPlayerDrawTick;
    private readonly int m_subDrawAdvance;
    private readonly int m_subAiDraw;
    private readonly int m_subReplaceMasked;
    private readonly int m_subShowdown;
    private readonly int m_subAwardPot;
    private readonly int m_subNextHand;
    private readonly int m_subBoardRepaint;
    private readonly int m_subMenuShow;
    private readonly int m_subMenuClearTick;
    private readonly int m_subCursorSprite;
    private readonly int m_subMsgNameQueued;
    private readonly int m_subMsgNameDirect;
    private readonly int m_subChipsQueued;
    private readonly int m_subHiInsert;
    private readonly int m_subSaveIfLive;
    private readonly int m_subPlayCore;
    private readonly int m_subHandEndWait;

    // The game's identity as a declarative manifest: the card layer's tiles/records/palettes, the two screens
    // (each art-backed when its bake installed), the cursor sprite set (baked or the hand-authored fallback), the
    // personality/evaluation tables, the attract script, and every string. The linker owns all relocation.
    private static GameManifest BuildManifest(PbakBackground? titleArt, PbakBackground? feltArt, PbakBundle? cursorArt) {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "card-tiles", tiles2bpp: CardTables.BuildCardTiles());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg-gameplay", paletteData: HgbImage.EncodePalette(palette: CardTables.BackgroundPalette));
        manifest.DefineObjectPalettes(name: "obj-gameplay", paletteData: HgbImage.EncodePalette(palette: CardTables.ObjectPalette));

        if (titleArt is not null) {
            manifest.DefineArtScreen(name: "title", art: titleArt, overlays: PokerTables.TitleOverlays);
        }
        else {
            manifest.DefineScreen(name: "title", cells: PokerTables.BuildTitleBannerCells(), overlays: PokerTables.TitleOverlays);
        }

        if (feltArt is not null) {
            manifest.DefineArtScreen(name: "play", art: feltArt, overlays: PokerTables.PlayOverlays);
        }
        else {
            manifest.DefineScreen(name: "play", cells: PokerTables.BuildFallbackFeltCells(), overlays: PokerTables.PlayOverlays);
        }

        manifest.DefineSpriteArt(name: "cursor", bundle: (cursorArt ?? CardTables.BuildFallbackCursorBundle()));
        manifest.DefineRecords(name: "cards", stride: 4, records: CardTables.BuildCardRecords());
        manifest.DefineRecords(name: "personalities", stride: PokerTables.PersonalityRecordStride, records: PokerTables.BuildPersonalityRecords());
        manifest.DefineTable(name: "category-table", bytes: PokerTables.BuildCategoryTable());
        manifest.DefineTable(name: "strength-bases", bytes: PokerTables.BuildStrengthBaseTable());
        manifest.DefineInputScript(name: "attract", steps: PokerTables.BuildAttractScript());
        manifest.DefineText(name: "str-deal", text: "DEAL");
        manifest.DefineText(name: "str-scores", text: "SCORES");
        manifest.DefineText(name: "str-pause", text: "PAUSE");
        manifest.DefineText(name: "str-fold-tag", text: "FOLD");
        manifest.DefineText(name: "str-out-tag", text: "OUT ");
        manifest.DefineText(name: "str-menu-clear", text: "      ");
        manifest.DefineText(name: "str-amount-clear", text: "    ");
        manifest.DefineText(name: "str-name-you", text: "YOU");
        manifest.DefineText(name: "str-verb-check", text: "CHECKS ");
        manifest.DefineText(name: "str-verb-bet", text: "BETS   ");
        manifest.DefineText(name: "str-verb-call", text: "CALLS  ");
        manifest.DefineText(name: "str-verb-raise", text: "RAISES ");
        manifest.DefineText(name: "str-verb-fold", text: "FOLDS  ");
        manifest.DefineText(name: "str-verb-draws", text: "DRAWS  ");
        manifest.DefineText(name: "str-verb-wins", text: "WINS   ");
        manifest.DefineText(name: "str-menu-check", text: "CHECK");
        manifest.DefineText(name: "str-menu-bet", text: "BET  ");
        manifest.DefineText(name: "str-menu-call", text: "CALL ");
        manifest.DefineText(name: "str-menu-raise", text: "RAISE");
        manifest.DefineText(name: "str-menu-fold", text: "FOLD ");
        manifest.DefineText(name: "str-busted", text: "   OUT OF CHIPS   ");
        manifest.DefineText(name: "str-cleared", text: "  TABLE CLEARED   ");
        manifest.DefineText(name: "str-new-high", text: "GREAT STACK");
        manifest.DefineText(name: "str-hi-scores", text: "BEST STACKS");
        manifest.DefineText(name: "str-hands-won", text: "HANDS WON");
        manifest.DefineText(name: "str-top-pot", text: "TOP POT");

        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            manifest.DefineText(name: $"str-name-{seat}", text: PokerTables.OpponentNames[seat - 1]);
        }

        for (var category = 0; (category < PokerTables.CategoryCount); category++) {
            manifest.DefineText(name: $"str-cat-{category}", text: PokerTables.CategoryNames[category]);
        }

        // The shared sound catalog (the CardSfx ids alias its streams) rides the manifest like every other table;
        // the ApuSoundDriver binds to the linked streams in the ctor.
        SoundTables.DefineIn(manifest: manifest);

        return manifest;
    }

    private PokerGame() {
        var manifest = BuildManifest(titleArt: PokerTables.TitleArt, feltArt: PokerTables.FeltArt, cursorArt: PokerTables.CursorArt);

        // The protocol/verify layer references the font base as a constant; guard the two against drifting apart.
        if (manifest.FontTileBase != PokerTables.FontTileBase) {
            throw new InvalidOperationException(message: $"The manifest landed the font at tile {manifest.FontTileBase}, not the pinned {PokerTables.FontTileBase}.");
        }

        var sound = new ApuSoundDriver();

        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: PokerTables.BuildDefaultSavePayload(), saveVersion: 1, sound: sound);

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
        m_personalities = linked.Records(name: "personalities");
        m_categoryTable = linked.Table(name: "category-table");
        m_strengthBases = linked.Table(name: "strength-bases");
        m_attractScript = linked.InputScript(name: "attract");
        m_strPause = linked.Text(name: "str-pause");
        m_strFold = linked.Text(name: "str-fold-tag");
        m_strOut = linked.Text(name: "str-out-tag");
        m_strMenuClear = linked.Text(name: "str-menu-clear");
        m_strAmountClear = linked.Text(name: "str-amount-clear");
        m_strBusted = linked.Text(name: "str-busted");
        m_strCleared = linked.Text(name: "str-cleared");
        m_strNewHigh = linked.Text(name: "str-new-high");
        m_strHiScores = linked.Text(name: "str-hi-scores");
        m_strHandsWon = linked.Text(name: "str-hands-won");
        m_strTopPot = linked.Text(name: "str-top-pot");
        m_strNames = [
            linked.Text(name: "str-name-you"),
            linked.Text(name: "str-name-1"),
            linked.Text(name: "str-name-2"),
            linked.Text(name: "str-name-3"),
        ];
        // Verb order follows the LastAction ids (index 0 unused).
        m_strVerbs = [
            linked.Text(name: "str-verb-check"),
            linked.Text(name: "str-verb-check"),
            linked.Text(name: "str-verb-bet"),
            linked.Text(name: "str-verb-call"),
            linked.Text(name: "str-verb-raise"),
            linked.Text(name: "str-verb-fold"),
        ];
        m_strWins = linked.Text(name: "str-verb-wins");
        m_strDraws = linked.Text(name: "str-verb-draws");
        m_strMenuOpen = [linked.Text(name: "str-menu-check"), linked.Text(name: "str-menu-bet"), linked.Text(name: "str-menu-fold")];
        m_strMenuFacing = [linked.Text(name: "str-menu-call"), linked.Text(name: "str-menu-raise"), linked.Text(name: "str-menu-fold")];
        m_strCategories = new RomTable[PokerTables.CategoryCount];

        for (var category = 0; (category < PokerTables.CategoryCount); category++) {
            m_strCategories[category] = linked.Text(name: $"str-cat-{category}");
        }

        m_cursor = linked.SpriteArt(name: "cursor")[0];
        m_cursorMaxEntries = m_cursor.FrameEntryCounts.Max();
        m_cursorSlot = m_fw.Oam.Reserve(count: m_cursorMaxEntries);
        m_menu = new CardMenu(
            cursorAddress: PokerProtocol.TitleCursor,
            fw: m_fw,
            items: [
                new CardMenuItem(Column: 7, Row: 11, Text: linked.Text(name: "str-deal")),
                new CardMenuItem(Column: 7, Row: 13, Text: linked.Text(name: "str-scores")),
            ]
        );
        m_pad = new CardInitialsPad(column: 8, cursorAddress: PokerProtocol.EntryCursor, fw: m_fw, glyphsAddress: PokerProtocol.EntryGlyphs, row: 10);

        var e = m_fw.Emitter;

        m_subCardRecord = e.NewLabel();
        m_subRowColAddr = e.NewLabel();
        m_subCellOffset = e.NewLabel();
        m_subWriteCell = e.NewLabel();
        m_subWriteFeltCell = e.NewLabel();
        m_subFeltRestore = e.NewLabel();
        m_subDrawCardAt = e.NewLabel();
        m_subDrawStrip = e.NewLabel();
        m_subHandAddr = e.NewLabel();
        m_subEvalAddr = e.NewLabel();
        m_subBankrollAddr = e.NewLabel();
        m_subSeatBroke = e.NewLabel();
        m_subEvalSeat = e.NewLabel();
        m_subDecide = e.NewLabel();
        m_subChipPay = e.NewLabel();
        m_subPotToWinner = e.NewLabel();
        m_subShuffle = e.NewLabel();
        m_subDealHand = e.NewLabel();
        m_subSessionReset = e.NewLabel();
        m_subStartBetRound = e.NewLabel();
        m_subBeginTurn = e.NewLabel();
        m_subBettingTick = e.NewLabel();
        m_subPlayerMenuTick = e.NewLabel();
        m_subApplyAction = e.NewLabel();
        m_subAdvanceTurn = e.NewLabel();
        m_subDrawTick = e.NewLabel();
        m_subPlayerDrawTick = e.NewLabel();
        m_subDrawAdvance = e.NewLabel();
        m_subAiDraw = e.NewLabel();
        m_subReplaceMasked = e.NewLabel();
        m_subShowdown = e.NewLabel();
        m_subAwardPot = e.NewLabel();
        m_subNextHand = e.NewLabel();
        m_subBoardRepaint = e.NewLabel();
        m_subMenuShow = e.NewLabel();
        m_subMenuClearTick = e.NewLabel();
        m_subCursorSprite = e.NewLabel();
        m_subMsgNameQueued = e.NewLabel();
        m_subMsgNameDirect = e.NewLabel();
        m_subChipsQueued = e.NewLabel();
        m_subHiInsert = e.NewLabel();
        m_subSaveIfLive = e.NewLabel();
        m_subPlayCore = e.NewLabel();
        m_subHandEndWait = e.NewLabel();

        m_fw.States.DefineState(id: PokerProtocol.StateTitle, emitEnter: EmitTitleEnter, emitTick: EmitTitleTick);
        m_fw.States.DefineState(id: PokerProtocol.StateAttract, emitEnter: EmitAttractEnter, emitTick: EmitAttractTick);
        m_fw.States.DefineState(id: PokerProtocol.StateHighScores, emitEnter: EmitHighScoresEnter, emitTick: EmitHighScoresTick);
        m_fw.States.DefineState(id: PokerProtocol.StatePlay, emitEnter: EmitPlayEnter, emitTick: EmitPlayTick);
        m_fw.States.DefineState(id: PokerProtocol.StatePause, emitEnter: EmitPauseEnter, emitTick: EmitPauseTick);
        m_fw.States.DefineState(id: PokerProtocol.StateGameOver, emitEnter: EmitGameOverEnter, emitTick: EmitGameOverTick);
        m_fw.States.DefineState(id: PokerProtocol.StateScoreEntry, emitEnter: EmitScoreEntryEnter, emitTick: EmitScoreEntryTick);
    }

    /// <summary>Assembles the cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new PokerGame();
        var spec = new FrameworkBootSpec(
            BgPalettes: game.m_bgPalettes,
            ObjPalettes: game.m_objPalettes,
            Tiles: game.m_tiles,
            TileByteCount: game.m_tiles.Length,
            InitialMap: game.m_titleMap,
            Lcdc: GameLcdc,
            InitialState: PokerProtocol.StateTitle
        );

        return game.m_fw.BuildRom(title: title, bootSpec: spec, emitGameLibrary: game.EmitGameLibrary);
    }

    // ==== The game library (shared subroutines). ========================================================================

    private void EmitGameLibrary(Sm83Emitter e) {
        EmitCardRecord(e: e);
        EmitRowColAddr(e: e);
        EmitCellOffset(e: e);
        EmitWriteCell(e: e);
        EmitWriteFeltCell(e: e);
        EmitFeltRestore(e: e);
        EmitDrawCardAt(e: e);
        EmitDrawStrip(e: e);
        EmitHandAddr(e: e);
        EmitEvalAddr(e: e);
        EmitBankrollAddr(e: e);
        EmitSeatBroke(e: e);
        EmitEvalSeat(e: e);
        EmitDecide(e: e);
        EmitChipPay(e: e);
        EmitPotToWinner(e: e);
        CardDeck.EmitShuffleSubroutine(e: e, prng: m_fw.Prng, label: m_subShuffle, deckBase: PokerProtocol.DeckScratch, indexScratch: PokerProtocol.IdxI, drawScratch: PokerProtocol.IdxJ);
        EmitDealHand(e: e);
        EmitSessionReset(e: e);
        EmitStartBetRound(e: e);
        EmitBeginTurn(e: e);
        EmitBettingTick(e: e);
        EmitPlayerMenuTick(e: e);
        EmitApplyAction(e: e);
        EmitAdvanceTurn(e: e);
        EmitDrawTick(e: e);
        EmitPlayerDrawTick(e: e);
        EmitAiDraw(e: e);
        EmitReplaceMasked(e: e);
        EmitShowdown(e: e);
        EmitAwardPot(e: e);
        EmitNextHand(e: e);
        EmitBoardRepaint(e: e);
        EmitMenuShow(e: e);
        EmitMenuClearTick(e: e);
        EmitCursorSprite(e: e);
        EmitMsgName(e: e);
        EmitChipsQueued(e: e);
        EmitHiInsert(e: e);
        EmitSaveIfLive(e: e);
        EmitPlayCore(e: e);
        EmitHandEndWait(e: e);
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
        e.StoreAToAddress(address: PokerProtocol.TmpRank);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: PokerProtocol.TmpSuit);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: PokerProtocol.TmpRed);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: PokerProtocol.TmpRankTile);
        e.Return();
    }

    // HL := 0x9800 + TmpRow × 32 + TmpCol. Clobbers A, C.
    private void EmitRowColAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subRowColAddr);
        e.LoadAFromAddress(address: PokerProtocol.TmpRow);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x07);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A); // (row & 7) × 32.
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.TmpCol);
        e.Arithmetic(op: AluOp.Add, source: Reg8.C);
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.TmpRow);
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
        e.LoadAFromAddress(address: PokerProtocol.TmpRow);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x07);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.A);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.TmpCol);
        e.Arithmetic(op: AluOp.Add, source: Reg8.C);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.TmpRow);
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

    // Restores the whole board region (rows 0..17, columns 0..19) to the felt. LCD off only.
    private void EmitFeltRestore(Sm83Emitter e) {
        var rowLoop = e.NewLabel();
        var columnLoop = e.NewLabel();

        e.MarkLabel(label: m_subFeltRestore);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpRow);
        e.MarkLabel(label: rowLoop);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpCol);
        e.MarkLabel(label: columnLoop);
        e.Call(label: m_subWriteFeltCell);
        e.LoadAFromAddress(address: PokerProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpCol);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.BoardColumns);
        e.JumpRelative(condition: Condition.Carry, label: columnLoop);
        e.LoadAFromAddress(address: PokerProtocol.TmpRow);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpRow);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.BoardRows);
        e.JumpRelative(condition: Condition.Carry, label: rowLoop);
        e.Return();
    }

    // Draws the full 2×2 face of TmpCard at (TmpRow, TmpCol); TmpRow/TmpCol are restored on return.
    private void EmitDrawCardAt(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawCardAt);
        e.LoadAFromAddress(address: PokerProtocol.TmpCard);
        e.Call(label: m_subCardRecord);
        e.LoadAFromAddress(address: PokerProtocol.TmpRankTile);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: PokerProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpCol);
        e.LoadAFromAddress(address: PokerProtocol.TmpSuit);
        e.ArithmeticImmediate(op: AluOp.Add, value: CardTables.TileSuitBase);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: PokerProtocol.TmpRow);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpRow);
        e.LoadImmediate(destination: Reg8.B, value: CardTables.TileCardBottomRight);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: PokerProtocol.TmpCol);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpCol);
        e.LoadImmediate(destination: Reg8.B, value: CardTables.TileCardBottomLeft);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: PokerProtocol.TmpRow);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpRow);
        e.Return();
    }

    // A rank + suit strip of TmpCard at (TmpRow, TmpCol); TmpCol restored (the showdown reveal rows).
    private void EmitDrawStrip(Sm83Emitter e) {
        e.MarkLabel(label: m_subDrawStrip);
        e.LoadAFromAddress(address: PokerProtocol.TmpCard);
        e.Call(label: m_subCardRecord);
        e.LoadAFromAddress(address: PokerProtocol.TmpRankTile);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: PokerProtocol.TmpCol);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpCol);
        e.LoadAFromAddress(address: PokerProtocol.TmpSuit);
        e.ArithmeticImmediate(op: AluOp.Add, value: CardTables.TileSuitBase);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subWriteCell);
        e.LoadAFromAddress(address: PokerProtocol.TmpCol);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpCol);
        e.Return();
    }

    // HL := HandBase + TmpSeat × 8 (one page). Clobbers A.
    private void EmitHandAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subHandAddr);
        e.LoadAFromAddress(address: PokerProtocol.TmpSeat);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8.
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.HandBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.HandBase >> 8));
        e.Return();
    }

    // HL := EvalBase + TmpSeat × 8 (one page). Clobbers A.
    private void EmitEvalAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subEvalAddr);
        e.LoadAFromAddress(address: PokerProtocol.TmpSeat);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8.
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.EvalBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.EvalBase >> 8));
        e.Return();
    }

    // HL := BankrollMirror + TmpSeat × 2 (the MSB; one page). Clobbers A.
    private void EmitBankrollAddr(Sm83Emitter e) {
        e.MarkLabel(label: m_subBankrollAddr);
        e.LoadAFromAddress(address: PokerProtocol.TmpSeat);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×2.
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.BankrollMirror & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.BankrollMirror >> 8));
        e.Return();
    }

    // A := 1 when TmpSeat's bankroll is below the ante (hi == 0 and lo < 5), else 0. Clobbers A, H, L.
    private void EmitSeatBroke(Sm83Emitter e) {
        var notBroke = e.NewLabel();

        e.MarkLabel(label: m_subSeatBroke);
        e.Call(label: m_subBankrollAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: notBroke);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.BustThresholdBcd);
        e.JumpRelative(condition: Condition.NoCarry, label: notBroke);
        e.LoadAImmediate(value: 1);
        e.Return();
        e.MarkLabel(label: notBroke);
        e.XorA();
        e.Return();
    }

    // ==== Hand evaluation, the decision seam, and chip arithmetic. =====================================================

    // Evaluates TmpSeat's five cards → EvalBase[seat] = [category, tb0..tb4, strength, 0] and Strength[seat].
    // Pure integer table logic: rank/suit counts, the flush/straight/shape scan, the shape→category DATA table,
    // tiebreak ranks ordered by (count desc, poker rank desc), strength = strengthBase[category] + tb0. The C#
    // oracle in PokerVerify mirrors this byte for byte.
    private void EmitEvalSeat(Sm83Emitter e) {
        e.MarkLabel(label: m_subEvalSeat);

        // Clear the rank (16) and suit (4) counters — one contiguous 0xC2A0..0xC2B3 sweep.
        var clearLoop = e.NewLabel();

        e.LoadImmediate(pair: Reg16.Hl, value: PokerProtocol.RankCountBase);
        e.LoadImmediate(destination: Reg8.B, value: 20);
        e.XorA();
        e.MarkLabel(label: clearLoop);
        e.StoreAToHlIncrement();
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: clearLoop);

        // Count the five cards (rank counts by POKER rank — the ace lands at 14, mirrored at 1 for the wheel).
        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            var notAce = e.NewLabel();
            var counted = e.NewLabel();

            e.Call(label: m_subHandAddr);

            for (var step = 0; (step < slot); step++) {
                e.Increment(pair: Reg16.Hl);
            }

            e.Load(destination: Reg8.A, source: Reg8.Memory);
            e.Call(label: m_subCardRecord);

            // Suit count.
            e.LoadAFromAddress(address: PokerProtocol.TmpSuit);
            e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.SuitCountBase & 0xFF));
            e.Load(destination: Reg8.L, source: Reg8.A);
            e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.SuitCountBase >> 8));
            e.Increment(register: Reg8.Memory);

            // Rank count(s).
            e.LoadAFromAddress(address: PokerProtocol.TmpRank);
            e.ArithmeticImmediate(op: AluOp.Compare, value: CardDeck.RankAce);
            e.JumpRelative(condition: Condition.NotZero, label: notAce);
            e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.RankCountBase >> 8));
            e.LoadImmediate(destination: Reg8.L, value: (byte)((PokerProtocol.RankCountBase + 1) & 0xFF));
            e.Increment(register: Reg8.Memory);
            e.LoadAImmediate(value: 14);
            e.MarkLabel(label: notAce);
            e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.RankCountBase & 0xFF));
            e.Load(destination: Reg8.L, source: Reg8.A);
            e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.RankCountBase >> 8));
            e.Increment(register: Reg8.Memory);
            e.JumpRelative(label: counted);
            e.MarkLabel(label: counted);
        }

        // Flush: any suit holding all five cards.
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.EvalFlush);

        for (var suit = 0; (suit < 4); suit++) {
            var notFlush = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.SuitCountBase + suit));
            e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.HandSize);
            e.JumpRelative(condition: Condition.NotZero, label: notFlush);
            e.LoadAImmediate(value: 1);
            e.StoreAToAddress(address: PokerProtocol.EvalFlush);
            e.MarkLabel(label: notFlush);
        }

        // Straight: the highest window high = 14..5 whose five rank counts are all non-zero (5 = the wheel, via
        // the ace mirror at index 1).
        var straightLoop = e.NewLabel();
        var straightInner = e.NewLabel();
        var straightFail = e.NewLabel();
        var straightDone = e.NewLabel();

        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.EvalStraightHigh);
        e.LoadAImmediate(value: 14);
        e.StoreAToAddress(address: PokerProtocol.IdxI);
        e.MarkLabel(label: straightLoop);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.RankCountBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.RankCountBase >> 8));
        e.LoadImmediate(destination: Reg8.B, value: 5);
        e.MarkLabel(label: straightInner);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: straightFail);
        e.Decrement(pair: Reg16.Hl);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: straightInner);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.StoreAToAddress(address: PokerProtocol.EvalStraightHigh);
        e.JumpRelative(label: straightDone);
        e.MarkLabel(label: straightFail);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.IdxI);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 5);
        e.JumpRelative(condition: Condition.NoCarry, label: straightLoop);
        e.MarkLabel(label: straightDone);

        // Quads / trips / pairs, scanning poker ranks 14..2 (descending, so the first pair found is the higher).
        var groupLoop = e.NewLabel();
        var groupNext = e.NewLabel();
        var notQuad = e.NewLabel();
        var notTrip = e.NewLabel();
        var pairSecond = e.NewLabel();

        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.EvalQuadRank);
        e.StoreAToAddress(address: PokerProtocol.EvalTripRank);
        e.StoreAToAddress(address: PokerProtocol.EvalPairCount);
        e.StoreAToAddress(address: PokerProtocol.EvalPairHigh);
        e.StoreAToAddress(address: PokerProtocol.EvalPairLow);
        e.LoadAImmediate(value: 14);
        e.StoreAToAddress(address: PokerProtocol.IdxI);
        e.MarkLabel(label: groupLoop);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.RankCountBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.RankCountBase >> 8));
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 4);
        e.JumpRelative(condition: Condition.Carry, label: notQuad);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.StoreAToAddress(address: PokerProtocol.EvalQuadRank);
        e.JumpRelative(label: groupNext);
        e.MarkLabel(label: notQuad);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.NotZero, label: notTrip);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.StoreAToAddress(address: PokerProtocol.EvalTripRank);
        e.JumpRelative(label: groupNext);
        e.MarkLabel(label: notTrip);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpRelative(condition: Condition.NotZero, label: groupNext);
        e.LoadAFromAddress(address: PokerProtocol.EvalPairCount);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.EvalPairCount);
        e.LoadAFromAddress(address: PokerProtocol.EvalPairHigh);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: pairSecond);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.StoreAToAddress(address: PokerProtocol.EvalPairHigh);
        e.JumpRelative(label: groupNext);
        e.MarkLabel(label: pairSecond);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.StoreAToAddress(address: PokerProtocol.EvalPairLow);
        e.MarkLabel(label: groupNext);
        e.LoadAFromAddress(address: PokerProtocol.IdxI);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.IdxI);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpRelative(condition: Condition.NoCarry, label: groupLoop);

        // The shape byte, then the category from the DATA table.
        EmitEvalShapeAndCategory(e: e);

        // Tiebreaks into EvalBase[seat][1..5], then the strength.
        EmitEvalTiebreaksAndStrength(e: e);
        e.Return();
    }

    // Packs the shape byte (flush | straight<<1 | pairs<<2 | trips<<4 | quads<<5) and resolves the category
    // through the manifest's 64-entry table.
    private void EmitEvalShapeAndCategory(Sm83Emitter e) {
        var noStraightBit = e.NewLabel();
        var noTripBit = e.NewLabel();
        var noQuadBit = e.NewLabel();

        e.LoadAFromAddress(address: PokerProtocol.EvalFlush);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.EvalStraightHigh);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noStraightBit);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.ArithmeticImmediate(op: AluOp.Or, value: 0x02);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.MarkLabel(label: noStraightBit);
        e.LoadAFromAddress(address: PokerProtocol.EvalPairCount);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // pairs << 2.
        e.Arithmetic(op: AluOp.Or, source: Reg8.B);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.EvalTripRank);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noTripBit);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.ArithmeticImmediate(op: AluOp.Or, value: 0x10);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.MarkLabel(label: noTripBit);
        e.LoadAFromAddress(address: PokerProtocol.EvalQuadRank);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noQuadBit);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.ArithmeticImmediate(op: AluOp.Or, value: 0x20);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.MarkLabel(label: noQuadBit);
        e.Load(destination: Reg8.A, source: Reg8.B);
        e.StoreAToAddress(address: PokerProtocol.EvalShape);

        // category = categoryTable[shape].
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_categoryTable.Address);
        e.AddToHl(pair: Reg16.De);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: PokerProtocol.EvalCategory);
        e.Call(label: m_subEvalAddr);
        e.LoadAFromAddress(address: PokerProtocol.EvalCategory);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
    }

    // Writes the five tiebreak ranks (a straight overrides with [high, 0…]; otherwise distinct ranks ordered by
    // count 4→1 then rank 14→2), then strength = strengthBase[category] + tb0 into EvalBase[6] and Strength[seat].
    private void EmitEvalTiebreaksAndStrength(Sm83Emitter e) {
        var straightTb = e.NewLabel();
        var targetLoop = e.NewLabel();
        var rankLoop = e.NewLabel();
        var rankNext = e.NewLabel();
        var padLoop = e.NewLabel();
        var padDone = e.NewLabel();
        var tbDone = e.NewLabel();

        // DE := the tiebreak write pointer (EvalBase[seat] + 1).
        e.Call(label: m_subEvalAddr);
        e.Load(destination: Reg8.D, source: Reg8.H);
        e.Load(destination: Reg8.E, source: Reg8.L);
        e.Increment(pair: Reg16.De);

        e.LoadAFromAddress(address: PokerProtocol.EvalStraightHigh);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.NotZero, label: straightTb);

        // The grouped pass: for target counts 4, 3, 2, 1 append every rank holding exactly that count.
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.EvalTbCount);
        e.LoadAImmediate(value: 4);
        e.StoreAToAddress(address: PokerProtocol.TmpVal);
        e.MarkLabel(label: targetLoop);
        e.LoadAFromAddress(address: PokerProtocol.TmpVal);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAImmediate(value: 14);
        e.StoreAToAddress(address: PokerProtocol.IdxJ);
        e.MarkLabel(label: rankLoop);
        e.LoadAFromAddress(address: PokerProtocol.IdxJ);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.RankCountBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.RankCountBase >> 8));
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.C);
        e.JumpRelative(condition: Condition.NotZero, label: rankNext);
        e.LoadAFromAddress(address: PokerProtocol.IdxJ);
        e.StoreAToDe();
        e.Increment(pair: Reg16.De);
        e.LoadAFromAddress(address: PokerProtocol.EvalTbCount);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.EvalTbCount);
        e.MarkLabel(label: rankNext);
        e.LoadAFromAddress(address: PokerProtocol.IdxJ);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.IdxJ);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpRelative(condition: Condition.NoCarry, label: rankLoop);
        e.LoadAFromAddress(address: PokerProtocol.TmpVal);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpVal);
        e.JumpRelative(condition: Condition.NotZero, label: targetLoop);

        // Pad the list out to five bytes.
        e.MarkLabel(label: padLoop);
        e.LoadAFromAddress(address: PokerProtocol.EvalTbCount);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 5);
        e.JumpRelative(condition: Condition.NoCarry, label: padDone);
        e.XorA();
        e.StoreAToDe();
        e.Increment(pair: Reg16.De);
        e.LoadAFromAddress(address: PokerProtocol.EvalTbCount);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.EvalTbCount);
        e.JumpRelative(label: padLoop);
        e.MarkLabel(label: padDone);
        e.JumpRelative(label: tbDone);

        // A straight's tiebreak is its high card alone.
        e.MarkLabel(label: straightTb);
        e.StoreAToDe();
        e.Increment(pair: Reg16.De);
        e.XorA();

        for (var pad = 0; (pad < 4); pad++) {
            e.StoreAToDe();
            e.Increment(pair: Reg16.De);
        }

        e.MarkLabel(label: tbDone);

        // strength = strengthBases[category] + tb0.
        e.LoadAFromAddress(address: PokerProtocol.EvalCategory);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_strengthBases.Address);
        e.AddToHl(pair: Reg16.De);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subEvalAddr);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.Load(destination: Reg8.B, source: Reg8.A);

        // EvalBase[seat][6] and Strength[seat].
        e.Call(label: m_subEvalAddr);

        for (var step = 0; (step < 6); step++) {
            e.Increment(pair: Reg16.Hl);
        }

        e.Load(destination: Reg8.Memory, source: Reg8.B);
        e.LoadAFromAddress(address: PokerProtocol.TmpSeat);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.StrengthBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.StrengthBase >> 8));
        e.Load(destination: Reg8.Memory, source: Reg8.B);
    }

    // THE OPPONENT SEAM: DecisionSeat/Strength/Facing/Raises in → DecisionAction out. Personality thresholds come
    // from the manifest's record table (seat − 1; the attract's auto-played seat 0 borrows personality 2), and the
    // bluff roll consumes EXACTLY ONE framework PRNG draw per decision — fixed consumption keeps replay and the C#
    // oracle aligned. The link arc swaps this subroutine's body for a link-fed action; everything downstream
    // consumes only DecisionAction.
    private void EmitDecide(Sm83Emitter e) {
        var notPlayerSeat = e.NewLabel();
        var facingPath = e.NewLabel();
        var noBluffFlag = e.NewLabel();
        var openBet = e.NewLabel();
        var openCheck = e.NewLabel();
        var noRaise = e.NewLabel();
        var facingRaise = e.NewLabel();
        var facingCall = e.NewLabel();
        var facingFold = e.NewLabel();

        e.MarkLabel(label: m_subDecide);

        // Personality row: index = (seat == 0) ? 2 : seat − 1.
        e.LoadAFromAddress(address: PokerProtocol.DecisionSeat);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: notPlayerSeat);
        e.LoadAImmediate(value: 3);
        e.MarkLabel(label: notPlayerSeat);
        e.Decrement(register: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×4 (the record stride).
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_personalities.Table.Address);
        e.AddToHl(pair: Reg16.De);

        for (var field = 0; (field < PokerTables.PersonalityRecordStride); field++) {
            e.LoadAFromHlIncrement();
            e.StoreAToAddress(address: (ushort)(PokerProtocol.DecPersonality + field));
        }

        // The bluff roll (always exactly one draw).
        m_fw.Prng.EmitNext();
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.DecPersonality + PokerTables.PersonalityFieldBluff));
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.LoadAImmediate(value: 0);
        e.JumpRelative(condition: Condition.NoCarry, label: noBluffFlag);
        e.LoadAImmediate(value: 1);
        e.MarkLabel(label: noBluffFlag);
        e.StoreAToAddress(address: PokerProtocol.DecisionBluff);

        e.LoadAFromAddress(address: PokerProtocol.DecisionFacing);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: facingPath);

        // Nothing outstanding: bet on strength or a bluff, otherwise check.
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.DecPersonality + PokerTables.PersonalityFieldBet));
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.DecisionStrength);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: openBet);
        e.LoadAFromAddress(address: PokerProtocol.DecisionBluff);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: openBet);
        e.JumpRelative(label: openCheck);
        e.MarkLabel(label: openBet);
        e.LoadAImmediate(value: PokerProtocol.ActionBetRaise);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.Return();
        e.MarkLabel(label: openCheck);
        e.LoadAImmediate(value: PokerProtocol.ActionCheckCall);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.Return();

        // Facing a bet: raise on strength (inside the cap), call on strength or a bluff, otherwise fold.
        e.MarkLabel(label: facingPath);
        e.LoadAFromAddress(address: PokerProtocol.DecisionRaises);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.RaiseCap);
        e.JumpRelative(condition: Condition.NoCarry, label: noRaise);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.DecPersonality + PokerTables.PersonalityFieldRaise));
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.DecisionStrength);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: facingRaise);
        e.MarkLabel(label: noRaise);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.DecPersonality + PokerTables.PersonalityFieldCall));
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.DecisionStrength);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: facingCall);
        e.LoadAFromAddress(address: PokerProtocol.DecisionBluff);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: facingCall);
        e.JumpRelative(label: facingFold);
        e.MarkLabel(label: facingRaise);
        e.LoadAImmediate(value: PokerProtocol.ActionBetRaise);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.Return();
        e.MarkLabel(label: facingCall);
        e.LoadAImmediate(value: PokerProtocol.ActionCheckCall);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.Return();
        e.MarkLabel(label: facingFold);
        e.LoadAImmediate(value: PokerProtocol.ActionFold);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.Return();
    }

    // TmpSeat pays TmpAmount (one packed-BCD byte) from its bankroll into the pot (both two-byte BCD, MSB first).
    private void EmitChipPay(Sm83Emitter e) {
        e.MarkLabel(label: m_subChipPay);
        e.LoadAFromAddress(address: PokerProtocol.TmpAmount);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subBankrollAddr);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.B);
        e.DecimalAdjustA();
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.Decrement(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.SubtractWithCarry, value: 0);
        e.DecimalAdjustA();
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.Pot + 1));
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(PokerProtocol.Pot + 1));
        e.LoadAFromAddress(address: PokerProtocol.Pot);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: PokerProtocol.Pot);
        e.Return();
    }

    // Adds the whole pot to WinnerSeat's bankroll (two-byte BCD add, MSB first). The pot itself stays for the
    // caller's display prints; the award subroutine zeroes it.
    private void EmitPotToWinner(Sm83Emitter e) {
        e.MarkLabel(label: m_subPotToWinner);
        e.LoadAFromAddress(address: PokerProtocol.WinnerSeat);
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.Call(label: m_subBankrollAddr);
        e.Increment(pair: Reg16.Hl);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.Pot + 1));
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);
        e.DecimalAdjustA();
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.Decrement(pair: Reg16.Hl);
        e.LoadAFromAddress(address: PokerProtocol.Pot);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.AddWithCarry, source: Reg8.B);
        e.DecimalAdjustA();
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.Return();
    }

    // ==== The hand flow (deal, betting, draw, showdown). ===============================================================

    // A := array[seatAddress] for a one-page per-seat byte array. Clobbers A, H, L.
    private static void EmitLoadSeatByte(Sm83Emitter e, ushort arrayBase, ushort seatAddress) {
        e.LoadAFromAddress(address: seatAddress);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(arrayBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(arrayBase >> 8));
        e.Load(destination: Reg8.A, source: Reg8.Memory);
    }

    // array[seatAddress] := B for a one-page per-seat byte array. Clobbers A, H, L.
    private static void EmitStoreSeatByte(Sm83Emitter e, ushort arrayBase, ushort seatAddress) {
        e.LoadAFromAddress(address: seatAddress);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(arrayBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(arrayBase >> 8));
        e.Load(destination: Reg8.Memory, source: Reg8.B);
    }

    // Per-hand init: rotate-independent — the caller owns DealerSeat. Ante, the deterministic deal (the shared
    // Fisher–Yates over the framework PRNG), and a fresh evaluation for every seat. No visuals.
    private void EmitDealHand(Sm83Emitter e) {
        e.MarkLabel(label: m_subDealHand);

        // FirstSeat = (DealerSeat + 1) & 3; re-activate every non-busted seat and count the field.
        e.LoadAFromAddress(address: PokerProtocol.DealerSeat);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x03);
        e.StoreAToAddress(address: PokerProtocol.FirstSeat);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.InHand);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.SeatBusted);
            e.JumpRelative(condition: Condition.Zero, label: skip);
            e.XorA();
            e.StoreAToAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.LoadAFromAddress(address: PokerProtocol.InHand);
            e.Increment(register: Reg8.A);
            e.StoreAToAddress(address: PokerProtocol.InHand);
            e.MarkLabel(label: skip);
        }

        // Clear the per-hand table state.
        e.XorA();

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            e.StoreAToAddress(address: (ushort)(PokerProtocol.RoundBetBase + seat));
            e.StoreAToAddress(address: (ushort)(PokerProtocol.LastActionBase + seat));
            e.StoreAToAddress(address: (ushort)(PokerProtocol.DrawCountBase + seat));
        }

        e.StoreAToAddress(address: PokerProtocol.DiscardMask);
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        e.StoreAToAddress(address: PokerProtocol.MenuClearRows);
        e.StoreAToAddress(address: PokerProtocol.WinnerSeat);
        e.StoreAToAddress(address: PokerProtocol.Pot);
        e.StoreAToAddress(address: (ushort)(PokerProtocol.Pot + 1));

        // The ante (every seated player).
        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.LoadAImmediate(value: PokerProtocol.AnteBcd);
            e.StoreAToAddress(address: PokerProtocol.TmpAmount);
            e.Call(label: m_subChipPay);
            e.MarkLabel(label: skip);
        }

        // The deal: identity deck → Fisher–Yates (51 PRNG draws) → five cards per seat, block order.
        CardDeck.EmitInitDeck(e: e, deckBase: PokerProtocol.DeckScratch);
        e.Call(label: m_subShuffle);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            FrameworkKernel.EmitBlockCopy(
                emitter: e,
                sourceAddress: (ushort)(PokerProtocol.DeckScratch + (seat * PokerProtocol.HandSize)),
                destinationAddress: (ushort)(PokerProtocol.HandBase + (seat * PokerProtocol.HandStride)),
                byteCount: PokerProtocol.HandSize
            );
        }

        e.LoadAImmediate(value: (byte)(PokerProtocol.SeatCount * PokerProtocol.HandSize));
        e.StoreAToAddress(address: PokerProtocol.NextCard);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Shuffle);

        // Fresh evaluations (and strengths) for every seat.
        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.Call(label: m_subEvalSeat);
        }

        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.Phase); // PhaseBet1.
        e.Return();
    }

    // Session start: continue the persisted bankrolls, or reset all four to the defaults when the player (or the
    // whole opposition) can no longer ante. Marks busted seats, zeroes the session state, and deals the first hand.
    private void EmitSessionReset(Sm83Emitter e) {
        var doReset = e.NewLabel();
        var noReset = e.NewLabel();

        e.MarkLabel(label: m_subSessionReset);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.Call(label: m_subSeatBroke);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: doReset);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpVal);

        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            var next = e.NewLabel();

            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.Call(label: m_subSeatBroke);
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpRelative(condition: Condition.Zero, label: next);
            e.LoadAFromAddress(address: PokerProtocol.TmpVal);
            e.Increment(register: Reg8.A);
            e.StoreAToAddress(address: PokerProtocol.TmpVal);
            e.MarkLabel(label: next);
        }

        e.LoadAFromAddress(address: PokerProtocol.TmpVal);
        e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)(PokerProtocol.SeatCount - 1));
        e.JumpRelative(condition: Condition.NotZero, label: noReset);
        e.MarkLabel(label: doReset);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            e.LoadAImmediate(value: 0x02);
            e.StoreAToAddress(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2)));
            e.XorA();
            e.StoreAToAddress(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2) + 1));
        }

        e.MarkLabel(label: noReset);

        // Busted marks from the (possibly reset) bankrolls.
        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var active = e.NewLabel();
            var write = e.NewLabel();

            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.Call(label: m_subSeatBroke);
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpRelative(condition: Condition.Zero, label: active);
            e.LoadAImmediate(value: PokerProtocol.SeatBusted);
            e.JumpRelative(label: write);
            e.MarkLabel(label: active);
            e.XorA();
            e.MarkLabel(label: write);
            e.StoreAToAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
        }

        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.DealerSeat);
        e.StoreAToAddress(address: PokerProtocol.TurnSerial);
        e.StoreAToAddress(address: PokerProtocol.GameOverKind);
        e.JumpAbsolute(label: m_subDealHand);
    }

    // Fresh betting round: level/raises/round-bets cleared, everyone owes an action, first actor from the
    // dealer's left (skipping folded/busted seats).
    private void EmitStartBetRound(Sm83Emitter e) {
        var skipLoop = e.NewLabel();
        var found = e.NewLabel();

        e.MarkLabel(label: m_subStartBetRound);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.BetLevel);
        e.StoreAToAddress(address: PokerProtocol.RaiseCount);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            e.StoreAToAddress(address: (ushort)(PokerProtocol.RoundBetBase + seat));
            e.StoreAToAddress(address: (ushort)(PokerProtocol.LastActionBase + seat));
        }

        e.LoadAFromAddress(address: PokerProtocol.InHand);
        e.StoreAToAddress(address: PokerProtocol.ToActCount);
        e.LoadAFromAddress(address: PokerProtocol.FirstSeat);
        e.StoreAToAddress(address: PokerProtocol.ActorSeat);
        e.MarkLabel(label: skipLoop);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.FoldedBase, seatAddress: PokerProtocol.ActorSeat);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: found);
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x03);
        e.StoreAToAddress(address: PokerProtocol.ActorSeat);
        e.JumpRelative(label: skipLoop);
        e.MarkLabel(label: found);
        e.JumpAbsolute(label: m_subBeginTurn);
    }

    // Hands the turn to the actor: the live player gets the action menu; an AI seat (and the attract's auto-played
    // seat 0) arms the think timer.
    private void EmitBeginTurn(Sm83Emitter e) {
        var ai = e.NewLabel();
        var facingSet = e.NewLabel();

        e.MarkLabel(label: m_subBeginTurn);
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: ai);
        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.StateAttract);
        e.JumpRelative(condition: Condition.Zero, label: ai);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.MenuCursor);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.RoundBetBase);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.LoadAImmediate(value: 0);
        e.JumpRelative(condition: Condition.Zero, label: facingSet);
        e.LoadAImmediate(value: 1);
        e.MarkLabel(label: facingSet);
        e.StoreAToAddress(address: PokerProtocol.Facing);
        e.Call(label: m_subMenuShow);
        e.Return();
        e.MarkLabel(label: ai);
        e.LoadAImmediate(value: PokerProtocol.AiDelayFrames);
        e.StoreAToAddress(address: PokerProtocol.DelayTimer);
        e.Return();
    }

    // One betting-phase frame: the player's menu when awaited, otherwise the actor's think timer and — on zero —
    // the decision seam, the table's legality downgrades, the application, and the turn advance.
    private void EmitBettingTick(Sm83Emitter e) {
        var act = e.NewLabel();
        var facingZero = e.NewLabel();
        var notRaiseIntent = e.NewLabel();
        var capOk = e.NewLabel();
        var raiseAfford = e.NewLabel();
        var notCallIntent = e.NewLabel();
        var callFree = e.NewLabel();
        var callAfford = e.NewLabel();
        var applyGo = e.NewLabel();

        e.MarkLabel(label: m_subBettingTick);
        e.LoadAFromAddress(address: PokerProtocol.AwaitInput);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 1);
        e.JumpAbsolute(condition: Condition.Zero, label: m_subPlayerMenuTick);
        e.LoadAFromAddress(address: PokerProtocol.DelayTimer);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: act);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.DelayTimer);
        e.Return();
        e.MarkLabel(label: act);

        // The seam's inputs.
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.StoreAToAddress(address: PokerProtocol.DecisionSeat);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.StrengthBase, seatAddress: PokerProtocol.ActorSeat);
        e.StoreAToAddress(address: PokerProtocol.DecisionStrength);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Load(destination: Reg8.B, source: Reg8.A);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.RoundBetBase, seatAddress: PokerProtocol.ActorSeat);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.LoadAImmediate(value: 0);
        e.JumpRelative(condition: Condition.Zero, label: facingZero);
        e.LoadAImmediate(value: 1);
        e.MarkLabel(label: facingZero);
        e.StoreAToAddress(address: PokerProtocol.DecisionFacing);
        e.LoadAFromAddress(address: PokerProtocol.RaiseCount);
        e.StoreAToAddress(address: PokerProtocol.DecisionRaises);
        e.Call(label: m_subDecide);

        // Legality downgrades (the seam emits INTENT; the table enforces the cap and affordability — the link arc
        // applies the same downgrades to remote actions).
        e.LoadAFromAddress(address: PokerProtocol.DecisionAction);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.ActionBetRaise);
        e.JumpAbsolute(condition: Condition.NotZero, label: notRaiseIntent);
        e.LoadAFromAddress(address: PokerProtocol.RaiseCount);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.RaiseCap);
        e.JumpRelative(condition: Condition.Carry, label: capOk);
        e.LoadAImmediate(value: PokerProtocol.ActionCheckCall);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.JumpAbsolute(label: notRaiseIntent);
        e.MarkLabel(label: capOk);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.RoundBetBase, seatAddress: PokerProtocol.ActorSeat);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Increment(register: Reg8.A);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subBankrollAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: raiseAfford);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: raiseAfford);
        e.LoadAImmediate(value: PokerProtocol.ActionCheckCall);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.MarkLabel(label: raiseAfford);
        e.MarkLabel(label: notRaiseIntent);
        e.LoadAFromAddress(address: PokerProtocol.DecisionAction);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.ActionCheckCall);
        e.JumpAbsolute(condition: Condition.NotZero, label: notCallIntent);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.RoundBetBase, seatAddress: PokerProtocol.ActorSeat);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpAbsolute(condition: Condition.Zero, label: callFree);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Call(label: m_subBankrollAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: callAfford);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: callAfford);
        e.LoadAImmediate(value: PokerProtocol.ActionFold);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);
        e.MarkLabel(label: callAfford);
        e.MarkLabel(label: callFree);
        e.MarkLabel(label: notCallIntent);
        e.MarkLabel(label: applyGo);
        e.Call(label: m_subApplyAction);
        e.JumpAbsolute(label: m_subAdvanceTurn);
    }

    // Applies DecisionAction for ActorSeat: table state, chips, the message line, the per-seat tags, sound, and
    // the TurnSerial observation point. Legality/affordability is the CALLER's contract.
    private void EmitApplyAction(Sm83Emitter e) {
        var checkCall = e.NewLabel();
        var betRaise = e.NewLabel();
        var isCheck = e.NewLabel();

        e.MarkLabel(label: m_subApplyAction);
        e.LoadAFromAddress(address: PokerProtocol.TurnSerial);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TurnSerial);
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.StoreAToAddress(address: PokerProtocol.LastActor);
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.LoadAFromAddress(address: PokerProtocol.DecisionAction);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.ActionCheckCall);
        e.JumpAbsolute(condition: Condition.Zero, label: checkCall);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.ActionBetRaise);
        e.JumpAbsolute(condition: Condition.Zero, label: betRaise);

        // Fold.
        e.LoadImmediate(destination: Reg8.B, value: PokerProtocol.SeatFolded);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.FoldedBase, seatAddress: PokerProtocol.ActorSeat);
        e.LoadAFromAddress(address: PokerProtocol.InHand);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.InHand);
        e.LoadAFromAddress(address: PokerProtocol.ToActCount);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.ToActCount);
        e.LoadImmediate(destination: Reg8.B, value: PokerProtocol.ActedFold);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.LastActionBase, seatAddress: PokerProtocol.ActorSeat);
        e.Call(label: m_subMsgNameQueued);
        m_fw.Text.EmitPrintQueued(text: m_strVerbs[PokerProtocol.ActedFold], row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        m_fw.Text.EmitPrintQueued(text: m_strAmountClear, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageAmountColumn);

        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)seat);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            m_fw.Text.EmitPrintQueued(text: m_strFold, row: 2, column: PokerTables.OpponentColumns[seat - 1]);
            e.MarkLabel(label: skip);
        }

        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Place);
        e.Return();

        // Check / call.
        e.MarkLabel(label: checkCall);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.RoundBetBase, seatAddress: PokerProtocol.ActorSeat);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpAbsolute(condition: Condition.Zero, label: isCheck);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpAmount);
        e.Call(label: m_subChipPay);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Load(destination: Reg8.B, source: Reg8.A);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.RoundBetBase, seatAddress: PokerProtocol.ActorSeat);
        e.LoadImmediate(destination: Reg8.B, value: PokerProtocol.ActedCall);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.LastActionBase, seatAddress: PokerProtocol.ActorSeat);
        e.LoadAFromAddress(address: PokerProtocol.ToActCount);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.ToActCount);
        e.Call(label: m_subMsgNameQueued);
        m_fw.Text.EmitPrintQueued(text: m_strVerbs[PokerProtocol.ActedCall], row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: PokerProtocol.TmpAmount, byteCount: 1, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageAmountColumn);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: PokerProtocol.Pot, byteCount: 2, row: PokerProtocol.PotRow, column: PokerProtocol.PotColumn);
        e.Call(label: m_subChipsQueued);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Place);
        e.Return();
        e.MarkLabel(label: isCheck);
        e.LoadImmediate(destination: Reg8.B, value: PokerProtocol.ActedCheck);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.LastActionBase, seatAddress: PokerProtocol.ActorSeat);
        e.LoadAFromAddress(address: PokerProtocol.ToActCount);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.ToActCount);
        e.Call(label: m_subMsgNameQueued);
        m_fw.Text.EmitPrintQueued(text: m_strVerbs[PokerProtocol.ActedCheck], row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        m_fw.Text.EmitPrintQueued(text: m_strAmountClear, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageAmountColumn);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.Return();

        // Bet / raise.
        var flagDone = e.NewLabel();
        var verbIsBet = e.NewLabel();
        var actedIsBet = e.NewLabel();
        var actedStore = e.NewLabel();
        var verbDone = e.NewLabel();

        e.MarkLabel(label: betRaise);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.LoadAImmediate(value: 0);
        e.JumpRelative(condition: Condition.Zero, label: flagDone);
        e.LoadAImmediate(value: 1);
        e.MarkLabel(label: flagDone);
        e.StoreAToAddress(address: PokerProtocol.TmpVal2); // 0 = an opening bet, 1 = a raise.
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.BetLevel);
        e.LoadAFromAddress(address: PokerProtocol.RaiseCount);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.RaiseCount);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.RoundBetBase, seatAddress: PokerProtocol.ActorSeat);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.TmpAmount);
        e.Call(label: m_subChipPay);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Load(destination: Reg8.B, source: Reg8.A);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.RoundBetBase, seatAddress: PokerProtocol.ActorSeat);
        e.LoadAFromAddress(address: PokerProtocol.TmpVal2);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: actedIsBet);
        e.LoadImmediate(destination: Reg8.B, value: PokerProtocol.ActedRaise);
        e.JumpRelative(label: actedStore);
        e.MarkLabel(label: actedIsBet);
        e.LoadImmediate(destination: Reg8.B, value: PokerProtocol.ActedBet);
        e.MarkLabel(label: actedStore);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.LastActionBase, seatAddress: PokerProtocol.ActorSeat);
        e.LoadAFromAddress(address: PokerProtocol.InHand);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.ToActCount);
        e.Call(label: m_subMsgNameQueued);
        e.LoadAFromAddress(address: PokerProtocol.TmpVal2);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: verbIsBet);
        m_fw.Text.EmitPrintQueued(text: m_strVerbs[PokerProtocol.ActedRaise], row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        e.JumpRelative(label: verbDone);
        e.MarkLabel(label: verbIsBet);
        m_fw.Text.EmitPrintQueued(text: m_strVerbs[PokerProtocol.ActedBet], row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        e.MarkLabel(label: verbDone);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: PokerProtocol.TmpAmount, byteCount: 1, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageAmountColumn);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: PokerProtocol.Pot, byteCount: 2, row: PokerProtocol.PotRow, column: PokerProtocol.PotColumn);
        e.Call(label: m_subChipsQueued);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Place);
        e.Return();
    }

    // Turn advance: an uncontested pot pays out immediately; a finished round moves to the draw phase or the
    // showdown; otherwise the next active seat begins its turn.
    private void EmitAdvanceTurn(Sm83Emitter e) {
        var notLone = e.NewLabel();
        var loneFound = e.NewLabel();
        var nextActor = e.NewLabel();
        var toShowdown = e.NewLabel();
        var actorLoop = e.NewLabel();

        e.MarkLabel(label: m_subAdvanceTurn);
        e.LoadAFromAddress(address: PokerProtocol.InHand);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 1);
        e.JumpAbsolute(condition: Condition.NotZero, label: notLone);

        // Uncontested: the last seat standing takes the pot without a showdown or reveal.
        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var next = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpRelative(condition: Condition.NotZero, label: next);
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.WinnerSeat);
            e.JumpAbsolute(label: loneFound);
            e.MarkLabel(label: next);
        }

        e.MarkLabel(label: loneFound);
        e.LoadAFromAddress(address: PokerProtocol.WinnerSeat);
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.Call(label: m_subMsgNameQueued);
        m_fw.Text.EmitPrintQueued(text: m_strWins, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: PokerProtocol.Pot, byteCount: 2, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageAmountColumn);
        e.Call(label: m_subAwardPot);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: PokerProtocol.Pot, byteCount: 2, row: PokerProtocol.PotRow, column: PokerProtocol.PotColumn);
        e.Call(label: m_subChipsQueued);
        e.LoadAImmediate(value: PokerProtocol.PhaseHandEnd);
        e.StoreAToAddress(address: PokerProtocol.Phase);
        e.LoadAImmediate(value: PokerProtocol.HandEndFrames);
        e.StoreAToAddress(address: PokerProtocol.EndTimer);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        e.Return();

        e.MarkLabel(label: notLone);
        e.LoadAFromAddress(address: PokerProtocol.ToActCount);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.NotZero, label: nextActor);
        e.LoadAFromAddress(address: PokerProtocol.Phase);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.PhaseBet1);
        e.JumpRelative(condition: Condition.NotZero, label: toShowdown);
        e.LoadAImmediate(value: PokerProtocol.PhaseDraw);
        e.StoreAToAddress(address: PokerProtocol.Phase);
        e.LoadAFromAddress(address: PokerProtocol.FirstSeat);
        e.StoreAToAddress(address: PokerProtocol.ActorSeat);
        e.LoadAImmediate(value: PokerProtocol.SeatCount);
        e.StoreAToAddress(address: PokerProtocol.ToActCount);
        e.LoadAImmediate(value: PokerProtocol.RoundGapFrames);
        e.StoreAToAddress(address: PokerProtocol.DelayTimer);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        e.Return();
        e.MarkLabel(label: toShowdown);
        e.JumpAbsolute(label: m_subShowdown);

        e.MarkLabel(label: nextActor);
        e.MarkLabel(label: actorLoop);
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x03);
        e.StoreAToAddress(address: PokerProtocol.ActorSeat);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.FoldedBase & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.FoldedBase >> 8));
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: actorLoop);
        e.JumpAbsolute(label: m_subBeginTurn);
    }

    // One draw-phase frame: the player's discard selection when awaited, otherwise the seat timer and — on zero —
    // a skip (folded), the player hand-off, or the AI's rule-driven draw.
    private void EmitDrawTick(Sm83Emitter e) {
        var act = e.NewLabel();
        var ai = e.NewLabel();

        e.MarkLabel(label: m_subDrawTick);
        e.LoadAFromAddress(address: PokerProtocol.AwaitInput);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpAbsolute(condition: Condition.Zero, label: m_subPlayerDrawTick);
        e.LoadAFromAddress(address: PokerProtocol.DelayTimer);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: act);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.DelayTimer);
        e.Return();
        e.MarkLabel(label: act);
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.FoldedBase, seatAddress: PokerProtocol.ActorSeat);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.NotZero, label: m_subDrawAdvance);
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: ai);
        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.StateAttract);
        e.JumpRelative(condition: Condition.Zero, label: ai);
        e.LoadAImmediate(value: 2);
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.DrawCursor);
        e.StoreAToAddress(address: PokerProtocol.DiscardMask);
        e.Return();
        e.MarkLabel(label: ai);
        e.Call(label: m_subAiDraw);
        e.Call(label: m_subMsgNameQueued);
        m_fw.Text.EmitPrintQueued(text: m_strDraws, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        EmitLoadSeatByte(e: e, arrayBase: PokerProtocol.DrawCountBase, seatAddress: PokerProtocol.TmpSeat);
        e.StoreAToAddress(address: PokerProtocol.TmpVal);
        m_fw.Text.EmitPrintBcdQueued(bcdAddress: PokerProtocol.TmpVal, byteCount: 1, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageAmountColumn);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Flip);
        e.JumpAbsolute(label: m_subDrawAdvance);

        // Advance to the next seat, or — once all four visited — refresh every evaluation and begin bet two.
        var allDone = e.NewLabel();

        e.MarkLabel(label: m_subDrawAdvance);
        e.LoadAFromAddress(address: PokerProtocol.ActorSeat);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x03);
        e.StoreAToAddress(address: PokerProtocol.ActorSeat);
        e.LoadAFromAddress(address: PokerProtocol.ToActCount);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.ToActCount);
        e.JumpRelative(condition: Condition.Zero, label: allDone);
        e.LoadAImmediate(value: PokerProtocol.AiDelayFrames);
        e.StoreAToAddress(address: PokerProtocol.DelayTimer);
        e.Return();
        e.MarkLabel(label: allDone);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.Call(label: m_subEvalSeat);
        }

        e.LoadAImmediate(value: PokerProtocol.PhaseBet2);
        e.StoreAToAddress(address: PokerProtocol.Phase);
        e.JumpAbsolute(label: m_subStartBetRound);
    }

    // The player's discard selection: Left/Right move the cursor, A toggles a card's discard mark (with the row-12
    // marker glyph), START performs the replacement and rejoins the seat rotation.
    private void EmitPlayerDrawTick(Sm83Emitter e) {
        var noLeft = e.NewLabel();
        var leftStore = e.NewLabel();
        var noRight = e.NewLabel();
        var rightStore = e.NewLabel();
        var noToggle = e.NewLabel();
        var toggleDone = e.NewLabel();
        var noConfirm = e.NewLabel();

        e.MarkLabel(label: m_subPlayerDrawTick);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 1, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noLeft);
        e.LoadAFromAddress(address: PokerProtocol.DrawCursor);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: leftStore);
        e.LoadAImmediate(value: PokerProtocol.HandSize);
        e.MarkLabel(label: leftStore);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.DrawCursor);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.MarkLabel(label: noLeft);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        e.LoadAFromAddress(address: PokerProtocol.DrawCursor);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.HandSize);
        e.JumpRelative(condition: Condition.Carry, label: rightStore);
        e.XorA();
        e.MarkLabel(label: rightStore);
        e.StoreAToAddress(address: PokerProtocol.DrawCursor);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.MarkLabel(label: noRight);

        // A: toggle the cursor card's discard mark and its marker glyph.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpAbsolute(condition: Condition.Zero, label: noToggle);

        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            var skip = e.NewLabel();
            var clearGlyph = e.NewLabel();
            var pushGlyph = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.DrawCursor);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)slot);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            e.LoadAFromAddress(address: PokerProtocol.DiscardMask);
            e.ArithmeticImmediate(op: AluOp.Xor, value: (byte)(1 << slot));
            e.StoreAToAddress(address: PokerProtocol.DiscardMask);
            e.ArithmeticImmediate(op: AluOp.And, value: (byte)(1 << slot));
            e.JumpRelative(condition: Condition.Zero, label: clearGlyph);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: 'X'));
            e.JumpRelative(label: pushGlyph);
            e.MarkLabel(label: clearGlyph);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: ' '));
            e.MarkLabel(label: pushGlyph);
            m_fw.Bg.EmitQueueCell(row: PokerProtocol.MarkerRow, column: (PokerProtocol.PlayerCardColumn + (slot * 3)));
            e.JumpAbsolute(label: toggleDone);
            e.MarkLabel(label: skip);
        }

        e.MarkLabel(label: toggleDone);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.MarkLabel(label: noToggle);

        // START: replace the marked cards and move on.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noConfirm);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.Call(label: m_subReplaceMasked);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Flip);
        e.Call(label: m_subBoardRepaint);
        e.JumpAbsolute(label: m_subDrawAdvance);
        e.MarkLabel(label: noConfirm);
        e.JumpAbsolute(label: m_subCursorSprite);
    }

    // The AI draw rules (shared by every opponent, and the attract's auto-played seat 0): keep a made hand
    // (category ≥ straight) whole; otherwise keep every card whose rank appears at least twice; on a pure high
    // card keep only the best-ranked card. Deterministic — no PRNG.
    private void EmitAiDraw(Sm83Emitter e) {
        var drawSome = e.NewLabel();
        var maskDone = e.NewLabel();

        e.MarkLabel(label: m_subAiDraw);
        e.Call(label: m_subEvalSeat); // Refresh the counts/eval for THIS seat.
        e.Call(label: m_subEvalAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 4);
        e.JumpRelative(condition: Condition.Carry, label: drawSome);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.DiscardMask);
        e.JumpAbsolute(label: m_subReplaceMasked);
        e.MarkLabel(label: drawSome);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpMask);

        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            var keep = e.NewLabel();
            var notAce = e.NewLabel();

            e.Call(label: m_subHandAddr);

            for (var step = 0; (step < slot); step++) {
                e.Increment(pair: Reg16.Hl);
            }

            e.Load(destination: Reg8.A, source: Reg8.Memory);
            e.Call(label: m_subCardRecord);
            e.LoadAFromAddress(address: PokerProtocol.TmpRank);
            e.ArithmeticImmediate(op: AluOp.Compare, value: CardDeck.RankAce);
            e.JumpRelative(condition: Condition.NotZero, label: notAce);
            e.LoadAImmediate(value: 14);
            e.MarkLabel(label: notAce);
            e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.RankCountBase & 0xFF));
            e.Load(destination: Reg8.L, source: Reg8.A);
            e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.RankCountBase >> 8));
            e.Load(destination: Reg8.A, source: Reg8.Memory);
            e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
            e.JumpRelative(condition: Condition.NoCarry, label: keep);
            e.LoadAFromAddress(address: PokerProtocol.TmpMask);
            e.ArithmeticImmediate(op: AluOp.Or, value: (byte)(1 << slot));
            e.StoreAToAddress(address: PokerProtocol.TmpMask);
            e.MarkLabel(label: keep);
        }

        // A pure high card keeps only the FIRST card matching the top tiebreak rank.
        e.Call(label: m_subEvalAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.NotZero, label: maskDone);
        e.Call(label: m_subEvalAddr);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: PokerProtocol.TmpVal2);

        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            var next = e.NewLabel();
            var notAce = e.NewLabel();

            e.Call(label: m_subHandAddr);

            for (var step = 0; (step < slot); step++) {
                e.Increment(pair: Reg16.Hl);
            }

            e.Load(destination: Reg8.A, source: Reg8.Memory);
            e.Call(label: m_subCardRecord);
            e.LoadAFromAddress(address: PokerProtocol.TmpRank);
            e.ArithmeticImmediate(op: AluOp.Compare, value: CardDeck.RankAce);
            e.JumpRelative(condition: Condition.NotZero, label: notAce);
            e.LoadAImmediate(value: 14);
            e.MarkLabel(label: notAce);
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: PokerProtocol.TmpVal2);
            e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            e.JumpRelative(condition: Condition.NotZero, label: next);
            e.LoadAFromAddress(address: PokerProtocol.TmpMask);
            e.ArithmeticImmediate(op: AluOp.And, value: (byte)(~(1 << slot) & 0xFF));
            e.StoreAToAddress(address: PokerProtocol.TmpMask);
            e.JumpAbsolute(label: maskDone);
            e.MarkLabel(label: next);
        }

        e.MarkLabel(label: maskDone);
        e.LoadAFromAddress(address: PokerProtocol.TmpMask);
        e.StoreAToAddress(address: PokerProtocol.DiscardMask);
        e.JumpAbsolute(label: m_subReplaceMasked);
    }

    // Replaces TmpSeat's DiscardMask-marked cards from the deck (NextCard onward) and records the count.
    private void EmitReplaceMasked(Sm83Emitter e) {
        e.MarkLabel(label: m_subReplaceMasked);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpVal);

        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.DiscardMask);
            e.ArithmeticImmediate(op: AluOp.And, value: (byte)(1 << slot));
            e.JumpRelative(condition: Condition.Zero, label: skip);
            e.LoadAFromAddress(address: PokerProtocol.NextCard);
            e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.DeckScratch & 0xFF));
            e.Load(destination: Reg8.L, source: Reg8.A);
            e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.DeckScratch >> 8));
            e.Load(destination: Reg8.A, source: Reg8.Memory);
            e.Load(destination: Reg8.C, source: Reg8.A);
            e.Call(label: m_subHandAddr);

            for (var step = 0; (step < slot); step++) {
                e.Increment(pair: Reg16.Hl);
            }

            e.Load(destination: Reg8.Memory, source: Reg8.C);
            e.LoadAFromAddress(address: PokerProtocol.NextCard);
            e.Increment(register: Reg8.A);
            e.StoreAToAddress(address: PokerProtocol.NextCard);
            e.LoadAFromAddress(address: PokerProtocol.TmpVal);
            e.Increment(register: Reg8.A);
            e.StoreAToAddress(address: PokerProtocol.TmpVal);
            e.MarkLabel(label: skip);
        }

        e.LoadAFromAddress(address: PokerProtocol.TmpVal);
        e.Load(destination: Reg8.B, source: Reg8.A);
        EmitStoreSeatByte(e: e, arrayBase: PokerProtocol.DrawCountBase, seatAddress: PokerProtocol.TmpSeat);
        e.Return();
    }

    // Showdown: fresh evaluations for every live seat, the lexicographic winner (category then tiebreaks; a full
    // tie keeps the earliest seat), the reveal repaint (every live AI hand as rank+suit strips), the winner
    // message with its category name, and the award — all direct prints inside one LCD-off window.
    private void EmitShowdown(Sm83Emitter e) {
        e.MarkLabel(label: m_subShowdown);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.Call(label: m_subEvalSeat);
            e.MarkLabel(label: skip);
        }

        // The winner scan.
        e.LoadAImmediate(value: 0xFF);
        e.StoreAToAddress(address: PokerProtocol.WinnerSeat);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var next = e.NewLabel();
            var compare = e.NewLabel();
            var take = e.NewLabel();
            var keep = e.NewLabel();
            var cmpLoop = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpAbsolute(condition: Condition.NotZero, label: next);
            e.LoadAFromAddress(address: PokerProtocol.WinnerSeat);
            e.ArithmeticImmediate(op: AluOp.Compare, value: 0xFF);
            e.JumpRelative(condition: Condition.NotZero, label: compare);
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.WinnerSeat);
            e.JumpAbsolute(label: next);
            e.MarkLabel(label: compare);
            e.LoadAFromAddress(address: PokerProtocol.WinnerSeat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.Call(label: m_subEvalAddr);
            e.Load(destination: Reg8.D, source: Reg8.H);
            e.Load(destination: Reg8.E, source: Reg8.L);
            e.LoadImmediate(pair: Reg16.Hl, value: (ushort)(PokerProtocol.EvalBase + (seat * 8)));
            e.LoadImmediate(destination: Reg8.B, value: 6);
            e.MarkLabel(label: cmpLoop);
            e.LoadAFromDe();
            e.Load(destination: Reg8.C, source: Reg8.A);
            e.Load(destination: Reg8.A, source: Reg8.Memory);
            e.Arithmetic(op: AluOp.Compare, source: Reg8.C);
            e.JumpRelative(condition: Condition.Carry, label: keep);
            e.JumpRelative(condition: Condition.NotZero, label: take);
            e.Increment(pair: Reg16.Hl);
            e.Increment(pair: Reg16.De);
            e.Decrement(register: Reg8.B);
            e.JumpRelative(condition: Condition.NotZero, label: cmpLoop);
            e.JumpRelative(label: keep);
            e.MarkLabel(label: take);
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.WinnerSeat);
            e.MarkLabel(label: keep);
            e.MarkLabel(label: next);
        }

        // The reveal + result card (direct, LCD off).
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();

        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpAbsolute(condition: Condition.NotZero, label: skip);

            for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
                e.LoadAImmediate(value: (byte)PokerTables.RevealRows[seat - 1]);
                e.StoreAToAddress(address: PokerProtocol.TmpRow);
                e.LoadAImmediate(value: (byte)(PokerTables.RevealColumn + (slot * 2)));
                e.StoreAToAddress(address: PokerProtocol.TmpCol);
                e.LoadAFromAddress(address: (ushort)(PokerProtocol.HandBase + (seat * PokerProtocol.HandStride) + slot));
                e.StoreAToAddress(address: PokerProtocol.TmpCard);
                e.Call(label: m_subDrawStrip);
            }

            e.MarkLabel(label: skip);
        }

        e.LoadAFromAddress(address: PokerProtocol.WinnerSeat);
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.Call(label: m_subMsgNameDirect);
        m_fw.Text.EmitPrintDirect(text: m_strWins, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageVerbColumn);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: PokerProtocol.Pot, byteCount: 2, row: PokerProtocol.MessageRow, column: PokerProtocol.MessageAmountColumn);

        // The winning category over the pot label.
        var catDone = e.NewLabel();

        e.Call(label: m_subEvalAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToAddress(address: PokerProtocol.TmpVal);

        for (var category = 0; (category < PokerTables.CategoryCount); category++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.TmpVal);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)category);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            m_fw.Text.EmitPrintDirect(text: m_strCategories[category], row: PokerProtocol.PotRow, column: 6);
            e.JumpAbsolute(label: catDone);
            e.MarkLabel(label: skip);
        }

        e.MarkLabel(label: catDone);
        e.Call(label: m_subAwardPot);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: PokerProtocol.Pot, byteCount: 2, row: PokerProtocol.PotRow, column: PokerProtocol.PotColumn);
        EmitBankrollPrintsDirect(e: e);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.LoadAImmediate(value: PokerProtocol.PhaseHandEnd);
        e.StoreAToAddress(address: PokerProtocol.Phase);
        e.LoadAImmediate(value: PokerProtocol.HandEndFrames);
        e.StoreAToAddress(address: PokerProtocol.EndTimer);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        e.Return();
    }

    // Pays the pot to WinnerSeat, keeps the records (biggest pot, hands won on a player win), rings the win/place
    // sound, zeroes the pot, and persists — unless the attract table is playing (attract never writes SRAM).
    private void EmitAwardPot(Sm83Emitter e) {
        var notBigger = e.NewLabel();
        var newBiggest = e.NewLabel();
        var notPlayer = e.NewLabel();
        var soundDone = e.NewLabel();

        e.MarkLabel(label: m_subAwardPot);
        e.LoadAFromAddress(address: PokerProtocol.Pot);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.BiggestPotMirror);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.Carry, label: newBiggest);
        e.JumpRelative(condition: Condition.NotZero, label: notBigger);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.Pot + 1));
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.BiggestPotMirror + 1));
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpRelative(condition: Condition.NoCarry, label: notBigger);
        e.MarkLabel(label: newBiggest);
        e.LoadAFromAddress(address: PokerProtocol.Pot);
        e.StoreAToAddress(address: PokerProtocol.BiggestPotMirror);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.Pot + 1));
        e.StoreAToAddress(address: (ushort)(PokerProtocol.BiggestPotMirror + 1));
        e.MarkLabel(label: notBigger);
        e.LoadAFromAddress(address: PokerProtocol.WinnerSeat);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: notPlayer);
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.HandsWonMirror + 1));
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: (ushort)(PokerProtocol.HandsWonMirror + 1));
        e.LoadAFromAddress(address: PokerProtocol.HandsWonMirror);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.DecimalAdjustA();
        e.StoreAToAddress(address: PokerProtocol.HandsWonMirror);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Win);
        e.JumpRelative(label: soundDone);
        e.MarkLabel(label: notPlayer);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Place);
        e.MarkLabel(label: soundDone);
        e.Call(label: m_subPotToWinner);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.Pot);
        e.StoreAToAddress(address: (ushort)(PokerProtocol.Pot + 1));
        e.JumpAbsolute(label: m_subSaveIfLive);
    }

    // The hand rotation: advance the dealer, bust the seats that can no longer ante, resolve the session (the
    // player busting loses it; the last AI busting clears the table), otherwise deal and paint the next hand. The
    // attract table never reaches the game-over path — its session simply hands back to the title.
    private void EmitNextHand(Sm83Emitter e) {
        var playerOk = e.NewLabel();
        var loseReal = e.NewLabel();
        var anyAlive = e.NewLabel();
        var winReal = e.NewLabel();

        e.MarkLabel(label: m_subNextHand);
        e.LoadAFromAddress(address: PokerProtocol.DealerSeat);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.And, value: 0x03);
        e.StoreAToAddress(address: PokerProtocol.DealerSeat);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.SeatBusted);
            e.JumpRelative(condition: Condition.Zero, label: skip);
            e.LoadAImmediate(value: (byte)seat);
            e.StoreAToAddress(address: PokerProtocol.TmpSeat);
            e.Call(label: m_subSeatBroke);
            e.Arithmetic(op: AluOp.Or, source: Reg8.A);
            e.JumpRelative(condition: Condition.Zero, label: skip);
            e.LoadAImmediate(value: PokerProtocol.SeatBusted);
            e.StoreAToAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.MarkLabel(label: skip);
        }

        e.LoadAFromAddress(address: PokerProtocol.FoldedBase);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.SeatBusted);
        e.JumpRelative(condition: Condition.NotZero, label: playerOk);
        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.StateAttract);
        e.JumpRelative(condition: Condition.NotZero, label: loseReal);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateTitle);
        e.Return();
        e.MarkLabel(label: loseReal);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.GameOverKind);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateGameOver);
        e.Return();
        e.MarkLabel(label: playerOk);

        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.SeatBusted);
            e.JumpAbsolute(condition: Condition.NotZero, label: anyAlive);
        }

        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.StateAttract);
        e.JumpRelative(condition: Condition.NotZero, label: winReal);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateTitle);
        e.Return();
        e.MarkLabel(label: winReal);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: PokerProtocol.GameOverKind);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateGameOver);
        e.Return();
        e.MarkLabel(label: anyAlive);
        e.Call(label: m_subDealHand);
        e.Call(label: m_subBoardRepaint);
        e.JumpAbsolute(label: m_subStartBetRound);
    }

    // The whole-table repaint (LCD off): the felt (with the baked overlays), every bankroll, the pot, the player's
    // five cards, the dealer markers, and the fold/out tags — the Brickfall line-clear discipline.
    private void EmitBoardRepaint(Sm83Emitter e) {
        e.MarkLabel(label: m_subBoardRepaint);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        e.Call(label: m_subFeltRestore);
        EmitBankrollPrintsDirect(e: e);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: PokerProtocol.Pot, byteCount: 2, row: PokerProtocol.PotRow, column: PokerProtocol.PotColumn);

        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            e.LoadAFromAddress(address: (ushort)(PokerProtocol.HandBase + slot));
            e.StoreAToAddress(address: PokerProtocol.TmpCard);
            e.LoadAImmediate(value: PokerProtocol.PlayerCardRow);
            e.StoreAToAddress(address: PokerProtocol.TmpRow);
            e.LoadAImmediate(value: (byte)(PokerProtocol.PlayerCardColumn + (slot * 3)));
            e.StoreAToAddress(address: PokerProtocol.TmpCol);
            e.Call(label: m_subDrawCardAt);
        }

        // Dealer markers: '>' beside the dealer's name, a space beside everyone else's.
        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var isDealer = e.NewLabel();
            var write = e.NewLabel();
            var row = ((seat == 0) ? 16 : 0);
            var column = ((seat == 0) ? 0 : (PokerTables.OpponentColumns[seat - 1] - 1));

            e.LoadAFromAddress(address: PokerProtocol.DealerSeat);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)seat);
            e.JumpRelative(condition: Condition.Zero, label: isDealer);
            e.LoadImmediate(destination: Reg8.B, value: m_fw.Text.TileFor(character: ' '));
            e.JumpRelative(label: write);
            e.MarkLabel(label: isDealer);
            e.LoadImmediate(destination: Reg8.B, value: m_fw.Text.TileFor(character: '>'));
            e.MarkLabel(label: write);
            e.LoadAImmediate(value: (byte)row);
            e.StoreAToAddress(address: PokerProtocol.TmpRow);
            e.LoadAImmediate(value: (byte)column);
            e.StoreAToAddress(address: PokerProtocol.TmpCol);
            e.Call(label: m_subWriteCell);
        }

        // Fold / out tags for the AI seats.
        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            var folded = e.NewLabel();
            var busted = e.NewLabel();
            var done = e.NewLabel();

            e.LoadAFromAddress(address: (ushort)(PokerProtocol.FoldedBase + seat));
            e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.SeatFolded);
            e.JumpRelative(condition: Condition.Zero, label: folded);
            e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.SeatBusted);
            e.JumpRelative(condition: Condition.Zero, label: busted);
            e.JumpRelative(label: done);
            e.MarkLabel(label: folded);
            m_fw.Text.EmitPrintDirect(text: m_strFold, row: 2, column: PokerTables.OpponentColumns[seat - 1]);
            e.JumpRelative(label: done);
            e.MarkLabel(label: busted);
            m_fw.Text.EmitPrintDirect(text: m_strOut, row: 2, column: PokerTables.OpponentColumns[seat - 1]);
            e.MarkLabel(label: done);
        }

        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.Return();
    }

    // All four bankrolls, direct (LCD off / VBlank only).
    private void EmitBankrollPrintsDirect(Sm83Emitter e) {
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: PokerProtocol.BankrollMirror, byteCount: 2, row: 16, column: 5);

        for (var seat = 1; (seat < PokerProtocol.SeatCount); seat++) {
            m_fw.Text.EmitPrintBcdDirect(bcdAddress: (ushort)(PokerProtocol.BankrollMirror + (seat * 2)), byteCount: 2, row: 1, column: PokerTables.OpponentColumns[seat - 1]);
        }
    }

    // The action menu labels (queued): CHECK/BET/FOLD or CALL/RAISE/FOLD by Facing.
    private void EmitMenuShow(Sm83Emitter e) {
        var facing = e.NewLabel();

        e.MarkLabel(label: m_subMenuShow);
        e.LoadAFromAddress(address: PokerProtocol.Facing);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: facing);

        for (var item = 0; (item < 3); item++) {
            m_fw.Text.EmitPrintQueued(text: m_strMenuOpen[item], row: (PokerProtocol.MenuRow + item), column: (PokerProtocol.MenuCursorColumn + 1));
        }

        e.Return();
        e.MarkLabel(label: facing);

        for (var item = 0; (item < 3); item++) {
            m_fw.Text.EmitPrintQueued(text: m_strMenuFacing[item], row: (PokerProtocol.MenuRow + item), column: (PokerProtocol.MenuCursorColumn + 1));
        }

        e.Return();
    }

    // Drains one pending menu-row clear per frame (the confirm frame's queue budget is already spent).
    private void EmitMenuClearTick(Sm83Emitter e) {
        var rowTwo = e.NewLabel();
        var rowThree = e.NewLabel();
        var advance = e.NewLabel();

        e.MarkLabel(label: m_subMenuClearTick);
        e.LoadAFromAddress(address: PokerProtocol.MenuClearRows);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.NotZero, label: rowTwo);
        m_fw.Text.EmitPrintQueued(text: m_strMenuClear, row: PokerProtocol.MenuRow, column: PokerProtocol.MenuCursorColumn);
        e.JumpRelative(label: advance);
        e.MarkLabel(label: rowTwo);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpRelative(condition: Condition.NotZero, label: rowThree);
        m_fw.Text.EmitPrintQueued(text: m_strMenuClear, row: (PokerProtocol.MenuRow + 1), column: PokerProtocol.MenuCursorColumn);
        e.JumpRelative(label: advance);
        e.MarkLabel(label: rowThree);
        m_fw.Text.EmitPrintQueued(text: m_strMenuClear, row: (PokerProtocol.MenuRow + 2), column: PokerProtocol.MenuCursorColumn);
        e.MarkLabel(label: advance);
        e.LoadAFromAddress(address: PokerProtocol.MenuClearRows);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.MenuClearRows);
        e.Return();
    }

    // The draw-select cursor metasprite over the cursor card (hardware Y 112, X = 16 + 24 × cursor).
    private void EmitCursorSprite(Sm83Emitter e) {
        e.MarkLabel(label: m_subCursorSprite);
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        e.LoadAFromAddress(address: PokerProtocol.DrawCursor);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.B); // ×3.
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×24.
        e.ArithmeticImmediate(op: AluOp.Add, value: 16);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.B, value: 112);
        e.LoadImmediate(pair: Reg16.Hl, value: m_cursor.FrameAddresses[0]);
        m_fw.Oam.EmitDrawMetasprite(baseSlot: m_cursorSlot, spriteCount: m_cursor.FrameEntryCounts[0]);
        e.Return();
    }

    // The message line's actor name (TmpSeat), queued and direct variants.
    private void EmitMsgName(Sm83Emitter e) {
        e.MarkLabel(label: m_subMsgNameQueued);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.TmpSeat);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)seat);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            m_fw.Text.EmitPrintQueued(text: m_strNames[seat], row: PokerProtocol.MessageRow, column: PokerProtocol.MessageNameColumn);
            e.Return();
            e.MarkLabel(label: skip);
        }

        e.Return();
        e.MarkLabel(label: m_subMsgNameDirect);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.TmpSeat);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)seat);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            m_fw.Text.EmitPrintDirect(text: m_strNames[seat], row: PokerProtocol.MessageRow, column: PokerProtocol.MessageNameColumn);
            e.Return();
            e.MarkLabel(label: skip);
        }

        e.Return();
    }

    // TmpSeat's bankroll digits, queued at the seat's chip cell.
    private void EmitChipsQueued(Sm83Emitter e) {
        e.MarkLabel(label: m_subChipsQueued);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            var skip = e.NewLabel();
            var row = ((seat == 0) ? 16 : 1);
            var column = ((seat == 0) ? 5 : PokerTables.OpponentColumns[seat - 1]);

            e.LoadAFromAddress(address: PokerProtocol.TmpSeat);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)seat);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            m_fw.Text.EmitPrintBcdQueued(bcdAddress: (ushort)(PokerProtocol.BankrollMirror + (seat * 2)), byteCount: 2, row: row, column: column);
            e.Return();
            e.MarkLabel(label: skip);
        }

        e.Return();
    }

    // The player's action menu: Up/Down move the cursor (wrap), the '>' glyph redraws through the queue, A
    // confirms — validated against the raise cap and the bankroll (an unaffordable call/raise just buzzes).
    private void EmitPlayerMenuTick(Sm83Emitter e) {
        var noUp = e.NewLabel();
        var upStore = e.NewLabel();
        var noDown = e.NewLabel();
        var downStore = e.NewLabel();
        var noConfirm = e.NewLabel();
        var cursorNotZero = e.NewLabel();
        var cursorTwo = e.NewLabel();
        var actionSet = e.NewLabel();
        var validateCall = e.NewLabel();
        var raiseAfford = e.NewLabel();
        var callAfford = e.NewLabel();
        var valid = e.NewLabel();
        var errorBuzz = e.NewLabel();

        e.MarkLabel(label: m_subPlayerMenuTick);

        // Up.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 2, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noUp);
        e.LoadAFromAddress(address: PokerProtocol.MenuCursor);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: upStore);
        e.LoadAImmediate(value: 3);
        e.MarkLabel(label: upStore);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.MenuCursor);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.MarkLabel(label: noUp);

        // Down.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 3, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noDown);
        e.LoadAFromAddress(address: PokerProtocol.MenuCursor);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 3);
        e.JumpRelative(condition: Condition.Carry, label: downStore);
        e.XorA();
        e.MarkLabel(label: downStore);
        e.StoreAToAddress(address: PokerProtocol.MenuCursor);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectCursor);
        e.MarkLabel(label: noDown);

        // The cursor glyphs.
        for (var item = 0; (item < 3); item++) {
            var isCursor = e.NewLabel();
            var push = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.MenuCursor);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)item);
            e.JumpRelative(condition: Condition.Zero, label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: ' '));
            e.JumpRelative(label: push);
            e.MarkLabel(label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: '>'));
            e.MarkLabel(label: push);
            m_fw.Bg.EmitQueueCell(row: (PokerProtocol.MenuRow + item), column: PokerProtocol.MenuCursorColumn);
        }

        // Confirm (A).
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpAbsolute(condition: Condition.Zero, label: noConfirm);
        e.LoadAFromAddress(address: PokerProtocol.MenuCursor);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: cursorNotZero);
        e.LoadAImmediate(value: PokerProtocol.ActionCheckCall);
        e.JumpRelative(label: actionSet);
        e.MarkLabel(label: cursorNotZero);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 1);
        e.JumpRelative(condition: Condition.NotZero, label: cursorTwo);
        e.LoadAImmediate(value: PokerProtocol.ActionBetRaise);
        e.JumpRelative(label: actionSet);
        e.MarkLabel(label: cursorTwo);
        e.LoadAImmediate(value: PokerProtocol.ActionFold);
        e.MarkLabel(label: actionSet);
        e.StoreAToAddress(address: PokerProtocol.DecisionAction);

        // Validate.
        e.LoadAFromAddress(address: PokerProtocol.DecisionAction);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.ActionBetRaise);
        e.JumpRelative(condition: Condition.NotZero, label: validateCall);
        e.LoadAFromAddress(address: PokerProtocol.RaiseCount);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.RaiseCap);
        e.JumpAbsolute(condition: Condition.NoCarry, label: errorBuzz);
        e.LoadAFromAddress(address: PokerProtocol.RoundBetBase);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Increment(register: Reg8.A);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.Call(label: m_subBankrollAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: raiseAfford);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpAbsolute(condition: Condition.Carry, label: errorBuzz);
        e.MarkLabel(label: raiseAfford);
        e.JumpAbsolute(label: valid);
        e.MarkLabel(label: validateCall);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.ActionCheckCall);
        e.JumpRelative(condition: Condition.NotZero, label: valid);
        e.LoadAFromAddress(address: PokerProtocol.RoundBetBase);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: PokerProtocol.BetLevel);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(condition: Condition.Zero, label: valid);
        e.Shift(op: ShiftOp.Swap, register: Reg8.A);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.TmpSeat);
        e.Call(label: m_subBankrollAddr);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: callAfford);
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        e.JumpAbsolute(condition: Condition.Carry, label: errorBuzz);
        e.MarkLabel(label: callAfford);
        e.MarkLabel(label: valid);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.AwaitInput);
        e.LoadAImmediate(value: 3);
        e.StoreAToAddress(address: PokerProtocol.MenuClearRows);
        e.Call(label: m_subApplyAction);
        e.JumpAbsolute(label: m_subAdvanceTurn);
        e.MarkLabel(label: errorBuzz);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Error);
        e.MarkLabel(label: noConfirm);
        e.Return();
    }

    // Inserts the captured Score + entered initials into the mirror table (sorted, ties keep the older entry) —
    // the shared score-table shape.
    private void EmitHiInsert(Sm83Emitter e) {
        var insertGo = e.NewLabel();
        var noShift = e.NewLabel();
        var shiftLoop = e.NewLabel();

        e.MarkLabel(label: m_subHiInsert);

        for (var slot = 0; (slot < (PokerProtocol.HiScoreEntryCount - 1)); slot++) {
            var take = e.NewLabel();
            var skip = e.NewLabel();
            var entryScore = (ushort)(PokerProtocol.HiScoreMirror + (slot * PokerProtocol.HiScoreEntryByteCount) + 3);

            for (var index = 0; (index < 3); index++) {
                e.LoadAFromAddress(address: (ushort)(PokerProtocol.Score + index));
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
            e.LoadImmediate(destination: Reg8.C, value: (byte)(slot * PokerProtocol.HiScoreEntryByteCount));
            e.JumpAbsolute(label: insertGo);
            e.MarkLabel(label: skip);
        }

        e.LoadImmediate(destination: Reg8.C, value: (byte)((PokerProtocol.HiScoreEntryCount - 1) * PokerProtocol.HiScoreEntryByteCount));
        e.MarkLabel(label: insertGo);

        e.LoadAImmediate(value: (byte)((PokerProtocol.HiScoreEntryCount - 1) * PokerProtocol.HiScoreEntryByteCount));
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(condition: Condition.Zero, label: noShift);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadImmediate(pair: Reg16.Hl, value: (ushort)(PokerProtocol.HiScoreMirror + (4 * PokerProtocol.HiScoreEntryByteCount) - 1));
        e.LoadImmediate(pair: Reg16.De, value: (ushort)(PokerProtocol.HiScoreMirror + (5 * PokerProtocol.HiScoreEntryByteCount) - 1));
        e.MarkLabel(label: shiftLoop);
        e.LoadAFromHlDecrement();
        e.StoreAToDe();
        e.Decrement(pair: Reg16.De);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: shiftLoop);
        e.MarkLabel(label: noShift);

        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(PokerProtocol.HiScoreMirror & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(PokerProtocol.HiScoreMirror >> 8));

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(PokerProtocol.EntryGlyphs + index));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            e.StoreAToHlIncrement();
        }

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(PokerProtocol.Score + index));
            e.StoreAToHlIncrement();
        }

        e.Return();
    }

    // Persists the mirror — unless the attract table is playing (attract never writes SRAM).
    private void EmitSaveIfLive(Sm83Emitter e) {
        e.MarkLabel(label: m_subSaveIfLive);
        e.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.StateAttract);
        e.Return(condition: Condition.Zero);
        m_fw.Save.EmitStore();
        e.Return();
    }

    // The shared per-frame play simulation (Play ticks it directly; Attract ticks it under the input script):
    // pending menu clears, the pause gate, then the phase dispatch.
    private void EmitPlayCore(Sm83Emitter e) {
        var noClear = e.NewLabel();
        var noPause = e.NewLabel();
        var dispatchDone = e.NewLabel();

        e.MarkLabel(label: m_subPlayCore);
        e.LoadAFromAddress(address: PokerProtocol.MenuClearRows);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noClear);
        e.Call(label: m_subMenuClearTick);
        e.MarkLabel(label: noClear);

        // START pauses everywhere except the discard selection (where START confirms the draw).
        e.LoadAFromAddress(address: PokerProtocol.AwaitInput);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpRelative(condition: Condition.Zero, label: noPause);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noPause);
        m_fw.States.EmitRequestState(id: PokerProtocol.StatePause);
        e.Return();
        e.MarkLabel(label: noPause);

        e.LoadAFromAddress(address: PokerProtocol.Phase);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A); // PhaseBet1 == 0.
        e.JumpAbsolute(condition: Condition.Zero, label: m_subBettingTick);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.PhaseDraw);
        e.JumpAbsolute(condition: Condition.Zero, label: m_subDrawTick);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.PhaseBet2);
        e.JumpAbsolute(condition: Condition.Zero, label: m_subBettingTick);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PokerProtocol.PhaseHandEnd);
        e.JumpAbsolute(condition: Condition.Zero, label: m_subHandEndWait);
        e.MarkLabel(label: dispatchDone);
        e.Return();
    }

    // The resolved-hand wait: A skips ahead, the timer otherwise runs the table on its own.
    private void EmitHandEndWait(Sm83Emitter e) {
        var go = e.NewLabel();

        e.MarkLabel(label: m_subHandEndWait);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: go);
        e.LoadAFromAddress(address: PokerProtocol.EndTimer);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.EndTimer);
        e.JumpRelative(condition: Condition.Zero, label: go);
        e.Return();
        e.MarkLabel(label: go);
        e.JumpAbsolute(label: m_subNextHand);
    }

    // ==== The seven states. ==============================================================================================

    private void EmitTitleEnter(Sm83Emitter e) {
        // The trio's title-always-quiet invariant, plus a mirror refresh so an attract session never leaks its
        // in-memory chips into a real one.
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_fw.Input.EmitScriptStop();
        m_fw.Save.EmitLoad();
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_titleMap.Address);
        EmitAttributesPaint(e: e, attributes: m_titleAttributes);
        m_menu.EmitEnterDraw(e: e);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.IdleTimer);
        e.StoreAToAddress(address: PokerProtocol.IdleTimerHigh);
    }

    private void EmitTitleTick(Sm83Emitter e) {
        var stay = e.NewLabel();

        m_menu.EmitTick(e: e, emitConfirm: (emitter, index) => {
            if (index == 0) {
                // The D4 input-entropy seed — the frame counter is sampled at THIS confirm edge — then the session.
                m_fw.Prng.EmitSeedFromFrameCounter();
                emitter.Call(label: m_subSessionReset);
                emitter.LoadAImmediate(value: 1);
                emitter.StoreAToAddress(address: PokerProtocol.PendingDeal);
                m_fw.States.EmitRequestState(id: PokerProtocol.StatePlay);
                emitter.Return();
            }
            else {
                m_fw.States.EmitRequestState(id: PokerProtocol.StateHighScores);
                emitter.Return();
            }
        });
        EmitIdleAdvance(e: e, stayLabel: stay);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateAttract);
        e.MarkLabel(label: stay);
    }

    private void EmitAttractEnter(Sm83Emitter e) {
        // The constant seed makes the scripted table identical every time; attract never writes SRAM (the mirror
        // is refreshed on the way back to the title).
        e.LoadAImmediate(value: AttractSeedLow);
        e.StoreAToAddress(address: FrameworkMemoryMap.PrngState);
        e.LoadAImmediate(value: AttractSeedHigh);
        e.StoreAToAddress(address: FrameworkMemoryMap.PrngStateHigh);
        e.Call(label: m_subSessionReset);
        m_fw.Input.EmitScriptStart(script: m_attractScript);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitAttributesPaint(e: e, attributes: m_playAttributes);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.Call(label: m_subBoardRepaint);
        e.Call(label: m_subStartBetRound);
    }

    private void EmitAttractTick(Sm83Emitter e) {
        var noReal = e.NewLabel();
        var running = e.NewLabel();

        // Any REAL press hands the machine back to the title.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputRaw);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noReal);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: PokerProtocol.StateTitle);
        e.Return();

        e.MarkLabel(label: noReal);
        e.LoadAFromAddress(address: FrameworkMemoryMap.ScriptEnded);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: running);
        m_fw.Input.EmitScriptStop();
        m_fw.States.EmitRequestState(id: PokerProtocol.StateTitle);
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
        m_fw.Text.EmitPrintDirect(text: m_strHiScores, row: 1, column: 4);

        for (var entry = 0; (entry < PokerProtocol.HiScoreEntryCount); entry++) {
            var row = (3 + (entry * 2));
            var entryBase = (ushort)(PokerProtocol.HiScoreMirror + (entry * PokerProtocol.HiScoreEntryByteCount));

            for (var glyph = 0; (glyph < 3); glyph++) {
                e.LoadAFromAddress(address: (ushort)(entryBase + glyph));
                e.StoreAToAddress(address: Hw.MapCell(row: row, column: (4 + glyph)));
            }

            m_fw.Text.EmitPrintBcdDirect(bcdAddress: (ushort)(entryBase + 3), byteCount: 3, row: row, column: 9);
        }

        m_fw.Text.EmitPrintDirect(text: m_strHandsWon, row: 14, column: 2);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: PokerProtocol.HandsWonMirror, byteCount: 2, row: 14, column: 13);
        m_fw.Text.EmitPrintDirect(text: m_strTopPot, row: 16, column: 2);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: PokerProtocol.BiggestPotMirror, byteCount: 2, row: 16, column: 13);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.IdleTimer);
        e.StoreAToAddress(address: PokerProtocol.IdleTimerHigh);
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
        m_fw.States.EmitRequestState(id: PokerProtocol.StateTitle);
        e.MarkLabel(label: stay);
    }

    private void EmitPlayEnter(Sm83Emitter e) {
        var noDeal = e.NewLabel();
        var resumeMenu = e.NewLabel();
        var resumeMarkers = e.NewLabel();
        var resumeDone = e.NewLabel();

        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitCopyMap(sourceAddress: m_playMap.Address);
        EmitAttributesPaint(e: e, attributes: m_playAttributes);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
        e.Call(label: m_subBoardRepaint);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicLoop);

        // A fresh session runs the first betting round exactly once; a pause resume instead restores the
        // in-flight UI (the menu labels, or the discard markers) the repaint wiped.
        e.LoadAFromAddress(address: PokerProtocol.PendingDeal);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: noDeal);
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.PendingDeal);
        e.Call(label: m_subStartBetRound);
        e.Return();
        e.MarkLabel(label: noDeal);
        e.LoadAFromAddress(address: PokerProtocol.AwaitInput);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 1);
        e.JumpRelative(condition: Condition.Zero, label: resumeMenu);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 2);
        e.JumpRelative(condition: Condition.Zero, label: resumeMarkers);
        e.JumpAbsolute(label: resumeDone);
        e.MarkLabel(label: resumeMenu);
        e.Call(label: m_subMenuShow);
        e.JumpAbsolute(label: resumeDone);
        e.MarkLabel(label: resumeMarkers);

        for (var slot = 0; (slot < PokerProtocol.HandSize); slot++) {
            var off = e.NewLabel();
            var push = e.NewLabel();

            e.LoadAFromAddress(address: PokerProtocol.DiscardMask);
            e.ArithmeticImmediate(op: AluOp.And, value: (byte)(1 << slot));
            e.JumpRelative(condition: Condition.Zero, label: off);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: 'X'));
            e.JumpRelative(label: push);
            e.MarkLabel(label: off);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: ' '));
            e.MarkLabel(label: push);
            m_fw.Bg.EmitQueueCell(row: PokerProtocol.MarkerRow, column: (PokerProtocol.PlayerCardColumn + (slot * 3)));
        }

        e.MarkLabel(label: resumeDone);
    }

    private void EmitPlayTick(Sm83Emitter e) {
        ArgumentNullException.ThrowIfNull(e);
        e.Call(label: m_subPlayCore);
    }

    private void EmitPauseEnter(Sm83Emitter e) {
        ArgumentNullException.ThrowIfNull(e);
        m_fw.Text.EmitPrintQueued(text: m_strPause, row: (PokerProtocol.MenuRow + 1), column: PokerProtocol.MenuCursorColumn);
    }

    private void EmitPauseTick(Sm83Emitter e) {
        var resume = e.NewLabel();
        var noAbandon = e.NewLabel();

        // The simulation halts BY CONSTRUCTION here; START resumes, SELECT abandons to the title (the chips as
        // they stand persist — the antes and bets already paid stay paid).
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resume);
        e.TestBit(bit: 6, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noAbandon);
        e.Call(label: m_subSaveIfLive);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateTitle);
        e.Return();
        e.MarkLabel(label: resume);
        m_fw.States.EmitRequestState(id: PokerProtocol.StatePlay);
        e.MarkLabel(label: noAbandon);
    }

    private void EmitGameOverEnter(Sm83Emitter e) {
        var cleared = e.NewLabel();
        var messageDone = e.NewLabel();

        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);

        // Capture the final stack as the session score BEFORE anything resets.
        e.XorA();
        e.StoreAToAddress(address: PokerProtocol.Score);
        e.LoadAFromAddress(address: PokerProtocol.BankrollMirror);
        e.StoreAToAddress(address: (ushort)(PokerProtocol.Score + 1));
        e.LoadAFromAddress(address: (ushort)(PokerProtocol.BankrollMirror + 1));
        e.StoreAToAddress(address: (ushort)(PokerProtocol.Score + 2));

        e.LoadAFromAddress(address: PokerProtocol.GameOverKind);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: cleared);
        m_fw.Text.EmitPrintQueued(text: m_strBusted, row: PokerProtocol.MessageRow, column: 1);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.EffectOver);
        e.JumpRelative(label: messageDone);
        e.MarkLabel(label: cleared);
        m_fw.Text.EmitPrintQueued(text: m_strCleared, row: PokerProtocol.MessageRow, column: 1);
        m_fw.Sound.EmitEffect(emitter: e, effectId: CardSfx.Win);
        e.MarkLabel(label: messageDone);
        e.LoadAImmediate(value: PokerProtocol.GameOverFrames);
        e.StoreAToAddress(address: PokerProtocol.EndTimer);
    }

    private void EmitGameOverTick(Sm83Emitter e) {
        var resolve = e.NewLabel();
        var qualify = e.NewLabel();
        var noQualify = e.NewLabel();
        var entry4Score = (ushort)(PokerProtocol.HiScoreMirror + ((PokerProtocol.HiScoreEntryCount - 1) * PokerProtocol.HiScoreEntryByteCount) + 3);

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: resolve);
        e.LoadAFromAddress(address: PokerProtocol.EndTimer);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: PokerProtocol.EndTimer);
        e.JumpRelative(condition: Condition.Zero, label: resolve);
        e.Return();

        // The session is over: the table's chips reset to the defaults and persist, then a final stack STRICTLY
        // greater than the table's fifth entry earns initials entry.
        e.MarkLabel(label: resolve);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            e.LoadAImmediate(value: 0x02);
            e.StoreAToAddress(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2)));
            e.XorA();
            e.StoreAToAddress(address: (ushort)(PokerProtocol.BankrollMirror + (seat * 2) + 1));
        }

        e.Call(label: m_subSaveIfLive);

        for (var index = 0; (index < 3); index++) {
            e.LoadAFromAddress(address: (ushort)(PokerProtocol.Score + index));
            e.Load(destination: Reg8.B, source: Reg8.A);
            e.LoadAFromAddress(address: (ushort)(entry4Score + index));
            e.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            e.JumpRelative(condition: Condition.Carry, label: qualify);

            if (index < 2) {
                e.JumpRelative(condition: Condition.NotZero, label: noQualify);
            }
        }

        e.MarkLabel(label: noQualify);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateHighScores);
        e.Return();

        e.MarkLabel(label: qualify);
        m_fw.States.EmitRequestState(id: PokerProtocol.StateScoreEntry);
    }

    private void EmitScoreEntryEnter(Sm83Emitter e) {
        m_fw.Oam.EmitHideRange(baseSlot: m_cursorSlot, count: m_cursorMaxEntries);
        m_pad.EmitEnterReset(e: e);
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        EmitAttributesPaint(e: e, attributes: null);
        m_fw.Text.EmitPrintDirect(text: m_strNewHigh, row: 4, column: 4);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: PokerProtocol.Score, byteCount: 3, row: 6, column: 7);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }

    private void EmitScoreEntryTick(Sm83Emitter e) {
        m_pad.EmitTick(e: e, emitConfirm: emitter => {
            emitter.Call(label: m_subHiInsert);
            m_fw.Save.EmitStore();
            m_fw.States.EmitRequestState(id: PokerProtocol.StateHighScores);
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
        e.LoadAFromAddress(address: PokerProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Add, value: 1);
        e.StoreAToAddress(address: PokerProtocol.IdleTimer);
        e.LoadAFromAddress(address: PokerProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.AddWithCarry, value: 0);
        e.StoreAToAddress(address: PokerProtocol.IdleTimerHigh);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdHigh);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
        e.LoadAFromAddress(address: PokerProtocol.IdleTimer);
        e.ArithmeticImmediate(op: AluOp.Compare, value: IdleThresholdLow);
        e.JumpRelative(condition: Condition.NotZero, label: stayLabel);
    }
}
