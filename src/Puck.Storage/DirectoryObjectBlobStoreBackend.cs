using System.Security.Cryptography;

namespace Puck.Storage;

/// <summary>Local blob storage through pinned, no-follow directory capabilities. Conditional writes hold a
/// cross-process directory lock and publish a flushed temporary file by atomic replacement. Local roots must
/// remain host-owned; this is not a sandbox for native code running as that host identity.</summary>
internal sealed class DirectoryObjectBlobStoreBackend : IObjectBlobStoreBackend {
    public bool Supports(ObjectStorageTarget target) => target is DirectoryObjectStorageTarget;

    public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var local = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(target, "a directory target");
        var (parent, name) = Resolve(local, address);
        using var directory = ConfinedDirectory.Open(parent, create: false);
        if (directory is null) { return ValueTask.FromResult<ObjectBlobContent?>(null); }
        using var gate = directory.AcquireLock(cancellationToken);
        return ValueTask.FromResult(Read(directory, name, local.MaximumBlobBytes));
    }

    public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address,
        ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var local = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(target, "a directory target");
        if (content.Length > local.MaximumBlobBytes) { throw new IOException("Storage blob exceeds its byte budget."); }
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        var (parent, name) = Resolve(local, address);
        using var directory = ConfinedDirectory.Open(parent, create: true)!;
        using var gate = directory.AcquireLock(cancellationToken);
        var current = Read(directory, name, local.MaximumBlobBytes);
        if (mode == ObjectBlobWriteMode.CreateOnly && current is not null) {
            return ValueTask.FromResult(new ObjectBlobWriteResult(false, false, current.Value.VersionToken));
        }
        if (ifMatchVersion is not null && current?.VersionToken != ifMatchVersion) {
            return ValueTask.FromResult(new ObjectBlobWriteResult(false, true, current?.VersionToken));
        }
        var bytes = content.ToArray();
        var temporary = $".puck-{Guid.NewGuid():N}.tmp";
        try {
            using (var stream = directory.OpenFile(temporary, create: true, exclusive: true, newFile: true)!) {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            directory.Publish(temporary, name);
        } finally { directory.RemoveTemporary(temporary); }
        return ValueTask.FromResult(new ObjectBlobWriteResult(true, false, Token(bytes)));
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var local = ObjectStorageTarget.Require<DirectoryObjectStorageTarget>(target, "a directory target");
        var prefix = ObjectBlobAddressPath.GetNormalizedPrefix(keyPrefix);
        using var directory = ConfinedDirectory.Open(Path.Combine(local.RootPath, objectId.ToString()), create: false);
        var keys = new List<string>();
        var remaining = local.MaximumListEntries;
        if (directory is not null) { Walk(directory, "", prefix, keys, ref remaining, cancellationToken); }
        return ValueTask.FromResult<IReadOnlyList<string>>(keys);
    }

    private static void Walk(ConfinedDirectory directory, string path, string prefix, List<string> keys, ref int remaining,
        CancellationToken cancellationToken) {
        foreach (var name in directory.Entries()) {
            cancellationToken.ThrowIfCancellationRequested();
            if (--remaining < 0) { throw new IOException("Storage listing exceeds its entry budget."); }
            if (name.StartsWith(".puck-", StringComparison.OrdinalIgnoreCase)) { continue; }
            var key = path + name;
            _ = ObjectBlobAddressPath.GetNormalizedKey(new(Guid.Empty, key));
            if (directory.IsDirectory(name)) {
                using var child = directory.Child(name);
                if (child is not null) { Walk(child, key + "/", prefix, keys, ref remaining, cancellationToken); }
            } else {
                using var file = directory.OpenFile(name);
                if (file is not null && key.StartsWith(prefix, StringComparison.Ordinal)) {
                    keys.Add(key);
                }
            }
        }
    }

    private static (string Parent, string Name) Resolve(DirectoryObjectStorageTarget target, ObjectBlobAddress address) {
        var segments = ObjectBlobAddressPath.GetKeySegments(address);
        var path = Path.Combine(target.RootPath, address.ObjectId.ToString(), Path.Combine(segments));
        return (Path.GetDirectoryName(path)!, segments[^1]);
    }

    private static ObjectBlobContent? Read(ConfinedDirectory directory, string name, int maximum) {
        using var stream = directory.OpenFile(name);
        if (stream is null) { return null; }
        if (stream.Length > maximum) { throw new IOException("Storage blob exceeds its byte budget."); }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return new(bytes, Token(bytes));
    }

    private static string Token(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
