// The glass raster accumulates clipped ray intervals in integer words. For each closed draw a back face adds
// and a front face subtracts the interval from the near plane to the face or opaque hit, whichever comes first.
// The same difference applies to the RGB optical depth, weighted by the material's absorption. Winding and
// material keys use every forward face, so a solid still contributes when opaque geometry hides its exit.
// The front record holds up to three visible entries in any order and the nearest back distance. The compute
// pass uses them to shade the nearest interfaces and the medium at the start of the ray.
#ifndef FRAME_SDF_GLASS_HLSLI
#define FRAME_SDF_GLASS_HLSLI
#include "sdf-optical-depth.hlsli"

#if defined(SDF_GLASS_ACCUMULATES)
#define SDF_GLASS_SUMS_READ sdfGlassSumsRW
#else
#define SDF_GLASS_SUMS_READ sdfGlassSums
#endif

// The second record's words within a pixel's SDF_GLASS_SUM_WORDS, after the sums.
static const uint SdfGlassFrontCountWord = 8u;
static const uint SdfGlassFrontEntryWord = 9u;
static const uint SdfGlassFrontEntries = 3u;
static const uint SdfGlassNearestBackWord = 15u;
static const uint SdfGlassWindingWord = 1u;
static const uint SdfGlassMaterialWord = 16u;

uint sdfGlassSumWord(uint2 pixel, uint2 extent) {
    return (SDF_GLASS_SUM_WORDS * ((pixel.y * extent.x) + pixel.x));
}
// The clipped intervals' summed length, bounded to the segment being shaded.
float sdfGlassThickness(int sum, float front, float opaque) {
    return clamp((float(sum) / SDF_GLASS_SUM_SCALE), 0.0, max((opaque - front), 0.0));
}
// The thickness of the pixel whose sums start at `word`.
float sdfGlassThicknessAt(uint word, float front, float opaque) {
    return sdfGlassThickness(asint(SDF_GLASS_SUMS_READ[word]), front, opaque);
}
// One 64-bit optical depth sum, its low word at `word` and its high word after it, in optical depth.
float sdfGlassDepthOf(uint word) {
    return sdfOpticalDepthOf(SDF_GLASS_SUMS_READ[word], SDF_GLASS_SUMS_READ[word + 1u]);
}
// The RGB optical depth through every glass material before the opaque hit, of the pixel whose sums start at `word`.
// Each closed, homogeneous solid contributes its clipped intervals.
float3 sdfGlassOpticalDepthAt(uint word) {
    return float3(sdfGlassDepthOf(word + 2u), sdfGlassDepthOf(word + 4u), sdfGlassDepthOf(word + 6u));
}
// The count of glass front faces before the opaque hit, of the pixel whose sums start at `word`.
uint sdfGlassFrontCountAt(uint word) {
    return SDF_GLASS_SUMS_READ[(word + SdfGlassFrontCountWord)];
}
// The draw and triangle of the front face that claimed `entry`, below both SdfGlassFrontEntries and the front count.
void sdfGlassFrontAt(uint word, uint entry, out uint draw, out uint triangleIndex) {
    uint at = (word + SdfGlassFrontEntryWord + (2u * entry));

    draw = (SDF_GLASS_SUMS_READ[at] - 1u);
    triangleIndex = SDF_GLASS_SUMS_READ[(at + 1u)];
}
// The ray distance of the nearest glass back face before the opaque hit, or -1 where none lies there.
float sdfGlassNearestBackAt(uint word) {
    uint key = SDF_GLASS_SUMS_READ[(word + SdfGlassNearestBackWord)];

    return ((key == 0u) ? -1.0 : asfloat(~key));
}

// A positive winding says the ray begins within a closed glass draw. For a single homogeneous medium
// the material indices on all fully traversed solids cancel, leaving the containing material.
int sdfGlassWindingAt(uint word) {
    return asint(SDF_GLASS_SUMS_READ[word + SdfGlassWindingWord]);
}
int sdfGlassInsideMaterialAt(uint word) {
    return asint(SDF_GLASS_SUMS_READ[word + SdfGlassMaterialWord]) - 1;
}

#if defined(SDF_GLASS_ACCUMULATES)
// Writes one crossing to the pixel. The caller excludes helper lanes.
void sdfGlassAccumulate(uint word, float t, float start, float opaque, bool front, float3 absorption, int material, uint draw, uint triangleIndex, bool clippedDraw) {
    bool visible = (t < opaque);
    if (clippedDraw && !visible) return;

    // Integrate a closed draw from the eye to the opaque hit by clamping both endpoints to that hit.
    // A draw cut by a portal plane can lack either endpoint. Keep its receiver-relative integral.
    float from = (clippedDraw ? t : start);
    float to = (clippedDraw ? opaque : max(start, min(t, opaque)));
    bool positive = (clippedDraw ? front : !front);
    int distance = (int)round(to * SDF_GLASS_SUM_SCALE) - (int)round(from * SDF_GLASS_SUM_SCALE);
    uint before;

    InterlockedAdd(sdfGlassSumsRW[word], asuint(positive ? distance : -distance), before);
    if (!clippedDraw) {
        InterlockedAdd(sdfGlassSumsRW[word + SdfGlassWindingWord], asuint(front ? -1 : 1), before);
        InterlockedAdd(sdfGlassSumsRW[word + SdfGlassMaterialWord], asuint((front ? -1 : 1) * (material + 1)), before);
    }
    if (front && visible) {
        uint claim;

        InterlockedAdd(sdfGlassSumsRW[(word + SdfGlassFrontCountWord)], 1u, claim);
        if (claim < SdfGlassFrontEntries) {
            uint at = (word + SdfGlassFrontEntryWord + (2u * claim));

            sdfGlassSumsRW[at] = (draw + 1u);
            sdfGlassSumsRW[(at + 1u)] = triangleIndex;
        }
    } else if (!front && (!clippedDraw || visible)) {
        InterlockedMax(sdfGlassSumsRW[(word + SdfGlassNearestBackWord)], ~asuint(max(t, 0.0)), before);
    }

    [unroll] for (uint channel = 0u; channel < 3u; channel++) {
        sdfOpticalDepthAccumulate(sdfGlassSumsRW, word + 2u + 2u * channel, from, to, positive, absorption[channel]);
    }
}
#endif
#endif
