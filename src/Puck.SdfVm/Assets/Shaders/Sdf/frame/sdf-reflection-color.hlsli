// The reflection pass's prefiltered history colour (SdfWorldPackage.ReflectionColorWritten), which the pass's first
// dispatch writes from the preceding frame's history colour and its second reads a mirror ray's hit from
// (passes/sdf-world-views-reflections.comp.hlsl): levels one to SDF_REFLECTION_COLOR_LEVELS of the history's extent (the
// output extent, passGroup.viewsRate.zw), each texel of level k the mean of the history pixels of its 2^k block that lie
// inside the extent, premultiplied by coverage as the history is. A texel is two words, its red and green as halves,
// then its blue and coverage. Levels lie one after another from level one, each row-major over its extent, the extent
// halved per level and rounded up, as the reflection pyramid's are (sdfReflectDepthBases). KEEP IN SYNC with
// SdfWorldPackage.ReflectionColorWordsPerTile, which sizes the buffer per tile of the output extent.
#ifndef FRAME_SDF_REFLECTION_COLOR_HLSLI
#define FRAME_SDF_REFLECTION_COLOR_HLSLI
#include "sdf-reflection-depth.hlsli"

#if SDF_REFLECTION_COLOR_LEVELS > 5
#error One group of the prefilter, 8x8 invocations of level two, reduces a 32 by 32 block to every level.
#endif

uint2 sdfReflectColorPack(float4 color) {
    return uint2((f32tof16(color.r) | (f32tof16(color.g) << 16u)), (f32tof16(color.b) | (f32tof16(color.a) << 16u)));
}
float4 sdfReflectColorUnpack(uint2 words) {
    return float4(f16tof32(words.x), f16tof32(words.x >> 16u), f16tof32(words.y), f16tof32(words.y >> 16u));
}
// The first word of a level's texel, from the level's base texel (sdfReflectDepthBaseOf over the history's extent).
uint sdfReflectColorWord(uint base, uint2 dims, uint level, uint2 texel) {
    return (2u * sdfReflectDepthWord(base, dims, level, texel));
}
float4 sdfReflectColorTexel(uint base, uint2 dims, uint level, uint2 texel) {
    uint word = sdfReflectColorWord(base, dims, level, texel);

    return sdfReflectColorUnpack(uint2(sdfReflectionColorRW[word], sdfReflectionColorRW[word + 1u]));
}
// One level's bilinear sample at `uv` over the history's extent `dims`: level zero is the history itself, filtered by
// the sampler; a level above it filters its four texels about the position, each clamped into the level's extent.
float4 sdfReflectColorLevel(SdfReflectDepthBases bases, float2 uv, uint2 dims, uint level) {
    if (level == 0u) {
        return sdfReflectionHistory.SampleLevel(samplers[SDF_FILTER_LINEAR], uv, 0.0);
    }

    uint base = sdfReflectDepthBaseOf(bases, level);
    int2 last = (int2(sdfMeshPyramidExtent(dims, level)) - 1);
    float2 position = (((uv * float2(dims)) / float(1u << level)) - 0.5);
    float2 corner = floor(position);
    float2 f = (position - corner);
    int2 low = clamp(int2(corner), int2(0, 0), last);
    int2 high = clamp((int2(corner) + 1), int2(0, 0), last);
    float4 top = lerp(sdfReflectColorTexel(base, dims, level, uint2(low)), sdfReflectColorTexel(base, dims, level, uint2(high.x, low.y)), f.x);
    float4 bottom = lerp(sdfReflectColorTexel(base, dims, level, uint2(low.x, high.y)), sdfReflectColorTexel(base, dims, level, uint2(high)), f.x);

    return lerp(top, bottom, f.y);
}
// The history colour about `uv` averaged over a footprint of `radius` history pixels: the levels whose texels span the
// radius, blended between the two either side of it, so the read widens smoothly as the footprint grows and a footprint
// under a pixel reads the history itself.
float4 sdfReflectColorFootprint(float2 uv, uint2 dims, float radius) {
    SdfReflectDepthBases bases = sdfReflectDepthBases(dims);
    float lod = clamp(log2(max(radius, 1.0)), 0.0, float(SDF_REFLECTION_COLOR_LEVELS));
    uint fine = min((uint)lod, (SDF_REFLECTION_COLOR_LEVELS - 1u));
    float blend = saturate(lod - float(fine));
    float4 color = sdfReflectColorLevel(bases, uv, dims, fine);

    if (blend > 0.0) {
        color = lerp(color, sdfReflectColorLevel(bases, uv, dims, (fine + 1u)), blend);
    }
    return color;
}

#endif
