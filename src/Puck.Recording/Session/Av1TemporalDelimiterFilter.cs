namespace Puck.Recording.Session;

/// <summary>
/// Strips temporal-delimiter OBUs (<c>OBU_TEMPORAL_DELIMITER</c>, type 2) from an AV1 temporal unit before it
/// becomes a Matroska/WebM block. Each Matroska SimpleBlock already IS one temporal unit, so the in-band delimiter
/// is redundant and the AV1-in-Matroska/WebM carriage mapping prefers it removed; some players reject or mis-seek a
/// stream that keeps it. The walk mirrors <c>Av1ConfigRecord</c>'s OBU parser (header + optional extension byte +
/// optional leb128 size); it copies every non-delimiter OBU verbatim and preserves order, and bails (returning
/// <c>-1</c>, keep-original) on any byte it cannot parse so a non-AV1 or truncated payload is never corrupted.
/// </summary>
internal static class Av1TemporalDelimiterFilter {
    private const int ObuTemporalDelimiter = 2;

    /// <summary>Copies <paramref name="temporalUnit"/> into <paramref name="destination"/> minus any temporal-delimiter
    /// OBUs, growing the buffer as needed.</summary>
    /// <param name="temporalUnit">The encoder's AV1 temporal unit (a run of OBUs).</param>
    /// <param name="destination">A reusable scratch buffer; reassigned to a larger array when it is too small.</param>
    /// <returns>The number of bytes written to <paramref name="destination"/>, or <c>-1</c> when the payload could not
    /// be parsed (the caller then writes the original bytes unchanged).</returns>
    public static int Strip(ReadOnlySpan<byte> temporalUnit, ref byte[] destination) {
        if (destination.Length < temporalUnit.Length) {
            destination = new byte[temporalUnit.Length];
        }

        var index = 0;
        var written = 0;

        while (index < temporalUnit.Length) {
            var headerByte = temporalUnit[index];

            // forbidden bit (bit 7) must be zero in a well-formed OBU header; a set bit means this is not an OBU run.
            if ((headerByte & 0x80) != 0) {
                return -1;
            }

            var obuType = ((headerByte >> 3) & 0xF);
            var extensionFlag = ((headerByte >> 2) & 0x1);
            var hasSizeField = ((headerByte >> 1) & 0x1);
            var cursor = (index + 1 + extensionFlag);
            int payloadLength;

            if (hasSizeField == 1) {
                if (!TryReadLeb128(data: temporalUnit, offset: ref cursor, value: out var size)) {
                    return -1;
                }

                payloadLength = (int)size;
            } else {
                payloadLength = (temporalUnit.Length - cursor);
            }

            var obuEnd = (cursor + payloadLength);

            if ((payloadLength < 0) || (obuEnd > temporalUnit.Length)) {
                return -1;
            }

            if (obuType != ObuTemporalDelimiter) {
                var obuLength = (obuEnd - index);

                temporalUnit.Slice(start: index, length: obuLength).CopyTo(destination: destination.AsSpan(start: written));
                written += obuLength;
            }

            index = obuEnd;
        }

        return written;
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
}
