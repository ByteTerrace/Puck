using System.Buffers.Binary;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A forward-only, little-endian binary sink that a component serializes its mutable state into for a snapshot. Every
/// width is written in a fixed byte order regardless of host endianness, so a snapshot taken on one machine restores
/// bit-identically on another — the property the whole fork-and-diverge model rests on.
/// </summary>
public sealed class StateWriter {
    private byte[] m_buffer;
    private int m_length;

    /// <summary>Creates an empty writer.</summary>
    /// <param name="capacity">The initial backing-buffer capacity in bytes; it grows as needed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public StateWriter(int capacity = 256) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        m_buffer = new byte[capacity];
        m_length = 0;
    }

    /// <summary>Gets the number of bytes written so far.</summary>
    public int Length =>
        m_length;

    /// <summary>Copies everything written so far into a new array.</summary>
    /// <returns>The written bytes.</returns>
    public byte[] ToArray() =>
        m_buffer[..m_length];
    /// <summary>Writes a single byte.</summary>
    /// <param name="value">The value to write.</param>
    public void WriteByte(byte value) =>
        Reserve(count: sizeof(byte))[0] = value;
    /// <summary>Writes a raw run of bytes verbatim.</summary>
    /// <param name="value">The bytes to write.</param>
    public void WriteBytes(ReadOnlySpan<byte> value) =>
        value.CopyTo(destination: Reserve(count: value.Length));
    /// <summary>Writes a boolean as one byte (<c>0</c> or <c>1</c>).</summary>
    /// <param name="value">The value to write.</param>
    public void WriteBoolean(bool value) =>
        WriteByte(value: (byte)(value ? 1 : 0));
    /// <summary>Writes a 16-bit unsigned integer in little-endian order.</summary>
    /// <param name="value">The value to write.</param>
    public void WriteUInt16(ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(destination: Reserve(count: sizeof(ushort)), value: value);
    /// <summary>Writes a 64-bit unsigned integer in little-endian order.</summary>
    /// <param name="value">The value to write.</param>
    public void WriteUInt64(ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(destination: Reserve(count: sizeof(ulong)), value: value);
    /// <summary>Writes a 32-bit signed integer in little-endian order.</summary>
    /// <param name="value">The value to write.</param>
    public void WriteInt32(int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination: Reserve(count: sizeof(int)), value: value);

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
