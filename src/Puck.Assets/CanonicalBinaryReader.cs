using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Puck.Assets;

/// <summary>Reads the canonical binary primitives <see cref="CanonicalBinaryWriterExtensions"/> writes, over bytes
/// that need not be trusted. Every read refuses a value spelled any way but the canonical one — a variable-width
/// integer with a redundant byte, a magnitude with a leading zero, a zero with a sign — and a read past the end, with an
/// <see cref="InvalidDataException"/>.</summary>
public ref struct CanonicalBinaryReader {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly ReadOnlySpan<byte> m_content;

    private int m_offset;

    /// <summary>Initializes a reader at the start of <paramref name="content"/>.</summary>
    /// <param name="content">The bytes to read.</param>
    public CanonicalBinaryReader(ReadOnlySpan<byte> content) {
        m_content = content;
    }

    /// <summary>Gets the number of bytes read so far.</summary>
    public readonly int Offset => m_offset;
    /// <summary>Gets the number of bytes not yet read.</summary>
    public readonly int Remaining => (m_content.Length - m_offset);

    /// <summary>Consumes exactly <paramref name="value"/>.</summary>
    /// <param name="value">The bytes the content must carry next, such as an artifact's magic.</param>
    /// <exception cref="InvalidDataException">The next bytes differ.</exception>
    public void Expect(ReadOnlySpan<byte> value) {
        if (
            (Remaining < value.Length) ||
            !m_content.Slice(
            start: m_offset,
            length: value.Length
        ).SequenceEqual(other: value)
        ) {
            throw new InvalidDataException(message: "the artifact magic is invalid");
        }

        m_offset += value.Length;
    }
    /// <summary>Requires that every byte has been read.</summary>
    /// <exception cref="InvalidDataException">Bytes remain.</exception>
    public readonly void ExpectEnd() {
        if (m_offset != m_content.Length) {
            throw new InvalidDataException(message: "the artifact contains trailing bytes");
        }
    }
    /// <summary>Consumes <paramref name="count"/> bytes that must all be zero, such as alignment padding.</summary>
    /// <param name="count">The number of bytes.</param>
    /// <exception cref="InvalidDataException">A byte is nonzero or the content ends first.</exception>
    public void ExpectZeros(int count) {
        if (ReadBytes(count: count).IndexOfAnyExcept(value: ((byte)0)) >= 0) {
            throw new InvalidDataException(message: "the artifact's padding is not zero");
        }
    }
    /// <summary>Reads an integer <see cref="CanonicalBinaryWriterExtensions.WriteBigInteger"/> wrote.</summary>
    /// <param name="maximumByteCount">The largest magnitude, in bytes, the caller accepts.</param>
    /// <returns>The integer.</returns>
    /// <exception cref="InvalidDataException">The integer is malformed, noncanonical, or larger than the ceiling.</exception>
    public BigInteger ReadBigInteger(int maximumByteCount) {
        var sign = ReadByte();
        var byteCount = ReadBoundedInt(maximum: maximumByteCount);

        if (byteCount == 0) {
            if (sign != 0) {
                throw new InvalidDataException(message: "zero has a nonzero sign marker");
            }
            return BigInteger.Zero;
        }
        if (
            (sign != 1) &&
            (sign != 2)
        ) {
            throw new InvalidDataException(message: "a nonzero integer has an invalid sign marker");
        }
        if (Remaining < byteCount) {
            throw new InvalidDataException(message: "an integer magnitude is truncated");
        }

        var bytes = m_content.Slice(
            length: byteCount,
            start: m_offset
        );

        if (bytes[0] == 0) {
            throw new InvalidDataException(message: "an integer magnitude contains a leading zero byte");
        }

        m_offset += byteCount;
        var magnitude = new BigInteger(
            isBigEndian: true,
            isUnsigned: true,
            value: bytes
        );

        return ((sign == 1)
            ? magnitude
            : -magnitude
        );
    }
    /// <summary>Reads a variable-width unsigned integer no greater than <paramref name="maximum"/>.</summary>
    /// <param name="maximum">The largest value the caller accepts.</param>
    /// <returns>The integer.</returns>
    /// <exception cref="InvalidDataException">The integer is malformed, noncanonical, or above the ceiling.</exception>
    public int ReadBoundedInt(int maximum) {
        var value = ReadVarUInt();

        if (value > ((uint)maximum)) {
            throw new InvalidDataException(message: "an artifact count exceeds its configured ceiling");
        }
        return checked((int)value);
    }
    /// <summary>Reads one byte.</summary>
    /// <returns>The byte.</returns>
    /// <exception cref="InvalidDataException">The content has ended.</exception>
    public byte ReadByte() {
        if (m_offset >= m_content.Length) {
            throw new InvalidDataException(message: "the artifact is truncated");
        }
        return m_content[m_offset++];
    }
    /// <summary>Reads the next <paramref name="count"/> bytes as they stand.</summary>
    /// <param name="count">The number of bytes.</param>
    /// <returns>The bytes, a view into the content.</returns>
    /// <exception cref="InvalidDataException">The content ends first.</exception>
    public ReadOnlySpan<byte> ReadBytes(int count) {
        if (
            (count < 0) ||
            (Remaining < count)
        ) {
            throw new InvalidDataException(message: "the artifact is truncated");
        }

        var bytes = m_content.Slice(
            length: count,
            start: m_offset
        );

        m_offset += count;
        return bytes;
    }
    /// <summary>Reads text <see cref="CanonicalBinaryWriterExtensions.WriteText"/> wrote.</summary>
    /// <param name="maximumByteCount">The longest UTF-8 spelling, in bytes, the caller accepts.</param>
    /// <returns>The text.</returns>
    /// <exception cref="InvalidDataException">The text is longer than the ceiling, truncated, or not UTF-8.</exception>
    public string ReadText(int maximumByteCount) {
        var bytes = ReadBytes(count: ReadBoundedInt(maximum: maximumByteCount));

        try {
            return StrictUtf8.GetString(bytes: bytes);
        } catch (DecoderFallbackException exception) {
            throw new InvalidDataException(
                innerException: exception,
                message: "the artifact's text is not UTF-8"
            );
        }
    }
    /// <summary>Reads a 64-bit word <see cref="CanonicalBinaryWriterExtensions.WriteUInt64"/> wrote.</summary>
    /// <returns>The word.</returns>
    /// <exception cref="InvalidDataException">The content ends first.</exception>
    public ulong ReadUInt64() =>
        BinaryPrimitives.ReadUInt64LittleEndian(source: ReadBytes(count: sizeof(ulong)));
    /// <summary>Reads a variable-width unsigned integer <see cref="CanonicalBinaryWriterExtensions.WriteVarUInt"/>
    /// wrote.</summary>
    /// <returns>The integer.</returns>
    /// <exception cref="InvalidDataException">The integer overflows 32 bits, is longer than five bytes, or is not
    /// minimally encoded.</exception>
    public uint ReadVarUInt() {
        var value = 0U;

        for (var index = 0; (index < 5); ++index) {
            var current = ReadByte();

            if (
                (index == 4) &&
                ((current & 0xf0) != 0)
            ) {
                throw new InvalidDataException(message: "a variable-width integer overflows UInt32");
            }

            value |= (((uint)(current & 0x7f)) << (7 * index));
            if ((current & 0x80) != 0) { continue; }
            if (
                (index > 0) &&
                ((current & 0x7f) == 0)
            ) {
                throw new InvalidDataException(message: "a variable-width integer is not minimally encoded");
            }
            return value;
        }

        throw new InvalidDataException(message: "a variable-width integer is too long");
    }
}
