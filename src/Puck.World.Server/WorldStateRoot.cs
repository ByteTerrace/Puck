namespace Puck.World.Server;

/// <summary>
/// A host's on-disk state root: the directory its owned-world catalog, machine id, replay tapes, instance stores and
/// the other files one run keeps persist under. Compiled worlds and bakes are not state: they live in per-user caches
/// every boot shares. Every consumer takes the root it is handed, so each host, service collection or test fixture
/// carries its own and two of them in one process never share one. The World executable resolves its root from
/// <c>--state-dir</c>, falling back to the <c>world</c> subdirectory of the per-user Puck directory
/// (<see cref="Puck.Abstractions.PuckUserDirectory"/>) only in its own composition root; a hosted silo takes its
/// definition's state directory.
/// </summary>
public sealed class WorldStateRoot {
    /// <summary>Initializes a new instance of the <see cref="WorldStateRoot"/> class at <paramref name="path"/>. Nothing
    /// is created until a consumer writes.</summary>
    /// <param name="path">The state root directory, made absolute here.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public WorldStateRoot(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        FullPath = Path.GetFullPath(path: path);
    }

    /// <summary>Gets the absolute path of the root directory.</summary>
    public string FullPath { get; }

    /// <summary>Returns the absolute path of <paramref name="name"/> under the root.</summary>
    /// <param name="name">A relative path under the root.</param>
    /// <returns>The absolute path; nothing is created.</returns>
    public string PathOf(string name) => Path.Combine(
        path1: FullPath,
        path2: name
    );
    /// <summary>Reads the host's persisted machine id from <paramref name="fileName"/> under the root, minting and
    /// persisting a fresh one when the file is absent or unreadable as a non-empty id.</summary>
    /// <param name="fileName">The id file's name under the root.</param>
    /// <param name="failure">Why the id could not be persisted, in which case the returned id lasts this session only;
    /// otherwise <see langword="null"/>.</param>
    /// <returns>The machine id.</returns>
    public Guid MachineId(string fileName, out string? failure) {
        var path = PathOf(name: fileName);

        failure = null;

        try {
            _ = Directory.CreateDirectory(path: FullPath);

            if (
                File.Exists(path: path) &&
                Guid.TryParse(
                    input: File.ReadAllText(path: path).Trim(),
                    result: out var stored
                ) &&
                (stored != Guid.Empty)
            ) {
                return stored;
            }

            var created = Guid.NewGuid();

            File.WriteAllText(
                contents: created.ToString(format: "D"),
                path: path
            );

            return created;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            failure = exception.Message;

            return Guid.NewGuid();
        }
    }
    /// <inheritdoc/>
    public override string ToString() => FullPath;
}
