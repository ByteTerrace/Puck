// resample.comp.hlsl — the SAMPLED-IMAGE COMPUTE PRIMITIVE (the "ResampleNode" kernel).
//
// This is the FIRST kernel in the engine that SAMPLES a texture from a compute shader: every existing compute
// kernel reads storage images by integer index (e.g. sdf-world-composite.comp does sources[v][localPixel], a
// 1:1 integer copy that requires the source to already be the destination's exact pixel size). This kernel is
// the missing "sampled-image binding in the compute seam" the feature-parity table calls out — it lets a source
// of ANY resolution be filtered/scaled into a differently-sized destination. A ResampleNode wraps a source, runs
// this kernel into an exact-rect, rgba8 storage image, and hands THAT to the unchanged Stage-2 compositor, so the
// bit-identical SDF composite path is never edited.
//
// Single-source HLSL: DXC compiles it to SPIR-V (Vulkan) and DXIL (Direct3D 12). The vk:: attributes are
// SPIR-V-only; the register() slots drive the Direct3D 12 root signature. The source binds the engine's standard
// combined-image-sampler way (same as blit.frag / cursor-overlay.frag): on Vulkan the Texture2D + SamplerState
// FUSE into one combined-image-sampler descriptor at set 0, binding 1; on Direct3D 12 they stay separate at t0
// (an SRV) + s0 (a STATIC sampler in the compute root signature). Compute shaders cannot use .Sample() (there is
// no implicit LOD), so sampling is .SampleLevel(..., 0).
//
// FILTER is chosen HOST-SIDE per node instance (not a push-constant branch): a fit-to-rect ResampleNode uses a
// LINEAR sampler, a pixelation ResampleNode a NEAREST one. On Vulkan that is the filter of the bound VkSampler;
// on Direct3D 12 it is the filter baked into the root signature's static sampler at pipeline creation.
//
// Reused unchanged by the pixelation decorator (Stage 4): cellSize snaps the sample point to cell centers (the
// "low-res downsample + nearest-upscale" retro look in a single pass) and quantizeLevels reduces per-channel
// color depth (the 8/16-bit palette look). Both are no-ops at their defaults (cellSize <= 1, quantizeLevels <= 1).
//
// HOST CONTRACT (the compute-seam combined-image-sampler binding is the new capability; see the C# side):
//   u0 (binding 0) : RWTexture2D<rgba8> Output        — the exact-rect destination storage image (General layout)
//   t0/s0 (binding 1): Texture2D Source + SamplerState — the source, sampled with a CLAMP sampler of the chosen filter
//   push_constant ResampleParams
// One invocation per output pixel over an 8x8 workgroup (matches the other world kernels).

struct ResampleParams {
    uint2 outExtent;       // destination size in pixels (the pane's pixel rect)
    float2 srcOrigin;      // normalized origin of the source sub-region to sample (0,0 = whole source)
    float2 srcSize;        // normalized size of the source sub-region to sample (1,1 = whole source). A sub-rect
                           // crops the source — e.g. a single window out of a full-desktop capture.
    uint cellSize;         // pixelation cell size in destination pixels; <= 1 = off
    uint quantizeLevels;   // per-channel color levels (e.g. 8 => 3-bit per channel); <= 1 = off
};
[[vk::push_constant]] ConstantBuffer<ResampleParams> params;

[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Output : register(u0);
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] Texture2D Source : register(t0);
[[vk::combinedImageSampler]] [[vk::binding(1, 0)]] SamplerState SourceSampler : register(s0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if ((id.x >= params.outExtent.x) || (id.y >= params.outExtent.y)) {
        return;
    }

    // Destination pixel center, optionally snapped to a pixelation cell center (single-pass low-res look).
    float2 dstPixel = (float2(id.xy) + 0.5);

    if (params.cellSize > 1u) {
        float cell = (float)params.cellSize;

        dstPixel = ((floor(float2(id.xy) / cell) * cell) + (cell * 0.5));
    }

    // Map the destination pixel into the source sub-region's normalized UV, then sample at LOD 0 (compute has no
    // implicit derivative). The bound sampler's filter (LINEAR or NEAREST) decides smooth-fit vs blocky-pixelation.
    float2 dstUv = (dstPixel / float2(params.outExtent));
    float2 srcUv = (params.srcOrigin + (dstUv * params.srcSize));

    float3 color = saturate(Source.SampleLevel(SourceSampler, srcUv, 0.0).rgb);

    // Optional per-channel color-depth reduction (the retro palette look). A single levels value here; an
    // authentic 3-3-2 split (more green levels than blue) is a later per-channel refinement.
    if (params.quantizeLevels > 1u) {
        float levels = (float)params.quantizeLevels;

        color = (round(color * (levels - 1.0)) / (levels - 1.0));
    }

    Output[id.xy] = float4(color, 1.0);
}
