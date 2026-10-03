// The hit passes' shared entry point: primary, surface, ambient, shadow and views each select their pass macro and compile
// their own stage (sdf-hit-stages.hlsli) over the pixel the entry point gathers. All five use an 8x8 workgroup over the
// one indirect tile box, and the same camera, masks and active-pixel test. Primary also reads the mesh pass's target
// (sdf-mesh.hlsli). Every hit pass reads its resources through the sdf-world interface: dynamic transforms, screen sources,
// and the read-only instance mask instance-cull produced (sdfInstanceMasks). Primary, surface, ambient and shadow write
// the visibility records through sdfVisibilityRecordsRW; views reads them through sdfVisibilityRecords
// (sdf-visibility.hlsli).
// Unused shading resources compile out of primary traversal.
#define SDF_DYNAMIC_TRANSFORMS
#ifndef SDF_PRIMARY_PASS
#define SDF_PRIMARY_READ
#if !defined(SDF_SURFACE_PASS) && !defined(SDF_AMBIENT_PASS) && !defined(SDF_SHADOW_PASS)
#define SDF_VIEWS_PASS
#endif
#endif
#define SDF_FRAME_INSTANCE_GRID
#define SDF_INSTANCE_MASKS
#define SDF_SCREEN_SOURCES
// Primary and views sample the glyph atlas, so primary marches true lettering.
// The beam and other non-atlas kernels retain the conservative cell box.
#define SDF_GLYPH_ATLAS
// The brick pool: primary and shading sample baked SampledRegion carves O(1), so the primary, shadow and occlusion marches
// do not pay for every carve. All views variants inherit it.
#define SDF_SAMPLED_REGIONS
// The bounded volumes are in the shared interface, but only the composite pass integrates them (shade-volumes.hlsli).
// The per-group ambient and shadow gathers (surface/sdf-shadow-gather.hlsli): one groupshared candidate mask per 8x8
// workgroup, built cooperatively at a uniform seam of the ambient and shadow stages. Every lane, rendered pixel or not,
// reaches its stage, so the entry point turns the per-pixel extent test into the pixel's `active` flag instead of a
// return. Primary, surface and views gather nothing and hold no groupshared mask.
#if defined(SDF_AMBIENT_PASS) || defined(SDF_SHADOW_PASS)
#define SDF_GROUP_SHADOW_GATHER
#endif
#define SDF_PART_RAY_BOUNDS
// Every hit pass reads the beam's tile planes and part bounds through tiles, and the surviving-tile box from the cull-args
// pass through cullBounds (sdf-cull-args.comp): its group origin, then its exclusive group end. The dispatch is
// origin-anchored, so the origin offsets each invocation onto the box's pixels, and the all-empty margins outside the box
// are never dispatched; the whole box is where this frame wrote visibility records, which worldVisibilityCurrent reads.
#include "sdf-world.hlsli"

[numthreads(SDF_VISIBILITY_BOX_EDGE, SDF_VISIBILITY_BOX_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

    // The indirect dispatch covers the surviving-tile box anchored at (0,0); its group origin, in pixels, moves this
    // invocation onto the box's pixel rather than the frame's top-left.
    uint2 pixel = ((uint2(cullBounds[0], cullBounds[1]) * SDF_VISIBILITY_BOX_EDGE) + id.xy);

    ViewportData view = worldView();

    // The symmetry-LOD origin: this viewport's camera (the per-sample wallpaper LOD rule measures from it).
    sdfLodOrigin = view.position.xyz;
    // The per-invocation program-layout cache (field/sdf-layout.hlsli), decoded once before the stage's field queries.
    sdfProgramLayout = sdfLoadProgramLayout();
    sdfPartBoundsViewport = viewIndex;

    SdfPixel p = sdfPixelAt(view, pixel, viewIndex);

#if defined(SDF_PRIMARY_PASS)
    sdfPrimaryStage(p);
#elif defined(SDF_SURFACE_PASS)
    sdfSurfaceStage(p);
#elif defined(SDF_AMBIENT_PASS)
    sdfAmbientStage(p);
#elif defined(SDF_SHADOW_PASS)
    sdfShadowStage(p);
#else
    float coverage;
    float reactivity;
    float3 color = sdfViewsStage(p, coverage, reactivity);

    if (p.active) {
        // The lit image: the float working color premultiplied by the pixel's coverage, the coverage in its alpha, which
        // the composite puts over the sky.
        output[pixel] = float4((color * coverage), coverage);
        sdfWorkTexels = 1u;
        if (passGroup.temporal != 0u) {
            reactivityRW[sdfReactivityIndex(pixel, viewIndex, passGroup.imageExtent)] = reactivity;
        }
    }
#endif

    // A hit pass counts a pixel whose visibility record it stored and views a pixel whose texel it wrote.
#ifdef SDF_SHADOW_PASS
    puckCountDetail(0u, sdfWorkSteps, sdfWorkTexels, 0u, 0u, 0u);
#else
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
#endif
}
