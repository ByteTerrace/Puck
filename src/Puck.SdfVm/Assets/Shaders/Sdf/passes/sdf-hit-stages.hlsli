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
#include "../shade/sdf-transport.hlsli"
#include "../debug/sdf-debug-views.hlsli"

#ifdef SDF_VIEWS_PASS
// What motion cannot describe in a pixel the light stage shaded, for a temporal view's resolve: one for a screen, whose
// content changes on its own, and for emission, which the material model cannot tell animated from steady, the share of
// the pixel's color it emits.
float sdfSurfaceReactivity(SdfSurfaceSample s, float3 color) {
    if (!s.hit) {
        return 0.0;
    }
    if (s.material >= SDF_SCREEN_MATERIAL) {
        return 1.0;
    }

    SdfMaterialData material = sdfMaterialLoad(s.material);

    if (material.emissive <= 0.0) {
        return 0.0;
    }

    static const float3 Luma = float3(0.2126, 0.7152, 0.0722);

    return saturate(dot((material.albedo * material.emissive), Luma) / max(dot(color, Luma), 1.0e-4));
}
// The views stage: the pixel's light stage over its surface sample and the debug view, with the pixel's coverage and
// reactivity (sdfSurfaceReactivity). A hit's color leaves through the fog's transmittance over its own ray distance
// (sdf-transport.hlsli), so the resolve filters it with the coverage as one premultiplied quantity; the fog's in-scatter,
// the sky and the bounded volumes are the composite's, so a moving medium never enters a temporal view's history. A
// debug view draws the whole pixel, so it covers it and is not fogged. A lane past the render extent returns black,
// which the caller never stores.
float3 sdfViewsStage(SdfPixel p, out float coverage, out float reactivity) {
    coverage = 0.0;
    reactivity = 0.0;

    if (!p.active) {
        return float3(0.0, 0.0, 0.0);
    }

    SdfSurfaceSample s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));

    // The query tally the evals heatmap reads, from the marches every stage before this one made.
    sdfEvalCount = s.queries;

    float3 color = sdfLightStage(p, s, coverage);

    reactivity = sdfSurfaceReactivity(s, color);

    if (p.viewMode != 0) {
        coverage = 1.0;

        return sdfDebugView(p, s, color);
    }

    return (sdfDebugView(p, s, color) * (s.hit ? sdfFogTransmittance(s.t) : 1.0));
}
#endif

#endif
