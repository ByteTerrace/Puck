using System.Buffers.Binary;

namespace Puck.GamingBricks.Forge;

/// <summary>Encodes validated document graphics into native cartridge data.</summary>
public static class CartridgeGraphics {
    /// <summary>Encodes RGB555 colors or map entries as little-endian halfwords.</summary>
    /// <param name="values">Validated unsigned halfwords.</param>
    /// <returns>The encoded bytes.</returns>
    public static byte[] Halfwords(int[] values) {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; ++i) {
            BinaryPrimitives.WriteUInt16LittleEndian(destination: bytes.AsSpan(start: i * 2), value: checked((ushort)values[i]));
        }

        return bytes;
    }

    /// <summary>Encodes validated tiles as CGB planar 2bpp or AGB packed 4bpp.</summary>
    /// <param name="document">Validated cartridge source.</param>
    /// <returns>The contiguous native tile bank.</returns>
    public static byte[] Tiles(CartridgeDocument document) {
        var agb = document.Target == "agb";
        var bytes = new byte[document.Tiles.Length * (agb ? 32 : 16)];
        for (var tile = 0; tile < document.Tiles.Length; ++tile) {
            for (var y = 0; y < 8; ++y) {
                for (var x = 0; x < 8; ++x) {
                    var color = CartridgeValidation.Digit(digit: document.Tiles[tile].Pixels[y][x]);
                    if (agb) {
                        bytes[tile * 32 + y * 4 + x / 2] |= (byte)(color << ((x & 1) * 4));
                    } else {
                        bytes[tile * 16 + y * 2] |= (byte)((color & 1) << (7 - x));
                        bytes[tile * 16 + y * 2 + 1] |= (byte)((color >> 1) << (7 - x));
                    }
                }
            }
        }
        return bytes;
    }
}
