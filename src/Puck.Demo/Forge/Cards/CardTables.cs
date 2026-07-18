using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge.Cards;

/// <summary>
/// The card layer's shared identity-as-data: the 52-card record table (rank / suit / colour / rank tile, one
/// <see cref="GameManifest.DefineRecords"/> declaration away from any card game), the hand-authored card tile set
/// (rank corners in black and red, suit corners, card bottoms, the back, the empty-slot outline), the shared card
/// palettes, and the deterministic no-GPU fallback cursor sprite bundle. Poker and Solitaire both declare THESE
/// tables — rules and art conventions live here once, never copied per game.
/// </summary>
internal static class CardTables {
    // The card tile set's layout (indices within the game tile segment, which every card game declares FIRST so
    // these constants hold; tile 0 stays blank so a cleared map renders flat felt).
    /// <summary>The blank tile id (flat felt — colour 0 of the gameplay palette).</summary>
    public const byte TileBlank = 0;
    /// <summary>The first black rank-corner tile (ace; ranks are contiguous, so rank r = this + r − 1).</summary>
    public const byte TileRankBlackBase = 1;
    /// <summary>The first red rank-corner tile.</summary>
    public const byte TileRankRedBase = 14;
    /// <summary>The first suit-corner tile (spade, heart, diamond, club — suit s = this + s).</summary>
    public const byte TileSuitBase = 27;
    /// <summary>The card face's bottom-left tile (left + bottom border on white).</summary>
    public const byte TileCardBottomLeft = 31;
    /// <summary>The card face's bottom-right tile.</summary>
    public const byte TileCardBottomRight = 32;
    /// <summary>The card back's 2×2 tile block (top-left, top-right, bottom-left, bottom-right).</summary>
    public const byte TileBackBase = 33;
    /// <summary>The empty-slot outline's 2×2 tile block.</summary>
    public const byte TileOutlineBase = 37;
    /// <summary>The "more cards above" marker tile (drawn when a tall tableau column clips).</summary>
    public const byte TileMarker = 41;
    /// <summary>The number of tiles in <see cref="BuildCardTiles"/> (the font follows at this base).</summary>
    public const int CardTileCount = 42;

    // Gameplay palette indices (background palette slot 0 and the card tiles agree on these).
    private const byte ColourFelt = 0;
    private const byte ColourInk = 1;
    private const byte ColourRed = 2;
    private const byte ColourWhite = 3;

    /// <summary>The shared gameplay background palette: felt green, ink, red, white. Colour 0 is the felt so the
    /// blank tile and a cleared map both read as the table; the framework font renders white on felt.</summary>
    public static HgbImage.Rgb[] BackgroundPalette => [
        new HgbImage.Rgb(R: 24, G: 96, B: 56),
        new HgbImage.Rgb(R: 20, G: 20, B: 28),
        new HgbImage.Rgb(R: 200, G: 36, B: 44),
        new HgbImage.Rgb(R: 240, G: 240, B: 234),
    ];

    /// <summary>The shared gameplay object palette (the fallback cursor renders with it): transparent, ink, gold,
    /// white.</summary>
    public static HgbImage.Rgb[] ObjectPalette => [
        new HgbImage.Rgb(R: 0, G: 0, B: 0),
        new HgbImage.Rgb(R: 20, G: 20, B: 28),
        new HgbImage.Rgb(R: 240, G: 200, B: 88),
        new HgbImage.Rgb(R: 255, G: 255, B: 255),
    ];

    /// <summary>Builds the 52-card record table (stride 4): rank (1..13), suit (0..3), red flag, and the
    /// PRECOMPUTED rank-corner tile id — so the SM83 renderer reads its tile straight from the record and the rules
    /// read rank/suit/colour from the same row. Declare with
    /// <c>manifest.DefineRecords(name: …, stride: 4, records: CardTables.BuildCardRecords())</c>.</summary>
    /// <returns>The 52 records, in card-id order.</returns>
    public static IReadOnlyList<byte[]> BuildCardRecords() {
        var records = new List<byte[]>(capacity: CardDeck.CardCount);

        for (var id = 0; (id < CardDeck.CardCount); id++) {
            var rank = CardDeck.RankOf(id: id);
            var suit = CardDeck.SuitOf(id: id);
            var red = CardDeck.IsRedSuit(suit: suit);
            var rankTile = (byte)((red ? TileRankRedBase : TileRankBlackBase) + (rank - 1));

            records.Add(item: [(byte)rank, (byte)suit, (byte)(red ? 1 : 0), rankTile]);
        }

        return records;
    }

    /// <summary>Builds the card tile segment (<see cref="CardTileCount"/> tiles): blank felt, the 13 rank corners in
    /// black then red, the 4 suit corners, the card bottoms, the back block, the outline block, and the clip
    /// marker.</summary>
    /// <returns>The 2bpp tile bytes.</returns>
    public static byte[] BuildCardTiles() {
        var tiles = new List<byte[]> {
            new byte[16], // Blank (all colour 0 = felt).
        };

        for (var rank = 0; (rank < CardDeck.RankCount); rank++) {
            tiles.Add(item: EncodeCornerTile(glyph: RankGlyphs[rank], ink: ColourInk, rightBorder: false));
        }

        for (var rank = 0; (rank < CardDeck.RankCount); rank++) {
            tiles.Add(item: EncodeCornerTile(glyph: RankGlyphs[rank], ink: ColourRed, rightBorder: false));
        }

        tiles.Add(item: EncodeCornerTile(glyph: SuitGlyphs[0], ink: ColourInk, rightBorder: true));
        tiles.Add(item: EncodeCornerTile(glyph: SuitGlyphs[1], ink: ColourRed, rightBorder: true));
        tiles.Add(item: EncodeCornerTile(glyph: SuitGlyphs[2], ink: ColourRed, rightBorder: true));
        tiles.Add(item: EncodeCornerTile(glyph: SuitGlyphs[3], ink: ColourInk, rightBorder: true));
        tiles.Add(item: EncodeBottomTile(left: true));
        tiles.Add(item: EncodeBottomTile(left: false));
        tiles.AddRange(collection: BuildBackTiles());
        tiles.AddRange(collection: BuildOutlineTiles());
        tiles.Add(item: EncodeMarkerTile());

        if (tiles.Count != CardTileCount) {
            throw new InvalidOperationException(message: $"The card tile set built {tiles.Count} tiles, not the pinned {CardTileCount}.");
        }

        var bytes = new byte[(tiles.Count * 16)];

        for (var index = 0; (index < tiles.Count); index++) {
            tiles[index].CopyTo(array: bytes, index: (index * 16));
        }

        return bytes;
    }

    /// <summary>Builds the deterministic no-GPU fallback cursor bundle: two hand-authored 8×8 frames (point and
    /// grab) on one gold-and-ink object palette, in the exact <c>PBAK</c> section shape the linker consumes — so a
    /// game's <see cref="GameManifest.DefineSpriteArt"/> call is identical whether the cursor was SDF-baked or not.</summary>
    /// <returns>The one-sprite-set bundle.</returns>
    public static PbakBundle BuildFallbackCursorBundle() {
        var tiles = new byte[32];

        EncodeGlyphTile(rows: [
            "#.......",
            "##......",
            "#2#.....",
            "#22#....",
            "#222#...",
            "#22###..",
            "#2#.....",
            "##......",
        ]).CopyTo(array: tiles, index: 0);
        EncodeGlyphTile(rows: [
            "........",
            ".####...",
            "#2222#..",
            "#2222#..",
            "#2222#..",
            "#2222#..",
            ".####...",
            "........",
        ]).CopyTo(array: tiles, index: 16);

        // Two one-entry frames (dy = dx = 0, palette 0 of the set); the linker rebases tile ids and palette bits.
        var frames = new List<PbakMetaspriteFrame> {
            new(Rows: [0x00, 0x00, 0x00, 0x00]),
            new(Rows: [0x00, 0x00, 0x01, 0x00]),
        };

        return new PbakBundle(
            Background: null,
            Sprites: [
                new PbakSpriteSet(
                    AnimationPayload: null,
                    Frames: frames,
                    PaletteData: HgbImage.EncodePalette(palette: ObjectPalette),
                    Registers: null,
                    Tiles2bpp: tiles
                ),
            ]
        );
    }

    // A rank/suit corner tile: ink border along the top and one side, white card body, a 5×6 glyph in the given ink.
    private static byte[] EncodeCornerTile(string[] glyph, byte ink, bool rightBorder) {
        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            for (var column = 0; (column < 8); column++) {
                indices[((row * 8) + column)] = PickCornerColour(column: column, glyph: glyph, ink: ink, rightBorder: rightBorder, row: row);
            }
        }

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }
    private static byte PickCornerColour(int column, string[] glyph, byte ink, bool rightBorder, int row) {
        if ((row == 0) || (rightBorder ? (column == 7) : (column == 0))) {
            return ColourInk;
        }

        // The glyph occupies columns 2..6 (left corner) or 1..5 (right corner), rows 1..6.
        var glyphColumn = (column - (rightBorder ? 1 : 2));
        var glyphRow = (row - 1);

        if ((glyphRow >= 0) && (glyphRow < 6) && (glyphColumn >= 0) && (glyphColumn < 5) && (glyph[glyphRow][glyphColumn] == '#')) {
            return ink;
        }

        return ColourWhite;
    }

    // A card bottom tile: white body, ink along the bottom and one side.
    private static byte[] EncodeBottomTile(bool left) {
        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            for (var column = 0; (column < 8); column++) {
                var border = ((row == 7) || (left ? (column == 0) : (column == 7)));

                indices[((row * 8) + column)] = (border ? ColourInk : ColourWhite);
            }
        }

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }

    // The card back's 2×2 block: ink frame with a red-on-white lattice fill.
    private static List<byte[]> BuildBackTiles() {
        var tiles = new List<byte[]>(capacity: 4);

        for (var quadrant = 0; (quadrant < 4); quadrant++) {
            var isTop = (quadrant < 2);
            var isLeft = ((quadrant % 2) == 0);
            var indices = new byte[64];

            for (var row = 0; (row < 8); row++) {
                for (var column = 0; (column < 8); column++) {
                    var border = ((isTop && (row == 0)) || (!isTop && (row == 7)) || (isLeft && (column == 0)) || (!isLeft && (column == 7)));
                    var globalRow = (row + (isTop ? 0 : 8));
                    var globalColumn = (column + (isLeft ? 0 : 8));
                    var lattice = (((globalRow + globalColumn) % 4) < 2);

                    indices[((row * 8) + column)] = (border ? ColourInk : (lattice ? ColourRed : ColourWhite));
                }
            }

            tiles.Add(item: HgbImage.EncodeTile2bpp(tileIndices: indices));
        }

        return tiles;
    }

    // The empty-slot outline's 2×2 block: a thin ink rectangle on open felt.
    private static List<byte[]> BuildOutlineTiles() {
        var tiles = new List<byte[]>(capacity: 4);

        for (var quadrant = 0; (quadrant < 4); quadrant++) {
            var isTop = (quadrant < 2);
            var isLeft = ((quadrant % 2) == 0);
            var indices = new byte[64];

            for (var row = 0; (row < 8); row++) {
                for (var column = 0; (column < 8); column++) {
                    var border = ((isTop && (row == 0)) || (!isTop && (row == 7)) || (isLeft && (column == 0)) || (!isLeft && (column == 7)));

                    indices[((row * 8) + column)] = (border ? ColourInk : ColourFelt);
                }
            }

            tiles.Add(item: HgbImage.EncodeTile2bpp(tileIndices: indices));
        }

        return tiles;
    }

    // The clip marker: a small white "+" on felt (a tall column has more cards above the visible fan).
    private static byte[] EncodeMarkerTile() =>
        EncodeGlyphTile(rows: [
            "........",
            "...33...",
            "...33...",
            ".333333.",
            ".333333.",
            "...33...",
            "...33...",
            "........",
        ]);

    // Encodes an 8×8 pattern where '#' = ink (1), '2' = colour 2, '3' = colour 3, anything else = 0.
    private static byte[] EncodeGlyphTile(string[] rows) {
        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            for (var column = 0; (column < 8); column++) {
                indices[((row * 8) + column)] = (byte)(((column < rows[row].Length) ? rows[row][column] : '.') switch {
                    '#' => 1,
                    '2' => 2,
                    '3' => 3,
                    _ => 0,
                });
            }
        }

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }

    // The 13 rank glyphs (ace through king), 5×6, '#' = ink. The ten is a condensed "10".
    private static readonly string[][] RankGlyphs = [
        [".###.", "#...#", "#...#", "#####", "#...#", "#...#"],
        ["####.", "....#", "..##.", ".#...", "#....", "#####"],
        ["####.", "....#", ".###.", "....#", "....#", "####."],
        ["#..#.", "#..#.", "#####", "...#.", "...#.", "...#."],
        ["#####", "#....", "####.", "....#", "....#", "####."],
        [".###.", "#....", "####.", "#...#", "#...#", ".###."],
        ["#####", "....#", "...#.", "..#..", "..#..", "..#.."],
        [".###.", "#...#", ".###.", "#...#", "#...#", ".###."],
        [".###.", "#...#", ".####", "....#", "....#", ".###."],
        ["#.###", "#.#.#", "#.#.#", "#.#.#", "#.#.#", "#.###"],
        ["..###", "...#.", "...#.", "...#.", "#..#.", ".##.."],
        [".###.", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"],
        ["#...#", "#..#.", "###..", "#.#..", "#..#.", "#...#"],
    ];

    // The 4 suit glyphs (spade, heart, diamond, club), 5×6, '#' = the suit's ink.
    private static readonly string[][] SuitGlyphs = [
        ["..#..", ".###.", "#####", "#####", "..#..", ".###."],
        [".#.#.", "#####", "#####", ".###.", "..#..", "....."],
        ["..#..", ".###.", "#####", ".###.", "..#..", "....."],
        ["..#..", ".###.", "#.#.#", "#####", "..#..", ".###."],
    ];
}
