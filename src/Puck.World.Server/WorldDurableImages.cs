namespace Puck.World.Server;

/// <summary>Where a host keeps the images of the rows it runs, so a row stopped or abandoned by a quit is recovered from
/// its image by the next activation of the same name. A host decides nothing about location: the store names it from the
/// instance name alone, and the instance name of a persisted destination is a pure function of its destination and
/// scope key.</summary>
public interface IWorldDurableImages {
    /// <summary>Gets whether the row named <paramref name="instanceName"/> keeps an image: a row no persisted
    /// destination minted keeps none, and a stop of one retires it only once nothing it holds is left to carry.</summary>
    /// <param name="instanceName">The instance name.</param>
    /// <returns><see langword="true"/> when the row's state outlives its activation.</returns>
    bool Keeps(string instanceName);
    /// <summary>Writes a row's image, replacing the one before it only once the new one is whole.</summary>
    /// <param name="instanceName">The instance name.</param>
    /// <param name="checkpoint">The row's image, its host slice included.</param>
    /// <param name="reason">Why the image is not durable, on failure.</param>
    /// <returns><see langword="true"/> once the image is durable.</returns>
    bool TryWrite(string instanceName, WorldAuthorityCheckpoint checkpoint, out string reason);
    /// <summary>Reads the image a row's last activation left.</summary>
    /// <param name="instanceName">The instance name.</param>
    /// <param name="documentDirectory">The directory the image's document names its relative paths against, or
    /// <see langword="null"/> for a document that names none.</param>
    /// <param name="checkpoint">The image, on success.</param>
    /// <param name="reason">Why a held image could not be read, empty when the row has none.</param>
    /// <returns><see langword="true"/> when an image was read; <see langword="false"/> with an empty
    /// <paramref name="reason"/> when the row has none.</returns>
    bool TryRead(string instanceName, string? documentDirectory, out WorldAuthorityCheckpoint? checkpoint, out string reason);
    /// <summary>Moves a held image out of the way of the next write, keeping its bytes: the image a restore refused is
    /// evidence, not something the next activation's first write may silently replace.</summary>
    /// <param name="instanceName">The instance name.</param>
    void SetAside(string instanceName);
}
/// <summary>The desktop's image store: one file per persisted row under the row's own directory in the state root's
/// instance store, written to a sibling file and moved into place so a crash mid-write leaves the previous image.</summary>
public sealed class WorldDirectoryImages : IWorldDurableImages {
    private const string ImageFileName = "durable.image";
    private const string SetAsideFileName = "durable.image.refused";

    private readonly WorldSessionResolver m_resolver;
    private readonly WorldStateRoot m_root;

    /// <summary>Initializes a new instance of the <see cref="WorldDirectoryImages"/> class.</summary>
    /// <param name="root">The state root whose <c>instances</c> directory holds each row's image.</param>
    /// <param name="resolver">The resolver whose minted names say which rows a persisted destination owns.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="resolver"/> is
    /// <see langword="null"/>.</exception>
    public WorldDirectoryImages(WorldStateRoot root, WorldSessionResolver resolver) {
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: resolver);

        m_resolver = resolver;
        m_root = root;
    }

    /// <summary>Gets the path of the image file for <paramref name="instanceName"/>; nothing is created.</summary>
    /// <param name="instanceName">The instance name, a single safe path segment.</param>
    /// <returns>The absolute path.</returns>
    public string PathOf(string instanceName) => Path.Combine(
        path1: m_root.PathOf(name: "instances"),
        path2: instanceName,
        path3: ImageFileName
    );
    /// <inheritdoc/>
    public bool Keeps(string instanceName) => (
        m_resolver.TryDescribeInstance(
            durability: out var durability,
            instanceName: instanceName,
            scopeKey: out _
        ) &&
        (durability == WorldDestinationDurability.Persisted)
    );
    /// <inheritdoc/>
    public bool TryWrite(string instanceName, WorldAuthorityCheckpoint checkpoint, out string reason) {
        var path = PathOf(instanceName: instanceName);
        var staged = (path + ".writing");

        try {
            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);

            using (var stream = new FileStream(
                path: staged,
                mode: FileMode.Create,
                access: FileAccess.Write,
                share: FileShare.None
            )) {
                stream.Write(buffer: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint));
                stream.Flush(flushToDisk: true);
            }

            File.Move(
                destFileName: path,
                overwrite: true,
                sourceFileName: staged
            );
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = exception.Message;

            return false;
        }
    }
    /// <inheritdoc/>
    public bool TryRead(string instanceName, string? documentDirectory, out WorldAuthorityCheckpoint? checkpoint, out string reason) {
        var path = PathOf(instanceName: instanceName);

        checkpoint = null;
        reason = string.Empty;

        try {
            if (!File.Exists(path: path)) {
                return false;
            }

            return WorldAuthorityCheckpointCodec.TryDecode(
                bytes: File.ReadAllBytes(path: path),
                checkpoint: out checkpoint,
                documentDirectory: documentDirectory,
                reason: out reason
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = exception.Message;

            return false;
        }
    }
    /// <inheritdoc/>
    public void SetAside(string instanceName) {
        var path = PathOf(instanceName: instanceName);

        try {
            if (File.Exists(path: path)) {
                File.Move(
                    destFileName: Path.Combine(
                        path1: Path.GetDirectoryName(path: path)!,
                        path2: SetAsideFileName
                    ),
                    overwrite: true,
                    sourceFileName: path
                );
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // The image stays where it was: the next write replaces it only through a whole new one.
        }
    }
}
