// The pixel passes' shared entry point: the surface pass, the views filter pass and views each compile their own stage
// over the pixel the entry point gathers, one invocation a pixel of the render extent in an 8x8 workgroup. A pixel kernel
// includes it last, after sdf-pixel-pass.hlsli and its stage.
#ifndef PASSES_SDF_PIXEL_ENTRY_HLSLI
#define PASSES_SDF_PIXEL_ENTRY_HLSLI

[numthreads(SDF_VISIBILITY_GROUP_EDGE, SDF_VISIBILITY_GROUP_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
#if defined(SDF_VIEWS_BINNED)
    // A binned job (SdfShadeDispatch): the pushed index names its bin in its low four bits and its first entry in the
    // bin's tile list above them, and each group shades the tile its entry names (sdf-views-filter.hlsli).
    uint bin = (pushedIndex.index & 0xFu);
    uint entry = ((sdfShadeTiles[(sdfShadeBinsBase(worldViewOf(id.z)) + SdfShadeOffsetWord) + bin] + (pushedIndex.index >> 4u)) + (id.x / 8u));
    uint tile = sdfShadeTiles[sdfShadeListWord(worldViewOf(id.z), entry)];
    uint across = sdfShadeTilesAcross();
    id.xy = uint2(tile % across, tile / across) * 8u + uint2(id.x % 8u, id.y);
#elif !defined(SDF_SURFACE_PASS)
    // An unsplit job (SdfShadeDispatch): the pushed index is its first group in raster order.
    uint across = (passGroup.extent.x + 7u) / 8u;
    uint group = pushedIndex.index + id.x / 8u;
    id.xy = uint2(group % across, group / across) * 8u + uint2(id.x % 8u, id.y);
#endif
    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

#if !defined(SDF_SURFACE_PASS) && !defined(SDF_VIEWS_FILTER_PASS)
    uint rate = 0u;
    bool shadesPixel = true;
    if (passGroup.temporal != 0u && passGroup.viewsRate.x == 2u && passGroup.debugMode == 0u) {
        // Keep each cell's representative in the first quarter of the group. A coarse 8x8 group
        // runs its light stage in 16 adjacent invocations. The other lanes still gather the group tiles.
        uint2 local = id.xy % SDF_VISIBILITY_GROUP_EDGE;
        uint lane = local.y * SDF_VISIBILITY_GROUP_EDGE + local.x;
        uint cellsAcross = SDF_VISIBILITY_GROUP_EDGE / 2u;
        uint2 cell = uint2(lane % cellsAcross, (lane / cellsAcross) % cellsAcross);
        uint subpixel = lane / (cellsAcross * cellsAcross);
        uint phase = subpixel ^ (passGroup.historyFrames % 4u);
        id.xy = id.xy - local + 2u * cell + uint2(phase % 2u, phase / 2u);
        if (all(id.xy < passGroup.imageExtent)) {
            uint word = sdfViewsProbeRecord(id.xy / SDF_VIEWS_PROBE_RATE, viewIndex) + SdfViewsProbeStateWord;
            rate = (sdfViewsProbeRecords[word] >> SdfViewsRateShift) & 3u;
            shadesPixel = (subpixel & rate) == 0u;
        }
    }
#endif

    SdfPixel p = sdfPixelAt(worldView(), id.xy, viewIndex);

#if defined(SDF_SURFACE_PASS)
    sdfSurfaceStage(p);
#elif defined(SDF_VIEWS_FILTER_PASS)
    sdfViewsFilterStage(p);
#else
    float coverage;
    float reactivity;
    float3 color = sdfViewsStage(p, shadesPixel, coverage, reactivity);

    if (p.active && shadesPixel) {
        // The lit image: the float working color premultiplied by the pixel's coverage, the coverage in its alpha, which
        // the composite puts over the sky.
        float4 lit = float4((color * coverage), coverage);
        output[p.pixel] = lit;
        sdfWorkTexels = 1u;
        if (passGroup.temporal != 0u) {
            reactivityRW[sdfReactivityIndex(p.pixel, viewIndex, passGroup.imageExtent)] = reactivity;
        }
        if (rate != 0u) {
            // Qualification covered the entire cell, including its extent, coverage and ink.
            uint2 origin = p.pixel & ~uint2(rate & 1u, rate >> 1u);
            [unroll] for (uint offset = 0u; offset < 4u; offset++) {
                uint2 dest = origin + uint2(offset % 2u, offset / 2u);
                if ((offset & rate) == offset && any(dest != p.pixel)) {
                    output[dest] = lit;
                    reactivityRW[sdfReactivityIndex(dest, viewIndex, passGroup.imageExtent)] = reactivity;
                }
            }
            sdfWorkTexels = 1u << countbits(rate);
        }
    }
#endif

    // The surface pass counts a pixel whose visibility record it stored, the filter pass a pixel whose filter record it
    // stored and views a pixel whose texel it wrote.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}

#endif
