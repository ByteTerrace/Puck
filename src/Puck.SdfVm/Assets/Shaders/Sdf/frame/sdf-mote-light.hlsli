// The motes' light (SdfWorldPackage.MoteLightWritten): a grid at half the pass's output extent, rounded up, of
// SDF_MOTE_LIGHT_TEXEL_WORDS fixed-point words a texel (red, green, blue, SDF_MOTE_LIGHT_SCALE a nit), which the mote pass
// (passes/sdf-motes.comp.hlsl) adds each visible mote into and the composite reads at each output pixel. A texel's value is
// its mean light, so a mote smaller than a texel adds its light times the share of the texel it covers. The grid and its
// texel words are the one statement of the layout both read; the composite's recorder sizes the buffer to it
// (SdfSkyRecorder.MoteLightBytes). Requires moteLightRW and passGroup.extent from the sky interface.
#ifndef SDF_MOTE_LIGHT_HLSLI
#define SDF_MOTE_LIGHT_HLSLI
#include "../isa/sdf-render.hlsli"

// The grid's extent: half the output extent, rounded up.
uint2 sdfMoteGrid() {
    return ((passGroup.extent + 1u) / 2u);
}
// Whether the bound buffer holds the whole grid: the tables' dummy, bound while no mote flies, does not, and nothing
// reads or writes it then.
bool sdfMoteLightBound() {
    uint words;
    uint stride;
    uint2 grid = sdfMoteGrid();

    moteLightRW.GetDimensions(words, stride);
    return (words >= ((grid.x * grid.y) * SDF_MOTE_LIGHT_TEXEL_WORDS));
}
// Adds light (nits, a texel's mean) to the texel at `texel`; nothing outside the grid.
void sdfMoteLightAdd(int2 texel, float3 light) {
    uint2 grid = sdfMoteGrid();

    if (any(texel < 0) || any(uint2(texel) >= grid)) { return; }

    uint word = (((uint(texel.y) * grid.x) + uint(texel.x)) * SDF_MOTE_LIGHT_TEXEL_WORDS);
    uint3 steps = uint3(round(max(light, 0.0) * SDF_MOTE_LIGHT_SCALE));

    if (steps.x != 0u) { InterlockedAdd(moteLightRW[word], steps.x); }
    if (steps.y != 0u) { InterlockedAdd(moteLightRW[word + 1u], steps.y); }
    if (steps.z != 0u) { InterlockedAdd(moteLightRW[word + 2u], steps.z); }
}
// Adds light to the grid at a point in output pixels, shared by the four texels about it by their bilinear weights.
void sdfMoteLightSplat(float2 pixel, float3 light) {
    float2 at = ((pixel * 0.5) - 0.5);
    int2 base = int2(floor(at));
    float2 f = (at - float2(base));

    sdfMoteLightAdd(base, (light * ((1.0 - f.x) * (1.0 - f.y))));
    sdfMoteLightAdd((base + int2(1, 0)), (light * (f.x * (1.0 - f.y))));
    sdfMoteLightAdd((base + int2(0, 1)), (light * ((1.0 - f.x) * f.y)));
    sdfMoteLightAdd((base + int2(1, 1)), (light * (f.x * f.y)));
}
// One texel's light, in nits; black outside the grid.
float3 sdfMoteLightAt(int2 texel) {
    uint2 grid = sdfMoteGrid();

    if (any(texel < 0) || any(uint2(texel) >= grid)) { return float3(0.0, 0.0, 0.0); }

    uint word = (((uint(texel.y) * grid.x) + uint(texel.x)) * SDF_MOTE_LIGHT_TEXEL_WORDS);

    return (float3(moteLightRW[word], moteLightRW[word + 1u], moteLightRW[word + 2u]) / SDF_MOTE_LIGHT_SCALE);
}
// The motes' light at an output pixel, in nits: the grid filtered bilinearly at the pixel's centre.
float3 sdfMoteLightOf(uint2 pixel) {
    float2 at = (((float2(pixel) + 0.5) * 0.5) - 0.5);
    int2 base = int2(floor(at));
    float2 f = (at - float2(base));

    return ((((sdfMoteLightAt(base) * (1.0 - f.x)) + (sdfMoteLightAt(base + int2(1, 0)) * f.x)) * (1.0 - f.y)) +
        (((sdfMoteLightAt(base + int2(0, 1)) * (1.0 - f.x)) + (sdfMoteLightAt(base + int2(1, 1)) * f.x)) * f.y));
}

#endif
