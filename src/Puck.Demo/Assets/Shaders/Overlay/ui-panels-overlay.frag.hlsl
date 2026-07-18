// Demo overlay-panels pass (single-source HLSL; DXC -> SPIR-V for Vulkan). ONE node draws N PANELS from a packed
// storage buffer: each panel is token chrome (a scrim fill in a rounded rect, a 1px hairline outline, an optional
// title band + divider, an optional Tier-1 status ring + bloom halo) plus a flat list of ELEMENTS — text runs
// (glyph indices into the ONE shared SDF atlas, reconstructed with per-channel bilinear + MEDIAN-OF-3 + a
// screenPxRange coverage ramp + an outline band, exactly the console overlay's decode) and rounded-rect cells
// (chips, selection fills, accent ticks, state rails). Panels are DATA, surfaces are CONTENT: the toast, the hub
// picker, the tracker transport, and the gallery plaque are all writers into the same records — a future surface
// is a new writer, not a new shader.
//
// The storage buffer: [0, panelBase) = the shared atlas' per-glyph SDF cells (one RGBA texel per uint, uploaded
// once); [panelBase, elementBase) = the panel records; [elementBase, textBase) = the element records;
// [textBase, ...) = the glyph-code words the text runs index (one code per uint).
// KEEP IN SYNC with Puck.Demo.Ui.OverlayPanelsNode (push-constant layout, the panel/element word layouts, the
// color-role table = Puck.Demo.Ui.PanelColorRole) and SharedGlyphSdfPack (RGBA cell packing + median-of-3 decode).
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

[[vk::binding(1, 0)]] StructuredBuffer<uint> panelData : register(t1);

// counts: panelCount, elementCount, atlasCellW, atlasCellH (texels)
// sdf:    distanceRange (texels), outlineBand (encoded units), panelBase (word index), elementBase (word index)
// misc:   textBase (word index), reserved x3
struct PanelPassData {
    float4 counts;
    float4 sdf;
    float4 misc;
};
[[vk::push_constant]] ConstantBuffer<PanelPassData> pc;

// The atlas holds printable ASCII 0x20..0x7E (95 glyphs); codes are stored as ALREADY-RESOLVED glyph indices.
#define GLYPH_COUNT 95

// Words per record. KEEP IN SYNC with OverlayPanelsNode.PanelWords / ElementWords.
#define PANEL_WORDS 12u
#define ELEMENT_WORDS 12u

// ---- design tokens (docs/ui-design-tokens.md) — HLSL-literal mirrors; KEEP IN SYNC with DesignTokens.cs ----------

// Not a token — the AA ramp width for hairline/rounded-rect edges.
#define EDGE_AA 1.25

// Section 2: the 3-step radius scale (per panel styleKind below: 0 -> r.3 panels, 1/2 -> r.2 strips/toast/plaque).
#define RADIUS_2 6.0
#define RADIUS_3 9.0

// Section 4: the scrims (panel 0.90 / strip 0.86 / chip 0.94 over rgb(18,21,25) — toast uses rgb(23,27,31)).
static const float3 SCRIM_PANEL_RGB = float3(0.070588, 0.082353, 0.098039);
static const float SCRIM_PANEL_A = 0.90;
static const float SCRIM_STRIP_A = 0.86;
static const float3 SCRIM_CHIP_RGB = float3(0.090196, 0.105882, 0.121569);
static const float SCRIM_CHIP_A = 0.94;
// Outlines.
static const float3 LINE_HAIR_RGB = float3(1.0, 1.0, 1.0);
static const float LINE_HAIR_A = 0.09;
static const float3 LINE_SOFT_RGB = float3(1.0, 1.0, 1.0);
static const float LINE_SOFT_A = 0.06;
// Section 5: the Tier-1 bloom geometry (ring 1px @ 0.55, halo 18px falloff @ 0.42).
#define BLOOM_HALO_BLUR 18.0
static const float BLOOM_RING_A = 0.55;
static const float BLOOM_HALO_A = 0.42;

// The color-role table — KEEP IN SYNC with Puck.Demo.Ui.PanelColorRole (the C# enum names each index).
// 0 text.primary · 1 text.dim · 2 text.mute · 3 accent · 4 positive · 5 warning · 6 danger · 7 phosphor
// 8 accent.ink · 9 surface.raised · 10 surface.inset · 11 accent.quiet (its 0.14 alpha baked into .a) ·
// 12 phosphor.cyan (the gallery kicker — the ONE sanctioned non-diegetic phosphor quote per the token spec).
static const float4 ROLE_COLORS[13] = {
    float4(0.929412, 0.937255, 0.949020, 1.0),  //  0 text.primary #EDEFF2
    float4(0.607843, 0.639216, 0.670588, 1.0),  //  1 text.dim     #9BA3AB
    float4(0.360784, 0.392157, 0.423529, 1.0),  //  2 text.mute    #5C646C
    float4(1.000000, 0.415686, 0.168627, 1.0),  //  3 accent       #FF6A2B
    float4(0.356863, 0.788235, 0.549020, 1.0),  //  4 positive     #5BC98C
    float4(0.909804, 0.701961, 0.254902, 1.0),  //  5 warning      #E8B341
    float4(0.949020, 0.337255, 0.356863, 1.0),  //  6 danger       #F2565B
    float4(0.360784, 0.980392, 0.627451, 1.0),  //  7 phosphor     #5CFAA0
    float4(0.086275, 0.039216, 0.015686, 1.0),  //  8 accent.ink   #160A04
    float4(0.113725, 0.129412, 0.149020, 1.0),  //  9 surface.raised #1D2126
    float4(0.043137, 0.050980, 0.058824, 1.0),  // 10 surface.inset  #0B0D0F
    float4(1.000000, 0.415686, 0.168627, 0.14), // 11 accent.quiet
    float4(0.368627, 0.921569, 0.878431, 1.0),  // 12 phosphor.cyan #5EEBE0
};

// ---- shared-atlas glyph reconstruction (mirrors console-overlay.frag.hlsl exactly) -------------------------------

// One glyph SDF texel's RGB channels (edge-clamped): decode the packed RGBA word (each channel encoded = 0.5 + d/range).
float3 PanelSdfTexel(int glyph, int2 texel, int cellW, int cellH) {
    texel = clamp(texel, int2(0, 0), int2((cellW - 1), (cellH - 1)));

    uint word = panelData[(glyph * cellW * cellH) + (texel.y * cellW) + texel.x];   // the atlas packs at buffer offset 0

    return (float3(float(word & 0xFFu), float((word >> 8u) & 0xFFu), float((word >> 16u) & 0xFFu)) * (1.0 / 255.0));
}

// Per-channel manual bilinear then MEDIAN-OF-3 — legitimate at shade time (only geometry marching bans the median);
// a replicated single-channel pack medians to exactly its own value.
float PanelSdfBilinear(int glyph, float2 atlasCoord, int cellW, int cellH) {
    float2 t = (atlasCoord - 0.5);
    int2 b = int2(floor(t));
    float2 f = (t - float2(b));
    float3 s00 = PanelSdfTexel(glyph, (b + int2(0, 0)), cellW, cellH);
    float3 s10 = PanelSdfTexel(glyph, (b + int2(1, 0)), cellW, cellH);
    float3 s01 = PanelSdfTexel(glyph, (b + int2(0, 1)), cellW, cellH);
    float3 s11 = PanelSdfTexel(glyph, (b + int2(1, 1)), cellW, cellH);
    float3 s = lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y);

    return max(min(s.r, s.g), min(max(s.r, s.g), s.b));
}

// Samples one glyph cell: x = fill coverage, y = the darker outline band's coverage (the floats-over-a-lit-world
// contrast toolkit, from the SAME field at zero extra taps).
float2 SamplePanelGlyph(int glyph, float2 cellLocal, float2 cellSize) {
    if ((glyph < 0) || (glyph >= GLYPH_COUNT)) {
        return float2(0.0, 0.0);
    }

    int atlasCellW = (int)pc.counts.z;
    int atlasCellH = (int)pc.counts.w;
    float2 atlasCoord = float2(((cellLocal.x / cellSize.x) * atlasCellW), ((cellLocal.y / cellSize.y) * atlasCellH));
    float encoded = PanelSdfBilinear(glyph, atlasCoord, atlasCellW, atlasCellH);
    // screenPxRange = distanceRange(texels) x screen-px-per-texel (the on-screen cell maps the atlas cell).
    float screenPxRange = (pc.sdf.x * (cellSize.y / pc.counts.w));
    float coverage = saturate((screenPxRange * (encoded - 0.5)) + 0.5);
    float outline = saturate((screenPxRange * ((encoded - 0.5) + pc.sdf.y)) + 0.5);

    return float2(coverage, outline);
}

// ---- panel-chrome distance primitives ----------------------------------------------------------------------------

float sdRoundedBoxPanel(float2 p, float2 halfSize, float radius) {
    float2 q = ((abs(p) - halfSize) + radius);

    return ((length(max(q, 0.0)) + min(max(q.x, q.y), 0.0)) - radius);
}

float strokeMaskPanel(float distance, float width, float aa) {
    return (1.0 - smoothstep(width, (width + aa), distance));
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));
    float3 color = sourceTexture.Sample(sourceSampler, uv).rgb;

    uint panelBase = (uint)pc.sdf.z;
    uint elementBase = (uint)pc.sdf.w;
    uint textBase = (uint)pc.misc.x;
    int panelCount = (int)pc.counts.x;
    int elementCount = (int)pc.counts.y;

    // ---- panel chrome (fill + hairline + optional band divider + optional Tier-1 ring/halo) ----------------------
    // Panel word layout (12 words) — KEEP IN SYNC with OverlayPanelsNode.WritePanel:
    //   0..3  rect x, y, w, h (float, pixels, top-left origin)
    //   4     flags (uint: bit0 = title band present)
    //   5     styleKind (uint: 0 = panel scrim + r.3, 1 = strip scrim + r.2, 2 = chip scrim + r.2)
    //   6     ring role (uint: 0 = none, else a ROLE_COLORS index — the Tier-1 bloom ring + halo hue)
    //   7     title band height (float, pixels)
    //   8     panel alpha (float — the toast's content-tick fade scales its whole chrome)
    //   9..11 reserved
    for (int p = 0; (p < panelCount); p++) {
        uint o = (panelBase + ((uint)p * PANEL_WORDS));
        float4 rect = float4(asfloat(panelData[o + 0u]), asfloat(panelData[o + 1u]), asfloat(panelData[o + 2u]), asfloat(panelData[o + 3u]));
        uint flags = panelData[o + 4u];
        uint styleKind = panelData[o + 5u];
        uint ringRole = panelData[o + 6u];
        float bandHeight = asfloat(panelData[o + 7u]);
        float panelAlpha = asfloat(panelData[o + 8u]);
        float2 local = (fragCoord.xy - rect.xy);
        // The Tier-1 halo legitimately bleeds past the rect; use its reach as the slack when a ring is lit.
        float slack = ((ringRole != 0u) ? BLOOM_HALO_BLUR : EDGE_AA);

        if ((local.x < -slack) || (local.y < -slack) || (local.x >= (rect.z + slack)) || (local.y >= (rect.w + slack))) {
            continue;
        }

        float radius = ((styleKind == 0u) ? RADIUS_3 : RADIUS_2);
        float scrimAlpha = ((styleKind == 0u) ? SCRIM_PANEL_A : ((styleKind == 1u) ? SCRIM_STRIP_A : SCRIM_CHIP_A));
        float3 scrimRgb = ((styleKind == 2u) ? SCRIM_CHIP_RGB : SCRIM_PANEL_RGB);
        float2 halfSize = (rect.zw * 0.5);
        float panelDist = sdRoundedBoxPanel((local - halfSize), halfSize, radius);
        float panelMask = (1.0 - smoothstep(0.0, EDGE_AA, panelDist));

        color = lerp(color, scrimRgb, (panelMask * scrimAlpha * panelAlpha));
        color = lerp(color, LINE_HAIR_RGB, (strokeMaskPanel(abs(panelDist), 0.5, EDGE_AA) * LINE_HAIR_A * panelAlpha));

        // The title band's divider (a 1px line.soft rule below the band, like the console's).
        if ((flags & 1u) != 0u) {
            float dividerDist = abs(local.y - bandHeight);

            if (dividerDist < (EDGE_AA + 0.5)) {
                color = lerp(color, LINE_SOFT_RGB, (strokeMaskPanel(dividerDist, 0.5, EDGE_AA) * LINE_SOFT_A * panelMask * panelAlpha));
            }
        }

        // Tier-1 (a transient echo — the toast): a 1px lit ring straddling the boundary + an outward distance-falloff
        // halo in the SAME semantic hue — the one-geometry/hue-varies bloom recipe.
        if (ringRole != 0u) {
            float3 hue = ROLE_COLORS[ringRole].rgb;
            float ring = strokeMaskPanel(abs(panelDist), 0.5, EDGE_AA);
            float halo = saturate(1.0 - (max(panelDist, 0.0) / BLOOM_HALO_BLUR));

            color = lerp(color, hue, (max((ring * BLOOM_RING_A), (halo * halo * BLOOM_HALO_A * (panelDist > 0.0 ? 1.0 : 0.0))) * panelAlpha));
        }
    }

    // ---- elements (rects then text runs, in submission order) ----------------------------------------------------
    // Element word layout (12 words) — KEEP IN SYNC with OverlayPanelsNode.WriteRect/WriteText:
    //   0..3  x, y, a, b (float: text = origin + one glyph cell's on-screen w/h; rect = origin + w/h)
    //   4     packed (uint: bit0 = 1 rect / 0 text; colorRole << 4)
    //   5     text: glyph start (uint, word offset into the text region); rect: unused
    //   6     text: glyph count (uint); rect: corner radius (float)
    //   7     alpha (float)
    //   8..11 reserved
    for (int e = 0; (e < elementCount); e++) {
        uint o = (elementBase + ((uint)e * ELEMENT_WORDS));
        float2 origin = float2(asfloat(panelData[o + 0u]), asfloat(panelData[o + 1u]));
        float2 ab = float2(asfloat(panelData[o + 2u]), asfloat(panelData[o + 3u]));
        uint packed = panelData[o + 4u];
        float alpha = asfloat(panelData[o + 7u]);
        uint role = ((packed >> 4u) & 0xFFu);
        float2 local = (fragCoord.xy - origin);

        if ((packed & 1u) != 0u) {
            // A rounded-rect cell (chip fill, selection fill, accent tick, state rail).
            if ((local.x < -EDGE_AA) || (local.y < -EDGE_AA) || (local.x >= (ab.x + EDGE_AA)) || (local.y >= (ab.y + EDGE_AA))) {
                continue;
            }

            float radius = asfloat(panelData[o + 6u]);
            float2 halfSize = (ab * 0.5);
            float dist = sdRoundedBoxPanel((local - halfSize), halfSize, radius);
            float mask = (1.0 - smoothstep(0.0, EDGE_AA, dist));
            float4 fill = ROLE_COLORS[role];

            color = lerp(color, fill.rgb, (mask * fill.a * alpha));
        } else {
            // A text run: a row of monospace glyph cells; codes are pre-resolved atlas indices.
            uint count = panelData[o + 6u];
            float runWidth = (ab.x * (float)count);

            if ((local.x < 0.0) || (local.y < 0.0) || (local.x >= runWidth) || (local.y >= ab.y)) {
                continue;
            }

            int column = (int)floor(local.x / ab.x);
            uint glyph = panelData[textBase + panelData[o + 5u] + (uint)column];
            float2 cellLocal = float2((local.x - (column * ab.x)), local.y);
            float2 sample = SamplePanelGlyph((int)glyph, cellLocal, ab);

            color = lerp(color, float3(0.0, 0.01, 0.015), (sample.y * 0.85 * alpha));
            color = lerp(color, ROLE_COLORS[role].rgb, (sample.x * alpha));
        }
    }

    return float4(color, 1.0);
}
