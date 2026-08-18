// Sky pre-pass: fills every pixel of each SDF viewport's render-dims source texture with the authored sky BEFORE
// sdf-beam.comp/sdf-world-views.comp run. Dispatched directly (never DispatchIndirect) over a plain
// (renderDims.x, renderDims.y, viewportCount) grid with no cull-bounds offset — the beam cull hasn't run yet, so
// there is no bbox to restrict to, and restricting to one would defeat the point: a beam-culled tile's source pixel
// is otherwise never touched by Stage 1 at all, so this pass is the only writer that reaches it. A frame whose beam
// later proves every tile live still runs this pass — the redundant write on a live tile's pixel is thrown away the
// moment Stage 1 overwrites it moments later; a conditional dispatch would save nothing worth the branch.
//
// SHARES Stage 1's descriptor-set layout: SdfWorldEngine builds this kernel's pipeline from the SAME bindings array
// sdf-world-views.comp.hlsl uses, so it binds against the SAME per-slot descriptor set Stage 1 already has — no new
// descriptor set, no second binding layout. Only `viewports` (binding 2) and `sdfScreenLights` (binding 11, the
// sky/lighting rows SdfWorldEngine.PackSkyFrame writes) are actually read; every other slot in the shared layout
// goes untouched here. SDF_SCREEN_SOURCES is required even though this kernel never samples a screen source: it is
// the only configuration under which sdfScreenLights — and the real (non-pinned-literal) skyColor/lighting
// accessors — are declared at all (sdf-world.hlsli's #else half returns the pinned defaults unconditionally, which
// would make worldSkyEnabled() always false here). SDF_DYNAMIC_TRANSFORMS is required too: sdf-world.hlsli's
// shadow-gather body (unconditionally compiled, unreached from this kernel's CSMain) references
// sdfInstanceShadowSuppressed, whose declaration in sdf-vm.hlsli is itself gated on this macro.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_SCREEN_SOURCES
#include "sdf-world.hlsli"

// The per-view source textures — the SAME binding/register as sdf-world-views.comp.hlsl's own declaration (binding
// 4, register u1), so the shared views layout resolves identically regardless of which of the two kernels is bound.
[[vk::binding(4, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> sources[5] : register(u1);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (id.z >= params.viewportCount) {
        return;
    }

    // A child viewport shows another node's surface — there is no SDF camera and no source texture of this engine's
    // own to write; sdf-world-composite.comp copies the child's own image untouched, exactly as before.
    if (isChildViewport(id.z)) {
        return;
    }

    ViewportData view = viewports[id.z];
    uint2 rectDims = worldRenderDims((uint2)(view.region.zw * float2(params.imageExtent)), view.renderScale.x);

    if ((id.x >= rectDims.x) || (id.y >= rectDims.y)) {
        return;
    }

    float2 localUv = ((float2(id.xy) + 0.5) / float2(rectDims));
    float3 rayDirection = cameraRayDirection(view, localUv);
    float3 color = skyColor(rayDirection);

    // The SAME dither, on the SAME render-space pixel coordinate Stage 1 dithers its own miss-branch sky with
    // (sdf-world-views.comp.hlsl's `pixel`, which for this kernel's un-offset dispatch is exactly id.xy) — so a sky
    // pixel this pass alone produces (a beam-culled tile) and a sky pixel Stage 1's own miss branch produces (a live
    // tile's ray that clears the field) are bit-identical, and a screenshot across the tile seam shows no step.
    color += ((sdfR2Dither(id.xy) - 0.5) * DitherQuantum);

    // Read-modify-write to preserve the alpha lane: it carries the soft-shadow temporal accumulator's history
    // (sdf-world-views.comp.hlsl's sdfShadowHistoryIn), which Stage 1 reads from this same texture immediately after
    // this pass runs. Overwriting it here would hand Stage 1 this frame's freshly written value instead of the
    // prior frame's real history on every live-tile pixel, resetting shadow accumulation every frame. A tile that
    // stays empty this frame keeps whatever alpha its last live frame left, unchanged.
    sources[id.z][id.xy] = float4(color, sources[id.z][id.xy].a);
}
