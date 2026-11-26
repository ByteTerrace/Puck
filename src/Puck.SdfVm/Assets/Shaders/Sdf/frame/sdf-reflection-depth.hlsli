// The reflection pass's depth pyramid (SdfWorldPackage.ReflectionDepth), which the pyramid pass writes from the mesh
// target in a fragment that reflects (passes/sdf-mesh-pyramid.comp.hlsl) and the reflection pass marches its mirror rays
// through (passes/sdf-world-views-reflections.comp.hlsl): levels one to SDF_REFLECTION_DEPTH_LEVELS of the render extent,
// each texel of level k standing for its 2^k block of render pixels. A texel holds the nearest covered surface of its
// block as the inverse of its forward depth (asfloat), the largest over the block, and zero where no mesh covers any of
// its pixels; inverse depth is linear along a ray's projection, and a ray whose inverse depth stays above a texel's
// across the texel passes in front of every surface the block shows. Levels lie one after another from level one, each
// row-major over its extent, the view's extent halved per level and rounded up. KEEP IN SYNC with
// SdfWorldPackage.ReflectionDepthWordsBelow, by which the recorder clears the levels the pyramid pass gathers with atomics.
#ifndef FRAME_SDF_REFLECTION_DEPTH_HLSLI
#define FRAME_SDF_REFLECTION_DEPTH_HLSLI
#include "../field/sdf-component.hlsli"
#include "sdf-mesh-pyramid.hlsli"

#if SDF_REFLECTION_DEPTH_LEVELS > 8
#error A march reads a level's base from two four-lane vectors (sdfReflectDepthBases).
#endif
#if SDF_REFLECTION_DEPTH_GROUP_LEVEL != 4
#error A group of the pyramid pass, 8x8 texels of level one, reduces to one texel of level four.
#endif

// The level a group of the pyramid pass reduces in its own memory; the pass gathers the levels above it from the groups'
// texels with atomics.
static const uint SdfReflectDepthGroupLevel = SDF_REFLECTION_DEPTH_GROUP_LEVEL;

// The texels of one level, whose extent is the view's halved per level and rounded up, as the occlusion's pyramid's
// levels are (sdfMeshPyramidExtent).
uint sdfReflectDepthTexels(uint2 dims, uint level) {
    uint2 extent = sdfMeshPyramidExtent(dims, level);

    return (extent.x * extent.y);
}
// The words of the levels below `level`, from level one: the first word of `level`.
uint sdfReflectDepthBase(uint2 dims, uint level) {
    uint base = 0u;

    [loop]
    for (uint below = 1u; (below < level); below++) {
        base += sdfReflectDepthTexels(dims, below);
    }
    return base;
}
// Whether a buffer of `words` words holds every level: the pyramid pass of a fragment that does not reflect binds a
// dummy, and writes no level.
bool sdfReflectDepthHeld(uint2 dims, uint words) {
    return (sdfReflectDepthBase(dims, (SDF_REFLECTION_DEPTH_LEVELS + 1u)) <= words);
}
// The bases of levels one to eight, in two vectors, from which a march reads a level's base at each step
// (sdfReflectDepthBaseOf) rather than summing the levels below it.
struct SdfReflectDepthBases {
    uint4 low;
    uint4 high;
};
SdfReflectDepthBases sdfReflectDepthBases(uint2 dims) {
    SdfReflectDepthBases bases;

    bases.low.x = 0u;
    bases.low.y = (bases.low.x + sdfReflectDepthTexels(dims, 1u));
    bases.low.z = (bases.low.y + sdfReflectDepthTexels(dims, 2u));
    bases.low.w = (bases.low.z + sdfReflectDepthTexels(dims, 3u));
    bases.high.x = (bases.low.w + sdfReflectDepthTexels(dims, 4u));
    bases.high.y = (bases.high.x + sdfReflectDepthTexels(dims, 5u));
    bases.high.z = (bases.high.y + sdfReflectDepthTexels(dims, 6u));
    bases.high.w = (bases.high.z + sdfReflectDepthTexels(dims, 7u));
    return bases;
}
uint sdfReflectDepthBaseOf(SdfReflectDepthBases bases, uint level) {
    return sdfComponent(((level <= 4u) ? bases.low : bases.high), ((level - 1u) & 3u));
}
// The word of a level's texel, from the level's base.
uint sdfReflectDepthWord(uint base, uint2 dims, uint level, uint2 texel) {
    return (base + ((texel.y * sdfMeshPyramidExtent(dims, level).x) + texel.x));
}
// Level zero, which no buffer holds: a render pixel's own surface as the mesh target holds it, its inverse forward depth,
// or zero where no mesh covers it. The includer reads the world interface's mesh target.
float sdfReflectDepthPixel(uint2 pixel) {
    float4 texel = meshVisibility.Load(int3(int2(pixel), 0));

    return (((passGroup.meshDraws != 0u) && (texel.y >= 1.0) && (texel.w > 0.0)) ? (1.0 / texel.w) : 0.0);
}

#endif
