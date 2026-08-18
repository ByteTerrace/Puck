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
    /// <summary>The hard cap on any one control-stream frame this protocol writes or reads.</summary>
    public const int MaxFrameBytes = (64 * 1024);
    /// <summary>The purpose every attested message claim declares.</summary>
    public const string MessagePurpose = "puck.peer.message";

    /// <summary>Gets the codec every attestation on this wire is encoded and decoded under.</summary>
    public static IAttestationCodec Codec { get; } = new CborAttestationCodec();
    /// <summary>Gets the ceiling on one handshake, from the transport connection being established to the peer's
    /// proof verifying — a connection that opens no control stream or stalls mid-Hello is dropped at it.</summary>
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

    /// <summary>Builds a single-entry trust list pinning exactly one identity — the shape both the handshake
    /// authenticator and a link's per-message verification trust, since a peer substrate has no admission
    /// document to author a wider list from.</summary>
    /// <param name="id">The pinned identity.</param>
    /// <param name="subjectPublicKeyInfo">The pinned identity's actual public key bytes.</param>
    /// <param name="reach">The one slot this entry may reach.</param>
    /// <param name="maximumAge">The maximum age a claim under this entry may be accepted at.</param>
    /// <returns>The trust list.</returns>
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
