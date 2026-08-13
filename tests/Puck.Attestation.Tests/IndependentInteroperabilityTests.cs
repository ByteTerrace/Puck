using System.Security.Cryptography;
using System.Text;

using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

/// <summary>
/// Bidirectional interoperability at the byte boundary. One side of every case is the production library;
/// the other is <see cref="IndependentAttestationImplementation"/>, which has its own models, encoding,
/// signing, chain walk, key-id derivation, sealing, and unsealing code.
/// </summary>
public sealed class IndependentInteroperabilityTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    [Fact]
    public void IndependentSignedChainAndBearerClaim_AreAcceptedByProduction() {
        using var rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var subjectKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var rootSpki = rootKey.ExportSubjectPublicKeyInfo();
        var issuingSpki = issuingKey.ExportSubjectPublicKeyInfo();
        var subjectSpki = subjectKey.ExportSubjectPublicKeyInfo();
        var rootId = IndependentAttestationImplementation.RootId(subjectPublicKeyInfo: rootSpki);
        var issuingId = IndependentAttestationImplementation.IssuingId(domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki);
        var subjectId = IndependentAttestationImplementation.SubjectId(domain: rootId.Domain, subject: "user:independent", subjectPublicKeyInfo: subjectSpki);
        var rootToIssuingWire = IndependentAttestationImplementation.SignKeyBinding(
            domain: rootId.Domain,
            signingKey: rootKey,
            targetId: issuingId,
            targetSubjectPublicKeyInfo: issuingSpki,
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600)
        );
        var issuingToSubjectWire = IndependentAttestationImplementation.SignKeyBinding(
            domain: rootId.Domain,
            signingKey: issuingKey,
            targetId: subjectId,
            targetSubjectPublicKeyInfo: subjectSpki,
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600)
        );
        var header = new IndependentHeader(
            Domain: rootId.Domain,
            Subject: subjectId.Subject,
            Algorithm: IndependentAttestationImplementation.SigningAlgorithm,
            Purpose: "interop.bearer",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: null,
            Sequence: 17UL
        );
        var claimWire = IndependentAttestationImplementation.SignClaim(
            header: header,
            payloadKind: IndependentAttestationImplementation.OpaquePayloadKind,
            payload: "independent signed payload"u8,
            signingKey: subjectKey
        );
        var codec = new CborAttestationCodec();
        var profile = AttestationProfile.Base;
        var claim = profile.DecodeAttestation(codec: codec, wire: claimWire);
        var chain = new[] {
            profile.DecodeAttestation(codec: codec, wire: rootToIssuingWire),
            profile.DecodeAttestation(codec: codec, wire: issuingToSubjectWire),
        };
        var trust = BuildProductionTrust(rootId: rootId, rootSpki: rootSpki, replayHorizon: TimeSpan.FromHours(hours: 1));

        var result = profile.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trust,
            now: Now,
            expectedPurpose: "interop.bearer",
            expectedAudience: null
        );

        Assert.True(condition: result.TryGetReplayCommit(slot: "slot:wallet", requirement: out var requirement));
        Assert.NotNull(@object: requirement);
        Assert.Equal(expected: rootId.Domain, actual: requirement.Domain);
        Assert.Equal(expected: "user:independent", actual: requirement.Subject);
        Assert.Equal(expected: 17UL, actual: requirement.Sequence);
        Assert.Equal(expected: "independent signed payload", actual: Encoding.UTF8.GetString(bytes: claim.PayloadBytes.Span));
    }

    [Fact]
    public void ProductionSignedChainAndDirectedClaim_AreAcceptedByIndependentVerifier() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:production");
        var (rootToIssuing, issuingToSubject) = BuildChain(
            codec: codec,
            keys: keys,
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600)
        );
        var claim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "interop.directed",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 1_800),
            audience: "world:independent",
            sequence: null,
            text: "production signed payload"
        );
        var rootId = IndependentAttestationImplementation.RootId(subjectPublicKeyInfo: keys.RootSpki);

        var payload = IndependentAttestationImplementation.VerifyChain(
            rootToIssuingWire: codec.EncodeAttestation(attestation: rootToIssuing),
            issuingToSubjectWire: codec.EncodeAttestation(attestation: issuingToSubject),
            claimWire: codec.EncodeAttestation(attestation: claim),
            trustedRootId: rootId,
            trustedRootSubjectPublicKeyInfo: keys.RootSpki,
            expectedPurpose: "interop.directed",
            expectedAudience: "world:independent",
            now: Epoch
        );

        Assert.Equal(expected: "production signed payload", actual: Encoding.UTF8.GetString(bytes: payload));
    }

    [Fact]
    public void IndependentSignedAndSealedClaim_VerifiesAndOpensInProduction() {
        using var rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var subjectSigningKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var recipientSealingKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);
        var rootSpki = rootKey.ExportSubjectPublicKeyInfo();
        var issuingSpki = issuingKey.ExportSubjectPublicKeyInfo();
        var subjectSigningSpki = subjectSigningKey.ExportSubjectPublicKeyInfo();
        var recipientSealingSpki = recipientSealingKey.ExportSubjectPublicKeyInfo();
        var rootId = IndependentAttestationImplementation.RootId(subjectPublicKeyInfo: rootSpki);
        var issuingId = IndependentAttestationImplementation.IssuingId(domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki);
        var subjectId = IndependentAttestationImplementation.SubjectId(domain: rootId.Domain, subject: "user:sealed-independent", subjectPublicKeyInfo: subjectSigningSpki);
        var recipientId = IndependentAttestationImplementation.SubjectId(
            domain: rootId.Domain,
            subject: "user:sealed-independent",
            subjectPublicKeyInfo: recipientSealingSpki,
            algorithm: IndependentAttestationImplementation.SealingAlgorithm
        );
        var rootToIssuingWire = IndependentAttestationImplementation.SignKeyBinding(rootId.Domain, rootKey, issuingId, issuingSpki, Epoch - 60, Epoch + 3_600);
        var issuingToSubjectWire = IndependentAttestationImplementation.SignKeyBinding(rootId.Domain, issuingKey, subjectId, subjectSigningSpki, Epoch - 60, Epoch + 3_600);
        var header = new IndependentHeader(
            Domain: rootId.Domain,
            Subject: subjectId.Subject,
            Algorithm: IndependentAttestationImplementation.SigningAlgorithm,
            Purpose: "interop.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:vault",
            Sequence: null
        );
        var sealedPayloadBytes = IndependentAttestationImplementation.Seal(
            recipientId: recipientId,
            recipientSubjectPublicKeyInfo: recipientSealingSpki,
            headerBytes: IndependentAttestationImplementation.EncodeHeader(header: header),
            plaintext: "independently sealed and signed"u8
        );
        var claimWire = IndependentAttestationImplementation.SignClaim(
            header: header,
            payloadKind: IndependentAttestationImplementation.SealedPayloadKind,
            payload: sealedPayloadBytes,
            signingKey: subjectSigningKey
        );
        var codec = new CborAttestationCodec();
        var profile = AttestationProfile.Base.WithExtensions(AttestationExtensions.SealedAttestationV1);
        var claim = profile.DecodeAttestation(codec: codec, wire: claimWire);
        var result = profile.VerifyChain(
            codec: codec,
            claim: claim,
            chain: [
                profile.DecodeAttestation(codec: codec, wire: rootToIssuingWire),
                profile.DecodeAttestation(codec: codec, wire: issuingToSubjectWire),
            ],
            trustList: BuildProductionTrust(rootId: rootId, rootSpki: rootSpki, replayHorizon: null),
            now: Now,
            expectedPurpose: "interop.sealed",
            expectedAudience: "world:vault"
        );

        Assert.True(condition: result.Admits(slot: "slot:wallet"), userMessage: result.RefusalReason);

        var plaintext = SealedAttestation.Unseal(
            recipientPrivateKey: recipientSealingKey,
            payload: codec.DecodeSealedPayload(bytes: claim.PayloadBytes.Span),
            associatedData: codec.EncodeHeader(header: claim.Header)
        );

        Assert.Equal(expected: "independently sealed and signed", actual: Encoding.UTF8.GetString(bytes: plaintext));
    }

    [Fact]
    public void ProductionSignedAndSealedClaim_VerifiesAndOpensIndependently() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:sealed-production");
        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notBefore: (Epoch - 60), notAfter: (Epoch + 3_600));
        var header = new AttestationHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            Purpose: "interop.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:independent-vault",
            Sequence: null
        );
        var sealedPayload = SealedAttestation.Seal(
            recipientId: keys.SubjectSealingId,
            recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
            associatedData: codec.EncodeHeader(header: header),
            plaintext: "production sealed and signed"u8
        );
        var claim = AttestationSigner.Sign(
            codec: codec,
            header: header,
            payloadKind: AttestationPayloadKind.Sealed,
            payloadBytes: codec.EncodeSealedPayload(payload: sealedPayload),
            signingKey: keys.SubjectSigningKey,
            signingAlgorithm: AttestationAlgorithms.EcdsaP256Sha256
        );
        var independentRootId = IndependentAttestationImplementation.RootId(subjectPublicKeyInfo: keys.RootSpki);
        var independentlyVerifiedPayload = IndependentAttestationImplementation.VerifyChain(
            rootToIssuingWire: codec.EncodeAttestation(attestation: rootToIssuing),
            issuingToSubjectWire: codec.EncodeAttestation(attestation: issuingToSubject),
            claimWire: codec.EncodeAttestation(attestation: claim),
            trustedRootId: independentRootId,
            trustedRootSubjectPublicKeyInfo: keys.RootSpki,
            expectedPurpose: "interop.sealed",
            expectedAudience: "world:independent-vault",
            now: Epoch
        );
        var independentHeader = new IndependentHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: IndependentAttestationImplementation.SigningAlgorithm,
            Purpose: "interop.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:independent-vault",
            Sequence: null
        );

        var plaintext = IndependentAttestationImplementation.Unseal(
            sealedPayloadBytes: independentlyVerifiedPayload,
            recipientPrivateKey: keys.SubjectSealingKey,
            headerBytes: IndependentAttestationImplementation.EncodeHeader(header: independentHeader)
        );

        Assert.Equal(expected: "production sealed and signed", actual: Encoding.UTF8.GetString(bytes: plaintext));
    }

    [Fact]
    public void IndependentSpkiImporter_TrailingBytesAreRefused() {
        using var recipientKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);

        var tailedRecipientSpki = (byte[])[.. recipientKey.ExportSubjectPublicKeyInfo(), 0x00];
        var recipientId = IndependentAttestationImplementation.SubjectId(
            domain: new string(c: '0', count: 64),
            subject: "user:tailed-independent-key",
            subjectPublicKeyInfo: tailedRecipientSpki,
            algorithm: IndependentAttestationImplementation.SealingAlgorithm
        );

        var exception = Assert.Throws<CryptographicException>(testCode: () => _ = IndependentAttestationImplementation.Seal(
            recipientId: recipientId,
            recipientSubjectPublicKeyInfo: tailedRecipientSpki,
            headerBytes: [],
            plaintext: []
        ));

        Assert.Contains(expectedSubstring: "trailing", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    private static TrustList BuildProductionTrust(IndependentId rootId, byte[] rootSpki, TimeSpan? replayHorizon) => new(
        entries: [
            new TrustListEntry(
                PinnedId: new KeyId {
                    Domain = rootId.Domain,
                    Subject = rootId.Subject,
                    Algorithm = rootId.Algorithm,
                    KeyHash = rootId.KeyHash,
                },
                PublicKeySubjectPublicKeyInfo: rootSpki,
                Mode: AttestationTrustMode.Vouches,
                Reach: new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:wallet" },
                MaximumAge: null
            ),
        ],
        defaultMaximumAge: TimeSpan.FromHours(hours: 1),
        replayAcceptanceHorizon: replayHorizon
    );
}
