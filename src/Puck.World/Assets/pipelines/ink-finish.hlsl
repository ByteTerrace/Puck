[[vk::binding(0, 0)]] Texture2D<float4> sourceImage : register(t0);
[[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);
float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    float3 color = sourceImage.Sample(sourceSampler, uv).rgb;
    float2 offset = uv - 0.5;
    float vignette = 1.0 - 0.55 * dot(offset, offset);
    return float4(color * vignette, 1);
}
