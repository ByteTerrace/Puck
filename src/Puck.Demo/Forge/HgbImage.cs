namespace Puck.Demo.Forge;

/// <summary>
/// The pure-C# image half of the ROM forge: it turns an RGBA8 buffer (as produced by
/// <see cref="Puck.SdfVm.SdfWorldEngine.RenderFrame"/>) into the Humble GamingBrick's brutal indexed-tile world — a
/// derived 4-colour palette, per-pixel colour indices, and deduplicated 8×8 2bpp tiles + a tilemap. Every byte layout
/// here is the INVERSE of the emulator's own decode (<c>experimental/Puck.HumbleGamingBrick/Ppu.cs</c>): a tile is 16
/// bytes (per row a low byte then a high byte, bit 7 = leftmost pixel, pixel = <c>(high&lt;&lt;1)|low</c>); a CGB colour
/// is 2 bytes little-endian RGB555 (<c>bbbbbgggggrrrrr</c>). No external image library — this is the whole point of the
/// pure-.NET forge.
/// </summary>
internal static class HgbImage {
    /// <summary>A straight 0..255-per-channel colour; the forge's working currency before it is crushed to RGB555.</summary>
    public readonly record struct Rgb(byte R, byte G, byte B) {
        public int DistanceSquaredTo(Rgb other) {
            var dr = (R - other.R);
            var dg = (G - other.G);
            var db = (B - other.B);

            return (((dr * dr) + (dg * dg)) + (db * db));
        }
    }

    /// <summary>Box-downsamples an RGBA8 image by an integer <paramref name="factor"/> on both axes (a supersample
    /// resolve): each destination pixel is the average of its <paramref name="factor"/>×<paramref name="factor"/> source
    /// block. Rendering the SDF scene larger and reducing here is how the crushed tiles get their anti-aliased edges.</summary>
    public static byte[] BoxReduce(byte[] rgba, int width, int height, int factor, out int outWidth, out int outHeight) {
        ArgumentNullException.ThrowIfNull(rgba);

        if ((factor < 1) || ((width % factor) != 0) || ((height % factor) != 0)) {
            throw new ArgumentException(message: $"The reduce factor {factor} must be >= 1 and divide the {width}x{height} source exactly.", paramName: nameof(factor));
        }

        outWidth = (width / factor);
        outHeight = (height / factor);

        var destination = new byte[((outWidth * outHeight) * 4)];
        var samples = (factor * factor);

        for (var y = 0; (y < outHeight); y++) {
            for (var x = 0; (x < outWidth); x++) {
                int r = 0, g = 0, b = 0, a = 0;

                for (var sy = 0; (sy < factor); sy++) {
                    for (var sx = 0; (sx < factor); sx++) {
                        var offset = (((((y * factor) + sy) * width) + ((x * factor) + sx)) * 4);

                        r += rgba[offset];
                        g += rgba[(offset + 1)];
                        b += rgba[(offset + 2)];
                        a += rgba[(offset + 3)];
                    }
                }

                var destinationOffset = (((y * outWidth) + x) * 4);

                destination[destinationOffset] = (byte)(r / samples);
                destination[(destinationOffset + 1)] = (byte)(g / samples);
                destination[(destinationOffset + 2)] = (byte)(b / samples);
                destination[(destinationOffset + 3)] = (byte)(a / samples);
            }
        }

        return destination;
    }

    /// <summary>Reads the RGB of a single pixel (ignores alpha).</summary>
    public static Rgb PixelAt(byte[] rgba, int width, int x, int y) {
        var offset = (((y * width) + x) * 4);

        return new Rgb(R: rgba[offset], G: rgba[(offset + 1)], B: rgba[(offset + 2)]);
    }

    /// <summary>Derives a <paramref name="count"/>-colour palette from the pixels via a small median-cut: the colour
    /// space is repeatedly split along its widest channel at the median until there are <paramref name="count"/> boxes,
    /// each box contributing its mean colour. Deriving the palette FROM the render (rather than a fixed guess) is what
    /// lets an arbitrary SDF scene land on a palette that actually fits it. <paramref name="seed"/> colours, when given,
    /// occupy the low palette slots verbatim (e.g. a sprite's transparent slot 0 = the sampled background) and only the
    /// remaining slots are median-cut from <paramref name="pixels"/>.</summary>
    public static Rgb[] MedianCutPalette(IReadOnlyList<Rgb> pixels, int count, IReadOnlyList<Rgb>? seed = null) {
        ArgumentNullException.ThrowIfNull(pixels);

        var palette = new List<Rgb>();

        if (seed is not null) {
            palette.AddRange(collection: seed);
        }

        var remaining = (count - palette.Count);

        if (remaining > 0) {
            var boxes = new List<List<Rgb>> { new(collection: pixels) };

            // Grow to `remaining` boxes by always splitting the one with the widest single-channel spread.
            while (boxes.Count < remaining) {
                var bestBox = -1;
                var bestRange = 0;

                for (var index = 0; (index < boxes.Count); index++) {
                    if (boxes[index].Count < 2) {
                        continue;
                    }

                    var range = ChannelRange(box: boxes[index], channel: out _);

                    if (range > bestRange) {
                        bestRange = range;
                        bestBox = index;
                    }
                }

                if (bestBox < 0) {
                    break; // No box can be split further (all uniform or singletons).
                }

                _ = ChannelRange(box: boxes[bestBox], channel: out var channel);

                var source = boxes[bestBox];

                source.Sort(comparison: (left, right) => ChannelValue(colour: left, channel: channel).CompareTo(value: ChannelValue(colour: right, channel: channel)));

                var mid = (source.Count / 2);

                boxes[bestBox] = source.GetRange(index: 0, count: mid);
                boxes.Add(item: source.GetRange(index: mid, count: (source.Count - mid)));
            }

            foreach (var box in boxes) {
                palette.Add(item: MeanColour(box: box));
            }
        }

        // Pad (a near-uniform region can yield fewer boxes than asked) so the palette is always exactly `count` long.
        while (palette.Count < count) {
            palette.Add(item: ((palette.Count > 0) ? palette[^1] : new Rgb(R: 0, G: 0, B: 0)));
        }

        return palette.GetRange(index: 0, count: count).ToArray();
    }

    /// <summary>Maps every pixel to the index of its nearest palette colour (squared RGB distance). For a sprite whose
    /// palette slot 0 is the sampled background colour, background pixels fall to index 0 naturally — which the OBJ
    /// hardware then renders as transparent.</summary>
    public static byte[] Quantize(byte[] rgba, int width, int height, Rgb[] palette) {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(palette);

        var indices = new byte[(width * height)];

        for (var pixel = 0; (pixel < indices.Length); pixel++) {
            var offset = (pixel * 4);
            var colour = new Rgb(R: rgba[offset], G: rgba[(offset + 1)], B: rgba[(offset + 2)]);
            var bestIndex = 0;
            var bestDistance = int.MaxValue;

            for (var candidate = 0; (candidate < palette.Length); candidate++) {
                var distance = colour.DistanceSquaredTo(other: palette[candidate]);

                if (distance < bestDistance) {
                    bestDistance = distance;
                    bestIndex = candidate;
                }
            }

            indices[pixel] = (byte)bestIndex;
        }

        return indices;
    }

    /// <summary>Encodes one 8×8 index tile (values 0..3, row-major) into the brick's 16-byte 2bpp form: for each of
    /// the 8 rows a low bitplane byte then a high bitplane byte, bit 7 = leftmost pixel. The exact inverse of the PPU's
    /// tile fetch.</summary>
    public static byte[] EncodeTile2bpp(ReadOnlySpan<byte> tileIndices) {
        if (tileIndices.Length != 64) {
            throw new ArgumentException(message: "A tile is 8x8 = 64 indices.", paramName: nameof(tileIndices));
        }

        var bytes = new byte[16];

        for (var row = 0; (row < 8); row++) {
            byte low = 0;
            byte high = 0;

            for (var column = 0; (column < 8); column++) {
                var value = tileIndices[((row * 8) + column)] & 0x03;
                var bit = (7 - column);

                low |= (byte)((value & 0x01) << bit);
                high |= (byte)(((value >> 1) & 0x01) << bit);
            }

            bytes[(row * 2)] = low;
            bytes[((row * 2) + 1)] = high;
        }

        return bytes;
    }

    /// <summary>Decodes one 16-byte 2bpp tile back into its 64 row-major indices — the exact inverse of
    /// <see cref="EncodeTile2bpp"/> (per row a low byte then a high byte, bit 7 = leftmost pixel).</summary>
    public static byte[] DecodeTile2bpp(ReadOnlySpan<byte> tileBytes) {
        if (tileBytes.Length != 16) {
            throw new ArgumentException(message: "A 2bpp tile is 16 bytes.", paramName: nameof(tileBytes));
        }

        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            var low = tileBytes[(row * 2)];
            var high = tileBytes[((row * 2) + 1)];

            for (var column = 0; (column < 8); column++) {
                var bit = (7 - column);

                indices[((row * 8) + column)] = (byte)(((low >> bit) & 0x01) | (((high >> bit) & 0x01) << 1));
            }
        }

        return indices;
    }

    /// <summary>Slices a WxH index image (multiples of 8) into 8×8 tiles, deduplicates identical tiles, and returns the
    /// unique tiles' concatenated 2bpp bytes plus a row-major tilemap of tile ids. Hard 4-colour quantization is what
    /// makes the dedup collapse a photographic render down to a handful of tiles that fit the VRAM budget.</summary>
    public static void SliceTilesDeduplicated(byte[] indices, int width, int height, out byte[] tileData, out byte[] tileIds, out int tileCount) {
        ArgumentNullException.ThrowIfNull(indices);

        if (((width % 8) != 0) || ((height % 8) != 0)) {
            throw new ArgumentException(message: $"The {width}x{height} image must tile into 8x8 blocks.", paramName: nameof(indices));
        }

        var tilesWide = (width / 8);
        var tilesHigh = (height / 8);
        var uniqueTiles = new List<byte[]>();
        var lookup = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        tileIds = new byte[(tilesWide * tilesHigh)];

        var tileIndices = new byte[64];

        for (var tileY = 0; (tileY < tilesHigh); tileY++) {
            for (var tileX = 0; (tileX < tilesWide); tileX++) {
                for (var row = 0; (row < 8); row++) {
                    for (var column = 0; (column < 8); column++) {
                        tileIndices[((row * 8) + column)] = indices[((((tileY * 8) + row) * width) + ((tileX * 8) + column))];
                    }
                }

                var encoded = EncodeTile2bpp(tileIndices: tileIndices);
                var key = Convert.ToHexString(inArray: encoded);

                if (!lookup.TryGetValue(key: key, value: out var id)) {
                    id = uniqueTiles.Count;
                    uniqueTiles.Add(item: encoded);
                    lookup[key] = id;
                }

                tileIds[((tileY * tilesWide) + tileX)] = (byte)id;
            }
        }

        tileCount = uniqueTiles.Count;
        tileData = new byte[(tileCount * 16)];

        for (var tile = 0; (tile < tileCount); tile++) {
            uniqueTiles[tile].CopyTo(array: tileData, index: (tile * 16));
        }
    }

    /// <summary>Encodes four palette colours into the CGB's 8-byte palette-RAM form: each colour a little-endian
    /// RGB555 pair (5 bits per channel; 8-bit channels are truncated by <c>&gt;&gt; 3</c>).</summary>
    public static byte[] EncodePalette(Rgb[] palette) {
        ArgumentNullException.ThrowIfNull(palette);

        if (palette.Length != 4) {
            throw new ArgumentException(message: "A brick palette is 4 colours.", paramName: nameof(palette));
        }

        var bytes = new byte[8];

        for (var colour = 0; (colour < 4); colour++) {
            var r = (palette[colour].R >> 3);
            var g = (palette[colour].G >> 3);
            var b = (palette[colour].B >> 3);
            var rgb555 = r | (g << 5) | (b << 10);

            bytes[(colour * 2)] = (byte)(rgb555 & 0xFF);
            bytes[((colour * 2) + 1)] = (byte)((rgb555 >> 8) & 0xFF);
        }

        return bytes;
    }

    private static int ChannelRange(List<Rgb> box, out int channel) {
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;

        foreach (var colour in box) {
            minR = Math.Min(val1: minR, val2: colour.R);
            minG = Math.Min(val1: minG, val2: colour.G);
            minB = Math.Min(val1: minB, val2: colour.B);
            maxR = Math.Max(val1: maxR, val2: colour.R);
            maxG = Math.Max(val1: maxG, val2: colour.G);
            maxB = Math.Max(val1: maxB, val2: colour.B);
        }

        var rangeR = (maxR - minR);
        var rangeG = (maxG - minG);
        var rangeB = (maxB - minB);

        if ((rangeG >= rangeR) && (rangeG >= rangeB)) {
            channel = 1;

            return rangeG;
        }

        if ((rangeB >= rangeR) && (rangeB >= rangeG)) {
            channel = 2;

            return rangeB;
        }

        channel = 0;

        return rangeR;
    }
    private static int ChannelValue(Rgb colour, int channel) =>
        (channel switch {
            0 => colour.R,
            1 => colour.G,
            _ => colour.B,
        });
    private static Rgb MeanColour(List<Rgb> box) {
        if (box.Count == 0) {
            return new Rgb(R: 0, G: 0, B: 0);
        }

        long r = 0, g = 0, b = 0;

        foreach (var colour in box) {
            r += colour.R;
            g += colour.G;
            b += colour.B;
        }

        return new Rgb(R: (byte)(r / box.Count), G: (byte)(g / box.Count), B: (byte)(b / box.Count));
    }
}
