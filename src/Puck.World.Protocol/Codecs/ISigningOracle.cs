using System.Security.Cryptography;
using Puck.Attestation;

namespace Puck.World.Protocol;

/// <summary>
/// Signs a federation-identity claim over a challenge nonce. <see cref="WorldAttestedAuthenticator"/> never
/// distinguishes which shape produced the bytes it returns — a locally held per-authority key
/// (<see cref="LocalKeySigningOracle"/>, a <see cref="AttestationTrustMode.SignsDirectly"/> claim with no chain) or a
/// per-run key an issuer vouched for (<see cref="IssuedKeySigningOracle"/>, a <see cref="AttestationTrustMode.Vouches"/>
/// claim with a root-issuing-subject chain) both hand back the same wrapped claim+chain envelope
/// (<see cref="AttestationChainEnvelope"/>).
/// </summary>
public interface ISigningOracle {
    /// <summary>Signs a fresh claim over <paramref name="challenge"/>.</summary>
    /// <param name="challenge">The exact bytes to bind into the claim's opaque payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The wire-encoded (<see cref="AttestationChainEnvelope"/>) claim and its chain.</returns>
    byte[] Sign(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken);
}
/// <summary>The one claim layout every federation-identity oracle signs: the challenge, optionally followed by a
/// 16-byte run session, as an opaque payload directed at <see cref="WorldAttestedAuthenticator.Audience"/>.</summary>
internal static class FederationClaims {
    /// <summary>How far each claim backdates its <c>notBefore</c>, so a verifier whose clock trails the signer's by up
    /// to this many seconds still finds the claim valid; the attestation profile carries no verifier-side skew grace
    /// and puts slack in the signed window instead.</summary>
    internal const long ClockSkewSeconds = 60;

    internal static byte[] Sign(ECDsa key, string domain, string subject, ReadOnlySpan<byte> challenge, byte[]? session, long notBefore, long notAfter, IReadOnlyList<byte[]> chain) {
        var codec = WorldAttestedAuthenticator.Codec;
        byte[] payload;

        if (session is { } sessionBytes) {
            payload = new byte[challenge.Length + sessionBytes.Length];
            challenge.CopyTo(destination: payload);
            sessionBytes.CopyTo(destination: payload.AsSpan(start: challenge.Length));
        } else {
            payload = challenge.ToArray();
        }
        var claim = AttestationSigner.SignClaim(
            audience: WorldAttestedAuthenticator.Audience,
            claimBytes: payload,
            codec: codec,
            domain: domain,
            notAfter: notAfter,
            notBefore: notBefore,
            purpose: WorldAttestedAuthenticator.Purpose,
            sequence: null,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: key,
            subject: subject
        );

        return AttestationChainEnvelope.Encode(
            chain: chain,
            claim: codec.EncodeAttestation(attestation: claim)
        );
    }
}
/// <summary>
/// Signs directly with a locally held private key — no chain, no remote round trip. The offline shape: a
/// per-authority keypair pinned into a peer's own <c>admission</c> section as a
/// <see cref="AttestationTrustMode.SignsDirectly"/> entry (<see cref="WorldAdmissionEntry.Subject"/> names the
/// authority namespace <see cref="WorldAttestedAuthenticator.VerifyAsync"/> derives on the far side).
/// </summary>
public sealed class LocalKeySigningOracle : ISigningOracle, IDisposable {
    private readonly string m_domain;
    private readonly ECDsa m_key;
    private readonly Func<DateTimeOffset> m_now;
    private readonly string? m_session;
    private readonly byte[]? m_sessionBytes;
    private readonly string m_subject;
    private readonly TimeSpan m_validity;

    /// <summary>Initializes the oracle over an already-generated key.</summary>
    /// <param name="key">The private signing key. Owned by this instance; disposed with it.</param>
    /// <param name="subject">The authority namespace this key signs as — becomes the verified
    /// <see cref="AttestationHeader.Subject"/>, and must equal the peer's pinned
    /// <see cref="WorldAdmissionEntry.Subject"/> for a <see cref="AttestationTrustMode.SignsDirectly"/> entry
    /// naming this key.</param>
    /// <param name="validity">The signed window every minted claim carries, backdated by the clock-skew allowance and
    /// kept short because this door has no per-claim nonce beyond the challenge itself (see
    /// <see cref="WorldAttestedAuthenticator"/>'s own remarks).</param>
    /// <param name="now">The wall-clock read, overridable for tests; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="session">Optional per-run client session nonce; when supplied, binds those 16 bytes into every
    /// signed challenge payload so <see cref="WorldAttestedAuthenticator.VerifyAsync"/> derives the per-run authority
    /// identity <see cref="AuthorityIdentity"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is null or whitespace.</exception>
    public LocalKeySigningOracle(ECDsa key, string subject, TimeSpan validity, Func<DateTimeOffset>? now = null, Guid? session = null) {
        ArgumentNullException.ThrowIfNull(argument: key);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: subject);

        m_key = key;
        m_domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: key.ExportSubjectPublicKeyInfo());
        m_subject = subject;
        m_validity = validity;
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));
        if (session is { } runSession) {
            m_session = runSession.ToString(format: "N");
            m_sessionBytes = Convert.FromHexString(s: m_session);
        }
    }

    /// <summary>Gets the federation authority identity <see cref="WorldAttestedAuthenticator.VerifyAsync"/> derives
    /// from claims this oracle signs: <c>&lt;domain&gt;/&lt;subject&gt;/&lt;session&gt;</c> for a session-scoped client
    /// run, or <see cref="Subject"/> for a persistent world authority.</summary>
    public string AuthorityIdentity => ((m_session is { } session) ? $"{m_domain}/{m_subject}/{session}" : m_subject);
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
        var notBefore = (m_now().ToUnixTimeSeconds() - FederationClaims.ClockSkewSeconds);

        return FederationClaims.Sign(
            chain: [],
            challenge: challenge,
            domain: m_domain,
            key: m_key,
            notAfter: (notBefore + checked((long)m_validity.TotalSeconds)),
            notBefore: notBefore,
            session: m_sessionBytes,
            subject: m_subject
        );
    }
}
/// <summary>A per-run key's vouched identity as an issuer returned it: the issuer root's domain, the subject the
/// issuer vouched for, the two bindings (root to issuing key, issuing key to the run's key) and the last second the
/// subject binding is valid.</summary>
/// <param name="Domain">The issuer root's key fingerprint, the domain every claim names.</param>
/// <param name="Subject">The subject the issuer's sign-in established.</param>
/// <param name="Chain">The encoded root-to-issuing and issuing-to-subject bindings, in that order.</param>
/// <param name="NotAfter">The last inclusive Unix second the issuing-to-subject binding is valid.</param>
public sealed record WorldIssuedIdentity(string Domain, string Subject, IReadOnlyList<byte[]> Chain, long NotAfter);
/// <summary>
/// Signs with a key generated for this run and never stored, which an issuer vouched for after its sign-in: a
/// <see cref="AttestationTrustMode.Vouches"/> claim under the issuer's root, carrying the two-binding chain the issuer
/// returned. The run's key proves possession by signing every challenge itself, so nothing a verifier sees is a
/// bearer credential. A binding near its end is issued again before the next claim.
/// </summary>
public sealed class IssuedKeySigningOracle : ISigningOracle, IDisposable {
    private readonly Func<ECDsa, CancellationToken, WorldIssuedIdentity> m_issue;
    private readonly Lock m_gate = new();
    private readonly ECDsa m_key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
    private readonly Func<DateTimeOffset> m_now;
    private readonly string m_session;
    private readonly byte[] m_sessionBytes;
    private readonly TimeSpan m_validity;

    private (WorldIssuedIdentity Identity, long IssuedAt)? m_current;

    /// <summary>Initializes the oracle with a fresh P-256 key for this run.</summary>
    /// <param name="issue">Asks the issuer to vouch for the supplied key: it proves possession with that key and
    /// returns the vouched identity, or throws <see cref="InvalidOperationException"/> naming the issuer's refusal.</param>
    /// <param name="validity">The signed window every claim carries, backdated by the clock-skew allowance and never
    /// past the subject binding's own end.</param>
    /// <param name="session">The per-run session bound into every claim's payload.</param>
    /// <param name="now">The wall-clock read, overridable for tests; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="issue"/> is <see langword="null"/>.</exception>
    public IssuedKeySigningOracle(Func<ECDsa, CancellationToken, WorldIssuedIdentity> issue, TimeSpan validity, Guid session, Func<DateTimeOffset>? now = null) {
        ArgumentNullException.ThrowIfNull(argument: issue);

        m_issue = issue;
        m_validity = validity;
        m_session = session.ToString(format: "N");
        m_sessionBytes = Convert.FromHexString(s: m_session);
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));
    }

    /// <summary>Gets the federation authority identity a verifier derives from this oracle's claims:
    /// <c>&lt;domain&gt;/&lt;subject&gt;/&lt;session&gt;</c>.</summary>
    /// <exception cref="InvalidOperationException">No identity has been issued yet, and issuing one is refused.</exception>
    public string AuthorityIdentity {
        get {
            var identity = Current(cancellationToken: CancellationToken.None);

            return $"{identity.Domain}/{identity.Subject}/{m_session}";
        }
    }

    // Returns the vouched identity, issuing again once less than half its life, or ten minutes, remains: early enough
    // that a claim is never signed under a binding that ends before the claim does, late enough to ask rarely.
    private WorldIssuedIdentity Current(CancellationToken cancellationToken) {
        lock (m_gate) {
            var now = m_now().ToUnixTimeSeconds();

            if (m_current is { } current) {
                var margin = Math.Min(val1: 600L, val2: ((current.Identity.NotAfter - current.IssuedAt) / 2));

                if (now < (current.Identity.NotAfter - margin)) {
                    return current.Identity;
                }
            }

            var issued = m_issue(m_key, cancellationToken);

            if (
                string.IsNullOrEmpty(value: issued.Domain) ||
                string.IsNullOrEmpty(value: issued.Subject) ||
                (issued.Chain.Count != 2) ||
                (issued.NotAfter <= now)
            ) {
                throw new InvalidOperationException(message: "the issuer returned an identity that is not a current two-binding chain");
            }
            // A session keeps the namespace it began under: its boot authority, its travelers and its routes all name
            // it. An issuer that now vouches for the same key under another root or subject (its root was replaced, or
            // the sign-in resolved to someone else) has ended this session, and no claim is signed under the new name.
            if (
                (m_current is { } began) &&
                (!string.Equals(a: began.Identity.Domain, b: issued.Domain, comparisonType: StringComparison.Ordinal) ||
                 !string.Equals(a: began.Identity.Subject, b: issued.Subject, comparisonType: StringComparison.Ordinal))
            ) {
                throw new InvalidOperationException(message: $"the issuer now vouches for this run as '{issued.Domain}/{issued.Subject}', and the session began as '{began.Identity.Domain}/{began.Identity.Subject}'; a session keeps the namespace it began under, so this one has ended and the client must be started again to join");
            }
            m_current = (issued, now);

            return issued;
        }
    }

    /// <summary>Asks the issuer for this run's identity now, so a refusal surfaces before the first connection.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The vouched identity.</returns>
    /// <exception cref="InvalidOperationException">The issuer refused or returned an unusable identity.</exception>
    public WorldIssuedIdentity Issue(CancellationToken cancellationToken) => Current(cancellationToken: cancellationToken);
    /// <inheritdoc/>
    public void Dispose() => m_key.Dispose();
    /// <inheritdoc/>
    /// <exception cref="IOException">The binding needed issuing again and the issuer refused or could not be reached;
    /// the connection fails like any other transport failure and its lane retries, because an oracle that holds a key
    /// is never one that cannot prove.</exception>
    public byte[] Sign(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken) {
        WorldIssuedIdentity identity;

        try {
            identity = Current(cancellationToken: cancellationToken);
        } catch (InvalidOperationException exception) {
            throw new IOException(message: $"the federation identity could not be issued again: {exception.Message}", innerException: exception);
        }
        var notBefore = (m_now().ToUnixTimeSeconds() - FederationClaims.ClockSkewSeconds);

        return FederationClaims.Sign(
            chain: identity.Chain,
            challenge: challenge,
            domain: identity.Domain,
            key: m_key,
            notAfter: Math.Min(val1: (notBefore + checked((long)m_validity.TotalSeconds)), val2: identity.NotAfter),
            notBefore: notBefore,
            session: m_sessionBytes,
            subject: identity.Subject
        );
    }
}
