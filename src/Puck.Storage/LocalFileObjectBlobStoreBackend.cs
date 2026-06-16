namespace Puck.Storage;

internal sealed class LocalFileObjectBlobStoreBackend : IObjectBlobStoreBackend {
    private static string GetFilePath(LocalFileObjectStorageTarget target, ObjectBlobAddress address) {
        if (string.IsNullOrWhiteSpace(value: target.BasePath)) {
            throw new ArgumentException(
                message: "The local file storage base path must not be empty.",
                paramName: nameof(target)
            );
        }

        var root = ObjectBlobAddressPath.GetRoot(address: address);

        ValidateSegment(
            paramName: nameof(address),
            segment: root
        );

        var keySegments = ObjectBlobAddressPath.GetKeySegments(address: address);

        foreach (var keySegment in keySegments) {
            ValidateSegment(
                paramName: nameof(address),
                segment: keySegment
            );
        }

        var pathSegments = new string[(keySegments.Length + 2)];

        pathSegments[0] = target.BasePath;
        pathSegments[1] = root;
        Array.Copy(
            destinationArray: pathSegments,
            destinationIndex: 2,
            length: keySegments.Length,
            sourceArray: keySegments,
            sourceIndex: 0
        );
        return Path.Combine(pathSegments);
    }
    private static LocalFileObjectStorageTarget GetTarget(ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(target);

        return ((target as LocalFileObjectStorageTarget)
            ?? throw new ArgumentException(
                message: "The storage target must be a local file target.",
                paramName: nameof(target)
            ));
    }
    private static void ValidateSegment(string segment, string paramName) {
        if (segment.IndexOfAny(anyOf: Path.GetInvalidFileNameChars()) >= 0) {
            throw new ArgumentException(
                message: $"The storage path segment '{segment}' contains characters that are invalid for the local file backend.",
                paramName: paramName
            );
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    ) {
        var filePath = GetFilePath(
            address: address,
            target: GetTarget(target: target)
        );

        if (!File.Exists(path: filePath)) {
            return null;
        }

        return await File.ReadAllBytesAsync(
            cancellationToken: cancellationToken,
            path: filePath
        );
    }
    public bool Supports(ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(target);

        return (target is LocalFileObjectStorageTarget);
    }
    public async ValueTask<bool> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        CancellationToken cancellationToken = default
    ) {
        var filePath = GetFilePath(
            address: address,
            target: GetTarget(target: target)
        );
        var directoryPath = (Path.GetDirectoryName(path: filePath)
            ?? throw new InvalidOperationException(message: "The resolved local object-blob path did not include a parent directory."));

        Directory.CreateDirectory(path: directoryPath);

        try {
            var fileMode = mode switch {
                ObjectBlobWriteMode.Overwrite => FileMode.Create,
                ObjectBlobWriteMode.CreateOnly => FileMode.CreateNew,
                _ => throw new ArgumentOutOfRangeException(
                    actualValue: mode,
                    message: "Unsupported object blob write mode.",
                    paramName: nameof(mode)
                )
            };

            await using var stream = new FileStream(
                filePath,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true
            );

            await stream.WriteAsync(
                buffer: content,
                cancellationToken: cancellationToken
            );
            return true;
        } catch (IOException) when (((mode == ObjectBlobWriteMode.CreateOnly) && File.Exists(path: filePath))) {
            return false;
        }
    }
}
