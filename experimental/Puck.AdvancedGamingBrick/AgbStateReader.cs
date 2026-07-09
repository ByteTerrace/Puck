using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A forward-only binary source each component reads its mutable state back from when restoring a snapshot. It mirrors
/// <see cref="AgbStateWriter"/> exactly: every field must be read in the same order and width it was written, which is
/// the discipline that keeps a restore deterministic.
/// </summary>
public sealed class AgbStateReader {
    private readonly byte[] m_buffer;
    private readonly int m_end;

    private int m_offset;

    /// <summary>Creates a reader over a snapshot's bytes.</summary>
    /// <param name="buffer">The bytes previously produced by an <see cref="AgbStateWriter"/>.</param>
    /// <param name="start">The offset to begin reading at (past any leading header).</param>
    /// <param name="length">The number of bytes to read; a negative value reads to the end of <paramref name="buffer"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public AgbStateReader(byte[] buffer, int start = 0, int length = -1) {
        ArgumentNullException.ThrowIfNull(argument: buffer);

        m_buffer = buffer;
        m_offset = start;
        m_end = (length < 0) ? buffer.Length : (start + length);
    }

    /// <summary>Gets the read position, in bytes from the start of the backing buffer.</summary>
    public int Position => m_offset;

    /// <summary>Gets whether every byte in the reader's window has been consumed.</summary>
    public bool AtEnd => (m_offset == m_end);

    /// <summary>Reads a single byte.</summary>
    public byte ReadByte() => Take(count: sizeof(byte))[0];

    /// <summary>Reads a signed byte.</summary>
    public sbyte ReadSByte() => (sbyte)Take(count: sizeof(sbyte))[0];

    /// <summary>Reads a boolean written as one byte.</summary>
    public bool ReadBoolean() => (ReadByte() != 0);

    /// <summary>Reads a little-endian 16-bit unsigned integer.</summary>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(source: Take(count: sizeof(ushort)));

    /// <summary>Reads a little-endian 32-bit signed integer.</summary>
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(source: Take(count: sizeof(int)));

    /// <summary>Reads a little-endian 32-bit unsigned integer.</summary>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(source: Take(count: sizeof(uint)));

    /// <summary>Reads a little-endian 64-bit signed integer.</summary>
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(source: Take(count: sizeof(long)));

    /// <summary>Reads a little-endian 64-bit unsigned integer.</summary>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(source: Take(count: sizeof(ulong)));

    /// <summary>Reads a raw run of bytes into a destination span, filling it completely.</summary>
    public void ReadBytes(Span<byte> destination) => Take(count: destination.Length).CopyTo(destination: destination);

    /// <summary>Bulk-reads raw bytes into a span of unmanaged values — the memcpy counterpart of
    /// <see cref="AgbStateWriter.WriteBlock{T}"/>, filling <paramref name="destination"/> completely.</summary>
    public void ReadBlock<T>(Span<T> destination) where T : unmanaged => ReadBytes(destination: MemoryMarshal.AsBytes(span: destination));

    private ReadOnlySpan<byte> Take(int count) {
        if ((m_offset + count) > m_end) {
            throw new InvalidOperationException(message: "Snapshot restore read past the end of the state buffer; the save/load field order has drifted.");
        }

        var slice = m_buffer.AsSpan(start: m_offset, length: count);

        m_offset += count;

        return slice;
    }
}
