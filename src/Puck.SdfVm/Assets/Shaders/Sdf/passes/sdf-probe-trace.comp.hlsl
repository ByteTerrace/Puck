#include "../isa/sdf-probe-trace.interface.hlsli"
#define SDF_PROBE_RESULT_STRIDE SDF_PROBE_HIT_WORDS
#include "../shade/sdf-probe-trace.hlsli"

[numthreads(SDF_PROBE_GROUP_SIZE, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex) {
    uint probes, stride;
    sdfProbeOrigins.GetDimensions(probes, stride);
    uint probe = id.x / SDF_PROBE_HIT_COUNT;
    if (probe >= probes) { return; }
    uint texel = id.x % SDF_PROBE_HIT_COUNT;
    float4 origin = sdfProbeOrigins[probe];
    uint2 identity;
    uint2 hit = sdfProbeHit(origin.xyz, sdfProbeDirections[texel].xyz, origin.w, lane, identity);
    sdfProbeHitsRW[2 * id.x] = hit.x;
    sdfProbeHitsRW[2 * id.x + 1] = hit.y;
    uint identities;
    sdfProbeIdentitiesRW.GetDimensions(identities, stride);
    if (id.x < identities) { sdfProbeIdentitiesRW[id.x] = identity; }
}
