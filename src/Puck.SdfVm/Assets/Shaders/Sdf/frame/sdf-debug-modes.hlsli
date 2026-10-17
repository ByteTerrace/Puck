#ifndef FRAME_SDF_DEBUG_MODES_HLSLI
#define FRAME_SDF_DEBUG_MODES_HLSLI
// The debug-view-mode wire contract: the pass block's debugMode carries the mode index into DebugViewModes.Names
// (src/Puck.SdfVm/DebugViewModes.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch in
// debug/sdf-debug-views.hlsli). Mode 0 and modes from DebugViewModeCount on render final shading.
static const int DebugViewModeCount = 9;
static const int DebugViewModeNormals = 2;
// Mode 5 (visibility) colors each pixel by the kind of its visibility record: background dark blue, mesh orange.
static const int DebugViewModeVisibility = 5;
// Mode 6 encodes visibility-derived motion in render pixels; its valid-history blue channel distinguishes a cut.
static const int DebugViewModeMotion = 6;
// Mode 7 shows the sky's cost: its evaluations, hashes and texture loads.
static const int DebugViewModeSkyCost = 7;
// Mode 8 shows the ambient occlusion views shades with, after its window filter, in gray; a miss is black.
static const int DebugViewModeAo = 8;

#endif
