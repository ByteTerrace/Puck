// The explicit diffuse source fold shared by the cache solve, near-field replacement and comparison methods.
// Callers provide stable/incoming visibility; this fold performs no field or irradiance query.
#ifndef SDF_INDIRECT_DIFFUSE_HLSLI
#define SDF_INDIRECT_DIFFUSE_HLSLI
#include "sdf-indirect-irradiance.hlsli"
#include "sdf-indirect-light.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-levers.hlsli"
#include "../shade/sdf-light.hlsli"
#ifdef SDF_VIEWS_PASS
#include "../isa/sdf-sky-kinds.hlsli"
#endif

float sdfIndirectDiffuseVisibility(int light, float3 surfacePoint, float3 normal) {
    float3 direction;
    if (!sdfLightDirection(light, direction)) { return 1.0; }
    bool fallback;
    uint region;
#ifndef SDF_VIEWS_PASS
    uint before = sdfIndirectEvaluations;
#endif
    float visibility = sdfIndirectLightVisibility((uint)light, surfacePoint, normal, direction, fallback, region);
#ifdef SDF_VIEWS_PASS
    // The receiver's outer fold owns every field step once; views have one reserved indirect detail row.
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT, 0u, 1u, fallback ? 1u : 0u);
#else
    puckCountIndirect(fallback ? 6u : 5u, 0u, 1u, fallback ? 1u : 0u);
    puckCountDetail(6u, sdfIndirectEvaluations - before, 0u, 0u, 0u, 0u);
#endif
    return visibility;
}

// Stable slots precede incoming slots in one rolled traversal. A fallback visibility ray contains the field
// interpreter, so both destinations share its call site while retaining their own slot order and counters.
void sdfIndirectDiffuseVisibilities(float3 surfacePoint, float3 normal, out float4 shadows, out float2 incoming) {
    shadows = 1.0;
    incoming = 1.0;
    uint stableCount = min(passGroup.shadowSlotCount, SDF_MAX_SHADOW_SLOTS);
    uint count = stableCount;
#if SDF_SHADOW_FADE_SLOTS > 0
    uint handoffCount, handoffStride;
    sdfShadowHandoffs.GetDimensions(handoffCount, handoffStride);
    count += min(min(passGroup.shadowFadeCount, (uint)SDF_SHADOW_FADE_SLOTS), handoffCount);
#endif
    [loop] for (uint slot = 0u; slot < count; slot++) {
        int light;
#if SDF_SHADOW_FADE_SLOTS > 0
        if (slot >= stableCount) {
            light = sdfShadowHandoffs[slot - stableCount].Incoming;
        } else
#endif
        {
            light = passGroup.shadowSlots[slot];
        }
        float visibility = sdfIndirectDiffuseVisibility(light, surfacePoint, normal);
        if (slot < stableCount) {
            shadows[slot] = visibility;
        }
#if SDF_SHADOW_FADE_SLOTS > 0
        else {
            incoming[slot - stableCount] = visibility;
        }
#endif
    }
}

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
