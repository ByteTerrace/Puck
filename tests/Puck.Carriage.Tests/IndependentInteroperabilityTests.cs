using System.Security.Cryptography;
using System.Text;

using Xunit;

using static Puck.Carriage.Tests.CarriageTestSupport;

namespace Puck.Carriage.Tests;

/// <summary>
/// Bidirectional interoperability at the byte boundary. One side of every case is the production library;
/// the other is <see cref="IndependentCarriageImplementation"/>, which has its own models, encoding,
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
        var rootId = IndependentCarriageImplementation.RootId(subjectPublicKeyInfo: rootSpki);
        var issuingId = IndependentCarriageImplementation.IssuingId(domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki);
        var subjectId = IndependentCarriageImplementation.SubjectId(domain: rootId.Domain, subject: "user:independent", subjectPublicKeyInfo: subjectSpki);
        var rootToIssuingWire = IndependentCarriageImplementation.SignKeyBinding(
            domain: rootId.Domain,
            signingKey: rootKey,
            targetId: issuingId,
            targetSubjectPublicKeyInfo: issuingSpki,
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600)
        );
        var issuingToSubjectWire = IndependentCarriageImplementation.SignKeyBinding(
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
            Algorithm: IndependentCarriageImplementation.SigningAlgorithm,
            Purpose: "interop.bearer",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: null,
            Sequence: 17UL
        );
        var claimWire = IndependentCarriageImplementation.SignClaim(
            header: header,
            payloadKind: IndependentCarriageImplementation.OpaquePayloadKind,
            payload: "independent signed payload"u8,
            signingKey: subjectKey
        );
        var codec = new CborCarriageCodec();
        var profile = CarriageConformanceProfile.Base;
        var claim = profile.DecodeEnvelope(codec: codec, wire: claimWire);
        var chain = new[] {
            profile.DecodeEnvelope(codec: codec, wire: rootToIssuingWire),
            profile.DecodeEnvelope(codec: codec, wire: issuingToSubjectWire),
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
        var codec = new CborCarriageCodec();
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
        var rootId = IndependentCarriageImplementation.RootId(subjectPublicKeyInfo: keys.RootSpki);

        var payload = IndependentCarriageImplementation.VerifyChain(
            rootToIssuingWire: codec.EncodeEnvelope(envelope: rootToIssuing),
            issuingToSubjectWire: codec.EncodeEnvelope(envelope: issuingToSubject),
            claimWire: codec.EncodeEnvelope(envelope: claim),
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
        var rootId = IndependentCarriageImplementation.RootId(subjectPublicKeyInfo: rootSpki);
        var issuingId = IndependentCarriageImplementation.IssuingId(domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki);
        var subjectId = IndependentCarriageImplementation.SubjectId(domain: rootId.Domain, subject: "user:sealed-independent", subjectPublicKeyInfo: subjectSigningSpki);
        var recipientId = IndependentCarriageImplementation.SubjectId(
            domain: rootId.Domain,
            subject: "user:sealed-independent",
            subjectPublicKeyInfo: recipientSealingSpki,
            algorithm: IndependentCarriageImplementation.SealingAlgorithm
        );
        var rootToIssuingWire = IndependentCarriageImplementation.SignKeyBinding(rootId.Domain, rootKey, issuingId, issuingSpki, Epoch - 60, Epoch + 3_600);
        var issuingToSubjectWire = IndependentCarriageImplementation.SignKeyBinding(rootId.Domain, issuingKey, subjectId, subjectSigningSpki, Epoch - 60, Epoch + 3_600);
        var header = new IndependentHeader(
            Domain: rootId.Domain,
            Subject: subjectId.Subject,
            Algorithm: IndependentCarriageImplementation.SigningAlgorithm,
            Purpose: "interop.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:vault",
            Sequence: null
        );
        var sealedPayloadBytes = IndependentCarriageImplementation.Seal(
            recipientId: recipientId,
            recipientSubjectPublicKeyInfo: recipientSealingSpki,
            headerBytes: IndependentCarriageImplementation.EncodeHeader(header: header),
            plaintext: "independently sealed and signed"u8
        );
        var claimWire = IndependentCarriageImplementation.SignClaim(
            header: header,
            payloadKind: IndependentCarriageImplementation.SealedPayloadKind,
            payload: sealedPayloadBytes,
            signingKey: subjectSigningKey
        );
        var codec = new CborCarriageCodec();
        var profile = CarriageConformanceProfile.Base.WithExtensions(CarriageConformanceExtensions.SealedCarriageV1);
        var claim = profile.DecodeEnvelope(codec: codec, wire: claimWire);
        var result = profile.VerifyChain(
            codec: codec,
            claim: claim,
            chain: [
                profile.DecodeEnvelope(codec: codec, wire: rootToIssuingWire),
                profile.DecodeEnvelope(codec: codec, wire: issuingToSubjectWire),
            ],
            trustList: BuildProductionTrust(rootId: rootId, rootSpki: rootSpki, replayHorizon: null),
            now: Now,
            expectedPurpose: "interop.sealed",
            expectedAudience: "world:vault"
        );

        Assert.True(condition: result.Admits(slot: "slot:wallet"), userMessage: result.RefusalReason);

        var plaintext = SealedCarriage.Unseal(
            recipientPrivateKey: recipientSealingKey,
            payload: codec.DecodeSealedPayload(bytes: claim.PayloadBytes.Span),
            associatedData: codec.EncodeHeader(header: claim.Header)
        );

        Assert.Equal(expected: "independently sealed and signed", actual: Encoding.UTF8.GetString(bytes: plaintext));
    }

    [Fact]
    public void ProductionSignedAndSealedClaim_VerifiesAndOpensIndependently() {
        var codec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:sealed-production");
        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notBefore: (Epoch - 60), notAfter: (Epoch + 3_600));
        var header = new CarriageEnvelopeHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: CarriageAlgorithms.EcdsaP256Sha256,
            Purpose: "interop.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:independent-vault",
            Sequence: null
        );
        var sealedPayload = SealedCarriage.Seal(
            recipientId: keys.SubjectSealingId,
            recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
            associatedData: codec.EncodeHeader(header: header),
            plaintext: "production sealed and signed"u8
        );
        var claim = CarriageSigner.Sign(
            codec: codec,
            header: header,
            payloadKind: CarriagePayloadKind.Sealed,
            payloadBytes: codec.EncodeSealedPayload(payload: sealedPayload),
            signingKey: keys.SubjectSigningKey,
            signingAlgorithm: CarriageAlgorithms.EcdsaP256Sha256
        );
        var independentRootId = IndependentCarriageImplementation.RootId(subjectPublicKeyInfo: keys.RootSpki);
        var independentlyVerifiedPayload = IndependentCarriageImplementation.VerifyChain(
            rootToIssuingWire: codec.EncodeEnvelope(envelope: rootToIssuing),
            issuingToSubjectWire: codec.EncodeEnvelope(envelope: issuingToSubject),
            claimWire: codec.EncodeEnvelope(envelope: claim),
            trustedRootId: independentRootId,
            trustedRootSubjectPublicKeyInfo: keys.RootSpki,
            expectedPurpose: "interop.sealed",
            expectedAudience: "world:independent-vault",
            now: Epoch
        );
        var independentHeader = new IndependentHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: IndependentCarriageImplementation.SigningAlgorithm,
            Purpose: "interop.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:independent-vault",
            Sequence: null
        );

        var plaintext = IndependentCarriageImplementation.Unseal(
            sealedPayloadBytes: independentlyVerifiedPayload,
            recipientPrivateKey: keys.SubjectSealingKey,
            headerBytes: IndependentCarriageImplementation.EncodeHeader(header: independentHeader)
        );

        Assert.Equal(expected: "production sealed and signed", actual: Encoding.UTF8.GetString(bytes: plaintext));
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
                Mode: CarriageTrustMode.Vouches,
                Reach: new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:wallet" },
                MaximumAge: null
            ),
        ],
        defaultMaximumAge: TimeSpan.FromHours(hours: 1),
        replayAcceptanceHorizon: replayHorizon
    );
}
