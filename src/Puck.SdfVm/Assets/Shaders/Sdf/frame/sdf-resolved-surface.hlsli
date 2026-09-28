// Surface reconstruction never filters a distance or an identity. The closest hit among the spatial filter's
// color taps supplies both exact words. Empty/culled samples supply the background sentinel.
#ifndef SDF_RESOLVED_SURFACE_HLSLI
#define SDF_RESOLVED_SURFACE_HLSLI
#include "sdf-visibility.hlsli"
#include "sdf-visibility-current.hlsli"

uint2 sdfRenderSurface(uint2 pixel, uint2 extent) {
    if (!worldVisibilityCurrent(pixel)) return uint2(asuint(passGroup.farDistance), 0u);
    SdfVisibility hit = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, extent));
    return uint2(asuint(hit.identity == 0u ? passGroup.farDistance : hit.t), hit.identity);
}
uint2 sdfNearestSurfaceAt(float2 position, uint2 renderExtent, out uint2 nearestPixel) {
    nearestPixel = (uint2)clamp(position + 0.5, 0.0, float2(renderExtent) - 1.0);
    int2 origin = (int2)clamp(position, 0.0, float2(renderExtent) - 1.0);
    int first = passGroup.upscaleSharpness > 0.0 ? -1 : 0;
    int last = passGroup.upscaleSharpness > 0.0 ? 2 : 1;
    uint2 nearest = uint2(asuint(passGroup.farDistance), 0u);
    for (int y = first; y <= last; y++) {
        for (int x = first; x <= last; x++) {
            uint2 tap = (uint2)clamp(origin + int2(x, y), int2(0, 0), int2(renderExtent) - 1);
            uint2 candidate = sdfRenderSurface(tap, renderExtent);
            if ((candidate.y != 0u) && ((nearest.y == 0u) || (asfloat(candidate.x) < asfloat(nearest.x)))) { nearest = candidate; nearestPixel = tap; }
        }
    }
    return nearest;
}
uint2 sdfNearestSurface(uint2 pixel, uint2 outputExtent, uint2 renderExtent) {
    if (all(outputExtent == renderExtent)) return sdfRenderSurface(pixel, renderExtent);
    float2 position = (((float2(pixel) + 0.5) * float2(renderExtent) / float2(outputExtent)) - 0.5);
    uint2 nearestPixel;
    return sdfNearestSurfaceAt(position, renderExtent, nearestPixel);
}
// The downstream sky/fog route uses this same surface contract at native and resolved extents.
uint2 sdfSurfaceAt(uint2 pixel, uint2 extent) {
#ifdef SDF_READ_RESOLVED_SURFACE
    return resolvedSurface[pixel.y * extent.x + pixel.x];
#else
    return sdfRenderSurface(pixel, extent);
#endif
}
#endif
