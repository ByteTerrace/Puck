using System.Security.Cryptography;
using Puck.Attestation;
using Puck.Networking;

namespace Puck.World.Protocol;

/// <summary>
/// The federation identity door's <see cref="IAuthenticator"/>: a challenge proof is a signed claim over the
/// challenge nonce, verified with <see cref="Puck.Attestation"/> against the reading world's own <c>admission</c>
/// trust list — <see cref="AttestationTrustMode.SignsDirectly"/> (a pinned per-authority key) or
/// <see cref="AttestationTrustMode.Vouches"/> (root-issuing-subject, subject an owner id). The verified identity
/// <see cref="VerifyAsync"/> returns is the claim's own chain-authenticated subject — never the bytes a caller
/// presented alongside it — so the connection this authenticates can never be steered onto a namespace its
/// signature does not actually name.
/// </summary>
/// <remarks>
/// This door has no per-claim nonce beyond the challenge itself: the claim's opaque payload is required to equal
/// the exact challenge bytes (mirrors <see cref="WorldAdmissionDoor.TryAdmit"/>'s own nonce-binding discipline), so
/// a captured proof only ever verifies against the one nonce it was signed over. <see cref="MaximumClaimAge"/>
/// bounds how long a claim itself (independent of the fresh per-connection nonce) may be presented before it stops
/// being accepted at all, which is the one thing a directed-but-unsequenced claim otherwise leaves entirely to the
/// issuer's own signed window.
/// </remarks>
public sealed class WorldAttestedAuthenticator : IAuthenticator {
    private const int NewChallengeBytes = 32;

    /// <summary>The fixed audience every federation-identity claim is directed at.</summary>
    public const string Audience = "puck.world";
    /// <summary>The fixed purpose every federation-identity claim declares.</summary>
    public const string Purpose = "puck.world.federation-identity";

    private readonly Func<DateTimeOffset> m_now;
    private readonly ISigningOracle? m_oracle;
    private readonly Func<IReadOnlyList<WorldAdmissionEntry>?>? m_trustEntries;

    /// <summary>The verifier-side ceiling on a federation-identity claim's own age, independent of the fresh
    /// per-connection challenge nonce that already bounds replay.</summary>
    public static readonly TimeSpan MaximumClaimAge = TimeSpan.FromMinutes(value: 5);

    internal static readonly IAttestationCodec Codec = new CborAttestationCodec();

    /// <summary>Initializes the authenticator.</summary>
    /// <param name="trustEntries">Reads the reading world's current <c>admission</c> rows fresh at every verify
    /// attempt (a live accessor, not a boot-time snapshot — mirrors <see cref="WorldAdmissionDoor.TryAdmit"/>'s own
    /// per-connection read of <c>WorldServer.Definition.Admission</c>); <see langword="null"/> when this instance
    /// never verifies.</param>
    /// <param name="oracle">Signs this authority's own identity claims; <see langword="null"/> when this instance
    /// never proves.</param>
    /// <param name="now">The verification-boundary wall-clock read, overridable for tests.</param>
    public WorldAttestedAuthenticator(Func<IReadOnlyList<WorldAdmissionEntry>?>? trustEntries = null, ISigningOracle? oracle = null, Func<DateTimeOffset>? now = null) {
        m_trustEntries = trustEntries;
        m_oracle = oracle;
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));
    }

    int IAuthenticator.ChallengeBytes => NewChallengeBytes;

    /// <inheritdoc/>
    /// <remarks>Configured to prove OR to verify — a host's door needs only the latter. A client that needs a proof
    /// from a verify-only instance learns so from <see cref="Prove"/>'s own refusal (<c>Puck.World.Server.WorldRemoteAuthority</c>
    /// records that and closes its signing gate on it).</remarks>
    public bool IsConfigured => ((m_oracle is not null) || (m_trustEntries is not null));

    /// <inheritdoc/>
    public byte[] NewChallenge() => RandomNumberGenerator.GetBytes(count: NewChallengeBytes);
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">This instance holds no signing oracle.</exception>
    public byte[] Prove(ReadOnlySpan<byte> challenge) {
        if (m_oracle is null) {
            throw new InvalidOperationException(message: "this authenticator holds no signing oracle; it cannot prove an identity");
        }

        return m_oracle.Sign(
            cancellationToken: CancellationToken.None,
            challenge: challenge
        );
    }
    /// <inheritdoc/>
    public ValueTask<VerifiedPeerIdentity?> VerifyAsync(ReadOnlyMemory<byte> challenge, ReadOnlyMemory<byte> proof, CancellationToken ct) {
        var entries = m_trustEntries?.Invoke();
        if (!WorldAdmissionDoor.TryBuildTrustList(
            defaultMaximumAge: MaximumClaimAge,
            entries: entries,
            reason: out _,
            trustList: out var trustList
        )) {
            return new((VerifiedPeerIdentity?)null);
        }

        if (
            !AttestationChainEnvelope.TryDecode(
            chain: out var chainBytes,
            claim: out var claimBytes,
            reason: out _,
            wire: proof.Span
        ) ||
            (claimBytes is null) ||
            (chainBytes is null)
        ) {
            return new((VerifiedPeerIdentity?)null);
        }

        SignedAttestation claim;
        var chain = new SignedAttestation[chainBytes.Length];

        try {
            claim = AttestationProfile.Base.DecodeAttestation(
                codec: Codec,
                wire: claimBytes
            );

            for (var index = 0; (index < chainBytes.Length); index++) {
                chain[index] = AttestationProfile.Base.DecodeAttestation(
                    codec: Codec,
                    wire: chainBytes[index]
                );
            }
        } catch (FormatException) {
            return new((VerifiedPeerIdentity?)null);
        }

        var result = AttestationProfile.Base.VerifyChain(
            chain: chain,
            claim: claim,
            codec: Codec,
            expectedAudience: Audience,
            expectedPurpose: Purpose,
            now: m_now(),
            trustList: trustList!
        );

        if (
            !result.Verified ||
            result.RequiresReplayCommit
        ) {
            return new((VerifiedPeerIdentity?)null);
        }

        if (
            (claim.PayloadKind != AttestationPayloadKind.Opaque) ||
            !challenge.Span.SequenceEqual(other: claim.PayloadBytes.Span)
        ) {
            return new((VerifiedPeerIdentity?)null);
        }

        bool TrustIsCurrent() {
            var current = m_trustEntries?.Invoke();
            return ReferenceEquals(current, entries) || (current is not null && entries is not null && current.SequenceEqual(entries));
        }
        return new(claim.Header.Subject is { } subject ? new VerifiedPeerIdentity(subject, result.ValidThrough, TrustIsCurrent) : null);
    }
}
