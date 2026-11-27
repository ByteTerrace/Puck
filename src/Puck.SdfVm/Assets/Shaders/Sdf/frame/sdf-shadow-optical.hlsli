// A cascade entry's glass extinction (SdfWorldShadows' optical buffer), which the cascade glass raster accumulates and the
// surface pass's shadow taps read. Its words: SDF_SHADOW_OPTICAL_TEXEL_WORDS a texel (the red, or grey, 64-bit optical
// depth sum to the texel's first opaque depth, low word first, then the nearest glass entry), then the dirty-tile bitmap
// the clear consumes, then, only where a drawn glass casts colour, the colour plane: SDF_SHADOW_OPTICAL_COLOR_WORDS a
// texel, the green and the blue sums. Every reader tells the two layouts apart by the bound buffer's length
// (sdfShadowOpticalHoldsColor), so writer and readers cannot disagree; a buffer without the plane casts the red sum's
// grey. KEEP IN SYNC with SdfWorldPackage.ShadowOpticalBytes.
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

// The colour plane's first word, past the bitmap (SdfWorldPackage.ShadowOpticalBitmapWords).
uint sdfShadowOpticalColorPlane(uint side) {
    uint tiles = sdfShadowOpticalTilesAcross(side);

    return (sdfShadowOpticalBitmap(side) + (((tiles * tiles) + 31u) / 32u));
}

// The first of a texel's colour words: its green sum, then its blue sum.
uint sdfShadowOpticalColorWord(uint side, uint2 pixel) {
    return (sdfShadowOpticalColorPlane(side) + (SDF_SHADOW_OPTICAL_COLOR_WORDS * ((pixel.y * side) + pixel.x)));
}

// Whether a bound optical buffer of `words` words holds the colour plane for an entry of `side` texels.
bool sdfShadowOpticalHoldsColor(uint words, uint side) {
    return (words >= (sdfShadowOpticalColorPlane(side) + (SDF_SHADOW_OPTICAL_COLOR_WORDS * side * side)));
}
#endif
