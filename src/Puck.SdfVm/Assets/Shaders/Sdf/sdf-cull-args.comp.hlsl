// GPU-driven cull args: a single-WORKGROUP parallel reduction over the beam prepass's per-tile cull buffer. It computes
// the bounding box of SURVIVING (non-empty) tiles across all SDF viewports and writes (a) the Stage-1 "views" INDIRECT
// dispatch group counts and (b) the bbox group origin. The views dispatch then covers ONLY that bbox — the all-empty
// margins (e.g. the sky above the scene) are never dispatched — and the source-agnostic compositor flattens every
// remaining empty tile to a constant. Dispatched (1,1,1) AFTER the beam prepass (a compute->compute barrier orders the
// cull-buffer read); its args output feeds the indirect Stage-1 dispatch (a draw-indirect barrier) and its bounds
// output the Stage-1 kernel (a shader-read barrier). Generic: it operates only on the cull buffer, not on any scene.
//
// The reduction is a min/max/any over tile coordinates — order-independent (min/max are associative and commutative), so
// the workgroup-parallel form is BIT-IDENTICAL to a serial scan regardless of atomic contention order. One workgroup of
// SDF_CULL_ARGS_THREADS threads strides the flattened (viewport, tile) index space and folds each surviving tile into
// groupshared min/max via InterlockedMin/InterlockedMax; thread 0 emits the args after a group barrier.
#include "sdf-world.hlsli"

// register(t0) DELIBERATELY shadows sdf-vm.hlsli's sdfWords (and t1 shadows sdf-world.hlsli's viewports): this kernel
// reads NEITHER, DXC dead-code-eliminates both declarations, and `tiles` ends up the only SRV — which the Direct3D 12
// pipeline factory assigns t0 by binding order anyway. If you ever call map() or read viewports[] from here, the
// overlap becomes real: split CompositeParams/isChildViewport out of sdf-world.hlsli rather than renumbering this
// register, because the factory assigns registers POSITIONALLY and an annotation change alone desyncs the root signature.
[[vk::binding(3, 0)]] StructuredBuffer<float> tiles : register(t0);       // read-only beam cull buffer
[[vk::binding(5, 0)]] RWStructuredBuffer<uint> viewsArgs : register(u0);  // [groupCountX, groupCountY, groupCountZ]
[[vk::binding(6, 0)]] RWStructuredBuffer<uint> cullBounds : register(u1); // [minGroupX, minGroupY]

#define SDF_CULL_ARGS_THREADS 256u

// The surviving-tile bounding box, folded across the workgroup. minX/minY seed to the max sentinel (an all-empty frame
// leaves them untouched, detected below); maxX/maxY seed to 0. A tile survives => all four atomics fire, so
// minTileX == sentinel iff no tile survived (tileGrid dimensions never reach the sentinel).
groupshared uint minTileX;
groupshared uint minTileY;
groupshared uint maxTileX;
groupshared uint maxTileY;

[numthreads(SDF_CULL_ARGS_THREADS, 1, 1)]
void CSMain(uint threadIndex : SV_GroupIndex) {
    if (0u == threadIndex) {
        minTileX = 0xFFFFFFFFu;
        minTileY = 0xFFFFFFFFu;
        maxTileX = 0u;
        maxTileY = 0u;
    }

    GroupMemoryBarrierWithGroupSync();

    // Every (viewport, tile) cull entry, flattened: entry = ((v * tilesPerView) + (ty * tileGrid.x) + tx). The strided
    // walk visits the SAME entry set as a serial triple loop; a child viewport's tiles are skipped identically (the beam
    // never culled them). The grid is small (≈80x50 per viewport at 1280x800) and this runs once per frame.
    uint tilesPerView = (params.tileGrid.x * params.tileGrid.y);
    uint total = (params.viewportCount * tilesPerView);

    for (uint entry = threadIndex; (entry < total); entry += SDF_CULL_ARGS_THREADS) {
        uint v = (entry / tilesPerView);

        if (isChildViewport(v)) {
            continue; // a child viewport's slot is a hosted surface, not an SDF camera — the beam never culled it.
        }

        uint rem = (entry - (v * tilesPerView));
        uint ty = (rem / params.tileGrid.x);
        uint tx = (rem - (ty * params.tileGrid.x));

        // Surviving tiles hold a non-negative march-start; empty tiles hold TileEmpty (-1.0).
        if (tiles[worldTileIndex(v, uint2(tx, ty), params.tileGrid)] >= 0.0) {
            InterlockedMin(minTileX, tx);
            InterlockedMin(minTileY, ty);
            InterlockedMax(maxTileX, tx);
            InterlockedMax(maxTileY, ty);
        }
    }

    GroupMemoryBarrierWithGroupSync();

    if (0u != threadIndex) {
        return;
    }

    uint boxMinX = minTileX;
    uint boxMinY = minTileY;
    uint boxMaxX = maxTileX;
    uint boxMaxY = maxTileY;

    if (0xFFFFFFFFu == boxMinX) {
        // No surviving tiles (every ray clears the field): dispatch one degenerate tile; the compositor flattens all.
        boxMinX = 0u;
        boxMinY = 0u;
        boxMaxX = 0u;
        boxMaxY = 0u;
    }

    // A tile is WorldTileSize (16) px = (WorldTileSize / 8) groups of the views kernel's 8x8 workgroup. The dispatch
    // is origin-anchored (0,0); the views kernel adds cullBounds as its pixel-group origin to land on the bbox.
    uint groupsPerTile = (WorldTileSize / 8u);

    cullBounds[0] = (boxMinX * groupsPerTile);
    cullBounds[1] = (boxMinY * groupsPerTile);
    viewsArgs[0] = (((boxMaxX - boxMinX) + 1u) * groupsPerTile);
    viewsArgs[1] = (((boxMaxY - boxMinY) + 1u) * groupsPerTile);
    viewsArgs[2] = params.viewportCount;
}
