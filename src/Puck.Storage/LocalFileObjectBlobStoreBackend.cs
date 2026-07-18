using System.Security.Cryptography;

namespace Puck.Storage;

/// <summary>
/// The local-file backend. Its version token is a SHA-256 content hash (lowercase hex): a read hashes the bytes on the
/// way out, a write recomputes the hash of what it stored. The if-match guard is BEST-EFFORT within one process — a
/// conditional write re-reads the current bytes and refuses on a hash mismatch, but the file backend has an inherent
/// read-hash → write TOCTOU gap (another writer can slip between the two), so true optimistic concurrency is an
/// Azure-backend property. Documented, not "fixed": the local token is an LWW input and an in-process clobber guard.
/// </summary>
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

    // The content hash serving as the version token — SHA-256, lowercase hex. Deterministic and collision-resistant, so
    // a conditional write compares "same bytes" byte-for-byte without re-reading the whole blob twice on the write path.
    private static string HashOf(ReadOnlySpan<byte> content) {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];

        _ = SHA256.HashData(source: content, destination: digest);

        return Convert.ToHexStringLower(bytes: digest);
    }

    public async ValueTask<ObjectBlobContent?> ReadAsync(
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

        var bytes = await File.ReadAllBytesAsync(
            cancellationToken: cancellationToken,
            path: filePath
        );

        return new ObjectBlobContent(Content: bytes, VersionToken: HashOf(content: bytes));
    }
    public bool Supports(ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(target);

        return (target is LocalFileObjectStorageTarget);
    }
    public async ValueTask<ObjectBlobWriteResult> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        string? ifMatchVersion = null,
        CancellationToken cancellationToken = default
    ) {
        var filePath = GetFilePath(
            address: address,
            target: GetTarget(target: target)
        );

        // The best-effort clobber guard: re-hash the current bytes and refuse when they no longer carry the token the
        // caller last saw (a null token means "the blob was absent when I read"; a now-present blob fails that too). The
        // read→write TOCTOU gap is inherent to a file backend and documented at the type; this closes the in-process case.
        if (ifMatchVersion is not null) {
            var current = (File.Exists(path: filePath)
                ? HashOf(content: await File.ReadAllBytesAsync(cancellationToken: cancellationToken, path: filePath))
                : null);

            if (!string.Equals(a: current, b: ifMatchVersion, comparisonType: StringComparison.Ordinal)) {
                return new ObjectBlobWriteResult(Succeeded: false, PreconditionFailed: true, VersionToken: current);
            }
        }

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

            return new ObjectBlobWriteResult(Succeeded: true, PreconditionFailed: false, VersionToken: HashOf(content: content.Span));
        } catch (IOException) when (((mode == ObjectBlobWriteMode.CreateOnly) && File.Exists(path: filePath))) {
            // A create-only loss (the blob already exists) is a distinct outcome from a precondition failure.
            return new ObjectBlobWriteResult(Succeeded: false, PreconditionFailed: false, VersionToken: null);
        }
    }
}
