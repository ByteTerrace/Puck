using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A forward-only binary sink each component serializes its mutable state into for a whole-machine snapshot. Scalars
/// are written little-endian (portable) and large memory blocks are bulk-copied verbatim through
/// <see cref="MemoryMarshal"/> — the flat, memcpy-friendly savestate the survey calls for. The writer keeps and grows
/// one backing buffer that <see cref="Reset"/> rewinds, so repeated snapshots reuse the same allocation rather than
/// churning the GC.
/// </summary>
public sealed class AgbStateWriter {
    private byte[] m_buffer;
    private int m_length;

    /// <summary>Creates an empty writer.</summary>
    /// <param name="capacity">The initial backing-buffer capacity in bytes; it grows as needed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public AgbStateWriter(int capacity = 4096) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        m_buffer = new byte[capacity];
        m_length = 0;
    }

    /// <summary>Gets the number of bytes written so far.</summary>
    public int Length => m_length;

    /// <summary>Rewinds the writer to empty, keeping the backing buffer so the next snapshot reuses it.</summary>
    public void Reset() => m_length = 0;

    /// <summary>Copies everything written so far into a new array.</summary>
    /// <returns>The written bytes.</returns>
    public byte[] ToArray() => m_buffer[..m_length];

    /// <summary>Writes a single byte.</summary>
    public void WriteByte(byte value) => Reserve(count: sizeof(byte))[0] = value;

    /// <summary>Writes a signed byte.</summary>
    public void WriteSByte(sbyte value) => Reserve(count: sizeof(sbyte))[0] = (byte)value;

    /// <summary>Writes a boolean as one byte (<c>0</c> or <c>1</c>).</summary>
    public void WriteBoolean(bool value) => WriteByte(value: (byte)(value ? 1 : 0));

    /// <summary>Writes a 16-bit unsigned integer, little-endian.</summary>
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(destination: Reserve(count: sizeof(ushort)), value: value);

    /// <summary>Writes a 32-bit signed integer, little-endian.</summary>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(destination: Reserve(count: sizeof(int)), value: value);

    /// <summary>Writes a 32-bit unsigned integer, little-endian.</summary>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(destination: Reserve(count: sizeof(uint)), value: value);

    /// <summary>Writes a 64-bit signed integer, little-endian.</summary>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(destination: Reserve(count: sizeof(long)), value: value);

    /// <summary>Writes a 64-bit unsigned integer, little-endian.</summary>
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(destination: Reserve(count: sizeof(ulong)), value: value);

    /// <summary>Writes a raw run of bytes verbatim.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(destination: Reserve(count: value.Length));

    /// <summary>Bulk-writes a span of unmanaged values as their raw bytes — the memcpy path for the big memory
    /// regions and register files. Host byte order (Puck targets little-endian hosts), reversed exactly by
    /// <see cref="AgbStateReader.ReadBlock{T}"/>.</summary>
    public void WriteBlock<T>(ReadOnlySpan<T> values) where T : unmanaged =>
        WriteBytes(value: MemoryMarshal.AsBytes(span: values));

    private Span<byte> Reserve(int count) {
        var required = (m_length + count);

        if (required > m_buffer.Length) {
            var capacity = Math.Max(val1: m_buffer.Length, val2: 1);

            while (capacity < required) {
                capacity *= 2;
            }

            Array.Resize(array: ref m_buffer, newSize: capacity);
        }

        var slice = m_buffer.AsSpan(start: m_length, length: count);

        m_length = required;

        return slice;
    }
}
