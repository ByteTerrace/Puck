#include "../isa/sdf-probe-relight.interface.hlsli"
#define SDF_PROBE_RESULT_STRIDE SDF_PROBE_RELIGHT_RESULT_WORDS
#include "../shade/sdf-probe-lighting.hlsli"

groupshared float3 probeVarying[6 * SDF_PROBE_GROUP_SIZE];
groupshared float3 probeLocal[6 * SDF_PROBE_GROUP_SIZE];
groupshared uint probeSet[SDF_MESH_IRRADIANCE_LIGHT_WORDS];
groupshared uint probeCleared[SDF_MESH_IRRADIANCE_LIGHT_WORDS];
// KEEP IN SYNC with SdfMeshIrradiance.Shade and Relight. CPU is the diagnostic/reference path.
// One group is one slice of one listed probe: its SDF_PROBE_GROUP_SIZE hit texels, one a lane, and every
// SDF_PROBE_RELIGHT_SLICES-th group of its light visibility rays. The slice writes its partial sums behind the batch's
// results and fault word; sdf-probe-combine sums a probe's slices into its output and record.
[numthreads(SDF_PROBE_GROUP_SIZE, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint slice = group.x % SDF_PROBE_RELIGHT_SLICES;
    uint probe = sdfProbeWork[3 + group.x / SDF_PROBE_RELIGHT_SLICES];
    uint input = probe * SDF_PROBE_RELIGHT_INPUT_WORDS;
    uint hits = sdfProbeInput[input + 3] * SDF_PROBE_CACHE_WORDS + 2;
    uint flags = sdfProbeInput[input];
    bool valid = (flags & 1u) != 0;
    bool reuse = (flags & 2u) != 0;
    float3 origin = sdfProbeOrigins[probe].xyz;
    float4 counts = sdfProbeLighting[0];
    uint lightBase = SDF_PROBE_RELIGHT_HEADER_ROWS + (uint)counts.x * SDF_PROBE_RELIGHT_MATERIAL_ROWS;
    uint lightCount = (uint)(counts.y + counts.z);
    sdfProbeLightingSetup();
    [unroll] for (uint axis = 0; axis < 6; axis++) {
        probeVarying[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
        probeLocal[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
    }
    for (uint w = lane; w < SDF_MESH_IRRADIANCE_LIGHT_WORDS; w += SDF_PROBE_GROUP_SIZE) {
        probeSet[w] = 0;
        probeCleared[w] = 0;
    }
    GroupMemoryBarrierWithGroupSync();
    uint texel = slice * SDF_PROBE_GROUP_SIZE + lane;
    float distance = (valid && texel < SDF_PROBE_HIT_COUNT) ? asfloat(sdfProbeCache[hits + 2 * texel]) : -1;
    if (isfinite(distance) && distance >= 0) {
        uint packed = sdfProbeCache[hits + 1 + 2 * texel];
        float3 normal = sdfOctDecode(float2((int)(packed << 8) >> 24, (int)packed >> 24) / SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
        float4 ray = sdfProbeDirections[texel];
        float3 local;
        float3 varying = sdfProbeShade(origin + ray.xyz * distance, normal, packed & 65535u, reuse, lane, local);
        [unroll] for (uint axis = 0; axis < 6; axis++) {
            float cosine = max(0, ray[axis / 2] * ((axis & 1) == 0 ? 1 : -1));
            probeVarying[axis * SDF_PROBE_GROUP_SIZE + lane] = varying * (ray.w * cosine);
            probeLocal[axis * SDF_PROBE_GROUP_SIZE + lane] = local * (ray.w * cosine);
        }
    }
    for (uint light = slice * SDF_PROBE_GROUP_SIZE + lane; valid && light < lightCount; light += SDF_PROBE_RELIGHT_SLICES * SDF_PROBE_GROUP_SIZE) {
        uint row = lightBase + light * SDF_PROBE_RELIGHT_LIGHT_ROWS;
        float4 position = sdfProbeLighting[row];
        float4 mode = sdfProbeLighting[row + 3];
        uint index = (uint)mode.y;
        if (index >= 32 * SDF_MESH_IRRADIANCE_LIGHT_WORDS || (reuse && mode.w != 0)) { continue; }
        bool visible = true;
        float3 direction = position.xyz;
        float reach = counts.w;
        if (mode.w != 0) {
            float3 toward = position.xyz - origin;
            float length = sqrt(dot(toward, toward));
            visible = length < position.w;
            direction = toward / max(length, 0.0001);
            reach = length - SDF_MESH_IRRADIANCE_LIGHT_BIAS;
        }
        if (visible && mode.z != 0 && reach > 0) { visible = probeClear(origin, direction, reach, lane); }
        uint bit = 1u << (index & 31);
        if (visible) { InterlockedOr(probeSet[index >> 5], bit); }
        else { InterlockedOr(probeCleared[index >> 5], bit); }
    }
    GroupMemoryBarrierWithGroupSync();
    uint count, stride;
    sdfProbeOrigins.GetDimensions(count, stride);
    uint partial = count * SDF_PROBE_RELIGHT_RESULT_WORDS + 1 + (probe * SDF_PROBE_RELIGHT_SLICES + slice) * SDF_PROBE_RELIGHT_PARTIAL_WORDS;
    if (lane < 6) {
        float3 varying = 0, local = 0;
        for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) {
            varying += probeVarying[lane * SDF_PROBE_GROUP_SIZE + i];
            local += probeLocal[lane * SDF_PROBE_GROUP_SIZE + i];
        }
        [unroll] for (uint c = 0; c < 3; c++) {
            sdfProbeHitsRW[partial + 3 * lane + c] = asuint(varying[c]);
            sdfProbeHitsRW[partial + 18 + 3 * lane + c] = asuint(local[c]);
        }
    }
    for (uint v = lane; v < SDF_MESH_IRRADIANCE_LIGHT_WORDS; v += SDF_PROBE_GROUP_SIZE) {
        sdfProbeHitsRW[partial + 36 + v] = probeSet[v];
        sdfProbeHitsRW[partial + 36 + SDF_MESH_IRRADIANCE_LIGHT_WORDS + v] = probeCleared[v];
    }
}
