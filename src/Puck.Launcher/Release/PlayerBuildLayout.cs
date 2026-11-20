namespace Puck.Launcher.Release;

/// <summary>The names inside a player build (<c>puck world player</c>'s folder, and so a player release's payload) that its
/// producer, its publisher and its installer agree on.</summary>
public static class PlayerBuildLayout {
    /// <summary>The build's record: its world, cache directory, backends, targets and how each boot went.</summary>
    public const string RecordName = "player.json";
    /// <summary>The device caches a boot reads and fills (<c>--cache-dir</c>), relative to the build.</summary>
    public const string CacheDirectoryName = "cache";
    /// <summary>The baked-mesh store inside <see cref="CacheDirectoryName"/>.</summary>
    public const string MeshesDirectoryName = "meshes";
    /// <summary>The launcher: the stub and the installer, each one self-contained file, which the installer lays beside
    /// an install's release record.</summary>
    public const string LauncherDirectoryName = "launcher";
    /// <summary>The producing boots' logs, which a release never ships.</summary>
    public const string LogsDirectoryName = "player-logs";
}
