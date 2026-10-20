// The shared overlay decode toolkit: the word loader over the raw storage buffer, the design-token-block accessors,
// and the SDF glyph-reconstruction trio every overlay surface shades text with.
//
// BUFFER SHAPE: a ByteAddressBuffer, the raw buffer a pass group binds, so both backends address it in bytes. All
// OFFSETS stay in 32-bit words (the C# packers' unit); OverlayWord() turns a word index into its byte address.
//
// TOKEN BLOCK — the slab Puck.Overlays.OverlayTokenBlock uploads at buffer word 0: one RGBA float4 per color role
// (role r IS uint4 element r), then the geometry scalars. KEEP IN SYNC with OverlayTokenBlock.cs — that file and
// the two accessors here are the ONE layout contract.
//
// GLYPH DECODE — extracted from the proven per-surface copies with NO math changed: the pack is one RGBA texel per
// word (little-endian R|G|B|A), each channel encoded 0.5 + d/range; reconstruction is per-channel manual bilinear +
// MEDIAN-OF-3 + a screenPxRange coverage ramp, with a darker outline band from the SAME field at zero extra taps.
// This file declares no resources — a caller passes its own buffer in by name (DXC lowers a resource-typed parameter
// to identical code as an inlined global). The median reconstruction and the coverage ramp are Puck.Shaders' shared
// msdf.hlsli.
#ifndef PUCK_OVERLAY_COMMON_HLSLI
#define PUCK_OVERLAY_COMMON_HLSLI

#include "../../../Puck.Shaders/Assets/Shaders/Shared/msdf.hlsli"

// KEEP IN SYNC with OverlayTokenBlock.RoleCount.
#define OVERLAY_TOKEN_ROLE_COUNT 21u

// KEEP IN SYNC with OverlayColorRole.Custom — the ring-element sentinel meaning "read a raw RGB triple from the
// record's own reserved words instead of indexing the token slab" (a marker's authored, possibly state-bound ring
// color; see overlay-unified.frag.hlsl's ring element decode).
#define OVERLAY_ROLE_CUSTOM 255u

// One 32-bit word at word index i.
uint OverlayWord(ByteAddressBuffer data, uint i) {
    return data.Load(i * 4u);
}

// One 32-bit word reinterpreted as float.
float OverlayFloat(ByteAddressBuffer data, uint i) {
    return asfloat(OverlayWord(data, i));
}

// A color role's RGBA from the token block (role r occupies uint4 element r exactly).
float4 OverlayTokenColor(ByteAddressBuffer data, uint role) {
    return asfloat(data.Load4(role * 16u));
}

// A geometry scalar from the token block (indexed by OverlayTokenBlock.Scalar).
float OverlayTokenScalar(ByteAddressBuffer data, uint index) {
    return OverlayFloat(data, ((OVERLAY_TOKEN_ROLE_COUNT * 4u) + index));
}

// Token-scalar indices — KEEP IN SYNC with OverlayTokenBlock.Scalar.
#define OVERLAY_SCALAR_RADIUS_1 0u
#define OVERLAY_SCALAR_RADIUS_2 1u
#define OVERLAY_SCALAR_RADIUS_3 2u
#define OVERLAY_SCALAR_EDGE_HAIRLINE 3u
#define OVERLAY_SCALAR_RING_STATUS 4u
#define OVERLAY_SCALAR_BLOOM_HALO_BLUR 5u
#define OVERLAY_SCALAR_BLOOM_RING_A 6u
#define OVERLAY_SCALAR_BLOOM_HALO_A 7u
#define OVERLAY_SCALAR_BLOOM_NEUTRAL_RING_A 8u
#define OVERLAY_SCALAR_BLOOM_NEUTRAL_HALO_A 9u
#define OVERLAY_SCALAR_EDGE_AA 10u
#define OVERLAY_SCALAR_CHIP_REST_OPACITY 11u
#define OVERLAY_SCALAR_GLYPH_STROKE 12u
#define OVERLAY_SCALAR_GLYPH_AA 13u
#define OVERLAY_SCALAR_REFERENCE_CHIP_HALF 14u

// Color-role indices — KEEP IN SYNC with OverlayColorRole.
#define OVERLAY_ROLE_TEXT_PRIMARY 0u
#define OVERLAY_ROLE_TEXT_DIM 1u
#define OVERLAY_ROLE_ACCENT 3u
#define OVERLAY_ROLE_POSITIVE 4u
#define OVERLAY_ROLE_WARNING 5u
#define OVERLAY_ROLE_DANGER 6u
#define OVERLAY_ROLE_ACCENT_INK 8u
#define OVERLAY_ROLE_SURFACE_RAISED 9u
#define OVERLAY_ROLE_ACCENT_QUIET 11u
#define OVERLAY_ROLE_SURFACE_BASE 13u
#define OVERLAY_ROLE_BADGE_DARK 14u
#define OVERLAY_ROLE_BADGE_LIGHT 15u
#define OVERLAY_ROLE_LINE_HAIR 16u
#define OVERLAY_ROLE_LINE_SOFT 17u
#define OVERLAY_ROLE_SCRIM_PANEL 18u
#define OVERLAY_ROLE_SCRIM_STRIP 19u
#define OVERLAY_ROLE_SCRIM_CHIP 20u

// One glyph SDF texel's RGB channels (edge-clamped): decode the packed RGBA word (each channel encoded =
// 0.5 + d/range). `atlasBase` is the buffer's starting word offset for the atlas pack.
float3 OverlaySdfTexel(ByteAddressBuffer data, uint atlasBase, int glyph, int2 texel, int cellW, int cellH) {
    texel = clamp(texel, int2(0, 0), int2((cellW - 1), (cellH - 1)));

    uint word = OverlayWord(data, atlasBase + (uint)((glyph * cellW * cellH) + (texel.y * cellW) + texel.x));

    return (float3(float(word & 0xFFu), float((word >> 8u) & 0xFFu), float((word >> 16u) & 0xFFu)) * (1.0 / 255.0));
}

// Per-channel manual bilinear (four point taps + arithmetic lerp) then MEDIAN-OF-3 — the classic MSDF
// reconstruction, legitimate at shade time (only geometry marching bans the median). A replicated single-channel
// atlas medians to exactly its own value (bit-identical to an alpha-only decode); a true MTSDF atlas medians to
// sharp corners.
float OverlaySdfBilinear(ByteAddressBuffer data, uint atlasBase, int glyph, float2 atlasCoord, int cellW, int cellH) {
    float2 t = (atlasCoord - 0.5);
    int2 b = int2(floor(t));
    float2 f = (t - float2(b));
    float3 s00 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(0, 0)), cellW, cellH);
    float3 s10 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(1, 0)), cellW, cellH);
    float3 s01 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(0, 1)), cellW, cellH);
    float3 s11 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(1, 1)), cellW, cellH);

    return puckMsdfReconstruct(s00, s10, s01, s11, f);
}

// The pack's per-glyph metric table, just past the last glyph's cell — KEEP IN SYNC with OverlayGlyphSdfPack.MetricWords:
// per glyph, the pen origin's offset from the cell's left edge, then the advance, both float atlas texels. A glyph's
// cell is wider than its advance (it carries the distance field's padding), so a run places each cell at its pen
// position minus that offset and neighbouring cells overlap. An out-of-range glyph reads 0.
#define OVERLAY_GLYPH_METRIC_WORDS 2u

float OverlayGlyphMetric(ByteAddressBuffer data, uint atlasBase, int glyph, int glyphCount, int cellW, int cellH, uint word) {
    if ((glyph < 0) || (glyph >= glyphCount)) {
        return 0.0;
    }

    return OverlayFloat(data, (atlasBase + (uint)(glyphCount * cellW * cellH) + ((uint)glyph * OVERLAY_GLYPH_METRIC_WORDS) + word));
}

float OverlayGlyphOrigin(ByteAddressBuffer data, uint atlasBase, int glyph, int glyphCount, int cellW, int cellH) {
    return OverlayGlyphMetric(data, atlasBase, glyph, glyphCount, cellW, cellH, 0u);
}

float OverlayGlyphAdvance(ByteAddressBuffer data, uint atlasBase, int glyph, int glyphCount, int cellW, int cellH) {
    return OverlayGlyphMetric(data, atlasBase, glyph, glyphCount, cellW, cellH, 1u);
}

// Samples one glyph cell at an arbitrary pixel-space cell origin/size: x = fill coverage, y = the darker outline
// band's coverage (the floats-over-a-lit-world contrast toolkit, from the SAME field at zero extra taps).
// `screenPxRange` is the caller's own distanceRange(texels) x screen-px-per-texel; `glyphCount` bounds-checks a
// resolved-but-out-of-range glyph index to blank (zero coverage) rather than wrapping into the next glyph's cell.
float2 SampleGlyphCoverage(ByteAddressBuffer data, uint atlasBase, int glyph, int glyphCount, float2 cellLocal, float2 cellSize, int atlasCellW, int atlasCellH, float screenPxRange, float outlineBand) {
    if ((glyph < 0) || (glyph >= glyphCount)) {
        return float2(0.0, 0.0);
    }

    float2 atlasCoord = float2(((cellLocal.x / cellSize.x) * atlasCellW), ((cellLocal.y / cellSize.y) * atlasCellH));
    float encoded = OverlaySdfBilinear(data, atlasBase, glyph, atlasCoord, atlasCellW, atlasCellH);
    float coverage = puckMsdfCoverage(screenPxRange, (encoded - 0.5));
    float outline = puckMsdfCoverage(screenPxRange, ((encoded - 0.5) + outlineBand));

    return float2(coverage, outline);
}

#endif
