// The SDF virtual machine — single-source HLSL, compiled by DXC to both SPIR-V (Vulkan) and DXIL (Direct3D 12).
// A program is a flat uint4 word stream that map() interprets per sample point: transform opcodes mutate the
// evaluation point, SHAPE opcodes evaluate a primitive and blend it into the running nearest-surface result.
// The opcode, shape, blend and packed-layout constants come from sdf-isa.hlsli, generated from Puck.SignedDistance by
// `puck shaders generate`; the word layout below and every decoder are written against Puck.SignedDistance.SdfProgram.
// The includer declares the resources the interpreter reads (sdfWords, the dynamic transforms, the brick pool, the
// bake's points, the glyph atlas and the samplers) through its interface: the bake's (isa/sdf-mesh-bake.interface.hlsli)
// or the world's (isa/sdf-world.interface.hlsli). The interpreter reads nothing of the renderer's vocabulary
// (isa/sdf-render.hlsli), so the bake kernels, whose bytecode keys the stored meshes, never include it.
#ifndef SDF_VM_HLSLI
#define SDF_VM_HLSLI

#include "../isa/sdf-isa.hlsli"
#include "sdf-hash.hlsli"

// The field modules, in the order the interpreter declares them: each reads only what the modules before it declare.
#include "sdf-program.hlsli"
#include "sdf-point.hlsli"
#include "sdf-noise.hlsli"
#include "sdf-shapes.hlsli"
#include "sdf-blend.hlsli"
#include "sdf-layout.hlsli"
#include "sdf-parts.hlsli"
#include "sdf-map.hlsli"

#endif
