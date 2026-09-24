// Adds 1/16 to the previous submission's history, so submission n holds n/16 in every texel.
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] Texture2D<float4> previousHistory : register(t0);
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] SamplerState previousSampler : register(s0);
[[vk::binding(0, 0)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> history : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    history.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(width, height));
    float previous = previousHistory.SampleLevel(previousSampler, uv, 0.0).r;

    history[id.xy] = float4((previous + (1.0 / 16.0)), 0.0, 0.0, 1.0);
}
