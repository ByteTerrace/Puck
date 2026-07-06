namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The wire form of a <see cref="BakedAssetBundle"/> — a self-contained <c>PBAK</c> blob a cartridge assembler (or an
/// external tool) can consume without the pipeline in memory. Little-endian throughout: the header is <c>"PBAK"</c>
/// (4 ASCII bytes) + u16 version (1) + u16 chunk count, followed by <c>{fourcc u32, byteLength u32, payload}</c>
/// chunks. Chunk order is FIXED — the background's (TILE, MAPX, ATTR, PALB, DMGP) then each sprite set's (TILE,
/// PALO, DMGP, META, ANIM) — and every payload derives from the bundle alone, so the same bundle always serializes
/// to the same bytes.
/// </summary>
internal static class BakedAssetBlob {
    private const ushort Version = 1;
    // The default animation loop's cadence (in ticks per frame) — a steady walk-speed loop over every frame.
    private const byte DefaultTicksPerFrame = 8;

    /// <summary>Serializes the bundle to its <c>PBAK</c> blob.</summary>
    /// <param name="bundle">The bundle to serialize.</param>
    /// <returns>The blob bytes.</returns>
    public static byte[] ToBlob(this BakedAssetBundle bundle) =>
        ToBlob(bundle: bundle, chunks: out _);

    /// <summary>Serializes the bundle and reports the emitted chunk list (fourcc + payload length, in wire order) —
    /// the tool modes print it beside the blob path.</summary>
    /// <param name="bundle">The bundle to serialize.</param>
    /// <param name="chunks">The emitted chunks.</param>
    /// <returns>The blob bytes.</returns>
    public static byte[] ToBlob(this BakedAssetBundle bundle, out IReadOnlyList<(string FourCc, int ByteLength)> chunks) {
        ArgumentNullException.ThrowIfNull(bundle);

        var built = BuildChunks(bundle: bundle);
        var summary = new List<(string FourCc, int ByteLength)>(capacity: built.Count);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(output: stream);

        writer.Write(buffer: "PBAK"u8);
        writer.Write(value: Version);
        writer.Write(value: (ushort)built.Count);

        foreach (var (fourCc, payload) in built) {
            foreach (var character in fourCc) {
                writer.Write(value: (byte)character);
            }

            writer.Write(value: (uint)payload.Length);
            writer.Write(buffer: payload);
            summary.Add(item: (fourCc, payload.Length));
        }

        writer.Flush();
        chunks = summary;

        return stream.ToArray();
    }

    // The fixed chunk plan: background chunks first, then each sprite set's. ANIM emits only when there is an
    // animation to carry — a single default loop over all frames (a lone frame is a static pose, not an animation).
    private static List<(string FourCc, byte[] Payload)> BuildChunks(BakedAssetBundle bundle) {
        var chunks = new List<(string FourCc, byte[] Payload)>();

        if (bundle.Background is { } background) {
            chunks.Add(item: ("TILE", TilePayload(tiles: background.Tiles)));
            chunks.Add(item: ("MAPX", MapPayload(tileMap: background.TileMap)));

            if (background.AttributeMap is { } attributes) {
                chunks.Add(item: ("ATTR", attributes));
            }

            chunks.Add(item: ("PALB", PalettePayload(palettes: background.Palettes)));

            if (background.Registers is { } registers) {
                chunks.Add(item: ("DMGP", [registers.Bgp, registers.Obp0, registers.Obp1]));
            }
        }

        foreach (var sprites in bundle.Sprites) {
            chunks.Add(item: ("TILE", TilePayload(tiles: sprites.Tiles)));
            chunks.Add(item: ("PALO", PalettePayload(palettes: sprites.Palettes)));

            if (sprites.Registers is { } registers) {
                chunks.Add(item: ("DMGP", [registers.Bgp, registers.Obp0, registers.Obp1]));
            }

            chunks.Add(item: ("META", MetaspritePayload(frames: sprites.Frames)));

            if (sprites.Frames.Count > 1) {
                chunks.Add(item: ("ANIM", AnimationPayload(frameCount: sprites.Frames.Count)));
            }
        }

        return chunks;
    }

    // TILE: u16 tile count, then the tiles' 2bpp bytes (16 per tile, bank order).
    private static byte[] TilePayload(BakedTileSet tiles) {
        var payload = new byte[2 + tiles.TileData.Length];

        payload[0] = (byte)(tiles.Count & 0xFF);
        payload[1] = (byte)((tiles.Count >> 8) & 0xFF);
        tiles.TileData.CopyTo(array: payload, index: 2);

        return payload;
    }

    // MAPX: u16 width, u16 height (the hardware's 32×32 cells), then the cell bytes row-major.
    private static byte[] MapPayload(byte[] tileMap) {
        const int side = 32;
        var payload = new byte[4 + tileMap.Length];

        payload[0] = side;
        payload[2] = side;
        tileMap.CopyTo(array: payload, index: 4);

        return payload;
    }

    // PALB/PALO: u8 palette count, then count × 8 bytes of little-endian RGB555 (already the palette-RAM wire form).
    private static byte[] PalettePayload(BakedPaletteSet palettes) {
        var payload = new byte[1 + palettes.Rgb555Data.Length];

        payload[0] = (byte)palettes.Count;
        palettes.Rgb555Data.CopyTo(array: payload, index: 1);

        return payload;
    }

    // META: u16 frame count; per frame a u8 entry count then entries × 4 bytes in OAM order (dy, dx, tile, attr).
    private static byte[] MetaspritePayload(IReadOnlyList<MetaspriteFrame> frames) {
        var length = 2;

        foreach (var frame in frames) {
            length += (1 + (frame.Entries.Count * 4));
        }

        var payload = new byte[length];
        var offset = 0;

        payload[offset++] = (byte)(frames.Count & 0xFF);
        payload[offset++] = (byte)((frames.Count >> 8) & 0xFF);

        foreach (var frame in frames) {
            payload[offset++] = (byte)frame.Entries.Count;

            foreach (var entry in frame.Entries) {
                payload[offset++] = (byte)entry.OffsetY;
                payload[offset++] = (byte)entry.OffsetX;
                payload[offset++] = entry.TileId;
                payload[offset++] = entry.Attributes;
            }
        }

        return payload;
    }

    // ANIM: u8 animation count (1); the default loop = u8 name length + ASCII name, u8 frame count, u8 ticks per
    // frame, then the frame ids in play order (every metasprite frame, once).
    private static byte[] AnimationPayload(int frameCount) {
        const string name = "default";
        var payload = new byte[1 + 1 + name.Length + 2 + frameCount];
        var offset = 0;

        payload[offset++] = 1;
        payload[offset++] = (byte)name.Length;

        foreach (var character in name) {
            payload[offset++] = (byte)character;
        }

        payload[offset++] = (byte)frameCount;
        payload[offset++] = DefaultTicksPerFrame;

        for (var frame = 0; (frame < frameCount); frame++) {
            payload[offset++] = (byte)frame;
        }

        return payload;
    }
}
