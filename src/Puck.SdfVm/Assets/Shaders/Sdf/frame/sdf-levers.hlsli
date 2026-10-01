// The debug view modes and the engine levers the bench verbs and the world's settings set, each read from the pass block
// (the view's quality, SdfViewSnapshot.Quality, and SdfFrame's bench levers, written by SdfFrameBlock).
#ifndef FRAME_SDF_LEVERS_HLSLI
#define FRAME_SDF_LEVERS_HLSLI
#include "sdf-environment.hlsli"
#include "sdf-lights.hlsli"
// March + shade one viewport's ray for a pixel at the viewport-local UV, starting the march at `marchStart` (the
// tile-cull lower bound; TileEmpty skips the march entirely → background) and resolving the debug view mode.
// `instanceMaskBase` is the pixel's tile mask base in the mask buffer (SDF_INSTANCE_MASK_ALL when the beam prepass
// never resolved one, e.g. a consumer that skips it) — the WHOLE march (and its normal probe) uses the SAME mask
// throughout, so the masked field a ray marches through is self-consistent start to finish.
// The debug-view-mode wire contract: the pass block's debugMode carries the mode index into DebugViewModes.Names
// (src/Puck.SdfVm/DebugViewModes.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch below).
// Mode 0 / >= DebugViewModeCount render final shading.
static const int DebugViewModeCount = 13;
static const int DebugViewModeNormals = 2;
// Mode 7 (slice) is special-cased in TWO other places: the primary stage skips the march for it (the slice never needs a
// hit), and the beam prepass FORCE-SURVIVES every in-viewport tile for it (sdf-beam.comp) so the indirect dispatch
// cannot truncate the field picture — the slice must show the IDEAL field wall to
// wall. KEEP IN SYNC with DebugViewModes.Names in src/Puck.SdfVm/DebugViewModes.cs.
static const int DebugViewModeSlice = 7;
// Mode 8 (mask density) tints each pixel by its tile's kept-instance count — cull correctness by eye, and the way the
// lead WATCHES the storm cliff. Mode 9 (overshoot detector) marches the pixel TWICE — the production Lipschitz-clamped
// field and the same field with the clamp forced to 1.0 — and colors their depth disagreement (the liar's-spiral class
// as a live view). BOTH skip the primary march (mask reads the mask buffer directly; overshoot runs its own two
// marches), so neither needs the slice's beam force-survive: like the termination view they show what the PIPELINE
// dispatched (a beam-culled tile reads as background). KEEP IN SYNC with DebugViewModes.Names in
// src/Puck.SdfVm/DebugViewModes.cs.
static const int DebugViewModeMask = 8;
static const int DebugViewModeOvershoot = 9;
// Mode 10 (eval-count heatmap — tallies primary-march evaluation cost per pixel by eye). UNLIKE every other debug
// mode, this one needs the REAL final-shading epilogue to run (normal, soft
// shadow, AO, coverage-AA) so the tallied count reflects actual per-frame cost — see useFinalShading below, which
// folds this mode in alongside the final-image modes instead of skipping straight to a cheap switch-only case.
static const int DebugViewModeEvals = 10;
// Mode 11 (visibility) colors each pixel by the kind of its visibility record: background dark blue, SDF green, mesh
// orange. Like the termination view it shows what the pipeline dispatched, so a tile outside the dispatch box keeps the
// sky pre-pass's color. KEEP IN SYNC with DebugViewModes.Names in src/Puck.SdfVm/DebugViewModes.cs.
static const int DebugViewModeVisibility = 11;
// Mode 12 encodes visibility-derived motion in render pixels; its valid-history blue channel distinguishes a cut.
static const int DebugViewModeMotion = 12;

// The analytic-normal A/B toggle (the forward-mode dual's debug lever): 0 (the default) selects the analytic dual normal
// (calculateNormalAnalytic), 1 selects the 4-tap finite-difference probe (calculateTapNormal) for comparison under
// world.debug-view normals.
bool worldUseTapNormals() {
    return (passGroup.finiteDifferenceNormals != 0u);
}

// The per-view shader-feature levers (World's world.shadows drives the soft shadows and their reach; world.ao drives
// ambient occlusion): each defaults to the shipped behavior, every feature on at full reach.
// Whether a pixel of `viewMode` takes the final shading. The evals heatmap rides it too, since it tallies what a lit
// pixel really costs.
bool worldFinalShadingMode(int viewMode) {
    return ((viewMode <= 0) || (viewMode >= DebugViewModeCount) || (viewMode == DebugViewModeEvals));
}
bool worldSoftShadowsDisabled() {
    return (passGroup.disableSoftShadows != 0u);
}
bool worldAoDisabled() {
    return (passGroup.disableAmbientOcclusion != 0u);
}
// The soft-shadow reach's scale; 0 reads as the full 1.0 reach.
float worldShadowDistanceScale() {
    float s = passGroup.shadowDistanceScale;
    return ((s > 0.0) ? s : 1.0);
}
bool worldScreenLightsDisabled() {
    return (passGroup.disableScreenLights != 0u);
}
// The F1 far-bound A/B lever: 0 (the default) keeps the beam-published far bound active (the fine march exits at
// traveled >= farBound); 1 pushes the far bound out of reach so the march runs to the far distance (the paired-run "off"
// side).
bool worldFarBoundDisabled() {
    return (passGroup.disableFarBound != 0u);
}
// The shadow-proxy lever (sdf.shadow-proxy): when enabled, sdfShadowGather omits Subtraction-family carve instances
// (host-flagged shadow-transparent) from the soft-shadow occluder set, so the shadow march evaluates the pre-carve union
// hull — O(few) on a dense carve cluster by construction. Conservative: skipping a pure carve can only make the field more
// solid, so shadows go darker, never leak. Default off.
bool worldShadowProxyEnabled() {
    return (passGroup.enableShadowProxy != 0u);
}
// The dense-crowd approximation: reuse Stage 0's camera-tile mask for the shadow march and skip the per-lit-pixel
// shadow-grid gather. It can omit an off-camera occluder whose shadow reaches into the tile, so it is opt-in and the
// default remains the correctness-complete gathered mask.
bool worldUseCameraTileShadowMask() {
    return (passGroup.cameraTileShadowMask != 0u);
}
// Dense-scene presentation path: bound the number, reach, and spacing of shadow samples. Default false preserves the
// full quality path for every engine consumer; Puck.World opts in only at its declared fleet tiers.
bool worldUseFastSoftShadowMarch() {
    return (passGroup.fastSoftShadowMarch != 0u);
}
// Fleet-scale contact AO: one calibrated middle-rung field sample instead of the quality path's three samples.
// Default false preserves the full ladder for every engine consumer.
bool worldUseFastAmbientOcclusion() {
    return (passGroup.fastAmbientOcclusion != 0u);
}

// Stylized curvature/NPR shading (render.lighting.curvature) — artistic, not physically-based, and inert until a
// world authors a gain. The runtime gate is "any gain above zero": the curvature normal costs a 5th map() centre tap
// beyond the four the normal already takes, and the enrichment carries a divide, so both hang off this one predicate
// rather than an arithmetic *0 that DXC's DXIL backend does not fold away.
bool worldCurvatureShadingEnabled() {
    return (max(worldCurvatureCavity(), max(worldCurvatureRim(), worldCurvatureInk())) > 0.0);
}
// The soft-shadow grid-cull A/B lever (the sdf.shadowcull verb): 0 (the default) = on, the grid-gathered shadow-ray march;
// 1 = off, the flat all-instances march, the ground-truth reference the cull matches.
bool worldShadowCullEnabled() {
    return (passGroup.disableShadowCull == 0u);
}

#endif
