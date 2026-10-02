// Shared exact-copy, bilinear and clamped Catmull-Rom reconstruction. Loads use the active render grid,
// which can be smaller than the backing image. Color is premultiplied RGBA; no alpha carries reactivity.
#ifndef PUCK_RECONSTRUCTION_HLSLI
#define PUCK_RECONSTRUCTION_HLSLI

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
float4 puckReconstructionTap(Texture2D<float4> image, int2 pixel, uint2 sourceDims, uint2 sourceOrigin) {
    uint2 p = (uint2)clamp(pixel, int2(0, 0), (int2(sourceDims) - 1));

    return image.Load(int3((p + sourceOrigin), 0));
}
// Reconstructs at a continuous source position in texel units, the first texel's center at zero: bilinear over the four
// nearest texels at sharpness 0, blending to clamped Catmull-Rom over the sixteen nearest at sharpness 1.
float4 puckReconstructAt(Texture2D<float4> image, float2 sourcePos, uint2 sourceDims, uint2 sourceOrigin, float sharpness) {
    float2 clamped = clamp(sourcePos, float2(0.0, 0.0), (float2(sourceDims) - 1.0));
    int2 origin = int2(clamped);
    float2 f = (clamped - float2(origin));
    float4 c00 = puckReconstructionTap(image, (origin + int2(0, 0)), sourceDims, sourceOrigin);
    float4 c10 = puckReconstructionTap(image, (origin + int2(1, 0)), sourceDims, sourceOrigin);
    float4 c01 = puckReconstructionTap(image, (origin + int2(0, 1)), sourceDims, sourceOrigin);
    float4 c11 = puckReconstructionTap(image, (origin + int2(1, 1)), sourceDims, sourceOrigin);
    float4 bilinear = lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
    sharpness = saturate(sharpness);

    if (sharpness == 0.0) {
        return bilinear;
    }

    // The central four taps serve both the bilinear baseline and the cubic's middle rows, so the cubic path makes
    // exactly sixteen loads.
    float4 wx = puckCatmullRomWeights(f.x);
    float4 wy = puckCatmullRomWeights(f.y);
    float4 row0 =
        (wx.x * puckReconstructionTap(image, (origin + int2(-1, -1)), sourceDims, sourceOrigin)) +
        (wx.y * puckReconstructionTap(image, (origin + int2( 0, -1)), sourceDims, sourceOrigin)) +
        (wx.z * puckReconstructionTap(image, (origin + int2( 1, -1)), sourceDims, sourceOrigin)) +
        (wx.w * puckReconstructionTap(image, (origin + int2( 2, -1)), sourceDims, sourceOrigin));
    float4 row1 =
        (wx.x * puckReconstructionTap(image, (origin + int2(-1,  0)), sourceDims, sourceOrigin)) +
        (wx.y * c00) +
        (wx.z * c10) +
        (wx.w * puckReconstructionTap(image, (origin + int2( 2,  0)), sourceDims, sourceOrigin));
    float4 row2 =
        (wx.x * puckReconstructionTap(image, (origin + int2(-1,  1)), sourceDims, sourceOrigin)) +
        (wx.y * c01) +
        (wx.z * c11) +
        (wx.w * puckReconstructionTap(image, (origin + int2( 2,  1)), sourceDims, sourceOrigin));
    float4 row3 =
        (wx.x * puckReconstructionTap(image, (origin + int2(-1,  2)), sourceDims, sourceOrigin)) +
        (wx.y * puckReconstructionTap(image, (origin + int2( 0,  2)), sourceDims, sourceOrigin)) +
        (wx.z * puckReconstructionTap(image, (origin + int2( 1,  2)), sourceDims, sourceOrigin)) +
        (wx.w * puckReconstructionTap(image, (origin + int2( 2,  2)), sourceDims, sourceOrigin));
    float4 cubic = ((((wy.x * row0) + (wy.y * row1)) + (wy.z * row2)) + (wy.w * row3));
    float4 neighborhoodMin = min(min(c00, c10), min(c01, c11));
    float4 neighborhoodMax = max(max(c00, c10), max(c01, c11));

    return lerp(bilinear, clamp(cubic, neighborhoodMin, neighborhoodMax), sharpness);
}
// Reconstructs a rect's pixel from its source: an exact copy where the rect has the source's extent, otherwise the
// source resampled at the pixel's center (puckReconstructAt).
float4 puckReconstructRegion(Texture2D<float4> image, uint2 pixel, uint2 rectDims, uint2 sourceDims, uint2 sourceOrigin, float sharpness) {
    if (all(sourceDims == rectDims)) {
        return image.Load(int3((pixel + sourceOrigin), 0));
    }

    float2 sourcePos = ((((float2(pixel) + 0.5) * float2(sourceDims)) / float2(rectDims)) - 0.5);

    return puckReconstructAt(image, sourcePos, sourceDims, sourceOrigin, sharpness);
}

float4 puckReconstruct(Texture2D<float4> image, uint2 pixel, uint2 rectDims, uint2 sourceDims, float sharpness) {
    return puckReconstructRegion(image, pixel, rectDims, sourceDims, uint2(0, 0), sharpness);
}

#endif
