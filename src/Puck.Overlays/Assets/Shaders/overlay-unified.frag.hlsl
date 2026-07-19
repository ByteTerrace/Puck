// COST MODEL: the overlay is a single linear scan over declared rows — every pixel walks every submitted record
// with per-record early-outs. Re-measure through the live pass-timing instrument before growing content; the
// linear scan stays until it does.
//
// The unified overlay pass (single-source HLSL; DXC -> SPIR-V for Vulkan AND DXIL for Direct3D 12). ONE decorator
// draws every 2D surface from one packed storage buffer: N PANELS (token chrome — a scrim fill in a rounded rect, a
// 1px hairline outline, an optional title band + divider, an optional Tier-1 status ring + bloom halo) plus a flat
// list of ELEMENTS — rounded-rect cells, fixed-cell text runs into the ONE shared SDF glyph atlas, and ICON CHIPS
// (the binding-bar repertoire folded in as an element kind: rounded plate with the four chip-state tiers, a
// procedural action icon, and a gamepad badge — atlas letters or procedural symbols). SURFACES ARE WRITERS: the
// console panel, the per-seat binding bars, and the toast are all CPU writers into the same records — a future
// surface is a new writer, not a new shader.
//
// The storage buffer (uint4-strided; word offsets — see overlay-common.hlsli's buffer-shape note):
//   [0, tokenEnd)          the design-token slab (colors + geometry scalars; OverlayTokenBlock.cs)
//   [atlasBase, panelBase) the shared atlas' per-glyph SDF cells (one RGBA texel per word, uploaded once)
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
// misc:   textBase (word index), atlasBase (word index), clipBase (word index), reserved
struct OverlayPassData {
    float4 counts;
    float4 sdf;
    float4 misc;
};
[[vk::push_constant]] ConstantBuffer<OverlayPassData> pc;

// The atlas holds printable ASCII 0x20..0x7E; codes are stored as ALREADY-RESOLVED glyph indices.
#define GLYPH_COUNT 95

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

// ---- THE WORLD ICON GRAMMAR ---------------------------------------------------------------------------------------
// One geometric grammar for every procedural glyph, numeral, and icon — the artwork half of the token identity
// (docs/ui-design-tokens.md, "precision-tool minimalism"). Symbols draw in [-1, 1] glyph space, y down:
//   GRID   — symbol content lives in the +/-0.6 box; round forms may overshoot to +/-0.66 for optical balance.
//   STROKE — every line is the ONE hairline weight (the GlyphStroke token) with round caps: unions of capsule
//            segments and circular arcs, never a second weight.
//   ANGLES — verticals, horizontals, 45-degree diagonals, and circular arcs; only a numeral's skeleton may take
//            the diagonal its letterform demands.
//   FILL   — at most one small filled focal form per symbol (a dot, a lens, a half-disc); the strokes carry the
//            silhouette, the fill marks the point of action.
//   HUE    — tints come from the token block's semantic roles, never per-icon literals: navigation reads TextDim,
//            structure TextPrimary, commit/go Positive, appearance Warning, destructive/live Danger, and the
//            interact verb Accent (the one accent-hued icon, mirroring the accent budget).

// The token palette's rgb for an icon tint.
float3 tokenHue(uint role) {
    return OverlayTokenColor(overlayData, role).rgb;
}

// A directional chevron pointing up: two 45-degree capsule arms; the arrows rotate it by quarter turns.
float chevron(float2 p) {
    float d = distanceToSegment(p, float2(-0.45, 0.22), float2(0.0, -0.23));

    return min(d, distanceToSegment(p, float2(0.0, -0.23), float2(0.45, 0.22)));
}

// ---- gamepad glyph badges (ids: KEEP IN SYNC with OverlayGlyphId) -------------------------------------------------
// Text-labeled badges (LB/RB/LT/RT/LS/RS) render from the shared atlas via the packed label bits; the d-pad arrows
// and the neutral face-position glyphs stay procedural.

// The face-position glyph — the NEUTRAL, family-invariant face-button treatment: the four-position diamond drawn
// as abstract positions (a filled dot at the named position, hairline pips at the other three), no vendor's
// branding. Position index: 0 north, 1 east, 2 south, 3 west (compass order, matching the physical diamond).
float facePosition(uint position, float2 p) {
    float d = 1e3;

    [unroll]
    for (uint i = 0u; (i < 4u); i++) {
        // Compass point i on the badge diamond (N/E/S/W), radius 0.55.
        float2 c = ((i == 0u) ? float2(0.0, -0.55) : ((i == 1u) ? float2(0.55, 0.0) : ((i == 2u) ? float2(0.0, 0.55) : float2(-0.55, 0.0))));

        d = min(d, (length(p - c) - ((i == position) ? 0.30 : 0.06)));
    }

    return d;
}

float glyphDistance(uint glyphId, float2 p) {
    // Atlas ids (>= 1024) are the reserved texture path; until it exists they draw nothing.
    if (glyphId >= 1024u) {
        return 1e3;
    }

    switch (glyphId) {
        case 1u: return chevron(p);                                    // ArrowUp
        case 2u: return chevron(float2(p.y, -p.x));                    // ArrowRight
        case 3u: return chevron(-p);                                   // ArrowDown
        case 4u: return chevron(float2(-p.y, p.x));                    // ArrowLeft
        case 5u: return facePosition(0u, p);                           // FaceNorth
        case 6u: return facePosition(1u, p);                           // FaceEast
        case 7u: return facePosition(2u, p);                           // FaceSouth
        case 8u: return facePosition(3u, p);                           // FaceWest
        default: return 1e3;
    }
}

// ---- numerals (the generic action-slot icons) ---------------------------------------------------------------------
// Hairline drafting digits from the grammar's own vocabulary — capsule strokes and circular arcs on a shared
// 0.40 x 0.60 half-extent box — so a numeral and an icon read as one hand. Arcs compose as a ring cut by
// half-plane / quadrant terms (max), the same construction the icons use.

float digitDistance(uint digit, float2 p) {
    switch (min(digit, 9u)) {
        case 0u: return abs(sdRoundedBox(p, float2(0.38, 0.58), 0.38));
        case 1u: {
            float d = distanceToSegment(p, float2(-0.16, -0.34), float2(0.08, -0.58));

            return min(d, distanceToSegment(p, float2(0.08, -0.58), float2(0.08, 0.58)));
        }
        case 2u: {
            // The upper half-arc, the descending diagonal, the base.
            float d = max(abs(length(p - float2(0.0, -0.26)) - 0.32), (p.y - -0.26));

            d = min(d, distanceToSegment(p, float2(0.32, -0.26), float2(-0.32, 0.58)));

            return min(d, distanceToSegment(p, float2(-0.32, 0.58), float2(0.36, 0.58)));
        }
        case 3u: {
            // Flat-topped: the top bar, the diagonal into the waist, the lower bowl (open at its upper-left).
            float d = distanceToSegment(p, float2(-0.30, -0.58), float2(0.30, -0.58));

            d = min(d, distanceToSegment(p, float2(0.30, -0.58), float2(-0.02, -0.12)));

            return min(d, max(abs(length(p - float2(0.0, 0.22)) - 0.34), min((0.0 - p.x), (0.22 - p.y))));
        }
        case 4u: {
            float d = distanceToSegment(p, float2(0.12, -0.58), float2(-0.38, 0.22));

            d = min(d, distanceToSegment(p, float2(-0.38, 0.22), float2(0.38, 0.22)));

            return min(d, distanceToSegment(p, float2(0.12, -0.58), float2(0.12, 0.58)));
        }
        case 5u: {
            float d = distanceToSegment(p, float2(-0.28, -0.58), float2(0.32, -0.58));

            d = min(d, distanceToSegment(p, float2(-0.28, -0.58), float2(-0.28, -0.10)));
            d = min(d, distanceToSegment(p, float2(-0.28, -0.10), float2(-0.02, -0.16)));

            // The lower bowl: a ring open at its upper-left quadrant.
            return min(d, max(abs(length(p - float2(-0.02, 0.20)) - 0.36), min((-0.02 - p.x), (0.20 - p.y))));
        }
        case 6u: {
            float d = distanceToSegment(p, float2(0.24, -0.58), float2(-0.20, -0.02));

            return min(d, abs(length(p - float2(0.0, 0.24)) - 0.33));
        }
        case 7u: {
            float d = distanceToSegment(p, float2(-0.34, -0.58), float2(0.36, -0.58));

            return min(d, distanceToSegment(p, float2(0.36, -0.58), float2(-0.06, 0.58)));
        }
        case 8u: {
            float d = abs(length(p - float2(0.0, -0.28)) - 0.27);

            return min(d, abs(length(p - float2(0.0, 0.27)) - 0.31));
        }
        default: {
            // 9 — the point-mirror of 6.
            float d = distanceToSegment(p, float2(-0.24, 0.58), float2(0.20, 0.02));

            return min(d, abs(length(p - float2(0.0, -0.24)) - 0.33));
        }
    }
}

float numberDistance(uint number, float2 p) {
    // Single digits draw at native grammar size; two digits (the actions run 1-12) evaluate in a scaled space and
    // the distances scale back (x the divisor) so the stroke width the caller applies stays uniform.
    if (number < 10u) {
        return digitDistance(number, p);
    }

    float d = digitDistance((number / 10u), ((p - float2(-0.32, 0.0)) / 0.72));

    return (min(d, digitDistance((number % 10u), ((p - float2(0.32, 0.0)) / 0.72))) * 0.72);
}

// ---- action icons (ids: KEEP IN SYNC with OverlayIconId) ----------------------------------------------------------
// Every symbol follows the icon grammar above; an icon's identifying hue is part of its drawing, but it is fetched
// from the token block by SEMANTIC ROLE — the artwork draws from the one token palette, never its own literals.

// Returns rgb = the icon tint, a = the symbol coverage, for a point in [-1, 1] icon space.
float4 actionIcon(uint iconId, float2 p, float stroke, float aa) {
    if ((iconId == 0u) || (iconId >= 1024u)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    if (iconId == 1u) {                                                // Generic: the focal dot alone
        return float4(tokenHue(OVERLAY_ROLE_TEXT_DIM), strokeMask((length(p) - 0.16), 0.0, aa));
    }

    if (iconId == 2u) {                                                // Jump: a double up-chevron
        float d = min(chevron(p - float2(0.0, -0.20)), chevron(p - float2(0.0, 0.24)));

        return float4(tokenHue(OVERLAY_ROLE_POSITIVE), strokeMask(d, stroke, aa));
    }

    if (iconId == 3u) {                                                // Interact: the diamond, focal dot at center
        float2 q = (float2((p.x + p.y), (p.y - p.x)) * 0.7071);        // rotate 45 degrees: the box becomes a diamond

        float d = abs(sdRoundedBox(q, float2(0.42, 0.42), 0.08));

        d = min(d, (length(p) - 0.10));

        return float4(tokenHue(OVERLAY_ROLE_ACCENT), strokeMask(d, stroke, aa));
    }

    if (iconId == 4u) {                                                // Target: a reticle, focal dot at center
        float d = abs(length(p) - 0.42);

        d = min(d, distanceToSegment(p, float2(0.0, -0.72), float2(0.0, -0.52)));
        d = min(d, distanceToSegment(p, float2(0.0, 0.52), float2(0.0, 0.72)));
        d = min(d, distanceToSegment(p, float2(-0.72, 0.0), float2(-0.52, 0.0)));
        d = min(d, distanceToSegment(p, float2(0.52, 0.0), float2(0.72, 0.0)));
        d = min(d, (length(p) - 0.10));

        return float4(tokenHue(OVERLAY_ROLE_DANGER), strokeMask(d, stroke, aa));
    }

    if ((iconId >= 8u) && (iconId <= 19u)) {                           // Number1..Number12: drafting digits
        uint number = ((iconId - 8u) + 1u);

        return float4(tokenHue(OVERLAY_ROLE_TEXT_PRIMARY), strokeMask(numberDistance(number, p), stroke, aa));
    }

    // ---- editing verb icons (KEEP IN SYNC with OverlayIconId.Edit*) ----

    if ((iconId == 20u) || (iconId == 21u)) {                          // EditPrev / EditNext: a cycle arrow
        // One drawing, mirrored: a ring open at its upper quadrant on the pointing side, with a 45-degree
        // arrowhead at the side point. EditPrev points left; EditNext is its x-mirror.
        float2 q = ((iconId == 21u) ? float2(-p.x, p.y) : p);
        float d = max(abs(length(q) - 0.44), min(-q.x, -q.y));         // remove the upper-left quadrant

        d = min(d, distanceToSegment(q, float2(-0.44, 0.0), float2(-0.61, 0.17)));
        d = min(d, distanceToSegment(q, float2(-0.44, 0.0), float2(-0.27, 0.17)));

        return float4(tokenHue(OVERLAY_ROLE_TEXT_DIM), strokeMask(d, stroke, aa));
    }

    if (iconId == 22u) {                                               // EditPlace: a down-arrow onto a baseline
        float d = distanceToSegment(p, float2(0.0, -0.52), float2(0.0, 0.22));

        d = min(d, distanceToSegment(p, float2(-0.24, -0.02), float2(0.0, 0.22)));
        d = min(d, distanceToSegment(p, float2(0.24, -0.02), float2(0.0, 0.22)));
        d = min(d, distanceToSegment(p, float2(-0.44, 0.52), float2(0.44, 0.52)));

        return float4(tokenHue(OVERLAY_ROLE_POSITIVE), strokeMask(d, stroke, aa));
    }

    if (iconId == 23u) {                                               // EditDelete: an X
        float d = distanceToSegment(p, float2(-0.38, -0.38), float2(0.38, 0.38));

        d = min(d, distanceToSegment(p, float2(-0.38, 0.38), float2(0.38, -0.38)));

        return float4(tokenHue(OVERLAY_ROLE_DANGER), strokeMask(d, stroke, aa));
    }

    if (iconId == 24u) {                                               // EditExit: a leftward return arrow
        float d = distanceToSegment(p, float2(0.44, 0.0), float2(-0.36, 0.0));

        d = min(d, distanceToSegment(p, float2(-0.36, 0.0), float2(-0.12, -0.24)));
        d = min(d, distanceToSegment(p, float2(-0.36, 0.0), float2(-0.12, 0.24)));
        d = min(d, distanceToSegment(p, float2(0.44, 0.0), float2(0.44, -0.30)));  // the return riser

        return float4(tokenHue(OVERLAY_ROLE_WARNING), strokeMask(d, stroke, aa));
    }

    if (iconId == 25u) {                                               // EditDuplicate: two offset squares
        float d = abs(sdRoundedBox((p - float2(0.14, 0.14)), float2(0.30, 0.30), 0.06));

        d = min(d, abs(sdRoundedBox((p + float2(0.14, 0.14)), float2(0.30, 0.30), 0.06)));

        return float4(tokenHue(OVERLAY_ROLE_TEXT_PRIMARY), strokeMask(d, stroke, aa));
    }

    if (iconId == 26u) {                                               // EditLink: two interlocked rings
        float d = abs(length(p - float2(0.20, 0.0)) - 0.28);

        d = min(d, abs(length(p + float2(0.20, 0.0)) - 0.28));

        return float4(tokenHue(OVERLAY_ROLE_POSITIVE), strokeMask(d, stroke, aa));
    }

    if (iconId == 27u) {                                               // EditMaterial: a drop, focal dot inside
        // The round body (open at its top wedge), two strokes tapering to the tip, the focal dot.
        float d = max(abs(length(p - float2(0.0, 0.16)) - 0.34), (-0.05 - p.y));

        d = min(d, distanceToSegment(p, float2(-0.27, -0.05), float2(0.0, -0.52)));
        d = min(d, distanceToSegment(p, float2(0.27, -0.05), float2(0.0, -0.52)));
        d = min(d, (length(p - float2(0.10, 0.22)) - 0.06));

        return float4(tokenHue(OVERLAY_ROLE_WARNING), strokeMask(d, stroke, aa));
    }

    if (iconId == 28u) {                                               // EditOpCycle: a two-circle boolean venn
        float left = (length(p - float2(-0.16, 0.0)) - 0.32);
        float right = (length(p - float2(0.16, 0.0)) - 0.32);
        float d = min(abs(left), abs(right));
        // The overlap lens is the focal fill — the icon reads as an OPERATION, not just two rings.
        float lens = max(left, right);

        return float4(tokenHue(OVERLAY_ROLE_WARNING), max(strokeMask(d, stroke, aa), (0.55 * strokeMask(lens, 0.0, aa))));
    }

    if (iconId == 29u) {                                               // EditStyle: a half-filled circle
        float ring = abs(length(p) - 0.44);
        float fill = max((length(p) - 0.44), p.x);                     // the filled left half is the focal form

        return float4(tokenHue(OVERLAY_ROLE_WARNING), max(strokeMask(ring, stroke, aa), (0.7 * strokeMask(fill, 0.0, aa))));
    }

    if (iconId == 30u) {                                               // EditDeselect: a slashed circle
        float d = abs(length(p) - 0.44);

        d = min(d, distanceToSegment(p, float2(-0.31, 0.31), float2(0.31, -0.31)));

        return float4(tokenHue(OVERLAY_ROLE_TEXT_DIM), strokeMask(d, stroke, aa));
    }

    if (iconId == 31u) {                                               // EditRecord: the focal dot alone, live-red
        return float4(tokenHue(OVERLAY_ROLE_DANGER), strokeMask((length(p) - 0.26), 0.0, aa));
    }

    if (iconId == 32u) {                                               // EditPlay: a play triangle
        float d = distanceToSegment(p, float2(-0.30, -0.42), float2(-0.30, 0.42));

        d = min(d, distanceToSegment(p, float2(-0.30, 0.42), float2(0.48, 0.0)));
        d = min(d, distanceToSegment(p, float2(0.48, 0.0), float2(-0.30, -0.42)));

        return float4(tokenHue(OVERLAY_ROLE_POSITIVE), strokeMask(d, stroke, aa));
    }

    if (iconId == 35u) {                                               // AudioSpeaker: cabinet + driver dot + emission arc
        // The cabinet outline on the left, its filled driver dot (the one focal form — the point of action, where
        // sound is made), a hairline tweeter pip above it, and one emission arc opening right.
        float d = abs(sdRoundedBox((p + float2(0.24, 0.0)), float2(0.26, 0.46), 0.08));

        d = min(d, abs(length(p - float2(-0.24, -0.20)) - 0.09));      // the tweeter pip
        d = min(d, max(abs(length(p - float2(0.16, 0.0)) - 0.42), -(p.x - 0.16))); // the right-opening arc

        float dot_ = (length(p - float2(-0.24, 0.14)) - 0.11);         // the driver — the focal fill

        return float4(tokenHue(OVERLAY_ROLE_TEXT_PRIMARY), max(strokeMask(d, stroke, aa), strokeMask(dot_, 0.0, aa)));
    }

    if (iconId == 36u) {                                               // AudioBed: concentric presence rings
        // A region, not a position: two concentric hairline rings around the focal dot — presence radiating from
        // an extent center (the drawn twin of the bed's envelope-by-presence semantics).
        float d = abs(length(p) - 0.30);

        d = min(d, abs(length(p) - 0.56));

        return float4(tokenHue(OVERLAY_ROLE_TEXT_PRIMARY), max(strokeMask(d, stroke, aa), strokeMask((length(p) - 0.10), 0.0, aa)));
    }

    if ((iconId == 33u) || (iconId == 34u)) {                          // EditUndo / EditRedo: a hook arrow over its arc
        // One drawing, mirrored: the ring's TOP half (an arc from the left point over to the right point) with a
        // downward arrowhead at the left end — the classic "curl back" gesture. EditUndo hooks left; EditRedo is
        // its x-mirror. Distinct from the EditPrev/EditNext cycle rings (those open a quadrant and point sideways).
        float2 q = ((iconId == 34u) ? float2(-p.x, p.y) : p);
        float d = max(abs(length(q) - 0.42), q.y);                     // keep the arc's upper half only (y <= 0 up here)

        d = min(d, distanceToSegment(q, float2(-0.42, 0.10), float2(-0.62, -0.10)));
        d = min(d, distanceToSegment(q, float2(-0.42, 0.10), float2(-0.22, -0.10)));

        return float4(tokenHue(OVERLAY_ROLE_ACCENT), strokeMask(d, stroke, aa));
    }

    return float4(0.0, 0.0, 0.0, 0.0);
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
    //   icon:     0..1 plate center (normalized) · 2 plate half (px) · 3 badge half (px) · 5 glyph<<16|icon ·
    //             6 state bits · 7..8 badge offset (px)
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
            float2 sample = SampleGlyphCoverage(overlayData, atlasBase, (int)glyph, GLYPH_COUNT, cellLocal, cellSize, atlasCellW, atlasCellH, screenPxRange, pc.sdf.y);

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
            // procedural action icon, and a gamepad badge hugging its corner — atlas letters or procedural symbols.
            float plateHalf = OverlayFloat(overlayData, (o + 2u));
            float glyphHalf = OverlayFloat(overlayData, (o + 3u));
            uint ids = OverlayWord(overlayData, (o + 5u));
            uint glyphId = (ids >> 16u);
            uint iconId = (ids & 0xFFFFu);
            uint state = OverlayWord(overlayData, (o + 6u));
            float2 glyphOffset = float2(OverlayFloat(overlayData, (o + 7u)), OverlayFloat(overlayData, (o + 8u)));
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
            float glyphStroke = OverlayTokenScalar(overlayData, OVERLAY_SCALAR_GLYPH_STROKE);
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

            // The bound action's icon, centered on the plate.
            if (iconId != 0u) {
                float4 icon = actionIcon(iconId, (slotLocal / max((plateHalf * 0.62), 1e-5)), glyphStroke, glyphAa);

                color = lerp(color, icon.rgb, (icon.a * fill * alpha));
            }

            // The gamepad badge, hugging its corner: a dark backing disc, then a light glyph — EXCEPT on the ACCENT
            // tier, where the badge fills accent and the glyph inks accent.ink. A LETTER label (char0 != 0 in the
            // state high bits) renders from the shared SDF atlas; the iconographic glyphs stay procedural.
            uint char0 = ((state >> 9u) & 0x7Fu);
            float3 badgeBackingColor = (isAccentTier ? accentRgb : OverlayTokenColor(overlayData, OVERLAY_ROLE_BADGE_DARK).rgb);
            float3 badgeInkColor = (isAccentTier
                ? OverlayTokenColor(overlayData, OVERLAY_ROLE_ACCENT_INK).rgb
                : OverlayTokenColor(overlayData, OVERLAY_ROLE_BADGE_LIGHT).rgb);

            if ((glyphHalf > 0.0) && (char0 != 0u)) {
                float2 glyphLocal = ((fragCoord.xy - (slotCenter + glyphOffset)) / glyphHalf);

                if (max(abs(glyphLocal.x), abs(glyphLocal.y)) < 1.6) {
                    color = lerp(color, badgeBackingColor, ((1.0 - smoothstep(1.0, (1.0 + (glyphAa * 2.0)), length(glyphLocal))) * alpha * 0.85));

                    uint char1 = ((state >> 16u) & 0x7Fu);
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
                            overlayData, atlasBase, glyphIndex, GLYPH_COUNT,
                            float2(u, v), float2(1.0, 1.0), atlasCellW, atlasCellH, screenPxRange, 0.25);

                        color = lerp(color, (badgeInkColor * 0.3), (coverage.y * alpha * 0.85));
                        color = lerp(color, badgeInkColor, (coverage.x * alpha));
                    }
                }
            } else if ((glyphId != 0u) && (glyphHalf > 0.0)) {
                float2 glyphLocal = ((fragCoord.xy - (slotCenter + glyphOffset)) / glyphHalf);

                if (max(abs(glyphLocal.x), abs(glyphLocal.y)) < 1.6) {
                    float backing = (1.0 - smoothstep(1.0, (1.0 + (glyphAa * 2.0)), length(glyphLocal)));
                    float glyph = strokeMask(glyphDistance(glyphId, glyphLocal), glyphStroke, glyphAa);

                    color = lerp(color, badgeBackingColor, (backing * alpha * 0.85));
                    color = lerp(color, badgeInkColor, (glyph * alpha));
                }
            }
        }
    }

    return float4(color, 1.0);
}
