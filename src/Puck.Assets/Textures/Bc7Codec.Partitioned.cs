namespace Puck.Assets.Textures;

public static partial class Bc7Codec {
    /// <summary>How many partitions the encoder fits for each partitioned mode: the ones whose subsets vary least, by the
    /// exact sum of each subset's per-channel variance, the lower partition number winning a tie.</summary>
    public const int PartitionCandidates = 4;

    // The format's two-subset partitions: bit t is texel t's subset.
    private static readonly ushort[] Partitions2 = [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];
    // The format's three-subset partitions: bits 2t and 2t + 1 are texel t's subset.
    private static readonly uint[] Partitions3 = [
        0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8, 0xA5A50000, 0xA0A05050, 0x5555A0A0, 0x5A5A5050,
        0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090, 0x94949494, 0xA4A4A4A4, 0xA9A59450, 0x2A0A4250,
        0xA5945040, 0x0A425054, 0xA5A5A500, 0x55A0A0A0, 0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
        0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400, 0xA08585A0, 0xAA821414, 0x50A4A450, 0x6A5A0200,
        0xA9A58000, 0x5090A0A8, 0xA8A09050, 0x24242424, 0x00AA5500, 0x24924924, 0x24499224, 0x50A50A50,
        0x500AA550, 0xAAAA4444, 0x66660000, 0xA5A0A5A0, 0x50A050A0, 0x69286928, 0x44AAAA44, 0x66666600,
        0xAA444444, 0x54A854A8, 0x95809580, 0x96969600, 0xA85454A8, 0x80959580, 0xAA141414, 0x96960000,
        0xAAAA1414, 0xA05050A0, 0xA0A5A5A0, 0x96000000, 0x40804080, 0xA9A8A9A8, 0xAAAAAA44, 0x2A4A5254,
    ];
    // The anchor texel of a two-subset partition's second subset.
    private static readonly byte[] Anchors2 = [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];
    // The anchor texel of a three-subset partition's second subset.
    private static readonly byte[] Anchors3Second = [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];
    // The anchor texel of a three-subset partition's third subset.
    private static readonly byte[] Anchors3Third = [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];
    private static readonly PartitionedMode Mode0 = new(AlphaBits: 0, ColorBits: 4, IndexBits: 3, Mode: 0, Parity: EndpointParity.PerEndpoint, PartitionBits: 4, Subsets: 3);
    private static readonly PartitionedMode Mode1 = new(AlphaBits: 0, ColorBits: 6, IndexBits: 3, Mode: 1, Parity: EndpointParity.Shared, PartitionBits: 6, Subsets: 2);
    private static readonly PartitionedMode Mode2 = new(AlphaBits: 0, ColorBits: 5, IndexBits: 2, Mode: 2, Parity: EndpointParity.None, PartitionBits: 6, Subsets: 3);
    private static readonly PartitionedMode Mode3 = new(AlphaBits: 0, ColorBits: 7, IndexBits: 2, Mode: 3, Parity: EndpointParity.PerEndpoint, PartitionBits: 6, Subsets: 2);
    private static readonly PartitionedMode Mode7 = new(AlphaBits: 5, ColorBits: 5, IndexBits: 2, Mode: 7, Parity: EndpointParity.PerEndpoint, PartitionBits: 6, Subsets: 2);
    // The order the encoder tries them in, after modes 6 and 5.
    private static readonly PartitionedMode[] EncodedPartitionedModes = [Mode7, Mode3, Mode1, Mode2, Mode0];

    // How a partitioned mode stores parity bits: none, one below each endpoint, or one per subset below both of its
    // endpoints.
    private enum EndpointParity {
        None,
        PerEndpoint,
        Shared,
    }
    // One partitioned mode's field widths, as the format's mode table gives them.
    private sealed record PartitionedMode(int Mode, int Subsets, int PartitionBits, int ColorBits, int AlphaBits, EndpointParity Parity, int IndexBits) {
        public int Channels => ((AlphaBits > 0) ? 4 : 3);
        public int ParityOptions => Parity switch {
            EndpointParity.None => 1,
            EndpointParity.Shared => 2,
            _ => 4,
        };
        public int Partitions => (1 << PartitionBits);
        public byte[] Weights => ((IndexBits == 2) ? Weights2 : Weights3);

        public int BitsOf(int channel) =>
            ((channel < 3) ? ColorBits : AlphaBits);
        // A parity option's bits for a subset's two endpoints; -1 where the mode has none.
        public (int P0, int P1) ParityOf(int option) => Parity switch {
            EndpointParity.None => (-1, -1),
            EndpointParity.Shared => (option, option),
            _ => (option & 1, (option >> 1)),
        };
    }

    /// <summary>Returns the subset a texel belongs to under one of the format's partitions.</summary>
    /// <param name="subsets">The partition table: 2 or 3 subsets.</param>
    /// <param name="partition">The partition number, 0 to 63.</param>
    /// <param name="texel">The texel, <c>4 y + x</c>.</param>
    /// <returns>The subset, 0 to <paramref name="subsets"/> - 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="subsets"/> is neither 2 nor 3.</exception>
    public static int SubsetOf(int subsets, int partition, int texel) => subsets switch {
        2 => ((Partitions2[partition] >> texel) & 1),
        3 => ((int)((Partitions3[partition] >> (2 * texel)) & 3u)),
        _ => throw new ArgumentOutOfRangeException(actualValue: subsets, message: "A BC7 partition has 2 or 3 subsets.", paramName: nameof(subsets)),
    };
    /// <summary>Returns a subset's anchor texel under one of the format's partitions: the texel whose index is stored one
    /// bit short, its top bit implied zero. The first subset's anchor is texel 0.</summary>
    /// <param name="subsets">The partition table: 2 or 3 subsets.</param>
    /// <param name="partition">The partition number, 0 to 63.</param>
    /// <param name="subset">The subset, 0 to <paramref name="subsets"/> - 1.</param>
    /// <returns>The anchor texel, <c>4 y + x</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="subsets"/> is neither 2 nor 3.</exception>
    public static int AnchorOf(int subsets, int partition, int subset) => (subsets, subset) switch {
        (2 or 3, 0) => 0,
        (2, _) => Anchors2[partition],
        (3, 1) => Anchors3Second[partition],
        (3, _) => Anchors3Third[partition],
        _ => throw new ArgumentOutOfRangeException(actualValue: subsets, message: "A BC7 partition has 2 or 3 subsets.", paramName: nameof(subsets)),
    };
    /// <summary>Encodes one block in one partitioned mode and partition, as the encoder fits a candidate it tries.</summary>
    /// <param name="rgba">The sixteen source texels of four channels, texel <c>(x, y)</c> at <c>4 (4 y + x)</c>.</param>
    /// <param name="mode">The partitioned mode: 0, 1, 2, 3 or 7.</param>
    /// <param name="partition">The partition number: 0 to 15 for mode 0, 0 to 63 otherwise.</param>
    /// <param name="block">The destination for the block's sixteen bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not partitioned, or
    /// <paramref name="partition"/> is outside its range.</exception>
    public static void EncodePartitionedBlock(ReadOnlySpan<byte> rgba, int mode, int partition, Span<byte> block) {
        var partitioned = (PartitionedModeOf(mode: mode) ?? throw new ArgumentOutOfRangeException(actualValue: mode, message: "BC7 modes 0, 1, 2, 3 and 7 are partitioned.", paramName: nameof(mode)));

        ArgumentOutOfRangeException.ThrowIfNegative(value: partition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: partitioned.Partitions, value: partition);

        Span<int> values = stackalloc int[64];

        for (var index = 0; (index < 64); index++) {
            values[index] = rgba[index];
        }

        EncodePartition(block: block, mode: partitioned, partition: partition, rgba: rgba, values: values);
    }

    private static PartitionedMode? PartitionedModeOf(int mode) => mode switch {
        0 => Mode0,
        1 => Mode1,
        2 => Mode2,
        3 => Mode3,
        7 => Mode7,
        _ => null,
    };
    private static int MembersOf(int subsets, int partition, int subset) {
        var members = 0;

        for (var texel = 0; (texel < 16); texel++) {
            if (SubsetOf(partition: partition, subsets: subsets, texel: texel) == subset) {
                members |= (1 << texel);
            }
        }

        return members;
    }
    // A stored endpoint channel's 8-bit value: its bits with the parity bit below them where the mode has one, the top
    // bits replicated into the rest of the byte.
    private static int EndpointOf(int q, int bits, int parity) =>
        ((parity < 0) ? Expand(bits: bits, value: q) : Expand(bits: (bits + 1), value: (q << 1) | parity));
    // The `bits`-bit value whose endpoint with parity `parity` is nearest `value`, the lower value winning a tie.
    private static int Quantize(double value, int bits, int parity) {
        var precision = (bits + ((parity < 0) ? 0 : 1));
        var scaled = ((value * ((1 << precision) - 1)) / 255.0);
        var estimate = ((parity < 0) ? ((int)Math.Floor(d: (scaled + 0.5))) : ((int)Math.Floor(d: (((scaled - parity) * 0.5) + 0.5))));
        var top = ((1 << bits) - 1);
        var first = Math.Clamp(max: top, min: 0, value: (estimate - 2));
        var last = Math.Clamp(max: top, min: 0, value: (estimate + 2));
        var best = first;

        for (var candidate = (first + 1); (candidate <= last); candidate++) {
            if (Math.Abs(value: (EndpointOf(bits: bits, parity: parity, q: candidate) - value)) < Math.Abs(value: (EndpointOf(bits: bits, parity: parity, q: best) - value))) {
                best = candidate;
            }
        }

        return best;
    }
    private static void DecodePartitioned(ReadOnlySpan<byte> block, PartitionedMode mode, Span<byte> rgba) {
        var bits = new BlockBits(block: block);
        var endpoints = (mode.Subsets * 2);
        // Endpoint e's channel c at 4 e + c; a mode with no alpha bits holds alpha at 255.
        Span<int> e = stackalloc int[24];
        Span<int> parity = stackalloc int[6];

        e.Fill(value: 255);
        _ = bits.Read(count: (mode.Mode + 1));

        var partition = bits.Read(count: mode.PartitionBits);

        for (var channel = 0; (channel < mode.Channels); channel++) {
            for (var endpoint = 0; (endpoint < endpoints); endpoint++) {
                e[((endpoint * 4) + channel)] = bits.Read(count: mode.BitsOf(channel: channel));
            }
        }

        if (mode.Parity == EndpointParity.PerEndpoint) {
            for (var endpoint = 0; (endpoint < endpoints); endpoint++) {
                parity[endpoint] = bits.Read(count: 1);
            }
        } else if (mode.Parity == EndpointParity.Shared) {
            for (var subset = 0; (subset < mode.Subsets); subset++) {
                parity[(subset * 2)] = parity[((subset * 2) + 1)] = bits.Read(count: 1);
            }
        } else {
            parity.Fill(value: -1);
        }

        for (var endpoint = 0; (endpoint < endpoints); endpoint++) {
            for (var channel = 0; (channel < mode.Channels); channel++) {
                e[((endpoint * 4) + channel)] = EndpointOf(bits: mode.BitsOf(channel: channel), parity: parity[endpoint], q: e[((endpoint * 4) + channel)]);
            }
        }

        var weights = mode.Weights;

        for (var texel = 0; (texel < 16); texel++) {
            var subset = SubsetOf(partition: partition, subsets: mode.Subsets, texel: texel);
            var anchor = (texel == AnchorOf(partition: partition, subset: subset, subsets: mode.Subsets));
            var weight = weights[bits.Read(count: (mode.IndexBits - (anchor ? 1 : 0)))];

            for (var channel = 0; (channel < 4); channel++) {
                rgba[((texel * 4) + channel)] = ((byte)Interpolate(e0: e[((subset * 8) + channel)], e1: e[(((subset * 8) + 4) + channel)], weight: weight));
            }
        }
    }
    // Tries each partitioned mode over its best-ranked partitions and replaces `best` with any candidate that decodes
    // strictly nearer the source, so an earlier candidate keeps a tie.
    private static void EncodePartitioned(ReadOnlySpan<byte> rgba, ReadOnlySpan<int> values, Span<byte> best, long bestError) {
        var opaque = true;

        for (var texel = 0; (texel < 16); texel++) {
            opaque &= (rgba[((texel * 4) + 3)] == 255);
        }

        Span<byte> candidate = stackalloc byte[BlockBytes];
        Span<int> ranked = stackalloc int[PartitionCandidates];

        foreach (var mode in EncodedPartitionedModes) {
            // Nothing beats an exact candidate.
            if (bestError == 0L) {
                return;
            }

            if (
                (mode.Channels == 3) &&
                !opaque
            ) {
                continue;
            }

            var count = PartitionRanking.Rank(channels: mode.Channels, partitions: mode.Partitions, ranked: ranked, stride: 4, subsets: mode.Subsets, values: values);

            for (var rank = 0; (rank < count); rank++) {
                EncodePartition(block: candidate, mode: mode, partition: ranked[rank], rgba: rgba, values: values);

                var error = Error(block: candidate, rgba: rgba);

                if (error < bestError) {
                    bestError = error;
                    candidate.CopyTo(destination: best);
                }
            }
        }
    }
    private static void EncodePartition(ReadOnlySpan<byte> rgba, ReadOnlySpan<int> values, PartitionedMode mode, int partition, Span<byte> block) {
        // Subset s's endpoint channels at 4 s + c; its parity bits at s.
        Span<int> q0 = stackalloc int[12];
        Span<int> q1 = stackalloc int[12];
        Span<int> p0 = stackalloc int[3];
        Span<int> p1 = stackalloc int[3];
        Span<int> indices = stackalloc int[16];
        var last = ((1 << mode.IndexBits) - 1);

        for (var subset = 0; (subset < mode.Subsets); subset++) {
            var members = MembersOf(partition: partition, subset: subset, subsets: mode.Subsets);

            (p0[subset], p1[subset]) = FitSubset(
                indices: indices,
                members: members,
                mode: mode,
                q0: q0.Slice(length: 4, start: (subset * 4)),
                q1: q1.Slice(length: 4, start: (subset * 4)),
                rgba: rgba,
                values: values
            );

            // The anchor's index must have its top bit clear: reverse the subset's endpoints when it does not.
            if (indices[AnchorOf(partition: partition, subset: subset, subsets: mode.Subsets)] > (last >> 1)) {
                for (var texel = 0; (texel < 16); texel++) {
                    if (((members >> texel) & 1) != 0) {
                        indices[texel] = (last - indices[texel]);
                    }
                }

                for (var channel = 0; (channel < 4); channel++) {
                    (q0[((subset * 4) + channel)], q1[((subset * 4) + channel)]) = (q1[((subset * 4) + channel)], q0[((subset * 4) + channel)]);
                }

                (p0[subset], p1[subset]) = (p1[subset], p0[subset]);
            }
        }

        var bits = new BlockBits();

        bits.Write(count: (mode.Mode + 1), value: (1 << mode.Mode));
        bits.Write(count: mode.PartitionBits, value: partition);

        for (var channel = 0; (channel < mode.Channels); channel++) {
            for (var subset = 0; (subset < mode.Subsets); subset++) {
                bits.Write(count: mode.BitsOf(channel: channel), value: q0[((subset * 4) + channel)]);
                bits.Write(count: mode.BitsOf(channel: channel), value: q1[((subset * 4) + channel)]);
            }
        }

        for (var subset = 0; (subset < mode.Subsets); subset++) {
            if (mode.Parity == EndpointParity.PerEndpoint) {
                bits.Write(count: 1, value: p0[subset]);
                bits.Write(count: 1, value: p1[subset]);
            } else if (mode.Parity == EndpointParity.Shared) {
                bits.Write(count: 1, value: p0[subset]);
            }
        }

        for (var texel = 0; (texel < 16); texel++) {
            var subset = SubsetOf(partition: partition, subsets: mode.Subsets, texel: texel);
            var anchor = (texel == AnchorOf(partition: partition, subset: subset, subsets: mode.Subsets));

            bits.Write(count: (mode.IndexBits - (anchor ? 1 : 0)), value: indices[texel]);
        }

        bits.CopyTo(block: block);
    }
    // Fits one subset's endpoints and each member's index, as modes 5 and 6 fit a block: the bounding box, then two
    // least-squares refits, each quantized under every parity option. Returns the subset's parity bits.
    private static (int P0, int P1) FitSubset(ReadOnlySpan<byte> rgba, ReadOnlySpan<int> values, PartitionedMode mode, int members, Span<int> q0, Span<int> q1, Span<int> indices) {
        Span<double> low = stackalloc double[4];
        Span<double> high = stackalloc double[4];
        Span<int> trial = stackalloc int[16];
        Span<int> trialQ0 = stackalloc int[4];
        Span<int> trialQ1 = stackalloc int[4];
        var bestError = long.MaxValue;
        var bestParity = (P0: -1, P1: -1);

        EndpointFit.BoundingBox(channels: mode.Channels, high: high, low: low, members: members, stride: 4, texels: values);

        for (var pass = 0; (pass < 3); pass++) {
            for (var option = 0; (option < mode.ParityOptions); option++) {
                var (a, b) = mode.ParityOf(option: option);

                for (var channel = 0; (channel < mode.Channels); channel++) {
                    trialQ0[channel] = Quantize(bits: mode.BitsOf(channel: channel), parity: a, value: low[channel]);
                    trialQ1[channel] = Quantize(bits: mode.BitsOf(channel: channel), parity: b, value: high[channel]);
                }

                var error = FitSubsetIndices(indices: trial, members: members, mode: mode, p0: a, p1: b, q0: trialQ0, q1: trialQ1, rgba: rgba);

                if (error < bestError) {
                    bestError = error;
                    bestParity = (a, b);
                    trialQ0.CopyTo(destination: q0);
                    trialQ1.CopyTo(destination: q1);

                    for (var texel = 0; (texel < 16); texel++) {
                        if (((members >> texel) & 1) != 0) {
                            indices[texel] = trial[texel];
                        }
                    }
                }
            }

            if (!EndpointFit.LeastSquares(channels: mode.Channels, high: high, indices: indices, low: low, members: members, stride: 4, texels: values, weights: mode.Weights)) {
                break;
            }
        }

        return bestParity;
    }
    // Picks each member's nearest weight over the mode's channels, the lowest index winning a tie, and returns the
    // squared error over those channels.
    private static long FitSubsetIndices(ReadOnlySpan<byte> rgba, PartitionedMode mode, int members, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, int p0, int p1, Span<int> indices) {
        Span<int> a = stackalloc int[4];
        Span<int> b = stackalloc int[4];
        var weights = mode.Weights;
        var error = 0L;

        for (var channel = 0; (channel < mode.Channels); channel++) {
            a[channel] = EndpointOf(bits: mode.BitsOf(channel: channel), parity: p0, q: q0[channel]);
            b[channel] = EndpointOf(bits: mode.BitsOf(channel: channel), parity: p1, q: q1[channel]);
        }

        for (var texel = 0; (texel < 16); texel++) {
            if (((members >> texel) & 1) == 0) {
                continue;
            }

            var best = 0;
            var bestError = int.MaxValue;

            for (var index = 0; (index < weights.Length); index++) {
                var squared = 0;

                for (var channel = 0; (channel < mode.Channels); channel++) {
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
