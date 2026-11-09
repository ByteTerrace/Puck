using Puck.Confinement;

namespace Puck.Launcher.Stub;

/// <summary>
/// The stub's own pieces of the install root's layout: its configuration file's name, where a version's executable lives,
/// and holding the launched version. Every selection decision belongs to <see cref="InstallTransaction.SelectLaunch"/>;
/// the stub parses no release manifest and references no launcher implementation.
/// </summary>
public static class StubInstall {
    /// <summary>The stub's own configuration file name, beside the stub executable (the install root itself).</summary>
    public const string StubConfigFileName = "stub.json";

    /// <summary>Gets how long the stub waits for the install root's update lease before refusing the launch.</summary>
    public static TimeSpan LeaseWait { get; } = TimeSpan.FromSeconds(seconds: 10);

    /// <summary>Under the caller's transaction, registers the stub as a holder of <c>versions/&lt;version&gt;</c>, so
    /// the launcher's pruner never removes the version the child runs from. Hold it from before the child starts until the
    /// child exits; the stub's exit releases it, a crash included, and the child registers itself as well.</summary>
    /// <param name="root">The held install root.</param>
    /// <param name="version">The version directory the child runs from.</param>
    /// <returns>The held version directory.</returns>
    /// <exception cref="ArgumentException"><paramref name="version"/> is not a single directory name.</exception>
    /// <exception cref="IOException">The version directory is missing, is a link, or is being removed.</exception>
    public static ConfinedDirectory HoldVersion(ConfinedDirectory root, string version) {
        ArgumentNullException.ThrowIfNull(argument: root);
        using var versions = (root.Child(name: InstallLayout.VersionsDirectoryName) ?? throw new DirectoryNotFoundException(message: $"'{InstallLayout.VersionsDirectoryName}' does not exist."));
        var held = (versions.Child(name: version) ?? throw new DirectoryNotFoundException(message: $"'{InstallLayout.VersionsDirectoryName}/{version}' does not exist."));

        if (!held.TryHoldShared()) {
            held.Dispose();
            throw new IOException(message: $"'{InstallLayout.VersionsDirectoryName}/{version}' is being removed.");
        }
        return held;
    }
    /// <summary>Resolves a version's directory.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The version directory name.</param>
    /// <returns>The directory's path.</returns>
    public static string VersionDirectory(string installRoot, string version) =>
        Path.Combine(path1: installRoot, path2: InstallLayout.VersionsDirectoryName, path3: version);
}
