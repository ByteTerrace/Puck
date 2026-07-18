using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared constants of the five-star five-card-draw Poker cartridge: state ids, the in-hand phase ids, the
/// game-owned work-RAM layout (everything at <see cref="FrameworkMemoryMap.GameRam"/> and above), the table facts
/// (four seats, fixed-limit betting in ten-chip units, a five-chip ante), and the save-payload layout. The
/// self-verify battery reads the SAME constants it drives the ROM against, so the C# oracle and the SM83 game can
/// never drift.
///
/// The table model: seat 0 is the player, seats 1..3 the data-table AI opponents. Each hand: ante → the shared
/// Fisher–Yates deal (5 cards per seat, block order) → a fixed-limit betting round → a draw phase (0..5 discards)
/// → a second betting round → showdown (full evaluation, high card through straight flush). Chips are packed BCD
/// throughout. The <see cref="DecisionAction"/> byte is THE opponent seam: hand strength + table state flow in
/// through <see cref="DecisionSeat"/>/<see cref="DecisionStrength"/>/<see cref="DecisionFacing"/>/<see cref="DecisionRaises"/>,
/// one action byte flows out. Link-fed actions can substitute at this
/// seam without restructuring the table.
/// </summary>
internal static class PokerProtocol {
    // State ids.
    /// <summary>The title screen (the card menu).</summary>
    public const byte StateTitle = 0;
    /// <summary>The scripted attract loop (a real four-AI table over a constant seed; never writes SRAM).</summary>
    public const byte StateAttract = 1;
    /// <summary>The battery-backed best-stacks table (and hands-won / top-pot records) screen.</summary>
    public const byte StateHighScores = 2;
    /// <summary>Live play.</summary>
    public const byte StatePlay = 3;
    /// <summary>Paused (the play tick never runs; SELECT abandons to the title).</summary>
    public const byte StatePause = 4;
    /// <summary>The session end card (the player busted, or cleared the table).</summary>
    public const byte StateGameOver = 5;
    /// <summary>Initials entry for a qualifying final stack.</summary>
    public const byte StateScoreEntry = 6;

    // In-hand phase ids (the play state's sub-machine).
    /// <summary>The first betting round (post-deal).</summary>
    public const byte PhaseBet1 = 0;
    /// <summary>The draw phase (each active seat replaces 0..5 cards, in seat order from the dealer's left).</summary>
    public const byte PhaseDraw = 1;
    /// <summary>The second betting round (post-draw).</summary>
    public const byte PhaseBet2 = 2;
    /// <summary>The hand has resolved (showdown or uncontested); waiting out the result card.</summary>
    public const byte PhaseHandEnd = 3;

    // Decision-seam actions (the opponent seam's output vocabulary).
    /// <summary>Fold.</summary>
    public const byte ActionFold = 0;
    /// <summary>Check when nothing is outstanding, call otherwise.</summary>
    public const byte ActionCheckCall = 1;
    /// <summary>Open a bet when nothing is outstanding, raise otherwise.</summary>
    public const byte ActionBetRaise = 2;

    // Last-action display ids (per seat, for the verifier and the table read).
    /// <summary>No action yet this round.</summary>
    public const byte ActedNone = 0;
    /// <summary>Checked.</summary>
    public const byte ActedCheck = 1;
    /// <summary>Opened the betting.</summary>
    public const byte ActedBet = 2;
    /// <summary>Called.</summary>
    public const byte ActedCall = 3;
    /// <summary>Raised.</summary>
    public const byte ActedRaise = 4;
    /// <summary>Folded.</summary>
    public const byte ActedFold = 5;

    // Seat status values (the Folded array).
    /// <summary>In the hand.</summary>
    public const byte SeatActive = 0;
    /// <summary>Folded this hand.</summary>
    public const byte SeatFolded = 1;
    /// <summary>Busted out of the session (sits out every hand).</summary>
    public const byte SeatBusted = 2;

    // Table facts.
    /// <summary>Seats at the table (the player plus three AI opponents).</summary>
    public const int SeatCount = 4;
    /// <summary>Cards per hand.</summary>
    public const int HandSize = 5;
    /// <summary>The ante, as packed BCD chips.</summary>
    public const byte AnteBcd = 0x05;
    /// <summary>One betting unit, as packed BCD chips (fixed-limit: every bet/raise is ten chips).</summary>
    public const byte BetUnitBcd = 0x10;
    /// <summary>The bet-level ceiling per round (the opening bet plus three raises).</summary>
    public const byte RaiseCap = 4;
    /// <summary>A seat needs at least the ante to play the next hand; below this it busts.</summary>
    public const byte BustThresholdBcd = AnteBcd;

    // Timing (frames).
    /// <summary>Frames an AI seat waits before acting (so a human can read the table).</summary>
    public const byte AiDelayFrames = 24;
    /// <summary>Frames between the end of one round and the next seat acting.</summary>
    public const byte RoundGapFrames = 30;
    /// <summary>Frames the resolved hand (showdown or uncontested) stays on screen before the next deal.</summary>
    public const byte HandEndFrames = 150;
    /// <summary>Frames the session-end card stays before resolving on its own.</summary>
    public const byte GameOverFrames = 240;

    // Game work RAM (0xC200+).
    /// <summary>The in-hand phase (<see cref="PhaseBet1"/>..<see cref="PhaseHandEnd"/>).</summary>
    public const ushort Phase = 0xC200;
    /// <summary>The seat whose turn it is.</summary>
    public const ushort ActorSeat = 0xC201;
    /// <summary>What the game is waiting on: 0 = nothing (AI timers run), 1 = the player's action menu, 2 = the
    /// player's discard selection.</summary>
    public const ushort AwaitInput = 0xC202;
    /// <summary>The action menu cursor (0..2).</summary>
    public const ushort MenuCursor = 0xC203;
    /// <summary>The current bet level this round, in betting units (0..<see cref="RaiseCap"/>).</summary>
    public const ushort BetLevel = 0xC204;
    /// <summary>Bets/raises used this round (the opening bet counts; capped at <see cref="RaiseCap"/>).</summary>
    public const ushort RaiseCount = 0xC205;
    /// <summary>Active seats that still owe an action this round (0 = the round is over).</summary>
    public const ushort ToActCount = 0xC206;
    /// <summary>The dealer seat (rotates left each hand; first to act is the dealer's left).</summary>
    public const ushort DealerSeat = 0xC207;
    /// <summary>The pot: two packed-BCD bytes, most significant first.</summary>
    public const ushort Pot = 0xC208;
    /// <summary>Frames left before the current AI seat acts.</summary>
    public const ushort DelayTimer = 0xC20A;
    /// <summary>The draw-phase card cursor (0..4).</summary>
    public const ushort DrawCursor = 0xC20B;
    /// <summary>The draw-phase discard mask (bit k = replace card k).</summary>
    public const ushort DiscardMask = 0xC20C;
    /// <summary>The first seat to act this hand (the dealer's left).</summary>
    public const ushort FirstSeat = 0xC20D;
    /// <summary>Seats still in the hand (not folded, not busted).</summary>
    public const ushort InHand = 0xC20E;
    /// <summary>The seat that won the hand (valid from the hand's resolution).</summary>
    public const ushort WinnerSeat = 0xC20F;
    /// <summary>The title/high-score idle counter (low byte; 600 idle frames start the attract loop).</summary>
    public const ushort IdleTimer = 0xC210;
    /// <summary>The idle counter's high byte.</summary>
    public const ushort IdleTimerHigh = 0xC211;
    /// <summary>Frames left on the hand-end / game-over wait.</summary>
    public const ushort EndTimer = 0xC212;
    /// <summary>The initials-entry slot cursor (0..2).</summary>
    public const ushort EntryCursor = 0xC213;
    /// <summary>The three initials as letter indices (0 = A .. 25 = Z).</summary>
    public const ushort EntryGlyphs = 0xC214;
    /// <summary>The title menu cursor (0 = deal, 1 = scores).</summary>
    public const ushort TitleCursor = 0xC217;
    /// <summary>How the session ended (0 = the player busted, 1 = the table was cleared).</summary>
    public const ushort GameOverKind = 0xC218;
    /// <summary>The next undealt card's index into the deck scratch (draw replacements consume from here).</summary>
    public const ushort NextCard = 0xC219;
    /// <summary>Whether the player's menu faces an outstanding bet (0 = check/bet labels, 1 = call/raise).</summary>
    public const ushort Facing = 0xC21A;
    /// <summary>Pending action-menu row clears (drained one row per frame to respect the queue budget).</summary>
    public const ushort MenuClearRows = 0xC21B;
    /// <summary>The captured final score (three packed-BCD bytes, most significant first).</summary>
    public const ushort Score = 0xC21C;
    /// <summary>Set by the title's confirm so the play enter runs the first betting round exactly once (a pause
    /// resume re-enters the play state without restarting the round).</summary>
    public const ushort PendingDeal = 0xC21F;
    /// <summary>Increments on every applied table action — the verifier's turn-by-turn observation point.</summary>
    public const ushort TurnSerial = 0xC220;
    /// <summary>The seat whose action <see cref="TurnSerial"/> last counted.</summary>
    public const ushort LastActor = 0xC221;

    // Scratch.
    /// <summary>Scratch: the seat the pile/eval subroutines operate on.</summary>
    public const ushort TmpSeat = 0xC222;
    /// <summary>Scratch: the card id being examined.</summary>
    public const ushort TmpCard = 0xC223;
    /// <summary>Scratch: the examined card's rank (1..13, from its record).</summary>
    public const ushort TmpRank = 0xC224;
    /// <summary>Scratch: the examined card's suit.</summary>
    public const ushort TmpSuit = 0xC225;
    /// <summary>Scratch: the examined card's red flag.</summary>
    public const ushort TmpRed = 0xC226;
    /// <summary>Scratch: the examined card's rank-corner tile.</summary>
    public const ushort TmpRankTile = 0xC227;
    /// <summary>Scratch: the map row for cell writes.</summary>
    public const ushort TmpRow = 0xC228;
    /// <summary>Scratch: the map column for cell writes.</summary>
    public const ushort TmpCol = 0xC229;
    /// <summary>Scratch: a general loop index.</summary>
    public const ushort IdxI = 0xC22A;
    /// <summary>Scratch: a second general loop index.</summary>
    public const ushort IdxJ = 0xC22B;
    /// <summary>Scratch: a general value.</summary>
    public const ushort TmpVal = 0xC22C;
    /// <summary>Scratch: a second general value.</summary>
    public const ushort TmpVal2 = 0xC22D;
    /// <summary>Scratch: the packed-BCD amount of the chip movement in flight.</summary>
    public const ushort TmpAmount = 0xC22E;
    /// <summary>Scratch: the draw mask being built.</summary>
    public const ushort TmpMask = 0xC22F;

    // Inputs and output for the replaceable opponent-decision routine.
    /// <summary>The seat whose action is being selected.</summary>
    public const ushort DecisionSeat = 0xC230;
    /// <summary>The deciding seat's evaluated hand strength.</summary>
    public const ushort DecisionStrength = 0xC231;
    /// <summary>Indicates whether the deciding seat must answer an outstanding bet.</summary>
    public const ushort DecisionFacing = 0xC232;
    /// <summary>The number of bets or raises already made in the current round.</summary>
    public const ushort DecisionRaises = 0xC233;
    /// <summary>The selected <see cref="ActionFold"/>, <see cref="ActionCheckCall"/>, or
    /// <see cref="ActionBetRaise"/> intent. The table applies legality and affordability constraints.</summary>
    public const ushort DecisionAction = 0xC234;
    /// <summary>Indicates whether the current decision's bluff roll succeeded.</summary>
    public const ushort DecisionBluff = 0xC235;

    // Evaluator scratch.
    /// <summary>Eval scratch: the flush flag.</summary>
    public const ushort EvalFlush = 0xC236;
    /// <summary>Eval scratch: the straight's high poker rank (0 = no straight; 5 = the wheel).</summary>
    public const ushort EvalStraightHigh = 0xC237;
    /// <summary>Eval scratch: the quads rank (0 = none).</summary>
    public const ushort EvalQuadRank = 0xC238;
    /// <summary>Eval scratch: the trips rank (0 = none).</summary>
    public const ushort EvalTripRank = 0xC239;
    /// <summary>Eval scratch: how many pairs.</summary>
    public const ushort EvalPairCount = 0xC23A;
    /// <summary>Eval scratch: the higher pair's rank.</summary>
    public const ushort EvalPairHigh = 0xC23B;
    /// <summary>Eval scratch: the lower pair's rank.</summary>
    public const ushort EvalPairLow = 0xC23C;
    /// <summary>Eval scratch: the packed shape byte (the category table's index).</summary>
    public const ushort EvalShape = 0xC23D;
    /// <summary>Eval scratch: the resolved category.</summary>
    public const ushort EvalCategory = 0xC23E;
    /// <summary>Eval scratch: tiebreak bytes written so far.</summary>
    public const ushort EvalTbCount = 0xC23F;

    // Per-seat arrays.
    /// <summary>The per-seat status bytes (<see cref="SeatActive"/>/<see cref="SeatFolded"/>/<see cref="SeatBusted"/>).</summary>
    public const ushort FoldedBase = 0xC240;
    /// <summary>The per-seat matched bet level this round, in units.</summary>
    public const ushort RoundBetBase = 0xC244;
    /// <summary>The per-seat last action this round (<see cref="ActedNone"/>..<see cref="ActedFold"/>).</summary>
    public const ushort LastActionBase = 0xC248;
    /// <summary>The per-seat draw-phase replacement counts.</summary>
    public const ushort DrawCountBase = 0xC24C;
    /// <summary>The per-seat cached hand strengths (recomputed at the deal and after the draw phase).</summary>
    public const ushort StrengthBase = 0xC250;
    /// <summary>The per-seat 5-card hands (stride <see cref="HandStride"/>; 5 bytes used).</summary>
    public const ushort HandBase = 0xC258;
    /// <summary>Bytes per hand slot (a power of two keeps SM83 indexing to shifts).</summary>
    public const int HandStride = 8;
    /// <summary>The per-seat evaluations (stride 8): category, five tiebreak ranks (descending significance),
    /// strength, zero.</summary>
    public const ushort EvalBase = 0xC280;
    /// <summary>The rank-count scratch: index by POKER rank (2..14, ace high); index 1 mirrors the ace for the
    /// wheel-straight window only.</summary>
    public const ushort RankCountBase = 0xC2A0;
    /// <summary>The suit-count scratch (4 bytes).</summary>
    public const ushort SuitCountBase = 0xC2B0;
    /// <summary>Decision scratch: the personality row copied for the current decision (bet, call, raise, bluff).</summary>
    public const ushort DecPersonality = 0xC2B4;
    /// <summary>The 52-byte deal buffer the shuffle runs over (one 256-byte page; draws consume from
    /// <see cref="NextCard"/>).</summary>
    public const ushort DeckScratch = 0xC2C0;

    // Board geometry (map cells).
    /// <summary>The board's width in map columns (the whole 20-column screen repaints).</summary>
    public const int BoardColumns = 20;
    /// <summary>The board's height in map rows.</summary>
    public const int BoardRows = 18;
    /// <summary>The message line's row.</summary>
    public const int MessageRow = 7;
    /// <summary>The message line's name column.</summary>
    public const int MessageNameColumn = 1;
    /// <summary>The message line's verb column.</summary>
    public const int MessageVerbColumn = 5;
    /// <summary>The message line's amount column (four cells).</summary>
    public const int MessageAmountColumn = 12;
    /// <summary>The pot label's row.</summary>
    public const int PotRow = 6;
    /// <summary>The pot digits' column.</summary>
    public const int PotColumn = 10;
    /// <summary>The action menu's first row (three rows).</summary>
    public const int MenuRow = 8;
    /// <summary>The action menu's cursor column (labels sit one to the right).</summary>
    public const int MenuCursorColumn = 14;
    /// <summary>The discard markers' row (above the player's cards).</summary>
    public const int MarkerRow = 12;
    /// <summary>The player's cards' top row (2×2 faces on this row and the next).</summary>
    public const int PlayerCardRow = 13;
    /// <summary>The player's first card's column (cards at this plus three per index).</summary>
    public const int PlayerCardColumn = 1;

    // The save payload (the framework mirror at 0xC060).
    /// <summary>The high-score table's work-RAM mirror (the framework save mirror).</summary>
    public const ushort HiScoreMirror = FrameworkMemoryMap.SaveMirror;
    /// <summary>The number of table entries.</summary>
    public const int HiScoreEntryCount = 5;
    /// <summary>Bytes per entry: three initials (font tile ids) + a three-byte BCD score, most significant first.</summary>
    public const int HiScoreEntryByteCount = 6;
    /// <summary>The persisted per-seat bankrolls (4 × 2 packed-BCD bytes, most significant first) — the session
    /// continues across power-off.</summary>
    public const ushort BankrollMirror = (ushort)(HiScoreMirror + (HiScoreEntryCount * HiScoreEntryByteCount));
    /// <summary>The persisted hands-won count (2 packed-BCD bytes).</summary>
    public const ushort HandsWonMirror = (ushort)(BankrollMirror + (SeatCount * 2));
    /// <summary>The persisted biggest pot (2 packed-BCD bytes).</summary>
    public const ushort BiggestPotMirror = (ushort)(HandsWonMirror + 2);
    /// <summary>The save payload's byte count (the score table + bankrolls + hands won + biggest pot).</summary>
    public const int SavePayloadByteCount = ((((HiScoreEntryCount * HiScoreEntryByteCount) + (SeatCount * 2)) + 2) + 2);
}
