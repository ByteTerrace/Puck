// The glass raster accumulates clipped ray intervals in integer words. For each closed draw a back face adds
// and a front face subtracts the interval from the near plane to the face or opaque hit, whichever comes first.
// The same difference applies to the RGB optical depth, weighted by the material's absorption. Winding and
// material keys use every forward face, so a solid still contributes when opaque geometry hides its exit.
// Every visible front face also enters the pixel's layer list: SDF_GLASS_LAYERS 64-bit entries, each a face's key
// (its distance's code above its draw and triangle) stored inverted, so the cleared zero is empty and a 64-bit atomic
// maximum keeps the nearest. A face takes the first entry it beats and carries the entry it displaced on to the next, so
// the list ends holding the nearest SDF_GLASS_LAYERS faces in distance order whatever order they rasterized in. The
// compute pass shades those layers as interfaces of their own and the medium at the start of the ray; every face,
// listed or not, absorbs through the sums.
//
// A pixel's words, SDF_GLASS_SUM_WORDS of them: the clipped thickness (0), the winding (1), the RGB optical depth as
// three 64-bit sums (2 to 7), the layer list (SdfGlassLayerWord, two words an entry, low word first), the signed
// material key (SdfGlassMaterialWord) and the nearest back face (SdfGlassNearestBackWord).
#ifndef FRAME_SDF_GLASS_HLSLI
#define FRAME_SDF_GLASS_HLSLI
#include "sdf-optical-depth.hlsli"

#if defined(SDF_GLASS_ACCUMULATES)
#define SDF_GLASS_SUMS_READ sdfGlassSumsRW
#else
#define SDF_GLASS_SUMS_READ sdfGlassSums
#endif

static const uint SdfGlassWindingWord = 1u;
static const uint SdfGlassLayerWord = 8u;
static const uint SdfGlassMaterialWord = (SdfGlassLayerWord + (2u * SDF_GLASS_LAYERS));
static const uint SdfGlassNearestBackWord = (SdfGlassMaterialWord + 1u);
// A layer key's fields, from the low bit: the triangle (24 bits), the draw (18 bits) and the distance's code (22 bits).
// A face whose draw or triangle passes its field absorbs but takes no entry.
static const uint SdfGlassLayerTriangleBits = 24u;
static const uint SdfGlassLayerDrawBits = 18u;
static const uint SdfGlassLayerDrawLimit = ((1u << SdfGlassLayerDrawBits) - 1u);
static const uint SdfGlassLayerTriangleLimit = (1u << SdfGlassLayerTriangleBits);
// The distance code: log2 of the ray distance over [2^-10, 2^16) metres in 2^22 steps, so a code orders faces a few
// millionths of their distance apart; the compute pass orders ties by the faces' exact planes.
static const float SdfGlassLayerCodeLow = -10.0;
static const float SdfGlassLayerCodeOctaves = 26.0;
static const float SdfGlassLayerCodeSteps = 4194303.0;

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
// The draw and triangle of the layer list's `entry`th face, nearest first; false where the entry is empty, and so is
// every entry after it.
bool sdfGlassLayerAt(uint word, uint entry, out uint draw, out uint triangleIndex) {
    uint at = (word + SdfGlassLayerWord + (2u * entry));
    // The stored entry is the key inverted.
    uint low = SDF_GLASS_SUMS_READ[at];
    uint high = SDF_GLASS_SUMS_READ[(at + 1u)];
    uint keyLow = ~low;
    uint keyHigh = ~high;

    triangleIndex = (keyLow & (SdfGlassLayerTriangleLimit - 1u));
    draw = ((keyLow >> SdfGlassLayerTriangleBits) | ((keyHigh & ((1u << (SdfGlassLayerDrawBits - (32u - SdfGlassLayerTriangleBits))) - 1u)) << (32u - SdfGlassLayerTriangleBits)));

    return ((low | high) != 0u);
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
// Enters a visible front face at ray distance `t` into the layer list of the pixel whose sums start at `word`: the
// nearest SDF_GLASS_LAYERS faces stay, in distance order, whatever order the faces arrive in. A displaced entry moves
// on to the next; the last entry's loser leaves the list.
void sdfGlassEnterLayer(uint word, float t, uint draw, uint triangleIndex) {
    if ((draw >= SdfGlassLayerDrawLimit) || (triangleIndex >= SdfGlassLayerTriangleLimit)) return;

    uint code = (uint)(saturate(((log2(max(t, exp2(SdfGlassLayerCodeLow))) - SdfGlassLayerCodeLow) / SdfGlassLayerCodeOctaves)) * SdfGlassLayerCodeSteps);
    uint64_t key = ((((uint64_t)code) << (SdfGlassLayerDrawBits + SdfGlassLayerTriangleBits)) |
        (((uint64_t)draw) << SdfGlassLayerTriangleBits) | ((uint64_t)triangleIndex));
    uint64_t carried = ~key;
    uint first = ((word + SdfGlassLayerWord) / 2u);

    [unroll] for (uint entry = 0u; entry < SDF_GLASS_LAYERS; entry++) {
        uint64_t before;

        InterlockedMax(sdfGlassLayersRW[(first + entry)], carried, before);
        carried = min(carried, before);
        if (carried == 0u) break;
    }
}
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
        sdfGlassEnterLayer(word, max(t, 0.0), draw, triangleIndex);
    } else if (!front && (!clippedDraw || visible)) {
        InterlockedMax(sdfGlassSumsRW[(word + SdfGlassNearestBackWord)], ~asuint(max(t, 0.0)), before);
    }

    [unroll] for (uint channel = 0u; channel < 3u; channel++) {
        sdfOpticalDepthAccumulate(sdfGlassSumsRW, word + 2u + 2u * channel, from, to, positive, absorption[channel]);
    }
}
#endif
#endif
