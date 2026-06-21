// viewport-composite.comp.hlsl — the SOURCE-AGNOSTIC viewport compositor.
//
// Places each viewport's source texture into its screen region by a 1:1 copy (the source is already the region's
// exact pixel size — a pane that needs scaling is wrapped in a ResampleNode upstream, so the compositor itself
// never scales). VIEWPORTS as data: the regions drive the layout; the compositor neither knows nor cares what
// produced each source (an SDF view, a hosted child node, a resampled image, a captured window, ...).
//
// This is the engine's neutral compositor: unlike sdf-world-composite.comp it carries NO SDF concern — no beam
// cull buffer, no empty-tile flattening. The generic ViewportCompositorNode (and, later, the data-driven
// ViewportsNode) hosts N child IRenderNodes and runs this kernel over their surfaces. One invocation per output
// pixel over an 8x8 workgroup. Single-source HLSL: DXC compiles it to SPIR-V (Vulkan) and DXIL (Direct3D 12).

struct CompositeParams {
    uint2 imageExtent;   // output image size in pixels
    uint viewportCount;  // number of live source rects (1..4)
    uint pad;
    float4 rects[4];     // per viewport: xy = normalized origin, zw = normalized size (of the output image)
};
[[vk::push_constant]] ConstantBuffer<CompositeParams> params;

[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Output : register(u0);
// The per-view source textures (binding 1, an array): each a child node's rect-sized output. The format is declared
// so the read is a formatted OpImageRead (no shaderStorageImageReadWithoutFormat dependency).
[[vk::binding(1, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> sources[4] : register(u1);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if ((id.x >= params.imageExtent.x) || (id.y >= params.imageExtent.y)) {
        return;
    }

    float2 uv = ((float2(id.xy) + 0.5) / float2(params.imageExtent));
    float3 color = float3(0.015, 0.016, 0.02); // letterbox outside every viewport region

    // First region that contains the pixel wins (split-screen regions are disjoint, so order is irrelevant).
    for (uint v = 0u; (v < params.viewportCount); v++) {
        float4 r = params.rects[v];

        if (
            (uv.x >= r.x) && (uv.y >= r.y) &&
            (uv.x < (r.x + r.z)) && (uv.y < (r.y + r.w))
        ) {
            uint2 originPx = (uint2)(r.xy * float2(params.imageExtent));
            uint2 localPixel = (id.xy - originPx);
            // Clamp to the source's allocated extent — floor(size * extent), exactly how the host sizes each child.
            // The region membership test above uses pixel CENTERS, so a fractional / non-pixel-aligned rect can put
            // the last localPixel one column past the source; clamp repeats the edge texel instead of reading out of
            // bounds. For pixel-aligned rects (e.g. halves, quadrants) the clamp never triggers, so the copy is exact.
            uint2 srcExtent = max(uint2(1u, 1u), (uint2)(r.zw * float2(params.imageExtent)));
            localPixel = min(localPixel, (srcExtent - uint2(1u, 1u)));

            color = sources[v][localPixel].rgb;

            break;
        }
    }

    Output[id.xy] = float4(color, 1.0);
}
