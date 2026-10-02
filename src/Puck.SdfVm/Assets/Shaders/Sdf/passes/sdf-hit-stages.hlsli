// The hit passes' stages, one per pass over the pixel's visibility record: primary marches and writes its V, C and L
// rows (march/sdf-primary.hlsli), surface its N and S rows and ambient its occlusion (surface/sdf-surface.hlsli), shadow
// its K row (surface/sdf-shadow.hlsli), and views shades the record (sdfViewsStage below). Each kernel compiles only its
// own stage.
#ifndef PASSES_SDF_HIT_STAGES_HLSLI
#define PASSES_SDF_HIT_STAGES_HLSLI
#ifdef SDF_PART_RAY_BOUNDS
#include "../march/sdf-part-bounds.hlsli"
#endif
#include "../frame/sdf-mesh-textures.hlsli"
#include "../march/sdf-primary.hlsli"
#include "../surface/sdf-surface.hlsli"
#include "../surface/sdf-shadow.hlsli"
#include "../shade/sdf-light-stage.hlsli"
#include "../debug/sdf-debug-views.hlsli"

#ifdef SDF_VIEWS_PASS
// The views stage: the pixel's light stage over its surface sample, the bounded volumes composited last, and the debug
// view, with the shaded emission's reactivity and one where a volume covers it. A lane past the render
// extent returns black, which the caller never stores.
float3 sdfViewsStage(SdfPixel p, out float reactivity) {
    reactivity = 0.0;

    if (!p.active) {
        return float3(0.0, 0.0, 0.0);
    }

    SdfSurfaceSample s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));

    // The query tally the evals heatmap reads, from the marches every stage before this one made.
    sdfEvalCount = s.queries;

    float3 color = sdfLightStage(p, s, reactivity);

#ifdef SDF_SCREEN_SOURCES
    // The bounded emissive volumes composite after the surface or sky color is final, and never paint through solid
    // geometry: each is clipped to the span from the near plane to the hit distance, or to the far distance on a miss.
    float covered;
    color = shadeVolumes(color, p.rayOrigin, p.rayDirection, worldRayDistanceAt(p.view, p.rayDirection, worldNearDistance(p.view)), (s.hit ? s.t : p.farDistance), p.pixel, covered);
    reactivity = max(reactivity, covered);
#endif

    return sdfDebugView(p, s, color);
}
#endif

#endif
