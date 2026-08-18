namespace Puck.Storage;

/// <summary>A local-filesystem storage target: blobs live under <c>{RootPath}/{objectId:D}/{key}</c>. For local runs
/// and hermetic proof of the write semantics <see cref="AzureBlobObjectStorageTarget"/> grants — never a deployment
/// target.</summary>
public sealed record DirectoryObjectStorageTarget : ObjectStorageTarget {
    /// <summary>Gets the directory every object's blobs are rooted under.</summary>
    public string RootPath { get; }

    /// <summary>Initializes the target.</summary>
    /// <param name="rootPath">The root directory. Created on first write if it does not exist.</param>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is <see langword="null"/> or whitespace.</exception>
    public DirectoryObjectStorageTarget(string rootPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: rootPath);

        RootPath = rootPath;
    }
}
