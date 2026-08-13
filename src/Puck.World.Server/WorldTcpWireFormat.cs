using System.Buffers.Binary;
using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The remote socket's raw byte grammar — shared by <see cref="WorldTcpHost"/> (the server door) and the <c>--connect</c>
/// peer clients, so both sides frame bytes identically without a second
/// definition drifting from the first. Two directions:
/// <list type="bullet">
/// <item><description><b>Upstream</b> (client → server, after Hello): the existing <see cref="WorldFrameCodec"/>
/// grammar, unchanged — <see cref="TryReadLengthPrefixedFrameAsync"/> reads the raw
/// <c>[u32 length][u8 kind][payload]</c> bytes off the stream; <see cref="WorldFrameCodec.TryDecode"/> still owns
/// decoding them into a <see cref="WorldSubmissionPayload"/>.</description></item>
/// <item><description><b>Downstream</b> (server → client): a new, deliberately small v1 grammar — a Hello verdict
/// once, then one completion per submitted frame (this v1 socket is strictly request-then-response per connection,
/// so no correlation id travels on the wire; see <see cref="WorldTcpHost"/>'s own remarks). Not one of
/// <see cref="WorldSubmissionCodec"/>'s twelve leaf kinds — the design's output-lane section names only the
/// Completion lane for v1 (streamed snapshots/definitions/compositions/levers are not carried here).</description></item>
/// </list>
/// </summary>
public static class WorldTcpWireFormat {
    /// <summary>The Hello handshake's fixed size: one little-endian <see cref="WorldProtocol.WireProtocolKey"/>.</summary>
    public const int HelloBytes = sizeof(ulong);

    /// <summary>The hard cap on an upstream frame's total bytes (prefix + payload) — generous enough for the largest
    /// leaf (<c>Definition</c>, 16 MiB) while still refusing an absurd length before allocating for it.</summary>
    public const int MaxUpstreamFrameBytes = ((16 * 1024 * 1024) + WorldFrameCodec.PrefixBytes);

    /// <summary>The hard cap on a downstream frame's total bytes — every v1 downstream case is a short status/text
    /// reply, never a bulk payload.</summary>
    public const int MaxDownstreamFrameBytes = (64 * 1024);

    /// <summary>The downstream message kinds — Hello's two outcomes, then one per <see cref="WorldSubmissionResult"/>
    /// case, plus a codec/apply-level refusal for a frame that never reached a typed result at all.</summary>
    public enum DownstreamKind : byte {
        /// <summary>The Hello door accepted this connection and admitted a peer body.</summary>
        HelloAccepted,

        /// <summary>The Hello door refused this connection — the socket closes right after.</summary>
        HelloRefused,

        /// <summary>The protocol-version check passed; this is the admission door's fresh challenge nonce (see
        /// <see cref="Protocol.WorldAdmissionDoor.NewChallenge"/>). The peer answers with a HelloIdentity frame
        /// (<see cref="WriteHelloIdentityAsync"/>/<see cref="TryReadHelloIdentityAsync"/>) before either
        /// <see cref="HelloAccepted"/> or <see cref="HelloRefused"/> follows.</summary>
        HelloChallenge,

        /// <summary><see cref="WorldSubmissionResult.Ack"/> — the envelope finished draining; no data.</summary>
        Ack,

        /// <summary><see cref="WorldSubmissionResult.Session"/> — a <see cref="SessionReply"/>.</summary>
        Session,

        /// <summary><see cref="WorldSubmissionResult.Query"/> — a <see cref="QueryAnswer"/>.</summary>
        Query,

        /// <summary>The submitted frame refused before it ever became a typed result (a codec refusal, an
        /// unadmitted/mismatched principal, or a capacity refusal).</summary>
        Refusal,
    }

    /// <summary>Reads exactly <paramref name="buffer"/>'s length of bytes, or reports a clean/abrupt disconnect.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="buffer">The exact-size destination.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="true"/> once <paramref name="buffer"/> is full; <see langword="false"/> on EOF.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task<bool> TryReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

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

    /// <summary>Reads one raw <c>[u32 length][…]</c> length-prefixed block — the shape both the upstream
    /// (<see cref="WorldFrameCodec"/>) and downstream grammars share. Returns the whole buffer, prefix included, so
    /// an upstream caller can hand it straight to <see cref="WorldFrameCodec.TryDecode"/>.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="maxTotalBytes">The hard cap on prefix+body bytes — refused before any body allocation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The whole frame buffer, or <see langword="null"/> on a clean/abrupt disconnect or an oversized length.</returns>
    public static async Task<byte[]?> TryReadLengthPrefixedFrameAsync(Stream stream, int maxTotalBytes, CancellationToken ct) {
        var prefix = new byte[sizeof(uint)];

        if (!await TryReadExactAsync(stream: stream, buffer: prefix, ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
            return null;
        }

        var following = BinaryPrimitives.ReadUInt32LittleEndian(source: prefix);

        if (following > (uint)Math.Max(val1: 0, val2: (maxTotalBytes - sizeof(uint)))) {
            return null;
        }

        var whole = new byte[checked(sizeof(uint) + (int)following)];

        prefix.CopyTo(array: whole, index: 0);

        if ((following > 0) && !await TryReadExactAsync(stream: stream, buffer: whole.AsMemory(start: sizeof(uint)), ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
            return null;
        }

        return whole;
    }

    /// <summary>Writes a length-prefixed Hello key upstream (the client's half of the handshake).</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="key">The offered <see cref="WorldProtocol.WireProtocolKey"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task WriteHelloAsync(Stream stream, ulong key, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var buffer = new byte[HelloBytes];

        BinaryPrimitives.WriteUInt64LittleEndian(destination: buffer, value: key);
        await stream.WriteAsync(buffer: buffer, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>Writes a downstream Hello-accepted verdict.</summary>
    public static Task WriteHelloAcceptedAsync(Stream stream, int peerIndex, int generation, int connectionId, CancellationToken ct) {
        var body = new byte[(3 * sizeof(int))];

        BinaryPrimitives.WriteInt32LittleEndian(destination: body, value: peerIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination: body.AsSpan(start: sizeof(int)), value: generation);
        BinaryPrimitives.WriteInt32LittleEndian(destination: body.AsSpan(start: (2 * sizeof(int))), value: connectionId);

        return WriteDownstreamAsync(stream: stream, kind: DownstreamKind.HelloAccepted, body: body, ct: ct);
    }

    /// <summary>Writes a downstream Hello-refused verdict (the caller closes the socket right after).</summary>
    public static Task WriteHelloRefusedAsync(Stream stream, string reason, CancellationToken ct) =>
        WriteDownstreamAsync(stream: stream, kind: DownstreamKind.HelloRefused, body: EncodeText(text: reason), ct: ct);

    /// <summary>Writes the downstream Hello-challenge frame: a fresh admission nonce
    /// (<see cref="Protocol.WorldAdmissionDoor.NewChallenge"/>). Sent once the protocol-version check passes and
    /// before any identity is asked for.</summary>
    public static Task WriteHelloChallengeAsync(Stream stream, byte[] challenge, CancellationToken ct) =>
        WriteDownstreamAsync(stream: stream, kind: DownstreamKind.HelloChallenge, body: challenge, ct: ct);

    /// <summary>The hard cap on the upstream HelloIdentity frame's total bytes — generous for two chain envelopes
    /// plus one claim attestation (all small P-256 payloads), while still refusing an absurd length before allocating
    /// for it.</summary>
    public const int MaxHelloIdentityBytes = (64 * 1024);

    /// <summary>Writes the upstream HelloIdentity frame the peer sends in direct response to a HelloChallenge,
    /// before the ordinary submission frame loop begins: its attestation chain (0-2 bindings, root-to-subject order —
    /// empty for a <c>signsDirectly</c> identity), then its claim, each an already encoded attestation.
    /// This is part of the handshake, not a <see cref="WorldFrameCodec"/>-decoded submission — it carries no kind
    /// byte, exactly like the initial Hello key, because the protocol state machine already knows what must follow
    /// a HelloChallenge.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="chain">The 0-2 encoded binding attestations, root-to-subject order.</param>
    /// <param name="claim">The encoded attestation carrying the claim.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/>, <paramref name="chain"/>, or <paramref name="claim"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chain"/> carries more than two entries.</exception>
    public static async Task WriteHelloIdentityAsync(Stream stream, IReadOnlyList<byte[]> chain, byte[] claim, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);
        ArgumentNullException.ThrowIfNull(argument: chain);
        ArgumentNullException.ThrowIfNull(argument: claim);

        if ((uint)chain.Count > 2) {
            throw new ArgumentOutOfRangeException(paramName: nameof(chain), actualValue: chain.Count, message: "a attestation chain is at most two bindings deep.");
        }

        using var body = new MemoryStream();

        body.WriteByte(value: (byte)chain.Count);

        foreach (var envelope in chain) {
            WriteLengthPrefixedTo(stream: body, bytes: envelope);
        }

        WriteLengthPrefixedTo(stream: body, bytes: claim);

        var whole = body.ToArray();
        var frame = new byte[checked(sizeof(uint) + whole.Length)];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: frame, value: checked((uint)whole.Length));
        whole.CopyTo(array: frame, index: sizeof(uint));

        await stream.WriteAsync(buffer: frame, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static void WriteLengthPrefixedTo(Stream stream, byte[] bytes) {
        Span<byte> prefix = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: prefix, value: checked((uint)bytes.Length));
        stream.Write(buffer: prefix);
        stream.Write(buffer: bytes);
    }

    /// <summary>
    /// <see cref="TryReadHelloIdentityAsync"/>'s closed read-outcome union. A frame either decodes
    /// (<see cref="Ok"/>), the peer disconnects before any frame length was declared (<see cref="Eof"/> — a
    /// correct, silent close, never a refusal), or bytes arrive that violate the HelloIdentity grammar
    /// (<see cref="Malformed"/>) — including a disconnect after a length prefix already declared a frame, which is
    /// a truncated frame rather than a clean close.
    /// </summary>
    public abstract record HelloIdentityReadResult {
        private HelloIdentityReadResult() {
        }

        /// <summary>The frame decoded to a well-formed attestation chain and claim, with no bytes left over.</summary>
        /// <param name="Chain">The 0-2 encoded binding attestations, root-to-subject order.</param>
        /// <param name="Claim">The encoded attestation carrying the claim.</param>
        public sealed record Ok(IReadOnlyList<byte[]> Chain, byte[] Claim) : HelloIdentityReadResult;

        /// <summary>The peer disconnected before a length prefix declared a frame.</summary>
        public sealed record Eof : HelloIdentityReadResult {
            /// <summary>The single shared instance — value-free, so one instance serves every disconnect.</summary>
            public static readonly Eof Instance = new();
        }

        /// <summary>Bytes arrived but violate the HelloIdentity wire grammar.</summary>
        /// <param name="Reason">A fixed-shape description of the violation. Never includes attacker-supplied bytes.</param>
        public sealed record Malformed(string Reason) : HelloIdentityReadResult;
    }

    /// <summary>Reads the upstream HelloIdentity frame the server expects right after sending a HelloChallenge (see
    /// <see cref="WriteHelloIdentityAsync"/>). Distinguishes a genuine pre-frame disconnect
    /// (<see cref="HelloIdentityReadResult.Eof"/>, silent — the caller closes the socket with no reply) from bytes
    /// that arrived but violate the frame's grammar (<see cref="HelloIdentityReadResult.Malformed"/>, carrying a
    /// fixed-shape reason the caller reports by name) — a half-close after a length prefix already declared a frame
    /// is the latter, since the send side is still open for the named refusal.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The read outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task<HelloIdentityReadResult> TryReadHelloIdentityAsync(Stream stream, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var prefix = new byte[sizeof(uint)];

        if (!await TryReadExactAsync(stream: stream, buffer: prefix, ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
            return HelloIdentityReadResult.Eof.Instance;
        }

        var following = BinaryPrimitives.ReadUInt32LittleEndian(source: prefix);

        if (following > (uint)Math.Max(val1: 0, val2: (MaxHelloIdentityBytes - sizeof(uint)))) {
            return new HelloIdentityReadResult.Malformed(Reason: "the declared frame length exceeds the HelloIdentity frame cap");
        }

        if (following == 0) {
            return new HelloIdentityReadResult.Malformed(Reason: "the frame carries no chain-count byte");
        }

        var body = new byte[following];

        if (!await TryReadExactAsync(stream: stream, buffer: body, ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
            return new HelloIdentityReadResult.Malformed(Reason: "the connection closed before the declared frame's body completed");
        }

        var offset = 0;
        var chainCount = body[offset++];

        if (chainCount > 2) {
            return new HelloIdentityReadResult.Malformed(Reason: "the chain-count byte exceeds the two-binding attestation limit");
        }

        var chain = new byte[chainCount][];

        for (var index = 0; (index < chainCount); index++) {
            if (!TryReadLengthPrefixedFrom(bytes: body, offset: ref offset, value: out var envelope)) {
                return new HelloIdentityReadResult.Malformed(Reason: "a chain envelope's length prefix or body is truncated");
            }

            chain[index] = envelope;
        }

        if (!TryReadLengthPrefixedFrom(bytes: body, offset: ref offset, value: out var claim)) {
            return new HelloIdentityReadResult.Malformed(Reason: "the claim attestation's length prefix or body is truncated");
        }

        if (offset != body.Length) {
            return new HelloIdentityReadResult.Malformed(Reason: "the frame carries trailing bytes after the claim attestation");
        }

        return new HelloIdentityReadResult.Ok(Chain: chain, Claim: claim);
    }

    private static bool TryReadLengthPrefixedFrom(byte[] bytes, ref int offset, out byte[] value) {
        value = [];

        if ((offset + sizeof(uint)) > bytes.Length) {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes.AsSpan(start: offset));

        offset += sizeof(uint);

        if ((length > int.MaxValue) || ((offset + (long)length) > bytes.Length)) {
            return false;
        }

        value = bytes.AsSpan(start: offset, length: (int)length).ToArray();
        offset += (int)length;

        return true;
    }

    /// <summary>Writes a downstream refusal for a frame that never reached a typed result.</summary>
    public static Task WriteRefusalAsync(Stream stream, string reason, CancellationToken ct) =>
        WriteDownstreamAsync(stream: stream, kind: DownstreamKind.Refusal, body: EncodeText(text: reason), ct: ct);

    /// <summary>Writes a downstream <see cref="WorldSubmissionResult"/> as the v1 Completion lane.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="result">The typed submission result.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static Task WriteResultAsync(Stream stream, WorldSubmissionResult result, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: result);

        switch (result) {
            case WorldSubmissionResult.Ack:
                return WriteDownstreamAsync(stream: stream, kind: DownstreamKind.Ack, body: [], ct: ct);
            case WorldSubmissionResult.Session session: {
                // Tightly packed: [u8 Accepted][i32 AssignedIndex][u16 reasonLen][reason utf8].
                var reasonBytes = Encoding.UTF8.GetBytes(s: session.Reply.Reason);
                var body = new byte[(sizeof(byte) + sizeof(int) + sizeof(ushort) + reasonBytes.Length)];

                body[0] = (byte)(session.Reply.Accepted ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(destination: body.AsSpan(start: sizeof(byte)), value: session.Reply.AssignedIndex);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: body.AsSpan(start: (sizeof(byte) + sizeof(int))), value: checked((ushort)reasonBytes.Length));
                reasonBytes.CopyTo(destination: body.AsSpan(start: (sizeof(byte) + sizeof(int) + sizeof(ushort))));

                return WriteDownstreamAsync(stream: stream, kind: DownstreamKind.Session, body: body, ct: ct);
            }
            case WorldSubmissionResult.Query query: {
                var textBytes = Encoding.UTF8.GetBytes(s: query.Answer.Text);
                var body = new byte[(sizeof(byte) + sizeof(ushort) + textBytes.Length)];

                body[0] = (byte)(query.Answer.Refused ? 1 : 0);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: body.AsSpan(start: sizeof(byte)), value: checked((ushort)textBytes.Length));
                textBytes.CopyTo(destination: body.AsSpan(start: (sizeof(byte) + sizeof(ushort))));

                return WriteDownstreamAsync(stream: stream, kind: DownstreamKind.Query, body: body, ct: ct);
            }
            default:
                return WriteDownstreamAsync(stream: stream, kind: DownstreamKind.Refusal, body: EncodeText(text: $"no downstream encoding for {result.GetType().Name}"), ct: ct);
        }
    }

    /// <summary>Reads one downstream frame — the client's one reader for both the Hello verdict and every completion.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The decoded kind and its raw body, or <see langword="null"/> on disconnect.</returns>
    public static async Task<(DownstreamKind Kind, byte[] Body)?> TryReadDownstreamAsync(Stream stream, CancellationToken ct) {
        var whole = await TryReadLengthPrefixedFrameAsync(stream: stream, maxTotalBytes: MaxDownstreamFrameBytes, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        if ((whole is not { Length: > sizeof(uint) } bytes)) {
            return null;
        }

        var kind = (DownstreamKind)bytes[sizeof(uint)];

        if (!Enum.IsDefined(value: kind)) {
            return null;
        }

        return (kind, bytes[(sizeof(uint) + sizeof(byte))..]);
    }

    private static async Task WriteDownstreamAsync(Stream stream, DownstreamKind kind, byte[] body, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var following = checked(sizeof(byte) + body.Length);
        var frame = new byte[checked(sizeof(uint) + following)];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: frame, value: checked((uint)following));
        frame[sizeof(uint)] = (byte)kind;
        body.CopyTo(array: frame, index: (sizeof(uint) + sizeof(byte)));

        await stream.WriteAsync(buffer: frame, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(s: (text ?? string.Empty));

    /// <summary>Decodes a whole downstream body as raw UTF-8 text — the shape <see cref="EncodeText"/> writes for
    /// <see cref="DownstreamKind.HelloRefused"/> and <see cref="DownstreamKind.Refusal"/>: a single text field with
    /// no internal length prefix, since the outer frame's own u32 length already delimits it. Distinct from
    /// <see cref="ReadLengthPrefixedString"/>, which decodes one length-prefixed field inside a body that packs
    /// several (Session's <c>[u8][i32][u16 len][text]</c>, Query's <c>[u8][u16 len][text]</c>) — reading a
    /// single-field body with that method misreads the text's own first two bytes as a length header.</summary>
    /// <param name="body">The whole downstream body.</param>
    /// <returns>The decoded string.</returns>
    public static string DecodeText(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(bytes: body);

    /// <summary>Decodes a <c>[u16 len][utf8 bytes]</c> string at <paramref name="offset"/>, advancing it past the field.</summary>
    /// <param name="body">The frame body.</param>
    /// <param name="offset">The read cursor, advanced past the decoded field.</param>
    /// <returns>The decoded string.</returns>
    public static string ReadLengthPrefixedString(ReadOnlySpan<byte> body, ref int offset) {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(source: body[offset..]);

        offset += sizeof(ushort);

        var text = Encoding.UTF8.GetString(bytes: body.Slice(start: offset, length: length));

        offset += length;

        return text;
    }
}
