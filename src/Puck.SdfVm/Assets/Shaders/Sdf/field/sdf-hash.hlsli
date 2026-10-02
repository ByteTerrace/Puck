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
// 2^-32, exact. Maps a full-range uint hash to a float in [0, 1] — NOTE the CLOSED upper end: (float)0xFFFFFFFFu
// rounds UP to 2^32, so the product can be exactly 1.0. Every consumer is written to tolerate that.
#define SDF_INV_2POW32 (1.0 / 4294967296.0)
// The R2 low-discrepancy lattice: alpha_i = round(2^32 / phi2^i) for the plastic number
// phi2 = 1.32471795724474602596 (the real root of x^3 = x + 1). The uint multiply wraps mod 2^32, which IS the
// fractional part of the additive recurrence — so the lattice is exact in fixed point.
#define SDF_R2_ALPHA1 3242174889u
#define SDF_R2_ALPHA2 2447445414u
// The R3 siblings: alpha_i = round(2^32 / phi3^i) for phi3 = 1.2207440846057596 (the real root of x^4 = x + 1).
// SDF_OP_CELL_JITTER's Blue flavor rotates these three across its axes so the offset components decorrelate.
#define SDF_R3_ALPHA1 3518319155u
#define SDF_R3_ALPHA2 2882110345u
#define SDF_R3_ALPHA3 2360945575u

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
