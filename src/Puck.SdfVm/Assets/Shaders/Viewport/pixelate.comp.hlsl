// pixelate.comp.hlsl — the RETRO PIXELATION post-effect.
//
// Reads a source pane (a same-size storage image produced by the wrapped node) and writes a retro-styled copy: each
// output pixel takes the color of its CELL CENTER in the source (a NEAREST downsample — chunky, 8/16-bit blocks),
// then optionally reduces the per-channel color depth to a small number of levels (the palette/posterize look). With
// cellSize <= 1 and quantizeLevels <= 1 it is an exact copy, so the effect is purely additive.
//
// Source is read as a STORAGE image (an integer load, no sampler), so this composes in the same integer-copy child
// ecosystem the ViewportCompositorNode uses — a PixelateNode wraps any node that hands back a General-layout pane and
// itself hands back a General-layout pane, so it can be a composited viewport source (the Stage-6 { "$type":
// "pixelate", "source": {...} } decorator). One invocation per output pixel over an 8x8 workgroup. Single-source
// HLSL: DXC compiles it to SPIR-V (Vulkan) and DXIL (Direct3D 12).

struct PixelateParams {
    uint2 extent;         // the (shared) source/output size in pixels
    uint cellSize;        // block size in pixels; <= 1 = off
    uint quantizeLevels;  // per-channel color levels (e.g. 8 => 3-bit per channel); <= 1 = off
};
[[vk::push_constant]] ConstantBuffer<PixelateParams> params;

[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Output : register(u0);
[[vk::binding(1, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Source : register(u1);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if ((id.x >= params.extent.x) || (id.y >= params.extent.y)) {
        return;
    }

    // Snap to the center of this pixel's cell, so every pixel in a cell samples the same source texel (the block).
    uint cell = max(1u, params.cellSize);
    uint2 cellCoord = (((id.xy / cell) * cell) + (cell / 2u));

    cellCoord = min(cellCoord, (params.extent - uint2(1u, 1u)));

    float3 color = saturate(Source[cellCoord].rgb);

    // Optional per-channel color-depth reduction (the retro palette / posterize look).
    if (params.quantizeLevels > 1u) {
        float levels = (float)params.quantizeLevels;

        color = (round(color * (levels - 1.0)) / (levels - 1.0));
    }

    Output[id.xy] = float4(color, 1.0);
}
