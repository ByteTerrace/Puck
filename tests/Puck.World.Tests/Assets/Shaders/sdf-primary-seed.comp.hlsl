// Runs the real primary trace with and without its proposal through the ordinary field-device descriptors.
#define SDF_PRIMARY_PASS
#define SDF_MARCH_SEED
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-world.hlsli"
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
    uint mode = (uint)probe.x;
    bool seeded = (mode & 1u) != 0u;
    uint outputKind = mode >> 1u;
    float footprint = probe.z < 0.0 ? 0.0 : 1.0 / 64.0;
    sdfProgramLayout = sdfLoadProgramLayout();
    sdfPrimarySeedStart = 2.0;
    if (seeded) sdfPreparePrimarySeed(2.0, abs(probe.z), footprint, 1000000.0);
    SdfPrimaryMarch hit = sdfTracePrimary(float3(probe.y, 0.0, 0.0), float3(1.0, 0.0, 0.0),
        2.0, 1000000.0, 1000000.0, 1000000.0, 1000000.0, SDF_INSTANCE_MASK_ALL, footprint);
    fieldResults[uint2(index % 256u, index / 256u)] = outputKind == 1u
        ? float4(sdfEvalCount, (float)sdfWorkSteps, sdfPrimarySeedStart, sdfCanTracePartsIndependently() ? 1.0 : 0.0)
        : outputKind == 2u ? float4(hit.radius, hit.threshold, hit.blendWeight, (float)hit.blendOther)
        : float4(hit.traveled, (float)hit.steps, hit.found ? 1.0 : 0.0, (float)hit.material);
}
