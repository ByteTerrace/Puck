// GPU-driven cull args: a single-WORKGROUP parallel reduction over the beam prepass's per-tile cull buffer. It computes
// the bounding box of SURVIVING (non-empty) tiles of the one view its dispatch set renders and writes (a) the Stage-1 "views" INDIRECT
// dispatch group counts and (b) the bbox group origin. The views dispatch then covers ONLY that bbox — the all-empty
// margins (e.g. the sky above the scene) are never dispatched, and every pixel outside the box reads as uncovered to
// the sky and composite passes. Mesh draws and the probe debug view cover the whole grid instead: meshes and probe
// spheres can occupy tiles the beam proved empty. Dispatched (1,1,1) AFTER the beam prepass (a compute->compute barrier orders the
// cull-buffer read); its args output feeds the indirect Stage-1 dispatch (a draw-indirect barrier) and its bounds
// output the Stage-1 kernel (a shader-read barrier). Generic: it operates only on the cull buffer, not on any scene.
//
// The reduction is a min/max/any over tile coordinates — order-independent (min/max are associative and commutative), so
// the workgroup-parallel form is BIT-IDENTICAL to a serial scan regardless of atomic contention order. One workgroup of
// SDF_CULL_ARGS_THREADS threads strides the flattened (viewport, tile) index space and folds each surviving tile into
// groupshared min/max via InterlockedMin/InterlockedMax; thread 0 emits the args after a group barrier.
#include "sdf-world.hlsli"

// It reads the beam's cull buffer through tiles and writes the three indirect group counts through viewsArgsRW and the
// dispatch box (minGroupX, minGroupY, endGroupX, endGroupY) through cullBoundsRW.

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

    // Every tile cull entry of the view this dispatch set renders, flattened: entry = ((ty * tileGrid.x) + tx). The
    // strided walk visits the SAME entry set as a serial double loop, and runs once per view per frame.
    uint v = worldViewOf(0u);
    uint total = (passGroup.tileGrid.x * passGroup.tileGrid.y);

    for (uint entry = threadIndex; (entry < total); entry += SDF_CULL_ARGS_THREADS) {
        uint ty = (entry / passGroup.tileGrid.x);
        uint tx = (entry - (ty * passGroup.tileGrid.x));

        // Surviving tiles hold a non-negative march-start; empty tiles hold TileEmpty (-1.0).
        if (tiles[worldTileIndex(v, uint2(tx, ty), passGroup.tileGrid)] >= 0.0) {
            InterlockedMin(minTileX, tx);
            InterlockedMin(minTileY, ty);
            InterlockedMax(maxTileX, tx);
            InterlockedMax(maxTileY, ty);
        }
    }

    GroupMemoryBarrierWithGroupSync();

    // The reduction walks no field and writes no texel, so its row stays zero.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);

    if (0u != threadIndex) {
        return;
    }

    // Empty beam tiles retain TileEmpty, so primary skips their field march while the debug view can draw probes there.
    uint4 box = sdfViewDispatchBox(uint4(minTileX, minTileY, maxTileX, maxTileY), passGroup.tileGrid,
        passGroup.meshDraws, (int)passGroup.debugMode);
    uint boxMinX = box.x;
    uint boxMinY = box.y;
    uint boxMaxX = box.z;
    uint boxMaxY = box.w;

    // A tile is WorldTileSize px, (WorldTileSize / SDF_VISIBILITY_BOX_EDGE) groups of the hit passes' workgroup on each
    // axis. The dispatch is origin-anchored (0,0); the hit passes add cullBounds as their pixel-group origin to land on
    // the bbox. The box's exclusive end is the extent the hit passes wrote this frame, which is where a visibility record
    // is current (SDF_VISIBILITY_CURRENT).
    uint groupsPerTile = (WorldTileSize / SDF_VISIBILITY_BOX_EDGE);

    cullBoundsRW[0] = (boxMinX * groupsPerTile);
    cullBoundsRW[1] = (boxMinY * groupsPerTile);
    cullBoundsRW[2] = ((boxMaxX + 1u) * groupsPerTile);
    cullBoundsRW[3] = ((boxMaxY + 1u) * groupsPerTile);
    viewsArgsRW[0] = (((boxMaxX - boxMinX) + 1u) * groupsPerTile);
    viewsArgsRW[1] = (((boxMaxY - boxMinY) + 1u) * groupsPerTile);
    viewsArgsRW[2] = 1u;
}
