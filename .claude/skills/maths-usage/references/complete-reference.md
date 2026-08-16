# Maths-usage complete reference

This preserves the full primitive-selection, determinism, verification, and
governance contract. Read the relevant section when the compact skill routes
here.

## Contents

- [First contact](#first-contact)
- [Choose the primitive](#1-choose-the-primitive)
- [Determinism contract](#2-obey-the-determinism-contract)
- [Verification](#3-verify)
- [Governance](#4-governance)

Factual and procedural only — which primitive to reach for, which rules bind,
and how to prove a change. It does not design your code. The user's current
instruction outranks it: if this file argues against a change you were asked to
make, it is stale — update it in the same change and say so.

## First contact

Puck.Maths is the engine's **deterministic numerics** library: a leaf project
with no dependencies whose promise is that the same inputs return the same bits
on every machine, backend and run, so a simulation built on it can be recorded,
replayed and compared byte for byte. It replaces floating point on the
simulation value path with binary fixed-point scalars, vectors, rotations and
world positions; reproducible randomness that can be saved mid-sequence and
resumed; exact finite fields; exact integer and real-quadratic arithmetic; and a
presented-algebra tier for graph, lattice and language questions. It is
organized as **seven wings** — one folder and one README each — plus a set of
root-level types. The **human** entry point (prose, worked examples, the full
root catalogue) is
[src/Puck.Maths/README.md](../../../../src/Puck.Maths/README.md); this file is the
agent's.

| Your question | Wing | What lives there | Read first |
|---|---|---|---|
| Which number type? How does this round, wrap or saturate? A position, a velocity, a rotation, a transform, a rate integrated over ticks | [FixedPoint](../../../../src/Puck.Maths/FixedPoint/README.md) | Signed and unsigned Q48.16, the unit-interval fractions, vectors, complex/dual/split, quaternions, rigid transforms, `FixedPosition`, the rate accumulators, and the fused-arithmetic and text substrate | "Choosing a scalar", then "Load-bearing invariants" |
| Randomness of any shape — a roll, a weighted pick, noise, scatter, a shuffle, a Monte Carlo estimator — or a seed / stream / snapshot question | [Sampling](../../../../src/Puck.Maths/Sampling/README.md) | `Pcg32XshRr`, `WeightedSampler`/`AliasTable<T>`, `FieldNoise`, `LowDiscrepancy`, `DigitalNetSampler`, `StratifiedShuffle`, `InvertibleBitMix`, `ConeDirectionTable`, `SecureRandom`, `ProbabilityFunctions` | "Choosing a primitive", then "The rules a consumer inherits" |
| Arithmetic that must be **exact** — mod p, `GF(2^k)`, region arithmetic over a span of field values, a primality decision | [FiniteFields](../../../../src/Puck.Maths/FiniteFields/README.md) | `BinaryPolynomial`, `BinaryField<T>` (k = 1…128 over a packed carrier), the canonical `BinaryFields` moduli, `PrimeField64`, `QuadraticExtensionField64` | "At a glance", then "Primality on `ulong`" |
| One relation over several carriers; "is this construction a special case of something the library already has?" | [Algebra](../../../../src/Puck.Maths/Algebra/README.md) | `QuadraticAlgebra<TScalar>`, `GeometricAlgebra`/`Multivector`, `MonogenicAlgebra<TScalar>`, `DoublingAlgebra<TInner>`/`IConjugationRing<TSelf>` | "At a glance" — then §4 here before proposing a collapse |
| Grids and index spaces — hex cells, tile/chunk order, rings and shells, an exact 2×2 integer transform | [Geometry](../../../../src/Puck.Maths/Geometry/README.md) | `HexagonalCoordinate`, `HilbertCurve`, `LayerSequence`, `ModularTransform` | "At a glance" |
| The subject is a graph, a lattice, a language or a finite structure rather than a number — reachability, convolution and inversion, pattern matching, homology, group orbits | [Oracle](../../../../src/Puck.Maths/Oracle/README.md) | One presented-algebra product over swappable materials: `Presentations`, `PresentedAlgebra`, `DivisibilityAlgebra`, `TokenPattern`/`PatternMatcher`, `ExteriorCalculus`/`IntegerHomology`, `ReflectionSystem`/`PresentedGroup` | "Choosing an entry point", then "Contracts every consumer inherits" |
| An exploratory or open-problem question — continued-fraction tails, Sturmian and quasicrystal words, Fibonacci and metallic means, real-quadratic orders | [Research](../../../../src/Puck.Maths/Research/README.md) | Exact, certificate-bearing, off the hot path; a budget-exhausted search (`SearchLimitReached`) is never conflated with a proof or a counterexample | "At a glance" — and the namespace split stated above it |
| A per-tick state hash, an exact allocation over intervals, bucket routing, an exact real-quadratic value, a bit trick or a GCD | root level (owns no wing) | `Fnv1aHash`, `DiscreteMeasure`/`CompiledDiscreteMeasure64`, `MonotonicPartitioner`, `QuadraticSurd`, `ContinuedFraction`, `CyclicRotation`, `SymmetryLattice`, `NumberTheoryFunctions`, and the integer kit (`BinaryIntegerFunctions`, `UnsignedNumberFunctions`, `PrimeExtensions`) | [the root README](../../../../src/Puck.Maths/README.md) — "The root-level catalogue", then "Integer routines" |

**Depth lives in the wing READMEs**, and this skill never restates their
contract tables or re-argues a rule they own. When one disagrees with this file,
**the wing wins**.

**Namespace.** Everything is flat `Puck.Maths`, except part of `Research/`,
which is `Puck.Maths.Research` — that wing's README lists which side each type
is on.

---

## 1. Choose the primitive

### Scalars

| The quantity is… | Use | Not |
|---|---|---|
| Signed, has an integer part — position, velocity, accumulated advance | `FixedQ4816` (Q48.16, `long` carrier) | a `float`/`double`; a bare `long` scaled by hand |
| The same, but genuinely cannot be negative | `UFixedQ4816` (UQ48.16) | `FixedQ4816` plus an assertion |
| A fraction in `[0, 1)` — blend factor, normalized coordinate, sub-pixel offset | `UnitFraction16` (2⁻¹⁶) or `UnitFraction32` (2⁻³²) | a Q48.16 you promise stays under one |
| A value that must be able to *reach* 1 — probability, certainty, membership, a weight meaning "all the way" | `UnitInterval32` (closed `[0, 1]`, `ulong` carrier) | `UnitFraction32` — it has **no** `One`, no `MultiplicativeIdentity`, no `++`, by design |
| A count, an index, a tick, a bitfield, a raw carrier you are about to reinterpret | a plain integer | a fixed-point type |

**Choose signedness deliberately.** Helpers constrained to `IUnsignedNumber<T>`
accept only the non-negative types, so the choice propagates. The full decision
is [Choosing a scalar](../../../../src/Puck.Maths/FixedPoint/README.md#choosing-a-scalar).

**A raw `long` is the wrong type** whenever the value has a scale. A hand-scaled
integer carries its exponent in a comment, so its rounding, its overflow
behaviour and its text form are all re-invented locally and none of them are
gated. The one legitimate raw is a value crossing a byte boundary — a snapshot,
the `Puck.Scripting` addon ABI — and it round-trips through
`FixedQ4816.FromRawBits` / `.Value`, never through arithmetic.

**`UnitInterval32` costs a thirty-third bit and buys three things** a Q0.32
cannot have: a multiplicative identity, exact absorbing elements at both ends,
and closure of `Multiply` (one rounding, ties to even) over the whole interval.
It has **no arithmetic operators at all** — every combining operation is a named
method (`Multiply`, `AddSaturating`, `SumExcess`, `Complement`, `Min`, `Max`),
because `UnitFraction32` shares the grid and reads `~`, `+` and `-` differently.
`FromUnitFraction32` carries a sampler draw in exactly; `TryToUnitFraction32`
carries it back whenever it is still below one.

### Vectors, rotations, transforms, positions

| You need | Use |
|---|---|
| A 2D/3D displacement | `FixedVector2` / `FixedVector3` |
| A 2D rotation | `FixedComplex` (`FromAngle`, `*` composes, `Rotate` applies) |
| A 3D rotation | `FixedQuaternion` (`FromAxisAngle`, `Slerp`, `FromTo`) |
| A rotation *and* a translation as one object | `FixedRigidTransform` (a unit dual quaternion; `ScLerp` interpolates the screw) |
| A world position beyond a small scene | `FixedPosition` — 64-bit cell indices plus a centred local offset, the floating-origin coordinate. A `FixedVector3` alone is a **displacement**, not a position |
| A scaling / rate-composition flow | `FixedSplit` (`j² = +1`, `FromRapidity`) |
| A forward-mode sensitivity | `FixedDual<FixedQ4816>` (`FixedDual.Variable` seeds it) |
| Rate → quantity over ticks, with no drift | `FixedRateAccumulator` / `FixedVector3RateAccumulator` — the sub-unit division remainder carries across calls, and **is authoritative snapshot state** |
| To interpolate or clamp | `FixedQ4816.Lerp` / `.Clamp`, `FixedVector2.Lerp` / `FixedVector3.Lerp`, `FixedQuaternion.Slerp`, `FixedRigidTransform.ScLerp`. Never a hand-rolled `a + (b − a)·t` |

Products in this family are **fused**: the whole expression accumulates exactly
and the result rounds once per returned component. Do not decompose one into
pairwise operations.

### Sampling — one shape of randomness each, and they do not overlap

| You need | Use | State? |
|---|---|---|
| Sequential draws over time — rolls, wander, decisions | `Pcg32XshRr` | **Yes** — it is simulation state |
| A weighted pick | `WeightedSampler.Create` once at load → `AliasTable<T>.Sample` (O(1), exactly two advances) | Immutable table |
| Spatial randomness — terrain, wind, per-cell decisions | `FieldNoise` (`Sample`, `SampleGradient`, `Hash`) | None |
| Points that merely spread out — scatter, placement | `LowDiscrepancy.R1` / `R2` | None |
| Stratification as a **theorem** — Monte Carlo, area lights, anything averaged | `DigitalNetSampler` | None |
| Security-sensitive draws | `SecureRandom` — and **never** in simulation state; it is deliberately non-reproducible | Hidden platform state |
| A permutation of a list | `Pcg32XshRr.Shuffle` (in-place Fisher–Yates) | Uses the generator's state |

`LowDiscrepancy` and `DigitalNetSampler` have the same shape (index in, point
out, no state). The net's guarantee is strictly stronger: the additive
recurrences equidistribute asymptotically and stratify *nothing* exactly. Reach
for the net only when the estimator's error actually depends on stratification.

**One stream per system**, derived from the run's master seed with a small
consecutive id (`Pcg32XshRr.Create(state: masterSeed, stream: id)`); persist
`State`/`Increment`/`Multiplier` with the world and restore through
`FromRawBits`; never build `Advance`-based seeking on a rejection-sampling draw
(`NextUInt32(min, max)`, `Shuffle`). Those three, and the fourth beside them
(alias tables are order-sensitive), are the wing's own contract; the argument
for each is at
[the rules a consumer inherits](../../../../src/Puck.Maths/Sampling/README.md#the-rules-a-consumer-inherits).

### Grids, index spaces, hashing, exact integers

| You need | Use | Wing |
|---|---|---|
| A hex grid whose 60° rotations are exact | `HexagonalCoordinate` (Eisenstein integers) | [Geometry](../../../../src/Puck.Maths/Geometry/README.md#hexagonalcoordinate) |
| Locality-preserving tile/chunk order | `HilbertCurve`, not Morton (`BinaryIntegerFunctions.BitwisePair`) | [Geometry](../../../../src/Puck.Maths/Geometry/README.md#hilbertcurve) |
| Rings, shells, shards — index → layer in constant time | `LayerSequence` | [Geometry](../../../../src/Puck.Maths/Geometry/README.md#layersequence) |
| A per-tick state hash for a determinism or replay probe | `Fnv1aHash` — allocation-free, endianness-independent | root level |
| An exact integer allocation over intervals (jobs/frame, samples/frame) | `DiscreteMeasure` → `CompiledDiscreteMeasure64` for the hot path | root level |
| An exact real-quadratic value | `QuadraticSurd` | root level |
| Bit tricks, GCD, integer roots, pairing, factorization | `BinaryIntegerFunctions`, `UnsignedNumberFunctions`, `PrimeExtensions` | root level |
| One relation over several carriers, or a proof two carriers agree | `QuadraticAlgebra<TScalar>` and the structure tier | [Algebra](../../../../src/Puck.Maths/Algebra/README.md) |

### Finite fields

`BinaryField<T>` is `GF(2^k)` for k in 1…128 over a bare packed carrier
(`byte`…`UInt128`), so a region of field values is just a span; the canonical
minimum-weight moduli are `BinaryFields.Degree8/16/32/64/128`.
`BinaryPolynomial` is the `GF(2)[t]` ring beneath it. `ReedSolomon` sits on
`BinaryField<T>` and does systematic coding over any of them — generator,
check symbols, syndromes — so an error-correcting consumer builds a code rather
than a field of its own; a consumer whose standard names a modulus the catalog
does not carry constructs it with `BinaryField<T>.Create`, which precomputes
nothing. `PrimeField64` is `F_p` for an odd prime below 2⁶², with
`QuadraticExtensionField64` for `F_{p²}`.

Field products are **exact** — associative, commutative, distributive, with an
exact inverse for every non-zero element — so unlike a rounded fixed-point
product they *are* safe to reassociate. `PrimeField64.IsPrime` is this library's
exact decision for every `ulong`; `IsStrongProbablePrime`,
`IsStrongLucasProbablePrime` and `IsBaillieProbablePrime` are **probable**-prime
tests and are contracted as such.

### The presented-algebra tier (`Oracle/`)

Reach for it when the question is about a **graph, a lattice, a language, or a
finite structure** rather than about a number: reachability, shortest paths,
walk counts, best-probability or bottleneck routes (`Presentations.Quiver` at
the matching material), Dirichlet convolution and Möbius inversion
(`DivisibilityAlgebra`), pattern matching where a language *is* an element
(`TokenPattern` → `PatternMatcher`), homology with torsion (`ExteriorCalculus`
→ `IntegerHomology`), group words and orbits (`ReflectionSystem` →
`PresentedGroup`). One product serves all of them; the material chooses the
arithmetic. `PresentedAlgebra.Residual` is the one derivative operator with
three twists — `Counit` gives the left quotient (Brzozowski's derivative),
`Identity` gives an ordinary derivation whose unit coefficient *is* `FixedDual`'s
chain rule bit for bit, and `ShiftGenerator` gives the skew step behind
holonomic recurrences — all satisfying one twisted Leibniz rule rather than
three code paths. Entry points and the contracts every consumer inherits:
[Oracle](../../../../src/Puck.Maths/Oracle/README.md#choosing-an-entry-point).

An algebra instance is **not** safe for concurrent use; the presentation it
wraps is immutable and shareable.

---

## 2. Obey the determinism contract

**Determinism pins the mapping, not the values.** Same document + same input →
bit-identical state on every run, machine and backend *at a fixed code version*.
It is not output stability across versions: a deliberate correction to maths is
*expected* to move state hashes. Never preserve a wrong result to keep a hash
stable, and never add a path that reproduces old-wrong behaviour.

- **No floating point in simulation state.** Two boundaries only: authoring in
  (`FromDouble`, a run-document weight) and presentation out (`(double)`,
  `ToVector3`, `ToQuaternion`, `ToComplex`, `FixedPosition.ToRenderRelative`).
  A **seam** is a boundary where a value passes from one world into another, and
  **presentation seams are one-way** — nothing they return flows back into
  state. Which conversions are seams, and why the `double`-taking direction is
  still deterministic, is the
  [FixedPoint wing's opening](../../../../src/Puck.Maths/FixedPoint/README.md).
- **Wrap is the default; saturation and refusal are named.** Bare operators are
  unchecked; `checked` forms throw *after* the operation's rounding;
  `AddSaturating`/`SubtractSaturating` clamp. Some saturators have **no** `Try…`
  sibling, so do not assume a member reports its boundary — the unpaired ones
  are enumerated at
  [load-bearing invariants](../../../../src/Puck.Maths/FixedPoint/README.md#load-bearing-invariants).
- **Ties go to even, and there are exactly three exceptions** — `Exp2`,
  `Log2`/`Atan2`, and `SinCos`. Each says so at the member and the wing argues
  why. Assume ties-to-even everywhere else; do not invent a fourth exception,
  and do not "correct" one of the three.
- **Do not reassociate a rounded product.** `INumber<T>` grants capabilities,
  not a proof that multiplication is associative: `(a·b)·c ≠ a·(b·c)` at some
  operands over `FixedQ4816`. The fused kernels exist so a whole expression
  rounds once, and the suite's divergence canaries fail if one is decomposed.
  Finite-field products are the exception — exact, therefore reassociable.
- **Do not hand-roll what the library provides.** A second implementation can
  silently diverge in rounding, overflow, or floor/ceiling convention. Before
  writing a lerp, clamp, integer square root, GCD, floor division, bit mixer,
  2×2 integer matrix, continued-fraction step, or rate/remainder carrier,
  search for it (route through `content-search` in the main `SKILL.md`). If the
  shape genuinely is missing, add it to the library rather than beside it.
  This failure mode is present in the tree today, and this paragraph is the
  surviving record of the finding: an engine-absorption audit found five
  `AlignUp`/`AlignDown`/`CeilDiv`/`FloorDiv` hand-rollings across
  `Puck.SdfVm`, `Puck.Platform`, and `Puck.World` — one of them,
  `WorldQueryBaker`, hash-bearing, which makes the divergence class
  determinism-adjacent. Their landing site is the `BinaryIntegerFunctions`
  generics, and the migration awaits the owner's go. Re-derive the exact call
  sites with `puck search` rather than trusting the count.
- **Bound an unbounded search from inside it, with two clauses.** A search whose
  termination rests on a theorem rather than on a validation — the Selfridge
  parameter walk, the descent to the smallest non-residue — fails by HANGING,
  and a hang is not a red test. Such a loop owes two guards with **distinct**
  messages: a deterministic step budget, which says the predicate is broken, and
  a wrap guard, which says the range is exhausted. Both, because they catch
  different things: `FirstPrimeAtOrAbove(2⁶⁴ − 40)` returned **3** by stepping
  past the carrier and wrapping — a wrong answer no budget can catch. Prefer a
  deterministic bound to a wall-clock timeout wherever the loop is
  instrumentable; a timeout puts machine speed into a pass/fail verdict. The one
  exception is a guard inside a primitive whose termination rests on its own
  validations, where a budget masks a validation gap instead of exposing it.
- **Results do not depend on ambient culture, locale, or CPU features.** The
  hardware paths (`PDEP`/`PEXT` in the bit routines, the carryless multiply and
  region ladders in the fields) are bit-identical to their portable fallbacks,
  and that equivalence is *gated* — see
  [FiniteFields → Verifying changes](../../../../src/Puck.Maths/FiniteFields/README.md#verifying-changes).
  Parameterless formatting and parsing are invariant; explicit providers are
  honored deterministically.

---

## 3. Verify

There is no repo-wide verification story left to sit on top of: the battery that
owned the engine and document gates is quarantined with `Puck.Post`, and the
skill that routed it went with it. `tests/Puck.Maths.Tests` is the tree's only
unit-test project — a committed four-tier suite — and the tiers below are the
whole machine-checked story a `src/Puck.Maths` change gets.

**Is a contract claim actually tested?**
`tests/Puck.Maths.Tests/LawRegistry.cs` is the executable index of what the
suite proves about every member: every case it runs is declared there with the
public members that case covers and the legs it stands on, so that one file is
the first place to look, and per-member classification — covered and by which
case, waived, or uncovered — is queryable in `coverage-manifest.json` beside it.

**The floor: one command.** Anything under `src/Puck.Maths` owes this, whichever
wing it touched, and in the ordinary case owes nothing else:

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
```

That is the Default tier — Smoke + Default, **about thirteen seconds**. It is
cheap on purpose and it is where the *structural* gates live: the coverage
ratchet and both leg gates carry the `Default` trait, so this run is the only
one that checks a new public member is classified, that legs are declared and
siblings resolve, and the only one that regenerates `coverage-manifest.json`
and `leg-ledger.md`. Skipping it does not save time; it defers a failure that
cannot heal itself once the manifest and the surface disagree.

**The tier ladder.** Higher tiers are opt-in by runsettings and are *not* to be
fired on every change. Their cost is the point: they buy breadth you do not need
in a change loop.

| Tier | Command | Budget | When |
|---|---|---|---|
| Smoke | `--settings tests/Puck.Maths.Tests/smoke.runsettings` | < 2 s | tight inner loop while iterating one kernel; carries **no new evidence** — every row is a declared mirror |
| **Default** | *(bound by default — no `--settings`)* | ~13 s | **every change**, unconditionally |
| Deep | `--settings tests/Puck.Maths.Tests/deep.runsettings` | minutes | **before you commit**, and before any rounding change lands |
| Exhaustive | `--settings tests/Puck.Maths.Tests/exhaustive.runsettings` | long | on demand or nightly; full-width sweeps over an entire carrier |
| Bench | `--settings tests/Puck.Maths.Tests/bench.runsettings` | timing | on demand; breach-tolerant, gates no value |

**Do not run the `Exhaustive` tier or `Puck.Post` reflexively.** They are
minutes-to-many-minutes each and neither supports running a subset. Run one only
when the member you touched names it as its gate of record, which
`coverage-manifest.json` and `Coverage.cs`'s waiver reasons will tell you by
name. When in doubt, check the waiver rather than running the tier.

Then, by what you touched — **only if the member's classification still points
at one of these**:

| You touched | Also run |
|---|---|
| A rounding path, a value kernel, an exhaustive claim | the suite again with `--settings tests/Puck.Maths.Tests/deep.runsettings` — Deep is the tier that has to pass before a rounding change lands |
| `FixedPosition` | `… -- --stage worldcoord3` — the stage kept its pre-rename name; it is the stage a `FixedPosition` change owes |
| `BinaryPolynomial` / `BinaryField` / `BinaryFields` | `… -- --stage binary-field` |
| `DigitalNetSampler` / `StratifiedShuffle` / `InvertibleBitMix` | `… -- --stage digital-net` |
| `MonotonicPartitioner` | `… -- --stage monotonic-partitioner` |
| `BinaryIntegerFunctions`, `SecureRandom`'s refusal edge | `… -- --stage binary-integer-functions` |
| The presented algebra (`Oracle/`) | the `presented.*` law families, which run as Default-tier cases; the Default tier is the gate of record |
| `QuadraticAlgebra` / `MonogenicAlgebra` / `GeometricAlgebra` / `DoublingAlgebra` | the matching law family — see [Algebra → Verifying changes](../../../../src/Puck.Maths/Algebra/README.md#verifying-changes) |
| `PrimeField64` / `QuadraticExtensionField64` | the `prime-field.*` and `extension-field.*` law families |
| Quadratic integer arithmetic | the `quadratic-integer.*` and `algebra.quadratic-*` law families (`laws/quadratic-integer.json`, `laws/doubling-tower.json`) |

**Machine gotcha — `-c Release` must PRECEDE the file path.** In
`dotnet run -c Release wasm/build.cs` the flag comes first or the file
is silently built and run as Debug. This is not cosmetic on the reference
machine: Windows App Control blocks loading never-seen Debug binaries
(`FileLoadException 0x800711C7`), so file-based `dotnet run <script>.cs`
programs fail outright at their default configuration. Release outputs load
cleanly. `puck bench` is unaffected — it runs through `dotnet run --project`,
not a file-based script.

**What green means, and does not.** A green battery means no probe failed. It
does not by itself mean no probe *diverged*: a defect that turns a subject
predicate uniformly false can spin a search helper rather than trip an
assertion, which is why every unbounded search whose predicate is a subject
carries a step budget whose exhaustion is a named failure. Read the exit code
*and* the section output.

**Land a new public member with its classification.** A law case in
`LawRegistry.cs` or a waiver with a reason in `Coverage.WaiverDeclarations`;
never a hand-edit of `coverage-manifest.json`. Without one the coverage ratchet
fails on every run and cannot heal itself — mechanism in
[tests/Puck.Maths.Tests → The ratchet](../../../../tests/Puck.Maths.Tests/README.md#the-ratchet).
Authoring the case or the waiver itself belongs to
[`maths-laws`](../../maths-laws/SKILL.md).

**When the documentation and the behaviour disagree, do not edit the generated
register.** A law that pins behaviour against the member's own XML doc must be
spelled `Leg.PinnedAsObserved`; `tests/Puck.Maths.Tests/leg-ledger.md` is
derived from those declarations on every run, so a row closes only by correcting
the doc (or the code) and re-spelling the leg — mechanism in
[FixedPoint → Verifying changes](../../../../src/Puck.Maths/FixedPoint/README.md#verifying-changes).

**A changed hash after a deliberate correction is not a failure.** Re-run the
tier to prove determinism still holds, and re-record any persisted replays or
baselines the correction invalidates in the same change.

---

## 4. Governance

Whether a specialized primitive may be collapsed into the generic algebra tier
is decided by two standing gates: the generic replacement must carry the **same
correctness and the same performance guarantees**. No retained artifact
establishes the performance evidence behind the existing type boundaries; the
`complex.*`, `split.*`, and `algebra.quadratic-*` law families pin only the
correctness half. Treat retained types as retained. Revisit one only by
remeasuring from scratch, and never collapse one on argument alone.
