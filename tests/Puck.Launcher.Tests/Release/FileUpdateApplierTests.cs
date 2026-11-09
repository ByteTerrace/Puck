using Puck.Assets;
using Puck.Confinement;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over <see cref="FileUpdateApplier"/>: hash re-verification, the release record's current version, the
/// install record, and rollback targets that are never revoked or unproven.</summary>
public sealed class FileUpdateApplierTests : IDisposable {
    private readonly TempStagingRoot m_root = new();

    private string Root => m_root.RootPath;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void Apply_RefusesCaseAliasOfAnActiveDirectory(bool rollbackTarget) {
        var file = Stage(version: "1.0.1-alpha", content: "verified bytes"u8.ToArray());

        _ = InstallSeed.Install(installRoot: Root, version: (rollbackTarget ? "1.0.0" : "1.0.1-Alpha"));
        if (rollbackTarget) {
            // A proven 1.0.1-Alpha stays the rollback target while a newer, unproven version is current.
            _ = InstallSeed.Install(installRoot: Root, version: "1.0.1-Alpha");
            _ = InstallSeed.Install(installRoot: Root, proven: false, version: "1.0.1-Beta");
        }
        var result = Apply(file: file, version: "1.0.1-alpha");

        Assert.False(condition: result.Applied);
        Assert.Contains(expectedSubstring: "collides", actualString: result.RefusalReason!);
    }
    [Fact]
    public void Apply_RefusesDowngradeAndReapplyAgainstTheCurrentVersion() {
        var file = Stage(version: "1.0.2", content: "verified bytes"u8.ToArray());

        _ = Stage(version: "1.0.3", content: "verified bytes"u8.ToArray());
        Assert.True(condition: Apply(file: file, version: "1.0.3").Applied);
        Assert.False(condition: Apply(file: file, version: "1.0.2").Applied);
        Assert.False(condition: Apply(file: file, version: "1.0.3").Applied);
        Assert.Equal(expected: "1.0.3", actual: InstallSeed.Current(installRoot: Root));
        Assert.Null(@object: InstallSeed.RollbackTarget(installRoot: Root));
    }
    [Fact]
    public void Apply_Refuses_WhenAStagedFileWasTamperedAfterStaging() {
        var original = "original"u8.ToArray();

        _ = Stage(version: "1.0.1", content: "tampered"u8.ToArray());
        var result = Apply(file: new ReleasePayloadFile(Path: "a.dll", Hash: ContentPin.Compute(content: original).ToString(), Size: original.Length), version: "1.0.1");

        Assert.False(condition: result.Applied);
        Assert.Contains(expectedSubstring: "hash", actualString: result.RefusalReason!, comparisonType: StringComparison.OrdinalIgnoreCase);
        Assert.Null(@object: InstallSeed.Current(installRoot: Root));
    }
    [Fact]
    public void Apply_Refuses_WhenTheVersionWasNeverStaged() {
        var result = Apply(file: new ReleasePayloadFile(Path: "a.dll", Hash: $"sha256/{new string(c: '0', count: 64)}", Size: 1), version: "1.0.1");

        Assert.False(condition: result.Applied);
        Assert.Contains(expectedSubstring: "not staged", actualString: result.RefusalReason!, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Apply_RecordsTheInstallIdentityAndKeepsOnlyAProvenPreviousAsRollbackTarget() {
        var first = Stage(version: "1.0.0", content: "v1"u8.ToArray());
        var second = Stage(version: "1.0.1", content: "v2"u8.ToArray());
        var third = Stage(version: "1.0.2", content: "v3"u8.ToArray());

        Assert.True(condition: Apply(file: first, version: "1.0.0").Applied);
        // Two applies before any launch: the unstarted 1.0.0 is never a rollback target.
        Assert.Equal(expected: "1.0.0", actual: Apply(file: second, version: "1.0.1", stateGeneration: 2).PreviousVersion);
        Assert.Null(@object: InstallSeed.RollbackTarget(installRoot: Root));
        Assert.True(condition: InstallSeed.Healthy(installRoot: Root, version: "1.0.1"));
        Assert.True(condition: Apply(file: third, version: "1.0.2", stateGeneration: 2).Applied);
        Assert.Equal(expected: "1.0.1", actual: InstallSeed.RollbackTarget(installRoot: Root));
        using var transaction = InstallTransaction.TryBegin(installRoot: Root)!;
        var record = transaction.ReadRecord(version: "1.0.2")!;

        Assert.Equal(expected: 3, actual: record.Sequence);
        Assert.Equal(expected: 2, actual: record.StateGeneration);
        Assert.Equal(expected: ReleasePayloadIdentity.Of(payload: Manifest(file: third, stateGeneration: 2, version: "1.0.2").Payloads[0], stateGeneration: 2),
            actual: record.PayloadPin);
    }
    [Fact]
    public void Apply_NeverKeepsARevokedPreviousAsRollbackTarget() {
        _ = InstallSeed.Install(installRoot: Root, version: "1.0.0");
        _ = InstallSeed.Install(installRoot: Root, version: "1.1.0");
        using (var transaction = InstallTransaction.TryBegin(installRoot: Root)!) {
            Assert.Null(@object: transaction.MergeRevocations(precedence: ReleaseVersion.Compare, revoked: ["1.1.0"]));
        }
        Assert.True(condition: Apply(file: Stage(version: "1.2.0", content: "v12"u8.ToArray()), version: "1.2.0").Applied);
        Assert.Equal(expected: "1.0.0", actual: InstallSeed.RollbackTarget(installRoot: Root));
    }

    private static ReleaseManifest Manifest(string version, int stateGeneration, ReleasePayloadFile file) => new(
        App: "puck.world", Channel: "stable", MinimumSupported: null, Notes: null, Payloads: [new ReleasePayload(Rid: "win-x64", Files: [file])], Revoked: null,
        Rollout: new ReleaseRollout(Percent: 100), Schema: ReleaseManifest.CurrentSchema, Signature: null, StateGeneration: stateGeneration, Version: version);
    private UpdateApplyResult Apply(ReleasePayloadFile file, string version, int stateGeneration = 1) {
        using var transaction = InstallTransaction.TryBegin(installRoot: Root)!;

        return new FileUpdateApplier().Apply(manifest: Manifest(file: file, stateGeneration: stateGeneration, version: version), rid: "win-x64", transaction: transaction);
    }
    private ReleasePayloadFile Stage(string version, byte[] content) {
        var stagedPath = Path.Combine(path1: Root, path2: "versions", path3: version, path4: "a.dll");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: stagedPath)!);
        File.WriteAllBytes(bytes: content, path: stagedPath);
        return new ReleasePayloadFile(Path: "a.dll", Hash: ContentPin.Compute(content: content).ToString(), Size: content.Length);
    }

    public void Dispose() => m_root.Dispose();
}
