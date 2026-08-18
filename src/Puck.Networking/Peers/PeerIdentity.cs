using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>One peer's key pair and its self-certifying <see cref="KeyId"/>: a subject key whose domain and
/// subject are both its own SPKI fingerprint, so the identity needs no external root or admission list — a peer
/// proves it by signing, never by asserting it.</summary>
public sealed class PeerIdentity : IDisposable {
    private static readonly Oid[] TransportCertificateUsages = [
        new Oid(oid: "1.3.6.1.5.5.7.3.1"),
        new Oid(oid: "1.3.6.1.5.5.7.3.2"),
    ];

    private readonly ECDsa m_key;

    private PeerIdentity(ECDsa key) {
        m_key = key;

        var spki = key.ExportSubjectPublicKeyInfo();
        var fingerprint = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki);

        SubjectPublicKeyInfo = spki;
        Id = KeyId.ForSubject(
            algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            domain: fingerprint,
            subject: fingerprint,
            subjectPublicKeyInfo: spki
        );
    }

    /// <summary>Gets this identity's self-certifying id (<see cref="KeyId.Domain"/> and <see cref="KeyId.Subject"/> both equal its SPKI fingerprint).</summary>
    public KeyId Id { get; }
    /// <summary>Gets the DER-encoded public key every claim this identity signs is verified against.</summary>
    public byte[] SubjectPublicKeyInfo { get; }

    /// <summary>Generates a fresh P-256 identity.</summary>
    /// <returns>The new identity.</returns>
    public static PeerIdentity Create() => new(key: ECDsa.Create(curve: ECCurve.NamedCurves.nistP256));
    /// <summary>Rebuilds an identity from a previously exported PKCS8 private key.</summary>
    /// <param name="pkcs8PrivateKey">The bytes <see cref="ExportPkcs8PrivateKey"/> produced.</param>
    /// <returns>The identity, carrying the same <see cref="Id"/> it had when exported.</returns>
    public static PeerIdentity FromPkcs8PrivateKey(ReadOnlySpan<byte> pkcs8PrivateKey) {
        var key = ECDsa.Create();

        key.ImportPkcs8PrivateKey(
            bytesRead: out _,
            source: pkcs8PrivateKey
        );

        return new PeerIdentity(key: key);
    }
    /// <summary>Loads an identity a prior <see cref="Save(string)"/> persisted.</summary>
    /// <param name="path">The key file's path.</param>
    /// <returns>The identity.</returns>
    public static PeerIdentity Load(string path) => FromPkcs8PrivateKey(pkcs8PrivateKey: File.ReadAllBytes(path: path));

    /// <summary>Mints a self-signed X.509 certificate over this identity's own key — the credential a TLS-bearing
    /// transport presents, whose public key a remote peer's handshake compares against the identity this side
    /// offers. Server- and client-authentication usages are both asserted because a peer plays either TLS role.
    /// The private key is bound to the certificate as a persisted key rather than an ephemeral one, which the
    /// Windows TLS stack requires before it will present the certificate.</summary>
    /// <returns>The certificate, with private key. The caller owns disposal.</returns>
    public X509Certificate2 CreateTransportCertificate() {
        var request = new CertificateRequest(
            hashAlgorithm: HashAlgorithmName.SHA256,
            key: m_key,
            subjectName: $"CN={Id.Domain}"
        );

        request.CertificateExtensions.Add(item: new X509KeyUsageExtension(
            critical: false,
            keyUsages: X509KeyUsageFlags.DigitalSignature
        ));
        request.CertificateExtensions.Add(item: new X509EnhancedKeyUsageExtension(
            critical: false,
            enhancedKeyUsages: [.. TransportCertificateUsages]
        ));

        var now = DateTimeOffset.UtcNow;

        using var ephemeral = request.CreateSelfSigned(
            notAfter: now.AddYears(years: 10),
            notBefore: now.AddDays(days: -1)
        );

        return X509CertificateLoader.LoadPkcs12(
            data: ephemeral.Export(contentType: X509ContentType.Pkcs12),
            keyStorageFlags: X509KeyStorageFlags.Exportable,
            password: null
        );
    }
    /// <summary>Exports this identity's private key so it can be reloaded later with the same <see cref="Id"/>.</summary>
    /// <returns>The PKCS8 private key bytes.</returns>
    public byte[] ExportPkcs8PrivateKey() => m_key.ExportPkcs8PrivateKey();
    /// <summary>Persists this identity's private key to a file <see cref="Load(string)"/> can read back.</summary>
    /// <param name="path">The destination path.</param>
    public void Save(string path) => File.WriteAllBytes(
        bytes: ExportPkcs8PrivateKey(),
        path: path
    );
    /// <summary>Signs an opaque claim under this identity's own id — the shape every handshake proof and every
    /// attested message shares (only <paramref name="purpose"/> tells them apart).</summary>
    /// <param name="purpose">The claim's purpose. Must not be a reserved attestation purpose.</param>
    /// <param name="audience">The verifier this claim is directed at (the peer's own fingerprint).</param>
    /// <param name="payload">The opaque claim bytes.</param>
    /// <param name="now">The signing instant; defaults to the current time.</param>
    /// <param name="validity">How long the claim's signed window stays open; defaults to 30 seconds.</param>
    /// <returns>The signed claim.</returns>
    public SignedAttestation SignClaim(string purpose, string audience, ReadOnlyMemory<byte> payload, DateTimeOffset? now = null, TimeSpan? validity = null) {
        var at = (now ?? DateTimeOffset.UtcNow);
        var window = (validity ?? TimeSpan.FromSeconds(value: 30));

        return AttestationSigner.SignClaim(
            audience: audience,
            claimBytes: payload,
            codec: PeerWireProtocol.Codec,
            domain: Id.Domain,
            notAfter: at.Add(window).ToUnixTimeSeconds(),
            notBefore: at.AddSeconds(seconds: -5).ToUnixTimeSeconds(),
            purpose: purpose,
            sequence: null,
            signerAlgorithm: Id.Algorithm,
            signerKey: m_key,
            subject: Id.Subject!
        );
    }

    /// <inheritdoc/>
    public void Dispose() => m_key.Dispose();
}
