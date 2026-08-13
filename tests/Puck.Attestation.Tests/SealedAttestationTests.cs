using System.Security.Cryptography;
using System.Text;

using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class SealedAttestationTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    private static (CborAttestationCodec Codec, DomainKeys Keys, AttestationHeader Header, byte[] HeaderBytes, byte[] Plaintext, SealedPayload Sealed) BuildFixture() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:dana");
        var header = new AttestationHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm,
            Purpose: "test.sealed-claim",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 3_600),
            Audience: "world:vault",
            Sequence: null
        );
        var headerBytes = codec.EncodeHeader(header: header);
        var plaintext = Encoding.UTF8.GetBytes(s: "a secret only dana's sealing key can open");
        var sealedPayload = SealedAttestation.Seal(recipientId: keys.SubjectSealingId, recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki, associatedData: headerBytes, plaintext: plaintext);

        return (codec, keys, header, headerBytes, plaintext, sealedPayload);
    }

    [Fact]
    public void RoundTrip_PlaintextRecoveredExactly() {
        var (_, keys, _, headerBytes, plaintext, sealedPayload) = BuildFixture();

        var recovered = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: sealedPayload, associatedData: headerBytes);

        Assert.True(condition: plaintext.AsSpan().SequenceEqual(other: recovered));
    }

    [Fact]
    public void BindingAttestationKnownAnswer_IndependentSealedAttestationOpensExactly() {
        const string SealedAttestationBase64 = "glkBsosBWCAeZLSaUYAZHCocFTx+EPR39ATppL+x1+q5xyf8Rhdq+Hghd2ViLmZ1bmN0aW9uczppbnRlcmNoYW5nZS1zdWJqZWN0cWVjZHNhLXAyNTYtc2hhMjU2eBtjYXJyaWFnZS5jcm9zcy1jaGVjay5zZWFsZWQaan0CkhpqpJ2icXdvcmxkOmludGVyY2hhbmdl9gNZARuIWCA3O22jicb/s7iJOH+lQbRwegSaheqKFpgfs/QFeK97VfZ4H2VjZGgtcDI1Ni1oa2RmLXNoYTI1Ni1hZXMyNTZnY21YIDc7baOJxv+zuIk4f6VBtHB6BJqF6ooWmB+z9AV4r3tVWFswWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAQxo4M6SFz87nFc/5UscQupi/bVb6SruO6QdIO+zUunups4Fwkad47MS1Yt2xxB1BJjuog9wz39H1hYKnl1VybqTNmP5UpuNB6CUyppYVCTwOZ+mYDz9lcxyFEB46P/WDfMdH5459UXohQ1c6SmL4Y2DLcVzzw7BYbDgxmAMj3DeILTvQbLZt39SdmOTSLwlilwcEnlsbd3WEBwIVNxdaB504veT8gRT1ajnJvKDjJte032xEdKapwvdYigegv842AIVNXHmIWGQ829Hysc+2khHSil/dmP4u6u";
        const string RecipientPrivateKeyBase64 = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg/aB8cus5B1vOFFpOmvz23Hp/X5p2pN/OT9i8uQ5zbgihRANCAAQxuZWmDSc5hgEQloPvOVe1IU0tVD+YrLGEkh/TJfo1RQIb08YRGr+rt4XI5ivUkULJM1HaECoqu7UXGrgcgBOL";
        var codec = new CborAttestationCodec();
        var attestation = codec.DecodeAttestation(wire: Convert.FromBase64String(s: SealedAttestationBase64));
        var payload = codec.DecodeSealedPayload(bytes: attestation.PayloadBytes.Span);

        using var recipientKey = ECDiffieHellman.Create();

        recipientKey.ImportPkcs8PrivateKey(
            source: Convert.FromBase64String(s: RecipientPrivateKeyBase64),
            bytesRead: out _
        );

        var plaintext = SealedAttestation.Unseal(
            recipientPrivateKey: recipientKey,
            payload: payload,
            associatedData: codec.EncodeHeader(header: attestation.Header)
        );

        Assert.Equal(
            expected: "sealed by BindingCarriage under puck.carriage.sealed.v1",
            actual: Encoding.UTF8.GetString(bytes: plaintext)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AadTamper_FlippingAHeaderByte_BreaksDecryption(int offsetFromEnd) {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();
        var offset = ((offsetFromEnd >= 0) ? offsetFromEnd : (headerBytes.Length + offsetFromEnd));
        var tampered = (byte[])headerBytes.Clone();

        tampered[offset] ^= 0xFF;

        var exception = Assert.ThrowsAny<CryptographicException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: sealedPayload, associatedData: tampered));

        Assert.Contains(expectedSubstring: "tag", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CiphertextTamper_FlippingOneByte_BreaksDecryption() {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();
        var tamperedCiphertext = sealedPayload.Ciphertext.ToArray();

        tamperedCiphertext[0] ^= 0xFF;

        var exception = Assert.ThrowsAny<CryptographicException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: (sealedPayload with { Ciphertext = tamperedCiphertext }), associatedData: headerBytes));

        Assert.Contains(expectedSubstring: "tag", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongRecipient_AnotherIdentitysSealingKeyIsRefusedBeforeDecryption() {
        var (_, _, _, headerBytes, _, sealedPayload) = BuildFixture();
        var otherKeys = MintDomainKeys(subject: "user:eve");

        var exception = Assert.Throws<FormatException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: otherKeys.SubjectSealingKey, payload: sealedPayload, associatedData: headerBytes));

        Assert.Contains(expectedSubstring: "does not identify", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecipientSpki_TrailingBytesAreRefusedBeforeSealing() {
        var (_, keys, _, headerBytes, plaintext, _) = BuildFixture();
        var tailedRecipientSpki = (byte[])[.. keys.SubjectSealingSpki, 0x00];
        var tailedRecipientId = KeyId.ForSubject(
            domain: keys.Domain,
            subject: keys.Subject,
            subjectPublicKeyInfo: tailedRecipientSpki,
            algorithm: AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm
        );

        var exception = Assert.Throws<FormatException>(testCode: () => _ = SealedAttestation.Seal(
            recipientId: tailedRecipientId,
            recipientPublicKeySubjectPublicKeyInfo: tailedRecipientSpki,
            associatedData: headerBytes,
            plaintext: plaintext
        ));

        Assert.Contains(expectedSubstring: "trailing", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoleSeparation_ASigningAlgorithmIdCannotBePresentedAsARecipientSealingKey() {
        var (_, keys, _, headerBytes, plaintext, _) = BuildFixture();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = SealedAttestation.Seal(recipientId: keys.SubjectSigningId, recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki, associatedData: headerBytes, plaintext: plaintext));

        Assert.Contains(expectedSubstring: "rather than a sealing algorithm", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecipientContextTamper_ChangingTheRecipientSubject_BreaksTheAeadTag() {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();

        var exception = Assert.ThrowsAny<CryptographicException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: (sealedPayload with { RecipientId = sealedPayload.RecipientId with { Subject = "user:someone-else" } }), associatedData: headerBytes));

        Assert.Contains(expectedSubstring: "tag", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidCurve_AP384EphemeralKeyOfferedAgainstAP256Recipient_IsRefused() {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();

        using var wrongCurveKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP384);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: (sealedPayload with { EphemeralPublicKeySubjectPublicKeyInfo = wrongCurveKey.ExportSubjectPublicKeyInfo() }), associatedData: headerBytes));

        Assert.Contains(expectedSubstring: "not on P-256", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EphemeralSpki_TrailingBytesAreRefusedBeforeAgreement() {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();
        var tailedEphemeralSpki = (byte[])[.. sealedPayload.EphemeralPublicKeySubjectPublicKeyInfo.Span, 0x00];

        var exception = Assert.Throws<FormatException>(testCode: () => _ = SealedAttestation.Unseal(
            recipientPrivateKey: keys.SubjectSealingKey,
            payload: sealedPayload with { EphemeralPublicKeySubjectPublicKeyInfo = tailedEphemeralSpki },
            associatedData: headerBytes
        ));

        Assert.Contains(expectedSubstring: "trailing", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // An SPKI names a key TYPE before it names a curve; an RSA SPKI has no curve to ask about at all, so
    // the type check (the AlgorithmIdentifier OID) has to come first.
    [Fact]
    public void WrongKeyType_AnRsaSpkiOfferedAsTheEphemeralKey_IsRefusedOnItsAlgorithmOid() {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();

        using var rsaKey = RSA.Create(keySizeInBits: 2048);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: (sealedPayload with { EphemeralPublicKeySubjectPublicKeyInfo = rsaKey.ExportSubjectPublicKeyInfo() }), associatedData: headerBytes));

        Assert.Contains(expectedSubstring: "does not import as an EC public key", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // An EC SPKI carries id-ecPublicKey whether its holder means to sign or to agree, so a P-256 SIGNING
    // key's SPKI imports cleanly as an agreement key and fails only at the AEAD tag.
    [Fact]
    public void KeyTypeControl_ASigningKeysSpkiImportsAsAnAgreementKeyAndFailsOnlyAtTheTag() {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();

        var exception = Assert.ThrowsAny<CryptographicException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: (sealedPayload with { EphemeralPublicKeySubjectPublicKeyInfo = keys.SubjectSigningSpki }), associatedData: headerBytes));

        Assert.Contains(expectedSubstring: "tag", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedNonce_AnElevenByteNonce_IsRefusedAsAFormatError() {
        var (_, keys, _, headerBytes, _, sealedPayload) = BuildFixture();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: (sealedPayload with { Nonce = sealedPayload.Nonce[..^1] }), associatedData: headerBytes));

        Assert.Contains(expectedSubstring: "nonce must be", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncodeDecodeSymmetry_AnInvalidRecipientFingerprint_IsRefusedByTheEncoder() {
        var (codec, _, _, _, _, sealedPayload) = BuildFixture();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.EncodeSealedPayload(payload: sealedPayload with { RecipientId = sealedPayload.RecipientId with { KeyHash = "not-a-fingerprint" } }));

        Assert.Contains(expectedSubstring: "fingerprint", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // The nonce is random per seal AND the AEAD key is fresh per seal (ephemeral agreement), so two seals of
    // the same plaintext under the same header share neither.
    [Fact]
    public void NonceUniqueness_TwoSealsOfTheSamePlaintext_ShareNeitherNonceNorEphemeralKey() {
        var (_, keys, _, headerBytes, plaintext, sealedPayload) = BuildFixture();
        var secondSeal = SealedAttestation.Seal(recipientId: keys.SubjectSealingId, recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki, associatedData: headerBytes, plaintext: plaintext);

        Assert.False(condition: secondSeal.Nonce.Span.SequenceEqual(other: sealedPayload.Nonce.Span));
        Assert.False(condition: secondSeal.EphemeralPublicKeySubjectPublicKeyInfo.Span.SequenceEqual(other: sealedPayload.EphemeralPublicKeySubjectPublicKeyInfo.Span));
    }

    private static (CborAttestationCodec Codec, DomainKeys Keys, SignedAttestation[] Chain, TrustList Trust, AttestationHeader Header, byte[] Plaintext) BuildAttestationFixture() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:mira");
        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notBefore: (Epoch - 30), notAfter: (Epoch + (86_400L * 30)));
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var header = new AttestationHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            Purpose: "test.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 3_600),
            Audience: "world:vault",
            Sequence: null
        );
        var plaintext = "a sealed claim riding inside a signed attestation"u8.ToArray();

        return (codec, keys, chain, trust, header, plaintext);
    }

    [Fact]
    public void SealedAttestation_SurvivesEncodeDecodeAndItsChainVerifies() {
        var (codec, keys, chain, trust, header, plaintext) = BuildAttestationFixture();
        var payload = SealedAttestation.Seal(recipientId: keys.SubjectSealingId, recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki, associatedData: codec.EncodeHeader(header: header), plaintext: plaintext);
        var attestation = AttestationSigner.Sign(codec: codec, header: header, payloadKind: AttestationPayloadKind.Sealed, payloadBytes: codec.EncodeSealedPayload(payload: payload), signingKey: keys.SubjectSigningKey, signingAlgorithm: AttestationAlgorithms.EcdsaP256Sha256);
        var decoded = codec.DecodeAttestation(wire: codec.EncodeAttestation(attestation: attestation));

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: decoded, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.sealed", expectedAudience: "world:vault");

        AssertAccepted(result: result);
    }

    [Fact]
    public void SealedAttestationValidation_AuthenticatedButMalformedPayload_IsRefusedDuringClaimVerification() {
        var (codec, keys, chain, trust, header, _) = BuildAttestationFixture();
        var malformed = AttestationSigner.Sign(codec: codec, header: header, payloadKind: AttestationPayloadKind.Sealed, payloadBytes: new byte[] { 0x00 }, signingKey: keys.SubjectSigningKey, signingAlgorithm: AttestationAlgorithms.EcdsaP256Sha256);

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: malformed, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.sealed", expectedAudience: "world:vault");

        AssertRefused(result: result, reasonMustContain: "sealed claim payload is malformed");
    }

    [Fact]
    public void SealedAttestationValidationOrder_MalformedNestedBytesUnderABadSignature_StopsAtAuthentication() {
        var (codec, keys, chain, trust, header, _) = BuildAttestationFixture();
        var malformed = AttestationSigner.Sign(codec: codec, header: header, payloadKind: AttestationPayloadKind.Sealed, payloadBytes: new byte[] { 0x00 }, signingKey: keys.SubjectSigningKey, signingAlgorithm: AttestationAlgorithms.EcdsaP256Sha256);
        var badSignature = malformed.Signature.ToArray();

        badSignature[^1] ^= 0xFF;

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: (malformed with { Signature = badSignature }), chain: chain, trustList: trust, now: Now, expectedPurpose: "test.sealed", expectedAudience: "world:vault");

        AssertRefused(result: result, reasonMustContain: "claim signature");
    }

    [Fact]
    public void SealedAttestation_DecodedPayloadOpensToTheOriginalPlaintext() {
        var (codec, keys, _, _, header, plaintext) = BuildAttestationFixture();
        var payload = SealedAttestation.Seal(recipientId: keys.SubjectSealingId, recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki, associatedData: codec.EncodeHeader(header: header), plaintext: plaintext);
        var attestation = AttestationSigner.Sign(codec: codec, header: header, payloadKind: AttestationPayloadKind.Sealed, payloadBytes: codec.EncodeSealedPayload(payload: payload), signingKey: keys.SubjectSigningKey, signingAlgorithm: AttestationAlgorithms.EcdsaP256Sha256);
        var decoded = codec.DecodeAttestation(wire: codec.EncodeAttestation(attestation: attestation));

        var recovered = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: codec.DecodeSealedPayload(bytes: decoded.PayloadBytes.Span), associatedData: codec.EncodeHeader(header: decoded.Header));

        Assert.True(condition: plaintext.AsSpan().SequenceEqual(other: recovered));
    }

    [Fact]
    public void SealedAttestationAadControl_SameCiphertextUnderAOneFieldDifferentHeader_IsRefused() {
        var (codec, keys, _, _, header, plaintext) = BuildAttestationFixture();
        var payload = SealedAttestation.Seal(recipientId: keys.SubjectSealingId, recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki, associatedData: codec.EncodeHeader(header: header), plaintext: plaintext);
        var attestation = AttestationSigner.Sign(codec: codec, header: header, payloadKind: AttestationPayloadKind.Sealed, payloadBytes: codec.EncodeSealedPayload(payload: payload), signingKey: keys.SubjectSigningKey, signingAlgorithm: AttestationAlgorithms.EcdsaP256Sha256);
        var decoded = codec.DecodeAttestation(wire: codec.EncodeAttestation(attestation: attestation));

        var exception = Assert.ThrowsAny<CryptographicException>(testCode: () => _ = SealedAttestation.Unseal(recipientPrivateKey: keys.SubjectSealingKey, payload: codec.DecodeSealedPayload(bytes: decoded.PayloadBytes.Span), associatedData: codec.EncodeHeader(header: (decoded.Header with { Audience = "world:elsewhere" }))));

        Assert.Contains(expectedSubstring: "tag", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
