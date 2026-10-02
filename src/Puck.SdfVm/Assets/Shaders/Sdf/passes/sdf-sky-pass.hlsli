// What the sky's field runs (sdf-sky-runs.comp.hlsl) and the composite (sdf-composite.comp.hlsl) share: the sky interface
// (SdfWorldPackage.SkyMembers), one invocation an output pixel, and the reads of what the views or the resolve left for
// them. A native view's lit image and its visibility records are views', current only inside the dispatch box cull-args
// wrote, outside which the beam proved every tile empty: a pixel there reads as a miss. A reduced or temporal view's are
// the resolve's at the output extent, written for every pixel (passGroup.resolvedSurface). A pixel's sky direction is
// its unjittered one, so the sky never moves with a temporal view's samples.
#ifndef PASSES_SDF_SKY_PASS_HLSLI
#define PASSES_SDF_SKY_PASS_HLSLI
#define SDF_DYNAMIC_TRANSFORMS
#include "../isa/sdf-sky.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-sky.hlsli"

// Whether the lit image and the surface distance at a pixel were written this frame.
bool sdfSkyPassCurrent(uint2 pixel) {
    return ((passGroup.resolvedSurface != 0u) || SDF_VISIBILITY_CURRENT(pixel, cullBounds));
}
// The lit image at a pixel, clamped to the output: the color premultiplied by its coverage, the coverage in alpha, and
// nothing where the pixel was not written this frame.
float4 sdfSkyPassLit(int2 pixel) {
    uint2 clamped = (uint2)clamp(pixel, int2(0, 0), (int2(passGroup.extent) - 1));

    return (sdfSkyPassCurrent(clamped) ? lit.Load(int3(clamped, 0)) : float4(0.0, 0.0, 0.0, 0.0));
}
// The ray distance of the surface a pixel shows, or zero for a miss.
float sdfSkyPassSurfaceDistance(uint2 pixel) {
    if (passGroup.resolvedSurface != 0u) {
        return surfaceDistance[((pixel.y * passGroup.extent.x) + pixel.x)];
    }
    if (!SDF_VISIBILITY_CURRENT(pixel, cullBounds)) {
        return 0.0;
    }

    SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent));

    return (sdfVisibilityHit(visibility) ? visibility.t : 0.0);
}
// The pixel's view without the sample's jitter.
ViewportData sdfSkyPassView() {
    ViewportData view = worldView();

    view.lens.yz = passGroup.frustumOffset;

    return view;
}
// The pixel's unjittered sky direction.
float3 sdfSkyPassDirection(ViewportData view, uint2 pixel) {
    return cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(passGroup.extent)));
}
#endif
