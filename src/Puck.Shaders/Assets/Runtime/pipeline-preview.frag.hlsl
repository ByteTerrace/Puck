// The float preview: the selected output sampled 1:1 and clamped to the displayable range. Its source is a separate image
// and sampler in the pass group, set 3, each register equal to its binding (ShaderPipelineRenderNode.PreviewLayout).
[[vk::binding(0, 3)]] Texture2D<float4> sourceTexture : register(t0, space3);
[[vk::binding(1, 3)]] SamplerState sourceSampler : register(s1, space3);

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;
    sourceTexture.GetDimensions(width, height);
    float2 uv = fragCoord.xy / float2(width, height);
    float4 value = sourceTexture.Sample(sourceSampler, uv);
    return float4(saturate(value.rgb), 1.0);
}
