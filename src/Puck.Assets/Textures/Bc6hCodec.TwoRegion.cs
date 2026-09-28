namespace Puck.Assets.Textures;

public static partial class Bc6hCodec {
    /// <summary>How many partitions the encoder fits for each two-region mode: the ones whose regions vary least, by the
    /// exact sum of each region's per-channel variance, the lower partition number winning a tie.</summary>
    public const int PartitionCandidates = 4;

    // A two-region block's header: the mode, endpoint and partition bits before its 46 index bits.
    private const int TwoRegionHeaderBits = 82;
    // A layout entry naming a partition bit is this plus the bit; one naming a mode bit is -1.
    private const int PartitionField = 256;

    // The two-region modes 1 to 10 in that order: the mode code and its width, whether endpoints 1 to 3 are deltas from
    // endpoint 0, and the format's description of the header, least significant bit first (a range hi..lo fills the
    // block from lo up; Xen is bit n of endpoint e's channel X).
    private static readonly TwoRegionMode[] TwoRegionModes = [
        new(code: 0x00, codeBits: 2, description: "M1..0, G24, B24, B34, R09..0, G09..0, B09..0, R14..0, G34, G23..0, G14..0, B30, G33..0, B14..0, B31, B23..0, R24..0, B32, R34..0, B33, PB4..0", number: 1, transformed: true),
        new(code: 0x01, codeBits: 2, description: "M1..0, G25, G34, G35, R06..0, B30, B31, B24, G06..0, B25, B32, G24, B06..0, B33, B35, B34, R15..0, G23..0, G15..0, G33..0, B15..0, B23..0, R25..0, R35..0, PB4..0", number: 2, transformed: true),
        new(code: 0x02, codeBits: 5, description: "M4..0, R09..0, G09..0, B09..0, R14..0, R010, G23..0, G13..0, G010, B30, G33..0, B13..0, B010, B31, B23..0, R24..0, B32, R34..0, B33, PB4..0", number: 3, transformed: true),
        new(code: 0x06, codeBits: 5, description: "M4..0, R09..0, G09..0, B09..0, R13..0, R010, G34, G23..0, G14..0, G010, G33..0, B13..0, B010, B31, B23..0, R23..0, B30, B32, R33..0, G24, B33, PB4..0", number: 4, transformed: true),
        new(code: 0x0A, codeBits: 5, description: "M4..0, R09..0, G09..0, B09..0, R13..0, R010, B24, G23..0, G13..0, G010, B30, G33..0, B14..0, B010, B23..0, R23..0, B31, B32, R33..0, B34, B33, PB4..0", number: 5, transformed: true),
        new(code: 0x0E, codeBits: 5, description: "M4..0, R08..0, B24, G08..0, G24, B08..0, B34, R14..0, G34, G23..0, G14..0, B30, G33..0, B14..0, B31, B23..0, R24..0, B32, R34..0, B33, PB4..0", number: 6, transformed: true),
        new(code: 0x12, codeBits: 5, description: "M4..0, R07..0, G34, B24, G07..0, B32, G24, B07..0, B33, B34, R15..0, G23..0, G14..0, B30, G33..0, B14..0, B31, B23..0, R25..0, R35..0, PB4..0", number: 7, transformed: true),
        new(code: 0x16, codeBits: 5, description: "M4..0, R07..0, B30, B24, G07..0, G25, G24, B07..0, G35, B34, R14..0, G34, G23..0, G15..0, G33..0, B14..0, B31, B23..0, R24..0, B32, R34..0, B33, PB4..0", number: 8, transformed: true),
        new(code: 0x1A, codeBits: 5, description: "M4..0, R07..0, B31, B24, G07..0, B25, G24, B07..0, B35, B34, R14..0, G34, G23..0, G14..0, B30, G33..0, B15..0, B23..0, R24..0, B32, R34..0, B33, PB4..0", number: 9, transformed: true),
        new(code: 0x1E, codeBits: 5, description: "M4..0, R05..0, G34, B30, B31, B24, G05..0, G25, B25, B32, G24, B05..0, G35, B33, B35, B34, R15..0, G23..0, G15..0, G33..0, B15..0, B23..0, R25..0, R35..0, PB4..0", number: 10, transformed: false),
    ];

    // One two-region mode, with its description parsed into Layout: for each of the header's 82 bits, the endpoint field
    // it holds as 16 (3 e + c) + n (bit n of endpoint e's channel c), PartitionField + n for partition bit n, or -1 for a
    // mode bit. EndpointBits and DeltaBits are each channel's width in endpoint 0 and in endpoints 1 to 3.
    private sealed class TwoRegionMode {
        public TwoRegionMode(int code, int codeBits, int number, bool transformed, string description) {
            Code = code;
            CodeBits = codeBits;
            Number = number;
            Transformed = transformed;
            Layout = new short[TwoRegionHeaderBits];
            EndpointBits = new int[3];
            DeltaBits = new int[3];

            var position = 0;

            foreach (var field in description.Split(separator: ", ")) {
                if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "M") || field.StartsWith(comparisonType: StringComparison.Ordinal, value: "PB")) {
                    var partition = (field[0] == 'P');
                    var high = int.Parse(s: field[(partition ? 2 : 1)..field.IndexOf(comparisonType: StringComparison.Ordinal, value: "..")], provider: System.Globalization.CultureInfo.InvariantCulture);

                    for (var bit = 0; (bit <= high); bit++) {
                        Layout[position++] = ((short)(partition ? (PartitionField + bit) : -1));
                    }

                    continue;
                }

                var channel = "RGB".IndexOf(value: field[0]);
                var endpoint = (field[1] - '0');
                var range = field[2..].Split(separator: "..");
                var first = int.Parse(s: range[0], provider: System.Globalization.CultureInfo.InvariantCulture);
                var last = ((range.Length == 1) ? first : int.Parse(s: range[1], provider: System.Globalization.CultureInfo.InvariantCulture));
                var step = ((first >= last) ? 1 : -1);

                // A range a..b fills the block from b, so its last-named bit sits lowest.
                for (var bit = last; ; bit += step) {
                    Layout[position++] = ((short)((16 * ((3 * endpoint) + channel)) + bit));

                    var widths = ((endpoint == 0) ? EndpointBits : DeltaBits);

                    widths[channel] = Math.Max(val1: widths[channel], val2: (bit + 1));

                    if (bit == first) {
                        break;
                    }
                }
            }

            if (position != TwoRegionHeaderBits) {
                throw new InvalidOperationException(message: $"BC6H mode {Number}'s description lays out {position} header bits, not {TwoRegionHeaderBits}.");
            }
        }

        public int Code { get; }
        public int CodeBits { get; }
        public int[] DeltaBits { get; }
        public int[] EndpointBits { get; }
        public short[] Layout { get; }
        public int Number { get; }
        public bool Transformed { get; }
    }

    /// <summary>Encodes one block in one two-region mode and partition, as the encoder fits a candidate it tries.</summary>
    /// <param name="rgb">The sixteen source texels of three half bits each, texel <c>(x, y)</c> at
    /// <c>3 (4 y + x)</c>.</param>
    /// <param name="mode">The two-region mode, 1 to 10.</param>
    /// <param name="partition">The partition number, 0 to 31.</param>
    /// <param name="block">The destination for the block's sixteen bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> or <paramref name="partition"/> is outside its
    /// range.</exception>
    public static void EncodePartitionedBlock(ReadOnlySpan<ushort> rgb, int mode, int partition, Span<byte> block) {
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 1, value: mode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(other: TwoRegionModes.Length, value: mode);
        ArgumentOutOfRangeException.ThrowIfNegative(value: partition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: 32, value: partition);

        Span<int> values = stackalloc int[48];

        for (var index = 0; (index < 48); index++) {
            values[index] = Clamp(half: rgb[index]);
        }

        EncodeTwoRegionPartition(block: block, mode: TwoRegionModes[(mode - 1)], partition: partition, values: values);
    }

    private static TwoRegionMode? TwoRegionModeOf(ReadOnlySpan<byte> block) {
        foreach (var mode in TwoRegionModes) {
            if ((block[0] & ((1 << mode.CodeBits) - 1)) == mode.Code) {
                return mode;
            }
        }

        return null;
    }
    // Region r's anchor: texel 0 for the first, the BC7 two-subset table's second-subset anchor for the second.
    private static int AnchorOf(int partition, int region) =>
        Bc7Codec.AnchorOf(partition: partition, subset: region, subsets: 2);
    private static int RegionOf(int partition, int texel) =>
        Bc7Codec.SubsetOf(partition: partition, subsets: 2, texel: texel);
    private static void DecodeTwoRegion(ReadOnlySpan<byte> block, TwoRegionMode mode, Span<ushort> rgb) {
        var bits = new BlockBits(block: block);
        // Endpoint e's channel c at 3 e + c: region r's pair is endpoints 2 r and 2 r + 1.
        Span<int> e = stackalloc int[12];
        var partition = 0;

        for (var position = 0; (position < TwoRegionHeaderBits); position++) {
            var bit = bits.Read(count: 1);
            var field = mode.Layout[position];

            if (field >= PartitionField) {
                partition |= (bit << (field - PartitionField));
            } else if (field >= 0) {
                e[(field >> 4)] |= (bit << (field & 15));
            }
        }

        if (mode.Transformed) {
            for (var endpoint = 1; (endpoint < 4); endpoint++) {
                for (var channel = 0; (channel < 3); channel++) {
                    e[((endpoint * 3) + channel)] = (e[channel] + SignExtend(bits: mode.DeltaBits[channel], value: e[((endpoint * 3) + channel)])) & ((1 << mode.EndpointBits[channel]) - 1);
                }
            }
        }

        var weights = Bc7Codec.Weights3;
        var anchor = AnchorOf(partition: partition, region: 1);

        for (var texel = 0; (texel < 16); texel++) {
            var region = RegionOf(partition: partition, texel: texel);
            var weight = weights[bits.Read(count: (((texel == 0) || (texel == anchor)) ? 2 : 3))];

            for (var channel = 0; (channel < 3); channel++) {
                var u0 = Unquantize(bits: mode.EndpointBits[channel], value: e[((region * 6) + channel)]);
                var u1 = Unquantize(bits: mode.EndpointBits[channel], value: e[(((region * 6) + 3) + channel)]);

                rgb[((texel * 3) + channel)] = ((ushort)Finish(value: Interpolate(u0: u0, u1: u1, weight: weight)));
            }
        }
    }
    // Tries each two-region mode over its best-ranked partitions and replaces `best` with any candidate that decodes
    // strictly nearer the source, so an earlier candidate keeps a tie.
    private static void EncodeTwoRegion(ReadOnlySpan<int> values, Span<byte> best, long bestError) {
        Span<byte> candidate = stackalloc byte[BlockBytes];
        Span<int> ranked = stackalloc int[PartitionCandidates];
        var count = PartitionRanking.Rank(channels: 3, partitions: 32, ranked: ranked, stride: 3, subsets: 2, values: values);

        foreach (var mode in TwoRegionModes) {
            for (var rank = 0; (rank < count); rank++) {
                // Nothing beats an exact candidate.
                if (bestError == 0L) {
                    return;
                }

                EncodeTwoRegionPartition(block: candidate, mode: mode, partition: ranked[rank], values: values);

                var error = Error(block: candidate, values: values);

                if (error < bestError) {
                    bestError = error;
                    candidate.CopyTo(destination: best);
                }
            }
        }
    }
    // Fits each region's endpoints as the one-region modes fit a block, orders each pair so the region's anchor takes
    // a low index, clamps a transformed mode's deltas into their signed range (moving an endpoint toward endpoint 0), and
    // picks every texel's index against the final endpoints with each anchor held to the low half.
    private static void EncodeTwoRegionPartition(ReadOnlySpan<int> values, TwoRegionMode mode, int partition, Span<byte> block) {
        Span<int> e = stackalloc int[12];
        Span<int> indices = stackalloc int[16];

        for (var region = 0; (region < 2); region++) {
            var members = 0;

            for (var texel = 0; (texel < 16); texel++) {
                if (RegionOf(partition: partition, texel: texel) == region) {
                    members |= (1 << texel);
                }
            }

            var q0 = e.Slice(length: 3, start: (region * 6));
            var q1 = e.Slice(length: 3, start: ((region * 6) + 3));

            FitRegion(indices: indices, members: members, mode: mode, q0: q0, q1: q1, values: values);

            if (indices[AnchorOf(partition: partition, region: region)] >= 4) {
                for (var channel = 0; (channel < 3); channel++) {
                    (q0[channel], q1[channel]) = (q1[channel], q0[channel]);
                }
            }
        }

        if (mode.Transformed) {
            for (var endpoint = 1; (endpoint < 4); endpoint++) {
                for (var channel = 0; (channel < 3); channel++) {
                    var reach = (1 << (mode.DeltaBits[channel] - 1));
                    var delta = Math.Clamp(max: (reach - 1), min: -reach, value: (e[((endpoint * 3) + channel)] - e[channel]));

                    e[((endpoint * 3) + channel)] = (e[channel] + delta);
                }
            }
        }

        var anchor = AnchorOf(partition: partition, region: 1);

        for (var region = 0; (region < 2); region++) {
            var members = 0;

            for (var texel = 0; (texel < 16); texel++) {
                if (RegionOf(partition: partition, texel: texel) == region) {
                    members |= (1 << texel);
                }
            }

            _ = FitRegionIndices(anchor: ((region == 0) ? 0 : anchor), indices: indices, members: members, mode: mode, q0: e.Slice(length: 3, start: (region * 6)), q1: e.Slice(length: 3, start: ((region * 6) + 3)), values: values);
        }

        var bits = new BlockBits();

        for (var position = 0; (position < TwoRegionHeaderBits); position++) {
            var field = mode.Layout[position];

            if (field < 0) {
                bits.WriteBit(bit: position, value: mode.Code);
            } else if (field >= PartitionField) {
                bits.WriteBit(bit: (field - PartitionField), value: partition);
            } else {
                // The field's endpoint channel, 3 e + c; a transformed mode stores endpoints 1 to 3 as deltas.
                var slot = (field >> 4);
                var channel = (slot % 3);
                var stored = (((slot >= 3) && mode.Transformed) ? (e[slot] - e[channel]) : e[slot]);

                bits.WriteBit(bit: field & 15, value: stored);
            }
        }

        for (var texel = 0; (texel < 16); texel++) {
            bits.Write(count: (((texel == 0) || (texel == anchor)) ? 2 : 3), value: indices[texel]);
        }

        bits.CopyTo(block: block);
    }
    // Fits one region's endpoints and its members' indices: the bounding box, then two least-squares refits, each
    // quantized to the mode's endpoint bits, keeping the fit with the smallest error.
    private static void FitRegion(ReadOnlySpan<int> values, TwoRegionMode mode, int members, Span<int> q0, Span<int> q1, Span<int> indices) {
        Span<double> low = stackalloc double[3];
        Span<double> high = stackalloc double[3];
        Span<int> trialQ0 = stackalloc int[3];
        Span<int> trialQ1 = stackalloc int[3];
        Span<int> trial = stackalloc int[16];
        var bestError = long.MaxValue;

        EndpointFit.BoundingBox(channels: 3, high: high, low: low, members: members, stride: 3, texels: values);

        for (var pass = 0; (pass < 3); pass++) {
            for (var channel = 0; (channel < 3); channel++) {
                trialQ0[channel] = Quantize(bits: mode.EndpointBits[channel], value: low[channel]);
                trialQ1[channel] = Quantize(bits: mode.EndpointBits[channel], value: high[channel]);
            }

            var error = FitRegionIndices(anchor: -1, indices: trial, members: members, mode: mode, q0: trialQ0, q1: trialQ1, values: values);

            if (error < bestError) {
                bestError = error;
                trialQ0.CopyTo(destination: q0);
                trialQ1.CopyTo(destination: q1);

                for (var texel = 0; (texel < 16); texel++) {
                    if (((members >> texel) & 1) != 0) {
                        indices[texel] = trial[texel];
                    }
                }
            }

            if (!EndpointFit.LeastSquares(channels: 3, high: high, indices: indices, low: low, members: members, stride: 3, texels: values, weights: Bc7Codec.Weights3)) {
                break;
            }
        }
    }
    // Picks each member's nearest 3-bit weight in finished half bits, the lowest index winning a tie and the texel
    // `anchor` (when a member) choosing among the low four, and returns the squared error.
    private static long FitRegionIndices(ReadOnlySpan<int> values, TwoRegionMode mode, int members, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, int anchor, Span<int> indices) {
        Span<int> u0 = stackalloc int[3];
        Span<int> u1 = stackalloc int[3];
        var weights = Bc7Codec.Weights3;
        var error = 0L;

        for (var channel = 0; (channel < 3); channel++) {
            u0[channel] = Unquantize(bits: mode.EndpointBits[channel], value: q0[channel]);
            u1[channel] = Unquantize(bits: mode.EndpointBits[channel], value: q1[channel]);
        }

        for (var texel = 0; (texel < 16); texel++) {
            if (((members >> texel) & 1) == 0) {
                continue;
            }

            var best = 0;
            var bestError = long.MaxValue;

            for (var index = 0; (index < ((texel == anchor) ? 4 : 8)); index++) {
                var squared = 0L;

                for (var channel = 0; (channel < 3); channel++) {
                    long difference = (Finish(value: Interpolate(u0: u0[channel], u1: u1[channel], weight: weights[index])) - values[((texel * 3) + channel)]);

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
