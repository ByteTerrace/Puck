#ifndef SDF_PROBE_LIGHTING_HLSLI
#define SDF_PROBE_LIGHTING_HLSLI
#include "sdf-probe-trace.hlsli"
struct ProbePass {
    float4 meshIrradianceLevels[3];
    float4 meshIrradianceSpacings[3];
    uint meshIrradianceEpoch;
};
static ProbePass passGroup;
// The relight reads the volume only as the current lighting epoch left it (sdfMeshIrradianceHolds).
#define SDF_MESH_IRRADIANCE_EPOCH passGroup.meshIrradianceEpoch
#include "sdf-mesh-irradiance.hlsli"
void sdfProbeLightingSetup() {
    [unroll] for (uint level = 0; level < 3; level++) {
        passGroup.meshIrradianceLevels[level] = sdfProbeLighting[3 + 2 * level];
        passGroup.meshIrradianceSpacings[level] = sdfProbeLighting[4 + 2 * level];
    }
    passGroup.meshIrradianceEpoch = (uint)sdfProbeLighting[SDF_PROBE_RELIGHT_EPOCH_ROW].z;
}
// Whether a ray reaches `reach` unblocked; `toward` a point light at that distance, past the solid that holds it.
bool probeClear(float3 positionWorld, float3 direction, float reach, uint lane, bool toward) {
    return !sdfProbeOccluded(positionWorld, direction, reach, lane, toward);
}
// One shading mechanism for lattice relight and camera-meter fallback; CPU Shade remains the reference. `unanswered` is
// the reflectance (times the bounce gain) a hit no probe answers leaves without its bounce, zero where the volume answers
// or the bounce is off: the camera sample closes it with its own light (sdf-probe-camera-sample-stage.hlsli).
float3 sdfProbeShade(float3 positionWorld, float3 normal, uint material, bool reuse, uint lane, out float3 local, out float3 unanswered) {
    float4 counts = sdfProbeLighting[0], sources = sdfProbeLighting[1], gains = sdfProbeLighting[2];
    uint lightBase = SDF_PROBE_RELIGHT_HEADER_ROWS + (uint)counts.x * SDF_PROBE_RELIGHT_MATERIAL_ROWS;
    uint lightCount = (uint)(counts.y + counts.z);
    material = min(material, (uint)counts.x - 1);
    float4 surface = sdfProbeLighting[SDF_PROBE_RELIGHT_HEADER_ROWS + material * SDF_PROBE_RELIGHT_MATERIAL_ROWS];
    float3 lifted = positionWorld + normal * 0.001;
    float3 varying = 0;
    local = 0;
    unanswered = 0;
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
                reach = length - SDF_MESH_IRRADIANCE_LIGHT_BIAS;
            }
            float lambert = max(0, (dot(normal, direction) + surface.w) / (1 + surface.w));
            if (lambert <= 0 || attenuation <= 0) { continue; }
            if (mode.z != 0 && reach > 0 && !probeClear(lifted, direction, reach, lane, mode.w != 0)) { continue; }
            float3 value = surface.xyz * color.xyz * (color.w * lambert * mode.x * attenuation * gains.x);
            if (mode.w == 0) { varying += value; } else { local += value; }
        }
    }
    // A hit's own emission adds nothing: every emitting triangle the scene traces is a patch the relight samples from the
    // probe's centre (sdfProbeEmitters), which a ray meeting it would count again.
    if (sources.z != 0 && any(surface.xyz != 0)) {
        SdfMeshProbes probes = sdfMeshProbesAt(positionWorld, normal);
        if (probes.share > 0) { varying += surface.xyz * sdfMeshIndirect(probes, normal) * gains.z; }
        else { unanswered = surface.xyz * gains.z; }
    }
    return varying;
}
// The area an emitting patch shows along a unit direction leaving it, from its area-weighted normal sum and area
// (`normalArea`): its flat part's projection plus a quarter of the area its normals cancel. KEEP IN SYNC with
// SdfMeshEmitter.ProjectedArea.
float sdfProbeEmitterArea(float4 normalArea, float3 direction) {
    return max(0, dot(normalArea.xyz, direction)) + 0.25 * max(0, normalArea.w - length(normalArea.xyz));
}
// Adds to `light`, one value an axis (+X, -X, +Y, -Y, +Z, -Z) in the cube's units (the light a surface facing the axis
// receives, over pi), what the emitting patches `first`, `first + stride`, ... after the lights send `origin`: each one
// within its range that `origin` sees (probeClear toward it, so the solid holding it never hides it) lights it as a
// Lambertian face of the area it shows toward it does, E = L * P * cos / (d^2 + P / pi), eased to zero at its range, the
// cosine each axis's with the direction toward it. KEEP IN SYNC with SdfMeshIrradiance.AddEmitters.
void sdfProbeEmitters(float3 origin, uint first, uint stride, uint lane, inout float3 light[6]) {
    const float pi = 3.14159265358979;
    float4 counts = sdfProbeLighting[0];
    uint emitterBase = SDF_PROBE_RELIGHT_HEADER_ROWS + (uint)counts.x * SDF_PROBE_RELIGHT_MATERIAL_ROWS + (uint)(counts.y + counts.z) * SDF_PROBE_RELIGHT_LIGHT_ROWS;
    uint emitters = (uint)sdfProbeLighting[SDF_PROBE_RELIGHT_EPOCH_ROW].w;
    for (uint emitter = first; emitter < emitters; emitter += stride) {
        uint row = emitterBase + emitter * SDF_PROBE_RELIGHT_EMITTER_ROWS;
        // The patch's centroid and range (`centroid` is an HLSL interpolation keyword).
        float4 at = sdfProbeLighting[row];
        float3 toward = at.xyz - origin;
        float distanceSquared = dot(toward, toward);
        if (distanceSquared >= at.w * at.w) { continue; }
        float distance = sqrt(distanceSquared);
        if (!(distance > SDF_MESH_IRRADIANCE_LIGHT_BIAS)) { continue; }
        float3 direction = toward / distance;
        float shown = sdfProbeEmitterArea(sdfProbeLighting[row + 1], -direction);
        if (!(shown > 0) || !probeClear(origin, direction, distance - SDF_MESH_IRRADIANCE_LIGHT_BIAS, lane, true)) { continue; }
        float ratio = distance / at.w;
        float window = saturate(1 - (ratio * ratio) * (ratio * ratio));
        float3 value = sdfProbeLighting[row + 2].xyz * ((shown * (window * window)) / (pi * distanceSquared + shown));
        [unroll] for (uint axis = 0; axis < 6; axis++) {
            light[axis] += value * max(0, direction[axis / 2] * ((axis & 1) == 0 ? 1 : -1));
        }
    }
}
#endif
