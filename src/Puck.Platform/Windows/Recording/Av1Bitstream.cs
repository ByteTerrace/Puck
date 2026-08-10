namespace Puck.Platform.Windows.Recording;

// Reads the AV1 bitstream the MFT emits. Two things come out of the same OBU walk: the Matroska `av1C`
// (AV1CodecConfigurationRecord) CodecPrivate, assembled from the sequence-header OBU in the first keyframe temporal
// unit; and whether a temporal unit is a random-access point, read from the frame header's own frame_type. The four
// fixed config bytes are filled by bit-parsing the sequence header (AV1 spec 5.5); the sequence-header OBU itself is
// appended as the record's configOBUs. Parsing walks far enough into color_config() to read the
// bit-depth/monochrome/subsampling fields the record carries.
internal static class Av1Bitstream {
    private const int ObuFrame = 6;
    private const int ObuFrameHeader = 3;
    private const int ObuSequenceHeader = 1;

    /// <summary>Reads whether a temporal unit starts a random access point, from the bitstream rather than from an
    /// encoder-reported flag: a frame header whose <c>frame_type</c> is <c>KEY_FRAME</c> and which is not merely
    /// re-showing an already-decoded frame.</summary>
    /// <param name="temporalUnit">The temporal unit (a run of size-delimited OBUs).</param>
    /// <returns><see langword="true"/> for a key frame, <see langword="false"/> for a non-key frame, and
    /// <see langword="null"/> when the unit carries no frame header this walk can read — the caller then has nothing
    /// to override its encoder's own report with.</returns>
    public static bool? TryReadIsKeyFrame(ReadOnlySpan<byte> temporalUnit) {
        var index = 0;

        while (TryReadObu(temporalUnit: temporalUnit, index: ref index, obuType: out var obuType, payloadOffset: out var payloadOffset, payloadLength: out var payloadLength)) {
            if (((obuType != ObuFrame) && (obuType != ObuFrameHeader)) || (payloadLength <= 0)) {
                continue;
            }

            // uncompressed_header() opens with show_existing_frame f(1) then frame_type f(2). A unit that only re-shows
            // an already-decoded frame starts nothing, whatever the frame it points at was.
            var first = temporalUnit[payloadOffset];

            if ((first & 0x80) != 0) {
                return false;
            }

            return (((first >> 5) & 0x03) == 0);
        }

        return null;
    }

    /// <summary>Finds the sequence-header OBU inside an AV1 temporal unit and builds <c>av1C</c> from it.</summary>
    /// <param name="temporalUnit">The keyframe temporal unit (a run of size-delimited OBUs).</param>
    /// <returns>The AV1CodecConfigurationRecord bytes, or empty when no sequence header is present.</returns>
    public static byte[] BuildConfigRecord(ReadOnlySpan<byte> temporalUnit) {
        if (!TryFindSequenceHeaderObu(temporalUnit: temporalUnit, obu: out var sequenceHeaderObu, payload: out var payload)) {
            return [];
        }

        var fields = ParseSequenceHeader(payload: payload);
        var record = new byte[(4 + sequenceHeaderObu.Length)];

        record[0] = 0x81; // marker(1)=1, version(7)=1
        record[1] = (byte)((fields.SeqProfile << 5) | (fields.SeqLevelIdx0 & 0x1F));
        record[2] = (byte)(
            (fields.SeqTier0 << 7)
            | (fields.HighBitdepth << 6)
            | (fields.TwelveBit << 5)
            | (fields.Monochrome << 4)
            | (fields.SubsamplingX << 3)
            | (fields.SubsamplingY << 2)
            | (fields.ChromaSamplePosition & 0x3));
        record[3] = 0x00; // reserved(3)=0, initial_presentation_delay_present(1)=0, minus_one(4)=0

        sequenceHeaderObu.CopyTo(destination: record.AsSpan(start: 4));

        return record;
    }

    // Walk the OBUs (obu_header + optional extension + leb128 size) looking for the sequence header. Returns the full
    // OBU bytes (header included) and a view of just its payload for field parsing.
    private static bool TryFindSequenceHeaderObu(ReadOnlySpan<byte> temporalUnit, out byte[] obu, out byte[] payload) {
        var index = 0;

        while (true) {
            var start = index;

            if (!TryReadObu(temporalUnit: temporalUnit, index: ref index, obuType: out var obuType, payloadOffset: out var payloadOffset, payloadLength: out var payloadLength)) {
                break;
            }

            if (obuType == ObuSequenceHeader) {
                obu = temporalUnit.Slice(start: start, length: (index - start)).ToArray();
                payload = temporalUnit.Slice(start: payloadOffset, length: payloadLength).ToArray();

                return true;
            }
        }

        obu = [];
        payload = [];

        return false;
    }

    // Reads one OBU at index and advances index past it. Returns false at the end of the unit or on a header the walk
    // cannot trust (a truncated size or a payload running past the buffer) — a malformed tail stops the walk rather
    // than being guessed at.
    private static bool TryReadObu(ReadOnlySpan<byte> temporalUnit, ref int index, out int obuType, out int payloadOffset, out int payloadLength) {
        obuType = 0;
        payloadOffset = 0;
        payloadLength = 0;

        if (index >= temporalUnit.Length) {
            return false;
        }

        var headerByte = temporalUnit[index];
        var extensionFlag = ((headerByte >> 2) & 0x1);
        var hasSizeField = ((headerByte >> 1) & 0x1);
        var cursor = (index + (1 + extensionFlag));

        obuType = ((headerByte >> 3) & 0xF);

        if (hasSizeField == 1) {
            if (!TryReadLeb128(data: temporalUnit, offset: ref cursor, value: out var size)) {
                return false;
            }

            payloadLength = (int)size;
        } else {
            payloadLength = (temporalUnit.Length - cursor);
        }

        if ((payloadLength < 0) || ((cursor + payloadLength) > temporalUnit.Length)) {
            return false;
        }

        payloadOffset = cursor;
        index = (cursor + payloadLength);

        return true;
    }
    private static bool TryReadLeb128(ReadOnlySpan<byte> data, ref int offset, out ulong value) {
        value = 0;

        for (var i = 0; (i < 8); i++) {
            if (offset >= data.Length) {
                return false;
            }

            var b = data[offset];

            offset++;
            value |= (((ulong)(b & 0x7F)) << (i * 7));

            if ((b & 0x80) == 0) {
                return true;
            }
        }

        return false;
    }
    private static SequenceHeaderFields ParseSequenceHeader(ReadOnlySpan<byte> payload) {
        var reader = new BitReader(data: payload);
        var fields = default(SequenceHeaderFields);

        fields.SeqProfile = reader.Read(bits: 3);

        var stillPicture = reader.Read(bits: 1);
        var reducedStillPicture = reader.Read(bits: 1);
        var decoderModelInfoPresent = 0;
        var bufferDelayLength = 0;

        _ = stillPicture;

        if (reducedStillPicture == 1) {
            fields.SeqLevelIdx0 = reader.Read(bits: 5);
            fields.SeqTier0 = 0;
        } else {
            var timingInfoPresent = reader.Read(bits: 1);

            if (timingInfoPresent == 1) {
                _ = reader.Read(bits: 32); // num_units_in_display_tick
                _ = reader.Read(bits: 32); // time_scale

                if (reader.Read(bits: 1) == 1) {  // equal_picture_interval
                    reader.SkipUvlc();
                }

                decoderModelInfoPresent = reader.Read(bits: 1);

                if (decoderModelInfoPresent == 1) {
                    bufferDelayLength = (reader.Read(bits: 5) + 1);
                    _ = reader.Read(bits: 32); // num_units_in_decoding_tick
                    _ = reader.Read(bits: 5);  // buffer_removal_time_length_minus_1
                    _ = reader.Read(bits: 5);  // frame_presentation_time_length_minus_1
                }
            }

            var initialDisplayDelayPresent = reader.Read(bits: 1);
            var operatingPointsCount = (reader.Read(bits: 5) + 1);

            for (var i = 0; (i < operatingPointsCount); i++) {
                _ = reader.Read(bits: 12); // operating_point_idc

                var seqLevelIdx = reader.Read(bits: 5);
                var seqTier = 0;

                if (seqLevelIdx > 7) {
                    seqTier = reader.Read(bits: 1);
                }

                if (decoderModelInfoPresent == 1) {
                    if (reader.Read(bits: 1) == 1) { // decoder_model_present_for_this_op
                        _ = reader.Read(bits: bufferDelayLength); // decoder_buffer_delay
                        _ = reader.Read(bits: bufferDelayLength); // encoder_buffer_delay
                        _ = reader.Read(bits: 1);                 // low_delay_mode_flag
                    }
                }

                if (initialDisplayDelayPresent == 1) {
                    if (reader.Read(bits: 1) == 1) { // initial_display_delay_present_for_this_op
                        _ = reader.Read(bits: 4);    // initial_display_delay_minus_1
                    }
                }

                if (i == 0) {
                    fields.SeqLevelIdx0 = seqLevelIdx;
                    fields.SeqTier0 = seqTier;
                }
            }
        }

        var frameWidthBits = (reader.Read(bits: 4) + 1);
        var frameHeightBits = (reader.Read(bits: 4) + 1);

        _ = reader.Read(bits: frameWidthBits);  // max_frame_width_minus_1
        _ = reader.Read(bits: frameHeightBits); // max_frame_height_minus_1

        var frameIdNumbersPresent = 0;

        if (reducedStillPicture == 0) {
            frameIdNumbersPresent = reader.Read(bits: 1);
        }

        if (frameIdNumbersPresent == 1) {
            _ = reader.Read(bits: 4); // delta_frame_id_length_minus_2
            _ = reader.Read(bits: 3); // additional_frame_id_length_minus_1
        }

        _ = reader.Read(bits: 1); // use_128x128_superblock
        _ = reader.Read(bits: 1); // enable_filter_intra
        _ = reader.Read(bits: 1); // enable_intra_edge_filter

        if (reducedStillPicture == 0) {
            _ = reader.Read(bits: 1); // enable_interintra_compound
            _ = reader.Read(bits: 1); // enable_masked_compound
            _ = reader.Read(bits: 1); // enable_warped_motion
            _ = reader.Read(bits: 1); // enable_dual_filter

            var enableOrderHint = reader.Read(bits: 1);

            if (enableOrderHint == 1) {
                _ = reader.Read(bits: 1); // enable_jnt_comp
                _ = reader.Read(bits: 1); // enable_ref_frame_mvs
            }

            var seqChooseScreenContentTools = reader.Read(bits: 1);
            var seqForceScreenContentTools = 2; // SELECT_SCREEN_CONTENT_TOOLS

            if (seqChooseScreenContentTools == 0) {
                seqForceScreenContentTools = reader.Read(bits: 1);
            }

            if (seqForceScreenContentTools > 0) {
                var seqChooseIntegerMv = reader.Read(bits: 1);

                if (seqChooseIntegerMv == 0) {
                    _ = reader.Read(bits: 1); // seq_force_integer_mv
                }
            }

            if (enableOrderHint == 1) {
                _ = reader.Read(bits: 3); // order_hint_bits_minus_1
            }
        }

        _ = reader.Read(bits: 1); // enable_superres
        _ = reader.Read(bits: 1); // enable_cdef
        _ = reader.Read(bits: 1); // enable_restoration

        ParseColorConfig(reader: ref reader, seqProfile: fields.SeqProfile, fields: ref fields);

        return fields;
    }
    private static void ParseColorConfig(ref BitReader reader, int seqProfile, ref SequenceHeaderFields fields) {
        var highBitdepth = reader.Read(bits: 1);
        var twelveBit = 0;

        if ((seqProfile == 2) && (highBitdepth == 1)) {
            twelveBit = reader.Read(bits: 1);
        }

        var monochrome = 0;

        if (seqProfile != 1) {
            monochrome = reader.Read(bits: 1);
        }

        var colorDescriptionPresent = reader.Read(bits: 1);
        var colorPrimaries = 2;         // CP_UNSPECIFIED
        var transferCharacteristics = 2; // TC_UNSPECIFIED
        var matrixCoefficients = 2;      // MC_UNSPECIFIED

        if (colorDescriptionPresent == 1) {
            colorPrimaries = reader.Read(bits: 8);
            transferCharacteristics = reader.Read(bits: 8);
            matrixCoefficients = reader.Read(bits: 8);
        }

        var subsamplingX = 1;
        var subsamplingY = 1;
        var chromaSamplePosition = 0;

        if (monochrome == 1) {
            _ = reader.Read(bits: 1); // color_range
            subsamplingX = 1;
            subsamplingY = 1;
        } else if ((colorPrimaries == 1) && (transferCharacteristics == 13) && (matrixCoefficients == 0)) {
            subsamplingX = 0;
            subsamplingY = 0;
        } else {
            _ = reader.Read(bits: 1); // color_range

            if (seqProfile == 0) {
                subsamplingX = 1;
                subsamplingY = 1;
            } else if (seqProfile == 1) {
                subsamplingX = 0;
                subsamplingY = 0;
            } else if (twelveBit == 1) {
                subsamplingX = reader.Read(bits: 1);

                if (subsamplingX == 1) {
                    subsamplingY = reader.Read(bits: 1);
                } else {
                    subsamplingY = 0;
                }
            } else {
                subsamplingX = 1;
                subsamplingY = 0;
            }

            if ((subsamplingX == 1) && (subsamplingY == 1)) {
                chromaSamplePosition = reader.Read(bits: 2);
            }
        }

        fields.HighBitdepth = highBitdepth;
        fields.TwelveBit = twelveBit;
        fields.Monochrome = monochrome;
        fields.SubsamplingX = subsamplingX;
        fields.SubsamplingY = subsamplingY;
        fields.ChromaSamplePosition = chromaSamplePosition;
    }

    private struct SequenceHeaderFields {
        public int SeqProfile;
        public int SeqLevelIdx0;
        public int SeqTier0;
        public int HighBitdepth;
        public int TwelveBit;
        public int Monochrome;
        public int SubsamplingX;
        public int SubsamplingY;
        public int ChromaSamplePosition;
    }

    // Big-endian MSB-first bit reader over the OBU payload; reads past the end return zero bits (defensive: a truncated
    // header yields conservative config bytes rather than throwing).
    private ref struct BitReader {
        private readonly ReadOnlySpan<byte> m_data;
        private int m_bitPosition;

        public BitReader(ReadOnlySpan<byte> data) {
            m_data = data;
            m_bitPosition = 0;
        }

        public int Read(int bits) {
            var value = 0;

            for (var i = 0; (i < bits); i++) {
                var byteIndex = (m_bitPosition >> 3);
                var bit = 0;

                if (byteIndex < m_data.Length) {
                    var shift = (7 - (m_bitPosition & 7));

                    bit = (m_data[byteIndex] >> shift) & 1;
                }

                value = (value << 1) | bit;
                m_bitPosition++;
            }

            return value;
        }
        public void SkipUvlc() {
            var leadingZeros = 0;

            while (leadingZeros < 32) {
                if (Read(bits: 1) == 1) {
                    break;
                }

                leadingZeros++;
            }

            if (leadingZeros > 0) {
                _ = Read(bits: leadingZeros);
            }
        }
    }
}
