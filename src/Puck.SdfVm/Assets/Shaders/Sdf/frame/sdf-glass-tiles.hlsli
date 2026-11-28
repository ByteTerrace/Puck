// The glass tiles (SdfWorldPackage.GlassTiles): the 8 by 8 tiles of the render grid the glass raster wrote sums in this
// frame, the ones of them that show glass, and the sky filter of every pixel of a tile that does. The buffer's words:
// - [0, SDF_GLASS_TILE_HEADER_WORDS): the header, word 0 the tiles listed;
// - then `capacity` words for the marked mask, one bit a tile the raster wrote sums in, from the low bit of the first
//   word (the first ceil(capacity / 32) words hold it);
// - then `capacity` words for the shown mask, laid out alike: the listed tiles the shading pass found glass in;
// - then `capacity` words for the tile list, each marked tile once, in increasing tile order (the glass tiles pass
//   compacts the marked mask, passes/sdf-glass-tiles.comp.hlsl), so the shading pass's groups walk the screen in
//   raster order and the groups running together share what neighbouring pixels read;
// - then SDF_GLASS_TILE_PIXELS words a tile for the sky filter, tile-major, a pixel's word at its place in its tile
//   (frame/sdf-glass-sky-filter.hlsli).
// `capacity` comes from the bound buffer's length (sdfGlassTileCapacity, the twin of SdfWorldPackage.GlassTileCapacity),
// so the layout is the allocation's whatever extent records into it, a list a frame left reads the same next frame, and
// the tables' dummy, which a reader binds in a frame whose glass passes did not run, holds no tile.
// A tile is numbered row-major over the render grid's tiles at the recording's extent (sdfGlassTileOf); the glass sums
// lie tile by tile at the same numbers (frame/sdf-glass.hlsli), so a tile's sums are one contiguous run.
// Invariants the passes keep, each at one door:
// - every glass sums word outside the listed tiles is zero (the clear zeroes the listed tiles' sums before the raster;
//   the raster marks a tile before it accumulates into it, sdfGlassMarkTile; the tiles pass lists every marked tile, and
//   only the raster and the clear write the marked mask);
// - after the shading pass, a tile is shown only if the glass image and the sky filter hold every pixel of it (the
//   shading pass sets or clears the shown bit of every listed tile by its own group's answer and writes both for every
//   pixel of a tile it shows, passes/sdf-glass-stage.hlsli, and the clear leaves no tile shown), so a reader that finds a
//   tile shown reads the glass image there and the lit image views wrote everywhere else (sdfGlassTileShows).
#ifndef FRAME_SDF_GLASS_TILES_HLSLI
#define FRAME_SDF_GLASS_TILES_HLSLI
#include "../isa/sdf-render.hlsli"

#define SDF_GLASS_TILE_EDGE SDF_VISIBILITY_GROUP_EDGE
#define SDF_GLASS_TILE_PIXELS (SDF_GLASS_TILE_EDGE * SDF_GLASS_TILE_EDGE)

// The tiles a glass tiles buffer of `words` words lays out.
uint sdfGlassTileCapacity(uint words) {
    return ((words > SDF_GLASS_TILE_HEADER_WORDS) ? ((words - SDF_GLASS_TILE_HEADER_WORDS) / SDF_GLASS_TILE_WORDS) : 0u);
}
// The tiles across a render grid of `extent` pixels.
uint sdfGlassTilesAcross(uint2 extent) {
    return ((extent.x + (SDF_GLASS_TILE_EDGE - 1u)) / SDF_GLASS_TILE_EDGE);
}
// The number of the tile holding a render pixel.
uint sdfGlassTileOf(uint2 pixel, uint2 extent) {
    return (((pixel.y / SDF_GLASS_TILE_EDGE) * sdfGlassTilesAcross(extent)) + (pixel.x / SDF_GLASS_TILE_EDGE));
}
// A pixel's place in its tile, row-major.
uint sdfGlassTilePlace(uint2 pixel) {
    return (((pixel.y % SDF_GLASS_TILE_EDGE) * SDF_GLASS_TILE_EDGE) + (pixel.x % SDF_GLASS_TILE_EDGE));
}
// The render pixel at a place of a tile.
uint2 sdfGlassTilePixel(uint tile, uint place, uint2 extent) {
    uint across = sdfGlassTilesAcross(extent);

    return ((uint2((tile % across), (tile / across)) * SDF_GLASS_TILE_EDGE) + uint2((place % SDF_GLASS_TILE_EDGE), (place / SDF_GLASS_TILE_EDGE)));
}
// The words each mask uses for a buffer of `capacity` tiles.
uint sdfGlassTileMaskWords(uint capacity) {
    return ((capacity + 31u) / 32u);
}
// The word of each region holding a tile's bit or entry, for a buffer of `capacity` tiles.
uint sdfGlassTileMarkedWord(uint tile) {
    return (SDF_GLASS_TILE_HEADER_WORDS + (tile >> 5u));
}
uint sdfGlassTileShownWord(uint capacity, uint tile) {
    return ((SDF_GLASS_TILE_HEADER_WORDS + capacity) + (tile >> 5u));
}
uint sdfGlassTileListWord(uint capacity, uint entry) {
    return ((SDF_GLASS_TILE_HEADER_WORDS + (2u * capacity)) + entry);
}
uint sdfGlassTileFilterWord(uint capacity, uint tile, uint place) {
    return (((SDF_GLASS_TILE_HEADER_WORDS + (3u * capacity)) + (tile * SDF_GLASS_TILE_PIXELS)) + place);
}
// The words the clear zeroes, numbered from zero (SdfWorldPackage.GlassTileClearWords): the header, then the used words of
// the marked mask, then those of the shown mask; the buffer's word for each.
uint sdfGlassTileClearWords(uint capacity) {
    return (SDF_GLASS_TILE_HEADER_WORDS + (2u * sdfGlassTileMaskWords(capacity)));
}
uint sdfGlassTileClearWord(uint capacity, uint index) {
    uint marked = (SDF_GLASS_TILE_HEADER_WORDS + sdfGlassTileMaskWords(capacity));

    return ((index < marked) ? index : ((SDF_GLASS_TILE_HEADER_WORDS + capacity) + (index - marked)));
}

// Whether the glass tiles show the tile of a render pixel: the glass image and the sky filter hold that pixel. False for a
// pixel past the buffer's capacity, which the tables' dummy is for every pixel.
bool sdfGlassTileShows(StructuredBuffer<uint> tiles, uint2 pixel, uint2 extent) {
    uint words;
    uint stride;

    tiles.GetDimensions(words, stride);

    uint capacity = sdfGlassTileCapacity(words);
    uint tile = sdfGlassTileOf(pixel, extent);

    return ((tile < capacity) && (((tiles[sdfGlassTileShownWord(capacity, tile)] >> (tile & 31u)) & 1u) != 0u));
}
// The sky filter word of a render pixel whose tile the glass tiles show (sdfGlassTileShows), or zero for any other.
uint sdfGlassTileFilter(StructuredBuffer<uint> tiles, uint2 pixel, uint2 extent) {
    if (!sdfGlassTileShows(tiles, pixel, extent)) {
        return 0u;
    }

    uint words;
    uint stride;

    tiles.GetDimensions(words, stride);
    return tiles[sdfGlassTileFilterWord(sdfGlassTileCapacity(words), sdfGlassTileOf(pixel, extent), sdfGlassTilePlace(pixel))];
}
#endif
