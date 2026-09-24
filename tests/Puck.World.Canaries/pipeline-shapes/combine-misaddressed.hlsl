// The discriminating second stage: blue reads the word at byte 0 instead of byte 4, so mixed = (3/4, 3/8, 3/8, 1).
[[vk::combinedImageSampler]] [[vk::binding(2, 0)]] Texture2D<float4> fieldImage : register(t0);
[[vk::combinedImageSampler]] [[vk::binding(2, 0)]] SamplerState fieldSampler : register(s0);
[[vk::binding(7, 0)]] ByteAddressBuffer words : register(t1);
[[vk::binding(5, 0)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> mixed : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    mixed.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(width, height));
    float field = fieldImage.SampleLevel(fieldSampler, uv, 0.0).r;

    mixed[id.xy] = float4((2.0 * field), (float(words.Load(0)) / 256.0), (float(words.Load(0)) / 256.0), 1.0);
}
