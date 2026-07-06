namespace Puck.Demo.Forge.Framework;

/// <summary>The DMG shade-ramp registers a <c>DMGP</c> chunk carries (BGP, OBP0, OBP1). Parsed for wire-form
/// completeness; the framework cartridge is Color-required, so the linker ignores them.</summary>
/// <param name="Bgp">The background palette register.</param>
/// <param name="Obp0">Object palette register 0.</param>
/// <param name="Obp1">Object palette register 1.</param>
internal readonly record struct PbakDmgRegisters(byte Bgp, byte Obp0, byte Obp1);

/// <summary>A parsed background section: deduplicated 2bpp tiles, the 32×32 tilemap referencing them ZERO-based (the
/// linker relocates onto the composed tile bank), the optional CGB attribute map (palette bits 0-2 zero-based, flip
/// bits 5/6), and the palette table in palette-RAM wire form (8 bytes per palette, little-endian RGB555).</summary>
/// <param name="Tiles2bpp">The tiles' 2bpp bytes (16 per tile, bank order).</param>
/// <param name="TileMap">The 1024-byte 32×32 tilemap.</param>
/// <param name="AttributeMap">The 1024-byte CGB attribute map, or <see langword="null"/> on a DMG bake.</param>
/// <param name="PaletteData">The background palettes' bytes.</param>
/// <param name="Registers">The DMG registers, or <see langword="null"/> on a CGB bake.</param>
internal sealed record PbakBackground(byte[] Tiles2bpp, byte[] TileMap, byte[]? AttributeMap, byte[] PaletteData, PbakDmgRegisters? Registers) {
    /// <summary>How many 8×8 tiles the section carries.</summary>
    public int TileCount => (Tiles2bpp.Length / 16);
    /// <summary>How many 4-colour palettes the section carries.</summary>
    public int PaletteCount => (PaletteData.Length / 8);
}

/// <summary>One parsed metasprite frame: anchor-relative OAM rows, 4 bytes each in (dy, dx, tile, attributes) order —
/// exactly the row shape <see cref="OamManager.EmitDrawMetasprite"/> walks.</summary>
/// <param name="Rows">The frame's row bytes (tile ids zero-based within the owning set until the linker relocates them).</param>
internal sealed record PbakMetaspriteFrame(byte[] Rows) {
    /// <summary>How many OAM entries the frame carries.</summary>
    public int EntryCount => (Rows.Length / 4);
}

/// <summary>A parsed sprite section: the shared tile set and object palettes (fitted jointly across every frame), the
/// per-pose metasprite frames, and the raw <c>ANIM</c> payload when the set carries an animation.</summary>
/// <param name="Tiles2bpp">The tiles' 2bpp bytes (16 per tile, shared by every frame).</param>
/// <param name="PaletteData">The object palettes' bytes (8 per palette; slot 0 of each is transparent).</param>
/// <param name="Registers">The DMG registers, or <see langword="null"/> on a CGB bake.</param>
/// <param name="Frames">The metasprite frames, in wire order.</param>
/// <param name="AnimationPayload">The raw <c>ANIM</c> chunk payload (u8 animation count; per animation a u8 name
/// length + ASCII name, u8 frame count, u8 ticks per frame, then the frame ids in play order), or <see langword="null"/>.</param>
internal sealed record PbakSpriteSet(byte[] Tiles2bpp, byte[] PaletteData, PbakDmgRegisters? Registers, IReadOnlyList<PbakMetaspriteFrame> Frames, byte[]? AnimationPayload) {
    /// <summary>How many 8×8 tiles the section carries.</summary>
    public int TileCount => (Tiles2bpp.Length / 16);
    /// <summary>How many 4-colour palettes the section carries.</summary>
    public int PaletteCount => (PaletteData.Length / 8);
}

/// <summary>
/// The framework-side reader of the bake's <c>PBAK</c> wire form — the inverse of the bake pipeline's writer, so the
/// framework links exactly the bytes an external assembler would receive. Little-endian throughout: the header is
/// <c>"PBAK"</c> + u16 version (1) + u16 chunk count, followed by <c>{fourcc, u32 byteLength, payload}</c> chunks in
/// the FIXED order — the background's (TILE, MAPX, ATTR, PALB, DMGP) then each sprite set's (TILE, PALO, DMGP, META,
/// ANIM). <see cref="Parse"/> works on raw bytes only (no bake types), preserving the framework's
/// Sm83Emitter-plus-HgbImage-only dependency posture: a blob loaded from disk links identically to one straight out
/// of the pipeline.
/// </summary>
/// <param name="Background">The background section, or <see langword="null"/>.</param>
/// <param name="Sprites">The sprite sections, in wire order.</param>
internal sealed record PbakBundle(PbakBackground? Background, IReadOnlyList<PbakSpriteSet> Sprites) {
    private const ushort SupportedVersion = 1;
    private const int HeaderByteCount = 8;
    private const int MapSide = 32;
    private const int MapByteCount = (MapSide * MapSide);

    /// <summary>Parses a <c>PBAK</c> blob. Throws <see cref="InvalidDataException"/> on any malformed byte — a bundle
    /// is either consumed whole or rejected, never half-trusted.</summary>
    /// <param name="blob">The blob bytes.</param>
    /// <returns>The parsed bundle.</returns>
    public static PbakBundle Parse(byte[] blob) {
        ArgumentNullException.ThrowIfNull(blob);

        var chunks = ReadChunks(blob: blob);
        var index = 0;
        var background = (IsBackgroundStart(chunks: chunks, index: index) ? ParseBackground(chunks: chunks, index: ref index) : null);
        var sprites = new List<PbakSpriteSet>();

        while (index < chunks.Count) {
            sprites.Add(item: ParseSpriteSet(chunks: chunks, index: ref index));
        }

        return new PbakBundle(Background: background, Sprites: sprites);
    }

    // Validates the header and slices the chunk list (fourcc + payload copy per chunk, bounds-checked).
    private static List<(string FourCc, byte[] Payload)> ReadChunks(byte[] blob) {
        if ((blob.Length < HeaderByteCount) || (blob[0] != (byte)'P') || (blob[1] != (byte)'B') || (blob[2] != (byte)'A') || (blob[3] != (byte)'K')) {
            throw new InvalidDataException(message: "Not a PBAK blob (bad magic).");
        }

        var version = ReadU16(blob: blob, offset: 4);

        if (version != SupportedVersion) {
            throw new InvalidDataException(message: $"PBAK version {version} is not supported (expected {SupportedVersion}).");
        }

        var chunkCount = ReadU16(blob: blob, offset: 6);
        var chunks = new List<(string FourCc, byte[] Payload)>(capacity: chunkCount);
        var offset = HeaderByteCount;

        for (var chunk = 0; (chunk < chunkCount); chunk++) {
            if ((offset + 8) > blob.Length) {
                throw new InvalidDataException(message: $"PBAK chunk {chunk} header overruns the blob ({blob.Length} bytes).");
            }

            var fourCc = string.Create(length: 4, state: (blob, offset), action: static (span, state) => {
                for (var character = 0; (character < 4); character++) {
                    span[character] = (char)state.blob[state.offset + character];
                }
            });
            var byteLength = ReadU32(blob: blob, offset: (offset + 4));

            offset += 8;

            if ((byteLength > int.MaxValue) || ((offset + (int)byteLength) > blob.Length)) {
                throw new InvalidDataException(message: $"PBAK chunk '{fourCc}' payload ({byteLength} bytes) overruns the blob.");
            }

            var payload = new byte[(int)byteLength];

            Array.Copy(sourceArray: blob, sourceIndex: offset, destinationArray: payload, destinationIndex: 0, length: payload.Length);
            chunks.Add(item: (fourCc, payload));
            offset += payload.Length;
        }

        return chunks;
    }

    // A background section is a TILE chunk immediately followed by MAPX (a sprite section's TILE is followed by PALO).
    private static bool IsBackgroundStart(List<(string FourCc, byte[] Payload)> chunks, int index) =>
        ((index < (chunks.Count - 1)) && (chunks[index].FourCc == "TILE") && (chunks[index + 1].FourCc == "MAPX"));

    // TILE, MAPX, [ATTR], PALB, [DMGP].
    private static PbakBackground ParseBackground(List<(string FourCc, byte[] Payload)> chunks, ref int index) {
        var tiles = ParseTiles(payload: Expect(chunks: chunks, index: ref index, fourCc: "TILE"));
        var map = ParseMap(payload: Expect(chunks: chunks, index: ref index, fourCc: "MAPX"));
        var attributes = TryTake(chunks: chunks, index: ref index, fourCc: "ATTR");

        if ((attributes is not null) && (attributes.Length != MapByteCount)) {
            throw new InvalidDataException(message: $"An ATTR payload is {MapByteCount} bytes (got {attributes.Length}).");
        }

        var palettes = ParsePalettes(payload: Expect(chunks: chunks, index: ref index, fourCc: "PALB"), fourCc: "PALB");
        var registers = ParseRegisters(payload: TryTake(chunks: chunks, index: ref index, fourCc: "DMGP"));

        return new PbakBackground(Tiles2bpp: tiles, TileMap: map, AttributeMap: attributes, PaletteData: palettes, Registers: registers);
    }

    // TILE, PALO, [DMGP], META, [ANIM].
    private static PbakSpriteSet ParseSpriteSet(List<(string FourCc, byte[] Payload)> chunks, ref int index) {
        var tiles = ParseTiles(payload: Expect(chunks: chunks, index: ref index, fourCc: "TILE"));
        var palettes = ParsePalettes(payload: Expect(chunks: chunks, index: ref index, fourCc: "PALO"), fourCc: "PALO");
        var registers = ParseRegisters(payload: TryTake(chunks: chunks, index: ref index, fourCc: "DMGP"));
        var frames = ParseFrames(payload: Expect(chunks: chunks, index: ref index, fourCc: "META"));
        var animation = TryTake(chunks: chunks, index: ref index, fourCc: "ANIM");

        return new PbakSpriteSet(Tiles2bpp: tiles, PaletteData: palettes, Registers: registers, Frames: frames, AnimationPayload: animation);
    }

    // TILE: u16 tile count, then 16 2bpp bytes per tile.
    private static byte[] ParseTiles(byte[] payload) {
        if (payload.Length < 2) {
            throw new InvalidDataException(message: "A TILE payload is at least 2 bytes.");
        }

        var count = ReadU16(blob: payload, offset: 0);

        if (payload.Length != (2 + (count * 16))) {
            throw new InvalidDataException(message: $"A TILE payload of {count} tiles is {2 + (count * 16)} bytes (got {payload.Length}).");
        }

        return payload[2..];
    }

    // MAPX: u16 width (32), u16 height (32), then the cell bytes row-major.
    private static byte[] ParseMap(byte[] payload) {
        if ((payload.Length != (4 + MapByteCount)) || (ReadU16(blob: payload, offset: 0) != MapSide) || (ReadU16(blob: payload, offset: 2) != MapSide)) {
            throw new InvalidDataException(message: $"A MAPX payload is a {MapSide}×{MapSide} map ({4 + MapByteCount} bytes).");
        }

        return payload[4..];
    }

    // PALB/PALO: u8 palette count, then count × 8 bytes of little-endian RGB555.
    private static byte[] ParsePalettes(byte[] payload, string fourCc) {
        if ((payload.Length < 1) || (payload.Length != (1 + (payload[0] * 8)))) {
            throw new InvalidDataException(message: $"A {fourCc} payload is a u8 count plus 8 bytes per palette.");
        }

        return payload[1..];
    }

    // DMGP: BGP, OBP0, OBP1.
    private static PbakDmgRegisters? ParseRegisters(byte[]? payload) {
        if (payload is null) {
            return null;
        }

        if (payload.Length != 3) {
            throw new InvalidDataException(message: $"A DMGP payload is 3 bytes (got {payload.Length}).");
        }

        return new PbakDmgRegisters(Bgp: payload[0], Obp0: payload[1], Obp1: payload[2]);
    }

    // META: u16 frame count; per frame a u8 entry count then entries × 4 bytes in OAM order (dy, dx, tile, attr).
    private static List<PbakMetaspriteFrame> ParseFrames(byte[] payload) {
        if (payload.Length < 2) {
            throw new InvalidDataException(message: "A META payload is at least 2 bytes.");
        }

        var frameCount = ReadU16(blob: payload, offset: 0);
        var frames = new List<PbakMetaspriteFrame>(capacity: frameCount);
        var offset = 2;

        for (var frame = 0; (frame < frameCount); frame++) {
            if (offset >= payload.Length) {
                throw new InvalidDataException(message: $"META frame {frame} overruns the payload.");
            }

            var entryCount = payload[offset++];
            var byteCount = (entryCount * 4);

            if ((offset + byteCount) > payload.Length) {
                throw new InvalidDataException(message: $"META frame {frame} ({entryCount} entries) overruns the payload.");
            }

            frames.Add(item: new PbakMetaspriteFrame(Rows: payload[offset..(offset + byteCount)]));
            offset += byteCount;
        }

        if (offset != payload.Length) {
            throw new InvalidDataException(message: $"A META payload has {payload.Length - offset} trailing bytes.");
        }

        return frames;
    }

    private static byte[] Expect(List<(string FourCc, byte[] Payload)> chunks, ref int index, string fourCc) {
        if ((index >= chunks.Count) || (chunks[index].FourCc != fourCc)) {
            var found = ((index < chunks.Count) ? $"'{chunks[index].FourCc}'" : "the end of the blob");

            throw new InvalidDataException(message: $"Expected a {fourCc} chunk at index {index}; found {found}.");
        }

        return chunks[index++].Payload;
    }

    private static byte[]? TryTake(List<(string FourCc, byte[] Payload)> chunks, ref int index, string fourCc) =>
        (((index < chunks.Count) && (chunks[index].FourCc == fourCc)) ? chunks[index++].Payload : null);

    private static ushort ReadU16(byte[] blob, int offset) => (ushort)(blob[offset] | (blob[offset + 1] << 8));

    private static uint ReadU32(byte[] blob, int offset) =>
        (uint)(blob[offset] | (blob[offset + 1] << 8) | (blob[offset + 2] << 16) | (blob[offset + 3] << 24));
}
