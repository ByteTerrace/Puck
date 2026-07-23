# Puck.Maths

Low-level deterministic numeric primitives for the Puck engine: signed and
unsigned binary **fixed-point** types, vectors, rotations, rigid transforms,
spatial coordinates, reproducible random distributions, and a kit of
**width-agnostic integer routines**.

The simulation primitives are deterministic and the hot scalar/vector paths are
allocation-free. Generic integer algorithms use `System.Numerics` interfaces so
one implementation serves multiple integer widths. Table construction and large
prime-counting operations may allocate or rent working storage. `Puck.Maths` has
no external package dependencies and remains a leaf library.

```text
namespace Puck.Maths
target     net10.0
deps       none
```

---

## At a glance

| Type | Kind | What it's for |
|------|------|---------------|
| `UFixedQ4816` | `readonly record struct` | UQ48.16 fixed-point — 48 integer bits, 16 fraction bits (range `[0, 2⁴⁸)`). Implements the complete .NET `INumber<T>` + `IUnsignedNumber<T>` surface. |
| `UFixedQ0016` | `readonly record struct` | UQ0.16 fraction — a real number in `[0, 1)` stored in 16 bits. |
| `UFixedQ0032` | `readonly record struct` | UQ0.32 fraction — a real number in `[0, 1)` stored in 32 bits. |
| `FixedQ4816` | `readonly record struct` | SIGNED Q48.16 fixed-point (two's-complement) — the signed companion to `UFixedQ4816`, implementing the complete .NET `INumber<T>` + `ISignedNumber<T>` surface, with deterministic `Sqrt`/`Atan2`/`Sin`/`Cos`/`SinCos`/`Log2`/`Exp2`/`Pow` (pure-integer table/polynomial kernels, each within ~0.65 ULP where absolute ULPs are representable; the square root's hardware seed is settled to the exact integer floor); the raw-bits carrier for every fixed-point value crossing a deterministic-simulation boundary (e.g. the `Puck.Scripting` WASM addon ABI). `SinCos` inverts `Atan2`; `Exp2` inverts `Log2`; `Pow` computes whole-number exponents by exact squaring. The everyday helpers round it out: `Abs`/`Sign`/`CopySign`, `Min`/`Max`/`Clamp`, `Floor`/`Ceiling`/`Round`/`Truncate`/`Fractional`, and `Lerp` (endpoint-exact linear interpolation). |
| `FixedRateAccumulator` / `FixedVector3RateAccumulator` | `struct` | Exact-tick integration of Q48.16 per-second rates. Division remainders carry across fixed updates, so 120 integrations of one unit/second over 1/120 second total exactly one represented unit instead of repeating a rounded step. Use the same primitive for acceleration → velocity and velocity → position; its remainder fields are authoritative snapshot/hash state. |
| `DiscreteMeasure` | `readonly record struct` | An exact integer-valued measure on integer intervals: `Cumulative(n) = floor(rate*n + offset)`, and any range receives the difference of its two boundaries. One neutral object covers balanced jobs-per-frame, clock/sample conversion, quotas, pacing, density, and 1D point sets. It is stateless and randomly seekable; adjacent ranges compose exactly; rational rates are periodic, while quadratic-surd rates are exactly aperiodic. `LowerBound`/`IndexContaining` provide direct inverse lookup without scanning. |
| `CompiledDiscreteMeasure64` | `readonly record struct` | The allocation-free signed-64-bit execution form produced by `DiscreteMeasure.TryCompileInt64`. Its rational kernel stores two reduced fractions; its bounded quadratic kernel clears denominators once and uses an exact two-limb `UInt128` square-root kernel. Both avoid `BigInteger` at runtime, handle `AmountAt(long.MaxValue)`, and expose bounded `Try` forms. A quadratic source compiles only when its full signed-long core domain is proved to fit the fixed-width envelope. |
| `FixedVector2` / `FixedVector3` | `readonly record struct` | 2D/3D vectors of `FixedQ4816` components — deterministic, bit-identical world-space math; `Dot`, `Wedge`/`Cross`, complex/quaternion products, and rotations widen every product and round once per result component. `FixedVector3.Normalize` is scale-free; `Length`/`LengthSquared` saturate only when the nonnegative result cannot fit, while `TryLength`/`TryLengthSquared` expose that boundary. |
| `FixedQuaternion` | `readonly record struct` | Deterministic 3D rotation: `FromAxisAngle` (fixed-point SinCos), full-width fused Hamilton `*` and `Rotate`, `Slerp` (shortest-arc, nlerp guard), exact-denominator `Inverse`, and scale-free `FromTo`/`Normalize`. Norm properties saturate with `TryLength` variants for explicit overflow. Implements the applicable generic-math operator interfaces, so it composes with `FixedDual<T>`. |
| `FixedComplex` | `readonly record struct` | Deterministic 2D rotation (the yaw-plane analog of the quaternion): `FromAngle`, full-width fused `*`/`Rotate`, full-range exact-rounding division, scale-free `FromTo`/`Normalize`, and full-width saturating `Magnitude`/`MagnitudeSquared` with explicit `TryMagnitude` variants. |
| `FixedDual<TValue>` | `readonly record struct` | The dual construction `a + b·ε` (`ε² = 0`) over any carrier with the required generic-math operators and identities: over `FixedQ4816` it carries a quantized formal forward-mode sensitivity (`FixedDual.Variable` seeds and `Dual` follows the ideal expression's chain rule; the discrete raw-bit program has no classical derivative); over `FixedQuaternion` it is the dual quaternion behind `FixedRigidTransform`. |
| `FixedRigidTransform` | `readonly record struct` | Rotation + translation as one unit dual quaternion: normalized `FromRotationTranslation`/`FromDualQuaternion` boundaries, fast raw composition by `*`, explicit `ComposeNormalized` for long chains, matching generic-math multiplication/identity interfaces, `TransformPoint`, `Inverse`, `Normalize`, and screw interpolation by `ScLerp`. Positional construction is the documented unchecked representation seam. Precision ≈ 2⁻¹⁵ relative to translation magnitude. |
| `WorldCoord3` | `readonly record struct` | Canonical hierarchical world position: signed 64-bit cell indices plus a centred `FixedVector3` local offset — the floating-origin coordinate for planet-scale scenes. Construction and `WithLocal` canonicalize; `TryCreate`, `TryTranslate`, and `TryDelta` expose range failure without exceptions; throwing operators fail rather than silently wrap. Its heterogeneous generic-math interfaces expose position + displacement → position and position − position → displacement without pretending that positions form a vector space. |
| `BinaryIntegerFunctions` | `static` ext. methods | Bit manipulation & base-10 digit ops over `IBinaryInteger<T>`. |
| `BinaryPolynomial` | `readonly record struct` | Exact polynomials over `GF(2)`, packed one coefficient per bit into a `ulong` (degree ≤ 63). Addition is XOR and doubles as subtraction. Euclidean division is exact — `(a / b) * b + a % b == a` with no rounding seam — and `<<`/`>>` are multiplication and division by the indeterminate. Ordinary `*` truncates like every other fixed-width operator in the library and `checked *` reports the loss; both come from one carryless multiply, so there is one thing to be right about. A product that needs its bits above degree 63 is a field operation — `BinaryField<T>` keeps that wide intermediate internally. Rounds out with monic GCD, an irreducibility decision, a **primitivity** decision through degree 32 (strictly stronger — it additionally says the root generates the whole multiplicative group, which is what makes a linear recurrence maximal-period; bounded there because it needs `2ⁿ−1` factored), and the factorization of `tⁿ+1` for odd `n ≤ 31`. It is the modulus carrier beneath `BinaryField<T>` and cyclic-incidence analysis, and is independently useful for binary linear codes and CRC-style recurrences. There is deliberately no ordering: a polynomial ring has none, and the packed carrier would have made `<` compile and lie. |
| `BinaryField<T>` / `BinaryFields` | `readonly record struct` / `static` | The finite field `GF(2^k)` for any `k` from 1 through 128, over a caller-chosen or canonical irreducible modulus. Elements are bare packed integers — `byte`, `ushort`, `uint`, `ulong`, `UInt128` — so a region of field values is just a span and the field object names the structure they live in: `Multiply`, `Square`, `SquareRoot`, `Inverse`, `Divide`, `Exponentiate`, `Reduce`. The modulus is stored as its tail because a degree-`k` modulus needs `k+1` bits and would not fit the carrier at the top degree; nothing else is precomputed, so constructing a field is free. Everything is exactly associative, commutative, and distributive, with an exact inverse for every non-zero element — unlike a rounded fixed-point product, a field product is safe to reassociate. `MultiplyAccumulateRegion` is the vectorized bulk primitive that erasure coding, syndrome evaluation, and elimination over the field all sit on. `BinaryFields` holds the canonical minimum-weight fields at degrees 8, 16, 32, 64, and 128. |
| `PrimeField64` / `QuadraticExtensionField64` | `readonly record struct` | The finite fields of odd characteristic — the companions to `BinaryField<T>`: `F_p` for an odd prime `p < 2⁶²`, and its quadratic extension `F_{p²} = F_p(√d)` over a non-square `d`. Elements are bare `ulong` (base) or `(A, B)` pairs (extension); the field object names the structure they live in, so nothing but the modulus (and the non-square) is stored. `Add`/`Subtract`/`Multiply` (widen to `UInt128`, reduce once)/`Negate`/`Pow`/`Inverse`, `LegendreCharacter` (the quadratic character), `TrySqrt` (both residue classes; a nonresidue-assisted descent when `p ≡ 1 mod 4`), a `BatchInverse` that turns a whole region over with one inversion, and a validated `Create` that decides primality exactly to a fixed complete witness set. The extension adds `Frobenius`/`Norm`/`Trace` and a deterministic smallest-non-square chooser. For deterministic odd-base permutations and scrambles, odd-radix sampling nets, procedural incidence structures over a prime alphabet, and exact square roots modulo a prime. |
| `NumberTheoryFunctions` | `static` | Arbitrary-width number theory, the past-`2⁶⁴` companion to `PrimeField64`: `JacobiSymbol` (binary reciprocity — no factorization, no exponentiation), `SegmentedPrimeSieve`/`EnumeratePrimes` (a closed range of primes in working memory bounded by the range's square root, delivered by callback or lazily), and `HenselLiftRoot` (lifts a simple polynomial root from a prime to a prime power — the derivative-unit case, honest about the non-unit failure mode). |
| `FixedSplit` | `readonly struct` | Split numbers over `FixedQ4816` — the hyperbolic sibling completing the planar trio with `FixedComplex` (`i² = −1`) and `FixedDual` (`ε² = 0`): here `j² = +1`, multiplication is hyperbolic rotation, and the quadratic form `u² − v²` is indefinite — it vanishes on the two diagonals and the ring has zero divisors, so division is unit-checked. `FromRapidity` composes boosts/squeezes the way `FixedComplex.Rotate` composes turns. For scaling flows, rate composition, and anything metallic-mean-shaped. |
| `QuadraticAlgebra<TScalar>` | `readonly record struct` | The unifying skeleton behind every two-dimensional number system in this library: adjoin one root of `x² = P·x + Q` to any carrier satisfying six operator interfaces. `(0,−1)` is `FixedComplex`, `(0,0)` is `FixedDual`, `(0,+1)` is `FixedSplit`, `(k,1)` is the metallic surd world, and over `PrimeField64` it is `F_{p²}` — all verified reproductions (`tools/quadratic-algebra-verifier.cs`). Carries `Conjugate`/`Norm`/`Trace`/`Discriminant`, the division-free companion (Möbius) step on projective pairs, and `CompanionPower` — the closed-form engine for metallic and continued-fraction sequences. The discriminant's sign/character (negative, zero, positive; split, ramified, inert) is the one trichotomy every specialization inherits. |
| `GeometricAlgebra` / `Multivector` | `readonly struct` / `struct` | The multi-generator quadratic algebra over `FixedQ4816`: signatures `(p, q, r)` up to four generators (≤ 16 blades), the geometric product driven by a once-computed blade-sign table (reordering parity × signature squares; degenerate generators annihilate). Frees the generator count `QuadraticAlgebra` fixes at one: the planar trio is the one-generator case, `FixedQuaternion` is the even subalgebra of `(3,0,0)` (bit-identical, mapped explicitly), and rigid motions are the `SandwichTransform` of motors in `(3,0,1)` — reproduced against `FixedRigidTransform` to the family's measured fixed-point envelope. `Exponential` branches on the bivector square's sign — circular, hyperbolic, or degenerate — the discriminant trichotomy one more time, now steering rotors, boosts, and translators. Verified in `tools/geometric-algebra-verifier.cs`. |
| `MonogenicAlgebra<TScalar>` | `readonly struct` | The any-degree adjunction that frees the degree `QuadraticAlgebra` fixes at two: adjoin one root of one monic modulus `xⁿ + m₍ₙ₋₁₎xⁿ⁻¹ + … + m₀` to any carrier satisfying the same six operator interfaces. Degree 2 *is* `QuadraticAlgebra`; degree `k` over the two-element carrier *is* the `BinaryField` tower — both verified reproductions (`tools/monogenic-algebra-verifier.cs`). Elements are immutable power-basis coordinate vectors; `Multiply` is schoolbook plus one division-free companion-recurrence reduction; `CompanionPower` is the closed-form engine for order-`n` recurrences; `ProjectiveStep` is the degree-`n` Möbius step; `Trace`/`Norm` ride the multiplication matrix (cofactor for `n ≤ 4`, division-free characteristic-polynomial elimination beyond — chosen over pivot-dividing elimination precisely so the two-element carrier never stalls); `CharacteristicDiscriminant` is the resultant of the modulus and its derivative, the degree-2 `Δ` generalized. First new ground: the degree-3 world of `x³ = x + 1`, whose companion powers count the order-3 additive sequence the way degree 2 counts the metallic ones. |
| `QuadraticIntegerArithmetic` | `static` | Primality and factorization *inside* `QuadraticAlgebra<BigInteger>` — the concepts of `PrimeExtensions`, one story up: `IsPrimeElement` via norms, `SplittingCharacter` (split/inert/ramified by the Jacobi symbol), `FundamentalUnit` (the continued-fraction walk; the `Δ = 5` world answers with the golden unit), `CanonicalAssociate` (documented deterministic normalization under the unit action), and `TryFactorize` — exact, deterministic, ascending-by-norm factorization that reassembles bit-identically, and that **fails honestly with a class-group witness** when a needed prime is non-principal (the obstruction names the rational prime and its splitting; in the `Δ = −20` world, `6` refuses to factor and the API tells you exactly why). Zero obstructions across the nine imaginary class-number-one worlds; the failure witness is a feature — non-unique factorization made measurable. |
| `DoublingAlgebra<TInner>` / `IConjugationRing<T>` | `readonly record struct` / interface | The doubling construction: builds each rung of the division-algebra ladder from ordered pairs of the rung below, over any conjugation ring. Adapters absorb `FixedComplex` (floor 1, bit-identical on the exact sublattice) and `FixedQuaternion` (floor 2 — bit-identical on the sublattice; off-lattice the hand-written type's fused four-product rounding diverges, measured and documented, with exact agreement proven over an exact carrier); a third wrap reaches the octonions. `Commutator`/`Associator` make the price of each floor a computed witness — commutativity dies at the quaternions, associativity at the octonions with alternativity retained — not a comment. Verified in `tools/doubling-algebra-verifier.cs`. |
| `DigitalNetSampler` / `NestedDyadicPermutation` / `UnitriangularBitMix` / `SphericalCapSampleTable` | `static` | Digital `(0, m, 2)`-nets over `GF(2)` — Sobol' sequences — whose stratification is a **theorem**, not a measurement: the first `2ᵐ` points put exactly one point in every dyadic box of area `2⁻ᵐ`. A point is the XOR of the direction vectors its index's set bits select, so it is pure integer, stateless (sample `N` is a function of `N` alone — nothing to snapshot), and seekable. Direction numbers come from a generator polynomial proved primitive by `BinaryPolynomial`. Two randomizations are offered because two are all that provably preserve the net: a digital shift (`DeriveScramble`, XOR by a fixed vector) and `NestedDyadicPermutation` (index shuffling that carries an aligned dyadic block onto an aligned dyadic block — an *arbitrary* mixing bijection does not, and would destroy stratification). `UnitriangularBitMix` is the invertible key mix beneath them, bijective by theorem rather than by tuning. `SphericalCapSampleTable` bakes a whole net-point-to-cap-direction map into one flat `uint` table, so a consumer needs no `sqrt`, reciprocal, normalize, or trigonometry at the point of use. |
| `UnsignedNumberFunctions` | `static` ext. methods | Pairing functions, prime factorization, modular inverse, integer roots. |
| `PrimeExtensions` | `static` ext. methods | Deterministic primality, n-th prime, prime counting (`uint`). |
| `MonotonicPartitioner` | `static` | Jump-consistent routing of 65536 values (or a `Guid`, via its trailing entropy) onto 1–1024 buckets: deterministic (a client/server agreement — both ends of a wire compute the same route), monotonic (growth only moves values into the new bucket), uniform (⌊65536/N⌋ or ⌈65536/N⌉ per bucket, quantization skew ≤ ~2 % at every count). Ownership chains compressed into checkpoint + varint tail-stream tables; `GetMetrics` reports a value's rank, migration count, and distance to its next migration (`MonotonicPartitionerMetrics`). |
| `Pcg32XshRr` | `struct` | Deterministic seedable PCG32 (XSH-RR) for simulation: reference-vector-exact, per-entity streams, O(log n) `Advance`, `NextUFixedQ0016`/`NextUFixedQ0032` fraction draws, `NextGaussianPair` standard normals (Box–Muller; exactly two advances per pair), and an in-place `Shuffle` (Fisher–Yates). Generator state is simulation state — fully readable and restored exactly by `FromRawBits`; persist it in snapshots. |
| `AliasTable<TElement>` | `sealed class` | Immutable weighted-choice table (Walker/Vose): exact-integer construction from ORDERED entries (order is part of the contract), O(1) sampling at exactly two generator advances per draw. Weights come as `ulong` (exact), `double` (deterministically quantized at 2⁵³ resolution — the run-document seam), or `FixedQ4816`/`UFixedQ4816` (exact). Bake a `puck.run.v1` distribution once at load, then sample deterministically. |
| `FieldNoise` | `static` | Deterministic spatial randomness: a stateless pure function from (seed, position) to smooth noise in `[−1, 1]` (value noise, quintic fade, octave fBm; the `WorldCoord3` overload is exact at planet scale). Nothing to snapshot. `SampleGradient` returns the value and its exact analytic gradient in one pass, sharing the eight corner hashes — normals, flow direction, and slope limits without differencing `Sample` or choosing a step size. `Hash` exposes the raw lattice hash for per-cell decisions. |
| `LowDiscrepancy` | `static` | Deterministic even-coverage sequences: `R1` (golden ratio) and `R2` (plastic number) map an index to `[0, 1)` points that cover without clumping — placement, spawn scatter, stratified sampling. One multiply per component; the 64-bit wrap performs the mod-1. |
| `LayerSequence` | `readonly record struct` | Layered index spaces (the generalized figurate numbers): a `Seed`-sized core wrapped by layers that start at `Start` and grow by `Step`, with **constant-time** index→layer lookup by inverting the quadratic prefix sum in pure integer arithmetic — no walking, no floating point. A negative `Step` bounds the space; `Project` saturates against that horizon with linear-overflow and square-root-depth excess channels. |
| `CyclicRotation` | `static` | Deterministic, perfectly looping rotation driven by a tick: four planes turning at speeds {1, 7, 11, 13} in 12° steps, resyncing to the identity every 30 ticks. Rotations are `FixedComplex` read from a baked table of the 30th roots of unity, indexed by `tick mod 30` and never accumulated, so the loop closes bit-exactly with no drift on any backend. For looping deterministic animation: SDF spins, light-phase cycles, colour wheels. (Mathematically, the Coxeter element of E₈ — see `SymmetryLattice`.) |
| `SymmetryLattice` | `static` | A fixed, maximally symmetric set of 240 nodes in 8D (the root system of E₈), addressed by index. `Reflect` composes to the whole symmetry group W(E₈) (order 696,729,600); `Cycle` is the order-30 element `CyclicRotation` drives, cutting the nodes into `Ring`s of thirty; `Antipode`, `CanonicalRay`, and `AreOrthogonal` expose the exact 120-ray incidence seam; `RayCycleFactors` gives the five binary factors for the induced order-15 action; `Project` lays the roots on the Coxeter plane. |
| `OddCyclicIncidence` / `OddCyclicWordAnalysis` | `sealed class` | Geometry-neutral exact analysis of any free odd-cyclic binary incidence system. A compact letter×ray-orbit polynomial table yields `t=1` syndromes, the syndrome-matroid circuit filter, ranks over every CRT field, exact expanded nullity, and parity-proof irreducibility. Optional direct expansion recomputes the large binary rank and fails if it disagrees with the CRT sum, making the theorem executable as a per-word certificate. |
| `AutomaticSelectionAutomaton` / `AutomaticCyclicIncidence` | `sealed class` | Composes a finite-output positional or quadratic-Ostrowski sequence with `OddCyclicIncidence`. Prefix and range masks are accumulated exactly in `GF(2)` without scanning the range; positional prefix accumulation can itself be compiled to a DFAO. Reusable factories cover digit-sum residue selectors and the canonical binary Gray orbit through every selection mask—and hence every kernel relation—while safety-bounded compilation avoids materializing exponential output spaces. |
| `HilbertCurve` | `static` | The Hilbert space-filling curve: an exact bijection between a 1D distance and a 2D grid point (`Encode`/`Decode`) that preserves locality — consecutive distances are always grid neighbours, unlike Morton/Z-order (`BitwisePair`), which jumps at power-of-two seams. For cache-coherent chunk/tile ordering, spatial hashing, texture swizzling. `order` in `[1, 31]`. |
| `HexCoord` | `readonly record struct` | An exact hexagonal grid coordinate — the Eisenstein integer `Q + R·ω`. Because it is a genuine number ring, a 60° rotation is an exact integer multiply (`RotatedLeft`/`RotatedRight`, order 6) with no drift, unlike `FixedComplex`; `Length` (hex-grid distance), the six neighbours (`Direction`/`Neighbor`), the ring product `*` (rotation composed with scaling), and `Round` (fractional position → nearest cell, deterministic `FixedQ4816`) are all exact. For deterministic hex-grid games. |
| `ModularTransform` | `readonly record struct` | An exact element of the modular group — a 2×2 integer matrix of determinant one — acting on the hyperbolic plane by `z ↦ (A·z + B)/(C·z + D)`. The one object beneath the library's three motions: `Classify` sorts it by trace into the elliptic rotations (the sixth root of unity of `HexCoord`), the parabolic tick shear (the kinematics step of `LayerSequence`), and the hyperbolic golden inflation (the step of `MetallicQuasicrystal`). Composition is matrix product; `Inverse` is the adjugate, no division; `Apply` moves a cusp (rational `p/q`, with `∞ = 1/0`) exactly and an interior `FixedComplex` point at the one rounding seam; `GaussReduce` carries a positive-definite form into the fundamental domain by an exact word in `S` and `T`, terminating because the leading coefficient is a strictly decreasing positive integer. |
| `ContinuedFraction` | `static` | The eventually periodic continued-fraction expansion of an exact quadratic irrational `(p + q·√d)/r`, in pure integer arithmetic — the symbolic coding of a closed geodesic on the modular surface. The golden ratio codes to the all-ones period `[1; 1, …]` and the silver ratio `1 + √2` to the all-twos period `[2; 2, …]`: the two shortest geodesics, the two units that drive the golden and silver cases of `MetallicQuasicrystal`. Fills a caller span, reporting where the period begins and how long it is; no approximate seam. |
| `QuadraticSurd` | `readonly struct` | An exact real-quadratic value `(a+b·√d)/c`: field arithmetic, sign, comparison, floor, and ceiling use arbitrary-width integers and no floating point. Square-equivalent radicands interoperate in equality, hashing, ordering, and arithmetic without requiring arbitrary-width integer factorization; ordering values from genuinely different quadratic fields uses certified rational enclosures, while arithmetic remains field-local. A square radicand collapses to a rational value, and the default struct is exact zero. |
| `PolynomialContinuedFractionTail` / `PolynomialContinuedFractionAnalysis` | `static` / `sealed class` | Exact analysis of every integer family `sₙ=p·n+q+(r·n²+u·n+v)/sₙ₊₁` with non-negative base and positive numerator on all positive indices. A successful `Analyze` certifies existence and uniqueness of the positive tail, returns its exact quadratic slope/offset/residual, constructs an integer-checkable interval `|sₙ−(λn+β)|≤H/n` beyond an explicit cutoff, and generates any requested finite number of exact asymptotic coefficients. `TryRationalTailCertificate` recognizes polynomial-denominator rational-function tails over the full characteristic field `Q(λ)` through the dense solver's explicit degree-128 resource ceiling, caches the result, and certifies positivity and absence of positive-integer poles; the former linear-fractional recognizer remains as a specialized compatibility API. `TryDegreeOneMinimalityReduction` recognizes the double-square subfamily covered directly by the 2026 minimality theorem. `TryOnePeriodEqualityReduction` additionally recognizes the aligned irrational-characteristic branch `p(u-r)=2rq`, where the transformed Gauss parameters remain rational and equality reduces to an effective 1-period relation. |
| `QuadraticInflation` | `readonly record struct` | The inflation lens of a quadratic irrational: reads its `ContinuedFraction` period as the exact substitution matrix `∏[[aᵢ,1],[1,0]]`, exposing the conjugacy invariants (`Trace`, `Determinant = (−1)^period`, `Discriminant`), the closed-geodesic `Axis` (a hyperbolic `ModularTransform`), and the `InflationFactor` (the Perron eigenvalue). Golden recovers discriminant 5 and factor φ, silver discriminant 8 and factor 1 + √2 — read from the continued fraction, not fed in. |
| `QuadraticQuasicrystal` / `QuadraticQuasicrystalIndex` | `static` / `sealed class` | The tiling word of the quasicrystal beneath **any** quadratic irrational, for an arbitrary CF period. `Word` streams its Sturmian fixed point, while `Compile` builds a straight-line substitution grammar supporting exact `TileAt`, prefix long-tile rank, and exact position at arbitrarily large `BigInteger` indices in period-times-logarithmic work without generating the preceding prefix. |
| `FibonacciResearch` / `GoldenInteger` / `FibonacciRulerWordIndex` / `FibonacciReturnResearch` | `static` / `readonly record struct` / `sealed class` / `static` | Lean-derived exact tools for the golden research branch. Fast doubling and closed two-adic rank formulas jump Fibonacci and golden-ring phases at arbitrary `BigInteger` indices; primitive pairs modulo `2^t` are classified into the two proved projective orbits by their norm modulo eight; `FibonacciSymmetricMinimum.Find` returns a self-verifying exact best-return certificate for any positive period; the ruler index gives random access, prefix counts, and factor counts for the balanced construction; and the return analyzer exposes the canonical signed Cassini coordinates, exact mechanical-error bracket, and every successful period-decomposition certificate. Crucially, a negative coordinate remains visible as a counterexample profile instead of being filtered out. |
| `ConstantGapCoveringResearch` | `static` | Exact-cover machinery for Hubert colorings: constructs the canonical ruler coloring, decides attainable least periods, enumerates period spectra, and returns independently verifiable residue-class witnesses. Searches and witness verification have an explicit period ceiling. |
| `SturmianReturnSpectrumResearch` | `static` | Exact Proposition-20 return-spectrum machinery for arbitrary periodic Sturmian directives and periodic left/right colorings. It evaluates a specified finite preperiod, enumerates every determinant-compatible congruence component representing all preperiods, reconstructs an explicit positive prefix for a component, and returns exact phase witnesses and minima; phase-invariant candidate tables make broad searches practical, while a separate optimized Fibonacci path supports cross-checking. |
| `MetallicQuasicrystal` | `static` | The metallic-mean quasicrystals `δₙ = (n + √(n²+4))/2` for any index — golden is `n = 1` (the Fibonacci chain), silver is `n = 2` (the Pell chain). `Word` streams the tiling; `Contains`/`StartsLongTile`/`Next`/`Previous`/`Position` address points by ring coordinate `a + b·δₙ` in O(1), the membership-and-traversal surface generalized from the retired hand-coded golden and silver chains — `n = 1` reproduces the former golden chain coordinate for coordinate. Exact integer arithmetic above one fixed-point seam, so it never drifts. |
| `MetallicPolynomialContinuedFraction` | `static` | Exact random access to the metallic polynomial continued fraction: `TailFloor(k, n)` evaluates `⌊sₙ⌋`, where `sₙ = k·n−1+n²/sₙ₊₁`, directly from its proved quadratic-irrational formula. It uses arbitrary-width integer arithmetic and an integer square root instead of a truncation depth or floating-point tolerance; differences of consecutive floors give its associated integer sequence. |
| `SecureRandom` | `static` | Uniform, unbiased, cryptographically secure unsigned draws — NOT for simulation (deliberately non-reproducible); use `Pcg32XshRr` there. |
| `ProbabilityFunctions` | `static` ext. methods | The normal-distribution quantile (inverse CDF): `InverseStandardNormalCdf` and `InverseNormalCdf` evaluate minimax rational approximations by Horner's method with fused multiply-adds over three regions of the probability. |
| `Fnv1aHash` | `struct` | A 64-bit FNV-1a hash accumulator — the allocation-free, endianness-independent state probe a determinism/replay check folds per tick. `Create` primes the offset basis; `Add` folds bytes and 32/64-bit values least-significant byte first; `Value` reads the digest. |

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

`tools/fibonacci-research-verifier.cs` independently checks the fast Fibonacci arithmetic, two-adic ranks, both
projective orbits, symmetric minima, the balanced ruler construction, and a finite box of maximal right returns
using exact integer and quadratic-surd comparisons. `SearchLimitReached` is kept distinct from failure so a bounded
experiment can never be mistaken for a proof or counterexample.

For a larger configurable regression sweep of the kernel-checked
`FibonacciRichPeriodClassification` lemma:

```text
dotnet run -c Release tools/fibonacci-return-classification-explorer.cs -- 4096 2048 3 32768
```

The explorer exits `1` with the first exact counterexample candidate, including its canonical Ostrowski
representations, `2` if the right-mismatch bound is inconclusive, and `0` with phase, coordinate-frequency,
boundary-case, and extremal-certificate diagnostics when the whole finite box passes. It separately verifies the
Lean-proved mechanical bracket and rich-period classification: at the least phase with
`F_(phase+3)-2 >= maximalOverlap`, both signed Cassini coordinates are nonnegative.

### Hubert-converse exploration

The remaining equality problem is no longer inside the Fibonacci construction. It is the general
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

The verifier independently checks exact-cover search against all small periodic words, continued-
fraction tail recurrences, published Fibonacci spectra, phase maximization, optimized/general
agreement, sampled finite preperiods, and the explicit 11-letter counterexample to the formerly
conjectured stronger colored-lifting inequality. The explorer performs a configurable exact sweep
across constant-gap period spectra and primitive directive necklaces; the second search scans all
period pairs in its determinant envelope and stops on an exact theorem-bound or colored-lifting
counterexample:

```text
dotnet run -c Release tools/hubert-converse-verifier.cs
dotnet run -c Release tools/hubert-converse-explorer.cs -- 5 2 2 32
dotnet run -c Release tools/colored-lifting-conjecture-search.cs -- 64 3 4
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

The focused public-API verifier factors every odd order through 31, exercises
a caller-factored order-61 system, compares CRT and direct expanded nullities
across deterministic generated systems, and replays all eight 600-cell words
plus nine 120-cell examples:

```text
dotnet run -c Release -p:NuGetAudit=false tools/odd-cyclic-maths-verifier.cs
```

`AutomaticCyclicIncidence` composes this finite incidence analysis with a
positional or quadratic-Ostrowski DFAO. It computes prefix and range relations
without scanning their terms, and it can compile positional prefix accumulation
back into a finite selector. The binary Gray factory gives a canonical automatic
walk through all `2^letterCount` selection masks. The constructive theorem, state bound,
120-cell application, and focused verifier are documented in
[Automatic cyclic incidence](../../docs/automatic-cyclic-incidence-theorem.md).
Its first nontrivial quadratic-Ostrowski application is the
[Fibonacci--600-cell theorem](../../docs/fibonacci-600-cell-automatic-parity.md):
an exact 255-period selector recurrence producing irreducible complete-C15-orbit
proofs in 32 residue classes.

> `BinaryIntegerConstants<T>` is an internal helper (width, log2-width, the constants
> 9 and 10 for an arbitrary `T`) and is not part of the public surface.

**Verifying changes**: the fast contract gates are Post A1
(`dotnet run --project src/Puck.Post -c Release -- --stage fixed-point`), the
binary-field stage (`… -- --stage binary-field`), and the digital-net stage
(`… -- --stage digital-net`); the deep oracle battery — ULP sweeps,
independent wide-integer specifications, distribution tests, benchmarks — is
`dotnet run -c Release tools/maths-battery.cs` (~2–3 minutes). Run the battery before
completing any change to this project.

---

## Fixed-point types

The fixed-point structs store an integer whose value is the represented real
number **scaled by `2^FractionBitCount`**. `FixedQ4816` uses signed `long`;
`UFixedQ4816`, `UFixedQ0016`, and `UFixedQ0032` use `ulong`, `ushort`, and
`uint`. `FixedQ4816` and `UFixedQ4816` implement the full .NET generic-number
contract (`INumber<T>` plus signed/unsigned classification); the Q0 fraction
types expose only the truthful fine-grained operator interfaces. All four
therefore drop into generic code at the strongest constraint their
representable identities support.

The unsigned types use the most-significant bit as an ordinary magnitude bit.
Their subtraction and overflow behavior wrap unless a saturating helper is
selected. `FixedQ4816` uses ordinary two's-complement signed semantics and is the
authoritative simulation scalar for positions, vectors, rotations, and fields.

### Choosing a type

- **`UFixedQ4816`** — when you need an integer part: positions, sizes, accumulated
  advances. Range `[0, ~2.8×10¹⁴)`, resolution `2⁻¹⁶ ≈ 1.5×10⁻⁵`.
- **`UFixedQ0016` / `UFixedQ0032`** — pure fractions in `[0, 1)`: normalized
  coordinates, blend factors, sub-pixel offsets. There is **no representable `1.0`**
  (hence no `One`, no `MultiplicativeIdentity`, no `++`). Multiplication is closed over
  `[0, 1)` and cannot overflow; addition/division/left-shift can leave the range.

### Wrap vs. saturate

Ordinary operators wrap (modular arithmetic, matching `unchecked` integer semantics).
Their `checked` forms throw when the final rounded result is outside the carrier. When
you want clamping instead, call the explicit helpers:

```csharp
using Puck.Maths;

var a = UFixedQ4816.FromInteger(value: 3);     // 3.0
var b = UFixedQ4816.FromDouble(value: 0.25);   // 0.25

var sum     = a + b;                                  // 3.25  (wraps on overflow)
var clamped = UFixedQ4816.AddSaturating(x: a, y: b);  // 3.25, but pins to MaxValue
var product = a * b;                                  // 0.75  (round-half-to-even)

double d = (double)product;        // 0.75
string s = product.ToString();     // "0.75"  — exact decimal expansion, invariant culture
```

### Rounding & conversion notes

- `*` and `/` round **to nearest, ties to even**. The `…Unchecked` variants truncate
  and hand back the remainder so you can build your own rounding.
- `FromDouble` rounds to nearest (ties to even) and **clamps** out-of-range / negative /
  NaN inputs into range.
- Generic `T.CreateChecked`, `T.CreateSaturating`, and `T.CreateTruncating` convert
  numeric values, never raw storage. Checked conversion throws on range/NaN/infinity; saturating clamps
  (NaN becomes zero); truncating uses the same decimal-like range clamp. Fractional
  input is quantized to nearest, ties to even, in all three modes.
- Ordinary arithmetic operators wrap; explicit `checked` operators and checked generic
  arithmetic throw on overflow after the operation's ties-to-even rounding.
- `ToString` / `TryFormat` emit the **exact** decimal expansion (a `/2ⁿ` fraction always
  terminates). Parameterless formatting is invariant; the `G` overload honors an explicit
  provider's decimal separator and rejects unsupported formats.
- `Parse` / `TryParse` use `decimal` only to validate syntax, then quantize the original
  digits directly with round-to-nearest, ties-to-even; arbitrarily long midpoint tails
  cannot double-round. Parameterless overloads are invariant, while provider/style
  overloads honor their enabled culture tokens. Custom digit-bearing sign or currency
  tokens are rejected because they are ambiguous with significand digits.
- In-range exact formatting round-trips: `Parse(x.ToString()) == x`.
- `FromRawBits` / the `Value` member let you reinterpret the raw storage directly.

`UFixedQ4816.DivideUnchecked` and the `FixedQ4816` division operator use a hardware
128÷64 divide on x64 when the quotient provably fits 64 bits (the dividend's high word
is below the divisor), and fall back to `UInt128` arithmetic elsewhere — so every
platform wraps identically instead of the hardware instruction faulting on overflow.

Generic algorithms can now use the ordinary .NET numeric contract without a
Puck-specific algebra hierarchy:

```csharp
static T Sum<T>(ReadOnlySpan<T> values) where T : System.Numerics.INumber<T>
{
    var sum = T.Zero;
    foreach (var value in values) sum += value;
    return sum;
}

FixedQ4816 total = Sum<FixedQ4816>([FixedQ4816.One, FixedQ4816.FromDouble(0.5)]);
```

The loop fixes evaluation order deliberately. Do not parallelize or reassociate a
rounded product merely because its carrier satisfies `INumber<T>`.

### Integrating rates without per-step drift

Do not pre-round a per-second velocity or acceleration into one fixed-update delta and
then repeat that rounded value. Carry the integer-division remainder across updates:

```csharp
const long ticksPerSecond = 50_400;
const ulong stepTicks = 420; // 120 Hz

// The time base is bound once at construction, so Integrate cannot silently reinterpret the
// carried remainder under a different denominator.
var velocityCarry = new FixedRateAccumulator(ticksPerSecond);
var positionCarry = new FixedRateAccumulator(ticksPerSecond);
var velocity = FixedQ4816.Zero;
var position = FixedQ4816.Zero;

// Semi-implicit Euler: acceleration updates velocity, then velocity updates position.
velocity += velocityCarry.Integrate(accelerationPerSecond, stepTicks);
position += positionCarry.Integrate(velocity, stepTicks);
```

The accumulator remainder belongs to the integrated quantity. Include both the remainder and
its `TicksPerSecond` in deterministic snapshots and state hashes, restore them together with
`FromRemainder` / `FromRemainders`, and call `Reset` (or the appropriate vector-axis reset)
when that quantity is teleported, assigned, or clamped. Keep one accumulator per independently
integrated scalar or vector; its denominator is fixed at construction. A default-initialized
accumulator (denominator zero) throws from `Integrate`.

### Measuring discrete rates without carried state

`DiscreteMeasure` assigns indivisible output units to integer input intervals by
flooring one exact affine rate at their boundaries. It is the stateless counterpart
to a rate accumulator: any index or range can be answered directly, and splitting or
joining a range never changes its total.

```csharp
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
`AmountOver`/`Map` use a start and length, while `AmountBetween`/`MapBetween` use two
boundaries. `Translate` moves the allocation origin exactly. `LowerBound` inverts cumulative
amounts, while `IndexContaining` maps an output index back to the input interval that owns it.
Offsets are normalized modulo one, selecting a different allocation origin without
changing the rate. Rational rates expose their exact period; irrational quadratic
rates remain exact and aperiodic rather than being approximated by a long rational
cycle.

For a hot rational path, compile once and retain the bounded value:

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

Compilation accepts bounded rational measures and a proved subset of real-quadratic
measures. The quadratic backend clears denominators during compilation, proves that every
core signed-`long` boundary has a root fitting `Int128`, then performs the exact integer
square root in a fixed 256-bit accumulator made from two `UInt128` limbs. It allocates
nothing and uses no `BigInteger` at runtime. Wider range endpoints and results report
failure through `Try...`; sources outside the envelope remain on the exact unbounded
`DiscreteMeasure`. `LowerBound` and `IndexContaining` use at most 64 monotone boundary
probes.

---

## Randomness

Four primitives, one per shape of randomness. All are deterministic: identical inputs
produce identical bits on every machine.

### Choosing a primitive

- **`Pcg32XshRr`** — *sequential* randomness: a seeded stream with history (combat
  rolls, wander decisions, anything drawn over time). State is simulation state.
- **`AliasTable<T>`** — *weighted choice*: an O(1) pick from a baked discrete
  distribution (loot, spawn kinds, production rules). Build once at load, sample with a
  `Pcg32XshRr`.
- **`FieldNoise`** — *spatial* randomness: a stateless pure function of (seed,
  position) for smooth variation over space (terrain, wind, per-cell decisions via
  `Hash`). Nothing to persist.
- **`LowDiscrepancy`** — *coverage*: `R1`/`R2` map an index to points that fill the
  interval/square evenly (spawn scatter, placement). Stateless.
- **`DigitalNetSampler`** — *provable stratification*: the same shape as `LowDiscrepancy`
  (index in, point out, no state) but a strictly stronger guarantee. `R1`/`R2` are additive
  recurrences: they equidistribute asymptotically and stratify **nothing** exactly — there is
  no `m` for which you can name the boxes and say each holds one point. A `(0, 2)`-sequence
  does exactly that for *every* `m` and *every* box shape, which is a theorem with a finite
  witness (and is checked exhaustively by the `digital-net` stage). It also scrambles: XOR by
  a `GF(2)` vector randomizes the point set without breaking the theorem, so per-pixel or
  per-entity decorrelation is free. Reach for `R2` when "spread out" is enough and for
  `DigitalNetSampler` when the estimator's error depends on real stratification — Monte Carlo
  integration, area-light sampling, anything averaged over many draws.

### Rules that keep it deterministic

- **One stream per system.** Derive each consumer's `Pcg32XshRr` from a master seed
  with small, consecutive stream ids (`Create(masterSeed, streamId)`). Sharing one
  generator across systems couples them through draw order; two systems that drew in a
  different order would diverge.
- **Generator state rides snapshots.** Persist `State`/`Increment`/`Multiplier` and
  restore with `FromRawBits` — a replayed world must resume the exact sequence.
  `FieldNoise` and `LowDiscrepancy` carry no state and need nothing persisted.
- **Advance counting.** `NextUInt32()`, fraction draws, `NextGaussianPair` (2), and
  `AliasTable` samples (2) consume a fixed number of state advances, so
  `Advance`-based seek arithmetic is exact. Bounded `NextUInt32(min, max)` may consume
  extra advances on rejection — as do `Shuffle` (one bounded draw per element from the
  high end down, `n − 1` for a span of length `n`) and any other bounded-draw consumer.
- **Alias tables are order-sensitive.** Entry order is part of the table's identity;
  build from deterministically ordered data. Weights may be `ulong` (exact),
  `double` (quantized deterministically at 2⁵³ resolution), or
  `FixedQ4816`/`UFixedQ4816` (exact).

```csharp
using Puck.Maths;

var masterSeed = 0xC0FFEEUL;                                  // from the run document
var combat = Pcg32XshRr.Create(state: masterSeed, stream: 0UL);
var spawns = Pcg32XshRr.Create(state: masterSeed, stream: 1UL);

var loot = AliasTable.Create<string>(entries: [("common", 0.7d), ("rare", 0.25d), ("epic", 0.05d),]);
var drop = loot.Sample(generator: ref spawns);                // exactly 2 advances

var (sin, cos) = combat.NextGaussianPair();                   // exactly 2 advances, N(0, 1) pair
var sway = FieldNoise.Sample(seed: masterSeed, position: worldPosition, octaves: 4);
var (x, y) = LowDiscrepancy.R2(index: spawnIndex);            // even scatter in [0, 1)²
```

---

## Integer routines

### `BinaryIntegerFunctions` (generic over `IBinaryInteger<T>`)

Branchless, width-agnostic bit and digit operations. A closed generic compiles to a
compact, value-independent instruction sequence, and hardware instructions (`PDEP`/`PEXT`
via BMI2) are used when available.

| Method | Result |
|--------|--------|
| `BitwisePair` / `BitwiseUnpair` | Morton (Z-order) interleave and its inverse. |
| `ReverseBits` | Reverse all bits (SWAR butterfly). |
| `ReflectedBinaryEncode` / `…Decode` | Gray code ↔ binary. |
| `PermuteBitsLexicographically` | Next integer with the same popcount (Gosper's hack). |
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

### `UnsignedNumberFunctions` (generic, `IBinaryInteger<T>` + `IUnsignedNumber<T>`)

| Method | Result |
|--------|--------|
| `ElegantPair` / `ElegantUnpair` | Szudzik pairing of two non-negatives ↔ one value. |
| `EnumeratePrimeFactors` | Lazy prime factors with multiplicity (mod-30 wheel). |
| `ModularInverse` | Multiplicative inverse of an odd value mod `2^width` (Newton–Hensel). |
| `SquareRoot` | Floor integer square root (hardware-seeded through 128-bit). |
| `NextPowerOfTwo` / `NextSquare` | Round up to the next power of two / perfect square. |

### `PrimeExtensions` (on `uint`)

Exact over the **entire** 32-bit range — never probabilistic. Vectorized trial division
by the odd primes through 59 (a scalar ladder through 37 on narrower hardware), then a
single base-2 strong-probable-prime round in Montgomery form, corrected by the complete
list of the 2,256 base-2 strong pseudoprimes that survive the ladder — fingerprint-
filtered, enumerated in-house, and the whole method verified by an exhaustive sweep of
every 32-bit value; the hot loop performs no hardware division.

```csharp
using Puck.Maths;

bool isPrime = 1_000_003u.IsPrime();              // true
uint p       = 100u.NthPrime();                   // 547   (0-based index)
uint pi      = 1_000_000u.PrimeCountingFunction();// 78498 (primes ≤ 1,000,000)

Span<uint> factors = stackalloc uint[32];
int count = 4_294_967_295u.Factorize(factors);    // 3, 5, 17, 257, 65537
```

`Factorize` fills a span with the prime factors, ascending and with multiplicity
(empty for primes, matching `EnumeratePrimeFactors`): factors through 59 strip by
reciprocal multiplication, and the remaining cofactor splits by deterministic Brent
cycle walks on the same Montgomery kernel — microseconds even for the hardest
semiprimes of two ~2¹⁶ primes.

`PrimeCountingFunction` uses a sublinear combinatorial method — bulk range updates are
vectorized and the summation-phase divisions reduce through precomputed reciprocals —
renting its working buffers from `ArrayPool<T>` (peak ≈ 768 KiB). `NthPrime` seeds with
Cipolla's asymptotic expansion, aligns the seed exactly via `PrimeCountingFunction`,
and walks off the residual with a windowed segmented sieve in whichever direction the
target lies.

### `MonotonicPartitioner`

A static router that maps 65536 values — or a `Guid` through its trailing entropy
(matching `ObjectBlobAddress.ObjectId` routing) — onto between 1 and 1024 buckets:
jump-consistent routing with the ownership chains precomputed at static init. Three
invariants hold over the whole domain, proven exhaustively by the POST battery's
`monotonic-partitioner` stage against an independent table-free reference walk:
**deterministic** (the same `(value, bucketCount)` pair yields the same bucket on every
machine — the routing map is a client/server agreement, so both ends of a wire compute
identical routes; a mapping change is a protocol break), **monotonic** (raising the bucket
count from N to N + 1 only moves values *into* bucket N, so scaling out migrates the
minimal set), and **uniform** (each bucket owns ⌊65536/N⌋ or ⌈65536/N⌉ values —
quantization skew stays ≤ ~2 % at every count).

Bucket counts ≤ 64 resolve from a checkpoint bitmask; larger counts decode a varint
tail stream or re-walk the jump chain from the checkpoint. `GetMetrics` returns
`MonotonicPartitionerMetrics` — the value's normalized rank, its migration count across
the whole range, and the bucket-count distance to its next migration (`Velocity` is the
reciprocal pressure form) — the shard-ops telemetry view.

```csharp
using Puck.Maths;

int home = MonotonicPartitioner.GetBucketId(value: playerId, bucketCount: 96);
// growing to 97 shards only moves values whose new owner IS shard 96

var metrics = MonotonicPartitioner.GetMetrics(value: playerId, bucketCount: 96);
// metrics.MigrationDistance == 1 → the next scale-out moves this player's data
```

`GetBucketIdDangerous` skips the bucket-count range check for hot routing loops.

### `LayerSequence`

A layered index space in three constants — `Seed` indices at the core (layer 0),
`Start` indices in layer 1, each later layer changing by `Step` — with every query
answered in **constant time** by inverting the quadratic prefix sum
`Count(n) = Seed + Start·n + Step·n·(n − 1)/2` instead of walking it. The inverse is
pure integer arithmetic (an `Int128` discriminant, the exact floor square root, a
floor division), so results are bit-identical on every platform and exact over the
whole `long` index range — verified by exhaustive sweeps against an incremental
reference.

| Sequence | `Start` | `Step` | `Seed` | Geometry |
|----------|---------|--------|--------|----------|
| `Triangular` | 1 | 1 | 0 | Cantor-style diagonal layers. |
| `Pronic` | 2 | 2 | 0 | `n·(n + 1)` rectangles; asymmetric sharding. |
| `Square` | 1 | 2 | 0 | Corner-expanding grid (the square numbers). |
| `CenteredSquare` | 4 | 4 | 1 | Taxicab rings around a center cell. |
| `CenteredHexagonal` | 6 | 6 | 1 | Honeycomb rings around a center cell. |
| `Centered(k)` | k | k | 1 | Centered k-gonal rings. |
| `Polygonal(k)` | 1 | k − 2 | 0 | Corner-expanding k-gonal numbers. |
| `Linear(size, seed)` | size | 0 | seed | Flat layers — ordinary linear indexing. |
| `Create(a, d, c)` | a | d | c | Anything the three constants can say. |

A **negative `Step`** bounds the space: layer sizes shrink to zero and the total
tops out at `Capacity`. `LayerOf`/`Locate` treat indices beyond capacity as errors;
`Project` is the saturating query — the layer locks at `MaxLayer` while two excess
channels report how far past the boundary the index lies (`Overflow` grows linearly,
`Depth` — the imaginary component of the layer equation's complex root — grows with
the square root of the excess once the index passes the continuous vertex). Overflow
routing and backpressure fall out as data instead of exceptions.

```csharp
using Puck.Maths;

var rings = LayerSequence.CenteredHexagonal;      // 1 core cell, rings of 6, 12, 18, …
var ring = rings.LayerOf(index: 100L);            // 6 — constant time, pure integer
var (layer, offset) = rings.Locate(index: 100L);  // (6, 9) — ring plus position within it

var arena = LayerSequence.Create(start: 6L, step: -2L, seed: 1L); // bounded: 13 indices, 3 shrinking layers
var probe = arena.Project(index: 20L);            // (Layer 3, Overflow 8, Depth 2) — saturates, never throws
```

### `SecureRandom`

Uniform, bias-free unsigned draws backed by `RandomNumberGenerator`. Bounded draws use
rejection sampling (not modulo), so the output is exactly uniform.

```csharp
using Puck.Maths;

uint full   = SecureRandom.NextUInt<uint>();                      // whole range
uint bounded = SecureRandom.NextUInt<uint>(maximum: 99, minimum: 1); // inclusive [1, 99]
```

### Binary fields

`BinaryField<T>` separates the two objects a packed polynomial can be. A `BinaryPolynomial`
is an element of `GF(2)[t]`: it has a degree, exact division with remainder, and no inverse.
A `BinaryField<T>` is the quotient by a fixed irreducible modulus, and its elements are bare
packed integers that only mean anything relative to that modulus — so the field is named at
the call site rather than smuggled into every value.

```csharp
using Puck.Maths;

var field = BinaryFields.Degree8;                               // GF(2^8), t^8+t^4+t^3+t+1
byte product = field.Multiply(left: 0x53, right: 0xCA);         // 0x01 — they are inverses
byte inverse = field.Inverse(value: 0x53);                      // 0xCA
byte power = field.Exponentiate(value: 0x03, exponent: 255UL);  // 1 — the group has order 255

// Any modulus, any degree through 128, not just the canonical ones.
var custom = BinaryField<ulong>.FromModulus(modulus: new BinaryPolynomial(bits: 0x1FUL));
bool usable = custom.IsIrreducible();

// The bulk primitive: destination ^= scalar * source, over a whole region.
field.MultiplyAccumulateRegion(destination: parity, source: shard, scalar: 0x1D);
```

Elements must be reduced and the modulus must be irreducible; `IsReduced` and `IsIrreducible`
test both, and neither is enforced on the hot path, exactly as `ModularInverse` states its own
precondition. `Inverse` and `Divide` throw on zero.

Unlike a rounded fixed-point product, a field product carries no rounding at all:
multiplication is exactly associative, exactly commutative, and exactly distributive over
addition, so reassociating or parallelizing a chain of field products is safe.

The hardware paths are performance only. Multiplication runs on the carryless-multiply
instruction when it is available and on a table-free masked-comb fallback otherwise; region
scaling runs on the hardware Galois-field affine transform for byte-wide and sixteen-bit
fields, a nibble-split shuffle when only a byte shuffle is available, and a scalar loop
otherwise. Reduction, inversion, division, and exponentiation are one implementation shared by
both paths, so there is nothing above the product that could differ. Every region path is
executed against the scalar path on the same inputs by the gate — including on machines that
support the fast paths, because the portable kernels are named separately and never gated — and
the gate additionally relaunches itself with each instruction set suppressed and requires an
identical result digest. "Bit-identical to the fallback" is a result, not a claim.

```text
dotnet run --project src/Puck.Post -c Release -- --stage binary-field
```

### Digital nets

`DigitalNetSampler` is the sampler you reach for when a stochastic estimator has to run inside
a deterministic simulation. It is Sobol's construction over `GF(2)`, built on the same
`BinaryPolynomial` the fields are: dimension 0 is bit reversal, dimension 1 is the recurrence
generated by the primitive polynomial `t + 1`, and a point is nothing but the XOR of the
direction vectors its index's set bits select.

Four properties follow, and each is the reason a piece of it is shaped the way it is.

- **Pure integer.** No float appears anywhere in the point. There is nothing for two
  compilers, two backends, or two machines to round differently.
- **Provably stratified.** Two dimensions of the construction form a `(0, 2)`-sequence: for
  every `m`, the first `2ᵐ` points place exactly one point in every dyadic box of area `2⁻ᵐ`,
  at every box shape. That is a theorem, and the gate checks it exhaustively rather than
  measuring discrepancy.
- **Stateless and seekable.** Sample `N` is a pure function of `N`. There is no generator
  state to persist, so — unlike `Pcg32XshRr` — nothing rides a snapshot, and an accumulation
  indexed by a tick counter is resumable and replay-exact by construction.
- **Randomizable without losing the theorem.** A digital shift (XOR by a fixed vector, from
  `DeriveScramble`) is an affine bijection of the coordinate's bits, so it carries dyadic
  boxes onto dyadic boxes of the same shape. That is what decorrelates one consumer from
  its neighbour.

```csharp
using Puck.Maths;

Span<uint> directionNumbers = stackalloc uint[DigitalNetSampler.PlaneDirectionNumberCount];

DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

// One key per lattice site; the scramble decorrelates neighbouring sites.
var key = DigitalNetSampler.DeriveKey(x: pixelX, y: pixelY, stream: viewIndex);
var scramble = DigitalNetSampler.DeriveScramble(key: key);

for (var draw = 0U; (draw < samplesPerSite); ++draw) {
    var index = DigitalNetSampler.ShuffleIndex(index: ((tick * samplesPerSite) + draw), salt: key);
    var point = DigitalNetSampler.SamplePlane(index: index, directionNumbers: directionNumbers, scramble: scramble);
    // point.X and point.Y are UQ0.32 raw bits.
}
```

`ShuffleIndex` is the one place a plausible shortcut is wrong. Giving each site a different
walk through the sequence needs a permutation of the *index*, and an arbitrary mixing bijection
scatters a site's first `2ᵐ` draws across the whole index space — the resulting point set is
not a net at all, and the stratification you paid for is gone. `NestedDyadicPermutation` is
used instead because it carries an aligned block of `2ᵐ` indices onto an aligned block of `2ᵐ`
indices, and every such block of a `(0, 2)`-sequence is itself a net. `UnitriangularBitMix`
sits beneath both: every step is either XOR with a shift of the word (a unit-diagonal matrix
over `GF(2)`, hence nonsingular) or multiplication by an odd constant (a unit modulo `2³²`),
so its invertibility is a theorem with a closed-form inverse rather than a property hoped for
of a tuned hash.

`SphericalCapSampleTable` is the GPU-facing form: one flat `uint[]` holding the direction
numbers, a quantized azimuth table, and a quantized polar table stored as `(axial, radial)`
pairs sharing one denominator, so `axial² + radial² = 1` holds by construction. A consumer
reads the leading 12 bits of each net coordinate, loads four floats, and combines them with
multiplies and adds — no `sqrt`, no reciprocal, no normalize, no trigonometry, because those
are exactly the operations a shading language is permitted to round differently (Vulkan allows
3 ULP on `Sqrt` and 2.5 ULP on division). Every table value is computed in `double` and rounded
exactly once. The azimuth table calls the platform's `Math.Cos`/`Math.Sin`, which makes the
table a per-machine build-time input, not a portable constant: the reproducibility claim it
supports is same-machine replay, and the geometric invariant above holds anywhere.

Nothing here allocates: every entry point writes into a caller-owned span, and the tables are
rebuilt only when their defining angle changes.

The gate proves rather than asserts. It shows the mix is a bijection over all `2³²` words in
both directions; that the nested permutation really does preserve aligned blocks; that
`IsPrimitive` is correct by re-deriving the classical census `φ(2ⁿ−1)/n` over every monic
polynomial through degree 14; that both dimensions' direction numbers match oracles sharing no
code with the recurrence (the anti-diagonal, and Pascal's triangle mod 2 via Lucas' theorem);
and that the net property survives exhaustively through order 14, under 256 digital shifts and
five index shuffles, and at the shipped 12-bit quantization.

```text
dotnet run --project src/Puck.Post -c Release -- --stage digital-net
```

---

## Notes for agents

- **Choose signedness deliberately.** Use `FixedQ4816` for signed simulation
  values and the `UFixed*` types for magnitudes and fractions. Helpers constrained
  to `IUnsignedNumber<T>` accept only non-negative numeric types.
- **Wrap is the default.** Bare operators follow unchecked integer overflow
  semantics. Reach for `AddSaturating` / `SubtractSaturating` when you need
  clamping.
- **No `1.0` in the Q0.x types.** Don't look for `One` / `MultiplicativeIdentity` / `++`
  on `UFixedQ0016` or `UFixedQ0032` — they don't exist by design.
- **Determinism.** Results do not depend on ambient culture, locale, or CPU feature
  availability (hardware paths are bit-identical to the fallbacks). Parameterless
  formatting/parsing is invariant; explicit providers are honored deterministically.
- **Generic-math friendly.** Pass these as `T` into code constrained on the
  `System.Numerics` operator interfaces; use `INumber<T>` when an algorithm genuinely
  needs the complete scalar contract. That constraint provides capabilities, not a
  proof that rounded multiplication is associative.
- See the [generated API reference](../../docs/api) for the full member-by-member docs.
