using System.Buffers.Binary;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>A peer control-stream frame's kind byte.</summary>
public enum PeerFrameKind : byte {
    /// <summary>Protocol key, offered SPKI, and a fresh challenge — the opening frame every side writes without
    /// waiting to read the other's.</summary>
    HelloOffer = 1,
    /// <summary>The signed identity claim proving control of the SPKI a <see cref="HelloOffer"/> carried.</summary>
    HelloProof = 2,
    /// <summary>A named pre-identity refusal (<c>[u8 PeerRefusal]</c>), in place of the frame the recipient
    /// expected next.</summary>
    HelloRefused = 3,
    /// <summary>A signed attestation carrying one opaque message payload (<c>[block attestation]</c>).</summary>
    Message = 4,
}
/// <summary>The constants and shared attestation configuration every side of a <see cref="PeerLink"/> handshake
/// or message exchange reads from — one definition, so a mismatch between what one side writes and the other
/// reads is impossible by construction.</summary>
public static class PeerWireProtocol {
    /// <summary>The fixed byte length of a handshake challenge.</summary>
    public const int ChallengeBytes = 32;
    /// <summary>The purpose every handshake identity claim declares.</summary>
    public const string IdentityPurpose = "puck.peer.identity";
    /// <summary>The cap on inbound handshakes in flight at once on one <see cref="Peer"/>; an accepted transport
    /// connection waits for a slot before its handshake starts, so a flood of connections that never complete
    /// their Hello holds at most this many accept tasks, connections, and deadlines.</summary>
    public const int MaxConcurrentHandshakes = 64;
    /// <summary>The hard cap on any one control-stream frame this protocol writes or reads.</summary>
    public const int MaxFrameBytes = (64 * 1024);
    /// <summary>The cap on one message's opaque payload, as <see cref="PeerLink.SendAsync"/> accepts it. It is
    /// the attestation payload cap (<see cref="AttestationResourceLimits.PayloadBytes"/>, 48 KiB), which binds
    /// below <see cref="MaxFrameBytes"/>: a message frame is one block holding one signed attestation, whose
    /// header and signature add a few hundred bytes to the payload, so the profile refuses a payload over this
    /// size before the 64 KiB frame cap is reached, and a payload that fits it always fits the frame.</summary>
    public const int MaxMessagePayloadBytes = AttestationResourceLimits.PayloadBytes;
    /// <summary>The purpose every attested message claim declares.</summary>
    public const string MessagePurpose = "puck.peer.message";

    /// <summary>Gets the clock disagreement between two peers this protocol tolerates. A signer backdates every
    /// claim's <c>notBefore</c> by this much and a verifier accepts a claim up to <c>validity + ClockSkewTolerance</c>
    /// old (every <see cref="SingleEntryTrust"/> caller passes that sum as <c>maximumAge</c>), so a message verifies
    /// for verifier-minus-signer offsets of about [−15 s, +15 s] and an identity proof for about [−15 s, +30 s],
    /// to one-second granularity. This MUST stay a whole number of seconds: <see cref="TrustListEntry"/> validates
    /// every maximum age as whole wire seconds, and claim windows are Unix seconds.</summary>
    public static TimeSpan ClockSkewTolerance { get; } = TimeSpan.FromSeconds(value: 15);
    /// <summary>Gets the codec every attestation on this wire is encoded and decoded under.</summary>
    public static IAttestationCodec Codec { get; } = new CborAttestationCodec();
    /// <summary>Gets the ceiling from an accepted transport connection to its control stream being opened. It is
    /// shorter than <see cref="HandshakeTimeout"/> so a connection that never opens a stream releases its
    /// handshake slot (<see cref="MaxConcurrentHandshakes"/>) quickly; expiry is refused as
    /// <see cref="PeerRefusal.HandshakeTimedOut"/>.</summary>
    public static TimeSpan ControlStreamTimeout { get; } = TimeSpan.FromSeconds(value: 3);
    /// <summary>Gets the ceiling on one handshake over its control stream, to the peer's proof verifying — a
    /// connection that stalls mid-Hello is dropped at it and refused as <see cref="PeerRefusal.HandshakeTimedOut"/>.
    /// A dialer's clock starts once its transport has connected (it opens the stream inside it); an acceptor's
    /// starts once the control stream has been accepted, after <see cref="ControlStreamTimeout"/> has already
    /// bounded the wait for it, so an inbound connection is bounded by the two in sequence.</summary>
    public static TimeSpan HandshakeTimeout { get; } = TimeSpan.FromSeconds(value: 15);
    /// <summary>Gets the maximum age a handshake identity claim's own signed window may span, independent of the
    /// per-connection challenge nonce that already bounds replay.</summary>
    public static TimeSpan MaximumIdentityClaimAge { get; } = TimeSpan.FromSeconds(value: 30);
    /// <summary>Gets the maximum age an attested message claim's own signed window may span.</summary>
    public static TimeSpan MaximumMessageClaimAge { get; } = TimeSpan.FromSeconds(value: 15);
    /// <summary>Gets the profile every attestation on this wire is verified under.</summary>
    public static AttestationProfile Profile { get; } = AttestationProfile.Base;
    /// <summary>Gets the fixed protocol key every <see cref="PeerFrameKind.HelloOffer"/> carries — an ASCII
    /// spelling read as a big-endian <see cref="ulong"/>, so the two are never independently maintained.</summary>
    public static ulong ProtocolKey { get; } = BinaryPrimitives.ReadUInt64BigEndian(source: "PUCKPEER"u8);
    /// <summary>Gets how long a side that has written a <see cref="PeerFrameKind.HelloRefused"/> frame waits for
    /// the peer to close before closing itself, so that two sides refusing each other do not both sit until
    /// <see cref="HandshakeTimeout"/>.</summary>
    public static TimeSpan RefusalDrainTimeout { get; } = TimeSpan.FromMilliseconds(value: 500);
    /// <summary>Gets the ceiling on one message frame's write to the control stream. A transport's stream write
    /// completes only once the peer has granted flow-control credit for the bytes, so a peer that keeps the
    /// connection alive but never grants any could otherwise park a <see cref="PeerLink.SendAsync"/> — and every send
    /// queued behind it — until the link is disposed; at this ceiling the link closes itself as
    /// <see cref="PeerRefusal.ConnectionClosed"/> instead, and the parked sends are refused by that name.</summary>
    public static TimeSpan SendTimeout { get; } = TimeSpan.FromSeconds(value: 15);

    /// <summary>Builds a single-entry trust list pinning exactly one identity — the shape both the handshake
    /// authenticator and a link's per-message verification trust, since a peer substrate has no admission
    /// document to author a wider list from.</summary>
    /// <param name="id">The pinned identity.</param>
    /// <param name="subjectPublicKeyInfo">The pinned identity's actual public key bytes.</param>
    /// <param name="reach">The one slot this entry may reach.</param>
    /// <param name="maximumAge">The maximum age a claim under this entry may be accepted at — the claim's validity
    /// plus <see cref="ClockSkewTolerance"/>, a whole number of seconds.</param>
    /// <returns>The trust list.</returns>
    /// <exception cref="ArgumentException"><paramref name="subjectPublicKeyInfo"/> does not decode as exactly one
    /// SPKI on the curve <paramref name="id"/>'s algorithm names, or does not hash to it.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumAge"/> is not a positive whole number
    /// of seconds.</exception>
    public static TrustList SingleEntryTrust(KeyId id, byte[] subjectPublicKeyInfo, string reach, TimeSpan maximumAge) => new(
        defaultMaximumAge: maximumAge,
        entries: [
            new TrustListEntry(
                MaximumAge: maximumAge,
                Mode: AttestationTrustMode.SignsDirectly,
                PinnedId: id,
                PublicKeySubjectPublicKeyInfo: subjectPublicKeyInfo,
                Reach: new HashSet<string>(comparer: StringComparer.Ordinal) { reach }
            ),
        ]
    );
}
