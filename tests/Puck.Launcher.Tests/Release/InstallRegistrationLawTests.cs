using Puck.Confinement;
using Puck.Launcher.Release;
using Puck.Testing;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over which processes register: membership is decided by where the process runs from, never by what the
/// install root has published yet or by an entry assembly's file alone.</summary>
public sealed class InstallRegistrationLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(prefix: "puck-register-");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private string Root => m_directory.PathOf(name: "root");

    private string Version(string version) => Path.Combine(path1: Root, path2: InstallLayout.VersionsDirectoryName, path3: version);
    // A version staged the way the stager stages it, in a root that has not published `current` yet: what a first install
    // interrupted before its pointer was written leaves.
    private string Stage(string version) {
        Directory.CreateDirectory(path: Version(version: version));
        return m_directory.WriteText(name: Path.Combine(path1: "root", path2: InstallLayout.VersionsDirectoryName, path3: version, path4: "app.dll"), text: $"binary-{version}");
    }

    // A process launched from versions/<v>/ registers whether or not the root has written `current` yet.
    [Fact]
    public void AVersionStagedBeforeCurrentExistsRegisters() {
        using var image = new MappedImage(path: Stage(version: "1.0.0"));

        Assert.False(condition: File.Exists(path: Path.Combine(path1: Root, path2: InstallLayout.CurrentFileName)));
        using var registration = InstallRegistration.RegisterImage(baseDirectory: Version(version: "1.0.0"), cancellationToken: Cancel,
            entryAssemblyLocation: image.Path, imageAddress: image.Address, processPath: null);

        Assert.Equal(expected: "1.0.0", actual: registration.Version);
        Assert.Throws<IOException>(testCode: () => InstallPruner.RemoveVersion(installRoot: Root, version: "1.0.0"));
    }
    // A single-file app has no entry assembly file; its executable is the image it was loaded from, and it registers by it.
    [Fact]
    public void ASingleFileImageRegistersByItsExecutable() {
        using var image = new MappedImage(path: Stage(version: "1.0.0"));
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
    public void Dispose() => m_directory.Dispose();
}
