#ifndef FRAME_SDF_DEBUG_MODES_HLSLI
#define FRAME_SDF_DEBUG_MODES_HLSLI
// The debug-view-mode wire contract: the pass block's debugMode carries the mode index into DebugViewModes.Names
// (src/Puck.SdfVm/DebugViewModes.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch below).
// Mode 0 / >= DebugViewModeCount render final shading.
static const int DebugViewModeCount = 18;
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
// orange. Like the termination view it shows what the pipeline dispatched, so a tile outside the dispatch box shows the
// lit image's nothing, black, since a debug view draws no sky. KEEP IN SYNC with DebugViewModes.Names in src/Puck.SdfVm/DebugViewModes.cs.
static const int DebugViewModeVisibility = 11;
// Mode 12 encodes visibility-derived motion in render pixels; its valid-history blue channel distinguishes a cut.
static const int DebugViewModeMotion = 12;
static const int DebugViewModeSkyCost = 13;
static const int DebugViewModeIndirectProbes = 14;
static const int DebugViewModeIndirectCells = 15;
static const int DebugViewModeIndirectLight = 16;
static const int DebugViewModeIndirect = 17;

#endif
