// Copies the grayscale image through the fullscreen path unchanged.
[[vk::binding(0, 0)]] Texture2D<float4> sourceImage : register(t0);
[[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    return sourceImage.Sample(sourceSampler, uv);
}
