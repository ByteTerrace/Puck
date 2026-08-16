using System.Buffers.Binary;
using System.Text;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The remote socket's downstream reply grammar — shared by <see cref="WorldTcpHost"/> (the server door) and the
/// <c>--connect</c> peer clients, so both sides frame bytes identically without a second definition drifting from
/// the first. Upstream (client → server, after Hello) rides the existing <see cref="WorldFrameCodec"/> grammar over
/// <see cref="HandshakeWireFormat.TryReadLengthPrefixedFrameAsync"/>'s raw read; Hello and identity ride
/// <see cref="HandshakeWireFormat"/> directly. Downstream (server → client) is this type's own, deliberately small
/// v1 grammar: a Hello verdict once, then one completion per submitted frame (this v1 socket is strictly
/// request-then-response per connection, so no correlation id travels on the wire; see <see cref="WorldTcpHost"/>'s
/// own remarks) — not one of <see cref="WorldSubmissionCodec"/>'s twelve leaf kinds, since v1 carries only the
/// Completion lane (streamed snapshots/definitions/compositions/levers are not carried here).
/// </summary>
public static class WorldTcpWireFormat {
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
    private static Task WriteDownstreamAsync(Stream stream, DownstreamKind kind, byte[] body, CancellationToken ct) => WireFrame.WriteAsync(
        body: body,
        ct: ct,
        kind: ((byte)kind),
        stream: stream
    );

    /// <summary>Decodes a whole downstream body as raw UTF-8 text — the shape <see cref="EncodeText"/> writes for
    /// <see cref="DownstreamKind.HelloRefused"/> and <see cref="DownstreamKind.Refusal"/>: a single text field with
    /// no internal length prefix, since the outer frame's own u32 length already delimits it. Distinct from
    /// <see cref="ReadLengthPrefixedString"/>, which decodes one length-prefixed field inside a body that packs
    /// several (Session's <c>[u8][i32][u16 len][text]</c>, Query's <c>[u8][u16 len][text]</c>) — reading a
    /// single-field body with that method misreads the text's own first two bytes as a length header.</summary>
    /// <param name="body">The whole downstream body.</param>
    /// <returns>The decoded string.</returns>
    public static string DecodeText(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(bytes: body);
    /// <summary>Decodes a <c>[u16 len][utf8 bytes]</c> string at <paramref name="offset"/>, advancing it past the field.
    /// Bounds-checked against <paramref name="body"/>'s own length: a peer that declares a length running past the
    /// end of the body never throws past this reader — it reports <paramref name="ok"/> <see langword="false"/>
    /// instead, so the caller reports a named refusal rather than an escaping exception. See this type's remarks on
    /// <see cref="DecodeText"/> for the body shapes this reads inside.</summary>
    /// <param name="body">The frame body.</param>
    /// <param name="offset">The read cursor, advanced past the decoded field on success; past the end of
    /// <paramref name="body"/> on failure.</param>
    /// <param name="ok">Whether the field's length prefix and bytes both fit inside <paramref name="body"/>.</param>
    /// <returns>The decoded string on success; empty when <paramref name="ok"/> is <see langword="false"/>.</returns>
    public static string ReadLengthPrefixedString(ReadOnlySpan<byte> body, ref int offset, out bool ok) {
        if ((offset + sizeof(ushort)) > body.Length) {
            offset = body.Length;
            ok = false;

            return string.Empty;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(source: body[offset..]);

        offset += sizeof(ushort);

        if ((offset + length) > body.Length) {
            offset = body.Length;
            ok = false;

            return string.Empty;
        }

        var text = Encoding.UTF8.GetString(bytes: body.Slice(
            length: length,
            start: offset
        ));

        offset += length;
        ok = true;

        return text;
    }
    /// <summary>Reads one downstream frame — the client's one reader for both the Hello verdict and every completion.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The decoded kind and its raw body, or <see langword="null"/> on disconnect.</returns>
    public static async Task<(DownstreamKind Kind, byte[] Body)?> TryReadDownstreamAsync(Stream stream, CancellationToken ct) {
        var whole = await HandshakeWireFormat.TryReadLengthPrefixedFrameAsync(
            ct: ct,
            maxTotalBytes: MaxDownstreamFrameBytes,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if ((whole is not { Length: > sizeof(uint) } bytes)) {
            return null;
        }

        var kind = ((DownstreamKind)bytes[sizeof(uint)]);

        if (!Enum.IsDefined(value: kind)) {
            return null;
        }

        return (kind, bytes[(sizeof(uint) + sizeof(byte))..]);
    }
    /// <summary>Writes a downstream Hello-accepted verdict.</summary>
    public static Task WriteHelloAcceptedAsync(Stream stream, int peerIndex, int generation, int connectionId, CancellationToken ct) {
        var body = new byte[(3 * sizeof(int))];

        BinaryPrimitives.WriteInt32LittleEndian(
            destination: body,
            value: peerIndex
        );
        BinaryPrimitives.WriteInt32LittleEndian(
            destination: body.AsSpan(start: sizeof(int)),
            value: generation
        );
        BinaryPrimitives.WriteInt32LittleEndian(
            destination: body.AsSpan(start: (2 * sizeof(int))),
            value: connectionId
        );

        return WriteDownstreamAsync(
            body: body,
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
                    body: [],
                    ct: ct,
                    kind: DownstreamKind.Ack,
                    stream: stream
                );
            case WorldSubmissionResult.Session session: {
                    // Tightly packed: [u8 Accepted][i32 AssignedIndex][u16 reasonLen][reason utf8].
                    var reasonBytes = Encoding.UTF8.GetBytes(s: session.Reply.Reason);
                    var body = new byte[(((sizeof(byte) + sizeof(int)) + sizeof(ushort)) + reasonBytes.Length)];

                    body[0] = ((byte)(session.Reply.Accepted
                        ? 1
                        : 0));
                    BinaryPrimitives.WriteInt32LittleEndian(
                        destination: body.AsSpan(start: sizeof(byte)),
                        value: session.Reply.AssignedIndex
                    );
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        destination: body.AsSpan(start: (sizeof(byte) + sizeof(int))),
                        value: checked((ushort)reasonBytes.Length)
                    );
                    reasonBytes.CopyTo(destination: body.AsSpan(start: ((sizeof(byte) + sizeof(int)) + sizeof(ushort))));

                    return WriteDownstreamAsync(
                        body: body,
                        ct: ct,
                        kind: DownstreamKind.Session,
                        stream: stream
                    );
                }
            case WorldSubmissionResult.Query query: {
                    var textBytes = Encoding.UTF8.GetBytes(s: query.Answer.Text);
                    var body = new byte[((sizeof(byte) + sizeof(ushort)) + textBytes.Length)];

                    body[0] = ((byte)(query.Answer.Refused
                        ? 1
                        : 0));
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        destination: body.AsSpan(start: sizeof(byte)),
                        value: checked((ushort)textBytes.Length)
                    );
                    textBytes.CopyTo(destination: body.AsSpan(start: (sizeof(byte) + sizeof(ushort))));

                    return WriteDownstreamAsync(
                        body: body,
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
}
