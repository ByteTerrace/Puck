// The view a dispatch renders, its visibility records and mesh samples, and its tile planes and instance masks.
#ifndef FRAME_SDF_FRAME_HLSLI
#define FRAME_SDF_FRAME_HLSLI
// The world values (imageExtent, tileGrid, viewportCount, screenCount, instanceMaskWordCount and the view the set
// renders, viewBase) are the views set's block, passGroup, written per view by SdfFrameBlock:
// imageExtent is the engine extent, the largest a view renders and the per-view visibility record stride; tileGrid the
// tiles per viewport, the cull buffer's per-viewport stride; viewportCount every view the frame renders; screenCount one
// past the highest screen whose source is bound (the views passes only); instanceMaskWordCount the live program's per-tile mask width;
// meshDraws the frame's mesh draws, zero when no mesh draws
// and the mesh visibility target holds nothing this frame. Each view renders through its own set of dispatches, one
// deep in Z, so a kernel's view is worldViewOf(id.z).

// The view a dispatch-set invocation renders.
uint worldViewOf(uint z) {
    return (passGroup.viewBase + z);
}

#if defined(SDF_PRIMARY_PASS) || defined(SDF_PRIMARY_READ)
// The per-pixel visibility record the hit passes write and views shades (sdf-visibility.hlsli owns its layout).
#include "sdf-visibility.hlsli"
uint worldVisibilityRecord(uint2 pixel, uint viewIndex) {
    return sdfVisibilityRecord(pixel, viewIndex, passGroup.imageExtent);
}

// What the mesh pass wrote for a pixel of the view the set renders: nothing when no mesh covers it.
struct SdfMeshSample {
    bool covered;
    float t;
    uint draw;
    uint triangleIndex;
};

// Reads the mesh visibility target at a pixel of the view the set renders, whose mesh pass ran just before its hit
// passes. A texel whose second channel is zero is one no mesh covers, and a frame with no mesh draws reads nothing, since
// its target holds an earlier frame's draws.
SdfMeshSample sdfMeshSampleAt(uint2 pixel) {
    SdfMeshSample hit = (SdfMeshSample)0;

    if (passGroup.meshDraws != 0u) {
        float4 texel = meshVisibility.Load(int3(int2(pixel), 0));

        if (texel.y >= 1.0) {
            hit.covered = true;
            hit.t = texel.x;
            hit.draw = ((uint)texel.y - 1u);
            hit.triangleIndex = (uint)texel.z;
        }
    }

    return hit;
}
#endif
#ifdef SDF_PRIMARY_READ
// Whether `pixel`'s visibility record belongs to this frame. The hit passes share one indirect dispatch box, and primary
// writes a record for every active pixel inside it, misses included, so a record is current exactly inside the box.
// Outside it a record is whatever an earlier frame left, and every tile there is one the beam proved empty this frame.
// The rule is SdfVisibility.IsCurrent's, generated as SDF_VISIBILITY_CURRENT, which a pick applies to the box it reads back.
bool worldVisibilityCurrent(uint2 pixel) {
    return SDF_VISIBILITY_CURRENT(pixel, cullBounds);
}
#endif

uint worldInstanceMaskBase(uint tileIndex) {
    uint summaryWords = ((passGroup.instanceMaskWordCount + 31u) >> 5u);

    return ((passGroup.instanceMaskWordCount + summaryWords) * tileIndex);
}
// The tile cull buffer's plane layout (four-bound teleport, Larsson "The Gunk", + the F1 far bound). Plane 0 = the
// march-start lower bound (the classic beam output; sdf-cull-args + the compositor read ONLY this plane, so their
// worldTileIndex stride is unchanged). Planes 1/2 = the proven-empty gap [firstExit, secondEntry] a tile's cone
// cleared between two occupied bands: sdf-beam writes them, sdf-world-views teleports across them. Plane 3 = the F1
// FAR BOUND: the depth beyond which the tile's cone provably cannot produce ANY footprint-accepted hit through the far
// distance (sdf-beam writes it, sdf-world-views exits the fine march at traveled >= farBound). Each plane is one
// entry per (viewport, tile) THIS frame — the same span worldTileIndex covers — so plane k of tile T sits at
// (k * stride + tileIndex). KEEP IN SYNC with SdfWorldPackage.TilePlaneCount.
static const uint WorldTilePlaneCount = 4u;
uint worldTilePlaneStride() {
    return (passGroup.tileGrid.x * passGroup.tileGrid.y * passGroup.viewportCount);
}
// Plane 0 (march-start) needs no stride multiply — this accessor exists only for symmetry with the three below (see
// the layout comment above: sdf-cull-args and the compositor deliberately read plane 0 directly, unaffected by any
// plane-count change, so they do not call it).
uint worldTileMarchStartIndex(uint tileIndex) {
    return tileIndex;
}
uint worldTileFirstExitIndex(uint tileIndex) {
    return (worldTilePlaneStride() + tileIndex);
}
uint worldTileSecondEntryIndex(uint tileIndex) {
    return ((2u * worldTilePlaneStride()) + tileIndex);
}
uint worldTileFarBoundIndex(uint tileIndex) {
    return (((WorldTilePlaneCount - 1u) * worldTilePlaneStride()) + tileIndex);
}

#endif
