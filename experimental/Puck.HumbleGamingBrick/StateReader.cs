using System.Buffers.Binary;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A forward-only, little-endian binary source that a component reads its mutable state back from when restoring a
/// snapshot. It mirrors <see cref="StateWriter"/> exactly: fields must be read in the same order and widths they were
/// written, which is the discipline that keeps restore deterministic.
/// </summary>
public sealed class StateReader {
    private readonly byte[] m_buffer;

    private int m_offset;

    /// <summary>Creates a reader over a snapshot's bytes.</summary>
    /// <param name="buffer">The bytes previously produced by a <see cref="StateWriter"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public StateReader(byte[] buffer) {
        ArgumentNullException.ThrowIfNull(argument: buffer);

        m_buffer = buffer;
        m_offset = 0;
    }

    /// <summary>Gets the read position, in bytes from the start.</summary>
    public int Position =>
        m_offset;

    /// <summary>Reads a single byte.</summary>
    /// <returns>The next byte.</returns>
    public byte ReadByte() =>
        Take(count: sizeof(byte))[0];
    /// <summary>Reads a raw run of bytes into a destination span, filling it completely.</summary>
    /// <param name="destination">The span to fill.</param>
    public void ReadBytes(Span<byte> destination) =>
        Take(count: destination.Length).CopyTo(destination: destination);
    /// <summary>Reads a boolean written as one byte.</summary>
    /// <returns>The boolean value.</returns>
    public bool ReadBoolean() =>
        (ReadByte() != 0);
    /// <summary>Reads a little-endian 16-bit unsigned integer.</summary>
    /// <returns>The value.</returns>
    public ushort ReadUInt16() =>
        BinaryPrimitives.ReadUInt16LittleEndian(source: Take(count: sizeof(ushort)));
    /// <summary>Reads a little-endian 64-bit unsigned integer.</summary>
    /// <returns>The value.</returns>
    public ulong ReadUInt64() =>
        BinaryPrimitives.ReadUInt64LittleEndian(source: Take(count: sizeof(ulong)));
    /// <summary>Reads a little-endian 32-bit signed integer.</summary>
    /// <returns>The value.</returns>
    public int ReadInt32() =>
        BinaryPrimitives.ReadInt32LittleEndian(source: Take(count: sizeof(int)));

    private ReadOnlySpan<byte> Take(int count) {
        var slice = m_buffer.AsSpan(start: m_offset, length: count);

        m_offset += count;

        return slice;
    }
}
