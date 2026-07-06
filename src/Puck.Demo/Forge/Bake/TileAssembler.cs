namespace Puck.Demo.Forge.Bake;

/// <summary>One quantized view, ready for tile assembly: per-pixel 2-bit indices on the fit's own 8×8 grid, the
/// per-grid-tile palette assignment, and (for sprites) the foreground mask.</summary>
/// <param name="Name">The view's name.</param>
/// <param name="Indices">Per-pixel colour indices (0..3; 0 = transparent on a sprite).</param>
/// <param name="Mask">The foreground mask (sprites), or null (backgrounds).</param>
/// <param name="Width">The native width.</param>
/// <param name="Height">The native height.</param>
/// <param name="TilePalettes">Per grid tile (row-major), the palette index it quantized against.</param>
internal sealed record QuantizedView(string Name, byte[] Indices, bool[]? Mask, int Width, int Height, int[] TilePalettes);

/// <summary>A background assembly plus its dedupe accounting.</summary>
/// <param name="Background">The baked layer.</param>
/// <param name="TileCountBeforeDedupe">Sliced tiles before deduplication.</param>
/// <param name="TileCount">Unique tiles after deduplication.</param>
internal sealed record BackgroundAssembly(BakedBackground Background, int TileCountBeforeDedupe, int TileCount);

/// <summary>A sprite assembly plus its dedupe/OAM accounting.</summary>
/// <param name="Sprites">The baked sprite set.</param>
/// <param name="TileCountBeforeDedupe">Non-transparent sliced tiles before deduplication (all frames).</param>
/// <param name="TileCount">Unique tiles after deduplication.</param>
/// <param name="MaxEntriesPerFrame">The largest OAM entry count any frame needs.</param>
/// <param name="WorstLineSprites">The worst per-scanline sprite count across every frame.</param>
internal sealed record SpriteAssembly(BakedSpriteSet Sprites, int TileCountBeforeDedupe, int TileCount, int MaxEntriesPerFrame, int WorstLineSprites);

/// <summary>
/// Turns quantized views into hardware tiles: 2bpp encoding, dedupe on the encoded bytes (with X/Y-flip matching on
/// CGB — attribute/OAM bits 5/6 carry the flip), the 32×32 background tilemap + attribute map, and the trimmed
/// anchor-relative sprite OAM slicing. Deterministic: tiles enter the bank in raster order, first-seen wins.
/// </summary>
internal static class TileAssembler {
    private const int TileEdge = 8;

    /// <summary>Assembles a background: a 32×32 tilemap (top-left anchored; the visible native grid fills it) over a
    /// deduplicated tile bank, plus — on CGB — the parallel attribute map (palette bits 0-2, flip bits 5/6).</summary>
    /// <param name="view">The quantized background view.</param>
    /// <param name="target">The hardware target (flip dedupe and the attribute map are CGB-only).</param>
    /// <param name="palettes">The already-encoded palette table to carry on the layer.</param>
    /// <param name="registers">The DMG shade registers (null on CGB).</param>
    /// <returns>The assembly with its accounting.</returns>
    public static BackgroundAssembly AssembleBackground(QuantizedView view, BakeTarget target, BakedPaletteSet palettes, DmgRegisters? registers) {
        ArgumentNullException.ThrowIfNull(view);

        var bank = new TileBank(matchFlips: (target == BakeTarget.Cgb));
        var tilesWide = (view.Width / TileEdge);
        var tilesHigh = (view.Height / TileEdge);
        var tileMap = new byte[0x400];
        var attributeMap = ((target == BakeTarget.Cgb) ? new byte[0x400] : null);

        for (var tileY = 0; (tileY < tilesHigh); tileY++) {
            for (var tileX = 0; (tileX < tilesWide); tileX++) {
                var indices = ExtractTile(view: view, tileX: tileX, tileY: tileY);
                var (id, flipX, flipY) = bank.Add(indices: indices);
                var cell = ((tileY * 32) + tileX);

                tileMap[cell] = (byte)(id & 0xFF);

                if (attributeMap is not null) {
                    attributeMap[cell] = AttributeByte(flipX: flipX, flipY: flipY, palette: view.TilePalettes[(tileY * tilesWide) + tileX]);
                }
            }
        }

        return new BackgroundAssembly(
            Background: new BakedBackground(AttributeMap: attributeMap, Palettes: palettes, Registers: registers, TileMap: tileMap, Tiles: bank.ToTileSet()),
            TileCount: bank.Count,
            TileCountBeforeDedupe: (tilesWide * tilesHigh)
        );
    }

    /// <summary>Assembles a sprite set: every frame's foreground trims to its 8-aligned box, slices into 8×8 OAM
    /// entries (fully transparent tiles skipped) with offsets relative to the native cell's CENTRE anchor, and all
    /// frames share one deduplicated tile bank so animation costs only its unique tiles.</summary>
    /// <param name="views">The quantized pose views, in plan order.</param>
    /// <param name="target">The hardware target (flip dedupe is CGB-only; DMG OAM attributes stay zero).</param>
    /// <param name="palettes">The already-encoded object palette table.</param>
    /// <param name="registers">The DMG shade registers (null on CGB).</param>
    /// <returns>The assembly with its accounting.</returns>
    public static SpriteAssembly AssembleSprites(IReadOnlyList<QuantizedView> views, BakeTarget target, BakedPaletteSet palettes, DmgRegisters? registers) {
        ArgumentNullException.ThrowIfNull(views);

        var bank = new TileBank(matchFlips: (target == BakeTarget.Cgb));
        var frames = new List<MetaspriteFrame>(capacity: views.Count);
        var slicedTiles = 0;
        var maxEntries = 0;
        var worstLine = 0;

        foreach (var view in views) {
            var entries = SliceFrame(bank: bank, slicedTiles: ref slicedTiles, target: target, view: view);

            frames.Add(item: new MetaspriteFrame(Entries: entries, Name: view.Name));
            maxEntries = Math.Max(val1: maxEntries, val2: entries.Count);
            worstLine = Math.Max(val1: worstLine, val2: WorstScanline(entries: entries));
        }

        return new SpriteAssembly(
            MaxEntriesPerFrame: maxEntries,
            Sprites: new BakedSpriteSet(Frames: frames, Palettes: palettes, Registers: registers, Tiles: bank.ToTileSet()),
            TileCount: bank.Count,
            TileCountBeforeDedupe: slicedTiles,
            WorstLineSprites: worstLine
        );
    }

    private static List<OamEntry> SliceFrame(TileBank bank, ref int slicedTiles, BakeTarget target, QuantizedView view) {
        var entries = new List<OamEntry>();

        if (!TryTrimBox(view: view, minTileX: out var minTileX, minTileY: out var minTileY, maxTileX: out var maxTileX, maxTileY: out var maxTileY)) {
            return entries;
        }

        // The anchor is the native cell's centre, so every pose of a walking subject shares one reference point.
        var anchorX = (view.Width / 2);
        var anchorY = (view.Height / 2);
        var tilesWide = (view.Width / TileEdge);

        for (var tileY = minTileY; (tileY <= maxTileY); tileY++) {
            for (var tileX = minTileX; (tileX <= maxTileX); tileX++) {
                var indices = ExtractTile(view: view, tileX: tileX, tileY: tileY);

                if (IsFullyTransparent(indices: indices)) {
                    continue;
                }

                slicedTiles++;

                var (id, flipX, flipY) = bank.Add(indices: indices);
                var attributes = ((target == BakeTarget.Cgb)
                    ? AttributeByte(flipX: flipX, flipY: flipY, palette: view.TilePalettes[(tileY * tilesWide) + tileX])
                    : (byte)0);

                entries.Add(item: new OamEntry(
                    Attributes: attributes,
                    OffsetX: (sbyte)((tileX * TileEdge) - anchorX),
                    OffsetY: (sbyte)((tileY * TileEdge) - anchorY),
                    TileId: (byte)(id & 0xFF)
                ));
            }
        }

        return entries;
    }

    // The frame's foreground bounding box, in GRID-TILE coordinates (the trim snaps to the fit's own 8×8 grid so an
    // assembled tile's pixels never straddle two fitted palettes). False = an empty frame.
    private static bool TryTrimBox(QuantizedView view, out int minTileX, out int minTileY, out int maxTileX, out int maxTileY) {
        var minX = view.Width;
        var minY = view.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; (y < view.Height); y++) {
            for (var x = 0; (x < view.Width); x++) {
                if ((view.Mask is { } mask) ? !mask[(y * view.Width) + x] : (view.Indices[(y * view.Width) + x] == 0)) {
                    continue;
                }

                minX = Math.Min(val1: minX, val2: x);
                minY = Math.Min(val1: minY, val2: y);
                maxX = Math.Max(val1: maxX, val2: x);
                maxY = Math.Max(val1: maxY, val2: y);
            }
        }

        minTileX = (minX / TileEdge);
        minTileY = (minY / TileEdge);
        maxTileX = (maxX / TileEdge);
        maxTileY = (maxY / TileEdge);

        return (maxX >= 0);
    }

    private static byte[] ExtractTile(QuantizedView view, int tileX, int tileY) {
        var indices = new byte[64];

        for (var row = 0; (row < TileEdge); row++) {
            for (var column = 0; (column < TileEdge); column++) {
                indices[(row * TileEdge) + column] = view.Indices[(((tileY * TileEdge) + row) * view.Width) + ((tileX * TileEdge) + column)];
            }
        }

        return indices;
    }

    private static bool IsFullyTransparent(byte[] indices) {
        foreach (var index in indices) {
            if (index != 0) {
                return false;
            }
        }

        return true;
    }

    private static byte AttributeByte(int palette, bool flipX, bool flipY) =>
        (byte)((palette & 0x07) | (flipX ? 0x20 : 0x00) | (flipY ? 0x40 : 0x00));

    // The worst per-scanline sprite count: each 8×8 entry covers rows OffsetY..OffsetY+7 (anchor-relative — only
    // relative overlap matters for the hardware's 10-per-line limit).
    private static int WorstScanline(List<OamEntry> entries) {
        if (entries.Count == 0) {
            return 0;
        }

        var worst = 0;

        foreach (var probe in entries) {
            var line = probe.OffsetY;
            var covering = 0;

            foreach (var entry in entries) {
                if ((entry.OffsetY <= line) && (line < (entry.OffsetY + TileEdge))) {
                    covering++;
                }
            }

            worst = Math.Max(val1: worst, val2: covering);
        }

        return worst;
    }

    // The dedupe bank: tiles enter in raster order; on CGB a tile also matches its X/Y/XY flips (checked in that
    // fixed order) so mirrored art shares VRAM.
    private sealed class TileBank(bool matchFlips) {
        private readonly Dictionary<string, int> m_lookup = new(comparer: StringComparer.Ordinal);
        private readonly List<byte[]> m_tiles = [];

        public int Count => m_tiles.Count;

        public (int Id, bool FlipX, bool FlipY) Add(byte[] indices) {
            var encoded = HgbImage.EncodeTile2bpp(tileIndices: indices);
            var key = Convert.ToHexString(inArray: encoded);

            if (m_lookup.TryGetValue(key: key, value: out var id)) {
                return (id, false, false);
            }

            if (matchFlips && TryMatchFlip(indices: indices, match: out var flipped)) {
                return flipped;
            }

            id = m_tiles.Count;
            m_tiles.Add(item: encoded);
            m_lookup[key] = id;

            return (id, false, false);
        }

        public BakedTileSet ToTileSet() {
            var data = new byte[m_tiles.Count * 16];

            for (var tile = 0; (tile < m_tiles.Count); tile++) {
                m_tiles[tile].CopyTo(array: data, index: (tile * 16));
            }

            return new BakedTileSet(TileData: data);
        }

        private bool TryMatchFlip(byte[] indices, out (int Id, bool FlipX, bool FlipY) match) {
            Span<(bool FlipX, bool FlipY)> variants = [(true, false), (false, true), (true, true)];

            foreach (var (flipX, flipY) in variants) {
                var key = Convert.ToHexString(inArray: HgbImage.EncodeTile2bpp(tileIndices: Flip(flipX: flipX, flipY: flipY, indices: indices)));

                if (m_lookup.TryGetValue(key: key, value: out var id)) {
                    match = (id, flipX, flipY);

                    return true;
                }
            }

            match = default;

            return false;
        }

        private static byte[] Flip(byte[] indices, bool flipX, bool flipY) {
            var flipped = new byte[64];

            for (var row = 0; (row < TileEdge); row++) {
                for (var column = 0; (column < TileEdge); column++) {
                    var sourceRow = (flipY ? (TileEdge - 1 - row) : row);
                    var sourceColumn = (flipX ? (TileEdge - 1 - column) : column);

                    flipped[(row * TileEdge) + column] = indices[(sourceRow * TileEdge) + sourceColumn];
                }
            }

            return flipped;
        }
    }
}
