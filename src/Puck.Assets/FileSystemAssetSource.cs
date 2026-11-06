namespace Puck.Assets;

/// <summary>An <see cref="IAssetSource"/> backed by the local file system. Paths are resolved by the runtime
/// exactly as supplied; callers are responsible for any base-path or normalization concerns.</summary>
public sealed class FileSystemAssetSource : IAssetSource {
    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public bool Exists(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return WasmGuestBuild.TryFind(Path.GetFullPath(path), out _, out _) || File.Exists(path: path);
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    public ReadOnlyMemory<byte> Read(string path, int maximumBytes = int.MaxValue) => ReadBytes(path, maximumBytes);

    /// <summary>Reads an owned array through the same source-backed asset path, without copying its contents.</summary>
    /// <param name="path">Requested asset path.</param>
    /// <param name="maximumBytes">Maximum admitted byte length.</param>
    /// <returns>The asset bytes.</returns>
    public byte[] ReadBytes(string path, int maximumBytes = int.MaxValue) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        if (WasmGuestBuild.TryRead(Path.GetFullPath(path), out var derived)) {
            if (derived.Length > maximumBytes) { throw new InvalidDataException($"The asset '{path}' exceeds the {maximumBytes}-byte source limit."); }
            return derived;
        }

        using var stream = File.OpenRead(path: path);
        var length = stream.Length;

        if (length > maximumBytes) {
            throw new InvalidDataException(message: $"The asset '{path}' exceeds the {maximumBytes}-byte source limit.");
        }

        var bytes = new byte[(int)length];

        stream.ReadExactly(buffer: bytes);
        // The open handle, not a prior FileInfo lookup, supplies the length. Still detect a source that grows
        // after that observation instead of returning a valid prefix of different content.
        if (stream.ReadByte() != -1) {
            throw new InvalidDataException(message: $"The asset '{path}' grew while it was being read.");
        }

        return bytes;
    }
}
