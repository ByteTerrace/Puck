using Puck.Attestation;
using Puck.Assets;
using Puck.Confinement;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

public sealed class ReleaseSequenceStoreTests : IDisposable {
    private static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(seconds: 60);

    private readonly TempStagingRoot m_root = new();

    private string Install => Path.Combine(path1: m_root.RootPath, path2: "install");
    private string StorePath => Path.Combine(path1: Install, path2: InstallLayout.StateDirectoryName, path3: InstallLayout.SequenceMarkFileName);

    // Every store call runs under a transaction on the install root, waiting for the lease as the updater does. The
    // in-memory store keeps its marks in the instance, so its calls use a root of their own and still run concurrently.
    private T Under<T>(bool persistent, Func<InstallTransaction, T> call) {
        using var transaction = InstallTransaction.Begin(
            cancellationToken: TestContext.Current.CancellationToken,
            installRoot: (persistent ? Install : Path.Combine(path1: m_root.RootPath, path2: Guid.NewGuid().ToString(format: "N"))),
            timeout: LeaseWait
        );

        return call(arg: transaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallReservation_ResumesOnlyItsExactManifestAndEpoch(bool persistent) {
        IReleaseSequenceStore store = persistent ? new FileReleaseSequenceStore() : new InMemoryReleaseSequenceStore();
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 5);
        var pin = ContentPin.Compute(content: new byte[] { 1 });
        var other = ContentPin.Compute(content: new byte[] { 2 });
        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Under(persistent: persistent, call: transaction => store.TryReserve(requirement: requirement, manifestPin: pin, transaction: transaction)));
        if (persistent) { store = new FileReleaseSequenceStore(); }
        Assert.True(condition: Under(persistent: persistent, call: transaction => store.IsReserved(requirement: requirement, manifestPin: pin, transaction: transaction)));
        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Under(persistent: persistent, call: transaction => store.TryReserve(requirement: requirement, manifestPin: pin, transaction: transaction)));
        Assert.Equal(expected: ReleaseDecision.TerminalRefusal, actual: Under(persistent: persistent, call: transaction => store.TryAdvance(requirement: requirement, transaction: transaction)));
        Assert.Equal(expected: ReleaseDecision.TerminalRefusal, actual: Under(persistent: persistent, call: transaction => store.TryReserve(requirement: requirement, manifestPin: other, transaction: transaction)));
        Assert.False(condition: Under(persistent: persistent, call: transaction => store.IsAcceptable(requirement: requirement, transaction: transaction)));
        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Under(persistent: persistent, call: transaction => store.TryAdvance(requirement: requirement with { EpochStartUnixSeconds = 1, Sequence = 0 }, transaction: transaction)));
        Assert.False(condition: Under(persistent: persistent, call: transaction => store.IsReserved(requirement: requirement, manifestPin: pin, transaction: transaction)));
        Assert.Equal(expected: ReleaseDecision.TerminalRefusal, actual: Under(persistent: persistent, call: transaction => store.TryReserve(requirement: requirement, manifestPin: pin, transaction: transaction)));
    }

    [Fact]
    public void SeparateFileStoreInstances_CannotBothCommitTheSameSequence() {
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 5);
        var outcomes = new ReleaseDecision[16];
        Parallel.For(fromInclusive: 0, toExclusive: outcomes.Length,
            body: index => outcomes[index] = Under(persistent: true, call: transaction => new FileReleaseSequenceStore().TryAdvance(requirement: requirement, transaction: transaction)));
        Assert.Single(collection: outcomes, predicate: accepted => accepted == ReleaseDecision.Accepted);
        Assert.False(condition: Under(persistent: true, call: transaction => new FileReleaseSequenceStore().IsAcceptable(requirement: requirement, transaction: transaction)));
    }

    [Fact]
    public void FileStore_RetainsEverySignerAcrossReopenAndEpochChanges() {
        var first = new ReplayCommitRequirement(Domain: "domain-a", Subject: "subject\twith\nseparators", EpochStartUnixSeconds: 100, RetainThroughUnixSeconds: 1000, Sequence: 5);
        var second = first with { Subject = "subject-b", Sequence = 2 };
        var third = first with { Domain = "domain-b", Sequence = 1 };
        var store = new FileReleaseSequenceStore();
        bool Acceptable(ReplayCommitRequirement requirement) => Under(persistent: true, call: transaction => store.IsAcceptable(requirement: requirement, transaction: transaction));
        ReleaseDecision Advance(ReplayCommitRequirement requirement) => Under(persistent: true, call: transaction => store.TryAdvance(requirement: requirement, transaction: transaction));

        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Advance(first));
        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Advance(second));
        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Advance(third));
        store = new FileReleaseSequenceStore();

        Assert.False(condition: Acceptable(first));
        Assert.False(condition: Acceptable(second));
        Assert.False(condition: Acceptable(third));
        Assert.False(condition: Acceptable(first with { EpochStartUnixSeconds = 99, Sequence = ulong.MaxValue }));
        Assert.True(condition: Acceptable(first with { Sequence = 6 }));

        var nextEpoch = first with { EpochStartUnixSeconds = 101, Sequence = 0 };
        Assert.True(condition: Acceptable(nextEpoch));
        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Advance(nextEpoch));
        store = new FileReleaseSequenceStore();
        Assert.False(condition: Acceptable(first with { Sequence = ulong.MaxValue }));
        Assert.False(condition: Acceptable(nextEpoch));
        Assert.False(condition: Acceptable(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("truncated")]
    [InlineData("puck.release.sequence.v1\n")]
    [InlineData("puck.release.sequence.v1\ninvalid\tsubject\t0\t1\n")]
    public void FileStore_RefusesCorruptExistingMarks(string text) {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: StorePath)!);
        File.WriteAllText(path: StorePath, contents: text);
        var store = new FileReleaseSequenceStore();
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 1);

        Assert.False(condition: Under(persistent: true, call: transaction => store.IsAcceptable(requirement: requirement, transaction: transaction)));
        Assert.Equal(expected: ReleaseDecision.TerminalRefusal, actual: Under(persistent: true, call: transaction => store.TryAdvance(requirement: requirement, transaction: transaction)));
        Assert.Equal(expected: text, actual: File.ReadAllText(path: StorePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentCommits_HaveOneWinnerAndCannotLowerTheMark(bool persistent) {
        IReleaseSequenceStore store = persistent ? new FileReleaseSequenceStore() : new InMemoryReleaseSequenceStore();
        var requirement = new ReplayCommitRequirement(Domain: "domain", Subject: "subject", EpochStartUnixSeconds: 0, RetainThroughUnixSeconds: 100, Sequence: 5);
        var outcomes = new ReleaseDecision[32];
        Parallel.For(fromInclusive: 0, toExclusive: outcomes.Length,
            body: index => outcomes[index] = Under(persistent: persistent, call: transaction => store.TryAdvance(requirement: requirement, transaction: transaction)));

        Assert.Single(collection: outcomes, predicate: accepted => accepted == ReleaseDecision.Accepted);
        Assert.Equal(expected: ReleaseDecision.Accepted, actual: Under(persistent: persistent, call: transaction => store.TryAdvance(requirement: requirement with { Sequence = 10 }, transaction: transaction)));
        Assert.Equal(expected: ReleaseDecision.TerminalRefusal, actual: Under(persistent: persistent, call: transaction => store.TryAdvance(requirement: requirement with { Sequence = 9 }, transaction: transaction)));
        Assert.False(condition: Under(persistent: persistent, call: transaction => store.IsAcceptable(requirement: requirement with { Sequence = 10 }, transaction: transaction)));
    }

    public void Dispose() => m_root.Dispose();
}
