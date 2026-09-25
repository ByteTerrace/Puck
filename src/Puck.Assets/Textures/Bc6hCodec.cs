namespace Puck.Assets.Textures;

/// <summary>
/// The BC6H unsigned-float block: three half-precision channels of a 4x4 block in sixteen bytes, least significant bit
/// first (Direct3D 11's BC6H_UF16 format). A channel is carried as the bits of a non-negative half, which the format
/// quantizes, interpolates and finishes as integers: an endpoint of <c>n</c> bits unquantizes to
/// <c>((e &lt;&lt; 16) + 0x8000) &gt;&gt; n</c> (0 and the largest code to 0 and 0xFFFF, and 16-bit endpoints as
/// they are), texels interpolate as <c>((64 - w) u0 + w u1 + 32) &gt;&gt; 6</c>, and the result finishes as
/// <c>(v 31) &gt;&gt; 6</c>, a half no larger than 65504.
/// <para>The decoder reads every mode exactly. The one-region modes are mode 11 (two 10-bit endpoints) and the
/// transformed modes 12, 13 and 14 (an 11-, 12- or 16-bit base and a 9-, 8- or 4-bit signed delta), each with sixteen
/// 4-bit weights. The two-region modes 1 to 10 split the block by one of the first 32 BC7 two-subset partitions
/// (<see cref="Bc7Codec.SubsetOf"/>) into two regions of two endpoints each, with 3-bit weights and the anchors at texel
/// 0 and the partition's second-subset anchor; modes 1 to 9 store the second, third and fourth endpoints as signed
/// deltas from the first, wrapped at the endpoint bits, and mode 10 stores four 6-bit endpoints. Their fields are
/// scattered over the block as the format's mode descriptions lay them out. Reserved modes decode to zero, as the format
/// defines.</para>
/// <para>The encoder is integer arithmetic over half bits, plus scalar double addition, multiplication and division in
/// a written order for its least-squares refit, so its bytes are the same on every machine. A negative or NaN input
/// clamps to zero and one above 65504 to 65504. It fits each one-region mode's endpoints to the block's bounding box and
/// refits them twice by least squares, measures every candidate's squared error in half bits by decoding it, and keeps
/// the smallest. It then tries the two-region modes 1 to 10 in that order over the <see cref="PartitionCandidates"/>
/// partitions whose regions vary least, and keeps one only when it decodes strictly nearer, so the earlier candidate
/// keeps a tie. A block of one value takes mode 14 and round-trips exactly.</para>
/// </summary>
public static partial class Bc6hCodec {
    /// <summary>The bytes of one block.</summary>
    public const int BlockBytes = 16;
    /// <summary>The largest half the unsigned format holds, 65504, as bits.</summary>
    public const ushort MaximumHalf = 0x7BFF;

    private static readonly byte[] Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    // The one-region modes: 5-bit code, endpoint bits, delta bits (0 for untransformed).
    private static readonly (int Code, int Bits, int Delta)[] Modes = [(0x03, 10, 0), (0x07, 11, 9), (0x0B, 12, 8), (0x0F, 16, 4)];

    /// <summary>Returns the half bits a non-negative float input stores as in this format: zero for a negative value,
    /// negative zero or NaN, and <see cref="MaximumHalf"/> for anything larger.</summary>
    /// <param name="half">The half's bits.</param>
    /// <returns>The clamped bits.</returns>
    public static int Clamp(ushort half) => (((half & 0x8000) != 0)
        ? 0
        : Math.Min(val1: half, val2: MaximumHalf));
    /// <summary>Decodes one block.</summary>
    /// <param name="block">The block's sixteen bytes.</param>
    /// <param name="rgb">The destination: sixteen texels of three half bits each, texel <c>(x, y)</c> at
    /// <c>3 (4 y + x)</c>.</param>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<ushort> rgb) {
        if (TwoRegionModeOf(block: block) is { } twoRegion) {
            DecodeTwoRegion(block: block, mode: twoRegion, rgb: rgb);
            return;
        }

        var bits = new BlockBits(block: block);
        var mode = OneRegionModeOf(block: block);

        if (mode < 0) {
            rgb[..48].Clear();
            return;
        }

        _ = bits.Read(count: 5);

        var (_, endpointBits, deltaBits) = Modes[mode];
        Span<int> e0 = stackalloc int[3];
        Span<int> e1 = stackalloc int[3];

        for (var channel = 0; (channel < 3); channel++) {
            e0[channel] = bits.Read(count: 10);
        }

        for (var channel = 0; (channel < 3); channel++) {
            e1[channel] = bits.Read(count: ((deltaBits == 0) ? 10 : deltaBits));

            // The base's bits above ten follow its channel's delta, highest first.
            for (var bit = (endpointBits - 1); (bit >= 10); bit--) {
                bits.ReadInto(bit: bit, value: ref e0[channel]);
            }
        }

        if (deltaBits != 0) {
            var mask = ((1 << endpointBits) - 1);

            for (var channel = 0; (channel < 3); channel++) {
                e1[channel] = (e0[channel] + SignExtend(bits: deltaBits, value: e1[channel])) & mask;
            }
        }

        Span<int> indices = stackalloc int[16];

        indices[0] = bits.Read(count: 3);

        for (var texel = 1; (texel < 16); texel++) {
            indices[texel] = bits.Read(count: 4);
        }

        for (var channel = 0; (channel < 3); channel++) {
            var u0 = Unquantize(bits: endpointBits, value: e0[channel]);
            var u1 = Unquantize(bits: endpointBits, value: e1[channel]);

            for (var texel = 0; (texel < 16); texel++) {
                rgb[((texel * 3) + channel)] = ((ushort)Finish(value: Interpolate(u0: u0, u1: u1, weight: Weights[indices[texel]])));
            }
        }
    }
    /// <summary>Encodes one block.</summary>
    /// <param name="rgb">The sixteen source texels of three half bits each, texel <c>(x, y)</c> at
    /// <c>3 (4 y + x)</c>.</param>
    /// <param name="block">The destination for the block's sixteen bytes.</param>
    public static void EncodeBlock(ReadOnlySpan<ushort> rgb, Span<byte> block) {
        Span<int> values = stackalloc int[48];
        var uniform = true;

        for (var index = 0; (index < 48); index++) {
            values[index] = Clamp(half: rgb[index]);
            uniform &= (values[index] == values[(index % 3)]);
        }

        if (uniform) {
            Span<int> q = stackalloc int[3];

            for (var channel = 0; (channel < 3); channel++) {
                // The smallest 16-bit endpoint that finishes to the value: 31 q lands in [64 h, 64 h + 63].
                q[channel] = (((64 * values[channel]) + 30) / 31);
            }

            Span<int> zeros = stackalloc int[16];

            Write(block: block, deltaBits: 4, e0: q, e1: q, endpointBits: 16, indices: zeros, mode: 3);
            return;
        }

        Span<double> low = stackalloc double[3];
        Span<double> high = stackalloc double[3];
        Span<int> q0 = stackalloc int[3];
        Span<int> q1 = stackalloc int[3];
        Span<int> indices = stackalloc int[16];
        Span<byte> candidate = stackalloc byte[BlockBytes];
        var bestError = long.MaxValue;

        for (var mode = 0; (mode < Modes.Length); mode++) {
            var (_, endpointBits, deltaBits) = Modes[mode];

            EndpointFit.BoundingBox(channels: 3, high: high, low: low, members: EndpointFit.AllTexels, stride: 3, texels: values);

            for (var pass = 0; (pass < 3); pass++) {
                for (var channel = 0; (channel < 3); channel++) {
                    q0[channel] = Quantize(bits: endpointBits, value: low[channel]);
                    q1[channel] = Quantize(bits: endpointBits, value: high[channel]);
                }

                FitIndices(bits: endpointBits, indices: indices, q0: q0, q1: q1, values: values);

                if (indices[0] >= 8) {
                    for (var texel = 0; (texel < 16); texel++) {
                        indices[texel] = (15 - indices[texel]);
                    }

                    for (var channel = 0; (channel < 3); channel++) {
                        (q0[channel], q1[channel]) = (q1[channel], q0[channel]);
                    }
                }

                if (Fits(deltaBits: deltaBits, q0: q0, q1: q1)) {
                    Write(block: candidate, deltaBits: deltaBits, e0: q0, e1: q1, endpointBits: endpointBits, indices: indices, mode: mode);

                    var error = Error(block: candidate, values: values);

                    if (error < bestError) {
                        bestError = error;
                        candidate.CopyTo(destination: block);
                    }
                }

                if (!EndpointFit.LeastSquares(channels: 3, high: high, indices: indices, low: low, members: EndpointFit.AllTexels, stride: 3, texels: values, weights: Weights)) {
                    break;
                }
            }
        }

        EncodeTwoRegion(best: block, bestError: bestError, values: values);
    }
    /// <summary>Returns the mode of a block, numbered as Direct3D numbers them: 1 to 10 for the two-region modes, 11 to 14
    /// for the one-region modes, and 0 for a reserved mode code.</summary>
    /// <param name="block">The block's sixteen bytes.</param>
    /// <returns>The mode, 0 to 14.</returns>
    public static int ModeOf(ReadOnlySpan<byte> block) {
        if (TwoRegionModeOf(block: block) is { } twoRegion) {
            return twoRegion.Number;
        }

        var mode = OneRegionModeOf(block: block);

        return ((mode < 0) ? 0 : (11 + mode));
    }

    // The index of a block's one-region mode in Modes, or -1 when its five-bit code names none.
    private static int OneRegionModeOf(ReadOnlySpan<byte> block) {
        var code = block[0] & 0x1F;

        for (var mode = 0; (mode < Modes.Length); mode++) {
            if (Modes[mode].Code == code) {
                return mode;
            }
        }

        return -1;
    }
    private static int SignExtend(int value, int bits) =>
        ((value << (32 - bits)) >> (32 - bits));
    private static int Unquantize(int value, int bits) {
        if (bits >= 15) {
            return value;
        }

        if (value == 0) {
            return 0;
        }

        return ((value == ((1 << bits) - 1)) ? 0xFFFF : (((value << 16) + 0x8000) >> bits));
    }
    private static int Interpolate(int u0, int u1, int weight) =>
        (((((64 - weight) * u0) + (weight * u1)) + 32) >> 6);
    private static int Finish(int value) =>
        ((value * 31) >> 6);
    // The endpoint of `bits` bits that finishes nearest `value` (half bits), the smaller winning a tie.
    private static int Quantize(double value, int bits) {
        var top = ((1 << bits) - 1);
        var estimate = Math.Clamp(
            max: top,
            min: 0,
            value: ((int)Math.Floor(d: ((((value * 64.0) / 31.0) * (1 << bits)) / 65536.0)))
        );
        var best = -1;
        var bestError = double.MaxValue;

        for (var candidate = Math.Max(val1: 0, val2: (estimate - 1)); (candidate <= Math.Min(val1: top, val2: (estimate + 2))); candidate++) {
            var error = Math.Abs(value: (Finish(value: Unquantize(bits: bits, value: candidate)) - value));

            if (error < bestError) {
                best = candidate;
                bestError = error;
            }
        }

        return best;
    }
    private static bool Fits(int deltaBits, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1) {
        if (deltaBits == 0) {
            return true;
        }

        for (var channel = 0; (channel < 3); channel++) {
            var delta = (q1[channel] - q0[channel]);

            if ((delta < -(1 << (deltaBits - 1))) || (delta >= (1 << (deltaBits - 1)))) {
                return false;
            }
        }

        return true;
    }
    private static void FitIndices(ReadOnlySpan<int> values, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, int bits, Span<int> indices) {
        Span<int> u0 = stackalloc int[3];
        Span<int> u1 = stackalloc int[3];

        for (var channel = 0; (channel < 3); channel++) {
            u0[channel] = Unquantize(bits: bits, value: q0[channel]);
            u1[channel] = Unquantize(bits: bits, value: q1[channel]);
        }

        for (var texel = 0; (texel < 16); texel++) {
            var best = 0;
            var bestError = long.MaxValue;

            for (var index = 0; (index < 16); index++) {
                var error = 0L;

                for (var channel = 0; (channel < 3); channel++) {
                    long difference = (Finish(value: Interpolate(u0: u0[channel], u1: u1[channel], weight: Weights[index])) - values[((texel * 3) + channel)]);

                    error += (difference * difference);
                }

                if (error < bestError) {
                    best = index;
                    bestError = error;
                }
            }

            indices[texel] = best;
        }
    }
    private static long Error(ReadOnlySpan<byte> block, ReadOnlySpan<int> values) {
        Span<ushort> decoded = stackalloc ushort[48];
        var error = 0L;

        DecodeBlock(block: block, rgb: decoded);

        for (var index = 0; (index < 48); index++) {
            long difference = (decoded[index] - values[index]);

            error += (difference * difference);
        }

        return error;
    }
    private static void Write(Span<byte> block, int mode, int endpointBits, int deltaBits, ReadOnlySpan<int> e0, ReadOnlySpan<int> e1, ReadOnlySpan<int> indices) {
        var bits = new BlockBits();
        var transformed = (mode != 0);

        bits.Write(count: 5, value: Modes[mode].Code);

        for (var channel = 0; (channel < 3); channel++) {
            bits.Write(count: 10, value: e0[channel]);
        }

        for (var channel = 0; (channel < 3); channel++) {
            bits.Write(
                count: (transformed ? deltaBits : 10),
                value: (transformed ? (e1[channel] - e0[channel]) : e1[channel])
            );

            for (var bit = (endpointBits - 1); (bit >= 10); bit--) {
                bits.WriteBit(bit: bit, value: e0[channel]);
            }
        }

        bits.Write(count: 3, value: indices[0]);

        for (var texel = 1; (texel < 16); texel++) {
            bits.Write(count: 4, value: indices[texel]);
        }

        bits.CopyTo(block: block);
    }
}
