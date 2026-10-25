using Puck.Assets;
using Puck.Launcher.Release;
using Puck.Launcher.Stub;
using Xunit;

namespace Puck.Launcher.Tests.Release;

public sealed class UpdateRecoveryHardeningTests : IDisposable {
    private readonly TempStagingRoot m_root = new();

    private string Current => Path.Combine(path1: m_root.RootPath, path2: "current");
    private string Marker => Path.Combine(path1: m_root.RootPath, path2: "state", path3: "health.json");

    private string Version(string version) => Path.Combine(path1: m_root.RootPath, path2: "versions", path3: version);
    private void PendingHealth(string version = "1.0.1") {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: Marker)!);
        File.WriteAllText(path: Marker, contents: "pending");
        File.WriteAllText(path: Current, contents: version);
    }

    [Fact]
    public void HealthClearingRequiresTheSelectedExecutableAndTheInstallLease() {
        PendingHealth();
        HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.0", runningDirectory: Version(version: "1.0.0"));
        Assert.True(condition: File.Exists(path: Marker));
        // Reading the new pointer must not let an old executable report the new version healthy.
        HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.1", runningDirectory: Version(version: "1.0.0"));
        Assert.True(condition: File.Exists(path: Marker));
        using (StubInstall.AcquireUpdateLease(installRoot: m_root.RootPath)) {
            HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.1", runningDirectory: Version(version: "1.0.1"));
            Assert.True(condition: File.Exists(path: Marker));
        }
        HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.1", runningDirectory: Version(version: "1.0.1"));
        Assert.False(condition: File.Exists(path: Marker));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ApplyingOverAnUnprovenCurrentPreservesLastGood(bool hasLastGood) {
        PendingHealth();
        var lastGood = Path.Combine(path1: m_root.RootPath, path2: "last-good");

        if (hasLastGood) { File.WriteAllText(contents: "1.0.0", path: lastGood); }
        var content = "new binary"u8.ToArray();

        Directory.CreateDirectory(path: Version(version: "1.0.2"));
        File.WriteAllBytes(path: Path.Combine(path1: Version(version: "1.0.2"), path2: "app.dll"), bytes: content);
        var manifest = new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: null, Notes: null,
            Payloads: [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Path: "app.dll", Hash: ContentPin.Compute(content: content).ToString(), Size: content.Length)])],
            Revoked: null, Rollout: new ReleaseRollout(Percent: 100), Schema: ReleaseManifest.CurrentSchema,
            Signature: null, StateGeneration: 1, Version: "1.0.2");
        var result = new FileUpdateApplier().Apply(manifest: manifest, rid: "win-x64", cacheRoot: m_root.RootPath);

        Assert.True(condition: result.Applied, userMessage: result.RefusalReason);
        Assert.Equal(expected: "1.0.2", actual: File.ReadAllText(path: Current));
        Assert.Equal(expected: hasLastGood, actual: File.Exists(path: lastGood));
        if (hasLastGood) { Assert.Equal(expected: "1.0.0", actual: File.ReadAllText(path: lastGood)); }
    }
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("garbage")]
    [InlineData("2147483648")]
    [Theory]
    public void UnknownGenerationCannotAuthorizeRollback(string? text) {
        Directory.CreateDirectory(path: Version(version: "1.0.0"));
        if (text is not null) { File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.0"), path2: "state-generation"), contents: text); }
        Assert.ThrowsAny<IOException>(testCode: () => StubInstall.ReadGeneration(installRoot: m_root.RootPath, version: "1.0.0"));
    }
    [InlineData(0)]
    [InlineData(3)]
    [Theory]
    public void KnownGenerationIsReadExactly(int generation) {
        Directory.CreateDirectory(path: Version(version: "1.0.0"));
        File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.0"), path2: "state-generation"), contents: generation.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected: generation, actual: StubInstall.ReadGeneration(installRoot: m_root.RootPath, version: "1.0.0"));
    }
    public void Dispose() => m_root.Dispose();
}
