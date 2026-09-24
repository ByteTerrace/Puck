// Samples the layered color image by the fullscreen adapter's top-left UV, so the published image shows the layers the
// right way up on every backend.
[[vk::binding(0, 0)]] Texture2D<float4> layersImage : register(t0);
[[vk::binding(0, 0)]] SamplerState layersSampler : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    return layersImage.SampleLevel(layersSampler, uv, 0.0);
}
