using Puck.Demo.Forge.Cards;
using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// Poker's identity as DATA: the three AI personalities (aggression/call/bluff thresholds keyed by evaluated hand
/// strength — records the shared SM83 decision code looks up, never per-opponent code), the hand-evaluation tables
/// (the 64-entry shape→category table and the per-category strength bases the ROM indexes at run time), the seat
/// layout, the screen overlays, the attract input script, and the default save payload (the shared score-table
/// shape plus the persisted bankrolls and records). The card substrate — the 52-card records, the card tile set,
/// the palettes, the deal — comes from <c>Forge/Cards/</c> and is DECLARED by the game, never copied. The title,
/// felt, and cursor ship SDF-BAKED by default (<see cref="PokerBake"/> installs them through the
/// <see cref="SetTitleArt"/>/<see cref="SetFeltArt"/>/<see cref="SetCursorArt"/> seams); the hand-authored banner,
/// flat felt, and pointer stay the no-GPU fallbacks.
/// </summary>
internal static class PokerTables {
    /// <summary>Where the framework font starts in the tile bank — pinned (the manifest declares the card tile set
    /// first, so the linker lands the font here; <see cref="PokerGame"/> guards the equality).</summary>
    public const byte FontTileBase = (byte)CardTables.CardTileCount;

    /// <summary>The personality record stride: open-bet threshold, call threshold, raise threshold, bluff chance
    /// (the PRNG roll must be strictly below it).</summary>
    public const int PersonalityRecordStride = 4;
    /// <summary>The record offset of the open-bet strength threshold.</summary>
    public const int PersonalityFieldBet = 0;
    /// <summary>The record offset of the call strength threshold.</summary>
    public const int PersonalityFieldCall = 1;
    /// <summary>The record offset of the raise strength threshold.</summary>
    public const int PersonalityFieldRaise = 2;
    /// <summary>The record offset of the bluff chance (0..255).</summary>
    public const int PersonalityFieldBluff = 3;

    /// <summary>The evaluation categories, weakest to strongest (indexes into the strength-base table and the
    /// category name texts).</summary>
    public const int CategoryCount = 9;

    private static PbakBackground? s_titleArt;
    private static PbakBackground? s_feltArt;
    private static PbakBundle? s_cursorArt;

    /// <summary>The installed baked title art, or <see langword="null"/> when the banner fallback ships.</summary>
    public static PbakBackground? TitleArt => s_titleArt;
    /// <summary>The installed baked felt table, or <see langword="null"/> when the flat fallback ships.</summary>
    public static PbakBackground? FeltArt => s_feltArt;
    /// <summary>The installed baked cursor bundle, or <see langword="null"/> when the hand-authored pointer ships.</summary>
    public static PbakBundle? CursorArt => s_cursorArt;

    /// <summary>Installs (or removes) the baked title art.</summary>
    /// <param name="art">The parsed background, or <see langword="null"/>.</param>
    public static void SetTitleArt(PbakBackground? art) => s_titleArt = art;

    /// <summary>Installs (or removes) the baked felt-table art (the play screen's backdrop).</summary>
    /// <param name="art">The parsed background, or <see langword="null"/>.</param>
    public static void SetFeltArt(PbakBackground? art) => s_feltArt = art;

    /// <summary>Installs (or removes) the baked cursor sprite bundle.</summary>
    /// <param name="art">The parsed bundle, or <see langword="null"/>.</param>
    public static void SetCursorArt(PbakBundle? art) => s_cursorArt = art;

    /// <summary>The three opponents' names, in seat order 1..3 (seat 0 is the player).</summary>
    public static IReadOnlyList<string> OpponentNames => ["DOT", "REX", "IVY"];

    /// <summary>Builds the personality records, in seat order 1..3 — the opponents' table manners as pure data:
    /// DOT plays tight (rarely opens, folds to pressure, almost never bluffs), REX plays loose and aggressive
    /// (opens any pair, calls nearly anything, bluffs often), IVY plays balanced. All thresholds are on the
    /// strength scale <c>categoryBase + highTiebreakRank</c> (see <see cref="BuildStrengthBaseTable"/>).</summary>
    /// <returns>The records, personality-index order (seat − 1).</returns>
    public static IReadOnlyList<byte[]> BuildPersonalityRecords() => [
        // bet, call, raise, bluff
        [48, 30, 74, 14],  // DOT — tight.
        [28, 10, 50, 52],  // REX — loose-aggressive.
        [38, 26, 62, 30],  // IVY — balanced.
    ];

    /// <summary>Builds the 64-entry shape→category table the ROM indexes at showdown: the shape byte packs
    /// flush (bit 0), straight (bit 1), pair count (bits 2..3), trips (bit 4), and quads (bit 5); the entry is the
    /// category (0 = high card … 8 = straight flush).</summary>
    /// <returns>The table bytes.</returns>
    public static byte[] BuildCategoryTable() {
        var table = new byte[64];

        for (var shape = 0; (shape < table.Length); shape++) {
            var flush = ((shape & 0x01) != 0);
            var straight = ((shape & 0x02) != 0);
            var pairs = (shape >> 2) & 0x03;
            var trips = ((shape & 0x10) != 0);
            var quads = ((shape & 0x20) != 0);

            table[shape] = (byte)(
                quads ? 7
                : ((trips && (pairs >= 1)) ? 6
                : ((flush && straight) ? 8
                : (flush ? 5
                : (straight ? 4
                : (trips ? 3
                : ((pairs == 2) ? 2
                : ((pairs == 1) ? 1
                : 0))))))));
        }

        return table;
    }

    /// <summary>Builds the per-category strength bases (strength = base + the highest tiebreak rank, 2..14) — the
    /// one-byte hand-strength scale the personalities' thresholds are written against.</summary>
    /// <returns>The 9 bases, category order.</returns>
    public static byte[] BuildStrengthBaseTable() => [0, 24, 48, 72, 96, 120, 144, 168, 192];

    /// <summary>The category display names, category order, padded to ten cells so a print fully overwrites the
    /// pot label region.</summary>
    public static IReadOnlyList<string> CategoryNames => [
        "HIGH CARD ", "ONE PAIR  ", "TWO PAIR  ", "TRIPS     ", "STRAIGHT  ",
        "FLUSH     ", "FULL BOAT ", "QUADS     ", "STR FLUSH ",
    ];

    /// <summary>The title screen's overlays — the contract, not the art: the game's name and the menu rows (with
    /// two leading spaces so the cursor cells also swap to font tiles and return to palette 0).</summary>
    public static IReadOnlyList<ScreenText> TitleOverlays => [
        new ScreenText(Row: 3, Column: 7, Text: "POKER"),
        new ScreenText(Row: 11, Column: 5, Text: "  DEAL"),
        new ScreenText(Row: 13, Column: 5, Text: "  SCORES"),
    ];

    /// <summary>The play screen's overlays: every fixed label AND every dynamic text region (names, chip digits,
    /// the message line, the action menu zone, the marker row) as placeholder cells — so all queued prints land on
    /// cells the linker already swapped to the font and pinned to palette 0, whatever the felt bake chose.</summary>
    public static IReadOnlyList<ScreenText> PlayOverlays => [
        new ScreenText(Row: 0, Column: 0, Text: " DOT    REX    IVY "),
        new ScreenText(Row: 1, Column: 1, Text: "0200   0200   0200"),
        new ScreenText(Row: 2, Column: 1, Text: "                  "),
        new ScreenText(Row: PokerProtocol.PotRow, Column: 6, Text: "POT 0000  "),
        new ScreenText(Row: PokerProtocol.MessageRow, Column: 1, Text: "                  "),
        new ScreenText(Row: PokerProtocol.MenuRow, Column: 13, Text: "       "),
        new ScreenText(Row: (PokerProtocol.MenuRow + 1), Column: 13, Text: "       "),
        new ScreenText(Row: (PokerProtocol.MenuRow + 2), Column: 13, Text: "       "),
        new ScreenText(Row: PokerProtocol.MarkerRow, Column: 1, Text: "               "),
        new ScreenText(Row: 16, Column: 0, Text: " YOU 0200"),
    ];

    /// <summary>The AI seats' status-line map columns, seat-1-indexed (names on row 0, chips on row 1, the
    /// fold/out indicator on row 2 — all at this column).</summary>
    public static IReadOnlyList<int> OpponentColumns => [1, 8, 15];

    /// <summary>The AI seats' showdown reveal rows, seat-1-indexed (five rank+suit strips per row).</summary>
    public static IReadOnlyList<int> RevealRows => [3, 4, 5];

    /// <summary>The showdown reveal strips' first column (five 2-cell strips).</summary>
    public const int RevealColumn = 4;

    /// <summary>The default save payload: the shared score-table shape (final stacks, best first; the fifth entry
    /// is 100 so a busted session never qualifies) followed by the four seat bankrolls at 200 chips and the zeroed
    /// hands-won / biggest-pot records.</summary>
    /// <returns>The payload bytes (<see cref="PokerProtocol.SavePayloadByteCount"/>).</returns>
    public static byte[] BuildDefaultSavePayload() {
        var table = GameManifest.BuildScoreTable(
            entries: [
                new ScoreTableEntry(Initials: "ACE", Score: 750),
                new ScoreTableEntry(Initials: "KNG", Score: 600),
                new ScoreTableEntry(Initials: "QUN", Score: 450),
                new ScoreTableEntry(Initials: "JCK", Score: 300),
                new ScoreTableEntry(Initials: "TEN", Score: 100),
            ],
            fontTileBase: FontTileBase
        );
        var payload = new byte[PokerProtocol.SavePayloadByteCount];

        table.CopyTo(array: payload, index: 0);

        for (var seat = 0; (seat < PokerProtocol.SeatCount); seat++) {
            payload[(table.Length + (seat * 2))] = 0x02;      // 0200 chips, packed BCD, MSB first.
            payload[((table.Length + (seat * 2)) + 1)] = 0x00;
        }

        return payload;
    }

    /// <summary>The attract input script: pure idle (the attract table plays all four seats itself — the script
    /// only paces the loop's length). Never presses anything, so the in-attract pause/menu paths stay unreachable
    /// and any REAL press exits to the title.</summary>
    /// <returns>The steps, in play order (~25 seconds).</returns>
    public static IReadOnlyList<InputScriptStep> BuildAttractScript() {
        var script = new List<InputScriptStep>();

        for (var step = 0; (step < 6); step++) {
            script.Add(item: new InputScriptStep(Buttons: 0, Frames: 255));
        }

        return script;
    }

    /// <summary>Builds the hand-authored title banner cells (suit-tile strips; the name and menu ride
    /// <see cref="TitleOverlays"/> whichever art ships) — the no-GPU fallback screen.</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildTitleBannerCells() {
        var map = new byte[0x400];

        for (var column = 2; (column <= 17); column++) {
            map[((6 * 32) + column)] = (byte)(CardTables.TileSuitBase + (column % 4));
            map[((16 * 32) + column)] = (byte)(CardTables.TileSuitBase + ((column + 2) % 4));
        }

        return map;
    }

    /// <summary>Builds the fallback play-screen cells: open felt (tile 0 everywhere — the table repaint draws the
    /// hands over it).</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildFallbackFeltCells() => new byte[0x400];
}
