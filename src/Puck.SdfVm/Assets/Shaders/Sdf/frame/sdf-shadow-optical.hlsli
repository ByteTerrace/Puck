#ifndef SDF_SHADOW_OPTICAL_HLSLI
#define SDF_SHADOW_OPTICAL_HLSLI
#include "../isa/sdf-render.hlsli"

uint sdfShadowOpticalBitmap(uint side) {
    return SDF_SHADOW_OPTICAL_TEXEL_WORDS * side * side;
}

uint sdfShadowOpticalWord(uint side, uint2 pixel) {
    return SDF_SHADOW_OPTICAL_TEXEL_WORDS * (pixel.y * side + pixel.x);
}

uint sdfShadowOpticalTilesAcross(uint side) {
    return (side + SDF_SHADOW_OPTICAL_TILE_EDGE - 1u) / SDF_SHADOW_OPTICAL_TILE_EDGE;
}
#endif
