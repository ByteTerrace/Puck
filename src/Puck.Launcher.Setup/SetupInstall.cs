using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Confinement;

namespace Puck.Launcher.Setup;

/// <summary>Lays the launcher a release carries beside the install's release record, and names the command that starts
/// the installed version.</summary>
public static class SetupInstall {
    private const string LauncherDirectoryName = Puck.Launcher.Release.PlayerBuildLayout.LauncherDirectoryName;
    private const string PlayerRecordName = Puck.Launcher.Release.PlayerBuildLayout.RecordName;

    private static string Executable(string name) => (OperatingSystem.IsWindows() ? $"{name}.exe" : name);

    /// <summary>Copies every file of a version's launcher directory
    /// (<see cref="Puck.Launcher.Release.PlayerBuildLayout.LauncherDirectoryName"/>) into the install root, replacing a file
    /// only when its bytes changed (a running stub keeps its image), and writes the stub's configuration to start the
    /// version's World.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The installed version whose launcher is laid.</param>
    /// <exception cref="IOException">The version carries no launcher, or a file cannot be written.</exception>
    public static void LayLauncher(string installRoot, string version) {
        var launcher = Path.Combine(path1: Path.Combine(path1: installRoot, path2: InstallLayout.VersionsDirectoryName, path3: version), path2: LauncherDirectoryName);

        if (!File.Exists(path: Path.Combine(path1: launcher, path2: Executable(name: "Puck.Launcher.Stub")))) {
            throw new IOException(message: $"version {version} carries no {LauncherDirectoryName}/{Executable(name: "Puck.Launcher.Stub")}");
        }
        foreach (var file in Directory.EnumerateFiles(path: launcher)) {
            var target = Path.Combine(path1: installRoot, path2: Path.GetFileName(path: file));

            AtomicFile.DeleteReplacedBackups(path: target);
            _ = AtomicFile.WriteAllBytesIfChanged(bytes: File.ReadAllBytes(path: file), path: target);
        }
        _ = AtomicFile.WriteAllBytesIfChanged(path: Path.Combine(path1: installRoot, path2: InstallLayout.StubConfigFileName),
            bytes: new StubConfiguration(AppExecutableFileName: Executable(name: "Puck.World")).Serialize());
    }
    /// <summary>Returns the command line that starts a version through the stub: its world and its prewarmed caches, as
    /// the player build's record names them, relative to the version's directory, where the stub starts it.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The installed version.</param>
    /// <returns>The command line.</returns>
    public static string StartCommand(string installRoot, string version) {
        var world = "worlds/debug";
        var cache = Puck.Launcher.Release.PlayerBuildLayout.CacheDirectoryName;

        try {
            var record = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: Path.Combine(path1: Path.Combine(path1: installRoot, path2: InstallLayout.VersionsDirectoryName, path3: version), path2: PlayerRecordName)));

            world = ((string?)record?["world"] ?? world);
            cache = ((string?)record?["cacheDirectory"] ?? cache);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return $"\"{Path.Combine(path1: installRoot, path2: Executable(name: "Puck.Launcher.Stub"))}\" --world {world} --cache-dir {cache}";
    }
}
