using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class SignatureLevelTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    private static (CborAttestationCodec Codec, SignedAttestation[] Chain, TrustList Trust, SignedAttestation Claim) BuildFixture() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:iris");
        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notBefore: (Epoch - 30), notAfter: (Epoch + (86_400L * 30)));
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "iris's claim");

        return (codec, chain, trust, claim);
    }

    private static AttestationVerifyResult VerifyWithSignature(CborAttestationCodec codec, SignedAttestation[] chain, TrustList trust, SignedAttestation claim, ReadOnlyMemory<byte> signature) =>
        AttestationVerifier.VerifyChain(codec: codec, claim: (claim with { Signature = signature }), chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

    [Fact]
    public void MintedP1363Signature_Verifies() {
        var (codec, chain, trust, claim) = BuildFixture();

        var result = VerifyWithSignature(codec: codec, chain: chain, trust: trust, claim: claim, signature: claim.Signature);

        AssertAccepted(result: result);
    }

    // ECDSA malleability: (r, s) and (r, n-s) are both valid signatures over the same message, and .NET's
    // signer does not canonicalise s. This is a negative result recorded on purpose — a signature is NOT a
    // unique identifier for a claim, so replay defence rests on the sequence mark and the audience, never
    // on "have I seen these bytes before".
    [Fact]
    public void EcdsaMalleability_RnMinusSIsASecondValidSignature() {
        var (codec, chain, trust, claim) = BuildFixture();
        var malleated = MalleateSignature(signature: claim.Signature.Span);

        var result = VerifyWithSignature(codec: codec, chain: chain, trust: trust, claim: claim, signature: malleated);

        Assert.True(condition: result.Verified);
        Assert.False(condition: malleated.AsSpan().SequenceEqual(other: claim.Signature.Span));
    }

    // Encoding malleability, by contrast, is closed: P1363 is a fixed 64 bytes for P-256, so a DER SEQUENCE
    // of the same (r, s) is not a candidate encoding.
    [Fact]
    public void SignatureEncoding_SameRsReencodedAsDer_IsRefused() {
        var (codec, chain, trust, claim) = BuildFixture();

        var result = VerifyWithSignature(codec: codec, chain: chain, trust: trust, claim: claim, signature: EncodeSignatureAsDer(signature: claim.Signature.Span));

        AssertRefused(result: result, reasonMustContain: "signature does not verify");
    }

    [Fact]
    public void SignatureEncoding_ValidSignatureWithOneZeroByteAppended_IsRefused() {
        var (codec, chain, trust, claim) = BuildFixture();

        var result = VerifyWithSignature(codec: codec, chain: chain, trust: trust, claim: claim, signature: (byte[])[.. claim.Signature.Span, 0x00]);

        AssertRefused(result: result, reasonMustContain: "signature does not verify");
    }

    [Fact]
    public void SignatureEncoding_ValidSignatureWithLastByteRemoved_IsRefused() {
        var (codec, chain, trust, claim) = BuildFixture();

        var result = VerifyWithSignature(codec: codec, chain: chain, trust: trust, claim: claim, signature: claim.Signature[..^1]);

        AssertRefused(result: result, reasonMustContain: "signature does not verify");
    }

    [Fact]
    public void SignatureEncoding_AllZeroSignatureOfTheRightLength_IsRefused() {
        var (codec, chain, trust, claim) = BuildFixture();

        var result = VerifyWithSignature(codec: codec, chain: chain, trust: trust, claim: claim, signature: new byte[claim.Signature.Length]);

        AssertRefused(result: result, reasonMustContain: "signature does not verify");
    }
}
