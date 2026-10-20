// Shared distance-field helpers for shade-time consumers: the multi-channel glyph decode (bilinear, then the median of
// the three channels) and its coverage ramp, and the 2D box distance. Each consumer keeps its own texel fetch, since
// HLSL has no function pointers; Puck.Text.MtsdfSampling is the CPU twin of the decode.
#ifndef PUCK_MSDF_HLSLI
#define PUCK_MSDF_HLSLI

// The median of three channels: the multi-channel distance the corners keep sharp. Shade-time only; it kinks a marched
// field.
float puckMedian(float3 s) {
    return max(min(s.r, s.g), min(max(s.r, s.g), s.b));
}
// Per-channel bilinear blend of four texels at the fraction f inside their cell, then the median of the blend.
float puckMsdfReconstruct(float3 s00, float3 s10, float3 s01, float3 s11, float2 f) {
    return puckMedian(lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y));
}
// Coverage of an encoded distance's offset from the edge (the encoded value minus 0.5): half at the edge, one inside by
// half a screen pixel, zero outside by the same. `screenPxRange` is the distance range in texels times the screen
// pixels per texel.
float puckMsdfCoverage(float screenPxRange, float edgeOffset) {
    return saturate((screenPxRange * edgeOffset) + 0.5);
}
// The signed distance to a 2D box from q = abs(p) - halfSize: negative inside, the Euclidean distance to the nearest edge
// or corner outside.
float puckBoxDistance2(float2 q) {
    return (length(max(q, 0.0)) + min(max(q.x, q.y), 0.0));
}

#endif
