// Shared exact-copy, bilinear and clamped Catmull-Rom reconstruction. Loads use the active render grid,
// which can be smaller than the backing image. Color is premultiplied RGBA; no alpha carries reactivity. A source written
// only inside part of its grid is reconstructed through the *Within forms, which read every texel outside that box,
// [current.xy, current.zw), as transparent.
#ifndef PUCK_RECONSTRUCTION_HLSLI
#define PUCK_RECONSTRUCTION_HLSLI

// The box every texel of a wholly written source lies in.
static const uint4 PuckReconstructionAllCurrent = uint4(0u, 0u, 0xFFFFFFFFu, 0xFFFFFFFFu);

float4 puckCatmullRomWeights(float t) {
    float t2 = (t * t);
    float t3 = (t2 * t);

    return float4(
        ((-0.5 * t) + t2 - (0.5 * t3)),
        (1.0 - (2.5 * t2) + (1.5 * t3)),
        ((0.5 * t) + (2.0 * t2) - (1.5 * t3)),
        ((-0.5 * t2) + (0.5 * t3))
    );
}
float4 puckReconstructionTapWithin(Texture2D<float4> image, int2 pixel, uint2 sourceDims, uint2 sourceOrigin, uint4 current) {
    uint2 p = (uint2)clamp(pixel, int2(0, 0), (int2(sourceDims) - 1));

    if (any(p < current.xy) || any(p >= current.zw)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    return image.Load(int3((p + sourceOrigin), 0));
}
float4 puckReconstructionTap(Texture2D<float4> image, int2 pixel, uint2 sourceDims, uint2 sourceOrigin) {
    return puckReconstructionTapWithin(image, pixel, sourceDims, sourceOrigin, PuckReconstructionAllCurrent);
}
// The sixteen texels a reconstruction at a continuous source position in texel units reads, the first texel's center at
// zero: the 4x4 neighbourhood around its bilinear origin, the fractions toward the next texel, the Catmull-Rom weights
// along each axis and the saturated sharpness. A caller that reconstructs several quantities with one set of weights
// (the SDF resolve's color and transport) reads each quantity's taps where puckReconstructionReads says, at
// puckReconstructionTapOffset from the origin, adds each to its own PuckReconstructionSum as it reads it and finishes
// each with puckReconstructionFinish.
struct PuckReconstructionFootprint {
    int2 origin;
    float2 f;
    float4 wx;
    float4 wy;
    float sharpness;
};
PuckReconstructionFootprint puckReconstructionFootprintAt(float2 sourcePos, uint2 sourceDims, float sharpness) {
    float2 clamped = clamp(sourcePos, float2(0.0, 0.0), (float2(sourceDims) - 1.0));
    PuckReconstructionFootprint footprint;

    footprint.origin = int2(clamped);
    footprint.f = (clamped - float2(footprint.origin));
    footprint.wx = puckCatmullRomWeights(footprint.f.x);
    footprint.wy = puckCatmullRomWeights(footprint.f.y);
    footprint.sharpness = saturate(sharpness);

    return footprint;
}
// A footprint tap's offset from its origin: the sixteen row by row from (-1, -1), the bilinear four at 5, 6, 9 and 10.
int2 puckReconstructionTapOffset(uint tap) {
    return int2(((int)(tap & 3u) - 1), ((int)(tap >> 2u) - 1));
}
// Whether a reconstruction reads a tap: the central four always, the outer twelve only above sharpness 0, so the
// bilinear path makes four loads and the cubic path sixteen.
bool puckReconstructionReads(PuckReconstructionFootprint footprint, uint tap) {
    uint2 cell = uint2((tap & 3u), (tap >> 2u));

    return ((footprint.sharpness > 0.0) || (all(cell >= 1u) && all(cell <= 2u)));
}
// A reconstruction in progress: the taps read so far, weighted for the bilinear and the Catmull-Rom result, and the
// central four's bounds, which clamp the cubic. Adding taps one at a time holds four values a quantity where the
// sixteen taps would hold sixteen; with a loop unrolled over the taps every weight below folds to a constant choice.
struct PuckReconstructionSum {
    float4 bilinear;
    float4 cubic;
    float4 low;
    float4 high;
};
PuckReconstructionSum puckReconstructionSumStart() {
    PuckReconstructionSum sum;

    sum.bilinear = float4(0.0, 0.0, 0.0, 0.0);
    sum.cubic = float4(0.0, 0.0, 0.0, 0.0);
    sum.low = float4(3.402823e+38, 3.402823e+38, 3.402823e+38, 3.402823e+38);
    sum.high = -sum.low;

    return sum;
}
// Adds a footprint tap's value: above sharpness 0 its Catmull-Rom weight is its column's times its row's, and each of
// the central four also takes its bilinear weight and bounds the clamp. A tap the footprint does not read adds a zero
// value.
void puckReconstructionAdd(PuckReconstructionFootprint footprint, uint tap, float4 value, inout PuckReconstructionSum sum) {
    uint2 cell = uint2((tap & 3u), (tap >> 2u));

    if (footprint.sharpness > 0.0) {
        sum.cubic += ((footprint.wx[cell.x] * footprint.wy[cell.y]) * value);
    }
    if (all(cell >= 1u) && all(cell <= 2u)) {
        float2 weight = float2(
            ((cell.x == 1u) ? (1.0 - footprint.f.x) : footprint.f.x),
            ((cell.y == 1u) ? (1.0 - footprint.f.y) : footprint.f.y)
        );

        sum.bilinear += ((weight.x * weight.y) * value);
        sum.low = min(sum.low, value);
        sum.high = max(sum.high, value);
    }
}
// Finishes a reconstruction: bilinear over the central four at sharpness 0, blending to clamped Catmull-Rom over all
// sixteen at sharpness 1.
float4 puckReconstructionFinish(PuckReconstructionFootprint footprint, PuckReconstructionSum sum) {
    if (footprint.sharpness == 0.0) {
        return sum.bilinear;
    }

    return lerp(sum.bilinear, clamp(sum.cubic, sum.low, sum.high), footprint.sharpness);
}
// Reconstructs at a continuous source position in texel units, the first texel's center at zero: bilinear over the four
// nearest texels at sharpness 0, blending to clamped Catmull-Rom over the sixteen nearest at sharpness 1.
float4 puckReconstructAtWithin(Texture2D<float4> image, float2 sourcePos, uint2 sourceDims, uint2 sourceOrigin, float sharpness, uint4 current) {
    PuckReconstructionFootprint footprint = puckReconstructionFootprintAt(sourcePos, sourceDims, sharpness);
    PuckReconstructionSum sum = puckReconstructionSumStart();

    [unroll] for (uint tap = 0u; tap < 16u; tap++) {
        float4 value = float4(0.0, 0.0, 0.0, 0.0);

        if (puckReconstructionReads(footprint, tap)) {
            value = puckReconstructionTapWithin(image, (footprint.origin + puckReconstructionTapOffset(tap)), sourceDims, sourceOrigin, current);
        }
        puckReconstructionAdd(footprint, tap, value, sum);
    }

    return puckReconstructionFinish(footprint, sum);
}
float4 puckReconstructAt(Texture2D<float4> image, float2 sourcePos, uint2 sourceDims, uint2 sourceOrigin, float sharpness) {
    return puckReconstructAtWithin(image, sourcePos, sourceDims, sourceOrigin, sharpness, PuckReconstructionAllCurrent);
}
// Reconstructs a rect's pixel from its source: an exact copy where the rect has the source's extent, otherwise the
// source resampled at the pixel's center (puckReconstructAt).
float4 puckReconstructRegionWithin(Texture2D<float4> image, uint2 pixel, uint2 rectDims, uint2 sourceDims, uint2 sourceOrigin, float sharpness, uint4 current) {
    if (all(sourceDims == rectDims)) {
        return puckReconstructionTapWithin(image, int2(pixel), sourceDims, sourceOrigin, current);
    }

    float2 sourcePos = ((((float2(pixel) + 0.5) * float2(sourceDims)) / float2(rectDims)) - 0.5);

    return puckReconstructAtWithin(image, sourcePos, sourceDims, sourceOrigin, sharpness, current);
}
float4 puckReconstructRegion(Texture2D<float4> image, uint2 pixel, uint2 rectDims, uint2 sourceDims, uint2 sourceOrigin, float sharpness) {
    return puckReconstructRegionWithin(image, pixel, rectDims, sourceDims, sourceOrigin, sharpness, PuckReconstructionAllCurrent);
}

float4 puckReconstruct(Texture2D<float4> image, uint2 pixel, uint2 rectDims, uint2 sourceDims, float sharpness) {
    return puckReconstructRegion(image, pixel, rectDims, sourceDims, uint2(0, 0), sharpness);
}

#endif
