using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class ConformanceProfileTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    [Fact]
    public void Base_RequiresAndAcceptsCborAndEcdsaP256Sha256() {
        var cbor = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var claim = SignTestClaim(codec: cbor, keys: keys, purpose: "profile.test", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, text: "base profile");

        var result = AttestationProfile.Base.VerifyChain(codec: cbor, claim: claim, chain: null, trustList: trust, now: Now, expectedPurpose: "profile.test", expectedAudience: "world:profile");

        AssertAccepted(result: result);
    }

    [Fact]
    public void ResourceProfileFacade_IsTheOnlyPublicVerificationBoundary() {
        Assert.False(condition: typeof(AttestationVerifier).IsPublic);
    }

    [Fact]
    public void ComposedProfileNames_UseTheNormativeExtensionSpelling() {
        var profile = AttestationProfile.Base.WithExtensions(extensions: AttestationExtensions.SealedAttestationV1);

        Assert.Equal(expected: "attestation-v1-base+sealed-attestation-v1", actual: profile.Name);
    }

    [Fact]
    public void AttestationCeiling_IsEnforcedBeforeCborParsing() {
        var cbor = new CborAttestationCodec();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = AttestationProfile.Base.DecodeAttestation(codec: cbor, wire: new byte[AttestationResourceLimits.AttestationBytes + 1]));

        Assert.Contains(expectedSubstring: "permits at most", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PayloadAboveTheExplicitCeiling_IsRefusedDeterministically() {
        var cbor = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var oversizedPayloadClaim = AttestationSigner.SignClaim(codec: cbor, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256, purpose: "profile.test", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, claimBytes: new byte[AttestationResourceLimits.PayloadBytes + 1]);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = AttestationProfile.Base.DecodeAttestation(codec: cbor, wire: cbor.EncodeAttestation(attestation: oversizedPayloadClaim)));

        Assert.Contains(expectedSubstring: "payload", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignatureOutsideTheExactCeiling_IsRefusedBeforeSignatureVerification() {
        var cbor = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var claim = SignTestClaim(codec: cbor, keys: keys, purpose: "profile.test", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, text: "base profile");
        var oversizedSignature = new byte[AttestationResourceLimits.SignatureBytes + 1];

        var result = AttestationProfile.Base.VerifyChain(codec: cbor, claim: (claim with { Signature = oversizedSignature }), chain: null, trustList: trust, now: Now, expectedPurpose: "profile.test", expectedAudience: "world:profile");

        AssertRefused(result: result, reasonMustContain: "exactly 64");
    }

    [Fact]
    public void TextCeilings_AreMeasuredInUtf8BytesNotCharacters() {
        var cbor = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:profile");
        var oversizedPurposeClaim = AttestationSigner.SignClaim(codec: cbor, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256, purpose: new string(c: 'p', count: (AttestationResourceLimits.TextStringUtf8Bytes + 1)), notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:profile", sequence: null, claimBytes: Array.Empty<byte>());

        var exception = Assert.Throws<FormatException>(testCode: () => _ = AttestationProfile.Base.DecodeAttestation(codec: cbor, wire: cbor.EncodeAttestation(attestation: oversizedPurposeClaim)));

        Assert.Contains(expectedSubstring: "UTF-8 bytes", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(230)]
    [InlineData(300)]
    [InlineData(65_400)]
    public void DerivedAttestationLength_MatchesTheRealCborEncoding(int payloadLength) {
        var codec = new CborAttestationCodec();
        var header = new AttestationHeader(
            Domain: new string(c: '0', count: 64),
            Subject: "user:length",
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            Purpose: "profile.length",
            NotBefore: 0L,
            NotAfter: 1L,
            Audience: "world:length",
            Sequence: null
        );
        var attestation = SignedAttestation.Reencode(
            codec: codec,
            header: header,
            payloadKind: AttestationPayloadKind.Opaque,
            payloadBytes: new byte[payloadLength],
            signature: new byte[AttestationResourceLimits.SignatureBytes]
        );

        Assert.Equal(
            expected: codec.EncodeAttestation(attestation: attestation).LongLength,
            actual: CborAttestationCodec.EncodedAttestationLength(attestation: attestation)
        );
    }

    [Fact]
    public void SealedRecipientTextCeiling_IsCheckedOnTheAuthenticatedPayloadDecode() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:sealed-profile");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var header = new AttestationHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            Purpose: "profile.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 1_800),
            Audience: "world:profile",
            Sequence: null
        );
        var sealedPayload = SealedAttestation.Seal(
            recipientId: keys.SubjectSealingId,
            recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
            associatedData: codec.EncodeHeader(header: header),
            plaintext: "profile-checked ciphertext"u8
        );
        var oversizedRecipient = sealedPayload with {
            RecipientId = sealedPayload.RecipientId with {
                Subject = new string(c: 's', count: (AttestationResourceLimits.TextStringUtf8Bytes + 1)),
            },
        };
        var claim = AttestationSigner.Sign(
            codec: codec,
            header: header,
            payloadKind: AttestationPayloadKind.Sealed,
            payloadBytes: codec.EncodeSealedPayload(payload: oversizedRecipient),
            signingKey: keys.SubjectSigningKey,
            signingAlgorithm: AttestationAlgorithms.EcdsaP256Sha256
        );
        var profile = AttestationProfile.Base.WithExtensions(AttestationExtensions.SealedAttestationV1);

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
