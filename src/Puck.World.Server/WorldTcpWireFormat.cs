using System.Buffers.Binary;
using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The P7 socket's raw byte grammar — shared by <see cref="WorldTcpHost"/> (the server door) and the <c>--connect</c>
/// client harness (<c>Puck.World.WorldRemoteClient</c>), so both sides frame bytes identically without a second
/// definition drifting from the first. Two directions:
/// <list type="bullet">
/// <item><description><b>Upstream</b> (client → server, after Hello): the EXISTING <see cref="WorldFrameCodec"/>
/// grammar, unchanged — <see cref="TryReadLengthPrefixedFrameAsync"/> reads the raw
/// <c>[u32 length][u8 kind][payload]</c> bytes off the stream; <see cref="WorldFrameCodec.TryDecode"/> still owns
/// decoding them into a <see cref="WorldSubmissionPayload"/>.</description></item>
/// <item><description><b>Downstream</b> (server → client): a NEW, deliberately small v1 grammar — a Hello verdict
/// once, then one completion per submitted frame (this v1 socket is strictly request-then-response per connection,
/// so no correlation id travels on the wire; see <see cref="WorldTcpHost"/>'s own remarks). NOT one of
/// <see cref="WorldSubmissionCodec"/>'s twelve leaf kinds — the design's output-lane section names only the
/// Completion lane for v1 (streamed snapshots/definitions/compositions/levers are explicitly NOT carried here).</description></item>
/// </list>
/// </summary>
public static class WorldTcpWireFormat {
    /// <summary>The Hello handshake's fixed size: one little-endian <see cref="WorldProtocol.WireProtocolKey"/>.</summary>
    public const int HelloBytes = sizeof(ulong);

    /// <summary>The hard cap on an UPSTREAM frame's total bytes (prefix + payload) — generous enough for the largest
    /// leaf (<c>Definition</c>, 16 MiB) while still refusing an absurd length before allocating for it.</summary>
    public const int MaxUpstreamFrameBytes = ((16 * 1024 * 1024) + WorldFrameCodec.PrefixBytes);

    /// <summary>The hard cap on a DOWNSTREAM frame's total bytes — every v1 downstream case is a short status/text
    /// reply, never a bulk payload.</summary>
    public const int MaxDownstreamFrameBytes = (64 * 1024);

    /// <summary>The downstream message kinds — Hello's two outcomes, then one per <see cref="WorldSubmissionResult"/>
    /// case, plus a codec/apply-level refusal for a frame that never reached a typed result at all.</summary>
    public enum DownstreamKind : byte {
        /// <summary>The Hello door accepted this connection and admitted a peer body.</summary>
        HelloAccepted,

        /// <summary>The Hello door refused this connection — the socket closes right after.</summary>
        HelloRefused,

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

    /// <summary>Reads one raw <c>[u32 length][…]</c> length-prefixed block — the shape BOTH the upstream
    /// (<see cref="WorldFrameCodec"/>) and downstream grammars share. Returns the WHOLE buffer, prefix included, so
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
