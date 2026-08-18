using System.Security.Cryptography;

namespace Puck.Storage;

/// <summary>
/// The local-filesystem backend. Its version token is a content hash of the blob's own bytes (SHA-256, lowercase
/// hex) rather than a write-order stamp, so two writers depositing identical bytes agree on the token without
/// coordinating. <see cref="ObjectBlobWriteMode.CreateOnly"/> uses <see cref="FileMode.CreateNew"/>, which is atomic
/// at the filesystem level (the OS refuses a second creator). <see cref="ObjectBlobWriteMode.Overwrite"/> with an
/// if-match token holds one exclusive file handle across the whole read-compare-write, so the compare-and-swap is
/// atomic on a single machine — stronger than the "best-effort" a local backend is allowed to be
/// (<see cref="IObjectBlobStoreBackend"/>'s own remarks), and exactly the guarantee the write-semantics law suite
/// proves against a real Azure account.
/// </summary>
internal sealed class DirectoryObjectBlobStoreBackend : IObjectBlobStoreBackend {
    private static string ComputeVersionToken(ReadOnlySpan<byte> content) {
        Span<byte> hash = stackalloc byte[32];

        SHA256.HashData(
            destination: hash,
            source: content
        );

        return Convert.ToHexStringLower(bytes: hash);
    }
    private static string ResolvePath(DirectoryObjectStorageTarget target, ObjectBlobAddress address) {
        var key = ObjectBlobAddressPath.GetNormalizedKey(address: address).Replace(
            newChar: Path.DirectorySeparatorChar,
            oldChar: '/'
        );

        return Path.GetFullPath(path: Path.Combine(
            path1: target.RootPath,
            path2: address.ObjectId.ToString(),
            path3: key
        ));
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(
        ObjectStorageTarget target,
        Guid objectId,
        string keyPrefix,
        CancellationToken cancellationToken = default
    ) {
        var directoryTarget = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(
            description: "a directory target",
            target: target
        );
        var root = Path.GetFullPath(path: Path.Combine(
            path1: directoryTarget.RootPath,
            path2: objectId.ToString()
        ));

        if (!Directory.Exists(path: root)) {
            return ValueTask.FromResult<IReadOnlyList<string>>(result: []);
        }

        var normalizedPrefix = ObjectBlobAddressPath.GetNormalizedPrefix(keyPrefix: keyPrefix);
        var keys = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            path: root,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            var relative = Path.GetRelativePath(
                path: file,
                relativeTo: root
            ).Replace(
                newChar: '/',
                oldChar: Path.DirectorySeparatorChar
            );

            if (relative.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: normalizedPrefix
            )) {
                keys.Add(item: relative);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(result: keys);
    }
    public ValueTask<ObjectBlobContent?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    ) {
        var directoryTarget = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(
            description: "a directory target",
            target: target
        );
        var path = ResolvePath(
            address: address,
            target: directoryTarget
        );

        if (!File.Exists(path: path)) {
            return ValueTask.FromResult<ObjectBlobContent?>(result: null);
        }

        var bytes = File.ReadAllBytes(path: path);

        return ValueTask.FromResult<ObjectBlobContent?>(result: new ObjectBlobContent(
            Content: bytes,
            VersionToken: ComputeVersionToken(content: bytes)
        ));
    }
    public bool Supports(ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(argument: target);

        return (target is DirectoryObjectStorageTarget);
    }
    public ValueTask<ObjectBlobWriteResult> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        string? ifMatchVersion = null,
        CancellationToken cancellationToken = default
    ) {
        var directoryTarget = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(
            description: "a directory target",
            target: target
        );
        var path = ResolvePath(
            address: address,
            target: directoryTarget
        );

        if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) {
            Directory.CreateDirectory(path: directory);
        }

        var bytes = content.ToArray();

        if (mode == ObjectBlobWriteMode.CreateOnly) {
            try {
                using var stream = new FileStream(
                    access: FileAccess.Write,
                    mode: FileMode.CreateNew,
                    path: path,
                    share: FileShare.None
                );

                stream.Write(buffer: bytes);
            } catch (IOException) {
                var current = (File.Exists(path: path)
                    ? ComputeVersionToken(content: File.ReadAllBytes(path: path))
                    : null);

                return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                    PreconditionFailed: false,
                    Succeeded: false,
                    VersionToken: current
                ));
            }

            return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                PreconditionFailed: false,
                Succeeded: true,
                VersionToken: ComputeVersionToken(content: bytes)
            ));
        }

        if (mode != ObjectBlobWriteMode.Overwrite) {
            throw new ArgumentOutOfRangeException(
                actualValue: mode,
                message: "Unsupported object blob write mode.",
                paramName: nameof(mode)
            );
        }

        // Held across the whole read-compare-write below so the compare-and-swap is atomic on this machine.
        var preExisted = File.Exists(path: path);

        using var handle = new FileStream(
            access: FileAccess.ReadWrite,
            mode: FileMode.OpenOrCreate,
            path: path,
            share: FileShare.None
        );

        byte[] currentBytes = [];

        if (preExisted) {
            currentBytes = new byte[handle.Length];

            var offset = 0;

            while (offset < currentBytes.Length) {
                var read = handle.Read(
                    buffer: currentBytes,
                    count: (currentBytes.Length - offset),
                    offset: offset
                );

                if (read == 0) {
                    break;
                }

                offset += read;
            }
        }

        if (ifMatchVersion is not null) {
            if (!preExisted) {
                return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                    PreconditionFailed: true,
                    Succeeded: false,
                    VersionToken: null
                ));
            }

            var currentToken = ComputeVersionToken(content: currentBytes);

            if (!string.Equals(
                a: currentToken,
                b: ifMatchVersion,
                comparisonType: StringComparison.Ordinal
            )) {
                return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                    PreconditionFailed: true,
                    Succeeded: false,
                    VersionToken: currentToken
                ));
            }
        }

        handle.SetLength(value: 0);
        handle.Position = 0;
        handle.Write(buffer: bytes);
        handle.Flush();

        return ValueTask.FromResult(result: new ObjectBlobWriteResult(
            PreconditionFailed: false,
            Succeeded: true,
            VersionToken: ComputeVersionToken(content: bytes)
        ));
    }
}
