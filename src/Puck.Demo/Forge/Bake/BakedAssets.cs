namespace Puck.Demo.Forge.Bake;

/// <summary>A baked VRAM tile set: the deduplicated 8×8 tiles' concatenated 2bpp bytes (16 per tile).</summary>
/// <param name="TileData">The tiles' 2bpp bytes, 16 per tile, in first-seen order.</param>
internal sealed record BakedTileSet(byte[] TileData) {
    /// <summary>How many tiles the set holds.</summary>
    public int Count => (TileData.Length / 16);
}

/// <summary>A baked CGB palette table in palette-RAM wire form.</summary>
/// <param name="Rgb555Data">The palettes' bytes: <see cref="Count"/> × 4 colours × 2 bytes (little-endian RGB555,
/// ROUNDED from 8-bit — the bake's own conversion, distinct from the legacy truncating encoder).</param>
/// <param name="Count">How many 4-colour palettes the table holds.</param>
internal sealed record BakedPaletteSet(byte[] Rgb555Data, int Count);

/// <summary>The DMG shade-ramp registers a monochrome bake pairs with its tiles.</summary>
/// <param name="Bgp">The background palette register (the identity ramp, <c>0xE4</c>).</param>
/// <param name="Obp0">Object palette 0 (colour 0 transparent — 3 usable shades).</param>
/// <param name="Obp1">Object palette 1.</param>
internal sealed record DmgRegisters(byte Bgp, byte Obp0, byte Obp1);

/// <summary>A baked background layer: tiles, the 32×32 tilemap (top-left anchored, 20×18 visible), the palette
/// table, and — on CGB — the parallel 32×32 attribute map (palette bits 0-2, flip bits 5/6).</summary>
/// <param name="Tiles">The deduplicated background tiles.</param>
/// <param name="TileMap">The 32×32 tilemap (1024 bytes of tile ids).</param>
/// <param name="AttributeMap">The 32×32 CGB attribute map, or null on DMG (no attribute plane).</param>
/// <param name="Palettes">The background palette table.</param>
/// <param name="Registers">The DMG shade registers, or null on CGB.</param>
internal sealed record BakedBackground(
    BakedTileSet Tiles,
    byte[] TileMap,
    byte[]? AttributeMap,
    BakedPaletteSet Palettes,
    DmgRegisters? Registers
);

/// <summary>One hardware OAM entry of a metasprite frame, ANCHOR-RELATIVE: the offsets place the 8×8 tile's
/// top-left corner relative to the frame's anchor (the native cell's centre), so a ROM positions the whole frame by
/// adding one on-screen anchor point.</summary>
/// <param name="OffsetY">The tile top edge relative to the anchor.</param>
/// <param name="OffsetX">The tile left edge relative to the anchor.</param>
/// <param name="TileId">The tile id in the sprite set's shared tile data.</param>
/// <param name="Attributes">The OAM attribute byte (CGB palette bits 0-2, X/Y flip bits 5/6).</param>
internal readonly record struct OamEntry(sbyte OffsetY, sbyte OffsetX, byte TileId, byte Attributes);

/// <summary>One metasprite frame: a named pose's OAM entries (fully-transparent tiles already skipped).</summary>
/// <param name="Name">The pose name (the source view's name).</param>
/// <param name="Entries">The frame's OAM entries.</param>
internal sealed record MetaspriteFrame(string Name, IReadOnlyList<OamEntry> Entries);

/// <summary>A baked sprite subject: one shared tile set + palette set (fitted JOINTLY across every frame so
/// animation never flickers palettes) and the per-pose metasprite frames.</summary>
/// <param name="Tiles">The deduplicated sprite tiles, shared by every frame.</param>
/// <param name="Frames">The metasprite frames, in plan view order.</param>
/// <param name="Palettes">The object palette table (slot 0 of each palette is transparent).</param>
/// <param name="Registers">The DMG shade registers, or null on CGB.</param>
internal sealed record BakedSpriteSet(
    BakedTileSet Tiles,
    IReadOnlyList<MetaspriteFrame> Frames,
    BakedPaletteSet Palettes,
    DmgRegisters? Registers
);

/// <summary>Everything one bake produced, ready for a cartridge assembler (or the preview).</summary>
/// <param name="StyleName">The resolved style's name.</param>
/// <param name="Target">The hardware generation the assets fit.</param>
/// <param name="Background">The background layer, when the plan's intent was <see cref="BakeIntent.Background"/>.</param>
/// <param name="Sprites">The sprite sets, when the plan's intent was <see cref="BakeIntent.Sprite"/>.</param>
internal sealed record BakedAssetBundle(
    string StyleName,
    BakeTarget Target,
    BakedBackground? Background,
    IReadOnlyList<BakedSpriteSet> Sprites
);
