---
name: maths-usage
description: Selects and verifies Puck.Maths primitives for deterministic simulation values. Use before numeric, random, or geometric simulation code; when reviewing src/Puck.Maths; for fixed-point, rounding, overflow, RNG streams or snapshots, sampling, finite fields, exact arithmetic, hashing, grids, transforms, rates, determinism, or library-discovery questions; and when routing Maths verification. Excludes presentation-only floating point, shaders, renderer or UI math, and presentation seams; maths-laws owns authoring the law suite.
---

# Puck.Maths: choose, obey, verify

Puck.Maths is the deterministic numerics library for values a simulation
advances, compares, hashes, snapshots, or replays. Presentation-only floats,
shaders, renderer/UI math, and explicit presentation conversions are outside
this contract.

## Choose

Before hand-rolling numeric behavior, inspect the library and the README for
the relevant wing. Match the primitive to the job:

- fixed-point scalars, vectors, rotations, transforms, positions, and rates;
- reproducible RNG state, streams, distributions, sampling, and noise;
- grids, index spaces, hashes, space-filling curves, and exact integers;
- modular arithmetic, finite fields, primes, and presented algebra.

Do not substitute a convenient-looking type until its overflow, rounding,
range, equality, serialization, and determinism contract matches the value
path.

## Obey

- Simulation results must be bit-identical for identical inputs.
- Make rounding and overflow policy explicit at narrowing boundaries.
- Treat RNG state and stream identity as simulation state. Preserve draw order;
  do not share or reseed streams casually.
- Keep presentation conversions at the presentation seam.
- Gate a `PDEP`/`PEXT` path on `BitManipulation.HasFastParallelBits`, never
  `Bmi2.IsSupported`: AMD before Zen 3, the Steam Deck's Zen 2 included, and
  Hygon's licensed Zen 1 run them in microcode at up to hundreds of cycles.
- Do not infer a primitive's contract from its name; verify its source, XML
  documentation, and tests.

## Prime counting and selection

These rules govern `PrimeCountingFunction`, `NthPrime`, `TryRandomPrime` and
the kernels under them ([prime exploration](../../../docs/reference/prime-exploration.md)):

- They run on one core. Never propose threads, tasks or GPU work for them.
- They target Intel and AMD across modern cores. AVX2 is the floor that gets
  tuning and AVX-512 is the upper path; the 128-bit and scalar rungs stay as
  correct, lawed fallbacks, never deleted and never refused. Gate on
  capability, never on vendor.
- Cost models assume 32 KiB of L1D and 512 KiB of L2 per core and no help
  from L3: data that misses L2 costs as memory traffic. Derive constants from
  the algorithm or the vector width; never fit one to a single chip's caches.
- A table may make the work faster; it may not let the work be skipped.
  Structural tables (wheel masks and steps, the base-prime table below 65536,
  presieve patterns, witness constants) are fine. Tables that encode answers
  over the query range (π at checkpoints, n-th prime lists, prime or gap lists
  over large ranges, zeta zeros) are not. Verified bounds such as the maximal
  prime gap below 2^64 are facts, not shipped data.
- Raw kernels the laws call are public checked shells over internal cores: the
  shell validates its preconditions and instruction set in release builds, and
  hot loops call the core.
- Judge a change by counted work, JIT disassembly and llvm-mca (znver4 for the
  AVX-512 path; znver2 and alderlake for the AVX2 path), then hardware
  counters where available; time it only when the owner asks.
- Measured dead ends, not to re-propose: a cutoff z above y, VPOPCNTQ from
  C#, fusing the semiprime stream into the hard-leaf sweep, per-pair rework of
  the medium-prime sieve entry, walk-to-next-prime selection (it weights each
  prime by its preceding gap), window-sieve uniform selection, the
  Õ(√N) or analytic counting methods below 2^64, a masked (branchless) step
  for the lazy Lucas trace ladder below 2^61 (selection 1% slower; the
  centered and exact ladders select), moving the lazy ladder's trace
  subtrahend out of RDX (its loop is 2% slower with any other order; the
  masked ladder does move it), the centered Lucas ladder below 2^61 (17%
  slower than the lazy one), the centered batch below 2^60 (30% slower than
  the lazy one), a signed widening multiply (`Math.BigMul(long, long)`,
  `Int128`) in a centered reduction (its low word goes through memory), and
  the centered cross product formed factor first (the Lucas ladder about 14%
  slower).
- Centered (signed) Montgomery reduction is the base-two batch's arithmetic
  between 2^60 and 2^62, the Lucas ladder's between 2^61 and 2^62, and the
  single base-two round's below 2^62; `ScaledResidueRing64`'s
  `ReduceCentered` and `ReduceCenteredSquare` carry its interval proofs, and
  `prime-field.centered-reduction-interval-vs-big-integer` checks each result's
  interval as well as its congruence, since a dropped correction of a negative
  factor stays congruent. Its square forms the factor before the high word and
  measures faster that way in every chain even where the JIT then gives the
  factor a stack slot; reading stack traffic alone would choose wrongly.
- llvm-mca models no branch mispredicts, and its znver4 model rates a single
  chain's shift-fused lazy step at 18 cycles against 14 for the masked one, where Zen 4 counters and timing favor the shift; it also
  models no memory renaming, so it misjudges a loop's in-loop spill (one
  centered loop reads 4 cycles). Weigh both against counters.

## Branded members

A member carrying `[VerifiedCode("id", …)]` is settled: proven correct over its
whole input space. `VerifiedCode.json` records a fingerprint sealing the source
that proof was read against — the member's own declaration plus the declarations
its entry names under `dependencies`, one level deep and listed by hand. Editing
anything under the seal fails the build with **VER001** and the recomputed hash.
Do not silence that by deleting the attribute — re-establish the basis, update
the manifest hash, and say why in the commit; or if the member should stop being
branded, remove the attribute and its manifest entry together. Inspect the
current declarations and read each entry's `basis`, `argument`, and
`dependencies` before concluding a change keeps the proof intact.

## Verify

The floor is one command — `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release`,
under thirty seconds. It is the Default tier, and the only one carrying the
coverage ratchet and both leg gates, so every change owes it and most changes
owe nothing more. Run the Deep tier (`--explicit on --filter-trait tier=Deep`) before committing;
the Exhaustive tier on demand only. No tier measures time; cost is `puck bench`'s.

**Do not reflexively run the `exhaustive` tier.** It takes minutes; narrow it to one law with
`--explicit on --filter-display-name '<law-id>*'` (maths-laws owns the filter's details). Run it only when the member you touched names it as its gate of
record. A public API change also requires a law or precise waiver
under `maths-laws`. Report the exact commands, and distinguish failures caused
by the change from pre-existing ones.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) for
the complete primitive catalogue, sampling boundaries, finite-field and
presented-algebra guidance, determinism traps, exact verification commands,
tier routing, or governance.

## Route adjacent work

| Skill | Use when |
|---|---|
| [`maths-laws`](../maths-laws/SKILL.md) | Authoring or changing a Maths law, subject, oracle, leg, waiver, or public-member classification. |
| [`content-search`](../content-search/SKILL.md) | Finding textual occurrences, candidate hand-rollings, tokens, or patterns across the tree. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | Answering semantic C# questions about references, implementers, dead code, renames, or deletion safety. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | Verifying a Maths change that reaches emulator code and its dedicated batteries. |
