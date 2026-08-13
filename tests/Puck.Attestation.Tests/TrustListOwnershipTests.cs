using System.Runtime.InteropServices;

using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class TrustListOwnershipTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    [Fact]
    public void CallerOwnedInputsAndDetachedViews_CannotMutateVerifierState() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:ownership");
        var callerOwnedSpki = keys.SubjectSigningSpki.ToArray();
        var callerOwnedReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:wallet" };
        var entry = new TrustListEntry(
            PinnedId: keys.SubjectSigningId,
            PublicKeySubjectPublicKeyInfo: callerOwnedSpki,
            Mode: AttestationTrustMode.SignsDirectly,
            Reach: callerOwnedReach,
            MaximumAge: null
        );
        var callerOwnedEntries = new List<TrustListEntry> { entry };
        var trust = new TrustList(entries: callerOwnedEntries, defaultMaximumAge: TimeSpan.FromHours(hours: 1));
        var exposedEntry = trust.Entries[0];
        var foundEntry = Assert.IsType<TrustListEntry>(@object: trust.FindDirectSigner(domain: keys.Domain, subject: keys.Subject));

        callerOwnedEntries.Clear();
        callerOwnedReach.Clear();
        callerOwnedSpki[0] ^= 0xFF;
        MutateExposedMemory(memory: exposedEntry.PublicKeySubjectPublicKeyInfo);
        MutateExposedMemory(memory: foundEntry.PublicKeySubjectPublicKeyInfo);

        _ = Assert.Throws<NotSupportedException>(testCode: () => ((IList<TrustListEntry>)trust.Entries)[0] = entry);

        var claim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.ownership",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 1_800),
            audience: "world:home",
            sequence: null,
            text: "the verifier owns its validated snapshot"
        );
        var result = AttestationProfile.Base.VerifyChain(
            codec: codec,
            claim: claim,
            chain: [],
            trustList: trust,
            now: Now,
            expectedPurpose: "test.ownership",
            expectedAudience: "world:home"
        );

        Assert.True(condition: result.Admits(slot: "slot:wallet"), userMessage: result.RefusalReason);
    }

    private static void MutateExposedMemory(ReadOnlyMemory<byte> memory) {
        Assert.True(condition: MemoryMarshal.TryGetArray(memory: memory, segment: out var segment));
        Assert.NotNull(@object: segment.Array);

        segment.Array[segment.Offset] ^= 0xFF;
    }
}
