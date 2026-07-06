using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// Chroma's identity as DATA: the palettes, the game tiles (the three gutter-framed colour blocks and the hollow
/// cursor box, ported verbatim from the original hand-authored ROM), the screen cells and text overlays, the attract
/// input script, and the default high-score table — all declared into a <see cref="GameManifest"/> by
/// <see cref="ChromaGame"/>. The title screen ships SDF-BAKED by default: <see cref="ChromaTitleBake"/> installs the
/// baked emblem through <see cref="SetTitleArt"/> as a parsed <c>PBAK</c> background the manifest links as an
/// art-backed screen (the linker owns tile/palette relocation; the manifest's overlay contract keeps the menu text
/// readable); the hand-authored banner stays the no-GPU fallback.
/// </summary>
internal static class ChromaTables {
    /// <summary>The blank tile id (an empty well cell).</summary>
    public const byte TileBlank = 0;
    /// <summary>The first block tile id (red; a cell's colour value 1..3 IS its tile id).</summary>
    public const byte TileBlockBase = 1;
    /// <summary>The hollow cursor-box tile id (a hardware sprite).</summary>
    public const byte TileCursor = 4;
    /// <summary>Where the framework font starts in the tile bank — pinned (the game's manifest declares its five
    /// game tiles first, so the linker lands the font here; <see cref="ChromaGame"/> guards the equality).</summary>
    public const byte FontTileBase = 5;

    // The title menu's fixed placement — identical whichever art ships (the manifest overlays it onto baked art too).
    private const int TitlePromptRow = 11;
    private const int TitlePromptColumn = 5;
    private const string TitlePromptText = "PUSH START";
    private const int TitleScoresRow = 13;
    private const int TitleScoresColumn = 3;
    private const string TitleScoresText = "SELECT SCORES";

    private static PbakBackground? s_titleArt;

    /// <summary>The background palette: well dark, red, green, blue (blue doubles as the text colour, as on the
    /// original cartridge).</summary>
    public static HgbImage.Rgb[] Palette => [
        new HgbImage.Rgb(R: 20, G: 22, B: 34),
        new HgbImage.Rgb(R: 236, G: 96, B: 104),
        new HgbImage.Rgb(R: 120, G: 210, B: 130),
        new HgbImage.Rgb(R: 110, G: 170, B: 240),
    ];

    /// <summary>The object palette: transparent, the cream cursor, unused, unused.</summary>
    public static HgbImage.Rgb[] ObjectPalette => [
        new HgbImage.Rgb(R: 0, G: 0, B: 0),
        new HgbImage.Rgb(R: 250, G: 246, B: 210),
        new HgbImage.Rgb(R: 0, G: 0, B: 0),
        new HgbImage.Rgb(R: 0, G: 0, B: 0),
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

    /// <summary>The play-screen HUD's fixed cells: the SCORE label row and the six BCD digits below it, right of the
    /// well.</summary>
    public const int HudLabelRow = 3;
    /// <summary>The BCD score row.</summary>
    public const int HudScoreRow = 4;
    /// <summary>The HUD column (right of the well).</summary>
    public const int HudColumn = 14;

    /// <summary>The play screen's HUD cells (a zeroed counter), declared as overlays beside the well.</summary>
    public static IReadOnlyList<ScreenText> PlayHudOverlays => [
        new ScreenText(Row: HudLabelRow, Column: HudColumn, Text: "SCORE"),
        new ScreenText(Row: HudScoreRow, Column: HudColumn, Text: "000000"),
    ];

    /// <summary>Installs (or, with <see langword="null"/>, removes) the baked title art. The sculpt→bake→forge seam:
    /// <see cref="ChromaTitleBake"/> hands the background section it parsed back out of the bake's own <c>PBAK</c>
    /// wire form, and the manifest's art-backed screen does the rest — no per-seam byte plumbing.</summary>
    /// <param name="art">The parsed background, or <see langword="null"/> to restore the hand-authored banner.</param>
    public static void SetTitleArt(PbakBackground? art) => s_titleArt = art;

    /// <summary>Builds the game's own tile segment: blank, the three gutter-framed colour blocks, and the hollow
    /// cursor box (the font and any title art follow in the bank via the manifest).</summary>
    /// <returns>The 2bpp tile bytes (five tiles).</returns>
    public static byte[] BuildGameTiles() {
        var parts = new byte[][] {
            EncodeTile(rows: ["........", "........", "........", "........", "........", "........", "........", "........"]),
            ColourTile(index: '1'),
            ColourTile(index: '2'),
            ColourTile(index: '3'),
            EncodeTile(rows: ["11111111", "1......1", "1......1", "1......1", "1......1", "1......1", "1......1", "11111111"]),
        };
        var tiles = new byte[parts.Length * 16];

        for (var index = 0; (index < parts.Length); index++) {
            parts[index].CopyTo(array: tiles, index: (index * 16));
        }

        return tiles;
    }

    /// <summary>The attract input script: about twenty seconds of cursor walks and swap presses. Never presses Start
    /// or Select.</summary>
    /// <returns>The steps, in play order.</returns>
    public static IReadOnlyList<InputScriptStep> BuildAttractScript() {
        var script = new List<InputScriptStep>();

        void Step(byte buttons, byte frames) {
            script.Add(item: new InputScriptStep(Buttons: buttons, Frames: frames));
        }

        for (var cycle = 0; (cycle < 4); cycle++) {
            Step(buttons: 0, frames: 30);
            Step(buttons: InputModule.ButtonRight, frames: 2);
            Step(buttons: 0, frames: 10);
            Step(buttons: InputModule.ButtonRight, frames: 2);
            Step(buttons: 0, frames: 10);
            Step(buttons: InputModule.ButtonDown, frames: 2);
            Step(buttons: 0, frames: 10);
            Step(buttons: InputModule.ButtonA, frames: 2);
            Step(buttons: 0, frames: 40);
            Step(buttons: InputModule.ButtonLeft, frames: 2);
            Step(buttons: 0, frames: 10);
            Step(buttons: InputModule.ButtonUp, frames: 2);
            Step(buttons: 0, frames: 10);
            Step(buttons: InputModule.ButtonA, frames: 2);
            Step(buttons: 0, frames: 50);
        }

        return script;
    }

    /// <summary>The default high-score table (the save payload's ROM defaults): PUC 000500, CHR 000400, OMA 000300,
    /// SM8 000200, CGB 000000 — the fifth entry is zero so ANY score qualifies for entry.</summary>
    /// <returns>The 5 × 6 payload bytes (the framework's score-table shape).</returns>
    public static byte[] BuildDefaultScoreTable() =>
        GameManifest.BuildScoreTable(
            entries: [
                new ScoreTableEntry(Initials: "PUC", Score: 500),
                new ScoreTableEntry(Initials: "CHR", Score: 400),
                new ScoreTableEntry(Initials: "OMA", Score: 300),
                new ScoreTableEntry(Initials: "SM8", Score: 200),
                new ScoreTableEntry(Initials: "CGB", Score: 0),
            ],
            fontTileBase: FontTileBase
        );

    /// <summary>Builds the hand-authored title banner's cells (block strips + CHROMA in the framework font) — the
    /// no-GPU fallback screen. The menu prompts are NOT baked in; they ride <see cref="TitleMenuOverlays"/> whichever
    /// art ships.</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildTitleBannerCells() {
        var map = new byte[0x400];

        for (var column = 2; (column <= 17); column++) {
            map[(2 * 32) + column] = (byte)(TileBlockBase + (column % 3));
            map[(7 * 32) + column] = (byte)(TileBlockBase + ((column + 2) % 3));
        }

        WriteText(map: map, row: 5, column: 7, text: "CHROMA");

        return map;
    }

    /// <summary>Builds the play screen's cells: a dark field (the HUD rides <see cref="PlayHudOverlays"/>; the well
    /// itself is painted from the grid at state enter and kept fresh by the per-frame diff pass).</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildPlayCells() => new byte[0x400];

    // A block with a dark 1-px gutter so a field of same-colour blocks still reads as a grid of cells.
    private static byte[] ColourTile(char index) {
        var f = index.ToString();
        var body = ("0" + f + f + f + f + f + f + "0");

        return EncodeTile(rows: ["00000000", body, body, body, body, body, body, "00000000"]);
    }

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
