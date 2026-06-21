// A standalone compute "source" kernel for a CHILD render node: it fills a rect-sized storage image with an
// animated, deliberately non-SDF field so a viewport can show ANOTHER IRenderNode's output instead of an SDF
// camera. The two-stage world compositor's Stage 2 (sdf-world-composite.comp) reads this image as just another
// source[] slot — it neither knows nor cares the source was produced by a different node. Cross-backend: the same
// HLSL compiles to SPIR-V (Vulkan) and DXIL (Direct3D 12); register(u0) drives the Direct3D 12 root signature.
// One invocation per output pixel over an 8x8 workgroup; the image is left in the compute working layout for the
// parent compositor to read directly (an integer-copy source, no sampling).

struct ChildParams {
    uint2 extent;   // the child surface size in pixels (the viewport's pixel rect)
    float time;     // seconds, for animation
    uint pad;
};
[[vk::push_constant]] ConstantBuffer<ChildParams> params;

[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Output : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if ((id.x >= params.extent.x) || (id.y >= params.extent.y)) {
        return;
    }

    float2 uv = ((float2(id.xy) + 0.5) / float2(params.extent));
    float2 centered = (uv - 0.5);
    float radius = length(centered);
    float angle = atan2(centered.y, centered.x);

    // Concentric rings sweeping outward crossed with rotating spokes — a clear "test pattern", unmistakably not an
    // SDF render of the scene next to it.
    float rings = (0.5 + (0.5 * sin((radius * 34.0) - (params.time * 3.0))));
    float spokes = (0.5 + (0.5 * sin((angle * 8.0) + (params.time * 1.7))));
    float mix = saturate(((rings * 0.65) + (spokes * 0.35)));

    float3 inner = float3(0.05, 0.02, 0.18);
    float3 outer = float3(1.00, 0.62, 0.10);
    float3 color = lerp(inner, outer, mix);

    // A thin teal crosshair marks the surface as a hosted child viewport.
    float crosshair = max(smoothstep(0.012, 0.0, abs(centered.x)), smoothstep(0.012, 0.0, abs(centered.y)));
    color = lerp(color, float3(0.20, 0.95, 0.85), (crosshair * 0.8));

    Output[id.xy] = float4(color, 1.0);
}
