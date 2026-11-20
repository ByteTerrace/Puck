using System.Security.Cryptography;
using Puck.Attestation;

namespace Puck.Launcher.Release;

/// <summary>Signs a release manifest through the ordinary release attestation profile: a root key vouches for a fresh
/// issuing key, which vouches for a fresh application key, which signs the manifest's canonical content pin. The root is
/// the only key a consumer pins (<see cref="ReleaseTrustAnchor"/>); who holds it (a local development file, a
/// deployment's key custody) is the caller's, and the verifier treats every root alike.</summary>
public static class ReleaseSigner {
    /// <summary>The window a signature verifies in when the caller names none: the verifier's default replay horizon
    /// (<see cref="AddSelfUpdateDefaults.ReplayAcceptanceHorizon"/>), past which a manifest is refused however long it
    /// was signed for.</summary>
    public static readonly TimeSpan DefaultValidity = AddSelfUpdateDefaults.ReplayAcceptanceHorizon;

    /// <summary>Loads a root key from a PKCS#8 file, or creates one there when the file does not exist. Never replaces an
    /// existing key.</summary>
    /// <param name="path">The key file's path.</param>
    /// <returns>The key, owned by the caller.</returns>
    public static ECDsa LoadOrCreate(string path) {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: path))!);
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        if (File.Exists(path: path)) {
            key.ImportPkcs8PrivateKey(File.ReadAllBytes(path: path), out _);
        } else {
            using var file = new FileStream(access: FileAccess.Write, mode: FileMode.CreateNew, path: path, share: FileShare.None);

            file.Write(buffer: key.ExportPkcs8PrivateKey());
            file.Flush(flushToDisk: true);
        }
        return key;
    }
    /// <summary>Returns the trust anchor a consumer pins for a root key.</summary>
    /// <param name="root">The root key.</param>
    /// <returns>The anchor.</returns>
    public static ReleaseTrustAnchor AnchorOf(ECDsa root) {
        ArgumentNullException.ThrowIfNull(argument: root);
        var spki = root.ExportSubjectPublicKeyInfo();

        return new(KeyId.ForRoot(algorithm: AttestationAlgorithms.EcdsaP256Sha256, subjectPublicKeyInfo: spki).Domain, AttestationAlgorithms.EcdsaP256Sha256, Convert.ToBase64String(inArray: spki));
    }
    /// <summary>Signs a complete manifest with fresh issuing and application keys vouched for by <paramref name="root"/>.
    /// The caller supplies a sequence that increases with every manifest this root signs for the application.</summary>
    /// <param name="manifest">The manifest, its payload inventory complete.</param>
    /// <param name="root">The root key.</param>
    /// <param name="sequence">The manifest's replay sequence.</param>
    /// <param name="validity">How long the signature verifies, counted from a minute before now; <see langword="null"/> for
    /// <see cref="DefaultValidity"/>.</param>
    /// <returns>The signed manifest and the anchor that verifies it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="validity"/> is not positive.</exception>
    public static (ReleaseManifest Manifest, ReleaseTrustAnchor TrustAnchor) Sign(ReleaseManifest manifest, ECDsa root, ulong sequence, TimeSpan? validity = null) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentNullException.ThrowIfNull(argument: root);
        var window = (validity ?? DefaultValidity);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: window, other: TimeSpan.Zero);
        var algorithm = AttestationAlgorithms.EcdsaP256Sha256;
        var anchor = AnchorOf(root: root);
        var rootId = KeyId.ForRoot(algorithm: algorithm, subjectPublicKeyInfo: Convert.FromBase64String(s: anchor.PublicKeySubjectPublicKeyInfoBase64));
        using var issuing = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var issuingSpki = issuing.ExportSubjectPublicKeyInfo();
        var issuingId = KeyId.ForIssuing(algorithm: algorithm, domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki);
        using var subject = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var subjectSpki = subject.ExportSubjectPublicKeyInfo();
        var subjectId = KeyId.ForSubject(algorithm: algorithm, domain: rootId.Domain, subject: manifest.App, subjectPublicKeyInfo: subjectSpki);
        var codec = new CborAttestationCodec();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var from = (now - 60);
        // The whole signed window, from its minute of clock-skew grace on, is the validity: a sequenced claim whose window
        // exceeds the verifier's replay horizon is refused (AttestationVerifier), and the default validity is that horizon.
        var until = (from + ((long)window.TotalSeconds));
        var rootBinding = AttestationSigner.SignKeyBinding(codec: codec, domain: rootId.Domain, targetId: issuingId,
            targetSubjectPublicKeyInfo: issuingSpki, signerAlgorithm: algorithm, signerKey: root, notBefore: from, notAfter: until);
        var subjectBinding = AttestationSigner.SignKeyBinding(codec: codec, domain: rootId.Domain, targetId: subjectId,
            targetSubjectPublicKeyInfo: subjectSpki, signerAlgorithm: algorithm, signerKey: issuing, notBefore: from, notAfter: until);
        var claim = AttestationSigner.SignClaim(codec: codec, domain: rootId.Domain, subject: manifest.App,
            claimBytes: ReleaseCanonicalizer.SigningBytes(document: manifest), purpose: AttestationReleaseVerifier.Purpose,
            audience: null, sequence: sequence, signerAlgorithm: algorithm, signerKey: subject, notBefore: from, notAfter: until);

        return (manifest with {
            Signature = new ReleaseSignature(Convert.ToBase64String(inArray: codec.EncodeAttestation(attestation: claim)),
            [Convert.ToBase64String(inArray: codec.EncodeAttestation(attestation: rootBinding)), Convert.ToBase64String(inArray: codec.EncodeAttestation(attestation: subjectBinding))]),
        }, anchor);
    }
}
