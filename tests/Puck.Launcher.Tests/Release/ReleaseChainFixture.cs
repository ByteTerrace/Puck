using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Attestation;
using Puck.Attestation.Tests;
using Puck.Launcher.Release;

namespace Puck.Launcher.Tests.Release;

/// <summary>A minted throwaway root/issuing/subject signing chain plus the helpers to sign a
/// <see cref="ReleaseManifest"/> claim under it, built on <see cref="AttestationTestSupport"/>'s shared
/// key-minting and two-hop chain-signing helpers.</summary>
internal sealed class ReleaseChainFixture {
    public const long Epoch = 1_700_000_000L;

    private readonly DomainKeys m_keys;

    public readonly IAttestationCodec Codec = new CborAttestationCodec();
    public KeyId RootId => m_keys.RootId;
    public string Subject => m_keys.Subject;

    public ReleaseChainFixture(string subject = "puck.world") {
        m_keys = AttestationTestSupport.MintDomainKeys(subject: subject);
    }

    public ReleaseTrustAnchor TrustAnchor => new(
        Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
        Domain: RootId.Domain,
        PublicKeySubjectPublicKeyInfoBase64: Convert.ToBase64String(inArray: m_keys.RootSpki)
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
        var chain = AttestationTestSupport.BuildChain(codec: Codec, keys: m_keys, notAfter: notAfter, notBefore: notBefore);
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
            signerKey: m_keys.SubjectSigningKey,
            subject: Subject
        );
        var signature = new ReleaseSignature(
            Chain: [
                Convert.ToBase64String(inArray: Codec.EncodeAttestation(attestation: chain.RootToIssuing)),
                Convert.ToBase64String(inArray: Codec.EncodeAttestation(attestation: chain.IssuingToSubject)),
            ],
            Claim: Convert.ToBase64String(inArray: Codec.EncodeAttestation(attestation: claim))
        );

        return (document with { Signature = signature });
    }
    /// <summary>Serializes <paramref name="manifest"/> to the raw bytes an <see cref="IReleaseSource"/> would hand back.</summary>
    public static byte[] ToWireBytes(ReleaseManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(value: manifest, options: DocumentJsonOptions.Shared);
}
