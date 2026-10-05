#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-indirect-layout.hlsli"
[[vk::binding(60, 3)]] StructuredBuffer<int4> indirectBricks : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> brickResults : register(u61, space3);
struct BrickParameters { uint indirectTier; };
[[vk::binding(0, 3)]] ConstantBuffer<BrickParameters> passGroup : register(b0, space3);
struct BrickProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<BrickProbeIndex> brickProbeIndex : register(b0, space4);
static uint sdfIndirectLoads = 0u;
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-bricks.hlsli"

[numthreads(1, 1, 1)]
void CSMain() {
    uint capacity = sdfIndirectBrickCapacity(passGroup.indirectTier);
    int4 query = indirectBricks[capacity + capacity / 4u + brickProbeIndex.index];
    int found = sdfIndirectProbeIndex(query.xyz, (uint)query.w);
    brickResults[uint2(brickProbeIndex.index, 0u)] = float4((float)found, (float)sdfIndirectLoads, 0.0, 0.0);
}
