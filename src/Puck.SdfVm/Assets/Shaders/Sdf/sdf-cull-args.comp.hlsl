// GPU-driven cull args: a single-thread reduction over the beam prepass's per-tile cull buffer. It computes the
// bounding box of SURVIVING (non-empty) tiles across all SDF viewports and writes (a) the Stage-1 "views" INDIRECT
// dispatch group counts and (b) the bbox group origin. The views dispatch then covers ONLY that bbox — the all-empty
// margins (e.g. the sky above the scene) are never dispatched — and the source-agnostic compositor flattens every
// remaining empty tile to a constant. Dispatched (1,1,1) AFTER the beam prepass (a compute->compute barrier orders
// the cull-buffer read); its args output feeds the indirect Stage-1 dispatch (a draw-indirect barrier) and its bounds
// output the Stage-1 kernel (a shader-read barrier). Generic: it operates only on the cull buffer, not on any scene.
#include "sdf-world.hlsli"

[[vk::binding(3, 0)]] StructuredBuffer<float> tiles : register(t0);       // read-only beam cull buffer
[[vk::binding(5, 0)]] RWStructuredBuffer<uint> viewsArgs : register(u0);  // [groupCountX, groupCountY, groupCountZ]
[[vk::binding(6, 0)]] RWStructuredBuffer<uint> cullBounds : register(u1); // [minGroupX, minGroupY]

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint minTileX = 0xFFFFFFFFu;
    uint minTileY = 0xFFFFFFFFu;
    uint maxTileX = 0u;
    uint maxTileY = 0u;
    bool any = false;

    // One serial pass over every (viewport, tile) cull entry. The grid is small (≈60x38 per viewport at 960x600) and
    // this runs once per frame, so a single-thread scan is negligible next to the per-pixel SDF march it gates.
    for (uint v = 0u; (v < params.viewportCount); v++) {
        if (isChildViewport(v)) {
            continue; // a child viewport's slot is a hosted surface, not an SDF camera — the beam never culled it.
        }

        for (uint ty = 0u; (ty < params.tileGrid.y); ty++) {
            for (uint tx = 0u; (tx < params.tileGrid.x); tx++) {
                // Surviving tiles hold a non-negative march-start; empty tiles hold TileEmpty (-1.0).
                if (tiles[worldTileIndex(v, uint2(tx, ty), params.tileGrid)] >= 0.0) {
                    any = true;
                    minTileX = min(minTileX, tx);
                    minTileY = min(minTileY, ty);
                    maxTileX = max(maxTileX, tx);
                    maxTileY = max(maxTileY, ty);
                }
            }
        }
    }

    if (!any) {
        // No surviving tiles (every ray clears the field): dispatch one degenerate tile; the compositor flattens all.
        minTileX = 0u;
        minTileY = 0u;
        maxTileX = 0u;
        maxTileY = 0u;
    }

    // A tile is WorldTileSize (16) px = (WorldTileSize / 8) groups of the views kernel's 8x8 workgroup. The dispatch
    // is origin-anchored (0,0); the views kernel adds cullBounds as its pixel-group origin to land on the bbox.
    uint groupsPerTile = (WorldTileSize / 8u);

    cullBounds[0] = (minTileX * groupsPerTile);
    cullBounds[1] = (minTileY * groupsPerTile);
    viewsArgs[0] = (((maxTileX - minTileX) + 1u) * groupsPerTile);
    viewsArgs[1] = (((maxTileY - minTileY) + 1u) * groupsPerTile);
    viewsArgs[2] = params.viewportCount;
}
