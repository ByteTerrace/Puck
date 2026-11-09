using Puck.Confinement;
using Puck.Launcher.Release;
using Puck.Testing;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over which processes register: membership is decided by where the process runs from, never by an entry
/// assembly's file alone, and a process from an install runs only when the release record admits its exact install.</summary>
public sealed class InstallRegistrationLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(prefix: "puck-register-");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private string Root => m_directory.PathOf(name: "root");

    private string Version(string version) => Path.Combine(path1: Root, path2: InstallLayout.VersionsDirectoryName, path3: version);
    // A version staged the way the stager stages it, before any apply recorded it.
    private string Stage(string version) {
        Directory.CreateDirectory(path: Version(version: version));
        return m_directory.WriteText(name: Path.Combine(path1: "root", path2: InstallLayout.VersionsDirectoryName, path3: version, path4: "app.dll"), text: $"binary-{version}");
    }
    private InstallRegistration Register(MappedImage image, string version) => InstallRegistration.RegisterImage(baseDirectory: Version(version: version),
        cancellationToken: Cancel, entryAssemblyLocation: image.Path, imageAddress: image.Address, processPath: null);

    // A staged payload no apply has recorded has no known identity or generation, so nothing may run from it.
    [Fact]
    public void AStagedVersionWithoutAnInstallRecordRefusesToStart() {
        using var image = new MappedImage(path: Stage(version: "1.0.0"));
        var refusal = Assert.Throws<InstallRegistrationException>(testCode: () => Register(image: image, version: "1.0.0"));

        Assert.Contains(expectedSubstring: "no valid install record", actualString: refusal.Message);
    }
    // An installed version registers, and the registration holds its directory against removal.
    [Fact]
    public void AnInstalledVersionRegistersAndIsHeld() {
        using var image = new MappedImage(path: Stage(version: "1.0.0"));

        _ = InstallSeed.Install(installRoot: Root, proven: false, version: "1.0.0");
        using var registration = Register(image: image, version: "1.0.0");

        Assert.Equal(expected: "1.0.0", actual: registration.Version);
        Assert.Throws<IOException>(testCode: () => InstallPruner.RemoveVersion(installRoot: Root, version: "1.0.0"));
    }
    // A single-file app has no entry assembly file; its executable is the image it was loaded from, and it registers by it.
    [Fact]
    public void ASingleFileImageRegistersByItsExecutable() {
        using var image = new MappedImage(path: Stage(version: "1.0.0"));

        _ = InstallSeed.Install(installRoot: Root, proven: false, version: "1.0.0");
        using var registration = InstallRegistration.RegisterImage(baseDirectory: Version(version: "1.0.0"), cancellationToken: Cancel,
            entryAssemblyLocation: "", imageAddress: image.Address, processPath: image.Path);

        Assert.Equal(expected: "1.0.0", actual: registration.Version);
    }
    // A process under a versions/ directory that cannot confirm its image refuses to start rather than run unregistered.
    [Fact]
    public void AProcessUnderVersionsThatCannotConfirmItsImageRefusesToStart() {
        var elsewhere = m_directory.WriteText(name: "elsewhere/app.dll", text: "elsewhere");

        _ = Stage(version: "1.0.0");
        Assert.Throws<InstallRegistrationException>(testCode: () => InstallRegistration.RegisterImage(baseDirectory: Version(version: "1.0.0"),
            cancellationToken: Cancel, entryAssemblyLocation: "", imageAddress: 0, processPath: null));
        Assert.Throws<InstallRegistrationException>(testCode: () => InstallRegistration.RegisterImage(baseDirectory: Version(version: "1.0.0"),
            cancellationToken: Cancel, entryAssemblyLocation: elsewhere, imageAddress: 0, processPath: null));
        // Outside any versions/ directory a process registers as nothing.
        using var outside = InstallRegistration.RegisterImage(baseDirectory: Path.GetDirectoryName(path: elsewhere)!, cancellationToken: Cancel,
            entryAssemblyLocation: elsewhere, imageAddress: 0, processPath: null);

        Assert.Null(@object: outside.Version);
    }
    // Registration is the launch policy's door for every installed entry point, whoever started it (U11, and U03's
    // re-check of a stub launch): a revoked or rejected version, or one below the floor, refuses to start, and a start
    // raises the floor to its own generation before any application code runs.
    [Fact]
    public void RegistrationRefusesRevokedRejectedAndIncompatibleVersionsAndRaisesTheFloor() {
        using var revoked = new MappedImage(path: Stage(version: "1.0.0"));
        using var rejected = new MappedImage(path: Stage(version: "1.1.0"));
        using var older = new MappedImage(path: Stage(version: "1.2.0"));
        using var newer = new MappedImage(path: Stage(version: "1.3.0"));

        _ = InstallSeed.Install(installRoot: Root, version: "1.0.0");
        _ = InstallSeed.Install(installRoot: Root, proven: false, version: "1.1.0");
        Assert.Equal(expected: "1.1.0", actual: InstallSeed.Launch(installRoot: Root, maxAttempts: 1).Version);
        Assert.Equal(expected: "1.0.0", actual: InstallSeed.Launch(installRoot: Root, maxAttempts: 1).Version);
        _ = InstallSeed.Install(installRoot: Root, proven: false, version: "1.2.0");
        _ = InstallSeed.Install(installRoot: Root, proven: false, stateGeneration: 3, version: "1.3.0");
        using (var transaction = InstallTransaction.TryBegin(installRoot: Root)!) {
            Assert.Null(@object: transaction.MergeRevocations(precedence: ReleaseVersion.TryCompare, revoked: ["1.0.0"]));
        }

        Assert.Contains(expectedSubstring: "revoked", actualString: Assert.Throws<InstallRegistrationException>(testCode: () => Register(image: revoked, version: "1.0.0")).Message);
        Assert.Contains(expectedSubstring: "rejected", actualString: Assert.Throws<InstallRegistrationException>(testCode: () => Register(image: rejected, version: "1.1.0")).Message);
        Assert.Equal(expected: 1, actual: InstallSeed.Policy(installRoot: Root).StateGenerationFloor);
        using (var started = Register(image: newer, version: "1.3.0")) {
            Assert.Equal(expected: 3, actual: InstallSeed.Policy(installRoot: Root).StateGenerationFloor);
        }
        Assert.Contains(expectedSubstring: "below this install's floor", actualString: Assert.Throws<InstallRegistrationException>(testCode: () => Register(image: older, version: "1.2.0")).Message);
    }
    public void Dispose() => m_directory.Dispose();
}
