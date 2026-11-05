// The glass sums the glass raster pass accumulates and the glass shading pass reads (SdfWorldPackage.GlassSumsWritten):
// SDF_GLASS_SUM_WORDS words a render-extent pixel, laid out as the visibility records are. The first word is the signed
// sum of every glass face's ray distance before the pixel's opaque hit, in SDF_GLASS_SUM_SCALE steps a world unit, a
// front face subtracting and a back face adding; the second the count of front faces less back faces. The remaining six
// are three 64-bit sums, low word first, of RGB optical depth: each face adds its material's absorption times its
// distance to the opaque hit, both quantized to whole steps, a front face adding and a back face subtracting. Every sum
// is two's complement integer arithmetic accumulated atomically, so the order faces rasterize in never moves a word,
// and a pair of faces cancels its shared distance to the opaque hit exactly, so no error grows with that distance.
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

// The distances and absorptions the optical depth sums quantize are clamped below 2^31 steps
// (SdfWorldPackage.GlassDepthDistanceScale, GlassAbsorptionScale), so one face's product stays below 2^62.
static const float SdfGlassDepthDistanceLimit = 65535.0;
static const float SdfGlassAbsorptionLimit = 32767.0;

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
    float steps = ((float(asint(SDF_GLASS_SUMS_READ[(word + 1u)])) * 4294967296.0) + float(SDF_GLASS_SUMS_READ[word]));

    return (steps / (SDF_GLASS_ABSORPTION_SCALE * SDF_GLASS_DEPTH_DISTANCE_SCALE));
}
// The RGB optical depth through every glass material before the opaque hit, of the pixel whose sums start at `word`.
// It is negative only where the eye is inside a glass solid.
float3 sdfGlassOpticalDepthAt(uint word) {
    return float3(sdfGlassDepthOf(word + 2u), sdfGlassDepthOf(word + 4u), sdfGlassDepthOf(word + 6u));
}

#if defined(SDF_GLASS_ACCUMULATES)
// The 64-bit product of two words, low word first, from their 16-bit halves.
uint2 sdfGlassProduct(uint left, uint right) {
    uint low = ((left & 0xFFFFu) * (right & 0xFFFFu));
    uint lowHigh = ((left & 0xFFFFu) * (right >> 16u));
    uint highLow = ((left >> 16u) * (right & 0xFFFFu));
    uint middle = ((low >> 16u) + (lowHigh & 0xFFFFu) + (highLow & 0xFFFFu));

    return uint2(((low & 0xFFFFu) | (middle << 16u)), (((left >> 16u) * (right >> 16u)) + (lowHigh >> 16u) + (highLow >> 16u) + (middle >> 16u)));
}
// Adds a 64-bit value, low word first, to the sum at `word`: the low word atomically, then the high word with the
// carry that addition produced. Every carry is counted once, so the sum is exact in any order.
void sdfGlassAddWide(uint word, uint2 value) {
    uint before;
    uint ignored;

    InterlockedAdd(sdfGlassSumsRW[word], value.x, before);
    InterlockedAdd(sdfGlassSumsRW[(word + 1u)], (value.y + ((before > (0xFFFFFFFFu - value.x)) ? 1u : 0u)), ignored);
}
// Adds one glass face at ray distance `t`, before the opaque hit at `opaque`, to the pixel whose sums start at `word`:
// its signed distance and face count, and its material's absorption over the distance from the face to the opaque hit.
// Paired faces subtract out the air between solids; an opaque surface inside a solid clips its interval without
// needing a back face behind it. The caller keeps helper lanes out.
void sdfGlassAccumulate(uint word, float t, float opaque, bool front, float3 absorption) {
    int distance = (int)round(t * SDF_GLASS_SUM_SCALE);
    uint before;

    InterlockedAdd(sdfGlassSumsRW[word], asuint(front ? -distance : distance), before);
    InterlockedAdd(sdfGlassSumsRW[(word + 1u)], asuint(front ? 1 : -1), before);

    uint opaqueSteps = (uint)round(min(opaque, SdfGlassDepthDistanceLimit) * SDF_GLASS_DEPTH_DISTANCE_SCALE);
    uint faceSteps = (uint)round(min(max(t, 0.0), SdfGlassDepthDistanceLimit) * SDF_GLASS_DEPTH_DISTANCE_SCALE);
    uint steps = ((opaqueSteps > faceSteps) ? (opaqueSteps - faceSteps) : 0u);
    uint3 absorptionSteps = (uint3)round(clamp(absorption, 0.0, SdfGlassAbsorptionLimit) * SDF_GLASS_ABSORPTION_SCALE);

    [unroll] for (uint channel = 0u; (channel < 3u); channel++) {
        uint2 depth = sdfGlassProduct(absorptionSteps[channel], steps);

        if (!front) { depth = uint2((0u - depth.x), ((~depth.y) + ((depth.x == 0u) ? 1u : 0u))); }
        sdfGlassAddWide((word + 2u + (2u * channel)), depth);
    }
}
#endif
#endif
