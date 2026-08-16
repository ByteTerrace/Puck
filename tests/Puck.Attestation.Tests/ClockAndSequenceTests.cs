using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class ClockAndSequenceTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    private static (CborAttestationCodec Codec, DomainKeys Keys, SignedAttestation[] Chain, TrustList Trust) BuildFixture() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:gwen");

        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notAfter: (Epoch + (86_400L * 30)), notBefore: (Epoch - 30));
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));

        return (codec, keys, chain, trust);
    }
    private static AttestationVerifyResult VerifyAt(CborAttestationCodec codec, SignedAttestation[] chain, TrustList trust, SignedAttestation attestation, DateTimeOffset now, ReplayTestStore? store) {
        var result = AttestationVerifier.VerifyChain(codec: codec, claim: attestation, chain: chain, trustList: trust, now: now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        return ((store is null) ? result : store.Commit(result: result));
    }

    // There is no skew tolerance: notBefore is honoured to the second, in both directions.
    [Fact]
    public void ClockSkew_NotBeforeEqualToNow_IsInsideTheInclusiveWindow() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: Epoch, notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "opens exactly now");

        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        AssertAccepted(result: result);
    }
    [Fact]
    public void ClockSkew_NotBeforeOneSecondInTheFuture_IsRefused() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch + 1), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "one second early");

        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        AssertRefused(reasonMustContain: "not yet valid", result: result);
    }
    [Fact]
    public void ClockSkew_NotAfterEqualToNow_IsInsideTheInclusiveWindow() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: Epoch, audience: "world:home", sequence: null, text: "closes exactly now");

        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        AssertAccepted(result: result);
    }
    [Fact]
    public void ClockSkew_NotAfterOneSecondInThePast_IsRefused() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch - 1), audience: "world:home", sequence: null, text: "one second late");

        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        AssertRefused(reasonMustContain: "expired", result: result);
    }
    [Fact]
    public void MalformedWindow_NotAfterPrecedesNotBefore_IsRefused() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch + 3_600), notAfter: (Epoch - 3_600), audience: "world:home", sequence: null, text: "inverted window");

        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        AssertRefused(reasonMustContain: "malformed window", result: result);
    }
    // A directed claim WITHOUT a sequence replays freely at its own audience — the control that proves the
    // next case is the sequence doing the work.
    [Fact]
    public void DirectedClaimWithoutSequence_ReplaysFreelyAtItsOwnAudience() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "directed, no sequence");
        var store = new ReplayTestStore();

        var first = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: store, trust: trust);
        var second = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: store, trust: trust);

        AssertAccepted(result: first);
        AssertAccepted(result: second);
    }
    [Fact]
    public void DirectedClaimWithSequence_SecondPresentationIsRefused() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: 7UL, text: "directed, sequence 7");
        var store = new ReplayTestStore();

        var first = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: store, trust: trust);
        var second = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: store, trust: trust);

        AssertAccepted(result: first);
        AssertRefused(reasonMustContain: "sequence replay", result: second);
    }
    [Fact]
    public void PureVerification_SequencedClaimReturnsAReplayCommitRequirementWithoutMutatingStorage() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: 7UL, text: "directed, sequence 7");

        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        Assert.True(condition: result.Verified);
        Assert.NotNull(@object: result.ReplayCommit);
    }
    [Fact]
    public void SequencedVerificationIsNotAdmission_AdmitsStaysFalseUntilTheReceiverCommits() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: 7UL, text: "directed, sequence 7");

        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        Assert.False(condition: result.Admits(slot: "slot:wallet"));
        Assert.True(condition: result.TryGetReplayCommit(requirement: out var requirement, slot: "slot:wallet"));
        Assert.NotNull(@object: requirement);
    }
    [Fact]
    public void FiniteReplayHorizon_DerivesTheEpochAndRetentionDeadlineFromSignedNotBefore() {
        var (codec, keys, chain, trust) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: 7UL, text: "directed, sequence 7");
        var result = VerifyAt(attestation: claim, chain: chain, codec: codec, now: Now, store: null, trust: trust);
        var commit = result.ReplayCommit!;
        var horizonSeconds = 86_400L;
        var expectedEpochStart = (Math.DivRem(a: claim.Header.NotBefore, b: horizonSeconds, result: out _) * horizonSeconds);

        Assert.Equal(expected: expectedEpochStart, actual: commit.EpochStartUnixSeconds);
        Assert.Equal(expected: ((expectedEpochStart + (2 * horizonSeconds)) - 1), actual: commit.RetainThroughUnixSeconds);
        Assert.Equal(expected: 7UL, actual: commit.Sequence);
    }
    [Fact]
    public void FiniteReplayHorizon_NoFiniteHorizonRefusesEverySequencedClaim() {
        var (codec, keys, chain, _) = BuildFixture();
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: 7UL, text: "directed, sequence 7");
        var noHorizonTrust = new TrustList(
            entries: [new TrustListEntry(PinnedId: keys.RootId, PublicKeySubjectPublicKeyInfo: keys.RootSpki, Mode: AttestationTrustMode.Vouches, Reach: DefaultReach, MaximumAge: null)],
            defaultMaximumAge: null
        );

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: noHorizonTrust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "no finite replay-acceptance horizon", result: result);
    }
    [Fact]
    public void FiniteReplayHorizon_SequencedWindowLongerThanHorizon_IsRefused() {
        var (codec, keys, chain, trust) = BuildFixture();
        var horizonSeconds = 86_400L;
        var tooLongSequenced = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: ((Epoch + horizonSeconds) + 60), audience: "world:home", sequence: 8UL, text: "window exceeds replay horizon");

        var result = VerifyAt(attestation: tooLongSequenced, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        AssertRefused(reasonMustContain: "exceeds the verifier's replay-acceptance horizon", result: result);
    }
    [Fact]
    public void ExtremeNotBefore_RefusesWithoutInt64SubtractionOrTimeSpanOverflow() {
        var (codec, keys, chain, trust) = BuildFixture();
        var extremeNotBefore = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: long.MinValue, notAfter: (Epoch + 60), audience: "world:home", sequence: 9UL, text: "extreme wire timestamp");

        var result = VerifyAt(attestation: extremeNotBefore, chain: chain, codec: codec, now: Now, store: null, trust: trust);

        AssertRefused(reasonMustContain: "exceeds the verifier's maximum age", result: result);
    }
    // The sequence check is one atomic store call, so the same bearer claim presented concurrently is
    // accepted exactly once — a store that let every caller read before any caller wrote would turn the
    // race from intermittent into certain, accepting both presentations.
    [Fact]
    public void SequenceAtomicity_OneBearerClaimPresentedByFourReceiversAtOnce_AcceptsExactlyOnce() {
        var (codec, keys, chain, trust) = BuildFixture();
        var bearerOnce = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 42UL, text: "one bearer claim, two receivers");
        var contendedStore = new ReplayTestStore(participants: 4);
        var contendedResults = new AttestationVerifyResult[4];

        Parallel.For(fromInclusive: 0, toExclusive: 4, body: index => contendedResults[index] = contendedStore.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearerOnce, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null)));

        Assert.Equal(expected: 1, actual: contendedResults.Count(predicate: result => result.Verified));
    }
    [Fact]
    public void SequenceAtomicity_SplitCompareAndAdvanceControl_AcceptsEveryPresentation() {
        var (codec, keys, chain, trust) = BuildFixture();
        var bearerOnce = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 42UL, text: "one bearer claim, four receivers");
        var brokenStore = new SplitReplayTestStore(participants: 4);
        var contendedResults = new AttestationVerifyResult[4];

        Parallel.For(fromInclusive: 0, toExclusive: 4, body: index => contendedResults[index] = brokenStore.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearerOnce, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null)));

        Assert.Equal(expected: 4, actual: contendedResults.Count(predicate: result => result.Verified));
    }
    [Fact]
    public void SequenceAtomicity_UncontendedStore_Accepts() {
        var (codec, keys, chain, trust) = BuildFixture();
        var bearerOnce = SignTestClaim(codec: codec, keys: keys, purpose: "test.bearer", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: null, sequence: 42UL, text: "one bearer claim, two receivers");
        var uncontendedStore = new ReplayTestStore(participants: 1);

        var result = uncontendedStore.Commit(result: AttestationVerifier.VerifyChain(codec: codec, claim: bearerOnce, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.bearer", expectedAudience: null));

        AssertAccepted(result: result);
    }
}
