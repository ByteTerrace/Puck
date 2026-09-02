using System.Security.Cryptography;
using Puck.Attestation;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The federation identity door's <see cref="IAuthenticator"/>: a challenge proof is a signed claim over the
/// challenge nonce, verified with <see cref="Puck.Attestation"/> against the reading world's own <c>admission</c>
/// trust list — <see cref="AttestationTrustMode.SignsDirectly"/> (a pinned per-authority key) or
/// <see cref="AttestationTrustMode.Vouches"/> (root-issuing-subject, subject an owner id). The verified identity
/// <see cref="TryVerify"/> returns is the claim's own chain-authenticated subject — never the bytes a caller
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
    /// <summary>The fixed audience every federation-identity claim is directed at.</summary>
    public const string Audience = "puck.world";

    /// <summary>The verifier-side ceiling on a federation-identity claim's own age, independent of the fresh
    /// per-connection challenge nonce that already bounds replay.</summary>
    public static readonly TimeSpan MaximumClaimAge = TimeSpan.FromMinutes(value: 5);

    /// <summary>The fixed purpose every federation-identity claim declares.</summary>
    public const string Purpose = "puck.world.federation-identity";

    internal static readonly IAttestationCodec Codec = new CborAttestationCodec();

    private const int NewChallengeBytes = 32;

    private readonly Func<DateTimeOffset> m_now;
    private readonly ISigningOracle? m_oracle;
    private readonly Func<IReadOnlyList<WorldAdmissionEntry>?>? m_trustEntries;

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
    /// from a verify-only instance learns so from <see cref="Prove"/>'s own refusal (<see cref="WorldRemoteAuthority"/>
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
    public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) {
        sourceAuthority = null;

        if (!WorldAdmissionDoor.TryBuildTrustList(
            defaultMaximumAge: MaximumClaimAge,
            entries: m_trustEntries?.Invoke(),
            reason: out _,
            trustList: out var trustList
        )) {
            return false;
        }

        if (!AttestationChainEnvelope.TryDecode(
            chain: out var chainBytes,
            claim: out var claimBytes,
            reason: out _,
            wire: proof
        ) ||
            (claimBytes is null) ||
            (chainBytes is null)
        ) {
            return false;
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
            return false;
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
            return false;
        }

        if (
            (claim.PayloadKind != AttestationPayloadKind.Opaque) ||
            !challenge.SequenceEqual(other: claim.PayloadBytes.Span)
        ) {
            return false;
        }

        sourceAuthority = claim.Header.Subject;

        return (sourceAuthority is not null);
    }
}
