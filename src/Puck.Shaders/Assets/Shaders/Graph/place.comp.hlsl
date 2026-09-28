// The frame graph's one placement pass: reconstructs its source image into a destination rect over its base image.
// Outside the rect the destination is the base, or, with letterbox set, the letterbox color. Inside it, a source of the rect's own extent is an exact copy; otherwise
// sharpness 0 is bilinear over the four nearest texels, and sharpness 1 is Catmull-Rom over the sixteen nearest, clamped
// to the central four texels' range so its negative lobes cannot ring; a sharpness between blends the two. A rect of the
// whole destination resamples the whole source. Every tap is a formatted load clamped to its image's edge, so no sampler
// state decides the filter and both backends compute the same arithmetic. With tonemap set, the reconstructed source,
// and only it, goes through the filmic curve: a world's root places its SDF views this way, so the scene is tonemapped
// where it enters the frame and the letterbox color, the base and every pane reach the display as they are.
// The generated interface declares the frame group and the pass group: the extent, the config (letterbox, rect,
// sharpness, tonemap) and the images base, source and destination, each image input with a sampler it never reads. rect
// is the destination rect as fractions of the destination's extent: left, top, width, height.
#include "place.interface.hlsli"

// What the display shows between placed views: a near-black with a faint blue cast, opaque.
static const float4 LetterboxColor = float4(0.015, 0.016, 0.02, 1.0);

// The Narkowicz ACES-fit filmic curve, and only the curve: the stylized shading is already display-referred, so no gamma
// encode follows it, and the display encode writes its result as it is.
float3 filmicTonemap(float3 color) {
    return saturate((color * ((2.51 * color) + 0.03)) / (((color * ((2.43 * color) + 0.59)) + 0.14)));
}

#include "../Shared/reconstruction.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 destinationDims;
    uint2 baseDims;
    uint2 sourceDims;

    destination.GetDimensions(destinationDims.x, destinationDims.y);
    if ((id.x >= destinationDims.x) || (id.y >= destinationDims.y)) {
        return;
    }

    // The rect's pixel edges, each fraction rounded to the nearest pixel edge and clamped to the destination.
    float4 rect = passGroup.rect;
    uint2 rectMin = (uint2)clamp(floor((rect.xy * float2(destinationDims)) + 0.5), float2(0.0, 0.0), float2(destinationDims));
    uint2 rectMax = (uint2)clamp(floor(((rect.xy + rect.zw) * float2(destinationDims)) + 0.5), float2(rectMin), float2(destinationDims));

    float4 written;

    if (any(id.xy < rectMin) || any(id.xy >= rectMax)) {
        if (passGroup.letterbox != 0u) {
            written = LetterboxColor;
        } else {
            base.GetDimensions(baseDims.x, baseDims.y);
            written = base.Load(int3(min(id.xy, (baseDims - 1)), 0));
        }
    } else {
        source.GetDimensions(sourceDims.x, sourceDims.y);

        float3 color = puckReconstruct(source, (id.xy - rectMin), (rectMax - rectMin), sourceDims, passGroup.sharpness).rgb;

        written = float4(((passGroup.tonemap != 0u) ? filmicTonemap(color) : color), 1.0);
    }

    destination[id.xy] = written;
    // Every pixel inside the destination writes its texel.
    puckCountWork(0u, 1u);
}
