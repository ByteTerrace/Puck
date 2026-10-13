// The baked-mesh path's surface pass (surface/sdf-mesh-path.hlsli): the whole visibility record of every pixel from the
// mesh target, the screen-space occlusion and the shadow cascades. It calls no field query, so the interpreter the world
// modules declare compiles out of it.
#define SDF_MESH_SURFACE_PASS
#include "sdf-hit-pass.hlsli"
#include "../surface/sdf-mesh-path.hlsli"
#include "sdf-hit-entry.hlsli"
