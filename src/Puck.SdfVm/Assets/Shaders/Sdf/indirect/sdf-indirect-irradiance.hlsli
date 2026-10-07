// The finite solve and views share the same stored irradiance and receiver weights. No field query occurs here.
#ifndef SDF_INDIRECT_IRRADIANCE_HLSLI
#define SDF_INDIRECT_IRRADIANCE_HLSLI
#include "sdf-indirect-cache.hlsli"
#include "sdf-indirect-radiance.hlsli"

struct SdfIndirectSources { float3 values[SdfIndirectSourceCount]; };
// Finite R11G11B10 packing cannot produce this NaN word. All five words carry it for an unresolved sample;
// readers test the first word before decoding. Zero remains a known black sample with its full weight.
static const uint SdfIndirectUnresolvedSample = 0xffffffffu;
#ifdef SDF_VIEWS_PASS
static bool sdfIndirectPickActive = false;
static uint sdfIndirectPickStores = 0u;
void sdfIndirectPickStore(uint word, uint value) {
    uint count, stride;
    indirectPickRW.GetDimensions(count, stride);
    if (word >= min(count, SdfIndirectPickWords)) { return; }
    indirectPickRW[word] = value;
    sdfIndirectPickStores++;
}
void sdfIndirectPickClearCorners() {
    [unroll] for (uint corner = 0u; corner < 8u; corner++) {
        uint word = 16u + 4u * corner;
        sdfIndirectPickStore(word, 0xffffffffu);
        sdfIndirectPickStore(word + 1u, SdfIndirectClassInactive);
        sdfIndirectPickStore(word + 2u, 0u);
        sdfIndirectPickStore(word + 3u, 0u);
    }
}
#endif
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
    return probe < sdfIndirectProbeCapacity(passGroup.indirectTier) && generation < SdfIndirectLightingGenerations &&
        publication != 0u && sdfIndirectLoad(sdfIndirectPublicationAddress(probe, generation)) == publication;
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
bool sdfIndirectProbeIrradiance(uint probe, uint generation, float3 normal, out SdfIndirectSources value) {
    value = (SdfIndirectSources)0;
    if (probe >= sdfIndirectProbeCapacity(passGroup.indirectTier) || generation >= SdfIndirectLightingGenerations ||
        any(!isfinite(normal)) || dot(normal, normal) <= 0.0) { return false; }
    float2 uv = sdfOctEncode(normal.xzy) * 0.5 + 0.5;
    float2 position = uv * (float)(SdfIndirectIrradianceEdge - 2u) + 0.5;
    if (any(!isfinite(position))) { return false; }
    uint2 baseTexel = (uint2)clamp(floor(position), 0.0, (float)SdfIndirectIrradianceEdge - 2.0);
    float2 fraction = frac(position);
    uint address = sdfIndirectIrradianceAddress(probe, generation);
    float total = 0.0;
    [unroll] for (uint tap = 0u; tap < 4u; tap++) {
        uint2 corner = uint2(tap & 1u, tap >> 1u);
        uint2 texel = baseTexel + corner;
        float weight = lerp(1.0 - fraction.x, fraction.x, (float)corner.x)
            * lerp(1.0 - fraction.y, fraction.y, (float)corner.y);
        if (weight <= 0.0) { continue; }
        uint word = address + (texel.y * SdfIndirectIrradianceEdge + texel.x) * SdfIndirectRadianceWords;
        uint first = sdfIndirectLoad(word);
        if (first == SdfIndirectUnresolvedSample) { continue; }
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            uint packed = source == 0u ? first : sdfIndirectLoad(word + source);
            value.values[source] += sdfIndirectUnpackRadiance(packed) * weight;
        }
        total += weight;
    }
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        value.values[source] = total > 0.0 ? value.values[source] / total : 0.0;
    }
    return total > 0.0;
}
float sdfIndirectCornerWeight(float3 fraction, uint corner) {
    float3 weight = lerp(1.0 - fraction, fraction, float3(sdfIndirectCorner(corner)));
    return weight.x * weight.y * weight.z;
}

// A proof mask belongs to this exact launched position's cell. Geometry-only proof construction is outside shading.
bool sdfIndirectIrradianceAt(float3 surfacePoint, float3 launched, float3 normal, uint level, uint mask,
    uint generation, uint publication, out SdfIndirectSources irradiance) {
    irradiance = (SdfIndirectSources)0;
    if (level >= sdfIndirectLevelCount() || generation >= SdfIndirectLightingGenerations) { return false; }
    int3 cell;
    if (!sdfIndirectCellAt(launched, sdfIndirectSpacing(passGroup.indirectTier, level), cell)) { return false; }
    float3 scaled = launched / sdfIndirectSpacing(passGroup.indirectTier, level);
    float3 fraction = frac(scaled);
    if (!sdfIndirectCellCurrent(sdfIndirectProbeIndex(cell, level))) { return false; }
    float total = 0.0;
#ifdef SDF_VIEWS_PASS
    if (sdfIndirectPickActive) { sdfIndirectPickClearCorners(); }
#endif
    // Keep one guarded sampling body while retaining ascending corner accumulation.
    [loop] for (uint corner = 0u; corner < 8u; corner++) {
        bool included = (mask & (1u << corner)) != 0u;
#ifdef SDF_VIEWS_PASS
        if (!included && !sdfIndirectPickActive) { continue; }
#else
        if (!included) { continue; }
#endif
        int index = sdfIndirectProbeIndex(cell + sdfIndirectCorner(corner), level);
        uint stamp = index < 0 ? 0u : sdfIndirectLoad(sdfIndirectPublicationAddress((uint)index, generation));
        SdfIndirectPlacement probe = sdfIndirectReadProbe(index);
#ifdef SDF_VIEWS_PASS
        if (sdfIndirectPickActive) {
            uint word = 16u + 4u * corner;
            sdfIndirectPickStore(word, (uint)index);
            sdfIndirectPickStore(word + 1u, probe.classification);
            sdfIndirectPickStore(word + 3u, stamp);
        }
#endif
        if (!included || index < 0 || publication == 0u || stamp != publication) { continue; }
        if (probe.classification != SdfIndirectClassActive && probe.classification != SdfIndirectClassRelocated) { continue; }
        float3 toward = probe.position - surfacePoint;
        float magnitude = length(toward);
        float facing = (magnitude > 0.0 ? max(dot(normal, toward / magnitude), 0.0) : 0.0) + 0.01;
        float weight = sdfIndirectCornerWeight(fraction, corner) * facing;
        if (weight <= 0.0) { continue; }
        SdfIndirectSources candidate;
        if (!sdfIndirectProbeIrradiance((uint)index, generation, normal, candidate)) { continue; }
#ifdef SDF_VIEWS_PASS
        if (sdfIndirectPickActive) { sdfIndirectPickStore(18u + 4u * corner, asuint(weight)); }
#endif
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            irradiance.values[source] += candidate.values[source] * weight;
        }
        total += weight;
    }
#ifdef SDF_VIEWS_PASS
    if (sdfIndirectPickActive) {
        uint pickCount, pickStride;
        indirectPickRW.GetDimensions(pickCount, pickStride);
        [unroll] for (uint corner = 0u; corner < 8u; corner++) {
            uint word = 18u + 4u * corner;
            if (word >= pickCount) { continue; }
            float weight = asfloat(indirectPickRW[word]);
            sdfIndirectPickStore(word, asuint(total > 0.0 ? weight / total : 0.0));
        }
    }
#endif
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        irradiance.values[source] = total > 0.0 ? irradiance.values[source] / total : 0.0;
    }
    return total > 0.0;
}
#endif
