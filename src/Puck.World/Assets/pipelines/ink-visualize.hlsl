// Colors the simulated ink over a dark background.
// The frame block is the pass's generated interface: the frame values and the pass's config.
#include "ink-visualize.interface.hlsli"

[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] Texture2D<float4> simulation : register(t0);
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] SamplerState simulationSampler : register(s0);
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> color : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    color.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(width, height));
    float ink = simulation.SampleLevel(simulationSampler, uv, 0.0).r;
    float3 background = float3(0.018, 0.027, 0.060);
    // The hue term rises toward the top of the image, where uv.y is zero.
    float3 pigment = (0.5 + (0.5 * cos(float3(0.0, 1.8, 3.6) + (ink * 4.0) + ((1.0 - uv.y) * 1.4))));

    color[id.xy] = float4(lerp(background, (pigment * frameGroup.exposure), smoothstep(0.015, 0.8, ink)), 1.0);
}
