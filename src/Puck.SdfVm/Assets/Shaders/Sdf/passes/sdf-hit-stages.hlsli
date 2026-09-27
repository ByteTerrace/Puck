// The hit passes' stages, one per pass over the pixel's visibility record: primary marches and writes its V, C and L
// rows (march/sdf-primary.hlsli), surface its N and S rows and ambient its occlusion (surface/sdf-surface.hlsli), and views
// shades the record (sdfViewsStage below). Each kernel compiles only its own stage.
#ifndef PASSES_SDF_HIT_STAGES_HLSLI
#define PASSES_SDF_HIT_STAGES_HLSLI
#ifdef SDF_PART_RAY_BOUNDS
#include "../march/sdf-part-bounds.hlsli"
#endif
#include "../frame/sdf-mesh-textures.hlsli"
#include "../march/sdf-primary.hlsli"
#include "../surface/sdf-surface.hlsli"
#include "../shade/sdf-light-stage.hlsli"
#include "../debug/sdf-debug-views.hlsli"

#ifdef SDF_VIEWS_PASS
// The views stage: the workgroup's shadow gather, which every lane reaches, the pixel's light stage over its surface
// sample, the bounded volumes composited last, and the debug view. A lane past the render extent returns black, which the
// caller never stores.
float3 sdfViewsStage(SdfPixel p) {
    SdfSurfaceSample s = (SdfSurfaceSample)0;

    s.t = max(p.marchStart, 0.0);

    if (p.active) {
        s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));
    }

    // The query tally the evals heatmap reads, from the marches every stage before this one made.
    sdfEvalCount = s.queries;

    // The group shadow gather, at the one seam every lane of the workgroup reaches: the lanes' hit points reduce to one
    // shadow candidate mask for all of them (sdfShadowGatherGroup, uniform control flow). Its inputs are uniform: the
    // view's mode, the engine levers, the reach. A lane that is not lit still publishes, as unlit, and walks its share of
    // the grid.
    uint groupGather = 0u;
#ifdef SDF_SCREEN_SOURCES
    bool cullOn = worldShadowCullEnabled();

    groupGather = (cullOn ? 1u : 0u);
#ifdef SDF_GROUP_SHADOW_GATHER
    if (sdfFinalShadingMode(p.viewMode) && cullOn && !worldUseCameraTileShadowMask() && !worldSoftShadowsDisabled()) {
        float groupShadowReach = (ShadowMaxDistance * worldShadowDistanceScale());

        groupGather = sdfShadowGatherGroup((s.hit && !s.mesh), (p.rayOrigin + (p.rayDirection * s.t)), worldSunDirection(), groupShadowReach, p.lane);
    }
#endif
#endif

    if (!p.active) {
        return float3(0.0, 0.0, 0.0);
    }

    float3 color = sdfLightStage(p, s, groupGather);

#ifdef SDF_SCREEN_SOURCES
    // The bounded emissive volumes composite after the surface or sky color is final, and never paint through solid
    // geometry: each is clipped at the hit distance, or at the far distance on a miss.
    color = shadeVolumes(color, p.rayOrigin, p.rayDirection, (s.hit ? s.t : p.farDistance), p.pixel, p.view.position.w);
#endif

    return sdfDebugView(p, s, color);
}
#endif

#endif
