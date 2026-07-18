// Demo binding-bar overlay (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for DirectX):
// sample the inner producer's render and draw the controller action-bar cluster over it — per visible slot a
// rounded plate, a procedural SDF action icon, and a gamepad-glyph badge hugging the slot corner; plus the
// modifier pips between the clusters. Per-slot data rides a storage buffer (12-60 slots exceed any push-constant
// budget); push constants carry only the scalar style knobs. This lives in Puck.Demo so no UI concept leaks into
// the reusable SDF engine.
//
// The button-face LETTER badges (A/B/X/Y and the LB/RB/LT/RT/LS/RS labels) GRADUATED to the ONE shared SDF glyph
// atlas: its per-glyph cells are packed once into the TAIL of the SAME storage buffer (after the slot records, at
// BB_ATLAS_BASE), so the badge text is the same field the console overlay and the diegetic bar draw — reconstructed
// with per-channel bilinear + MEDIAN-OF-3 + a screenPxRange coverage ramp + an outline band. The iconographic glyphs
// (d-pad arrows, PlayStation shapes) and the action icons stay PROCEDURAL below. The whole buffer is one
// StructuredBuffer<uint>: slot records are eight uints each (asfloat of the packed floats), the atlas' SDF texels
// follow one RGBA texel per uint.
// KEEP IN SYNC with Puck.Demo.BindingBar.BindingBarOverlayNode's packing (AtlasUintBase, the label bits) and
// BindingGlyphId/BindingIconId.
//
// On Vulkan the texture+sampler fuse into one combined image sampler at set 0, binding 0, and the buffer is the
// storage buffer at binding 1; on DirectX they are t0/s0 and the storage SRV packs in at t1.
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

[[vk::binding(1, 0)]] StructuredBuffer<uint> data : register(t1);

// The first atlas word's uint offset (right after the 64 eight-uint slot records). KEEP IN SYNC with AtlasUintBase.
#define BB_ATLAS_BASE 512u

// a: xy = plate center (aspect units, origin top-left), z = plate half-size, w = glyph half-size
// b: x = asfloat(glyphId << 16 | iconId), yz = glyph-badge offset from the plate center,
//    w = asfloat(alpha byte | pressed<<8 | (glyphIndex0+1)<<9 | (glyphIndex1+1)<<16 | accent<<23 | bound<<24)
// Bits 23/24 are the two chip-state signals docs/ui-design-tokens.md section 5 needs beyond "pressed": ACCENT (the
// context-primary action — Tier 1) and BOUND (0 selects the DISABLED Tier-0 look). KEEP IN SYNC with
// BindingBarOverlayNode.Pack's packedState bit layout.
#define BB_STATE_ACCENT_BIT 23u
#define BB_STATE_BOUND_BIT 24u
struct BindingSlot {
    float4 a;
    float4 b;
};

// One slot record decoded from the raw uint buffer (eight uints: a.xyzw then b.xyzw, each a reinterpreted float).
BindingSlot readSlot(int i) {
    uint o = ((uint)i * 8u);
    BindingSlot s;

    s.a = float4(asfloat(data[o + 0u]), asfloat(data[o + 1u]), asfloat(data[o + 2u]), asfloat(data[o + 3u]));
    s.b = float4(asfloat(data[o + 4u]), asfloat(data[o + 5u]), asfloat(data[o + 6u]), asfloat(data[o + 7u]));

    return s;
}

// header: x = slot count, y = plate corner radius ratio (r.1 / half-chip-height, x plate half), z = global alpha,
//         w = reserved
// style:  x = plate darkness, y = outline/ring width ratio (edge.hairline / half-chip-height, x plate half),
//         z = anti-alias ramp ratio (x plate half), w = bloom halo falloff radius ratio (bloom.halo.blur /
//         half-chip-height, x plate half) — the Tier-1 outward glow's reach
// atlas:  x = atlas cell width, y = atlas cell height (texels), z = distance range (texels), w = label outline band
struct BarData {
    float4 header;
    float4 style;
    float4 atlas;
};
[[vk::push_constant]] ConstantBuffer<BarData> bar;

// ---- design tokens (docs/ui-design-tokens.md) — HLSL-literal mirrors; KEEP IN SYNC with Puck.Demo.Ui.DesignTokens.
// HLSL cannot #include the C# module, so the four chip states (section 5's Tier recipes table) are transcribed by
// hand here: REST (surface.raised + line.hair), HELD (surface.base + bloom.neutral — Tier 1), ACCENT (accent.quiet
// + bloom.accent — Tier 1, the context-primary action), DISABLED (transparent + line.soft — Tier 0, unbound).
static const float3 SURFACE_RAISED_RGB = float3(0.113725, 0.129412, 0.149020);
static const float3 SURFACE_BASE_RGB = float3(0.054902, 0.062745, 0.074510);
static const float3 LINE_HAIR_RGB = float3(1.0, 1.0, 1.0);
static const float LINE_HAIR_A = 0.09;
static const float3 LINE_SOFT_RGB = float3(1.0, 1.0, 1.0);
static const float LINE_SOFT_A = 0.06;
static const float3 ACCENT_RGB = float3(1.0, 0.415686, 0.168627);
static const float ACCENT_QUIET_A = 0.14;
static const float3 ACCENT_INK_RGB = float3(0.086275, 0.039216, 0.015686);
static const float3 TEXT_PRIMARY_RGB = float3(0.929412, 0.937255, 0.949020);
static const float3 BADGE_DARK_RGB = float3(0.05, 0.05, 0.07);
static const float3 BADGE_LIGHT_RGB = float3(0.96, 0.96, 0.98);
// bloom.accent / bloom.neutral ring+halo alphas (the hue itself is ACCENT_RGB or TEXT_PRIMARY_RGB above).
static const float BLOOM_ACCENT_RING_A = 0.55;
static const float BLOOM_ACCENT_HALO_A = 0.42;
static const float BLOOM_NEUTRAL_RING_A = 0.30;
static const float BLOOM_NEUTRAL_HALO_A = 0.22;

// One atlas SDF texel's RGB channels (edge-clamped): decode the packed RGBA word (each channel encoded = 0.5 + d/range).
float3 bbAtlasTexel(int glyphIndex, int2 texel, int cellW, int cellH) {
    texel = clamp(texel, int2(0, 0), int2((cellW - 1), (cellH - 1)));

    uint word = data[BB_ATLAS_BASE + (uint)((glyphIndex * cellW * cellH) + (texel.y * cellW) + texel.x)];

    return (float3(float(word & 0xFFu), float((word >> 8u) & 0xFFu), float((word >> 16u) & 0xFFu)) * (1.0 / 255.0));
}

// Per-channel manual bilinear then MEDIAN-OF-3 (identical to the console overlay's reconstruction) — legitimate at
// shade time (only geometry marching bans the median); a replicated single-channel pack medians to its own value.
float bbAtlasBilinear(int glyphIndex, float2 atlasCoord, int cellW, int cellH) {
    float2 t = (atlasCoord - 0.5);
    int2 b = int2(floor(t));
    float2 f = (t - float2(b));
    float3 s00 = bbAtlasTexel(glyphIndex, (b + int2(0, 0)), cellW, cellH);
    float3 s10 = bbAtlasTexel(glyphIndex, (b + int2(1, 0)), cellW, cellH);
    float3 s01 = bbAtlasTexel(glyphIndex, (b + int2(0, 1)), cellW, cellH);
    float3 s11 = bbAtlasTexel(glyphIndex, (b + int2(1, 1)), cellW, cellH);
    float3 s = lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y);

    return max(min(s.r, s.g), min(max(s.r, s.g), s.b));
}

// ---- distance primitives -------------------------------------------------------------------------------------

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

// ---- letters (unions of capsule strokes in [-1, 1] glyph space, y down) ---------------------------------------

#define GLYPH_STROKE 0.16
#define GLYPH_AA 0.14

float letterA(float2 p) {
    float d = distanceToSegment(p, float2(-0.5, 0.6), float2(0.0, -0.6));

    d = min(d, distanceToSegment(p, float2(0.0, -0.6), float2(0.5, 0.6)));
    d = min(d, distanceToSegment(p, float2(-0.27, 0.15), float2(0.27, 0.15)));

    return d;
}

float letterB(float2 p) {
    float d = distanceToSegment(p, float2(-0.35, -0.6), float2(-0.35, 0.6));

    d = min(d, abs(length(p - float2(-0.02, -0.3)) - 0.30));
    d = min(d, abs(length(p - float2(0.0, 0.3)) - 0.33));

    return d;
}

float letterX(float2 p) {
    float d = distanceToSegment(p, float2(-0.5, -0.6), float2(0.5, 0.6));

    return min(d, distanceToSegment(p, float2(-0.5, 0.6), float2(0.5, -0.6)));
}

float letterY(float2 p) {
    float d = distanceToSegment(p, float2(-0.5, -0.6), float2(0.0, -0.05));

    d = min(d, distanceToSegment(p, float2(0.5, -0.6), float2(0.0, -0.05)));
    d = min(d, distanceToSegment(p, float2(0.0, -0.05), float2(0.0, 0.6)));

    return d;
}

float letterL(float2 p) {
    float d = distanceToSegment(p, float2(-0.25, -0.6), float2(-0.25, 0.6));

    return min(d, distanceToSegment(p, float2(-0.25, 0.6), float2(0.35, 0.6)));
}

float letterR(float2 p) {
    float d = distanceToSegment(p, float2(-0.35, -0.6), float2(-0.35, 0.6));

    d = min(d, abs(length(p - float2(-0.02, -0.28)) - 0.32));
    d = min(d, distanceToSegment(p, float2(-0.05, 0.02), float2(0.4, 0.6)));

    return d;
}

// A chevron pointing up; the arrows rotate it by quarter turns.
float chevron(float2 p) {
    float d = distanceToSegment(p, float2(-0.55, 0.28), float2(0.0, -0.28));

    return min(d, distanceToSegment(p, float2(0.0, -0.28), float2(0.55, 0.28)));
}

// ---- gamepad glyph badges (ids: KEEP IN SYNC with BindingGlyphId) ---------------------------------------------

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
        case 5u: {                                                     // ShapeTriangle (PlayStation 5)
            float d = distanceToSegment(p, float2(0.0, -0.62), float2(0.6, 0.5));

            d = min(d, distanceToSegment(p, float2(0.6, 0.5), float2(-0.6, 0.5)));

            return min(d, distanceToSegment(p, float2(-0.6, 0.5), float2(0.0, -0.62)));
        }
        case 6u: return abs(length(p) - 0.58);                         // ShapeCircle
        case 7u: {                                                     // ShapeCross
            float d = distanceToSegment(p, float2(-0.5, -0.5), float2(0.5, 0.5));

            return min(d, distanceToSegment(p, float2(-0.5, 0.5), float2(0.5, -0.5)));
        }
        case 8u: return abs(sdRoundedBox(p, float2(0.5, 0.5), 0.12));  // ShapeSquare
        case 9u: return letterA(p);
        case 10u: return letterB(p);
        case 11u: return letterX(p);
        case 12u: return letterY(p);
        // The letters evaluate in a scaled-down space, so their distances scale back up (/ scale) to keep the
        // stroke width uniform with the enclosing outline.
        case 13u: return min(abs(sdRoundedBox(p, float2(0.85, 0.45), 0.3)), (letterL(p * 1.8) / 1.8));   // BumperLeft
        case 14u: return min(abs(sdRoundedBox(p, float2(0.85, 0.45), 0.3)), (letterR(p * 1.8) / 1.8));   // BumperRight
        case 15u: return min(abs(sdRoundedBox(p, float2(0.5, 0.75), 0.3)), (letterL(p * 1.6) / 1.6));    // TriggerLeft
        case 16u: return min(abs(sdRoundedBox(p, float2(0.5, 0.75), 0.3)), (letterR(p * 1.6) / 1.6));    // TriggerRight
        case 17u: return min(abs(length(p) - 0.75), (letterL(p * 1.7) / 1.7));                           // StickLeft
        case 18u: return min(abs(length(p) - 0.75), (letterR(p * 1.7) / 1.7));                           // StickRight
        default: return 1e3;
    }
}

// ---- seven-segment numerals (the generic action-slot icons) ---------------------------------------------------

// Segment coverage bit masks for digits 0-9 (a=1 top, b=2 top-right, c=4 bottom-right, d=8 bottom, e=16
// bottom-left, f=32 top-left, g=64 middle).
static const uint SegmentMasks[10] = { 63u, 6u, 91u, 79u, 102u, 109u, 125u, 7u, 127u, 111u };

float digitDistance(uint digit, float2 p) {
    uint mask = SegmentMasks[min(digit, 9u)];
    float d = 1e3;

    if (mask & 1u) { d = min(d, distanceToSegment(p, float2(-0.4, -0.8), float2(0.4, -0.8))); }
    if (mask & 2u) { d = min(d, distanceToSegment(p, float2(0.5, -0.7), float2(0.5, -0.1))); }
    if (mask & 4u) { d = min(d, distanceToSegment(p, float2(0.5, 0.1), float2(0.5, 0.7))); }
    if (mask & 8u) { d = min(d, distanceToSegment(p, float2(-0.4, 0.8), float2(0.4, 0.8))); }
    if (mask & 16u) { d = min(d, distanceToSegment(p, float2(-0.5, 0.1), float2(-0.5, 0.7))); }
    if (mask & 32u) { d = min(d, distanceToSegment(p, float2(-0.5, -0.7), float2(-0.5, -0.1))); }
    if (mask & 64u) { d = min(d, distanceToSegment(p, float2(-0.4, 0.0), float2(0.4, 0.0))); }

    return d;
}

float numberDistance(uint number, float2 p) {
    // The digits evaluate in a scaled space; the distances scale back (x the divisor) so the stroke width the
    // caller applies stays uniform.
    if (number < 10u) {
        return (digitDistance(number, (p / 0.8)) * 0.8);
    }

    // Two digits, side by side (the placeholder actions run 1-12).
    float d = digitDistance((number / 10u), ((p - float2(-0.42, 0.0)) / 0.62));

    return (min(d, digitDistance((number % 10u), ((p - float2(0.42, 0.0)) / 0.62))) * 0.62);
}

float3 hueColor(float hue) {
    float3 k = (frac(hue + float3(0.0, (2.0 / 3.0), (1.0 / 3.0))) * 6.0);
    float3 rgb = saturate(min((k - 3.0), (5.0 - k)));

    // A gentle palette: desaturated toward white so the numerals stay readable on every hue.
    return lerp(float3(0.85, 0.85, 0.85), (1.0 - rgb), 0.62);
}

// ---- action icons (ids: KEEP IN SYNC with BindingIconId) ------------------------------------------------------

// Returns rgb = the icon tint, a = the symbol coverage, for a point in [-1, 1] icon space.
float4 actionIcon(uint iconId, float2 p) {
    if ((iconId == 0u) || (iconId >= 1024u)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    if (iconId == 1u) {                                                // Generic: a plain dot
        return float4(0.78, 0.78, 0.82, strokeMask(length(p), 0.24, GLYPH_AA));
    }

    if (iconId == 2u) {                                                // Jump: a double up-chevron
        float d = min(chevron(p - float2(0.0, -0.22)), chevron(p + float2(0.0, -0.26)));

        return float4(0.36, 0.86, 0.46, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 3u) {                                                // Interact: a diamond
        float d = distanceToSegment(p, float2(0.0, -0.6), float2(0.6, 0.0));

        d = min(d, distanceToSegment(p, float2(0.6, 0.0), float2(0.0, 0.6)));
        d = min(d, distanceToSegment(p, float2(0.0, 0.6), float2(-0.6, 0.0)));
        d = min(d, distanceToSegment(p, float2(-0.6, 0.0), float2(0.0, -0.6)));

        return float4(0.95, 0.76, 0.28, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 4u) {                                                // Target: a reticle
        float d = abs(length(p) - 0.5);

        d = min(d, distanceToSegment(p, float2(0.0, -0.85), float2(0.0, -0.55)));
        d = min(d, distanceToSegment(p, float2(0.0, 0.55), float2(0.0, 0.85)));
        d = min(d, distanceToSegment(p, float2(-0.85, 0.0), float2(-0.55, 0.0)));
        d = min(d, distanceToSegment(p, float2(0.55, 0.0), float2(0.85, 0.0)));
        d = min(d, (length(p) - 0.1));

        return float4(0.92, 0.34, 0.32, strokeMask(d, (GLYPH_STROKE * 0.8), GLYPH_AA));
    }

    if ((iconId >= 8u) && (iconId <= 19u)) {                           // Number1..Number12
        uint number = ((iconId - 8u) + 1u);
        float mask = strokeMask(numberDistance(number, p), (GLYPH_STROKE * 0.75), GLYPH_AA);

        return float4(hueColor(float(number - 1u) / 12.0), mask);
    }

    // ---- creator-mode action icons (KEEP IN SYNC with BindingIconId.Creator*) ----

    if (iconId == 20u) {                                               // CreatorPrev: a left-pointing cycle arrow
        float d = abs(length(p) - 0.45);                              // most of a ring...
        d = max(d, -(p.x + 0.15));                                    // ...cut to the left half (an open loop)
        d = min(d, distanceToSegment(p, float2(-0.45, 0.0), float2(-0.14, -0.28)));   // arrowhead
        d = min(d, distanceToSegment(p, float2(-0.45, 0.0), float2(-0.14, 0.28)));

        return float4(0.72, 0.82, 0.95, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 21u) {                                               // CreatorNext: a right-pointing cycle arrow
        float d = abs(length(p) - 0.45);
        d = max(d, (p.x - 0.15));                                     // cut to the right half
        d = min(d, distanceToSegment(p, float2(0.45, 0.0), float2(0.14, -0.28)));
        d = min(d, distanceToSegment(p, float2(0.45, 0.0), float2(0.14, 0.28)));

        return float4(0.72, 0.82, 0.95, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 22u) {                                               // CreatorPlace: a down-arrow onto a baseline
        float d = distanceToSegment(p, float2(0.0, -0.6), float2(0.0, 0.32));
        d = min(d, distanceToSegment(p, float2(-0.28, 0.05), float2(0.0, 0.34)));
        d = min(d, distanceToSegment(p, float2(0.28, 0.05), float2(0.0, 0.34)));
        d = min(d, distanceToSegment(p, float2(-0.5, 0.62), float2(0.5, 0.62)));      // the ground line

        return float4(0.40, 0.90, 0.52, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 23u) {                                               // CreatorDelete: an X
        float d = distanceToSegment(p, float2(-0.45, -0.45), float2(0.45, 0.45));
        d = min(d, distanceToSegment(p, float2(-0.45, 0.45), float2(0.45, -0.45)));

        return float4(0.94, 0.42, 0.40, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 24u) {                                               // CreatorExit: a leftward return arrow
        float d = distanceToSegment(p, float2(0.5, 0.0), float2(-0.4, 0.0));
        d = min(d, distanceToSegment(p, float2(-0.4, 0.0), float2(-0.05, -0.32)));
        d = min(d, distanceToSegment(p, float2(-0.4, 0.0), float2(-0.05, 0.32)));

        return float4(0.95, 0.82, 0.45, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 25u) {                                               // CreatorDuplicate: two offset squares
        float d = abs(sdRoundedBox((p - float2(0.16, 0.16)), float2(0.34, 0.34), 0.06));
        d = min(d, abs(sdRoundedBox((p + float2(0.16, 0.16)), float2(0.34, 0.34), 0.06)));

        return float4(0.55, 0.85, 0.95, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 26u) {                                               // CreatorLink: two interlocked rings
        float d = abs(length(p - float2(0.24, 0.0)) - 0.34);
        d = min(d, abs(length(p + float2(0.24, 0.0)) - 0.34));

        return float4(0.62, 0.92, 0.62, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 27u) {                                               // CreatorMaterial: a paint drop
        float d = abs(length(p - float2(0.0, 0.18)) - 0.4);            // the round body...
        d = min(d, distanceToSegment(p, float2(-0.26, -0.10), float2(0.0, -0.62)));   // ...tapering to a tip
        d = min(d, distanceToSegment(p, float2(0.26, -0.10), float2(0.0, -0.62)));
        d = min(d, (length(p - float2(0.12, 0.26)) - 0.08));           // the highlight dot

        return float4(0.92, 0.62, 0.88, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 28u) {                                               // CreatorOpCycle: a two-circle boolean venn
        float left = (length(p - float2(-0.2, 0.0)) - 0.42);
        float right = (length(p - float2(0.2, 0.0)) - 0.42);
        float d = min(abs(left), abs(right));
        // Fill the overlap lens so the icon reads as an OPERATION, not just two rings.
        float lens = max(left, right);

        return float4(0.95, 0.78, 0.42, max(strokeMask(d, GLYPH_STROKE, GLYPH_AA), (0.55 * strokeMask(lens, 0.02, GLYPH_AA))));
    }

    if (iconId == 29u) {                                               // CreatorStyle: a half-filled circle
        float ring = abs(length(p) - 0.5);
        float fill = max((length(p) - 0.5), -p.x);                    // solid left half

        return float4(0.85, 0.85, 0.55, max(strokeMask(ring, GLYPH_STROKE, GLYPH_AA), (0.7 * strokeMask(fill, 0.02, GLYPH_AA))));
    }

    if (iconId == 30u) {                                               // CreatorDeselect: a slashed circle
        float d = abs(length(p) - 0.5);
        d = min(d, distanceToSegment(p, float2(-0.36, 0.36), float2(0.36, -0.36)));

        return float4(0.78, 0.78, 0.82, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    if (iconId == 31u) {                                               // CreatorRecord: a filled dot
        return float4(0.94, 0.38, 0.38, strokeMask((length(p) - 0.34), 0.02, GLYPH_AA));
    }

    if (iconId == 32u) {                                               // CreatorPlay: a play triangle
        float d = distanceToSegment(p, float2(-0.34, -0.5), float2(-0.34, 0.5));
        d = min(d, distanceToSegment(p, float2(-0.34, 0.5), float2(0.52, 0.0)));
        d = min(d, distanceToSegment(p, float2(0.52, 0.0), float2(-0.34, -0.5)));

        return float4(0.45, 0.92, 0.55, strokeMask(d, GLYPH_STROKE, GLYPH_AA));
    }

    return float4(0.0, 0.0, 0.0, 0.0);
}

// ---- the pass --------------------------------------------------------------------------------------------------

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));
    float3 color = sourceTexture.Sample(sourceSampler, uv).rgb;

    float aspect = (float(width) / float(height));
    float2 pointA = float2((uv.x * aspect), uv.y);
    int slotCount = (int)bar.header.x;
    float globalAlpha = bar.header.z;

    // press.held's 1px translate (docs/ui-design-tokens.md section 5), converted from a design-space px to a
    // frame-aspect-unit offset (one y aspect-unit spans the frame height in pixels).
    float pressTranslateY = (1.0 / float(height));

    for (int i = 0; i < slotCount; ++i) {
        BindingSlot slot = readSlot(i);
        float plateHalf = slot.a.z;

        uint ids = asuint(slot.b.x);
        uint glyphId = (ids >> 16);
        uint iconId = (ids & 0xFFFFu);
        uint state = asuint(slot.b.w);
        float alpha = ((float(state & 0xFFu) / 255.0) * globalAlpha);
        bool pressed = ((state & 0x100u) != 0u);
        bool accent = ((state & (1u << BB_STATE_ACCENT_BIT)) != 0u);
        bool bound = ((state & (1u << BB_STATE_BOUND_BIT)) != 0u);
        // The four chip states (section 5's Tier recipes table). HELD wins over ACCENT (pressing the context-primary
        // chip still needs press feedback); DISABLED only shows when nothing else lights the chip.
        bool isHeld = pressed;
        bool isAccentTier = (accent && !pressed);
        bool isDisabled = (!bound && !pressed && !accent);
        // The whole chip (plate + icon + badge) rides press.held's 1px translateY while held.
        float2 slotCenter = (slot.a.xy + float2(0.0, (isHeld ? pressTranslateY : 0.0)));
        float2 local = (pointA - slotCenter);

        // Early out: the glyph badge can hang past the plate corner, so the bound is generous.
        if (max(abs(local.x), abs(local.y)) > (plateHalf * 2.2)) {
            continue;
        }

        float aa = (plateHalf * bar.style.z);
        float outlineWidth = (plateHalf * bar.style.y);
        float haloBlurPx = (plateHalf * bar.style.w);
        float plateDistance = sdRoundedBox(local, float2((plateHalf * 0.92), (plateHalf * 0.92)), (plateHalf * bar.header.y));
        float fill = (1.0 - smoothstep(0.0, aa, plateDistance));
        float outline = strokeMask(abs(plateDistance), outlineWidth, aa);

        // Tier 0 REST: surface.raised + line.hair (the plate-darkness ratio tunes its translucency).
        // Tier 0 DISABLED: transparent fill + line.soft (a free/unbound button, still shown so its socket reads).
        // Tier 1 HELD: surface.base, fully seated, + bloom.neutral. Tier 1 ACCENT: accent.quiet + bloom.accent (the
        // context-primary action). Tier-1 chips skip the plain hairline — the bloom ring below IS their edge.
        float3 fillColor = (isHeld ? SURFACE_BASE_RGB : (isAccentTier ? ACCENT_RGB : SURFACE_RAISED_RGB));
        float plateOpacity = (isDisabled ? 0.0 : (isHeld ? 1.0 : (isAccentTier ? ACCENT_QUIET_A : bar.style.x)));

        color = lerp(color, fillColor, (fill * alpha * plateOpacity));

        if (isHeld || isAccentTier) {
            // Tier-1 bloom: an SDF distance-falloff halo OUTSIDE the plate plus a brighter 1px ring AT the edge, in
            // the element's own semantic hue — an extra SDF pass, never a blur (section 9's GPU-implementability rule).
            float3 hue = (isAccentTier ? ACCENT_RGB : TEXT_PRIMARY_RGB);
            float ringA = (isAccentTier ? BLOOM_ACCENT_RING_A : BLOOM_NEUTRAL_RING_A);
            float haloA = (isAccentTier ? BLOOM_ACCENT_HALO_A : BLOOM_NEUTRAL_HALO_A);
            float haloMask = (saturate(1.0 - (max(plateDistance, 0.0) / max(haloBlurPx, 1e-4))) * step(0.0, plateDistance));

            color = lerp(color, hue, (haloMask * haloA * alpha));
            color = lerp(color, hue, (outline * ringA * alpha));
        } else {
            float3 outlineColor = (isDisabled ? LINE_SOFT_RGB : LINE_HAIR_RGB);
            float outlineAlpha = (isDisabled ? LINE_SOFT_A : LINE_HAIR_A);

            color = lerp(color, outlineColor, (outline * alpha * outlineAlpha));
        }

        // The bound action's icon, centered on the plate.
        if (iconId != 0u) {
            float4 icon = actionIcon(iconId, (local / max((plateHalf * 0.62), 1e-5)));

            color = lerp(color, icon.rgb, (icon.a * fill * alpha));
        }

        // The gamepad-glyph badge, hugging its corner: a dark backing disc, then a light glyph — EXCEPT on the
        // ACCENT tier, where "badge fills accent, glyph accent.ink" (section 5's accent chip recipe). A LETTER label
        // (char0 != 0 in the state high bits) renders from the shared SDF atlas; the iconographic glyphs stay procedural.
        float glyphHalf = slot.a.w;
        uint char0 = ((state >> 9u) & 0x7Fu);
        float3 badgeBackingColor = (isAccentTier ? ACCENT_RGB : BADGE_DARK_RGB);
        float3 badgeInkColor = (isAccentTier ? ACCENT_INK_RGB : BADGE_LIGHT_RGB);

        if ((glyphHalf > 0.0) && (char0 != 0u)) {
            float2 glyphLocal = ((pointA - (slotCenter + slot.b.yz)) / glyphHalf);

            if (max(abs(glyphLocal.x), abs(glyphLocal.y)) < 1.6) {
                color = lerp(color, badgeBackingColor, ((1.0 - smoothstep(1.0, (1.0 + (GLYPH_AA * 2.0)), length(glyphLocal))) * alpha * 0.85));

                uint char1 = ((state >> 16u) & 0x7Fu);
                int labelLen = ((char1 != 0u) ? 2 : 1);
                int atlasCellW = (int)bar.atlas.x;
                int atlasCellH = (int)bar.atlas.y;
                float distanceRange = bar.atlas.z;
                float outlineBand = bar.atlas.w;
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
                    float2 atlasCoord = float2((u * atlasCellW), (v * atlasCellH));
                    float encoded = bbAtlasBilinear(glyphIndex, atlasCoord, atlasCellW, atlasCellH);

                    // screenPxRange from the on-screen char height: 2*labelHalfH glyph-local units = that many
                    // glyphHalf aspect-units, and one y aspect-unit is the frame height in pixels.
                    float charPxH = ((2.0 * labelHalfH) * glyphHalf * float(height));
                    float screenPxRange = max((distanceRange * (charPxH / float(atlasCellH))), 1.0);
                    float coverage = saturate((screenPxRange * (encoded - 0.5)) + 0.5);
                    float outlineC = saturate((screenPxRange * ((encoded - 0.5) + outlineBand)) + 0.5);

                    color = lerp(color, (badgeInkColor * 0.3), (outlineC * alpha * 0.85));
                    color = lerp(color, badgeInkColor, (coverage * alpha));
                }
            }
        } else if ((glyphId != 0u) && (glyphHalf > 0.0)) {
            float2 glyphLocal = ((pointA - (slotCenter + slot.b.yz)) / glyphHalf);

            if (max(abs(glyphLocal.x), abs(glyphLocal.y)) < 1.6) {
                float backing = (1.0 - smoothstep(1.0, (1.0 + (GLYPH_AA * 2.0)), length(glyphLocal)));
                float glyph = strokeMask(glyphDistance(glyphId, glyphLocal), GLYPH_STROKE, GLYPH_AA);

                color = lerp(color, badgeBackingColor, (backing * alpha * 0.85));
                color = lerp(color, badgeInkColor, (glyph * alpha));
            }
        }
    }

    return float4(color, 1.0);
}
