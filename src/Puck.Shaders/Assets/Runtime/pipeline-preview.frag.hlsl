[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D<float4> sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;
    sourceTexture.GetDimensions(width, height);
    float2 uv = fragCoord.xy / float2(width, height);
    float4 value = sourceTexture.Sample(sourceSampler, uv);
    return float4(saturate(value.rgb), 1.0);
}
