namespace Puck.Assets.Textures;

/// <summary>
/// The BC4 unsigned-normalized block: one channel of a 4x4 block in eight bytes. Bytes 0 and 1 are the endpoints
/// <c>e0</c> and <c>e1</c>; the next six hold sixteen 3-bit palette indices, texel <c>(x, y)</c> at bit
/// <c>3 (4 y + x)</c> of those bytes read little-endian. When <c>e0 &gt; e1</c> the palette is the two endpoints and six
/// values evenly between them; otherwise it is the endpoints, four values between them, then 0 and 255.
/// <para>The decoder is exact: an interpolated value is the nearest integer to its rational value
/// (<c>(6 e0 + e1) / 7</c> and so on), which a device's float decode agrees with to within half a code. The encoder is
/// integer arithmetic only, so its bytes are the same on every machine. It tries the eight-value palette over the
/// block's extremes and the six-value palette over its extremes other than 0 and 255, and keeps the one with the smaller
/// squared error: a block of one or two distinct values round-trips exactly, and any block decodes to within
/// <c>(max - min) / 10 + 1</c> of every source value (the eight-value palette alone is within
/// <c>(max - min) / 14 + 1</c>).</para>
/// </summary>
public static class Bc4Codec {
    /// <summary>The bytes of one block.</summary>
    public const int BlockBytes = 8;

    /// <summary>Writes the eight-entry palette the endpoints <paramref name="e0"/> and <paramref name="e1"/> select.</summary>
    /// <param name="e0">The first endpoint.</param>
    /// <param name="e1">The second endpoint.</param>
    /// <param name="palette">The destination, at least eight entries.</param>
    public static void Palette(byte e0, byte e1, Span<byte> palette) {
        palette[0] = e0;
        palette[1] = e1;

        if (e0 > e1) {
            for (var index = 2; (index < 8); index++) {
                palette[index] = ((byte)(((((8 - index) * e0) + ((index - 1) * e1)) + 3) / 7));
            }
        } else {
            for (var index = 2; (index < 6); index++) {
                palette[index] = ((byte)(((((6 - index) * e0) + ((index - 1) * e1)) + 2) / 5));
            }

            palette[6] = 0;
            palette[7] = 255;
        }
    }
    /// <summary>Decodes one block.</summary>
    /// <param name="block">The block's eight bytes.</param>
    /// <param name="values">The destination: sixteen values, texel <c>(x, y)</c> at <c>4 y + x</c>.</param>
    /// <param name="stride">The distance, in bytes, between consecutive values of <paramref name="values"/>.</param>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> values, int stride = 1) {
        Span<byte> palette = stackalloc byte[8];

        Palette(e0: block[0], e1: block[1], palette: palette);

        var indices = Indices(block: block);

        for (var texel = 0; (texel < 16); texel++) {
            values[(texel * stride)] = palette[((int)((indices >> (3 * texel)) & 7UL))];
        }
    }
    /// <summary>Encodes one block.</summary>
    /// <param name="values">The sixteen source values, texel <c>(x, y)</c> at <c>(4 y + x) stride</c>.</param>
    /// <param name="block">The destination for the block's eight bytes.</param>
    /// <param name="stride">The distance, in bytes, between consecutive values of <paramref name="values"/>.</param>
    public static void EncodeBlock(ReadOnlySpan<byte> values, Span<byte> block, int stride = 1) {
        Span<byte> source = stackalloc byte[16];
        int low = 255, high = 0, innerLow = 255, innerHigh = 0;

        for (var texel = 0; (texel < 16); texel++) {
            var value = values[(texel * stride)];

            source[texel] = value;
            low = Math.Min(val1: low, val2: value);
            high = Math.Max(val1: high, val2: value);

            if ((value != 0) && (value != 255)) {
                innerLow = Math.Min(val1: innerLow, val2: value);
                innerHigh = Math.Max(val1: innerHigh, val2: value);
            }
        }

        if (innerLow > innerHigh) {
            innerLow = innerHigh = 0;
        }

        // The eight-value palette needs e0 > e1; a block of one value takes the six-value palette with equal endpoints.
        var eightIndices = 0UL;
        var eightError = ((high > low)
            ? Fit(e0: ((byte)high), e1: ((byte)low), indices: out eightIndices, source: source)
            : long.MaxValue);
        var sixError = Fit(e0: ((byte)innerLow), e1: ((byte)innerHigh), indices: out var sixIndices, source: source);

        if (eightError <= sixError) {
            Write(block: block, e0: ((byte)high), e1: ((byte)low), indices: eightIndices);
        } else {
            Write(block: block, e0: ((byte)innerLow), e1: ((byte)innerHigh), indices: sixIndices);
        }
    }

    private static ulong Indices(ReadOnlySpan<byte> block) {
        var indices = 0UL;

        for (var index = 0; (index < 6); index++) {
            indices |= (((ulong)block[(2 + index)]) << (8 * index));
        }

        return indices;
    }
    // Picks each texel's nearest palette entry, the lowest index winning a tie, and returns the squared error.
    private static long Fit(byte e0, byte e1, ReadOnlySpan<byte> source, out ulong indices) {
        Span<byte> palette = stackalloc byte[8];
        var error = 0L;

        Palette(e0: e0, e1: e1, palette: palette);
        indices = 0UL;

        for (var texel = 0; (texel < 16); texel++) {
            var best = 0;
            var bestError = int.MaxValue;

            for (var entry = 0; (entry < 8); entry++) {
                var difference = (palette[entry] - source[texel]);
                var squared = (difference * difference);

                if (squared < bestError) {
                    best = entry;
                    bestError = squared;
                }
            }

            indices |= (((ulong)best) << (3 * texel));
            error += bestError;
        }

        return error;
    }
    private static void Write(Span<byte> block, byte e0, byte e1, ulong indices) {
        block[0] = e0;
        block[1] = e1;

        for (var index = 0; (index < 6); index++) {
            block[(2 + index)] = ((byte)(indices >> (8 * index)));
        }
    }
}
/// <summary>The BC5 unsigned-normalized block: two channels of a 4x4 block in sixteen bytes, the first channel's
/// <see cref="Bc4Codec"/> block followed by the second's. A unit normal stored in two channels (see
/// <see cref="OctahedralNormal"/>) reconstructs its third component from them.</summary>
public static class Bc5Codec {
    /// <summary>The bytes of one block.</summary>
    public const int BlockBytes = 16;

    /// <summary>Decodes one block.</summary>
    /// <param name="block">The block's sixteen bytes.</param>
    /// <param name="values">The destination: sixteen texels of two channels each, texel <c>(x, y)</c> at
    /// <c>2 (4 y + x)</c>.</param>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> values) {
        Bc4Codec.DecodeBlock(block: block, stride: 2, values: values);
        Bc4Codec.DecodeBlock(block: block[Bc4Codec.BlockBytes..], stride: 2, values: values[1..]);
    }
    /// <summary>Encodes one block.</summary>
    /// <param name="values">The sixteen source texels of two channels each, texel <c>(x, y)</c> at
    /// <c>2 (4 y + x)</c>.</param>
    /// <param name="block">The destination for the block's sixteen bytes.</param>
    public static void EncodeBlock(ReadOnlySpan<byte> values, Span<byte> block) {
        Bc4Codec.EncodeBlock(block: block, stride: 2, values: values);
        Bc4Codec.EncodeBlock(block: block[Bc4Codec.BlockBytes..], stride: 2, values: values[1..]);
    }
}
