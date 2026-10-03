// Calls the production K-word packer and decoder with independent expected words supplied by the law.
[[vk::binding(0, 3)]] StructuredBuffer<float4> visibilityCases : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> expectedWords : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> visibilityResults : register(u2, space3);
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-shadow-visibility.hlsli"

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint packed = sdfPackShadowVisibility(visibilityCases[id.x]);
    float4 decoded = sdfUnpackShadowVisibility(expectedWords[id.x]);
    uint repacked = sdfPackShadowVisibility(decoded);

    // Two exactly representable halves preserve every packed bit through a floating-point readback image.
    visibilityResults[uint2(id.x, 0u)] = float4(packed & 0xFFFFu, packed >> 16u, repacked & 0xFFFFu, repacked >> 16u);
    visibilityResults[uint2(id.x, 1u)] = decoded;
}
