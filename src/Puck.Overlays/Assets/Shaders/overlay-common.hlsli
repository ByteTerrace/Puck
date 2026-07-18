// The shared SDF-decode trio every overlay surface reconstructs a glyph with: the console panel, the binding bar,
// and the editor HUD all sample the SAME packed atlas — Puck.Overlays.OverlayGlyphSdfPack (C#) flattens it into a
// StructuredBuffer<uint>, one RGBA texel per uint, little-endian R|G|B|A, each channel encoded as 0.5 + d/range.
// Extracted from the three near-identical copies this collapses (Puck.Demo's console-overlay.frag.hlsl,
// ui-panels-overlay.frag.hlsl, binding-bar-overlay.frag.hlsl) with NO math changed — the decode is settled, proven
// code; only what differed between the three call sites (the buffer's binding/name, the atlas' base word offset,
// the cell dimensions, the glyph count) becomes a function argument here instead of a file-local global/macro.
//
// A caller's shader still declares its OWN StructuredBuffer<uint> at whatever binding its layout needs (this file
// declares no resources, no push constants, and includes nothing) and passes it in by name — HLSL/DXC resolves a
// resource-typed parameter at compile time (verified: DXC lowers this to identical SPIR-V/DXIL as an inlined
// global), so this costs nothing over the old copy-pasted globals while letting three different buffers share one
// definition.
#ifndef PUCK_OVERLAY_COMMON_HLSLI
#define PUCK_OVERLAY_COMMON_HLSLI

// One glyph SDF texel's RGB channels (edge-clamped): decode the packed RGBA word (each channel encoded =
// 0.5 + d/range). `atlasBase` is the buffer's starting word offset for the atlas pack (0 when packed at the
// buffer front, or a caller's own tail offset — e.g. the binding bar's slot-record region — otherwise).
float3 OverlaySdfTexel(StructuredBuffer<uint> data, uint atlasBase, int glyph, int2 texel, int cellW, int cellH) {
    texel = clamp(texel, int2(0, 0), int2((cellW - 1), (cellH - 1)));

    uint word = data[atlasBase + (uint)((glyph * cellW * cellH) + (texel.y * cellW) + texel.x)];

    return (float3(float(word & 0xFFu), float((word >> 8u) & 0xFFu), float((word >> 16u) & 0xFFu)) * (1.0 / 255.0));
}

// Per-channel manual bilinear (four point taps + arithmetic lerp) then MEDIAN-OF-3 — the classic MSDF
// reconstruction, legitimate at shade time (only geometry marching bans the median). A replicated single-channel
// atlas medians to exactly its own value (bit-identical to an alpha-only decode); a true MTSDF atlas medians to
// sharp corners.
float OverlaySdfBilinear(StructuredBuffer<uint> data, uint atlasBase, int glyph, float2 atlasCoord, int cellW, int cellH) {
    float2 t = (atlasCoord - 0.5);
    int2 b = int2(floor(t));
    float2 f = (t - float2(b));
    float3 s00 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(0, 0)), cellW, cellH);
    float3 s10 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(1, 0)), cellW, cellH);
    float3 s01 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(0, 1)), cellW, cellH);
    float3 s11 = OverlaySdfTexel(data, atlasBase, glyph, (b + int2(1, 1)), cellW, cellH);
    float3 s = lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y);

    return max(min(s.r, s.g), min(max(s.r, s.g), s.b));
}

// Samples one glyph cell at an arbitrary pixel-space cell origin/size: x = fill coverage, y = the darker outline
// band's coverage (the floats-over-a-lit-world contrast toolkit, from the SAME field at zero extra taps).
// `screenPxRange` is the caller's own distanceRange(texels) x screen-px-per-texel; `glyphCount` bounds-checks a
// resolved-but-out-of-range glyph index to blank (zero coverage) rather than wrapping into the next glyph's cell.
float2 SampleGlyphCoverage(StructuredBuffer<uint> data, uint atlasBase, int glyph, int glyphCount, float2 cellLocal, float2 cellSize, int atlasCellW, int atlasCellH, float screenPxRange, float outlineBand) {
    if ((glyph < 0) || (glyph >= glyphCount)) {
        return float2(0.0, 0.0);
    }

    float2 atlasCoord = float2(((cellLocal.x / cellSize.x) * atlasCellW), ((cellLocal.y / cellSize.y) * atlasCellH));
    float encoded = OverlaySdfBilinear(data, atlasBase, glyph, atlasCoord, atlasCellW, atlasCellH);
    float coverage = saturate((screenPxRange * (encoded - 0.5)) + 0.5);
    float outline = saturate((screenPxRange * ((encoded - 0.5) + outlineBand)) + 0.5);

    return float2(coverage, outline);
}

#endif
