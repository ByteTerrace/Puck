using Puck.Abstractions.Recording;

namespace Puck.Recording.Session;

/// <summary>
/// Strips temporal-delimiter OBUs (<c>OBU_TEMPORAL_DELIMITER</c>, type 2) from an AV1 temporal unit before it
/// becomes a Matroska/WebM block. Each Matroska SimpleBlock already IS one temporal unit, so the in-band delimiter
/// is redundant and the AV1-in-Matroska/WebM carriage mapping prefers it removed; some players reject or mis-seek a
/// stream that keeps it. The walk is <see cref="Av1Obu.TryRead"/>'s OBU framing; it copies every non-delimiter OBU verbatim and preserves order, and bails (returning
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
            var start = index;

            if (!Av1Obu.TryRead(
                index: ref index,
                obuType: out var obuType,
                payloadLength: out _,
                payloadOffset: out _,
                temporalUnit: temporalUnit
            )) {
                return -1;
            }

            if (obuType != ObuTemporalDelimiter) {
                var obuLength = (index - start);

                temporalUnit.Slice(
                    length: obuLength,
                    start: start
                ).CopyTo(destination: destination.AsSpan(start: written));
                written += obuLength;
            }
        }

        return written;
    }
}
