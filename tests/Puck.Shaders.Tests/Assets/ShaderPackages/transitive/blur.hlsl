#include "lib/common.hlsli"

// Fills the half-float field with the brightened constant common.hlsli and its own include, math.hlsli, define.
[[vk::binding(0, 0)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> field : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    field[id.xy] = float4(Brighten(0.25), 0.0, 0.0, 1.0);
}
