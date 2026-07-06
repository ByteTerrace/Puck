using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared constants of the five-star Solitaire cartridge: state ids, the game-owned work-RAM layout (everything
/// at <see cref="FrameworkMemoryMap.GameRam"/> and above), the pile model, and the board geometry. The self-verify
/// battery reads the SAME constants it drives the ROM against, so the C# oracle and the SM83 game can never drift.
///
/// The pile model: twelve card arrays at <see cref="PileBase"/> (stride <see cref="PileStride"/>) — pile 0 is the
/// COMBINED stock-and-waste (dealt once; <see cref="WastePos"/> splits it: bytes below are the waste in draw order,
/// bytes above are the stock; a recycle just zeroes the split), piles 1..4 are the foundations, piles 5..11 the
/// tableau columns. Per-pile counts live at <see cref="CountsBase"/> and the tableau face-up tail lengths at
/// <see cref="FaceUpBase"/>. Cursor POSITIONS (0 stock, 1 waste, 2..5 foundations, 6..12 tableau) map onto piles
/// through the manifest's position record table.
/// </summary>
internal static class SolitaireProtocol {
    // State ids.
    /// <summary>The title screen (the card menu).</summary>
    public const byte StateTitle = 0;
    /// <summary>The scripted attract loop (a real play tick over a constant-seed deal; never writes SRAM).</summary>
    public const byte StateAttract = 1;
    /// <summary>The battery-backed high-score table (and streak) screen.</summary>
    public const byte StateHighScores = 2;
    /// <summary>Live play.</summary>
    public const byte StatePlay = 3;
    /// <summary>Paused (the play tick never runs; SELECT abandons to the title, breaking the streak).</summary>
    public const byte StatePause = 4;
    /// <summary>The win fanfare card (streak++, persisted).</summary>
    public const byte StateWin = 5;
    /// <summary>Initials entry for a qualifying score.</summary>
    public const byte StateScoreEntry = 6;

    // Game work RAM (0xC200+).
    /// <summary>The cursor's board position (0 = stock, 1 = waste, 2..5 = foundations, 6..12 = tableau).</summary>
    public const ushort CursorPos = 0xC200;
    /// <summary>The carried run length (0 = not carrying; A on the source cycles it through the face-up tail).</summary>
    public const ushort CarryDepth = 0xC201;
    /// <summary>The position the carry was picked from.</summary>
    public const ushort CarrySrc = 0xC202;
    /// <summary>The stock/waste split: how many of pile 0's cards are face-up in the waste.</summary>
    public const ushort WastePos = 0xC203;
    /// <summary>The score: three packed-BCD bytes, most significant first.</summary>
    public const ushort Score = 0xC204;
    /// <summary>The title/high-score idle counter (low byte; 600 idle frames start the attract loop).</summary>
    public const ushort IdleTimer = 0xC209;
    /// <summary>The idle counter's high byte.</summary>
    public const ushort IdleTimerHigh = 0xC20A;
    /// <summary>Frames left on the win card before it resolves on its own.</summary>
    public const ushort WinTimer = 0xC20B;
    /// <summary>The initials-entry slot cursor (0..2).</summary>
    public const ushort EntryCursor = 0xC20C;
    /// <summary>The three initials as letter indices (0 = A .. 25 = Z).</summary>
    public const ushort EntryGlyphs = 0xC20D;
    /// <summary>The title menu cursor (0 = new deal, 1 = scores).</summary>
    public const ushort MenuCursor = 0xC210;

    // Move/loop scratch.
    /// <summary>The move in flight: the source pile id.</summary>
    public const ushort MoveSrc = 0xC211;
    /// <summary>The move in flight: the destination pile id.</summary>
    public const ushort MoveDst = 0xC212;
    /// <summary>The move in flight: how many cards.</summary>
    public const ushort MoveCount = 0xC213;
    /// <summary>The move in flight: whether it flipped the source's new tail card.</summary>
    public const ushort MoveFlip = 0xC214;
    /// <summary>The move in flight: the BCD score delta it awarded.</summary>
    public const ushort MoveScore = 0xC215;
    /// <summary>Scratch: the card id being examined.</summary>
    public const ushort TmpCard = 0xC216;
    /// <summary>Scratch: the shuffle's loop index / general loop index i.</summary>
    public const ushort IdxI = 0xC217;
    /// <summary>Scratch: the shuffle's drawn index / general loop index j.</summary>
    public const ushort IdxJ = 0xC218;
    /// <summary>Scratch: the map row for cell writes.</summary>
    public const ushort TmpRow = 0xC219;
    /// <summary>Scratch: the map column for cell writes.</summary>
    public const ushort TmpCol = 0xC21A;
    /// <summary>Scratch: a pile id for the pile-address subroutines.</summary>
    public const ushort TmpPile = 0xC21B;
    /// <summary>Scratch: an index within a pile for the pile-address subroutines.</summary>
    public const ushort TmpIdx = 0xC21C;
    /// <summary>Scratch: the tableau column being drawn (0..6).</summary>
    public const ushort TabIdx = 0xC21D;
    /// <summary>Scratch: the drawn column's face-down count.</summary>
    public const ushort TmpDown = 0xC21E;
    /// <summary>Scratch: the drawn column's clipped-card count.</summary>
    public const ushort TmpSkip = 0xC21F;
    /// <summary>Scratch: the drawn column's current map row.</summary>
    public const ushort TmpY = 0xC220;
    /// <summary>Scratch: the examined card's rank (from its record).</summary>
    public const ushort TmpRank = 0xC221;
    /// <summary>Scratch: the examined card's suit.</summary>
    public const ushort TmpSuit = 0xC222;
    /// <summary>Scratch: the examined card's red flag.</summary>
    public const ushort TmpRed = 0xC223;
    /// <summary>Scratch: the examined card's rank-corner tile.</summary>
    public const ushort TmpRankTile = 0xC224;
    /// <summary>Scratch: the drawn column's count copy.</summary>
    public const ushort TmpCount = 0xC225;
    /// <summary>Scratch: a second card id (the drop target's top card).</summary>
    public const ushort TmpCard2 = 0xC226;
    /// <summary>Scratch: the pending state to enter after the win card resolves.</summary>
    public const ushort TmpFlag = 0xC227;
    /// <summary>Scratch: the drawn column's board column (survives the per-cell scratch churn).</summary>
    public const ushort TmpColumn = 0xC22E;
    /// <summary>Scratch: the cursor sprite's computed hardware Y.</summary>
    public const ushort TmpSpriteY = 0xC22F;

    // The undo ring (the card layer's fixed-size mechanism).
    /// <summary>The undo staging record: op (0 = move, 1 = draw, 2 = recycle), src, dst, count, flip, score delta.</summary>
    public const ushort UndoStaging = 0xC228;
    /// <summary>The undo ring's head byte.</summary>
    public const ushort UndoHead = 0xC230;
    /// <summary>The undo ring's live count.</summary>
    public const ushort UndoCount = 0xC231;
    /// <summary>The undo ring's records (8 × 6 bytes).</summary>
    public const ushort UndoRing = 0xC232;
    /// <summary>Records in the undo ring.</summary>
    public const int UndoCapacity = 8;
    /// <summary>Bytes per undo record.</summary>
    public const int UndoStride = 6;

    // The pile model.
    /// <summary>The per-pile card counts (12 bytes: stock, 4 foundations, 7 tableau).</summary>
    public const ushort CountsBase = 0xC270;
    /// <summary>The tableau face-up tail lengths (7 bytes).</summary>
    public const ushort FaceUpBase = 0xC280;
    /// <summary>The 52-byte deal buffer the shuffle runs over (page-aligned enough for 8-bit walks).</summary>
    public const ushort DeckScratch = 0xC2A0;
    /// <summary>The win waterfall's tumbling card: screen Y in pixels (the verifier reads this to prove the
    /// celebration is alive).</summary>
    public const ushort WinCardY = 0xC2E0;
    /// <summary>The win waterfall's tumbling card: screen X in pixels.</summary>
    public const ushort WinCardX = 0xC2E1;
    /// <summary>The win waterfall's tumbling card: signed Y velocity (pixels/frame).</summary>
    public const ushort WinCardVy = 0xC2E2;
    /// <summary>The win waterfall's tumbling card: signed X velocity (pixels/frame).</summary>
    public const ushort WinCardVx = 0xC2E3;
    /// <summary>The win waterfall's tumbling card: frame phase (gravity cadence + the tumble's flip cycle).</summary>
    public const ushort WinCardPhase = 0xC2E4;
    /// <summary>The pile card arrays (12 × <see cref="PileStride"/> bytes).</summary>
    public const ushort PileBase = 0xC300;
    /// <summary>Bytes per pile array (pile 0's 24 stock cards fit exactly).</summary>
    public const int PileStride = 24;
    /// <summary>The number of pile arrays.</summary>
    public const int PileCount = 12;
    /// <summary>The stock/waste pile id.</summary>
    public const byte PileStock = 0;
    /// <summary>The first foundation pile id.</summary>
    public const byte PileFoundationBase = 1;
    /// <summary>The first tableau pile id.</summary>
    public const byte PileTableauBase = 5;

    // Cursor positions.
    /// <summary>The number of cursor positions.</summary>
    public const int PositionCount = 13;
    /// <summary>The stock position.</summary>
    public const byte PositionStock = 0;
    /// <summary>The waste position.</summary>
    public const byte PositionWaste = 1;
    /// <summary>The first foundation position.</summary>
    public const byte PositionFoundationBase = 2;
    /// <summary>The first tableau position.</summary>
    public const byte PositionTableauBase = 6;

    // Board geometry (map cells).
    /// <summary>The top area's first map row (stock/waste/foundations render as 2×2 cards on rows 0..1).</summary>
    public const int TopRow = 0;
    /// <summary>The HUD text row between the top area and the tableau.</summary>
    public const int HudRow = 2;
    /// <summary>The tableau's first map row.</summary>
    public const int TableauTopRow = 3;
    /// <summary>The tableau rows available before a tall column clips (rows 3..17).</summary>
    public const int TableauRows = 15;
    /// <summary>The board's width in map columns (columns 0..15 repaint; 16..19 stay felt).</summary>
    public const int BoardColumns = 16;

    /// <summary>The high-score table's work-RAM mirror (the framework save mirror).</summary>
    public const ushort HiScoreMirror = FrameworkMemoryMap.SaveMirror;
    /// <summary>The number of table entries.</summary>
    public const int HiScoreEntryCount = 5;
    /// <summary>Bytes per entry: three initials (font tile ids) + a three-byte BCD score, most significant first.</summary>
    public const int HiScoreEntryByteCount = 6;
    /// <summary>The persisted current win streak (packed BCD, part of the save payload after the score table).</summary>
    public const ushort StreakMirror = (ushort)(HiScoreMirror + (HiScoreEntryCount * HiScoreEntryByteCount));
    /// <summary>The persisted best win streak (packed BCD).</summary>
    public const ushort BestStreakMirror = (ushort)(StreakMirror + 1);
    /// <summary>The save payload's byte count (the score table + streak + best streak).</summary>
    public const int SavePayloadByteCount = ((HiScoreEntryCount * HiScoreEntryByteCount) + 2);
}
