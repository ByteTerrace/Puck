namespace Puck.Maths;

/// <summary>The one little-endian base-128 (LEB128) unsigned integer codec: seven value bits a byte, low group first,
/// the high bit set on every byte but the last. It serves AV1's <c>leb128</c> size fields, WebAssembly's section and
/// limit integers, the canonical artifact encoding's lengths and the partitioner's baked delta streams, which differ
/// only in how many value bits they accept and whether a padded spelling is refused.</summary>
public static class Leb128 {
    /// <summary>The longest encoding of a 64-bit value, in bytes.</summary>
    public const int MaxByteCount = 10;

    /// <summary>Reads one unsigned value starting at <paramref name="offset"/>.</summary>
    /// <param name="data">The buffer to read from.</param>
    /// <param name="offset">The read cursor; advanced past each byte consumed, whether or not the read succeeds.</param>
    /// <param name="maxBits">The number of value bits the caller accepts, from 1 to 64; a value needing more, or a
    /// spelling with more bytes than those bits need, is refused.</param>
    /// <param name="requireMinimal">Whether a spelling padded with a final zero group (more bytes than the value needs)
    /// is refused.</param>
    /// <param name="value">The decoded value, or a partial accumulation on failure.</param>
    /// <returns><see langword="true"/> when a complete value within <paramref name="maxBits"/> was read before the
    /// buffer ran out.</returns>
    public static bool TryRead(ReadOnlySpan<byte> data, ref int offset, int maxBits, bool requireMinimal, out ulong value) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: maxBits, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: maxBits, other: 64);

        value = 0UL;

        for (var shift = 0; (shift < maxBits); shift += 7) {
            if (offset >= data.Length) {
                return false;
            }

            var next = data[offset++];
            var payload = ((ulong)(next & 0x7F));

            if (
                ((maxBits - shift) < 7) &&
                ((payload >> (maxBits - shift)) != 0UL)
            ) {
                return false;
            }

            value |= (payload << shift);

            if ((next & 0x80) == 0) {
                return (!requireMinimal || (shift == 0) || (payload != 0UL));
            }
        }

        return false;
    }
    /// <summary>Writes <paramref name="value"/> in its minimal spelling.</summary>
    /// <param name="destination">The buffer to write into; <see cref="MaxByteCount"/> bytes always suffice.</param>
    /// <param name="value">The value to write.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short for the spelling.</exception>
    public static int Write(Span<byte> destination, ulong value) {
        var count = 0;

        do {
            var current = ((byte)(value & 0x7F));

            value >>= 7;
            if (value != 0UL) { current |= 0x80; }
            if (count >= destination.Length) {
                throw new ArgumentException(
                    message: "the destination is too short for the value's LEB128 spelling",
                    paramName: nameof(destination)
                );
            }
            destination[count++] = current;
        } while (value != 0UL);

        return count;
    }
}
