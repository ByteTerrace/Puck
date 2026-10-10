#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-view-domain.hlsli"

[[vk::binding(126, 3)]] StructuredBuffer<float4> debugCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> debugResults : register(u127, space3);
struct DebugProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<DebugProbeIndex> debugProbeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain() {
    uint index = debugProbeIndex.index;
    float4 surviving = debugCases[3u * index];
    float4 grid = debugCases[3u * index + 1u];
    float4 depth = debugCases[3u * index + 2u];
    uint4 box = surviving.x < 0.0 ? uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0u, 0u) : (uint4)surviving;
    debugResults[uint2(index, 0u)] = (float4)sdfViewDispatchBox(box, (uint2)grid.xy, (uint)grid.z, (int)grid.w);
    debugResults[uint2(index, 1u)] = float4(sdfIndirectDebugMaximum(depth.x != 0.0, depth.y, depth.z), 0.0, 0.0, 0.0);
}
