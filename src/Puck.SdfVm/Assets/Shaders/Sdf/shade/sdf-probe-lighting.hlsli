#ifndef SDF_PROBE_LIGHTING_HLSLI
#define SDF_PROBE_LIGHTING_HLSLI
#include "sdf-probe-trace.hlsli"
struct ProbePass {
    float4 meshIrradianceLevels[3];
    float4 meshIrradianceSpacings[3];
};
static ProbePass passGroup;
#include "sdf-mesh-irradiance.hlsli"
void sdfProbeLightingSetup() {
    [unroll] for (uint level = 0; level < 3; level++) {
        passGroup.meshIrradianceLevels[level] = sdfProbeLighting[3 + 2 * level];
        passGroup.meshIrradianceSpacings[level] = sdfProbeLighting[4 + 2 * level];
    }
}
bool probeClear(float3 positionWorld, float3 direction, float reach, uint lane) {
    return !sdfProbeOccluded(positionWorld, direction, reach, lane);
}
// One shading mechanism for lattice relight and camera-meter fallback; CPU Shade remains the reference.
float3 sdfProbeShade(float3 positionWorld, float3 normal, uint material, bool reuse, uint lane, out float3 local) {
    float4 counts = sdfProbeLighting[0], sources = sdfProbeLighting[1], gains = sdfProbeLighting[2];
    uint lightBase = SDF_PROBE_RELIGHT_HEADER_ROWS + (uint)counts.x * SDF_PROBE_RELIGHT_MATERIAL_ROWS;
    uint lightCount = (uint)(counts.y + counts.z);
    material = min(material, (uint)counts.x - 1);
    float4 surface = sdfProbeLighting[SDF_PROBE_RELIGHT_HEADER_ROWS + material * SDF_PROBE_RELIGHT_MATERIAL_ROWS];
    float3 lifted = positionWorld + normal * 0.001;
    float3 varying = 0;
    local = 0;
    if (sources.x != 0 && any(surface.xyz != 0)) {
        for (uint light = 0; light < (reuse ? (uint)counts.y : lightCount); light++) {
            uint row = lightBase + light * SDF_PROBE_RELIGHT_LIGHT_ROWS;
            float4 position = sdfProbeLighting[row], color = sdfProbeLighting[row + 1];
            float4 cone = sdfProbeLighting[row + 2], mode = sdfProbeLighting[row + 3];
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
    return varying;
}
#endif
