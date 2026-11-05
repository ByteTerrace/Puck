#include "../isa/sdf-probe-relight.interface.hlsli"
#define SDF_PROBE_RESULT_STRIDE SDF_PROBE_METER_OUTPUT_WORDS
#include "../shade/sdf-probe-lighting.hlsli"

groupshared float meterAxis[6 * SDF_PROBE_GROUP_SIZE];
groupshared float meterSky[6 * SDF_PROBE_GROUP_SIZE];
groupshared float meterWeight[SDF_PROBE_GROUP_SIZE];
groupshared uint meterBack[SDF_PROBE_GROUP_SIZE];
groupshared uint meterTrace;
float3 meterNormal(uint axis) {
    float3 normal = 0;
    normal[axis / 2] = (axis & 1) == 0 ? 1 : -1;
    return normal;
}
float meterLuminance(float3 value) { return dot(value, SDF_LUMINANCE); }
float meterSkyLuminance(uint axis, float sky) {
    return sdfProbeLighting[1].w != 0 ? meterLuminance(sdfSkyPhysicalIrradiance(meterNormal(axis))) * (sky * sdfProbeLighting[2].w / 3.141592653589793) : 0;
}
// KEEP IN SYNC with TryMeter: eight unobstructed fine probes, otherwise a sample at the camera itself.
[numthreads(SDF_PROBE_GROUP_SIZE, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    sdfProbeLightingSetup();
    float4 origin = sdfProbeOrigins[group.x];
    uint cache = group.x * (1 + 2 * SDF_PROBE_HIT_COUNT);
    if (lane == 0 && origin.w != 0) { sdfProbeCacheRW[cache] = 0; }
    meterWeight[lane] = 0;
    meterBack[lane] = 0;
    [unroll] for (uint axis = 0; axis < 6; axis++) {
        meterAxis[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
        meterSky[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
    }
    if (lane < 8) {
        float3 spacing = sdfProbeLighting[4].xyz;
        float3 sample = origin.xyz / spacing - 0.5;
        int3 cell = (int3)floor(sample);
        float3 fraction = frac(sample);
        int3 high = int3(lane & 1, (lane >> 1) & 1, lane >> 2);
        cell += high;
        uint dims = (uint)sdfProbeLighting[3].w;
        uint address = sdfMeshIrradianceSlot(cell, int3(dims & 1023, (dims >> 10) & 1023, dims >> 20)) * SDF_MESH_IRRADIANCE_PROBE_WORDS;
        uint key = sdfMeshIrradiance[address];
        if (key == (sdfMeshIrradianceKey(cell) | SDF_MESH_IRRADIANCE_WRITTEN)) {
            float3 toward = (cell + 0.5) * spacing + sdfMeshProbeOffset(address) - origin.xyz;
            float distance = length(toward);
            if (distance <= SDF_MESH_IRRADIANCE_METER_COINCIDENT || probeClear(origin.xyz, toward / distance, distance, lane)) {
                float3 weights = lerp(1 - fraction, fraction, (float3)high);
                float weight = weights.x * weights.y * weights.z;
                meterWeight[lane] = weight;
                [unroll] for (uint axis = 0; axis < 6; axis++) {
                    float sky; float3 radiance;
                    sdfMeshIrradianceProbe(address, meterNormal(axis), sky, radiance);
                    meterAxis[axis * SDF_PROBE_GROUP_SIZE + lane] = weight * (meterLuminance(radiance) + meterSkyLuminance(axis, sky));
                    meterSky[axis * SDF_PROBE_GROUP_SIZE + lane] = weight * sky;
                }
            }
        }
    }
    DeviceMemoryBarrierWithGroupSync();
    float total = 0;
    [unroll] for (uint i = 0; i < 8; i++) { total += meterWeight[i]; }
    if (lane == 0) { meterTrace = total <= 0 && sdfProbeCacheRW[cache] == 0 ? 1 : 0; }
    GroupMemoryBarrierWithGroupSync();
    if (total <= 0) {
        for (uint texel = lane; texel < SDF_PROBE_HIT_COUNT; texel += SDF_PROBE_GROUP_SIZE) {
            float4 ray = sdfProbeDirections[texel];
            uint2 hit;
            if (meterTrace != 0) {
                uint2 identity;
                hit = sdfProbeHit(origin.xyz, ray.xyz, sdfProbeLighting[0].w, lane, identity);
                sdfProbeCacheRW[cache + 1 + 2 * texel] = hit.x;
                sdfProbeCacheRW[cache + 2 + 2 * texel] = hit.y;
            } else { hit = uint2(sdfProbeCacheRW[cache + 1 + 2 * texel], sdfProbeCacheRW[cache + 2 + 2 * texel]); }
            float distance = asfloat(hit.x);
            float sky = isfinite(distance) ? 0 : 1;
            meterBack[lane] += distance < 0 ? 1 : 0;
            float3 radiance = 0;
            if (isfinite(distance) && distance >= 0) {
                float3 normal = sdfOctDecode(float2((int)(hit.y << 8) >> 24, (int)hit.y >> 24) / SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
                float3 local;
                radiance = sdfProbeShade(origin.xyz + ray.xyz * distance, normal, hit.y & 65535u, false, lane, local);
                radiance += local;
            }
            [unroll] for (uint axis = 0; axis < 6; axis++) {
                float weight = ray.w * max(0, dot(ray.xyz, meterNormal(axis)));
                meterAxis[axis * SDF_PROBE_GROUP_SIZE + lane] += weight * meterLuminance(radiance);
                meterSky[axis * SDF_PROBE_GROUP_SIZE + lane] += weight * sky;
            }
        }
        if (lane == 0) { sdfProbeCacheRW[cache] = 1; }
    }
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0) {
        uint back = 0;
        for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) { back += meterBack[i]; }
        float open = 0;
        [unroll] for (uint axis = 0; axis < 6; axis++) {
            float value = 0, sky = 0;
            for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) {
                value += meterAxis[axis * SDF_PROBE_GROUP_SIZE + i];
                sky += meterSky[axis * SDF_PROBE_GROUP_SIZE + i];
            }
            float divisor = total > 0 ? total : sdfProbeLighting[9 + axis / 4][axis % 4];
            sky /= divisor;
            value /= divisor;
            if (total <= 0) { value += meterSkyLuminance(axis, sky); }
            sdfProbeHitsRW[group.x * SDF_PROBE_METER_OUTPUT_WORDS + axis] = asuint(value);
            open += sky / 6;
        }
        sdfProbeHitsRW[group.x * SDF_PROBE_METER_OUTPUT_WORDS + 6] = asuint(saturate(SDF_MESH_IRRADIANCE_OPEN_GROUND_SHARE * open));
        sdfProbeHitsRW[group.x * SDF_PROBE_METER_OUTPUT_WORDS + 7] = (total > 0 ? 1 : back / (float)SDF_PROBE_HIT_COUNT <= SDF_MESH_IRRADIANCE_INSIDE_SHARE ? 2 : 0) | (meterTrace != 0 ? 4 : 0);
    }
}
