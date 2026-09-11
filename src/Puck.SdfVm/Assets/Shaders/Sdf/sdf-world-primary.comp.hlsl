// Primary traversal uses the same camera, tile bounds, participation modes, and march as the views reference.
// It writes a compact hit record; normal/material reconstruction and lighting compile only into the views pass.
#define SDF_PRIMARY_PASS
#include "sdf-world-views.comp.hlsl"
