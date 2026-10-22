// The modules every world kernel reads but the bake's (passes/sdf-bake-world.hlsli): the surface and views passes, which
// read only the SDF program's material table and the baked meshes. Views shades the visibility records into the
// view's lit image, which the sky and composite passes (passes/sdf-sky-runs.comp.hlsl, passes/sdf-composite.comp.hlsl)
// put over the sky. KEEP IN SYNC with SdfWorldTables's packing and pass order.
#ifndef SDF_WORLD_HLSLI
#define SDF_WORLD_HLSLI
#include "../field/sdf-vm.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"

// Each module reads only what the modules before it declare. A module only some kernels read (a pass's stage) is
// included by the module or wrapper that reads it, never here: everything here is in every world kernel's include
// closure, so an edit to it recompiles them all.
#include "../frame/sdf-work.hlsli"
#include "../frame/sdf-frame.hlsli"
#include "../shade/sdf-lighting.hlsli"
#include "../frame/sdf-levers.hlsli"

#endif
