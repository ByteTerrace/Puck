using System.Text;

namespace Puck.World.Qr;

/// <summary>
/// A deterministic, spec-correct QR code encoder (ISO/IEC 18004) — byte mode only (the payloads this engine encodes
/// are URLs, including platform-minted storage links), auto version selection across versions 1..10, all four
/// error-correction levels, proper Reed–Solomon EC (<see cref="QrReedSolomon"/>), all eight mask patterns scored by
/// the spec's four penalty rules, and correct format/version info bits. Pure integer math throughout; the same payload
/// + level always builds the identical <see cref="QrMatrix"/> — no wall clock, no RNG, so it is safe to call from
/// presentation code with no determinism caveat of its own, and safe to call from the document validator, which is
/// why it lives beside the document model rather than beside its renderer.
/// </summary>
public static class QrEncoder {
    private const int ByteModeIndicator = 0b0100;
    private const uint FormatInfoGenerator = 0x537u; // x^10+x^8+x^5+x^4+x^2+x+1, degree 10
    private const int FormatInfoGeneratorDegree = 10;
    private const uint FormatInfoXorMask = 0x5412u;
    private const uint VersionInfoGenerator = 0x1F25u; // x^12+x^11+x^10+x^9+x^8+x^5+x^2+1, degree 12
    private const int VersionInfoGeneratorDegree = 12;

    /// <summary>The highest QR version this encoder will choose — a payload too large even at this version and the
    /// requested level is REFUSED, never carried by a higher version this encoder does not implement.</summary>
    public const int MaxSupportedVersion = QrCapacityTable.MaxVersion;
    /// <summary>The lowest QR version this encoder will choose.</summary>
    public const int MinSupportedVersion = QrCapacityTable.MinVersion;

    // Mode indicator (4 bits, byte mode) + character-count indicator + the payload bytes, terminated, byte-packed,
    // and padded to the version+level's exact data-codeword capacity.
    private static byte[] BuildDataCodewords(byte[] payload, int version, QrBlockPlan plan) {
        var writer = new QrBitWriter(capacityBytes: plan.TotalDataCodewords);

        writer.WriteBits(
            bitCount: 4,
            value: ByteModeIndicator
        );
        writer.WriteBits(
            value: payload.Length,
            bitCount: QrCapacityTable.ByteModeCharacterCountBits(version: version)
        );

        foreach (var value in payload) {
            writer.WriteBits(
                bitCount: 8,
                value: value
            );
        }

        return writer.FinishAndPad();
    }
    // The BCH remainder shared by format info (5 data bits, degree-10 generator) and version info (6 data bits,
    // degree-12 generator) — CRC-style polynomial division entirely in integer XOR/shift, no field-element machinery
    // (BCH here is a DIFFERENT code from the Reed-Solomon EC codewords; the QR spec uses both).
    private static uint ComputeBchRemainder(uint data, int dataBits, uint generator, int generatorDegree) {
        var value = (data << generatorDegree);

        for (var i = (dataBits - 1); (i >= 0); i--) {
            if (((value >> (i + generatorDegree)) & 1) != 0) {
                value ^= (generator << i);
            }
        }

        return value & ((1u << generatorDegree) - 1);
    }
    // Splits the padded data codewords into their group-1/group-2 blocks, computes each block's EC codewords, then
    // interleaves data (column-major, short blocks simply exhausted first) followed by EC (column-major, uniform
    // length) — ISO/IEC 18004 §8.6, the exact sequence the matrix placement zigzag consumes.
    private static byte[] InterleaveBlocks(byte[] dataCodewords, QrBlockPlan plan) {
        var blocks = new byte[plan.TotalBlocks][];
        var eccBlocks = new byte[plan.TotalBlocks][];
        var offset = 0;
        var blockIndex = 0;

        for (var i = 0; (i < plan.Group1Blocks); i++) {
            blocks[blockIndex++] = dataCodewords[offset..(offset + plan.Group1DataCodewords)];
            offset += plan.Group1DataCodewords;
        }

        for (var i = 0; (i < plan.Group2Blocks); i++) {
            blocks[blockIndex++] = dataCodewords[offset..(offset + plan.Group2DataCodewords)];
            offset += plan.Group2DataCodewords;
        }

        for (var i = 0; (i < blocks.Length); i++) {
            eccBlocks[i] = QrReedSolomon.ComputeCodewords(
                data: blocks[i],
                eccCount: plan.EccCodewordsPerBlock
            );
        }

        var result = new byte[plan.TotalCodewords];
        var position = 0;
        var maxDataLength = Math.Max(
            val1: plan.Group1DataCodewords,
            val2: plan.Group2DataCodewords
        );

        for (var column = 0; (column < maxDataLength); column++) {
            foreach (var block in blocks) {
                if (column < block.Length) {
                    result[position++] = block[column];
                }
            }
        }

        for (var column = 0; (column < plan.EccCodewordsPerBlock); column++) {
            foreach (var eccBlock in eccBlocks) {
                result[position++] = eccBlock[column];
            }
        }

        return result;
    }

    /// <summary>Builds the 15-bit format-info string for (<paramref name="level"/>, <paramref name="mask"/>) — the
    /// 5-bit indicator, its BCH(15,5) remainder, XORed with the spec's fixed mask (ISO/IEC 18004 §8.9, Annex C).
    /// Exposed beyond <see cref="QrMatrix.Build"/>'s own use because it is independently verifiable against the spec's
    /// published format-string table — the shape a PASS/FAIL harness checks against.</summary>
    /// <param name="level">The error-correction level.</param>
    /// <param name="mask">The mask pattern (0..7).</param>
    /// <returns>The 15-bit format string, bit 0 least significant.</returns>
    public static uint ComputeFormatInfoBits(QrErrorCorrectionLevel level, int mask) {
        var data = ((uint)((((int)level) << 3) | mask));
        var remainder = ComputeBchRemainder(
            data: data,
            dataBits: 5,
            generator: FormatInfoGenerator,
            generatorDegree: FormatInfoGeneratorDegree
        );

        return ((data << FormatInfoGeneratorDegree) | remainder) ^ FormatInfoXorMask;
    }
    /// <summary>Builds the 18-bit version-info string for <paramref name="version"/> — the 6-bit version number, its
    /// BCH(18,6) remainder, no XOR mask (ISO/IEC 18004 §8.10, Annex D). Only versions 7+ carry this in the matrix.
    /// Exposed for the same reason as <see cref="ComputeFormatInfoBits"/> — independently verifiable against the
    /// spec's published version-string table.</summary>
    /// <param name="version">The QR version.</param>
    /// <returns>The 18-bit version string, bit 0 least significant.</returns>
    public static uint ComputeVersionInfoBits(int version) {
        var data = ((uint)version);
        var remainder = ComputeBchRemainder(
            data: data,
            dataBits: 6,
            generator: VersionInfoGenerator,
            generatorDegree: VersionInfoGeneratorDegree
        );

        return (data << VersionInfoGeneratorDegree) | remainder;
    }
    /// <summary>Encodes a payload string into a QR matrix at the smallest version (1..10) that holds it at
    /// <paramref name="level"/>.</summary>
    /// <param name="payload">The payload, encoded UTF-8 (byte mode — the QR spec's default interpretation for
    /// arbitrary text with no ECI segment; a URL's ASCII range round-trips identically in UTF-8 and ISO-8859-1).</param>
    /// <param name="level">The requested error-correction level.</param>
    /// <param name="matrix">The built matrix, on success; <see langword="null"/> otherwise.</param>
    /// <param name="error">A human-readable refusal reason, on failure; <see langword="null"/> otherwise.</param>
    /// <returns>Whether encoding succeeded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    public static bool TryEncode(string payload, QrErrorCorrectionLevel level, out QrMatrix? matrix, out string? error) {
        ArgumentNullException.ThrowIfNull(argument: payload);

        var bytes = Encoding.UTF8.GetBytes(s: payload);

        if (!TryFindVersion(
            payloadByteCount: bytes.Length,
            level: level,
            out var version,
            out error
        )) {
            matrix = null;

            return false;
        }

        var plan = QrCapacityTable.BlockPlan(
            level: level,
            version: version
        );
        var dataCodewords = BuildDataCodewords(
            payload: bytes,
            plan: plan,
            version: version
        );
        var finalCodewords = InterleaveBlocks(
            dataCodewords: dataCodewords,
            plan: plan
        );

        matrix = QrMatrix.Build(
            codewords: finalCodewords,
            level: level,
            version: version
        );
        error = null;

        return true;
    }
    /// <summary>Finds the smallest supported version whose data-codeword capacity, at <paramref name="level"/>, holds
    /// a byte-mode payload of <paramref name="payloadByteCount"/> bytes (mode indicator + character-count indicator +
    /// the payload itself). Exposed separately from <see cref="TryEncode"/> so a document validator can refuse an
    /// oversized payload BY NAME without building the matrix.</summary>
    /// <param name="payloadByteCount">The UTF-8 byte length of the candidate payload.</param>
    /// <param name="level">The requested error-correction level.</param>
    /// <param name="version">The smallest version that fits, on success; 0 otherwise.</param>
    /// <param name="error">A human-readable refusal reason naming the payload length, the level, and the capacity it
    /// exceeded, on failure; <see langword="null"/> otherwise.</param>
    /// <returns>Whether a supported version fits the payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadByteCount"/> is negative.</exception>
    public static bool TryFindVersion(int payloadByteCount, QrErrorCorrectionLevel level, out int version, out string? error) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: payloadByteCount);

        for (version = MinSupportedVersion; (version <= MaxSupportedVersion); version++) {
            var requiredBits = ((4 + QrCapacityTable.ByteModeCharacterCountBits(version: version)) + (payloadByteCount * 8));
            var capacityBits = (QrCapacityTable.BlockPlan(
                level: level,
                version: version
            ).TotalDataCodewords * 8);

            if (requiredBits <= capacityBits) {
                error = null;

                return true;
            }
        }

        version = 0;

        var plan = QrCapacityTable.BlockPlan(
            level: level,
            version: MaxSupportedVersion
        );
        // The largest payload that still leaves room for the 4-bit mode indicator and the 16-bit version-10 byte-mode
        // character count — the number an author needs, not the raw codeword capacity.
        var maximumPayloadBytes = ((((plan.TotalDataCodewords * 8) - 4) - QrCapacityTable.ByteModeCharacterCountBits(version: MaxSupportedVersion)) / 8);

        error = $"payload is {payloadByteCount} bytes; the largest this encoder can carry at level {QrErrorCorrection.Letter(level: level)} is {maximumPayloadBytes} bytes (version {MaxSupportedVersion}, {plan.TotalDataCodewords} data codewords) — refused, never truncated";

        return false;
    }
}
