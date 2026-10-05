// Resource-only continuation fold shared by finite shading and the High near-field receiver.
#ifndef SDF_INDIRECT_CONTINUATION_HLSLI
#define SDF_INDIRECT_CONTINUATION_HLSLI
#include "sdf-indirect-irradiance.hlsli"

bool sdfIndirectReadContinuation(float3 position, float3 direction, uint terminal,
    uint generation, uint publication, out SdfIndirectSources result) {
    result = (SdfIndirectSources)0;
    uint level = (terminal >> SdfIndirectProofLevelShift) & 3u;
    uint mask = (terminal >> SdfIndirectProofMaskShift) & 255u;
    float3 scaled = position / sdfIndirectSpacing(passGroup.indirectTier, level);
    int3 cell = int3(floor(scaled));
    float total = 0.0;
    // Keep one directional-search body; corners still accumulate in ascending order with every read counted.
    [loop] for (uint corner = 0u; corner < 8u; corner++) {
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
        uint first = sdfIndirectLoad(address);
        if (first == SdfIndirectUnresolvedSample) { continue; }
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
            uint packed = source == 0u ? first : sdfIndirectLoad(address + source);
            result.values[source] += sdfIndirectUnpackRadiance(packed) * weight;
        }
        total += weight;
    }
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) {
        result.values[source] = total > 0.0 ? result.values[source] / total : 0.0;
    }
    return total > 0.0;
}

#endif
