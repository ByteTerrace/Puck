// The views pass's stage over the pixel's visibility record, which the surface pass wrote (sdf-surface-stage.hlsli):
// views shades the record (sdfViewsStage below).
#ifndef PASSES_SDF_VIEWS_STAGE_HLSLI
#define PASSES_SDF_VIEWS_STAGE_HLSLI
#include "sdf-light-stage.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "../debug/sdf-debug-views.hlsli"

// The pattern filter's tile: the group's pixels with two pixels of apron before them and one after on each axis, each as
// its surface point and the surface pass's occlusion, a negative occlusion where it holds no hit, beside its K word.
#define SDF_VIEWS_FILTER_TILE_EDGE (SDF_VISIBILITY_GROUP_EDGE + 3u)
groupshared float4 sdfViewsFilterTile[SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE];
groupshared uint sdfViewsFilterShadows[SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE];

// Loads the group's filter tile from the visibility records, the K words only when `shadows` asks for them. Every lane of
// the group calls it, a lane past the render extent included, since it ends at the group's barrier.
void sdfViewsGatherFilter(SdfPixel p, bool shadows) {
    uint2 lane = (p.pixel % SDF_VISIBILITY_GROUP_EDGE);
    int2 origin = (int2(p.pixel - lane) - 2);
    int2 dims = int2(worldViewDims(p.view));

    for (uint index = ((lane.y * SDF_VISIBILITY_GROUP_EDGE) + lane.x); (index < (SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE));
        index += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
        int2 pixel = (origin + int2((index % SDF_VIEWS_FILTER_TILE_EDGE), (index / SDF_VIEWS_FILTER_TILE_EDGE)));
        float4 entry = float4(0.0, 0.0, 0.0, -1.0);
        uint word = 0u;

        if (all(pixel >= 0) && all(pixel < dims)) {
            uint record = worldVisibilityRecord(uint2(pixel), p.viewIndex);
            float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);

            // A miss records the far distance.
            if (t < p.farDistance) {
                float3 direction = cameraRayDirection(p.view, ((float2(pixel) + 0.5) / float2(dims)));
                entry = float4((p.rayOrigin + (direction * t)), f16tof32(sdfVisibilityRecordBuffer[record + SdfVisibilityShadingWord] >> 16u));
                word = (shadows ? sdfVisibilityRecordBuffer[record + SdfVisibilityShadowWord] : 0u);
            }
        }

        sdfViewsFilterTile[index] = entry;
        sdfViewsFilterShadows[index] = word;
    }

    GroupMemoryBarrierWithGroupSync();
}
// The pixel's occlusion and shadow visibilities averaged over the 4x4 window at offsets -2 to 1 around it, which holds
// each of the surface pass's sixteen tap patterns once (sdfMeshPattern in sdf-surface-stage.hlsli). A pixel joins the
// average only while its surface point lies within two pixel footprints of this pixel's tangent plane, so neither
// crosses a depth edge or a silhouette; the pixel itself always joins.
void sdfViewsFilter(SdfPixel p, float3 surfacePoint, float3 normal, float t, out float ambient, out float4 shadows) {
    uint2 lane = (p.pixel % SDF_VISIBILITY_GROUP_EDGE);
    float tolerance = (2.0 * (p.pixelFootprint * t));
    float ambientSum = 0.0;
    float4 shadowSum = float4(0.0, 0.0, 0.0, 0.0);
    float weight = 0.0;

    [unroll]
    for (uint y = 0u; (y < 4u); y++) {
        [unroll]
        for (uint x = 0u; (x < 4u); x++) {
            uint index = ((((lane.y + y) * SDF_VIEWS_FILTER_TILE_EDGE) + lane.x) + x);
            float4 entry = sdfViewsFilterTile[index];
            float w = ((entry.w < 0.0) ? 0.0 : saturate(1.0 - (abs(dot((entry.xyz - surfacePoint), normal)) / tolerance)));

            ambientSum += (w * entry.w);
            shadowSum += (w * sdfUnpackShadowVisibility(sdfViewsFilterShadows[index]));
            weight += w;
        }
    }

    float inverse = (1.0 / max(weight, 1.0e-4));
    ambient = (ambientSum * inverse);
    shadows = (shadowSum * inverse);
}

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
    bool ambientFilter = (!worldAoDisabled() && (worldFinalShadingMode(p.viewMode) || (p.viewMode == DebugViewModeAo)));
    bool shadowFilter = ((passGroup.shadowSlotCount > 0u) && !worldSoftShadowsDisabled()
        && (worldFinalShadingMode(p.viewMode) || (p.viewMode == DebugViewModeShadow)));

    if (ambientFilter || shadowFilter) {
        sdfViewsGatherFilter(p, shadowFilter);
    }
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

        sdfViewsFilter(p, surfacePoint, s.normal, s.t, ambient, shadows);
        s.ambient = (ambientFilter ? ambient : s.ambient);
        s.shadows = (shadowFilter ? shadows : s.shadows);
    }

    float3 color = sdfLightStage(p, s, surfacePoint, useFinalShading, curvatureShading, material, coverage, reactivity);
    float3 viewColor = sdfDebugView(p, s, color);

    if ((p.viewMode != 0) && (p.viewMode != DebugViewModeSkyCost)) {
        coverage = 1.0;

        return viewColor;
    }

    return (viewColor * (s.hit ? sdfAirTransmittance(p.rayOrigin, p.rayDirection, s.t) : 1.0));
}

#endif
