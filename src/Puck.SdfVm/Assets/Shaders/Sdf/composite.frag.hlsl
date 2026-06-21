// The compositor (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for DirectX). Places up to
// four rendered viewport textures into their current screen regions, with a shader-driven ripple while a "Warp"
// layout transition is in flight. NOTE: not wired into the showcase yet (the producer renders a single SDF
// view), so this path is compile-checked but not runtime-verified on either backend.
// On Vulkan the four view textures bind as a sampled-image array at binding 0 with a sampler at binding 1
// (DXC rejects a combined-image-sampler array); on DirectX they are SRVs t0..t3 + sampler s0. When this path is
// wired, the Vulkan composite descriptor layout must follow suit. KEEP IN SYNC with the host's packing.
[[vk::binding(0, 0)]] Texture2D viewTextures[4] : register(t0);
[[vk::binding(1, 0)]] SamplerState viewSampler : register(s0);

struct CompositeData {
    float4 rects[4];     // per viewport: xy = top-left, zw = size (normalized screen space)
    float4 params;       // x = viewport count, y = warp amount, z = time
    float4 resolution;   // xy = framebuffer size in pixels
};
[[vk::push_constant]] ConstantBuffer<CompositeData> composite;

// Constant-indexed sampling so no dynamic texture-array indexing is required. Single-return (index 3 the
// default) so the compiler sees the value is always initialized.
float3 sampleView(int index, float2 uv) {
    float3 result = viewTextures[3].Sample(viewSampler, uv).rgb;

    if (index == 0) { result = viewTextures[0].Sample(viewSampler, uv).rgb; }
    else if (index == 1) { result = viewTextures[1].Sample(viewSampler, uv).rgb; }
    else if (index == 2) { result = viewTextures[2].Sample(viewSampler, uv).rgb; }

    return result;
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    float2 uv = (fragCoord.xy / composite.resolution.xy);
    int count = (int)(composite.params.x + 0.5);
    float warp = composite.params.y;
    float time = composite.params.z;
    float3 color = float3(0.015, 0.017, 0.022); // the gutter between/behind panes

    [loop]
    for (int index = 0; (index < 4); index++) {
        if (index >= count) {
            break;
        }

        float4 rect = composite.rects[index];

        if ((rect.z <= 0.0001) || (rect.w <= 0.0001)) {
            continue; // hidden viewport
        }

        float2 local = ((uv - rect.xy) / rect.zw);

        if (warp > 0.0) {
            local.x += (warp * 0.05 * sin((local.y * 28.0) + (time * 6.0)));
            local.y += (warp * 0.05 * sin((local.x * 24.0) - (time * 5.0)));
        }

        if ((local.x >= 0.0) && (local.x <= 1.0) && (local.y >= 0.0) && (local.y <= 1.0)) {
            color = sampleView(index, local);
        }
    }

    return float4(color, 1.0);
}
