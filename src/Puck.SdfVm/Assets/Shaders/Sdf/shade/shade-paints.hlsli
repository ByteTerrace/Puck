// A material's paints (SdfPaint), drawn over its albedo. It reads the program words, the dynamic transforms a paint riding
// a slot reads (field/sdf-program.hlsli), the shape dispatch (field/sdf-shapes.hlsli) and, for a text paint, the glyph
// ink, so a kernel that shades no hit (the screens' emission reduction, which averages an emitter face's paints) draws
// them without the mesh and visibility tables the shading layers read.
#ifndef SDF_SHADE_PAINTS_HLSLI
#define SDF_SHADE_PAINTS_HLSLI
#include "sdf-glyph-ink.hlsli"
// Draws a material's paints (SdfPaint), each over the one before. A paint's shape, evaluated at the hit through the one
// shape dispatch (evaluateShape) and never through the field, gives the albedo the paint's colour inside the shape, or
// within its stroke of the shape's surface, the edge antialiased over `footprint`, the world size of the pixel at the
// hit, or feathered over the paint's softness where that is wider. A text paint (SdfPaint.Text) colours only where one of its slot's glyphs also covers the hit, the glyphs laid
// out in the shape's bound box on its local XY plane (sdfSurfaceTextInk); a kernel that binds no glyph atlas draws
// none of it. `worldPoint` is the hit in world space and `layerPoint` in the winning frame; a paint riding a slot reads that
// slot's frame. A hit outside the shape's bound box, widened by the stroke and the pixel, costs one rotation. It reads
// and writes the albedo alone (sdfMaterialPaints), so the caller loads the rest of the material after it: a material
// live across the shape dispatch held the views pass at 168 registers on Direct3D 12. KEEP IN SYNC with
// SdfAppearance.PackPaint.
void sdfApplyPaints(float3 worldPoint, float3 layerPoint, float footprint, uint paintFirst, uint paintCount, bool deferLast, inout float3 albedo, out float coverage) {
    coverage = 0.0;
    uint count = min(paintCount, SDF_MAX_PAINTS_PER_MATERIAL);
    if ((count == 0u) || !sdfProgramRange(paintFirst, count, SDF_PAINT_VECTORS_PER_ENTRY)) return;
    float edge = max((0.5 * footprint), 1.0e-6);

    [loop] for (uint i = 0u; i < count; i++) {
        uint base = (paintFirst + (i * SDF_PAINT_VECTORS_PER_ENTRY));
        float4 frame = asfloat(sdfWords[base]);
        float4 colorStroke = asfloat(sdfWords[base + 5u]);
        float4 slotCenter = asfloat(sdfWords[base + 6u]);
        uint slot = asuint(slotCenter.x);
        float3 p = layerPoint;

        if (slot != SDF_PAINT_WINNING_FRAME) {
            float4 slotRotation = sdfDynamicTransformRow((3u * slot) + 1u);
            if (dot(slotRotation, slotRotation) <= 0.0) continue;
            p = rotatePointByInverseQuaternion((worldPoint - sdfDynamicTransformRow(3u * slot).xyz), slotRotation);
        }

        float3 local = rotatePointByInverseQuaternion((p - frame.xyz), asfloat(sdfWords[base + 1u]));
        // A repeated paint folds the hit into its lattice cell (SdfPaintRepeat), so its shape is drawn about each cell's
        // centre, and moves its colour toward the tone by a hash of the cell, a variation that fades to its mean where a
        // cell grows under a few pixels.
        float4 cellSeed = asfloat(sdfWords[base + 9u]);
        // The tone and the edge softness (SdfPaint.Softness): the edge ramps over the wider of the pixel and the softness.
        float4 toneSoftness = asfloat(sdfWords[base + 10u]);
        float3 color = colorStroke.rgb;
        if (any(cellSeed.xyz > 0.0)) {
            float3 period = max(cellSeed.xyz, 1.0e-6);
            float3 cell = select((cellSeed.xyz > 0.0), round(local / period), float3(0.0, 0.0, 0.0));
            local -= (cell * cellSeed.xyz);
            int3 index = int3(cell);
            uint3 hash = sdfPcg3d(uint3(asuint(index.x), asuint(index.y), (asuint(index.z) ^ asuint(cellSeed.w))));
            float smallest = min(min(((cellSeed.x > 0.0) ? cellSeed.x : 3.4e38), ((cellSeed.y > 0.0) ? cellSeed.y : 3.4e38)), ((cellSeed.z > 0.0) ? cellSeed.z : 3.4e38));
            float variation = (((float)hash.x * SDF_INV_2POW32) - 0.5) * (1.0 - smoothstep(0.125, 0.5, (footprint / smallest)));
            color = lerp(colorStroke.rgb, toneSoftness.rgb, (0.5 + variation));
        }
        float feather = max(edge, (0.5 * toneSoftness.w));
        float4 bound = asfloat(sdfWords[base + 7u]);
        if (any(abs(local - slotCenter.yzw) > (bound.xyz + (colorStroke.w + footprint + feather)))) continue;
        float4 scale = asfloat(sdfWords[base + 2u]);
        float3 primitivePoint = rotatePointByInverseQuaternion(local / scale.xyz, asfloat(sdfWords[base + 8u]));
        float d = (evaluateShape(asuint(frame.w), primitivePoint, asfloat(sdfWords[base + 3u]), asfloat(sdfWords[base + 4u])) * scale.w);
        if (colorStroke.w > 0.0) d = (abs(d) - colorStroke.w);
        float reveal = 1.0 - smoothstep(-feather, feather, d);
        uint text = asuint(bound.w);
        if (text != 0u) {
#if defined(SDF_GLYPH_ATLAS)
            float4 ink = sdfSurfaceTextInk(text - 1u, ((local.xy - slotCenter.yz) / max(bound.xy, 1.0e-9)), bound.xy, footprint, color);
            reveal *= ink.a;
            color = ink.rgb;
#else
            reveal = 0.0;
#endif
        }
        if (deferLast && i + 1u == count) coverage = reveal;
        else albedo = lerp(albedo, color, reveal);
    }
}
#endif
