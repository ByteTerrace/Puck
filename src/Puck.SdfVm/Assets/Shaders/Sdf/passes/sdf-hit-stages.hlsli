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
#include "../frame/sdf-mesh-impostor-surface.hlsli"
#include "../march/sdf-primary.hlsli"
#include "../surface/sdf-surface.hlsli"
#include "../surface/sdf-shadow.hlsli"
#include "sdf-light-stage.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "../debug/sdf-debug-views.hlsli"

#ifdef SDF_VIEWS_PASS
// The views stage: the pixel's light stage over its surface sample and the debug view, with the pixel's coverage and
// reactivity (sdfLightStage). A hit's color leaves through the atmosphere's transmittance over its own ray
// (sdf-transport.hlsli), so the resolve filters it with the coverage as one premultiplied quantity; the atmosphere's
// in-scatter, the sky and the bounded volumes are the composite's, so a moving medium never enters a temporal view's
// history. A surface debug view draws the whole pixel, so it covers it and is not fogged. Sky cost retains the surface's real
// coverage so the composite can measure the visible sky. A lane past the render extent returns black,
// which the caller never stores.
float3 sdfViewsStage(SdfPixel p, out float coverage, out float reactivity) {
    coverage = 0.0;
    reactivity = 0.0;

    if (!p.active) {
        return float3(0.0, 0.0, 0.0);
    }

    SdfSurfaceSample s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));
    uint record = worldVisibilityRecord(p.pixel, p.viewIndex);
    if (!worldAoDisabled()) s.surfaceQueries += float(sdfVisibilityRecordBuffer[record + SDF_VISIBILITY_AMBIENT_QUERIES_WORD]);
    if (!worldSoftShadowsDisabled() && passGroup.shadowSlotCount > 0u) s.surfaceQueries += float(sdfVisibilityRecordBuffer[record + SDF_VISIBILITY_SHADOW_QUERIES_WORD]);

    // The query tally the evals heatmap reads, from the marches every stage before this one made.
    sdfEvalCount = s.queries;

    float3 color = sdfLightStage(p, s, coverage, reactivity);
    // Shadow changes reject old color even when every shadow is marched afresh and no K history is reused.
    if ((passGroup.temporal != 0u) && !worldSoftShadowsDisabled() && (passGroup.shadowSlotCount > 1u)) {
        uint word = (SDF_SHADOW_HISTORY_WORDS * (((p.viewIndex * passGroup.imageExtent.y + p.pixel.y) * passGroup.imageExtent.x) + p.pixel.x));
        reactivity = max(reactivity, (float)shadowHistory[word + 4u]);
    }

    // One call site: each sdfDebugView call inlines its field reads again.
    float3 viewColor = sdfDebugView(p, s, color);
    if ((p.viewMode != 0) && (p.viewMode != DebugViewModeSkyCost)) {
        coverage = 1.0;

        return viewColor;
    }

    return (viewColor * (s.hit ? sdfAirTransmittance(p.rayOrigin, p.rayDirection, s.t) : 1.0));
}
#endif

#endif
