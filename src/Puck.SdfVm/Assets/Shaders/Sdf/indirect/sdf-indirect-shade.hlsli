// One finite sweep's lighting of immutable transport records. The caller binds one pinned lighting snapshot and
// visits coarser levels before finer readers; only a complete submitted sweep becomes visible to view receivers.
#ifndef SDF_INDIRECT_SHADE_HLSLI
#define SDF_INDIRECT_SHADE_HLSLI
#include "sdf-indirect-irradiance.hlsli"
#include "sdf-indirect-light.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-levers.hlsli"
#include "../shade/sdf-light.hlsli"

// 256 rays * (five float3 sources, one direction and one terminal) = 19,456 bytes, beside the VM's gather mask.
groupshared SdfIndirectSources sdfIndirectShaded[SdfIndirectMaximumRaysPerProbe];
groupshared float3 sdfIndirectShadedDirections[SdfIndirectMaximumRaysPerProbe];
groupshared uint sdfIndirectShadedKinds[SdfIndirectMaximumRaysPerProbe];

float sdfIndirectShadeVisibility(int light, float3 surfacePoint, float3 normal) {
    if (light < 0) { return 1.0; }
    SdfLight record = sdfLights[(uint)light];
    if (record.Kind != SDF_LIGHT_DIRECTIONAL) { return 1.0; }
    bool fallback;
    uint region;
    uint before = sdfIndirectEvaluations;
    float visibility = sdfIndirectLightVisibility((uint)light, surfacePoint, normal, record.Direction, fallback, region);
    puckCountIndirect(fallback ? 6u : 5u, 0u, 1u, fallback ? 1u : 0u);
    puckCountDetail(6u, sdfIndirectEvaluations - before, 0u, 0u, 0u, 0u);
    return visibility;
}

SdfIndirectSources sdfIndirectShadeHit(float3 surfacePoint, float3 direction, uint4 hit, uint readGeneration,
    uint readPublication, float feedbackGain) {
    SdfIndirectSources result = (SdfIndirectSources)0;
    SdfShadeSurface surface = (SdfShadeSurface)0;
    surface.position = surfacePoint;
    surface.normal = sdfIndirectUnpackNormal(hit.y);
    surface.rayDirection = direction;
    surface.material = sdfMaterialLoad((int)hit.z);
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = 1.0;
    surface.incomingVisibility = 1.0;
    [loop] for (uint slot = 0u; slot < passGroup.shadowSlotCount; slot++) {
        surface.shadowVisibility[slot] = sdfIndirectShadeVisibility(passGroup.shadowSlots[slot], surfacePoint, surface.normal);
    }
    [loop] for (uint fade = 0u; fade < min(passGroup.shadowFadeCount, (uint)SDF_SHADOW_FADE_SLOTS); fade++) {
        surface.incomingVisibility[fade] = sdfIndirectShadeVisibility(sdfShadowHandoffs[fade].Incoming, surfacePoint, surface.normal);
    }
    float3 direct = 0.0;
    float3 screens = 0.0;
    float attenuation = 1.0;
    [loop] for (uint lightIndex = 0u; lightIndex < sdfLightCount(); lightIndex++) {
        SdfLightSource light;
        if (!sdfLightAt(lightIndex, light)) { continue; }
        SdfLightResponse response = sdfLightResponse(light, surface);
        if (light.kind == SdfLightScreen) { screens += response.diffuse * light.bounce; }
        else { direct += response.diffuse * light.bounce; }
        attenuation *= response.attenuation;
    }
    float3 reflected = surface.material.albedo * (1.0 - surface.material.metal) * surface.material.bleed;
    result.values[SdfIndirectSourceDirect] = reflected * direct * attenuation;
    result.values[SdfIndirectSourceScreens] = reflected * screens * attenuation;
    result.values[SdfIndirectSourceEmission] = surface.material.albedo * surface.material.emissive * surface.material.bleed;
    if (feedbackGain > 0.0 && readPublication != 0u) {
        uint level = (hit.w >> SdfIndirectProofLevelShift) & 3u;
        uint mask = (hit.w >> SdfIndirectProofMaskShift) & 255u;
        float3 launched = sdfIndirectLaunchPosition(surfacePoint, hit.y, hit.w, sdfIndirectSpacing(passGroup.indirectTier, level));
        SdfIndirectSources previous;
        if (mask != 0u && sdfIndirectIrradianceAt(surfacePoint, launched, surface.normal, level, mask,
            readGeneration, readPublication, previous)) {
            result.values[SdfIndirectSourceFeedback] = reflected * sdfIndirectSourceTotal(previous) * (feedbackGain * attenuation);
        }
    }
    return result;
}

SdfIndirectSources sdfIndirectShadeContinuation(float3 position, float3 direction, uint terminal,
    uint generation, uint publication) {
    SdfIndirectSources result = (SdfIndirectSources)0;
    uint level = (terminal >> SdfIndirectProofLevelShift) & 3u;
    uint mask = (terminal >> SdfIndirectProofMaskShift) & 255u;
    float3 scaled = position / sdfIndirectSpacing(passGroup.indirectTier, level);
    int3 cell = int3(floor(scaled));
    float total = 0.0;
    [unroll] for (uint corner = 0u; corner < 8u; corner++) {
        if ((mask & (1u << corner)) == 0u) { continue; }
        int3 lattice = cell + sdfIndirectCorner(corner);
        int index = sdfIndirectProbeIndex(lattice, level);
        if (index < 0 || !sdfIndirectPublished((uint)index, generation, publication)) { continue; }
        SdfIndirectPlacement probe = sdfIndirectReadProbe(index);
        if (probe.classification != SdfIndirectClassActive && probe.classification != SdfIndirectClassRelocated) { continue; }
        int ray = sdfIndirectContinuationRay(position, direction, lattice, level, (uint)index, probe.position);
        if (ray < 0) { continue; }
        // IrradianceCacheModel keeps a reachable face corner in the sum even at a zero trilinear coordinate.
        float weight = max(sdfIndirectCornerWeight(frac(scaled), corner), 1.0e-6);
        uint address = sdfIndirectRadianceAddress((uint)index, (uint)ray, generation);
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            result.values[source] += sdfIndirectUnpackRadiance(sdfIndirectLoad(address + source)) * weight;
        }
        total += weight;
    }
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        result.values[source] = total > 0.0 ? result.values[source] / total : 0.0;
    }
    return result;
}

// All lanes enter, including dormant and inactive probes. No field query classifies a probe during shading.
void sdfIndirectShadeProbe(uint index, uint level, int3 lattice, uint lane, uint readGeneration, uint readPublication,
    uint writeGeneration, uint writePublication, float feedbackGain) {
    SdfIndirectPlacement probe = sdfIndirectReadProbe((int)index);
    bool active = probe.classification == SdfIndirectClassActive || probe.classification == SdfIndirectClassRelocated;
    uint rays = sdfIndirectRaysPerProbe(passGroup.indirectTier);
    uint detail = passGroup.indirectTier == SdfIndirectTierHigh ? level : level + 1u;
    [loop] for (uint ray = lane; ray < rays; ray += 64u) {
        float3 direction = sdfIndirectDirection(lattice, level, ray);
        uint address = sdfIndirectHitWordOffset(passGroup.indirectTier) + (index * rays + ray) * SdfIndirectHitWords;
        uint4 hit = active ? uint4(sdfIndirectLoad(address), sdfIndirectLoad(address + 1u),
            sdfIndirectLoad(address + 2u), sdfIndirectLoad(address + 3u)) : uint4(0u, 0u, 0u, SdfIndirectKindUnresolved);
        uint kind = hit.w & SdfIndirectKindMask;
        float3 endpoint = probe.position + direction * asfloat(hit.x);
        SdfIndirectSources result = (SdfIndirectSources)0;
        if (kind == SdfIndirectKindHit) {
            result = sdfIndirectShadeHit(endpoint, direction, hit, readGeneration, readPublication, feedbackGain);
            puckCountIndirect(detail, 1u, 0u, 0u);
        } else if (kind == SdfIndirectKindContinuation) {
            result = sdfIndirectShadeContinuation(endpoint, direction, hit.w, writeGeneration, writePublication);
        }
        // Sky exits are filled by the environment snapshot seam. Unresolved rays never enter the cosine denominator.
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            uint packed = sdfIndirectPackRadiance(result.values[source]);
            sdfIndirectShaded[ray].values[source] = sdfIndirectUnpackRadiance(packed);
            if (index >= sdfIndirectRadianceProbeOffset(passGroup.indirectTier)) {
                indirectCacheRW[sdfIndirectRadianceAddress(index, ray, writeGeneration) + source] = packed;
            }
        }
        sdfIndirectShadedDirections[ray] = direction;
        sdfIndirectShadedKinds[ray] = kind;
    }
    GroupMemoryBarrierWithGroupSync();
    uint2 texel = uint2(lane % SdfIndirectIrradianceEdge, lane / SdfIndirectIrradianceEdge);
    float3 normal = sdfIndirectIrradianceDirection(texel);
    SdfIndirectSources irradiance = (SdfIndirectSources)0;
    float total = 0.0;
    [loop] for (uint ray = 0u; ray < rays; ray++) {
        if (sdfIndirectShadedKinds[ray] == SdfIndirectKindUnresolved) { continue; }
        float weight = max(dot(normal, sdfIndirectShadedDirections[ray]), 0.0);
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            irradiance.values[source] += sdfIndirectShaded[ray].values[source] * weight;
        }
        total += weight;
    }
    uint address = sdfIndirectIrradianceAddress(index, writeGeneration) + lane * SdfIndirectRadianceWords;
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        indirectCacheRW[address + source] = sdfIndirectPackRadiance(total > 0.0 ? irradiance.values[source] / total : 0.0);
    }
    DeviceMemoryBarrierWithGroupSync();
    if (lane == 0u) { indirectCacheRW[sdfIndirectPublicationAddress(index, writeGeneration)] = writePublication; }
    puckCountDetail(detail, 0u, 1u, 0u, sdfIndirectHashes, sdfIndirectLoads);
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps = 0u; }
    puckCountWork(sdfWorkSteps, passGroup.workCounterRowDetail == 0u ? 1u : 0u);
}
#endif
