# .NET 10 — Runtime, Native AOT, Reflection & Diagnostics

**TL;DR for this domain.** The runtime keeps absorbing hand-optimizations: native C helpers become inlinable C# IL, FCALLs become plain QCALLs, funclet scaffolding gets cheaper, stubs share one physical code page — ordinary constructs (`try/finally`, casts, exceptions-adjacent paths) tax you less for free. Reflection splits into two jobs: *discovery* stays on `System.Reflection`; *access* moves to `[UnsafeAccessor]`/`[UnsafeAccessorType]`, which the runtime fixes up to direct member access (~3.6x faster than cached `FieldInfo`, zero boxing) — the BCL itself now does this. Native AOT's fight is binary size, won by removing artificial reflection roots, folding identical generic bodies, and letting the build-time interpreter preinitialize more idiomatic static constructors. Diagnostics primitives (`Stopwatch`, `EventSource`, `ActivitySource`, logging) shed allocations and locks so instrumented code costs closer to uninstrumented code. Upgrading the TFM is itself a performance strategy; measure with BenchmarkDotNet across `--runtimes net9.0 net10.0` before hand-tuning anything below.

## Contents

- [Shrinking and speeding Native AOT binaries](#shrinking-and-speeding-native-aot-binaries)
- [Accessing non-public or cross-assembly members fast](#accessing-non-public-or-cross-assembly-members-fast)
- [Reflection & metadata costs that just dropped](#reflection--metadata-costs-that-just-dropped)
- [Runtime/VM baseline costs (all free)](#runtimevm-baseline-costs-all-free)
- [GC and GC handles](#gc-and-gc-handles)
- [Making diagnostics, tracing, and logging cheap](#making-diagnostics-tracing-and-logging-cheap)
- [Peanut-butter patterns worth copying](#peanut-butter-patterns-worth-copying)
- [Folklore to delete](#folklore-to-delete)

---

## Shrinking and speeding Native AOT binaries

All items in this section: **FREE** (recompile with the .NET 10 Native AOT toolchain), Native AOT only.

### Build-time preinitialization accepts idiomatic static constructors
- **What:** The AOT toolchain interprets (allow-listed) static-constructor code at build time and bakes resulting `static readonly` contents into the binary. dotnet/runtime#107575 lets it handle spans sourced from arrays (`.AsSpan()` no longer bails out); #114374 adds access to *other* types' static fields, calls to methods on types with their own cctors, and pointer dereferences.
- **Mechanism:** Build-time interpretation replaces run-time computation with baked data; the cctor (and everything only it referenced) becomes dead code the whole-program analysis trims — smaller binary, no cctor-check overhead.
- **Guidance:** Initialize lookup tables/constants in static constructors using natural code. Don't contort cctors into "preinit-friendly" shapes for the old stricter interpreter.
- **Caveat:** Preinit runs against an allow list — I/O, environment reads, arbitrary code still fall back to run-time init.

### Generic method bodies fold to one copy
- **What:** dotnet/runtime#117411 folds byte-identical compiled bodies of different generic instantiations of the same method into one copy; #117080 improves general method-body deduplication.
- **Mechanism:** Instantiations over reference types / same-layout types compile identically; emitting one body removes binary duplication.
- **Guidance:** Generic-heavy code is cheaper to ship under AOT. Don't de-genericize purely for binary size.
- **Caveat:** Only actually-identical bodies fold; struct instantiations with distinct layouts still get distinct code.

### Artificial reflection roots removed (enumerators, `CreateSpan`, boxed enums)
- **What:** #117345 stops reflection internals force-preserving *all* enumerators of *all* generic collection instantiations. #118718 removes enum boxing in `Thread`/`GC`/`CultureInfo`. #118832 fixes `RuntimeHelpers.CreateSpan` (emitted by the compiler for collection expressions / span constants) whose generic parameter was treated as "reflected-on," which pinned boxed-enum reflection support — even hello-world console apps couldn't trim it (via `System.Console`'s enum use). #112782 extends the "visible to user code?" metadata distinction to generic methods so non-user-visible metadata is stripped.
- **Mechanism:** The whole-program analyzer must keep anything reflection might touch; one framework-internal root can pin a combinatorial family of generic support code. Removing false roots lets trimming do its job.
- **Magnitude:** Not quantified; qualitative binary-size wins down to hello-world size.
- **Guidance (generalizable):** In AOT-facing libraries, boxing an enum or handing a generic type parameter to anything the analyzer treats as reflection has a *binary size* cost beyond the allocation. When auditing your own AOT size, hunt reflection roots over generic types.

---

## Accessing non-public or cross-assembly members fast

### `[UnsafeAccessor]` + new `[UnsafeAccessorType]` — reflection-speed problem solved
- **What:** dotnet/runtime#114881 adds `[UnsafeAccessorType("Fully.Qualified.TypeName, AssemblyName")]`, closing the last `[UnsafeAccessor]` gaps (attribute since .NET 8, generics since 9): targets whose parameter types aren't visible to your assembly, and *static* members on invisible declaring types. Type such parameters as `object` and adorn with the attribute.
- **Mechanism:** The runtime fixes up `[UnsafeAccessor]` extern methods to access the target member *directly* — same generated code as a normal member access. No reflection dispatch, no `object[]` boxing, JIT-optimizable.
- **Magnitude:** Private field `List<int>._items`: cached `FieldInfo.GetValue` 2.6397 ns vs `[UnsafeAccessor]` 0.7300 ns — **~3.6x**, and the reflection path also boxes.
- **Adoption:** New API.
- **Pattern:**
  ```csharp
  // Before: cached reflection
  private static readonly FieldInfo _itemsField =
      typeof(List<int>).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!;
  int[] items = (int[])_itemsField.GetValue(list)!;

  // After: direct access, ~3.6x faster, no boxing; generics via a generic accessor class
  private static class Accessors<T>
  {
      [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
      public static extern ref T[] GetItems(List<T> list);
  }
  int[] items = Accessors<int>.GetItems(list);

  // .NET 10: invisible target types / static members
  [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SomeMethod")]
  static extern void Call([UnsafeAccessorType("Some.Internal.Type, SomeAssembly")] object target);
  ```
- **Caveats:** You're binding to private implementation details — version fragility is yours. `[UnsafeAccessorType]` parameters are typed `object`; the string must be the fully-qualified type name.

### The BCL now eats its own dog food
- **What:** dotnet/runtime#115583 replaces most cross-library reflection *inside .NET itself* with `[UnsafeAccessor]`/`[UnsafeAccessorType]`. Flagship: `System.Security.Cryptography` needs `System.Net.Http` for OCSP revocation downloads but can't reference it (HTTP sits above crypto); the reflection bridge is now UnsafeAccessor.
- **Adoption:** FREE (framework-internal), and a **PATTERN** to copy: if your codebase uses reflection to break an assembly-reference cycle or call another assembly's internals, mirror the BCL.
- **Magnitude:** Per-call reflection overhead removed on those paths (order of the 2.64→0.73 ns comparison); TLS OCSP fetching gets cheaper for free.

---

## Reflection & metadata costs that just dropped

All FREE unless noted.

### `Type.AssemblyQualifiedName` is cached
- **What:** dotnet/runtime#118389 caches the value, which was previously recomputed and reallocated on every access.
- **Magnitude:** 132.3 ns / 712 B → **1.2 ns / 0 B** per repeated access (>100x).
- **Guidance:** Delete your own memoization caches of `AssemblyQualifiedName`.

### `TypeName` parsing/rendering allocates less
- **What:** dotnet/runtime#112350 reduces overhead and allocations in `System.Reflection.Metadata.TypeName` parse/render (`TypeName.Parse(...).FullName`).
- **Magnitude:** Gnarly nested generic/array name: 5.930 µs → 4.305 µs (~27%), allocations 12.25 KB → 5.75 KB (~53% less).
- **Guidance:** `TypeName` remains the right API for type-name round-tripping (serializers, trimmer-safe resolution) — not hand-rolled string slicing.

### `ActivatorUtilities.CreateFactory` drops a delegate layer
- **What:** dotnet/runtime#105814: `CreateFactory` was creating a `Func<...>` then wrapping an `ObjectFactory` around its `Invoke`; now creates the `ObjectFactory` directly.
- **Mechanism:** Every invocation paid one extra delegate indirection; the wrapper is gone.
- **Magnitude:** ~18% per instantiation (8.136 ns → 6.676 ns).
- **Caveat/adoption:** Ships in `Microsoft.Extensions.DependencyInjection.Abstractions` 10.x — needs the package update, not just the runtime.
- **Generalizable:** `new DelegateB(funcA.Invoke)` costs a call per invocation. Construct the final delegate type directly.

### `DebugDirectoryBuilder` streams deflate straight into `BlobBuilder`
- **What:** dotnet/runtime#113803: PDB embedding previously compressed into an intermediate `MemoryStream` then copied; now `DeflateStream` wraps the `BlobBuilder` destination directly.
- **Generalizable pattern:** never buffer a compression stream's output just to copy it somewhere else — wrap the destination:
  ```csharp
  // Before: compress → MemoryStream → copy. After:
  using (var deflate = new DeflateStream(destinationStream, CompressionLevel.Optimal, leaveOpen: true))
      deflate.Write(data);
  ```
- **Caveat:** Requires a destination that accepts incremental writes.

---

## Runtime/VM baseline costs (all free)

### Unboxing helpers rewritten from C to C#
- **What:** dotnet/runtime#108167, #109135 port runtime unboxing helpers (e.g. `(T?)o` to `Nullable<T>` in generic code) from native C to C# `CastHelpers.Unbox_Nullable` in CoreLib.
- **Mechanism:** Managed helpers skip the native↔managed transition and, being plain IL, get JIT-optimized/inlined in caller context. Part of the multi-release "port runtime magic to C# so the JIT can see through it" trend.
- **Magnitude:** ~15% on a contrived benchmark (1.626 → 1.379 ns).
- **Caveat:** These specific helpers are hit only in obscure generic/`Nullable<T>` unboxing; don't expect visible wins in typical code.

### FCALL → QCALL migration completed
- **What:** Many PRs (#107218 Exception/GC/Thread, #106497 object, #107152 profiler attach, #108415/#108535 reflection, a dozen more): all FCALLs that touched managed memory or threw exceptions are gone, converted to QCALLs (plain P/Invokes into the runtime).
- **Mechanism:** QCALLs avoid helper method frames (setup/teardown) and are less error-prone.
- **Guidance:** Lower baseline cost for core primitives (reflection, exceptions, GC interactions). Nothing to do.

### Cheaper catch/finally funclets on x64
- **What:** dotnet/runtime#115284 changes the funclet contract so the runtime preserves non-volatile registers instead of every funclet saving/restoring them in its own prolog/epilog.
- **Mechanism:** Smaller JIT-emitted funclets; cheaper entry/exit of exception handlers.
- **Guidance:** One less reason to distort code to avoid `finally`. **Throwing is still expensive** — this is handler scaffolding, not throw cost.
- **Caveat:** x64-specific.

### Shared read-only template page for runtime stubs
- **What:** dotnet/runtime#114462: small executable stubs (jump points, call counters, patchable trampolines) now map one shared read-only code page everywhere; each allocation gets only its own writable data page. Previously every stub regenerated the same instructions.
- **Mechanism:** Hundreds of virtual stub pages alias one physical page — less physical memory, less startup work, better i-cache locality. Wins grow with app size (many types / virtual dispatch sites).

### Mono interpreter + Wasm vectorization
- **What:** Interpreter opcode optimizations (switches #107423, new arrays #107430, memory barriers #107325); a dozen-plus PRs enabling Wasm SIMD in the interpreter (shifts #114669, `Abs`/`Divide`/`Truncate` #113743); `PackedSimd` in `Convert` hex/base64 workhorses (#115062).
- **Guidance:** Blazor/Wasm and Mono-interpreter workloads inherit vectorization already present on other architectures. Free.

---

## GC and GC handles

### DATAS tuned; re-measure if you opted out
- **What:** dotnet/runtime#105545 tunes DATAS (Dynamic Adaptation To Application Sizes, default since .NET 9): cuts unnecessary work, smooths pauses under high allocation rates, fixes fragmentation accounting that caused extra gen1 collections. #118762 adds config knobs, notably Gen0 growth behavior.
- **Adoption:** FREE (tuning) + config knobs.
- **Guidance:** If you disabled DATAS on .NET 9 for pause/throughput regressions, **re-measure on .NET 10 before keeping the opt-out**.

### Strongly-typed GC handles: `GCHandle<T>`, `PinnedGCHandle<T>`, `WeakGCHandle<T>`
- **What:** dotnet/runtime#111307 introduces typed handle structs replacing untyped `GCHandle` — safer by construction and lower overhead.
- **Magnitude:** Pinned alloc+free: 27.80 ns (`GCHandle.Alloc(...).Free()`) → 22.73 ns (`new PinnedGCHandle<byte[]>(...).Dispose()`), ~18%.
- **Adoption:** New API.
- **Pattern:**
  ```csharp
  // Before:
  GCHandle h = GCHandle.Alloc(_array, GCHandleType.Pinned);
  try { /* h.AddrOfPinnedObject() */ } finally { h.Free(); }
  // After:
  using PinnedGCHandle<byte[]> h = new(_array);
  ```

---

## Making diagnostics, tracing, and logging cheap

### `Stopwatch.StartNew()` is now allocation-free
- **What:** dotnet/runtime#111834 makes `StartNew`/`Stop`/`Elapsed` fully inlineable; JIT escape analysis then sees the instance never escapes and stack-allocates it.
- **Magnitude:** `StartNew`/`Stop`/`Elapsed`: 38.62 ns + 40 B → **28.21 ns + 0 B** — identical to manual `GetTimestamp` bookkeeping (28.32 ns, same 130 B code size).
- **Adoption:** FREE.
- **Guidance:** DELETE custom `ValueStopwatch` structs. Write the natural idiom:
  ```csharp
  // Workaround (no longer necessary):
  long start = Stopwatch.GetTimestamp();
  DoWork();
  TimeSpan elapsed = Stopwatch.GetElapsedTime(start, Stopwatch.GetTimestamp());
  // .NET 10 — same performance, zero alloc:
  Stopwatch sw = Stopwatch.StartNew();
  DoWork();
  sw.Stop();
  TimeSpan elapsed = sw.Elapsed;
  ```
- **Caveat:** Requires the instance to be provably non-escaping (don't store it in a field or pass it to non-inlined methods) and inlining to succeed.

### `EventSource`: sparse event IDs no longer a working-set hazard
- **What:** dotnet/runtime#117031 replaces the dense array indexed by event ID (a huge ID like 12_345_678 — seen in real ID-range-partitioned projects — forced an array that long, alive for the process lifetime) with `Dictionary<TKey,TValue>`, now fast enough for per-write lookup.
- **Magnitude:** Contrived new-instance-per-event benchmark: 1,876 µs / ~1.13 GB → 22 µs / 19.21 KB. Realistically: one large lifetime allocation + working set eliminated per sparse-ID EventSource.
- **Adoption:** FREE.
- **Guidance:** Keep exactly one `static readonly` instance per `EventSource`-derived type; ID density no longer matters.

### `ActivitySource` listener notification: lock → immutable array
- **What:** dotnet/runtime#107333: the listener list was lock-protected on every `Activity` start/stop notify; now it's an immutable array copied on the rare add/remove, iterated lock-free.
- **Mechanism:** Copy-on-write for read-mostly data — cost moves to rare mutation, contention on the hot notify path vanishes.
- **Adoption:** FREE.
- **Guidance:** Copy-on-write immutable-array-of-subscribers is the right shape for read-hot/write-rare listener lists in your own code.

### Logging: sealed `NullLogger`, null-logger exclusion, CA1873
- **What:** dotnet/runtime#117334 makes `LoggerFactory.CreateLoggers` exclude null loggers entirely; #117342 seals `NullLogger` so `logger is NullLogger` JITs to a cheap exact-type comparison; new analyzer **CA1873** flags call sites computing expensive arguments (interpolation, `Guid.NewGuid()`) eagerly before the `IsEnabled` check can reject them.
- **Adoption:** FREE (runtime bits) + **PATTERN**: the source-generated `[LoggerMessage]` `IsEnabled` guard cannot protect argument *computation* at the call site — guard expensive argument construction with `logger.IsEnabled(...)` yourself, or pass cheap raw values and let the template format.
- **Guidance:** Seal your own types where possible — recurring theme; helps exact-type checks and devirtualization.

### Faster stack-trace generation under profiling
- **What:** dotnet/runtime#117218 + #116031 optimize stack-trace generation in large, heavily multi-threaded apps under profilers.
- **Adoption:** FREE. Less observer effect when diagnosing production-scale services; only relevant under profilers.

---

## Peanut-butter patterns worth copying

Small cross-cutting items; each is FREE unless marked.

### `ArgumentNullException.ThrowIfNull` polyfill via C# 14 static extension members — PATTERN
C# 14 supports extension *static* methods, so `ThrowIfNull` can be polyfilled for netstandard2.0/.NET Framework targets; dotnet/runtime#114644 converted ~2500 call sites in multi-targeting dotnet/runtime libraries. Throw-helpers move the throw out of line, making calling methods more inlineable.
```csharp
internal static class Polyfills
{
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull([NotNull] object? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        { if (argument is null) throw new ArgumentNullException(paramName); }
    }
}
// All TFMs: ArgumentNullException.ThrowIfNull(arg);
```
Multi-targeting libraries no longer need `#if` forks or verbose null-check-throw blocks.

### Collection expressions targeting spans stack-allocate — PATTERN
dotnet/runtime#107797 (NRBF decoding) removed an `int[]` allocation by writing a collection expression into a span target. Prefer `ReadOnlySpan<int> x = [a, b, c];` over `new int[] { ... }` for small temporary state.

### Span-based `Split` kills transient tokenization allocations — PATTERN
dotnet/runtime#118288 (`EnumConverter`) and #111349 (`Size`/`SizeF`/`Point`/`Rectangle` `TypeConverter`s) replaced `string.Split` with `MemoryExtensions.Split` returning `Range`s — no `string[]`, no per-token strings.
```csharp
// Before: foreach (string part in value.Split(',')) Process(part.AsSpan());
// After (allocation-free):
foreach (Range r in value.AsSpan().Split(',')) Process(value.AsSpan()[r]);
```

### `DefaultInterpolatedStringHandler.Text` — new API
dotnet/runtime#112171 exposes the already-formatted buffered text as `ReadOnlySpan<char>`. When building custom handlers/formatting helpers on top of it, read `.Text` instead of paying `ToStringAndClear()`'s terminal `string` allocation when you don't need a `string`.

### One-liners
- **String interpolation null checks removed** (#114497): `$"{s} {s} {s} {s}"` 34.21 → 29.47 ns (~14%). FREE.
- **`Convert` UTF-8 hex overloads** (#117965): `FromHexString`/`TryToHexStringLower` and friends over `byte` — no transcode through `string`/`char`. New API.
- **FileProvider change tokens** (#116175, #115684): allocation-free hashing in `PollingWildCardChangeToken`; `CompositeFileProvider` stops storing nop `NullChangeToken`s. FREE.
- **Generic math `TryConvert*` inlining stragglers** (#112061): missing `AggressiveInlining` added on a few primitive implementations. FREE.

---

## Folklore to delete

- **"Cached `FieldInfo`/`MethodInfo` is the best you can do for private-member access."** Obsolete. `[UnsafeAccessor]` is ~3.6x faster than cached `FieldInfo.GetValue`, allocation/boxing-free, and with .NET 10's `[UnsafeAccessorType]` also covers static members and types invisible to your assembly. Delete reflection-based private-access shims on hot paths.
- **"Reflection is the only way to call across an assembly-reference cycle."** The BCL itself now uses `[UnsafeAccessor]`/`[UnsafeAccessorType]` for exactly this (crypto→HTTP OCSP). Do the same.
- **Custom `ValueStopwatch` structs.** Dead. `Stopwatch.StartNew()`/`Stop()`/`Elapsed` is allocation-free on .NET 10 via escape analysis; even the `GetTimestamp`/`GetElapsedTime` workaround is no longer required for zero-alloc timing.
- **Manually memoizing `Type.AssemblyQualifiedName`.** The runtime caches it (132 ns + 712 B → ~1 ns + 0 B).
- **"Keep `EventSource` event IDs small/dense to avoid huge lookup arrays."** The dense array is gone; a `Dictionary` handles sparse IDs. (One static instance per EventSource type still stands.)
- **"Keep static constructors trivial — spans/cross-type access defeat Native AOT preinitialization."** Increasingly false: `.AsSpan()`, other types' static fields, methods on types with their own cctors, and pointer dereferences all preinitialize now.
- **"Avoid generics under Native AOT because every instantiation bloats the binary."** Weakened: identical generic bodies fold, and non-user-visible generic-method metadata is stripped.
- **"`try`/`finally` carries heavy register save/restore — structure code to avoid it."** The x64 funclet contract moved non-volatile register preservation into the runtime; handler scaffolding is cheap. (Throwing exceptions is still expensive — that folklore stands.)
- **"Wrapping one delegate type in another is free."** It costs a call per invocation — removing one wrapper from `ActivatorUtilities.CreateFactory` was worth ~18%. Construct the target delegate type directly.
- **"Buffer compressed output in a `MemoryStream`, then copy to the destination."** Anti-pattern the BCL deleted from itself — wrap the compression stream around the final destination.
- **Untyped `GCHandle` everywhere.** `GCHandle<T>`/`PinnedGCHandle<T>`/`WeakGCHandle<T>` are safer *and* ~18% cheaper.
- **"Source-generated `[LoggerMessage]` makes logging call sites free."** The generated `IsEnabled` guard cannot protect argument computation at the call site. CA1873 flags this; guard expensive argument construction yourself.
- **"Leaving types unsealed doesn't matter."** `NullLogger` was sealed specifically to make `is NullLogger` checks JIT better. Sealing remains a cheap, recurring perf lever.
- **"Trust your gut over the harness."** All numbers here are nanosecond-scale micro-benchmarks on one machine. Measure with BenchmarkDotNet across `--runtimes net9.0 net10.0` before and after adopting anything above.
