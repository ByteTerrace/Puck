// The pyramid pass: the mesh depth pyramid (frame/sdf-mesh-pyramid.hlsli) from the mesh visibility target, one invocation a
// texel of level one. Each takes its texel's representative pixel and writes it at level one and up through every level
// whose block takes its representative from the texel below, so one dispatch writes every level with no level reading
// another.
#define SDF_DYNAMIC_TRANSFORMS
#include "sdf-world.hlsli"
#include "../frame/sdf-mesh-pyramid.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 dims = worldViewDims(worldView());
    uint words;
    uint stride;

    sdfMeshPyramidRW.GetDimensions(words, stride);

    uint levels = sdfMeshPyramidLevelsWithin(dims, words);
    // The occlusion's pattern step (sdfMeshPattern), which turns each level's choice of child.
    uint step = (passGroup.historyFrames & 7u);

    if ((levels > 0u) && all(id.xy < sdfMeshPyramidExtent(dims, 1u))) {
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

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
