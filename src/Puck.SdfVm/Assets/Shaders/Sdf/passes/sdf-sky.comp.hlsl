// Sky pre-pass: fills every pixel of the set's view's output image with the authored sky BEFORE
// sdf-beam.comp/sdf-world-views.comp run. Dispatched directly (never DispatchIndirect) over the engine's whole extent,
// bounds-checked against the view's render extent, with no cull-bounds offset — the beam cull hasn't run yet, so
// there is no bbox to restrict to, and restricting to one would defeat the point: a beam-culled tile's output pixel
// is otherwise never touched by Stage 1 at all, so this pass is the only writer that reaches it. A frame whose beam
// later proves every tile live still runs this pass — the redundant write on a live tile's pixel is thrown away the
// moment Stage 1 overwrites it moments later; a conditional dispatch would save nothing worth the branch.
//
// SHARES Stage 1's interface: SdfWorldTables builds this kernel's pipeline from sdf-world, as it builds every per-view
// kernel's, so it binds the SAME frame and views sets Stage 1 does — no descriptor set of its own. Reads the view and the
// environment from the pass block, bounded volumes, and the dynamic transforms their frames and optional intensity lanes
// use. SDF_SCREEN_SOURCES is required even though this kernel never samples a screen source: the sky and screen shading
// in shade/sdf-environment.hlsli is declared only under it. SDF_DYNAMIC_TRANSFORMS is required too: surface/sdf-shadow-gather.hlsli's
// shadow-gather body (unconditionally compiled, unreached from this kernel's CSMain) references
// sdfInstanceShadowSuppressed, whose declaration in field/sdf-instance-flags.hlsli is itself gated on this macro.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_SCREEN_SOURCES
#include "sdf-world.hlsli"

// It writes the set's view's output image through output, as Stage 1 does.

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

    ViewportData view = worldView();
    uint2 rectDims = worldViewDims(view);

    if ((id.x >= rectDims.x) || (id.y >= rectDims.y)) {
        return;
    }

    float2 localUv = ((float2(id.xy) + 0.5) / float2(rectDims));
    float3 rayDirection = cameraRayDirection(view, localUv);
    float3 color = skyColor(rayDirection);

    // Empty SDF tiles can still contain participating media. Match the views stage's miss; a live tile replaces
    // this result with its own integration clipped to the surface, so emission is never added twice.
    color = shadeVolumes(color, view.position.xyz, rayDirection, worldRayDistanceAt(view, rayDirection, worldNearDistance(view)), worldFarDistance(view), id.xy, view.position.w);

    output[id.xy] = float4(color, 1.0);
    sdfCountWork(1u);
}
