// One bounded local transport interval; a local clear end never invents a world sky exit.
#ifndef SDF_INDIRECT_NEAR_INCOMING_HLSLI
#define SDF_INDIRECT_NEAR_INCOMING_HLSLI
#include "sdf-indirect-march.hlsli"
#include "sdf-indirect-continuation.hlsli"
#include "sdf-indirect-diffuse.hlsli"
#include "sdf-indirect-screen.hlsli"

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
    bool localExit = ray.kind == SdfIndirectKindExit;
    bool needsFeedback = (passGroup.indirectSources & SdfIndirectSourcesFeedback) != 0u;
    float3 proofPosition = endpoint;
    float clearance = 0.0;
    if (!localExit) {
        float3 screenEmission;
        if (sdfIndirectScreenEmission((int)ray.material, endpoint, direction, screenEmission)) {
            result.values[SdfIndirectSourceScreens] = screenEmission;
            hitSurface = true;
            return true;
        }
        if (ray.material >= SDF_SCREEN_MATERIAL || dot(ray.normal, -direction) <= 0.0) { return false; }
        if (needsFeedback) {
            // The host exposes this stamp only while the exact current source's preceding whole bank is immutable.
            // An absent predecessor cannot turn a requested feedback term into an answered direct-only sample.
            if (passGroup.indirectPreviousPublication == 0u) { return false; }
            if (!sdfIndirectLaunch(endpoint, ray.normal, SdfIndirectNearReach, budget, proofPosition, clearance)) {
                evaluations = SdfIndirectNearSteps - budget;
                return false;
            }
        }
    }
    // Both paths enter one full-field proof body, after their original endpoint or secondary launch is known.
    uint mask = 0u;
    if (localExit || needsFeedback) {
        mask = sdfIndirectProve(proofPosition, 0u, budget, clearance);
        evaluations = SdfIndirectNearSteps - budget;
    }
    if (localExit) {
        // This proves only the local interval, never a world sky exit. The original direction continues into
        // a reachable finest-level ray in this exact completed bank; absent support remains unresolved.
        if (mask == 0u) { return false; }
        uint terminal = SdfIndirectKindContinuation | (mask << SdfIndirectProofMaskShift);
        return sdfIndirectReadContinuation(endpoint, direction, terminal, passGroup.indirectReadGeneration,
            passGroup.indirectReadPublication, result);
    }
    SdfIndirectSources previous = (SdfIndirectSources)0;
    if (needsFeedback) {
        if (mask == 0u || !sdfIndirectIrradianceAt(endpoint, proofPosition, ray.normal, 0u, mask,
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
    sdfIndirectDiffuseVisibilities(endpoint, ray.normal, surface.shadowVisibility, surface.incomingVisibility);
    float attenuation;
    result = sdfIndirectDiffuse(surface, attenuation);
    float3 reflected = surface.material.albedo * (1.0 - surface.material.metal) * surface.material.bleed;
    result.values[SdfIndirectSourceFeedback] = reflected * sdfIndirectSourceTotal(previous) * (attenuation * passGroup.indirectFeedbackGain);
    hitSurface = true;
    return true;
}

// The one incoming sample replaces the interval's cache answer. Failure keeps every source of that fallback.
SdfIndirectSources sdfIndirectNearResult(bool answered, SdfIndirectSources incoming, SdfIndirectSources fallback) {
    if (answered) { return incoming; }
    return fallback;
}
#endif
