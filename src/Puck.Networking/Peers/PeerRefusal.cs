namespace Puck.Networking.Peers;

/// <summary>The named refusal vocabulary a <see cref="Peer"/> or <see cref="PeerLink"/> returns instead of
/// throwing over bytes another process controls.</summary>
public enum PeerRefusal : byte {
    /// <summary>No refusal.</summary>
    None = 0,
    /// <summary>The peer closed the connection: before the handshake reached an identity decision, or on an
    /// established link (end of stream or a transport exception), where it also names a send on a link that is no
    /// longer open.</summary>
    ConnectionClosed,
    /// <summary>A handshake frame arrived out of order, with the wrong kind, or with a body its reader could not
    /// parse.</summary>
    HandshakeMalformed,
    /// <summary>The offered protocol key does not match this build's <see cref="PeerWireProtocol.ProtocolKey"/>.</summary>
    ProtocolMismatch,
    /// <summary>The identity the peer offered is not the key its transport proved possession of, so a proof over
    /// this connection could have been relayed from another one.</summary>
    ChannelUnbound,
    /// <summary>The peer's Hello offer did not carry the challenge it was later proven over, or the proof did not
    /// verify against the identity that offer named.</summary>
    IdentityUnproven,
    /// <summary>The peer refused this side's handshake and named its refusal in a <see cref="PeerFrameKind.HelloRefused"/>
    /// frame; the detail carries the peer's name for it.</summary>
    RefusedByPeer,
    /// <summary>A message frame's bytes did not decode as a signed attestation at all.</summary>
    MessageUnsigned,
    /// <summary>A message frame decoded, but its claimed domain and subject do not match the identity this link
    /// established at handshake.</summary>
    MessageWrongSigner,
    /// <summary>A message frame named the link's own established identity but failed cryptographic or policy
    /// verification — an invalid signature, a tampered payload, an expired window, or a purpose/audience
    /// mismatch.</summary>
    MessageUnverified,
    /// <summary>The SPKI the peer offered is not a P-256 key its identity algorithm names.</summary>
    IdentityKeyInvalid,
    /// <summary><see cref="PeerWireProtocol.HandshakeTimeout"/> (or <see cref="PeerWireProtocol.ControlStreamTimeout"/>)
    /// expired before the handshake reached an identity decision.</summary>
    HandshakeTimedOut,
    /// <summary>The handshake raised an exception outside the wire vocabulary; the detail names the type.</summary>
    HandshakeFaulted,
    /// <summary>The transport could not connect or authenticate; the detail names the exception.</summary>
    TransportFailed,
    /// <summary>A message frame's outer block framing did not decode; the link stays open.</summary>
    MessageMalformed,
    /// <summary>A frame on an established link violated the frame grammar (length or kind); the link closes because
    /// the stream cannot be resynchronized.</summary>
    FrameMalformed,
    /// <summary>The link's read loop raised an exception outside the wire vocabulary; the link closes.</summary>
    LinkFaulted,
    /// <summary>The link was disposed locally.</summary>
    Disposed,
}
/// <summary>One named <see cref="PeerRefusal"/> plus narration. <paramref name="Detail"/> is local narration for
/// logs and events: within <c>Puck.Networking.Peers</c> it is never written to the peer — only the
/// <paramref name="Refusal"/> byte crosses the wire, in a <see cref="PeerFrameKind.HelloRefused"/> frame — so it
/// may quote verifier internals (clock values, fingerprints, exception messages) without becoming an
/// oracle.</summary>
/// <param name="Refusal">The stable refusal name.</param>
/// <param name="Detail">The human-readable detail, kept on this side.</param>
public readonly record struct PeerFailure(PeerRefusal Refusal, string Detail) {
    /// <summary>Gets a value indicating whether this failure names a refusal.</summary>
    public bool IsRefusal => (Refusal != PeerRefusal.None);

    /// <summary>Formats the stable name beside its detail: <c>"{Refusal}: {Detail}"</c> when a refusal is named,
    /// else the detail alone when there is one, else <c>"ok"</c>.</summary>
    /// <returns>The refusal narration.</returns>
    public override string ToString() => (IsRefusal
        ? $"{Refusal}: {Detail}"
        : (string.IsNullOrEmpty(value: Detail)
            ? "ok"
            : Detail
        )
    );
}
/// <summary>The exception <see cref="Peer.DialAsync"/> raises when the handshake it ran was refused, timed out, or
/// faulted (or its transport never connected), and <see cref="PeerLink.SendAsync"/> raises on a link that is no
/// longer open, carrying the named refusal.</summary>
public sealed class PeerRefusedException : IOException {
    /// <summary>Initializes the exception over one refusal.</summary>
    /// <param name="failure">The named refusal.</param>
    public PeerRefusedException(PeerFailure failure) : base(message: $"the peer operation was refused — {failure}") {
        Failure = failure;
    }
    /// <summary>Initializes an exception naming no refusal.</summary>
    public PeerRefusedException() {
    }
    /// <summary>Initializes an exception with a message and no named refusal.</summary>
    /// <param name="message">The message.</param>
    public PeerRefusedException(string message) : base(message: message) {
    }
    /// <summary>Initializes an exception with a message, an inner exception, and no named refusal.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public PeerRefusedException(string message, Exception innerException) : base(
        innerException: innerException,
        message: message
    ) {
    }

    /// <summary>Gets the named refusal.</summary>
    public PeerFailure Failure { get; }
}
