// The frame graph's one placement pass: reconstructs its source image into a destination rect over its base image.
// Outside the rect the destination is the base, or, with letterbox set, the letterbox color. At equal extent, zero
// sharpness copies exactly and positive sharpness applies a contrast-adaptive, range-clamped five-tap sharpen. Otherwise
// sharpness 0 is bilinear over the four nearest texels, and sharpness 1 is Catmull-Rom over the sixteen nearest, clamped
// to the central four texels' range so its negative lobes cannot ring; a sharpness between blends the two. A rect of the
// whole destination resamples the whole source. Every tap is a formatted load clamped to its image's edge, so no sampler
// state decides the filter and both backends compute the same arithmetic. With tonemap set, the reconstructed source,
// and only it, goes through the filmic curve: a world's root places its SDF views this way, so the scene is tonemapped
// where it enters the frame and the letterbox color, the base and every pane reach the display as they are.
// The generated interface declares the frame group and the pass group: the extent, the config (letterbox, rect,
// sharpness, tonemap, compareMode, wipe) and the images base, source and destination, each image input with a sampler it never reads. rect
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

// Equal-extent sharpening belongs to placement, after resolve. A shared RGB gain falls as local contrast rises;
// clipping to the five taps' channel ranges prevents ringing and preserves flat colors, including HDR values.
float3 sharpenColor(uint2 pixel, uint2 sourceDims) {
    float3 center = source.Load(int3(pixel, 0)).rgb;
    if (passGroup.sharpness == 0.0) {
        return center;
    }
    int2 high = int2(sourceDims) - 1;
    int2 p = int2(pixel);
    float3 left = source.Load(int3(clamp(p + int2(-1, 0), int2(0, 0), high), 0)).rgb;
    float3 right = source.Load(int3(clamp(p + int2(1, 0), int2(0, 0), high), 0)).rgb;
    float3 above = source.Load(int3(clamp(p + int2(0, -1), int2(0, 0), high), 0)).rgb;
    float3 below = source.Load(int3(clamp(p + int2(0, 1), int2(0, 0), high), 0)).rgb;
    float3 lowColor = min(center, min(min(left, right), min(above, below)));
    float3 highColor = max(center, max(max(left, right), max(above, below)));
    float3 span = highColor - lowColor;
    float contrast = saturate(max(span.r, max(span.g, span.b)) / max(0.0001, max(highColor.r, max(highColor.g, highColor.b))));
    float gain = passGroup.sharpness / (1.0 + contrast);
    return clamp(center + (center - (left + right + above + below) * 0.25) * gain, lowColor, highColor);
}

// A comparison source already contains its held seat crop. The base still contains the whole live display.
// All reads clamp inside their own crop, including split's resampling at a nonzero seat origin.
float3 compareColor(uint2 pixel, uint2 rectDims, uint2 baseDims, uint2 sourceDims) {
    float4 rect = passGroup.rect;
    uint2 liveMin = (uint2)clamp(floor((rect.xy * float2(baseDims)) + 0.5), float2(0.0, 0.0), float2(baseDims - 1));
    uint2 liveMax = (uint2)clamp(floor(((rect.xy + rect.zw) * float2(baseDims)) + 0.5), float2(liveMin + 1), float2(baseDims));
    uint2 liveDims = liveMax - liveMin;

    if (passGroup.compareMode == 2u) {
        uint leftWidth = rectDims.x / 2u;
        if (pixel.x < leftWidth) {
            return puckReconstruct(source, pixel, uint2(leftWidth, rectDims.y), sourceDims, 0.0).rgb;
        }
        return puckReconstructRegion(base, uint2(pixel.x - leftWidth, pixel.y),
            uint2(rectDims.x - leftWidth, rectDims.y), liveDims, liveMin, 0.0).rgb;
    }

    float3 held = puckReconstruct(source, pixel, rectDims, sourceDims, 0.0).rgb;
    float3 live = puckReconstructRegion(base, pixel, rectDims, liveDims, liveMin, 0.0).rgb;
    if (passGroup.compareMode == 3u) {
        // The held PNG is SDR. Compare it against the same clamped display range, retaining no HDR headroom.
        return abs(held - saturate(live));
    }
    return ((float(pixel.x) + 0.5) < (passGroup.wipe * float(rectDims.x))) ? held : live;
}

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
            written = (passGroup.compareMode == 0u)
                ? base.Load(int3(min(id.xy, (baseDims - 1)), 0))
                : puckReconstruct(base, id.xy, destinationDims, baseDims, 0.0);
        }
    } else if (passGroup.compareMode != 0u) {
        base.GetDimensions(baseDims.x, baseDims.y);
        source.GetDimensions(sourceDims.x, sourceDims.y);
        written = float4(compareColor((id.xy - rectMin), (rectMax - rectMin), baseDims, sourceDims), 1.0);
    } else {
        source.GetDimensions(sourceDims.x, sourceDims.y);

        uint2 rectDims = rectMax - rectMin;
        float3 color = all(rectDims == sourceDims)
            ? sharpenColor((id.xy - rectMin), sourceDims)
            : puckReconstruct(source, (id.xy - rectMin), rectDims, sourceDims, passGroup.sharpness).rgb;

        written = float4(((passGroup.tonemap != 0u) ? filmicTonemap(color) : color), 1.0);
    }

    destination[id.xy] = written;
    // Every pixel inside the destination writes its texel.
    puckCountWork(0u, 1u);
}
