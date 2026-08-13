using Xunit;

using static Puck.Carriage.Tests.CarriageTestSupport;

namespace Puck.Carriage.Tests;

public sealed class ConformanceProfileTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    [Fact]
    public void Base_RequiresAndAcceptsCborAndEcdsaP256Sha256() {
        var cbor = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var claim = SignTestClaim(codec: cbor, keys: keys, purpose: "profile.test", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, text: "base profile");

        var result = CarriageConformanceProfile.Base.VerifyChain(codec: cbor, claim: claim, chain: null, trustList: trust, now: Now, expectedPurpose: "profile.test", expectedAudience: "world:profile");

        AssertAccepted(result: result);
    }

    [Fact]
    public void ResourceProfileFacade_IsTheOnlyPublicVerificationBoundary() {
        Assert.False(condition: typeof(CarriageVerifier).IsPublic);
    }

    [Fact]
    public void ComposedProfileNames_UseTheNormativeExtensionSpelling() {
        var profile = CarriageConformanceProfile.Base.WithExtensions(extensions: CarriageConformanceExtensions.SealedCarriageV1);

        Assert.Equal(expected: "carriage-v1-base+sealed-carriage-v1", actual: profile.Name);
    }

    [Fact]
    public void EnvelopeCeiling_IsEnforcedBeforeCborParsing() {
        var cbor = new CborCarriageCodec();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = CarriageConformanceProfile.Base.DecodeEnvelope(codec: cbor, wire: new byte[CarriageResourceLimits.EnvelopeBytes + 1]));

        Assert.Contains(expectedSubstring: "permits at most", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PayloadAboveTheExplicitCeiling_IsRefusedDeterministically() {
        var cbor = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var oversizedPayloadClaim = CarriageSigner.SignClaim(codec: cbor, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, purpose: "profile.test", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, claimBytes: new byte[CarriageResourceLimits.PayloadBytes + 1]);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = CarriageConformanceProfile.Base.DecodeEnvelope(codec: cbor, wire: cbor.EncodeEnvelope(envelope: oversizedPayloadClaim)));

        Assert.Contains(expectedSubstring: "payload", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignatureOutsideTheExactCeiling_IsRefusedBeforeSignatureVerification() {
        var cbor = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var claim = SignTestClaim(codec: cbor, keys: keys, purpose: "profile.test", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, text: "base profile");
        var oversizedSignature = new byte[CarriageResourceLimits.SignatureBytes + 1];

        var result = CarriageConformanceProfile.Base.VerifyChain(codec: cbor, claim: (claim with { Signature = oversizedSignature }), chain: null, trustList: trust, now: Now, expectedPurpose: "profile.test", expectedAudience: "world:profile");

        AssertRefused(result: result, reasonMustContain: "exactly 64");
    }

    [Fact]
    public void TextCeilings_AreMeasuredInUtf8BytesNotCharacters() {
        var cbor = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var oversizedPurposeClaim = CarriageSigner.SignClaim(codec: cbor, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, purpose: new string(c: 'p', count: (CarriageResourceLimits.TextStringUtf8Bytes + 1)), notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, claimBytes: Array.Empty<byte>());

        var exception = Assert.Throws<FormatException>(testCode: () => _ = CarriageConformanceProfile.Base.DecodeEnvelope(codec: cbor, wire: cbor.EncodeEnvelope(envelope: oversizedPurposeClaim)));

        Assert.Contains(expectedSubstring: "UTF-8 bytes", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(230)]
    [InlineData(300)]
    [InlineData(65_400)]
    public void DerivedEnvelopeLength_MatchesTheRealCborEncoding(int payloadLength) {
        var codec = new CborCarriageCodec();
        var header = new CarriageEnvelopeHeader(
            Domain: new string(c: '0', count: 64),
            Subject: "user:length",
            Algorithm: CarriageAlgorithms.EcdsaP256Sha256,
            Purpose: "profile.length",
            NotBefore: 0L,
            NotAfter: 1L,
            Audience: "world:length",
            Sequence: null
        );
        var envelope = SignedCarriageEnvelope.Reencode(
            codec: codec,
            header: header,
            payloadKind: CarriagePayloadKind.Opaque,
            payloadBytes: new byte[payloadLength],
            signature: new byte[CarriageResourceLimits.SignatureBytes]
        );

        Assert.Equal(
            expected: codec.EncodeEnvelope(envelope: envelope).LongLength,
            actual: CborCarriageCodec.EncodedEnvelopeLength(envelope: envelope)
        );
    }

    [Fact]
    public void SealedRecipientTextCeiling_IsCheckedOnTheAuthenticatedPayloadDecode() {
        var codec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:sealed-profile");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var header = new CarriageEnvelopeHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: CarriageAlgorithms.EcdsaP256Sha256,
            Purpose: "profile.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:profile",
            Sequence: null
        );
        var sealedPayload = SealedCarriage.Seal(
            recipientId: keys.SubjectSealingId,
            recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
            associatedData: codec.EncodeHeader(header: header),
            plaintext: "profile-checked ciphertext"u8
        );
        var oversizedRecipient = sealedPayload with {
            RecipientId = sealedPayload.RecipientId with {
                Subject = new string(c: 's', count: (CarriageResourceLimits.TextStringUtf8Bytes + 1)),
            },
        };
        var claim = CarriageSigner.Sign(
            codec: codec,
            header: header,
            payloadKind: CarriagePayloadKind.Sealed,
            payloadBytes: codec.EncodeSealedPayload(payload: oversizedRecipient),
            signingKey: keys.SubjectSigningKey,
            signingAlgorithm: CarriageAlgorithms.EcdsaP256Sha256
        );
        var profile = CarriageConformanceProfile.Base.WithExtensions(CarriageConformanceExtensions.SealedCarriageV1);

        var result = profile.VerifyChain(
            codec: codec,
            claim: claim,
            chain: [],
            trustList: trust,
            now: Now,
            expectedPurpose: "profile.sealed",
            expectedAudience: "world:profile"
        );

        AssertRefused(result: result, reasonMustContain: "UTF-8 bytes");
    }
}
