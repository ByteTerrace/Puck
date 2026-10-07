#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"

[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);
struct GaugeEnvelopeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<GaugeEnvelopeIndex> fieldProbeIndex : register(b0, space4);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint first = fieldProbeIndex.index & 0xFFFFu;
    uint count = fieldProbeIndex.index >> 16u;
    if (id.x >= count) { return; }
    float4 probe = fieldCases[first + id.x];
    float clamped = sdfClampSuperellipsoidNorm(probe.x, probe.y, probe.z);
    float choseEndpoint = sdfSuperellipsoidNormIsClamped(probe.x, probe.y, probe.z) ? 1.0 : 0.0;
    uint slot = asuint(probe.w);
    fieldResults[uint2(slot % 256u, slot / 256u)] = float4(clamped, choseEndpoint, 0.0, 0.0);
}
