---
name: dotnet10-performance
description: Expert-level .NET 10 performance knowledge distilled from the runtime team's official performance deep-dive (Stephen Toub's ".NET 10 Performance Improvements", verified complete against the source). Use this skill whenever writing, reviewing, refactoring, or optimizing C# code — this repo targets net10.0 everywhere — and especially before micro-optimizing anything, when reviewing changes to hot paths, when choosing collection/string/JSON/SIMD/interop APIs, or when any claim of the form "X is slow in .NET" comes up (the folklore list corrects stale performance beliefs). Also consult it when a performance regression or benchmark question arises, even if nobody says the word "performance".
---

# .NET 10 performance

This skill is **factual only**: it describes how .NET 10 actually behaves. It
does not govern architecture, design, or style, and the user's current
instruction outranks it — if it ever argues against a requested change, treat
the skill as stale, update it in the same change, and say so.

This file is the distilled core. Six domain references under `references/` carry the full detail (mechanisms, magnitudes, caveats, before/after snippets). Read the reference file for the domain you're touching; each is self-contained.

| You are working on… | Read |
|---|---|
| Hot loops, abstractions, allocation, inlining, bounds checks, `sealed`, ISA/codegen | [references/jit-and-codegen.md](references/jit-and-codegen.md) |
| Collections, LINQ, Frozen/Immutable, CollectionsMarshal | [references/collections-and-linq.md](references/collections-and-linq.md) |
| Strings, spans, searching, Regex, UTF-8 text, encoding | [references/strings-text-search.md](references/strings-text-search.md) |
| Numeric kernels, SIMD, TensorPrimitives, randomness, threading primitives | [references/numerics-simd-threading.md](references/numerics-simd-threading.md) |
| File/stream I/O, compression, sockets/HTTP, System.Text.Json, crypto | [references/io-network-json-crypto.md](references/io-network-json-crypto.md) |
| Native AOT, reflection/UnsafeAccessor, GC handles, diagnostics, DI, misc runtime | [references/runtime-aot-reflection-diagnostics.md](references/runtime-aot-reflection-diagnostics.md) |

When reviewing code, scan **Folklore to delete** below — flagging obsolete hand-optimizations is as valuable as finding bugs. When writing new code, follow **Pattern changes**.

## The .NET 10 performance mental model

- **Idiomatic C# is the fast path.** The JIT flattens abstraction for you (devirtualize → inline → escape-analyze → stack-allocate → range-analyze): interfaces, `foreach`, lambdas, `params` arrays, `^1`, guard-then-index, switch-on-length all compile to what hand-tuned code used to. Write for clarity; delete contortions.
- **Inlining is the gateway; escape analysis is the payoff.** Delegates, temporary arrays, spans-over-arrays, enumerators, even `Stopwatch` instances get stack-allocated — but only if *every* use of the object inlines. One opaque virtual call (even a no-op `Dispose`) makes the whole object escape to the heap. Keep hot members small and whole.
- **Safety checks pay only when unprovable.** Bounds checks, null checks, GC write barriers, array covariance checks and dead branches are deleted wherever the JIT proves them redundant (value ranges from bit math, dominating accesses, immutable lengths, `switch` constants, stack-guaranteed return buffers). Write natural validation; let the prover delete it.
- **`sealed` is a compounding lever.** It enables devirtualization, cheap exact-type checks, and array-covariance-check elimination (`List<string>` writes become plain stores). Seal every class you can.
- **UTF-8 and spans are the first-class shape.** `Guid`/`Version`/`Char`/`Rune` now implement `IUtf8SpanParsable`, and `IPAddress`/`IPNetwork` gained UTF-8 span parse overloads; new APIs (PQC crypto, hex, PEM, JSON marshal) are span-first with arrays as the convenience layer. Data born as bytes should stay bytes end-to-end — transcoding to UTF-16 first is now slower *and* unidiomatic.
- **The framework absorbs degenerate cases and hand-optimizations.** Single-task `WhenAll`, channel-cancellation buildup, local-queue starvation, `ValueStopwatch`, shuffle helpers, single-`Contains` LINQ pipelines — delete defensive special-casing. Delegating to primitives (`SearchValues`, `TensorPrimitives`, `MemoryExtensions`) means inheriting every future SIMD/algorithm improvement; a manual loop inherits none.
- **Optimization is work elimination, not faster work.** Regex auto-atomicity, LINQ cross-operator algebra (`OrderBy().Contains()` skips the sort), double-lookup elimination, one-fewer-delegate-layer. In your own hot paths, hunt single layers of indirection and redundant lookups.
- **Upgrading the TFM is itself a strategy.** The release is hundreds of compounding small wins; net8→net10 compounds two releases. Benchmark folklore ages fast — re-measure (BenchmarkDotNet, `--runtimes net9.0 net10.0`) before keeping any workaround.

## Free wins (just target net10.0)

- **JIT/escape analysis:** `foreach` over `IEnumerable<T>`-typed arrays/`List<T>` alloc-free and ~2-3x; non-escaping delegates ~3x; temp `new T[]{...}`/`params` arrays stack-allocated ~3x; array→`AsSpan`→`CopyTo` chains ~11x; `Stopwatch.StartNew` alloc-free.
- **Devirtualization:** array interface methods devirtualize (`ReadOnlyCollection` for-loop ~3x, array-backed LINQ `Skip/Take/Sum` ~2x); `static readonly` interface-typed fields get exact-typed without PGO (~8x); `EqualityComparer<T>.Default` in shared generics ~2x.
- **Inlining/EH:** try/finally no longer blocks inlining (`lock`/`using`/`foreach` helpers inline); provably-non-throwing try/catch deleted entirely; inlining budget >2x.
- **Checks/barriers:** big bounds-check-elimination expansion (bit-math ranges, `x[0]`/`x[^1]`, switch-on-length, order-independent guards, span loop cloning); struct returns and ref-struct fields skip GC write barriers.
- **Collections:** `ConcurrentDictionary` enumeration ~6.4x; `Stack<T>` enumeration ~4-5x and `Queue<T>` ~3.5x; constant-string dictionary keys ~2.4x; `FrozenDictionary` enum/integral keys ~2.4x (array-indexed when dense).
- **LINQ:** `Contains` specialized across ~30 iterators (`OrderBy/Distinct/Union.Contains` ~50-350x); `Skip/Take.ToArray/ToList` over array/`List` is a span slice-copy (~4.7x).
- **Regex:** auto-atomicity widened — the multi-char-loop change alone improved >7% of ~20k real-world patterns (~12% on its benchmark); word-boundary shapes ~5.4x; category-disjoint alternations ~2.6x.
- **Networking/JSON:** `Uri` construction ~9.5x on long inputs, path compression now O(N), 65K length cap gone; custom header `Add` ~2x and alloc-free; sync `JsonSerializer.Serialize(Stream)` alloc-free; `JsonObject` indexer set >2x; `HttpClient` body helpers ~40% fewer allocs on chunked responses.
- **Primitives/threading:** `DateTimeOffset` arithmetic ~5.8x (plus DateTime/DateOnly/TimeOnly construction micro-wins); `UInt128` division ~49x; `Task.WhenAll` single-task ~2.2x; channel cancellation working set bounded (was unbounded growth); ThreadPool local-queue starvation hangs fixed (mass sync-over-async blocking still exhausts the pool — the anti-pattern stands).
- **I/O/compression:** zlib-ng bump ~2.9x on compressible data; concatenated-gzip decompress ~3.2x; `ZipArchive` Update proportional to change; `BufferedStream.WriteByte` no longer flushes downstream (~4x over compression streams).
- **Misc:** `Type.AssemblyQualifiedName` cached (>100x repeated); `Random.GetItems` non-pow2 alphabets ~2x (crypto RNG ~27x); OpenSSL digests ~20% (Linux); DATAS GC tuned — re-measure before keeping a .NET 9 opt-out.
- **Native AOT:** preinit accepts idiomatic cctors (spans, cross-type statics); identical generic bodies fold; size-optimized LINQ regained high-value specializations.

## Pattern changes (write code differently now)

**Allocation & abstraction**
- Take/return `IEnumerable<T>` freely in hot paths — PGO+escape analysis stack-allocates the enumerator for arrays/`List<T>`/`Stack`/`Queue`/`ConcurrentDictionary`; stop adding concrete-type overloads for this. (references/collections-and-linq.md)
- Do NOT split cold tails into separate `SlowPath()` methods — PGO classifies them cold, blocks inlining, and kills stack allocation of the containing object (the BCL deleted its own `MoveNextRare`). (references/collections-and-linq.md)
- Replace `switch`/`goto case` state machines with plain `while` loops — irreducible loops defeat JIT analysis and inlining (~6.4x on `ConcurrentDictionary`'s enumerator). (references/collections-and-linq.md)
- Return multi-field structs (record structs of strings included) by value — return buffers are stack-guaranteed, write barriers elided; drop `out`-param contortions. (references/jit-and-codegen.md)
- Declare constant collections/singletons as `static readonly`, interface-typed is fine — the JIT retypes to the concrete type without PGO. (references/jit-and-codegen.md)
- Prefer span collection expressions (`ReadOnlySpan<T> x = [a, b, c];`) for small temp state; a `new T[]{...}` passed to an inlinable callee often stack-allocates now too. (references/jit-and-codegen.md)
- Seal every class you can — devirtualization, exact-type checks, covariance-check elimination. (references/jit-and-codegen.md)
- Time with plain `Stopwatch.StartNew()/Stop()/Elapsed` — alloc-free via escape analysis; delete `ValueStopwatch` structs. (references/runtime-aot-reflection-diagnostics.md)

**Checks & control flow**
- Write guards in logical order and index at proven lengths — assertion merging is order-independent; `x[0]`/`x[^1]` is single-check; access-then-`Slice` doesn't double-validate. (references/jit-and-codegen.md)
- Use `(uint)index < (uint)length` to merge sign+bounds checks; shape ring-buffer wraparound so the compare doubles as the bounds proof (compare+subtract, never `%`). (references/collections-and-linq.md)
- Use `c is 'a' or 'b' or ...` pattern matching for small char/enum set membership — compiles to a single bit test (~3.5x). (references/jit-and-codegen.md)
- Keep `lock`/`using`/try-finally inside small helpers — try/finally no longer blocks inlining (try/catch still does). (references/jit-and-codegen.md)
- Never gate async read loops on `StreamReader.EndOfStream` — it can block synchronously; use `while (await reader.ReadLineAsync() is string line)` (CA2024). (references/io-network-json-crypto.md)

**Searching & spans**
- Funnel every multi-value char/byte/substring scan through a cached `static readonly SearchValues<T>` — the strategy repertoire grows each release. (references/strings-text-search.md)
- Use `ContainsAny*` instead of `IndexOfAny*(...) >= 0` when the index is unused. (references/strings-text-search.md)
- In unconstrained-generic code, call the new `MemoryExtensions` comparer-accepting overloads instead of manual scan loops — null/default comparer stays vectorized (LINQ's `Enumerable.Contains` gained ~3.2x this way). (references/strings-text-search.md)
- Tokenize with span `MemoryExtensions.Split` (yields `Range`s), not `string.Split`. (references/strings-text-search.md)
- In encoders/decoders: scan first (SearchValues), allocate only if a change is needed; return the input when it's a no-op. (references/strings-text-search.md)

**Collections**
- Use `FrozenDictionary`/`FrozenSet` for long-lived read-mostly maps with enum or small-integral keys — dense keys become a flat array lookup (~0.9 ns). (references/collections-and-linq.md)
- Hoist `GetAlternateLookup` acquisition out of loops; general API rule: expose generic virtual methods as get-a-worker-delegate factories, never on the per-call path. (references/collections-and-linq.md)
- Pass `capacity` to `ConcurrentDictionary` you `Clear()` and refill — the hint now survives Clear. (references/collections-and-linq.md)
- Kill double lookups: `OrderedDictionary.TryAdd/TryGetValue(..., out int index)` + `SetAt`; `JsonObject.TryAdd` instead of `ContainsKey`+`Add`. (references/collections-and-linq.md, references/io-network-json-crypto.md)
- Fill numeric series with `AddRange(Enumerable.Sequence<T>(...))` (~40x vs manual `Add` loop); sample with `Shuffle().Take(n)` (reservoir sampling); use `LeftJoin`/`RightJoin` instead of the GroupJoin/SelectMany/DefaultIfEmpty idiom (~2x). (references/collections-and-linq.md)
- Remove multiple elements with `RemoveAll`/`RemoveRange` (`List<T>`, `JsonArray`) — `RemoveAt` loops are O(N²) (~175x on 100K elements). (references/io-network-json-crypto.md)
- Bulk bit work on `BitArray`: `CollectionsMarshal.AsBytes` + vectorized helpers (`TensorPrimitives.HammingBitDistance`, ~20x) instead of per-bit indexing. (references/collections-and-linq.md)

**Numerics & randomness**
- Default any elementwise/reduction math over numeric spans to `TensorPrimitives` — now covers `Half` (~12x), int division (~2.8x), `nint`, `LeadingZeroCount` (~33x on AVX512); it beats hand-written scalar and most hand-written vector loops. (references/numerics-simd-threading.md)
- Generate random tokens with `Random.GetString`/`GetHexString`/`GetItems` — crypto-secure `RandomNumberGenerator.GetItems` is now viable in hot paths (~27x). (references/numerics-simd-threading.md)
- On large mutable types, define C# 14 `operator +=` for in-place mutation; prefer `t1 += t2` on `Tensor<T>` — no longer sugar for allocate-and-replace. (references/numerics-simd-threading.md)

**UTF-8, JSON, networking**
- Parse primitives directly from UTF-8 bytes: `Guid.Parse(utf8)`, `Version.Parse(utf8)`, `IPAddress.Parse(utf8)` — never `Encoding.UTF8.GetString` first. (references/io-network-json-crypto.md, references/strings-text-search.md)
- Validate with `IPAddress.IsValid`/`IsValidUtf8`, not `TryParse(s, out _)`. (references/io-network-json-crypto.md)
- Get standalone elements via `JsonElement.Parse`; keep `using JsonDocument` for scoped use; never return an undisposed `RootElement` (permanently leaks an ArrayPool array). (references/io-network-json-crypto.md)
- Stream huge JSON string/binary values with `WriteStringValueSegment`/`WriteBase64StringSegment` — bounded memory, ~2.5x; read property names as raw UTF-8 via `JsonMarshal.GetRawUtf8PropertyName`. (references/io-network-json-crypto.md)

**Interop, reflection, I/O**
- Replace hot-path private/cross-assembly reflection with `[UnsafeAccessor]` + `[UnsafeAccessorType]` (~3.6x vs cached `FieldInfo`, no boxing; the BCL uses it for its own layering cycles). (references/runtime-aot-reflection-diagnostics.md)
- Use typed `GCHandle<T>`/`PinnedGCHandle<T>`/`WeakGCHandle<T>` (~18% cheaper, safer); allocate whole-life-pinned native-only buffers with `NativeMemory` instead of pinning managed arrays (stops heap fragmentation). (references/runtime-aot-reflection-diagnostics.md)
- Wrap compression streams around the final destination — no intermediate `MemoryStream` buffer-then-copy. Construct the final delegate type directly — each wrapper layer costs a call (~18% on DI factories). (references/runtime-aot-reflection-diagnostics.md)
- Serialize headers/records with span-based `BinaryPrimitives`, not `BinaryReader`/`BinaryWriter` (the BCL is deleting them from its own hot paths). (references/io-network-json-crypto.md)
- Use copy-on-write immutable arrays for read-hot/write-rare listener lists (the `ActivitySource` fix). (references/runtime-aot-reflection-diagnostics.md)

**Regex & logging**
- Use `Regex.IsMatch` over `Match(...).Success` (CA1874) and `Regex.Count` over `Matches(...).Count` (CA1875) — ~3x and alloc-free on NonBacktracking. Still: `(?:...)` for grouping-only, timeouts on untrusted patterns. (references/strings-text-search.md)
- Guard expensive log-argument computation with `IsEnabled` (CA1873) — `[LoggerMessage]`'s generated guard cannot protect call-site work. (references/runtime-aot-reflection-diagnostics.md)

**Native AOT & multi-targeting**
- Write idiomatic static constructors — preinit now handles `.AsSpan()`, cross-type statics, pointers. Set `<UseSizeOptimizedLinq>false</UseSizeOptimizedLinq>` if LINQ throughput matters; enable the HTTP/3 trim switch if unused. (references/runtime-aot-reflection-diagnostics.md)
- Polyfill statics like `ArgumentNullException.ThrowIfNull` via C# 14 static extension members for downlevel TFMs — cleaner call sites, better inlineability (~2500 BCL sites converted). (references/runtime-aot-reflection-diagnostics.md)

## New APIs worth adopting

| API | What it does | When to use |
|---|---|---|
| `[UnsafeAccessorType("...")]` | UnsafeAccessor reaches invisible types + static members | Hot private/cross-assembly access; replacing reflection shims |
| `Enumerable.Sequence<T>(start, endInclusive, step)` | Generic numeric series, rides `Range`'s specialized iterator | Numeric fills; `AddRange` targets (~40x vs manual loop) |
| `Enumerable.Shuffle()` | Randomize order; composed forms use reservoir sampling / closed-form math | Random sampling; delete hand-rolled shuffle helpers |
| `Enumerable.LeftJoin` / `RightJoin` | First-class outer joins | Replace GroupJoin+SelectMany+DefaultIfEmpty (~2x) |
| `IPAddress.IsValid` / `IsValidUtf8` | Validation without materializing the object | IP validity checks (alloc-free) |
| `Guid`/`Version`/`Char`/`Rune` : `IUtf8SpanParsable` | Parse directly from UTF-8 bytes | Byte-oriented pipelines; generic UTF-8 parse helpers |
| `JsonElement.Parse` | Standalone element, non-pooled backing | Element outlives a scope; kills clone-dance and pool leaks |
| `JsonObject.TryAdd` (+ index overloads) | Add-if-absent, one lookup | Hot JSON building |
| `JsonArray.RemoveAll` / `RemoveRange` | O(N) bulk removal | Any multi-element removal (~175x vs RemoveAt loop) |
| `Utf8JsonWriter.WriteStringValueSegment` / `WriteBase64StringSegment` | Chunked writes of one JSON string value | Large/lazily-produced strings and binary blobs |
| `JsonMarshal.GetRawUtf8PropertyName` | Zero-copy UTF-8 property names | Allocation-sensitive `JsonElement` traversal |
| `OrderedDictionary.TryAdd/TryGetValue(..., out int index)` | Index-returning lookups + `GetAt`/`SetAt` | Read-modify-write without double hashing |
| `CollectionsMarshal.AsBytes(BitArray)` | `Span<byte>` over live bit storage | Vectorized whole-array bit math |
| `ImmutableCollectionsMarshal.AsMemory(builder)` | `Memory<T>` over `ImmutableArray<T>.Builder` storage | Bulk populate/transform builders |
| `InlineArray2<T>`..`InlineArrayN<T>` | Shared public inline-buffer types | Named fixed-size buffers instead of hand-rolled `[InlineArray]` (compiler codegen didn't switch to them in .NET 10 — use them directly) |
| `MemoryExtensions` comparer overloads, `CountAny`, `ReplaceAny` | Span search without `IEquatable<T>` constraint | Unconstrained generic code; stays vectorized w/ default comparer |
| `TensorPrimitives` (+70 ops; stable `Tensor<T>`/`TensorSpan<T>`) | Vectorized elementwise/reduction math incl. `Half`, int, `nint` | Any numeric-buffer loop |
| `Random.GetString` / `GetHexString`; `GetItems` (newly fast) | Bulk random selection, rejection sampling | Token/ID generation, incl. crypto `RandomNumberGenerator` |
| `Volatile.ReadBarrier` / `WriteBarrier` | Standalone acquire/release half fences | Lock-free code only; replaces over-fencing `Thread.MemoryBarrier` |
| Unbuffered `Channel` factory | Rendezvous channel, zero buffering | Strict handoff/backpressure semantics |
| `GCHandle<T>` / `PinnedGCHandle<T>` / `WeakGCHandle<T>` | Strongly-typed GC handles | New interop code — safer and ~18% faster |
| `ZipArchive`/`ZipFile` async APIs | True async zip I/O | Servers; replaces `Task.Run`-wrapped sync zip code |
| `X509Certificate2Collection.FindByThumbprint`; `SymmetricAlgorithm.SetKey`; `ProtectedData` span overloads; `PemEncoding` UTF-8 | Span-based crypto plumbing | Alloc-free cert lookup, key setting, DPAPI, PEM-from-bytes |
| ML-KEM / ML-DSA / SLH-DSA (+Composite) | Post-quantum crypto, span-first design | PQC adoption; prefer span overloads |
| `Convert` UTF-8 hex overloads | Hex ⇄ UTF-8 bytes directly | Skip transcoding through `string`/`char` |
| `DefaultInterpolatedStringHandler.Text` | Read formatted text as `ReadOnlySpan<char>` | Custom handlers; avoid the terminal `string` alloc |
| `<UseSizeOptimizedLinq>false</UseSizeOptimizedLinq>` | Full speed-optimized LINQ on Native AOT | AOT apps where LINQ throughput beats binary size |

## Folklore to delete

- **"Never take `IEnumerable<T>` in hot paths — the enumerator allocates."** PGO → guarded devirt → escape analysis stack-allocates it for arrays, `List<T>`, `Stack`, `Queue`, `ConcurrentDictionary`.
- **Custom `ValueStopwatch` structs / `GetTimestamp` bookkeeping for zero-alloc timing.** `Stopwatch.StartNew()` is alloc-free via escape analysis.
- **"Avoid lambdas/`Func<>` — a delegate always allocates."** Non-escaping delegates whose consumer inlines are elided. (Captured-state display classes still allocate.)
- **"Never pass temporary `new T[]{...}` / `params` arrays in hot code."** Array stack allocation via escape analysis; spans remain the guarantee, not the only remedy.
- **Unsafe code (`Unsafe.Add`, pointers) to skip bounds checks on masked/shifted table indices.** Range analysis proves them; the BCL deleted its own unsafe versions and got faster.
- **Writing the highest array index first so one bounds check dominates.** Sequence cloning builds the guarded fast path from natural-order writes.
- **Reordering `&&` guards / hand-hoisting `Length` to appease the bounds-check eliminator.** Assertion merging is order-independent; `x[0]`/`x[^1]` is single-check.
- **"Spans don't get loop cloning — use `T[]` in the hottest loops."** Span loops clone like array loops now.
- **"Methods with `lock`/`using`/try-finally can't inline — flatten them."** try/finally no longer blocks inlining; provably-non-throwing try/catch is deleted outright.
- **Sprinkling `[MethodImpl(AggressiveInlining)]` to beat the inlining budget.** Budget >2x, local cap ~2x; budget-motivated annotations are mostly dead weight.
- **The `MoveNextRare` cold-split pattern ("keep the hot method thin").** Under PGO the split-out method goes cold, blocks inlining, and kills stack allocation — the BCL reverted it.
- **`switch`/`goto` state machines "for speed".** Irreducible loops defeat the JIT; a boring `while` loop was ~6.4x faster.
- **`if (cond) return true; return false;` "optimizes better than" `=> cond;`.** Normalized to identical codegen.
- **Avoiding by-value struct returns / object refs in ref structs over write-barrier cost.** Both barrier classes are elided now.
- **Hand-rolled bitmask membership instead of `is 'a' or 'b' or ...`.** The pattern form emits a bit test and is faster.
- **`for`+indexer vs `foreach` iteration folklore (both directions).** Array interface devirtualization restored expected ordering and sped up both.
- **Manually restructuring `OrderBy(...).Contains(x)` / `Distinct().Contains(x)` "because LINQ sorts first".** It doesn't anymore — ~50-350x via iterator specialization.
- **Pre-checking `tasks.Count == 1` before `Task.WhenAll`; materializing `IEnumerable<Task>` first.** `WhenAll` returns the lone task directly and no longer allocates a temp buffer.
- **"Channels leak when readers cancel a lot — recycle the channel."** Canceled waiters unlink immediately; working set is bounded.
- **Transcoding UTF-8 to chars before parsing `Guid`/`Version`/IPs; `Utf8Parser.TryParse` for GUIDs.** Direct UTF-8 `Parse` overloads are faster (keep `Utf8Parser` only for parse-off-the-front).
- **"`RandomNumberGenerator` is too slow for token generation."** Bulk rejection sampling made `GetItems` ~27x faster.
- **"Int division / `Half` math can't vectorize — write scalar loops"; converting `Half[]`→`float[]` for SIMD.** `TensorPrimitives` vectorizes via double/float smuggling internally.
- **Cached `FieldInfo`/`MethodInfo` as the ceiling for private access; reflection as the only way across assembly cycles.** `[UnsafeAccessor]`/`[UnsafeAccessorType]` is ~3.6x faster and BCL-sanctioned.
- **"Keep cctors trivial or Native AOT can't preinitialize"; "avoid generics under AOT for binary size".** Preinit accepts idiomatic code; identical generic bodies fold.
- **"Passing `EqualityComparer<T>.Default` explicitly kills vectorization."** Comparer overloads route default comparers to vectorized paths.
- **Hand-rolled char-scan loops in validators/encoders; caching dictionary literal keys in fields.** `SearchValues` beats them and inherits future SIMD; constant literal keys are now the specially-optimized dictionary path.
- **Memoizing `Type.AssemblyQualifiedName`; keeping `EventSource` IDs dense.** Runtime caches the former; a Dictionary replaced the dense-array hazard.
- **"`BufferedStream.WriteByte` over compression streams is pathological"; "rebuild the zip rather than Update mode"; "split concatenated gzip members yourself".** Hidden flush removed; Update is change-proportional; gzip resets native state across members.
- **Hand-written `(?>...)` atomic groups for regex perf; "avoid `(?:.|\n)`, alternation is slow".** Auto-atomicity covers the provable cases; negated-set merging collapsed the idiom. (Timeouts on untrusted patterns are still warranted.)
- **`Regex.Match(...).Success` for boolean checks; `Matches(...).Count` for counting; `IndexOfAny*(...) >= 0` for containment.** `IsMatch`/`Count`/`ContainsAny*` — now analyzer-enforced (CA1874/CA1875).
- **"Unsafe pointer code beats span code."** `Guid` "X" formatting got ~4x faster by *removing* pointers; safe span code is the optimizable code.
- **"`a += b` is just sugar for `a = a + b`."** C# 14 user-defined compound operators mutate in place — a perf tool and a correctness review item.
