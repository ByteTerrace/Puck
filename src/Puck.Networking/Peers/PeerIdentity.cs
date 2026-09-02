using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>One peer's key pair and its self-certifying <see cref="KeyId"/>: a subject key whose domain and
/// subject are both its own SPKI fingerprint, so the identity needs no external root or admission list — a peer
/// proves it by signing, never by asserting it. The key is always P-256, the curve
/// <see cref="AttestationAlgorithms.EcdsaP256Sha256"/> names; an identity cannot be built over any other.</summary>
public sealed class PeerIdentity : IDisposable {
    private static readonly Oid[] TransportCertificateUsages = [
        new Oid(oid: "1.3.6.1.5.5.7.3.1"),
        new Oid(oid: "1.3.6.1.5.5.7.3.2"),
    ];

    private readonly ECDsa m_key;

    private PeerIdentity(ECDsa key) {
        if (!AttestationCurves.IsNistP256(curve: key.ExportParameters(includePrivateParameters: false).Curve)) {
            throw new ArgumentException(
                message: $"a peer identity's key must be on P-256, the curve {AttestationAlgorithms.EcdsaP256Sha256} names",
                paramName: nameof(key)
            );
        }

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
    /// <summary>Rebuilds an identity from a previously exported PKCS8 private key, through
    /// <see cref="AttestationKeys.ImportPkcs8PrivateKey"/>: the whole span must be exactly one key, and the key
    /// must be on P-256.</summary>
    /// <param name="pkcs8PrivateKey">The bytes <see cref="ExportPkcs8PrivateKey"/> produced.</param>
    /// <returns>The identity, carrying the same <see cref="Id"/> it had when exported.</returns>
    /// <exception cref="ArgumentException">The bytes carry trailing data after the key, or the key is not on P-256.</exception>
    /// <exception cref="CryptographicException">The bytes do not decode as a PKCS8 private key.</exception>
    public static PeerIdentity FromPkcs8PrivateKey(ReadOnlySpan<byte> pkcs8PrivateKey) => new(key: AttestationKeys.ImportPkcs8PrivateKey(
        algorithm: AttestationAlgorithms.EcdsaP256Sha256,
        pkcs8: pkcs8PrivateKey
    ));
    /// <summary>Loads an identity a prior <see cref="Save(string)"/> persisted.</summary>
    /// <param name="path">The key file's path.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentException">The file carries trailing data after the key, or the key is not on P-256.</exception>
    /// <exception cref="CryptographicException">The file does not decode as a PKCS8 private key.</exception>
    /// <exception cref="IOException">The file or its directory does not exist, or the file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not read the file.</exception>
    public static PeerIdentity Load(string path) => FromPkcs8PrivateKey(pkcs8PrivateKey: File.ReadAllBytes(path: path));
    /// <summary>Mints a self-signed X.509 certificate over this identity's own key — the credential a TLS-bearing
    /// transport presents, whose public key a remote peer's handshake compares against the identity this side
    /// offers. Server- and client-authentication usages are both asserted because a peer plays either TLS role.
    /// The private key is bound to the certificate as a persisted key rather than an ephemeral one, which the
    /// operating system's TLS stack requires before it will present the certificate; it is not marked exportable,
    /// because nothing needs to read it back out, and the key container is deleted when the certificate is
    /// disposed.</summary>
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
            data: ephemeral.ExportPkcs12(
                exportParameters: Pkcs12ExportPbeParameters.Default,
                password: null
            ),
            keyStorageFlags: X509KeyStorageFlags.DefaultKeySet,
            password: null
        );
    }
    /// <summary>Exports this identity's private key so it can be reloaded later with the same <see cref="Id"/>.
    /// The bytes are an unencrypted PKCS8 <c>PrivateKeyInfo</c>; a caller that needs an encrypted form wraps
    /// them itself, since none is offered here.</summary>
    /// <returns>The PKCS8 private key bytes.</returns>
    public byte[] ExportPkcs8PrivateKey() => m_key.ExportPkcs8PrivateKey();
    /// <summary>Persists this identity's private key to a file <see cref="Load(string)"/> can read back. The file
    /// holds the unencrypted private key, so possession of it is the whole identity: it is written to a sibling
    /// <c>.tmp</c> file created fresh (owner read/write only on Unix), flushed to disk, and then moved over
    /// <paramref name="path"/> — replacing whatever file was there — so a crash mid-write never leaves a
    /// truncated key behind the real name. No encrypted export is offered; a caller that needs one wraps
    /// <see cref="ExportPkcs8PrivateKey"/>.</summary>
    /// <param name="path">The destination path; an existing file there is replaced.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="IOException">The sibling <c>.tmp</c> file could not be created, written, or moved.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not create the sibling <c>.tmp</c> file or
    /// replace <paramref name="path"/>.</exception>
    public void Save(string path) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        var temporary = (path + ".tmp");

        File.Delete(path: temporary);

        var options = new FileStreamOptions {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(
            options: options,
            path: temporary
        )) {
            stream.Write(buffer: ExportPkcs8PrivateKey());
            stream.Flush(flushToDisk: true);
        }

        File.Move(
            destFileName: path,
            overwrite: true,
            sourceFileName: temporary
        );
    }
    /// <summary>Signs an opaque claim under this identity's own id — the shape every handshake proof and every
    /// attested message shares (only <paramref name="purpose"/> tells them apart). The signed window opens
    /// <see cref="PeerWireProtocol.ClockSkewTolerance"/> before <paramref name="now"/> and closes
    /// <paramref name="validity"/> after it, so a verifier whose clock runs behind this side's by up to the
    /// tolerance still finds the claim valid.</summary>
    /// <param name="purpose">The claim's purpose. Must not be a reserved attestation purpose.</param>
    /// <param name="audience">The verifier this claim is directed at (the peer's own fingerprint).</param>
    /// <param name="payload">The opaque claim bytes.</param>
    /// <param name="now">The signing instant; defaults to the current time.</param>
    /// <param name="validity">How long after <paramref name="now"/> the claim's signed window stays open; defaults to 30 seconds.</param>
    /// <returns>The signed claim.</returns>
    public SignedAttestation SignClaim(string purpose, string audience, ReadOnlyMemory<byte> payload, DateTimeOffset? now = null, TimeSpan? validity = null) {
        var at = (now ?? DateTimeOffset.UtcNow);
        var window = (validity ?? TimeSpan.FromSeconds(value: 30));

        return AttestationSigner.SignClaim(
            audience: audience,
            claimBytes: payload,
            codec: PeerWireProtocol.Codec,
            domain: Id.Domain,
            notAfter: at.Add(timeSpan: window).ToUnixTimeSeconds(),
            notBefore: at.Subtract(value: PeerWireProtocol.ClockSkewTolerance).ToUnixTimeSeconds(),
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
