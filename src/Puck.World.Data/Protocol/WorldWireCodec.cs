using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>A stable transport-level refusal name. Every decoder over untrusted bytes returns one of these rather
/// than throwing: a malformed peer is a refusal, never an invariant violation.</summary>
public enum WorldWireRefusal : byte {
    /// <summary>No refusal.</summary>
    None = 0,

    /// <summary>The frame length prefix is missing, impossible, or disagrees with the bytes that followed.</summary>
    FrameLengthInvalid,

    /// <summary>The frame's kind byte is not declared by the protocol.</summary>
    FrameKindUnknown,

    /// <summary>The frame or a nested payload exceeds its declared hard cap.</summary>
    PayloadTooLarge,

    /// <summary>A read ran past the end of the payload.</summary>
    PayloadTruncated,

    /// <summary>The payload carries bytes after its canonical leaf.</summary>
    PayloadTrailingBytes,

    /// <summary>The payload decoded but does not satisfy a structural rule of its message.</summary>
    PayloadMalformed,

    /// <summary>A declared element count lies outside the bound its message admits.</summary>
    CountOutOfRange,

    /// <summary>An enum lane carries no declared wire value.</summary>
    EnumValueUnknown,

    /// <summary>A length-prefixed string exceeds the bytes its field admits.</summary>
    StringTooLong,

    /// <summary>The peer closed the connection where a frame was required.</summary>
    ConnectionClosed,

    /// <summary>The peer refused the handshake, or answered it with the wrong frame.</summary>
    HandshakeRefused,

    /// <summary>The persistent lane to this peer is not carrying traffic; the request was not sent or its answer was
    /// lost with the connection.</summary>
    LaneUnavailable,
}

/// <summary>One named transport refusal plus narration suitable for a console or error frame.</summary>
/// <param name="Refusal">The stable refusal name.</param>
/// <param name="Detail">The human-readable detail.</param>
public readonly record struct WorldWireFailure(WorldWireRefusal Refusal, string Detail) {
    /// <summary>Gets a value indicating whether this failure names a refusal.</summary>
    public bool IsRefusal => (Refusal != WorldWireRefusal.None);

    /// <summary>Formats the stable name beside its detail.</summary>
    /// <returns>The refusal narration.</returns>
    public override string ToString() => (IsRefusal ? $"{Refusal}: {Detail}" : "ok");
}

/// <summary>A bounded, forward-only reader over one already-framed payload. Every read is checked against the
/// remaining span; the first underflow latches a refusal and every later read is inert, so a leaf decoder reads its
/// whole shape and asks once — at <see cref="TryFinish"/> — whether the bytes were honest.</summary>
public ref struct WorldWireReader {
    private readonly ReadOnlySpan<byte> m_bytes;
    private int m_offset;
    private WorldWireFailure m_failure;

    /// <summary>Initializes a reader over one payload.</summary>
    /// <param name="bytes">The payload bytes.</param>
    public WorldWireReader(ReadOnlySpan<byte> bytes) {
        m_bytes = bytes;
        m_offset = 0;
        m_failure = default;
    }

    /// <summary>Gets a value indicating whether a refusal has latched.</summary>
    public readonly bool Failed => m_failure.IsRefusal;

    /// <summary>Gets the latched refusal, if any.</summary>
    public readonly WorldWireFailure Failure => m_failure;

    /// <summary>Gets the count of bytes not yet consumed.</summary>
    public readonly int Remaining => (m_bytes.Length - m_offset);

    /// <summary>Latches a refusal. The first one wins, so the narration names the original cause.</summary>
    /// <param name="refusal">The refusal name.</param>
    /// <param name="detail">The refusal detail.</param>
    public void Fail(WorldWireRefusal refusal, string detail) {
        if (!Failed) {
            m_failure = new WorldWireFailure(Refusal: refusal, Detail: detail);
        }
    }

    /// <summary>Reads one byte.</summary>
    /// <returns>The byte, or zero once a refusal has latched.</returns>
    public byte ReadByte() {
        var slice = Take(count: sizeof(byte));

        return (slice.IsEmpty ? (byte)0 : slice[0]);
    }

    /// <summary>Reads one boolean, refusing any value other than 0 or 1.</summary>
    /// <returns>The boolean.</returns>
    public bool ReadBoolean() {
        var value = ReadByte();

        if (value > 1) {
            Fail(refusal: WorldWireRefusal.PayloadMalformed, detail: $"boolean lane carries {value}, which is neither 0 nor 1");
        }

        return (value == 1);
    }

    /// <summary>Reads one little-endian signed 32-bit integer.</summary>
    /// <returns>The value.</returns>
    public int ReadInt32() {
        var slice = Take(count: sizeof(int));

        return (slice.IsEmpty ? 0 : BinaryPrimitives.ReadInt32LittleEndian(source: slice));
    }

    /// <summary>Reads one little-endian unsigned 32-bit integer.</summary>
    /// <returns>The value.</returns>
    public uint ReadUInt32() {
        var slice = Take(count: sizeof(uint));

        return (slice.IsEmpty ? 0U : BinaryPrimitives.ReadUInt32LittleEndian(source: slice));
    }

    /// <summary>Reads one little-endian signed 64-bit integer.</summary>
    /// <returns>The value.</returns>
    public long ReadInt64() {
        var slice = Take(count: sizeof(long));

        return (slice.IsEmpty ? 0L : BinaryPrimitives.ReadInt64LittleEndian(source: slice));
    }

    /// <summary>Reads one little-endian unsigned 64-bit integer.</summary>
    /// <returns>The value.</returns>
    public ulong ReadUInt64() {
        var slice = Take(count: sizeof(ulong));

        return (slice.IsEmpty ? 0UL : BinaryPrimitives.ReadUInt64LittleEndian(source: slice));
    }

    /// <summary>Reads one little-endian single-precision float. Presentation-only lanes (material color, blend
    /// seconds) carry float; simulation values never do.</summary>
    /// <returns>The value.</returns>
    public float ReadSingle() {
        var slice = Take(count: sizeof(float));

        return (slice.IsEmpty ? 0F : BinaryPrimitives.ReadSingleLittleEndian(source: slice));
    }

    /// <summary>Reads one fixed-point scalar.</summary>
    /// <returns>The value.</returns>
    public FixedQ4816 ReadFixed() => new(ReadInt64());

    /// <summary>Reads one fixed-point vector.</summary>
    /// <returns>The value.</returns>
    public FixedVector3 ReadFixedVector() => new(ReadFixed(), ReadFixed(), ReadFixed());

    /// <summary>Reads one fixed-point quaternion.</summary>
    /// <returns>The value.</returns>
    public FixedQuaternion ReadFixedQuaternion() => new(X: ReadFixed(), Y: ReadFixed(), Z: ReadFixed(), W: ReadFixed());

    /// <summary>Reads one presentation vector, refusing a non-finite lane.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <returns>The value.</returns>
    public Vector3 ReadFiniteVector(string field) {
        var value = new Vector3(ReadSingle(), ReadSingle(), ReadSingle());

        if (!float.IsFinite(f: value.X) || !float.IsFinite(f: value.Y) || !float.IsFinite(f: value.Z)) {
            Fail(refusal: WorldWireRefusal.PayloadMalformed, detail: $"{field} is not finite");
        }

        return value;
    }

    /// <summary>Reads one presentation quaternion.</summary>
    /// <returns>The value.</returns>
    public Quaternion ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

    /// <summary>Reads one UTF-8 string carried behind a 16-bit byte-length prefix.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the encoded byte count.</param>
    /// <returns>The string, or empty once a refusal has latched.</returns>
    public string ReadString(string field, int maxBytes = WorldWireLimits.MaxStringBytes) {
        var length = Take(count: sizeof(ushort)) is { IsEmpty: false } prefix ? BinaryPrimitives.ReadUInt16LittleEndian(source: prefix) : 0;

        if (length > maxBytes) {
            Fail(refusal: WorldWireRefusal.StringTooLong, detail: $"{field} declares {length} bytes; cap is {maxBytes}");

            return string.Empty;
        }

        var slice = Take(count: length);

        return ((length == 0) ? string.Empty : (slice.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes: slice)));
    }

    /// <summary>Reads a required non-blank UTF-8 string.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the encoded byte count.</param>
    /// <returns>The string.</returns>
    public string ReadRequiredString(string field, int maxBytes = WorldWireLimits.MaxStringBytes) {
        var value = ReadString(field: field, maxBytes: maxBytes);

        if (string.IsNullOrWhiteSpace(value: value)) {
            Fail(refusal: WorldWireRefusal.PayloadMalformed, detail: $"{field} is required and carries no text");
        }

        return value;
    }

    /// <summary>Reads a nullable UTF-8 string behind its presence bit.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the encoded byte count.</param>
    /// <returns>The string, or <see langword="null"/>.</returns>
    public string? ReadNullableString(string field, int maxBytes = WorldWireLimits.MaxStringBytes) =>
        (ReadBoolean() ? ReadString(field: field, maxBytes: maxBytes) : null);

    /// <summary>Reads a declared element count and refuses one outside its message's bound. This is the bound that
    /// stands between an untrusted prefix and an allocation sized by it.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="minimum">The smallest admitted count.</param>
    /// <param name="maximum">The largest admitted count.</param>
    /// <returns>The count, clamped to <paramref name="minimum"/> once a refusal has latched.</returns>
    public int ReadCount(string field, int minimum, int maximum) {
        var value = ReadInt32();

        if (Failed) {
            return minimum;
        }

        if ((value < minimum) || (value > maximum)) {
            Fail(refusal: WorldWireRefusal.CountOutOfRange, detail: $"{field} is {value}; the admitted range is {minimum}..{maximum}");

            return minimum;
        }

        return value;
    }

    /// <summary>Reads a bounded length-prefixed byte block.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the block.</param>
    /// <returns>The block, or empty once a refusal has latched.</returns>
    public byte[] ReadBlock(string field, int maxBytes) {
        var length = ReadInt32();

        if (Failed) {
            return [];
        }

        if ((length < 0) || (length > maxBytes)) {
            Fail(refusal: WorldWireRefusal.PayloadTooLarge, detail: $"{field} declares {length} bytes; cap is {maxBytes}");

            return [];
        }

        var slice = Take(count: length);

        return ((length == 0) ? [] : (slice.IsEmpty ? [] : slice.ToArray()));
    }

    /// <summary>Reads whatever bytes remain, refusing a block larger than its cap.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the block.</param>
    /// <returns>The remaining bytes.</returns>
    public byte[] ReadRest(string field, int maxBytes) {
        if (Failed) {
            return [];
        }

        if (Remaining > maxBytes) {
            Fail(refusal: WorldWireRefusal.PayloadTooLarge, detail: $"{field} carries {Remaining} bytes; cap is {maxBytes}");

            return [];
        }

        return Take(count: Remaining).ToArray();
    }

    /// <summary>Completes the read: succeeds only when nothing refused and nothing is left over.</summary>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the payload decoded exactly.</returns>
    public bool TryFinish(out WorldWireFailure failure) {
        if (!Failed && (Remaining != 0)) {
            Fail(refusal: WorldWireRefusal.PayloadTrailingBytes, detail: $"{Remaining} bytes follow the canonical leaf");
        }

        failure = m_failure;

        return !Failed;
    }

    private ReadOnlySpan<byte> Take(int count) {
        if (Failed || (count < 0)) {
            return default;
        }

        if (count > Remaining) {
            Fail(refusal: WorldWireRefusal.PayloadTruncated, detail: $"{count} bytes were required; {Remaining} remain");

            return default;
        }

        var slice = m_bytes.Slice(start: m_offset, length: count);

        m_offset += count;

        return slice;
    }
}

/// <summary>The representation bounds every wire reader and writer shares.</summary>
public static class WorldWireLimits {
    /// <summary>The default hard cap on one length-prefixed string's encoded bytes. Every wire string in this
    /// repository is a name, an authority spelling, or a refusal sentence.</summary>
    public const int MaxStringBytes = (16 * 1024);

    /// <summary>The hard cap on a serialized world document carried inside one message.</summary>
    public const int MaxDocumentBytes = (16 * 1024 * 1024);
}

/// <summary>A growable little-endian writer producing one canonical leaf. It is the exact mirror of
/// <see cref="WorldWireReader"/>; an encoder and its decoder are read side by side.</summary>
public sealed class WorldWireWriter {
    private byte[] m_buffer;
    private int m_length;

    /// <summary>Initializes a writer.</summary>
    /// <param name="capacity">The initial buffer size.</param>
    public WorldWireWriter(int capacity = 256) {
        m_buffer = new byte[Math.Max(val1: capacity, val2: 16)];
        m_length = 0;
    }

    /// <summary>Gets the bytes written so far.</summary>
    public int Length => m_length;

    /// <summary>Writes one byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteByte(byte value) => Reserve(count: sizeof(byte))[0] = value;

    /// <summary>Writes one boolean as a single 0/1 byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value) => WriteByte(value: (byte)(value ? 1 : 0));

    /// <summary>Writes one little-endian signed 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(destination: Reserve(count: sizeof(int)), value: value);

    /// <summary>Writes one little-endian unsigned 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(destination: Reserve(count: sizeof(uint)), value: value);

    /// <summary>Writes one little-endian signed 64-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(destination: Reserve(count: sizeof(long)), value: value);

    /// <summary>Writes one little-endian unsigned 64-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(destination: Reserve(count: sizeof(ulong)), value: value);

    /// <summary>Writes one little-endian single-precision float.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(destination: Reserve(count: sizeof(float)), value: value);

    /// <summary>Writes one fixed-point scalar.</summary>
    /// <param name="value">The value.</param>
    public void WriteFixed(FixedQ4816 value) => WriteInt64(value: value.Value);

    /// <summary>Writes one fixed-point vector.</summary>
    /// <param name="value">The value.</param>
    public void WriteFixedVector(FixedVector3 value) {
        WriteFixed(value: value.X);
        WriteFixed(value: value.Y);
        WriteFixed(value: value.Z);
    }

    /// <summary>Writes one fixed-point quaternion.</summary>
    /// <param name="value">The value.</param>
    public void WriteFixedQuaternion(FixedQuaternion value) {
        WriteFixed(value: value.X);
        WriteFixed(value: value.Y);
        WriteFixed(value: value.Z);
        WriteFixed(value: value.W);
    }

    /// <summary>Writes one presentation vector.</summary>
    /// <param name="value">The value.</param>
    public void WriteVector(Vector3 value) {
        WriteSingle(value: value.X);
        WriteSingle(value: value.Y);
        WriteSingle(value: value.Z);
    }

    /// <summary>Writes one presentation quaternion.</summary>
    /// <param name="value">The value.</param>
    public void WriteQuaternion(Quaternion value) {
        WriteSingle(value: value.X);
        WriteSingle(value: value.Y);
        WriteSingle(value: value.Z);
        WriteSingle(value: value.W);
    }

    /// <summary>Writes one UTF-8 string behind a 16-bit byte-length prefix.</summary>
    /// <param name="value">The value; <see langword="null"/> writes an empty string.</param>
    /// <exception cref="ArgumentException">The encoded string exceeds <see cref="WorldWireLimits.MaxStringBytes"/>.</exception>
    public void WriteString(string? value) {
        var bytes = Encoding.UTF8.GetBytes(s: (value ?? string.Empty));

        if (bytes.Length > WorldWireLimits.MaxStringBytes) {
            throw new ArgumentException(message: $"a wire string of {bytes.Length} bytes exceeds the {WorldWireLimits.MaxStringBytes}-byte field cap", paramName: nameof(value));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination: Reserve(count: sizeof(ushort)), value: (ushort)bytes.Length);
        WriteBytes(value: bytes);
    }

    /// <summary>Writes a nullable string behind its presence bit.</summary>
    /// <param name="value">The value.</param>
    public void WriteNullableString(string? value) {
        WriteBoolean(value: (value is not null));

        if (value is not null) {
            WriteString(value: value);
        }
    }

    /// <summary>Writes a length-prefixed byte block.</summary>
    /// <param name="value">The block.</param>
    public void WriteBlock(ReadOnlySpan<byte> value) {
        WriteInt32(value: value.Length);
        WriteBytes(value: value);
    }

    /// <summary>Writes raw bytes with no prefix.</summary>
    /// <param name="value">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(destination: Reserve(count: value.Length));

    /// <summary>Copies the written bytes into a new array.</summary>
    /// <returns>The canonical leaf.</returns>
    public byte[] ToArray() => m_buffer.AsSpan(start: 0, length: m_length).ToArray();

    private Span<byte> Reserve(int count) {
        if (checked(m_length + count) > m_buffer.Length) {
            Array.Resize(array: ref m_buffer, newSize: Math.Max(val1: (m_buffer.Length * 2), val2: (m_length + count)));
        }

        var span = m_buffer.AsSpan(start: m_length, length: count);

        m_length += count;

        return span;
    }
}

/// <summary>One frame read off a stream: the kind and body, or the named reason there is none.</summary>
/// <param name="Kind">The frame's kind byte.</param>
/// <param name="Body">The frame body, excluding the prefix and kind byte.</param>
/// <param name="Failure">The named refusal when <see cref="Ok"/> is <see langword="false"/>.</param>
public readonly record struct WorldWireFrameRead(byte Kind, byte[] Body, WorldWireFailure Failure) {
    /// <summary>Gets a value indicating whether a frame was read.</summary>
    public bool Ok => !Failure.IsRefusal;

    /// <summary>Creates a refused read.</summary>
    /// <param name="refusal">The refusal name.</param>
    /// <param name="detail">The refusal detail.</param>
    /// <returns>The refused read.</returns>
    public static WorldWireFrameRead Refused(WorldWireRefusal refusal, string detail) =>
        new(Kind: 0, Body: [], Failure: new WorldWireFailure(Refusal: refusal, Detail: detail));
}

/// <summary>The one framing discipline every World socket shares: <c>[u32 following][u8 kind][payload]</c>,
/// little-endian, where <c>following</c> counts the kind byte plus the payload and never its own prefix — the exact
/// grammar <see cref="WorldFrameCodec"/> already defines for submissions. A reader is given the cap its caller
/// admits, so an oversized length is refused by name before anything is allocated for it.</summary>
public static class WorldWireFrame {
    /// <summary>The fixed prefix size (length prefix plus kind byte).</summary>
    public const int PrefixBytes = WorldFrameCodec.PrefixBytes;

    /// <summary>Writes one framed kind/body pair and flushes it.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="kind">The frame kind.</param>
    /// <param name="body">The frame body.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The write task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task WriteAsync(Stream stream, byte kind, ReadOnlyMemory<byte> body, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var frame = new byte[checked(PrefixBytes + body.Length)];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: frame, value: checked((uint)(body.Length + sizeof(byte))));
        frame[sizeof(uint)] = kind;
        body.Span.CopyTo(destination: frame.AsSpan(start: PrefixBytes));

        await stream.WriteAsync(buffer: frame, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>Reads exactly one frame.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="maxFrameBytes">The hard cap on prefix plus body bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The frame, or a named refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task<WorldWireFrameRead> ReadAsync(Stream stream, int maxFrameBytes, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var prefix = new byte[sizeof(uint)];

        if (!await TryReadExactAsync(stream: stream, buffer: prefix, ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
            return WorldWireFrameRead.Refused(refusal: WorldWireRefusal.ConnectionClosed, detail: "the peer closed before a frame prefix arrived");
        }

        var following = BinaryPrimitives.ReadUInt32LittleEndian(source: prefix);
        var cap = (uint)Math.Max(val1: 0, val2: (maxFrameBytes - sizeof(uint)));

        if ((following < sizeof(byte)) || (following > cap)) {
            return WorldWireFrameRead.Refused(refusal: WorldWireRefusal.FrameLengthInvalid, detail: $"prefix declares {following} following bytes; the admitted range is 1..{cap}");
        }

        var frame = new byte[following];

        if (!await TryReadExactAsync(stream: stream, buffer: frame, ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
            return WorldWireFrameRead.Refused(refusal: WorldWireRefusal.ConnectionClosed, detail: $"the peer closed inside a {following}-byte frame");
        }

        return new WorldWireFrameRead(Kind: frame[0], Body: frame[1..], Failure: default);
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct) {
        var offset = 0;

        while (offset < buffer.Length) {
            var read = await stream.ReadAsync(buffer: buffer[offset..], cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

            if (read == 0) {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
