// Reconstruct the active render grid into the view's full output without sampling unrendered ceiling pixels.
#include "../isa/sdf-resolve.interface.hlsli"
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli"
#include "../frame/sdf-resolved-surface.hlsli"
#include "../frame/sdf-work.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;
    if (any(id.xy >= extent)) return;
    output[id.xy] = puckReconstruct(currentColor, id.xy, extent, passGroup.imageExtent, passGroup.upscaleSharpness);
    sdfWorkTexels = 1u;
    resolvedSurface[id.y * extent.x + id.x] = sdfNearestSurface(id.xy, extent, passGroup.imageExtent);
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
