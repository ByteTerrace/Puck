// The glass sums the glass raster pass accumulates and the glass shading pass reads (SdfWorldPackage.GlassSumsWritten):
// SDF_GLASS_SUM_WORDS words a render-extent pixel, laid out as the visibility records are. The first word is the signed
// sum of every glass face's ray distance before the pixel's opaque hit, in SDF_GLASS_SUM_SCALE steps a world unit, a
// front face subtracting and a back face adding; the second the count of front faces less back faces. The next six
// are three 64-bit sums, low word first, of RGB optical depth: each face adds its material's absorption times its
// distance to the opaque hit, both quantized to whole steps, a front face adding and a back face subtracting. Every sum
// is two's complement integer arithmetic accumulated atomically, so the order faces rasterize in never moves a word,
// and a pair of faces cancels its shared distance to the opaque hit exactly, so no error grows with that distance.
// The rest is the pixel's second glass record (SdfGlassFrontCountWord on): the count of front faces before the opaque
// hit, each of which claims the next of SdfGlassFrontEntries entries (its draw plus one and its triangle) while one is
// free, and the nearest back face's ray distance as an atomic maximum of its complemented bits (zero for none). Up to
// SdfGlassFrontEntries front faces every one is held, so the shading pass names the second-nearest exactly whatever
// order the entries were claimed in; past that the count says so and no second interface is named.
// The raster defines SDF_GLASS_ACCUMULATES before including this file and reads and writes sdfGlassSumsRW; the shading
// pass reads sdfGlassSums.
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

uint sdfGlassSumWord(uint2 pixel, uint2 extent) {
    return (SDF_GLASS_SUM_WORDS * ((pixel.y * extent.x) + pixel.x));
}
// The distance a ray travels through glass before its opaque hit at `opaque`, from the sums: the matched faces' sum,
// plus the opaque distance for each front face whose back face lies behind the opaque hit (a solid holding an opaque
// object), never more than the distance from the nearest front face at `front` to the opaque hit, and never negative.
// An unmatched back face (the eye inside a glass solid) pairs with nothing, so the glass before it is not counted.
float sdfGlassThickness(int sum, int unmatched, float front, float opaque) {
    float matched = (float(sum) / SDF_GLASS_SUM_SCALE);
    float thickness = (matched + (float(max(unmatched, 0)) * opaque));

    return clamp(thickness, 0.0, max((opaque - front), 0.0));
}
// The thickness of the pixel whose sums start at `word`.
float sdfGlassThicknessAt(uint word, float front, float opaque) {
    return sdfGlassThickness(asint(SDF_GLASS_SUMS_READ[word]), asint(SDF_GLASS_SUMS_READ[(word + 1u)]), front, opaque);
}
// One 64-bit optical depth sum, its low word at `word` and its high word after it, in optical depth.
float sdfGlassDepthOf(uint word) {
    return sdfOpticalDepthOf(SDF_GLASS_SUMS_READ[word], SDF_GLASS_SUMS_READ[word + 1u]);
}
// The RGB optical depth through every glass material before the opaque hit, of the pixel whose sums start at `word`.
// It is negative only where the eye is inside a glass solid.
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

#if defined(SDF_GLASS_ACCUMULATES)
// Adds one glass face at ray distance `t`, before the opaque hit at `opaque`, to the pixel whose sums start at `word`:
// its signed distance and face count, its material's absorption over the distance from the face to the opaque hit,
// and to the second record a front face's draw and triangle or a back face's distance. Paired faces subtract out the
// air between solids; an opaque surface inside a solid clips its interval without needing a back face behind it. The
// caller keeps helper lanes out.
void sdfGlassAccumulate(uint word, float t, float opaque, bool front, float3 absorption, uint draw, uint triangleIndex) {
    int distance = (int)round(t * SDF_GLASS_SUM_SCALE);
    uint before;

    InterlockedAdd(sdfGlassSumsRW[word], asuint(front ? -distance : distance), before);
    InterlockedAdd(sdfGlassSumsRW[(word + 1u)], asuint(front ? 1 : -1), before);
    if (front) {
        uint claim;

        InterlockedAdd(sdfGlassSumsRW[(word + SdfGlassFrontCountWord)], 1u, claim);
        if (claim < SdfGlassFrontEntries) {
            uint at = (word + SdfGlassFrontEntryWord + (2u * claim));

            sdfGlassSumsRW[at] = (draw + 1u);
            sdfGlassSumsRW[(at + 1u)] = triangleIndex;
        }
    } else {
        InterlockedMax(sdfGlassSumsRW[(word + SdfGlassNearestBackWord)], ~asuint(max(t, 0.0)), before);
    }

    [unroll] for (uint channel = 0u; channel < 3u; channel++) {
        sdfOpticalDepthAccumulate(sdfGlassSumsRW, word + 2u + 2u * channel, t, opaque, front, absorption[channel]);
    }
}
#endif
#endif
