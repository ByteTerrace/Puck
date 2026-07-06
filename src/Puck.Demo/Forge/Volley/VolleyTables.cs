using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// Volley's identity as DATA: the palette, the game tiles (the paddle bar, the ball dot, and the dashed court net,
/// ported verbatim from the original hand-authored ROM), the screen cells and text overlays, the attract input
/// script, and the default high-score table — all declared into a <see cref="GameManifest"/> by
/// <see cref="VolleyGame"/>. The title screen ships SDF-BAKED by default: <see cref="VolleyTitleBake"/> installs the
/// baked emblem through <see cref="SetTitleArt"/> as a parsed <c>PBAK</c> background the manifest links as an
/// art-backed screen (the linker owns tile/palette relocation; the manifest's overlay contract keeps the menu text
/// readable); the hand-authored banner stays the no-GPU fallback.
/// </summary>
internal static class VolleyTables {
    /// <summary>The blank tile id.</summary>
    public const byte TileBlank = 0;
    /// <summary>The dashed court-net tile id.</summary>
    public const byte TileNet = 1;
    /// <summary>The solid paddle tile id (stacked ×3 on screen for the 8×24 bar).</summary>
    public const byte TilePaddle = 2;
    /// <summary>The ball tile id.</summary>
    public const byte TileBall = 3;
    /// <summary>Where the framework font starts in the tile bank — pinned (the game's manifest declares its four
    /// game tiles first, so the linker lands the font here; <see cref="VolleyGame"/> guards the equality).</summary>
    public const byte FontTileBase = 4;

    // The title menu's fixed placement — identical whichever art ships (the manifest overlays it onto baked art too).
    private const int TitlePromptRow = 11;
    private const int TitlePromptColumn = 5;
    private const string TitlePromptText = "PUSH START";
    private const int TitleScoresRow = 13;
    private const int TitleScoresColumn = 3;
    private const string TitleScoresText = "SELECT SCORES";

    private static PbakBackground? s_titleArt;

    /// <summary>The shared 4-colour palette (used for both background and objects): court, net cyan, ball amber,
    /// paddle/text white — the original cartridge's colours folded onto one table.</summary>
    public static HgbImage.Rgb[] Palette => [
        new HgbImage.Rgb(R: 16, G: 18, B: 28),
        new HgbImage.Rgb(R: 96, G: 176, B: 200),
        new HgbImage.Rgb(R: 246, G: 214, B: 92),
        new HgbImage.Rgb(R: 232, G: 236, B: 245),
    ];

    /// <summary>The installed baked title art (a parsed <c>PBAK</c> background), or <see langword="null"/> when the
    /// hand-authored banner ships.</summary>
    public static PbakBackground? TitleArt => s_titleArt;

    /// <summary>The title screen's menu overlays — part of the title's CONTRACT, not the art: the manifest composes
    /// them onto whichever map ships and zeroes their cells' attributes, so no baked emblem can ever make the menu
    /// unreadable.</summary>
    public static IReadOnlyList<ScreenText> TitleMenuOverlays => [
        new ScreenText(Row: TitlePromptRow, Column: TitlePromptColumn, Text: TitlePromptText),
        new ScreenText(Row: TitleScoresRow, Column: TitleScoresColumn, Text: TitleScoresText),
    ];

    /// <summary>The play-screen HUD's fixed row: the player's point digit, the six-digit rally score, and the AI's
    /// point digit, all on row 0 (above the ball's reachable court).</summary>
    public const int HudRow = 0;
    /// <summary>The player point digit's column.</summary>
    public const int HudPlayerPointColumn = 4;
    /// <summary>The BCD score's first column.</summary>
    public const int HudScoreColumn = 7;
    /// <summary>The AI point digit's column.</summary>
    public const int HudAiPointColumn = 15;

    /// <summary>The play screen's HUD cells (zeroed counters), declared as overlays over the court.</summary>
    public static IReadOnlyList<ScreenText> PlayHudOverlays => [
        new ScreenText(Row: HudRow, Column: HudPlayerPointColumn, Text: "0"),
        new ScreenText(Row: HudRow, Column: HudScoreColumn, Text: "000000"),
        new ScreenText(Row: HudRow, Column: HudAiPointColumn, Text: "0"),
    ];

    /// <summary>Installs (or, with <see langword="null"/>, removes) the baked title art. The sculpt→bake→forge seam:
    /// <see cref="VolleyTitleBake"/> hands the background section it parsed back out of the bake's own <c>PBAK</c>
    /// wire form, and the manifest's art-backed screen does the rest — no per-seam byte plumbing.</summary>
    /// <param name="art">The parsed background, or <see langword="null"/> to restore the hand-authored banner.</param>
    public static void SetTitleArt(PbakBackground? art) => s_titleArt = art;

    /// <summary>The hand-authored art + palettes the bake calibration (<c>--forge-bake-calibration</c>) measures the
    /// SDF→bake pipeline against: the solid paddle tile (stacked ×3 on screen for the 8×24 bar), the ball dot, the
    /// dashed court net, and the palettes that give their indices display colours.</summary>
    /// <returns>The three 16-byte 2bpp tiles and the two 4-colour palettes.</returns>
    public static (byte[] PaddleTile, byte[] BallTile, byte[] NetTile, HgbImage.Rgb[] BackgroundColours, HgbImage.Rgb[] ObjectColours) CalibrationArt() =>
        (PaddleTileArt(), BallTileArt(), NetTileArt(), Palette, Palette);

    /// <summary>Builds the game's own tile segment: blank, the net dash, the paddle bar, and the ball dot (the font
    /// and any title art follow in the bank via the manifest).</summary>
    /// <returns>The 2bpp tile bytes (four tiles).</returns>
    public static byte[] BuildGameTiles() {
        var parts = new byte[][] {
            EncodeTile(rows: ["........", "........", "........", "........", "........", "........", "........", "........"]),
            NetTileArt(),
            PaddleTileArt(),
            BallTileArt(),
        };
        var tiles = new byte[parts.Length * 16];

        for (var index = 0; (index < parts.Length); index++) {
            parts[index].CopyTo(array: tiles, index: (index * 16));
        }

        return tiles;
    }

    /// <summary>The attract input script: about twenty seconds of paddle sweeps. Never presses Start or Select.</summary>
    /// <returns>The steps, in play order.</returns>
    public static IReadOnlyList<InputScriptStep> BuildAttractScript() {
        var script = new List<InputScriptStep>();

        void Step(byte buttons, byte frames) {
            script.Add(item: new InputScriptStep(Buttons: buttons, Frames: frames));
        }

        for (var cycle = 0; (cycle < 5); cycle++) {
            Step(buttons: 0, frames: 20);
            Step(buttons: InputModule.ButtonUp, frames: 30);
            Step(buttons: 0, frames: 12);
            Step(buttons: InputModule.ButtonDown, frames: 45);
            Step(buttons: 0, frames: 12);
            Step(buttons: InputModule.ButtonUp, frames: 25);
            Step(buttons: 0, frames: 30);
            Step(buttons: InputModule.ButtonDown, frames: 20);
            Step(buttons: 0, frames: 40);
        }

        return script;
    }

    /// <summary>The default high-score table (the save payload's ROM defaults): PUC 000500, VOL 000400, LEY 000300,
    /// SM8 000200, CGB 000000 — the fifth entry is zero so ANY score qualifies for entry.</summary>
    /// <returns>The 5 × 6 payload bytes (the framework's score-table shape).</returns>
    public static byte[] BuildDefaultScoreTable() =>
        GameManifest.BuildScoreTable(
            entries: [
                new ScoreTableEntry(Initials: "PUC", Score: 500),
                new ScoreTableEntry(Initials: "VOL", Score: 400),
                new ScoreTableEntry(Initials: "LEY", Score: 300),
                new ScoreTableEntry(Initials: "SM8", Score: 200),
                new ScoreTableEntry(Initials: "CGB", Score: 0),
            ],
            fontTileBase: FontTileBase
        );

    /// <summary>Builds the hand-authored title banner's cells (net strips + VOLLEY in the framework font) — the
    /// no-GPU fallback screen. The menu prompts are NOT baked in; they ride <see cref="TitleMenuOverlays"/> whichever
    /// art ships.</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildTitleBannerCells() {
        var map = new byte[0x400];

        for (var column = 2; (column <= 17); column++) {
            map[(2 * 32) + column] = TileNet;
            map[(7 * 32) + column] = TileNet;
        }

        WriteText(map: map, row: 5, column: 7, text: "VOLLEY");

        return map;
    }

    /// <summary>Builds the play screen's cells: the dashed net down the court's centre column (the HUD cells ride
    /// <see cref="PlayHudOverlays"/>).</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildPlayCells() {
        var map = new byte[0x400];

        for (var row = 1; (row < 18); row++) {
            map[(row * 32) + 10] = TileNet;
        }

        return map;
    }

    private static byte[] NetTileArt() =>
        EncodeTile(rows: [
            "...11...", "...11...", "...11...", "...11...", "........", "........", "...11...", "...11...",
        ]);

    private static byte[] PaddleTileArt() =>
        EncodeTile(rows: [
            "33333333", "33333333", "33333333", "33333333", "33333333", "33333333", "33333333", "33333333",
        ]);

    private static byte[] BallTileArt() =>
        EncodeTile(rows: [
            "........", "..2222..", ".222222.", ".222222.", ".222222.", ".222222.", "..2222..", "........",
        ]);

    private static void WriteText(byte[] map, int row, int column, string text) {
        for (var index = 0; (index < text.Length); index++) {
            map[(row * 32) + column + index] = TextModule.TileFor(fontTileBase: FontTileBase, character: text[index]);
        }
    }

    // Encodes an 8×8 pattern (eight 8-char rows, one palette-index digit per pixel) to the 16-byte 2bpp tile form.
    private static byte[] EncodeTile(string[] rows) {
        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            var line = rows[row];

            for (var column = 0; (column < 8); column++) {
                indices[(row * 8) + column] = (byte)((column < line.Length) ? (line[column] switch {
                    '1' => 1,
                    '2' => 2,
                    '3' or '#' => 3,
                    _ => 0,
                }) : 0);
            }
        }

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }
}
