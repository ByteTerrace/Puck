using Puck.Maths;

namespace Puck.Abstractions.Recording;

/// <summary>Frames one AV1 open bitstream unit (OBU): the header byte, the optional extension byte and the optional
/// <c>leb128</c> size, the walk both the recording engine's temporal-delimiter filter and a platform's bitstream
/// inspection take over an encoder's temporal unit.</summary>
public static class Av1Obu {
    // AV1's leb128 is at most eight bytes, so 56 value bits; a padded spelling is legal.
    private const int Av1Leb128MaxBits = 56;

    /// <summary>Reads the OBU at <paramref name="index"/> and advances <paramref name="index"/> past it. A header the
    /// walk cannot trust stops it instead of being guessed at: a set forbidden bit, a truncated size, or a payload that
    /// runs past the buffer.</summary>
    /// <param name="temporalUnit">The run of OBUs to read from.</param>
    /// <param name="index">The offset of the OBU's header byte; on success the offset one past its payload, unchanged on
    /// failure.</param>
    /// <param name="obuType">The OBU's <c>obu_type</c> (the four bits below the forbidden bit), or 0 on failure.</param>
    /// <param name="payloadOffset">The offset of the payload, after the header, extension and size, or 0 on failure.</param>
    /// <param name="payloadLength">The payload's byte count, or 0 on failure. An OBU without a size field runs to the
    /// end of <paramref name="temporalUnit"/>.</param>
    /// <returns><see langword="false"/> at the end of the unit or on a header that cannot be trusted.</returns>
    public static bool TryRead(ReadOnlySpan<byte> temporalUnit, ref int index, out int obuType, out int payloadOffset, out int payloadLength) {
        obuType = 0;
        payloadOffset = 0;
        payloadLength = 0;

        if (index >= temporalUnit.Length) {
            return false;
        }

        var headerByte = temporalUnit[index];

        // The forbidden bit (bit 7) is zero in a well-formed OBU header; a set bit means this is not an OBU run.
        if ((headerByte & 0x80) != 0) {
            return false;
        }

        var extensionFlag = ((headerByte >> 2) & 0x1);
        var hasSizeField = ((headerByte >> 1) & 0x1);
        var cursor = (index + (1 + extensionFlag));
        ulong length;

        if (hasSizeField == 1) {
            if (!Leb128.TryRead(
                data: temporalUnit,
                maxBits: Av1Leb128MaxBits,
                offset: ref cursor,
                requireMinimal: false,
                value: out length
            )) {
                return false;
            }
        } else {
            length = ((ulong)Math.Max(
                val1: 0,
                val2: (temporalUnit.Length - cursor)
            ));
        }

        if (
            (cursor > temporalUnit.Length) ||
            (length > ((ulong)(temporalUnit.Length - cursor)))
        ) {
            return false;
        }

        obuType = ((headerByte >> 3) & 0xF);
        payloadOffset = cursor;
        payloadLength = ((int)length);
        index = (cursor + payloadLength);

        return true;
    }
}
