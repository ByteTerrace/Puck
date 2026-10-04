// One finite sweep's lighting of immutable transport records. The caller binds one pinned lighting snapshot and
// visits coarser levels before finer readers; only a complete submitted sweep becomes visible to view receivers.
#ifndef SDF_INDIRECT_SHADE_HLSLI
#define SDF_INDIRECT_SHADE_HLSLI
#include "sdf-indirect-diffuse.hlsli"
#include "sdf-indirect-continuation.hlsli"
#include "sdf-indirect-sky.hlsli"

// 256 rays * (five float3 sources, one direction and one terminal) = 19,456 bytes, beside the VM's gather mask.
groupshared SdfIndirectSources sdfIndirectShaded[SdfIndirectMaximumRaysPerProbe];
groupshared float3 sdfIndirectShadedDirections[SdfIndirectMaximumRaysPerProbe];
groupshared uint sdfIndirectShadedKinds[SdfIndirectMaximumRaysPerProbe];

SdfIndirectSources sdfIndirectShadeHit(float3 surfacePoint, float3 direction, uint4 hit, uint readGeneration,
    uint readPublication, float feedbackGain) {
    SdfShadeSurface surface = (SdfShadeSurface)0;
    surface.position = surfacePoint;
    surface.normal = sdfIndirectUnpackNormal(hit.y);
    surface.rayDirection = direction;
    surface.material = sdfMaterialLoad((int)hit.z);
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = 1.0;
    surface.incomingVisibility = 1.0;
    [loop] for (uint slot = 0u; slot < passGroup.shadowSlotCount; slot++) {
        surface.shadowVisibility[slot] = sdfIndirectDiffuseVisibility(passGroup.shadowSlots[slot], surfacePoint, surface.normal);
    }
    [loop] for (uint fade = 0u; fade < min(passGroup.shadowFadeCount, (uint)SDF_SHADOW_FADE_SLOTS); fade++) {
        surface.incomingVisibility[fade] = sdfIndirectDiffuseVisibility(sdfShadowHandoffs[fade].Incoming, surfacePoint, surface.normal);
    }
    float attenuation;
    SdfIndirectSources result = sdfIndirectDiffuse(surface, attenuation);
    float3 reflected = surface.material.albedo * (1.0 - surface.material.metal) * surface.material.bleed;
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
    SdfIndirectSources result;
    sdfIndirectReadContinuation(position, direction, terminal, generation, publication, result);
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
        } else {
            result.values[SdfIndirectSourceSky] = sdfIndirectSky(kind, direction, passGroup.indirectSources);
        }
        // Unresolved rays contribute no source and never enter the cosine denominator.
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            uint packed = sdfIndirectPackRadiance((passGroup.indirectSources & (1u << source)) != 0u ? result.values[source] : 0.0);
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
