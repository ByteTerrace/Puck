// The discriminating producer: the same regions as seed.hlsl, except the NV12 header names the BT.601 matrix and every
// palette index is one higher, so each chroma-bearing NV12 quadrant and every palette quadrant turns another color while
// the neutral white quadrant holds.
#define SEED_NV12_MATRIX 0u
#define SEED_INDEX_BASE 6u
#include "seed-discriminating.interface.hlsli"
#include "seed.hlsli"
