// The second compute stage: mixed = (2 * field.r, word at byte 0 / 256, word at byte 4 / 256, 1) = (3/4, 3/8, 1/8, 1).
// Reading byte 4 is what proves the table is bound as a byte-address buffer. The bindings are sparse (2, 7 and 5), and
// each register number equals its binding on both backends.
[[vk::combinedImageSampler]] [[vk::binding(2, 0)]] Texture2D<float4> fieldImage : register(t2);
[[vk::combinedImageSampler]] [[vk::binding(2, 0)]] SamplerState fieldSampler : register(s2);
[[vk::binding(7, 0)]] ByteAddressBuffer words : register(t7);
[[vk::binding(5, 0)]] [[vk::image_format("rgba16f")]] RWTexture2D<float4> mixed : register(u5);

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

    mixed[id.xy] = float4((2.0 * field), (float(words.Load(0)) / 256.0), (float(words.Load(4)) / 256.0), 1.0);
}
