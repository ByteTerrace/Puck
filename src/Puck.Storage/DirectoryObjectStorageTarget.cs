namespace Puck.Storage;

/// <summary>A host-owned local storage target: blobs live under <c>{RootPath}/{objectId:D}/{key}</c>.
/// The backend rejects linked entries and publishes conditional writes atomically on Windows and Linux x64.</summary>
/// <remarks>The host must protect the root and its ancestors from other identities. Unsupported platforms and
/// Windows network roots fail closed. Filesystem crash durability remains a deployment responsibility.</remarks>
public sealed record DirectoryObjectStorageTarget : ObjectStorageTarget {
    /// <summary>Gets the directory every object's blobs are rooted under.</summary>
    public string RootPath { get; }

    /// <summary>Gets the maximum bytes read or written for one blob.</summary>
    public int MaximumBlobBytes { get; }
    /// <summary>Gets the maximum visited entries in one listing, including directories and storage metadata.</summary>
    public int MaximumListEntries { get; }

    /// <summary>Initializes the target.</summary>
    /// <param name="rootPath">The root directory, resolved to an absolute path now and created on first write.</param>
    /// <param name="maximumBlobBytes">The positive per-blob byte ceiling.</param>
    /// <param name="maximumListEntries">The positive per-list traversal ceiling.</param>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive.</exception>
    public DirectoryObjectStorageTarget(string rootPath, int maximumBlobBytes = 67108864, int maximumListEntries = 100000) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: rootPath);

        RootPath = Path.GetFullPath(rootPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBlobBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumListEntries);
        MaximumBlobBytes = maximumBlobBytes;
        MaximumListEntries = maximumListEntries;
    }
}
