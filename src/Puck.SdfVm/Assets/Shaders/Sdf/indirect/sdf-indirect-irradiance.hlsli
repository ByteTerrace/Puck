// The finite solve and views share the same stored irradiance and receiver weights. No field query occurs here.
#ifndef SDF_INDIRECT_IRRADIANCE_HLSLI
#define SDF_INDIRECT_IRRADIANCE_HLSLI
#include "sdf-indirect-cache.hlsli"
#include "sdf-indirect-radiance.hlsli"

struct SdfIndirectSources { float3 values[SdfIndirectSourceCount]; };
float3 sdfIndirectSourceTotal(SdfIndirectSources sources) {
    float3 total = 0.0;
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) { total += sources.values[source]; }
    return total;
}

uint sdfIndirectPublicationAddress(uint probe, uint generation) {
    return sdfIndirectPublicationWordOffset(passGroup.indirectTier)
        + generation * sdfIndirectProbeCapacity(passGroup.indirectTier) + probe;
}
bool sdfIndirectPublished(uint probe, uint generation, uint publication) {
    return publication != 0u && sdfIndirectLoad(sdfIndirectPublicationAddress(probe, generation)) == publication;
}
uint sdfIndirectIrradianceAddress(uint probe, uint generation) {
    return sdfIndirectIrradianceWordOffset(passGroup.indirectTier)
        + generation * sdfIndirectIrradianceGenerationWords(passGroup.indirectTier)
        + probe * SdfIndirectIrradianceTexels * SdfIndirectRadianceWords;
}
uint sdfIndirectRadianceAddress(uint probe, uint ray, uint generation) {
    return sdfIndirectRadianceWordOffset(passGroup.indirectTier)
        + generation * sdfIndirectRadianceGenerationWords(passGroup.indirectTier)
        + ((probe - sdfIndirectRadianceProbeOffset(passGroup.indirectTier)) * sdfIndirectRaysPerProbe(passGroup.indirectTier) + ray) * SdfIndirectRadianceWords;
}

// IrradianceLattice.BorderSource's interior source for the stored one-texel octahedral border.
uint2 sdfIndirectBorderSource(uint2 texel) {
    const uint Last = SdfIndirectIrradianceEdge - 2u;
    bool edgeX = texel.x == 0u || texel.x == Last + 1u;
    bool edgeY = texel.y == 0u || texel.y == Last + 1u;
    if (edgeX && edgeY) { return uint2(texel.x == 0u ? Last : 1u, texel.y == 0u ? Last : 1u); }
    if (edgeX) { return uint2(texel.x == 0u ? 1u : Last, Last + 1u - texel.y); }
    if (edgeY) { return uint2(Last + 1u - texel.x, texel.y == 0u ? 1u : Last); }
    return texel;
}
float3 sdfIndirectIrradianceDirection(uint2 texel) {
    uint2 interior = sdfIndirectBorderSource(texel);
    float2 uv = (float2(interior) - 0.5) / (float)(SdfIndirectIrradianceEdge - 2u);
    float3 direction = sdfOctDecode(uv * 2.0 - 1.0);
    return direction.xzy;
}
float3 sdfIndirectProbeIrradiance(uint probe, uint generation, float3 normal, uint source) {
    float2 uv = sdfOctEncode(normal.xzy) * 0.5 + 0.5;
    float2 position = uv * (float)(SdfIndirectIrradianceEdge - 2u) + 0.5;
    uint2 baseTexel = (uint2)floor(position);
    float2 fraction = frac(position);
    uint address = sdfIndirectIrradianceAddress(probe, generation);
    float3 value = 0.0;
    [unroll] for (uint tap = 0u; tap < 4u; tap++) {
        uint2 corner = uint2(tap & 1u, tap >> 1u);
        uint2 texel = baseTexel + corner;
        float weight = lerp(1.0 - fraction.x, fraction.x, (float)corner.x)
            * lerp(1.0 - fraction.y, fraction.y, (float)corner.y);
        uint word = address + (texel.y * SdfIndirectIrradianceEdge + texel.x) * SdfIndirectRadianceWords + source;
        value += sdfIndirectUnpackRadiance(sdfIndirectLoad(word)) * weight;
    }
    return value;
}
float sdfIndirectCornerWeight(float3 fraction, uint corner) {
    float3 weight = lerp(1.0 - fraction, fraction, float3(sdfIndirectCorner(corner)));
    return weight.x * weight.y * weight.z;
}

// A proof mask belongs to this exact launched position's cell. Geometry-only proof construction is outside shading.
bool sdfIndirectIrradianceAt(float3 surfacePoint, float3 launched, float3 normal, uint level, uint mask,
    uint generation, uint publication, out SdfIndirectSources irradiance) {
    float3 scaled = launched / sdfIndirectSpacing(passGroup.indirectTier, level);
    int3 cell = int3(floor(scaled));
    float3 fraction = frac(scaled);
    irradiance = (SdfIndirectSources)0;
    float total = 0.0;
    [unroll] for (uint corner = 0u; corner < 8u; corner++) {
        if ((mask & (1u << corner)) == 0u) { continue; }
        int index = sdfIndirectProbeIndex(cell + sdfIndirectCorner(corner), level);
        if (index < 0 || !sdfIndirectPublished((uint)index, generation, publication)) { continue; }
        SdfIndirectPlacement probe = sdfIndirectReadProbe(index);
        if (probe.classification != SdfIndirectClassActive && probe.classification != SdfIndirectClassRelocated) { continue; }
        float3 toward = probe.position - surfacePoint;
        float magnitude = length(toward);
        float facing = (magnitude > 0.0 ? max(dot(normal, toward / magnitude), 0.0) : 0.0) + 0.01;
        float weight = sdfIndirectCornerWeight(fraction, corner) * facing;
        if (weight <= 0.0) { continue; }
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            irradiance.values[source] += sdfIndirectProbeIrradiance((uint)index, generation, normal, source) * weight;
        }
        total += weight;
    }
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        irradiance.values[source] = total > 0.0 ? irradiance.values[source] / total : 0.0;
    }
    return total > 0.0;
}
#endif
