// The Vulkan presenter's blit (DXC compiles it to SPIR-V): sample the one surface the root node produced, 1:1, onto the
// swapchain. The source texture matches the framebuffer extent, so screen UV is fragment coord over texture size.
//
// The source is a separate image and sampler in the pass group, set 3, with each register equal to its binding: the
// layout both compositors bind (SurfaceBlitLayout). The Direct3D 12 presenter blits with its own surface-blit shaders
// on the same registers.
[[vk::binding(0, 3)]] Texture2D sourceTexture : register(t0, space3);
[[vk::binding(1, 3)]] SamplerState sourceSampler : register(s1, space3);

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));

    return float4(sourceTexture.Sample(sourceSampler, uv).rgb, 1.0);
}
