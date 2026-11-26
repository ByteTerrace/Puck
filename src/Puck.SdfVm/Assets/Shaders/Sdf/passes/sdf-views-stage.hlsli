// The views pass's stage over the pixel's visibility record, which the surface pass wrote (sdf-surface-stage.hlsli):
// views shades the record (sdfViewsStage below). A binned kernel (SDF_VIEWS_BINNED) reads the filtered occlusion and
// shadows from the filter record, and its tile's light masks from the shade bins, which its filter pass wrote; every
// other kernel filters and decides its tile's lights in its own group.
#ifndef PASSES_SDF_VIEWS_STAGE_HLSLI
#define PASSES_SDF_VIEWS_STAGE_HLSLI
#include "sdf-light-stage.hlsli"
#include "sdf-views-filter.hlsli"
#include "sdf-views-lights.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "../debug/sdf-debug-views.hlsli"

// Whether the view stores its samples' air (SdfWorldPackage.SampleAirWritten): its fragment resolves, so the pass binds
// a buffer that holds every pixel of the view, where a view that composites its own samples binds the filler, which
// holds none.
bool sdfViewsStoresAir(uint viewIndex) {
    uint count;
    uint stride;

    sampleAirRW.GetDimensions(count, stride);
    return (count > sdfReactivityIndex((passGroup.imageExtent - 1u), viewIndex, passGroup.imageExtent));
}
// The air of the sample at `pixel` from its visibility record alone, as the views stage forms its own pixel's.
uint2 sdfViewsSampleAirAt(ViewportData view, uint2 pixel, uint viewIndex) {
    SdfVisibility visibility = sdfLoadVisibility(worldVisibilityRecord(pixel, viewIndex));

    if (!sdfVisibilityHit(visibility)) {
        return uint2(0u, 0u);
    }

    SdfPixel q = sdfPixelAt(view, pixel, viewIndex);

    return sdfPackSampleAir(sdfAirAlong(q.rayOrigin, q.rayDirection, visibility.t, true), visibility.t);
}
// The views stage: the pixel's light stage over its surface sample and the debug view, with the pixel's coverage and
// reactivity (sdfLightStage). A hit's color leaves through the atmosphere's transmittance over its own ray
// (sdf-transport.hlsli), so the resolve filters it with the coverage as one premultiplied quantity; the atmosphere's
// in-scatter, the sky and the bounded volumes are the composite's, so a moving medium never enters a temporal view's
// history. A surface debug view draws the whole pixel, so it covers it and is not fogged. Sky cost retains the surface's
// real coverage so the composite can measure the visible sky. A lane past the render extent returns black, which the
// caller never stores. `sampleAir` receives the sample's air (sdfPackSampleAir) from that one evaluation of the
// atmosphere, zero for a miss, which a view that resolves stores for its resolve.
float3 sdfViewsStage(SdfPixel p, bool shadesPixel, out float coverage, out float reactivity, out uint2 sampleAir) {
    coverage = 0.0;
    reactivity = 0.0;
    sampleAir = uint2(0u, 0u);

    // The pass block's levers and the view's mode are uniform across the group, so every lane reaches the gather's
    // barrier or none does.
    bool ambientFilter = sdfViewsFiltersAmbient(p.viewMode);
    bool shadowFilter = sdfViewsFiltersShadows(p.viewMode);
    bool edgeSearch = sdfViewsSearchesEdges(p.viewMode);

#ifndef SDF_VIEWS_BINNED
    if (ambientFilter || shadowFilter || edgeSearch) {
        sdfViewsGatherFilter(p, shadowFilter, edgeSearch);
    }
#endif

    SdfSurfaceSample s = (SdfSurfaceSample)0;
    // The record's hit (every hit the surface pass writes is a mesh's), resolved through the mesh hit door; a hit the door
    // rejects shades as a miss.
    SdfMeshHit meshHit = (SdfMeshHit)0;
    if (p.active && shadesPixel) {
        s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));
        if (s.hit) {
            s.hit = sdfMeshResolveHit(s.meshDraw, s.meshTriangle, meshHit);
        }
    }
    bool useFinalShading = worldFinalShadingMode(p.viewMode);
    bool curvatureShading = worldCurvatureShadingEnabled();
    float3 surfacePoint = sdfPixelSurfacePoint(p, s.t);
    int material = s.material;
    // The view's mode is uniform across the group, so every lane of an unsplit kernel reaches the tile's barriers or none
    // does. A screen's face walks no light (sdfLightStage), so it joins neither the tile's boxes nor its seen lights; the
    // filter pass of a binned view passes the same (sdf-world-views-filter.comp.hlsl).
    SdfLightMasks lights = sdfViewsTileLights(p, useFinalShading, (s.hit && (material < SDF_SCREEN_MATERIAL)), surfacePoint, s.t);

    if (!p.active || !shadesPixel) {
        return float3(0.0, 0.0, 0.0);
    }

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

    // The pixel's edges, which its program's edge.* members read: the filter record's in a binned view, the group's own
    // search otherwise, and none in a view no program of which reads one.
    SdfShadeEdges edges = sdfShadeNoEdges();

    if (edgeSearch && s.hit) {
#ifdef SDF_VIEWS_BINNED
        edges = sdfViewsEdgesUnpack(sdfViewsFilterRecords[sdfViewsFilterRecord(p.pixel, p.viewIndex) + 2u]);
#else
        edges = sdfViewsEdgesUnpack(sdfViewsEdgesWord(p));
#endif
    }

    float3 color = sdfLightStage(p, s, meshHit, surfacePoint, useFinalShading, curvatureShading, material, lights, edges, coverage, reactivity);
    float3 viewColor = sdfDebugView(p, s, color);
    // The atmosphere along the sample's ray to its surface, evaluated once for the transmittance below and the air the
    // resolve reads.
    float transmittance = 1.0;

    if (s.hit) {
        SdfAir air = sdfAirAlong(p.rayOrigin, p.rayDirection, s.t, true);

        transmittance = air.transmittance;
        sampleAir = sdfPackSampleAir(air, s.t);
    }

    if (!sdfDebugComposesFinal((uint)p.viewMode) && (p.viewMode != SDF_DEBUG_VIEW_SKY_COST)) {
        coverage = 1.0;

        // A debug view passes the composite as it is: a mode that keeps the final shading shows it exposed.
        return (worldFinalShadingMode(p.viewMode) ? min((viewColor * passGroup.exposure), SDF_LUMINANCE_CEILING) : viewColor);
    }

    // The view's screen fidelity clears the atmosphere's hold on what the view shows, so a framed screen's pixels leave
    // as the glass shaded them (SdfViewSnapshot.ScreenFidelity); the composite clears the in-scatter by the same share.
    // The luminance is stored at the view's light scale, held within the half-float range the lit image stores.
    return min((viewColor * (s.hit ? lerp(transmittance, 1.0, saturate(passGroup.screenFidelity)) : 1.0) * sdfStoredLightScale((uint)p.viewMode, passGroup.lightScale)), SDF_LUMINANCE_CEILING);
}

#endif
