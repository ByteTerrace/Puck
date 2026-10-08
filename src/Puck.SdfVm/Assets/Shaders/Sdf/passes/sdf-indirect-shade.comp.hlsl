#define SDF_INDIRECT_PASS
#define SDF_INDIRECT_SHADE
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_SHADOW_FADE_SLOTS 2
#include "../indirect/sdf-indirect-shade.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint item = passGroup.indirectItemFirst + group.x;
    uint rays = sdfIndirectRaysPerProbe(passGroup.indirectTier);
    if (passGroup.indirectTier == SdfIndirectTierOff || item >= passGroup.indirectShadeCount
        || item >= sdfIndirectShadeBudget(passGroup.indirectTier)
        || passGroup.indirectReadGeneration >= SdfIndirectLightingGenerations
        || passGroup.indirectWriteGeneration >= SdfIndirectLightingGenerations
        || !sdfIndirectChunkTouches(group.x, rays)) { return; }
    sdfProgramLayout = sdfLoadProgramLayout();
    uint4 update;
    int3 lattice;
    if (!sdfIndirectProbeUpdate(item, update, lattice)) { return; }
    uint index = update.x;
    // The probe's admitted rays: all of them, or one chunk's run of a probe too heavy for one submission.
    uint first = group.x * rays;
    uint rayFirst = passGroup.indirectUnitFirst > first ? passGroup.indirectUnitFirst - first : 0u;
    uint rayEnd = min(rays, passGroup.indirectUnitFirst + passGroup.indirectUnitCount - first);
    sdfIndirectShadeProbe(index, update.z, lattice, lane, passGroup.indirectReadGeneration,
        passGroup.indirectReadPublication, passGroup.indirectWriteGeneration,
        passGroup.indirectWritePublication, passGroup.indirectFeedback, rayFirst, rayEnd);
}
