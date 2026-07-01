# .NET 10 Performance — Numerics, SIMD & Threading

**TL;DR for this domain:**
- Any elementwise or reduction math over a numeric span defaults to `TensorPrimitives` — its one optimized SIMD driver loop beats hand-written scalar and most hand-written vector code, and now covers types with no hardware SIMD (int division, `Half`, `nint`) via emulation.
- Where hardware support is missing, .NET 10 vectorizes by *smuggling*: int SIMD division via exact double round-trip, `Half` via reinterpret-to-short + float round-trip, `nint`/`nuint` via platform-width aliasing. "This type can't be vectorized" is dead.
- The framework absorbs degenerate cases: thread-pool sync-over-async local-queue starvation, channel cancellation working-set blowup, single-task `WhenAll`. Delete defensive guards around these calls.
- Ask randomness sources — especially crypto ones — for bytes in bulk (`GetItems`/`GetString`/`GetHexString`), never one integer per element.
- C# 14 user-defined compound operators break the `a += b` ≡ `a = a + b` identity so large structures can mutate in place — a language-level allocation-removal tool (and a correctness review item).

## Contents

1. [Elementwise math over buffers → TensorPrimitives](#1-elementwise-math-over-buffers--tensorprimitives)
2. [Vectorization by emulation: int division, Half, nint, ConvertTruncating](#2-vectorization-by-emulation-int-division-half-nint-converttruncating)
3. [Writing manual SIMD code](#3-writing-manual-simd-code)
4. [Wide integers and decimal](#4-wide-integers-and-decimal)
5. [Random data and token generation](#5-random-data-and-token-generation)
6. [Tensors and C# 14 compound operators](#6-tensors-and-c-14-compound-operators)
7. [Thread pool: sync-over-async starvation absorbed](#7-thread-pool-sync-over-async-starvation-absorbed)
8. [Channels](#8-channels)
9. [Task combinators (WhenAll)](#9-task-combinators-whenall)
10. [Low-lock code: Volatile half fences](#10-low-lock-code-volatile-half-fences)
11. [Folklore to delete](#folklore-to-delete)

---

## 1. Elementwise math over buffers → TensorPrimitives

### Architecture: one SIMD driver, many tiny operators

- **What changed:** dotnet/runtime#112933 adds 70+ overloads to `TensorPrimitives` (NuGet `System.Numerics.Tensors`): `StdDev`, `Average`, `Clamp`, `DivRem`, `IsNaN`, `IsPow2`, `Remainder`, and many more. Naming maps 1:1 to `INumber<T>`-style generic-math operations.
- **Mechanism:** Shared workhorse routines (e.g. `InvokeSpanIntoSpan<T, TOperator>`, reused by ~60 methods) own the hard parts — alignment, remainder handling, `Vector128/256/512` dispatch. Each operation supplies only a tiny operator struct with scalar + per-vector-width kernels (e.g. `DecrementOperator<T>` is four one-liners). Every op inherits the full driver quality and improves for free with new hardware (AVX512) and each release.
- **Magnitude:** `TensorPrimitives.Decrement` vs manual scalar loop over 1000 floats: 288.80 ns → 22.46 ns (~13x).
- **Adoption:** new API (package update).
- **Guidance:** For any elementwise or reduction math over spans/arrays of numbers, reach for `TensorPrimitives` before writing a loop — and definitely before hand-writing `Vector256` code.

```csharp
// Before:
for (int i = 0; i < src.Length; i++) dest[i] = src[i] - 1f;
// After:
TensorPrimitives.Decrement(src, dest);
```

### SoftMax: Exp computed once per element

- **What changed:** dotnet/runtime#111615: `SoftMax` caches `exp(x)` in the destination while accumulating the sum, then divides in place, instead of calling `T.Exp` twice per element.
- **Mechanism:** Redundant-computation removal; `exp` dominates cost, so halving invocations nearly doubles throughput.
- **Magnitude:** 1000 floats: 1,047.9 ns → 649.8 ns (~1.6x). FREE via updated package.

### LeadingZeroCount vectorized with AVX512

- **What changed:** dotnet/runtime#110333: with AVX512, `LeadingZeroCount` is vectorized for all `Vector512<T>`-supported element types using permute-based table lookups (`PermuteVar16x8x2`).
- **Magnitude:** 1000 bytes: 401.60 ns → 12.33 ns (~33x). FREE.
- **Caveat:** The big win requires AVX512 hardware.

---

## 2. Vectorization by emulation: int division, Half, nint, ConvertTruncating

The unifying mechanism: when the hardware lacks an instruction, .NET 10 routes lanes through a type the hardware *does* support, exactly.

### SIMD integer division (double round-trip)

- **What changed:** dotnet/runtime#111505: `TensorPrimitives.Divide<T>` is vectorized for `int`. No SIMD hardware integer divide exists; the JIT emulates it by converting `int` lanes to `double`, doing SIMD double division, and converting back.
- **Mechanism:** `double` represents every `int` exactly, so the round-trip is bit-exact; SIMD double divide + conversions beats scalar `idiv` per element. The JIT emulation benefits vectorized int division generally, not just TensorPrimitives.
- **Magnitude:** 1000-element int division: 1,293.9 ns → 458.4 ns (~2.8x). FREE for TensorPrimitives users.
- **Guidance:** Elementwise integer division over buffers no longer needs to stay scalar — use `TensorPrimitives.Divide`.

### ~60 operations vectorized for Half (float round-trip)

- **What changed:** dotnet/runtime#116898 + #116934 accelerate nearly 60 `TensorPrimitives` ops for `Half`: `Abs`, `Add`, `AddMultiply`, bitwise ops, `Ceiling`/`Floor`/`Round`/`Truncate`, `Clamp`, `CopySign`, trig (`Cos`/`Sin`/`Tan` + `*Pi`/`*h` variants), `CosineSimilarity`, `Divide`, the full `Exp*`/`Log*` families, `FusedAddMultiply`, `Hypot`, `Lerp`, `Max*`/`Min*`, `Multiply(Add)(Estimate)`, `Negate`, `Reciprocal`, `Remainder`, `Sigmoid`, `Sqrt`, `Subtract`, `Tanh`, `Xor`, and more.
- **Mechanism:** `Half` has no hardware acceleration and isn't a supported vector element type; scalar `Half` ops already round-trip through `float`. The fix vectorizes that same round-trip: reinterpret `Span<Half>` as `Span<short>` to smuggle values into vectors, use the already-accelerated `ConvertToSingle`, run the `float` SIMD kernel, `ConvertToHalf` back.
- **Magnitude:** `TensorPrimitives.Add` over 1000 `Half`s: 5,984.3 ns → 481.7 ns (~12x). FREE via updated package.
- **Guidance:** `Half` buffers (ML weights/embeddings, compact telemetry) now process at near-`float` throughput. Do NOT hand-convert to `float[]` first just to get vectorization — operate on the `Half` spans directly.

### nint/nuint vectorization; ConvertTruncating vectorized

- **What changed:** dotnet/runtime#116945 makes `TensorPrimitives.Divide`, `Sign`, `ConvertToInteger` vectorizable for `nint`/`nuint`. dotnet/runtime#116895 vectorizes `TensorPrimitives.ConvertTruncating` for `float`→`int`/`uint` and `double`→`long`/`ulong` — previously blocked because the underlying conversion had undefined behavior (fixed late in .NET 9 by defining saturating semantics).
- **Mechanism:** `nint`/`nuint` are bit-identical to `int`/`uint` (32-bit) or `long`/`ulong` (64-bit), so existing vector paths apply directly; defined float→int conversion semantics made the SIMD conversion legal.
- **Magnitude:** `ConvertTruncating` float→int, 1000 elements: 933.86 ns → 41.99 ns (~22x). FREE via updated package.

### Scalar FP↔int conversions (adjacent JIT win)

- **What changed:** dotnet/runtime#114410/#114597/#111595: unsigned int→float conversions use `vcvtusi2ss`-family instructions on AVX512, and skip the intermediate `double` hop elsewhere.
- **Guidance:** Plain casts between `uint`/`ulong` and `float`/`double` are efficient as written — delete the "widen to long/double first" bit tricks. FREE.

---

## 3. Writing manual SIMD code

Default position: don't. `TensorPrimitives` for buffer math, portable `Vector128/256/512` if you must go lower, per-ISA intrinsics only when profiling proves it. .NET 10 reinforces this ordering:

### New element-wise vector APIs

- dotnet/runtime#111179 + #115525 add methods like `IsNaN` on `Vector128/256/512` (used by TensorPrimitives internally). If you write manual `VectorXXX` code, check for these first-class predicates before composing them from compares/masks. New API.

### SIMD comparison constant folding

- dotnet/runtime#117099 + #117572: vector comparisons over JIT-time-constant operands (e.g. thresholds in `Vector128.Create(...)` of constants) are folded at JIT time instead of emitting the compare. FREE. Don't pre-compute such results by hand.

### AVX512 codegen keeps improving under portable code

- Broad batch of AVX512 improvements: EVEX embedded broadcasts in more places, better `Vector.Max/Min` codegen, better containment for widening intrinsics, better `Dot` acceleration, better k-mask register handling, `ExtractMostSignificantBits` for `short`/`ushort`/`char` via EVEX masks (feeds core-library `IndexOf` and friends), improved `ConditionalSelect`.
- **Guidance:** Cross-platform `Vector128/256/512` code gets better AVX512 codegen for free — prefer the portable vector APIs over hand-rolled per-ISA intrinsics unless profiling proves otherwise. FREE on AVX512 hardware.

### New ISA surface (expert intrinsic code only)

- **GFNI** (dotnet/runtime#109537): hardware GF(2^8) instructions. Implementing Reed-Solomon, erasure coding, AES-adjacent transforms? Check the GFNI intrinsic classes before table-based implementations. New API; GFNI hardware required.
- **VPCLMULQDQ** (dotnet/runtime#109137): vectorized carry-less 64-bit multiply — the core primitive of CRC and GHASH/GCM. Custom CRC/GHASH kernels can now be vector-width. New API; hardware required.
- **AVX10.2** (dotnet/runtime#111209 + follow-ups): FP min/max with proper NaN semantics and FP conversions as single instructions. FREE on AVX10.2 hardware (barely shipping); nothing to do.
- **Arm SVE**: `BitwiseSelect`, `MaxPairwise`/`MinPairwise`, `VectorTableLookup` added, but SVE remains **experimental** in .NET 10 — portable `Vector<T>`/`Vector128<T>` code stays the safe default on Arm.
- **Intel APX**: JIT groundwork (32 GPRs, `ccmp`) — existing binaries speed up automatically when APX CPUs arrive. Nothing to do.

---

## 4. Wide integers and decimal

### UInt128 division uses the x86 DivRem intrinsic

- **What changed:** dotnet/runtime#99747: `UInt128` division uses the x86 128/64→64 hardware divide when the dividend exceeds `ulong` but the divisor fits in a `ulong`.
- **Magnitude:** 27.3112 ns → 0.5522 ns (~49x) for that shape. FREE.
- **Caveat:** x86/x64-specific; the giant win requires a ≤64-bit divisor.

### decimal multiply/divide faster

- dotnet/runtime#99212: better scaled-integer arithmetic paths. Division: 27.09 ns → 23.68 ns (~13%). FREE.

### BigInteger

- `TryWriteBytes` does a straight memory copy when the value is non-negative (no two's-complement adjustment needed): 200-digit positive value 27.814 ns → 5.743 ns (~4.8x) (dotnet/runtime#115445). FREE; fast path is non-negative values only.
- `BigInteger.Parse` of exactly the `int.MinValue` string returns the cached singleton, allocation-free (dotnet/runtime#104666). Corner case; FREE.

---

## 5. Random data and token generation

### GetItems rejection sampling; new GetString/GetHexString

- **What changed:** dotnet/runtime#107988 extends the .NET 9 bulk-bytes fast path of `GetItems` (previously power-of-2 choice counts ≤ 256 only) to arbitrary choice counts via rejection sampling. dotnet/runtime#112162 adds `Random.GetString` and `Random.GetHexString`.
- **Mechanism:** Instead of one full `int` draw from the randomness source per selected element, request random bytes in bulk; for non-power-of-2 alphabet sizes a mask would bias, so out-of-range bytes are rejected and retried — still far cheaper than per-element draws, and dramatically so for `RandomNumberGenerator`, where each draw hits the crypto source.
- **Magnitude:** 30 items from a 58-char (Base58) alphabet: `Random.Shared.GetItems` 144.42 → 73.68 ns (~2x); `RandomNumberGenerator.GetItems` 23,179.73 → 853.47 ns (~27x).
- **Adoption:** FREE for existing `GetItems` calls; new API for `GetString`/`GetHexString`.
- **Guidance:** Use `GetItems`/`GetString`/`GetHexString` for random-token/ID generation instead of per-character `Next()` loops — including arbitrary alphabet sizes and including the crypto-secure `RandomNumberGenerator` variant, which is now viable in hot paths.

```csharp
// Before (slow, especially crypto):
var chars = new char[30];
for (int i = 0; i < chars.Length; i++)
    chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
// After:
char[] chars = RandomNumberGenerator.GetItems<char>(alphabet, 30);
// or: string s = Random.Shared.GetString(alphabet, 30);
```

---

## 6. Tensors and C# 14 compound operators

- **What changed:** `System.Numerics.Tensors` ships stable `Tensor<T>`, `ITensor<,>`, `TensorSpan<T>`, `ReadOnlyTensorSpan<T>`. dotnet/runtime#117997 gives them C# 14 *user-defined compound assignment operators* (compiler support dotnet/roslyn#78400) — a real `operator +=`, implemented as *extension operators* (also new in C# 14).
- **Mechanism:** Historically `a += b` always expanded to `a = a + b`, so every compound op on a tensor allocated a whole new (potentially huge) tensor. A user-defined `+=` mutates the target in place — allocation removal proportional to tensor size per operation.
- **Adoption:** new API / PATTERN change (requires C# 14).
- **Guidance:** For tensors, prefer `t1 += t2` over `t1 = t1 + t2` — they are no longer equivalent in cost. On big value-bag types you own, define `operator +=` when in-place mutation is semantically safe, instead of forcing allocate-and-replace through `+`.

```csharp
// C# 14: a type may define both; += no longer expands to a = a + b
public static C operator +(C left, C right) => new() { Value = left.Value + right.Value };
public static void operator +=(C other) => Value += other.Value; // mutates in place
```

- **Caveat (correctness, not just perf):** `+=` may now mutate the existing instance rather than rebind the variable. Aliasing code that assumed `a += b` produced a fresh object must be reviewed.

---

## 7. Thread pool: sync-over-async starvation absorbed

### Local queue dumped to global queue before blocking

- **What changed:** When a pool thread is about to block — in particular on a `Task` (`task.Wait()`, `.Result`) — it first dumps its entire thread-local work-item queue into the global queue (dotnet/runtime#109841, on by default via #112796).
- **Mechanism:** The pool drains local-first, then global, then steals. Under sync-over-async, a blocked thread's local queue holds exactly the items its unblocking depends on — but other threads only steal after the global queue is exhausted. With a steady stream of incoming global work, the logically highest-priority items become effectively lowest priority → deadlock-like starvation. Dumping the local queue gives those items a fair chance on other threads.
- **Magnitude:** A crafted repro (saturated global queue + N blocked items each waiting on 4 local sub-items) hangs on .NET 9 (20,002 ms timeout) and completes in ~4 ms on .NET 10. FREE.
- **Guidance:** Sync-over-async is still an anti-pattern — it still burns a thread and still risks starvation under thread-injection limits — but the worst degenerate hard-hang (a blocked thread's own continuations stranded in its local queue) is gone. Treat mysterious .NET ≤9 pool hangs under sync-over-async as likely fixed by upgrading; do not treat this as license to block.
- **Caveat:** Only fixes the local-queue starvation shape; pool-thread exhaustion from mass blocking is unchanged.

### Misc pool tuning

- Reduced Arm spin-waiting (aligned with x64), less frequent (and now configurable) CPU-utilization sampling, removed lock contention when starting new pool threads under load (dotnet/runtime#115402, #112789, #108135). FREE; relevant only if you profile pool-internal overhead on Arm or see contention during thread-injection bursts.

---

## 8. Channels

### Canceled waiters unlinked immediately — working-set blowup fixed

- **What changed:** dotnet/runtime#116021 switches channels' internal queues of pending `AsyncOperation` waiters from array-backed queues to linked lists (the waiter objects are the nodes), so canceled operations are removed promptly instead of lingering until dequeued. To offset the two node fields, `AsyncOperation` was shrunk (merged `ExecutionContext` with `TaskScheduler`/`SynchronizationContext` field; removed the stored `CancellationToken`); net object size *decreased*.
- **Mechanism:** Array-backed queues can't cheaply remove from the middle, so canceled waiters were skipped only at dequeue time — fine at balanced steady state, but when cancellations dominate completions (many readers timing out with no writers) canceled entries accumulated without bound.
- **Magnitude:** Degenerate repro (loop: `ReadAsync(cts.Token)` then cancel, unbounded channel, no writers): .NET 9 working set grows unbounded past ~1.5 GB; .NET 10 plateaus ~46 MB. FREE.
- **Guidance:** Channel reads/writes with timeouts/cancellation (routine in server code) no longer risk working-set growth. Delete workarounds: periodic dummy writes to flush canceled readers, channel recreation. (Rare edge: an awaiter needing both a captured `ExecutionContext` and a non-default scheduler now costs one extra allocation — irrelevant in practice.)

### Unbuffered (rendezvous) channel

- dotnet/runtime#116097 adds an unbuffered channel factory alongside `CreateUnbounded`/`CreateBounded`/`CreateUnboundedPrioritized`: writes complete only when paired with a reader — strict handoff/backpressure with no buffer. New API.

---

## 9. Task combinators (WhenAll)

### Single-task input returns that task directly

- **What changed:** dotnet/runtime#117715: when `Task.WhenAll`'s input turns out to be one task, it skips the combinator machinery and returns that task instance.
- **Magnitude:** `Task.WhenAll([t.Task])`: 72.73 → 33.06 ns (~2.2x), 144 B → 72 B. FREE.

```csharp
// Before (.NET ≤9 hand-optimization) — delete this:
Task combined = tasks.Count == 1 ? tasks[0] : Task.WhenAll(tasks);
// After (.NET 10):
Task combined = Task.WhenAll(tasks);
```

- **Caveat:** Returning the same instance is observable via reference equality; don't depend on `WhenAll` returning a distinct task.

### No temporary buffer for IEnumerable\<Task\>

- dotnet/runtime#110536: `Task.WhenAll(IEnumerable<Task>)` no longer allocates a temporary collection when buffering the input. 2-task LINQ enumerable: 216.8 → 181.9 ns, 496 B → 408 B. FREE. No need to pre-materialize an enumerable into an array purely to help `WhenAll`.

---

## 10. Low-lock code: Volatile half fences

- **What changed:** dotnet/runtime#107843 adds `Volatile.ReadBarrier()` (load-acquire, "downward fence") and `Volatile.WriteBarrier()` (store-release, "upward fence") — standalone half fences with no associated memory access.
- **Mechanism:** ReadBarrier prevents accesses after it from reordering before it; WriteBarrier prevents accesses before it from moving after it — analogous to `lock` entry/exit semantics. Cheaper than a full `Thread.MemoryBarrier()`/`Interlocked` full fence when only one direction is needed, and more expressive than contorting code around `Volatile.Read/Write` on a specific field.
- **Adoption:** new API.
- **Guidance:** For genuinely lock-free algorithms only. If you aren't writing low-lock code, ignore this and keep using `lock`/`Volatile.Read/Write`.
- **Caveats:** Half fences do NOT prevent outside accesses from moving *into* the protected region, a read barrier does not stop earlier accesses moving later, and vice versa. Getting this wrong produces heisenbugs. (Note: today's `lock` happens to use full barriers on enter/exit, but that's not required by spec — don't depend on it.)

---

### Folklore to delete

- **"Integer division / Half math / LeadingZeroCount can't be vectorized — write scalar loops."** Dead. `TensorPrimitives` vectorizes all three: int div via exact double emulation, `Half` via float round-trip, lzcnt via AVX512 permutes.
- **"Convert `Half[]` to `float[]` first to get SIMD throughput."** Dead. TensorPrimitives does the vectorized round-trip internally; operate on the `Half` spans directly.
- **"Manual scalar loops are fine for simple elementwise ops like `x - 1`."** Dead. `TensorPrimitives.Decrement` is ~13x faster than the manual loop; simple loops over numeric buffers are exactly what the library out-optimizes.
- **"Hand-write `Vector256` intrinsic code for buffer math."** Usually wasted effort now: the TensorPrimitives driver loop handles 128/256/512 dispatch, alignment, and remainders, and improves for free with hardware and each release.
- **"RandomNumberGenerator is too slow for random strings/tokens in hot paths."** Dead. ~27x faster `GetItems` with arbitrary alphabet sizes; use `GetItems`/`GetString`/`GetHexString` instead of per-char loops.
- **"Pre-check for a single task before calling Task.WhenAll."** Dead. `WhenAll` returns the lone task directly.
- **"Materialize your `IEnumerable<Task>` to an array before WhenAll to avoid allocs."** Dead. The enumerable path no longer allocates a temporary buffer.
- **"Channels leak memory if readers cancel a lot — recycle the channel / inject dummy writes."** Dead. Canceled waiters are unlinked at cancellation time.
- **"Mysterious thread-pool hang under sync-over-async — restructure everything immediately."** The hard-hang shape (continuations stranded in a blocked thread's local queue) is fixed in .NET 10. Sync-over-async remains an anti-pattern for thread-consumption reasons — but diagnose ≤9 hangs as this bug first.
- **"You need a full `Thread.MemoryBarrier()` when a half fence would do."** `Volatile.ReadBarrier`/`WriteBarrier` express standalone acquire/release fences precisely (low-lock experts only).
- **"`a += b` is just sugar for `a = a + b`."** No longer necessarily true in C# 14. On types with user-defined compound operators (like `Tensor<T>`), `+=` mutates in place — a perf opportunity on your own types and a correctness review item on others'.
- **"Cast `uint` to `long`/`double` first when converting to `float` to avoid the slow path."** Dead. Direct unsigned conversions are emitted (single `vcvtusi2ss` on AVX512; double-hop removed elsewhere).
- **"Keep hand-coded reciprocal tricks for constant division."** Unnecessary. Constant division stays optimal as written (`/ 10`); BMI2 `mulx` codegen removed the remaining register shuffling.
- **"Software 128-bit division is unavoidably slow."** For the common shape (128-bit dividend, ≤64-bit divisor) it's now a single hardware divide, ~49x faster — don't split `UInt128` math by hand to dodge it.
