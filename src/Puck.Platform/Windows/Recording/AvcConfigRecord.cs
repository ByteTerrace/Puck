namespace Puck.Platform.Windows.Recording;

// Parses the H.264 Annex-B elementary stream the MFT emits and assembles the Matroska `avcC` (AVCDecoderConfiguration-
// Record) CodecPrivate from the SPS/PPS NAL units. The V_MPEG4/ISO/AVC mapping also requires the per-frame payload be
// length-prefixed NAL units (not Annex-B start codes) with the parameter sets carried out-of-band, so the same walk
// converts each access unit: start codes -> 4-byte big-endian length prefixes, SPS/PPS/AUD stripped.
internal static class AvcConfigRecord {
    /// <summary>Splits an Annex-B access unit into its NAL units (payloads exclude the start code).</summary>
    /// <param name="annexB">The Annex-B byte stream (start codes 0x000001 or 0x00000001).</param>
    /// <returns>The NAL unit byte ranges, in stream order.</returns>
    public static List<(int Offset, int Length)> SplitNalUnits(ReadOnlySpan<byte> annexB) {
        var units = new List<(int, int)>();
        var index = 0;
        var length = annexB.Length;
        var start = -1;

        while (index < (length - 2)) {
            if ((annexB[index] == 0) && (annexB[index + 1] == 0) && (annexB[index + 2] == 1)) {
                if (start >= 0) {
                    var end = index;

                    // A four-byte start code (00 00 00 01) trailing zero belongs to the next code, not this NAL.
                    if ((end > start) && (annexB[end - 1] == 0)) {
                        end--;
                    }

                    units.Add(item: (start, (end - start)));
                }

                index += 3;
                start = index;
            } else {
                index++;
            }
        }

        if (start >= 0) {
            units.Add(item: (start, (length - start)));
        }

        return units;
    }

    /// <summary>Converts an Annex-B access unit into length-prefixed form, collecting any SPS/PPS it carries.</summary>
    /// <param name="annexB">The encoder's Annex-B output packet.</param>
    /// <param name="sps">Receives the last SPS NAL seen, if any.</param>
    /// <param name="pps">Receives the last PPS NAL seen, if any.</param>
    /// <returns>The access unit as concatenated 4-byte-length-prefixed VCL/SEI NAL units.</returns>
    public static byte[] ToLengthPrefixed(ReadOnlySpan<byte> annexB, ref byte[]? sps, ref byte[]? pps) {
        var units = SplitNalUnits(annexB: annexB);
        var output = new List<byte>(capacity: annexB.Length);

        foreach (var (offset, count) in units) {
            if (count <= 0) {
                continue;
            }

            var nalType = (annexB[offset] & 0x1F);

            switch (nalType) {
                case 7: {
                    sps = annexB.Slice(start: offset, length: count).ToArray();

                    continue;
                }
                case 8: {
                    pps = annexB.Slice(start: offset, length: count).ToArray();

                    continue;
                }
                case 9: {
                    // Access-unit delimiter: redundant in the length-prefixed mapping.
                    continue;
                }
                default: {
                    break;
                }
            }

            output.Add(item: (byte)((count >> 24) & 0xFF));
            output.Add(item: (byte)((count >> 16) & 0xFF));
            output.Add(item: (byte)((count >> 8) & 0xFF));
            output.Add(item: (byte)(count & 0xFF));

            for (var i = 0; (i < count); i++) {
                output.Add(item: annexB[offset + i]);
            }
        }

        return output.ToArray();
    }

    /// <summary>Assembles the <c>avcC</c> CodecPrivate from an SPS and PPS NAL unit.</summary>
    /// <param name="sps">The sequence parameter set NAL (including its 0x67 header byte).</param>
    /// <param name="pps">The picture parameter set NAL (including its 0x68 header byte).</param>
    /// <returns>The AVCDecoderConfigurationRecord bytes.</returns>
    public static byte[] Build(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps) {
        if ((sps.Length < 4) || (pps.Length < 1)) {
            return [];
        }

        var record = new List<byte>(capacity: (sps.Length + pps.Length + 16)) {
            1,             // configurationVersion
            sps[1],        // AVCProfileIndication
            sps[2],        // profile_compatibility
            sps[3],        // AVCLevelIndication
            0xFF,          // 6 reserved bits + lengthSizeMinusOne = 3 (4-byte NAL lengths)
            0xE1,          // 3 reserved bits + numOfSequenceParameterSets = 1
            (byte)((sps.Length >> 8) & 0xFF),
            (byte)(sps.Length & 0xFF),
        };

        record.AddRange(collection: sps.ToArray());
        record.Add(item: 1); // numOfPictureParameterSets
        record.Add(item: (byte)((pps.Length >> 8) & 0xFF));
        record.Add(item: (byte)(pps.Length & 0xFF));
        record.AddRange(collection: pps.ToArray());

        return record.ToArray();
    }
}
