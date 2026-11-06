// Shared signed optical-depth integration for camera and cascade rays. Quantize the two endpoints separately so
// paired faces cancel their distance to the receiver exactly, independently of raster order.
#ifndef SDF_OPTICAL_DEPTH_HLSLI
#define SDF_OPTICAL_DEPTH_HLSLI
#include "../isa/sdf-render.hlsli"
static const float SdfGlassDepthDistanceLimit = 65535.0;
static const float SdfGlassAbsorptionLimit = 32767.0;

float sdfOpticalDepthOf(uint low, uint high) {
    float steps = float(asint(high)) * 4294967296.0 + float(low);
    return steps / (SDF_GLASS_ABSORPTION_SCALE * SDF_GLASS_DEPTH_DISTANCE_SCALE);
}

uint2 sdfGlassProduct(uint left, uint right) {
    uint low = (left & 0xFFFFu) * (right & 0xFFFFu);
    uint lowHigh = (left & 0xFFFFu) * (right >> 16u);
    uint highLow = (left >> 16u) * (right & 0xFFFFu);
    uint middle = (low >> 16u) + (lowHigh & 0xFFFFu) + (highLow & 0xFFFFu);
    return uint2((low & 0xFFFFu) | (middle << 16u), (left >> 16u) * (right >> 16u) + (lowHigh >> 16u) + (highLow >> 16u) + (middle >> 16u));
}

void sdfOpticalDepthAccumulate(RWStructuredBuffer<uint> sums, uint word, float t, float receiver, bool front, float density) {
    uint end = (uint)round(clamp(receiver, 0.0, SdfGlassDepthDistanceLimit) * SDF_GLASS_DEPTH_DISTANCE_SCALE);
    uint start = (uint)round(clamp(t, 0.0, SdfGlassDepthDistanceLimit) * SDF_GLASS_DEPTH_DISTANCE_SCALE);
    uint coefficient = (uint)round(clamp(density, 0.0, SdfGlassAbsorptionLimit) * SDF_GLASS_ABSORPTION_SCALE);
    uint2 value = sdfGlassProduct(coefficient, end > start ? end - start : 0u);
    if (!front) { value = uint2(0u - value.x, ~value.y + (value.x == 0u ? 1u : 0u)); }
    uint before;
    uint ignored;
    InterlockedAdd(sums[word], value.x, before);
    InterlockedAdd(sums[word + 1u], value.y + (before > 0xFFFFFFFFu - value.x ? 1u : 0u), ignored);
}
#endif
