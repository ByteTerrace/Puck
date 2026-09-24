namespace Puck.Assets.Textures;

/// <summary>
/// The BC7 block: four unsigned-normalized 8-bit channels of a 4x4 block in sixteen bytes. The block's lowest set bit
/// names its mode; the fields follow least significant bit first (Direct3D 11's BC7 format). A block with no mode bit in
/// its first byte decodes to transparent black.
/// <para>The decoder reads the three single-subset modes, 4, 5 and 6, exactly as the format defines them: endpoints
/// expanded by bit replication or a parity bit, interpolated as <c>((64 - w) e0 + w e1 + 32) &gt;&gt; 6</c>, and channel
/// rotation applied last. It refuses a partitioned mode (0, 1, 2, 3 and 7), which the encoder never writes.</para>
/// <para>The encoder writes mode 6 (one RGBA endpoint pair with parity bits, sixteen weights) or mode 5 (a color pair
/// with four weights and an independent alpha pair with four), whichever decodes nearer the source by squared error. It
/// fits endpoints to the block's bounding box, oriented by the sign of each channel's covariance with the widest one,
/// then refits them twice by least squares over the chosen weights, and tries every parity-bit pair. A block of one
/// color takes an exact mode-5 encoding. The fit's only floating point is scalar double addition, multiplication and
/// division in a written order, so the bytes are the same on every machine.</para>
/// </summary>
public static class Bc7Codec {
    /// <summary>The bytes of one block.</summary>
    public const int BlockBytes = 16;

    private static readonly byte[] Weights2 = [0, 21, 43, 64];
    private static readonly byte[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly byte[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Returns the mode of a block: the index of the lowest set bit of its first byte, or 8 when that byte is
    /// zero.</summary>
    /// <param name="block">The block's sixteen bytes.</param>
    /// <returns>The mode, 0 to 8.</returns>
    public static int ModeOf(ReadOnlySpan<byte> block) =>
        ((block[0] == 0) ? 8 : System.Numerics.BitOperations.TrailingZeroCount(value: block[0]));
    /// <summary>Decodes one block.</summary>
    /// <param name="block">The block's sixteen bytes.</param>
    /// <param name="rgba">The destination: sixteen texels of four channels, texel <c>(x, y)</c> at
    /// <c>4 (4 y + x)</c>.</param>
    /// <exception cref="NotSupportedException">The block is in a partitioned mode (0, 1, 2, 3 or 7).</exception>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgba) {
        var mode = ModeOf(block: block);
        var bits = new BlockBits(block: block);
        Span<int> e0 = stackalloc int[4];
        Span<int> e1 = stackalloc int[4];
        Span<int> colorIndices = stackalloc int[16];
        Span<int> alphaIndices = stackalloc int[16];
        byte[] colorWeights, alphaWeights;
        var rotation = 0;

        switch (mode) {
            case 4: {
                    _ = bits.Read(count: 5);
                    rotation = bits.Read(count: 2);

                    var selector = bits.Read(count: 1);

                    ReadEndpoints(bits: ref bits, channels: 3, count: 5, e0: e0, e1: e1);
                    e0[3] = bits.Read(count: 6);
                    e1[3] = bits.Read(count: 6);

                    for (var channel = 0; (channel < 3); channel++) {
                        e0[channel] = Expand(bits: 5, value: e0[channel]);
                        e1[channel] = Expand(bits: 5, value: e1[channel]);
                    }

                    e0[3] = Expand(bits: 6, value: e0[3]);
                    e1[3] = Expand(bits: 6, value: e1[3]);

                    var twoBit = ((selector == 0) ? colorIndices : alphaIndices);
                    var threeBit = ((selector == 0) ? alphaIndices : colorIndices);

                    ReadIndices(bits: ref bits, count: 2, indices: twoBit);
                    ReadIndices(bits: ref bits, count: 3, indices: threeBit);
                    (colorWeights, alphaWeights) = ((selector == 0) ? (Weights2, Weights3) : (Weights3, Weights2));
                    break;
                }
            case 5:
                _ = bits.Read(count: 6);
                rotation = bits.Read(count: 2);
                ReadEndpoints(bits: ref bits, channels: 3, count: 7, e0: e0, e1: e1);
                e0[3] = bits.Read(count: 8);
                e1[3] = bits.Read(count: 8);

                for (var channel = 0; (channel < 3); channel++) {
                    e0[channel] = Expand(bits: 7, value: e0[channel]);
                    e1[channel] = Expand(bits: 7, value: e1[channel]);
                }

                ReadIndices(bits: ref bits, count: 2, indices: colorIndices);
                ReadIndices(bits: ref bits, count: 2, indices: alphaIndices);
                (colorWeights, alphaWeights) = (Weights2, Weights2);
                break;
            case 6: {
                    _ = bits.Read(count: 7);
                    ReadEndpoints(bits: ref bits, channels: 4, count: 7, e0: e0, e1: e1);

                    var p0 = bits.Read(count: 1);
                    var p1 = bits.Read(count: 1);

                    for (var channel = 0; (channel < 4); channel++) {
                        e0[channel] = (e0[channel] << 1) | p0;
                        e1[channel] = (e1[channel] << 1) | p1;
                    }

                    ReadIndices(bits: ref bits, count: 4, indices: colorIndices);
                    colorIndices.CopyTo(destination: alphaIndices);
                    (colorWeights, alphaWeights) = (Weights4, Weights4);
                    break;
                }
            case 8:
                rgba[..64].Clear();
                return;
            default:
                throw new NotSupportedException(message: $"BC7 mode {mode} is partitioned; this decoder reads modes 4, 5 and 6.");
        }

        for (var texel = 0; (texel < 16); texel++) {
            var at = (texel * 4);
            var colorWeight = colorWeights[colorIndices[texel]];
            var alphaWeight = alphaWeights[alphaIndices[texel]];

            for (var channel = 0; (channel < 3); channel++) {
                rgba[(at + channel)] = ((byte)Interpolate(e0: e0[channel], e1: e1[channel], weight: colorWeight));
            }

            rgba[(at + 3)] = ((byte)Interpolate(e0: e0[3], e1: e1[3], weight: alphaWeight));

            if (rotation != 0) {
                (rgba[(at + 3)], rgba[((at + rotation) - 1)]) = (rgba[((at + rotation) - 1)], rgba[(at + 3)]);
            }
        }
    }
    /// <summary>Encodes one block.</summary>
    /// <param name="rgba">The sixteen source texels of four channels, texel <c>(x, y)</c> at <c>4 (4 y + x)</c>.</param>
    /// <param name="block">The destination for the block's sixteen bytes.</param>
    public static void EncodeBlock(ReadOnlySpan<byte> rgba, Span<byte> block) {
        if (IsUniform(rgba: rgba)) {
            EncodeUniform(block: block, rgba: rgba);
            return;
        }

        Span<byte> six = stackalloc byte[BlockBytes];
        Span<byte> five = stackalloc byte[BlockBytes];
        Span<int> values = stackalloc int[64];

        for (var index = 0; (index < 64); index++) {
            values[index] = rgba[index];
        }

        EncodeMode6(block: six, rgba: rgba, values: values);
        EncodeMode5(block: five, rgba: rgba, values: values);

        var sixError = Error(block: six, rgba: rgba);
        var fiveError = Error(block: five, rgba: rgba);

        ((sixError <= fiveError) ? six : five).CopyTo(destination: block);
    }

    private static bool IsUniform(ReadOnlySpan<byte> rgba) {
        for (var texel = 1; (texel < 16); texel++) {
            if (!rgba.Slice(length: 4, start: (texel * 4)).SequenceEqual(other: rgba[..4])) {
                return false;
            }
        }

        return true;
    }
    private static int Expand(int value, int bits) =>
        (value << (8 - bits)) | (value >> ((2 * bits) - 8));
    private static int Interpolate(int e0, int e1, int weight) =>
        (((((64 - weight) * e0) + (weight * e1)) + 32) >> 6);
    private static void ReadEndpoints(ref BlockBits bits, int channels, int count, Span<int> e0, Span<int> e1) {
        for (var channel = 0; (channel < channels); channel++) {
            e0[channel] = bits.Read(count: count);
            e1[channel] = bits.Read(count: count);
        }
    }
    // Texel 0 is the anchor: its index's top bit is implied zero.
    private static void ReadIndices(ref BlockBits bits, int count, Span<int> indices) {
        indices[0] = bits.Read(count: (count - 1));

        for (var texel = 1; (texel < 16); texel++) {
            indices[texel] = bits.Read(count: count);
        }
    }
    private static void WriteIndices(ref BlockBits bits, int count, ReadOnlySpan<int> indices) {
        bits.Write(count: (count - 1), value: indices[0]);

        for (var texel = 1; (texel < 16); texel++) {
            bits.Write(count: count, value: indices[texel]);
        }
    }
    private static long Error(ReadOnlySpan<byte> block, ReadOnlySpan<byte> rgba) {
        Span<byte> decoded = stackalloc byte[64];
        var error = 0L;

        DecodeBlock(block: block, rgba: decoded);

        for (var index = 0; (index < 64); index++) {
            var difference = (decoded[index] - rgba[index]);

            error += (difference * difference);
        }

        return error;
    }
    // A block of one color: mode 5 with every color index 1 (weight 21), each color channel's endpoint pair the one
    // whose interpolation is nearest the channel's value, and the alpha pair the value itself.
    private static void EncodeUniform(ReadOnlySpan<byte> rgba, Span<byte> block) {
        var bits = new BlockBits();
        var table = UniformTable.Pairs;

        bits.Write(count: 6, value: (1 << 5));
        bits.Write(count: 2, value: 0);

        for (var channel = 0; (channel < 3); channel++) {
            var (q0, q1) = table[rgba[channel]];

            bits.Write(count: 7, value: q0);
            bits.Write(count: 7, value: q1);
        }

        bits.Write(count: 8, value: rgba[3]);
        bits.Write(count: 8, value: rgba[3]);

        Span<int> ones = stackalloc int[16];
        Span<int> zeros = stackalloc int[16];

        ones.Fill(value: 1);
        WriteIndices(bits: ref bits, count: 2, indices: ones);
        WriteIndices(bits: ref bits, count: 2, indices: zeros);
        bits.CopyTo(block: block);
    }

    // For each 8-bit value, the 7-bit endpoint pair whose mode-5 interpolation at weight 21 is nearest it, the first in
    // (q0, q1) order winning a tie. Every value is reached exactly (a law holds the table to that).
    private static class UniformTable {
        public static readonly (int Q0, int Q1)[] Pairs = Compute();

        private static (int Q0, int Q1)[] Compute() {
            var pairs = new (int Q0, int Q1)[256];
            var errors = new int[256];

            errors.AsSpan().Fill(value: int.MaxValue);

            for (var q0 = 0; (q0 < 128); q0++) {
                for (var q1 = 0; (q1 < 128); q1++) {
                    var value = Interpolate(e0: Expand(bits: 7, value: q0), e1: Expand(bits: 7, value: q1), weight: Weights2[1]);

                    if (errors[value] != 0) {
                        errors[value] = 0;
                        pairs[value] = (q0, q1);
                    }
                }
            }

            for (var value = 0; (value < 256); value++) {
                if (errors[value] != 0) {
                    throw new InvalidOperationException(message: $"BC7 mode 5 reaches no uniform encoding of {value}.");
                }
            }

            return pairs;
        }
    }

    private static void EncodeMode6(ReadOnlySpan<byte> rgba, ReadOnlySpan<int> values, Span<byte> block) {
        Span<double> low = stackalloc double[4];
        Span<double> high = stackalloc double[4];
        Span<int> indices = stackalloc int[16];
        Span<int> bestIndices = stackalloc int[16];
        Span<int> q0 = stackalloc int[4];
        Span<int> q1 = stackalloc int[4];
        Span<int> bestQ0 = stackalloc int[4];
        Span<int> bestQ1 = stackalloc int[4];
        var bestError = long.MaxValue;
        var bestP0 = 0;
        var bestP1 = 0;

        EndpointFit.BoundingBox(channels: 4, high: high, low: low, stride: 4, texels: values);

        for (var pass = 0; (pass < 3); pass++) {
            for (var p = 0; (p < 4); p++) {
                var p0 = p & 1;
                var p1 = (p >> 1);

                for (var channel = 0; (channel < 4); channel++) {
                    q0[channel] = QuantizeParity(parity: p0, value: low[channel]);
                    q1[channel] = QuantizeParity(parity: p1, value: high[channel]);
                }

                var error = FitIndices(
                    channels: 4,
                    indices: indices,
                    p0: p0,
                    p1: p1,
                    q0: q0,
                    q1: q1,
                    rgba: rgba,
                    weights: Weights4
                );

                if (error < bestError) {
                    bestError = error;
                    bestP0 = p0;
                    bestP1 = p1;
                    q0.CopyTo(destination: bestQ0);
                    q1.CopyTo(destination: bestQ1);
                    indices.CopyTo(destination: bestIndices);
                }
            }

            if (!EndpointFit.LeastSquares(channels: 4, high: high, indices: bestIndices, low: low, stride: 4, texels: values, weights: Weights4)) {
                break;
            }
        }

        if (bestIndices[0] >= 8) {
            Swap(indices: bestIndices, last: 15, q0: bestQ0, q1: bestQ1);
            (bestP0, bestP1) = (bestP1, bestP0);
        }

        var bits = new BlockBits();

        bits.Write(count: 7, value: (1 << 6));

        for (var channel = 0; (channel < 4); channel++) {
            bits.Write(count: 7, value: bestQ0[channel]);
            bits.Write(count: 7, value: bestQ1[channel]);
        }

        bits.Write(count: 1, value: bestP0);
        bits.Write(count: 1, value: bestP1);
        WriteIndices(bits: ref bits, count: 4, indices: bestIndices);
        bits.CopyTo(block: block);
    }
    private static void EncodeMode5(ReadOnlySpan<byte> rgba, ReadOnlySpan<int> values, Span<byte> block) {
        Span<double> low = stackalloc double[4];
        Span<double> high = stackalloc double[4];
        Span<int> indices = stackalloc int[16];
        Span<int> bestIndices = stackalloc int[16];
        Span<int> q0 = stackalloc int[4];
        Span<int> q1 = stackalloc int[4];
        Span<int> bestQ0 = stackalloc int[4];
        Span<int> bestQ1 = stackalloc int[4];
        var bestError = long.MaxValue;

        EndpointFit.BoundingBox(channels: 3, high: high, low: low, stride: 4, texels: values);

        for (var pass = 0; (pass < 3); pass++) {
            for (var channel = 0; (channel < 3); channel++) {
                q0[channel] = Quantize7(value: low[channel]);
                q1[channel] = Quantize7(value: high[channel]);
            }

            var error = FitIndices(
                channels: 3,
                indices: indices,
                p0: -1,
                p1: -1,
                q0: q0,
                q1: q1,
                rgba: rgba,
                weights: Weights2
            );

            if (error < bestError) {
                bestError = error;
                q0.CopyTo(destination: bestQ0);
                q1.CopyTo(destination: bestQ1);
                indices.CopyTo(destination: bestIndices);
            }

            if (!EndpointFit.LeastSquares(channels: 3, high: high, indices: bestIndices, low: low, stride: 4, texels: values, weights: Weights2)) {
                break;
            }
        }

        if (bestIndices[0] >= 2) {
            Swap(indices: bestIndices, last: 3, q0: bestQ0, q1: bestQ1);
        }

        // Alpha: its extremes as exact 8-bit endpoints, each texel's nearest weight.
        Span<int> alpha = stackalloc int[16];
        int alphaLow = 255, alphaHigh = 0;

        for (var texel = 0; (texel < 16); texel++) {
            alphaLow = Math.Min(val1: alphaLow, val2: rgba[((texel * 4) + 3)]);
            alphaHigh = Math.Max(val1: alphaHigh, val2: rgba[((texel * 4) + 3)]);
        }

        for (var texel = 0; (texel < 16); texel++) {
            var bestAlpha = int.MaxValue;

            for (var index = 0; (index < 4); index++) {
                var difference = Math.Abs(value: (Interpolate(e0: alphaLow, e1: alphaHigh, weight: Weights2[index]) - rgba[((texel * 4) + 3)]));

                if (difference < bestAlpha) {
                    bestAlpha = difference;
                    alpha[texel] = index;
                }
            }
        }

        if (alpha[0] >= 2) {
            (alphaLow, alphaHigh) = (alphaHigh, alphaLow);

            for (var texel = 0; (texel < 16); texel++) {
                alpha[texel] = (3 - alpha[texel]);
            }
        }

        var bits = new BlockBits();

        bits.Write(count: 6, value: (1 << 5));
        bits.Write(count: 2, value: 0);

        for (var channel = 0; (channel < 3); channel++) {
            bits.Write(count: 7, value: bestQ0[channel]);
            bits.Write(count: 7, value: bestQ1[channel]);
        }

        bits.Write(count: 8, value: alphaLow);
        bits.Write(count: 8, value: alphaHigh);
        WriteIndices(bits: ref bits, count: 2, indices: bestIndices);
        WriteIndices(bits: ref bits, count: 2, indices: alpha);
        bits.CopyTo(block: block);
    }
    private static void Swap(Span<int> indices, int last, Span<int> q0, Span<int> q1) {
        for (var texel = 0; (texel < 16); texel++) {
            indices[texel] = (last - indices[texel]);
        }

        for (var channel = 0; (channel < q0.Length); channel++) {
            (q0[channel], q1[channel]) = (q1[channel], q0[channel]);
        }
    }
    // The 7-bit value whose endpoint with parity `parity` is nearest `value`.
    private static int QuantizeParity(double value, int parity) =>
        Math.Clamp(max: 127, min: 0, value: ((int)Math.Floor(d: (((value - parity) * 0.5) + 0.5))));
    // The 7-bit value whose replicated 8-bit endpoint is nearest `value`.
    private static int Quantize7(double value) {
        var estimate = Math.Clamp(max: 127, min: 0, value: ((int)Math.Floor(d: (((value * 127.0) / 255.0) + 0.5))));
        var best = estimate;

        for (var candidate = Math.Max(val1: 0, val2: (estimate - 1)); (candidate <= Math.Min(val1: 127, val2: (estimate + 1))); candidate++) {
            if (Math.Abs(value: (Expand(bits: 7, value: candidate) - value)) < Math.Abs(value: (Expand(bits: 7, value: best) - value))) {
                best = candidate;
            }
        }

        return best;
    }
    // Mode 6 appends a parity bit to a 7-bit endpoint; mode 5 (parity -1) replicates its top bit.
    private static int Endpoint(int q, int parity) =>
        ((parity < 0) ? Expand(bits: 7, value: q) : (q << 1) | parity);
    // Picks each texel's nearest weight over the first `channels` channels, the lowest index winning a tie, and
    // returns the squared error over those channels.
    private static long FitIndices(ReadOnlySpan<byte> rgba, int channels, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, int p0, int p1, ReadOnlySpan<byte> weights, Span<int> indices) {
        Span<int> a = stackalloc int[4];
        Span<int> b = stackalloc int[4];
        var error = 0L;

        for (var channel = 0; (channel < channels); channel++) {
            a[channel] = Endpoint(parity: p0, q: q0[channel]);
            b[channel] = Endpoint(parity: p1, q: q1[channel]);
        }

        for (var texel = 0; (texel < 16); texel++) {
            var best = 0;
            var bestError = int.MaxValue;

            for (var index = 0; (index < weights.Length); index++) {
                var squared = 0;

                for (var channel = 0; (channel < channels); channel++) {
                    var difference = (Interpolate(e0: a[channel], e1: b[channel], weight: weights[index]) - rgba[((texel * 4) + channel)]);

                    squared += (difference * difference);
                }

                if (squared < bestError) {
                    best = index;
                    bestError = squared;
                }
            }

            indices[texel] = best;
            error += bestError;
        }

        return error;
    }
}
