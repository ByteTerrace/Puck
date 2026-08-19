// COST MODEL: the overlay is a single linear scan over declared rows — every pixel walks every submitted record
// with per-record early-outs. Re-measure through the live pass-timing instrument before growing content; the
// linear scan stays until it does.
//
// The unified overlay pass (single-source HLSL; DXC -> SPIR-V for Vulkan AND DXIL for Direct3D 12). ONE decorator
// draws every 2D surface from one packed storage buffer: N PANELS (token chrome — a scrim fill in a rounded rect, a
// 1px hairline outline, an optional title band + divider, an optional Tier-1 status ring + bloom halo) plus a flat
// list of ELEMENTS — rounded-rect cells, fixed-cell text runs into the ONE shared SDF glyph atlas, and ICON CHIPS
// (the binding-bar repertoire folded in as an element kind: rounded plate with the four chip-state tiers, a bound
// action's plate icon, and a physical-button badge — every glyph an authored atlas entry, none of it drawn
// procedurally; see Puck.World.WorldIconTable for how a name becomes an atlas index). SURFACES ARE WRITERS: the
// console panel, the per-seat binding bars, and the toast are all CPU writers into the same records — a future
// surface is a new writer, not a new shader.
//
// The storage buffer (uint4-strided; word offsets — see overlay-common.hlsli's buffer-shape note):
//   [0, tokenEnd)          the design-token slab (colors + geometry scalars; OverlayTokenBlock.cs)
//   [atlasBase, panelBase) the shared atlas' per-glyph SDF cells (one RGBA texel per word, uploaded once) — indices
//                          0..94 the printable-ASCII block, 95.. this boot's appended icon glyphs, total count
//                          carried in the push constants (glyphCount below), never a compile-time constant
//   [panelBase, elementBase) the panel records · [elementBase, textBase) the element records ·
//   [textBase, clipBase)   the glyph-code words the text runs index (one pre-resolved index per word) ·
//   [clipBase, ...)        the clip table (normalized x, y, w, h per rect; record word 9 indexes it, 0 = unclipped).
// Panel/element positions are NORMALIZED [0,1] screen space; each record's clip index CONFINES its pixels to a
// seat's viewport rect (placement inside a viewport is also clipping to it — the split-screen invariant); scalar
// widths (radii, plate halves, badge offsets) are PIXELS. KEEP IN SYNC with
// Puck.Overlays.OverlayFrameBuilder (record word layouts) and UnifiedOverlayNode (push constants).
//
// On Vulkan the texture+sampler fuse into one combined image sampler at set 0 binding 0 and the buffer is the
// storage buffer at binding 1; on Direct3D 12 they are t0/s0 (static sampler) and the storage SRV packs in at t1.
#include "overlay-common.hlsli"

[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

[[vk::binding(1, 0)]] StructuredBuffer<uint4> overlayData : register(t1);

// counts: panelCount, elementCount, atlasCellW, atlasCellH (texels)
// sdf:    distanceRange (texels), outlineBand (encoded units), panelBase (word index), elementBase (word index)
// misc:   textBase (word index), atlasBase (word index), clipBase (word index), glyphCount (this boot's atlas total)
struct OverlayPassData {
    float4 counts;
    float4 sdf;
    float4 misc;
};
[[vk::push_constant]] ConstantBuffer<OverlayPassData> pc;

// Words per record. KEEP IN SYNC with OverlayFrameBuilder.PanelWords / ElementWords.
#define PANEL_WORDS 12u
#define ELEMENT_WORDS 12u

// Icon-chip state bits — KEEP IN SYNC with OverlayFrameBuilder.WriteIcon.
#define ICON_STATE_ACCENT_BIT 23u
#define ICON_STATE_BOUND_BIT 24u

// ---- distance primitives -----------------------------------------------------------------------------------------

float distanceToSegment(float2 p, float2 a, float2 b) {
    float2 ab = (b - a);
    float t = saturate(dot((p - a), ab) / max(dot(ab, ab), 1e-6));

    return length(p - (a + (t * ab)));
}

float sdRoundedBox(float2 p, float2 halfSize, float radius) {
    float2 q = ((abs(p) - halfSize) + radius);

    return ((length(max(q, 0.0)) + min(max(q.x, q.y), 0.0)) - radius);
}

// Coverage of a stroked distance: 1 inside the stroke, 0 outside, an aa-wide ramp between.
float strokeMask(float distance, float width, float aa) {
    return (1.0 - smoothstep(width, (width + aa), distance));
}

// Whether the record's clip rect (word 9's index into the clip table; 0 = unclipped) rejects this pixel — the
// per-seat viewport confinement contract (see OverlayFrameBuilder.BeginClip).
bool clipRejects(uint clipIndex, float2 fragXy, uint clipBase, float2 dims) {
    if (clipIndex == 0u) {
        return false;
    }

    uint o = (clipBase + ((clipIndex - 1u) * 4u));
    float2 clipXy = (float2(OverlayFloat(overlayData, o), OverlayFloat(overlayData, (o + 1u))) * dims);
    float2 clipWh = (float2(OverlayFloat(overlayData, (o + 2u)), OverlayFloat(overlayData, (o + 3u))) * dims);

    return ((fragXy.x < clipXy.x) || (fragXy.y < clipXy.y) || (fragXy.x >= (clipXy.x + clipWh.x)) || (fragXy.y >= (clipXy.y + clipWh.y)));
}

// ---- the pass -----------------------------------------------------------------------------------------------------

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 dims = float2(width, height);
    float2 uv = (fragCoord.xy / dims);
    float3 color = sourceTexture.Sample(sourceSampler, uv).rgb;

    uint panelBase = (uint)pc.sdf.z;
    uint elementBase = (uint)pc.sdf.w;
    uint textBase = (uint)pc.misc.x;
    uint atlasBase = (uint)pc.misc.y;
    uint clipBase = (uint)pc.misc.z;
    int glyphCount = (int)pc.misc.w;
    int panelCount = (int)pc.counts.x;
    int elementCount = (int)pc.counts.y;
    int atlasCellW = (int)pc.counts.z;
    int atlasCellH = (int)pc.counts.w;

    float edgeAa = OverlayTokenScalar(overlayData, OVERLAY_SCALAR_EDGE_AA);
    float haloBlur = OverlayTokenScalar(overlayData, OVERLAY_SCALAR_BLOOM_HALO_BLUR);
    float bloomRingA = OverlayTokenScalar(overlayData, OVERLAY_SCALAR_BLOOM_RING_A);
    float bloomHaloA = OverlayTokenScalar(overlayData, OVERLAY_SCALAR_BLOOM_HALO_A);
    float4 lineHair = OverlayTokenColor(overlayData, OVERLAY_ROLE_LINE_HAIR);
    float4 lineSoft = OverlayTokenColor(overlayData, OVERLAY_ROLE_LINE_SOFT);

    // ---- panel chrome (fill + hairline + optional band divider + optional Tier-1 ring/halo) ----------------------
    // Panel word layout (12 words) — KEEP IN SYNC with OverlayFrameBuilder.WritePanel:
    //   0..3  rect x, y, w, h (float, NORMALIZED, top-left origin)
    //   4     flags (uint: bit0 = title band present)
    //   5     styleKind (uint: 0 = panel scrim + r.3, 1 = strip scrim + r.2, 2 = chip scrim + r.2)
    //   6     ring role (uint: 0 = none, else a token color-role index — the Tier-1 bloom ring + halo hue)
    //   7     title band height (float, normalized y)
    //   8     panel alpha (float — the toast's content-tick fade scales its whole chrome)
    //   9..11 reserved
    for (int p = 0; (p < panelCount); p++) {
        uint o = (panelBase + ((uint)p * PANEL_WORDS));
        float4 rect = (float4(OverlayFloat(overlayData, o), OverlayFloat(overlayData, (o + 1u)), OverlayFloat(overlayData, (o + 2u)), OverlayFloat(overlayData, (o + 3u))) * dims.xyxy);
        uint flags = OverlayWord(overlayData, (o + 4u));
        uint styleKind = OverlayWord(overlayData, (o + 5u));
        uint ringRole = OverlayWord(overlayData, (o + 6u));
        float bandHeight = (OverlayFloat(overlayData, (o + 7u)) * dims.y);
        float panelAlpha = OverlayFloat(overlayData, (o + 8u));

        if (clipRejects(OverlayWord(overlayData, (o + 9u)), fragCoord.xy, clipBase, dims)) {
            continue;
        }

        float2 local = (fragCoord.xy - rect.xy);
        // The Tier-1 halo legitimately bleeds past the rect; use its reach as the slack when a ring is lit.
        float slack = ((ringRole != 0u) ? haloBlur : edgeAa);

        if ((local.x < -slack) || (local.y < -slack) || (local.x >= (rect.z + slack)) || (local.y >= (rect.w + slack))) {
            continue;
        }

        float radius = OverlayTokenScalar(overlayData, ((styleKind == 0u) ? OVERLAY_SCALAR_RADIUS_3 : OVERLAY_SCALAR_RADIUS_2));
        float4 scrim = OverlayTokenColor(overlayData, ((styleKind == 0u)
            ? OVERLAY_ROLE_SCRIM_PANEL
            : ((styleKind == 1u) ? OVERLAY_ROLE_SCRIM_STRIP : OVERLAY_ROLE_SCRIM_CHIP)));
        float2 halfSize = (rect.zw * 0.5);
        float panelDist = sdRoundedBox((local - halfSize), halfSize, radius);
        float panelMask = (1.0 - smoothstep(0.0, edgeAa, panelDist));

        color = lerp(color, scrim.rgb, (panelMask * scrim.a * panelAlpha));
        color = lerp(color, lineHair.rgb, (strokeMask(abs(panelDist), 0.5, edgeAa) * lineHair.a * panelAlpha));

        // The title band's divider (a 1px line.soft rule below the band).
        if ((flags & 1u) != 0u) {
            float dividerDist = abs(local.y - bandHeight);

            if (dividerDist < (edgeAa + 0.5)) {
                color = lerp(color, lineSoft.rgb, (strokeMask(dividerDist, 0.5, edgeAa) * lineSoft.a * panelMask * panelAlpha));
            }
        }

        // Tier-1 (a transient echo — the toast): a 1px lit ring straddling the boundary + an outward
        // distance-falloff halo in the SAME semantic hue — the one-geometry/hue-varies bloom recipe.
        if (ringRole != 0u) {
            float3 hue = OverlayTokenColor(overlayData, ringRole).rgb;
            float ring = strokeMask(abs(panelDist), 0.5, edgeAa);
            float halo = saturate(1.0 - (max(panelDist, 0.0) / haloBlur));

            color = lerp(color, hue, (max((ring * bloomRingA), (halo * halo * bloomHaloA * (panelDist > 0.0 ? 1.0 : 0.0))) * panelAlpha));
        }
    }

    // ---- elements (rects, text runs, icon chips, rings, in submission order) -------------------------------------
    // Element word layout (12 words) — KEEP IN SYNC with OverlayFrameBuilder.WriteRect/WriteText/WriteIcon/WriteRing:
    //   4         kind (uint low nibble: 0 = text, 1 = rect, 2 = icon, 3 = ring) | colorRole << 4
    //   text:     0..1 origin (normalized) · 2..3 one glyph cell's on-screen w/h (normalized) · 5 glyph start ·
    //             6 glyph count · 7 alpha
    //   rect:     0..3 rect (normalized) · 6 corner radius (px) · 7 alpha
    //   icon:     0..1 plate center (normalized) · 2 plate half (px) · 3 badge half (px) · 5 iconGlyph0 ·
    //             6 state bits · 7..8 badge offset (px) · 10 iconGlyph1
    //   ring:     0..1 center (normalized) · 2 radius (px) · 7 alpha — a stroked hairline circle (the gizmo
    //             radius indicator), the ONE hairline weight like every grammar stroke
    for (int e = 0; (e < elementCount); e++) {
        uint o = (elementBase + ((uint)e * ELEMENT_WORDS));

        if (clipRejects(OverlayWord(overlayData, (o + 9u)), fragCoord.xy, clipBase, dims)) {
            continue;
        }

        uint packed = OverlayWord(overlayData, (o + 4u));
        uint kind = (packed & 0xFu);
        uint role = ((packed >> 4u) & 0xFFu);
        float2 origin = (float2(OverlayFloat(overlayData, o), OverlayFloat(overlayData, (o + 1u))) * dims);
        float2 ab = float2(OverlayFloat(overlayData, (o + 2u)), OverlayFloat(overlayData, (o + 3u)));
        float2 local = (fragCoord.xy - origin);

        if (kind == 1u) {
            // A rounded-rect cell (chip fill, selection fill, accent tick, state rail).
            float2 size = (ab * dims);

            if ((local.x < -edgeAa) || (local.y < -edgeAa) || (local.x >= (size.x + edgeAa)) || (local.y >= (size.y + edgeAa))) {
                continue;
            }

            float radius = OverlayFloat(overlayData, (o + 6u));
            float alpha = OverlayFloat(overlayData, (o + 7u));
            float2 halfSize = (size * 0.5);
            float dist = sdRoundedBox((local - halfSize), halfSize, radius);
            float mask = (1.0 - smoothstep(0.0, edgeAa, dist));
            float4 fill = OverlayTokenColor(overlayData, role);

            color = lerp(color, fill.rgb, (mask * fill.a * alpha));
        } else if (kind == 0u) {
            // A text run: a row of monospace glyph cells; codes are pre-resolved atlas indices.
            float2 cellSize = (ab * dims);
            uint count = OverlayWord(overlayData, (o + 6u));
            float alpha = OverlayFloat(overlayData, (o + 7u));
            float runWidth = (cellSize.x * (float)count);

            if ((local.x < 0.0) || (local.y < 0.0) || (local.x >= runWidth) || (local.y >= cellSize.y)) {
                continue;
            }

            int column = (int)floor(local.x / cellSize.x);
            uint glyph = OverlayWord(overlayData, (textBase + OverlayWord(overlayData, (o + 5u)) + (uint)column));
            float2 cellLocal = float2((local.x - (column * cellSize.x)), local.y);
            // screenPxRange = distanceRange(texels) x screen-px-per-texel (the on-screen cell maps the atlas cell).
            float screenPxRange = (pc.sdf.x * (cellSize.y / pc.counts.w));
            float2 sample = SampleGlyphCoverage(overlayData, atlasBase, (int)glyph, glyphCount, cellLocal, cellSize, atlasCellW, atlasCellH, screenPxRange, pc.sdf.y);

            color = lerp(color, float3(0.0, 0.01, 0.015), (sample.y * 0.85 * alpha));
            color = lerp(color, OverlayTokenColor(overlayData, role).rgb, (sample.x * alpha));
        } else if (kind == 3u) {
            // A RING: one hairline stroked circle — origin is the center, ab.x the radius in px.
            float radius = OverlayFloat(overlayData, (o + 2u));
            float dist = abs(length(local) - radius);

            if (dist > (edgeAa + 1.0)) {
                continue;
            }

            float alpha = OverlayFloat(overlayData, (o + 7u));
            float4 strokeColor = OverlayTokenColor(overlayData, role);

            color = lerp(color, strokeColor.rgb, (strokeMask(dist, 0.5, edgeAa) * strokeColor.a * alpha));
        } else {
            // An ICON CHIP: rounded plate with the four chip-state tiers (REST / HELD / ACCENT / DISABLED), a
            // bound action's plate icon, and a physical-button badge hugging its corner — both drawn from the SAME
            // shared atlas, up to two stacked glyphs each (a two-character label, or a single pictogram glyph).
            float plateHalf = OverlayFloat(overlayData, (o + 2u));
            float glyphHalf = OverlayFloat(overlayData, (o + 3u));
            uint iconGlyph0 = OverlayWord(overlayData, (o + 5u));
            uint state = OverlayWord(overlayData, (o + 6u));
            float2 glyphOffset = float2(OverlayFloat(overlayData, (o + 7u)), OverlayFloat(overlayData, (o + 8u)));
            uint iconGlyph1 = OverlayWord(overlayData, (o + 10u));
            float alpha = (float(state & 0xFFu) / 255.0);
            bool pressed = ((state & 0x100u) != 0u);
            bool accent = ((state & (1u << ICON_STATE_ACCENT_BIT)) != 0u);
            bool bound = ((state & (1u << ICON_STATE_BOUND_BIT)) != 0u);
            // The four chip states (the token spec's Tier recipes). HELD wins over ACCENT (pressing the
            // context-primary chip still needs press feedback); DISABLED only shows when nothing else lights it.
            bool isHeld = pressed;
            bool isAccentTier = (accent && !pressed);
            bool isDisabled = (!bound && !pressed && !accent);
            // The whole chip (plate + icon + badge) rides press.held's 1px translateY while held.
            float2 slotCenter = (origin + float2(0.0, (isHeld ? 1.0 : 0.0)));
            float2 slotLocal = (fragCoord.xy - slotCenter);

            // Early out: the glyph badge can hang past the plate corner, so the bound is generous.
            if (max(abs(slotLocal.x), abs(slotLocal.y)) > (plateHalf * 2.2)) {
                continue;
            }

            // Every px token scales by the chip's own size relative to the reference chip, so the recipes hold as
            // chips shrink/grow through the split-screen ladder.
            float chipScale = (plateHalf / OverlayTokenScalar(overlayData, OVERLAY_SCALAR_REFERENCE_CHIP_HALF));
            float aa = max((OverlayTokenScalar(overlayData, OVERLAY_SCALAR_EDGE_HAIRLINE) * chipScale), 0.75);
            float outlineWidth = aa;
            float haloBlurPx = (haloBlur * chipScale);
            float cornerRadius = (OverlayTokenScalar(overlayData, OVERLAY_SCALAR_RADIUS_1) * chipScale);
            float glyphAa = OverlayTokenScalar(overlayData, OVERLAY_SCALAR_GLYPH_AA);
            float plateDistance = sdRoundedBox(slotLocal, float2((plateHalf * 0.92), (plateHalf * 0.92)), cornerRadius);
            float fill = (1.0 - smoothstep(0.0, aa, plateDistance));
            float outline = strokeMask(abs(plateDistance), outlineWidth, aa);

            // Tier 0 REST: surface.raised + line.hair (the rest-opacity token tunes its translucency).
            // Tier 0 DISABLED: transparent fill + line.soft (a free/unbound button, still shown so its socket reads).
            // Tier 1 HELD: surface.base, fully seated, + bloom.neutral. Tier 1 ACCENT: accent.quiet + bloom.accent.
            // Tier-1 chips skip the plain hairline — the bloom ring below IS their edge.
            float3 accentRgb = OverlayTokenColor(overlayData, OVERLAY_ROLE_ACCENT).rgb;
            float3 fillColor = (isHeld
                ? OverlayTokenColor(overlayData, OVERLAY_ROLE_SURFACE_BASE).rgb
                : (isAccentTier ? accentRgb : OverlayTokenColor(overlayData, OVERLAY_ROLE_SURFACE_RAISED).rgb));
            float plateOpacity = (isDisabled
                ? 0.0
                : (isHeld
                    ? 1.0
                    : (isAccentTier
                        ? OverlayTokenColor(overlayData, OVERLAY_ROLE_ACCENT_QUIET).a
                        : OverlayTokenScalar(overlayData, OVERLAY_SCALAR_CHIP_REST_OPACITY))));

            color = lerp(color, fillColor, (fill * alpha * plateOpacity));

            if (isHeld || isAccentTier) {
                // Tier-1 bloom: an SDF distance-falloff halo OUTSIDE the plate plus a brighter 1px ring AT the
                // edge, in the element's own semantic hue — an extra SDF pass, never a blur.
                float3 hue = (isAccentTier ? accentRgb : OverlayTokenColor(overlayData, OVERLAY_ROLE_TEXT_PRIMARY).rgb);
                float ringA = OverlayTokenScalar(overlayData, (isAccentTier ? OVERLAY_SCALAR_BLOOM_RING_A : OVERLAY_SCALAR_BLOOM_NEUTRAL_RING_A));
                float haloA = OverlayTokenScalar(overlayData, (isAccentTier ? OVERLAY_SCALAR_BLOOM_HALO_A : OVERLAY_SCALAR_BLOOM_NEUTRAL_HALO_A));
                float haloMask = (saturate(1.0 - (max(plateDistance, 0.0) / max(haloBlurPx, 1e-4))) * step(0.0, plateDistance));

                color = lerp(color, hue, (haloMask * haloA * alpha));
                color = lerp(color, hue, (outline * ringA * alpha));
            } else {
                float4 outlineColor = (isDisabled ? lineSoft : lineHair);

                color = lerp(color, outlineColor.rgb, (outline * alpha * outlineColor.a));
            }

            // The bound action's icon, centered on the plate — up to two stacked atlas glyphs (a two-character
            // placeholder like a double-digit number), the SAME reconstruction the badge below uses, just larger
            // and centered rather than corner-hugging.
            if (iconGlyph0 != 0u) {
                float iconHalf = (plateHalf * 0.62);
                float2 iconLocal = (slotLocal / max(iconHalf, 1e-5));

                if (max(abs(iconLocal.x), abs(iconLocal.y)) < 1.6) {
                    int iconLen = ((iconGlyph1 != 0u) ? 2 : 1);
                    // The icon is centered in the plate; each glyph cell preserves the atlas aspect at a fixed height.
                    float iconHalfH = 0.82;
                    float iconCellW = ((2.0 * iconHalfH) * (float(atlasCellW) / float(atlasCellH)));
                    float iconTotalW = (iconCellW * float(iconLen));
                    float lx = (iconLocal.x + (iconTotalW * 0.5));
                    int ci = (int)floor(lx / iconCellW);

                    if ((ci >= 0) && (ci < iconLen) && (abs(iconLocal.y) <= iconHalfH)) {
                        int glyphIndex = ((int)((ci == 0) ? iconGlyph0 : iconGlyph1) - 1);
                        float u = ((lx - (float(ci) * iconCellW)) / iconCellW);
                        float v = ((iconLocal.y + iconHalfH) / (2.0 * iconHalfH));
                        // screenPxRange from the on-screen glyph height (2*iconHalfH glyph-local units x iconHalf px).
                        float glyphPxH = ((2.0 * iconHalfH) * iconHalf);
                        float screenPxRange = max((pc.sdf.x * (glyphPxH / float(atlasCellH))), 1.0);
                        float2 coverage = SampleGlyphCoverage(
                            overlayData, atlasBase, glyphIndex, glyphCount,
                            float2(u, v), float2(1.0, 1.0), atlasCellW, atlasCellH, screenPxRange, 0.25);
                        float3 iconHue = OverlayTokenColor(overlayData, OVERLAY_ROLE_TEXT_PRIMARY).rgb;

                        color = lerp(color, (iconHue * 0.3), (coverage.y * fill * alpha * 0.85));
                        color = lerp(color, iconHue, (coverage.x * fill * alpha));
                    }
                }
            }

            // The physical-button badge, hugging its corner: a dark backing disc, then a light glyph — EXCEPT on
            // the ACCENT tier, where the badge fills accent and the glyph inks accent.ink. Up to two stacked atlas
            // glyphs (a two-character label like "LB", or a single pictogram glyph like a d-pad arrow).
            uint char0 = ((state >> 9u) & 0x7Fu);
            uint char1 = ((state >> 16u) & 0x7Fu);
            float3 badgeBackingColor = (isAccentTier ? accentRgb : OverlayTokenColor(overlayData, OVERLAY_ROLE_BADGE_DARK).rgb);
            float3 badgeInkColor = (isAccentTier
                ? OverlayTokenColor(overlayData, OVERLAY_ROLE_ACCENT_INK).rgb
                : OverlayTokenColor(overlayData, OVERLAY_ROLE_BADGE_LIGHT).rgb);

            if ((glyphHalf > 0.0) && (char0 != 0u)) {
                float2 glyphLocal = ((fragCoord.xy - (slotCenter + glyphOffset)) / glyphHalf);

                if (max(abs(glyphLocal.x), abs(glyphLocal.y)) < 1.6) {
                    color = lerp(color, badgeBackingColor, ((1.0 - smoothstep(1.0, (1.0 + (glyphAa * 2.0)), length(glyphLocal))) * alpha * 0.85));

                    int labelLen = ((char1 != 0u) ? 2 : 1);
                    // The label is centered in the badge; each char cell preserves the atlas aspect at a fixed height.
                    float labelHalfH = 0.82;
                    float charCellW = ((2.0 * labelHalfH) * (float(atlasCellW) / float(atlasCellH)));
                    float totalW = (charCellW * float(labelLen));
                    float lx = (glyphLocal.x + (totalW * 0.5));
                    int ci = (int)floor(lx / charCellW);

                    if ((ci >= 0) && (ci < labelLen) && (abs(glyphLocal.y) <= labelHalfH)) {
                        int glyphIndex = ((int)((ci == 0) ? char0 : char1) - 1);
                        float u = ((lx - (float(ci) * charCellW)) / charCellW);       // [0, 1]
                        float v = ((glyphLocal.y + labelHalfH) / (2.0 * labelHalfH)); // [0, 1], top-down
                        // screenPxRange from the on-screen char height (2*labelHalfH glyph-local units x glyphHalf px).
                        float charPxH = ((2.0 * labelHalfH) * glyphHalf);
                        float screenPxRange = max((pc.sdf.x * (charPxH / float(atlasCellH))), 1.0);
                        float2 coverage = SampleGlyphCoverage(
                            overlayData, atlasBase, glyphIndex, glyphCount,
                            float2(u, v), float2(1.0, 1.0), atlasCellW, atlasCellH, screenPxRange, 0.25);

                        color = lerp(color, (badgeInkColor * 0.3), (coverage.y * alpha * 0.85));
                        color = lerp(color, badgeInkColor, (coverage.x * alpha));
                    }
                }
            }
        }
    }

    return float4(color, 1.0);
}
