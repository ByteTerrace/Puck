#ifndef FRAME_SDF_HISTORY_SURFACE_HLSLI
#define FRAME_SDF_HISTORY_SURFACE_HLSLI

// The address of history surface word `word` of output pixel `pixel` (its row-major index) in an output of `extent`: the
// one place the surface's planar layout is spelled.
uint sdfHistoryWord(uint pixel, uint word, uint2 extent) {
    return ((word * (extent.x * extent.y)) + pixel);
}

// Word five holds the feature lifetime in bits 16..29 and two smooth-direction bits in 30..31 (x then y).
static const uint SdfHistoryRateShift = 30u;
static const uint SdfHistoryFeatureMask = (1u << SdfHistoryRateShift) - 1u;

#endif
