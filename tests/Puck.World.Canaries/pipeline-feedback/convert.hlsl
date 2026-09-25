// Converts the history's red channel to opaque grayscale, saturated at full scale.
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] Texture2D<float4> historyImage : register(t1);
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] SamplerState historySampler : register(s1);
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> gray : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    gray.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    float2 uv = ((float2(id.xy) + 0.5) / float2(width, height));
    float value = saturate(historyImage.SampleLevel(historySampler, uv, 0.0).r);

    gray[id.xy] = float4(value, value, value, 1.0);
}
