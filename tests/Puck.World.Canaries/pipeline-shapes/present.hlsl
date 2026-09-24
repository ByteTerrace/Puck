// The fullscreen stage, drawn through the POSITION vertex adapter: it copies mixed and adds 1/2 to blue on the right
// half of the image, so its own contribution depends on the adapter's UV.
[[vk::binding(0, 0)]] Texture2D<float4> mixedImage : register(t0);
[[vk::binding(0, 0)]] SamplerState mixedSampler : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    float4 mixed = mixedImage.Sample(mixedSampler, uv);

    return float4(mixed.r, mixed.g, (mixed.b + ((uv.x >= 0.5) ? 0.5 : 0.0)), 1.0);
}
