// The views filter pass of a binned view (SdfWorldPackage.Parts.ViewsFilter): each 8x8 group gathers the views filter's
// tile, writes every hit's filtered occlusion and shadows into the filter record its view's mode filters, and writes the
// tile's bin and light masks into the shade bins and counts the bin there (sdf-views-filter.hlsli, sdf-views-lights.hlsli).
// A tile no pixel of which has a program is the plain kernel's; every other tile is the shading selection's. It
// dispatches over the extent as views does, jobs of whole groups in raster order, and binds the shading selection's
// binding rules (sdfShadeBinding) without running a program.
#define SDF_VIEWS_FILTER_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-light-stage.hlsli"
#include "sdf-views-filter.hlsli"
#include "sdf-views-lights.hlsli"

// The group's bins among its pixels that have a program: the lowest and the highest.
groupshared uint sdfViewsTileLow;
groupshared uint sdfViewsTileHigh;

void sdfViewsFilterStage(SdfPixel p) {
    // The levers and the view's mode are uniform across the group, so every lane reaches every barrier or none does.
    bool ambientFilter = sdfViewsFiltersAmbient(p.viewMode);
    bool shadowFilter = sdfViewsFiltersShadows(p.viewMode);
    bool first = all((p.pixel % SDF_VISIBILITY_GROUP_EDGE) == 0u);

    if (first) {
        sdfViewsTileLow = 0xFFFFFFFFu;
        sdfViewsTileHigh = 0u;
    }
    // The gather's barrier, or this one, orders the initialization before any lane's bin.
    if (ambientFilter || shadowFilter) {
        sdfViewsGatherFilter(p, shadowFilter);
    } else {
        GroupMemoryBarrierWithGroupSync();
    }

    uint bin = SdfShadeBinPlain;
    SdfSurfaceSample s = (SdfSurfaceSample)0;

    if (p.active) {
        s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));
    }

    float3 surfacePoint = sdfPixelSurfacePoint(p, s.t);

    if (p.active) {
        if (s.hit) {
            if (ambientFilter || shadowFilter) {
                float ambient;
                float4 shadows;

                sdfViewsFilter(p, surfacePoint, s.normal, s.t, ambient, shadows);
                uint2 words = sdfViewsFilterPack(ambient, shadows);
                uint record = sdfViewsFilterRecord(p.pixel, p.viewIndex);

                sdfViewsFilterRecordsRW[record] = words.x;
                sdfViewsFilterRecordsRW[record + 1u] = words.y;
                sdfWorkTexels = 1u;
            }
            if (sdfShadeBound(sdfShadeBinding(s.material))) {
                bin = SdfShadeBinShaded;
            }
        }
    }
    if (bin != SdfShadeBinPlain) {
        InterlockedMin(sdfViewsTileLow, bin);
        InterlockedMax(sdfViewsTileHigh, bin);
    }
    GroupMemoryBarrierWithGroupSync();

    // The tile's light masks, which the binned views kernels walk by: every lane joins the tile's boxes with the point
    // and the material the views stage shades its pixel by. The view's mode is uniform across the group, so every lane
    // reaches the tile's barriers.
    SdfLightMasks lights = sdfViewsTileLights(p, worldFinalShadingMode(p.viewMode), (s.hit && (s.material < SDF_SCREEN_MATERIAL)), surfacePoint, s.t);

    if (first) {
        // A tile whose programmed pixels share a bin takes it; one whose pixels part between bins takes the shading
        // selection's, which shades every program the frame runs.
        uint tileBin = ((sdfViewsTileHigh == 0u) ? SdfShadeBinPlain : ((sdfViewsTileLow == sdfViewsTileHigh) ? sdfViewsTileLow : SdfShadeBinShaded));
        uint tile = sdfShadeTileOf(p.pixel);
        uint word = sdfShadeTileLightsWord(p.viewIndex, tile);

        sdfShadeTilesRW[sdfShadeTileBinWord(p.viewIndex, tile)] = tileBin;
        InterlockedAdd(sdfShadeTilesRW[(sdfShadeBinsBase(p.viewIndex) + SdfShadeCountWord) + tileBin], 1u);
        sdfShadeTilesRW[word] = lights.lights.x;
        sdfShadeTilesRW[word + 1u] = lights.lights.y;
        sdfShadeTilesRW[word + 2u] = lights.lights.z;
        sdfShadeTilesRW[word + 3u] = lights.lights.w;
        sdfShadeTilesRW[word + 4u] = lights.screens;
    }
}

#include "sdf-pixel-entry.hlsli"
