using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbPpu {
    // One map lookup and one packed VRAM read per tile span, including clipped tiles at either edge. The row
    // exists only during this scanline's render callback: CPU/DMA writes between lines need no cache invalidation.
    // Horizontal mosaic retains the pixel sampler because a mosaic group can straddle tile boundaries.
    private void RenderTextRow(Span<int> destination, uint charBase, uint screenBase, bool is8Bpp, int size,
        int horizontalOffset, int widthMask, int tileY, int inTileY) {
        for (var x = 0; x < ScreenWidth;) {
            var px = (x + horizontalOffset) & widthMask;
            var inTileX = px & 7;
            var count = Math.Min(val1: (8 - inTileX), val2: (ScreenWidth - x));
            var entry = Vram16(offset: (screenBase + MapEntryOffset(tileX: (px >> 3), tileY: tileY, size: size)));
            var tileNumber = ((uint)(entry & 0x3FF));
            var ty = (((entry & 0x800) != 0) ? (7 - inTileY) : inTileY);
            var flipX = (entry & 0x400) != 0;
            var first = (flipX ? (7 - inTileX) : inTileX);
            var direction = (flipX ? -1 : 1);

            if (is8Bpp) {
                var address = charBase + tileNumber * 64u + ((uint)ty * 8u);
                // Rows are eight-byte aligned; a valid row starts at most eight bytes before VRAM's end.
                if (address < 0x18000u) {
                    var pixels = BinaryPrimitives.ReadUInt64LittleEndian(source: m_vram.AsSpan(start: ((int)address), length: 8));
                    var shift = first * 8;
                    for (var lane = 0; lane < count; ++lane, shift += direction * 8) {
                        var index = ((int)((pixels >> shift) & 255UL));
                        if (index != 0) {
                            destination[x + lane] = PaletteColor(index: index);
                        }
                    }
                }
            } else {
                var address = charBase + tileNumber * 32u + ((uint)ty * 4u);
                if (address < 0x18000u) {
                    var pixels = BinaryPrimitives.ReadUInt32LittleEndian(source: m_vram.AsSpan(start: ((int)address), length: 4));
                    var palette = (entry >> 12) * 16;
                    var shift = first * 4;
                    for (var lane = 0; lane < count; ++lane, shift += direction * 4) {
                        var index = ((int)((pixels >> shift) & 15u));
                        if (index != 0) {
                            destination[x + lane] = PaletteColor(index: (palette + index));
                        }
                    }
                }
            }
            x += count;
        }
    }
}
