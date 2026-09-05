using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Compares packed tile-row rendering with a scalar memory-image model across map sizes, scroll,
/// flips, palette banks, transparency, mosaic, and the upper tile-address boundary.</summary>
internal sealed class PpuTextRowStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "ppu-text-row";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ReadOnlySpan<int> scroll = [0, 1, 7, 8, 249, 255, 256, 511];
        var checkedPixels = 0;
        for (var depth = 0; depth < 2; ++depth) {
            for (var size = 0; size < 4; ++size) {
                for (var characterBlock = 0; characterBlock < 4; ++characterBlock) {
                    foreach (var screenBlock in ((ReadOnlySpan<int>)[0, 31])) {
                        var scheduler = new AgbScheduler();
                        var ppu = new AgbPpu(scheduler: scheduler, interrupts: new AgbInterruptController());
                        var image = new byte[0x18000];
                        var palette = new ushort[256];
                        for (var offset = 0; offset < image.Length; ++offset) {
                            image[offset] = ((byte)((offset * 37 ^ (offset >> 3) ^ (offset >> 11)) & 255));
                        }
                        for (var block = 0; block < 4; ++block) {
                            for (var entry = 0; entry < 1024; ++entry) {
                                var tile = (entry * 13 + block * 67) & 1023;
                                var value = ((ushort)(tile | ((entry & 3) << 10) | (((entry / 4) & 15) << 12)));
                                BinaryPrimitives.WriteUInt16LittleEndian(
                                    destination: image.AsSpan(start: (screenBlock * 2048 + block * 2048 + entry * 2)), value: value);
                            }
                        }
                        for (var offset = 0; offset < image.Length; offset += 4) {
                            ppu.WriteVideo(address: (0x06000000u + ((uint)offset)), width: 4,
                                value: BinaryPrimitives.ReadUInt32LittleEndian(source: image.AsSpan(start: offset)));
                        }
                        for (var index = 0; index < palette.Length; ++index) {
                            palette[index] = ((ushort)((index * 109 ^ (index << 7)) & 0x7FFF));
                            ppu.WriteVideo(address: (0x05000000u + ((uint)index * 2u)), width: 2, value: palette[index]);
                        }
                        ppu.WriteRegister(offset: 0, value: 0x0100); // Mode 0, BG0 only.
                        ppu.WriteRegister(offset: 8, value: ((ushort)((size << 14) | (screenBlock << 8)
                            | (characterBlock << 2) | (depth << 7) | 0x40)));

                        for (var line = 0; line < 16; ++line) {
                            var horizontal = scroll[line % scroll.Length];
                            var vertical = scroll[(line * 3) % scroll.Length];
                            var mosaicX = (line < 12 ? 1 : (line % 3 + 2));
                            var mosaicY = (line % 4 + 1);
                            ppu.WriteRegister(offset: 0x10, value: ((ushort)horizontal));
                            ppu.WriteRegister(offset: 0x12, value: ((ushort)vertical));
                            ppu.WriteRegister(offset: 0x4C, value: ((ushort)(((mosaicY - 1) << 4) | (mosaicX - 1))));
                            // Change palette data between scanlines; the current line must see the new value.
                            palette[31] ^= 0x7FFF;
                            ppu.WriteVideo(address: 0x0500003Eu, width: 2, value: palette[31]);
                            scheduler.Advance(cycles: 1232);

                            for (var x = 0; x < 240; ++x) {
                                var expected = Pixel(image: image, palette: palette, depth: depth, size: size,
                                    characterBlock: characterBlock, screenBlock: screenBlock,
                                    x: (x - x % mosaicX + horizontal), y: (line - line % mosaicY + vertical));
                                var actual = ppu.Framebuffer[line * 240 + x];
                                ++checkedPixels;
                                if (actual != expected) {
                                    return PostStageOutcome.Fail(detail: $"depth={depth}, size={size}, char={characterBlock}, "
                                        + $"screen={screenBlock}, line={line}, x={x}: {actual:X8} != {expected:X8}");
                                }
                            }
                        }
                    }
                }
            }
        }
        return PostStageOutcome.Pass(detail: $"{checkedPixels} pixels matched scalar tile sampling: 4/8 bpp, four map sizes, "
            + "all character bases, screen-block boundaries, flips, scrolling, palette writes, transparency and mosaic");
    }

    private static uint Pixel(byte[] image, ushort[] palette, int depth, int size, int characterBlock,
        int screenBlock, int x, int y) {
        var width = ((size % 2 == 0) ? 256 : 512);
        var height = ((size < 2) ? 256 : 512);
        x %= width;
        y %= height;
        var mapBlock = (y / 256) * (width / 256) + x / 256;
        var entryOffset = (screenBlock + mapBlock) * 2048 + ((y % 256 / 8) * 32 + x % 256 / 8) * 2;
        var entry = image[entryOffset] + image[entryOffset + 1] * 256;
        var tx = ((entry & 1024) != 0 ? 7 - x % 8 : x % 8);
        var ty = ((entry & 2048) != 0 ? 7 - y % 8 : y % 8);
        var tile = entry % 1024;
        var address = characterBlock * 16384 + tile * (depth == 1 ? 64 : 32)
            + ty * (depth == 1 ? 8 : 4) + (depth == 1 ? tx : tx / 2);
        var index = 0;
        if (address < image.Length) {
            index = image[address];
            if (depth == 0) {
                index = ((tx % 2 == 0) ? index % 16 : index / 16);
                if (index != 0) {
                    index += entry / 4096 * 16;
                }
            }
        }
        var color = palette[index];
        var output = 0xFF000000u;
        for (var channel = 0; channel < 3; ++channel) {
            var intensity = (color >> (5 * channel)) & 31;
            output |= ((uint)(intensity * 8 + intensity / 4)) << (channel * 8);
        }
        return output;
    }
}
