#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../shade/sdf-probe-lighting.hlsli"

groupshared float3 probeVarying[6 * SDF_PROBE_GROUP_SIZE];
groupshared float3 probeLocal[6 * SDF_PROBE_GROUP_SIZE];
groupshared uint probeVisible[SDF_MESH_IRRADIANCE_LIGHT_WORDS];
// KEEP IN SYNC with SdfMeshIrradiance.Shade and Relight. CPU is the diagnostic/reference path.
[numthreads(SDF_PROBE_GROUP_SIZE, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint probe = sdfProbeWork[3 + group.x];
    uint input = probe * SDF_PROBE_RELIGHT_INPUT_WORDS;
    uint output = probe * SDF_PROBE_RELIGHT_RESULT_WORDS;
    uint hits = sdfProbeInput[input + 3] * SDF_PROBE_CACHE_WORDS + 2;
    uint flags = sdfProbeInput[input];
    bool valid = (flags & 1u) != 0;
    bool reuse = (flags & 2u) != 0;
    float3 origin = sdfProbeOrigins[probe].xyz;
    float4 counts = sdfProbeLighting[0];
    float4 sources = sdfProbeLighting[1];
    float4 gains = sdfProbeLighting[2];
    uint lightBase = SDF_PROBE_RELIGHT_HEADER_ROWS + (uint)counts.x * SDF_PROBE_RELIGHT_MATERIAL_ROWS;
    uint lightCount = (uint)(counts.y + counts.z);
    sdfProbeLightingSetup();
    [unroll] for (uint axis = 0; axis < 6; axis++) {
        probeVarying[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
        probeLocal[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
    }
    for (uint w = lane; w < SDF_MESH_IRRADIANCE_LIGHT_WORDS; w += SDF_PROBE_GROUP_SIZE) {
        probeVisible[w] = reuse ? sdfProbeInput[input + 5 + 36 + w] : 0;
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint texel = lane; valid && texel < SDF_PROBE_HIT_COUNT; texel += SDF_PROBE_GROUP_SIZE) {
        float distance = asfloat(sdfProbeCache[hits + 2 * texel]);
        if (!isfinite(distance) || distance < 0) { continue; }
        uint packed = sdfProbeCache[hits + 1 + 2 * texel];
        float3 normal = sdfOctDecode(float2((int)(packed << 8) >> 24, (int)packed >> 24) / SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
        float4 ray = sdfProbeDirections[texel];
        float3 local;
        float3 varying = sdfProbeShade(origin + ray.xyz * distance, normal, packed & 65535u, reuse, lane, local);
        [unroll] for (uint axis = 0; axis < 6; axis++) {
            float cosine = max(0, ray[axis / 2] * ((axis & 1) == 0 ? 1 : -1));
            probeVarying[axis * SDF_PROBE_GROUP_SIZE + lane] += varying * (ray.w * cosine);
            probeLocal[axis * SDF_PROBE_GROUP_SIZE + lane] += local * (ray.w * cosine);
        }
    }
    for (uint light = lane; valid && light < lightCount; light += SDF_PROBE_GROUP_SIZE) {
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
            reach = length - 0.02;
        }
        if (visible && mode.z != 0 && reach > 0) { visible = probeClear(origin, direction, reach, lane); }
        uint bit = 1u << (index & 31);
        if (visible) { InterlockedOr(probeVisible[index >> 5], bit); }
        else { InterlockedAnd(probeVisible[index >> 5], ~bit); }
    }
    GroupMemoryBarrierWithGroupSync();
    if (lane < 6) {
        float3 varying = 0, local = 0;
        for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) {
            varying += probeVarying[lane * SDF_PROBE_GROUP_SIZE + i];
            local += probeLocal[lane * SDF_PROBE_GROUP_SIZE + i];
        }
        float total = sdfProbeLighting[9 + lane / 4][lane % 4];
        local = reuse ? asfloat(uint3(sdfProbeInput[input + 5 + 18 + 3 * lane],
            sdfProbeInput[input + 5 + 19 + 3 * lane], sdfProbeInput[input + 5 + 20 + 3 * lane])) : local / total;
        varying = varying / total + local;
        [unroll] for (uint c = 0; c < 3; c++) {
            sdfProbeHitsRW[output + 3 * lane + c] = asuint(varying[c]);
            sdfProbeHitsRW[output + 18 + 3 * lane + c] = asuint(local[c]);
        }
    }
    for (uint w = lane; w < SDF_MESH_IRRADIANCE_LIGHT_WORDS; w += SDF_PROBE_GROUP_SIZE) { sdfProbeHitsRW[output + 36 + w] = probeVisible[w]; }
    DeviceMemoryBarrierWithGroupSync();
    uint record = output + SDF_PROBE_RELIGHT_OUTPUT_WORDS;
    if (lane < 9) {
        sdfProbeHitsRW[record + 4 + lane] = f32tof16(asfloat(sdfProbeHitsRW[output + 2 * lane])) |
            (f32tof16(asfloat(sdfProbeHitsRW[output + 2 * lane + 1])) << 16);
    }
    for (uint w = lane; w < SDF_MESH_IRRADIANCE_LIGHT_WORDS; w += SDF_PROBE_GROUP_SIZE) {
        sdfProbeHitsRW[record + SDF_MESH_IRRADIANCE_LIGHTS_WORD + w] = probeVisible[w];
    }
}
