// Demo binding-bar overlay (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for DirectX):
// sample the inner producer's render and draw the controller action-bar cluster over it — per visible slot a
// rounded plate, a procedural SDF action icon, and a gamepad-glyph badge hugging the slot corner; plus the
// modifier pips between the clusters. Per-slot data rides a storage buffer (12-60 slots exceed any push-constant
// budget); push constants carry only the scalar style knobs. This lives in Puck.Demo so no UI concept leaks into
// the reusable SDF engine.
// KEEP IN SYNC with Puck.Demo.BindingBar.BindingBarOverlayNode's packing and BindingGlyphId/BindingIconId.
//
// On Vulkan the texture+sampler fuse into one combined image sampler at set 0, binding 0, and the slot buffer is
// the storage buffer at binding 1; on DirectX they are t0/s0 and the storage SRV packs in at t1.
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

// a: xy = plate center (aspect units, origin top-left), z = plate half-size, w = glyph half-size
// b: x = asfloat(glyphId << 16 | iconId), yz = glyph-badge offset from the plate center,
//    w = asfloat(alpha byte | pressed flag << 8)
struct BindingSlot {
    float4 a;
    float4 b;
};
[[vk::binding(1, 0)]] StructuredBuffer<BindingSlot> slots : register(t1);

// header: x = slot count, y = plate corner radius (x plate half), z = global alpha, w = pressed boost
// style:  x = plate darkness, y = outline width (x plate half), z = anti-alias ramp (x plate half), w = reserved
struct BarData {
    float4 header;
    float4 style;
};
[[vk::push_constant]] ConstantBuffer<BarData> bar;

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

    for (int i = 0; i < slotCount; ++i) {
        BindingSlot slot = slots[i];
        float2 local = (pointA - slot.a.xy);
        float plateHalf = slot.a.z;

        // Early out: the glyph badge can hang past the plate corner, so the bound is generous.
        if (max(abs(local.x), abs(local.y)) > (plateHalf * 2.2)) {
            continue;
        }

        uint ids = asuint(slot.b.x);
        uint glyphId = (ids >> 16);
        uint iconId = (ids & 0xFFFFu);
        uint state = asuint(slot.b.w);
        float alpha = ((float(state & 0xFFu) / 255.0) * globalAlpha);
        bool pressed = ((state & 0x100u) != 0u);

        float aa = (plateHalf * bar.style.z);
        float outlineWidth = (plateHalf * bar.style.y);
        float plateDistance = sdRoundedBox(local, float2((plateHalf * 0.92), (plateHalf * 0.92)), (plateHalf * bar.header.y));
        float fill = (1.0 - smoothstep(0.0, aa, plateDistance));
        float outline = strokeMask(abs(plateDistance), outlineWidth, aa);

        // The plate: a dark translucent backing; pressing lifts it toward the highlight.
        float3 plateColor = lerp(float3(0.07, 0.08, 0.11), float3(0.32, 0.34, 0.40), (pressed ? bar.header.w : 0.0));
        float3 outlineColor = (pressed ? float3(1.0, 0.92, 0.55) : float3(0.62, 0.66, 0.74));

        color = lerp(color, plateColor, (fill * alpha * bar.style.x));
        color = lerp(color, outlineColor, (outline * alpha * 0.9));

        // The bound action's icon, centered on the plate.
        if (iconId != 0u) {
            float4 icon = actionIcon(iconId, (local / max((plateHalf * 0.62), 1e-5)));

            color = lerp(color, icon.rgb, (icon.a * fill * alpha));
        }

        // The gamepad-glyph badge, hugging its corner: a dark backing disc, then the white glyph.
        float glyphHalf = slot.a.w;

        if ((glyphId != 0u) && (glyphHalf > 0.0)) {
            float2 glyphLocal = ((pointA - (slot.a.xy + slot.b.yz)) / glyphHalf);

            if (max(abs(glyphLocal.x), abs(glyphLocal.y)) < 1.6) {
                float backing = (1.0 - smoothstep(1.0, (1.0 + (GLYPH_AA * 2.0)), length(glyphLocal)));
                float glyph = strokeMask(glyphDistance(glyphId, glyphLocal), GLYPH_STROKE, GLYPH_AA);

                color = lerp(color, float3(0.05, 0.05, 0.07), (backing * alpha * 0.85));
                color = lerp(color, float3(0.96, 0.96, 0.98), (glyph * alpha));
            }
        }
    }

    return float4(color, 1.0);
}
