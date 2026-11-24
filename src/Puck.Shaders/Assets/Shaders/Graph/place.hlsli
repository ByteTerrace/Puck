// The frame graph's placement kernel, for the two packages that include it after their generated interface (place and
// place.views, which declare the same members): it reconstructs each of its sources into that source's destination rect
// over its base image, writing every destination pixel once. A pixel inside several rects shows the last source whose
// rect holds it, as a chain of passes placing one source each would. Outside every rect the destination is the base, or,
// with letterbox set, the letterbox color. Inside a rect, a source of the rect's own extent is an exact copy; otherwise
// sharpness 0 is bilinear over the four nearest texels, and sharpness 1 is Catmull-Rom over the sixteen nearest, clamped
// to the central four texels' range so its negative lobes cannot ring; a sharpness between blends the two. A rect of the
// whole destination resamples the whole source. Every tap is a formatted load clamped to its image's edge, so no sampler
// state decides the filter and both backends compute the same arithmetic. A crop shows only that part of the source in
// the rect, by the same rules, so a crop of the rect's own pixel extent is an exact copy of those pixels. Every image it
// reads and writes is a working image, already encoded for the display: a world's view reaches it converted by its own
// graph's display pass, so placing changes no value's meaning.
// The generated interface declares the frame group and the pass group: the extent, the config (letterbox,
// letterboxColor, rect, crop, sharpness, compareMode, wipe), what the recorder writes each frame (sources, the count it
// places, and rects, crops and sharpnesses, a row a source, from the host's placements or the config) and the images
// base, source (PUCK_PLACE_SOURCES of them) and destination, each image input with a sampler it never reads. A rect is
// a destination rect as fractions of the destination's extent and a crop the shown part as fractions of the source's:
// left, top, width, height. The comparison modes read the first source alone.
#ifndef PUCK_PLACE_HLSLI
#define PUCK_PLACE_HLSLI
#include "Shared/reconstruction.hlsli"

// The length of the source array and of the pass block's rows: RenderGraphPackageCatalog.PlaceSourceCount.
#define PUCK_PLACE_SOURCES 16u

// A comparison source already contains its held seat crop. The base still contains the whole live display.
// All reads clamp inside their own crop, including split's resampling at a nonzero seat origin.
float3 compareColor(uint2 pixel, uint2 rectDims, uint2 baseDims, uint2 sourceDims) {
    float4 rect = passGroup.rects[0];
    uint2 liveMin = (uint2)clamp(floor((rect.xy * float2(baseDims)) + 0.5), float2(0.0, 0.0), float2(baseDims - 1));
    uint2 liveMax = (uint2)clamp(floor(((rect.xy + rect.zw) * float2(baseDims)) + 0.5), float2(liveMin + 1), float2(baseDims));
    uint2 liveDims = liveMax - liveMin;

    if (passGroup.compareMode == 2u) {
        uint leftWidth = rectDims.x / 2u;
        if (pixel.x < leftWidth) {
            return puckReconstruct(source[0], pixel, uint2(leftWidth, rectDims.y), sourceDims, 0.0).rgb;
        }
        return puckReconstructRegion(base, uint2(pixel.x - leftWidth, pixel.y),
            uint2(rectDims.x - leftWidth, rectDims.y), liveDims, liveMin, 0.0).rgb;
    }

    float3 held = puckReconstruct(source[0], pixel, rectDims, sourceDims, 0.0).rgb;
    float3 live = puckReconstructRegion(base, pixel, rectDims, liveDims, liveMin, 0.0).rgb;
    if (passGroup.compareMode == 3u) {
        // The held PNG is SDR. Compare it against the same clamped display range, retaining no HDR headroom.
        return abs(held - saturate(live));
    }
    return ((float(pixel.x) + 0.5) < (passGroup.wipe * float(rectDims.x))) ? held : live;
}

// A rect's pixel edges in a destination of `dims`, each fraction rounded to the nearest pixel edge and clamped to it.
void placeRectPixels(float4 rect, uint2 dims, out uint2 low, out uint2 high) {
    low = (uint2)clamp(floor((rect.xy * float2(dims)) + 0.5), float2(0.0, 0.0), float2(dims));
    high = (uint2)clamp(floor(((rect.xy + rect.zw) * float2(dims)) + 0.5), float2(low), float2(dims));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 destinationDims;
    uint2 baseDims;
    uint2 sourceDims;
    uint texels = 0u;

    destination.GetDimensions(destinationDims.x, destinationDims.y);
    if ((id.x < destinationDims.x) && (id.y < destinationDims.y)) {
        // The last source whose rect holds the pixel, and that rect's pixel edges.
        uint count = min(passGroup.sources, PUCK_PLACE_SOURCES);
        uint placed = PUCK_PLACE_SOURCES;
        uint2 rectMin = uint2(0u, 0u);
        uint2 rectMax = uint2(0u, 0u);

        [loop] for (uint candidate = 0u; (candidate < count); candidate++) {
            uint2 low;
            uint2 high;

            placeRectPixels(passGroup.rects[candidate], destinationDims, low, high);
            if (all(id.xy >= low) && all(id.xy < high)) {
                placed = candidate;
                rectMin = low;
                rectMax = high;
            }
        }

        float4 written;

        if (placed == PUCK_PLACE_SOURCES) {
            if (passGroup.letterbox != 0u) {
                written = passGroup.letterboxColor;
            } else {
                base.GetDimensions(baseDims.x, baseDims.y);
                written = (passGroup.compareMode == 0u)
                    ? base.Load(int3(min(id.xy, (baseDims - 1)), 0))
                    : puckReconstruct(base, id.xy, destinationDims, baseDims, 0.0);
            }
        } else if (passGroup.compareMode != 0u) {
            base.GetDimensions(baseDims.x, baseDims.y);
            source[0].GetDimensions(sourceDims.x, sourceDims.y);
            written = float4(compareColor((id.xy - rectMin), (rectMax - rectMin), baseDims, sourceDims), 1.0);
        } else {
            source[placed].GetDimensions(sourceDims.x, sourceDims.y);

            // The crop's pixel edges, rounded as the rect's are: the whole source unless the host shows a part of it.
            float4 crop = passGroup.crops[placed];
            uint2 cropMin = (uint2)clamp(floor((crop.xy * float2(sourceDims)) + 0.5), float2(0.0, 0.0), float2(sourceDims - 1));
            uint2 cropMax = (uint2)clamp(floor(((crop.xy + crop.zw) * float2(sourceDims)) + 0.5), float2(cropMin + 1), float2(sourceDims));
            uint2 cropDims = (cropMax - cropMin);
            uint2 rectDims = (rectMax - rectMin);
            uint2 pixel = (id.xy - rectMin);
            float4 sharpnesses = passGroup.sharpnesses[placed >> 2u];
            uint lane = (placed & 3u);
            float sharpness = ((lane == 0u) ? sharpnesses.x : ((lane == 1u) ? sharpnesses.y : ((lane == 2u) ? sharpnesses.z : sharpnesses.w)));

            written = float4(puckReconstructRegion(source[placed], pixel, rectDims, cropDims, cropMin, sharpness).rgb, 1.0);
        }

        destination[id.xy] = written;
        texels = 1u;
    }
    // Every pixel inside the destination writes its texel.
    puckCountWork(0u, texels);
}

#endif
