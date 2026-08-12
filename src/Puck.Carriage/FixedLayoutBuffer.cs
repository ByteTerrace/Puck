using System.Buffers.Binary;
using System.Text;

namespace Puck.Carriage;

/// <summary>
/// Append-only writer for the fixed byte layout: every variable-length field is a 4-byte big-endian length
/// prefix followed by that many bytes, and every optional field is a 1-byte presence flag followed by the
/// value when present. No field is ever self-describing beyond that — the field ORDER is the schema, fixed
/// by the caller, and both sides must agree on it out of band (README.md, "Signed carriage": "The
/// byte layout is all that must agree").
/// </summary>
internal sealed class FixedLayoutWriter {
    private readonly List<byte> m_buffer = [];

    public byte[] ToArray() => [.. m_buffer];
    public void WriteByte(byte value) => m_buffer.Add(item: value);
    public void WriteBool(bool value) => WriteByte(value: (byte)(value
        ? 1
        : 0));
    public void WriteInt64(long value) {
        Span<byte> span = stackalloc byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(
            destination: span,
            value: value
        );
        m_buffer.AddRange(collection: span.ToArray());
    }
    public void WriteUInt64(ulong value) {
        Span<byte> span = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(
            destination: span,
            value: value
        );
        m_buffer.AddRange(collection: span.ToArray());
    }
    public void WriteBytes(ReadOnlySpan<byte> value) {
        Span<byte> lengthSpan = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: lengthSpan,
            value: checked((uint)value.Length)
        );
        m_buffer.AddRange(collection: lengthSpan.ToArray());
        m_buffer.AddRange(collection: value.ToArray());
    }
    public void WriteFixedBytes(ReadOnlySpan<byte> value) => m_buffer.AddRange(collection: value.ToArray());
    public void WriteString(string value) => WriteBytes(value: Encoding.UTF8.GetBytes(s: value));
    public void WriteOptionalString(string? value) {
        WriteBool(value: (value is not null));

        if (value is not null) {
            WriteString(value: value);
        }
    }
    public void WriteOptionalUInt64(ulong? value) {
        WriteBool(value: (value is not null));

        if (value is not null) {
            WriteUInt64(value: value.Value);
        }
    }
}

/// <summary>
/// Bounds-checked cursor reader for the fixed byte layout — the counterpart to <see cref="FixedLayoutWriter"/>.
/// Every read validates the claimed length against the bytes actually remaining BEFORE advancing or
/// allocating, so a forged or truncated length prefix throws <see cref="FormatException"/> rather than
/// reading past the buffer or attempting an oversized allocation. This is the "no parsing of
/// unauthenticated bytes beyond bounded reads" requirement: nothing here trusts a length claim wider than
/// what actually arrived.
/// </summary>
internal ref struct FixedLayoutReader(ReadOnlySpan<byte> buffer) {
    private readonly ReadOnlySpan<byte> m_buffer = buffer;
    private int m_position = 0;

    public readonly int Remaining => (m_buffer.Length - m_position);

    /// <summary>How many bytes have been consumed so far — how the codec slices out the signed portion exactly as it arrived.</summary>
    public readonly int Position => m_position;

    private readonly void RequireRemaining(int count) {
        if (count > Remaining) {
            throw new FormatException(message: $"The carriage envelope is truncated: needed {count} more byte(s) at offset {m_position}, but only {Remaining} remain.");
        }
    }

    public byte ReadByte() {
        RequireRemaining(count: 1);

        var value = m_buffer[m_position];

        m_position += 1;

        return value;
    }

    /// <summary>
    /// Reads a presence flag, refusing every byte but <c>0x00</c> and <c>0x01</c>. Treating "non-zero" as
    /// true is the whole of this layout's canonicality story going wrong: the writer only ever emits
    /// <c>0x01</c>, so accepting <c>0x02</c> gives one model 255 wire forms, and a receiver deduplicating
    /// on wire bytes then sees one claim as many. README.md §3 rule "one model, exactly
    /// one encoding" is a DECODER obligation, and §16 leans on this layout meeting it by construction.
    /// </summary>
    /// <exception cref="FormatException">The flag byte is neither <c>0x00</c> nor <c>0x01</c>.</exception>
    public bool ReadBool() {
        var value = ReadByte();

        return value switch {
            0 => false,
            1 => true,
            _ => throw new FormatException(message: $"The carriage envelope carries 0x{value:X2} in the presence flag at offset {(m_position - 1)}; a presence flag is exactly 0x00 or 0x01."),
        };
    }
    public long ReadInt64() {
        RequireRemaining(count: sizeof(long));

        var value = BinaryPrimitives.ReadInt64BigEndian(source: m_buffer.Slice(
            start: m_position,
            length: sizeof(long)
        ));

        m_position += sizeof(long);

        return value;
    }
    public ulong ReadUInt64() {
        RequireRemaining(count: sizeof(ulong));

        var value = BinaryPrimitives.ReadUInt64BigEndian(source: m_buffer.Slice(
            start: m_position,
            length: sizeof(ulong)
        ));

        m_position += sizeof(ulong);

        return value;
    }
    public ReadOnlySpan<byte> ReadBytes() {
        RequireRemaining(count: sizeof(uint));

        var length = BinaryPrimitives.ReadUInt32BigEndian(source: m_buffer.Slice(
            start: m_position,
            length: sizeof(uint)
        ));

        m_position += sizeof(uint);

        // The length prefix is attacker-controlled (it travels on the wire before verification), so it is
        // validated against what is actually left in the buffer before any slice or allocation is made —
        // never trusted as an instruction to read or allocate beyond that.
        if (length > int.MaxValue) {
            throw new FormatException(message: $"The carriage envelope declares a field length ({length}) wider than this reader will ever hold.");
        }

        RequireRemaining(count: (int)length);

        var slice = m_buffer.Slice(
            start: m_position,
            length: (int)length
        );

        m_position += (int)length;

        return slice;
    }
    public ReadOnlySpan<byte> ReadFixedBytes(int count) {
        RequireRemaining(count: count);

        var slice = m_buffer.Slice(
            start: m_position,
            length: count
        );

        m_position += count;

        return slice;
    }
    public string ReadString() => Encoding.UTF8.GetString(bytes: ReadBytes());
    public string? ReadOptionalString() => (ReadBool()
        ? ReadString()
        : null);
    public ulong? ReadOptionalUInt64() => (ReadBool()
        ? ReadUInt64()
        : null);
}
