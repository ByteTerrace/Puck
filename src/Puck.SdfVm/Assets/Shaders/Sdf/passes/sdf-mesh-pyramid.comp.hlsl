// The pyramid pass: the view's depth pyramids from the mesh visibility target, one invocation a texel of level one.
//
// The occlusion's pyramid (frame/sdf-mesh-pyramid.hlsli): each invocation takes its texel's representative pixel and
// writes it at level one and up through every level whose block takes its representative from the texel below, so one
// dispatch writes every level with no level reading another.
//
// The reflection pass's pyramid (frame/sdf-reflection-depth.hlsli), in a fragment that reflects: each invocation takes
// the nearest of its texel's four pixels, the group reduces its texels to levels two to four in its own memory, and each
// group gathers its level-four texel into the levels above with atomics, over a range the recorder clears to zero
// before the dispatch (SdfWorldPackage.ReflectionDepthWordsBelow). A texel's value is a positive float or zero, whose
// bits order as the floats do, so an atomic maximum of the bits is the maximum of the values.
#define SDF_DYNAMIC_TRANSFORMS
#include "sdf-world.hlsli"
#include "../frame/sdf-mesh-pyramid.hlsli"
#include "../frame/sdf-reflection-depth.hlsli"

groupshared float sdfReflectDepthShared[64];

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 lane : SV_GroupThreadID, uint3 group : SV_GroupID) {
    uint2 dims = worldViewDims(worldView());
    uint words;
    uint stride;

    sdfMeshPyramidRW.GetDimensions(words, stride);

    uint levels = sdfMeshPyramidLevelsWithin(dims, words);
    // The occlusion's pattern step (sdfMeshPattern), which turns each level's choice of child.
    uint step = (passGroup.historyFrames & 7u);
    bool inside = all(id.xy < sdfMeshPyramidExtent(dims, 1u));

    if ((levels > 0u) && inside) {
        uint2 pixel = sdfMeshPyramidChild(id.xy, dims, 1u, step);
        float4 texel = meshVisibility.Load(int3(int2(pixel), 0));
        bool covered = ((passGroup.meshDraws != 0u) && (texel.y >= 1.0));
        uint t = (covered ? asuint(texel.x) : 0u);
        uint at = (pixel.x | (pixel.y << 16u));
        uint2 block = id.xy;

        [loop]
        for (uint level = 1u; (level <= levels); level++) {
            uint word = sdfMeshPyramidWord(dims, level, block);

            sdfMeshPyramidRW[word] = t;
            sdfMeshPyramidRW[word + 1u] = at;
            sdfWorkTexels++;

            uint2 parent = (block >> 1u);

            if ((level == levels) || any(sdfMeshPyramidChild(parent, sdfMeshPyramidExtent(dims, level), (level + 1u), step) != block)) {
                break;
            }

            block = parent;
        }
    }

    // The reflection pyramid, held only by a fragment that reflects: a condition of the dispatch alone, so every lane of
    // a group reaches the barriers below.
    uint depthWords;

    sdfReflectionDepthRW.GetDimensions(depthWords, stride);
    if (sdfReflectDepthHeld(dims, depthWords)) {
        float nearest = 0.0;

        if (inside) {
            uint2 first = (id.xy << 1u);
            uint2 last = min((first + 1u), (dims - 1u));

            nearest = max(max(sdfReflectDepthPixel(first), sdfReflectDepthPixel(uint2(last.x, first.y))),
                max(sdfReflectDepthPixel(uint2(first.x, last.y)), sdfReflectDepthPixel(last)));
            sdfReflectionDepthRW[sdfReflectDepthWord(0u, dims, 1u, id.xy)] = asuint(nearest);
            sdfWorkTexels++;
        }
        sdfReflectDepthShared[(lane.y * 8u) + lane.x] = nearest;

        // Levels two to four: at each, the lanes on the level's grid take the nearest of their block's four texels of the
        // level below, which the lanes beside them hold.
        [unroll]
        for (uint level = 2u; (level <= SdfReflectDepthGroupLevel); level++) {
            uint span = (1u << (level - 2u));
            bool leads = all((lane.xy & ((span << 1u) - 1u)) == 0u);

            GroupMemoryBarrierWithGroupSync();
            if (leads) {
                uint at = ((lane.y * 8u) + lane.x);

                nearest = max(max(nearest, sdfReflectDepthShared[at + span]), max(sdfReflectDepthShared[at + (span * 8u)], sdfReflectDepthShared[at + (span * 9u)]));
            }
            GroupMemoryBarrierWithGroupSync();
            if (leads) {
                uint2 texel = (id.xy >> (level - 1u));

                sdfReflectDepthShared[(lane.y * 8u) + lane.x] = nearest;
                if (all(texel < sdfReflectDepthExtent(dims, level))) {
                    sdfReflectionDepthRW[sdfReflectDepthWord(sdfReflectDepthBase(dims, level), dims, level, texel)] = asuint(nearest);
                    sdfWorkTexels++;
                }
            }
        }

        // The levels above: the group's one texel of level four joins each texel it lies in.
        if (all(lane.xy == 0u) && (nearest > 0.0)) {
            [loop]
            for (uint level = (SdfReflectDepthGroupLevel + 1u); (level <= SDF_REFLECTION_DEPTH_LEVELS); level++) {
                uint2 texel = (group.xy >> (level - SdfReflectDepthGroupLevel));
                uint previous;

                InterlockedMax(sdfReflectionDepthRW[sdfReflectDepthWord(sdfReflectDepthBase(dims, level), dims, level, texel)], asuint(nearest), previous);
            }
        }
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
