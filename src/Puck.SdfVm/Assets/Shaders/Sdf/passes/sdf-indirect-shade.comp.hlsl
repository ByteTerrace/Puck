#define SDF_INDIRECT_PASS
#define SDF_INDIRECT_SHADE
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_SHADOW_FADE_SLOTS 2
// A hit's slot visibilities are the one procedure the kernel runs; its fallback rays are their march calls.
#define SDF_INDIRECT_PROCS_CUSTOM
#define SDF_INDIRECT_PROC_VISIBILITIES
#define SDF_INDIRECT_PROC_MARCH
#include "../indirect/sdf-indirect-shade.hlsli"
#include "../indirect/sdf-indirect-procedures.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    if (passGroup.indirectTier == SdfIndirectTierOff || group.x >= passGroup.indirectShadeCount
        || group.x >= sdfIndirectShadeBudget(passGroup.indirectTier)
        || passGroup.indirectReadGeneration >= SdfIndirectLightingGenerations
        || passGroup.indirectWriteGeneration >= SdfIndirectLightingGenerations) { return; }
    sdfProgramLayout = sdfLoadProgramLayout();
    uint4 update;
    int3 lattice;
    if (!sdfIndirectProbeUpdate(group.x, update, lattice)) { return; }
    uint index = update.x;
    sdfIndirectShadeProbe(index, update.z, lattice, lane, passGroup.indirectReadGeneration,
        passGroup.indirectReadPublication, passGroup.indirectWriteGeneration,
        passGroup.indirectWritePublication, passGroup.indirectFeedback);
}
