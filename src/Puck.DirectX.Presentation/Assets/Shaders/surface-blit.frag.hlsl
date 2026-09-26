// The Direct3D 12 surface compositor's blit: samples the one source surface at the interpolated coordinate from
// surface-blit.vert.hlsl. The texture and sampler are a separate image and sampler in the pass group, space 3, with each
// register equal to its binding: the layout both compositors bind (SurfaceBlitLayout), whose sampler the compositor
// writes clamp-addressed with a linear filter.
[[vk::binding(0, 3)]] Texture2D<float4> sourceTexture : register(t0, space3);
[[vk::binding(1, 3)]] SamplerState sourceSampler : register(s1, space3);

float4 PSMain(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    return sourceTexture.Sample(sourceSampler, uv);
}