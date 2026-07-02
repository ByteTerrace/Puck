# .NET 10 Performance: I/O, Networking, JSON & Cryptography

**TL;DR for this domain:** The convenience API is increasingly the fast path — `HttpClient` helpers, validated header adds, sync `JsonSerializer.Serialize(Stream, ...)`, `Uri` construction, and compression streams all got materially cheaper with zero caller changes, so hand-rolled "optimized" alternatives keep losing their justification. The dominant mechanisms are allocation removal (caching helper objects, span-based paths, right-sized lazy buffers), algorithmic fixes (O(N²)→O(N)), and UTF8-first API surface (parse bytes as bytes; never transcode to `string` just to parse). Streaming beats buffering for large payloads (`Utf8JsonWriter` segments). New crypto APIs are span-first by design. Quadratic paths on untrusted input are security bugs, not just perf smells.

## Contents
- [Reading and writing streams & files](#reading-and-writing-streams--files)
- [Compressing, decompressing, and zip archives](#compressing-decompressing-and-zip-archives)
- [Parsing and validating network primitives (IPAddress, Uri)](#parsing-and-validating-network-primitives-ipaddress-uri)
- [Making HTTP requests](#making-http-requests)
- [Serializing and deserializing JSON](#serializing-and-deserializing-json)
- [Building and mutating JSON DOM (JsonObject/JsonArray/JsonElement)](#building-and-mutating-json-dom-jsonobjectjsonarrayjsonelement)
- [Writing JSON at low level / streaming large values](#writing-json-at-low-level--streaming-large-values)
- [Cryptography](#cryptography)
- [Folklore to delete](#folklore-to-delete)

---

## Reading and writing streams & files

### BufferedStream.WriteByte no longer flushes the underlying stream — FREE
`BufferedStream.WriteByte` historically called `_stream.Flush()` on the underlying stream after draining its buffer (unlike every other write method). Removed. This matters because flushing the underlying stream can be very expensive — `DeflateStream.Flush` forces buffered bytes to be compressed and emitted, hurting both speed and compression ratio. ~4.2x (73.87 ms → 17.77 ms) writing 1 MB byte-by-byte through `BufferedStream(DeflateStream, bufferSize: 256)`.
- Byte-wise writes through a small `BufferedStream` over an expensive-to-flush inner stream are no longer pathological.
- Caveat — subtle behavior change: if you relied on `WriteByte` propagating flushes downstream, call `Flush()` explicitly.

### Async read loops: never gate on StreamReader.EndOfStream (analyzer CA2024) — PATTERN
New analyzer `CA2024` flags `StreamReader.EndOfStream` in async methods. The property can perform **synchronous, blocking I/O** — unless data is buffered or EOF was already seen, it must issue a read; on a network stream that blocks until data arrives, i.e., sync-over-async and thread-pool starvation. It's also redundant: read APIs already signal EOF.

```csharp
// Before: may block synchronously inside an async method
while (!reader.EndOfStream) { string? line = await reader.ReadLineAsync(); ... }
// After: let the read itself signal EOF
while (await reader.ReadLineAsync() is string line) { ... }
```
Equally redundant (though not thread-starving) in sync code. Treat any property that can do I/O with suspicion.

### FileSystemWatcher: native buffer instead of pinned managed array — FREE
The `byte[]` that `FileSystemWatcher` allocated-and-pinned for OS change records is now a native allocation (also fixes a Windows dispose-while-in-use leak). The buffer was only written by native code and read via spans — its "arrayness" was never used; a native buffer needs no pinning, eliminating a long-lived pinned object that fragmented the heap when many watchers existed. Allocation per watcher start: 8,944 B → 744 B; the bigger win is reduced GC fragmentation (Windows-specific).
- Generalizable pattern: a buffer only ever touched through pointers/spans that would need whole-life pinning should be `NativeMemory`, not a pinned `byte[]`.

### MemoryMappedFile.CreateNew on Linux uses memfd_create — FREE
Anonymous (non-file-backed) MMFs on Linux now use the kernel's `memfd_create` instead of `shm_open` — purpose-built anonymous in-memory files, cheaper creation, no named shared-memory object lifecycle. ~1.6x (9.916 µs → 6.358 µs) for CreateNew + CreateViewAccessor. Linux-only; falls back to `shm_open` on old kernels.

---

## Compressing, decompressing, and zip archives

### zlib-ng updated 2.2.1 → 2.2.5 — FREE
`DeflateStream`/`GZipStream`/`ZLibStream` inherit upstream improvements: better AVX2/AVX512 usage, and a revert of a 2.2.0 "cleanup" that had caused a throughput regression on long, highly compressible data. ~2.9x (202.79 µs → 70.45 µs) compressing highly compressible text via `ZLibStream`. If you benchmarked .NET 9 compression and found it regressed on compressible data, re-measure on .NET 10. Gains vary by compressibility and SIMD hardware.

### GZipStream: reset instead of recreate across concatenated gzip members — FREE
Decompressing back-to-back gzip members from one stream now uses zlib's reset capability at member boundaries instead of freeing/re-creating native inflate state per member. ~3.2x (331.3 µs → 104.3 µs) on the pathological case (1000 one-byte members); real gains scale with member-boundary density. No need to hand-split concatenated-gzip streams (log shipping, HTTP) anymore.

### ZipArchive Update mode: change tracking instead of rewrite-the-world — FREE
`ZipArchiveMode.Update` used to load every entry into memory and rewrite all headers + data + central directory. Now change tracking rewrites only from the first affected entry onward — work proportional to what changed and its position, not archive size. Deleting one late entry from a 1000-entry archive: ~2.8x faster, ~3.2x less allocation. Caveat: an edit to the *first* entry still rewrites everything after it; late edits are cheapest.

### ZipArchive drops BinaryReader/BinaryWriter — FREE
Header/metadata serialization no longer goes through `BinaryReader`/`BinaryWriter`, avoiding their buffer allocations and abstraction cost. Generalizable: for hot serialization paths prefer direct span-based reads/writes (`BinaryPrimitives`) over wrapping streams in reader/writer objects.

### ZipArchive/ZipFile async APIs — new API
Long-requested async overloads for loading, manipulating, and saving zips. In async contexts (servers), use them instead of `Task.Run` around sync zip code or blocking on request threads. Scalability feature, not a microbenchmark win.

---

## Parsing and validating network primitives (IPAddress, Uri)

### IPAddress/IPNetwork: UTF8 parsing added; UTF16 parsing faster too — FREE + new API
Both types now parse `ReadOnlySpan<byte>` directly (à la `IUtf8SpanParsable`); UTF8/UTF16 share one generic implementation, which also tightened the existing string path. `IPAddress.Parse("Fe08::1%13542")`: 71.35 ns → 54.60 ns with no caller change.
```csharp
// Before: transcode then parse
IPAddress ip = IPAddress.Parse(Encoding.UTF8.GetString(utf8Bytes));
// After: parse the bytes directly
IPAddress ip = IPAddress.Parse(utf8Bytes);
```
When input is born UTF8 (network buffers, `Utf8JsonReader` payloads), keep it UTF8 end-to-end.

### IPAddress.IsValid / IsValidUtf8: allocation-free validity check — new API
`TryParse(s, out _)` allocates the `IPAddress` you're throwing away. `IsValid(ReadOnlySpan<char>)` / `IsValidUtf8(ReadOnlySpan<byte>)` validate without materializing: 26.26 ns / 40 B → 21.88 ns / 0 B. Only switch when you don't need the parsed object.

### Uri: 65,535-character length limit removed — FREE
Internal offsets widened `ushort`→`int`. Motivated by data URIs (`data:...;base64,...`, common in AI payloads; Base64 inflates ~33%). Delete any workaround that avoided `Uri` for >65K inputs. Cost: instances grow a few bytes (+8 B observed).

### Uri path compression now O(N), was O(N²) — FREE
Collapsing `/hello/../` dot-segments during construction was quadratic; with the length cap gone that was a DoS vector, so it's now linear. Degenerate case (10,000 `a/../` segments): 18.989 µs → 2.228 µs (~8.5x). Typical URIs see little change; the win is adversarial inputs. Corollary for your own code: any input-size-proportional quadratic path is a latent security bug.

### Uri constructor: SearchValues character scan — FREE
The scan for Unicode characters needing deeper handling is now vectorized via `SearchValues` instead of char-at-a-time. Constructing a ~53 KB base64 data URI: 19.354 µs → 2.041 µs (~9.5x). Benefit scales with input length.

### Uri: cheaper IPv6 hosts and span-based normalization — FREE
Common-case specialization for IPv6 hosts without scope IDs (`.Host`: 304.9 ns → 254.2 ns; URIs *with* `%scopeId` get slightly more expensive). Hosts needing normalization (non-ASCII like `ümlauts`) now normalize over spans instead of allocating intermediate strings (377.6 ns / 440 B → 322.0 ns / 376 B).

---

## Making HTTP requests

### GetStringAsync / GetByteArrayAsync / ReadAsStringAsync / ReadAsByteArrayAsync buffer management — FREE
Reworked accumulation-buffer growth/pooling, especially when the server sends no `Content-Length` (chunked) so final size is unknown. 260 KB chunked response: `ReadAsByteArrayAsync` ~0.81x time, ~0.57x alloc; `ReadAsStringAsync` ~0.86x/~0.66x. The convenience helpers are now materially cheaper — you need stronger justification to hand-roll `Stream`-based accumulation for "give me the whole body."

### Custom header add/get: validation streamlined — FREE
Headers without dedicated parsers (i.e., most custom/service-defined headers) only need forbidden-newline validation; the per-header parser/validation objects are gone. `HttpResponseHeaders.Add("X-Custom", "Value")`: 28.04 ns / 32 B → 12.61 ns / 0 B; `GetValues` ~3.4x faster, half the alloc. Less reason to reach for `TryAddWithoutValidation` purely for perf. Well-known parsed headers keep their machinery.

### HTTP/2 HPackDecoder: lazy buffer growth — FREE
Header-decompression buffers now grow lazily from expected-case sizes instead of preallocating worst-case. This is a **connection-density** win (lower steady-state memory per pooled HTTP/2 connection), not per-request throughput — hard to see in pooled-connection microbenchmarks. Reminder: never create a new `HttpClient`/handler per request; it tears down the connection pool.

### Sockets: per-completion ConcurrentDictionary lookup removed (Linux/macOS) — FREE
The epoll/kqueue async-completion path no longer does a synchronized-dictionary lookup per operation; all socket-based I/O (including all HTTP) gets a small constant-cost reduction. Linux/macOS only.

### Native AOT: feature switch to trim HTTP/3 out of HttpClient — config
HTTP/3/QUIC is a large dependency; if your trimmed/AOT app never uses it, set the feature switch for "very sizeable" binary-size savings. Only if you genuinely never use HTTP/3.

---

## Serializing and deserializing JSON

### Sync JsonSerializer.Serialize(Stream, ...) now uses the writer cache — FREE
`JsonSerializer` caches its helper objects (`Utf8JsonWriter`, `IBufferWriter`) across calls; the cache was used by async streaming but not by sync stream serialization. Fixed. Small-object sync `Serialize(stream, data)`: 115.36 ns / 176 B → 77.73 ns / **0 B**. Don't contort code to the async overloads just to hit the cache.

### JsonElement.Parse: the right way to get a standalone JsonElement — new API
Previously the options were all flawed:
1. `JsonDocument.Parse` + `RootElement.Clone()` + dispose — clone overhead;
2. `JsonDocument.Parse(json).RootElement` without disposing — **never do this**: `JsonDocument` is backed by an `ArrayPool<>` array, and not disposing permanently leaks a pooled array (pool arrays tend to be older/higher-generation, so the leak is extra costly);
3. `JsonSerializer.Deserialize<JsonElement>(json)` — drags in the whole serializer.

`JsonElement.Parse(json)` gives non-pooled backing with no clone and no serializer overhead: 261.9 ns / 272 B — matches the leaky version's speed at the lowest allocation with no pool damage.
```csharp
// Before (clone-then-dispose dance)
JsonElement clone;
using (JsonDocument doc = JsonDocument.Parse(json)) clone = doc.RootElement.Clone();
return clone;
// After
return JsonElement.Parse(json);
```
Caveat: for strictly **scoped** use, `using (JsonDocument doc = JsonDocument.Parse(json)) { Use(doc.RootElement); }` remains the fastest option — keep it.

---

## Building and mutating JSON DOM (JsonObject/JsonArray/JsonElement)

### JsonObject indexer setter: double dictionary lookup eliminated — FREE
The setter did two key-based lookups into the backing `OrderedDictionary<,>`; it now uses .NET 10's index-returning `OrderedDictionary` overloads so the second access is by index. `_obj["key"] = "value"`: 40.56 ns → 16.96 ns (>2x). Hash lookup dominates the setter's cost; halving the lookups roughly halves the setter.

### JsonObject.TryAdd + index-returning overloads — new API
`TryAdd` (single lookup instead of `ContainsKey`+`Add` = two hashes), plus `TryAdd`/`TryGetPropertyValue` overloads returning the property's index so subsequent accesses skip hashing entirely.
```csharp
// Before
if (!obj.ContainsKey("key")) obj.Add("key", value);
// After
obj.TryAdd("key", value);
```
Modest per-call (16.59 ns → 14.31 ns); matters in hot JSON-building loops. Hunt the `ContainsKey`-then-`Add` double-lookup pattern in application code generally.

### JsonArray.RemoveAll / RemoveRange — new API
`JsonArray` wraps a `List<JsonNode?>`; the new methods inherit `List<T>`'s implementations. An index-walking `RemoveAt` loop shifts the whole tail per removal → O(N²) total; `RemoveAll`/`RemoveRange` shift each survivor once → O(N). Removing every other element from 100K: 355.230 ms → 2.022 ms (**~175x**).
```csharp
// Before: O(N^2)
int i = 0;
while (i < arr.Count) { if (ShouldRemove(arr[i])) arr.RemoveAt(i); else i++; }
// After: O(N)
arr.RemoveAll(n => ShouldRemove(n));
```
If truly removing everything use `Clear`; if hand-rolling, remove last-to-first. (`RemoveAll` currently allocates a 24 B closure.)

### JsonMarshal.GetRawUtf8PropertyName — new API
Complements .NET 9's `GetRawUtf8Value`: exposes the raw underlying UTF8 bytes of a property *name* from a `JsonElement` — zero-copy, no per-name string materialization/transcoding in low-level scanning code. Marshal-class API: spans are only valid while the backing document is alive.

---

## Writing JSON at low level / streaming large values

### Utf8JsonWriter.WriteStringValueSegment — new API
A single JSON string value can now be written in multiple chunks (previously one call, one buffer). For large, lazily-produced string values, write segments as they arrive instead of concatenating first — the win is peak working set and latency, not throughput.

### Utf8JsonWriter.WriteBase64StringSegment — new API
`WriteBase64StringSegment(ReadOnlySpan<byte>, bool isFinalSegment)` streams Base64-encoded chunks into one JSON string property (finish with `isFinalSegment: true`). Previously an entire binary blob had to be buffered (grow-and-copy churn) before one `WriteBase64String` call. 10 MB blob from a stream: 3.925 ms → 1.555 ms (~2.5x) — and the primary win is bounded peak memory, which that number understates.
```csharp
// Before: accumulate all bytes, then writer.WriteBase64String("data", allBytes);
// After: stream chunks
writer.WritePropertyName("data");
while ((read = await source.ReadAsync(buffer)) > 0)
    writer.WriteBase64StringSegment(buffer.AsSpan(0, read), isFinalSegment: false);
writer.WriteBase64StringSegment(default, isFinalSegment: true);
```
Remember the final `isFinalSegment: true` call to terminate the property.

---

## Cryptography

### Post-quantum crypto: ML-DSA, Composite ML-DSA, SLH-DSA, ML-KEM — span-first by design — new API
.NET 10 adds NIST PQC signatures (ML-DSA, SLH-DSA), draft-IETF Composite ML-DSA (ML-DSA + a classical algorithm like RSA), and ML-KEM key encapsulation. Unlike the array-centric `AsymmetricAlgorithm`-era types with spans bolted on later, these are designed spans-first; arrays are the convenience layer — no forced allocations on core paths. Prefer the span overloads; treat these as the model for modern crypto API shape. ("Harvest now, decrypt later" makes migration planning urgent.) Caveat: Linux PQC requires OpenSSL 3.5+.

### OpenSSL 3 digests: explicit EVP_MD_fetch + cache — FREE (Linux/OpenSSL)
OpenSSL 3.x turned the 1.x cheap getters (`EVP_sha256()`) into compat shims that trigger an "implicit fetch" inside *each* operation (`EVP_DigestInit_ex`). .NET now does the recommended explicit `EVP_MD_fetch` once and caches it — hoisting per-operation provider resolution out of every hash/sign/**TLS handshake**. `SHA256.HashData` of 1 KB: 1,206.8 ns → 960.6 ns (~20%). Generalizable: "fetch once, cache, reuse" applies to native handles too.

### PemEncoding UTF-8 support — new API
PEM (certs/keys) can be parsed directly from UTF-8 bytes; previously `char`-only, forcing a transcode + allocation. When source bytes are UTF-8 (files, network), parse PEM from bytes.

### X509Certificate2Collection.FindByThumbprint — new API (FREE inside SslStream)
Uses a stack buffer per candidate thumbprint, eliminating the per-candidate arrays a naive `Find`/`GetCertHashString` loop creates. `SslStream` uses it internally.
```csharp
// Before: allocates per candidate
var match = collection.Find(X509FindType.FindByThumbprint, thumbprint, false);
// After
X509Certificate2? match = collection.FindByThumbprint(thumbprintBytes);
```

### SymmetricAlgorithm.SetKey (span-based) — new API
Avoids the array the `Key` property setter requires. When key material is already in a span/stack buffer, use `SetKey` instead of allocating an array for the property.

### ProtectedData span overloads — new API
Windows DPAPI protect/unprotect without source/destination arrays.

### AsnWriter.Encode callback-based encoding — new API / FREE
Exposes encoded state via callback without a temporary array; used throughout the crypto stack internally (FREE), available to direct `AsnWriter` users (API).

### X509Certificate SafeHandle singleton — FREE
More `X509Certificate` paths use a singleton `SafeHandle`, avoiding temporary handle allocations.

---

## Folklore to delete

- **"`while (!reader.EndOfStream)` is the clean way to read to end."** It's a sync-I/O time bomb in async code and redundant everywhere; use the read call's null/zero result. Now analyzer-enforced (CA2024).
- **"Byte-wise writes through `BufferedStream` over a compression stream are pathologically slow."** The hidden per-`WriteByte` downstream `Flush()` is removed (~4x in the worst case).
- **"Rebuild the whole zip rather than using `ZipArchiveMode.Update` for a small change."** Update mode no longer rewrites the world; cost is proportional to what changed (late edits cheapest).
- **"You must split concatenated gzip members yourself to decompress efficiently."** One `GZipStream` resets native state across members (~3x on member-heavy streams).
- **"Wrap streams in `BinaryReader`/`BinaryWriter` for structured I/O."** The BCL itself is removing them from hot paths (`ZipArchive`); use span-based `BinaryPrimitives`.
- **"Use `TryParse(s, out _)` to validate an IP."** `IPAddress.IsValid`/`IsValidUtf8` is faster and allocation-free.
- **"`Uri` can't hold data URIs / anything over 65K chars."** Limit gone; delete raw-string plumbing that dodged `Uri`.
- **"Transcode UTF8 bytes to `string` before parsing (IPs, PEM, hex)."** `IPAddress`/`IPNetwork` UTF8 parse and `PemEncoding` UTF-8 parse bytes directly; keep data UTF8 end-to-end.
- **"Avoid the `HttpClient` string/byte[] helpers on hot paths; hand-roll stream reads."** Buffer management was overhauled (~40% fewer allocs on chunked responses); hand-rolling needs re-justification.
- **"Use `TryAddWithoutValidation` because validated header adds are slow/allocatey."** Validated `Add` on custom headers is now ~2x faster and allocation-free.
- **"Return `JsonDocument.Parse(json).RootElement` — GC will handle it."** It never did (permanently leaks an `ArrayPool` array). `JsonElement.Parse` is the correct one-liner; also delete clone-then-dispose dances and `Deserialize<JsonElement>` used purely to obtain an element.
- **"Prefer async `JsonSerializer` stream overloads because sync ones allocate helper objects per call."** Both paths use the cache now; sync stream serialization is alloc-free.
- **"`RemoveAt` in a loop is OK for JSON arrays."** O(N²); `JsonArray.RemoveAll`/`RemoveRange` are ~175x faster at scale. Same rule as `List<T>`; if hand-rolling, remove from the end.
- **"Buffer the whole blob before Base64-writing it into JSON."** `WriteBase64StringSegment` streams it: ~2.5x faster with bounded memory.
- **"`ContainsKey` then `Add`/indexer is fine on JSON objects."** Two hashes; `JsonObject.TryAdd` and the index-returning overloads do one.
