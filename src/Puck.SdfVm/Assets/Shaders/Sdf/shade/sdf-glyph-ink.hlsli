// Glyph ink at a shaded hit: the coverage of the glyph atlas's field under the pixel, which both shade-time text tiers
// read. A screen's glyph decal (sdfSampleGlyphDecal, shade/sdf-environment.hlsli) is a display's own picture; a
// surface text (sdfSurfaceTextInk) is lettering a paint draws on its material's surface, lit as the surface is.
// Neither is marched: geometry text is the Glyph shape (field/sdf-shapes.hlsli). The including kernel supplies the
// atlas and the glyph decode (field/sdf-shapes.hlsli) before this file.
#ifndef SHADE_SDF_GLYPH_INK_HLSLI
#define SHADE_SDF_GLYPH_INK_HLSLI
#if defined(SDF_GLYPH_ATLAS)
// Minimum antialiasing half-width in encoded-coverage units, so a glyph edge sampled one to one never collapses to a
// hard one-bit step.
static const float SdfGlyphInkMinAa = 0.03125;
// An rgba8 word, red in the low byte, as the hosts pack an ink.
float4 sdfGlyphInkUnpackRgba(uint packed) {
    return (float4(float(packed & 0xFFu), float((packed >> 8u) & 0xFFu), float((packed >> 16u) & 0xFFu), float(packed >> 24u)) * (1.0 / 255.0));
}
// The coverage of the atlas's field at `atlasUv`, by the median of its three channels: a shade-time threshold, where a
// replicated single-channel atlas medians to its alpha and a multi-channel one to sharp corners. The edge ramps over the
// pixel: `footprintDiameter` is the hit pixel's world diameter and `texelsPerWorld` the atlas texels one world unit of
// the glyph spans, so a far or grazing hit ramps softer. `distanceRange` is the atlas's distance range in texels; zero
// (a raw coverage atlas) takes one texel as the whole ramp.
float sdfGlyphInkCoverage(float2 atlasUv, float texelsPerWorld, float footprintDiameter, float distanceRange) {
    float encoded = sdfGlyphSampleFieldMedian(atlasUv);
    float encodedPerTexel = ((distanceRange > 0.0) ? (1.0 / distanceRange) : 1.0);
    float aaHalf = clamp((0.5 * (footprintDiameter * texelsPerWorld) * encodedPerTexel), SdfGlyphInkMinAa, 0.5);

    return smoothstep((0.5 - aaHalf), (0.5 + aaHalf), encoded);
}
// The ink of surface text slot `slot` at a point of its paint's bound box: `boxPoint` runs from -1 at the box's low
// face to 1 at its high face on each axis, y up, and `boxHalf` is the box's half-extents in world units. Returns the
// ink's colour, the paint's own unless the glyph carries a tint, and its coverage in alpha: zero off every glyph and
// for a slot the host supplied no glyphs for. Where glyph quads overlap the heaviest coverage wins. KEEP IN SYNC with
// SdfSurfaceTextLayout and SdfWorldTables.SetSurfaceText: the slot's descriptor follows the screens' in sdfDecalCells,
// (glyphCount, firstEntry, asuint(distanceRange), 0), and each glyph is two entries from firstEntry: the quad's
// rectangle as four floats (minimum x and y, maximum x and y), then (packedUvTopLeft, packedUvBottomRight, ink rgba8
// with coverage in alpha, flags).
float4 sdfSurfaceTextInk(uint slot, float2 boxPoint, float2 boxHalf, float footprintDiameter, float3 paintColor) {
    float4 ink = float4(paintColor, 0.0);

    if (slot >= SDF_MAX_SURFACE_TEXTS) {
        return ink;
    }

    uint4 descriptor = sdfDecalCells[SDF_MAX_SCREEN_SURFACES + slot];
    uint count = min(descriptor.x, SDF_MAX_SURFACE_TEXT_GLYPHS);
    uint2 atlasSize;

    sdfGlyphAtlas.GetDimensions(atlasSize.x, atlasSize.y);

    [loop]
    for (uint glyph = 0u; glyph < count; glyph++) {
        uint entry = (descriptor.y + (2u * glyph));
        float4 quad = asfloat(sdfDecalCells[entry]);

        if (any(boxPoint < quad.xy) || any(boxPoint > quad.zw)) {
            continue;
        }

        uint4 cell = sdfDecalCells[entry + 1u];
        float2 size = max((quad.zw - quad.xy), 1.0e-9);
        float2 along = ((boxPoint - quad.xy) / size);
        float2 uvTopLeft = sdfGlyphUnpackUv(asfloat(cell.x));
        float2 uvBottomRight = sdfGlyphUnpackUv(asfloat(cell.y));
        // The quad is y up and the atlas rows run down, so the quad's top edge reads the cell's top row.
        float2 atlasUv = float2(lerp(uvTopLeft.x, uvBottomRight.x, along.x), lerp(uvBottomRight.y, uvTopLeft.y, along.y));
        float texelsPerWorld = (((uvBottomRight.x - uvTopLeft.x) * float(atlasSize.x)) / max((size.x * boxHalf.x), 1.0e-9));
        float4 packed = sdfGlyphInkUnpackRgba(cell.z);
        float coverage = (sdfGlyphInkCoverage(atlasUv, texelsPerWorld, footprintDiameter, asfloat(descriptor.z)) * packed.a);

        if (coverage > ink.a) {
            ink = float4((((cell.w & SDF_SURFACE_TEXT_TINT) != 0u) ? packed.rgb : paintColor), coverage);
        }
    }

    return ink;
}
#endif
#endif
