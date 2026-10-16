// The views pass's stage over the pixel's visibility record, which the surface pass wrote (sdf-surface-stage.hlsli):
// views shades the record (sdfViewsStage below).
#ifndef PASSES_SDF_VIEWS_STAGE_HLSLI
#define PASSES_SDF_VIEWS_STAGE_HLSLI
#include "sdf-light-stage.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "../debug/sdf-debug-views.hlsli"

// The views stage: the pixel's light stage over its surface sample and the debug view, with the pixel's coverage and
// reactivity (sdfLightStage). A hit's color leaves through the atmosphere's transmittance over its own ray
// (sdf-transport.hlsli), so the resolve filters it with the coverage as one premultiplied quantity; the atmosphere's
// in-scatter, the sky and the bounded volumes are the composite's, so a moving medium never enters a temporal view's
// history. A surface debug view draws the whole pixel, so it covers it and is not fogged. Sky cost retains the surface's
// real coverage so the composite can measure the visible sky. A lane past the render extent returns black, which the
// caller never stores.
float3 sdfViewsStage(SdfPixel p, out float coverage, out float reactivity) {
    coverage = 0.0;
    reactivity = 0.0;

    if (!p.active) {
        return float3(0.0, 0.0, 0.0);
    }

    SdfSurfaceSample s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));
    bool useFinalShading = worldFinalShadingMode(p.viewMode);
    bool curvatureShading = worldCurvatureShadingEnabled();
    precise float3 rayTravel = p.rayDirection * s.t;
    precise float3 surfacePoint = p.rayOrigin + rayTravel;
    int material = s.material;
    SdfLightMesh mesh = (SdfLightMesh)0;

    if (s.hit && useFinalShading && (s.material < SDF_SCREEN_MATERIAL)) {
        mesh = sdfLightMeshAt(p, s, surfacePoint, material);
    }

    float3 color = sdfLightStage(p, s, surfacePoint, useFinalShading, curvatureShading, mesh, material, coverage, reactivity);
    float3 viewColor = sdfDebugView(p, s, color);

    if ((p.viewMode != 0) && (p.viewMode != DebugViewModeSkyCost)) {
        coverage = 1.0;

        return viewColor;
    }

    return (viewColor * (s.hit ? sdfAirTransmittance(p.rayOrigin, p.rayDirection, s.t) : 1.0));
}

#endif
