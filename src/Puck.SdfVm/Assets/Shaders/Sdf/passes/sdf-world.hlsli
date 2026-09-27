// Shared contract and rendering functions for the world kernels. Beam evaluates tile clearance; primary records
// camera hits; views reconstructs hit shading and diagnostics into the view's own output image. The scene program and
// cameras remain data. KEEP IN SYNC with SdfWorldTables's packing and pass order.
#ifndef SDF_WORLD_HLSLI
#define SDF_WORLD_HLSLI
#include "../frame/sdf-tile.hlsli"
#include "../field/sdf-vm.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"

// The world modules, in the order the kernels declare them: each reads only what the modules before it declare.
#include "../frame/sdf-frame.hlsli"
#include "../shade/sdf-environment.hlsli"
#include "../shade/sdf-lighting.hlsli"
#include "../march/sdf-march-constants.hlsli"
#include "../surface/sdf-normals.hlsli"
#include "../shade/sdf-sky.hlsli"
#include "../march/sdf-cone.hlsli"
#include "../frame/sdf-levers.hlsli"
#include "../surface/sdf-shadow-gather.hlsli"
#include "../surface/sdf-ambient.hlsli"
#include "../shade/sdf-surface-shading.hlsli"
#include "../debug/sdf-overshoot.hlsli"
#include "sdf-render-view.hlsli"

#endif
