// The debug view modes and the engine levers the bench verbs and the world's settings set, each read from the pass block
// (the view's quality, SdfViewSnapshot.Quality, and SdfFrame's bench levers, written by SdfFrameBlock).
#ifndef FRAME_SDF_LEVERS_HLSLI
#define FRAME_SDF_LEVERS_HLSLI
#include "sdf-environment.hlsli"
#include "sdf-lights.hlsli"
#include "sdf-debug-modes.hlsli"

// Whether a pixel of `viewMode` takes the final shading: the final image, and the sky-cost view, which tallies what the
// sky really costs beside it.
bool worldFinalShadingMode(int viewMode) {
    return ((viewMode <= 0) || (viewMode >= DebugViewModeCount) || (viewMode == DebugViewModeSkyCost));
}
// The per-view shader-feature levers (World's world.shadows drives the shadows; world.ao drives ambient occlusion): each
// defaults to the shipped behavior, every feature on.
bool worldSoftShadowsDisabled() {
    return (passGroup.disableSoftShadows != 0u);
}
bool worldAoDisabled() {
    return (passGroup.disableAmbientOcclusion != 0u);
}
bool worldScreenLightsDisabled() {
    return (passGroup.disableScreenLights != 0u);
}

// Stylized curvature/NPR shading (render.lighting.curvature) — artistic, not physically-based, and inert until a
// world authors a gain. The runtime gate is "any gain above zero", so the enrichment's divide hangs off this one predicate
// rather than an arithmetic *0 that DXC's DXIL backend does not fold away.
bool worldCurvatureShadingEnabled() {
    return (max(passGroup.curvatureCavity, max(passGroup.curvatureRim, passGroup.curvatureInk)) > 0.0);
}

#endif
