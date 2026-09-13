# Puck.Storage

Puck addresses a blob by an object ID and a relative key. The host chooses the
storage target; the same routed store supports local directories and Azure Blob
Storage. Callers do not need to know where a blob lives.

## Route an oid to its storage account

`IPartitionResolver` answers which storage account owns a user's container.
`DefaultPartitionResolver` runs `Puck.Maths`' `MonotonicPartitioner` over
`PartitioningOptions`, so the silo, the edge, and the browser's WASM build all
land on the same account without coordinating; the monotonic invariant means
raising `Count` only migrates the users who fall into a new bucket.

`PartitioningOptions.AnchorPartition` is the exception to routing: a user's
published `public/` content lives in their oid-named container on that partition
whatever their home partition is, so published URLs survive a migration.

The resolver reports where a user *should* live for the current partition count.
During a migration the recorded home and the computed partition differ and the
data is still in the home, so a caller that must reach real bytes resolves
through the owning grain rather than through this.

## Give callers a namespace

Keep the routed store, target, and journal address in the composition root.
Give an extension an `ObjectBlobNamespace` when it needs private persistence:

```csharp
var storage = new ObjectBlobNamespace(store, privateTarget, worldId,
    "creature-controller", maximumBlobBytes: 65536, writable: true);
await storage.WriteAsync("memory.json", "{}"u8.ToArray());
var saved = await storage.ReadAsync("memory.json");
```

The name is opaque and case-sensitive. Its SHA-256 directory name prevents
nested names or case-insensitive filesystems from merging namespaces. The API
accepts only keys inside that namespace, exposes no target or root path, and
defaults to read-only access. Writes copy their input. A write using the read
version token can refuse a concurrent change instead of overwriting it.

Dispose the capability to revoke future access. `WithLifetime` adds a host
lifetime check without widening access; revoking the parent also revokes its
children. Already-admitted writes may complete. The
[world extension host](../Puck.World.Server/ExtensionHosting.md) ties a client's
storage to its replay and disposal lifetime.

Keys reject rooted paths, dot segments, Windows device names, alternate data
streams, invalid filename characters, surrounding whitespace, and the reserved
`.puck-` prefix. A key has at most 64 segments and 4096 characters. Separators
normalize to `/`; a key's case sensitivity still follows its backend. Namespace
names are separate from keys and have a 1024-byte UTF-8 ceiling.

## Local filesystem boundary

`DirectoryObjectStorageTarget` captures an absolute root at construction. Its
backend opens every directory component without following links and keeps the
directory handles alive for the operation. On Windows it rejects reparse points
and denies write/delete sharing on those handles, preventing directory
substitution while paths are in use. Linux x64 resolves child entries relative
to directory descriptors with `openat` and `O_NOFOLLOW`. File handles must name
regular files with one hard link. Unsupported platforms and Windows network
roots refuse access.

Conditional writes lock the containing directory across cooperating processes,
check the existing content token, write and flush a new temporary file, and
atomically replace the destination. They never truncate the published blob.
Readers take the same lock. Private lock and temporary files are excluded from
listings. Byte and traversal limits bound individual reads, writes, and listings;
these are not total storage quotas. The defaults are 64 MiB per blob and 100,000
visited entries per listing. Storage calls can fail during competing filesystem
changes; callers should treat an uncertain write as requiring read-back.

The host must own the root and protect its ancestors and permissions. These
checks defend the storage API; they cannot sandbox arbitrary native or managed
code running under the host's OS identity. Crash and power-loss durability also
depend on the filesystem and volume. Use a suitably durable target for external
operation history, which must survive independently of rewindable game saves.

`ConfinedFile.ReadAllBytes` applies the same no-follow directory walk, regular-file
checks, and preallocation byte ceiling to an exact host-selected configuration
file. It is for the trusted composition root, not an extension's arbitrary-path API.

The native contracts are documented in Microsoft's
[CreateFile reference](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea)
and Linux's [openat](https://man7.org/linux/man-pages/man2/openat.2.html) and
[rename](https://man7.org/linux/man-pages/man2/rename.2.html) references.
`ConfinedStorageLawTests` exercises real directories, links, concurrent
replacement, conditional writes, and namespace revocation in `Puck.World.Tests`.

## Documentation

- [API reference](../../docs/api)

📚 [Engine overview](https://github.com/ByteTerrace/Puck/blob/main/docs/overview.md) · 🛠️ [Contributing to Puck](https://github.com/ByteTerrace/Puck/blob/main/docs/development/contributing.md)
