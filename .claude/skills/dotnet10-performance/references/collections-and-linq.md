# .NET 10 Performance — Collections & LINQ

**TL;DR for this domain.** The boxed-enumerator tax on `IEnumerable<T>`-typed enumeration is dying: dynamic PGO → guarded devirtualization → inlining → escape analysis → stack allocation now routinely makes interface-typed `foreach` alloc-free, and the BCL rewrote its enumerators (`List`, `Stack`, `Queue`, `PriorityQueue`, `ConcurrentDictionary`) specifically so their members are small enough for that pipeline to fire. Inlineability is the gateway optimization — one non-inlined virtual call (even a nop `Dispose`) makes the whole enumerator escape to the heap, so micro-structure (branch count, code size, reducible control flow) now has macro consequences. LINQ operators increasingly see through each other: terminal operators (`Contains`, `First`, `Count`, `Take`) replace entire pipelines with algorithmically superior equivalents, so composed declarative LINQ can beat hand-rolled loops asymptotically. `(uint)i < (uint)len` remains the lingua franca for merging state checks and proving bounds. Zero-copy `CollectionsMarshal`/`ImmutableCollectionsMarshal` escape hatches turn per-element loops into vectorized one-liners.

## Contents

- [Enumerating through `IEnumerable<T>` (the boxed-enumerator story)](#enumerating-through-ienumerablet-the-boxed-enumerator-story)
- [Writing your own enumerators and hot structs](#writing-your-own-enumerators-and-hot-structs)
- [Looking things up: dictionaries, frozen collections, alternate lookups](#looking-things-up-dictionaries-frozen-collections-alternate-lookups)
- [Building and mutating collections](#building-and-mutating-collections)
- [Zero-copy access to backing storage](#zero-copy-access-to-backing-storage)
- [Composing LINQ queries](#composing-linq-queries)
- [LINQ on Native AOT](#linq-on-native-aot)
- [Folklore to delete](#folklore-to-delete)

---

## Enumerating through `IEnumerable<T>` (the boxed-enumerator story)

### Enumerator stack allocation for interface-typed collections
- **What changed:** When a collection is consumed through `IEnumerable<T>` (so `GetEnumerator()` must return a boxed/reference enumerator), the JIT now very frequently devirtualizes all four enumerator calls (`GetEnumerator`/`MoveNext`/`get_Current`/`Dispose`), inlines them, and stack-allocates the enumerator. Routine for `T[]` and `List<T>`. `IEnumerator<T>` is now marked `[Intrinsic]` (dotnet/runtime#116978) so the JIT can reason about it specially.
- **Mechanism:** Dynamic PGO observes the dominant concrete enumerator type → guarded devirtualization emits a type-checked fast path with direct calls → those become inlineable → once ALL uses inline, escape analysis proves the enumerator never escapes → heap allocation becomes stack allocation.
- **Magnitude:** `foreach` over `List<int>` typed as `IEnumerable<int>`: 40 B/op → 0 B; 5000-element sum ~4,767 ns → ~2,082 ns (~2.3x); 15,000-element ~2x.
- **Adoption:** FREE.
- **Guidance:** Stop contorting API shapes to avoid `IEnumerable<T>` parameters/returns out of enumerator-allocation fear. Direct `foreach` over the concrete type is still the guaranteed-fast path, but the polymorphic path no longer carries a mandatory allocation tax.
- **Caveats:** Requires tiered compilation with dynamic PGO having observed the call site (hot, tiered-up code); probabilistic JIT optimization, not a language guarantee. Other enumerator types benefit to the degree their members are small enough to inline (hence the per-type rewrites below).

### `List<T>` / `PriorityQueue<,>`: `MoveNextRare` folded back into `MoveNext`
- **What changed:** dotnet/runtime#118425 (and #118467 for `PriorityQueue`) undoes the ancient split of `MoveNext` into thin-`MoveNext` + separate `MoveNextRare` (version-mismatch/end handling), recombining into one simple method.
- **Mechanism:** The split existed to keep `MoveNext` inlineable — but under PGO, `MoveNextRare` runs exactly once per enumeration (the final `false`), so PGO classifies it cold at large N and refuses to inline it. One non-inlined enumerator call → enumerator escapes → no stack allocation. The recombined `MoveNext` inlines with or without PGO.
- **Magnitude:** Count=5000 `IEnumerable<int>` sum: 40 B / ~4,767 ns → 0 B / ~2,154 ns (~2.2x).
- **Adoption:** FREE.
- **Guidance:** The lesson generalizes — see [Writing your own enumerators](#writing-your-own-enumerators-and-hot-structs).

### OSR + `Dispose`: PGO instrumentation inference for enumerators
- **What changed:** dotnet/runtime#118461. Long loops trigger OSR compilation, and OSR bodies carry no PGO probes — so the loop-tail `Dispose()` call had no profile, guarded devirt failed, the (no-op!) interface `Dispose` made the enumerator escape, and large collections allocated where small ones didn't. The JIT now infers the missing tail instrumentation from probes on the other enumerator methods, specifically for enumerators.
- **Mechanism:** Escape analysis needs every use devirtualized+inlined; a single unprofiled virtual `Dispose` spilled the enumerator to the heap. Inferring the profile restores the fast path.
- **Magnitude:** Count=15000 `List<int>`: 40 B → 0 B, ~14,725 ns → ~6,525 ns (~2.3x).
- **Adoption:** FREE.
- **Caveats:** Enumerator-specific fix, not a general OSR+PGO repair. Remember: interface calls are escape points until proven otherwise — even nops.

### `Stack<T>` enumerator: 5 branches → 2 per element
- **What changed:** dotnet/runtime#117328 rewrote the enumerator (~half the lines deleted). Constructor sets index to the stack length; `MoveNext` decrements; exhaustion goes negative; one unsigned compare `if ((uint)index < (uint)array.Length)` folds the lazy-init/ended/remaining/bounds checks, leaving just version check + that one compare.
- **Mechanism:** Fewer branches, less code; crucially the smaller bodies cross the inlining threshold, making the boxed enumerator stack-allocatable via `IEnumerable<T>`.
- **Magnitude:** `Stack<int>` (10 elems): direct `foreach` ~23.3 ns → ~4.5 ns (~5x), code 331 B → 55 B; via `IEnumerable<int>` ~30.9 ns → ~7.9 ns and 40 B → 0 B.
- **Adoption:** FREE.

### `Queue<T>` enumerator: wraparound restructured so the wrap test proves bounds
- **What changed:** dotnet/runtime#117341. .NET 9 wrote `if (index >= array.Length) index -= array.Length; _current = array[index];` — two branches per element (wrap check + un-eliminable bounds check). .NET 10 inverts it:
  ```csharp
  if ((uint)index < (uint)array.Length) { _current = array[index]; }        // compare doubles as bounds proof
  else { index -= array.Length; _current = array[index]; }
  ```
  The pre-wrap segment pays exactly one branch; only the post-wrap segment still incurs a bounds check.
- **Mechanism:** Branch shape chosen so the dominating compare doubles as the JIT's bounds proof (flow-sensitive range-check elimination). Smaller body → inlineable → stack-allocatable. (`% length` was already avoided — integer division is costly.)
- **Magnitude:** `Queue<int>` (10 elems, wrapped): direct ~24.3 ns → ~7.2 ns, branches/op 79 → 37; via `IEnumerable<int>` ~30.7 ns → ~8.7 ns, 40 B → 0 B.
- **Adoption:** FREE.

### `ConcurrentDictionary<,>` enumerator: switch/goto state machine → plain `while` loop
- **What changed:** dotnet/runtime#116949 rewrote `MoveNext` from a `switch`/`goto case` state machine (StateUninitialized/StateOuterloop/StateInnerLoop) into ordinary nested `while` loops.
- **Mechanism:** The `goto case` web formed *irreducible loops* (multiple entry points) that compilers largely cannot analyze or optimize. The reducible `while` rewrite is streamlined, inlineable, and makes the enumerator a stack-allocation candidate.
- **Magnitude:** Enumerating 1000-entry `ConcurrentDictionary<int,int>`: ~4,233 ns → ~664 ns (~6.4x), 56 B → 0 B.
- **Adoption:** FREE.

---

## Writing your own enumerators and hot structs

Distilled from the BCL rewrites above — these apply to any code you write:

1. **Do NOT split cold tails into separate methods to "keep the hot method inlineable"** (the `MoveNextRare` pattern). Under dynamic PGO the split-out method gets classified cold, isn't inlined, and one non-inlined call blocks escape analysis for the whole containing object. Write the whole small method; measure before splitting.
   ```csharp
   // Anti-pattern (was folklore):
   public bool MoveNext()
   {
       if (_version == _list._version && (uint)_index < (uint)_list._size) { ...; return true; }
       return MoveNextRare(); // PGO marks this cold at large N → not inlined → enumerator escapes
   }
   // Write one simple, whole MoveNext instead.
   ```
2. **Never use `goto case` / multi-entry state machines in hot code.** Irreducible loops block JIT loop optimization and inlining. A structured `while` loop is both more maintainable and faster (~6x in `ConcurrentDictionary`'s case).
3. **The `(uint)index < (uint)array.Length` unsigned-compare idiom** remains canonical: it merges sign+bounds+state checks into one branch AND gives the JIT the proof that a subsequent `array[index]` needs no bounds check. Count-down-to-negative is a good enumerator shape for it.
4. **For ring-buffer/wraparound indexing,** structure the code so the wrap test IS the unsigned compare that proves in-bounds (the `Queue<T>` shape above) — bounds-check elimination on the common segment for free. Prefer compare+subtract over `%`.
5. **Keep enumerator member bodies small.** Inlineability is the gateway: stack allocation requires *every* use of the object to inline. Branch count and code size of `MoveNext`/`Current`/`Dispose` decide whether callers allocate.

---

## Looking things up: dictionaries, frozen collections, alternate lookups

### FrozenDictionary/FrozenSet: dense integral- and enum-key specializations
- **What changed:** dotnet/runtime#111886, #112298. `FrozenDictionary<TKey,TValue>`/`FrozenSet<T>` gained specialized implementations for any primitive integral `TKey` that is `int`-sized or smaller (`byte`, `char`, `short`, `ushort`, `sbyte`, ...) **and enums backed by such primitives** — previously only `string` and `Int32`.
- **Mechanism:** When key values are densely packed, the dictionary becomes a flat array indexed directly by the key's integer value — lookup is a bounds check + array index, no hash, no probe.
- **Magnitude:** ~2.4x (2.07 ns → 0.87 ns) for `FrozenDictionary<HttpStatusCode, string>` indexer get.
- **Adoption:** FREE if you already use frozen collections with integral/enum keys; PATTERN if you still use `Dictionary` for long-lived read-mostly enum-keyed maps — switch to `ToFrozenDictionary()`.
- **Guidance:** Stop hand-writing `switch` tables or enum-indexed arrays for lookup tables; the frozen specialization does it when keys are dense.
- **Caveats:** Array-backed strategy only chosen for dense key sets; sparse keys fall back. `long`/`ulong`-backed enums not covered.

### Frozen collections: `GetAlternateLookup` GVM cost amortized into a delegate
- **What changed:** dotnet/runtime#108732. The alternate-lookup path (e.g. `ReadOnlySpan<char>` lookups into a `string`-keyed `FrozenDictionary`) no longer executes a generic virtual method (GVM) per lookup; a GVM now returns a *delegate* once, and per-lookup calls are plain delegate invocations.
- **Mechanism:** GVMs are notoriously hard to optimize (slow dispatch, no devirtualization). Moving the GVM from per-call to per-acquisition amortizes it to one hit at `GetAlternateLookup` time.
- **Magnitude:** ~1.6x (133.46 ns → 81.39 ns) for 12 span-keyed lookups including acquisition.
- **Adoption:** FREE.
- **Guidance:** Use span-keyed alternate lookups freely to avoid allocating `string` lookup keys; the GVM tax that ate the gains in .NET 9 is largely gone. Hoist `GetAlternateLookup` out of tight loops — acquisition still pays the GVM cost once. API-design lesson: if you must expose a GVM, make it a "get me a worker delegate" factory, not the hot-path operation.

### `Dictionary<string, V>`: constant-string key lookups become branch-lean
- **What changed:** dotnet/runtime#117427. `TryGetValue`'s optimized path now calls string helpers the JIT already intrinsifies for constant arguments (e.g. `string.Equals`).
- **Mechanism:** `TryGetValue` is small and commonly inlined; a literal key flows as a JIT-visible constant into those helpers, which specialize (constant-length compare, unrolled/vectorized equality). Classic constant-propagation-through-inlining, unlocked just by choosing JIT-known helpers.
- **Magnitude:** ~2.4x (33.81 ns → 14.02 ns) for five constant-key indexer gets.
- **Adoption:** FREE.
- **Guidance:** `dict["literal"]` / `TryGetValue("literal", ...)` is now the fast path. Do NOT cache literal keys in locals/fields "to help the dictionary" — that hides the constant from the JIT and defeats the optimization. Don't replace string-keyed dictionaries with switch-on-string for speed at small scale without measuring; the gap has narrowed sharply.
- **Caveats:** Requires a JIT-visible constant key (literal or `const`) and the lookup to inline; runtime-computed keys see no change.

### `OrderedDictionary<,>`: index-returning `TryAdd`/`TryGetValue`
- **What changed:** dotnet/runtime#109324 adds `TryAdd(key, value, out int index)` and `TryGetValue(key, out value, out int index)`; the index works with `SetAt(index, value)`/`GetAt(index)`.
- **Mechanism:** Composite read-modify-write operations previously hashed+compared twice; the returned index lets the second step address the slot directly, skipping the second keyed lookup.
- **Magnitude:** ~1.7x (6.961 ns → 4.201 ns) for an AddOrUpdate via `TryGetValue(..., out index)` + `SetAt`.
- **Adoption:** New API.
  ```csharp
  // Before: two keyed lookups
  if (d.TryGetValue(key, out int existing)) d[key] = update(key, existing);
  else d.Add(key, add(key));
  // After: second op addresses the slot by index
  if (d.TryGetValue(key, out int existing, out int index)) d.SetAt(index, update(key, existing));
  else d.Add(key, add(key));
  ```
- **Guidance:** Analogous in spirit to `CollectionsMarshal.GetValueRefOrAddDefault` for `Dictionary`.
- **Caveats:** The index is only valid until the next structural modification (inserts/removals shift indices).

---

## Building and mutating collections

### `List<T>.InsertRange`: no more double-copy on growth
- **What changed:** dotnet/runtime#107683. When an insert forces growth, the list no longer copies all elements to the new array and *then* shifts to make room; it copies the below/above segments directly to their final positions, then fills the gap.
- **Mechanism:** Eliminates redundant element copies — worst case (insert at 0) previously copied every element twice.
- **Magnitude:** ~1.6x (48.65 ns → 30.07 ns) for `AddRange` + `InsertRange(0, ...)` on a small list.
- **Adoption:** FREE.
- **Caveats:** Only the growth path; steady-state inserts into non-full lists are still O(n) shifts, as ever.

### `ConcurrentDictionary<,>.Clear` preserves the constructed capacity
- **What changed:** dotnet/runtime#108065. `Clear()` must allocate a fresh bucket array (lock-free readers); it previously reset to *default* size, now it re-allocates at the capacity passed to the constructor.
- **Mechanism:** Avoids the O(log n) re-growth cascade (repeated allocations + rehash/relink) when refilling to previous size.
- **Magnitude:** Clear + re-add 1024 items: ~1.7x faster (51.95 us → 30.32 us), ~2.8x less allocation (134.36 KB → 48.73 KB).
- **Adoption:** FREE — but only if you pass `capacity` to the constructor.
- **Guidance:** If you cyclically `Clear()`+refill a `ConcurrentDictionary`, construct it with explicit `capacity` (and `concurrencyLevel`); the hint now survives `Clear`. Presize concurrent collections you reuse.
- **Caveats:** Retention comes from the constructor argument, NOT the high-water mark of items actually added.

### Filling numeric sequences: `Enumerable.Sequence<T>` + `AddRange` instead of manual loops
- **What changed:** New API `Enumerable.Sequence<T>(T start, T endInclusive, T step) where T : INumber<T>` — like `Range` but for any `INumber<T>`, arbitrary/negative steps, wrapping permitted at T's min/max. When possible (e.g. step == 1) it delegates to `Range`'s internal implementation (now generic internally), inheriting all of `Range`'s iterator specializations (vectorized fill, optimized `ToList`/`ToArray`/`AddRange`).
- **Mechanism:** `List<T>.AddRange(Enumerable.Sequence(...))` fills via the specialized (potentially vectorized) range path instead of per-element `Add` with growth/version checks.
- **Magnitude:** Filling `List<short>` with 1001 values: manual `for`+`Add` ~1,480 ns vs `AddRange(Enumerable.Sequence<short>(42, 1042, 1))` ~37 ns (~40x).
- **Adoption:** New API.
  ```csharp
  // Before:
  for (short i = 42; i <= 1042; i++) values.Add(i);
  // After (~40x on this shape):
  values.AddRange(Enumerable.Sequence<short>(42, 1042, 1));
  ```
- **Caveats:** Step-1-style shapes ride `Range`'s fast paths; note `Sequence` takes endInclusive, `Range` takes (start, count).

---

## Zero-copy access to backing storage

### `CollectionsMarshal.AsBytes(BitArray)` + `byte[]` backing
- **What changed:** dotnet/runtime#116308 adds `CollectionsMarshal.AsBytes(BitArray)` returning a `Span<byte>` over the live backing storage; `BitArray` is now backed by `byte[]` (was `int[]`); the `byte[]` constructor now uses vectorized copies.
- **Mechanism:** Removes the old dichotomy of per-bit indexer access (multiple instructions per bit) vs. `CopyTo` into a rented/allocated temp (allocation + memcpy). The raw span feeds any vectorized algorithm with zero copies, and enables in-place mutation.
- **Magnitude:** Hamming distance over 1024 bits: ~20x (1,256.72 ns per-bit loop → 63.29 ns via `AsBytes` + `TensorPrimitives.HammingBitDistance`). `new BitArray(byte[])`: ~2x (160.10 ns → 83.07 ns).
- **Adoption:** New API (`AsBytes`); the constructor speedup is FREE.
  ```csharp
  // Before: per-bit loop
  long distance = 0;
  for (int i = 0; i < bits1.Length; i++)
      if (bits1[i] != bits2[i]) distance++;
  // After: zero-copy span + vectorized primitive
  long distance = TensorPrimitives.HammingBitDistance(
      CollectionsMarshal.AsBytes(bits1), CollectionsMarshal.AsBytes(bits2));
  ```
- **Guidance:** For any whole-array bit manipulation, get the span and use vectorized helpers (`TensorPrimitives`, `BitOperations.PopCount` over chunks). Delete per-bit loops and CopyTo-into-temp patterns.
- **Caveats:** Marshal-class API — exposes live storage; writes bypass `BitArray` invariants; trailing bits in the last byte beyond `Length` are implementation detail. `TensorPrimitives.HammingBitDistance` needs the `System.Numerics.Tensors` package.

### `ImmutableCollectionsMarshal.AsMemory(ImmutableArray<T>.Builder)`
- **What changed:** dotnet/runtime#112177 adds `AsMemory` over the builder's underlying storage (complementing the existing `AsArray` for `ImmutableArray<T>` itself).
- **Mechanism:** Zero-copy access to the builder's backing store — enables span/memory-based bulk processing (vectorized fills, writes from I/O) without materializing or copying.
- **Magnitude:** No benchmark given; win is avoided copies/allocations in builder-heavy code.
- **Adoption:** New API.
- **Guidance:** When populating or transforming a builder in bulk, get the `Memory<T>` and use span algorithms instead of per-element builder indexing.
- **Caveats:** Marshal-class API — aliases live builder storage; structural changes to the builder invalidate assumptions.

---

## Composing LINQ queries

The design underneath all of these: LINQ's internal concrete iterator types (`ArraySelectIterator<,>`, `IListSkipTakeSelectIterator<,>`, ...) carry source/selector/bounds state between operators, so a terminal operator can see through the pipeline to the producer and substitute a better algorithm. Consequence: returning lazily-composed LINQ from a producer is fine — the consumer's terminal operator triggers cross-operator optimizations your local code can't.

### `Contains` specialized across ~30 iterator types
- **What changed:** dotnet/runtime#112684 extends the iterator-specialization machinery to `Contains`. Order-irrelevant or work-skippable predecessors — `OrderBy`, `Distinct`, `Reverse`, `Union`, `Append`, `Concat`, `DefaultIfEmpty`, `SelectMany`, `Where.Select`, ... — now answer `Contains` by searching the underlying source directly instead of executing the pipeline.
- **Mechanism:** `Contains` doesn't care about order or duplication, so sorts/reversals/dedup buffers are skipped entirely; concrete iterator types avoid per-element interface dispatch.
- **Magnitude (1000-element array source):** `OrderBy.Contains` ~12,884 ns → ~50 ns (~257x, 12,280 B → 88 B); `Distinct.Contains` ~363x; `Union.Contains` ~304x; `Reverse.Contains` ~9x; `Append/Concat/SelectMany + Contains` ~50-60x; `Where.Select.Contains` ~7x; `DefaultIfEmpty.Contains` ~16%.
- **Adoption:** FREE.
- **Guidance:** Don't manually restructure `query.OrderBy(...).Contains(x)` / `Distinct().Contains(x)` "because LINQ will sort/dedup first" — it won't anymore. Composed LINQ ending in `Contains` is near raw-scan cost (same family as the pre-existing `OrderBy(...).First()` O(N) special case).
- **Caveats:** In-memory LINQ-to-Objects only; on Native AOT depends on the size/speed LINQ build (see below).

### `Shuffle` with `First`/`Count`/`Contains`/`Take`/`Take.Contains` specializations
- **What changed:** dotnet/runtime#112173 adds `Enumerable.Shuffle()` (`Random.Shared` semantics). Worst case it buffers like `OrderBy`, but composed forms substitute statistically equivalent algorithms: `Shuffle().First()` = random `IList<T>` pick; `Shuffle().Count()` = source count; `Shuffle().Contains(x)` = contains on the source; `Shuffle().Take(N)` = single-pass **reservoir sampling** (one pass, O(N) memory, uniform, no full buffer); `Shuffle().Take(N).Contains(x)` = closed-form **hypergeometric** draw — compute P(zero matches) and compare one `Random.Shared.NextDouble()` against it; no shuffle materialization at all.
- **Magnitude (vs hand-rolled `ToArray`+`Random.Shared.Shuffle`+yield, 1000 elems):** `Shuffle().Take(10).ToList()` allocations 4,232 B → 192 B (~22x less); `Shuffle().Take(10).Contains(...)` ~3,901 ns → ~79 ns (~49x).
- **Adoption:** New API.
- **Guidance:** Delete hand-rolled shuffle helpers (`ToArray` + `Random.Shared.Shuffle` + iterate, or `OrderBy(_ => Guid.NewGuid())` abominations). Compose freely — `Shuffle().Take(N)` is the right random-sampling primitive.
- **Caveats:** Full-sequence `Shuffle` enumeration still buffers the source (inherent); wins come from composed terminal operators.

### `LeftJoin` / `RightJoin`
- **What changed:** dotnet/runtime#110872 adds first-class `Enumerable.LeftJoin`/`RightJoin` (outer joins), implemented directly rather than via the `GroupJoin` + `SelectMany` + `DefaultIfEmpty` composition.
- **Mechanism:** Dedicated implementation avoids the intermediate group objects, tuple projections, and extra operator layers.
- **Magnitude:** ~2x time (29.02 us → 15.23 us) and ~2x allocation (65.84 KB → 36.95 KB) vs the manual composition.
- **Adoption:** New API.
  ```csharp
  // Before (folklore idiom):
  outer.GroupJoin(inner, ok, ik, (o, inners) => (o, inners))
       .SelectMany(x => x.inners.DefaultIfEmpty(), (x, i) => result(x.o, i));
  // After:
  outer.LeftJoin(inner, ok, ik, (o, i) => result(o, i)); // RightJoin likewise
  ```

### `Skip`/`Take` + `ToArray`/`ToList`: span-sliced bulk copy
- **What changed:** dotnet/runtime#112401. The Skip/Take iterator's `ToArray`/`ToList` check whether the source can cheaply yield a `ReadOnlySpan<T>` (it's a `T[]` or `List<T>`); if so they slice and `CopyTo` instead of element-by-element copying.
- **Mechanism:** Bulk (potentially vectorized) memory copy replaces a per-element enumeration loop.
- **Magnitude:** `source.Skip(200).Take(200).ToList()` over a 1000-element `string[]`: ~1,219 ns → ~257 ns (~4.7x).
- **Adoption:** FREE.
- **Guidance:** `array/list.Skip(a).Take(b).ToList()/ToArray()` is now effectively a slice-copy — no need to hand-roll `GetRange` or `AsSpan(a, b).ToArray()` for this shape.
- **Caveats:** Fast path only when the ultimate source is `T[]` or `List<T>`.

---

## LINQ on Native AOT

### `UseSizeOptimizedLinq` feature switch
- **What changed:** `System.Linq.dll` historically shipped two build-time flavors — speed-optimized (all iterator specializations; coreclr) and size-optimized (specializations removed to help trimming; Native AOT) — with no user choice. dotnet/runtime#111743 + #109978 make it a runtime feature switch: `<UseSizeOptimizedLinq>false</UseSizeOptimizedLinq>` in the project file gets the full speed-optimized LINQ on Native AOT.
- **Mechanism:** The specializations are virtual overrides on the internal `Iterator<T>` base; under Native AOT any use of e.g. `Enumerable.Contains` keeps ALL overrides alive, inflating binary size — hence the historical size build. The switch hands the size/speed tradeoff to the app author.
- **Adoption:** MSBuild property.
- **Guidance:** If you publish Native AOT and LINQ throughput matters, set it to `false`; unlocks the ~2x–300x specializations above at binary-size cost. Don't conclude "LINQ is slow on AOT" from old measurements without checking this switch.

### Size-optimized LINQ regains the high-value specializations
- **What changed:** dotnet/runtime#118156. The original size build removed ALL specialized overrides indiscriminately; many removals saved almost no size while costing large throughput. The impactful specializations are reinstated where throughput gains significantly outweigh minimal size cost.
- **Adoption:** FREE for Native AOT users.
- **Guidance:** Default .NET 10 Native AOT LINQ is much closer to coreclr LINQ; try the default before reaching for the switch.
- **Caveats:** Still not the complete set; the switch remains for full speed.

---

### Folklore to delete

- **"Never take/return `IEnumerable<T>` in hot paths — the enumerator allocates."** On .NET 10, for arrays, `List<T>`, `Stack<T>`, `Queue<T>`, `ConcurrentDictionary<,>` and friends in tiered/PGO'd code, the enumerator is devirtualized and stack-allocated. Stop distorting API shapes over this (exposing struct enumerators is still fine; just stop fearing the interface).
- **"Split the cold tail into a separate method so the hot method stays inlineable" (the `MoveNextRare` pattern).** Under dynamic PGO this backfires: the split-out method is classified cold, not inlined, and blocks escape analysis. The runtime itself deleted this pattern from `List<T>` and `PriorityQueue<,>`.
- **"Hand-rolled switch/goto state machines are faster than plain loops."** Opposite: `goto case` webs create irreducible loops the JIT can't optimize or inline. `ConcurrentDictionary`'s enumerator got ~6x faster by becoming a boring `while` loop.
- **"Rewrite `query.OrderBy(...).Contains(x)` / `Distinct().Contains(x)` manually or LINQ will sort/dedup first."** `Contains` is specialized behind ~30 iterator types; the composed form is ~50–350x faster than before and near raw-scan cost.
- **Hand-rolled shuffle helpers** (`ToArray` + `Random.Shared.Shuffle` + yield, `OrderBy(_ => rng.Next())`): superseded by `Enumerable.Shuffle()`, whose composed forms (reservoir sampling for `Take`, closed-form hypergeometric for `Take.Contains`) beat any local implementation.
- **The `GroupJoin` + `SelectMany` + `DefaultIfEmpty` outer-join idiom:** replaced by first-class `LeftJoin`/`RightJoin`, ~2x faster and ~2x fewer allocations.
- **Manual `for`-loop fills of numeric sequences into lists:** `list.AddRange(Enumerable.Sequence<T>(...))` (or `Range`) is ~40x faster via the specialized, vectorizable range iterator.
- **Manual slicing instead of `Skip(a).Take(b).ToList()/ToArray()`:** when the source is `T[]`/`List<T>`, LINQ does the span slice + bulk `CopyTo` itself (~4.7x).
- **"LINQ on Native AOT is permanently de-optimized."** It's the `UseSizeOptimizedLinq` feature switch now (set `false` for full speed), and the default build regained the high-value specializations anyway.
- **`% length` for ring-buffer wraparound:** compare-and-subtract, shaped so the compare doubles as the bounds proof, beats division and eliminates bounds checks on the non-wrapped segment.
- **"Hand-roll a switch or lookup array instead of a dictionary for enum keys."** For long-lived read-mostly maps, `FrozenDictionary` with a dense enum/integral key *is* an array lookup (~0.9 ns).
- **"Alternate lookups on frozen collections aren't worth it because of GVM overhead."** The per-call GVM cost is gone; span-keyed lookups into string-keyed frozen collections are a win again.
- **"Cache dictionary string literals in static fields / avoid repeated literal-key lookups."** Constant-string keys are now the *specially optimized* path through `TryGetValue`; the literal IS the optimization — hoisting it hides the constant from the JIT.
- **"Never insert at the front of a `List<T>` during construction; build backwards instead."** The growth-path double copy is fixed (non-growth inserts remain O(n) shifts, as ever).
- **"`Clear()` on a presized `ConcurrentDictionary` throws away your capacity, so recreate the dictionary instead."** `Clear` now retains the constructor-requested capacity.
- **"Avoid `BitArray` for anything performance-sensitive; copy bits out to an array first."** `CollectionsMarshal.AsBytes` gives direct vectorizable access; delete the per-bit-indexer and CopyTo-to-temp workarounds.
