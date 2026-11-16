using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Puck.Assets;

/// <summary>A forward-only growable byte sink that writes every scalar little-endian whatever the host's byte order.
/// It is the one buffer under every Puck binary writer: a wire leaf (<c>Puck.Networking.WireWriter</c>) and a machine
/// snapshot (<c>Puck.Machines.StateWriter</c>) derive from it and add their own framing, so growth, bounds and the
/// primitive encodings are written once. The writer keeps and grows one backing buffer that <see cref="Reset"/> rewinds,
/// so an owner that retains a writer reuses one allocation across writes. Unprefixed raw runs are
/// <see cref="WriteBytes"/> and <see cref="WriteRaw{T}"/>; a length-prefixed block is a derived writer's own choice.</summary>
public class LittleEndianWriter {
    private byte[] m_buffer;
    private int m_length;

    /// <summary>Creates an empty writer.</summary>
    /// <param name="capacity">The initial backing-buffer capacity in bytes; it grows as needed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    protected LittleEndianWriter(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        m_buffer = new byte[capacity];
        m_length = 0;
    }

    /// <summary>Gets the backing buffer, of which the first <see cref="Length"/> bytes are written. It is replaced when
    /// a write grows it, so a derived writer takes it only for immediate use.</summary>
    protected byte[] Buffer => m_buffer;
    /// <summary>Gets the number of bytes written so far.</summary>
    public int Length => m_length;
    /// <summary>Gets the written bytes as memory aliasing the writer's own buffer, with no copy. It is valid only until
    /// the next write (a resize moves the buffer) or <see cref="Reset"/>, so it is for immediate consumption; anything
    /// stored or queued takes <see cref="ToArray"/>.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => m_buffer.AsMemory(
        length: m_length,
        start: 0
    );
    /// <summary>Gets the written bytes as a span aliasing the writer's own buffer, with no copy. It is valid only until
    /// the next write (a resize moves the buffer) or <see cref="Reset"/>.</summary>
    public ReadOnlySpan<byte> WrittenSpan => m_buffer.AsSpan(
        length: m_length,
        start: 0
    );

    /// <summary>Claims the next <paramref name="count"/> bytes, growing the buffer to at least twice its size, or to
    /// exactly what is needed when that is more. The returned span is valid only until the next claim, because a claim
    /// may move the buffer: a caller that needs two spans claims the first, fills it, then claims the second.</summary>
    /// <param name="count">The number of bytes to claim.</param>
    /// <returns>The claimed bytes.</returns>
    /// <exception cref="OverflowException">The written length plus <paramref name="count"/> overflows an
    /// <see cref="int"/>.</exception>
    /// <exception cref="OutOfMemoryException">The written length would pass the largest array.</exception>
    protected Span<byte> Reserve(int count) {
        var required = checked((m_length + count));

        if (required > m_buffer.Length) {
            if (required > Array.MaxLength) {
                throw new OutOfMemoryException(message: $"a writer of {required} bytes exceeds the largest array");
            }

            Array.Resize(
                array: ref m_buffer,
                newSize: ((int)Math.Min(
                    val1: Math.Max(
                        val1: (2L * m_buffer.Length),
                        val2: required
                    ),
                    val2: Array.MaxLength
                ))
            );
        }

        var slice = m_buffer.AsSpan(
            length: count,
            start: m_length
        );

        m_length = required;

        return slice;
    }

    /// <summary>Rewinds the writer to empty, keeping the backing buffer so the next write reuses it.</summary>
    public void Reset() => m_length = 0;
    /// <summary>Copies everything written so far into a new, exact-size array, disjoint from the writer's reusable
    /// backing store: the form to keep when the bytes are stored or queued beyond the writer's next write.</summary>
    /// <returns>The written bytes.</returns>
    public byte[] ToArray() => WrittenSpan.ToArray();
    /// <summary>Writes one boolean as a single 0/1 byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value) => WriteByte(value: ((byte)(value
        ? 1
        : 0)));
    /// <summary>Writes one byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteByte(byte value) => Reserve(count: sizeof(byte))[0] = value;
    /// <summary>Writes a run of bytes verbatim, with no prefix.</summary>
    /// <param name="value">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(destination: Reserve(count: value.Length));
    /// <summary>Writes one GUID as its 16 little-endian bytes.</summary>
    /// <param name="value">The value.</param>
    public void WriteGuid(Guid value) => _ = value.TryWriteBytes(
        bigEndian: false,
        bytesWritten: out _,
        destination: Reserve(count: 16)
    );
    /// <summary>Writes one little-endian double-precision float.</summary>
    /// <param name="value">The value.</param>
    public void WriteDouble(double value) => BinaryPrimitives.WriteDoubleLittleEndian(
        destination: Reserve(count: sizeof(double)),
        value: value
    );
    /// <summary>Writes one little-endian signed 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(
        destination: Reserve(count: sizeof(int)),
        value: value
    );
    /// <summary>Writes one little-endian signed 64-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(
        destination: Reserve(count: sizeof(long)),
        value: value
    );
    /// <summary>Bulk-writes a span of unmanaged values as their raw in-memory bytes, with no prefix: the memcpy path for
    /// a big memory region or register file. The bytes are in host byte order (Puck targets little-endian hosts), so the
    /// reader that restores them must be the raw mirror, for example <c>StateReader.ReadRaw</c>.</summary>
    /// <typeparam name="T">The unmanaged element type.</typeparam>
    /// <param name="values">The values to write.</param>
    public void WriteRaw<T>(ReadOnlySpan<T> values) where T : unmanaged =>
        WriteBytes(value: MemoryMarshal.AsBytes(span: values));
    /// <summary>Writes one signed byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteSByte(sbyte value) => Reserve(count: sizeof(sbyte))[0] = ((byte)value);
    /// <summary>Writes one little-endian single-precision float.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(
        destination: Reserve(count: sizeof(float)),
        value: value
    );
    /// <summary>Writes one little-endian unsigned 16-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(
        destination: Reserve(count: sizeof(ushort)),
        value: value
    );
    /// <summary>Writes one little-endian unsigned 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(
        destination: Reserve(count: sizeof(uint)),
        value: value
    );
    /// <summary>Writes one little-endian unsigned 64-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(
        destination: Reserve(count: sizeof(ulong)),
        value: value
    );
}
