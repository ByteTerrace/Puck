// The hit passes' shared entry point: primary, surface, ambient, shadow, the indirect receiver and views each compile
// their own stage over the pixel the entry point gathers. All six use an 8x8 workgroup over the one indirect tile box, and
// the same camera, masks and active-pixel test. Primary also reads the mesh pass's target (sdf-mesh.hlsli). A hit kernel
// includes it last, after sdf-hit-pass.hlsli and its stage.
#ifndef PASSES_SDF_HIT_ENTRY_HLSLI
#define PASSES_SDF_HIT_ENTRY_HLSLI

[numthreads(SDF_VISIBILITY_BOX_EDGE, SDF_VISIBILITY_BOX_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

    // The indirect dispatch covers the surviving-tile box anchored at (0,0); its group origin, in pixels, moves this
    // invocation onto the box's pixel rather than the frame's top-left.
    uint2 pixel = ((uint2(cullBounds[0], cullBounds[1]) * SDF_VISIBILITY_BOX_EDGE) + id.xy);

    ViewportData view = worldView();

    // The per-invocation program-layout cache (field/sdf-layout.hlsli), decoded once before the stage's field queries.
    sdfProgramLayout = sdfLoadProgramLayout();
    sdfPartBoundsViewport = viewIndex;

    SdfPixel p = sdfPixelAt(view, pixel, viewIndex);

#if defined(SDF_PRIMARY_PASS)
    sdfPrimaryStage(p);
#elif defined(SDF_SURFACE_PASS)
    sdfSurfaceStage(p);
#elif defined(SDF_AMBIENT_PASS)
    sdfAmbientStage(p);
#elif defined(SDF_SHADOW_PASS)
    sdfShadowStage(p);
#elif defined(SDF_RECEIVER_PASS)
    sdfReceiverStage(p);
#else
    float coverage;
    float reactivity;
    sdfIndirectPickBegin(p);
    float3 color = sdfViewsStage(p, coverage, reactivity);
    sdfIndirectPickFinish();

    if (p.active) {
        // The lit image: the float working color premultiplied by the pixel's coverage, the coverage in its alpha, which
        // the composite puts over the sky.
        output[pixel] = float4((color * coverage), coverage);
        sdfWorkTexels = 1u;
        if (passGroup.temporal != 0u) {
            reactivityRW[sdfReactivityIndex(pixel, viewIndex, passGroup.imageExtent)] = reactivity;
        }
    }
#endif

    // A hit pass counts a pixel whose visibility record it stored and views a pixel whose texel it wrote.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}

#endif
