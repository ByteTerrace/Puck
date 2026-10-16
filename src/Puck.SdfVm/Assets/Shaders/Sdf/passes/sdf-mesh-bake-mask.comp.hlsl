// The mesh bake's instance masks: one workgroup per lattice of the dispatch's batch (the pass block's meshBakeLattices),
// writing lattice `lattice`'s mask words, which select the instances that lattice samples, so the bake pass's field walk
// (sdf-mesh-bake.comp.hlsl) evaluates that group alone. A non-negative selector names a dynamic-transform slot, every
// active instance riding it; a negative one names the single instance -(selector + 1). Each workgroup strides its words,
// then writes the summary the walk skips empty words by.
#define SDF_DYNAMIC_TRANSFORMS
#include "sdf-world.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint threadIndex : SV_GroupIndex, uint3 group : SV_GroupID) {
    uint lattice = group.x;
    bool active = (lattice < passGroup.meshBakeCount);
    uint maskBase = worldInstanceMaskBase(lattice);
    uint maskWordCount = passGroup.instanceMaskWordCount;
    uint instanceCount = sdfInstanceCountClamped();
    uint instanceOffset = sdfInstanceDirectoryOffset();
    int selector = (active ? asint(passGroup.meshBakeLattices[(lattice * 3u) + 1u].w) : 0);

    if (active) {
        [loop]
        for (uint word = threadIndex; (word < maskWordCount); word += 64u) {
            uint bits = 0u;
            uint end = min(((word + 1u) * 32u), instanceCount);

            [loop]
            for (uint index = (word * 32u); (index < end); index++) {
                uint entryBase = sdfInstanceEntryOffset(instanceOffset, index);
                float4 bound = asfloat(sdfProgramWord(entryBase));
                uint4 meta = sdfProgramWord(entryBase + 1u);
                bool chosen = ((selector >= 0)
                    ? ((meta.x == SDF_BOUND_DYNAMIC) && (meta.y == (uint)selector) && (bound.w >= 0.0))
                    : (index == (uint)(-(selector + 1))));

                bits |= (chosen ? (1u << (index & 31u)) : 0u);
            }

            sdfInstanceMasksRW[maskBase + word] = bits;
        }
    }

    DeviceMemoryBarrierWithGroupSync();

    if (active && (threadIndex == 0u)) {
        uint summaryBase = (maskBase + maskWordCount);
        uint summaryCount = ((maskWordCount + 31u) >> 5u);

        [loop]
        for (uint summary = 0u; (summary < summaryCount); summary++) {
            uint bits = 0u;
            uint firstWord = (summary << 5u);
            uint endWord = min((firstWord + 32u), maskWordCount);

            [loop]
            for (uint word = firstWord; (word < endWord); word++) {
                bits |= ((sdfInstanceMasksRW[maskBase + word] != 0u) ? (1u << (word - firstWord)) : 0u);
            }

            sdfInstanceMasksRW[summaryBase + summary] = bits;
        }
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}
