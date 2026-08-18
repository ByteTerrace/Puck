using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over <see cref="AttestationReleaseVerifier"/>: sequence high-water mark, hash match, revocation,
/// <c>minimumSupported</c>, and version-monotonicity refusals, plus the discriminating replayed-older-manifest
/// leg.</summary>
public sealed class AttestationReleaseVerifierTests {
    private static ReleaseManifest UnsignedDocument(string version = "1.0.1", string? minimumSupported = null, IReadOnlyList<string>? revoked = null) => new(
        App: "puck.world",
        Channel: "stable",
        MinimumSupported: minimumSupported,
        Notes: null,
        Payloads: [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Path: "a.dll", Hash: $"sha256/{new string(c: '0', count: 64)}", Size: 1)])],
        Revoked: revoked,
        Rollout: new ReleaseRollout(Percent: 100),
        Schema: ReleaseManifest.CurrentSchema,
        Signature: null,
        StateGeneration: 1,
        Version: version
    );
    private static (AttestationReleaseVerifier Verifier, ReleaseChainFixture Fixture) BuildVerifier(TimeSpan? replayHorizon = null, IReleaseSequenceStore? sequenceStore = null) {
        var fixture = new ReleaseChainFixture();
        var trustList = fixture.BuildTrustList(replayHorizon: (replayHorizon ?? TimeSpan.FromDays(days: 30)));
        var verifier = new AttestationReleaseVerifier(codec: fixture.Codec, sequenceStore: (sequenceStore ?? new InMemoryReleaseSequenceStore()), trustList: trustList);

        return (verifier, fixture);
    }

    [Fact]
    public void Verify_Accepts_ValidSignedNewerManifest() {
        var (verifier, fixture) = BuildVerifier();
        var manifest = fixture.Sign(document: UnsignedDocument(), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);

        var outcome = verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: manifest, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.True(condition: outcome.Accepted, userMessage: outcome.RefusalReason);
    }
    [Fact]
    public void Verify_Refuses_UnsignedManifest() {
        var (verifier, _) = BuildVerifier();
        var outcome = verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: UnsignedDocument(), now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.False(condition: outcome.Accepted);
    }
    [Fact]
    public void Verify_Refuses_TamperedPayload() {
        var (verifier, fixture) = BuildVerifier();
        var signed = fixture.Sign(document: UnsignedDocument(), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);
        var tampered = (signed with { Notes = "an attacker's note" });

        var outcome = verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: tampered, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.False(condition: outcome.Accepted);
        Assert.Contains(expectedSubstring: "does not match", actualString: outcome.RefusalReason!, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Verify_Refuses_RevokedVersion() {
        var (verifier, fixture) = BuildVerifier();
        var manifest = fixture.Sign(document: UnsignedDocument(revoked: ["1.0.1"]), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);

        var outcome = verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: manifest, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.False(condition: outcome.Accepted);
        Assert.Contains(expectedSubstring: "revoked", actualString: outcome.RefusalReason!, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Verify_Refuses_InstalledBelowMinimumSupported() {
        var (verifier, fixture) = BuildVerifier();
        var manifest = fixture.Sign(document: UnsignedDocument(minimumSupported: "0.9.0"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);

        var outcome = verifier.Verify(advanceSequence: true, installedVersion: "0.8.0", manifest: manifest, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.False(condition: outcome.Accepted);
        Assert.Contains(expectedSubstring: "minimumSupported", actualString: outcome.RefusalReason!, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Verify_Refuses_VersionNotStrictlyGreater() {
        var (verifier, fixture) = BuildVerifier();
        var manifest = fixture.Sign(document: UnsignedDocument(version: "1.0.0"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);

        var outcome = verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: manifest, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.False(condition: outcome.Accepted);
        Assert.Contains(expectedSubstring: "not strictly greater", actualString: outcome.RefusalReason!, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Verify_RefusesReplayedEqualSequence_AcceptsStrictlyGreaterSequence() {
        var store = new InMemoryReleaseSequenceStore();

        var (verifier, fixture) = BuildVerifier(sequenceStore: store);
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch);
        var first = fixture.Sign(document: UnsignedDocument(version: "1.0.1"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 5);

        Assert.True(condition: verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: first, now: now).Accepted);

        // The DISCRIMINATING leg: a client already holding 1.0.1 is served a validly signed OLDER manifest,
        // still signed by the same throwaway chain, at a sequence that does not strictly exceed the stored mark.
        // Signature/hash/revocation/minimumSupported all pass; only the sequence high-water mark can refuse it.
        var replayed = fixture.Sign(document: UnsignedDocument(version: "0.9.0"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 5);
        var replayOutcome = verifier.Verify(advanceSequence: true, installedVersion: "1.0.1", manifest: replayed, now: now);

        Assert.False(condition: replayOutcome.Accepted);
        Assert.Contains(expectedSubstring: "sequence", actualString: replayOutcome.RefusalReason!, comparisonType: StringComparison.OrdinalIgnoreCase);

        var next = fixture.Sign(document: UnsignedDocument(version: "1.0.2"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 6);

        Assert.True(condition: verifier.Verify(advanceSequence: true, installedVersion: "1.0.1", manifest: next, now: now).Accepted);
    }
    [Fact]
    public void Verify_DoesNotAdvanceSequenceMark_OnLaterRefusal() {
        var store = new InMemoryReleaseSequenceStore();

        var (verifier, fixture) = BuildVerifier(sequenceStore: store);
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch);

        // A higher sequence whose OTHER checks fail (revoked) must not consume the mark — a legitimately higher
        // claim must not suppress a later, valid, lower-or-equal-but-still-unused sequence at the same value.
        var revoked = fixture.Sign(document: UnsignedDocument(version: "1.0.1", revoked: ["1.0.1"]), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 5);

        Assert.False(condition: verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: revoked, now: now).Accepted);

        var accepted = fixture.Sign(document: UnsignedDocument(version: "1.0.1"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 5);

        Assert.True(condition: verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: accepted, now: now).Accepted);
    }
    [Fact]
    public void Verify_WithAdvanceSequenceFalse_NeverConsumesTheMark_SoACommittingCallStillAccepts() {
        var store = new InMemoryReleaseSequenceStore();

        var (verifier, fixture) = BuildVerifier(sequenceStore: store);
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch);
        var manifest = fixture.Sign(document: UnsignedDocument(), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 5);

        // Repeated read-only inspections (the update.check shape) must both accept and never commit anything.
        Assert.True(condition: verifier.Verify(advanceSequence: false, installedVersion: "1.0.0", manifest: manifest, now: now).Accepted);
        Assert.True(condition: verifier.Verify(advanceSequence: false, installedVersion: "1.0.0", manifest: manifest, now: now).Accepted);

        // The one committing call (the update.apply shape) over the SAME manifest still accepts — it is not a
        // replay of a mark the read-only calls above never wrote.
        Assert.True(condition: verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: manifest, now: now).Accepted);

        // Now the mark IS advanced: the identical manifest replayed a second time is refused.
        var replay = verifier.Verify(advanceSequence: false, installedVersion: "1.0.0", manifest: manifest, now: now);

        Assert.False(condition: replay.Accepted);
        Assert.Contains(expectedSubstring: "sequence", actualString: replay.RefusalReason!, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
