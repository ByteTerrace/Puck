using System.Buffers.Binary;

namespace Puck.GamingBricks.Forge;

/// <summary>Encodes validated document graphics into native cartridge data.</summary>
public static class CartridgeGraphics {
    /// <summary>
    /// Bakes a palette bank at every fade step, from untouched through fully the target colour. A machine with no
    /// blending hardware fades by republishing a whole bank, so the levels are computed here rather than on the
    /// cartridge, where per-channel arithmetic over every colour would be far too slow.
    /// </summary>
    /// <param name="bank">The palettes at rest.</param>
    /// <param name="steps">The number of steps; the returned table holds steps + 1 banks.</param>
    /// <param name="toWhite">Whether the fade runs toward white rather than black.</param>
    /// <returns>The packed banks, step zero first.</returns>
    public static byte[] FadeTable(int[][] bank, int steps, bool toWhite) {
        ArgumentNullException.ThrowIfNull(argument: bank);

        var colors = bank.Sum(selector: static palette => palette.Length);
        var bytes = new byte[(((steps + 1) * colors) * 2)];
        var cursor = 0;

        for (var step = 0; (step <= steps); ++step) {
            foreach (var palette in bank) {
                foreach (var color in palette) {
                    var faded = 0;

                    for (var channel = 0; (channel < 3); ++channel) {
                        var level = (color >> (channel * 5)) & 0x1F;
                        // Each channel walks toward the target in exact integer steps; 31 is full, 0 is none.
                        var target = (toWhite
                            ? 0x1F
                            : 0x00
                        );

                        faded |= ((level + (((target - level) * step) / steps)) << (channel * 5));
                    }

                    bytes[cursor++] = ((byte)(faded & 0xFF));
                    bytes[cursor++] = ((byte)((faded >> 8) & 0xFF));
                }
            }
        }

        return bytes;
    }
    /// <summary>
    /// Bakes the monochrome machine's three shade registers at every fade step: the background register, then the two
    /// object registers, from the same faded colours <see cref="FadeTable"/> produces for a colour machine.
    /// </summary>
    /// <param name="background">The background palettes at rest; only the first is shaded.</param>
    /// <param name="objects">The object palettes at rest; the first two are shaded, and a missing one shades as the first.</param>
    /// <param name="steps">The number of steps; the returned table holds steps + 1 register triples.</param>
    /// <param name="toWhite">Whether the fade runs toward white rather than black.</param>
    /// <returns>Three register bytes per step, step zero first.</returns>
    public static byte[] MonochromeFadeTable(int[][] background, int[][] objects, int steps, bool toWhite) {
        ArgumentNullException.ThrowIfNull(argument: background);
        ArgumentNullException.ThrowIfNull(argument: objects);

        var bank = new[] { background[0], objects[0], objects[Math.Min(val1: 1, val2: (objects.Length - 1))] };
        var faded = FadeTable(
            bank: bank,
            steps: steps,
            toWhite: toWhite
        );
        var bytes = new byte[((steps + 1) * bank.Length)];
        var cursor = 0;

        for (var register = 0; (register < bytes.Length); ++register) {
            var colors = new int[4];

            for (var index = 0; (index < 4); ++index) {
                colors[index] = (faded[cursor] | (faded[(cursor + 1)] << 8));
                cursor += 2;
            }

            bytes[register] = MonochromePalette(palette: colors);
        }

        return bytes;
    }
    /// <summary>
    /// Encodes a four-colour palette as the monochrome machine's shade register: two bits per colour index, index zero
    /// lowest, each the darkness of that colour from 0 (white) to 3 (black).
    /// </summary>
    /// <param name="palette">The four RGB555 colours.</param>
    /// <returns>The register byte.</returns>
    /// <remarks>Darkness is the colour's luma (ITU-R BT.601 weights over the five-bit channels) rounded to the nearest
    /// of four even steps, in integers so the same document always yields the same register.</remarks>
    public static byte MonochromePalette(int[] palette) {
        ArgumentNullException.ThrowIfNull(argument: palette);

        var register = 0;

        for (var index = 0; (index < 4); ++index) {
            var color = palette[index];
            var luma = ((((color & 0x1F) * 299) + (((color >> 5) & 0x1F) * 587)) + (((color >> 10) & 0x1F) * 114));
            // luma runs 0..31000; three even steps across it, rounded to the nearest.
            var lightness = (((luma * 3) + 15500) / 31000);

            register |= ((3 - lightness) << (index * 2));
        }

        return ((byte)register);
    }
    /// <summary>Encodes RGB555 colors or map entries as little-endian halfwords.</summary>
    /// <param name="values">Validated unsigned halfwords.</param>
    /// <returns>The encoded bytes.</returns>
    public static byte[] Halfwords(int[] values) {
        var bytes = new byte[(values.Length * 2)];

        for (var i = 0; (i < values.Length); ++i) {
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination: bytes.AsSpan(start: (i * 2)),
                value: checked((ushort)values[i])
            );
        }

        return bytes;
    }
    /// <summary>Flattens a palette bank into consecutive RGB555 halfwords, in selection order.</summary>
    /// <param name="bank">The palettes.</param>
    /// <returns>The packed bytes.</returns>
    public static byte[] PaletteBank(int[][] bank) {
        ArgumentNullException.ThrowIfNull(argument: bank);

        return Halfwords(values: [.. bank.SelectMany(selector: static palette => palette)]);
    }
    /// <summary>Encodes validated tiles as CGB planar 2bpp or AGB packed 4bpp.</summary>
    /// <param name="document">Validated cartridge source.</param>
    /// <returns>The contiguous native tile bank.</returns>
    public static byte[] Tiles(CartridgeDocument document) {
        var agb = (document.Target == "agb");
        var bytes = new byte[(document.Tiles.Length * (agb
            ? 32
            : 16))];

        for (var tile = 0; (tile < document.Tiles.Length); ++tile) {
            for (var y = 0; (y < 8); ++y) {
                for (var x = 0; (x < 8); ++x) {
                    var color = CartridgeValidation.Digit(digit: document.Tiles[tile].Pixels[y][x]);

                    if (agb) {
                        bytes[(((tile * 32) + (y * 4)) + (x / 2))] |= ((byte)(color << ((x & 1) * 4)));
                    } else {
                        bytes[((tile * 16) + (y * 2))] |= ((byte)((color & 1) << (7 - x)));
                        bytes[(((tile * 16) + (y * 2)) + 1)] |= ((byte)((color >> 1) << (7 - x)));
                    }
                }
            }
        }
        return bytes;
    }
    /// <summary>Encodes validated tiles one byte per pixel, the only depth a rotating background reads.</summary>
    /// <param name="document">Validated cartridge source.</param>
    /// <returns>The contiguous tile bank, 64 bytes per tile.</returns>
    public static byte[] TilesEightBit(CartridgeDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var bytes = new byte[(document.Tiles.Length * 64)];

        for (var tile = 0; (tile < document.Tiles.Length); ++tile) {
            for (var row = 0; (row < 8); ++row) {
                var pixels = document.Tiles[tile].Pixels[row];

                for (var column = 0; (column < 8); ++column) {
                    bytes[(((tile * 64) + (row * 8)) + column)] = ((byte)CartridgeValidation.Digit(digit: pixels[column]));
                }
            }
        }

        return bytes;
    }
}
