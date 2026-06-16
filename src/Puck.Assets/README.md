# Puck.Assets

**Content-addressed asset loading and caching primitives.** Puck.Assets is the small,
shared foundation the rest of the engine builds asset pipelines on: an abstraction over a
raw byte store (`IAssetSource`), a compact content identity derived from those bytes
(`AssetContentHash`), and a fixed-capacity LRU cache keyed by that identity
(`ContentAddressedLruCache<T>`).

It is deliberately **minimal**. It moves and identifies *bytes* — it does **not** decode
them (turning bytes into a texture, font, or shader is the caller's job), it does **not**
normalize or resolve paths (a source consumes the path it is given), and it is **not** a
virtual file system (no mounts, no layering). Those concerns belong to the consumers
(Puck.Text, Puck.Shaders, Puck.SdfVm, …) until a third one genuinely needs them shared.

```text
namespace Puck.Assets
target     net10.0
deps       none — BCL only
```

---

## At a glance

| Type | Kind | Role |
|------|------|------|
| `IAssetSource` | interface | A byte store addressed by path: `Exists` + `Read`. |
| `FileSystemAssetSource` | `sealed class` | An `IAssetSource` backed by the local file system. |
| `AssetContentHash` | `readonly record struct` | A 64-bit content identity — the leading 64 bits of a payload's SHA-256. |
| `ContentAddressedLruCache<TValue>` | `sealed class` | A fixed-capacity, least-recently-used cache keyed by `AssetContentHash`. |

---

## Sourcing bytes

`IAssetSource` decouples whatever loads an asset from wherever the bytes actually live — the
local file system, an archive, an embedded resource, a network blob. The contract is two
methods:

```csharp
bool Exists(string path);
ReadOnlyMemory<byte> Read(string path);
```

`Read` returns the **full** payload as `ReadOnlyMemory<byte>`; callers slice and decode from
there. Paths are **opaque** to the source: it resolves them exactly as supplied, so any
base-path joining or normalization happens *upstream* of this layer.

`FileSystemAssetSource` is the built-in implementation, a thin pass-through to
`System.IO.File`:

```csharp
using Puck.Assets;

IAssetSource source = new FileSystemAssetSource();

if (source.Exists(path: "assets/sprites/hero.png")) {
    ReadOnlyMemory<byte> bytes = source.Read(path: "assets/sprites/hero.png");
    // ... hand the bytes to a decoder ...
}
```

Both methods reject a `null`, empty, or whitespace `path` with `ArgumentException`; `Read`
throws `FileNotFoundException` when nothing exists at the path (`Exists` simply returns
`false`). Implement `IAssetSource` yourself to read from any other backing store.

---

## Content addressing

`AssetContentHash` is a value's *identity by its content*: hash the bytes, key everything by
the hash, and identical payloads collapse to one entry no matter how they were named or
where they came from.

```csharp
using Puck.Assets;

AssetContentHash hash = AssetContentHash.Compute(content: bytes.Span);

ulong  key  = hash.Value;        // the raw 64-bit identity
string text = hash.ToString();   // "sha256-64/1f3a9c0b7e5d2148"  (canonical form)
```

`Compute` takes the SHA-256 of the payload and keeps its **leading 64 bits**. That makes the
hash a cheap, fixed-size key to compare, store, and pass around. The trade-off is explicit:
truncating to 64 bits is an **identity and de-duplication** mechanism, **not** a security or
tamper-evidence guarantee — at 64 bits a collision becomes likely around ~2³² distinct
payloads (the birthday bound). Use it to key caches and dedupe loads; do not use it to
authenticate untrusted content.

`ToString` renders the canonical `sha256-64/{value}` form (lowercase, zero-padded 16-digit
hex) for logs and diagnostics.

---

## Caching

`ContentAddressedLruCache<TValue>` is a fixed-capacity cache that maps an `AssetContentHash`
to a decoded value (a parsed atlas, an uploaded texture handle, a compiled shader — whatever
`TValue` is). When it is full, the **least-recently-used** entry is evicted; reading or
writing an entry marks it most-recently-used.

```csharp
using Puck.Assets;

// Capacity 256; the optional callback fires for every value leaving the cache.
var cache = new ContentAddressedLruCache<Texture>(
    capacity: 256,
    onEvicted: texture => texture.Dispose()
);

AssetContentHash key = AssetContentHash.Compute(content: bytes.Span);

// Decode-on-miss; subsequent hits return the cached value and refresh its recency.
Texture texture = cache.GetOrAdd(
    hash: key,
    valueFactory: () => DecodeTexture(bytes)
);
```

| Member | Behavior |
|--------|----------|
| `GetOrAdd(hash, valueFactory)` | Returns the cached value, or invokes `valueFactory`, caches the result, and returns it on a miss. |
| `TryGet(hash, out value)` | Returns whether the entry exists; on a hit, marks it most-recently-used. |
| `Set(hash, value)` | Caches a value, evicting the least-recently-used entry if capacity is exceeded. |
| `Clear()` | Removes every entry, invoking the eviction callback for each. |
| `Capacity` / `Count` | The eviction threshold (fixed at construction, `> 0`) and the current entry count. |

The `onEvicted` callback runs whenever a value **leaves** the cache — on capacity eviction,
on `Clear`, **and** when `Set` replaces the value already stored under a key. That makes it
the single place to release whatever the cached value owns (native handles, pooled buffers,
`IDisposable`s). Construction rejects a non-positive `capacity` with
`ArgumentOutOfRangeException`.

The cache is **not thread-safe**: keep one instance per thread, or guard access with your
own synchronization.

---

## Notes for agents

- **Bytes in, bytes out.** This library never decodes or deserializes. Convert
  `ReadOnlyMemory<byte>` into typed assets in the consumer; key the decoded result on the
  content hash if you want to cache it.
- **Paths are opaque.** `IAssetSource` resolves a path exactly as given. Do base-path
  joining and normalization *before* calling in — don't push it down here.
- **The hash identifies, it doesn't authenticate.** 64 bits of SHA-256 is a fast dedup /
  cache key, not a collision-resistant or tamper-evident digest. Don't use it on untrusted
  input as a security check.
- **The cache is not thread-safe** and the eviction callback runs on the calling thread; do
  resource cleanup (`Dispose`, pool returns) inside `onEvicted` rather than scattering it.
- **Keep this library small.** No virtual file system, no decoders, no DI wiring belong here
  until a third consumer needs them shared — route those into the consuming library instead.
- See the [generated API reference](../../docs/api) for the full member-by-member docs.
