namespace Puck.Assets;

/// <summary>An <see cref="IAssetSource"/> backed by the local file system. Paths are resolved by the runtime
/// exactly as supplied; callers are responsible for any base-path or normalization concerns.</summary>
public sealed class FileSystemAssetSource : IAssetSource {
    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public bool Exists(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return File.Exists(path: path);
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    public ReadOnlyMemory<byte> Read(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return File.ReadAllBytes(path: path);
    }
}
