// The discriminating producer: the same regions as seed.hlsl, except the NV12 header names the BT.601 matrix, every
// palette index is one higher, the four-byte region's header names R8G8B8A8 over the same bytes and the transfer region's
// names the linear transfer function, so each chroma-bearing NV12 quadrant, every palette and four-byte quadrant and each
// transfer quadrant below white turns another color while the neutral white quadrants hold.
#define SEED_NV12_MATRIX 0u
#define SEED_INDEX_BASE 6u
#define SEED_RGBA_FORMAT 1u
#define SEED_TRANSFER 1u
#include "seed-discriminating.interface.hlsli"
#include "seed.hlsli"
