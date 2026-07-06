using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// Brickfall's identity as DATA: the palette, the game tiles, the 7×4×4 piece-cell table (ported verbatim from the
/// original hand-authored ROM), the preview metasprite records, the gravity speed curve, the BCD score bases, the
/// screen cells and text overlays, the attract input script, and the default high-score table — all declared into a
/// <see cref="GameManifest"/> by <see cref="BrickfallGame"/>. The title screen ships SDF-BAKED by default:
/// <see cref="BrickfallTitleBake"/> installs the baked emblem through <see cref="SetTitleArt"/> as a parsed
/// <c>PBAK</c> background the manifest links as an art-backed screen (the linker owns tile/palette relocation; the
/// manifest's overlay contract keeps the menu text readable); the hand-authored banner stays the no-GPU fallback.
/// </summary>
internal static class BrickfallTables {
    /// <summary>The blank tile id (an empty well cell).</summary>
    public const byte TileBlank = 0;
    /// <summary>The wall tile id.</summary>
    public const byte TileWall = 1;
    /// <summary>The first of the three block tiles (colour = <see cref="TileBlockBase"/> + type mod 3).</summary>
    public const byte TileBlockBase = 2;
    /// <summary>Where the framework font starts in the tile bank — pinned (the game's manifest declares its five
    /// game tiles first, so the linker lands the font here; <see cref="BrickfallGame"/> guards the equality).</summary>
    public const byte FontTileBase = 5;

    // The title menu's fixed placement — identical whichever art ships (the manifest overlays it onto baked art too).
    private const int TitlePromptRow = 11;
    private const int TitlePromptColumn = 5;
    private const string TitlePromptText = "PUSH START";
    private const int TitleScoresRow = 13;
    private const int TitleScoresColumn = 3;
    private const string TitleScoresText = "SELECT SCORES";

    private static PbakBackground? s_titleArt;

    /// <summary>The shared 4-colour palette (used for both background and objects): well, cyan, orange, white.</summary>
    public static HgbImage.Rgb[] Palette => [
        new HgbImage.Rgb(R: 18, G: 20, B: 30),
        new HgbImage.Rgb(R: 88, G: 196, B: 220),
        new HgbImage.Rgb(R: 240, G: 168, B: 72),
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

    /// <summary>The play screen's HUD labels (with zeroed counters), declared as overlays over the walled well.</summary>
    public static IReadOnlyList<ScreenText> PlayHudOverlays => [
        new ScreenText(Row: 1, Column: 13, Text: "SCORE"),
        new ScreenText(Row: 2, Column: 13, Text: "000000"),
        new ScreenText(Row: 4, Column: 13, Text: "LINES"),
        new ScreenText(Row: 5, Column: 13, Text: "0000"),
        new ScreenText(Row: 7, Column: 13, Text: "LEVEL"),
        new ScreenText(Row: 8, Column: 13, Text: "00"),
        new ScreenText(Row: 10, Column: 13, Text: "NEXT"),
    ];

    /// <summary>Installs (or, with <see langword="null"/>, removes) the baked title art. The sculpt→bake→forge seam:
    /// <see cref="BrickfallTitleBake"/> hands the background section it parsed back out of the bake's own <c>PBAK</c>
    /// wire form, and the manifest's art-backed screen does the rest — no per-seam byte plumbing.</summary>
    /// <param name="art">The parsed background, or <see langword="null"/> to restore the hand-authored banner.</param>
    public static void SetTitleArt(PbakBackground? art) => s_titleArt = art;

    /// <summary>Builds the game's own tile segment: blank, wall, and the three bevelled blocks (the font and any
    /// title art follow in the bank via the manifest).</summary>
    /// <returns>The 2bpp tile bytes (five tiles).</returns>
    public static byte[] BuildGameTiles() {
        var parts = new byte[][] {
            EncodeTile(rows: ["........", "........", "........", "........", "........", "........", "........", "........"]),
            EncodeTile(rows: ["11111111", "13333331", "13333331", "13333331", "13333331", "13333331", "13333331", "11111111"]),
            Block(fill: '1'),
            Block(fill: '2'),
            Block(fill: '3'),
        };
        var tiles = new byte[parts.Length * 16];

        for (var index = 0; (index < parts.Length); index++) {
            parts[index].CopyTo(array: tiles, index: (index * 16));
        }

        return tiles;
    }

    /// <summary>Builds the seven tetrominoes' cell table: four rotations each, four (dx, dy) cells per rotation —
    /// 7 × 4 × 4 × 2 = 224 bytes, ported verbatim from the original ROM. Rotations are generated by rotating the base
    /// shape clockwise within its 4×4 box; the O piece is left fixed (rotation is a no-op).</summary>
    /// <returns>The piece table bytes.</returns>
    public static byte[] BuildPieceTable() {
        var data = new byte[7 * 4 * 4 * 2];
        var index = 0;

        for (var type = 0; (type < 7); type++) {
            var cells = PieceBases[type];

            for (var rotation = 0; (rotation < 4); rotation++) {
                foreach (var (x, y) in cells) {
                    data[index++] = (byte)x;
                    data[index++] = (byte)y;
                }

                cells = ((type == 1) ? cells : RotateClockwise(cells: cells)); // O never rotates.
            }
        }

        return data;
    }

    /// <summary>Builds the preview metasprite records: per piece type, four (dy, dx, tile, attributes) rows for the
    /// rotation-0 cells (one 16-byte record per piece). The tile carries the piece's block colour.</summary>
    /// <returns>The seven records.</returns>
    public static IReadOnlyList<byte[]> BuildPreviewRecords() {
        var records = new List<byte[]>(capacity: 7);

        for (var type = 0; (type < 7); type++) {
            var record = new byte[16];
            var tile = (byte)(TileBlockBase + (type % 3));
            var index = 0;

            foreach (var (x, y) in PieceBases[type]) {
                record[index++] = (byte)(y * 8);
                record[index++] = (byte)(x * 8);
                record[index++] = tile;
                record[index++] = 0x00;
            }

            records.Add(item: record);
        }

        return records;
    }

    /// <summary>The gravity curve: frames per row by level, clamped at the last entry from level 9 up.</summary>
    /// <returns>The ten speed bytes.</returns>
    public static byte[] BuildSpeedTable() => [45, 40, 35, 30, 25, 20, 16, 12, 9, 6];

    /// <summary>The line-clear score bases, LEAST significant BCD byte first (the SM83's carry-chained add order):
    /// 1 → 000040, 2 → 000100, 3 → 000300, 4 → 001200; each awarded ×(level + 1).</summary>
    /// <returns>The 4 × 3 base bytes.</returns>
    public static byte[] BuildScoreBases() => [
        0x40, 0x00, 0x00,
        0x00, 0x01, 0x00,
        0x00, 0x03, 0x00,
        0x00, 0x12, 0x00,
    ];

    /// <summary>The default high-score table (the save payload's ROM defaults): PUC 000500, BRK 000400, FAL 000300,
    /// SM8 000200, CGB 000000 — the fifth entry is zero so ANY score qualifies for entry.</summary>
    /// <returns>The 5 × 6 payload bytes (the framework's score-table shape).</returns>
    public static byte[] BuildDefaultScoreTable() =>
        GameManifest.BuildScoreTable(
            entries: [
                new ScoreTableEntry(Initials: "PUC", Score: 500),
                new ScoreTableEntry(Initials: "BRK", Score: 400),
                new ScoreTableEntry(Initials: "FAL", Score: 300),
                new ScoreTableEntry(Initials: "SM8", Score: 200),
                new ScoreTableEntry(Initials: "CGB", Score: 0),
            ],
            fontTileBase: FontTileBase
        );

    /// <summary>The attract input script: about twenty seconds of shifts, rotations, and soft drops. Never presses
    /// Start or Select.</summary>
    /// <returns>The steps, in play order.</returns>
    public static IReadOnlyList<InputScriptStep> BuildAttractScript() {
        var script = new List<InputScriptStep>();

        void Step(byte buttons, byte frames) {
            script.Add(item: new InputScriptStep(Buttons: buttons, Frames: frames));
        }

        for (var cycle = 0; (cycle < 3); cycle++) {
            Step(buttons: 0, frames: 30);
            Step(buttons: InputModule.ButtonLeft, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonLeft, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonUp, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonDown, frames: 60);
            Step(buttons: 0, frames: 30);
            Step(buttons: InputModule.ButtonRight, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonRight, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonRight, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonUp, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonDown, frames: 60);
            Step(buttons: 0, frames: 30);
            Step(buttons: InputModule.ButtonLeft, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonUp, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonUp, frames: 2);
            Step(buttons: 0, frames: 8);
            Step(buttons: InputModule.ButtonDown, frames: 60);
            Step(buttons: 0, frames: 30);
        }

        return script;
    }

    /// <summary>Builds the hand-authored title banner's cells (block strips + BRICKFALL in the framework font) — the
    /// no-GPU fallback screen. The menu prompts are NOT baked in; they ride <see cref="TitleMenuOverlays"/> whichever
    /// art ships.</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildTitleBannerCells() {
        var map = new byte[0x400];

        for (var column = 2; (column <= 17); column++) {
            map[(2 * 32) + column] = (byte)(TileBlockBase + (column % 3));
            map[(7 * 32) + column] = (byte)(TileBlockBase + ((column + 2) % 3));
        }

        WriteText(map: map, row: 5, column: 5, text: "BRICKFALL");

        return map;
    }

    /// <summary>Builds the play screen's cells: the walled well at columns 0/11 (the HUD labels ride
    /// <see cref="PlayHudOverlays"/>).</summary>
    /// <returns>The 1024-byte cell map.</returns>
    public static byte[] BuildPlayCells() {
        var map = new byte[0x400];

        for (var row = 0; (row < BrickfallProtocol.WellRows); row++) {
            map[(row * 32) + 0] = TileWall;
            map[(row * 32) + 11] = TileWall;
        }

        return map;
    }

    // The seven tetromino base shapes (rotation 0), in the table's I/O/T/S/Z/J/L order.
    private static readonly (int X, int Y)[][] PieceBases = [
        [(0, 1), (1, 1), (2, 1), (3, 1)], // I
        [(1, 0), (2, 0), (1, 1), (2, 1)], // O
        [(1, 0), (0, 1), (1, 1), (2, 1)], // T
        [(1, 0), (2, 0), (0, 1), (1, 1)], // S
        [(0, 0), (1, 0), (1, 1), (2, 1)], // Z
        [(0, 0), (0, 1), (1, 1), (2, 1)], // J
        [(2, 0), (0, 1), (1, 1), (2, 1)], // L
    ];

    private static (int X, int Y)[] RotateClockwise((int X, int Y)[] cells) {
        var result = new (int X, int Y)[cells.Length];

        for (var index = 0; (index < cells.Length); index++) {
            result[index] = (3 - cells[index].Y, cells[index].X);
        }

        return result;
    }

    private static void WriteText(byte[] map, int row, int column, string text) {
        for (var index = 0; (index < text.Length); index++) {
            map[(row * 32) + column + index] = TextModule.TileFor(fontTileBase: FontTileBase, character: text[index]);
        }
    }

    // A bevelled block: a bright edge, the fill colour, and a dark inner shadow line, so a field of them reads as
    // stacked pieces rather than a flat slab.
    private static byte[] Block(char fill) {
        var f = fill.ToString();

        return EncodeTile(rows: [
            "33333333",
            ("3" + f + f + f + f + f + f + "0"),
            ("3" + f + f + f + f + f + f + "0"),
            ("3" + f + f + f + f + f + f + "0"),
            ("3" + f + f + f + f + f + f + "0"),
            ("3" + f + f + f + f + f + f + "0"),
            ("3" + f + f + f + f + f + f + "0"),
            "00000000",
        ]);
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
