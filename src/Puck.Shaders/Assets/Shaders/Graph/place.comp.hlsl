// The frame graph's one placement pass: reconstructs its source image into a destination rect over its base image.
// Outside the rect the destination is the base. Inside it, a source of the rect's own extent is an exact copy; otherwise
// sharpness 0 is bilinear over the four nearest texels, and sharpness 1 is Catmull-Rom over the sixteen nearest, clamped
// to the central four texels' range so its negative lobes cannot ring; a sharpness between blends the two. A rect of the
// whole destination resamples the whole source. Every tap is a formatted load clamped to its image's edge, so no sampler
// state decides the filter and both backends compute the same arithmetic.
// The generated interface declares the frame group and the pass group: the extent, the config (rect, sharpness) and
// the images base, source and destination, each image input with a sampler it never reads. rect is the destination
// rect as fractions of the destination's extent: left, top, width, height.
#include "place.interface.hlsli"

float4 catmullRomWeights(float t) {
    float t2 = (t * t);
    float t3 = (t2 * t);

    return float4(
        ((-0.5 * t) + t2 - (0.5 * t3)),
        (1.0 - (2.5 * t2) + (1.5 * t3)),
        ((0.5 * t) + (2.0 * t2) - (1.5 * t3)),
        ((-0.5 * t2) + (0.5 * t3))
    );
}
float3 tap(int2 pixel, uint2 sourceDims) {
    uint2 p = (uint2)clamp(pixel, int2(0, 0), (int2(sourceDims) - 1));

    return source.Load(int3(p, 0)).rgb;
}
float3 reconstruct(uint2 pixel, uint2 rectDims, uint2 sourceDims) {
    if (all(sourceDims == rectDims)) {
        return source.Load(int3(pixel, 0)).rgb;
    }

    float2 sourcePos = ((((float2(pixel) + 0.5) * float2(sourceDims)) / float2(rectDims)) - 0.5);
    float2 clamped = clamp(sourcePos, float2(0.0, 0.0), (float2(sourceDims) - 1.0));
    int2 origin = int2(clamped);
    float2 f = (clamped - float2(origin));
    float3 c00 = tap((origin + int2(0, 0)), sourceDims);
    float3 c10 = tap((origin + int2(1, 0)), sourceDims);
    float3 c01 = tap((origin + int2(0, 1)), sourceDims);
    float3 c11 = tap((origin + int2(1, 1)), sourceDims);
    float3 bilinear = lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
    float sharpness = saturate(passGroup.sharpness);

    if (sharpness == 0.0) {
        return bilinear;
    }

    // The central four taps serve both the bilinear baseline and the cubic's middle rows, so the cubic path makes
    // exactly sixteen loads.
    float4 wx = catmullRomWeights(f.x);
    float4 wy = catmullRomWeights(f.y);
    float3 row0 =
        (wx.x * tap((origin + int2(-1, -1)), sourceDims)) +
        (wx.y * tap((origin + int2( 0, -1)), sourceDims)) +
        (wx.z * tap((origin + int2( 1, -1)), sourceDims)) +
        (wx.w * tap((origin + int2( 2, -1)), sourceDims));
    float3 row1 =
        (wx.x * tap((origin + int2(-1,  0)), sourceDims)) +
        (wx.y * c00) +
        (wx.z * c10) +
        (wx.w * tap((origin + int2( 2,  0)), sourceDims));
    float3 row2 =
        (wx.x * tap((origin + int2(-1,  1)), sourceDims)) +
        (wx.y * c01) +
        (wx.z * c11) +
        (wx.w * tap((origin + int2( 2,  1)), sourceDims));
    float3 row3 =
        (wx.x * tap((origin + int2(-1,  2)), sourceDims)) +
        (wx.y * tap((origin + int2( 0,  2)), sourceDims)) +
        (wx.z * tap((origin + int2( 1,  2)), sourceDims)) +
        (wx.w * tap((origin + int2( 2,  2)), sourceDims));
    float3 cubic = ((((wy.x * row0) + (wy.y * row1)) + (wy.z * row2)) + (wy.w * row3));
    float3 neighborhoodMin = min(min(c00, c10), min(c01, c11));
    float3 neighborhoodMax = max(max(c00, c10), max(c01, c11));

    return lerp(bilinear, clamp(cubic, neighborhoodMin, neighborhoodMax), sharpness);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 destinationDims;
    uint2 baseDims;
    uint2 sourceDims;

    destination.GetDimensions(destinationDims.x, destinationDims.y);
    if ((id.x >= destinationDims.x) || (id.y >= destinationDims.y)) {
        return;
    }

    // The rect's pixel edges, each fraction rounded to the nearest pixel edge and clamped to the destination.
    float4 rect = passGroup.rect;
    uint2 rectMin = (uint2)clamp(floor((rect.xy * float2(destinationDims)) + 0.5), float2(0.0, 0.0), float2(destinationDims));
    uint2 rectMax = (uint2)clamp(floor(((rect.xy + rect.zw) * float2(destinationDims)) + 0.5), float2(rectMin), float2(destinationDims));

    if (any(id.xy < rectMin) || any(id.xy >= rectMax)) {
        base.GetDimensions(baseDims.x, baseDims.y);
        destination[id.xy] = base.Load(int3(min(id.xy, (baseDims - 1)), 0));
        return;
    }

    source.GetDimensions(sourceDims.x, sourceDims.y);
    destination[id.xy] = float4(reconstruct((id.xy - rectMin), (rectMax - rectMin), sourceDims), 1.0);
}
