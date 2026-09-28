// The integer hashes the SDF kernels and the passes beside them share: PCG3D and its decorrelation constants.
// Integer-only on purpose, so a hashed value comes out the same on Vulkan and Direct3D 12. It declares no resource,
// so any pass may include it.
#ifndef SDF_HASH_HLSLI
#define SDF_HASH_HLSLI

// Every hashed DECISION in the ISA is integer-only on purpose: DXC lowers multiply/add/xor/shift bit-identically to
// both SPIR-V and DXIL, while float codegen drifts +-1 LSB between the two. A cell index, a noise lattice, and a
// dither pattern therefore come out the SAME on Vulkan and Direct3D.

// Knuth's LCG step, the mixing core of PCG3D.
#define SDF_PCG_MULTIPLIER 1664525u
#define SDF_PCG_INCREMENT  1013904223u
// Decorrelation multipliers for deriving independent hash streams from one seed (the golden-ratio and Murmur3 finalizer
// constants). SDF_HASH_TUMBLE keys the CellJitter tumble stream apart from the position and material streams.
#define SDF_HASH_STREAM_A 0x9E3779B9u
#define SDF_HASH_STREAM_B 0x85EBCA6Bu
#define SDF_HASH_TUMBLE   0x27D4EB2Fu

// Canonical PCG3D integer hash (Jarzynski & Olano, "Hash Functions for GPU Rendering"): three uints in, three
// well-mixed uints out. SDF_OP_CELL_JITTER keys this on the two's-complement cell index.
uint3 sdfPcg3d(uint3 v) {
    v = ((v * SDF_PCG_MULTIPLIER) + SDF_PCG_INCREMENT);
    v.x += (v.y * v.z); v.y += (v.z * v.x); v.z += (v.x * v.y);
    v ^= (v >> 16u);
    v.x += (v.y * v.z); v.y += (v.z * v.x); v.z += (v.x * v.y);
    return v;
}

#endif // SDF_HASH_HLSLI
