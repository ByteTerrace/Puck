// Reconstruct the active render grid into the view's full output without sampling unrendered ceiling pixels.
#ifdef SDF_TEMPORAL_RESOLVE
#include "../isa/sdf-temporal-resolve.interface.hlsli"
#else
#include "../isa/sdf-resolve.interface.hlsli"
#endif
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli"
#include "../frame/sdf-resolved-surface.hlsli"
#include "../frame/sdf-work.hlsli"
#ifdef SDF_TEMPORAL_RESOLVE
#include "../frame/sdf-temporal-resolve.hlsli"
#endif

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;
    if (any(id.xy >= extent)) return;
#ifdef SDF_TEMPORAL_RESOLVE
    float2 position = ((float2(id.xy) + 0.5) * float2(passGroup.imageExtent) / float2(extent)) - 0.5 - passGroup.jitter;
    uint2 nearestPixel = id.xy;
    uint2 surface = (all(extent == passGroup.imageExtent) && all(passGroup.jitter == 0.0))
        ? sdfRenderSurface(id.xy, passGroup.imageExtent) : sdfNearestSurfaceAt(position, passGroup.imageExtent, nearestPixel);
    float4 current = all(passGroup.jitter == 0.0)
        ? puckReconstruct(currentColor, id.xy, extent, passGroup.imageExtent, passGroup.upscaleSharpness)
        : puckReconstructAt(currentColor, position, passGroup.imageExtent, passGroup.upscaleSharpness);
    output[id.xy] = sdfTemporalResolve(id.xy, position, nearestPixel, surface, current);
    sdfWorkTexels = 1u;
    resolvedSurface[id.y * extent.x + id.x] = surface;
#else
    output[id.xy] = puckReconstruct(currentColor, id.xy, extent, passGroup.imageExtent, passGroup.upscaleSharpness);
    sdfWorkTexels = 1u;
    resolvedSurface[id.y * extent.x + id.x] = sdfNearestSurface(id.xy, extent, passGroup.imageExtent);
#endif
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
