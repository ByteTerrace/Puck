using System.Security.Cryptography;

using Xunit;

using static Puck.Carriage.Tests.CarriageTestSupport;

namespace Puck.Carriage.Tests;

public sealed class ChainTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    private static (CborCarriageCodec Codec, DomainKeys Keys, SignedCarriageEnvelope[] Chain, TrustList Trust, SignedCarriageEnvelope Claim) BuildFixture() {
        var codec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:frank");
        var (rootToIssuing, issuingToSubject) = BuildChain(codec: codec, keys: keys, notBefore: (Epoch - 30), notAfter: (Epoch + (86_400L * 30)));
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24));
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "frank's claim");

        return (codec, keys, chain, trust, claim);
    }

    [Fact]
    public void BindingAgeCompatibility_OmittedDefaultInheritsAuthoredCeiling() {
        var (_, keys, _, trust, _) = BuildFixture();

        Assert.Equal(expected: trust.DefaultMaximumAge, actual: trust.DefaultRootBindingMaximumAge);
        Assert.Equal(expected: trust.DefaultMaximumAge, actual: trust.DefaultSubjectBindingMaximumAge);
    }

    [Fact]
    public void DepthTwo_ExactlyTwoBindingsUnderVouchingRoot_Accepts() {
        var (codec, _, chain, trust, claim) = BuildFixture();

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }

    [Fact]
    public void SlotReach_VerifiedClaimCarriesAdmittingEntrysAuthoredReach() {
        var (codec, _, chain, trust, claim) = BuildFixture();

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        Assert.NotNull(@object: result.Reach);
        Assert.True(condition: result.Reach!.SetEquals(other: DefaultReach));
    }

    [Fact]
    public void SlotReach_AdmitsAnswersTheReceivingWorldsQuestion() {
        var (codec, _, chain, trust, claim) = BuildFixture();

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        Assert.True(condition: result.Admits(slot: "slot:wallet"));
        Assert.False(condition: result.Admits(slot: "slot:unlisted"));
    }

    [Fact]
    public void SlotReach_NarrowerEntryYieldsNarrowerReachForTheSameClaim() {
        var (codec, keys, chain, _, claim) = BuildFixture();
        var narrowTrust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24), reach: new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:title" });

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: narrowTrust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        Assert.True(condition: result.Verified);
        Assert.True(condition: result.Admits(slot: "slot:title"));
        Assert.False(condition: result.Admits(slot: "slot:wallet"));
    }

    [Fact]
    public void SlotReach_DenyByDefault_EmptyReachVerifiesButReachesNothing() {
        var (codec, keys, chain, _, claim) = BuildFixture();
        var emptyReachTrust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24), reach: new HashSet<string>(comparer: StringComparer.Ordinal));

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: emptyReachTrust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        Assert.True(condition: result.Verified);
        Assert.NotNull(@object: result.Reach);
        Assert.Empty(collection: result.Reach!);
    }

    [Fact]
    public void SlotReach_VerifiedIsNotAdmission_EmptyReachAdmitsNoSlot() {
        var (codec, keys, chain, _, claim) = BuildFixture();
        var emptyReachTrust = BuildTrustList(keys: keys, defaultMaximumAge: TimeSpan.FromHours(hours: 24), reach: new HashSet<string>(comparer: StringComparer.Ordinal));

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: emptyReachTrust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        Assert.True(condition: result.Verified);
        Assert.False(condition: result.Admits(slot: "slot:wallet"));
        Assert.False(condition: result.Admits(slot: "slot:title"));
        Assert.False(condition: result.Admits(slot: ""));
    }

    [Fact]
    public void SlotReach_RefusedClaimAdmitsNothingAndCarriesNoReach() {
        var (codec, _, chain, trust, claim) = BuildFixture();

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.other", expectedAudience: "world:home");

        Assert.False(condition: result.Verified);
        Assert.False(condition: result.Admits(slot: "slot:wallet"));
        Assert.Null(@object: result.Reach);
    }

    [Fact]
    public void PurposeSeparation_ClaimPresentedForADifferentExpectedPurpose_IsRefused() {
        var (codec, _, chain, trust, claim) = BuildFixture();

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.other", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "purpose mismatch");
    }

    [Fact]
    public void PayloadKindSeparation_ClaimDeclaringKeyBindingKind_IsRefused() {
        var (codec, _, chain, trust, claim) = BuildFixture();
        var kindConfused = SignedCarriageEnvelope.Reencode(codec: codec, header: claim.Header, payloadKind: CarriagePayloadKind.KeyBinding, payloadBytes: claim.PayloadBytes, signature: claim.Signature);

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: kindConfused, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "payload kind must be opaque or sealed");
    }

    [Fact]
    public void PayloadKindSeparation_ClaimDeclaringAnUndefinedKind_IsRefusedByDefault() {
        var (codec, _, chain, trust, claim) = BuildFixture();
        var unknownKind = SignedCarriageEnvelope.Reencode(codec: codec, header: claim.Header, payloadKind: (CarriagePayloadKind)99, payloadBytes: claim.PayloadBytes, signature: claim.Signature);

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: unknownKind, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "payload kind must be opaque or sealed");
    }

    [Fact]
    public void DepthThree_SubjectKeyVouchesForAFurtherKey_IsRefused() {
        var (codec, keys, chain, trust, _) = BuildFixture();

        using var delegateKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        var delegateSpki = delegateKey.ExportSubjectPublicKeyInfo();
        var delegateId = KeyId.ForSubject(domain: keys.Domain, subject: "user:frank", subjectPublicKeyInfo: delegateSpki, algorithm: CarriageAlgorithms.EcdsaP256Sha256);
        var subjectToDelegate = CarriageSigner.SignKeyBinding(codec: codec, domain: keys.Domain, signerKey: keys.SubjectSigningKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, targetId: delegateId, targetSubjectPublicKeyInfo: delegateSpki, notBefore: (Epoch - 30), notAfter: (Epoch + (86_400L * 30)));
        var delegateClaim = CarriageSigner.SignClaim(codec: codec, domain: keys.Domain, subject: "user:frank", signerKey: delegateKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, claimBytes: System.Text.Encoding.UTF8.GetBytes(s: "claim signed one hop too deep"));

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: delegateClaim, chain: [chain[0], chain[1], subjectToDelegate], trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "broken chain");
    }

    [Fact]
    public void DepthOneInDisguise_RootVouchesForItselfAsTheIssuingKey_IsRefused() {
        var (codec, keys, _, trust, claim) = BuildFixture();
        var rootAsIssuingId = KeyId.ForIssuing(domain: keys.Domain, subjectPublicKeyInfo: keys.RootSpki, algorithm: CarriageAlgorithms.EcdsaP256Sha256);
        var rootVouchesItself = CarriageSigner.SignKeyBinding(codec: codec, domain: keys.Domain, signerKey: keys.RootKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, targetId: rootAsIssuingId, targetSubjectPublicKeyInfo: keys.RootSpki, notBefore: (Epoch - 30), notAfter: (Epoch + (86_400L * 30)));
        var rootVouchesSubject = CarriageSigner.SignKeyBinding(codec: codec, domain: keys.Domain, signerKey: keys.RootKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, targetId: keys.SubjectSigningId, targetSubjectPublicKeyInfo: keys.SubjectSigningSpki, notBefore: (Epoch - 30), notAfter: (Epoch + (86_400L * 30)));

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: [rootVouchesItself, rootVouchesSubject], trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "depth one in disguise");
    }

    [Fact]
    public void DepthZero_DirectlyPinnedSubjectKeyAdmitsClaimWithNoBindings() {
        var (codec, keys, _, _, claim) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };
        var directTrust = new TrustList(
            entries: [new TrustListEntry(PinnedId: keys.SubjectSigningId, PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null)],
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: null, trustList: directTrust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }

    [Fact]
    public void DepthZero_BindingsAttachedToADirectlyPinnedClaim_AreRefusedNeverIgnored() {
        var (codec, keys, chain, _, claim) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };
        var directTrust = new TrustList(
            entries: [new TrustListEntry(PinnedId: keys.SubjectSigningId, PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null)],
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: claim, chain: chain, trustList: directTrust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "no bindings");
    }

    [Fact]
    public void PinnedDomain_ClaimNamingADomainNoEntryPins_IsRefused() {
        var (codec, keys, chain, trust, _) = BuildFixture();
        var foreignDomainKeys = MintDomainKeys(subject: keys.Subject);
        var foreignDomainClaim = CarriageSigner.SignClaim(codec: codec, domain: foreignDomainKeys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, claimBytes: System.Text.Encoding.UTF8.GetBytes(s: "frank's key, somebody else's domain"));

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: foreignDomainClaim, chain: chain, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "not a trusted vouching root");
    }

    [Fact]
    public void TrustListShape_SelfConsistentVouchingEntry_Constructs() {
        var (_, keys, _, _, _) = BuildFixture();

        var exception = Record.Exception(testCode: () => _ = BuildTrustList(keys: keys, defaultMaximumAge: null));

        Assert.Null(@object: exception);
    }

    [Fact]
    public void TrustListShape_KeyBytesDoNotHashToPinnedId_Throws() {
        var (_, keys, _, _, _) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };

        var exception = Assert.Throws<ArgumentException>(testCode: () => _ = new TrustList(
            entries: [new TrustListEntry(PinnedId: keys.RootId, PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki, Mode: CarriageTrustMode.Vouches, Reach: friendReach, MaximumAge: null)],
            defaultMaximumAge: null
        ));

        Assert.Contains(expectedSubstring: "not self-certifying", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrustListShape_VouchingEntryPinsAnIssuingKeyRatherThanARoot_Throws() {
        var (_, keys, _, _, _) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };

        var exception = Assert.Throws<ArgumentException>(testCode: () => _ = new TrustList(
            entries: [new TrustListEntry(PinnedId: keys.IssuingId, PublicKeySubjectPublicKeyInfo: keys.IssuingSpki, Mode: CarriageTrustMode.Vouches, Reach: friendReach, MaximumAge: null)],
            defaultMaximumAge: null
        ));

        Assert.Contains(expectedSubstring: "must pin a root id", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrustListShape_DirectlySigningEntryPinsARootRatherThanASubjectKey_Throws() {
        var (_, keys, _, _, _) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };

        var exception = Assert.Throws<ArgumentException>(testCode: () => _ = new TrustList(
            entries: [new TrustListEntry(PinnedId: keys.RootId, PublicKeySubjectPublicKeyInfo: keys.RootSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null)],
            defaultMaximumAge: null
        ));

        Assert.Contains(expectedSubstring: "must pin a SUBJECT key", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrustListShape_EntryPinningASealingKey_Throws() {
        var (_, keys, _, _, _) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };

        var exception = Assert.Throws<ArgumentException>(testCode: () => _ = new TrustList(
            entries: [new TrustListEntry(PinnedId: keys.SubjectSealingId, PublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null)],
            defaultMaximumAge: null
        ));

        Assert.Contains(expectedSubstring: "not a carriage SIGNING algorithm", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrustListShape_SameDomainPinnedTwiceInOneMode_Throws() {
        var (_, keys, _, _, _) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };
        var entry = new TrustListEntry(PinnedId: keys.RootId, PublicKeySubjectPublicKeyInfo: keys.RootSpki, Mode: CarriageTrustMode.Vouches, Reach: friendReach, MaximumAge: null);

        var exception = Assert.Throws<ArgumentException>(testCode: () => _ = new TrustList(entries: [entry, entry], defaultMaximumAge: null));

        Assert.Contains(expectedSubstring: "twice in the same mode", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrustListShape_TwoDifferentKeysPinnedForOneSubjectInOneMode_Throws() {
        var (_, keys, _, _, _) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };

        using var rotatedKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        var rotatedSpki = rotatedKey.ExportSubjectPublicKeyInfo();
        var rotatedId = KeyId.ForSubject(domain: keys.Domain, subject: keys.Subject, subjectPublicKeyInfo: rotatedSpki, algorithm: CarriageAlgorithms.EcdsaP256Sha256);

        var exception = Assert.Throws<ArgumentException>(testCode: () => _ = new TrustList(
            entries: [
                new TrustListEntry(PinnedId: keys.SubjectSigningId, PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null),
                new TrustListEntry(PinnedId: rotatedId, PublicKeySubjectPublicKeyInfo: rotatedSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null),
            ],
            defaultMaximumAge: null
        ));

        Assert.Contains(expectedSubstring: "lookup returns the first match", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrustListShape_SameTwoKeysInDifferentSlots_Constructs() {
        var (_, keys, _, _, _) = BuildFixture();
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };

        using var rotatedKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        var rotatedSpki = rotatedKey.ExportSubjectPublicKeyInfo();

        var exception = Record.Exception(testCode: () => _ = new TrustList(
            entries: [
                new TrustListEntry(PinnedId: keys.SubjectSigningId, PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null),
                new TrustListEntry(PinnedId: KeyId.ForSubject(domain: keys.Domain, subject: "user:other", subjectPublicKeyInfo: rotatedSpki, algorithm: CarriageAlgorithms.EcdsaP256Sha256), PublicKeySubjectPublicKeyInfo: rotatedSpki, Mode: CarriageTrustMode.SignsDirectly, Reach: friendReach, MaximumAge: null),
            ],
            defaultMaximumAge: null
        ));

        Assert.Null(@object: exception);
    }
}
