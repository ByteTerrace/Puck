using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class CoreTests {
    private const long BindingNotAfter = (Epoch + (86_400L * 30));
    private const long BindingNotBefore = (Epoch - 30L);

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    [Fact]
    public void HappyPath_Verifies() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "hello from alice");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }
    // Only one signing algorithm remains (ecdsa-p256-sha256), so a header cannot lie about a rival SIGNING
    // algorithm; the sealing algorithm's name is the only other registered algorithm string, and declaring
    // it still exercises the rule: the header LIES about its algorithm, the real signature was produced
    // under the algorithm the trust chain pins, and the verifier must check against the pin, never the header.
    [Fact]
    public void AlgorithmConfusion_DeclaredAlgorithmDiffersFromPinnedKey_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var confusedClaim = SignTestClaim(audience: "world:home", codec: codec, declaredAlgorithm: AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm, keys: keys, notAfter: (Epoch + 3_600), notBefore: (Epoch - 60), purpose: "test.claim", sequence: null, text: "algorithm-confused claim");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: confusedClaim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "algorithm confusion", result: result);
    }
    [Fact]
    public void PurposeReplay_KeyBindingAttestationPresentedAsClaim_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: issuingToSubject, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "purpose", result: result);
    }
    [Fact]
    public void CrossDomain_ClaimAgainstForeignTrustList_IsRefused() {
        var codec = new CborAttestationCodec();
        var keysB = MintDomainKeys(subject: "user:bob");
        var keysA = MintDomainKeys(subject: "user:alice");

        var (rootToIssuingB, issuingToSubjectB) = BuildChain(codec: codec, keys: keysB, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chainB = new[] { rootToIssuingB, issuingToSubjectB };
        var trustA = BuildTrustList(keys: keysA, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var domainBClaim = SignTestClaim(codec: codec, keys: keysB, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "hello from bob");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: domainBClaim, chain: chainB, trustList: trustA, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "trusted vouching root", result: result);
    }
    [Fact]
    public void CrossDomain_ClaimAgainstOwnTrustList_Accepts() {
        var codec = new CborAttestationCodec();
        var keysB = MintDomainKeys(subject: "user:bob");

        var (rootToIssuingB, issuingToSubjectB) = BuildChain(codec: codec, keys: keysB, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chainB = new[] { rootToIssuingB, issuingToSubjectB };
        var trustB = BuildTrustList(keys: keysB, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var domainBClaim = SignTestClaim(codec: codec, keys: keysB, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "hello from bob");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: domainBClaim, chain: chainB, trustList: trustB, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }
    [Fact]
    public void ExpiredWindow_ClaimPastNotAfter_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var expiredClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 7_200), notAfter: (Epoch - 100), audience: "world:home", sequence: null, text: "stale claim");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: expiredClaim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "expired", result: result);
    }
    [Fact]
    public void ExpiredWindow_ClaimWithinOwnWindow_Accepts() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var freshClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 100), notAfter: (Epoch + 100), audience: "world:home", sequence: null, text: "fresh claim");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: freshClaim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }
    [Fact]
    public void TighterOfTwo_IssuerWindowGovernsWhenTighter_AcceptsWithinIt() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trustGenerous = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var tightIssuerClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 30), notAfter: (Epoch + 60), audience: "world:home", sequence: null, text: "tight issuer window");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: tightIssuerClaim, chain: chain, trustList: trustGenerous, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }
    [Fact]
    public void TighterOfTwo_IssuerWindowGovernsWhenTighter_RefusesPastIssuersOwnWindow() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trustGenerous = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var tightIssuerClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 30), notAfter: (Epoch + 60), audience: "world:home", sequence: null, text: "tight issuer window");
        var laterNow = DateTimeOffset.FromUnixTimeSeconds(seconds: (Epoch + 200));

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: tightIssuerClaim, chain: chain, trustList: trustGenerous, now: laterNow, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "issuer", result: result);
    }
    [Fact]
    public void TighterOfTwo_VerifierCeilingGovernsWhenTighter_AcceptsWithinIt() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trustTight = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 1));
        var tightVerifierClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 100), notAfter: (Epoch + (86_400L * 30)), audience: "world:home", sequence: null, text: "tight verifier ceiling");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: tightVerifierClaim, chain: chain, trustList: trustTight, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }
    [Fact]
    public void TighterOfTwo_VerifierCeilingGovernsWhenTighter_RefusesPastVerifiersHour() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trustTight = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 1));
        var tightVerifierClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 100), notAfter: (Epoch + (86_400L * 30)), audience: "world:home", sequence: null, text: "tight verifier ceiling");
        var muchLaterNow = DateTimeOffset.FromUnixTimeSeconds(seconds: (Epoch + 7_200));

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: tightVerifierClaim, chain: chain, trustList: trustTight, now: muchLaterNow, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "verifier", result: result);
    }
    [Fact]
    public void SeparatedLifetimePolicy_RootBindingCeilingGovernsIndependentlyOfClaimCeiling() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var tightVerifierClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 100), notAfter: (Epoch + (86_400L * 30)), audience: "world:home", sequence: null, text: "tight verifier ceiling");
        var muchLaterNow = DateTimeOffset.FromUnixTimeSeconds(seconds: (Epoch + 7_200));
        var rootAgedTrust = new TrustList(
            entries: [new TrustListEntry(
                PinnedId: keys.RootId,
                PublicKeySubjectPublicKeyInfo: keys.RootSpki,
                Mode: AttestationTrustMode.Vouches,
                Reach: DefaultReach,
                MaximumAge: TimeSpan.FromDays(value: 30),
                RootBindingMaximumAge: TimeSpan.FromHours(hours: 1),
                SubjectBindingMaximumAge: TimeSpan.FromDays(value: 7)
            )],
            defaultMaximumAge: null
        );

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: tightVerifierClaim, chain: chain, trustList: rootAgedTrust, now: muchLaterNow, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "root-vouches-issuing", result: result);
    }
    [Fact]
    public void MissingChain_ClaimWithNoBindings_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var happyClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "hello from alice");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: happyClaim, chain: null, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "missing chain", result: result);
    }
    [Fact]
    public void BrokenChain_IssuingVouchesSubjectBindingAbsent_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, _) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var happyClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "hello from alice");
        var brokenChain = new[] { rootToIssuing };

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: happyClaim, chain: brokenChain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "broken chain", result: result);
    }
    [Fact]
    public void AudienceMismatch_ExpectedAudienceMatches_Accepts() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var marketClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:market", sequence: null, text: "market claim");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: marketClaim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:market");

        AssertAccepted(result: result);
    }
    [Fact]
    public void AudienceMismatch_ExpectedAudienceDiffers_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var marketClaim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:market", sequence: null, text: "market claim");

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: marketClaim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:elsewhere");

        AssertRefused(reasonMustContain: "audience mismatch", result: result);
    }
    [Fact]
    public void BearerSequence_FirstUse_AcceptsAndAdvancesMark() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var store = new ReplayTestStore();
        var bearer10 = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 10UL, text: "bearer sequence 10");

        var result = store.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearer10, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));

        AssertAccepted(result: result);
    }
    [Fact]
    public void BearerSequence_EqualSequenceReplay_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var store = new ReplayTestStore();
        var bearer10 = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 10UL, text: "bearer sequence 10");
        var bearerEqual = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 10UL, text: "bearer sequence 10 replay");

        _ = store.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearer10, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));
        var result = store.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearerEqual, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));

        AssertRefused(reasonMustContain: "replay", result: result);
    }
    [Fact]
    public void BearerSequence_LowerSequenceReplay_IsRefused() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var store = new ReplayTestStore();
        var bearer10 = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 10UL, text: "bearer sequence 10");
        var bearerLower = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 5UL, text: "bearer sequence 5 replay");

        _ = store.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearer10, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));
        var result = store.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearerLower, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));

        AssertRefused(reasonMustContain: "replay", result: result);
    }
    [Fact]
    public void BearerSequence_HigherSequenceAdvancesMark_Accepts() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:alice");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: BindingNotAfter, notBefore: BindingNotBefore);
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var store = new ReplayTestStore();
        var bearer10 = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 10UL, text: "bearer sequence 10");
        var bearer11 = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 11UL, text: "bearer sequence 11");

        _ = store.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearer10, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));
        var result = store.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearer11, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));

        AssertAccepted(result: result);
    }
}
