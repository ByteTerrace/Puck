// The glass sums the glass raster pass accumulates and the glass shading pass reads (SdfWorldPackage.GlassSumsWritten):
// SDF_GLASS_SUM_WORDS words a render-extent pixel, laid out as the visibility records are. The first word is the signed
// sum of every glass face's ray distance before the pixel's opaque hit, in SDF_GLASS_SUM_SCALE steps a world unit, a
// front face subtracting and a back face adding; the second the count of front faces less back faces. Both are two's
// complement in a uint, accumulated atomically, so the order faces rasterize in never moves a sum.
#ifndef FRAME_SDF_GLASS_HLSLI
#define FRAME_SDF_GLASS_HLSLI
#include "../isa/sdf-render.hlsli"

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
#endif
