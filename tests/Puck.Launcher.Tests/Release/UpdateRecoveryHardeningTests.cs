using Puck.Confinement;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

public sealed class UpdateRecoveryHardeningTests : IDisposable {
    [Fact]
    public async Task AnInvalidReleaseRecordFailsHealthClosedWithoutRetrying() {
        using var image = Installed(version: "1.0.1");
        using var registration = image.Register(installRoot: m_root.RootPath);

        File.WriteAllText(path: PolicyPath, contents: "{ truncated");
        var clearing = HealthGateFile.ClearHealthyAsync(cacheRoot: m_root.RootPath, version: "1.0.1",
            registration: registration, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: clearing.IsCompleted);
        var result = await clearing;
        Assert.False(condition: result.Cleared);
        Assert.False(condition: result.Retryable);
    }
    [Fact]
    public async Task HealthyStartupRetriesAfterInstallLeaseContention() {
        using var image = Installed(version: "1.0.1");
        using var registration = image.Register(installRoot: m_root.RootPath);
        Task<HealthClearResult> clearing;

        using (InstallTransaction.TryBegin(installRoot: m_root.RootPath)) {
            clearing = HealthGateFile.ClearHealthyAsync(cacheRoot: m_root.RootPath, version: "1.0.1",
                registration: registration, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(condition: clearing.IsCompleted);
            Assert.Empty(collection: InstallSeed.Policy(installRoot: m_root.RootPath).ProvenInstalls);
        }
        Assert.True(condition: (await clearing).Cleared);
        Assert.Single(collection: InstallSeed.Policy(installRoot: m_root.RootPath).ProvenInstalls);
    }
    [Fact]
    public void HealthRecordingRequiresTheSelectedExecutableAndTheInstallLease() {
        using var oldImage = Installed(proven: true, version: "1.0.0");
        using var newImage = Installed(version: "1.0.1");
        using var old = oldImage.Register(installRoot: m_root.RootPath);
        using var selected = newImage.Register(installRoot: m_root.RootPath);

        Assert.False(condition: HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, registration: old, version: "1.0.0").Cleared);
        // The current version's name must not let an old executable report the new version healthy.
        Assert.False(condition: HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, registration: old, version: "1.0.1").Cleared);
        using (InstallTransaction.TryBegin(installRoot: m_root.RootPath)) {
            Assert.False(condition: HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, registration: selected, version: "1.0.1").Cleared);
        }
        Assert.Equal(expected: "1.0.0", actual: Assert.Single(collection: InstallSeed.Policy(installRoot: m_root.RootPath).ProvenInstalls).Version);
        var cleared = HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, keepVersions: 0, registration: selected, version: "1.0.1");

        Assert.True(condition: cleared.Cleared);
        Assert.Equal(expected: ["1.0.0", "1.0.1"], actual: InstallSeed.Policy(installRoot: m_root.RootPath).ProvenInstalls.Select(selector: static proof => proof.Version));
        // The pass that follows keeps both: current, and the old proven version, now the rollback target.
        Assert.Equal(expected: ["1.0.1", "1.0.0"], actual: cleared.Pruned!.KeptVersions);
        Assert.Empty(collection: cleared.Pruned.RunningVersions);
    }
    // A current version whose install record is missing or invalid has no known generation; the stub refuses to launch
    // it by name rather than guess, and rolls back when a proven install qualifies.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void UnknownGenerationRefusesLaunchOrRollsBack(bool provenFallback) {
        if (provenFallback) { _ = InstallSeed.Install(installRoot: m_root.RootPath, version: "1.0.0"); }
        _ = InstallSeed.Install(installRoot: m_root.RootPath, proven: false, version: "1.0.1");
        File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.1"), path2: InstallRecord.FileName), contents: "{\"sequence\": 2}");
        var decision = InstallSeed.Launch(installRoot: m_root.RootPath, maxAttempts: 3);

        Assert.Equal(expected: (provenFallback ? "1.0.0" : null), actual: decision.Version);
        if (!provenFallback) { Assert.Contains(expectedSubstring: "no valid install record", actualString: decision.Refusal); }
    }

    private readonly TempStagingRoot m_root = new();

    private string PolicyPath => Path.Combine(path1: m_root.RootPath, path2: InstallLayout.StateDirectoryName, path3: InstallLayout.ReleasePolicyFileName);

    private string Version(string version) => Path.Combine(path1: m_root.RootPath, path2: "versions", path3: version);
    // An installed, current, unproven version with an image file a test process maps as if it had been loaded from it.
    private MappedImage Installed(string version, bool proven = false) {
        Directory.CreateDirectory(path: Version(version: version));
        var path = Path.Combine(path1: Version(version: version), path2: "app.dll");

        File.WriteAllText(contents: version, path: path);
        _ = InstallSeed.Install(installRoot: m_root.RootPath, proven: proven, version: version);
        return new MappedImage(path: path);
    }

    public void Dispose() => m_root.Dispose();
}
