// The debug view modes and the engine levers the bench verbs and the world's settings set, each decoded from the
// frame's screen-light rows.
#ifndef FRAME_SDF_LEVERS_HLSLI
#define FRAME_SDF_LEVERS_HLSLI
#include "sdf-environment.hlsli"
#include "sdf-lights.hlsli"
// March + shade one viewport's ray for a pixel at the viewport-local UV, starting the march at `marchStart` (the
// tile-cull lower bound; TileEmpty skips the march entirely → background) and resolving the debug view mode.
// `instanceMaskBase` is the pixel's tile mask base in the mask buffer (SDF_INSTANCE_MASK_ALL when the beam prepass
// never resolved one, e.g. a consumer that skips it) — the WHOLE march (and its normal probe) uses the SAME mask
// throughout, so the masked field a ray marches through is self-consistent start to finish.
// The debug-view-mode wire contract: viewport forward.w carries the mode index into DebugViewModes.Names
// (src/Puck.SdfVm/DebugViewModes.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch below).
// Mode 0 / >= DebugViewModeCount render final shading.
static const int DebugViewModeCount = 12;
static const int DebugViewModeNormals = 2;
// Mode 7 (slice) is special-cased in TWO other places: renderView SKIPS the march for it (the slice never needs a
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

// The analytic-normal A/B toggle (the forward-mode dual's debug lever). Rides a reserved lane of the grid-object-params
// screen-light row (SdfGridObjParams.z): 0 (the DEFAULT) selects the analytic dual normal (calculateNormalAnalytic),
// 1 selects the 4-tap finite-difference probe (calculateNormal) for comparison under world.debug-view normals.
// Decoded only under SDF_SCREEN_SOURCES — the world-views kernel is the sole SDF-hit shader; every other config keeps
// analytic. KEEP IN SYNC with SdfFrame.UseFiniteDifferenceNormals and SdfWorldEngine.PackScreenLights.
bool worldUseTapNormals() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfGridObjParams].z > 0.5);
#else
    return false;
#endif
}

// The four per-frame shader-feature lanes (World's world.shadows drives x and z; world.ao drives y). Ride the
// reserved bench-params screen-light row (SdfBenchParams): x = disable soft shadows,
// y = disable AO, z = shadow-distance scale (0 => the full 1.0 reach, so an unset frame uploads 0 and is unchanged),
// w = disable screen lights. Decoded only under SDF_SCREEN_SOURCES (the world-views kernel is the sole lit SDF shader);
// every other config keeps the shipped defaults. KEEP IN SYNC with SdfFrame's DisableSoftShadows/DisableAmbientOcclusion/
// ShadowDistanceScale/DisableScreenLights fields and SdfWorldEngine.PackScreenLights.
bool worldSoftShadowsDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfBenchParams].x > 0.5);
#else
    return false;
#endif
}
bool worldAoDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfBenchParams].y > 0.5);
#else
    return false;
#endif
}
float worldShadowDistanceScale() {
#ifdef SDF_SCREEN_SOURCES
    float s = sdfScreenLights[SdfBenchParams].z;
    return ((s > 0.0) ? s : 1.0);
#else
    return 1.0;
#endif
}
bool worldScreenLightsDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfBenchParams].w > 0.5);
#else
    return false;
#endif
}
// The F1 FAR-BOUND A/B lever. Rides SdfFarFieldParams.x: 0 (the DEFAULT, an unset frame) keeps
// the beam-published far bound ACTIVE (the shipped behavior — the fine march exits at traveled >= farBound); 1 pushes
// the far bound out of reach so the march runs to the far distance exactly as pre-F1 (the paired-run "off" side). Decoded
// only under SDF_SCREEN_SOURCES (the world-views kernel is the sole SDF-hit shader). KEEP IN SYNC with
// SdfFrame.DisableFarBound and SdfWorldEngine.PackScreenLights.
bool worldFarBoundDisabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfFarFieldParams].x > 0.5);
#else
    return false;
#endif
}
// PATH B — the SHADOW-PROXY lever (sdf.shadow-proxy): when enabled, sdfShadowGather OMITS Subtraction-family carve
// instances (host-flagged SHADOW-TRANSPARENT) from the soft-shadow occluder set, so the shadow march evaluates the
// pre-carve union hull — O(few) on a dense carve cluster by construction, collapsing the shadow re-march the frame is
// bound on. Conservative: skipping a pure carve can only make the field MORE solid, so shadows go darker/never leak.
// Default 0 = OFF (an unset frame uploads 0 and is byte-identical). Rides SdfShadowProxyParams.x. KEEP IN SYNC with
// SdfFrame.EnableShadowProxy and SdfWorldEngine.PackScreenLights.
bool worldShadowProxyEnabled() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].x > 0.5);
#else
    return false;
#endif
}
// The dense-crowd approximation: reuse Stage 0's camera-tile mask for the shadow march and skip the per-lit-pixel
// shadow-grid gather. It can omit an off-camera occluder whose shadow reaches into the tile, so it is opt-in and the
// default remains the correctness-complete gathered mask. SdfShadowProxyParams.y; see SdfFrame.
bool worldUseCameraTileShadowMask() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].y > 0.5);
#else
    return false;
#endif
}
// Dense-scene presentation path: bound the number, reach, and spacing of shadow samples. Default false preserves the
// full quality path for every engine consumer; Puck.World opts in only at its declared fleet tiers.
bool worldUseFastSoftShadowMarch() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].z > 0.5);
#else
    return false;
#endif
}
// Fleet-scale contact AO: one calibrated middle-rung field sample instead of the quality path's three samples.
// Default false preserves the full ladder for every engine consumer. SdfShadowProxyParams.w; see SdfFrame.
bool worldUseFastAmbientOcclusion() {
#ifdef SDF_SCREEN_SOURCES
    return (sdfScreenLights[SdfShadowProxyParams].w > 0.5);
#else
    return false;
#endif
}

// Stylized curvature/NPR shading (render.lighting.curvature) — artistic, not physically-based, and inert until a
// world authors a gain. The runtime gate is "any gain above zero": the curvature normal costs a 5th map() centre tap
// beyond the four the normal already takes, and the enrichment carries a divide, so both hang off this one predicate
// rather than an arithmetic *0 that DXC's DXIL backend does not fold away.
bool worldCurvatureShadingEnabled() {
    return (max(worldCurvatureCavity(), max(worldCurvatureRim(), worldCurvatureInk())) > 0.0);
}
#ifdef SDF_SCREEN_SOURCES
// The soft-shadow GRID-CULL A/B lever (the sdf.shadowcull verb). Rides SdfGridObjParams.w: 0 (the DEFAULT, an unset
// frame uploads 0) = ON — the grid-gathered shadow-ray march; 1 = OFF — the flat all-instances march (the ground-truth
// reference the departed cull gate matched, and the A/B lever's slow reference). KEEP IN SYNC with SdfFrame.DisableShadowCull
// and SdfWorldEngine.PackScreenLights.
bool worldShadowCullEnabled() {
    return (sdfScreenLights[SdfGridObjParams].w < 0.5);
}
#endif

#endif
