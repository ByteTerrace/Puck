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
  counters where available; wall-clock time is evidence read beside those
  counts, never alone.
- Measure tiered code. A harness that pins itself to one CPU at high priority
  before it warms up starves the runtime's background tiering thread, and the
  run then times Tier0 code (four times slower, with swings between runs): warm
  up unpinned, sleep, then pin, and check each hot method's tier with
  `DOTNET_JitDisasmSummary=1`. AMDuProfPcm waits on its console when a killed
  run left the counters programmed; launch it with `-r` and stdin closed, and
  read its per-interval rows, since `-C` reports nothing for a launched
  program. Two builds whose hot loops match instruction for instruction can
  still differ by 1 to 3% in cycles; padding the code heap does not move it, so
  read instructions retired beside the cycles.
- Measured dead ends, not to re-propose: a cutoff z above y, VPOPCNTQ from
  C#, fusing the semiprime stream into the hard-leaf sweep, per-pair rework of
  the medium-prime sieve entry, walk-to-next-prime selection (it weights each
  prime by its preceding gap), window-sieve uniform selection, the
  Õ(√N) or analytic counting methods below 2^64, a branching trace ladder at
  any size (the signed masked ladder is 3 to 6% faster than the lazy branching
  one it replaced below 2^61), the signed batch below 2^60 (selection 7 to 18%
  slower than the lazy one), an `Int128` product in a signed reduction (its
  low word goes through memory), a vector selection filter (40 to 128 primes: within noise on AVX-512,
  5 to 18% slower per selected prime on AVX2, where most candidates leave at the
  first divisor), and above 2^63 a borrowed trace ladder (every trace a word and
  a borrow mask: 80 instructions a step against 53, 7% slower), a branchless
  result chain in the borrowed base-two walk (14% slower than branching on the
  bit, whose mispredicts resolve ahead of the chain), and the borrowed walk
  below 2^63 (48% slower than the signed seeded one); and, for the exact
  ladder's selects, a plain ternary inside the loop (the JIT branches it: 63%
  slower), the ladder fully unrolled behind a computed entry so that every
  select if-converts (all 252 are conditional moves, but 924 stack accesses
  leave it only 3% faster than the masks), and the non-inlined select on both
  of a reduction's subtractions (270 to 297 ns against 247 ns for the final one
  alone; it moves with code layout); and two exact ladders interleaved for
  selection above 2^63 (23 to 28% faster per ladder in isolation, but selection
  needs only the first base-two survivor's ladder, so a pair costs about 1.7
  ladders' time for one answer); and `[SkipLocalsInit]` on the speculative
  selector (it drops the prologue's zeroing of the batch block, yet selection
  measures 1 to 3% slower in every band).
- No hardware 64-bit divide runs per candidate on the selection or decision
  path, and each removal was bit-identical to the divide it replaced:
  `ScaledResidueRing64.RadixResidue` takes `2^64 mod n` as the carrier's
  negation of `n` above 2^63, of `2n` with one fold from 2^62, and from 2^15
  as a binary64 estimate of `(2^64 - 2^14) / n` truncated by the raw
  CVTTSD2SI, multiplied back and folded once by a select (its remarks carry
  the bound; the divide survives only below 2^15, out of line, where no prime
  kernel goes); Selfridge's search reads the residues modulo 5, 7, 11, 13 and
  15 from one constant-divisor remainder by 15015 and skips `D = 9`, whose
  symbol is never -1 (`PrimeKernels.SelfridgeDiscriminant` exposes the chosen
  `D`, which a prime's verdict cannot show); and `BoundedRandomSampling.Range64`
  derives Lemire's threshold on the first draw whose low product is below the
  bound. Per selected prime below 2^63 that is about 9.3 DIV r64 down to about
  0.5: the reciprocal digit's two remainders by the odd part of `Q` (about 0.4,
  from `D = -11` on) and the search loop from 19 (one search in sixteen) are
  what remains. On the 7800X3D, whose divider is fast, the three removals
  together measure 0.4 to 1.3% slower per selected prime at every band from
  2^33 to 2^64 (ten-round round-robin of the base and each step's build,
  paired medians; the three builds sit within 0.5% of one another), and 0.7
  to 1.5% slower against the `BigMul` kernels on .NET 11 (six alternating
  rounds, one-sided), inside the 1 to 3% that code layout alone moves but
  consistent in sign; `IsPrime(ulong)` moves under 0.5%; llvm-mca models
  the four-lane radix setup at 170 cycles with four divides against 61 with
  the estimate on znver2, 116 against 52 on skylake and 84 against 47 on
  alderlake. Dead ends measured on the way: a doubling loop from `2^b - n`
  (four doublings cost what the estimate costs, one what the fold costs), the
  managed saturating conversions (a NaN mask, an overflow select and two spills
  in the batch core, 1.5 to 2.5% per selected prime from 2^33 to 2^60), and a
  bias subtracted from the quotient after the divide instead of folded into
  the dividend. AMDuProfPcm's per-interval instruction counts on a loaded host
  differed 6% between intervals of one run, so counts are read from the JIT
  listings, not the counters.
- Every REDC in the prime kernels is one `Math.BigMul` for the product, one
  truncated multiply for the factor and one `MultiplyHigh` for the correction:
  three multiplies. .NET 11 lowers `Math.BigMul(ulong, ulong, out ulong)` to
  one `MUL` and the signed overload to one one-operand `IMUL`, both halves in
  registers (dotnet/runtime #117261); .NET 10 handed the low half back through
  memory, which is why the kernels once spent a fourth, truncated multiply on
  it. Against that four-multiply shape, on .NET 11, selection measures 9 to 16%
  faster per selected prime from 2^33 through 2^63 and 4% at 2^64, and
  `IsPrime(ulong)` 14% faster on primes and 12 to 14% on random odd words over
  the same bands (3 to 4% at 2^64), with the lazy and signed walks at
  12 multiplies per four-lane step instead of 16. On
  .NET 10 the same `BigMul` form measures 0 to 3% slower, so the build's major
  decides the shape. The four-chain walk is bound by multiplier throughput and
  rewards fewer multiplies even when a half crosses the stack; the single-chain
  ladder is latency-bound and gains uops more than cycles.
- The speculative selector's Lucas confirmations read each survivor's inverse
  and radix residue from the batch block the base-two kernel filled, unchecked:
  a checked read there makes the JIT clone the decision loop, which measured
  up to 0.6% slower at the top of the carrier.
- Signed Montgomery reduction is the arithmetic of the single base-two round,
  the Lucas ladder and the base-two batch below 2^63 (the batch from 2^60);
  `ScaledResidueRing64`'s `ReduceSigned`, `ReduceSignedSquareSubtract` and
  `ReduceSignedSquare` carry its interval proofs, and above 2^63
  `ReduceBorrowedSquare` carries the single round's squares as a word and a
  borrow mask. `prime-field.signed-reduction-interval-vs-big-integer` checks
  each result's interval as well as its congruence, since a dropped correction
  stays congruent. Every factor is read unsigned; the doubling shifts the
  inverse, not the operand. Each product is one signed `Math.BigMul`, whose
  high word is already signed, so no sign mask corrects it.
- Above 2^63 the ladder and the batch fold after every product, and the JIT's
  compare-and-mask fold (`setb`, `movzx`, `neg`, `and`, `add`) sits on the
  critical path where a conditional move would cost one cycle; that is the
  measured remaining gap for primes there (about 30% behind machine-prime's
  second strong round). Composites there are at parity, and random words
  about 9% behind, the primes' share.
- RyuJIT if-converts a select only outside loops (its pass refuses a block
  that is hot or reachable from itself), so a ternary inlined into a ladder's
  loop becomes a branch, never a conditional move. The one way to a CMOV on a
  loop's critical path is a call: `ScaledResidueRing64.SubtractBySelect` is a
  `NoInlining` leaf that compiles to `cmp`, `cmovb`, `ret`, and the exact
  ladder's `ExactArithmetic.ReduceSubtract` ends in it, on its final subtraction
  only: 16% off the isolated ladder, nothing below 2^63. A call clobbers every
  volatile register, so the exact walk carries one selection word (the Gray
  bits and the step count together) and leaves the pair unswapped for a caller
  that reads it symmetrically; that keeps the term in flight in a callee-saved
  register. `Subtract` stays the inlined mask for everything else. Re-run the select
  probe (see [open items](../../../docs/plans/open-items.md)) on each new .NET
  major, and when in-loop if-conversion lands, before changing this.
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

## Verify

The quick check is one command — `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release`,
under thirty seconds. It runs the Default tier, which carries the coverage
ratchet and both leg checks: a new public Maths member fails it until
`maths-laws` classifies the member with a law or a precise waiver. The Deep
tier (`--explicit on --filter-trait tier=Deep`) and the Exhaustive tier are
optional; Exhaustive takes minutes, so narrow it to one law with
`--explicit on --filter-display-name '<law-id>*'`. No tier measures time; cost
is `puck bench`'s. Report the exact commands you ran.

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
