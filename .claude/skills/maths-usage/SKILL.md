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
  Õ(√N) or analytic counting methods below 2^64, a branching trace ladder at
  any size (the signed masked ladder is 3 to 6% faster than the lazy branching
  one it replaced below 2^61), the signed batch below 2^60 (selection 7 to 18%
  slower than the lazy one), a signed widening multiply (`Math.BigMul(long,
  long)`, `Int128`) in a signed reduction (its low word goes through memory),
  the signed product's high word formed as the high multiply less its
  corrections (the JIT homes it in RDX and spills it; form it as the negated
  corrections plus the high multiply), the signed square formed high word first
  (spill-free, but the ladder is 8% slower: its critical multiplies must issue
  first), a vector selection filter (40 to 128 primes: within noise on AVX-512,
  5 to 18% slower per selected prime on AVX2, where most candidates leave at the
  first divisor), and above 2^63 a borrowed trace ladder (every trace a word and
  a borrow mask: 80 instructions a step against 53, 7% slower), a branchless
  result chain in the borrowed base-two walk (14% slower than branching on the
  bit, whose mispredicts resolve ahead of the chain), and the borrowed walk
  below 2^63 (48% slower than the signed seeded one).
- Signed Montgomery reduction is the arithmetic of the single base-two round,
  the Lucas ladder and the base-two batch below 2^63 (the batch from 2^60);
  `ScaledResidueRing64`'s `ReduceSigned`, `ReduceSignedSquareSubtract` and
  `ReduceSignedSquare` carry its interval proofs, and above 2^63
  `ReduceBorrowedSquare` carries the single round's squares as a word and a
  borrow mask. `prime-field.signed-reduction-interval-vs-big-integer` checks
  each result's interval as well as its congruence, since a dropped correction
  stays congruent. Every factor is read unsigned; the doubling shifts the
  inverse, not the operand. The squares and the cross product form the factor
  before the high word, and measure faster that way even where the JIT then
  gives the factor a stack slot; reading stack traffic alone would choose
  wrongly.
- Above 2^63 the ladder and the batch fold after every product, and the JIT's
  compare-and-mask fold (`setb`, `movzx`, `neg`, `and`, `add`) sits on the
  critical path where a conditional move would cost one cycle; that is the
  measured remaining gap for primes there (about 30% behind machine-prime's
  second strong round). Composites there are at parity, and random words
  about 9% behind, the primes' share.
- `IsPrime(ulong)` trial-divides by the 128 odd primes through 727 before
  Baillie–PSW, a depth derived from the round's cost and the vector's (see
  `PrimeKernels.TrialDivisionPrimeCount`); it stays one table with the
  selection filter and factorization. The vector rungs fold the word onto a
  32-bit value per prime (balanced 16-bit digits times the residues of 2^16i,
  VPMADDWD, plus an offset) and run the narrow 32-bit inverse test: 8 to 19%
  faster than three-VPMULUDQ 64-bit products on AVX2 and no slower than
  VPMULLQ on AVX-512. `IsPrime(uint)` reads the odd-prime
  bitmap below 65536, tests the same primes with 32-bit inverses (a narrow
  table beside the wide one: 64-bit products on AVX2 cost three multiplies for
  four lanes, and the narrow path measured slower with them) and runs the word
  decision's signed seeded round before its pseudoprime table; the branching
  narrow round it replaced mispredicted on about half its bits.
- llvm-mca models no branch mispredicts, and its znver4 model rates a single
  chain's shift-fused lazy step at 18 cycles against 14 for the masked one, where Zen 4 counters and timing favor the shift; it also
  models no memory renaming, so it misjudges a loop's in-loop spill (one
  centered loop read 4 cycles). Weigh both against counters.

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
