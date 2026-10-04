// One bounded local transport interval; a local clear end never invents a world sky exit.
#ifndef SDF_INDIRECT_NEAR_INCOMING_HLSLI
#define SDF_INDIRECT_NEAR_INCOMING_HLSLI
#include "sdf-indirect-march.hlsli"
#include "sdf-indirect-continuation.hlsli"
#include "sdf-indirect-diffuse.hlsli"

bool sdfIndirectNearIncoming(float3 launched, float3 direction, out SdfIndirectSources result,
    out bool hitSurface, out uint evaluations) {
    result = (SdfIndirectSources)0;
    hitSurface = false;
    uint budget = SdfIndirectNearSteps;
    SdfIndirectRay ray = sdfIndirectMarch(launched, direction, SdfIndirectNearReach, SdfIndirectNearReach,
        SDF_INSTANCE_MASK_ALL, 0.0, budget);
    evaluations = SdfIndirectNearSteps - budget;
    float3 endpoint = launched + direction * ray.distance;
    if (ray.kind == SdfIndirectKindUnresolved) { return false; }
    if (ray.kind == SdfIndirectKindExit) {
        // This proves only the local interval, never a world sky exit. The original direction continues into
        // a reachable finest-level ray in this exact completed bank; absent support remains unresolved.
        uint mask = sdfIndirectProve(endpoint, 0u, budget);
        evaluations = SdfIndirectNearSteps - budget;
        if (mask == 0u) { return false; }
        uint terminal = SdfIndirectKindContinuation | (mask << SdfIndirectProofMaskShift);
        return sdfIndirectReadContinuation(endpoint, direction, terminal, passGroup.indirectReadGeneration,
            passGroup.indirectReadPublication, result);
    }
    if (ray.material >= SDF_SCREEN_MATERIAL || dot(ray.normal, -direction) <= 0.0) { return false; }
    SdfIndirectSources previous = (SdfIndirectSources)0;
    if ((passGroup.indirectSources & SdfIndirectSourcesFeedback) != 0u) {
        // The host exposes this stamp only while the exact current source's preceding whole bank is immutable.
        // An absent predecessor cannot turn a requested feedback term into an answered direct-only sample.
        if (passGroup.indirectPreviousPublication == 0u) { return false; }
        float3 secondaryLaunch;
        float clearance;
        if (!sdfIndirectLaunch(endpoint, ray.normal, SdfIndirectNearReach, budget, secondaryLaunch, clearance)) {
            evaluations = SdfIndirectNearSteps - budget;
            return false;
        }
        uint mask = sdfIndirectProve(secondaryLaunch, 0u, budget, clearance);
        evaluations = SdfIndirectNearSteps - budget;
        if (mask == 0u || !sdfIndirectIrradianceAt(endpoint, secondaryLaunch, ray.normal, 0u, mask,
            passGroup.indirectReadGeneration ^ 1u, passGroup.indirectPreviousPublication, previous)) { return false; }
    }
    // Directional shadow queries retain their existing separately bounded fallback and are counted by the outer
    // indirect fold. The near row's field allowance above is shared by transport, launch and connectivity proof.
    SdfShadeSurface surface = (SdfShadeSurface)0;
    surface.position = endpoint;
    surface.normal = ray.normal;
    surface.rayDirection = direction;
    surface.material = sdfMaterialLoad((int)ray.material);
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = 1.0;
    surface.incomingVisibility = 1.0;
    [loop] for (uint slot = 0u; slot < passGroup.shadowSlotCount; slot++) {
        surface.shadowVisibility[slot] = sdfIndirectDiffuseVisibility(passGroup.shadowSlots[slot], endpoint, ray.normal);
    }
    [loop] for (uint fade = 0u; fade < min(passGroup.shadowFadeCount, (uint)SDF_SHADOW_FADE_SLOTS); fade++) {
        surface.incomingVisibility[fade] = sdfIndirectDiffuseVisibility(sdfShadowHandoffs[fade].Incoming, endpoint, ray.normal);
    }
    float attenuation;
    result = sdfIndirectDiffuse(surface, attenuation);
    float3 reflected = surface.material.albedo * (1.0 - surface.material.metal) * surface.material.bleed;
    result.values[SdfIndirectSourceFeedback] = reflected * sdfIndirectSourceTotal(previous) * attenuation;
    hitSurface = true;
    return true;
}

// The one incoming sample replaces the interval's cache answer. Failure keeps every source of that fallback.
SdfIndirectSources sdfIndirectNearResult(bool answered, SdfIndirectSources incoming, SdfIndirectSources fallback) {
    return answered ? incoming : fallback;
}
#endif