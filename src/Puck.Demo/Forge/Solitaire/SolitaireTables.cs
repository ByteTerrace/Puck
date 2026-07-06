using Puck.Demo.Forge.Cards;
using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// Solitaire's identity as DATA: the board's position record table (pile mapping, board columns, and the pad
/// navigation graph — movement is a table lookup, not code), the screen cells and text overlays, the attract input
/// script, and the default save payload (the shared score-table shape plus the win streaks). The card substrate —
/// the 52-card records, the card tile set, the palettes, the deal — comes from <c>Forge/Cards/</c> and is DECLARED
/// here, never copied. The title, felt, and cursor ship SDF-BAKED by default (<see cref="SolitaireBake"/> installs
/// them through the <see cref="SetTitleArt"/>/<see cref="SetFeltArt"/>/<see cref="SetCursorArt"/> seams as parsed
/// <c>PBAK</c> sections the manifest links); the hand-authored banner, flat felt, and pointer stay the no-GPU
/// fallbacks.
/// </summary>
internal static class SolitaireTables {
    /// <summary>Where the framework font starts in the tile bank — pinned (the manifest declares the card tile set
    /// first, so the linker lands the font here; <see cref="SolitaireGame"/> guards the equality).</summary>
    public const byte FontTileBase = (byte)CardTables.CardTileCount;

    /// <summary>The position record stride: pile id, kind, board column, nav-left, nav-right, nav-vertical.</summary>
    public const int PositionRecordStride = 6;
    /// <summary>The record offset of the pile id.</summary>
    public const int PositionFieldPile = 0;
    /// <summary>The record offset of the kind (0 stock, 1 waste, 2 foundation, 3 tableau).</summary>
    public const int PositionFieldKind = 1;
    /// <summary>The record offset of the board column.</summary>
    public const int PositionFieldColumn = 2;
    /// <summary>The record offset of the Left-press destination position.</summary>
    public const int PositionFieldNavLeft = 3;
    /// <summary>The record offset of the Right-press destination position.</summary>
    public const int PositionFieldNavRight = 4;
    /// <summary>The record offset of the vertical destination (Down from the top row, Up from the tableau).</summary>
    public const int PositionFieldNavVertical = 5;

    /// <summary>The stock kind.</summary>
    public const byte KindStock = 0;
    /// <summary>The waste kind.</summary>
    public const byte KindWaste = 1;
    /// <summary>The foundation kind.</summary>
    public const byte KindFoundation = 2;
    /// <summary>The tableau kind.</summary>
    public const byte KindTableau = 3;

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

    /// <summary>The title screen's overlays — the contract, not the art: the game's name and the menu rows (with
    /// two leading spaces so the cursor cells also swap to font tiles and return to palette 0).</summary>
    public static IReadOnlyList<ScreenText> TitleOverlays => [
        new ScreenText(Row: 3, Column: 5, Text: "SOLITAIRE"),
        new ScreenText(Row: 11, Column: 5, Text: "  NEW DEAL"),
        new ScreenText(Row: 13, Column: 5, Text: "  SCORES"),
    ];

    /// <summary>The play screen's HUD overlays on row 2 (labels and zeroed counters; the row repaints with the
    /// board, so these cells stay palette 0 whatever the felt bake chose).</summary>
    public static IReadOnlyList<ScreenText> PlayOverlays => [
        new ScreenText(Row: SolitaireProtocol.HudRow, Column: 0, Text: "SC 000000 L 24 S 00"),
    ];

    /// <summary>Builds the 13 position records — the board's layout AND the pad's navigation graph as one manifest
    /// table (stride <see cref="PositionRecordStride"/>). The verifier walks the same table to route the cursor, so
    /// the game and its oracle can never disagree about the board.</summary>
    /// <returns>The records, in position order.</returns>
    public static IReadOnlyList<byte[]> BuildPositionRecords() => [
        // pile, kind, column, navLeft, navRight, navVertical
        [SolitaireProtocol.PileStock, KindStock, 1, 5, 1, 6],
        [SolitaireProtocol.PileStock, KindWaste, 4, 0, 2, 7],
        [(SolitaireProtocol.PileFoundationBase + 0), KindFoundation, 7, 1, 3, 9],
        [(SolitaireProtocol.PileFoundationBase + 1), KindFoundation, 9, 2, 4, 10],
        [(SolitaireProtocol.PileFoundationBase + 2), KindFoundation, 11, 3, 5, 11],
        [(SolitaireProtocol.PileFoundationBase + 3), KindFoundation, 13, 4, 0, 12],
        [(SolitaireProtocol.PileTableauBase + 0), KindTableau, 1, 12, 7, 0],
        [(SolitaireProtocol.PileTableauBase + 1), KindTableau, 3, 6, 8, 1],
        [(SolitaireProtocol.PileTableauBase + 2), KindTableau, 5, 7, 9, 1],
        [(SolitaireProtocol.PileTableauBase + 3), KindTableau, 7, 8, 10, 2],
        [(SolitaireProtocol.PileTableauBase + 4), KindTableau, 9, 9, 11, 3],
        [(SolitaireProtocol.PileTableauBase + 5), KindTableau, 11, 10, 12, 4],
        [(SolitaireProtocol.PileTableauBase + 6), KindTableau, 13, 11, 6, 5],
    ];

    /// <summary>The default save payload: the shared score-table shape (PUC 300 … GBC 0 — the fifth entry is zero
    /// so any score qualifies) followed by the current and best win streaks, both zero.</summary>
    /// <returns>The payload bytes (<see cref="SolitaireProtocol.SavePayloadByteCount"/>).</returns>
    public static byte[] BuildDefaultSavePayload() {
        var table = GameManifest.BuildScoreTable(
            entries: [
                new ScoreTableEntry(Initials: "PUC", Score: 300),
                new ScoreTableEntry(Initials: "SOL", Score: 250),
                new ScoreTableEntry(Initials: "CRD", Score: 200),
                new ScoreTableEntry(Initials: "ACE", Score: 150),
                new ScoreTableEntry(Initials: "GBC", Score: 0),
            ],
            fontTileBase: FontTileBase
        );
        var payload = new byte[SolitaireProtocol.SavePayloadByteCount];

        table.CopyTo(array: payload, index: 0);

        return payload;
    }

    /// <summary>The attract input script: about twenty seconds of stock draws and cursor tours over the constant-
    /// seed attract deal. Never presses START or SELECT.</summary>
    /// <returns>The steps, in play order.</returns>
    public static IReadOnlyList<InputScriptStep> BuildAttractScript() {
        var script = new List<InputScriptStep>();

        void Step(byte buttons, byte frames) {
            script.Add(item: new InputScriptStep(Buttons: buttons, Frames: frames));
        }

        for (var cycle = 0; (cycle < 4); cycle++) {
            Step(buttons: 0, frames: 45);
            Step(buttons: InputModule.ButtonA, frames: 2); // Draw from the stock.
            Step(buttons: 0, frames: 40);
            Step(buttons: InputModule.ButtonA, frames: 2);
            Step(buttons: 0, frames: 40);
            Step(buttons: InputModule.ButtonRight, frames: 2); // Tour: waste …
            Step(buttons: 0, frames: 25);
            Step(buttons: InputModule.ButtonDown, frames: 2); // … down to the tableau …
            Step(buttons: 0, frames: 25);
            Step(buttons: InputModule.ButtonRight, frames: 2);
            Step(buttons: 0, frames: 25);
            Step(buttons: InputModule.ButtonUp, frames: 2); // … and back to the top row.
            Step(buttons: 0, frames: 25);
            Step(buttons: InputModule.ButtonLeft, frames: 2);
            Step(buttons: 0, frames: 25);
            Step(buttons: InputModule.ButtonLeft, frames: 2);
            Step(buttons: 0, frames: 40);
        }

        return script;
    }

    /// <summary>Builds the hand-authored title banner cells (suit-tile strips; the name and menu ride
    /// <see cref="TitleOverlays"/> whichever art ships) — the no-GPU fallback screen.</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildTitleBannerCells() {
        var map = new byte[0x400];

        for (var column = 2; (column <= 17); column++) {
            map[(6 * 32) + column] = (byte)(CardTables.TileSuitBase + (column % 4));
            map[(16 * 32) + column] = (byte)(CardTables.TileSuitBase + ((column + 2) % 4));
        }

        return map;
    }

    /// <summary>Builds the fallback play-screen cells: open felt (tile 0 everywhere — the board repaint draws the
    /// piles over it).</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildFallbackFeltCells() => new byte[0x400];
}
