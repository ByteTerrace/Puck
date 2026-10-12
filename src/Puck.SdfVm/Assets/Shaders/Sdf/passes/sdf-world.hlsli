// Shared contract and rendering functions for the world kernels. Beam evaluates tile clearance; primary records
// camera hits; views shades the hits and diagnostics into the view's lit image, which the sky and composite passes
// (passes/sdf-sky-runs.comp.hlsl, passes/sdf-composite.comp.hlsl) put over the sky. The scene program and
// cameras remain data. KEEP IN SYNC with SdfWorldTables's packing and pass order.
#ifndef SDF_WORLD_HLSLI
#define SDF_WORLD_HLSLI
#include "../frame/sdf-tile.hlsli"
#include "../field/sdf-vm.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"

// The world modules every world kernel reads, each reading only what the modules before it declare. A module only some
// kernels read (the cone march, the normals, the gathers, a pass's stage) is included by the module or wrapper that
// reads it, never here: everything here is in every world kernel's include closure, so an edit to it recompiles them all.
#include "../frame/sdf-work.hlsli"
#include "../frame/sdf-frame.hlsli"
#include "../shade/sdf-lighting.hlsli"
#include "../march/sdf-march-constants.hlsli"
#include "../frame/sdf-levers.hlsli"

#endif
