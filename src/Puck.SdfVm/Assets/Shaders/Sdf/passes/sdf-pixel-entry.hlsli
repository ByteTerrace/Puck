// The pixel passes' shared entry point: the surface pass and views each compile their own stage over the pixel the
// entry point gathers, one invocation a pixel of the render extent in an 8x8 workgroup. A pixel kernel includes it last,
// after sdf-pixel-pass.hlsli and its stage.
#ifndef PASSES_SDF_PIXEL_ENTRY_HLSLI
#define PASSES_SDF_PIXEL_ENTRY_HLSLI

[numthreads(SDF_VISIBILITY_GROUP_EDGE, SDF_VISIBILITY_GROUP_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

    SdfPixel p = sdfPixelAt(worldView(), id.xy, viewIndex);

#if defined(SDF_SURFACE_PASS)
    sdfSurfaceStage(p);
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

    // The surface pass counts a pixel whose visibility record it stored and views a pixel whose texel it wrote.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}

#endif
