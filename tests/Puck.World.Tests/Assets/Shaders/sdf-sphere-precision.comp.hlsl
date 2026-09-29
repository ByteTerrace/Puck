// Two exact inputs exercise the shipped sphere equation through both scalar and analytic-gradient VM walks.
// The existing field-device harness owns the same program/case/result descriptors and readback.
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);
struct FieldProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<FieldProbeIndex> fieldProbeIndex : register(b0, space4);
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint first = fieldProbeIndex.index & 0xFFFFu;
    uint count = fieldProbeIndex.index >> 16u;
    if (id.x >= count) return;
    float4 probe = fieldCases[first + id.x];
    uint index = asuint(probe.w);
    sdfProgramLayout = sdfLoadProgramLayout();
    SdfHit scalar = map(probe.xyz);
    float3 gradient;
    SdfHit dual = mapGradMasked(probe.xyz, SDF_INSTANCE_MASK_ALL, gradient);
    fieldResults[uint2(index % 256u, index / 256u)] = float4(
        scalar.distance, dual.distance, asfloat(scalar.material), sdfProgramLayout.stepScale);
}

