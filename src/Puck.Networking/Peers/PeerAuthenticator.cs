using System.Security.Cryptography;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>One handshake's <see cref="IAuthenticator"/>: proving is signing a claim over the challenge, directed
/// at the peer; verifying is checking a claim against the exact identity the peer offered at Hello — the
/// self-certifying <see cref="KeyId"/> its own SPKI fingerprint names, never an out-of-band admission list.
/// Built fresh per handshake because that offered identity is per-connection, unlike a shared admission-list
/// authenticator. Constructing one imports the offered SPKI, so it is also where a key that is not P-256 is
/// found out.</summary>
internal sealed class PeerAuthenticator : IAuthenticator {
    private readonly string m_expectedAudience;
    private readonly string m_expectedDomain;
    private readonly Func<DateTimeOffset> m_now;
    private readonly PeerIdentity m_prover;
    private readonly TrustList m_trust;

    /// <summary>Initializes the authenticator for one handshake, building the single-entry trust list that pins
    /// the offered SPKI once.</summary>
    /// <param name="prover">This side's own identity, used to sign a proof.</param>
    /// <param name="expectedSubjectPublicKeyInfo">The peer's SPKI, as offered at Hello.</param>
    /// <param name="expectedAudience">This side's own fingerprint — the audience a verified peer proof must name.</param>
    /// <param name="now">The verification-boundary clock read, overridable for tests.</param>
    /// <exception cref="ArgumentException"><paramref name="expectedSubjectPublicKeyInfo"/> is not exactly one SPKI
    /// holding a P-256 key.</exception>
    /// <exception cref="PlatformNotSupportedException"><paramref name="expectedSubjectPublicKeyInfo"/> carries a key
    /// this host's elliptic-curve implementation cannot import (explicit curve parameters on a named-curves-only
    /// host).</exception>
    public PeerAuthenticator(PeerIdentity prover, byte[] expectedSubjectPublicKeyInfo, string expectedAudience, Func<DateTimeOffset>? now = null) {
        m_prover = prover;
        m_expectedDomain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: expectedSubjectPublicKeyInfo);
        m_expectedAudience = expectedAudience;
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));
        m_trust = PeerWireProtocol.SingleEntryTrust(
            id: KeyId.ForSubject(
                algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                domain: m_expectedDomain,
                subject: m_expectedDomain,
                subjectPublicKeyInfo: expectedSubjectPublicKeyInfo
            ),
            maximumAge: (PeerWireProtocol.MaximumIdentityClaimAge + PeerWireProtocol.ClockSkewTolerance),
            reach: "identity",
            subjectPublicKeyInfo: expectedSubjectPublicKeyInfo
        );
    }

    int IAuthenticator.ChallengeBytes => PeerWireProtocol.ChallengeBytes;

    /// <inheritdoc/>
    public bool IsConfigured => true;

    /// <inheritdoc/>
    public byte[] NewChallenge() => RandomNumberGenerator.GetBytes(count: PeerWireProtocol.ChallengeBytes);
    /// <inheritdoc/>
    public byte[] Prove(ReadOnlySpan<byte> challenge) {
        var claim = m_prover.SignClaim(
            audience: m_expectedDomain,
            now: m_now(),
            payload: challenge.ToArray(),
            purpose: PeerWireProtocol.IdentityPurpose,
            validity: PeerWireProtocol.MaximumIdentityClaimAge
        );

        return PeerWireProtocol.Codec.EncodeAttestation(attestation: claim);
    }
    /// <inheritdoc/>
    public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) {
        sourceAuthority = null;

        SignedAttestation claim;

        try {
            claim = PeerWireProtocol.Profile.DecodeAttestation(
                codec: PeerWireProtocol.Codec,
                wire: proof
            );
        } catch (FormatException) {
            return false;
        }

        if (
            !string.Equals(
            a: claim.Header.Domain,
            b: m_expectedDomain,
            comparisonType: StringComparison.Ordinal
        ) ||
            !string.Equals(
            a: claim.Header.Subject,
            b: m_expectedDomain,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return false;
        }

        var result = PeerWireProtocol.Profile.VerifyChain(
            chain: [],
            claim: claim,
            codec: PeerWireProtocol.Codec,
            expectedAudience: m_expectedAudience,
            expectedPurpose: PeerWireProtocol.IdentityPurpose,
            now: m_now(),
            trustList: m_trust
        );

        if (
            !result.Admits(slot: "identity") ||
            !challenge.SequenceEqual(other: claim.PayloadBytes.Span)
        ) {
            return false;
        }

        sourceAuthority = m_expectedDomain;

        return true;
    }
}
