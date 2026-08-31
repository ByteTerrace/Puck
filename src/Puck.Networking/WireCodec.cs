using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Puck.Maths;

namespace Puck.Networking;

/// <summary>A stable transport-level refusal name. Every decoder over untrusted bytes returns one of these rather
/// than throwing: a malformed peer is a refusal, never an invariant violation.</summary>
public enum WireRefusal : byte {
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
public readonly record struct WireFailure(WireRefusal Refusal, string Detail) {
    /// <summary>Gets a value indicating whether this failure names a refusal.</summary>
    public bool IsRefusal => (Refusal != WireRefusal.None);

    /// <summary>Formats the stable name beside its detail.</summary>
    /// <returns>The refusal narration.</returns>
    public override string ToString() => (IsRefusal
        ? $"{Refusal}: {Detail}"
        : "ok"
    );
}
/// <summary>A bounded, forward-only reader over one already-framed payload. Every read is checked against the
/// remaining span; the first underflow latches a refusal and every later read is inert, so a leaf decoder reads its
/// whole shape and asks once — at <see cref="TryFinish"/> — whether the bytes were honest.</summary>
public ref struct WireReader {
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly ReadOnlySpan<byte> m_bytes;

    private int m_offset;
    private WireFailure m_failure;

    /// <summary>Initializes a reader over one payload.</summary>
    /// <param name="bytes">The payload bytes.</param>
    public WireReader(ReadOnlySpan<byte> bytes) {
        m_bytes = bytes;
        m_offset = 0;
        m_failure = default;
    }

    /// <summary>Gets a value indicating whether a refusal has latched.</summary>
    public readonly bool Failed => m_failure.IsRefusal;
    /// <summary>Gets the latched refusal, if any.</summary>
    public readonly WireFailure Failure => m_failure;
    /// <summary>Gets the count of bytes not yet consumed.</summary>
    public readonly int Remaining => (m_bytes.Length - m_offset);

    private ReadOnlySpan<byte> Take(int count) {
        if (
            Failed ||
            (count < 0)
        ) {
            return default;
        }

        if (count > Remaining) {
            Fail(
                refusal: WireRefusal.PayloadTruncated,
                detail: $"{count} bytes were required; {Remaining} remain"
            );

            return default;
        }

        var slice = m_bytes.Slice(
            length: count,
            start: m_offset
        );

        m_offset += count;

        return slice;
    }

    /// <summary>Latches a refusal. The first one wins, so the narration names the original cause.</summary>
    /// <param name="refusal">The refusal name.</param>
    /// <param name="detail">The refusal detail.</param>
    public void Fail(WireRefusal refusal, string detail) {
        if (!Failed) {
            m_failure = new WireFailure(
                Detail: detail,
                Refusal: refusal
            );
        }
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

        if (
            (length < 0) ||
            (length > maxBytes)
        ) {
            Fail(
                detail: $"{field} declares {length} bytes; cap is {maxBytes}",
                refusal: WireRefusal.PayloadTooLarge
            );

            return [];
        }

        var slice = Take(count: length);

        return ((length == 0)
            ? []
            : (slice.IsEmpty
                ? []
                : slice.ToArray()
        ));
    }
    /// <summary>Reads one boolean, refusing any value other than 0 or 1.</summary>
    /// <returns>The boolean.</returns>
    public bool ReadBoolean() {
        var value = ReadByte();

        if (value > 1) {
            Fail(
                detail: $"boolean lane carries {value}, which is neither 0 nor 1",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return (value == 1);
    }
    /// <summary>Reads one byte.</summary>
    /// <returns>The byte, or zero once a refusal has latched.</returns>
    public byte ReadByte() {
        var slice = Take(count: sizeof(byte));

        return (slice.IsEmpty
            ? (byte)0
            : slice[0]
        );
    }
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

        if (
            (value < minimum) ||
            (value > maximum)
        ) {
            Fail(
                detail: $"{field} is {value}; the admitted range is {minimum}..{maximum}",
                refusal: WireRefusal.CountOutOfRange
            );

            return minimum;
        }

        return value;
    }
    /// <summary>Reads one presentation vector, refusing a non-finite lane.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <returns>The value.</returns>
    public Vector3 ReadFiniteVector(string field) {
        var value = new Vector3(
            x: ReadSingle(),
            y: ReadSingle(),
            z: ReadSingle()
        );

        if (
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y) ||
            !float.IsFinite(f: value.Z)
        ) {
            Fail(
                detail: $"{field} is not finite",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return value;
    }
    /// <summary>Reads one fixed-point scalar.</summary>
    /// <returns>The value.</returns>
    public FixedQ4816 ReadFixed() => new(Value: ReadInt64());
    /// <summary>Reads one fixed-point quaternion.</summary>
    /// <returns>The value.</returns>
    public FixedQuaternion ReadFixedQuaternion() => new(
        X: ReadFixed(),
        Y: ReadFixed(),
        Z: ReadFixed(),
        W: ReadFixed()
    );
    /// <summary>Reads one fixed-point vector.</summary>
    /// <returns>The value.</returns>
    public FixedVector3 ReadFixedVector() => new(
        X: ReadFixed(),
        Y: ReadFixed(),
        Z: ReadFixed()
    );
    /// <summary>Reads one little-endian signed 32-bit integer.</summary>
    /// <returns>The value.</returns>
    public int ReadInt32() {
        var slice = Take(count: sizeof(int));

        return (slice.IsEmpty
            ? 0
            : BinaryPrimitives.ReadInt32LittleEndian(source: slice)
        );
    }
    /// <summary>Reads one little-endian signed 64-bit integer.</summary>
    /// <returns>The value.</returns>
    public long ReadInt64() {
        var slice = Take(count: sizeof(long));

        return (slice.IsEmpty
            ? 0L
            : BinaryPrimitives.ReadInt64LittleEndian(source: slice)
        );
    }
    /// <summary>Reads a nullable UTF-8 string behind its presence bit.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the encoded byte count.</param>
    /// <returns>The string, or <see langword="null"/>.</returns>
    public string? ReadNullableString(string field, int maxBytes = WireLimits.MaxStringBytes) =>
        (ReadBoolean()
            ? ReadString(
                field: field,
                maxBytes: maxBytes
            )
            : null
        );
    /// <summary>Reads one presentation quaternion.</summary>
    /// <returns>The value.</returns>
    public Quaternion ReadQuaternion() => new(
        ReadSingle(),
        ReadSingle(),
        ReadSingle(),
        ReadSingle()
    );
    /// <summary>Reads a required non-blank UTF-8 string.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the encoded byte count.</param>
    /// <returns>The string.</returns>
    public string ReadRequiredString(string field, int maxBytes = WireLimits.MaxStringBytes) {
        var value = ReadString(
            field: field,
            maxBytes: maxBytes
        );

        if (string.IsNullOrWhiteSpace(value: value)) {
            Fail(
                detail: $"{field} is required and carries no text",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return value;
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
            Fail(
                refusal: WireRefusal.PayloadTooLarge,
                detail: $"{field} carries {Remaining} bytes; cap is {maxBytes}"
            );

            return [];
        }

        return Take(count: Remaining).ToArray();
    }
    /// <summary>Reads one little-endian single-precision float. Presentation-only lanes (material color, blend
    /// seconds) carry float; simulation values never do.</summary>
    /// <returns>The value.</returns>
    public float ReadSingle() {
        var slice = Take(count: sizeof(float));

        return (slice.IsEmpty
            ? 0F
            : BinaryPrimitives.ReadSingleLittleEndian(source: slice)
        );
    }
    /// <summary>Reads one UTF-8 string carried behind a 16-bit byte-length prefix.</summary>
    /// <param name="field">The field name used in the refusal narration.</param>
    /// <param name="maxBytes">The hard cap on the encoded byte count.</param>
    /// <returns>The string, or empty once a refusal has latched.</returns>
    public string ReadString(string field, int maxBytes = WireLimits.MaxStringBytes) {
        var length = ((Take(count: sizeof(ushort)) is { IsEmpty: false } prefix)
            ? BinaryPrimitives.ReadUInt16LittleEndian(source: prefix)
            : 0
        );

        if (length > maxBytes) {
            Fail(
                detail: $"{field} declares {length} bytes; cap is {maxBytes}",
                refusal: WireRefusal.StringTooLong
            );

            return string.Empty;
        }

        var slice = Take(count: length);

        if (
            (length == 0) ||
            slice.IsEmpty
        ) {
            return string.Empty;
        }

        try {
            return s_strictUtf8.GetString(bytes: slice);
        } catch (DecoderFallbackException) {
            Fail(
                detail: $"{field} carries bytes that do not decode as UTF-8",
                refusal: WireRefusal.PayloadMalformed
            );

            return string.Empty;
        }
    }
    /// <summary>Reads one little-endian unsigned 32-bit integer.</summary>
    /// <returns>The value.</returns>
    public uint ReadUInt32() {
        var slice = Take(count: sizeof(uint));

        return (slice.IsEmpty
            ? 0U
            : BinaryPrimitives.ReadUInt32LittleEndian(source: slice)
        );
    }
    /// <summary>Reads one little-endian unsigned 64-bit integer.</summary>
    /// <returns>The value.</returns>
    public ulong ReadUInt64() {
        var slice = Take(count: sizeof(ulong));

        return (slice.IsEmpty
            ? 0UL
            : BinaryPrimitives.ReadUInt64LittleEndian(source: slice)
        );
    }
    /// <summary>Completes the read: succeeds only when nothing refused and nothing is left over.</summary>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the payload decoded exactly.</returns>
    public bool TryFinish(out WireFailure failure) {
        if (
            !Failed &&
            (Remaining != 0)
        ) {
            Fail(
                refusal: WireRefusal.PayloadTrailingBytes,
                detail: $"{Remaining} bytes follow the canonical leaf"
            );
        }

        failure = m_failure;

        return !Failed;
    }
}
/// <summary>The representation bounds every wire reader and writer shares.</summary>
public static class WireLimits {
    /// <summary>The hard cap on a serialized world document carried inside one message.</summary>
    public const int MaxDocumentBytes = ((16 * 1024) * 1024);
    /// <summary>The default hard cap on one length-prefixed string's encoded bytes. Every wire string in this
    /// repository is a name, an authority spelling, or a refusal sentence.</summary>
    public const int MaxStringBytes = (16 * 1024);
}
/// <summary>A growable little-endian writer producing one canonical leaf. It is the exact mirror of
/// <see cref="WireReader"/>; an encoder and its decoder are read side by side.</summary>
public sealed class WireWriter {
    private byte[] m_buffer;
    private int m_length;

    /// <summary>Initializes a writer.</summary>
    /// <param name="capacity">The initial buffer size.</param>
    public WireWriter(int capacity = 256) {
        m_buffer = new byte[Math.Max(
            val1: capacity,
            val2: 16
        )];
        m_length = 0;
    }

    /// <summary>Gets the bytes written so far.</summary>
    public int Length => m_length;

    private Span<byte> Reserve(int count) {
        if (checked((m_length + count)) > m_buffer.Length) {
            Array.Resize(
                array: ref m_buffer,
                newSize: Math.Max(
                    val1: (m_buffer.Length * 2),
                    val2: (m_length + count)
                )
            );
        }

        var span = m_buffer.AsSpan(
            length: count,
            start: m_length
        );

        m_length += count;

        return span;
    }

    /// <summary>Copies the written bytes into a new array.</summary>
    /// <returns>The canonical leaf.</returns>
    public byte[] ToArray() => m_buffer.AsSpan(
        length: m_length,
        start: 0
    ).ToArray();
    /// <summary>Writes a length-prefixed byte block.</summary>
    /// <param name="value">The block.</param>
    public void WriteBlock(ReadOnlySpan<byte> value) {
        WriteInt32(value: value.Length);
        WriteBytes(value: value);
    }
    /// <summary>Writes one boolean as a single 0/1 byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value) => WriteByte(value: ((byte)(value
        ? 1
        : 0)));
    /// <summary>Writes one byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteByte(byte value) => Reserve(count: sizeof(byte))[0] = value;
    /// <summary>Writes raw bytes with no prefix.</summary>
    /// <param name="value">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(destination: Reserve(count: value.Length));
    /// <summary>Writes one fixed-point scalar.</summary>
    /// <param name="value">The value.</param>
    public void WriteFixed(FixedQ4816 value) => WriteInt64(value: value.Value);
    /// <summary>Writes one fixed-point quaternion.</summary>
    /// <param name="value">The value.</param>
    public void WriteFixedQuaternion(FixedQuaternion value) {
        WriteFixed(value: value.X);
        WriteFixed(value: value.Y);
        WriteFixed(value: value.Z);
        WriteFixed(value: value.W);
    }
    /// <summary>Writes one fixed-point vector.</summary>
    /// <param name="value">The value.</param>
    public void WriteFixedVector(FixedVector3 value) {
        WriteFixed(value: value.X);
        WriteFixed(value: value.Y);
        WriteFixed(value: value.Z);
    }
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
    /// <summary>Writes a nullable string behind its presence bit.</summary>
    /// <param name="value">The value.</param>
    public void WriteNullableString(string? value) {
        WriteBoolean(value: (value is not null));

        if (value is not null) {
            WriteString(value: value);
        }
    }
    /// <summary>Writes one presentation quaternion.</summary>
    /// <param name="value">The value.</param>
    public void WriteQuaternion(Quaternion value) {
        WriteSingle(value: value.X);
        WriteSingle(value: value.Y);
        WriteSingle(value: value.Z);
        WriteSingle(value: value.W);
    }
    /// <summary>Writes one little-endian single-precision float.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(
        destination: Reserve(count: sizeof(float)),
        value: value
    );
    /// <summary>Writes one UTF-8 string behind a 16-bit byte-length prefix.</summary>
    /// <param name="value">The value; <see langword="null"/> writes an empty string.</param>
    /// <exception cref="ArgumentException">The encoded string exceeds <see cref="WireLimits.MaxStringBytes"/>.</exception>
    public void WriteString(string? value) {
        var bytes = Encoding.UTF8.GetBytes(s: (value ?? string.Empty));

        if (bytes.Length > WireLimits.MaxStringBytes) {
            throw new ArgumentException(
                message: $"a wire string of {bytes.Length} bytes exceeds the {WireLimits.MaxStringBytes}-byte field cap",
                paramName: nameof(value)
            );
        }

        BinaryPrimitives.WriteUInt16LittleEndian(
            destination: Reserve(count: sizeof(ushort)),
            value: ((ushort)bytes.Length)
        );
        WriteBytes(value: bytes);
    }
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
    /// <summary>Writes one presentation vector.</summary>
    /// <param name="value">The value.</param>
    public void WriteVector(Vector3 value) {
        WriteSingle(value: value.X);
        WriteSingle(value: value.Y);
        WriteSingle(value: value.Z);
    }
}
/// <summary>One frame read off a stream: the kind and body, or the named reason there is none.</summary>
/// <param name="Kind">The frame's kind byte.</param>
/// <param name="Body">The frame body, excluding the prefix and kind byte.</param>
/// <param name="Failure">The named refusal when <see cref="Ok"/> is <see langword="false"/>.</param>
public readonly record struct WireFrameRead(byte Kind, byte[] Body, WireFailure Failure) {
    /// <summary>Gets a value indicating whether a frame was read.</summary>
    public bool Ok => !Failure.IsRefusal;

    /// <summary>Creates a refused read.</summary>
    /// <param name="refusal">The refusal name.</param>
    /// <param name="detail">The refusal detail.</param>
    /// <returns>The refused read.</returns>
    public static WireFrameRead Refused(WireRefusal refusal, string detail) =>
        new(
            Kind: 0,
            Body: [],
            Failure: new WireFailure(
                Detail: detail,
                Refusal: refusal
            )
        );
}
/// <summary>The one framing discipline every socket shares: <c>[u32 following][u8 kind][payload]</c>,
/// little-endian, where <c>following</c> counts the kind byte plus the payload and never its own prefix — the exact
/// grammar <see cref="FrameCodec"/> already defines for submissions. A reader is given the cap its caller
/// admits, so an oversized length is refused by name before anything is allocated for it.</summary>
public static class WireFrame {
    /// <summary>The fixed prefix size (length prefix plus kind byte).</summary>
    public const int PrefixBytes = FrameCodec.PrefixBytes;

    /// <summary><see cref="TryReadPrefixedBodyAsync"/>'s outcome — which head step the read stopped at, so each
    /// caller can spell out its own refusal wording (or, for <see cref="PrefixEof"/>, a silent close) from a stable
    /// discriminant rather than re-deriving it from <c>Following</c>/body-length arithmetic.</summary>
    public enum PrefixedBodyOutcome {
        /// <summary>The prefix and following bytes both arrived; <c>Following</c>/<c>Body</c> are populated.</summary>
        Ok,
        /// <summary>The peer disconnected before the 4-byte length prefix completed — a clean close, never a
        /// mid-frame truncation.</summary>
        PrefixEof,
        /// <summary>The declared length exceeds the caller's cap; <c>Following</c> carries the declared value.</summary>
        OverCap,
        /// <summary>The peer disconnected after declaring a length but before the following bytes completed.</summary>
        BodyEof,
    }
    /// <summary>Reads the <c>[u32 following-length][following bytes]</c> head shared by every stream reader on this
    /// grammar: an exact little-endian length prefix, an over-cap refusal before the following bytes are ever
    /// allocated, then exactly <c>Following</c> more bytes (skipped when it is zero). What counts as an EOF-before-
    /// any-frame vs. a truncated frame, what the cap itself is, and what a zero length or an over-cap length means to
    /// the caller are read-head-adjacent but caller-specific policy — this reads only the length and the bytes it
    /// declares.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="cap">The hard cap on the declared length, already reduced for any prefix bytes the caller does
    /// not count against it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome, the declared length (valid whenever the outcome is not <see cref="PrefixedBodyOutcome.PrefixEof"/>),
    /// and the following bytes (populated only on <see cref="PrefixedBodyOutcome.Ok"/>).</returns>
    public static async Task<(PrefixedBodyOutcome Outcome, uint Following, byte[] Body)> TryReadPrefixedBodyAsync(Stream stream, uint cap, CancellationToken ct) {
        var prefix = new byte[sizeof(uint)];

        if (!await HandshakeWireFormat.TryReadExactAsync(
            buffer: prefix,
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false)) {
            return (PrefixedBodyOutcome.PrefixEof, 0, []);
        }

        var following = BinaryPrimitives.ReadUInt32LittleEndian(source: prefix);

        if (following > cap) {
            return (PrefixedBodyOutcome.OverCap, following, []);
        }

        var body = new byte[following];

        if (
            (following > 0) &&
            !await HandshakeWireFormat.TryReadExactAsync(
            buffer: body,
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false)
        ) {
            return (PrefixedBodyOutcome.BodyEof, following, []);
        }

        return (PrefixedBodyOutcome.Ok, following, body);
    }
    /// <summary>Reads exactly one frame.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="maxFrameBytes">The hard cap on prefix plus body bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The frame, or a named refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task<WireFrameRead> ReadAsync(Stream stream, int maxFrameBytes, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var cap = ((uint)Math.Max(
            val1: 0,
            val2: (maxFrameBytes - sizeof(uint))
        ));
        var (outcome, following, frame) = await TryReadPrefixedBodyAsync(
            cap: cap,
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        switch (outcome) {
            case PrefixedBodyOutcome.PrefixEof:
                return WireFrameRead.Refused(
                    detail: "the peer closed before a frame prefix arrived",
                    refusal: WireRefusal.ConnectionClosed
                );
            case PrefixedBodyOutcome.OverCap:
                return WireFrameRead.Refused(
                    detail: $"prefix declares {following} following bytes; the admitted range is 1..{cap}",
                    refusal: WireRefusal.FrameLengthInvalid
                );
            case PrefixedBodyOutcome.BodyEof:
                return WireFrameRead.Refused(
                    detail: $"the peer closed inside a {following}-byte frame",
                    refusal: WireRefusal.ConnectionClosed
                );
        }

        if (following < sizeof(byte)) {
            return WireFrameRead.Refused(
                detail: $"prefix declares {following} following bytes; the admitted range is 1..{cap}",
                refusal: WireRefusal.FrameLengthInvalid
            );
        }

        return new WireFrameRead(
            Kind: frame[0],
            Body: frame[1..],
            Failure: default
        );
    }
    /// <summary>Writes one framed kind/body pair and flushes it.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="kind">The frame kind.</param>
    /// <param name="body">The frame body.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The write task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task WriteAsync(Stream stream, byte kind, ReadOnlyMemory<byte> body, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var frame = FrameCodec.Join(
            kind: kind,
            payload: body.Span
        );

        await stream.WriteAsync(
            buffer: frame,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }
}
