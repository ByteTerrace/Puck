// The SDF virtual machine — single-source HLSL, compiled by DXC to both SPIR-V (Vulkan) and DXIL (Direct3D 12).
// A program is a flat uint4 word stream that map() interprets per sample point: transform opcodes mutate the
// evaluation point, SHAPE opcodes evaluate a primitive and blend it into the running nearest-surface result.
// The opcode, shape, blend and packed-layout constants come from sdf-isa.hlsli, generated from Puck.SignedDistance by
// `puck shaders generate`; the word layout below and every decoder are written against Puck.SignedDistance.SdfProgram.
#ifndef SDF_VM_HLSLI
#define SDF_VM_HLSLI

#include "../isa/sdf-isa.hlsli"
#include "sdf-hash.hlsli"
// Every resource and world value the world kernels read, generated from SdfWorldInterfaces.World. The incoming
// handoff image is always declared; a kernel compiled with SDF_SHADOW_FADE_SLOTS reads its fade count from the pass block.
#ifndef SDF_SHADOW_FADE_SLOTS
#define SDF_SHADOW_FADE_SLOTS 0
#endif
#ifdef SDF_INDIRECT_PASS
#include "../isa/indirect.interface.hlsli"
#else
#include "../isa/sdf-world.interface.hlsli"
#endif

// The field modules, in the order the interpreter declares them: each reads only what the modules before it declare.
#include "sdf-program.hlsli"
#include "sdf-point.hlsli"
#include "sdf-noise.hlsli"
#include "sdf-shapes.hlsli"
#include "sdf-blend.hlsli"
#include "sdf-gradients.hlsli"
#include "sdf-layout.hlsli"
#include "sdf-tape.hlsli"
#include "sdf-parts.hlsli"
#include "sdf-map.hlsli"
#include "sdf-map-grad.hlsli"
#include "sdf-instance-flags.hlsli"

#endif
