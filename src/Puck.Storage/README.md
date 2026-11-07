# Puck.Storage

Puck addresses a blob by an object ID and a relative key. The host chooses the
storage target; callers do not need to know where a blob lives. This project
holds the routed store and the local directory target. A cloud store is a
provider's target in the provider's own project (Azure Blob's is
[Puck.Storage.Azure](../Puck.Storage.Azure/README.md)), reached by a host
through an installed extension's storage provider.

## Add a store

An `ObjectStorageTarget` names the `IObjectBlobStoreBackend` that serves it, so
the routed `IObjectBlobStore` dispatches through the target and no registration
step exists for a new provider to forget. A provider adds a target subtype, its
backend, and a secret-free `Description` for status echoes; the backend keeps
`IObjectBlobStore`'s read, write, list and access-probe semantics.

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
`ObjectBlobAddressPath.GetNormalizedKey` exposes the same portable key validation
to hosts that prepare retained content before it reaches a backend.

## Local filesystem boundary

`DirectoryObjectStorageTarget` captures an absolute root at construction. Its
backend walks every directory component from the volume root through
`Puck.Confinement`'s `ConfinedDirectory`, which opens each component relative to
its parent's handle without following links and keeps the handles alive for the
operation (see [its README](../Puck.Confinement/README.md)). On Windows the held
directories deny delete sharing, so no ancestor can be renamed or replaced while
an operation holds it; on Linux x64 every lookup is `openat` with `O_NOFOLLOW`
relative to a held descriptor. File handles must name regular files with one
hard link. Unsupported platforms and Windows network roots refuse access. Each
lookup carries one component, so a valid nested blob key and its temporary file
can exceed the legacy 260-character path limit without depending on process
manifest settings, and an atomic replacement renames the temporary file's own
handle.

Conditional writes lock the containing directory across cooperating processes,
check the existing content token, write and flush a new temporary file, and
atomically replace the destination. They never truncate the published blob.
Readers take the same lock. Private lock and temporary files are excluded from
listings. Byte and traversal limits bound individual reads, writes, and listings;
these are not total storage quotas. The defaults are 64 MiB per blob and 100,000
visited entries per listing; a caller whose blobs have a larger codec bound sizes
the target from that bound (a world authority store's directory target comes from
`WorldAuthorityBlobStore.DirectoryTarget`). Storage calls can fail during competing filesystem
changes; callers should treat an uncertain write as requiring read-back.

The host must own the root and protect its ancestors and permissions. These
checks defend the storage API; they cannot sandbox arbitrary native or managed
code running under the host's OS identity. Crash and power-loss durability also
depend on the filesystem and volume. Use a suitably durable target for external
operation history, which must survive independently of rewindable game saves.

`ConfinedFile.ReadAllBytes` applies the same no-follow directory walk, regular-file
checks, and preallocation byte ceiling to an exact host-selected configuration
file. It is for the trusted composition root, not an extension's arbitrary-path API.

The native contracts and their sources are in the
[Puck.Confinement README](../Puck.Confinement/README.md). `ConfinedStorageLawTests` exercises real directories, links, concurrent
replacement, conditional writes, and namespace revocation in `Puck.World.Server.Tests`.

## Documentation

- [API reference](../../docs/api)

📚 [Engine overview](https://github.com/ByteTerrace/Puck/blob/main/docs/overview.md) · 🛠️ [Contributing to Puck](https://github.com/ByteTerrace/Puck/blob/main/docs/development/contributing.md)
