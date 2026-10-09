// The explicit diffuse source fold shared by the cache solve, near-field replacement and comparison methods.
// Callers provide stable/incoming visibility; this fold performs no field or irradiance query.
#ifndef SDF_INDIRECT_DIFFUSE_HLSLI
#define SDF_INDIRECT_DIFFUSE_HLSLI
#include "sdf-indirect-irradiance.hlsli"
#include "sdf-indirect-light.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-levers.hlsli"
#include "../shade/sdf-light.hlsli"
#ifdef SDF_RECEIVER_PASS
#include "../isa/sdf-sky-kinds.hlsli"
#endif

// The stable and incoming slots' visibilities at a surface point, stable slots before incoming slots. A light's map or
// facing decides it without the field (sdfIndirectLightDecided); otherwise its bounded fallback ray is the one march
// call of this procedure (sdfIndirectVisibilitiesStep), so every slot shares the fallback ray's field queries while
// retaining its own slot order and counters.
struct SdfIndirectVisibilitiesProc {
    float3 surfacePoint;
    float3 normal;
    float4 shadows;
    float2 incoming;
    uint stableCount;
    uint count;
    uint slot;
#ifndef SDF_RECEIVER_PASS
    uint before;
#endif
    bool previousSecondary;
    bool previousParticipation;
    bool resuming;
};
static SdfIndirectVisibilitiesProc sdfIndirectVisibilitiesProc = (SdfIndirectVisibilitiesProc)0;

uint sdfIndirectVisibilitiesBegin(float3 surfacePoint, float3 normal) {
    sdfIndirectVisibilitiesProc.surfacePoint = surfacePoint;
    sdfIndirectVisibilitiesProc.normal = normal;
    sdfIndirectVisibilitiesProc.resuming = false;
    return SdfIndirectProcVisibilities;
}

// Counts one slot's light visibility and stores it in its stable or incoming slot.
void sdfIndirectVisibilitiesTake(float visibility, bool fallback) {
#ifdef SDF_RECEIVER_PASS
    // The receiver's outer fold owns every field step once; it has one reserved indirect detail row.
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT, 0u, 1u, fallback ? 1u : 0u);
#else
    puckCountIndirect(fallback ? 6u : 5u, 0u, 1u, fallback ? 1u : 0u);
    puckCountDetail(6u, sdfIndirectEvaluations - sdfIndirectVisibilitiesProc.before, 0u, 0u, 0u, 0u);
#endif
    uint slot = sdfIndirectVisibilitiesProc.slot;
    if (slot < sdfIndirectVisibilitiesProc.stableCount) {
        sdfIndirectVisibilitiesProc.shadows[slot] = visibility;
    }
#if SDF_SHADOW_FADE_SLOTS > 0
    else {
        sdfIndirectVisibilitiesProc.incoming[slot - sdfIndirectVisibilitiesProc.stableCount] = visibility;
    }
#endif
}

uint sdfIndirectVisibilitiesStep() {
    if (!sdfIndirectVisibilitiesProc.resuming) {
        sdfIndirectVisibilitiesProc.shadows = 1.0;
        sdfIndirectVisibilitiesProc.incoming = 1.0;
        uint stableCount = min(passGroup.shadowSlotCount, SDF_MAX_SHADOW_SLOTS);
        uint count = stableCount;
#if SDF_SHADOW_FADE_SLOTS > 0
        uint handoffCount, handoffStride;
        sdfShadowHandoffs.GetDimensions(handoffCount, handoffStride);
        count += min(min(passGroup.shadowFadeCount, (uint)SDF_SHADOW_FADE_SLOTS), handoffCount);
#endif
        sdfIndirectVisibilitiesProc.stableCount = stableCount;
        sdfIndirectVisibilitiesProc.count = count;
        sdfIndirectVisibilitiesProc.slot = 0u;
        sdfIndirectVisibilitiesProc.resuming = true;
    } else {
        sdfShadowParticipationActive = sdfIndirectVisibilitiesProc.previousParticipation;
        sdfSecondaryMarchActive = sdfIndirectVisibilitiesProc.previousSecondary;
        sdfIndirectVisibilitiesTake(sdfIndirectMarchProc.result.kind == SdfIndirectKindExit ? 1.0 : 0.0, true);
        sdfIndirectVisibilitiesProc.slot++;
    }
    [loop]
    for (; sdfIndirectVisibilitiesProc.slot < sdfIndirectVisibilitiesProc.count; sdfIndirectVisibilitiesProc.slot++) {
        uint slot = sdfIndirectVisibilitiesProc.slot;
        int light;
#if SDF_SHADOW_FADE_SLOTS > 0
        if (slot >= sdfIndirectVisibilitiesProc.stableCount) {
            light = sdfShadowHandoffs[slot - sdfIndirectVisibilitiesProc.stableCount].Incoming;
        } else
#endif
        {
            light = passGroup.shadowSlots[slot];
        }
        float3 direction;
        if (!sdfLightDirection(light, direction)) {
            // A light with no direction is unshadowed and uncounted.
            if (slot < sdfIndirectVisibilitiesProc.stableCount) {
                sdfIndirectVisibilitiesProc.shadows[slot] = 1.0;
            }
#if SDF_SHADOW_FADE_SLOTS > 0
            else {
                sdfIndirectVisibilitiesProc.incoming[slot - sdfIndirectVisibilitiesProc.stableCount] = 1.0;
            }
#endif
            continue;
        }
#ifndef SDF_RECEIVER_PASS
        sdfIndirectVisibilitiesProc.before = sdfIndirectEvaluations;
#endif
        float visibility;
        uint region;
        if (sdfIndirectLightDecided((uint)light, sdfIndirectVisibilitiesProc.surfacePoint, sdfIndirectVisibilitiesProc.normal, direction, visibility, region)) {
            sdfIndirectVisibilitiesTake(visibility, false);
            continue;
        }
        sdfIndirectVisibilitiesProc.previousSecondary = sdfSecondaryMarchActive;
        sdfIndirectVisibilitiesProc.previousParticipation = sdfShadowParticipationActive;
        sdfSecondaryMarchActive = true;
        sdfShadowParticipationActive = true;
        return sdfIndirectCall(sdfIndirectMarchBegin(sdfIndirectVisibilitiesProc.surfacePoint + sdfIndirectVisibilitiesProc.normal * 0.002, direction, 0.0, passGroup.farDistance,
            SDF_INSTANCE_MASK_ALL, 0.0, SdfIndirectLightMarchSteps));
    }
    return SdfIndirectStepReturn;
}

#ifdef SDF_INDIRECT_PROC_VISIBILITIES
void sdfIndirectDiffuseVisibilities(float3 surfacePoint, float3 normal, out float4 shadows, out float2 incoming) {
    sdfIndirectRun(sdfIndirectVisibilitiesBegin(surfacePoint, normal));
    shadows = sdfIndirectVisibilitiesProc.shadows;
    incoming = sdfIndirectVisibilitiesProc.incoming;
}
#endif
// Attenuation is returned for the caller's separate feedback term, avoiding a second walk of the light table.
// Emission retains its material response without reflected-light attenuation; sky and feedback remain zero here.
SdfIndirectSources sdfIndirectDiffuse(SdfShadeSurface surface, out float attenuation) {
    SdfIndirectSources result = (SdfIndirectSources)0;
    float3 direct = 0.0;
    attenuation = 1.0;
    [loop] for (uint lightIndex = 0u; lightIndex < sdfLightCount(); lightIndex++) {
        SdfLightSource light;
        if (!sdfLightAt(lightIndex, light)) { continue; }
        // An indirect ray sees the emitting face itself. Analytic screen glow belongs only to the primary view hit.
        if (light.kind == SdfLightScreen) { continue; }
        SdfLightResponse response = sdfLightResponse(light, surface);
        direct += response.diffuse * light.bounce;
        attenuation *= response.attenuation;
    }
    float3 reflected = surface.material.albedo * (1.0 - surface.material.metal) * surface.material.bleed;
    if ((passGroup.indirectSources & SdfIndirectSourcesDirect) != 0u) {
        result.values[SdfIndirectSourceDirect] = reflected * direct * (attenuation * passGroup.indirectSourceGains.x);
    }
    if ((passGroup.indirectSources & SdfIndirectSourcesEmission) != 0u) {
        result.values[SdfIndirectSourceEmission] = surface.material.albedo * surface.material.emissive * surface.material.bleed * passGroup.indirectSourceGains.y;
    }
    return result;
}
#endif
