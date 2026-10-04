// Publishes exactly one region; untouched maps retain their previously submitted depths.
#define SDF_PRIMARY_READ
#include "sdf-world.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../isa/sdf-indirect-layout.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.imageExtent) || passGroup.lightMap == 0u) { return; }
    float depth = asfloat(0x7f800000u);
    if (worldVisibilityCurrent(id.xy)) {
        SdfVisibility visibility = sdfLoadVisibility(worldVisibilityRecord(id.xy, 0u));
        if (visibility.identity != 0u || isnan(visibility.t)) { depth = visibility.t; }
    }
    uint address = (passGroup.lightMap - 1u) * SdfIndirectLightResolution * SdfIndirectLightResolution +
        id.y * SdfIndirectLightResolution + id.x;
    indirectLightDepthRW[address] = depth;
    puckCountWork(0u, 1u);
}
