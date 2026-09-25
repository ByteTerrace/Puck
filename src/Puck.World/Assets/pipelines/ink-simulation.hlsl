// Ink is advected through a slowly turning flow and retained in a floating-point history image.
// The frame block is the pass's generated interface: the frame values and the pass's config.
#include "ink-simulation.interface.hlsli"

[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] Texture2D<float4> previousSimulation : register(t1);
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] SamplerState previousSampler : register(s1);
[[vk::binding(0, 0)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> simulation : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    simulation.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    // Image coordinates: the origin is the top-left corner and y grows downward, as the history is stored.
    float2 extent = float2(width, height);
    float2 uv = ((float2(id.xy) + 0.5) / extent);
    float2 center = (uv - 0.5);
    float2 flow = (float2(-center.y, center.x) * 0.004);
    float4 previous = previousSimulation.SampleLevel(previousSampler, saturate(uv - flow), 0.0);
    float2 pen = (0.5 + (0.27 * float2(cos(frameGroup.time * 0.91), sin(frameGroup.time * 1.17))));

    if (frameGroup.pointerDown != 0) {
        pen = (frameGroup.pointer / extent);
    }
    float2 offset = (uv - pen);
    float ink = exp(-dot(offset, offset) * 1700.0);
    float value = max((previous.r * frameGroup.decay), ink);

    simulation[id.xy] = float4(value, previous.r, 0.0, 1.0);
}
