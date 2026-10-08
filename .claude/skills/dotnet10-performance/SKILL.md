---
name: dotnet10-performance
description: Applies .NET 10 performance behavior when writing, reviewing, refactoring, or optimizing this repository's C# code. Use for hot paths, regressions, benchmarks, micro-optimization, collection, string, JSON, SIMD, interop, allocation, code-generation, Native AOT, or trimming questions and claims that a C# pattern is slow. Prefers measurement and idiomatic net10.0 code while preserving semantics. Determinism outranks throughput on simulation value paths, where maths-usage owns primitive choice; subsystem skills own their verification gates.
---

# .NET 10 performance

This skill is factual, not an architecture or style mandate. Judge a change by
code, disassembly and load-independent counts (allocations, operation counts);
a wall-clock benchmark runs only when the owner asks for one, through
`puck bench`. Distinguish runtime improvements from workload-specific evidence.

## Route to the relevant evidence

| Area | Read |
|---|---|
| Cross-domain mental model, pattern changes, API shortlist, folklore | [references/core-guidance.md](references/core-guidance.md) |
| JIT, codegen, allocation, inlining, bounds checks, ISA | [references/jit-and-codegen.md](references/jit-and-codegen.md) |
| Collections, LINQ, Frozen/Immutable, `CollectionsMarshal` | [references/collections-and-linq.md](references/collections-and-linq.md) |
| Strings, spans, search, regex, UTF-8, encoding | [references/strings-text-search.md](references/strings-text-search.md) |
| Numerics, SIMD, tensor APIs, randomness, threading | [references/numerics-simd-threading.md](references/numerics-simd-threading.md) |
| I/O, compression, networking, JSON, crypto | [references/io-network-json-crypto.md](references/io-network-json-crypto.md) |
| AOT, reflection, GC handles, diagnostics, DI, runtime | [references/runtime-aot-reflection-diagnostics.md](references/runtime-aot-reflection-diagnostics.md) |

Read only the files relevant to the code under review.

## Apply repository constraints

The references describe .NET 10 in general. Resolve their advice through the
current [`Directory.Build.props`](../../../Directory.Build.props) and the
affected project file before changing code.

- **`net10.0` is the repository default.** `Puck.Analyzers` deliberately targets
  `netstandard2.0`; inspect the affected project rather than assuming every
  project inherits the default. A reference that compares net9.0 with net10.0
  describes an upgrade decision this tree has already made.
- **`InvariantGlobalization` is on.** Culture-sensitive comparison and formatting
  guidance collapses to the invariant case, and there is no ICU behavior to tune.
- **`PlatformTarget` is x64 and `OptimizationPreference` is Speed.** Arm-specific
  and size-tuning material is background rather than a work item.
- **AOT and trim compatibility analysis is the default; Native AOT publication
  is not.** `Directory.Build.props` sets `IsAotCompatible=true`, enabling the
  AOT and trimming analyzers under warnings-as-errors. Executables opt into
  `PublishAot` deliberately when they ship that artifact. Projects unable to
  satisfy the analyzers set `IsAotCompatible=false` locally with a blocker
  comment. Discover the current exceptions with a search for that property;
  do not preserve a hard-coded count or backlog in this skill.
- **Prefer source-generated serialization over an AOT opt-out.** Follow an
  existing `[JsonSerializable]` context and use `JsonTypeInfo` overloads so the
  analyzer can see the supported graph. Add or retain a project opt-out only
  when the project file documents a genuine structural blocker.
- **World publication uses ReadyToRun.** `Puck.World` restores the `win-x64`
  and `linux-x64` graphs and precompiles published assemblies; ordinary builds
  still produce IL. Compare the exact published artifact with `puck bench startup
  --world-artifact <path>` and account for its larger assemblies. The World
  README owns the publish recipe; CI retains its compile-once contract. World
  regenerates publish dependency metadata through a pinned SDK hook when
  reusing portable IL; verify a real no-build published launch after SDK updates.

## Determinism outranks throughput

`AGENTS.md` rule 4 binds every value a simulation advances, compares, hashes,
snapshots, or replays: the same document and input produce bit-identical state
on every run, machine, and backend. Some of the references' strongest
recommendations are unsafe on that path, and the references do not say so
because they are not written about this repository.

- **`Vector<T>` is machine-width.** Any result that depends on `Vector<T>.Count`
  — a float reduction, a lane-order hash, chunking that reaches the output —
  differs between hosts. Fixed-width `Vector128/256/512` with an explicit scalar
  tail is the deterministic form.
- **`TensorPrimitives` promises no evaluation order for its reductions.** Over
  integer element types the result is exact and order cannot move it; over
  `float` or `double` it can differ by ISA. A float fast path is not a free win
  at any speed where the value it produces is compared, hashed, or replayed.
- **Randomness and parallel completion order are not optimization knobs.**
  `Random.Shared`, `RandomNumberGenerator`, and `Parallel.*` never touch a value
  path; `maths-usage` names the reproducible primitives that replace them.

Presentation-only float — shaders, renderer and UI math, capture output — sits
outside this contract and takes the references as written.

## Review discipline

- Start with a profile, benchmark, allocation trace, or demonstrated hot path.
- Route measurements through the existing harnesses: `puck bench kernels` for
  BenchmarkDotNet timing, allocation and disassembly; `puck bench world` for
  server construction and tick workloads; and `puck bench startup` for fresh-process
  readiness. The [CLI reference](../../../docs/reference/cli.md#puck-benchthe-puckmaths-microscope)
  owns options, report fields and measurement hygiene.
- Preserve comparable semantics, traversal and sinks; confirm specialization
  and call boundaries in optimized assembly. Record first-use work separately from warm
  tables, and distinguish allocated bytes from retained pools or peak workspace.
  Use idle, serial runs under the repository's timing authorization rules;
  sampled timings alone never establish an algorithmic or full-domain claim.
- No law gates a timing: cost laws state deterministic counts.
  The one managed-allocation meter is `Puck.Abstractions.Counting.AllocationWindow`:
  `Least` takes the least of up to 16 windows of
  `GC.GetAllocatedBytesForCurrentThread` and, when every window allocated,
  throws naming the window, the GC mode, and the types the runtime's
  `AllocationSampled` event sampled on that thread; `Measure` reads the same
  least as a count for a ceiling, and `Total` counts one run of a body that
  cannot repeat. Write allocation laws, stages and diagnostics with it rather
  than reading the counter by hand.
  Outside the existing harnesses, say which harness you built and why it measures the claim.
- Judge a hot kernel under default tiering as well as in FullOpts disassembly.
  A method runs its tier-0 code until call counting promotes it, and promotion
  waits for a spell with no new tier-0 compiles, about a second when the process
  sees one processor; generic-math helpers stay calls at tier 0. A compute kernel
  a request or count enters directly takes
  `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`, while the cheap
  dispatcher above several kernels stays tiered so first use compiles only the
  path taken. Check the tiers with `DOTNET_JitDisasmSummary` in a scratch process
  pinned to one CPU, and record first-call cost beside the steady state.
- Prefer idiomatic code the .NET 10 JIT and libraries recognize.
- Check the folklore section before preserving an old hand-optimization.
- Keep semantic behavior, exception behavior, and readability explicit;
  performance evidence does not silently authorize changing them.
- Record the runtime, build configuration, workload, and the counts or
  disassembly behind any performance claim, plus the `puck bench` result when
  the owner asked for a timing.

## Route adjacent work

| Skill | Route to it when |
|---|---|
| [`maths-usage`](../maths-usage/SKILL.md) | The code sits on a simulation value path. It owns the determinism contract this file defers to, the primitive that is correct, and which tier the change owes. |
| [`maths-laws`](../maths-laws/SKILL.md) | The optimization changes a public `Puck.Maths` member, or moves a value the law suite pins — both are law-or-waiver events. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The optimization changes emulator timing, snapshots, replay, allocation, or Post-stage behavior. |
| [`content-search`](../content-search/SKILL.md) | You need to locate a performance pattern, project property, analyzer opt-out, or repeated workaround textually. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | You are about to delete a hand-optimization the folklore sections retire and need to know what still references it. |
