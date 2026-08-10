# Puck.Maths

Puck.Maths is the engine's numerics library, and its defining promise is
determinism: give the same inputs to any program built on it — on a Windows
desktop, a Linux build server, an ARM laptop, a Vulkan backend or a Direct3D 12
one — and it returns exactly the same results, down to the last bit. A
simulation built on it can therefore be recorded, replayed, and compared byte
for byte, and the rest of the engine is designed around that property.

Ordinary floating-point arithmetic cannot make that promise across different
machines, so this library provides deterministic replacements for the places
floating point would normally go: **binary fixed-point** numbers (values stored
as scaled integers, so arithmetic is exact and rounding happens only where we
choose) for scalars, vectors, rotations and world positions; **reproducible
randomness** that can be saved mid-sequence and resumed later; **exact finite
fields**; exact integer and real-quadratic arithmetic; and a
**presented-algebra tier** that answers graph, lattice and language questions
through a single product operation. Nothing here reads the clock, the current
culture, or the CPU's feature set: the hardware-accelerated paths return the
same bits as their portable fallbacks.

Three surfaces are deliberately *not* reproducible, and none may be used in
simulation state:

- `SecureRandom`, which draws from the platform's cryptographic generator.
- `ProbabilityFunctions`, a `double` quantile function meant for analysis and
  display.
- `ConeDirectionTable`, which is same-machine only: it builds its table with the
  platform's `Math.Cos`, `Math.Sin` and `Math.Tan`, so two machines can disagree
  in the last bits. Never place a built table in replay state; if you need one
  across machines, generate and version the constants instead of rebuilding
  them.

Each says so under a **Determinism tier** heading in
[Sampling](Sampling/README.md#securerandom).

One more exclusion, narrower but easy to trip over: **`GetHashCode` is not part
of the bit-identical promise.** `QuadraticSurd` and `QuadraticAlgebra` fold
their hashes through the framework's `HashCode`, which .NET randomizes per
process — four runs of the same value give four different numbers, by design
and within the ordinary `GetHashCode` contract. Hashes are for hash tables.
Never use one as a replay fingerprint, a state hash, an ordering key or a
snapshot field; use the value's own components, which *are* reproducible.

Types that fold through `Fnv1aHash` instead — the algebra values among them —
happen to be stable across processes, but do not rely on that either: it is not
a contract, and any of them may move to the framework fold. If you need a stable
digest, take one explicitly.

```text
namespace  Puck.Maths — except Research/, which is Puck.Maths.Research in part
target     net10.0
deps       none — a leaf library, no external packages
```

The hot scalar and vector paths are allocation-free. Generic integer algorithms
sit on `System.Numerics` interfaces, so one implementation serves several widths.
Table construction and large prime-counting operations may allocate or rent.

---

## Orientation

The library is organized as seven **wings** — one per folder — plus a set of
root-level types. Each wing has its own README carrying the full contracts for
its types: what each operation guarantees, the invariants, and how the folder
is verified. Begin with the table below, then follow the link into the wing you
need.

| Wing | What lives in it | Read when you need |
|---|---|---|
| [`FixedPoint/`](FixedPoint/README.md) | The fixed-point scalars — signed and unsigned Q48.16, meaning 48 integer bits and 16 fraction bits, plus the signed Q16.48 that splits the same word the other way for reciprocal quantities — the three unit-interval fractions, vectors, the planar trio (complex, dual, split), quaternions, rigid transforms, the hierarchical world position, the rate accumulators. | Any value a simulation advances, compares, hashes or replays. |
| [`Sampling/`](Sampling/README.md) | The seeded generator, weighted choice, spatial noise, low-discrepancy sequences and digital nets (point sets that spread evenly by construction), and the two non-simulation paths (`SecureRandom`, `ProbabilityFunctions`). | Anything random, scattered, or noisy. |
| [`FiniteFields/`](FiniteFields/README.md) | `GF(2)` polynomials, `GF(2^k)` for k ≤ 128, the odd-characteristic prime field and its quadratic extension, exact primality on `ulong`. | Exact algebra: the arithmetic behind error-correcting codes and checksums, and modular arithmetic generally. |
| [`Algebra/`](Algebra/README.md) | The structure tier: adjoin a root (`QuadraticAlgebra`), add generators (`GeometricAlgebra`), raise the degree (`MonogenicAlgebra`), double the carrier (`DoublingAlgebra`). | A relation chosen at runtime, or a proof that two carriers agree. |
| [`Geometry/`](Geometry/README.md) | Hex grids, the Hilbert curve, layered index spaces, the modular group — exact integer geometry, no fixed point involved. | A grid, a space-filling order, a layered index, a hyperbolic motion. |
| [`Oracle/`](Oracle/README.md) | One configurable algebra with a single product operation, evaluated over eleven **materials** (the number system you evaluate in: `min`/`+`, `max`/`×`, boolean, counting, …), and the worlds built out of it — graphs, geometric algebras, planar tangles, divisor arithmetic, pattern languages. | Reachability, shortest paths, pattern matching, homology, group words. |
| [`Research/`](Research/README.md) | Exploratory exact tools: continued-fraction tails, Sturmian and quasicrystal words, Fibonacci and metallic-mean arithmetic, odd-cyclic incidence, real-quadratic orders. Partly in `namespace Puck.Maths.Research`; that wing says which types. | Research questions, never the hot path. |

The **root level** owns no folder and so no wing: its contracts are in
[the root-level catalogue](#the-root-level-catalogue) at the bottom of this file
— integer routines, exact discrete rates, exact real quadratics, hashing,
routing.

Why the specialized types were kept when the generic `Algebra/` tier could
reproduce them — the two standing gates, the measured evidence, the verdicts —
was the retention-gates write-up, now retired — the gates themselves are the record. Read them
before proposing to collapse one.

---

## What do I reach for?

One line each. Follow the link for the real contract.

| I need… | Reach for | Where |
|---|---|---|
| A probability, a certainty, a weight that can say "all the way" | `UnitInterval32` — the closed `[0, 1]` on a `2⁻³²` grid, with a real `1` | [FixedPoint](FixedPoint/README.md#unitinterval32) |
| A fraction in `[0, 1)` — blend factor, normalized coordinate, sub-pixel offset | `UnitFraction16` (16-bit) or `UnitFraction32` (32-bit). There is no `1.0` in either, by design | [FixedPoint](FixedPoint/README.md#unitfraction16-and-unitfraction32) |
| A general scalar with an integer part | `FixedQ4816` signed, `UFixedQ4816` unsigned. Choose signedness deliberately | [FixedPoint](FixedPoint/README.md#choosing-a-scalar) |
| A direction, a velocity, a displacement | `FixedVector2` / `FixedVector3`. A vector is a **displacement**, never a position | [FixedPoint](FixedPoint/README.md#fixedvector2-and-fixedvector3) |
| A world position at planet scale | `FixedPosition` — a coarse 64-bit cell index plus a small centred local offset, so precision is the same everywhere on the map | [FixedPoint](FixedPoint/README.md#fixedposition) |
| A 2D rotation | `FixedComplex` — `FromAngle`, `*` composes turns, `Rotate` applies one | [FixedPoint](FixedPoint/README.md#fixedcomplex) |
| A 3D rotation | `FixedQuaternion`; add a translation and it becomes `FixedRigidTransform` | [FixedPoint](FixedPoint/README.md#fixedquaternion) |
| To interpolate or clamp | `FixedQ4816.Lerp` / `FixedQ4816.Clamp`, `FixedVector2.Lerp` / `FixedVector3.Lerp`, `FixedQuaternion.Slerp`, `FixedRigidTransform.ScLerp` (the screw) | [FixedPoint](FixedPoint/README.md#fixedq4816) |
| Drift-free rate integration (velocity → position, acceleration → velocity) | `FixedRateAccumulator` / `FixedVector3RateAccumulator` — the division remainder carries across ticks | [FixedPoint](FixedPoint/README.md#fixedrateaccumulator-and-fixedvector3rateaccumulator) |
| A seeded RNG you can snapshot and resume | `Pcg32XshRr` — one stream per system | [Sampling](Sampling/README.md#pcg32xshrr) |
| To shuffle a list reproducibly | `Pcg32XshRr.Shuffle` — in-place Fisher–Yates from the high end down | [Sampling](Sampling/README.md#pcg32xshrr) |
| A weighted pick | `WeightedSampler.Create` once at load, then `AliasTable<T>.Sample` — O(1), two advances | [Sampling](Sampling/README.md#weightedsampler-and-aliastabletelement) |
| Smooth spatial noise | `FieldNoise` — a pure function of `(seed, position)`, nothing to persist | [Sampling](Sampling/README.md#fieldnoise) |
| Points that spread out without clumping | `LowDiscrepancy.R1`/`R2` when "spread out" is enough; `DigitalNetSampler` when you need stratification as a theorem | [Sampling](Sampling/README.md#choosing-a-primitive) |
| Cryptographic randomness | `SecureRandom` — and never in simulation state; it is not reproducible | [Sampling](Sampling/README.md#securerandom) |
| `GF(2^k)` arithmetic | `BinaryField<T>` over a chosen modulus, or the canonical `BinaryFields.Degree8/16/32/64/128` | [FiniteFields](FiniteFields/README.md#binaryfieldt) |
| Error-correction symbols over a binary field, and reading a codeword back | `ReedSolomon.BuildGenerator` once, then `ComputeCheckSymbols` per message and `ComputeSyndromes` to verify | [FiniteFields](FiniteFields/README.md#reedsolomon) |
| Modular arithmetic mod an odd prime, exact square roots, exact primality on `ulong` | `PrimeField64`, and `QuadraticExtensionField64` for `F_{p²}` | [FiniteFields](FiniteFields/README.md#primefield64) |
| Reachability, shortest paths, walk counts, best-probability routes | A `Presentations.Quiver` at the matching **material** — the semiring you evaluate in | [Oracle](Oracle/README.md#choosing-an-entry-point) |
| Pattern matching where a language *is* an algebra element | `TokenPattern` then `PatternMatcher.TryCompile` | [Oracle](Oracle/README.md#the-language-axis) |
| An exact integer allocation over intervals (jobs per frame, samples per video frame) | `DiscreteMeasure`, compiled to `CompiledDiscreteMeasure64` for the hot path | [below](#exact-discrete-rates) |
| An exact real-quadratic value — no floating point, no drift | `QuadraticSurd` | [below](#the-root-level-catalogue) |
| A hex grid whose 60° rotations are exact | `HexagonalCoordinate` | [Geometry](Geometry/README.md#hexagonalcoordinate) |
| Cache-coherent tile/chunk ordering | `HilbertCurve` (locality-preserving) rather than Morton order | [Geometry](Geometry/README.md#hilbertcurve) |
| A layered index space — rings, shells, shards | `LayerSequence` — constant-time index → layer, pure integer | [Geometry](Geometry/README.md#layersequence) |
| One relation over several carriers, or a proof that two number systems agree | `QuadraticAlgebra<TScalar>` and the rest of the structure tier | [Algebra](Algebra/README.md) |
| To fold a per-tick state hash for a determinism or replay check | `Fnv1aHash` — allocation-free, endianness-independent | [below](#the-root-level-catalogue) |
| An envelope that only ever narrows — a capability mask under AND, a budget/ceiling/fuel bound under min, or both paired as one value | `MeetMask64`, `MeetQuantity64`, `MeetProduct<TFirst, TSecond>` — the attenuation meet-semilattices | [below](#the-root-level-catalogue) |
| Bit tricks, GCD, integer roots, pairing functions, prime factorization | `BinaryIntegerFunctions`, `UnsignedNumberFunctions`, `PrimeExtensions` | [below](#integer-routines) |
| An exact square root, modular inverse or modular square root on integers past 64 bits, or a primality decision and factorization exact below the twelve-base witness boundary and refusing past it | `BigIntegerFunctions` — the five whose algorithm changes once the carrier is not a register; everything else generic already serves `BigInteger` | [below](#the-root-level-catalogue) |

---

## Three small programs

Each of these compiles as written; every member is real. They are meant to show
the flavor of the library before you commit to reading a wing README.

**Fixed-point: constructing values and computing with them.**

```csharp
using Puck.Maths;

// Three ways to construct a value. The double form is for authored input: it
// quantizes once, by a deterministic rounding rule, and gives the same answer
// on every machine.
var speed  = FixedQ4816.FromInteger(value: 12);        // 12.0
var half   = FixedQ4816.FromRawBits(value: 1L << 15);  // 0.5, from the raw bits
var tuning = FixedQ4816.FromDouble(value: 0.35);

// Dot accumulates all three products exactly and rounds once at the end.
var velocity = new FixedVector3(X: speed, Y: half, Z: tuning);
var forward  = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);
FixedQ4816 closing = FixedVector3.Dot(left: velocity, right: forward);

// Converting to double is for display only. Converted values must never flow
// back into simulation state.
double forDisplay = (double)closing;
```

**Sampling: a seeded stream you can save and resume.**

```csharp
using Puck.Maths;

// Stream ids are small and consecutive, all derived from the run's master seed.
var loot = Pcg32XshRr.Create(state: masterSeed, stream: 3UL);

// Build the distribution once at load; sampling is O(1) at exactly two advances.
ReadOnlySpan<(string Element, ulong Weight)> drops =
    [("common", 70UL), ("rare", 25UL), ("epic", 5UL)];
var table = WeightedSampler.Create(entries: drops);

string drop = table.Sample(generator: ref loot);

// Generator state IS simulation state. Persist all three words with the world…
var (increment, multiplier, state) = (loot.Increment, loot.Multiplier, loot.State);
// …and a restored generator continues the exact same sequence.
var resumed = Pcg32XshRr.FromRawBits(
    increment: increment,
    multiplier: multiplier,
    state: state
);
```

**Rate integration without drift.**

```csharp
using Puck.Maths;

// A 120 Hz time base. One unit per second, integrated as 120 single-tick steps.
var integrator = new FixedRateAccumulator(ticksPerSecond: 120L);
var travelled  = FixedQ4816.Zero;

for (var tick = 0; (tick < 120); ++tick) {
    travelled += integrator.Integrate(ratePerSecond: FixedQ4816.One, elapsedTicks: 1UL);
}
// travelled is exactly FixedQ4816.One — the sub-unit division remainder carried
// across every step instead of being rounded away 120 times.
// integrator.Remainder is authoritative state: snapshot it with the world.
```

---

## Four rules

These four rules protect the determinism promise. Each is stated briefly here
and explained fully — with the evidence behind it — in the wing that owns it.

1. **No floating point in simulation state.** `double` is admissible at an
   authoring boundary in and a presentation boundary out, and the presentation
   seams are one-way: nothing a renderer or a diagnostic computed may flow back
   into state. Which conversions are seams is enumerated in the
   [FixedPoint wing's opening](FixedPoint/README.md).

2. **One stream per system.** Derive each consumer's `Pcg32XshRr` from the run's
   master seed with its own small stream id; sharing one generator couples
   systems through draw order. This and the three rules beside it (snapshots,
   seek-counts-advances, alias-table ordering) are
   [the rules a consumer inherits](Sampling/README.md#the-rules-a-consumer-inherits).

3. **Do not reassociate a rounded product.** `INumber<T>` grants capabilities,
   not a proof that multiplication is associative — `(a·b)·c` and `a·(b·c)` are
   different values at some operands, and the fused kernels exist so a whole
   expression rounds *once*. Field products in
   [`FiniteFields/`](FiniteFields/README.md) are the exception: exact, therefore
   safe to reassociate. The one-rounding discipline, and the divergence canaries
   that prove it is load-bearing, are argued in the
   [FixedPoint wing](FixedPoint/README.md#load-bearing-invariants).

4. **Do not hand-roll what is already here.** The shapes most often re-rolled
   are already shipped: `FixedQ4816.Lerp` / `.Clamp` (and `FixedVector3.Lerp`,
   `FixedQuaternion.Slerp`, `FixedRigidTransform.ScLerp`),
   `UnsignedNumberFunctions.SquareRoot`,
   `BinaryIntegerFunctions.GreatestCommonDivisor`, and `FixedRateAccumulator`
   for anything integrating a rate. A second implementation rounds differently
   somewhere, and the two will eventually disagree. If what you need really is
   missing, add it to the library rather than beside it.

---

## Running the tests

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
```

That is the default tier (Smoke + Default). It is a declaration-first law suite;
[tests/Puck.Maths.Tests](../../tests/Puck.Maths.Tests/README.md) owns the other
tiers, the shared domains and oracles, and
[the coverage ratchet](../../tests/Puck.Maths.Tests/README.md#the-ratchet) that
a new public member must be landed with.

---

## The root-level catalogue

The root level owns no folder, so these contracts are here.

| Type | Kind | What it's for |
|------|------|---------------|
| `QuadraticSurd` | `readonly struct` | An exact real-quadratic value `(a+b·√d)/c`: field arithmetic, sign, comparison, floor, and ceiling use arbitrary-width integers and no floating point. Square-equivalent radicands interoperate in equality, hashing, ordering, and arithmetic without requiring arbitrary-width integer factorization; ordering values from genuinely different quadratic fields uses certified rational enclosures, while arithmetic remains field-local. A square radicand collapses to a rational value, and the default struct is exact zero. |
| `ContinuedFraction` | `static` | The eventually periodic continued-fraction expansion of an exact quadratic irrational `(p + q·√d)/r`, in pure integer arithmetic — the symbolic coding of a closed geodesic on the modular surface. The golden ratio codes to the all-ones period `[1; 1, …]` and the silver ratio `1 + √2` to the all-twos period `[2; 2, …]`. Fills a caller span, reporting where the period begins and how long it is; no approximate seam. |
| `DiscreteMeasure` | `readonly record struct` | An exact integer-valued measure on integer intervals — see [Exact discrete rates](#exact-discrete-rates). |
| `CompiledDiscreteMeasure64` | `readonly record struct` | Its allocation-free signed-64-bit execution form — see [Exact discrete rates](#exact-discrete-rates). |
| `NumberTheoryFunctions` | `static` | Arbitrary-width number theory: `JacobiSymbol` (binary reciprocity — no factorization, no exponentiation), `SegmentedPrimeSieve`/`EnumeratePrimes` (a closed range of primes through `uint.MaxValue`, in working memory bounded by the range's square root, delivered by callback or by a materialized convenience sequence), and `HenselLiftRoot` (lifts a simple polynomial root from any base modulus to a power of that base, exactly when the derivative is a unit modulo the base). |
| `BigIntegerFunctions` | `static` | The five exact `BigInteger` operations whose *algorithm* changes once the carrier stops being a register — everything a width-agnostic formulation already covers stays in `BinaryIntegerFunctions`, which serves `BigInteger` through `IBinaryInteger<T>`. `IsPrime` hands anything inside a word to `PrimeField64.IsPrime` and decides the rest by strong-probable-prime rounds to the **same** twelve-base table, so exactness holds strictly below `318665857834031151167461` — the least strong pseudoprime to those twelve bases, quoted exactly because rounding it up to 3.19e23 would place that very counterexample inside the promise — and the answer is *probable* at or above it. `EnumeratePrimeFactors` peels twos, gates on that decision, and splits a composite by a deterministic Floyd cycle walk. A prime below the boundary reports itself; at or above it, a value that neither splits within the bounded refutation nor can be certified is REFUSED rather than returned, because the contract says every item is prime. Its cost is the splitter's, so a hard semiprime of two large primes is not promised in any stated time — hold a word and use `UnsignedNumberFunctions.EnumeratePrimeFactors` instead, which reaches the Brent/Montgomery kernel. `SquareRoot` is the floor square root by Newton descent from a power of two above the root (no floating-point seed is representable up here). `ModularInverse` inverts modulo any positive modulus by extended Euclid, reading the greatest common divisor out of that same descent and **refusing** a non-coprime value rather than returning a coefficient that multiplies to the divisor instead of to one. `TrySquareRootModuloOddPrime` is the arbitrary-width `PrimeField64.TrySqrt`: Euler's criterion decides the character, then a single power or the two-part descent produces the root. It enforces what is cheap — odd, at least three — and treats primality as a caller's precondition; on an odd composite the answer is unspecified and the descent may not return. |
| `MonotonicPartitioner` | `static` | Jump-consistent routing of 65536 values — or a `Guid`, through its trailing entropy — onto 1–1024 buckets, with the ownership chains precomputed at static init (checkpoint bitmask at ≤ 64 buckets, a varint tail stream or a re-walk above). Three invariants hold over the whole domain, once proven exhaustively against a table-free reference walk by a `monotonic-partitioner` battery stage that left the build on 2026-08-02 and has no replacement: **deterministic** (the same `(value, bucketCount)` pair routes identically on every machine — a client/server agreement, so changing the map is a protocol break), **monotonic** (raising N to N+1 only moves values *into* bucket N, so scaling out migrates the minimal set), and **uniform** (⌊65536/N⌋ or ⌈65536/N⌉ per bucket, quantization skew ≤ ~2 % at every count). `GetMetrics` returns the value's normalized rank, its migration count across the whole range, and the bucket-count distance to its next migration — the shard-ops telemetry view. `GetBucketIdDangerous` skips the range check for hot routing loops. |
| `CyclicRotation` | `static` | Deterministic, perfectly looping rotation driven by a tick: four planes turning at speeds {1, 7, 11, 13} in 12° steps, resyncing to the identity every 30 ticks. Rotations are `FixedComplex` read from a baked table of the 30th roots of unity, indexed by `tick mod 30` and never accumulated, so the loop closes bit-exactly with no drift on any backend. For looping deterministic animation: SDF spins, light-phase cycles, colour wheels. (Mathematically, the Coxeter element of E₈ — see `SymmetryLattice`.) |
| `SymmetryLattice` | `static` | A fixed, maximally symmetric set of 240 nodes in 8D (the root system of E₈), addressed by index. `Reflect` composes to the whole symmetry group W(E₈) (order 696,729,600); `Cycle` is the order-30 element `CyclicRotation` drives, cutting the nodes into `Ring`s of thirty; `Antipode`, `CanonicalRay`, and `AreOrthogonal` expose the exact 120-ray incidence seam; `RayCycleFactors` gives the five binary factors for the induced order-15 action; `Project` lays the roots on the Coxeter plane. |
| `Fnv1aHash` | `struct` | A 64-bit FNV-1a hash accumulator — the allocation-free, endianness-independent state probe a determinism/replay check folds per tick. `Create` primes the offset basis; `Add` folds bytes, byte spans, and 32/64-bit values least-significant byte first; `Compute` hashes a byte span; `Value` reads the digest. |
| `IMeetSemilattice<TSelf>` / `MeetMask64` / `MeetQuantity64` / `MeetProduct<TFirst, TSecond>` | interface / `readonly record struct` ×3 | The attenuation meet-semilattices: one narrowing operation `Meet` — the greatest lower bound of the carrier's own order (`IsAtMost`) — with `Top` (unrestricted) as identity and `Bottom` (nothing) as absorber, so folding envelopes along a delegation chain never widens what any link allows and is independent of fold order. `MeetMask64` is a 64-bit mask under bitwise AND (capability masks, subject sets as membership bits); `MeetQuantity64` a non-negative 64-bit quantity under min (budgets, ceilings, fuel shares, structural caps — every integral bound in use embeds into it); `MeetProduct` the componentwise product, itself a meet-semilattice, nesting to any width. Deliberately NOT here: the authority *decision*, which is order-dependent and rule-reporting and therefore not a lattice — see the family's XML remarks. Laws: the `meet.*` cases, with `meet.attenuation-never-widens` carrying the security property. |
| `BinaryIntegerFunctions` / `UnsignedNumberFunctions` / `PrimeExtensions` | `static` ext. methods | The integer kit — see [Integer routines](#integer-routines). |

> `BinaryIntegerConstants<T>` is an internal helper (width, log2-width, the
> constants 9 and 10 for an arbitrary `T`) and is not part of the public surface.

---

## Exact discrete rates

`DiscreteMeasure` assigns indivisible output units to integer input intervals by
flooring one exact affine rate at their boundaries: `Cumulative(n) =
floor(rate·n + offset)`, and any range receives the difference of its two
boundaries. It is the **stateless** counterpart to a rate accumulator — any index
or range is answered directly, adjacent ranges compose exactly, and splitting or
joining a range never changes its total. One neutral object covers balanced
jobs-per-frame, clock/sample conversion, quotas, pacing, density, and 1D point
sets. Rational rates expose their exact period; quadratic-surd rates are exactly
aperiodic rather than approximated by a long rational cycle.

```csharp
using Puck.Maths;

// Four jobs every three input intervals: 1, 1, 2, 1, 1, 2, ...
var jobs = DiscreteMeasure.Rational(numerator: 4, denominator: 3);
var thisFrame = jobs.AmountAt(index: frame);
var wholeShot = jobs.Map(start: firstFrame, length: frameCount);

// 48 kHz audio against 60000/1001 Hz video: 800/801 samples per frame, exactly.
var samples = DiscreteMeasure.Rational(
    numerator: (48_000 * 1_001),
    denominator: 60_000
);
var sampleRange = samples.Map(start: firstVideoFrame, length: videoFrameCount);

// The inverse-golden rate yields an exact, seekable aperiodic zero/one allocation.
var aperiodic = DiscreteMeasure.Create(
    rate: QuadraticSurd.Create(-1, 1, 5, 2),
    offset: QuadraticSurd.Zero
);
var nextOccupied = aperiodic.NextNonemptyIndex(start: cursor);
```

`Cumulative` is the boundary function; `AmountAt` measures one unit interval;
`AmountOver`/`Map` take a start and length, `AmountBetween`/`MapBetween` take two
boundaries. `Translate` moves the allocation origin exactly, and offsets are
normalized modulo one — a different origin, never a different rate. `LowerBound`
inverts cumulative amounts and `IndexContaining` maps an output index back to the
input interval owning it, both in at most 64 monotone boundary probes.

For a hot path, compile once and keep the bounded value:

```csharp
if (!samples.TryCompileInt64(out var runtime, out var failure))
    throw new InvalidOperationException($"measure compilation failed: {failure}");

long count = runtime.AmountAt(index: videoFrame);
if (runtime.TryMap(start: firstVideoFrame, length: frameCount,
                   mappedStart: out var firstSample, mappedLength: out var sampleCount))
{
    // No BigInteger and no allocation occurred in the query.
}
```

`CompiledDiscreteMeasure64` accepts bounded rational measures and a proved subset
of real-quadratic ones. Its rational kernel stores two reduced fractions; its
quadratic kernel clears denominators once at compile time, proves every core
signed-`long` boundary has a root fitting `Int128`, then takes the exact integer
square root in a fixed 256-bit accumulator of two `UInt128` limbs. It allocates
nothing and touches no `BigInteger` at runtime, handles `AmountAt(long.MaxValue)`,
and reports out-of-envelope endpoints through its `Try…` forms. Sources outside
the envelope stay on the exact unbounded `DiscreteMeasure`.

---

## Integer routines

### `BinaryIntegerFunctions` (generic over `IBinaryInteger<T>`)

Branchless, width-agnostic bit and digit operations. A closed generic compiles to
a compact, value-independent instruction sequence, and hardware instructions
(`PDEP`/`PEXT` via BMI2) are used when available — bit-identically to the
fallbacks.

| Method | Result |
|--------|--------|
| `BitwisePair` / `BitwiseUnpair` | Morton (Z-order) interleave and its inverse. |
| `ReverseBits` | Reverse all bits (SWAR butterfly). |
| `ReflectedBinaryEncode` / `…Decode` | Gray code ↔ binary. |
| `PermuteBitsLexicographically` | Next bit pattern with the same popcount (Gosper's hack). Fixed-width carriers cycle: signed values are raw two's-complement bits and terminal patterns wrap. A `BigInteger` walks its infinite two's-complement string — same minority-bit count, non-negatives ascending, negatives descending, never wrapping. |
| `PopulationParity` | Parity of the popcount. |
| `ExtractLowestSetBit` / `ClearLowestSetBit` | Isolate / clear the lowest set bit. |
| `FillFromLowestSetBit` / `FillFromLowestClearBit` | Fill trailing zeros / clear trailing ones. |
| `LeastSignificantBit` / `MostSignificantBit` | 1-based bit position (0 if none). |
| `GreatestCommonDivisor` / `LeastCommonMultiple` | Binary GCD (Stein's) and LCM. |
| `Exponentiate` | Integer power by squaring. |
| `DigitalRoot`, `EnumerateDigits`, `LogarithmBase10` | Base-10 digit work. |
| `LeastSignificantDigit` / `MostSignificantDigit` | First / last decimal digit. |
| `ReverseDigits`, `RotateDigitsLeft` / `…Right` | Decimal digit reversal / rotation (sign-preserving). |

```csharp
using Puck.Maths;

ulong morton = 5u.BitwisePair<uint, ulong>(other: 3u);  // interleave x=5, y=3
(uint x, uint y) = morton.BitwiseUnpair<ulong, uint>();  // -> (5, 3)

uint g = 48u.GreatestCommonDivisor(other: 36u);          // 12
int  reversed = 1230.ReverseDigits();                    // 321
```

### `UnsignedNumberFunctions` (`IBinaryInteger<T>` + `IUnsignedNumber<T>`)

| Method | Result |
|--------|--------|
| `ElegantPair` / `ElegantUnpair` | Szudzik pairing of two non-negatives ↔ one value. |
| `EnumeratePrimeFactors` | Prime factors ascending with multiplicity; a prime below the deterministic primality boundary reports **itself**. Past that boundary an uncertifiable residual is refused rather than reported, so every item returned is a proved prime. Carriage only — the factoring is `PrimeExtensions`' Brent/Montgomery kernel for a `T` that fits a word, `BigIntegerFunctions.EnumeratePrimeFactors` above it. |
| `JacobiSymbol` | Quadratic character over an odd machine-word modulus (binary reciprocity). Legendre when the modulus is prime, but never presumes it — the fixed-width counterpart to `NumberTheoryFunctions.JacobiSymbol`. |
| `ModularInverse` | Multiplicative inverse of an odd value mod `2^width` (Newton–Hensel). Negated, it is the factor a Montgomery reduction folds its low half away with, at either width. |
| `SquareRoot` | Floor integer square root (hardware-seeded through 128-bit). |
| `NextPowerOfTwo` / `NextSquare` | Round up to the next power of two / perfect square. |

### `PrimeExtensions` (on `uint`)

Exact over the **entire** 32-bit range — never probabilistic. Vectorized trial
division by the odd primes through 59 (a scalar ladder through 37 on narrower
hardware), then a single base-2 strong-probable-prime round in Montgomery form,
corrected by the complete list of the 2,256 base-2 strong pseudoprimes that
survive the ladder — enumerated in-house, and the whole method verified by an
exhaustive sweep of every 32-bit value. The hot loop performs no hardware
division.

```csharp
using Puck.Maths;

bool isPrime = 1_000_003u.IsPrime();              // true
uint p       = 100u.NthPrime();                   // 547   (0-based index)
uint pi      = 1_000_000u.PrimeCountingFunction();// 78498 (primes ≤ 1,000,000)

Span<uint> factors = stackalloc uint[32];
int count = 4_294_967_295u.Factorize(factors);    // 3, 5, 17, 257, 65537
```

`Factorize` fills a span with the prime factors ascending and with multiplicity —
a prime reports **itself**, so the count is Ω and only a value below two reports
nothing, matching `EnumeratePrimeFactors`: factors through 59 strip by
reciprocal multiplication, and the remaining cofactor splits by deterministic
Brent cycle walks on the same Montgomery kernel — microseconds even for the
hardest semiprimes of two ~2¹⁶ primes. `PrimeCountingFunction` uses a sublinear
combinatorial method, renting working buffers from `ArrayPool<T>` (peak ≈ 768
KiB); `NthPrime` seeds with Cipolla's asymptotic expansion, aligns exactly via
`PrimeCountingFunction`, and walks off the residual with a windowed segmented
sieve.

The **exact** primality decision for every `ulong` — and the oracle the tests
above are measured against — is `PrimeField64.IsPrime`, in
[`FiniteFields/`](FiniteFields/README.md#primality-on-ulong). The three tests
beside it (`IsStrongProbablePrime`, `IsStrongLucasProbablePrime`,
`IsBaillieProbablePrime`) are **probable**-prime tests and are contracted as such.

---

## Where to go next

- The seven wing READMEs linked from [Orientation](#orientation) are the
  per-type contracts.
- [tests/Puck.Maths.Tests](../../tests/Puck.Maths.Tests/README.md) — the law
  suite, and `LawRegistry.cs` inside it is the executable index of what is proved.
- [The generated API reference](../../docs/api) — member-by-member docs.
- (retired write-up; the gates are the record) — why the
  specialized types survive beside the generic algebra tier.
