// The views filter pass of a binned view (SdfWorldPackage.Parts.ViewsFilter): each 8x8 group gathers the views filter's
// tile, writes every hit's filtered occlusion and shadows into the filter record its view's mode filters, and writes the
// tile's bin into the shade bins and counts it there (sdf-views-filter.hlsli). A tile no pixel of which has a program
// is the plain kernel's; every other tile is the shading selection's. It dispatches over the extent as views does, jobs
// of whole groups in raster order, and binds the shading selection's binding rules (sdfShadeBinding) without running a
// program.
#define SDF_VIEWS_FILTER_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-light-stage.hlsli"
#include "sdf-views-filter.hlsli"

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

    if (p.active) {
        SdfSurfaceSample s = sdfLoadSurfaceSample(worldVisibilityRecord(p.pixel, p.viewIndex));

        if (s.hit) {
            if (ambientFilter || shadowFilter) {
                // The surface point exactly as the unsplit views stage forms it, so the filter's averages are its.
                precise float3 rayTravel = p.rayDirection * s.t;
                precise float3 surfacePoint = p.rayOrigin + rayTravel;
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

    if (first) {
        // A tile whose programmed pixels share a bin takes it; one whose pixels part between bins takes the shading
        // selection's, which shades every program the frame runs.
        uint tileBin = ((sdfViewsTileHigh == 0u) ? SdfShadeBinPlain : ((sdfViewsTileLow == sdfViewsTileHigh) ? sdfViewsTileLow : SdfShadeBinShaded));
        uint tile = (((p.pixel.y / SDF_VISIBILITY_GROUP_EDGE) * sdfShadeTilesAcross()) + (p.pixel.x / SDF_VISIBILITY_GROUP_EDGE));

        sdfShadeTilesRW[sdfShadeTileBinWord(p.viewIndex, tile)] = tileBin;
        InterlockedAdd(sdfShadeTilesRW[(sdfShadeBinsBase(p.viewIndex) + SdfShadeCountWord) + tileBin], 1u);
    }
}

#include "sdf-pixel-entry.hlsli"
