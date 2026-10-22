// The view a dispatch renders, its visibility records and mesh samples, and the bake's instance masks.
#ifndef FRAME_SDF_FRAME_HLSLI
#define FRAME_SDF_FRAME_HLSLI
// The world values (imageExtent, viewportCount, screenCount, instanceMaskWordCount and the view the set renders,
// viewBase) are the views set's block, passGroup, written per view by SdfFrameBlock: imageExtent is the engine extent,
// the largest a view renders and the per-view visibility record stride; viewportCount every view the frame renders;
// screenCount one past the highest screen whose source is bound (views only); instanceMaskWordCount the live program's
// mask width; meshDraws the frame's mesh draws, zero when no mesh draws and the mesh visibility target holds nothing this
// frame. Each view renders through its own set of dispatches, one deep in Z, so a kernel's view is worldViewOf(id.z).

// The view a dispatch-set invocation renders.
uint worldViewOf(uint z) {
    return (passGroup.viewBase + z);
}

// The per-pixel visibility record the surface pass writes and views shades (sdf-visibility.hlsli owns its layout). The
// surface pass writes a record for every pixel of the render extent, misses included, so every record inside it is
// current.
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

// Reads the mesh visibility target at a pixel of the view the set renders, whose mesh pass ran just before its surface
// pass. A texel whose second channel is zero is one no mesh covers, and a frame with no mesh draws reads nothing, since
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
