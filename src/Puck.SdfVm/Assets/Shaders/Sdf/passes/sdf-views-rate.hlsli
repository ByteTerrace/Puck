// Each probe invocation tests its cell. The cells of one authored tile agree on a receiver and must all
// qualify before any can share shading. The decision travels to views in the existing probe state word.
#ifndef PASSES_SDF_VIEWS_RATE_HLSLI
#define PASSES_SDF_VIEWS_RATE_HLSLI
#include "../frame/sdf-history-surface.hlsli"
#include "../frame/sdf-reprojection.hlsli"

groupshared uint sdfRateEligible[SDF_VISIBILITY_GROUP_EDGE][SDF_VISIBILITY_GROUP_EDGE];

uint sdfRateCell(ViewportData view, uint viewIndex, uint2 origin, uint2 tile, uint tileSize) {
    uint2 dims = worldViewDims(view);
    // Require a border around the tile that is one surface with it: one pixel, or the edge search's reach in a view that
    // searches (sdf-views-filter.hlsli), since a line drawn from an edge that near varies across the tile's pixels.
    uint border = ((passGroup.inkEdges != 0u) ? SDF_EDGE_REACH : 1u);
    if (any(tile < border) || any(tile + tileSize + border > dims)) return 0u;
    // Refresh every tile at full rate once per window so new detail can enter an otherwise smooth history.
    uint2 index = tile / tileSize;
    if ((passGroup.historyFrames + index.x + 3u * index.y) % SDF_TEMPORAL_PERIOD == 0u) return 0u;

    uint first = worldVisibilityRecord(tile, viewIndex);
    SdfVisibility ref = sdfLoadVisibility(first);
    SdfMeshHit mesh;
    if (!sdfVisibilityHit(ref) || ref.material >= SDF_SCREEN_MATERIAL ||
        !sdfMeshResolveHit(sdfVisibilitySource(ref.identity), sdfVisibilityRecordBuffer[first + SdfVisibilityTriangleWord], mesh)) return 0u;
    if (!sdfMeshDrawUnmoved(mesh.record)) return 0u;

    SdfPixel anchor = sdfPixelAt(view, tile, viewIndex);
    float3 anchorNormal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[first + SdfVisibilityNormalWord]);
    float3 anchorTravel = anchor.rayDirection * ref.t;
    SdfMaterialData material = sdfMaterialLoad(ref.material);
    // A narrow specular lobe can introduce a glint as the camera turns, before the history can see it.
    if (material.roughness < 0.2 && (material.specular > 0.0 || material.metal > 0.0 || material.coat > 0.0)) return 0u;
    uint rate = 3u;
    uint2 extent = passGroup.viewsRate.zw;
    uint2 end = origin + SDF_VIEWS_PROBE_RATE;
    uint2 low = origin - (border * uint2(origin == tile));
    uint2 high = end + (border * uint2(end == tile + tileSize));
    [loop] for (uint y = low.y; y < high.y; y++) {
        [loop] for (uint x = low.x; x < high.x; x++) {
            uint2 pixel = uint2(x, y);
            uint record = worldVisibilityRecord(pixel, viewIndex);
            SdfVisibility vis = sdfLoadVisibility(record);
            if (vis.identity != ref.identity || vis.material != ref.material) return 0u;
            SdfPixel p = sdfPixelAt(view, pixel, viewIndex);
            SdfSurfaceSample s = sdfLoadSurfaceSample(record);
            float3 rayTravel = p.rayDirection * s.t;
            if (dot(s.normal, anchorNormal) < SdfViewsProbeFacing ||
                abs(dot(rayTravel - anchorTravel, anchorNormal)) > p.pixelFootprint * s.t) return 0u;
            if (any(pixel < origin) || any(pixel >= end)) continue;
            float3 surfacePoint = p.rayOrigin + rayTravel;
            float3 relative = surfacePoint - passGroup.previousView[0].xyz;
            float2 previous;
            if (!sdfProjectView(passGroup.previousView, relative, previous)) return 0u;
            float2 outputPixel = previous * (float2(extent) / passGroup.previousView[4].xy);
            if (any(outputPixel < 0.0) || any(outputPixel >= float2(extent))) return 0u;
            uint at = uint(outputPixel.y) * extent.x + uint(outputPixel.x);
            rate &= historySurface[sdfHistoryWord(at, 5u, extent)] >> SdfHistoryRateShift;
            if (rate == 0u ||
                !sdfHistoryReceiverMatches(vis.identity, historySurface[sdfHistoryWord(at, 0u, extent)], length(relative),
                    f16tof32(historySurface[sdfHistoryWord(at, 1u, extent)] & 0xffffu))) return 0u;
        }
    }
    return rate;
}

uint sdfRateTile(ViewportData view, uint viewIndex, uint2 cell, uint2 origin) {
    // A tile holds whole probe cells: one narrower than a cell is judged as the cell.
    uint tileSize = max(passGroup.viewsRate.y, SDF_VIEWS_PROBE_RATE);
    uint2 tile = origin & ~(tileSize - 1u);
    uint rate = 0u;
    // Uniform across this dispatch, including the lanes beyond a partial workgroup.
    if (passGroup.viewsRate.x == 2u && passGroup.viewsRate.z != 0u && passGroup.temporal != 0u &&
        passGroup.historyFrames != 0u && passGroup.previousView[0].w != 0.0 && passGroup.debugMode == 0u &&
        passGroup.gridFlags == 0u) {
        uint2 local = cell % SDF_VISIBILITY_GROUP_EDGE;
        sdfRateEligible[local.y][local.x] = sdfRateCell(view, viewIndex, origin, tile, tileSize);
        GroupMemoryBarrierWithGroupSync();
        uint cells = tileSize / SDF_VIEWS_PROBE_RATE;
        uint2 first = local & ~(cells - 1u);
        rate = 3u;
        [loop] for (uint y = 0u; y < cells; y++) {
            [loop] for (uint x = 0u; x < cells; x++) {
                rate &= sdfRateEligible[first.y + y][first.x + x];
            }
        }
    }
    if (passGroup.workCounterRowDetail != 0u) {
        bool firstCell = all(origin == tile) && all(origin < worldViewDims(view));
        [unroll] for (uint kind = 0u; kind < 4u; kind++) {
            uint count = WaveActiveCountBits(firstCell && rate == kind);
            if (WaveIsFirstLane() && count != 0u) puckCountDetail(kind, count, 0u, 0u, 0u, 0u);
        }
    }
    return rate;
}
#endif
