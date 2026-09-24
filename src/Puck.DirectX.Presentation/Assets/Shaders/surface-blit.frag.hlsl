// The Direct3D 12 surface compositor's blit: samples the one source surface at the interpolated coordinate from
// surface-blit.vert.hlsl. The texture and sampler sit at t0 and s0, the root signature DirectXSurfaceCompositor builds.
Texture2D<float4> sourceTexture : register(t0);
SamplerState sourceSampler : register(s0);

float4 PSMain(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    return sourceTexture.Sample(sourceSampler, uv);
}
