#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/indirect.interface.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-sky.hlsli"

[[vk::binding(60, 3)]] StructuredBuffer<float4> skyCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> skyResults : register(u61, space3);
struct SkyProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<SkyProbeIndex> skyProbeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain() {
    uint index = skyProbeIndex.index;
    float4 ray = skyCases[2u * index];
    uint sources = asuint(skyCases[2u * index + 1u].x);
    workCounters[PuckWorkSkyTextureLoadsWord] = 0u;
    workCounters[PuckWorkSkyTextureLoadsWord + 1u] = 0u;
    DeviceMemoryBarrier();
    float3 radiance = sdfIndirectSky(asuint(ray.w), ray.xyz, sources);
    DeviceMemoryBarrier();
    skyResults[uint2(index, 0u)] = float4(radiance, (float)workCounters[PuckWorkSkyTextureLoadsWord]);
    skyResults[uint2(index, 1u)] = float4((float)workCounters[PuckWorkSkyTextureLoadsWord + 1u], 0.0, 0.0, 0.0);
}
