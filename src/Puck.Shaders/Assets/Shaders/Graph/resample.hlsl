// The frame graph's one resample pass: reconstructs its source image at the destination's extent. A destination of the
// source's own extent is an exact copy. Otherwise sharpness 0 is bilinear over the four nearest texels, and sharpness 1
// is Catmull-Rom over the sixteen nearest, clamped to the central four texels' range so its negative lobes cannot
// ring; a sharpness between blends the two. Every tap is a formatted load clamped to the source's edge, so no sampler
// state decides the filter and both backends compute the same arithmetic.
// The generated interface declares the frame group, the pass block (extent and sharpness) and the ports, which a graph
// names "as": "source" and "as": "destination".
#include "resample.interface.hlsli"

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

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint2 destinationDims = passGroup.extent;
    uint2 sourceDims;

    source.GetDimensions(sourceDims.x, sourceDims.y);
    if ((id.x >= destinationDims.x) || (id.y >= destinationDims.y)) {
        return;
    }
    if (all(sourceDims == destinationDims)) {
        destination[id.xy] = float4(source.Load(int3(id.xy, 0)).rgb, 1.0);
        return;
    }

    float2 sourcePos = ((((float2(id.xy) + 0.5) * float2(sourceDims)) / float2(destinationDims)) - 0.5);
    float2 clamped = clamp(sourcePos, float2(0.0, 0.0), (float2(sourceDims) - 1.0));
    int2 base = int2(clamped);
    float2 f = (clamped - float2(base));
    float3 c00 = tap((base + int2(0, 0)), sourceDims);
    float3 c10 = tap((base + int2(1, 0)), sourceDims);
    float3 c01 = tap((base + int2(0, 1)), sourceDims);
    float3 c11 = tap((base + int2(1, 1)), sourceDims);
    float3 bilinear = lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
    float sharpness = saturate(passGroup.sharpness);

    if (sharpness == 0.0) {
        destination[id.xy] = float4(bilinear, 1.0);
        return;
    }

    // The central four taps serve both the bilinear baseline and the cubic's middle rows, so the cubic path makes
    // exactly sixteen loads.
    float4 wx = catmullRomWeights(f.x);
    float4 wy = catmullRomWeights(f.y);
    float3 row0 =
        (wx.x * tap((base + int2(-1, -1)), sourceDims)) +
        (wx.y * tap((base + int2( 0, -1)), sourceDims)) +
        (wx.z * tap((base + int2( 1, -1)), sourceDims)) +
        (wx.w * tap((base + int2( 2, -1)), sourceDims));
    float3 row1 =
        (wx.x * tap((base + int2(-1,  0)), sourceDims)) +
        (wx.y * c00) +
        (wx.z * c10) +
        (wx.w * tap((base + int2( 2,  0)), sourceDims));
    float3 row2 =
        (wx.x * tap((base + int2(-1,  1)), sourceDims)) +
        (wx.y * c01) +
        (wx.z * c11) +
        (wx.w * tap((base + int2( 2,  1)), sourceDims));
    float3 row3 =
        (wx.x * tap((base + int2(-1,  2)), sourceDims)) +
        (wx.y * tap((base + int2( 0,  2)), sourceDims)) +
        (wx.z * tap((base + int2( 1,  2)), sourceDims)) +
        (wx.w * tap((base + int2( 2,  2)), sourceDims));
    float3 cubic = ((((wy.x * row0) + (wy.y * row1)) + (wy.z * row2)) + (wy.w * row3));
    float3 neighborhoodMin = min(min(c00, c10), min(c01, c11));
    float3 neighborhoodMax = max(max(c00, c10), max(c01, c11));

    destination[id.xy] = float4(lerp(bilinear, clamp(cubic, neighborhoodMin, neighborhoodMax), sharpness), 1.0);
}
