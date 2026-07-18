// Demo on-screen developer-console overlay (single-source HLSL; DXC -> SPIR-V for Vulkan). Sample the inner
// producer's render and draw the console panel on top: a scrim panel with a title-bar band, a hairline outline, and
// a fixed monospace grid of scrollback + a phosphor prompt line. The glyph SOURCE graduated from a GDI+ coverage
// bitmap to the ONE shared exact-EDT SIGNED-DISTANCE atlas — the same field the world-glyph op marches and the
// diegetic UI embosses. The per-glyph SDF cells AND the per-frame character grid both ride ONE storage buffer (the
// atlas' packed SDF bytes at the front, the cols*rows character codes after it), so the overlay needs no second
// texture — it keeps the proven single-combined-image-sampler + one-storage-buffer shape of the binding bar. Each
// edge is reconstructed with BILINEAR sampling + a screenPxRange coverage ramp (crisp at any overlay scale), plus
// an OUTLINE band from the same field so text stays legible over bright world content.
//
// PROFESSIONAL-UI TOKEN SWEEP: every color/radius/padding/caret constant below is an HLSL-literal MIRROR of
// Puck.Demo.Ui.DesignTokens (docs/ui-design-tokens.md) — KEEP IN SYNC by hand, since HLSL cannot #include the C#
// module. Geometry that varies with the window (the panel rect, the grid metrics) still rides push constants,
// computed in ConsoleOverlayNode from the SAME token constants (StageMargin/ContentPad/TitleBandHeight there mirror
// PANEL_PAD/TITLE_BAND_HEIGHT/PANEL_RADIUS here).
// KEEP IN SYNC with Puck.Demo.DevConsole.ConsoleOverlayNode (push-constant layout + buffer packing) and
// ConsoleGlyphAtlas (per-glyph RGBA cell packing + the median-of-3 decode convention).
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

// data[0 .. textOffset) = per-glyph SDF cells, one RGBA texel per uint (little-endian R|G|B|A, each channel encoded =
// 0.5 + d/range, > 0.5 inside the glyph); data[textOffset ..] = the character grid (row-major, low byte = code point).
// Word of glyph g's atlas texel (x, y) is (g*atlasCellW*atlasCellH + y*atlasCellW + x). The shader medians RGB after
// per-channel bilinear — genuine multi-channel reconstruction when the pack carries a true MTSDF atlas, and exactly
// the old single-channel decode when the runtime atlas replicates one channel (median of equal values is the value).
[[vk::binding(1, 0)]] StructuredBuffer<uint> consoleData : register(t1);

struct ConsoleData {
    float4 panel;  // x, y, w, h  (pixels) — the OUTER rounded-rect bounds: title band + divider + padded grid
    float4 grid;   // cols, rows, cellW, cellH  (on-screen pixels) — the CONTENT grid only
    float4 state;  // caretOn (0/1), dragging (0/1) — brightens the title-band divider, reserved, reserved
    float4 misc;   // cursorCol, cursorRow, textCellUintOffset, firstChar
    float4 sdf;    // atlasCellW, atlasCellH (texels), screenPxRange, outlineBand
};
[[vk::push_constant]] ConstantBuffer<ConsoleData> pc;

// The atlas holds printable ASCII 0x20..0x7E (95 glyphs).
#define GLYPH_COUNT 95

// ---- design tokens (docs/ui-design-tokens.md) — HLSL-literal mirrors; KEEP IN SYNC with DesignTokens.cs ----------

// Section 1/2: the padding rhythm, the title band height, the panel radius (space.3 / height.consoleHead / r.3).
#define PANEL_PAD 12.0
#define TITLE_BAND_HEIGHT 38.0
#define PANEL_RADIUS 9.0
// Not a token — the AA ramp width for hairline/rounded-rect edges (uniform-stroke outline band ramp).
#define EDGE_AA 1.25

// Section 4: scrim.panel, line.hair, line.soft, text.primary, phosphor, accent (+ accent.quiet's alpha).
static const float3 SCRIM_PANEL_RGB = float3(0.070588, 0.082353, 0.098039);
static const float SCRIM_PANEL_A = 0.90;
static const float3 LINE_HAIR_RGB = float3(1.0, 1.0, 1.0);
static const float LINE_HAIR_A = 0.09;
static const float3 LINE_SOFT_RGB = float3(1.0, 1.0, 1.0);
static const float LINE_SOFT_A = 0.06;
static const float3 TEXT_PRIMARY_RGB = float3(0.929412, 0.937255, 0.949020);
static const float3 PHOSPHOR_RGB = float3(0.360784, 0.980392, 0.627451);
static const float3 ACCENT_RGB = float3(1.0, 0.415686, 0.168627);
static const float ACCENT_QUIET_A = 0.14;
// Section 5: the bloom halo's falloff radius, reused (approximately) as the caret glow's SDF falloff distance.
#define BLOOM_HALO_BLUR 18.0

// The static "CONSOLE" title (ASCII codes — HLSL has no portable char literal): C O N S O L E.
static const int TITLE_CHARS[7] = { 67, 79, 78, 83, 79, 76, 69 };
#define TITLE_CHAR_COUNT 7

// One glyph SDF texel's RGB channels (edge-clamped): decode the packed RGBA word (each channel encoded = 0.5 + d/range).
float3 ConsoleSdfTexel(int glyph, int2 texel, int cellW, int cellH) {
    texel = clamp(texel, int2(0, 0), int2((cellW - 1), (cellH - 1)));

    uint word = consoleData[(glyph * cellW * cellH) + (texel.y * cellW) + texel.x];   // the atlas packs at buffer offset 0

    return (float3(float(word & 0xFFu), float((word >> 8u) & 0xFFu), float((word >> 16u) & 0xFFu)) * (1.0 / 255.0));
}

// Per-channel manual bilinear (four point taps + arithmetic lerp) then MEDIAN-OF-3 — the classic MSDF reconstruction,
// legitimate at shade time (only geometry marching bans the median). The bilinear stays parity-safe, and the LETTER
// anti-aliases, not the cell.
float ConsoleSdfBilinear(int glyph, float2 atlasCoord, int cellW, int cellH) {
    float2 t = (atlasCoord - 0.5);
    int2 b = int2(floor(t));
    float2 f = (t - float2(b));
    float3 s00 = ConsoleSdfTexel(glyph, (b + int2(0, 0)), cellW, cellH);
    float3 s10 = ConsoleSdfTexel(glyph, (b + int2(1, 0)), cellW, cellH);
    float3 s01 = ConsoleSdfTexel(glyph, (b + int2(0, 1)), cellW, cellH);
    float3 s11 = ConsoleSdfTexel(glyph, (b + int2(1, 1)), cellW, cellH);
    float3 s = lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y);

    return max(min(s.r, s.g), min(max(s.r, s.g), s.b));
}

// ---- panel-chrome distance primitives (mirrors binding-bar-overlay.frag.hlsl's) ---------------------------------

float sdRoundedBoxConsole(float2 p, float2 halfSize, float radius) {
    float2 q = ((abs(p) - halfSize) + radius);

    return ((length(max(q, 0.0)) + min(max(q.x, q.y), 0.0)) - radius);
}

// Coverage of a stroked distance: 1 inside the stroke, 0 outside, an aa-wide ramp between.
float strokeMaskConsole(float distance, float width, float aa) {
    return (1.0 - smoothstep(width, (width + aa), distance));
}

// Samples one glyph cell (SAME reconstruction as the character grid) at an arbitrary pixel-space cell origin/size —
// shared by the title band's static text and the scrollback/prompt grid below.
float3 SampleGlyphCoverage(int code, float2 cellLocal, float2 cellSize, int atlasCellW, int atlasCellH, float screenPxRange, float outlineBand) {
    int glyph = (code - (int)pc.misc.w);

    if ((glyph < 0) || (glyph >= GLYPH_COUNT)) {
        return float3(0.0, 0.0, 0.0);
    }

    float2 atlasCoord = float2(((cellLocal.x / cellSize.x) * atlasCellW), ((cellLocal.y / cellSize.y) * atlasCellH));
    float encoded = ConsoleSdfBilinear(glyph, atlasCoord, atlasCellW, atlasCellH);
    float coverage = saturate((screenPxRange * (encoded - 0.5)) + 0.5);
    float outline = saturate((screenPxRange * ((encoded - 0.5) + outlineBand)) + 0.5);

    return float3(coverage, outline, 0.0);
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));
    float3 color = sourceTexture.Sample(sourceSampler, uv).rgb;

    float2 local = (fragCoord.xy - pc.panel.xy);
    float panelW = pc.panel.z;
    float panelH = pc.panel.w;

    // A few px of slack around the exact rect so the rounded-corner mask and the hairline stroke (which straddles
    // the boundary) never clip — everything beyond this still passes the world through untouched, exactly like the
    // pre-token bare rectangle did.
    if ((local.x < -EDGE_AA) || (local.y < -EDGE_AA) || (local.x >= (panelW + EDGE_AA)) || (local.y >= (panelH + EDGE_AA))) {
        return float4(color, 1.0);
    }

    int cols = (int)pc.grid.x;
    int rows = (int)pc.grid.y;
    float cellW = pc.grid.z;
    float cellH = pc.grid.w;
    int atlasCellW = (int)pc.sdf.x;
    int atlasCellH = (int)pc.sdf.y;
    float screenPxRange = pc.sdf.z;
    float outlineBand = pc.sdf.w;

    // The panel tier (section 5, Tier 0 float): scrim.panel fill, rounded r.3, a 1px line.hair outline. The SDF is
    // evaluated once and reused for both the fill mask and the outline stroke.
    float2 panelHalf = (float2(panelW, panelH) * 0.5);
    float panelDist = sdRoundedBoxConsole((local - panelHalf), panelHalf, PANEL_RADIUS);
    float panelMask = (1.0 - smoothstep(0.0, EDGE_AA, panelDist));
    // A 1px hairline band straddling the rounded-rect boundary (line.hair — the default edge language).
    float panelOutline = strokeMaskConsole(abs(panelDist), 0.5, EDGE_AA);

    color = lerp(color, SCRIM_PANEL_RGB, (panelMask * SCRIM_PANEL_A));
    color = lerp(color, LINE_HAIR_RGB, (panelOutline * LINE_HAIR_A));

    // The title-bar band + its divider (the panel-header treatment): a static "CONSOLE" label in text.primary,
    // left-padded by the same padding rhythm, vertically centered in the band; a 1px line.soft divider below it.
    // The grabbed affordance (pc.state.y, set while the panel is being dragged by this band): the divider warms
    // toward accent and brightens — a subtle "you're holding this" cue, no new buffer, one of state's reserved lanes.
    float dividerDist = abs(local.y - TITLE_BAND_HEIGHT);

    if (dividerDist < (EDGE_AA + 0.5)) {
        float3 dividerColor = lerp(LINE_SOFT_RGB, ACCENT_RGB, (pc.state.y * 0.6));
        float dividerAlpha = lerp(LINE_SOFT_A, (LINE_SOFT_A * 4.0), pc.state.y);

        color = lerp(color, dividerColor, (strokeMaskConsole(dividerDist, 0.5, EDGE_AA) * dividerAlpha * panelMask));
    }

    if ((local.y >= 0.0) && (local.y < TITLE_BAND_HEIGHT) && (local.x >= 0.0) && (local.x < panelW)) {
        float2 titleOrigin = float2(PANEL_PAD, ((TITLE_BAND_HEIGHT - cellH) * 0.5));
        float2 titleLocal = (local - titleOrigin);
        int ti = (int)floor(titleLocal.x / cellW);

        if ((ti >= 0) && (ti < TITLE_CHAR_COUNT) && (titleLocal.y >= 0.0) && (titleLocal.y < cellH)) {
            float2 cellLocal = float2((titleLocal.x - (ti * cellW)), titleLocal.y);
            float3 sample = SampleGlyphCoverage(TITLE_CHARS[ti], cellLocal, float2(cellW, cellH), atlasCellW, atlasCellH, screenPxRange, outlineBand);

            color = lerp(color, float3(0.0, 0.01, 0.015), (sample.y * 0.85));
            color = lerp(color, TEXT_PRIMARY_RGB, sample.x);
        }
    }

    // The content grid origin: below the title band + divider, inset by the padding rhythm on every side.
    float2 contentOrigin = float2(PANEL_PAD, (TITLE_BAND_HEIGHT + PANEL_PAD));
    float2 contentLocal = (local - contentOrigin);

    // The input caret (section 5 caret tokens: accent core + accent.quiet outward falloff glow — an SDF distance
    // falloff band, NOT a blur), blinking per caret.blink (steps(1): pc.state.x is a hard 0/1, no fade). Evaluated
    // for every pixel near the caret cell (its glow legitimately bleeds past the cell into neighbors), drawn BEFORE
    // the glyph so a typed character rendered over it stays legible.
    if (pc.state.x > 0.5) {
        float2 caretCenter = (contentOrigin + (float2((pc.misc.x + 0.5), (pc.misc.y + 0.5)) * float2(cellW, cellH)));
        float2 caretLocal = (local - caretCenter);
        float caretDist = sdRoundedBoxConsole(caretLocal, (float2(cellW, cellH) * 0.5), 2.0);
        float caretCore = (1.0 - smoothstep(-1.0, 0.0, caretDist));
        float caretGlow = (ACCENT_QUIET_A * saturate(1.0 - (max(caretDist, 0.0) / BLOOM_HALO_BLUR)));

        color = lerp(color, ACCENT_RGB, max(caretCore, caretGlow));
    }

    int col = (int)floor(contentLocal.x / cellW);
    int row = (int)floor(contentLocal.y / cellH);

    if ((col < 0) || (col >= cols) || (row < 0) || (row >= rows)) {
        return float4(color, 1.0);
    }

    int textOffset = (int)pc.misc.z;
    int firstChar = (int)pc.misc.w;
    uint code = consoleData[textOffset + (row * cols) + col];

    if ((code >= (uint)firstChar) && (code < (uint)(firstChar + GLYPH_COUNT))) {
        float2 cellLocal = float2((contentLocal.x - (col * cellW)), (contentLocal.y - (row * cellH)));
        float3 sample = SampleGlyphCoverage((int)code, cellLocal, float2(cellW, cellH), atlasCellW, atlasCellH, screenPxRange, outlineBand);
        // Text colors from the semantic roles: the bottom (prompt/input) row is phosphor — the console's ECHOED
        // INPUT LINE per the phosphor material rule; every other (scrollback) row is neutral text.primary.
        float3 rowColor = (((row == (rows - 1))) ? PHOSPHOR_RGB : TEXT_PRIMARY_RGB);

        color = lerp(color, float3(0.0, 0.01, 0.015), (sample.y * 0.85));
        color = lerp(color, rowColor, sample.x);
    }

    return float4(color, 1.0);
}
