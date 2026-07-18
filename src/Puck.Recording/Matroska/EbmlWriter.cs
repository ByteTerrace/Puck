using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Recording.Matroska;

/// <summary>
/// Writes EBML primitives — element identifiers, variable-length sizes, and the typed leaf elements
/// (unsigned integer, IEEE double, ASCII string, raw binary) — to a <see cref="Stream"/>. It is the byte-level
/// half of the <see cref="MatroskaMuxer"/>: every element the muxer emits crosses one of these helpers, so the
/// container's byte layout is defined in exactly one place and is invariant for a given input.
/// </summary>
/// <remarks>
/// An EBML element is an identifier followed by a variable-length size and then the content. Identifiers are
/// stored in <see cref="MatroskaIds"/> already carrying their length-marker bits, so they are written using
/// their natural byte count. Sizes are encoded in the shortest form that is not the reserved all-ones
/// (unknown-size) pattern for that length.
/// </remarks>
internal static class EbmlWriter {
    /// <summary>Writes an element identifier using the byte count implied by its highest set bit.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="id">The canonical encoded identifier.</param>
    public static void WriteId(Stream stream, uint id) {
        var length = ((32 - BitOperations.LeadingZeroCount(value: id)) + 7) / 8;
        Span<byte> buffer = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(destination: buffer, value: id);
        stream.Write(buffer: buffer[(4 - length)..]);
    }

    /// <summary>Writes a size as the shortest variable-length integer that can represent it.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="size">The non-negative content size.</param>
    public static void WriteSize(Stream stream, long size) {
        // Length L holds 7*L value bits; the all-ones value of that width is reserved (unknown size), so the
        // usable maximum is (2^(7L) - 2). Pick the shortest L whose usable range covers the size.
        for (var length = 1; (length <= 8); length++) {
            var capacity = ((1L << (7 * length)) - 1L);

            if (size < capacity) {
                Span<byte> buffer = stackalloc byte[8];
                var marker = (1L << (7 * length));

                BinaryPrimitives.WriteInt64BigEndian(destination: buffer, value: (marker | size));
                stream.Write(buffer: buffer[(8 - length)..]);

                return;
            }
        }

        throw new ArgumentOutOfRangeException(
            actualValue: size,
            message: "Size exceeds the eight-byte EBML variable-length integer range.",
            paramName: nameof(size)
        );
    }

    /// <summary>Writes the eight-byte reserved unknown-size marker (for a live, unbounded master element).</summary>
    /// <param name="stream">The destination stream.</param>
    public static void WriteUnknownSize(Stream stream) {
        // Eight-byte length whose seven-bit-per-byte value part is all ones: 0x01FF_FFFF_FFFF_FFFF.
        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteInt64BigEndian(destination: buffer, value: 0x01FFFFFFFFFFFFFFL);
        stream.Write(buffer: buffer);
    }

    /// <summary>Writes an unsigned-integer element in the fewest content bytes that hold the value.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="id">The element identifier.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteUInt(Stream stream, uint id, ulong value) {
        var length = 1;

        while ((length < 8) && (value >= (1UL << (8 * length)))) {
            length++;
        }

        WriteId(stream: stream, id: id);
        WriteSize(stream: stream, size: length);

        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteUInt64BigEndian(destination: buffer, value: value);
        stream.Write(buffer: buffer[(8 - length)..]);
    }

    /// <summary>Writes an eight-byte IEEE-754 double element.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="id">The element identifier.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteDouble(Stream stream, uint id, double value) {
        WriteId(stream: stream, id: id);
        WriteSize(stream: stream, size: 8);

        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteDoubleBigEndian(destination: buffer, value: value);
        stream.Write(buffer: buffer);
    }

    /// <summary>Writes an ASCII string element (the muxer's strings — codec ids and app names — are ASCII).</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="id">The element identifier.</param>
    /// <param name="value">The ASCII text to encode.</param>
    public static void WriteAsciiString(Stream stream, uint id, string value) {
        Span<byte> buffer = stackalloc byte[value.Length];

        for (var index = 0; (index < value.Length); index++) {
            buffer[index] = (byte)value[index];
        }

        WriteId(stream: stream, id: id);
        WriteSize(stream: stream, size: buffer.Length);
        stream.Write(buffer: buffer);
    }

    /// <summary>Writes a raw binary element.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="id">The element identifier.</param>
    /// <param name="value">The content bytes.</param>
    public static void WriteBinary(Stream stream, uint id, ReadOnlySpan<byte> value) {
        WriteId(stream: stream, id: id);
        WriteSize(stream: stream, size: value.Length);
        stream.Write(buffer: value);
    }

    /// <summary>Writes a master element's identifier and known content size (the caller writes the children).</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="id">The master element identifier.</param>
    /// <param name="contentSize">The exact byte length of the children that follow.</param>
    public static void WriteMasterHeader(Stream stream, uint id, long contentSize) {
        WriteId(stream: stream, id: id);
        WriteSize(stream: stream, size: contentSize);
    }
}
