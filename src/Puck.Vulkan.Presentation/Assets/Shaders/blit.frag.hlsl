// The terminal's blit (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for DirectX): sample
// the one surface the root node produced, 1:1, onto the swapchain. The source texture matches the framebuffer
// extent, so screen UV is fragment coord over texture size.
//
// On Vulkan the texture+sampler fuse into one combined image sampler at set 0, binding 0 (what the backend's
// descriptor layout expects); on DirectX they stay separate at t0/s0. One declaration, both bindings.
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));

    return float4(sourceTexture.Sample(sourceSampler, uv).rgb, 1.0);
}
