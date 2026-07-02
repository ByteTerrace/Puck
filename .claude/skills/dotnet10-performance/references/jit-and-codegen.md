# .NET 10 JIT & Code Generation

**TL;DR for this domain.** The .NET 10 JIT flattens abstraction through a compounding pipeline: dynamic PGO → guarded devirtualization → inlining → (conditional) escape analysis → stack allocation → range/assertion analysis → dead-check elimination. Inlining is the gateway — nearly every win below requires the relevant callee to be inlined so the JIT can see through it. Safety checks (bounds, null, GC write barriers, covariance, EH scaffolding) are now "pay only when unprovable." Practical consequence: idiomatic C# — interfaces, `foreach`, lambdas, spans, `params`, `^1`, switch-on-length, `is 'a' or 'b'` — is increasingly the fast path, and most .NET 9-era hand-optimizations are now dead weight to delete. Almost everything here is FREE on retarget to .NET 10 with tiered compilation + dynamic PGO left at their defaults.

## Contents
1. [Eliminating allocations via escape analysis](#1-eliminating-allocations-via-escape-analysis)
2. [Interface/virtual dispatch without the cost (devirtualization & PGO)](#2-interfacevirtual-dispatch-without-the-cost)
3. [Getting more inlining](#3-getting-more-inlining)
4. [Indexing without bounds checks](#4-indexing-without-bounds-checks)
5. [Exception-handling constructs in hot paths](#5-exception-handling-constructs-in-hot-paths)
6. [Structs, write barriers, and struct returns](#6-structs-write-barriers-and-struct-returns)
7. [Constant folding & idiom recognition](#7-constant-folding--idiom-recognition)
8. [Block layout & PGO plumbing](#8-block-layout--pgo-plumbing)
9. [New instruction-set support](#9-new-instruction-set-support)
10. [Runtime plumbing with codegen effects](#10-runtime-plumbing-with-codegen-effects)
11. [Folklore to delete](#folklore-to-delete)

---

## 1. Eliminating allocations via escape analysis

Escape analysis + stack allocation now covers delegates, arrays, refs threaded through struct fields (spans), and enumerators. Common requirement across all of these: **the consumer must be inlined** so the JIT can prove non-escape. Objects stored to fields, returned, or passed to non-inlined callees still allocate.

**Delegate stack allocation** (dotnet/runtime#115172) — FREE
- Escape analysis knows delegate `Invoke` doesn't stash `this`; a `Func<>`/`Action<>` whose consumer is inlined and that provably doesn't escape is elided entirely.
- Magnitude: closure-invoking benchmark 19.5 → 6.7 ns (~3x); 88 → 24 B allocated (the 64 B delegate is gone).
- Caveat: the closure **display class** (captured locals) still allocates 24 B — full closure avoidance isn't dead yet, but the delegate itself is often free.

**Array stack allocation** (dotnet/runtime#104906, #112250) — FREE
- Small arrays created and consumed within a method (explicit `new T[]{...}`, `params`, collection expressions) stack-allocate when the consumer is inlined and the array doesn't escape.
- Magnitude: `Process(new string[] { "a", "b", "c" })` 11.6 → 4.0 ns (~2.9x); 48 → 0 B.
- `ReadOnlySpan<T>` overloads remain the by-construction *guarantee*; but don't rewrite call sites that are forced to pass arrays.

**Escape analysis through struct fields / spans** (dotnet/runtime#113977, #116124, #113093) — FREE
- The JIT tracks refs through struct fields, critically `Span<T>` (`ref T` + length). `array → AsSpan → Slice → CopyTo` chains collapse to a stack buffer as if `stackalloc`'d; `Memmove` (backing `CopyTo`) marked non-escaping.
- Magnitude: `BitConverter.GetBytes(value).AsSpan(0, 3).CopyTo(dest)` 9.77 → 0.87 ns (~11x); 32 → 0 B.
- Caveat: non-escape knowledge is per-API (`Memmove` special-cased); exotic span consumers may still cause escape.

**Conditional escape analysis: `foreach` over `IEnumerable<T>` stack-allocates the enumerator** (dotnet/runtime#111473, #116978, #116992, #117222, #117295) — FREE (needs tiered compilation + dynamic PGO, both default)
- Full pipeline: PGO observes the dominant concrete type at `GetEnumerator()` → GDV clones a type-specialized path → devirtualizes + inlines `GetEnumerator`/`MoveNext`/`Current` → conditional escape analysis proves no-escape *on that path only* (the fallback path escaping no longer poisons it) → enumerator stack-allocated.
- Magnitude: `Sum(IEnumerable<int>)` over a 100-element `int[]`: ~109.9 ns / 32 B → ~35.5 ns / 0 B (~3.1x, alloc-free).
- Guidance: stop adding concrete `int[]`/`List<T>` overloads solely to dodge enumerator allocation on hot-but-not-hottest paths. Caveat: only the dominant type's path benefits; mixed-type call sites pay full cost off the fast path.

**Inlining boosts targeting allocation removal** (dotnet/runtime#114806 small-fixed-array-returning methods, #110596 boxing methods) — FREE
- Heuristic boosts buy the inline that unlocks stack allocation / box elision in caller context. Small factory helpers (`=> new[] { a, b }`) no longer need `AggressiveInlining` for this reason alone. Boosts, not guarantees.

## 2. Interface/virtual dispatch without the cost

**Array interface-method devirtualization** (dotnet/runtime#108153, #109209, #109237, #116771) — FREE
- The runtime-manufactured `IList<T>`/`IEnumerable<T>` implementations on `T[]` are now devirtualizable (they were uniquely opaque before). Fixes the .NET 9 inversion where `foreach` over `ReadOnlyCollection<T>`-wrapping-array beat indexer `for` loops.
- Magnitude: `ReadOnlyCollection<int>` (1000 elems) indexer sum 1,960 → 625 ns (~3.1x). Transitive: LINQ `Skip(100).Take(800).Sum()` on it 3.53 → 1.77 µs (~2x) — LINQ's `IList<T>` fast paths were a *deoptimization* on array-backed interfaces; now they work as intended.

**GDV in shared generic contexts** (dotnet/runtime#116453, #109256) — FREE (needs dynamic PGO, default)
- Guarded devirtualization now fires for virtual calls inside shared generic instantiations, e.g. `EqualityComparer<T>.Default.Equals` in a non-inlined generic method with reference-type `T`.
- Magnitude: ~1.9x (2.82 → 1.51 ns). Don't add manual `typeof(T) == typeof(...)` special-casing purely for dispatch cost. Needs a dominant runtime type at the call site.

**Exact-type retyping of `static readonly` fields — no PGO needed** (dotnet/runtime#111948) — FREE / PATTERN
- A `static readonly IEnumerable<int>` holding an `int[]` is retyped by the JIT to `int[]` (immutable after init → read the actual type at JIT time). Devirtualization by proof, not profile → inlining → enumerator stack allocation.
- Magnitude: `foreach` sum over a 5-element static readonly field ~16.3 ns / 32 B → ~2.1 ns / 0 B (~8x, alloc-free).
- Pattern: prefer `static readonly` (over mutable statics) for constant collections — it's now a devirtualization enabler. Field must be initialized by the time the consumer re-JITs at tier 1.

**Methods calling generic virtual methods can be inlined** (dotnet/runtime#116773) — FREE
- Calling a GVM no longer makes the *caller* un-inlinable; dispatch is expressed via `VirtualDispatchHelpers.VirtualFunctionPointer` in the inlined body. The GVM call itself is still helper-mediated indirect dispatch.

**Repeated late inlining pass** (dotnet/runtime#110827) — FREE
- An extra inlining pass runs after later devirtualization phases, so interface call → devirtualized → *now also inlined* chains collapse fully. Enabler that compounds with everything above.

## 3. Getting more inlining

**try/finally no longer blocks inlining** (dotnet/runtime#112968, #113023, #113497, #112998) — FREE
- Methods containing `try/finally` were hard inlining barriers regardless of size; `lock`/`using`/`foreach`/`await` all expand to `try/finally`, so tiny wrappers were systematically un-inlinable. Now inlined. **`try/catch` still blocks inlining.**
- Stop manually flattening small helpers because they contain `lock`/`using`.

**Inlining budget more than doubled** (dotnet/runtime#114191, #118641) — FREE
- The per-method-compilation budget could be exhausted by large early inlines before reaching small hot leaves. >2x budget makes those cliffs rare. Reduces the need for defensive `[MethodImpl(MethodImplOptions.AggressiveInlining)]`. A budget still exists.

**Local-tracking limit unhardcoded (~2x)** (dotnet/runtime#118515) — FREE
- The fixed "stop inlining at 512 tracked locals" rule (params, IL locals, temps, promoted struct fields) is now proportional to the JIT's overall tracked-local limit — nearly doubled in practice. Struct-heavy code hits the wall later.

Also see the allocation-targeted boosts (§1) and GVM-caller inlining (§2).

## 4. Indexing without bounds checks

Bounds-check elimination has moved from "recognize special shapes" to "propagate real value ranges and merged assertions." Knock-on effects usually dwarf the removed compare: dead throw block → no `call` → prologue/epilogue elided.

**Ranges from bit math** (dotnet/runtime#109900) — FREE / PATTERN (delete unsafe workarounds)
- `table[(int)((value * 0x07C4ACDDu) >> 27)]` into a 32-entry table: `>> 27` on `uint` proves index ∈ [0, 31] → no check. The BCL deleted its own unsafe code in `Log2SoftwareFallback` (#118560) — the safe version is now also the fast version.

**Known intrinsic result maxima** (dotnet/runtime#113790) — FREE
- The JIT knows e.g. `ulong.Log2 ≤ 63`; indexing a ≥64-entry table with it is check-free. Exactly the `FormattingHelpers.CountDigits` pattern — its unsafe workaround was reverted to plain span indexing, simpler *and* faster.

**Implied non-emptiness** (dotnet/runtime#116105) — FREE
- After `x[0]`, `x[^1]` needs no second check (`Length >= 1` ⇒ `Length - 1` in bounds). Write the idiomatic `x[0] == x[^1]`; don't hand-hoist `Length`.

**Immutable `string.Length` across comparisons** (dotnet/runtime#115980) — FREE
- `start.Length < text.Length && text[start.Length] == '.'` — guard-then-index at that length is check-free because `string.Length` can't change between reads. Method in the example nearly halved (42 → 26 B). Mutable length sources re-read across calls may still check.

**Order-independent assertion merging** (dotnet/runtime#113233) — FREE
- `pos <= span.Length - 42 && pos > 0 && span[pos - 1] != '\n'` elides the check in **either** condition order (.NET 9 needed one specific ordering). Stop reordering `&&` guards to appease the JIT.

**Non-negative counters across unroll tails** (dotnet/runtime#113862) — FREE
- Classic manual-unroll shape (`for (i = 0; i < arr.Length - 3; i += 4)` then `for (; i < arr.Length; i++)`): the remainder loop is now check-free (`i >= 0` no longer lost between loops).

**Concrete ranges fold later validation** (dotnet/runtime#112824) — FREE
- After `src[5] = 42` the JIT knows `Length >= 6`, so `src.Slice(4)`'s internal argument-validation branch (its ThrowHelper) is deleted — this elides *argument validation*, not just index checks. Access-then-slice no longer double-validates.

**Switch-case assertions** (dotnet/runtime#113998) — FREE
- Inside `case 3:` of `switch (x)` the JIT knows `x == 3`. The idiomatic `switch (span.Length) { case 1: return span[0]; case 2: return span[0] + span[1]; ... }` pattern loses ALL per-case bounds checks (example: six checks → zero, 103 → 70 B). Prefer it over unsafe reads for small fixed-length handling.

**Cloning of straight-line array-write sequences** (dotnet/runtime#112595) — FREE
- The JIT can't legally coalesce bounds checks (partial writes before a throw are observable), but it *can* clone: `arr[0]=..; ... arr[7]=..;` gets one upfront `length > 7` check guarding a check-free fast path, checked original as cold fallback. Bonus: stores coalesce in the fast path (eight 4-byte movs → four 8-byte movs).
```csharp
// .NET 9 folklore: write highest index first so one check dominates
arr[7] = 55; arr[0] = 2; /* ... */
// .NET 10: natural order IS the fast path
arr[0] = 2; arr[1] = 3; /* ... */ arr[7] = 55;
```
- Caveat: cloning duplicates code (114 → 179 B in the example); a size heuristic gates it.

**Loop cloning extended to `Span<T>`** (dotnet/runtime#113575) — FREE
- `for (int i = 0; i < count; i++) span[i] = i;` with `count` unrelated to `span.Length` gets the array treatment: one upfront `count <= Length` check selects a check-free clone. Per-iteration branch count halved. Don't convert span loops back to array loops or hand-slice (`span.Slice(0, count)`) to earn cloning.

**try/finally no longer blocks cloning** (dotnet/runtime#110020, #108604, #110483) — FREE
- Cloning previously bailed on any EH-containing region. Since `foreach` compiles to a hidden `try/finally` (enumerator `Dispose`), essentially every `foreach` loop was cloning-ineligible. Fixed.

**Cloning size heuristic** (dotnet/runtime#108771) — FREE
- Large loop bodies are less likely to be cloned (i-cache/code-size guard). Cloning is *not* guaranteed; if a big hot loop misses it, consider splitting the body into smaller helpers.

## 5. Exception-handling constructs in hot paths

**Provably-non-throwing try/catch and try/fault removed** (dotnet/runtime#110273, #110464) — FREE
- If the `try` body provably can't throw (analysis runs *after* inlining, so inlined non-throwing callees count), the entire EH region — frame setup, stack spills, blocked enregistration — is deleted. Example: `try { i++; } catch {...} return i;` → `lea eax,[rsi+1]; ret` (79 → 4 B).
- Defeaters: `checked` arithmetic, unproven bounds, unproven-non-null derefs, non-inlined calls inside the `try`.

**Empty-finally removal re-runs late** (dotnet/runtime#108003) — FREE
- `finally` blocks that other optimizations *emptied out* are now caught by a late re-check. `using`/`foreach` scaffolding with no-op `Dispose` paths costs nothing.

**Cheaper catch/finally funclets on x64** (dotnet/runtime#115284) — FREE
- Funclets (the mini-functions implementing `catch`/`finally`) no longer save/restore non-volatile registers in their own prologs/epilogs; the runtime preserves them. Smaller code, cheaper handler entry/exit. x64 only. **Throwing exceptions is still expensive** — this is scaffolding cost, not throw cost.

Also: try/finally inlining (§3), try/finally cloning (§4).

## 6. Structs, write barriers, and struct returns

**Write barriers elided for `ref struct` reference fields** (dotnet/runtime#111576, #111733) — FREE
- `ref struct`s can never live on the GC heap, so writing an `object` reference into one needs no `CORINFO_HELP_CHECKED_ASSIGN_REF` — it's a plain `mov`. Example: three-object-field ref struct construction, 3 helper calls gone, 59 → 25 B. Don't avoid object references in ref structs for barrier cost.

**Return buffers guaranteed on-stack — barrier-free struct returns** (dotnet/runtime#112060, #112227) — FREE (ABI change; called out as "much more impactful" than the ref-struct case)
- The hidden return buffer for large struct returns must now be on the caller's stack, so the callee elides ALL write barriers when writing reference fields into it. `record struct Person(string, string, string, string)` returned by value: 4 barrier calls gone, 81 → 35 B, straight-line loads/stores.
```csharp
// .NET 9-era folklore: dodge write barriers on struct returns
void GetPerson(out Person p) { p = new(...); }
// .NET 10: idiomatic by-value return is barrier-free
Person GetPerson() => new(_firstName, _lastName, _address, _city);
```
- Applies to the return-buffer path (structs too big for register return); callers wanting the value on the heap pay an explicit copy after the call.

**Covariance-check elimination via exact generic field types** (dotnet/runtime#107116) — FREE
- The JIT resolves the exact type of fields of closed generics: `List<string>`'s backing array is provably exactly `string[]` (`string` is sealed) → the `CastHelpers.StelemRef` covariance-check helper on `list[0] = "world"` is replaced by an inline store. **Sealing classes keeps paying:** sealed element types delete covariance checks in addition to enabling devirtualization.

**Arm64 write-barrier rework** (dotnet/runtime#111636, #106191) — FREE
- Arm64 gains an x64-style `WriteBarrierManager` selecting among barrier variants with region-aware card marking: each barrier slightly dearer, far fewer cards marked → less GC scan work; #106191 trims hot-path instructions. Arm64 only; nothing to do.

**GCD-based strength reduction for multiple induction variables** (dotnet/runtime#110222) — FREE
- Loops indexing differently-sized elements from one counter (`char[]` stride 2 + `int[]` stride 4) get a single GCD-based induction variable; per-iteration multiplies fold into addressing modes; loop becomes counted `dec/jne` form. No manual pointer arithmetic or `ref`-increment tricks needed. Supported by CSE/SSA integration (dotnet/runtime#106637), which makes .NET 9's strength reduction fire reliably.

## 7. Constant folding & idiom recognition

The idiomatic spelling is now the optimized spelling. Write for readability; the JIT picks the encoding (`bt`, `mulx`, `cbz`, shift-compare).

**Null-check folding across inlines** (dotnet/runtime#111985, #108420) — FREE
- `s ??= ""; return s.AsSpan();` — the null check *inside* the inlined `AsSpan` folds away (41 → 25 B). Both arms of a ternary producing known-non-null values fold a later `is not null` to a constant (37 → 6 B; whole method becomes `return true`). Defensive null checks in inlined helpers are increasingly free when callers established non-nullness. Opaque (non-inlined) producers still check.

**`return cond` normalized to ternary form** (dotnet/runtime#107499) — FREE
- `=> i > 10 && i < 20;` now optimizes identically to the verbose `if (...) return true; return false;` — both get the range-fusion (`(uint)(i - 11) <= 8`, 18 → 13 B). The .NET 9 asymmetry is gone; write whichever reads better.

**Bit-test recognition for `is`/`switch` set checks** (dotnet/runtime#107831, #111979) — FREE / PATTERN
- `c is ' ' or '\t' or '\r' or '\n' or '.'` compiles to a single bit test: 0.4537 → 0.1304 ns (~3.5x). The hand-written `(Mask & (1 << (int)value)) != 0` idiom also now emits `bt` instead of shift+test. New code should use the pattern-matching form. Members must fit a machine-word bitmask.

**Dead-branch elimination from comparison implications** (dotnet/runtime#111766) — FREE
- After `if (x < 16) return;`, a later `if (x < 8) ...` is proven unreachable and deleted — cascading until the example method is 1 byte (`ret`). Layered/defensive range checks that become redundant after inlining are increasingly free — don't strip them at the cost of safety.

**SIMD comparison constant folding** (dotnet/runtime#117099, #117572) — FREE. Comparisons over constant vectors evaluate at JIT time.

**Redundant sign-extension removal** (dotnet/runtime#111305) — FREE. Guarded `int`→`long`/`ulong` widening emits `mov` not `movsxd` when a dominating compare proves non-negative; no `(uint)` casts needed to coax zero-extension.

**Constant division via BMI2 `mulx`** (dotnet/runtime#116198) — FREE. `value / 10` magic-number multiply loses its register-shuffle `mov`s (24 → 20 B). Keep writing `/ 10`.

**`ulong <= uint.MaxValue` as shift** (dotnet/runtime#113037) — FREE. Emitted as `shr rsi,20; sete` — the range guard is optimal as written (15 → 11 B).

**Faster FP↔int conversions** (dotnet/runtime#114410, #114597, #111595) — FREE. Direct `vcvtusi2ss`-family unsigned converts on AVX512; the intermediate-`double` hop removed elsewhere. `float Compute(uint i) => i;` 16 → 11 B. No manual bit tricks.

**More memmove/fill unrolling** (dotnet/runtime#108576, #109036, #110893) — FREE / PATTERN (constants unlock it)
- Constant-length **non-zero** fills now unroll (zero fills already did): `_chars.AsSpan(0, 16).Fill('x')` inlines to a single 32-byte broadcast store instead of calling `SpanHelpers.Fill`. More Arm64 unrolling for `Equals`/`StartsWith`/`EndsWith`; redundant load removed for constant-source copies. Non-constant sizes still call (vectorized) helpers.

## 8. Block layout & PGO plumbing

All FREE; the actionable takeaway is to STOP hand-restructuring code for branch layout.

- **Loop-aware reverse-post-order initial layout** (dotnet/runtime#108903): blocks placed after predecessors, loops kept contiguous as indivisible units — better i-cache locality and branch prediction from the start.
- **3-opt/4-opt block reordering** (dotnet/runtime#103450, #109741, #109835, #110277): TSP-style local search over block orderings minimizes taken-branch/fetch cost. Example: `MemoryExtensions.BinarySearch`'s core loop now falls through into the body with one backward unconditional branch instead of a taken backward conditional every iteration; loop exit is a rare forward branch — matching branch-predictor preferences.
- **Profile-synthesis repair runs just before layout** (dotnet/runtime#111915): gaps in dynamic-PGO data are patched with realistic guesses right where layout consumes them. Another reason to leave dynamic PGO at its default (on).

## 9. New instruction-set support

All FREE on capable hardware unless marked API; existing binaries speed up when new CPUs arrive.

- **Intel APX** (dotnet/runtime#106557, #108796, #113237 encodings; #108799 32 GPRs; #116035 push/pop; #111072, #112153, #116445 `ccmp`): 16 → 32 general-purpose registers (fewer spills) and conditional-compare instructions (fewer branches). Hardware barely shipping as of the post.
- **AVX512 batch**: EVEX embedded broadcasts in more places (#109258, #109267, #108824); `Vector.Max/Min` (#116117); widening-intrinsic containment (#109474, #110736, #111778); `Vector128/256/512.Dot` (#111853); better k-mask handling (#110195, #110307, #117118); 32-byte fallback in frame zeroing (#115981); `ExtractMostSignificantBits` for `short`/`ushort`/`char` via EVEX masks (#110662 — feeds core-library `IndexOf` and friends); `ConditionalSelect` without masks (#113864). Guidance: prefer portable `Vector128/256/512` APIs — they keep inheriting better AVX512 codegen for free.
- **AVX10.2** (dotnet/runtime#111209; #112535 FP min/max; #111775 FP conversions): single-instruction FP min/max with proper NaN semantics and conversions. FREE + API for the new intrinsics.
- **GFNI intrinsics** (dotnet/runtime#109537) — API. GF(2^8) hardware ops for Reed-Solomon/erasure coding/AES-adjacent transforms; check these before table-based implementations.
- **VPCLMULQDQ intrinsics** (dotnet/runtime#109137) — API. Vectorized carry-less multiply — the core CRC/GHASH primitive.
- **Arm SVE build-out** (dotnet/runtime#115775 `BitwiseSelect`, #117711 `MaxPairwise`/`MinPairwise`, #117051 `VectorTableLookup`) — API, **still experimental** in .NET 10; portable vector code remains the safe default.
- **Arm64 compound instructions** (dotnet/runtime#111893, #111904, #111452, #112235, #111797): compare+branch fused into e.g. `cbz`; denser codegen for ordinary `if (x == 0)` code.

## 10. Runtime plumbing with codegen effects

**Unboxing helpers rewritten from C to C#** (dotnet/runtime#108167, #109135) — FREE
- `CORINFO_HELP_UNBOX_NULLABLE` → managed `CastHelpers.Unbox_Nullable`: no native↔managed transition, and the helper is plain IL the JIT can inline/optimize in caller context. ~15% on a contrived `(T?)obj` generic-unboxing benchmark. Part of the multi-release trend: the fastest helper is one the JIT can see through.

**Shared read-only template page for runtime stubs** (dotnet/runtime#114462) — FREE
- Jump stubs / call counters / trampolines now alias one physical read-only code page (per-stub data on its own writable page) instead of regenerating identical instructions per allocation. Lower memory footprint, faster startup, better i-cache — grows with app size (many types / virtual dispatch sites).

---

## Folklore to delete

Hand-optimizations .NET 10 makes obsolete (or actively harmful — they add unsafety and obscure code for zero or negative gain). Verify with BenchmarkDotNet across `--runtimes net9.0 net10.0` when it matters, but the default is: delete.

- **"Use `Unsafe.Add`/pointers to skip bounds checks on masked/shifted or `Log2`-bounded table indices."** The JIT proves these in range; the BCL deleted its own unsafe versions (`Log2SoftwareFallback`, `CountDigits`) and got simpler *and* faster.
- **"Lambdas/`Func<>` in hot paths always cost a 64 B delegate allocation."** Non-escaping delegates with inlined consumers are elided. (Captured-state display classes still allocate — that half survives.)
- **"Never pass a temporary `new T[]{...}` / `params` array in hot code."** Often stack-allocated now; span overloads are the guarantee, not the only remedy.
- **"Never take `IEnumerable<T>`; add concrete overloads to avoid enumerator allocation and interface dispatch."** Dominant-type call sites (and `static readonly` fields) get devirtualized, inlined, alloc-free enumeration.
- **"`for` + indexer beats `foreach`" and its .NET 9 inversion "`foreach` beats `for` on `ReadOnlyCollection<T>`."** Both dead — array interface devirtualization restored sane ordering and sped up both.
- **"Reorder `&&` guard clauses so the JIT elides the bounds check."** Assertion merging is order-independent.
- **"Hand-hoist `Length` / restructure first-last checks."** `x[0] == x[^1]` is single-check.
- **"Write the highest array index first (`arr[7]` before `arr[0]`) to make one bounds check dominate."** Sequence cloning gives natural-order writes the guarded fast path automatically.
- **"Manually clone `if (arr.Length >= N) { fast } else { same code }`."** The JIT does this transform itself, for arrays *and* spans.
- **"Spans don't get loop cloning — use `T[]` in the hottest loops."** Fixed.
- **"Avoid `Slice` re-validation via `MemoryMarshal.CreateSpan` / pointer arithmetic."** Dominating accesses fold `Slice`'s validation.
- **"Helpers containing `lock`/`using`/`try-finally` can't inline — flatten them into callers."** try/finally no longer blocks inlining. (try/catch still does — that one stands.)
- **"Avoid `try/catch` around trivial code; EH scaffolding forces stack spills."** Provably-non-throwing regions are deleted wholesale.
- **"Sprinkle `AggressiveInlining` to beat the inlining budget / 512-local cliff."** Budget >2x, local cap ~2x and proportional; keep the attribute only where profiling proves the inline itself is the win.
- **"`if (cond) return true; return false;` optimizes better than `return cond;`."** Normalized; identical codegen.
- **"Avoid returning reference-bearing structs by value; use `out` params or classes to dodge write barriers."** Return buffers are guaranteed on-stack; barriers on the return path are gone.
- **"Object references in `ref struct`s pay write barriers."** Plain stores now.
- **"Hand-roll bitmask membership instead of `is 'a' or 'b' or ...`."** The pattern form compiles to a bit test (~3.5x over .NET 9's compare cascade); even the hand-rolled mask idiom now emits `bt`.
- **"Manually special-case `EqualityComparer<T>.Default` dispatch in shared generics."** GDV reaches shared generic contexts.
- **"Restructure `if` ordering / loop exits for branch layout."** Loop-aware RPO + 3-opt/4-opt + PGO-driven placement do this; structure code for clarity.
- **"Cast `uint` through `long`/`double` on the way to `float`."** Direct unsigned conversions are emitted.
- **"Hand-write stores instead of small constant `span.Fill(c)` / `CopyTo` calls."** Constant-size fills/copies unroll into inline SIMD stores.
- **"Pointer-bump multi-stride loop indexing to avoid `i*2`/`i*4`."** GCD strength reduction handles multi-induction-variable loops.
