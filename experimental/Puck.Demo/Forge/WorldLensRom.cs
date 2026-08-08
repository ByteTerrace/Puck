namespace Puck.Demo.Forge;

/// <summary>
/// Builds the overworld's WORLD-LENS cartridge entirely on the CPU — no GPU forge, so it assembles eagerly beside the
/// camera cart. A hand-authored top-down room (a bordered floor with a grid) plus an 8×16 player sprite the ROM
/// repositions every frame from the work-RAM sensor page a host peripheral writes (<see cref="SensorPagePeripheral"/>).
/// The romance is the LIVE membrane, not the tile art: dropped into a cabinet, the brick becomes a lens back onto
/// the very room it stands in — its sprite tracks whoever is walking the overworld. The SDF-forged scene for this cart is
/// the deferred richer upgrade; this authored room is the honest first artifact.
/// </summary>
internal static class WorldLensRom {
    // The CPU background's three tiles (the forged path brings its own deduplicated tile set instead). The goal marker
    // and the player's 8×16 sprite are appended AFTER whichever background is used; the sprite base is rounded up to an
    // EVEN id (hardware pairs tile N with N+1).
    private const int FloorTile = 0;
    private const int GridTile = 2;
    private const int WallTile = 1;

    // The WIN goal: the tile the player's sprite must reach (within the sensor's [1,17]×[1,15] reachable window). A
    // marker is drawn here so the player can see where to run; reaching it raises the ROM's win flag.
    private const byte GoalTileX = 16;
    private const byte GoalTileY = 2;

    // BG palette: floor base / floor light / grid line / wall — a calm room the blue player reads against.
    private static readonly HgbImage.Rgb[] BackgroundPalette = [
        new HgbImage.Rgb(R: 58, G: 78, B: 66),    // 0: floor shadow
        new HgbImage.Rgb(R: 96, G: 132, B: 104),  // 1: floor
        new HgbImage.Rgb(R: 132, G: 172, B: 138), // 2: grid line / highlight
        new HgbImage.Rgb(R: 34, G: 40, B: 52),    // 3: wall
    ];

    // OBJ palette: index 0 is TRANSPARENT on sprites, so the player's background pixels must be index 0.
    private static readonly HgbImage.Rgb[] ObjectPalette = [
        new HgbImage.Rgb(R: 0, G: 0, B: 0),       // 0: transparent
        new HgbImage.Rgb(R: 64, G: 118, B: 224),  // 1: body
        new HgbImage.Rgb(R: 244, G: 208, B: 160), // 2: head / skin
        new HgbImage.Rgb(R: 20, G: 22, B: 44),    // 3: outline
    ];

    // 8×8 tile patterns, one char per pixel: '.' = index 0, '1'..'3' = that palette index.
    private static readonly string[] Floor = [
        "11111111",
        "11111111",
        "11111111",
        "11111111",
        "11111111",
        "11111111",
        "11111111",
        "11111111",
    ];
    private static readonly string[] Wall = [
        "33333333",
        "32222223",
        "33333333",
        "33333333",
        "32222223",
        "33333333",
        "33333333",
        "33333333",
    ];
    // A floor tile with grid lines on its top and left edges, so a field of them reads as a tiled floor.
    private static readonly string[] Grid = [
        "22222222",
        "21111111",
        "21111111",
        "21111111",
        "21111111",
        "21111111",
        "21111111",
        "21111111",
    ];
    // The goal marker: a bright tile with a dark X, clearly distinct from the floor grid so the player can aim for it.
    private static readonly string[] Goal = [
        "22222222",
        "23222232",
        "22322322",
        "22233222",
        "22233222",
        "22322322",
        "23222232",
        "22222222",
    ];
    private static readonly string[] PlayerTop = [
        "..3333..",
        ".322223.",
        ".322223.",
        ".323323.",
        ".322223.",
        "..3223..",
        ".311113.",
        "31111113",
    ];
    private static readonly string[] PlayerBottom = [
        "31111113",
        "31111113",
        "31111113",
        ".311113.",
        ".31..13.",
        ".31..13.",
        ".33..33.",
        ".33..33.",
    ];

    /// <summary>Assembles the world-lens <c>.gbc</c> from CPU-authored tiles — a bordered grid room. Pure CPU and
    /// deterministic (no GPU); the fallback when the SDF forge is unavailable.</summary>
    /// <param name="title">The cartridge header title (e.g. <c>"PUCKLENS"</c>).</param>
    /// <returns>A genuine 32&#160;KiB CGB ROM image.</returns>
    public static byte[] Build(string title) {
        var bgTiles = Concat(EncodeTile(rows: Floor), EncodeTile(rows: Wall), EncodeTile(rows: Grid));

        return Assemble(title: title, backgroundTiles: bgTiles, backgroundPalette: BackgroundPalette, backgroundMap: CpuBackgroundMap());
    }

    /// <summary>Assembles the world-lens <c>.gbc</c> around an SDF-FORGED room background (the two-worlds romance: the SDF
    /// room the player walks becomes the very tiles their brick renders). The forged BG replaces the CPU grid; the
    /// goal marker and player sprite are appended after the forged tiles exactly the same way.</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <param name="room">The forged room assets (deduplicated BG tiles, 32×32 tilemap, median-cut palette).</param>
    /// <returns>A genuine 32&#160;KiB CGB ROM image.</returns>
    public static byte[] BuildFromForgedRoom(string title, SceneForge.RoomAssets room) {
        ArgumentNullException.ThrowIfNull(room);

        return Assemble(title: title, backgroundTiles: room.TileData, backgroundPalette: room.Palette, backgroundMap: (byte[])room.TileMap.Clone());
    }

    // The shared tail: append the goal marker tile and the player's two 8×16 sprite halves after the background tiles
    // (rounding the sprite base up to EVEN), stamp the goal marker into the map at the goal tile, and emit the cartridge.
    // Background and goal share the BG palette; the player sprite carries its own OBJECT palette.
    private static byte[] Assemble(string title, byte[] backgroundTiles, HgbImage.Rgb[] backgroundPalette, byte[] backgroundMap) {
        var backgroundTileCount = (backgroundTiles.Length / 16);
        var goalTileId = backgroundTileCount;
        // The player's 8×16 sprite base must be even; pad one blank tile after the goal when the goal lands on an even id.
        var needsPad = (((goalTileId + 1) & 1) != 0);
        var playerTileBase = (goalTileId + (needsPad ? 2 : 1));

        if ((playerTileBase + 1) > 254) {
            // Too many forged tiles to fit the sprite pair under the single-byte tile budget — the caller falls back to
            // the CPU room (a handful of tiles). Signalled as an exception so the fallback path is explicit.
            throw new InvalidOperationException(message: $"the world-lens background forged {backgroundTileCount} tiles, leaving no room for the player sprite under the 254-tile budget.");
        }

        var parts = new List<byte[]>(capacity: 5) { backgroundTiles, EncodeTile(rows: Goal) };

        if (needsPad) {
            parts.Add(item: new byte[16]); // a blank pad tile so the sprite base is even
        }

        parts.Add(item: EncodeTile(rows: PlayerTop));
        parts.Add(item: EncodeTile(rows: PlayerBottom));

        // Stamp the goal marker into the (top-left, on-screen) goal cell so the player can see where to run.
        backgroundMap[((GoalTileY * 32) + GoalTileX)] = (byte)goalTileId;

        return HgbCartridge.BuildWorldLens(
            title: title,
            backgroundPalette: HgbImage.EncodePalette(palette: backgroundPalette),
            objectPalette: HgbImage.EncodePalette(palette: ObjectPalette),
            tileData: Concat([.. parts]),
            tileMap: backgroundMap,
            playerTileBase: playerTileBase,
            goalTileX: GoalTileX,
            goalTileY: GoalTileY
        );
    }

    // The CPU background's 32×32 map. The screen shows the top-left 20×18: a wall border around a grid-tiled floor.
    // Off-screen cells (columns 20..31, rows 18..31) stay floor — never scrolled into view.
    private static byte[] CpuBackgroundMap() {
        const int mapEdge = 32;
        const int visibleWidth = 20;
        const int visibleHeight = 18;

        var map = new byte[(mapEdge * mapEdge)];

        for (var row = 0; (row < mapEdge); row++) {
            for (var column = 0; (column < mapEdge); column++) {
                var onBorder = ((row == 0) || (column == 0) || (row == (visibleHeight - 1)) || (column == (visibleWidth - 1)));
                var visible = ((row < visibleHeight) && (column < visibleWidth));

                map[((row * mapEdge) + column)] = (byte)((visible && onBorder) ? WallTile : (visible ? GridTile : FloorTile));
            }
        }

        return map;
    }

    // Parse an 8-row pattern into 64 palette indices and encode it to the brick's 2bpp tile format.
    private static byte[] EncodeTile(string[] rows) {
        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            for (var column = 0; (column < 8); column++) {
                indices[((row * 8) + column)] = (byte)(rows[row][column] switch {
                    '1' => 1,
                    '2' => 2,
                    '3' => 3,
                    _ => 0,
                });
            }
        }

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }
    private static byte[] Concat(params byte[][] parts) {
        var length = 0;

        foreach (var part in parts) {
            length += part.Length;
        }

        var result = new byte[length];
        var offset = 0;

        foreach (var part in parts) {
            part.CopyTo(array: result, index: offset);
            offset += part.Length;
        }

        return result;
    }
}
