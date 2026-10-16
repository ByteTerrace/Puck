// The surface pass (sdf-surface-stage.hlsli): the whole visibility record of every pixel from the mesh target, the
// screen-space occlusion and the shadow cascades. It calls no field query, so the interpreter the world modules declare
// compiles out of it.
#define SDF_SURFACE_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-surface-stage.hlsli"
#include "sdf-pixel-entry.hlsli"
