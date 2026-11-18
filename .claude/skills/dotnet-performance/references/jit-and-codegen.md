# .NET 11 JIT & Code Generation

**TL;DR for this domain.** The .NET 11 JIT flattens abstraction through a compounding pipeline: dynamic PGO → guarded devirtualization → inlining → (conditional) escape analysis → stack allocation → range/assertion analysis → dead-check elimination. Inlining is the gateway — nearly every win below requires the relevant callee to be inlined so the JIT can see through it. Safety checks (bounds, null, GC write barriers, covariance, EH scaffolding) are now "pay only when unprovable." Practical consequence: idiomatic C# — interfaces, `foreach`, lambdas, spans, `params`, `^1`, switch-on-length, `is 'a' or 'b'` — is increasingly the fast path, and most .NET 9-era hand-optimizations are now dead weight to delete. Almost everything here is FREE on retarget to .NET 11 with tiered compilation + dynamic PGO left at their defaults.

**How to read an entry.** An entry headed ".NET 11" comes from Stephen Toub's
[Performance Improvements in .NET 11](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/)
and names the post's section and the pull request (`dotnet/runtime#N` is
`https://github.com/dotnet/runtime/pull/N`). The other entries arrived with .NET 10
and still hold. The .NET 11 entries carry no timings: the post's benchmark tables
are where to read a magnitude, and a change in Puck is judged by its own
disassembly and counts. Each one also says which kind of win it is. A *static*
win rests on a proof the JIT makes from the code alone (a value range, an exact
type, an object that provably does not escape), so it holds in the World. A win
that *rides PGO* needs the profile the World runs without
([repository constraints](../SKILL.md#apply-repository-constraints)).

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
11. [Codegen facts the repository relies on](#11-codegen-facts-the-repository-relies-on-in-net-11)
12. [Folklore to delete](#folklore-to-delete)

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

**.NET 11: more temporaries stop escaping** (post: Deabstraction) — FREE, static unless marked
- A `T?` value tested against an interface in generic code (`value is IFormattable f`) used to box through a runtime helper that hid the box from escape analysis. The JIT now expands the box itself and removes it when it does not escape (dotnet/runtime#122167). A method that returns the box still allocates it.
- A freshly allocated object passed to `EqualityComparer<T>.Default.Equals` no longer counts as escaping merely because the constrained call took its address (dotnet/runtime#121918).
- A reference the JIT spills into a temporary keeps its exact class, so a call that was devirtualizable stays devirtualizable and the box around a struct passed through an interface goes with it (dotnet/runtime#128485).
- `ArgumentNullException.ThrowIfNull(value)` on a struct `T` no longer boxes at tier 0 (dotnet/runtime#129392). Optimized code never allocated there. What changes is a tier-0 allocation trace and startup, which lose that noise.
- Rides PGO: an enumerator whose `GetEnumerator()` returns another collection's enumerator is now stack-allocated like the direct case (dotnet/runtime#122946). The wrapper a collection expression gets when converted to `IEnumerable<T>` has that shape. It is the guarded-devirtualization pipeline above, so it does not apply in the World.

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

**Generic virtual methods: the caller inlines, and the call devirtualizes when the receiver's type is known** (dotnet/runtime#116773; .NET 11 post, Deabstraction: dotnet/runtime#120866, #122023, #128702, #123183, #130202) — FREE, static
- Calling a generic virtual method (GVM) no longer makes the *caller* un-inlinable (#116773).
- .NET 11 devirtualizes the call itself when the JIT knows the receiver's exact type: an object it just allocated, a sealed class, a `static readonly` field. The call becomes direct and can inline. This covers shared generic instantiations and default interface methods (#122023, #128702), and ReadyToRun and Native AOT resolve more non-shared GVM calls when the image is built (#123183, #130202). The proof needs no profile, so it applies in the World, whose published build is ReadyToRun.
- When the call site does not know the receiver's type, the call is still an indirect dispatch through a runtime helper. Puck's GVMs (`IGpuStorageBuffer.Write`, `CartridgeBase.TransferRegisters`) are reached through an interface or a base reference, so keep a GVM off a per-element path unless the disassembly shows the direct call ([collections](collections-and-linq.md#frozen-collections-getalternatelookup-gvm-cost-amortized-into-a-delegate)).

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

**.NET 11: more ranges known without a guard** (post: Assertion Propagation, Bounds Checks; dotnet/runtime#119474, #124415, #124711, #128620, #122263) — FREE, static
- A value carries the range of its own type or conversion. A `(byte)` cast is 0 to 255, so it indexes a table of 256 or more entries with no check and no guarding `if` (#119474, #124415).
- `BitOperations.LeadingZeroCount`, `TrailingZeroCount` and `PopCount` results have known ranges (#128620). The post's example indexes a 33-entry table with `LeadingZeroCount(uint)`, whose result is 0 to 32. By the same reasoning, not measured here, a `ulong` count needs 65 entries. A table of 32 or 64 entries indexed by a bit count keeps its check unless something else bounds the count, so read the disassembly before assuming.
- The OR of two bounded values is bounded (#122263), beside the AND and shift ranges above.
- An operation that completed proves a fact the JIT uses afterwards: a created array's length was not negative, a covariant array store's index was in range, a division's divisor was not zero (#124711).

**.NET 11: range facts delete overflow checks too** (post: Bounds Checks; dotnet/runtime#124147, #124184) — FREE, static
- `checked` add, subtract and multiply compile to plain instructions when the operands' ranges show the result cannot overflow, and a `checked((byte)value)` narrowing cast under such a guard loses its check as well. Keep `checked` on bounded quantities (counts, lengths, indices a comparison constrained). Puck writes it widely, and it costs nothing exactly where it is provably redundant.

**.NET 11: guards the JIT reads in full** (post: Bounds Checks; dotnet/runtime#125056, #124242, #125235, #129268, #129303, #124571, #121273, #121527) — FREE, static
- Inside `(uint)i < (uint)span.Length` the JIT records the lower bound as well as the upper, so `span[i - 1]` under `i != 0` is check-free (#125056).
- A lookahead guard written `(uint)(i + 2) < (uint)span.Length` clears `span[i + 1]` and `span[i + 2]` (#124242, #125235). The post demonstrates this unsigned spelling. Puck's parsers mostly write the signed `(at + 1) < source.Length`, which these changes are not shown to cover, so read the disassembly before relying on it there.
- A loop that ends on `i != bound` clones like one that ends on `<` when its stride is exactly 1 or -1 (#129268). Where it cannot clone, the JIT still knows a tighter range for `i` (#129303).
- `arr[^4]` followed by `arr[^3]` reuses the first check (#124571).
- A constant excluded from the edge of a known range tightens that range, which is what the comparisons a C# list pattern lowers to need (#121273). A fact established earlier in the same basic block is used later in it: `arr[i] = 0; i++; if (i < arr.Length) arr[i] = 0;` has one check (#121527).

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
- A run of *writes* cannot share one strengthened check, because the writes that ran before a throw are observable. The JIT clones instead: `arr[0]=..; ... arr[7]=..;` gets one upfront `length > 7` check guarding a check-free fast path, with the checked original as the cold fallback. Bonus: stores coalesce in the fast path (eight 4-byte movs → four 8-byte movs). Reads are a different case, covered next.
```csharp
// .NET 9 folklore: write highest index first so one check dominates
arr[7] = 55; arr[0] = 2; /* ... */
// .NET 11: natural order IS the fast path
arr[0] = 2; arr[1] = 3; /* ... */ arr[7] = 55;
```
- Caveat: cloning duplicates code (114 → 179 B in the example); a size heuristic gates it.

**.NET 11: constant-index reads share one check, and a `return` expression clones** (post: Bounds Checks; dotnet/runtime#127439, #124705) — FREE, static
- Reads at constant indices in one basic block, with no side effect between them (`a[0] + a[1] + a[2] + a[3]`), coalesce: the first check is strengthened to the largest index and the others are removed (#127439). A read leaves nothing behind when a later one throws, which is what keeps writes on the cloning path.
- An expression inside a `return` statement is eligible for range-check cloning (#124705), so an expression-bodied `=> a[0] + a[1] + a[2] + a[3]` gets the guarded fast path it missed on .NET 10.
- Reading the largest index first, or adding an early `Length` test, to earn one check is folklore for reads as it already was for writes.

**.NET 11: a span sliced forward in a loop keeps its length facts** (post: Bounds Checks; dotnet/runtime#122040, #127117, #127488, #127429) — FREE, static
```csharp
// The loop condition is the only length test per iteration: Vector128.Create(data) repeats none.
while (data.Length >= Vector128<int>.Count)
{
    sum += Vector128.Create(data);
    data = data.Slice(Vector128<int>.Count);
}
foreach (int value in data) { tail += value; }
```
- Reading a fixed-size value from the end of a span behind a length guard, `BinaryPrimitives.ReadInt32BigEndian(span.Slice(span.Length - 4))` under `span.Length >= 4`, drops both `Slice`'s check and the reader's (#127488).
- The runtime rewrote its own vectorized `Enumerable.Sum` from `MemoryMarshal.GetReference`, `Vector.LoadUnsafe` and `Unsafe.Add` to this safe shape, and the safe shape was faster (#127429). A `LoadUnsafe` or `Unsafe.Add` walk whose only purpose is to avoid bounds checks records what an older JIT could not prove. Write the safe form, compare the two disassemblies through `puck bench kernels`, and keep the unsafe one only where it still measures faster; the post asks for such a case to be reported upstream with a repro. Puck's candidates are filed in the [refactoring packet](../../../../docs/plans/open-items.md#refactoring-campaign).
- An unsafe read that a mask or a count the JIT cannot see keeps in range (a table indexed through a mask held in a field) is not covered by any of this and stays until the disassembly says otherwise.

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
// .NET 11: idiomatic by-value return is barrier-free
Person GetPerson() => new(_firstName, _lastName, _address, _city);
```
- Applies to the return-buffer path (structs too big for register return); callers wanting the value on the heap pay an explicit copy after the call.

**Covariance-check elimination via exact generic field types** (dotnet/runtime#107116) — FREE
- The JIT resolves the exact type of fields of closed generics: `List<string>`'s backing array is provably exactly `string[]` (`string` is sealed) → the `CastHelpers.StelemRef` covariance-check helper on `list[0] = "world"` is replaced by an inline store. **Sealing classes keeps paying:** sealed element types delete covariance checks in addition to enabling devirtualization.

**Arm64 write-barrier rework** (dotnet/runtime#111636, #106191) — FREE
- Arm64 gains an x64-style `WriteBarrierManager` selecting among barrier variants with region-aware card marking: each barrier slightly dearer, far fewer cards marked → less GC scan work; #106191 trims hot-path instructions. Arm64 only; nothing to do.

**.NET 11: array stores, heap struct copies and neighbouring stores** (post: Write Barriers and Garbage Collection; dotnet/runtime#126547, #128238, #128542, #130535, #126562, #130107) — FREE, static
- A store into a reference array is expanded into its covariance check and its write barrier, where the JIT can see both. When it knows the array's exact type (a sealed element type, a `new T[]` in view), the covariance check goes and the barrier gets cheaper (#126547). This extends the sealed-element rule above from generic fields to any array whose type is known.
- Copying a struct that mixes references and plain data into a heap location uses barriered stores for the references and vector stores for the rest, in place of a helper that copied one reference field at a time (#128238, #128542).
- Stores to adjacent fields merge into fewer, wider stores: in small reference-free struct copies (#130535), in a promoted struct local whose fields are assigned one by one (#126562), and in adjacent static fields (#130107).
- Do not pack neighbouring field writes into one `ulong` or an `Unsafe` write by hand; the JIT merges them. The [copy less](data-and-memory.md#copy-less) rule still stands: a copy costs its size however it is emitted.

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

**.NET 11: equality chains, an implicit `else` and power-of-two tests reduce to one form** (post: Simplification; dotnet/runtime#124567, #128524, #124738, #128533) — FREE, static
- A chain of equality tests over contiguous constants becomes one unsigned range check, whether it starts at zero (`x == 0 || x == 1 || x == 2`, #124567) or at any base (`x == 3 || x == 4 || x == 5` compiles as `(uint)(x - 3) <= 2`, #128524).
- `bool x = false; if (c) x = true;` is if-converted like an explicit `else` (#124738).
- `(a & bit) == bit` and `(a & bit) != 0` are one canonical form when `bit` is a power of two (#128533).
- Write the readable spelling; the hand-written `(uint)(x - a) <= b` buys nothing. None of this lifts the limit on if-conversion inside a loop ([section 11](#11-codegen-facts-the-repository-relies-on-in-net-11)).

**.NET 11: `Enum.Equals` in generic code is an integer compare** (post: Intrinsics; dotnet/runtime#122779, #123086) — FREE, static
- `a.Equals(b)` with `T : Enum` boxed both operands and made a virtual call on .NET 10. The JIT folds it to a comparison of the underlying integers, in JIT-compiled code and under Native AOT, which deleted its own IL rewrite for the same purpose. A generic enum helper calls `Equals` directly; an `Unsafe.As` or underlying-type detour written to avoid the boxes is folklore.

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

- **Intel APX** (dotnet/runtime#106557, #108796, #113237 encodings; #108799 32 GPRs; #116035 push/pop; #111072, #112153, #116445 `ccmp`): 16 → 32 general-purpose registers (fewer spills) and conditional-compare instructions (fewer branches). .NET 11 completes the support (post: Intrinsics; dotnet/runtime#127536), adding the instruction forms that leave the condition flags untouched and `CTEST`/`CFCMOV`. Nothing to do; existing binaries benefit on APX hardware.
- **AVX512 batch**: EVEX embedded broadcasts in more places (#109258, #109267, #108824); `Vector.Max/Min` (#116117); widening-intrinsic containment (#109474, #110736, #111778); `Vector128/256/512.Dot` (#111853); better k-mask handling (#110195, #110307, #117118); 32-byte fallback in frame zeroing (#115981); `ExtractMostSignificantBits` for `short`/`ushort`/`char` via EVEX masks (#110662 — feeds core-library `IndexOf` and friends); `ConditionalSelect` without masks (#113864). Guidance: prefer portable `Vector128/256/512` APIs — they keep inheriting better AVX512 codegen for free.
- **AVX10.2** (dotnet/runtime#111209; #112535 FP min/max; #111775 FP conversions): single-instruction FP min/max with proper NaN semantics and conversions. FREE + API for the new intrinsics.
- **GFNI intrinsics** (dotnet/runtime#109537) — API. GF(2^8) hardware ops for Reed-Solomon/erasure coding/AES-adjacent transforms; check these before table-based implementations.
- **VPCLMULQDQ intrinsics** (dotnet/runtime#109137) — API. Vectorized carry-less multiply — the core CRC/GHASH primitive.
- **Arm SVE build-out** (dotnet/runtime#115775 `BitwiseSelect`, #117711 `MaxPairwise`/`MinPairwise`, #117051 `VectorTableLookup`) — API, **still experimental**: scalable `Vector<T>` stays experimental and off by default in .NET 11 (post: Intrinsics; dotnet/runtime#129852), so portable vector code remains the safe default.
- **.NET 11 portable lane APIs** (post: Intrinsics; dotnet/runtime#129627) — API. `Vector128`, `Vector256` and `Vector512` gain `CreateAlternatingSequence`, `CreateGeometricSequence`, `CreateHarmonicSequence`, the four `ConcatLowerLower`-style half concatenations, `Zip`/`ZipLower`/`ZipUpper`, `Unzip`/`UnzipEven`/`UnzipOdd` and `Reverse`, each recognized by the JIT. The names are read from the net11.0 reference assembly; the post describes the operations without naming them. Check for one of these before writing a per-ISA permute. They do not replace a byte table lookup inside a lane (`Ssse3.Shuffle` and its wider forms).
- **.NET 11 SIMD lowering** (post: Intrinsics) — FREE, static. `Vector128.Dot`, `Plane.Dot` and `Quaternion.Dot` use a multiply, add and permute reduction in place of the dedicated dot-product instruction (dotnet/runtime#125666); the post does not name `Vector3.Dot` or `Vector4.Dot`, so read their disassembly before claiming the same. `Vector256.Sum` and `Vector512.Sum` reduce at full width (dotnet/runtime#127329). A scalar cast between `Half` and `float` is one F16C instruction when AVX2 is enabled (dotnet/runtime#127094). A named-field struct reinterpreted as a vector through `Unsafe.BitCast` stays in a vector register (dotnet/runtime#129563). Prefer the BCL method to a hand-written intrinsic sequence in each case.
- **AVX-512 Bit Matrix Multiply** (`Avx512Bmm`, post: Intrinsics; dotnet/runtime#124804) and **`AvxVnni.V512`** (dotnet/runtime#128365) — API. Bit matrix multiply is a candidate for GF(2) linear maps and CRC beside GFNI and VPCLMULQDQ; `AvxVnni.V512` widens the small-integer multiply-accumulate to 512 bits. Hardware required.
- **Arm64 compound instructions** (dotnet/runtime#111893, #111904, #111452, #112235, #111797): compare+branch fused into e.g. `cbz`; denser codegen for ordinary `if (x == 0)` code.

## 10. Runtime plumbing with codegen effects

**Unboxing helpers rewritten from C to C#** (dotnet/runtime#108167, #109135) — FREE
- `CORINFO_HELP_UNBOX_NULLABLE` → managed `CastHelpers.Unbox_Nullable`: no native↔managed transition, and the helper is plain IL the JIT can inline/optimize in caller context. ~15% on a contrived `(T?)obj` generic-unboxing benchmark. Part of the multi-release trend: the fastest helper is one the JIT can see through.

**Shared read-only template page for runtime stubs** (dotnet/runtime#114462) — FREE
- Jump stubs / call counters / trampolines now alias one physical read-only code page (per-stub data on its own writable page) instead of regenerating identical instructions per allocation. Lower memory footprint, faster startup, better i-cache — grows with app size (many types / virtual dispatch sites).


## 11. Codegen facts the repository relies on in .NET 11

`Math.BigMul(ulong, ulong, out ulong)` and the signed overload lower to one `MUL`/`IMUL` that returns both halves in registers (post: Numerics; dotnet/runtime#117261), so the 128-bit product needs no `UInt128` detour or hand-split halves. `FusedArithmetic.Product` widens through `Int128` instead, on an earlier measurement that its own remark asks to repeat before the form is replaced; the re-measurement on .NET 11 is filed in the [refactoring packet](../../../../docs/plans/open-items.md#refactoring-campaign).

RyuJIT if-converts a select only outside loops (dotnet/runtime#96380, milestone 12.0), so an in-loop `CMOV` still needs the `NoInlining` leaf pattern the [`maths-usage` prime kernel notes](../../maths-usage/references/prime-kernels.md) describe. .NET 11 does not lift this: the runtime marked its own `Random.InternalSample` helper `NoInlining` for the same reason (post: Numerics; dotnet/runtime#131714).

---

## Folklore to delete

Hand-optimizations .NET 11 makes obsolete (or actively harmful — they add unsafety and obscure code for zero or negative gain). Confirm with disassembly when it matters, but the default is: delete.

- **"Use `Unsafe.Add`/pointers to skip bounds checks on masked/shifted or `Log2`-bounded table indices."** The JIT proves these in range; the BCL deleted its own unsafe versions (`Log2SoftwareFallback`, `CountDigits`) and got simpler *and* faster. .NET 11 adds indices from a narrowing cast, a bit count or an OR of bounded values.
- **"Walk a span with `MemoryMarshal.GetReference`, `LoadUnsafe` and `Unsafe.Add` so a vector loop pays no bounds checks."** A span sliced forward under `while (data.Length >= Count)` is check-free on .NET 11; the runtime's own `Enumerable.Sum` got faster in that safe form.
- **"Hand-write `(uint)(x - a) <= b` for a run of equal-to-constant tests"** and **"pack neighbouring field writes into one wide store."** The JIT does both from the readable spelling.
- **"Compare generic enums through `Unsafe.As` or their underlying type because `Equals` boxes."** `Enum.Equals` on `T : Enum` is an integer compare.
- **"A generic virtual method can never be a direct call."** It is one when the call site knows the receiver's exact type. Through an interface or base reference it is still indirect.
- **"Lambdas/`Func<>` in hot paths always cost a 64 B delegate allocation."** Non-escaping delegates with inlined consumers are elided. (Captured-state display classes still allocate — that half survives.)
- **"Never pass a temporary `new T[]{...}` / `params` array in hot code."** Often stack-allocated now; span overloads are the guarantee, not the only remedy.
- **"Never take `IEnumerable<T>`; add concrete overloads to avoid enumerator allocation and interface dispatch."** Dominant-type call sites (and `static readonly` fields) get devirtualized, inlined, alloc-free enumeration.
- **"`for` + indexer beats `foreach`" and its .NET 9 inversion "`foreach` beats `for` on `ReadOnlyCollection<T>`."** Both dead — array interface devirtualization restored sane ordering and sped up both.
- **"Reorder `&&` guard clauses so the JIT elides the bounds check."** Assertion merging is order-independent.
- **"Hand-hoist `Length` / restructure first-last checks."** `x[0] == x[^1]` is single-check.
- **"Touch the highest array index first (`arr[7]` before `arr[0]`), or test `Length` early, to make one bounds check dominate."** Sequence cloning gives natural-order writes the guarded fast path, and on .NET 11 constant-index reads in one block coalesce into a single check.
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
