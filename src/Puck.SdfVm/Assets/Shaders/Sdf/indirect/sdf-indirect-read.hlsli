#ifndef SDF_INDIRECT_READ_HLSLI
#define SDF_INDIRECT_READ_HLSLI
#include "sdf-indirect-cache.hlsli"

float3 sdfIndirectClassColor(uint classification) {
    if (classification == SdfIndirectClassActive) { return float3(0.1, 1.0, 0.2); }
    if (classification == SdfIndirectClassRelocated) { return float3(1.0, 0.65, 0.05); }
    if (classification == SdfIndirectClassInactive) { return float3(1.0, 0.1, 0.2); }
    return float3(0.15, 0.35, 1.0);
}

float3 sdfIndirectDebugProbes(float3 origin, float3 direction, float maximum, float3 background) {
    if (passGroup.indirectTier == SdfIndirectTierOff) { return background; }
    float nearest = maximum;
    float3 color = background;
    uint samples = 0u;
    // Debug work has a tier-derived finite ceiling: one sphere test per resident probe, no field evaluations.
    [loop] for (uint brick = 0u; brick < sdfIndirectBrickCapacity(passGroup.indirectTier); brick++) {
        int4 entry = indirectBricks[brick];
        samples++;
        if (entry.w < 0) { continue; }
        float spacing = sdfIndirectSpacing(passGroup.indirectTier, (uint)entry.w);
        float radius = spacing * 0.06;
        float3 center = (float3(entry.xyz) * 4.0 + 1.5) * spacing;
        float3 toCenter = center - origin;
        float along = dot(toCenter, direction);
        float bound = (2.6 + SdfIndirectRelocationAllowance) * spacing;
        if (along + bound < 0.0 || along - bound > nearest || length(toCenter - direction * along) > bound) { continue; }
        [loop] for (uint local = 0u; local < SdfIndirectProbesPerBrick; local++) {
            int index = (int)(brick * SdfIndirectProbesPerBrick + local);
            uint state = sdfIndirectLoad((uint)index * SdfIndirectProbeWords + 3u);
            if ((state >> SdfIndirectEpochShift) == 0u) { continue; }
            SdfIndirectPlacement probe = sdfIndirectReadProbe(index);
            samples++;
            float3 offset = probe.position - origin;
            float distance = dot(offset, direction);
            float squared = radius * radius - dot(offset - direction * distance, offset - direction * distance);
            if (squared < 0.0) { continue; }
            distance -= sqrt(squared);
            if (distance >= 0.0 && distance < nearest) { nearest = distance; color = sdfIndirectClassColor(probe.classification); }
        }
    }
    puckCountIndirect(0u, 0u, samples, 0u);
    return color;
}

float3 sdfIndirectDebugCells(float3 surface, float3 normal) {
    if (passGroup.indirectTier == SdfIndirectTierOff) { return 0.0; }
    uint budget = 32u;
    uint before = sdfWorkSteps;
    float3 color = 0.0;
    [loop] for (uint level = 0u; level < sdfIndirectLevelCount() && budget > 0u; level++) {
        float3 position;
        float clearance;
        if (!sdfIndirectLaunch(surface, normal, sdfIndirectSpacing(passGroup.indirectTier, level), budget, position, clearance)) { break; }
        uint mask = sdfIndirectProve(position, level, budget);
        if (mask != 0u) {
            uint first = (uint)firstbitlow(mask);
            float hue = frac((first + level * 8u) * 0.61803399);
            color = saturate(abs(frac(hue + float3(0.0, 0.333333, 0.666667)) * 6.0 - 3.0) - 1.0);
            break;
        }
    }
    if (passGroup.workCounterRowDetail != 0u) {
        puckCountDetail(0u, sdfWorkSteps - before, 0u, 0u, 0u, 0u);
        sdfWorkSteps = before;
    }
    return color;
}
#endif
