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
fields** (number systems with a fixed set of values); exact integer arithmetic;
exact values built from square roots; and configurable tools for questions
about graphs, patterns, and finite structures. Nothing here reads the clock or
the current culture. Some routines inspect the CPU's feature set so they can
use faster instructions, but those paths return the same bits as their portable
fallbacks.

## ✨ Key features

- *Bit-identical everywhere:* the same inputs return the same bits on every
  machine, operating system, and GPU backend — the hardware-accelerated paths
  return the same results as their portable fallbacks.
- *A complete fixed-point family:* signed and unsigned scalars, fractions,
  vectors, rotations, rigid transforms, a planet-scale world position, and
  rate accumulators that do not lose fractional progress between steps.
- *Reproducible randomness:* seeded streams that snapshot and resume
  mid-sequence, constant-time weighted sampling, spatial noise as a pure
  function of `(seed, position)`, and point sets designed to spread out evenly.
- *Exact algebra:* binary and odd-prime finite fields, exact primality on
  `ulong`, error-correction symbols, exact square-root values, and number theory
  over integers larger than a machine word.
- *Configurable structure tools:* build algebraic relationships at runtime and
  ask questions about reachability, shortest paths, patterns, and holes in
  finite structures through one product operation.
- *Allocation-free hot paths:* the scalar and vector paths never allocate;
  generic integer algorithms sit on `System.Numerics` interfaces so one
  implementation serves several widths.
- *Nothing environmental:* deterministic results do not depend on the clock,
  culture, or available CPU features.

## 📐 The determinism boundary

The library follows one rule: `double` may enter once at an authoring boundary
and leave for display, but simulation state itself is fixed-point, exact, or
seeded — and nothing computed for presentation ever flows back:

```mermaid
graph LR
    Author(["📝 Authored input (double)"]) -->|"quantize once,<br/>deterministic rounding"| Fixed
    subgraph State ["🎯 Simulation state — bit-identical on every machine and backend"]
        Fixed(["🔢 Fixed-point scalars · vectors · rotations · positions · rates"])
        Rng(["🎲 Pcg32XshRr streams — snapshot and resume"])
        Exact(["🧮 Exact fields · integers · square-root values"])
    end
    State -->|"one-way: nothing flows back"| Present(["🖥️ Presentation (double, display only)"])
    State -->|"fold per tick with Fnv1aHash"| Replay(["📼 Replay / determinism check"])
```

## ⚠️ Deliberately not reproducible

Three surfaces are deliberately *not* reproducible, and none may be used in
simulation state:

- [`SecureRandom`](Sampling/README.md#securerandom), which draws from the
  platform's cryptographic generator.
- [`ProbabilityFunctions`](Sampling/README.md#probabilityfunctions), a `double`
  quantile function meant for analysis and display.
- [`ConeDirectionTable`](Sampling/README.md#conedirectiontable), which is
  same-machine only: it builds its table with the platform's `Math.Cos`,
  `Math.Sin` and `Math.Tan`, so two machines can disagree in the last bits.
  Never place a built table in replay state; if you need one across machines,
  generate and version the constants instead of rebuilding them.

One more exclusion, narrower but easy to trip over: **`GetHashCode` is not part
of the bit-identical promise.** `RealQuadratic` and `QuadraticAlgebra` fold
their hashes through the framework's `HashCode`, which .NET randomizes per
process. The same value may therefore produce a different number in another
process, within the ordinary `GetHashCode` contract. Hashes are for hash tables.
Never use one as a replay fingerprint, a state hash, an ordering key or a
snapshot field; use the value's own components, which *are* reproducible.

Types that fold through `Fnv1aHash` instead — the algebra values among them —
happen to be stable across processes, but do not rely on that either: it is not
a contract, and any of them may move to the framework fold. If you need a stable
digest, take one explicitly.

`dotnet pack` produces `ByteTerrace.Puck.Maths`; the first NuGet.org release has
not been published yet. It is a leaf library with no package or project
dependencies. Everything is `namespace Puck.Maths` except part of `Research/`,
which is `Puck.Maths.Research`.

The hot scalar and vector paths are allocation-free. Generic integer algorithms
sit on `System.Numerics` interfaces, so one implementation serves several widths.
Table construction and large prime-counting operations may allocate or rent.

---

## Orientation

The library is organized into eight folders plus a set of root-level types.
Each folder has its own README carrying the detailed contracts for its types:
what each operation guarantees, the invariants, and how the folder is verified.
Begin with the map and table below, then follow the link into the folder you
need.

```mermaid
graph TB
    Root(["📦 Puck.Maths<br/>root: integers · rates · square-root values · hashing · routing"])
    FP(["🔢 FixedPoint<br/>scalars · vectors · rotations · positions"])
    SA(["🎲 Sampling<br/>seeded randomness · noise · evenly spread point sets"])
    FF(["🧮 FiniteFields<br/>binary fields · prime fields · primality"])
    GE(["📏 Geometry<br/>hex grids · Hilbert · layers"])
    AL(["🏗️ Algebra<br/>configurable number systems"])
    OR(["🔮 Oracle<br/>graphs · paths · patterns"])
    RE(["🔬 Research<br/>exploratory, never the hot path"])
    TR(["🎛️ Transforms<br/>NTT · Walsh–Hadamard · fixed-point FFT and DCT"])
    Root --- FP
    Root --- SA
    Root --- FF
    Root --- GE
    Root --- AL
    Root --- OR
    Root --- RE
    Root --- TR
```

| Folder | What lives in it | Read when you need |
|---|---|---|
| [`FixedPoint/`](FixedPoint/README.md) | The fixed-point scalars — signed and unsigned Q48.16, meaning 48 integer bits and 16 fraction bits, plus the signed Q16.48 that splits the same word the other way for reciprocal quantities — the three unit-interval fractions, vectors, the planar trio (complex, dual, split), quaternions, rigid transforms, the hierarchical world position, the rate accumulators. | Any value a simulation advances, compares, hashes or replays. |
| [`Sampling/`](Sampling/README.md) | The seeded generator, weighted choice, spatial noise, low-discrepancy sequences and digital nets (point sets that spread evenly by construction), and the two non-simulation paths (`SecureRandom`, `ProbabilityFunctions`). | Anything random, scattered, or noisy. |
| [`FiniteFields/`](FiniteFields/README.md) | Binary fields over fixed-size bit patterns, prime fields and their extensions, error-correction arithmetic, and exact primality on `ulong`. | Error-correcting codes, checksums, and modular arithmetic. |
| [`Algebra/`](Algebra/README.md) | Configurable number systems that can add a root, add generators, raise a degree, or double an existing number type. | A relationship chosen at runtime, or a proof that the same operation agrees across number types. |
| [`Geometry/`](Geometry/README.md) | Hex grids, the locality-preserving Hilbert curve, layered index spaces, and exact integer geometry. | A grid, a space-filling order, or a layered index. |
| [`Oracle/`](Oracle/README.md) | One configurable product operation evaluated with different rules for combining values, then used to build graphs, geometric algebras, planar tangles, divisor arithmetic, and pattern languages. | Reachability, shortest paths, pattern matching, holes in a structure, or group words. |
| [`Research/`](Research/README.md) | Exploratory exact tools: continued-fraction and radical tails, positional and Ostrowski automatic sequences, Sturmian and quasicrystal words, Fibonacci and metallic-mean arithmetic, odd-cyclic incidence, and real-quadratic orders. Partly in `namespace Puck.Maths.Research`; that folder README says which types. | Research questions and compiled random-access integer patterns, never the simulation hot path. |
| [`Transforms/`](Transforms/README.md) | The exact number-theoretic transform over `PrimeField64` and the exact Walsh–Hadamard transform over any binary integer; the fixed-point FFT over `FixedComplex` and the fixed-point DCT over `FixedQ4816` — one plan-then-in-place shape, cached twiddle plans, cyclic convolution on both spectral transforms. | A frequency-domain or sequency-domain transform, or a cyclic convolution. |

The [root-level type map](#root-level-types) below introduces the types that do
not belong to one of those folders: integer routines, exact discrete rates,
exact real quadratics, hashing, and deterministic routing.

---

## 🧭 What do I reach for?

I find the easiest way into a numerics library is to start with the value or
operation I need. Pick a row, then follow its link for the detailed contract.

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
| A pole-matched second-order response — a target that eases, overshoots, or anticipates instead of snapping | `SecondOrderDynamics` — `Create(f, ζ, r)` derives the coefficients, `Compile`+`Step` for per-tick advance, `Evaluate` for a closed-form read from initial conditions | [FixedPoint](FixedPoint/README.md#secondorderdynamics) |
| A curve authored by knot curvature rather than control points | `CurvatureSpline.Compile` — knots declare position, tangent direction, and signed curvature; the compiled tangent lengths and a Simpson arc-length table come out exactly. `CompiledCurvatureSpline.Evaluate(arcLength)` samples position, tangent, and curvature per tick | [FixedPoint](FixedPoint/README.md#curvaturespline) |
| A seeded RNG you can snapshot and resume | `Pcg32XshRr` — one stream per system | [Sampling](Sampling/README.md#pcg32xshrr) |
| To shuffle a list reproducibly | `Pcg32XshRr.Shuffle` — in-place Fisher–Yates from the high end down | [Sampling](Sampling/README.md#pcg32xshrr) |
| A weighted pick | `WeightedSampler.Create` once at load, then `AliasTable<T>.Sample` in constant time with two generator advances | [Sampling](Sampling/README.md#weightedsampler-and-aliastabletelement) |
| Smooth spatial noise | `FieldNoise` — a pure function of `(seed, position)`, nothing to persist | [Sampling](Sampling/README.md#fieldnoise) |
| Points that spread out without clumping | `LowDiscrepancy.R1`/`R2` for an even-looking spread; `DigitalNetSampler` when supported subdivisions must contain an exact share of the samples | [Sampling](Sampling/README.md#choosing-a-primitive) |
| Cryptographic randomness | `SecureRandom` — and never in simulation state; it is not reproducible | [Sampling](Sampling/README.md#securerandom) |
| Arithmetic over fixed-size bit patterns, written `GF(2^k)` | `BinaryField<T>` over a chosen modulus, or the canonical `BinaryFields.Degree8/16/32/64/128` | [FiniteFields](FiniteFields/README.md#binaryfieldt) |
| Error-correction symbols over a binary field, and reading a codeword back | `ReedSolomon.BuildGenerator` once, then `ComputeCheckSymbols` per message and `ComputeSyndromes` to verify | [FiniteFields](FiniteFields/README.md#reedsolomon) |
| Modular arithmetic mod an odd prime, exact square roots, exact primality on `ulong` | `PrimeField64`, or `QuadraticExtensionField64` when each value needs two prime-field parts | [FiniteFields](FiniteFields/README.md#primefield64) |
| An exact cyclic convolution, or a frequency-domain transform over a finite field | `NumberTheoreticTransformPlan.Create` once, then `NumberTheoreticTransform.Forward` / `.Inverse` / `.Convolve` | [Transforms](Transforms/README.md#numbertheoretictransform) |
| A fixed-point FFT — forward/inverse over `FixedComplex`, or a real sequence via `ForwardReal`/`InverseReal` | `FixedFourierTransformPlan.Create` once, then `FixedFourierTransform.Forward` / `.Inverse` | [Transforms](Transforms/README.md#fixedfouriertransform) |
| A plan-free exact ±1 transform over integer lanes | `WalshHadamardTransform.Forward` / `.Inverse` | [Transforms](Transforms/README.md#walshhadamardtransform) |
| A fixed-point DCT-II/DCT-III pair over real values | `FixedCosineTransformPlan.Create` once, then `FixedCosineTransform.Forward` / `.Inverse` | [Transforms](Transforms/README.md#fixedcosinetransform) |
| Reachability, shortest paths, walk counts, best-probability routes | A `Presentations.Quiver` with the matching material — the rules used to combine path values | [Oracle](Oracle/README.md#choosing-an-entry-point) |
| Pattern matching represented by algebra values | `TokenPattern` then `PatternMatcher.TryCompile` | [Oracle](Oracle/README.md#the-language-axis) |
| An exact integer allocation over intervals (jobs per frame, samples per video frame) | `DiscreteMeasure`, compiled to `CompiledDiscreteMeasure64` for the hot path | [below](#root-level-types) |
| An exact value involving a square root — no floating point, no drift | `RealQuadraticField` names the field, `RealQuadratic` carries the value | [below](#root-level-types) |
| Proof that a quantized slope reproduces exact Beatty floors — and the exact index where it first stops | `BeattyQuantization.CertifySlope`; `ContinuedFraction.Convergents` supplies the worst-case indices | [Research](Research/README.md) |
| The fraction with the smallest denominator inside an interval | `SimplestRational.InOpenInterval` | [below](#root-level-types) |
| A hex grid whose 60° rotations are exact | `HexagonalCoordinate` | [Geometry](Geometry/README.md#hexagonalcoordinate) |
| Cache-coherent tile/chunk ordering | `HilbertCurve` (locality-preserving) rather than Morton order | [Geometry](Geometry/README.md#hilbertcurve) |
| A layered index space — rings, shells, shards | `LayerSequence` — constant-time index → layer, pure integer | [Geometry](Geometry/README.md#layersequence) |
| One algebraic relationship over several number types, or a proof that two number systems agree | `QuadraticAlgebra<TScalar>` and the rest of the configurable algebra types | [Algebra](Algebra/README.md) |
| To fold a per-tick state hash for a determinism or replay check | `Fnv1aHash` — allocation-free, endianness-independent | [below](#root-level-types) |
| A restriction that can only narrow — a capability mask under AND, a quantity under minimum, or both paired as one value | `MeetMask64`, `MeetQuantity64`, `MeetProduct<TFirst, TSecond>` | [below](#root-level-types) |
| Bit tricks, GCD, integer roots, pairing functions, prime factorization | `BinaryIntegerFunctions`, `UnsignedNumberFunctions`, `PrimeExtensions` | [below](#root-level-types) |
| Integer square roots, inverses, primality, or factorization beyond 64 bits | `BigIntegerFunctions`; its API documentation states where primality and factorization are proved or refused | [below](#root-level-types) |

---

## 🚀 Three small programs

These small programs show the flavor of the library before you commit to
reading a folder README.

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
var closing = FixedVector3.Dot(left: velocity, right: forward);

// Converting to double is for display only. Converted values must never flow
// back into simulation state.
var forDisplay = (double)closing;
```

**Sampling: a seeded stream you can save and resume.**

```csharp
using Puck.Maths;

// Stream ids are small and consecutive, all derived from the run's master seed.
const ulong masterSeed = 0xC0FFEEUL;
var loot = Pcg32XshRr.Create(state: masterSeed, stream: 3UL);

// Build the distribution once at load; each sample takes constant time and
// advances the generator exactly twice.
ReadOnlySpan<(string Element, ulong Weight)> drops =
    [("common", 70UL), ("rare", 25UL), ("epic", 5UL)];
var table = WeightedSampler.Create(entries: drops);

var drop = table.Sample(generator: ref loot);

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

## 📏 Four rules

These four rules protect the determinism promise. Each is stated briefly here
and explained fully — with the evidence behind it — in the folder that owns it.

1. **No floating point in simulation state.** A `double` may enter at an
   authoring boundary and leave at a presentation boundary, but the presentation
   boundary is one-way: nothing a renderer or a diagnostic computed may flow
   back into state. The [FixedPoint README's opening](FixedPoint/README.md)
   lists the conversions that form these boundaries.

2. **One stream per system.** Derive each consumer's `Pcg32XshRr` from the run's
   master seed with its own small stream id; sharing one generator couples
   systems through draw order. This and the three rules beside it (snapshots,
   seeking by generator advances rather than method calls, and alias-table
   ordering) are
   [the rules a consumer inherits](Sampling/README.md#the-rules-a-consumer-inherits).

3. **Do not reassociate a rounded product.** .NET's generic `INumber<T>`
   interface says which operations a type supports; it does not prove that
   multiplication is associative. `(a·b)·c` and `a·(b·c)` are different at some
   operands, and the combined operations exist so a whole expression rounds
   *once*. Field products in
   [`FiniteFields/`](FiniteFields/README.md) are the exception: exact, therefore
   safe to reassociate. The
   [FixedPoint README](FixedPoint/README.md#load-bearing-invariants) explains
   the one-rounding rule and the tests that demonstrate why it matters.

4. **Do not reimplement what is already here.** The operations most often
   reimplemented are already shipped: `FixedQ4816.Lerp` / `.Clamp` (and
   `FixedVector3.Lerp`, `FixedQuaternion.Slerp`,
   `FixedRigidTransform.ScLerp`),
   `UnsignedNumberFunctions.SquareRoot`,
   `BinaryIntegerFunctions.GreatestCommonDivisor`, and `FixedRateAccumulator`
   for anything integrating a rate. A second implementation rounds differently
   somewhere, and the two will eventually disagree. If what you need really is
   missing, add it to the library rather than beside it.

---

## 🧪 Running the tests

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
```

This runs the normal suite, including the structural checks applied to every
public member. The [test project README](../../tests/Puck.Maths.Tests/README.md)
explains the optional deeper and exhaustive settings for contributors.

---

## Root-level types

This table is a conceptual map for the types that live at the project root. The
[generated API reference](../../docs/api) owns the complete member-by-member
surface, including parameters, return values, and exceptions.

| Type | Role |
|------|------|
| `Rational` / `RealQuadraticField` / `RealQuadratic` / `ContinuedFraction` | The exact rational (reduced on construction); the descriptor of a real quadratic field `ℚ(√d)`, its radicand canonicalized once; the exact value `(a + b·√d)/c` of such a field, with conjugate, norm and trace; and the repeating continued-fraction expansions of those values — including the convergents, the best rational approximations — without floating point. |
| `SimplestRational` | Locate the minimal-denominator fraction strictly inside an exact interval, by Stern–Brocot descent. |
| `DiscreteMeasure` / `CompiledDiscreteMeasure64` / `DiscreteMeasureCompilationFailure` | Allocate an exact integer amount across integer intervals, then compile supported measures into a bounded, allocation-free form for frequently run code. |
| `NumberTheoryFunctions` / `BigIntegerFunctions` | Provide prime enumeration, modular roots and inverses, primality, and factorization when the calculation needs arbitrary-width integers. |
| `MonotonicPartitioner` / `MonotonicPartitionerMetrics` | Route a value to one of 1–1024 buckets while minimizing movement when another bucket is added, and report when that value moves. |
| `CyclicRotation` / `SymmetryLattice` / `SymmetryWord` | Provide a bit-exact rotation loop (the thirty-step table, or any order's root of unity), the fixed, symmetric node set behind it in eight dimensions with its exact root pairing and ring walks, and a word of its reflections baked to a permutation with a derived order and a constant-time counted power. |
| `Fnv1aHash` | Accumulate an explicit, stable 64-bit digest for replay and determinism checks. |
| `IMeetSemilattice<TSelf>` / `MeetMask64` / `MeetQuantity64` / `MeetProduct<TFirst, TSecond>` | Combine restrictions so the result never grants more than either input, whether the restriction is a bit mask, a quantity, or a pair of both. |
| `BinaryIntegerFunctions` / `UnsignedNumberFunctions` / `PrimeExtensions` | Supply generic bit and decimal-digit operations, integer roots and pairing, and exact 32-bit primality and factorization. |

The chooser above is the quickest way into these types. The API reference is
the place to check a particular overload or failure condition.

---

## 🗺️ Where to go next

- The eight folder READMEs linked from [Orientation](#orientation) are the
  per-type contracts.
- [tests/Puck.Maths.Tests](../../tests/Puck.Maths.Tests/README.md) — the law
  suite, and `LawRegistry.cs` inside it is the executable index of what is proved.
- [The generated API reference](../../docs/api) — member-by-member docs.
