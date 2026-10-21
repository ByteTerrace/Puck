using Puck.Attestation;
using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

public sealed class ReleaseSequenceStoreTests : IDisposable {
    private readonly TempStagingRoot m_root = new();
    private string StorePath => Path.Combine(path1: m_root.RootPath, path2: "sequence");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallReservation_ResumesOnlyItsExactManifestAndEpoch(bool persistent) {
        IReleaseSequenceStore store = persistent ? new FileReleaseSequenceStore(filePath: StorePath) : new InMemoryReleaseSequenceStore();
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 5);
        var pin = ContentPin.Compute(content: new byte[] { 1 });
        var other = ContentPin.Compute(content: new byte[] { 2 });
        Assert.True(condition: store.TryReserve(requirement: requirement, manifestPin: pin));
        if (persistent) { store = new FileReleaseSequenceStore(filePath: StorePath); }
        Assert.True(condition: store.IsReserved(requirement: requirement, manifestPin: pin));
        Assert.True(condition: store.TryReserve(requirement: requirement, manifestPin: pin));
        Assert.False(condition: store.TryAdvance(requirement: requirement));
        Assert.False(condition: store.TryReserve(requirement: requirement, manifestPin: other));
        Assert.False(condition: store.IsAcceptable(requirement: requirement));
        Assert.True(condition: store.TryAdvance(requirement: requirement with { EpochStartUnixSeconds = 1, Sequence = 0 }));
        Assert.False(condition: store.IsReserved(requirement: requirement, manifestPin: pin));
        Assert.False(condition: store.TryReserve(requirement: requirement, manifestPin: pin));
    }

    [Fact]
    public void SeparateFileStoreInstances_CannotBothCommitTheSameSequence() {
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 5);
        var outcomes = new bool[16];
        Parallel.For(fromInclusive: 0, toExclusive: outcomes.Length,
            body: index => outcomes[index] = new FileReleaseSequenceStore(filePath: StorePath).TryAdvance(requirement: requirement));
        Assert.Single(collection: outcomes, predicate: accepted => accepted);
        Assert.False(condition: new FileReleaseSequenceStore(filePath: StorePath).IsAcceptable(requirement: requirement));
    }

    [Fact]
    public void FileStore_RetainsEverySignerAcrossReopenAndEpochChanges() {
        var first = new ReplayCommitRequirement(Domain: "domain-a", Subject: "subject\twith\nseparators", EpochStartUnixSeconds: 100, RetainThroughUnixSeconds: 1000, Sequence: 5);
        var second = first with { Subject = "subject-b", Sequence = 2 };
        var third = first with { Domain = "domain-b", Sequence = 1 };
        var store = new FileReleaseSequenceStore(filePath: StorePath);

        Assert.True(condition: store.TryAdvance(requirement: first));
        Assert.True(condition: store.TryAdvance(requirement: second));
        Assert.True(condition: store.TryAdvance(requirement: third));
        store = new FileReleaseSequenceStore(filePath: StorePath);

        Assert.False(condition: store.IsAcceptable(requirement: first));
        Assert.False(condition: store.IsAcceptable(requirement: second));
        Assert.False(condition: store.IsAcceptable(requirement: third));
        Assert.False(condition: store.IsAcceptable(requirement: first with { EpochStartUnixSeconds = 99, Sequence = ulong.MaxValue }));
        Assert.True(condition: store.IsAcceptable(requirement: first with { Sequence = 6 }));

        var nextEpoch = first with { EpochStartUnixSeconds = 101, Sequence = 0 };
        Assert.True(condition: store.IsAcceptable(requirement: nextEpoch));
        Assert.True(condition: store.TryAdvance(requirement: nextEpoch));
        store = new FileReleaseSequenceStore(filePath: StorePath);
        Assert.False(condition: store.IsAcceptable(requirement: first with { Sequence = ulong.MaxValue }));
        Assert.False(condition: store.IsAcceptable(requirement: nextEpoch));
        Assert.False(condition: store.IsAcceptable(requirement: second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("truncated")]
    [InlineData("puck.release.sequence.v1\n")]
    [InlineData("puck.release.sequence.v1\ninvalid\tsubject\t0\t1\n")]
    public void FileStore_RefusesCorruptExistingMarks(string text) {
        Directory.CreateDirectory(path: m_root.RootPath);
        File.WriteAllText(path: StorePath, contents: text);
        var store = new FileReleaseSequenceStore(filePath: StorePath);
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 1);

        Assert.False(condition: store.IsAcceptable(requirement: requirement));
        Assert.False(condition: store.TryAdvance(requirement: requirement));
        Assert.Equal(expected: text, actual: File.ReadAllText(path: StorePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentCommits_HaveOneWinnerAndCannotLowerTheMark(bool persistent) {
        IReleaseSequenceStore store = persistent ? new FileReleaseSequenceStore(filePath: StorePath) : new InMemoryReleaseSequenceStore();
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 5);
        var outcomes = new bool[32];
        Parallel.For(fromInclusive: 0, toExclusive: outcomes.Length, body: index => outcomes[index] = store.TryAdvance(requirement: requirement));

        Assert.Single(collection: outcomes, predicate: accepted => accepted);
        Assert.True(condition: store.TryAdvance(requirement: requirement with { Sequence = 10 }));
        Assert.False(condition: store.TryAdvance(requirement: requirement with { Sequence = 9 }));
        Assert.False(condition: store.IsAcceptable(requirement: requirement with { Sequence = 10 }));
    }

    public void Dispose() => m_root.Dispose();
}
