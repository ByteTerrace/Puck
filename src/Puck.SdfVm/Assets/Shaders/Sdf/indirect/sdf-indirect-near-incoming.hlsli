// One bounded local transport interval; a local clear end never invents a world sky exit.
#ifndef SDF_INDIRECT_NEAR_INCOMING_HLSLI
#define SDF_INDIRECT_NEAR_INCOMING_HLSLI
#include "sdf-indirect-march.hlsli"
#include "sdf-indirect-continuation.hlsli"
#include "sdf-indirect-diffuse.hlsli"
#include "sdf-indirect-screen.hlsli"

// The near-field incoming sample along `direction` from `launched`: its march, a hit's secondary launch, the one
// full-field proof body both paths enter, and a hit's diffuse visibilities are the calls of one procedure
// (sdfIndirectNearIncomingStep), all within the shared Near allowance, which `evaluations` reports spent.
struct SdfIndirectNearIncomingProc {
    float3 launched;
    float3 direction;
    SdfIndirectSources result;
    bool hitSurface;
    uint evaluations;
    bool answered;
    uint budget;
    SdfIndirectRay ray;
    float3 endpoint;
    bool localExit;
    bool needsFeedback;
    float3 proofPosition;
    float clearance;
    uint mask;
    SdfIndirectSources previous;
    uint phase;
};
static SdfIndirectNearIncomingProc sdfIndirectNearIncomingProc = (SdfIndirectNearIncomingProc)0;

static const uint SdfIndirectNearBegun = 0u;
static const uint SdfIndirectNearMarched = 1u;
static const uint SdfIndirectNearLaunched = 2u;
static const uint SdfIndirectNearProved = 3u;
static const uint SdfIndirectNearShadowed = 4u;

uint sdfIndirectNearIncomingBegin(float3 launched, float3 direction) {
    sdfIndirectNearIncomingProc.launched = launched;
    sdfIndirectNearIncomingProc.direction = direction;
    sdfIndirectNearIncomingProc.phase = SdfIndirectNearBegun;
    return SdfIndirectProcNearIncoming;
}

uint sdfIndirectNearIncomingStep() {
    uint phase = sdfIndirectNearIncomingProc.phase;
    float3 direction = sdfIndirectNearIncomingProc.direction;
    if (phase == SdfIndirectNearBegun) {
        sdfIndirectNearIncomingProc.result = (SdfIndirectSources)0;
        sdfIndirectNearIncomingProc.hitSurface = false;
        sdfIndirectNearIncomingProc.answered = false;
        sdfIndirectNearIncomingProc.evaluations = 0u;
        sdfIndirectNearIncomingProc.phase = SdfIndirectNearMarched;
        return sdfIndirectCall(sdfIndirectMarchBegin(sdfIndirectNearIncomingProc.launched, direction, SdfIndirectNearReach, SdfIndirectNearReach,
            SDF_INSTANCE_MASK_ALL, 0.0, SdfIndirectNearSteps));
    }
    bool prove = false;
    if (phase == SdfIndirectNearMarched) {
        SdfIndirectRay ray = sdfIndirectMarchProc.result;
        sdfIndirectNearIncomingProc.ray = ray;
        sdfIndirectNearIncomingProc.budget = sdfIndirectMarchProc.budget;
        sdfIndirectNearIncomingProc.evaluations = SdfIndirectNearSteps - sdfIndirectNearIncomingProc.budget;
        float3 endpoint = sdfIndirectNearIncomingProc.launched + direction * ray.distance;
        sdfIndirectNearIncomingProc.endpoint = endpoint;
        if (ray.kind == SdfIndirectKindUnresolved) { return SdfIndirectStepReturn; }
        sdfIndirectNearIncomingProc.localExit = ray.kind == SdfIndirectKindExit;
        sdfIndirectNearIncomingProc.needsFeedback = (passGroup.indirectSources & SdfIndirectSourcesFeedback) != 0u;
        sdfIndirectNearIncomingProc.proofPosition = endpoint;
        sdfIndirectNearIncomingProc.clearance = 0.0;
        if (!sdfIndirectNearIncomingProc.localExit) {
            float3 screenEmission;
            if (sdfIndirectScreenEmission((int)ray.material, endpoint, direction, screenEmission)) {
                sdfIndirectNearIncomingProc.result.values[SdfIndirectSourceScreens] = screenEmission;
                sdfIndirectNearIncomingProc.hitSurface = true;
                sdfIndirectNearIncomingProc.answered = true;
                return SdfIndirectStepReturn;
            }
            if (ray.material >= SDF_SCREEN_MATERIAL || dot(ray.normal, -direction) <= 0.0) { return SdfIndirectStepReturn; }
            if (sdfIndirectNearIncomingProc.needsFeedback) {
                // The host exposes this stamp only while the exact current source's preceding whole bank is immutable.
                // An absent predecessor cannot turn a requested feedback term into an answered direct-only sample.
                if (passGroup.indirectPreviousPublication == 0u) { return SdfIndirectStepReturn; }
                sdfIndirectNearIncomingProc.phase = SdfIndirectNearLaunched;
                return sdfIndirectCall(sdfIndirectLaunchBegin(endpoint, ray.normal, SdfIndirectNearReach, sdfIndirectNearIncomingProc.budget));
            }
        }
        prove = true;
    } else if (phase == SdfIndirectNearLaunched) {
        sdfIndirectNearIncomingProc.budget = sdfIndirectLaunchProc.budget;
        sdfIndirectNearIncomingProc.proofPosition = sdfIndirectLaunchProc.position;
        sdfIndirectNearIncomingProc.clearance = sdfIndirectLaunchProc.clearance;
        if (!sdfIndirectLaunchProc.launched) {
            sdfIndirectNearIncomingProc.evaluations = SdfIndirectNearSteps - sdfIndirectNearIncomingProc.budget;
            return SdfIndirectStepReturn;
        }
        prove = true;
    }
    if (prove) {
        // Both paths enter one full-field proof body, after their original endpoint or secondary launch is known.
        sdfIndirectNearIncomingProc.mask = 0u;
        if (sdfIndirectNearIncomingProc.localExit || sdfIndirectNearIncomingProc.needsFeedback) {
            sdfIndirectNearIncomingProc.phase = SdfIndirectNearProved;
            return sdfIndirectCall(sdfIndirectProveBegin(sdfIndirectNearIncomingProc.proofPosition, 0u, sdfIndirectNearIncomingProc.budget, sdfIndirectNearIncomingProc.clearance));
        }
        phase = SdfIndirectNearProved;
    } else if (phase == SdfIndirectNearProved) {
        sdfIndirectNearIncomingProc.mask = sdfIndirectProveProc.mask;
        sdfIndirectNearIncomingProc.budget = sdfIndirectProveProc.budget;
        sdfIndirectNearIncomingProc.evaluations = SdfIndirectNearSteps - sdfIndirectNearIncomingProc.budget;
    }
    if (phase == SdfIndirectNearProved) {
        SdfIndirectRay ray = sdfIndirectNearIncomingProc.ray;
        float3 endpoint = sdfIndirectNearIncomingProc.endpoint;
        uint mask = sdfIndirectNearIncomingProc.mask;
        if (sdfIndirectNearIncomingProc.localExit) {
            // This proves only the local interval, never a world sky exit. The original direction continues into
            // a reachable finest-level ray in this exact completed bank; absent support remains unresolved.
            if (mask == 0u) { return SdfIndirectStepReturn; }
            uint terminal = SdfIndirectKindContinuation | (mask << SdfIndirectProofMaskShift);
            SdfIndirectSources continued;
            sdfIndirectNearIncomingProc.answered = sdfIndirectReadContinuation(endpoint, direction, terminal, passGroup.indirectReadGeneration,
                passGroup.indirectReadPublication, continued);
            sdfIndirectNearIncomingProc.result = continued;
            return SdfIndirectStepReturn;
        }
        sdfIndirectNearIncomingProc.previous = (SdfIndirectSources)0;
        if (sdfIndirectNearIncomingProc.needsFeedback) {
            SdfIndirectSources previous;
            if (mask == 0u || !sdfIndirectIrradianceAt(endpoint, sdfIndirectNearIncomingProc.proofPosition, ray.normal, 0u, mask,
                passGroup.indirectReadGeneration ^ 1u, passGroup.indirectPreviousPublication, previous)) { return SdfIndirectStepReturn; }
            sdfIndirectNearIncomingProc.previous = previous;
        }
        // Directional shadow queries retain their existing separately bounded fallback and are counted by the outer
        // indirect fold. The near row's field allowance above is shared by transport, launch and connectivity proof.
        sdfIndirectNearIncomingProc.phase = SdfIndirectNearShadowed;
        return sdfIndirectCall(sdfIndirectVisibilitiesBegin(endpoint, ray.normal));
    }
    // The hit's diffuse fold over its slots' visibilities.
    SdfIndirectRay ray = sdfIndirectNearIncomingProc.ray;
    float3 endpoint = sdfIndirectNearIncomingProc.endpoint;
    SdfIndirectSources previous = sdfIndirectNearIncomingProc.previous;
    SdfShadeSurface surface = (SdfShadeSurface)0;
    surface.position = endpoint;
    surface.normal = ray.normal;
    surface.rayDirection = direction;
    surface.material = sdfMaterialLoad((int)ray.material);
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = sdfIndirectVisibilitiesProc.shadows;
    surface.incomingVisibility = sdfIndirectVisibilitiesProc.incoming;
    float attenuation;
    SdfIndirectSources result = sdfIndirectDiffuse(surface, attenuation);
    float3 reflected = surface.material.albedo * (1.0 - surface.material.metal) * surface.material.bleed;
    result.values[SdfIndirectSourceFeedback] = reflected * sdfIndirectSourceTotal(previous) * (attenuation * passGroup.indirectFeedbackGain);
    sdfIndirectNearIncomingProc.result = result;
    sdfIndirectNearIncomingProc.hitSurface = true;
    sdfIndirectNearIncomingProc.answered = true;
    return SdfIndirectStepReturn;
}

#ifdef SDF_INDIRECT_PROC_NEAR_INCOMING
bool sdfIndirectNearIncoming(float3 launched, float3 direction, out SdfIndirectSources result,
    out bool hitSurface, out uint evaluations) {
    sdfIndirectRun(sdfIndirectNearIncomingBegin(launched, direction));
    result = sdfIndirectNearIncomingProc.result;
    hitSurface = sdfIndirectNearIncomingProc.hitSurface;
    evaluations = sdfIndirectNearIncomingProc.evaluations;
    return sdfIndirectNearIncomingProc.answered;
}
#endif
// The one incoming sample replaces the interval's cache answer. Failure keeps every source of that fallback.
SdfIndirectSources sdfIndirectNearResult(bool answered, SdfIndirectSources incoming, SdfIndirectSources fallback) {
    if (answered) { return incoming; }
    return fallback;
}
#endif
