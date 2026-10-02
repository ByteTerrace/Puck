// The SDF virtual machine — single-source HLSL, compiled by DXC to both SPIR-V (Vulkan) and DXIL (Direct3D 12).
// A program is a flat uint4 word stream that map() interprets per sample point: transform opcodes mutate the
// evaluation point, SHAPE opcodes evaluate a primitive and blend it into the running nearest-surface result.
// The opcode, shape, blend and packed-layout constants come from sdf-isa.hlsli, generated from Puck.SignedDistance by
// `puck shaders generate`; the word layout below and every decoder are written against Puck.SignedDistance.SdfProgram.
#ifndef SDF_VM_HLSLI
#define SDF_VM_HLSLI

#include "../isa/sdf-isa.hlsli"
#include "sdf-hash.hlsli"
// Generated resources for the common traversal, hit shading, shadow or sky interface selected by this pass.
#if defined(SDF_VIEWS_PASS)
#define SDF_LIGHTING_TABLES
#define SDF_SKY_TABLES
#include "../isa/sdf-views.interface.hlsli"
#elif defined(SDF_SHADOW_PASS)
#define SDF_LIGHTING_TABLES
#include "../isa/sdf-shadow.interface.hlsli"
#elif defined(SDF_SKY_PASS)
#define SDF_SKY_TABLES
#include "../isa/sdf-sky.interface.hlsli"
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
#include "sdf-map.hlsli"
#include "sdf-map-grad.hlsli"
#include "sdf-instance-flags.hlsli"

#endif
