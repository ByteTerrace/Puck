#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../shade/sdf-probe-trace.hlsli"
#include "../shade/sdf-sky-environment.hlsli"

struct ProbePass {
    uint meshIrradianceVolume;
    float4 meshIrradianceLevels[3];
    float4 meshIrradianceSpacings[3];
    uint indirectSources;
    float4 indirectSourceGains;
};
static ProbePass passGroup;
float3 sdfSkyPhysicalIrradiance(float3 normal) {
    float3 coefficients[9];
    [unroll] for (uint k = 0; k < 9; k++) { coefficients[k] = sdfProbeLighting[11 + k].xyz; }
    return sdfSkyEnvironmentIrradiance(normal, coefficients);
}
#include "../shade/sdf-mesh-irradiance.hlsli"

groupshared float3 probeVarying[6 * SDF_PROBE_GROUP_SIZE];
groupshared float3 probeLocal[6 * SDF_PROBE_GROUP_SIZE];
groupshared uint probeVisible[SDF_MESH_IRRADIANCE_LIGHT_WORDS];
bool probeClear(float3 positionWorld, float3 direction, float reach, uint lane) {
    float distance; float3 normal; uint material; uint2 identity;
    return !sdfProbeIntersect(positionWorld, direction, reach, lane, distance, normal, material, identity);
}
// KEEP IN SYNC with SdfMeshIrradiance.Shade and Relight. CPU is the diagnostic/reference path.
[numthreads(SDF_PROBE_GROUP_SIZE, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint probe = group.x;
    uint input = probe * SDF_PROBE_RELIGHT_INPUT_WORDS;
    uint output = probe * SDF_PROBE_RELIGHT_OUTPUT_WORDS;
    uint flags = sdfProbeInput[input];
    bool valid = (flags & 1u) != 0;
    bool reuse = (flags & 2u) != 0;
    float3 origin = sdfProbeOrigins[probe].xyz;
    float4 counts = sdfProbeLighting[0];
    float4 sources = sdfProbeLighting[1];
    float4 gains = sdfProbeLighting[2];
    uint lightBase = SDF_PROBE_RELIGHT_HEADER_ROWS + (uint)counts.x * SDF_PROBE_RELIGHT_MATERIAL_ROWS;
    uint lightCount = (uint)(counts.y + counts.z);
    passGroup.meshIrradianceVolume = 1;
    passGroup.indirectSources = sources.w != 0 ? SDF_INDIRECT_SOURCES_SKY : 0;
    passGroup.indirectSourceGains = gains;
    [unroll] for (uint level = 0; level < 3; level++) {
        passGroup.meshIrradianceLevels[level] = sdfProbeLighting[3 + 2 * level];
        passGroup.meshIrradianceSpacings[level] = sdfProbeLighting[4 + 2 * level];
    }
    [unroll] for (uint axis = 0; axis < 6; axis++) {
        probeVarying[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
        probeLocal[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
    }
    for (uint w = lane; w < SDF_MESH_IRRADIANCE_LIGHT_WORDS; w += SDF_PROBE_GROUP_SIZE) {
        probeVisible[w] = reuse ? sdfProbeInput[input + 1 + SDF_PROBE_HIT_WORDS + 36 + w] : 0;
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint texel = lane; valid && texel < SDF_PROBE_HIT_COUNT; texel += SDF_PROBE_GROUP_SIZE) {
        float distance = asfloat(sdfProbeInput[input + 1 + 2 * texel]);
        if (!isfinite(distance) || distance < 0) { continue; }
        uint packed = sdfProbeInput[input + 2 + 2 * texel];
        uint material = min(packed & 65535u, (uint)counts.x - 1);
        float3 normal = sdfOctDecode(float2((int)(packed << 8) >> 24, (int)packed >> 24) / SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
        float4 surface = sdfProbeLighting[SDF_PROBE_RELIGHT_HEADER_ROWS + material * SDF_PROBE_RELIGHT_MATERIAL_ROWS];
        float4 ray = sdfProbeDirections[texel];
        float3 positionWorld = origin + ray.xyz * distance;
        float3 lifted = positionWorld + normal * 0.001;
        float3 varying = 0, local = 0;
        if (sources.x != 0 && any(surface.xyz != 0)) {
            for (uint light = 0; light < (reuse ? (uint)counts.y : lightCount); light++) {
                uint row = lightBase + light * SDF_PROBE_RELIGHT_LIGHT_ROWS;
                float4 position = sdfProbeLighting[row];
                float4 color = sdfProbeLighting[row + 1];
                float4 cone = sdfProbeLighting[row + 2];
                float4 mode = sdfProbeLighting[row + 3];
                float3 direction = position.xyz;
                float reach = counts.w, attenuation = 1;
                if (mode.w != 0) {
                    float3 toward = position.xyz - positionWorld;
                    float lengthSquared = dot(toward, toward);
                    if (lengthSquared >= position.w * position.w) { continue; }
                    float length = sqrt(lengthSquared);
                    direction = toward / max(length, 0.0001);
                    float spot = saturate(dot(-direction, cone.xyz) + cone.w);
                    float ratio = length / position.w;
                    float window = saturate(1 - ratio * ratio * ratio * ratio);
                    float near = max(length, SDF_LIGHT_MINIMUM_DISTANCE);
                    attenuation = window * window / (near * near) * spot * spot;
                    reach = length - 0.02;
                }
                float lambert = max(0, (dot(normal, direction) + surface.w) / (1 + surface.w));
                if (lambert <= 0 || attenuation <= 0) { continue; }
                if (mode.z != 0 && reach > 0 && !probeClear(lifted, direction, reach, lane)) { continue; }
                float3 value = surface.xyz * color.xyz * (color.w * lambert * mode.x * attenuation * gains.x);
                if (mode.w == 0) { varying += value; } else { local += value; }
            }
        }
        if (!reuse && sources.y != 0) {
            local += sdfProbeLighting[SDF_PROBE_RELIGHT_HEADER_ROWS + material * SDF_PROBE_RELIGHT_MATERIAL_ROWS + 1].xyz * gains.y;
        }
        if (sources.z != 0 && any(surface.xyz != 0)) {
            varying += surface.xyz * sdfMeshIndirect(sdfMeshProbesAt(positionWorld, normal), normal) * gains.z;
        }
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
        local = reuse ? asfloat(uint3(sdfProbeInput[input + 1 + SDF_PROBE_HIT_WORDS + 18 + 3 * lane],
            sdfProbeInput[input + 1 + SDF_PROBE_HIT_WORDS + 19 + 3 * lane], sdfProbeInput[input + 1 + SDF_PROBE_HIT_WORDS + 20 + 3 * lane])) : local / total;
        varying = varying / total + local;
        [unroll] for (uint c = 0; c < 3; c++) {
            sdfProbeHitsRW[output + 3 * lane + c] = asuint(varying[c]);
            sdfProbeHitsRW[output + 18 + 3 * lane + c] = asuint(local[c]);
        }
    }
    for (uint w = lane; w < SDF_MESH_IRRADIANCE_LIGHT_WORDS; w += SDF_PROBE_GROUP_SIZE) { sdfProbeHitsRW[output + 36 + w] = probeVisible[w]; }
}
