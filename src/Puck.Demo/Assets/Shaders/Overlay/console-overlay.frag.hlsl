// Demo on-screen developer-console overlay (single-source HLSL; DXC -> SPIR-V for Vulkan). Sample the inner
// producer's render and draw the console panel on top: a translucent backing plus a fixed monospace grid of text.
// The glyph atlas AND the per-frame character grid both ride ONE storage buffer (the font's packed coverage bytes at
// the front, the cols*rows character codes after it), so the overlay needs no second texture — it keeps the proven
// single-combined-image-sampler + one-storage-buffer shape of the binding bar.
// KEEP IN SYNC with Puck.Demo.DevConsole.ConsoleOverlayNode (push-constant layout + buffer packing) and
// ConsoleGlyphFont (cell packing).
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

// data[0 .. textOffset) = font coverage, four bytes per uint (little-endian); data[textOffset ..] = the character grid
// (row-major, low byte = code point). Font pixel (x, y) of glyph g is byte (g*cellW*cellH + y*cellW + x).
[[vk::binding(1, 0)]] StructuredBuffer<uint> consoleData : register(t1);

struct ConsoleData {
    float4 panel;  // x, y, w, h  (pixels)
    float4 grid;   // cols, rows, cellW, cellH
    float4 style;  // textColor.rgb, panelAlpha
    float4 misc;   // cursorCol, cursorRow, textCellUintOffset, firstChar
};
[[vk::push_constant]] ConstantBuffer<ConsoleData> pc;

// The atlas holds printable ASCII 0x20..0x7E (95 glyphs).
#define GLYPH_COUNT 95

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));
    float3 color = sourceTexture.Sample(sourceSampler, uv).rgb;

    float2 local = (fragCoord.xy - pc.panel.xy);

    if ((local.x < 0.0) || (local.y < 0.0) || (local.x >= pc.panel.z) || (local.y >= pc.panel.w)) {
        return float4(color, 1.0);
    }

    int cols = (int)pc.grid.x;
    int rows = (int)pc.grid.y;
    int cellW = (int)pc.grid.z;
    int cellH = (int)pc.grid.w;
    float3 textColor = pc.style.xyz;
    float panelAlpha = pc.style.w;

    // The panel backing: a dark translucent slab under the text.
    color = lerp(color, float3(0.03, 0.04, 0.055), panelAlpha);

    int col = (int)(local.x / cellW);
    int row = (int)(local.y / cellH);

    if ((col >= cols) || (row >= rows)) {
        return float4(color, 1.0);
    }

    // The input caret: a soft block on the caret cell (drawn under the glyph so a character over it stays legible).
    if ((col == (int)pc.misc.x) && (row == (int)pc.misc.y)) {
        color = lerp(color, textColor, 0.30);
    }

    int textOffset = (int)pc.misc.z;
    int firstChar = (int)pc.misc.w;
    uint code = consoleData[textOffset + (row * cols) + col];

    if ((code >= (uint)firstChar) && (code < (uint)(firstChar + GLYPH_COUNT))) {
        int localX = ((int)local.x - (col * cellW));
        int localY = ((int)local.y - (row * cellH));
        int glyph = ((int)code - firstChar);
        int byteIndex = ((glyph * cellW * cellH) + (localY * cellW) + localX);
        uint word = consoleData[byteIndex >> 2];              // the font packs at buffer offset 0
        uint coverage = ((word >> (uint)((byteIndex & 3) * 8)) & 0xFFu);

        color = lerp(color, textColor, (coverage / 255.0));
    }

    return float4(color, 1.0);
}
