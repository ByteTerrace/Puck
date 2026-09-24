using System.Security.Cryptography;

namespace Puck.Storage;

/// <summary>Local blob storage through pinned, no-follow directory capabilities. Conditional writes hold a
/// cross-process directory lock and publish a flushed temporary file by atomic replacement. Local roots must
/// remain host-owned; this is not a sandbox for native code running as that host identity.</summary>
internal sealed class DirectoryObjectBlobStoreBackend : IObjectBlobStoreBackend {
    private static ObjectBlobContent? Read(ConfinedDirectory directory, string name, int maximum) {
        using var stream = directory.OpenFile(name);

        if (stream is null) { return null; }
        if (stream.Length > maximum) { throw new IOException(message: "Storage blob exceeds its byte budget."); }
        var bytes = new byte[checked((int)stream.Length)];

        stream.ReadExactly(buffer: bytes);
        return new(
            Content: bytes,
            VersionToken: Token(content: bytes)
        );
    }
    private static (string Parent, string Name) Resolve(DirectoryObjectStorageTarget target, ObjectBlobAddress address) {
        var segments = ObjectBlobAddressPath.GetKeySegments(address: address);
        var path = Path.Combine(
            path1: target.RootPath,
            path2: address.ObjectId.ToString(),
            path3: Path.Combine(segments)
        );

        return (Path.GetDirectoryName(path: path)!, segments[^1]);
    }
    private static string Token(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(inArray: SHA256.HashData(source: content));
    private static void Walk(ConfinedDirectory directory, string path, string prefix, List<string> keys, ref int remaining,
        CancellationToken cancellationToken) {
        foreach (var name in directory.Entries()) {
            cancellationToken.ThrowIfCancellationRequested();
            if (--remaining < 0) { throw new IOException(message: "Storage listing exceeds its entry budget."); }
            if (name.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: ".puck-"
            )) { continue; }
            var key = (path + name);

            _ = ObjectBlobAddressPath.GetNormalizedKey(address: new(
                Key: key,
                ObjectId: Guid.Empty
            ));
            if (directory.IsDirectory(name: name)) {
                using var child = directory.Child(name: name);

                if (child is not null) {
                    Walk(
                    cancellationToken: cancellationToken,
                    directory: child,
                    keys: keys,
                    path: (key + "/"),
                    prefix: prefix,
                    remaining: ref remaining
                );
                }
            } else {
                using var file = directory.OpenFile(name);

                if (
                    (file is not null) &&
                    key.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: prefix
                )
                ) {
                    keys.Add(item: key);
                }
            }
        }
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var local = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(
            description: "a directory target",
            target: target
        );
        var prefix = ObjectBlobAddressPath.GetNormalizedPrefix(keyPrefix: keyPrefix);
        using var directory = ConfinedDirectory.Open(
            Path.Combine(
                path1: local.RootPath,
                path2: objectId.ToString()
            ),
            create: false
        );
        var keys = new List<string>();
        var remaining = local.MaximumListEntries;

        if (directory is not null) {
            Walk(
            cancellationToken: cancellationToken,
            directory: directory,
            keys: keys,
            path: "",
            prefix: prefix,
            remaining: ref remaining
        );
        }
        return ValueTask.FromResult<IReadOnlyList<string>>(result: keys);
    }
    public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var local = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(
            description: "a directory target",
            target: target
        );

        var (parent, name) = Resolve(
            address: address,
            target: local
        );
        using var directory = ConfinedDirectory.Open(
            parent,
            create: false
        );

        if (directory is null) { return ValueTask.FromResult<ObjectBlobContent?>(result: null); }
        using var gate = directory.AcquireLock(cancellationToken: cancellationToken);

        return ValueTask.FromResult(result: Read(
            directory,
            name,
            local.MaximumBlobBytes
        ));
    }
    public bool Supports(ObjectStorageTarget target) => (target is DirectoryObjectStorageTarget);
    public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address,
        ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var local = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(
            description: "a directory target",
            target: target
        );

        if (content.Length > local.MaximumBlobBytes) { throw new IOException(message: "Storage blob exceeds its byte budget."); }
        if (!Enum.IsDefined(value: mode)) { throw new ArgumentOutOfRangeException(paramName: nameof(mode)); }
        var (parent, name) = Resolve(
            address: address,
            target: local
        );
        using var directory = ConfinedDirectory.Open(
            parent,
            create: true
        )!;
        using var gate = directory.AcquireLock(cancellationToken: cancellationToken);
        var current = Read(
            directory,
            name,
            local.MaximumBlobBytes
        );

        if (
            (mode == ObjectBlobWriteMode.CreateOnly) &&
            (current is not null)
        ) {
            return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                false,
                false,
                current.Value.VersionToken
            ));
        }
        if (
            (ifMatchVersion is not null) &&
            (current?.VersionToken != ifMatchVersion)
        ) {
            return ValueTask.FromResult(result: new ObjectBlobWriteResult(
                false,
                true,
                current?.VersionToken
            ));
        }
        var bytes = content.ToArray();
        var temporary = $".puck-{Guid.NewGuid():N}.tmp";

        try {
            using (var stream = directory.OpenFile(
                temporary,
                create: true,
                exclusive: true,
                newFile: true
            )!) {
                stream.Write(buffer: bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            directory.Publish(
                name: name,
                temporary: temporary
            );
        } finally { directory.RemoveTemporary(name: temporary); }
        return ValueTask.FromResult(result: new ObjectBlobWriteResult(
            true,
            false,
            Token(content: bytes)
        ));
    }
}
