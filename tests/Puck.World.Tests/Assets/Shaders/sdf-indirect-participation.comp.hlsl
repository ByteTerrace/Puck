#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-field.hlsli"

[[vk::binding(126, 3)]] StructuredBuffer<float4> cases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> results : register(u127, space3);
struct ProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<ProbeIndex> probeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain() {
    sdfProgramLayout = sdfLoadProgramLayout();
    float3 samplePosition = cases[0].xyz;
    SdfHit ordinary = mapCore(samplePosition, SDF_INSTANCE_MASK_ALL, true);
    // Indirect policy must override the separately suppressed direct-shadow bit, then restore the caller.
    sdfShadowParticipationActive = true;
    // A full-field distance and gradient must also ignore an outer lighting mask without destroying it.
    sdfShadowMaskWords[0] = 0u;
    sdfAmbientMaskWords[0] = 0u;
    GroupMemoryBarrierWithGroupSync();
    sdfShadowMaskActive = true;
    sdfAmbientMaskActive = true;
    SdfHit indirect = sdfIndirectSample(samplePosition, SDF_INSTANCE_MASK_ALL);
    float3 gradient = sdfIndirectGradient(samplePosition + float3(1.0, 0.0, 0.0));
    bool masksRestored = sdfShadowMaskActive && sdfAmbientMaskActive;
    sdfShadowMaskActive = false;
    sdfAmbientMaskActive = false;
    sdfShadowParticipationActive = false;
    SdfHit restored = mapCore(samplePosition, SDF_INSTANCE_MASK_ALL, true);
    results[uint2(probeIndex.index, 0u)] = float4(ordinary.distance, indirect.distance,
        masksRestored ? restored.distance : SDF_FAR_DISTANCE, gradient.x);
}
