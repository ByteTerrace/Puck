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

    SdfPixel p = sdfPixelAt(worldView(), id.xy, viewIndex);

#if defined(SDF_SURFACE_PASS)
    sdfSurfaceStage(p);
#elif defined(SDF_VIEWS_FILTER_PASS)
    sdfViewsFilterStage(p);
#else
    float coverage;
    float reactivity;
    float3 color = sdfViewsStage(p, coverage, reactivity);

    if (p.active) {
        // The lit image: the float working color premultiplied by the pixel's coverage, the coverage in its alpha, which
        // the composite puts over the sky.
        output[p.pixel] = float4((color * coverage), coverage);
        sdfWorkTexels = 1u;
        if (passGroup.temporal != 0u) {
            reactivityRW[sdfReactivityIndex(p.pixel, viewIndex, passGroup.imageExtent)] = reactivity;
        }
    }
#endif

    // The surface pass counts a pixel whose visibility record it stored, the filter pass a pixel whose filter record it
    // stored and views a pixel whose texel it wrote.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}

#endif
