using System.Text;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The remote socket's downstream reply grammar — shared by <see cref="WorldPeerHost"/> (the server door) and the
/// <c>--connect</c> peer clients, so both sides frame bytes identically without a second definition drifting from
/// the first. Upstream (client → server, after Hello) rides the existing <see cref="WorldFrameCodec"/> grammar over
/// <see cref="HandshakeWireFormat.TryReadLengthPrefixedFrameAsync"/>'s raw read; Hello and identity ride
/// <see cref="HandshakeWireFormat"/> directly. Downstream (server → client) is this type's own, deliberately small
/// v1 grammar: a Hello verdict once, then one completion per submitted frame (this v1 socket is strictly
/// request-then-response per connection, so no correlation id travels on the wire; see <see cref="WorldPeerHost"/>'s
/// own remarks) — not one of <see cref="WorldSubmissionCodec"/>'s twelve leaf kinds, since v1 carries only the
/// Completion lane (streamed snapshots/definitions/compositions/levers are not carried here).
/// </summary>
public static class WorldPeerWireFormat {
    /// <summary>The hard cap on a downstream frame's total bytes — every v1 downstream case is a short status/text
    /// reply, never a bulk payload.</summary>
    public const int MaxDownstreamFrameBytes = (64 * 1024);
    /// <summary>The hard cap on an upstream frame's total bytes (prefix + payload) — generous enough for the largest
    /// leaf (<c>Definition</c>, 16 MiB) while still refusing an absurd length before allocating for it.</summary>
    public const int MaxUpstreamFrameBytes = (((16 * 1024) * 1024) + WorldFrameCodec.PrefixBytes);

    /// <summary>The downstream message kinds — Hello's two outcomes, then one per <see cref="WorldSubmissionResult"/>
    /// case, plus a codec/apply-level refusal for a frame that never reached a typed result at all.</summary>
    public enum DownstreamKind : byte {
        /// <summary>The Hello door accepted this connection and admitted a peer body.</summary>
        HelloAccepted,

        /// <summary>The Hello door refused this connection — the socket closes right after.</summary>
        HelloRefused,

        /// <summary>The protocol-version check passed; this is the admission door's fresh challenge nonce (see
        /// <see cref="Protocol.WorldAdmissionDoor.NewChallenge"/>). The peer answers with a HelloIdentity frame
        /// (<see cref="HandshakeWireFormat.WriteHelloIdentityAsync"/>/<see cref="HandshakeWireFormat.TryReadHelloIdentityAsync"/>)
        /// before either <see cref="HelloAccepted"/> or <see cref="HelloRefused"/> follows.</summary>
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

    private static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(s: (text ?? string.Empty));
    private static Task WriteDownstreamAsync(Stream stream, DownstreamKind kind, ReadOnlyMemory<byte> body, CancellationToken ct) => WireFrame.WriteAsync(
        body: body,
        ct: ct,
        kind: ((byte)kind),
        stream: stream
    );

    /// <summary>Decodes a whole downstream body as raw UTF-8 text — the shape <see cref="EncodeText"/> writes for
    /// <see cref="DownstreamKind.HelloRefused"/> and <see cref="DownstreamKind.Refusal"/>: a single text field with
    /// no internal length prefix, since the outer frame's own u32 length already delimits it.</summary>
    /// <param name="body">The whole downstream body.</param>
    /// <returns>The decoded string.</returns>
    public static string DecodeText(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(bytes: body);
    /// <summary>Decodes one whole downstream frame already in memory — <c>[u32 length][u8 kind][body]</c>, exactly
    /// the bytes <see cref="WriteResultAsync"/> and its siblings write — into its kind and body. The one parse both
    /// <see cref="TryReadDownstreamAsync"/> (over a socket) and the federation Completion lane (which embeds a whole
    /// downstream frame as a response body) share, so the grammar has one home.</summary>
    /// <param name="frame">The whole frame, prefix included.</param>
    /// <param name="kind">The decoded kind on success.</param>
    /// <param name="body">The body, sliced over <paramref name="frame"/> without copying, on success.</param>
    /// <returns><see langword="false"/> when the frame is shorter than its prefix plus kind byte, longer than
    /// <see cref="MaxDownstreamFrameBytes"/>, declares a length other than the bytes that follow the prefix, or names
    /// an undeclared kind.</returns>
    public static bool TryDecodeDownstream(ReadOnlyMemory<byte> frame, out DownstreamKind kind, out ReadOnlyMemory<byte> body) {
        kind = default;
        body = ReadOnlyMemory<byte>.Empty;

        if (
            (frame.Length <= sizeof(uint)) ||
            (frame.Length > MaxDownstreamFrameBytes) ||
            (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source: frame.Span) != ((uint)(frame.Length - sizeof(uint))))
        ) {
            return false;
        }

        var declared = ((DownstreamKind)frame.Span[sizeof(uint)]);

        if (!Enum.IsDefined(value: declared)) {
            return false;
        }

        kind = declared;
        body = frame[(sizeof(uint) + sizeof(byte))..];

        return true;
    }
    /// <summary>Reads one downstream frame — the client's one reader for both the Hello verdict and every completion.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The decoded kind and its raw body — a slice over the frame's own buffer, allocated per frame and never
    /// reused, so it is safe to keep — or <see langword="null"/> on disconnect or a frame that does not decode.</returns>
    public static async Task<(DownstreamKind Kind, ReadOnlyMemory<byte> Body)?> TryReadDownstreamAsync(Stream stream, CancellationToken ct) {
        var whole = await HandshakeWireFormat.TryReadLengthPrefixedFrameAsync(
            ct: ct,
            maxTotalBytes: MaxDownstreamFrameBytes,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (((whole is not null) && TryDecodeDownstream(
            body: out var body,
            frame: whole,
            kind: out var kind
        ))
            ? (kind, body)
            : null
        );
    }
    /// <summary>Writes a downstream Hello-accepted verdict.</summary>
    public static Task WriteHelloAcceptedAsync(Stream stream, int peerIndex, int generation, int connectionId, CancellationToken ct) {
        var writer = new WireWriter(capacity: (3 * sizeof(int)));

        writer.WriteInt32(value: peerIndex);
        writer.WriteInt32(value: generation);
        writer.WriteInt32(value: connectionId);

        return WriteDownstreamAsync(
            body: writer.WrittenMemory,
            ct: ct,
            kind: DownstreamKind.HelloAccepted,
            stream: stream
        );
    }
    /// <summary>Writes the downstream Hello-challenge frame: a fresh admission nonce
    /// (<see cref="Protocol.WorldAdmissionDoor.NewChallenge"/>). Sent once the protocol-version check passes and
    /// before any identity is asked for.</summary>
    public static Task WriteHelloChallengeAsync(Stream stream, byte[] challenge, CancellationToken ct) =>
        WriteDownstreamAsync(
            body: challenge,
            ct: ct,
            kind: DownstreamKind.HelloChallenge,
            stream: stream
        );
    /// <summary>Writes a downstream Hello-refused verdict (the caller closes the socket right after).</summary>
    public static Task WriteHelloRefusedAsync(Stream stream, string reason, CancellationToken ct) =>
        WriteDownstreamAsync(
            stream: stream,
            kind: DownstreamKind.HelloRefused,
            body: EncodeText(text: reason),
            ct: ct
        );
    /// <summary>Writes a downstream refusal for a frame that never reached a typed result.</summary>
    public static Task WriteRefusalAsync(Stream stream, string reason, CancellationToken ct) =>
        WriteDownstreamAsync(
            stream: stream,
            kind: DownstreamKind.Refusal,
            body: EncodeText(text: reason),
            ct: ct
        );
    /// <summary>Writes a downstream <see cref="WorldSubmissionResult"/> as the v1 Completion lane.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="result">The typed submission result.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static Task WriteResultAsync(Stream stream, WorldSubmissionResult result, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: result);

        switch (result) {
            case WorldSubmissionResult.Ack:
                return WriteDownstreamAsync(
                    body: ReadOnlyMemory<byte>.Empty,
                    ct: ct,
                    kind: DownstreamKind.Ack,
                    stream: stream
                );
            case WorldSubmissionResult.Session session: {
                    // [u8 Accepted][i32 AssignedIndex][u16 reasonLen][reason utf8] — WireWriter.WriteBoolean/
                    // WriteInt32/WriteString lay out exactly this shape.
                    var writer = new WireWriter();

                    writer.WriteBoolean(value: session.Reply.Accepted);
                    writer.WriteInt32(value: session.Reply.AssignedIndex);
                    writer.WriteString(value: session.Reply.Reason);

                    return WriteDownstreamAsync(
                        body: writer.WrittenMemory,
                        ct: ct,
                        kind: DownstreamKind.Session,
                        stream: stream
                    );
                }
            case WorldSubmissionResult.Query query: {
                    // [u8 Refused][u16 textLen][text utf8].
                    var writer = new WireWriter();

                    writer.WriteBoolean(value: query.Answer.Refused);
                    writer.WriteString(value: query.Answer.Text);

                    return WriteDownstreamAsync(
                        body: writer.WrittenMemory,
                        ct: ct,
                        kind: DownstreamKind.Query,
                        stream: stream
                    );
                }
            default:
                return WriteDownstreamAsync(
                    stream: stream,
                    kind: DownstreamKind.Refusal,
                    body: EncodeText(text: $"no downstream encoding for {result.GetType().Name}"),
                    ct: ct
                );
        }
    }
    /// <summary>Decodes a <paramref name="kind"/>/<paramref name="body"/> pair from <see cref="TryReadDownstreamAsync"/>
    /// back into its typed <see cref="WorldSubmissionResult"/> — the read-side twin of <see cref="WriteResultAsync"/>,
    /// shared by every consumer that turns a peer's completion frame into a result rather than re-deriving the field
    /// offsets per call site.</summary>
    /// <param name="kind">The downstream kind.</param>
    /// <param name="body">The frame body.</param>
    /// <param name="result">The decoded result on success.</param>
    /// <param name="reason">The refusal text — the peer's own refusal narration for <see cref="DownstreamKind.Refusal"/>,
    /// a truncation detail for a malformed <see cref="DownstreamKind.Session"/>/<see cref="DownstreamKind.Query"/> body,
    /// or empty on success.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> decoded.</returns>
    public static bool TryReadResult(DownstreamKind kind, ReadOnlySpan<byte> body, out WorldSubmissionResult? result, out string reason) {
        switch (kind) {
            case DownstreamKind.Ack:
                result = WorldSubmissionResult.Ack.Instance;
                reason = string.Empty;

                return true;
            case DownstreamKind.Session: {
                    var reader = new WireReader(bytes: body);
                    var accepted = reader.ReadBoolean();
                    var assignedIndex = reader.ReadInt32();
                    var sessionReason = reader.ReadString(field: "session completion reason");

                    if (!reader.TryFinish(failure: out _)) {
                        result = null;
                        reason = "remote authority returned a truncated session completion";

                        return false;
                    }

                    result = new WorldSubmissionResult.Session(Reply: new SessionReply(
                        Accepted: accepted,
                        AssignedIndex: assignedIndex,
                        Reason: sessionReason,
                        RosterEcho: string.Empty
                    ));
                    reason = string.Empty;

                    return true;
                }
            case DownstreamKind.Query: {
                    var reader = new WireReader(bytes: body);
                    var refused = reader.ReadBoolean();
                    var text = reader.ReadString(field: "query completion text");

                    if (!reader.TryFinish(failure: out _)) {
                        result = null;
                        reason = "remote authority returned a truncated query completion";

                        return false;
                    }

                    result = new WorldSubmissionResult.Query(Answer: new QueryAnswer(
                        Text: text,
                        Refused: refused
                    ));
                    reason = string.Empty;

                    return true;
                }
            case DownstreamKind.Refusal:
                result = null;
                reason = DecodeText(body: body);

                return false;
            default:
                result = null;
                reason = $"no downstream decoding for {kind}";

                return false;
        }
    }
}
