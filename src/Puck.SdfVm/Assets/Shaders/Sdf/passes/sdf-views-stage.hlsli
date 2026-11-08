// The views pass's stage over the pixel's visibility record, which the surface pass wrote (sdf-surface-stage.hlsli):
// views shades the record (sdfViewsStage below). A binned kernel (SDF_VIEWS_BINNED) reads the filtered occlusion and
// shadows from the filter record its filter pass wrote; every other kernel filters in its own group.
#ifndef PASSES_SDF_VIEWS_STAGE_HLSLI
#define PASSES_SDF_VIEWS_STAGE_HLSLI
#include "sdf-light-stage.hlsli"
#include "sdf-views-filter.hlsli"
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

    // The pass block's levers and the view's mode are uniform across the group, so every lane reaches the gather's
    // barrier or none does.
    bool ambientFilter = sdfViewsFiltersAmbient(p.viewMode);
    bool shadowFilter = sdfViewsFiltersShadows(p.viewMode);

#ifndef SDF_VIEWS_BINNED
    if (ambientFilter || shadowFilter) {
        sdfViewsGatherFilter(p, shadowFilter);
    }
#endif
    if (!p.active) {
        return float3(0.0, 0.0, 0.0);
    }

    SdfSurfaceSample s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));
    bool useFinalShading = worldFinalShadingMode(p.viewMode);
    bool curvatureShading = worldCurvatureShadingEnabled();
    precise float3 rayTravel = p.rayDirection * s.t;
    precise float3 surfacePoint = p.rayOrigin + rayTravel;
    int material = s.material;

    if ((ambientFilter || shadowFilter) && s.hit) {
        float ambient;
        float4 shadows;

#ifdef SDF_VIEWS_BINNED
        uint record = sdfViewsFilterRecord(p.pixel, p.viewIndex);

        sdfViewsFilterUnpack(uint2(sdfViewsFilterRecords[record], sdfViewsFilterRecords[record + 1u]), ambient, shadows);
#else
        sdfViewsFilter(p, surfacePoint, s.normal, s.t, ambient, shadows);
#endif
        s.ambient = (ambientFilter ? ambient : s.ambient);
        s.shadows = (shadowFilter ? shadows : s.shadows);
    }

    float3 color = sdfLightStage(p, s, surfacePoint, useFinalShading, curvatureShading, material, coverage, reactivity);
    float3 viewColor = sdfDebugView(p, s, color);

    if (!sdfDebugComposesFinal((uint)p.viewMode) && (p.viewMode != SDF_DEBUG_VIEW_SKY_COST)) {
        coverage = 1.0;

        // A debug view passes the composite as it is: a mode that keeps the final shading shows it exposed.
        return (worldFinalShadingMode(p.viewMode) ? min((viewColor * passGroup.exposure), SDF_LUMINANCE_CEILING) : viewColor);
    }

    // The view's screen fidelity clears the atmosphere's hold on what the view shows, so a framed screen's pixels leave
    // as the glass shaded them (SdfViewSnapshot.ScreenFidelity); the composite clears the in-scatter by the same share.
    // The luminance is held within the half-float range the lit image stores.
    return min((viewColor * (s.hit ? lerp(sdfAirTransmittance(p.rayOrigin, p.rayDirection, s.t), 1.0, saturate(passGroup.screenFidelity)) : 1.0)), SDF_LUMINANCE_CEILING);
}

#endif
