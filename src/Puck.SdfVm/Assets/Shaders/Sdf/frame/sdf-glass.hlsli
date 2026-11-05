// The glass sums the glass raster pass accumulates and the glass shading pass reads (SdfWorldPackage.GlassSumsWritten):
// SDF_GLASS_SUM_WORDS words a render-extent pixel, laid out as the visibility records are. The first word is the signed
// sum of every glass face's ray distance before the pixel's opaque hit, in SDF_GLASS_SUM_SCALE steps a world unit, a
// front face subtracting and a back face adding; the second the count of front faces less back faces. Both are two's
// complement in a uint, accumulated atomically, so the order faces rasterize in never moves a sum.
// The remaining three words are presentation floats accumulating RGB optical depth through all materials, by atomic
// compare/exchange. Their rounding can depend on raster order; simulation state never reads them.
// The raster defines SDF_GLASS_ACCUMULATES before including this file and reads and writes sdfGlassSumsRW; the shading
// pass reads sdfGlassSums.
#ifndef FRAME_SDF_GLASS_HLSLI
#define FRAME_SDF_GLASS_HLSLI
#include "../isa/sdf-render.hlsli"

#if defined(SDF_GLASS_ACCUMULATES)
#define SDF_GLASS_SUMS_READ sdfGlassSumsRW
#else
#define SDF_GLASS_SUMS_READ sdfGlassSums
#endif

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
// The RGB optical depth through every glass material before the opaque hit, of the pixel whose sums start at `word`.
float3 sdfGlassOpticalDepthAt(uint word) {
    return float3(asfloat(SDF_GLASS_SUMS_READ[(word + 2u)]), asfloat(SDF_GLASS_SUMS_READ[(word + 3u)]),
        asfloat(SDF_GLASS_SUMS_READ[(word + 4u)]));
}

#if defined(SDF_GLASS_ACCUMULATES)
// Presentation floats avoid overflow when a thin, strongly absorbing material lies far from the opaque hit.
// Each successful compare/exchange contributes once; helper lanes must not enter an atomic retry loop.
void sdfGlassAddOpticalDepth(uint word, float value) {
    uint expected = sdfGlassSumsRW[word];
    [allow_uav_condition] while (true) {
        uint observed;
        InterlockedCompareExchange(sdfGlassSumsRW[word], expected, asuint(asfloat(expected) + value), observed);
        if (observed == expected) { break; }
        expected = observed;
    }
}
// Adds one glass face at ray distance `t`, before the opaque hit at `opaque`, to the pixel whose sums start at `word`:
// its signed distance and face count, and its material's absorption over the interval from the face to the opaque hit.
// Paired faces subtract out the air between solids; an opaque surface inside a solid clips its interval without
// needing a back face behind it. The caller keeps helper lanes out.
void sdfGlassAccumulate(uint word, float t, float opaque, bool front, float3 absorption) {
    int distance = (int)round(t * SDF_GLASS_SUM_SCALE);
    uint before;

    InterlockedAdd(sdfGlassSumsRW[word], asuint(front ? -distance : distance), before);
    InterlockedAdd(sdfGlassSumsRW[(word + 1u)], asuint(front ? 1 : -1), before);
    float3 opticalDepth = absorption * (opaque - t);
    if (!front) { opticalDepth = -opticalDepth; }
    sdfGlassAddOpticalDepth(word + 2u, opticalDepth.x);
    sdfGlassAddOpticalDepth(word + 3u, opticalDepth.y);
    sdfGlassAddOpticalDepth(word + 4u, opticalDepth.z);
}
#endif
#endif
