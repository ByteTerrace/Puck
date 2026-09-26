// The per-tile cull grid's shared vocabulary: the tile size, the "no ray hits" sentinel, and the (viewport, tile) ->
// flat index mapping. Deliberately BINDING-FREE and dependency-free — it declares no buffers, no push constants, and
// includes nothing — so a kernel with a push-constant layout of its own can share one definition instead of keeping a
// hand-synced copy.
//
// The cull buffer these index is written by sdf-beam.comp, reduced by sdf-cull-args.comp, and read by
// sdf-world-views.comp as a march-start.
#ifndef SDF_TILE_HLSLI
#define SDF_TILE_HLSLI

// One cull tile's edge in pixels. The views kernel dispatches an 8x8 workgroup, so a tile is exactly
// (WorldTileSize / 8)^2 groups — sdf-cull-args.comp relies on that divisibility.
static const uint WorldTileSize = 16u;
// The sentinel a tile carries when the beam prepass's cone provably clears the field: no ray in the tile can hit, so
// Stage 1 skips the tile's pixels entirely, leaving the sky pre-pass's pixels. Every other value the beam writes
// is a march-start t >= ConeNear > 0, so `== TileEmpty` is an exact test rather than a tolerance.
static const float TileEmpty = -1.0;

// The cull buffer is one flat array of (viewportCount * tileGrid.y * tileGrid.x) floats, viewport-major.
uint worldTileIndex(uint viewportIndex, uint2 tileCoord, uint2 tileGrid) {
    return ((viewportIndex * (tileGrid.x * tileGrid.y)) + (tileCoord.y * tileGrid.x) + tileCoord.x);
}

#endif
