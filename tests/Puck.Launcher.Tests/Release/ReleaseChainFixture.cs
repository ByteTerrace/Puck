using System.Security.Cryptography;
using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Attestation;
using Puck.Launcher.Release;

namespace Puck.Launcher.Tests.Release;

/// <summary>A minted throwaway root/issuing/subject signing chain plus the helpers to sign a
/// <see cref="ReleaseManifest"/> claim under it — the law suite's own twin of the pattern
/// <c>Puck.Attestation.Tests</c>' internal <c>AttestationTestSupport</c> establishes, kept local since that helper
/// is not visible outside its own assembly.</summary>
internal sealed class ReleaseChainFixture {
    public const long Epoch = 1_700_000_000L;

    private readonly ECDsa m_issuingKey;
    private readonly byte[] m_issuingSpki;
    private readonly KeyId m_issuingId;
    private readonly ECDsa m_rootKey;
    private readonly byte[] m_rootSpki;
    private readonly ECDsa m_subjectKey;
    private readonly byte[] m_subjectSpki;
    private readonly KeyId m_subjectId;

    public readonly IAttestationCodec Codec = new CborAttestationCodec();
    public readonly KeyId RootId;
    public readonly string Subject;

    public ReleaseChainFixture(string subject = "puck.world") {
        Subject = subject;
        m_rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        m_rootSpki = m_rootKey.ExportSubjectPublicKeyInfo();
        RootId = KeyId.ForRoot(algorithm: AttestationAlgorithms.EcdsaP256Sha256, subjectPublicKeyInfo: m_rootSpki);

        m_issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        m_issuingSpki = m_issuingKey.ExportSubjectPublicKeyInfo();
        m_issuingId = KeyId.ForIssuing(algorithm: AttestationAlgorithms.EcdsaP256Sha256, domain: RootId.Domain, subjectPublicKeyInfo: m_issuingSpki);

        m_subjectKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        m_subjectSpki = m_subjectKey.ExportSubjectPublicKeyInfo();
        m_subjectId = KeyId.ForSubject(algorithm: AttestationAlgorithms.EcdsaP256Sha256, domain: RootId.Domain, subject: subject, subjectPublicKeyInfo: m_subjectSpki);
    }

    public ReleaseTrustAnchor TrustAnchor => new(
        Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
        Domain: RootId.Domain,
        PublicKeySubjectPublicKeyInfoBase64: Convert.ToBase64String(inArray: m_rootSpki)
    );

    public TrustList BuildTrustList(TimeSpan replayHorizon) => new(
        entries: [TrustAnchor.ToTrustListEntry(maximumAge: null, reach: new HashSet<string>(comparer: StringComparer.Ordinal) { AttestationReleaseVerifier.Slot })],
        defaultMaximumAge: null,
        replayAcceptanceHorizon: replayHorizon
    );
    /// <summary>Signs <paramref name="document"/> (its own <c>Signature</c> field must already be null) as a
    /// sequenced bearer release claim, returning the fully signed manifest.</summary>
    public ReleaseManifest Sign(ReleaseManifest document, ulong sequence, long notBefore, long notAfter) {
        var canonical = ReleaseCanonicalizer.Canonicalize(document: document);
        var rootToIssuing = AttestationSigner.SignKeyBinding(
            codec: Codec,
            domain: RootId.Domain,
            notAfter: notAfter,
            notBefore: notBefore,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: m_rootKey,
            targetId: m_issuingId,
            targetSubjectPublicKeyInfo: m_issuingSpki
        );
        var issuingToSubject = AttestationSigner.SignKeyBinding(
            codec: Codec,
            domain: RootId.Domain,
            notAfter: notAfter,
            notBefore: notBefore,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: m_issuingKey,
            targetId: m_subjectId,
            targetSubjectPublicKeyInfo: m_subjectSpki
        );
        var claim = AttestationSigner.SignClaim(
            audience: null,
            claimBytes: canonical.Bytes,
            codec: Codec,
            domain: RootId.Domain,
            notAfter: notAfter,
            notBefore: notBefore,
            purpose: AttestationReleaseVerifier.Purpose,
            sequence: sequence,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: m_subjectKey,
            subject: Subject
        );
        var signature = new ReleaseSignature(
            Chain: [
                Convert.ToBase64String(inArray: Codec.EncodeAttestation(attestation: rootToIssuing)),
                Convert.ToBase64String(inArray: Codec.EncodeAttestation(attestation: issuingToSubject)),
            ],
            Claim: Convert.ToBase64String(inArray: Codec.EncodeAttestation(attestation: claim))
        );

        return (document with { Signature = signature });
    }
    /// <summary>Serializes <paramref name="manifest"/> to the raw bytes an <see cref="IReleaseSource"/> would hand back.</summary>
    public static byte[] ToWireBytes(ReleaseManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(value: manifest, options: DocumentJsonOptions.Shared);
}
