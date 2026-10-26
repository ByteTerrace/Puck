using System.Security.Cryptography;
using Puck.Attestation;

namespace Puck.Launcher.Release;

/// <summary>Local development signing through the ordinary release attestation profile. Its explicitly selected
/// key is independent of publisher custody; consumers still pin its public key and verify every payload file.</summary>
public static class DevelopmentReleaseSigner {
    /// <summary>Loads or creates a local development root key. Never replaces an existing key.</summary>
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
    /// <summary>Signs a complete manifest with fresh issuing and application keys vouched for by the supplied
    /// development root. The caller supplies a monotonically increasing sequence for this root and application.</summary>
    public static (ReleaseManifest Manifest, ReleaseTrustAnchor TrustAnchor) Sign(ReleaseManifest manifest, ECDsa root, ulong sequence) {
        var algorithm = AttestationAlgorithms.EcdsaP256Sha256;
        var rootSpki = root.ExportSubjectPublicKeyInfo();
        var rootId = KeyId.ForRoot(algorithm: algorithm, subjectPublicKeyInfo: rootSpki);
        using var issuing = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var issuingSpki = issuing.ExportSubjectPublicKeyInfo();
        var issuingId = KeyId.ForIssuing(algorithm: algorithm, domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki);
        using var subject = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var subjectSpki = subject.ExportSubjectPublicKeyInfo();
        var subjectId = KeyId.ForSubject(algorithm: algorithm, domain: rootId.Domain, subject: manifest.App, subjectPublicKeyInfo: subjectSpki);
        var codec = new CborAttestationCodec();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var from = (now - 60);
        var until = (now + 86400);
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
        },
            new(rootId.Domain, algorithm, Convert.ToBase64String(inArray: rootSpki)));
    }
}
