---
name: dotnet10-performance
description: Applies .NET 10 performance behavior when writing, reviewing, refactoring, or optimizing this repository's C# code. Use for hot paths, regressions, benchmarks, micro-optimization, collection, string, JSON, SIMD, interop, allocation, code-generation, Native AOT, or trimming questions and claims that a C# pattern is slow. Prefers measurement and idiomatic net10.0 code while preserving semantics. Determinism outranks throughput on simulation value paths, where maths-usage owns primitive choice; subsystem skills own their verification gates.
---

# .NET 10 performance

This skill is factual, not an architecture or style mandate. Measure before
changing clear code, benchmark the actual target, and distinguish runtime
improvements from workload-specific evidence.

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
  describes an upgrade decision this tree has already made. Benchmark before
  and after on the actual target.
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

## Determinism outranks throughput

`CLAUDE.md` rule 4 binds every value a simulation advances, compares, hashes,
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
- **`Puck.Maths` has a measurement harness; the rest of the tree does not.**
  `puck bench` is the microscope — disassembly, allocation columns, percentiles
  — documented in [`src/Puck.Cli/README.md`](../../../src/Puck.Cli/README.md),
  which also carries the measurement hygiene: numbers taken on a busy machine
  are garbage rather than merely pessimistic, and two runs disagreeing by more
  than ~10% mean the machine was not quiet. The pass/fail counterpart is the law
  suite's Bench tier in
  [`tests/Puck.Maths.Tests/README.md`](../../../tests/Puck.Maths.Tests/README.md).
  Outside `Puck.Maths`, say which harness you built and why it measures the
  claim.
- Prefer idiomatic code the .NET 10 JIT and libraries recognize.
- Check the folklore section before preserving an old hand-optimization.
- Keep semantic behavior, exception behavior, and readability explicit;
  performance evidence does not silently authorize changing them.
- Record the runtime, build configuration, workload, and before/after result
  for any performance claim.

## Route adjacent work

| Skill | Route to it when |
|---|---|
| [`maths-usage`](../maths-usage/SKILL.md) | The code sits on a simulation value path. It owns the determinism contract this file defers to, the primitive that is correct, and which tier the change owes. |
| [`maths-laws`](../maths-laws/SKILL.md) | The optimization changes a public `Puck.Maths` member, or moves a value the law suite pins — both are law-or-waiver events. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The optimization changes emulator timing, snapshots, replay, allocation, or Post-stage behavior. |
| [`content-search`](../content-search/SKILL.md) | You need to locate a performance pattern, project property, analyzer opt-out, or repeated workaround textually. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | You are about to delete a hand-optimization the folklore sections retire and need to know what still references it. |
