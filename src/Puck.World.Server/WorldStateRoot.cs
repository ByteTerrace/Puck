namespace Puck.World.Server;

/// <summary>
/// The single source of the world's on-disk state root — the directory the profile store and the replay tape
/// persist under. Defaults to <c>%LOCALAPPDATA%\Puck\World</c> via the shell's known-folder resolution (which
/// deliberately IGNORES a <c>LOCALAPPDATA</c> environment override — that is why this seam exists). The
/// <c>--state-dir</c> CLI option overrides it at boot: a developer/deployment reflection in the established
/// nullable-override pattern, needed by anything that runs more than one world process per user — parallel
/// verification runs today, multiple headless hosts on one machine at the destination.
/// </summary>
public static class WorldStateRoot {
    private static string? OverridePath;

    /// <summary>Applies the boot-time override. Call at most once, before any store or tape is constructed —
    /// state must not split across two roots inside one process lifetime.</summary>
    /// <param name="path">The state root directory (created on first use).</param>
    /// <exception cref="InvalidOperationException">An override was already applied.</exception>
    public static void Override(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        if (OverridePath is not null) {
            throw new InvalidOperationException(message: "the world state root was already overridden this boot");
        }

        OverridePath = Path.GetFullPath(path: path);
    }
    /// <summary>Resolves the effective state root: the boot override when present, else the per-user default.</summary>
    public static string Resolve() =>
        (OverridePath ?? Path.Combine(
            path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData),
            path2: "Puck",
            path3: "World"
        ));
}
