using System.Security.Cryptography;
using Puck.Attestation;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// Signs a federation-identity claim over a challenge nonce. <see cref="WorldAttestedAuthenticator"/> never
/// distinguishes which shape produced the bytes it returns — a locally held per-authority key
/// (<see cref="LocalKeySigningOracle"/>, a <see cref="AttestationTrustMode.SignsDirectly"/> claim with no chain) or
/// a remote issuer consulted per connection attempt (a <see cref="AttestationTrustMode.Vouches"/> claim with a
/// root-issuing-subject chain, obtained from the platform at connect time) both hand back the same wrapped
/// claim+chain envelope (<see cref="AttestationChainEnvelope"/>).
/// </summary>
public interface ISigningOracle {
    /// <summary>Signs a fresh claim over <paramref name="challenge"/>.</summary>
    /// <param name="challenge">The exact bytes to bind into the claim's opaque payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The wire-encoded (<see cref="AttestationChainEnvelope"/>) claim and its chain.</returns>
    byte[] Sign(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken);
}
/// <summary>
/// Signs directly with a locally held private key — no chain, no remote round trip. The offline shape: a
/// per-authority keypair pinned into a peer's own <c>admission</c> section as a
/// <see cref="AttestationTrustMode.SignsDirectly"/> entry (<see cref="WorldAdmissionEntry.Subject"/> names the
/// authority namespace <see cref="WorldAttestedAuthenticator.TryVerify"/> derives on the far side).
/// </summary>
public sealed class LocalKeySigningOracle : ISigningOracle, IDisposable {
    private readonly string m_domain;
    private readonly ECDsa m_key;
    private readonly Func<DateTimeOffset> m_now;
    private readonly TimeSpan m_validity;
    private readonly string m_subject;

    /// <summary>Initializes the oracle over an already-generated key.</summary>
    /// <param name="key">The private signing key. Owned by this instance; disposed with it.</param>
    /// <param name="subject">The authority namespace this key signs as — becomes the verified
    /// <see cref="AttestationHeader.Subject"/>, and must equal the peer's pinned
    /// <see cref="WorldAdmissionEntry.Subject"/> for a <see cref="AttestationTrustMode.SignsDirectly"/> entry
    /// naming this key.</param>
    /// <param name="validity">The signed window every minted claim carries, kept short because this door has no
    /// per-claim nonce beyond the challenge itself (see <see cref="WorldAttestedAuthenticator"/>'s own remarks).</param>
    /// <param name="now">The wall-clock read, overridable for tests; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is null or whitespace.</exception>
    public LocalKeySigningOracle(ECDsa key, string subject, TimeSpan validity, Func<DateTimeOffset>? now = null) {
        ArgumentNullException.ThrowIfNull(argument: key);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: subject);

        m_key = key;
        m_domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: key.ExportSubjectPublicKeyInfo());
        m_subject = subject;
        m_validity = validity;
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));
    }

    /// <summary>Gets the signing key's own domain fingerprint — the value a peer's
    /// <see cref="WorldAdmissionEntry.Domain"/> must pin for a <see cref="AttestationTrustMode.SignsDirectly"/>
    /// entry naming <see cref="Subject"/>.</summary>
    public string Domain => m_domain;
    /// <summary>Gets the signing key's actual <c>SubjectPublicKeyInfo</c> bytes — what a peer's
    /// <see cref="WorldAdmissionEntry.PublicKey"/> pins alongside <see cref="Domain"/>.</summary>
    public byte[] PublicKeySubjectPublicKeyInfo => m_key.ExportSubjectPublicKeyInfo();
    /// <summary>Gets the authority namespace this oracle signs as.</summary>
    public string Subject => m_subject;

    /// <inheritdoc/>
    public void Dispose() => m_key.Dispose();
    /// <inheritdoc/>
    public byte[] Sign(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken) {
        var codec = WorldAttestedAuthenticator.Codec;
        var now = m_now();
        var seconds = now.ToUnixTimeSeconds();
        var claim = AttestationSigner.SignClaim(
            audience: WorldAttestedAuthenticator.Audience,
            claimBytes: challenge.ToArray(),
            codec: codec,
            domain: m_domain,
            notAfter: (seconds + checked((long)m_validity.TotalSeconds)),
            notBefore: seconds,
            purpose: WorldAttestedAuthenticator.Purpose,
            sequence: null,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: m_key,
            subject: m_subject
        );

        return AttestationChainEnvelope.Encode(
            chain: [],
            claim: codec.EncodeAttestation(attestation: claim)
        );
    }
}
