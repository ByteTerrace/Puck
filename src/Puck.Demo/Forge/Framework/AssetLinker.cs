namespace Puck.Demo.Forge.Framework;

/// <summary>A linked background: where its tiles landed in the composed tile bank, where its palettes landed in the
/// background palette table, and the RELOCATED map/attribute blocks (cells rebased onto the bank, attribute palette
/// bits rebased onto the slots) a game copies to VRAM by table address.</summary>
/// <param name="TileBase">The tile id of the background's first tile in the bank.</param>
/// <param name="TileCount">How many tiles the background added to the bank.</param>
/// <param name="PaletteBase">The background palette slot of the background's first palette.</param>
/// <param name="PaletteCount">How many palette slots the background occupies.</param>
/// <param name="TileMap">The relocated 1024-byte map block.</param>
/// <param name="AttributeMap">The relocated 1024-byte attribute block, or <see langword="null"/> (DMG bake).</param>
internal sealed record LinkedBackground(byte TileBase, int TileCount, byte PaletteBase, int PaletteCount, RomTable TileMap, RomTable? AttributeMap);

/// <summary>A linked sprite set: bank/slot bases, the relocated frame rows, and the runtime frame table. Frames land
/// back to back in <see cref="Frames"/> as bare (dy, dx, tile, attributes) rows — <see cref="OamManager.EmitDrawMetasprite"/>'s
/// row shape, tile ids and palette bits already rebased. <see cref="FrameTable"/> indexes them at run time with a
/// 4-byte stride per frame: address low, address high, entry count, zero (the power-of-two stride keeps SM83 indexing
/// to two adds); <see cref="FrameAddresses"/>/<see cref="FrameEntryCounts"/> carry the same facts at BUILD time for
/// unrolled draws.</summary>
/// <param name="TileBase">The tile id of the set's first tile in the bank.</param>
/// <param name="TileCount">How many tiles the set added to the bank.</param>
/// <param name="PaletteBase">The object palette slot of the set's first palette.</param>
/// <param name="PaletteCount">How many object palette slots the set occupies.</param>
/// <param name="Frames">The frames' concatenated row bytes.</param>
/// <param name="FrameTable">The 4-byte-stride runtime frame index.</param>
/// <param name="FrameAddresses">Each frame's first-row ROM address (build-time indexing).</param>
/// <param name="FrameEntryCounts">Each frame's OAM entry count (build-time draw unrolling).</param>
/// <param name="Animations">The raw <c>ANIM</c> payload block, or <see langword="null"/>.</param>
internal sealed record LinkedSpriteSet(byte TileBase, int TileCount, byte PaletteBase, int PaletteCount, RomTable Frames, RomTable FrameTable, IReadOnlyList<ushort> FrameAddresses, IReadOnlyList<int> FrameEntryCounts, RomTable? Animations);

/// <summary>A relocated (but not yet landed) background: the linker has allocated its bank/slot space and rebased the
/// map/attribute copies, and the caller decides what else to compose onto them (a screen's text overlay) before
/// landing the blocks itself.</summary>
/// <param name="TileBase">The tile id of the background's first tile in the bank.</param>
/// <param name="PaletteBase">The background palette slot of the background's first palette.</param>
/// <param name="TileMap">The relocated 1024-byte map (a fresh copy — safe to mutate).</param>
/// <param name="AttributeMap">The relocated 1024-byte attribute map copy, or <see langword="null"/>.</param>
internal sealed record RelocatedBackground(byte TileBase, byte PaletteBase, byte[] TileMap, byte[]? AttributeMap);

/// <summary>
/// The framework's asset linker: allocates the two scarce hardware tables — the 256-tile VRAM bank and the 8 + 8
/// background/object palette slots — as sequential named segments, relocates parsed <c>PBAK</c> sections onto the
/// allocations (map cells and OAM tiles rebased, attribute/OAM palette bits shifted to the granted slots), and lands
/// the relocated blocks in the cartridge's data window so games reference everything by table address. The bank and
/// palette tables are COMPOSED: every segment rides one boot copy, sealed once (as the reserved <c>tile-bank</c> /
/// <c>bg-palette-table</c> / <c>obj-palette-table</c> blocks) after the last allocation — <see cref="GameManifest"/>
/// drives all of this declaratively, and games with bespoke needs drive it directly through
/// <see cref="GameFramework.Assets"/>.
/// </summary>
internal sealed class AssetLinker {
    /// <summary>The tile bank's capacity (single-byte tile ids under 0x8000 unsigned addressing).</summary>
    public const int TileCapacity = 256;
    /// <summary>The hardware palette-slot capacity per plane (8 background, 8 object).</summary>
    public const int PaletteSlotCapacity = 8;

    private const int PaletteByteCount = 8;
    private const int TileByteCount = 16;

    private readonly RomDataBuilder m_data;
    private readonly List<byte[]> m_tileSegments = [];
    private readonly List<byte[]> m_backgroundPaletteSegments = [];
    private readonly List<byte[]> m_objectPaletteSegments = [];
    private int m_tileCount;
    private int m_backgroundPaletteCount;
    private int m_objectPaletteCount;
    private bool m_tileBankSealed;
    private bool m_backgroundPalettesSealed;
    private bool m_objectPalettesSealed;

    /// <summary>Creates the linker over the cartridge's data window.</summary>
    /// <param name="data">The data window the relocated blocks land in.</param>
    public AssetLinker(RomDataBuilder data) {
        ArgumentNullException.ThrowIfNull(data);

        m_data = data;
    }

    /// <summary>How many tiles the bank holds so far.</summary>
    public int TileCount => m_tileCount;
    /// <summary>How many background palette slots are allocated so far.</summary>
    public int BackgroundPaletteCount => m_backgroundPaletteCount;
    /// <summary>How many object palette slots are allocated so far.</summary>
    public int ObjectPaletteCount => m_objectPaletteCount;

    /// <summary>Allocates a tile segment in the bank and returns its first tile id.</summary>
    /// <param name="name">A diagnostic name for overrun messages.</param>
    /// <param name="tiles2bpp">The segment's 2bpp bytes (16 per tile).</param>
    /// <returns>The segment's first tile id.</returns>
    public byte AddTiles(string name, byte[] tiles2bpp) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(tiles2bpp);
        ThrowIfSealed(sealedFlag: m_tileBankSealed, name: name, table: "tile bank");

        if ((tiles2bpp.Length == 0) || ((tiles2bpp.Length % TileByteCount) != 0)) {
            throw new ArgumentException(message: $"The '{name}' tile segment must be a non-empty multiple of {TileByteCount} bytes.", paramName: nameof(tiles2bpp));
        }

        var count = (tiles2bpp.Length / TileByteCount);

        if ((m_tileCount + count) > TileCapacity) {
            throw new InvalidOperationException(message: $"Adding '{name}' ({count} tiles) overruns the {TileCapacity}-tile bank ({m_tileCount} already allocated).");
        }

        var baseId = (byte)m_tileCount;

        m_tileSegments.Add(item: tiles2bpp);
        m_tileCount += count;

        return baseId;
    }

    /// <summary>Allocates background palette slots and returns the first slot index.</summary>
    /// <param name="name">A diagnostic name for overrun messages.</param>
    /// <param name="paletteData">The palettes' bytes (8 per palette, palette-RAM wire form).</param>
    /// <returns>The first allocated slot.</returns>
    public byte AddBackgroundPalettes(string name, byte[] paletteData) {
        ThrowIfSealed(sealedFlag: m_backgroundPalettesSealed, name: name, table: "background palette table");

        var baseSlot = ValidatePalettes(name: name, paletteData: paletteData, currentCount: m_backgroundPaletteCount, plane: "background");

        m_backgroundPaletteSegments.Add(item: paletteData);
        m_backgroundPaletteCount += (paletteData.Length / PaletteByteCount);

        return baseSlot;
    }

    /// <summary>Allocates object palette slots and returns the first slot index.</summary>
    /// <param name="name">A diagnostic name for overrun messages.</param>
    /// <param name="paletteData">The palettes' bytes (8 per palette, palette-RAM wire form).</param>
    /// <returns>The first allocated slot.</returns>
    public byte AddObjectPalettes(string name, byte[] paletteData) {
        ThrowIfSealed(sealedFlag: m_objectPalettesSealed, name: name, table: "object palette table");

        var baseSlot = ValidatePalettes(name: name, paletteData: paletteData, currentCount: m_objectPaletteCount, plane: "object");

        m_objectPaletteSegments.Add(item: paletteData);
        m_objectPaletteCount += (paletteData.Length / PaletteByteCount);

        return baseSlot;
    }

    /// <summary>Links a parsed background: allocates its tiles and palette slots, relocates the map and attribute
    /// copies, and lands them as the <c>&lt;name&gt;-map</c> / <c>&lt;name&gt;-attributes</c> blocks.</summary>
    /// <param name="name">The link name (also the landed blocks' name prefix).</param>
    /// <param name="background">The parsed background section.</param>
    /// <returns>The linked background.</returns>
    public LinkedBackground LinkBackground(string name, PbakBackground background) {
        var relocated = Relocate(name: name, background: background);
        var map = m_data.Add(name: $"{name}-map", bytes: relocated.TileMap);
        var attributes = ((relocated.AttributeMap is { } attributeMap) ? m_data.Add(name: $"{name}-attributes", bytes: attributeMap) : (RomTable?)null);

        return new LinkedBackground(
            AttributeMap: attributes,
            PaletteBase: relocated.PaletteBase,
            PaletteCount: background.PaletteCount,
            TileBase: relocated.TileBase,
            TileCount: background.TileCount,
            TileMap: map
        );
    }

    /// <summary>Allocates a parsed background's tiles and palette slots and returns RELOCATED map/attribute copies
    /// WITHOUT landing them — the seam a caller composes onto (the manifest's screen text overlay) before landing the
    /// blocks itself.</summary>
    /// <param name="name">The allocation's diagnostic name.</param>
    /// <param name="background">The parsed background section.</param>
    /// <returns>The relocated background.</returns>
    public RelocatedBackground Relocate(string name, PbakBackground background) {
        ArgumentNullException.ThrowIfNull(background);

        var tileBase = AddTiles(name: $"{name}-tiles", tiles2bpp: background.Tiles2bpp);
        var paletteBase = AddBackgroundPalettes(name: $"{name}-palettes", paletteData: background.PaletteData);
        var map = RelocateMap(name: name, tileMap: background.TileMap, tileBase: tileBase, tileCount: background.TileCount);
        var attributes = ((background.AttributeMap is { } attributeMap) ? RelocateAttributes(name: name, attributes: attributeMap, paletteBase: paletteBase, paletteCount: background.PaletteCount) : null);

        return new RelocatedBackground(AttributeMap: attributes, PaletteBase: paletteBase, TileBase: tileBase, TileMap: map);
    }

    /// <summary>Links a parsed sprite set: allocates its tiles and object palette slots, relocates every frame's rows
    /// (tile ids rebased, OAM palette bits shifted), and lands the <c>&lt;name&gt;-frames</c> block plus the
    /// <c>&lt;name&gt;-frame-table</c> runtime index (and <c>&lt;name&gt;-animations</c> when present).</summary>
    /// <param name="name">The link name (also the landed blocks' name prefix).</param>
    /// <param name="sprites">The parsed sprite section.</param>
    /// <returns>The linked sprite set.</returns>
    public LinkedSpriteSet LinkSpriteSet(string name, PbakSpriteSet sprites) {
        ArgumentNullException.ThrowIfNull(sprites);

        var tileBase = AddTiles(name: $"{name}-tiles", tiles2bpp: sprites.Tiles2bpp);
        var paletteBase = AddObjectPalettes(name: $"{name}-palettes", paletteData: sprites.PaletteData);
        var offsets = new int[sprites.Frames.Count];
        var entryCounts = new int[sprites.Frames.Count];
        var rowBytes = new List<byte>();

        for (var frame = 0; (frame < sprites.Frames.Count); frame++) {
            offsets[frame] = rowBytes.Count;
            entryCounts[frame] = sprites.Frames[frame].EntryCount;
            AppendRelocatedRows(name: name, frame: frame, rows: sprites.Frames[frame].Rows, sprites: sprites, tileBase: tileBase, paletteBase: paletteBase, destination: rowBytes);
        }

        var frames = m_data.Add(name: $"{name}-frames", bytes: [.. rowBytes]);
        var addresses = new ushort[sprites.Frames.Count];
        var frameTable = new byte[(sprites.Frames.Count * 4)];

        for (var frame = 0; (frame < sprites.Frames.Count); frame++) {
            addresses[frame] = (ushort)(frames.Address + offsets[frame]);
            frameTable[((frame * 4) + 0)] = (byte)(addresses[frame] & 0xFF);
            frameTable[((frame * 4) + 1)] = (byte)(addresses[frame] >> 8);
            frameTable[((frame * 4) + 2)] = (byte)entryCounts[frame];
            frameTable[((frame * 4) + 3)] = 0x00;
        }

        return new LinkedSpriteSet(
            Animations: ((sprites.AnimationPayload is { } animation) ? m_data.Add(name: $"{name}-animations", bytes: animation) : (RomTable?)null),
            FrameAddresses: addresses,
            FrameEntryCounts: entryCounts,
            Frames: frames,
            FrameTable: m_data.Add(name: $"{name}-frame-table", bytes: frameTable),
            PaletteBase: paletteBase,
            PaletteCount: sprites.PaletteCount,
            TileBase: tileBase,
            TileCount: sprites.TileCount
        );
    }

    /// <summary>Seals the tile bank: concatenates every segment into the reserved <c>tile-bank</c> block (one boot
    /// copy to VRAM 0x8000). No further tile allocation is possible.</summary>
    /// <returns>The bank's table (its length is the boot's tile byte count).</returns>
    public RomTable SealTileBank() {
        SealGuard(sealedFlag: ref m_tileBankSealed, count: m_tileCount, table: "tile bank");

        return m_data.Add(name: "tile-bank", bytes: Concatenate(segments: m_tileSegments));
    }

    /// <summary>Seals the background palette table into the reserved <c>bg-palette-table</c> block (the boot spec's
    /// <c>BgPalettes</c>).</summary>
    /// <returns>The table.</returns>
    public RomTable SealBackgroundPalettes() {
        SealGuard(sealedFlag: ref m_backgroundPalettesSealed, count: m_backgroundPaletteCount, table: "background palette table");

        return m_data.Add(name: "bg-palette-table", bytes: Concatenate(segments: m_backgroundPaletteSegments));
    }

    /// <summary>Seals the object palette table into the reserved <c>obj-palette-table</c> block (the boot spec's
    /// <c>ObjPalettes</c>).</summary>
    /// <returns>The table.</returns>
    public RomTable SealObjectPalettes() {
        SealGuard(sealedFlag: ref m_objectPalettesSealed, count: m_objectPaletteCount, table: "object palette table");

        return m_data.Add(name: "obj-palette-table", bytes: Concatenate(segments: m_objectPaletteSegments));
    }

    // Map relocation: every cell rebases onto the bank (cells reference the section's tiles zero-based).
    private static byte[] RelocateMap(string name, byte[] tileMap, byte tileBase, int tileCount) {
        var map = new byte[tileMap.Length];

        for (var cell = 0; (cell < tileMap.Length); cell++) {
            if (tileMap[cell] >= tileCount) {
                throw new InvalidDataException(message: $"'{name}' map cell {cell} references tile {tileMap[cell]} of {tileCount}.");
            }

            map[cell] = (byte)(tileBase + tileMap[cell]);
        }

        return map;
    }

    // Attribute relocation: palette bits (0-2) shift to the granted slots; every other bit (flips, bank, priority)
    // rides along untouched.
    private static byte[] RelocateAttributes(string name, byte[] attributes, byte paletteBase, int paletteCount) {
        var relocated = new byte[attributes.Length];

        for (var cell = 0; (cell < attributes.Length); cell++) {
            var palette = attributes[cell] & 0x07;

            if (palette >= paletteCount) {
                throw new InvalidDataException(message: $"'{name}' attribute cell {cell} references palette {palette} of {paletteCount}.");
            }

            relocated[cell] = (byte)((paletteBase + palette) | (attributes[cell] & 0xF8));
        }

        return relocated;
    }

    // OAM row relocation: tile ids rebase onto the bank, OAM palette bits (0-2) shift to the granted slots, flip and
    // priority bits ride along.
    private static void AppendRelocatedRows(string name, int frame, byte[] rows, PbakSpriteSet sprites, byte tileBase, byte paletteBase, List<byte> destination) {
        for (var offset = 0; (offset < rows.Length); offset += 4) {
            var tile = rows[(offset + 2)];
            var palette = rows[(offset + 3)] & 0x07;

            if (tile >= sprites.TileCount) {
                throw new InvalidDataException(message: $"'{name}' frame {frame} references tile {tile} of {sprites.TileCount}.");
            }

            if (palette >= sprites.PaletteCount) {
                throw new InvalidDataException(message: $"'{name}' frame {frame} references palette {palette} of {sprites.PaletteCount}.");
            }

            destination.Add(item: rows[(offset + 0)]);
            destination.Add(item: rows[(offset + 1)]);
            destination.Add(item: (byte)(tileBase + tile));
            destination.Add(item: (byte)((paletteBase + palette) | (rows[(offset + 3)] & 0xF8)));
        }
    }
    private static void ThrowIfSealed(bool sealedFlag, string name, string table) {
        if (sealedFlag) {
            throw new InvalidOperationException(message: $"The {table} is sealed; '{name}' cannot be added.");
        }
    }
    private static void SealGuard(ref bool sealedFlag, int count, string table) {
        if (sealedFlag) {
            throw new InvalidOperationException(message: $"The {table} is already sealed.");
        }

        if (count == 0) {
            throw new InvalidOperationException(message: $"The {table} is empty — allocate at least one segment before sealing.");
        }

        sealedFlag = true;
    }
    private static byte ValidatePalettes(string name, byte[] paletteData, int currentCount, string plane) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(paletteData);

        if ((paletteData.Length == 0) || ((paletteData.Length % PaletteByteCount) != 0)) {
            throw new ArgumentException(message: $"The '{name}' palette segment must be a non-empty multiple of {PaletteByteCount} bytes.", paramName: nameof(paletteData));
        }

        var count = (paletteData.Length / PaletteByteCount);

        if ((currentCount + count) > PaletteSlotCapacity) {
            throw new InvalidOperationException(message: $"Adding '{name}' ({count} palettes) overruns the {PaletteSlotCapacity} {plane} palette slots ({currentCount} already allocated).");
        }

        return (byte)currentCount;
    }
    private static byte[] Concatenate(List<byte[]> segments) {
        var length = 0;

        foreach (var segment in segments) {
            length += segment.Length;
        }

        var bytes = new byte[length];
        var offset = 0;

        foreach (var segment in segments) {
            segment.CopyTo(array: bytes, index: offset);
            offset += segment.Length;
        }

        return bytes;
    }
}
