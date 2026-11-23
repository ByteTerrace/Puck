// The sdf-probe-camera-sample kernel after its interface: its software build (sdf-probe-camera-sample.comp.hlsl) and its ray-query build
// (sdf-probe-camera-sample-rays.comp.hlsl), which defines SDF_PROBE_RAY_QUERY, include this one body.
#define SDF_PROBE_RESULT_STRIDE SDF_PROBE_CAMERA_SAMPLE_OUTPUT_WORDS
#include "../shade/sdf-probe-lighting.hlsli"

groupshared float3 cameraSampleLight[6 * SDF_PROBE_GROUP_SIZE];
groupshared float cameraSampleSky[6 * SDF_PROBE_GROUP_SIZE];
groupshared float cameraSampleWeight[SDF_PROBE_GROUP_SIZE];
groupshared uint cameraSampleBack[SDF_PROBE_GROUP_SIZE];
groupshared uint cameraSampleTrace;
// A traced sample's closure (KEEP IN SYNC with SdfMeshIrradiance.CloseCameraSample): each hit's reflectance its bounce
// leaves open (sdfProbeShade's `unanswered`, zero for a miss, a back face or a hit the volume answers) and its normal, the
// sample's light without the closure, and its light as the passes iterate it.
groupshared float3 cameraSampleOpen[SDF_PROBE_HIT_COUNT];
groupshared float3 cameraSampleHitNormal[SDF_PROBE_HIT_COUNT];
groupshared float3 cameraSampleBase[6];
groupshared float3 cameraSampleCube[6];
float3 cameraSampleNormal(uint axis) {
    float3 normal = 0;
    normal[axis / 2] = (axis & 1) == 0 ? 1 : -1;
    return normal;
}
// KEEP IN SYNC with SdfMeshIrradiance.TrySampleCamera: eight unobstructed fine probes, otherwise a sample at the camera itself,
// closed over the hits no probe answers (CloseCameraSample). The answer is the
// light the sample receives along each axis, RGB (SdfMeshProbeRelight.CameraSampleOutputWords), then its sky visibility and
// its flags.
[numthreads(SDF_PROBE_GROUP_SIZE, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    sdfProbeLightingSetup();
    float4 origin = sdfProbeOrigins[group.x];
    uint cache = group.x * (1 + 2 * SDF_PROBE_HIT_COUNT);
    if (lane == 0 && origin.w != 0) { sdfProbeCacheRW[cache] = 0; }
    cameraSampleWeight[lane] = 0;
    cameraSampleBack[lane] = 0;
    [unroll] for (uint axis = 0; axis < 6; axis++) {
        cameraSampleLight[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
        cameraSampleSky[axis * SDF_PROBE_GROUP_SIZE + lane] = 0;
    }
    if (lane < 8) {
        uint level = 0;
        [unroll] for (uint finer = 0; finer + 1 < SDF_MESH_IRRADIANCE_LEVELS; finer++) {
            if (sdfMeshIrradianceSpacing(finer).x == 0) { level = finer + 1; }
        }
        float3 spacing = sdfMeshIrradianceSpacing(level);
        float3 sample = origin.xyz / spacing - 0.5;
        int3 cell = (int3)floor(sample);
        float3 fraction = frac(sample);
        int3 high = int3(lane & 1, (lane >> 1) & 1, lane >> 2);
        cell += high;
        uint address = (sdfMeshIrradianceBase(level) + sdfMeshIrradianceSlot(cell, sdfMeshIrradianceDims(level))) * SDF_MESH_IRRADIANCE_PROBE_WORDS;
        uint key = sdfMeshIrradiance[address];
        if ((key & SDF_MESH_IRRADIANCE_INVALID) == 0u && sdfMeshIrradianceHolds(address, key, cell)) {
            float3 toward = (cell + 0.5) * spacing + sdfMeshProbeOffset(address) - origin.xyz;
            float distance = length(toward);
            if (distance <= SDF_MESH_IRRADIANCE_CAMERA_SAMPLE_COINCIDENT || probeClear(origin.xyz, toward / distance, distance, lane, false)) {
                float3 weights = lerp(1 - fraction, fraction, (float3)high);
                float weight = weights.x * weights.y * weights.z;
                cameraSampleWeight[lane] = weight;
                [unroll] for (uint axis = 0; axis < 6; axis++) {
                    float sky; float3 radiance; float3 skyLight;
                    sdfMeshIrradianceProbe(address, cameraSampleNormal(axis), sky, radiance, skyLight);
                    cameraSampleLight[axis * SDF_PROBE_GROUP_SIZE + lane] = weight * (radiance + skyLight);
                    cameraSampleSky[axis * SDF_PROBE_GROUP_SIZE + lane] = weight * sky;
                }
            }
        }
    }
    DeviceMemoryBarrierWithGroupSync();
    float total = 0;
    [unroll] for (uint i = 0; i < 8; i++) { total += cameraSampleWeight[i]; }
    if (lane == 0) { cameraSampleTrace = total <= 0 && sdfProbeCacheRW[cache] == 0 ? 1 : 0; }
    GroupMemoryBarrierWithGroupSync();
    if (total <= 0) {
        for (uint texel = lane; texel < SDF_PROBE_HIT_COUNT; texel += SDF_PROBE_GROUP_SIZE) {
            float4 ray = sdfProbeDirections[texel];
            uint2 hit;
            if (cameraSampleTrace != 0) {
                uint2 identity;
                hit = sdfProbeHit(origin.xyz, ray.xyz, sdfProbeLighting[0].w, lane, identity);
                sdfProbeCacheRW[cache + 1 + 2 * texel] = hit.x;
                sdfProbeCacheRW[cache + 2 + 2 * texel] = hit.y;
            } else { hit = uint2(sdfProbeCacheRW[cache + 1 + 2 * texel], sdfProbeCacheRW[cache + 2 + 2 * texel]); }
            float distance = asfloat(hit.x);
            float sky = isfinite(distance) ? 0 : 1;
            cameraSampleBack[lane] += distance < 0 ? 1 : 0;
            float3 radiance = isfinite(distance) ? 0 : sdfProbeLighting[SDF_PROBE_RELIGHT_SKY_ROW + texel].xyz;
            float3 open = 0, normal = 0;
            if (isfinite(distance) && distance >= 0) {
                normal = sdfOctDecode(float2((int)(hit.y << 8) >> 24, (int)hit.y >> 24) / SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
                float3 local;
                radiance = sdfProbeShade(origin.xyz + ray.xyz * distance, normal, hit.y & 65535u, false, lane, local, open);
                radiance += local;
            }
            cameraSampleOpen[texel] = open;
            cameraSampleHitNormal[texel] = normal;
            [unroll] for (uint axis = 0; axis < 6; axis++) {
                float weight = ray.w * max(0, dot(ray.xyz, cameraSampleNormal(axis)));
                cameraSampleLight[axis * SDF_PROBE_GROUP_SIZE + lane] += weight * radiance;
                cameraSampleSky[axis * SDF_PROBE_GROUP_SIZE + lane] += weight * sky;
            }
        }
        if (lane == 0) { sdfProbeCacheRW[cache] = 1; }
        // The closure: a hit no probe answers reflects the light the sample itself receives about its normal, iterated
        // from the sample's light without it. `total` is the group's, so every lane takes these barriers.
        GroupMemoryBarrierWithGroupSync();
        if (lane < 6) {
            float3 sum = 0;
            for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) { sum += cameraSampleLight[lane * SDF_PROBE_GROUP_SIZE + i]; }
            cameraSampleBase[lane] = sum / sdfProbeLighting[9 + lane / 4][lane % 4];
            cameraSampleCube[lane] = cameraSampleBase[lane];
        }
        GroupMemoryBarrierWithGroupSync();
        for (uint pass = 0; pass < SDF_PROBE_CAMERA_SAMPLE_CLOSURE_PASSES; pass++) {
            [unroll] for (uint axis = 0; axis < 6; axis++) { cameraSampleLight[axis * SDF_PROBE_GROUP_SIZE + lane] = 0; }
            for (uint t = lane; t < SDF_PROBE_HIT_COUNT; t += SDF_PROBE_GROUP_SIZE) {
                float3 open = cameraSampleOpen[t];
                if (all(open == 0)) { continue; }
                float3 normal = cameraSampleHitNormal[t];
                float3 squared = normal * normal;
                float3 incoming = 0;
                [unroll] for (uint component = 0; component < 3; component++) {
                    incoming += squared[component] * cameraSampleCube[sdfMeshCubeFace(normal, component)];
                }
                float4 ray = sdfProbeDirections[t];
                [unroll] for (uint face = 0; face < 6; face++) {
                    cameraSampleLight[face * SDF_PROBE_GROUP_SIZE + lane] += (ray.w * max(0, dot(ray.xyz, cameraSampleNormal(face)))) * (open * incoming);
                }
            }
            GroupMemoryBarrierWithGroupSync();
            if (lane < 6) {
                float3 sum = 0;
                for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) { sum += cameraSampleLight[lane * SDF_PROBE_GROUP_SIZE + i]; }
                cameraSampleCube[lane] = cameraSampleBase[lane] + sum / sdfProbeLighting[9 + lane / 4][lane % 4];
            }
            GroupMemoryBarrierWithGroupSync();
        }
    }
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0) {
        uint back = 0;
        for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) { back += cameraSampleBack[i]; }
        float open = 0;
        [unroll] for (uint axis = 0; axis < 6; axis++) {
            float3 value = 0;
            float sky = 0;
            for (uint i = 0; i < SDF_PROBE_GROUP_SIZE; i++) {
                value += cameraSampleLight[axis * SDF_PROBE_GROUP_SIZE + i];
                sky += cameraSampleSky[axis * SDF_PROBE_GROUP_SIZE + i];
            }
            float divisor = total > 0 ? total : sdfProbeLighting[9 + axis / 4][axis % 4];
            sky /= divisor;
            value = total > 0 ? value / total : cameraSampleCube[axis];
            [unroll] for (uint channel = 0; channel < 3; channel++) {
                sdfProbeHitsRW[group.x * SDF_PROBE_CAMERA_SAMPLE_OUTPUT_WORDS + 3 * axis + channel] = asuint(value[channel]);
            }
            open += sky / 6;
        }
        sdfProbeHitsRW[group.x * SDF_PROBE_CAMERA_SAMPLE_OUTPUT_WORDS + SDF_PROBE_CAMERA_SAMPLE_OUTPUT_WORDS - 2] = asuint(saturate(SDF_MESH_IRRADIANCE_OPEN_GROUND_SHARE * open));
        sdfProbeHitsRW[group.x * SDF_PROBE_CAMERA_SAMPLE_OUTPUT_WORDS + SDF_PROBE_CAMERA_SAMPLE_OUTPUT_WORDS - 1] = (total > 0 ? 1 : back / (float)SDF_PROBE_HIT_COUNT <= SDF_MESH_IRRADIANCE_INSIDE_SHARE ? 2 : 0) | (cameraSampleTrace != 0 ? 4 : 0);
    }
}
