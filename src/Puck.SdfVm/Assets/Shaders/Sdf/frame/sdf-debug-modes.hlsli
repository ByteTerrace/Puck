#ifndef FRAME_SDF_DEBUG_MODES_HLSLI
#define FRAME_SDF_DEBUG_MODES_HLSLI
// The debug-view-mode wire contract: the pass block's debugMode carries the mode index into DebugViewModes.Names
// (Puck.SdfVm.Model), whose count and every mode's index sdf-render.hlsli declares (SDF_DEBUG_VIEW_*), so a switch reads
// a mode by name. Mode 0 and modes from SDF_DEBUG_VIEW_COUNT on render final shading.
#include "../isa/sdf-render.hlsli"
// Mode 5 (visibility) colors each pixel by the kind of its visibility record: background dark blue, mesh orange.
// Mode 6 encodes visibility-derived motion in render pixels; its valid-history blue channel distinguishes a cut.
// Mode 7 shows the sky's cost: its evaluations, hashes and texture loads.
// Mode 8 shows the ambient occlusion views shades with, after its window filter, in gray; a miss is black.
// Mode 9 shows the key light's shadow visibility views shades with, after its window filter, in gray; a miss is black.
// Modes rates and rate-flash are the final image with each rated item tinted by its update tier (SdfUpdateRateCode).

// Whether a mode composes the final image: the final image itself and the tier views, which tint it.
bool sdfDebugPreservesScene(uint mode) {
    return ((mode == 0u) || (mode == SDF_DEBUG_VIEW_COLLIDERS) || (mode == SDF_DEBUG_VIEW_CONTACTS) || (mode == SDF_DEBUG_VIEW_BLOOM));
}
bool sdfDebugComposesFinal(uint mode) {
    return (sdfDebugPreservesScene(mode) || (mode == SDF_DEBUG_VIEW_RATES) || (mode == SDF_DEBUG_VIEW_RATE_FLASH));
}

#endif
