namespace Puck.Abstractions.Recording;

/// <summary>Reads an AV1 <c>leb128</c>-encoded unsigned integer (little-endian base-128, at most 8 bytes) — the OBU
/// size field both the recording engine's temporal-delimiter filter and a platform's bitstream walk decode.</summary>
public static class Av1Leb128 {
    /// <summary>Reads one <c>leb128</c> value starting at <paramref name="offset"/>.</summary>
    /// <param name="data">The buffer to read from.</param>
    /// <param name="offset">The read cursor; advanced past each byte consumed, whether or not the read ultimately
    /// succeeds.</param>
    /// <param name="value">The decoded value, or a partial accumulation on failure.</param>
    /// <returns><see langword="true"/> when a complete value (at most 8 bytes) was read before the buffer ran out.</returns>
    public static bool TryRead(ReadOnlySpan<byte> data, ref int offset, out ulong value) {
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
