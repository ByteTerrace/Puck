using System.Buffers.Binary;

namespace Puck.Networking;

/// <summary>
/// The generic Hello/identity handshake grammar every socket dialect built on <see cref="WireFrame"/> shares:
/// the raw byte-exact read primitive, the length-prefixed-frame primitive (whole buffer, prefix included, tolerant
/// of a zero-length body — distinct from <see cref="WireFrame.ReadAsync"/>, which refuses one and returns kind/body
/// split), the fixed-size raw Hello key (no length prefix of its own), and the length-prefixed HelloIdentity
/// attestation-chain frame (a 0-2-entry chain plus a claim, each an already-encoded attestation blob — carried
/// outside any closed kind vocabulary, since the caller's own protocol state machine already knows what must follow
/// a challenge).
/// </summary>
public static class HandshakeWireFormat {
    /// <summary>The Hello handshake's fixed size: one little-endian protocol key.</summary>
    public const int HelloBytes = sizeof(ulong);
    /// <summary>The hard cap on a HelloIdentity frame's total bytes — generous for two chain envelopes plus one
    /// claim attestation (small P-256 payloads), while still refusing an absurd length before allocating for it.</summary>
    public const int MaxHelloIdentityBytes = (64 * 1024);

    // Same [u32 len][bytes] shape as WireReader.ReadBlock/WireWriter.WriteBlock, kept as private byte[]/ref-offset
    // helpers rather than routed through those ref-struct readers/writers: each chain entry needs only a bounds-
    // checked slice out of the already-buffered HelloIdentity body, not a second stateful reader over it.
    private static bool TryReadLengthPrefixedFrom(byte[] bytes, ref int offset, out byte[] value) {
        value = [];

        if ((offset + sizeof(uint)) > bytes.Length) {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes.AsSpan(start: offset));

        offset += sizeof(uint);

        if (
            (length > int.MaxValue) ||
            ((offset + ((long)length)) > bytes.Length)
        ) {
            return false;
        }

        value = bytes.AsSpan(
            length: ((int)length),
            start: offset
        ).ToArray();
        offset += ((int)length);

        return true;
    }
    private static void WriteLengthPrefixedTo(Stream stream, byte[] bytes) {
        Span<byte> prefix = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: prefix,
            value: checked((uint)bytes.Length)
        );
        stream.Write(buffer: prefix);
        stream.Write(buffer: bytes);
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
            var read = await stream.ReadAsync(
                buffer: buffer[offset..],
                cancellationToken: ct
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (read == 0) {
                return false;
            }

            offset += read;
        }

        return true;
    }
    /// <summary>Reads the upstream HelloIdentity frame a peer sends in direct response to a challenge: its
    /// attestation chain (0-2 bindings, root-to-subject order — empty for a directly-signing identity), then its
    /// claim, each an already encoded attestation. Distinguishes a genuine pre-frame disconnect
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

        var cap = ((uint)Math.Max(
            val1: 0,
            val2: (MaxHelloIdentityBytes - sizeof(uint))
        ));
        var (outcome, following, body) = await WireFrame.TryReadPrefixedBodyAsync(
            cap: cap,
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        switch (outcome) {
            case WireFrame.PrefixedBodyOutcome.PrefixEof:
                return HelloIdentityReadResult.Eof.Instance;
            case WireFrame.PrefixedBodyOutcome.OverCap:
                return new HelloIdentityReadResult.Malformed(Reason: "the declared frame length exceeds the HelloIdentity frame cap");
            case WireFrame.PrefixedBodyOutcome.BodyEof:
                return new HelloIdentityReadResult.Malformed(Reason: "the connection closed before the declared frame's body completed");
        }

        if (following == 0) {
            return new HelloIdentityReadResult.Malformed(Reason: "the frame carries no chain-count byte");
        }

        var offset = 0;
        var chainCount = body[offset++];

        if (chainCount > 2) {
            return new HelloIdentityReadResult.Malformed(Reason: "the chain-count byte exceeds the two-binding attestation limit");
        }

        var chain = new byte[chainCount][];

        for (var index = 0; (index < chainCount); index++) {
            if (!TryReadLengthPrefixedFrom(
                bytes: body,
                offset: ref offset,
                value: out var envelope
            )) {
                return new HelloIdentityReadResult.Malformed(Reason: "a chain envelope's length prefix or body is truncated");
            }

            chain[index] = envelope;
        }

        if (!TryReadLengthPrefixedFrom(
            bytes: body,
            offset: ref offset,
            value: out var claim
        )) {
            return new HelloIdentityReadResult.Malformed(Reason: "the claim attestation's length prefix or body is truncated");
        }

        if (offset != body.Length) {
            return new HelloIdentityReadResult.Malformed(Reason: "the frame carries trailing bytes after the claim attestation");
        }

        return new HelloIdentityReadResult.Ok(
            Chain: chain,
            Claim: claim
        );
    }
    /// <summary>Reads one raw <c>[u32 length][…]</c> length-prefixed block. Returns the whole buffer, prefix
    /// included — distinct from <see cref="WireFrame.ReadAsync"/>, which returns kind/body already split and refuses
    /// a zero-length body; this reader tolerates one, for a caller (a leaf decoder expecting the prefix in its own
    /// span) that draws that line itself.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="maxTotalBytes">The hard cap on prefix+body bytes — refused before any body allocation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The whole frame buffer, or <see langword="null"/> on a clean/abrupt disconnect or an oversized length.</returns>
    public static async Task<byte[]?> TryReadLengthPrefixedFrameAsync(Stream stream, int maxTotalBytes, CancellationToken ct) {
        var cap = ((uint)Math.Max(
            val1: 0,
            val2: (maxTotalBytes - sizeof(uint))
        ));
        var (outcome, following, body) = await WireFrame.TryReadPrefixedBodyAsync(
            cap: cap,
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (outcome != WireFrame.PrefixedBodyOutcome.Ok) {
            return null;
        }

        // Unlike WireFrame.ReadAsync/TryReadHelloIdentityAsync, this reader's own contract keeps the length prefix
        // IN the returned buffer (a caller decoding it as one already-framed blob) — re-encode it rather than
        // threading a second buffer shape through the shared read head for one caller.
        var whole = new byte[checked((sizeof(uint) + ((int)following)))];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: whole,
            value: following
        );
        body.CopyTo(
            destination: whole.AsSpan(start: sizeof(uint))
        );

        return whole;
    }
    /// <summary>Writes the Hello key as a fixed <see cref="HelloBytes"/>-byte value with no length prefix of its
    /// own — the opening exchange of every dialect built on this grammar.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="key">The offered opaque wire identity.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task WriteHelloAsync(Stream stream, ulong key, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var buffer = new byte[HelloBytes];

        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: buffer,
            value: key
        );
        await stream.WriteAsync(
            buffer: buffer,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Writes the upstream HelloIdentity frame in direct response to a challenge: an attestation chain
    /// (0-2 bindings, root-to-subject order — empty for a directly-signing identity), then a claim, each an already
    /// encoded attestation. This carries no kind byte — the caller's own protocol state machine already knows what
    /// must follow a challenge.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="chain">The 0-2 encoded binding attestations, root-to-subject order.</param>
    /// <param name="claim">The encoded attestation carrying the claim.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/>, <paramref name="chain"/>, or <paramref name="claim"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chain"/> carries more than two entries.</exception>
    /// <exception cref="ArgumentException">The encoded chain and claim together exceed
    /// <see cref="MaxHelloIdentityBytes"/> — the frame <see cref="TryReadHelloIdentityAsync"/> would refuse is
    /// never written.</exception>
    public static async Task WriteHelloIdentityAsync(Stream stream, IReadOnlyList<byte[]> chain, byte[] claim, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);
        ArgumentNullException.ThrowIfNull(argument: chain);
        ArgumentNullException.ThrowIfNull(argument: claim);

        if (((uint)chain.Count) > 2) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(chain),
                actualValue: chain.Count,
                message: "an attestation chain is at most two bindings deep."
            );
        }

        using var body = new MemoryStream();

        body.WriteByte(value: ((byte)chain.Count));

        foreach (var envelope in chain) {
            WriteLengthPrefixedTo(
                bytes: envelope,
                stream: body
            );
        }

        WriteLengthPrefixedTo(
            bytes: claim,
            stream: body
        );

        var whole = body.ToArray();
        var cap = (MaxHelloIdentityBytes - sizeof(uint));

        if (whole.Length > cap) {
            throw new ArgumentException(message: $"a HelloIdentity frame of {whole.Length} bytes exceeds the {cap}-byte cap its own reader admits");
        }

        var frame = new byte[checked((sizeof(uint) + whole.Length))];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: frame,
            value: checked((uint)whole.Length)
        );
        whole.CopyTo(
            array: frame,
            index: sizeof(uint)
        );

        await stream.WriteAsync(
            buffer: frame,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
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
}
