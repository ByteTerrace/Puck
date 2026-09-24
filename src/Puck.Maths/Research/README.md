# Research

The exploratory wing. Everything here is **exact**—arbitrary-width integers,
quadratic surds, `GF(2)` polynomial algebra—and none of it is on the
simulation hot path. These types answer research questions (continued-fraction
tails, Sturmian and quasicrystal words, Fibonacci and metallic-mean arithmetic,
odd-cyclic binary incidence, real-quadratic orders) with certificates rather
than measurements, and they keep a bounded search that ran out of budget
distinct from a search that found nothing: `SearchLimitReached` is never
conflated with a proof or a counterexample.

**Two namespaces, and the split is in flight.** Ten of the folder's thirty-one C#
files declare `namespace Puck.Maths.Research`; the other twenty-one still sit flat
in `namespace Puck.Maths` alongside the rest of the library. Check the declaring
file before writing a `using`—the compiler is the only authority, and this
line is a snapshot. The table below splits like this:

- **`Puck.Maths.Research`**—`QuadraticIntegerArithmetic`;
  `OddCyclicIncidence` / `OddCyclicWordAnalysis`;
  `AutomaticSelectionAutomaton` / `AutomaticCyclicIncidence`;
  `FibonacciResearch` and its companions (`GoldenInteger`,
  `FibonacciRulerWordIndex`, `FibonacciSymmetricMinimum`) / `FibonacciReturnResearch`;
  `ConstantGapCoveringResearch`; `SturmianReturnSpectrumResearch`. These are the
  types the three code samples below reach, which is why each opens
  `using Puck.Maths.Research;`.
- **`Puck.Maths`** (flat)—`CertifiedLowDiscrepancy`;
  `PolynomialContinuedFractionTail` / `PolynomialContinuedFractionAnalysis`;
  `QuadraticInflation`; `QuadraticQuasicrystal` / `QuadraticQuasicrystalIndex`;
  `MetallicQuasicrystal`; `MetallicPolynomialContinuedFraction`;
  `IntegerNumerationSystem` / `DeterministicOutputAutomaton` /
  `AutomaticIntegerSequence`; `BeattyQuantization`; `RadicalShadowTowerAnalyzer`
  and its certificate, verifier, compiler, and evaluator.

**Not yet covered here.** Nine files carry public types the table below does not
name, all flat `Puck.Maths`: `OstrowskiNumeration.cs` (`QuadraticOstrowskiSystem`,
its digit and output automata, and `OstrowskiPellChannel`), `PositionalNumeration.cs`
(`PositionalNumerationSystem` and its automata), `PellEquation.cs`,
`PolynomialBeattyShadow.cs`, `PolynomialExactBeattyTrap.cs`,
`PolynomialRationalTail.cs`, `PolynomialTailEulerMoment.cs`,
`PolynomialTailMinimalityReduction.cs`, and `PolynomialTailPairedForcing.cs`.
The last five also carry partials of the documented
`PolynomialContinuedFractionAnalysis`. Their XML docs are the contract until
someone who owns them writes the rows. `AutomatonStateDedup.cs`,
`PolynomialTailIndex.cs`, `QuadraticNormEquation.cs`, and
`QuadraticSurdRecurrence.cs` are absent deliberately: they declare `internal`
types only.

[Deterministic numerics](../../../docs/reference/maths.md) is the library's entry point;
this file is the contract for the folder.

---

## At a glance

| Type | Kind | What it's for |
|------|------|---------------|
| `QuadraticIntegerArithmetic` | `static` | Primality and factorization *inside* `QuadraticAlgebra<BigInteger>`—the concepts of `PrimeExtensions`, one story up: `IsPrimeElement` via norms, `SplittingCharacter` (split/inert/ramified by the Jacobi symbol), `FundamentalUnit` (the order's unit equation `X² − Δ·Y² = ±4` at minimal positive `Y`, solved by walking the continued fraction of the order's own reduced root `(b + √Δ)/2` to its first period closure—the norm sign is that period's parity, not a preference between branches; the `Δ = 5` world answers with the golden unit), `CanonicalAssociate` (documented deterministic normalization under the unit action), and `TryFactorize`—exact, deterministic, ascending-by-norm factorization that reassembles bit-identically, and that **fails honestly with a class-group witness** when a needed prime is non-principal (the obstruction names the rational prime and its splitting; in the `Δ = −20` world, `6` refuses to factor and the API tells you exactly why). Zero obstructions across the nine imaginary class-number-one worlds; the failure witness is a feature—non-unique factorization made measurable. One primitive answers both questions a real order asks: the unit equation *is* the norm equation `X² − Δ·Y² = 4·N` at `N = ±1`, and lifting a split or ramified prime is the same equation at `N = ±ℓ`, decided by walking the continued fraction of the ideal above `ℓ`—arriving at an ideal of norm one exhibits the generator, and closing that cycle without arriving certifies the obstruction. Neither answer is enumerated, so a real order with a 200-digit fundamental unit factors as readily as `Δ = 5`. |
| `CertifiedLowDiscrepancy` | `readonly record struct` | A Kronecker sequence `{n·α}` for a quadratic irrational `α = (p + q·√d)/r`, carrying an EXACT equidistribution certificate: `Certificate` is the largest continued-fraction partial quotient `K` (read from `ContinuedFraction`, the integer part dropped)—the badly-approximable bound, `K = 1` for the golden ratio (Hurwitz-optimal), `K = n` for the `n`-th metallic mean, which `MetallicMean` builds by index. `DiscrepancyBound(N)` is the closed-form `O(K·log N / N)` star-discrepancy guarantee that follows from `K` alone; `Point` is the `[0, 1)` value (the one seam), one multiply in the stateless style of `LowDiscrepancy.R1`. Reframes "well-distributed" as a bounded-partial-quotient certificate instead of a measured statistic. |
| `OddCyclicIncidence` / `OddCyclicWordAnalysis` | `sealed class` | Geometry-neutral exact analysis of any free odd-cyclic binary incidence system. A compact letter×ray-orbit polynomial table yields `t=1` syndromes, the syndrome-matroid circuit filter, ranks over every CRT field, exact expanded nullity, and parity-proof irreducibility. Optional direct expansion recomputes the large binary rank and fails if it disagrees with the CRT sum, making the theorem executable as a per-word certificate. |
| `AutomaticSelectionAutomaton` / `AutomaticCyclicIncidence` | `sealed class` | Composes a finite-output positional or quadratic-Ostrowski sequence with `OddCyclicIncidence`. Prefix and range masks are accumulated exactly in `GF(2)` without scanning the range; positional prefix accumulation can itself be compiled to a DFAO. Reusable factories cover digit-sum residue selectors and the canonical binary Gray orbit through every selection mask—and hence every kernel relation—while safety-bounded compilation avoids materializing exponential output spaces. |
| `IntegerNumerationSystem` / `DeterministicOutputAutomaton` / `AutomaticIntegerSequence` | `sealed class` | The engine-neutral random-access primitive beneath compiled sparse patterns. Non-negative integers are written either in a fixed radix or in the canonical Ostrowski system of a quadratic irrational; a dense deterministic finite automaton with output (DFAO) reads those digits; and a finite alphabet maps the resulting `int` symbol to an arbitrary-width integer. `BigInteger` is the contract, while `ulong` has a bounded-width traversal path. The automaton constructor discards unreachable states and renumbers the reachable graph deterministically. |
| `RadicalShadowTowerAnalyzer` / `RadicalShadowTowerVerifier` / `RadicalShadowTowerCompiler` / `RadicalShadowTowerEvaluator` | `static` / `sealed class` | The first sound slice of the radical tower `t_n²=d n²+u n+v+w t_(n+1)`. Analysis derives `sqrt(d)`, the forced affine offset, the exact residual `kappa`, the norm congruence, and the strict first-order channel threshold. Version one certifies only the exact-affine case `kappa=0`. The analyzer emits the mathematical certificate, the compiler separately produces its zero-correction DFAO program, and the exact verifier checks the pair before returning an opaque verified value. Only that value can construct a total evaluator. Nonzero residuals return `Undecided`: the global trap seed and second-order channel-boundary argument are still research gaps, so the API does not turn the observed Pell patterns into a theorem. |
| `PolynomialContinuedFractionTail` / `PolynomialContinuedFractionAnalysis` | `static` / `sealed class` | Exact analysis of every integer family `sₙ=p·n+q+(r·n²+u·n+v)/sₙ₊₁` with non-negative base and positive numerator on all positive indices. A successful `Analyze` certifies existence and uniqueness of the positive tail, returns its exact quadratic slope/offset/residual, constructs an integer-checkable interval `|sₙ−(λn+β)|≤H/n` beyond an explicit cutoff, and generates any requested finite number of exact asymptotic coefficients. `TryRationalTailCertificate` recognizes polynomial-denominator rational-function tails over the full characteristic field `Q(λ)` through the dense solver's explicit degree-128 resource ceiling, caches the result, and certifies positivity and absence of positive-integer poles; `TryLinearFractionalTailCertificate` is the narrower recognizer, complete within the linear-fractional class `λn+β+c/(n+d)`. `TryDegreeOneMinimalityReduction` recognizes the double-square subfamily covered directly by the 2026 minimality theorem. `TryOnePeriodEqualityReduction` additionally recognizes the aligned irrational-characteristic branch `p(u-r)=2rq`, where the transformed Gauss parameters remain rational and equality reduces to an effective 1-period relation. |
| `QuadraticInflation` | `readonly record struct` | The inflation lens of a quadratic irrational: reads its `ContinuedFraction` period as the exact substitution matrix `∏[[aᵢ,1],[1,0]]`, exposing the conjugacy invariants (`Trace`, `Determinant = (−1)^period`, `Discriminant`), the closed-geodesic `Axis` (a hyperbolic `ModularTransform`), and the `InflationFactor` (the Perron eigenvalue). Golden recovers discriminant 5 and factor φ, silver discriminant 8 and factor 1 + √2—read from the continued fraction, not fed in. |
| `QuadraticQuasicrystal` / `QuadraticQuasicrystalIndex` | `static` / `sealed class` | The tiling word of the quasicrystal beneath **any** quadratic irrational, for an arbitrary CF period. `Word` streams its Sturmian fixed point, while `Compile` builds a straight-line substitution grammar supporting exact `TileAt`, prefix long-tile rank, and exact position at arbitrarily large `BigInteger` indices in period-times-logarithmic work without generating the preceding prefix. The nested **`Chain`** adds O(1) ring-coordinate random access into that same tiling—`Chain.FromQuadraticIrrational(…)` caches the period, then a vertex is the `(longCount, shortCount)` abelianization of a prefix and `Contains` tests whether its Galois-conjugate internal coordinate `C·a + (λ′−A)·b` lands in the window `[0, C + A − λ′)` in exact integer surd arithmetic, with `StartsLongTile`/`Next`/`Previous`/`Position` walking it. Single-term periods reproduce the metallic tiling language (in tile-count coordinates; `MetallicQuasicrystal` keeps the `a + b·δₙ` ring coordinate—general periods admit no such embedding). |
| `FibonacciResearch` / `GoldenInteger` / `FibonacciRulerWordIndex` / `FibonacciReturnResearch` | `static` / `readonly record struct` / `sealed class` / `static` | Lean-derived exact tools for the golden research branch. Fast doubling and closed two-adic rank formulas jump Fibonacci and golden-ring phases at arbitrary `BigInteger` indices; primitive pairs modulo `2^t` are classified into the two proved projective orbits by their norm modulo eight; `FibonacciSymmetricMinimum.Find` returns a self-verifying exact best-return certificate for any positive period; the ruler index gives random access, prefix counts, and factor counts for the balanced construction; and the return analyzer exposes the canonical signed Cassini coordinates, exact mechanical-error bracket, and every successful period-decomposition certificate. Crucially, a negative coordinate remains visible as a counterexample profile instead of being filtered out. |
| `ConstantGapCoveringResearch` | `static` | Exact-cover machinery for Hubert colorings: constructs the canonical ruler coloring, decides attainable least periods, enumerates period spectra, and returns independently verifiable residue-class witnesses. Searches and witness verification have an explicit period ceiling. |
| `SturmianReturnSpectrumResearch` | `static` | Exact Proposition-20 return-spectrum machinery for arbitrary periodic Sturmian directives and periodic left/right colorings. It evaluates a specified finite preperiod, enumerates every determinant-compatible congruence component representing all preperiods, reconstructs an explicit positive prefix for a component, and returns exact phase witnesses and minima; phase-invariant candidate tables make broad searches practical, while a separate optimized Fibonacci path supports cross-checking. |
| `MetallicQuasicrystal` | `static` | The metallic-mean quasicrystals `δₙ = (n + √(n²+4))/2` for any index—golden is `n = 1` (the Fibonacci chain), silver is `n = 2` (the Pell chain). `Word` streams the tiling; `Contains`/`StartsLongTile`/`Next`/`Previous`/`Position` address points by ring coordinate `a + b·δₙ` in O(1). Exact integer arithmetic above one fixed-point seam, so it never drifts. |
| `MetallicPolynomialContinuedFraction` | `static` | Exact random access to the metallic polynomial continued fraction: `TailFloor(k, n)` evaluates `⌊sₙ⌋`, where `sₙ = k·n−1+n²/sₙ₊₁`, directly from its proved quadratic-irrational formula. It uses arbitrary-width integer arithmetic and an integer square root instead of a truncation depth or floating-point tolerance; differences of consecutive floors give its associated integer sequence. |
| `BeattyQuantization` / `BeattyQuantizationCertificate` | `static` / `readonly record struct` | Certify the nearest dyadic quantization of an exact irrational slope and the exact first index at which the quantized Beatty floors diverge from the true ones, with a verifiable witness; `ContinuedFraction.Convergents` supplies the worst-case indices. |

---

## Automatic integer sequences

`IntegerNumerationSystem` gives every non-negative integer one digit word. A
positional system uses the familiar powers of a radix; a quadratic Ostrowski
system uses the denominators of continued-fraction convergents. A
`DeterministicOutputAutomaton` reads that word from its most significant digit
and emits an `int` symbol. `AutomaticIntegerSequence` then maps the symbol to a
`BigInteger`, which keeps small corrections such as −1, 0, and 1 cheap without
making them the framework's numeric ceiling.

The generic sequence index starts at zero. It performs no hidden shift.
Consumers with a different mathematical origin impose it themselves; the
radical tower evaluator, for example, accepts only `n >= 1` and addresses its
correction sequence with that same positive `n`.

## Radical shadow towers

For parameters `(d,u,v,w)`, the analyzer derives the affine center
`sqrt(d)n+c`, where `c=u/(2sqrt(d))+w/2`, and the first residual coefficient
`kappa`. It also exposes the generalized-Pell channel modulus and activation
threshold so the later channel analyzer has one exact source for those values.
Analysis has distinct `Invalid`, `ResourceLimit`, and `Undecided` outcomes;
cooperative cancellation throws `OperationCanceledException` and is never
reported as a mathematical result.

The current total path is deliberately narrow. When `kappa=0` and the affine
solution is positive from `n=1`, the recurrence identity is checked exactly,
the analyzer emits a mathematical certificate, and `Compile` turns it into a
zero-correction program. `CompileTotal` verifies that program before building
an evaluator. For `kappa!=0`, the analyzer reports `Undecided`. The
Pell-channel compiler and the proof objects for eventual or finite-domain
results remain to be added after the trap-invariance and second-order boundary
arguments are closed.

---

### Lean-derived Fibonacci research

The golden research API turns the proved arithmetic lemmas into exact random-access and certificate-producing tools:

```csharp
using Puck.Maths.Research;

var rank = FibonacciResearch.TwoPowerRankOfApparition(exponent: 20);
var orbit = FibonacciResearch.ClassifyTwoPowerProjectiveOrbit(
    exponent: 20,
    value: new GoldenInteger(A: 37, B: 18));

var minimum = FibonacciSymmetricMinimum.Find(period: BigInteger.One << 16);
bool exactCertificate = minimum.Verify();
bool beatsNonFibonacciGap = minimum.IsBelowThreeCellGap;

var word = new FibonacciRulerWordIndex(rulerDepth: 16);
FibonacciRulerLetter letter = word.LetterAt(BigInteger.Parse("1000000000000000000000000"));
FibonacciFactorCounts counts = word.FactorCounts(start: 1_000_000, length: word.GuaranteedRichFactorLength);

var analysis = FibonacciReturnResearch.Analyze(
    word,
    start: 1_000_000,
    root: 233,
    requestedOverlap: 3,
    searchLimit: 10_000);
if (analysis.CanonicalProfile is { } profile) {
    bool exactMaximalReturnData = profile.Verify(word);
    bool provedMechanicalStep = profile.MechanicalBoundHolds;
    bool provedRichPeriodClassification = profile.CoordinatesAreNonnegative;
    Console.WriteLine($"phase={profile.Phase}, (l,k)=({profile.ShortCoordinate},{profile.LongCoordinate})");
}
foreach (var certificate in analysis.Decompositions) {
    bool leanPredicateCheckedExactly = certificate.Verify(word);
}
```

`SearchLimitReached` is kept distinct from failure so a bounded experiment can never be mistaken for a proof or
counterexample. The profile's `MechanicalBoundHolds` and `CoordinatesAreNonnegative` read back the Lean-proved
mechanical bracket and rich-period classification: at the least phase with `F_(phase+3)-2 >= maximalOverlap`, both
signed Cassini coordinates are nonnegative.

### Hubert-converse exploration

The remaining equality problem lies outside the Fibonacci construction. It is the general
balanced-word converse: combine Hubert's coloring representation with all Sturmian directive tails,
all finite preperiod congruence components, and every attainable constant-gap coloring period. The
research APIs make that finite exact layer executable:

```csharp
using Puck.Maths.Research;

var coloringPeriods = ConstantGapCoveringResearch.PeriodSpectrum(
    symbolCount: 5,
    inclusiveMaximumPeriod: 16);

var spectrum = SturmianReturnSpectrumResearch.ComponentMinimum(
    period: [1, 2],
    leftColoringPeriod: 4,
    rightColoringPeriod: 8);

var witness = spectrum.Minimum.Phases
    .First(phase => phase.Colored == spectrum.Minimum.ColoredLimsup);
bool congruencesHold = SturmianReturnSpectrumResearch.CongruenceHolds(
    witness.Matrix,
    witness.ColoredWitness,
    leftColoringPeriod: 4,
    rightColoringPeriod: 8);
Console.WriteLine($"components={spectrum.CycleCount}, minimum={spectrum.Minimum.ColoredLimsup}");
```

### Odd-cyclic incidence and executable CRT evidence

`Puck.Maths.Research.OddCyclicIncidence` accepts the mathematical boundary
rather than a particular polytope: an odd cycle order, a number of ray/object
orbits, and one packed incidence polynomial per letter×ray-orbit pair. Bit
`p` means that the chosen context-orbit generator contains the object at phase
`p`. The class verifies or derives the irreducible factors of `tⁿ+1`, derives
all syndromes, and applies the square-free polynomial Chinese remainder
theorem.

```csharp
using Puck.Maths.Research;

// One C3 context orbit. Its generator meets its only ray orbit at phases 0
// and 1, so its incidence polynomial is 1+t (binary 011).
var incidence = new OddCyclicIncidence(
    cycleOrder: 3,
    rayOrbitCount: 1,
    letterCount: 1,
    columns: [0b011UL]);

var result = incidence.Analyze(
    selectedLetters: [0],
    verifyExpandedMatrix: true);

// True: every expanded ray occurs evenly, the total selection is odd, and
// the all-contexts relation is the kernel's only nonzero relation.
bool irreducible = result.IsIrreducible;

// The fast CRT computation and independently expanded GF(2) matrix agree.
bool theoremCheck = result.CrtMatchesExpanded is true;
```

For production enumeration, call `Analyze(..., verifyExpandedMatrix: false)`:
only the small finite-field ranks are computed. Turn direct verification on
for certificates, tests, or sampled audit words. `IsSyndromeCircuit` is the
cheap first filter; if it fails, the word cannot be irreducible and no
extension-field ranks are needed.

The implementation scope is deliberately explicit:

- the cycle order must be odd and below 63;
- automatic factorization is available through order 31;
- larger orders accept caller-supplied factors, which are rechecked for exact
  product, irreducibility, and pairwise coprimality;
- the action must be free and inputs must represent complete cyclic orbits;
- this is binary incidence algebra, not a general real-valued geometry solver.

`AutomaticCyclicIncidence` composes this finite incidence analysis with a
positional or quadratic-Ostrowski DFAO. It computes prefix and range relations
without scanning their terms, and it can compile positional prefix accumulation
back into a finite selector. The binary Gray factory gives a canonical automatic
walk through all `2^letterCount` selection masks. The digit systems and automata
it composes are described under [Automatic integer sequences](#automatic-integer-sequences).

## Documentation

📚 [Puck.Maths](../../../docs/reference/maths.md) · 🛠️ [Contributing to Puck](../../../docs/development/contributing.md)
