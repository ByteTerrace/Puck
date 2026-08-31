# Sampling

This folder holds the engine's deterministic randomness. (Each folder under
`Puck.Maths` is what the project calls a *wing*: a small self-contained library
covering one subject.) Every source of variation a simulation may draw from
lives here, and each one states the exact terms on which it reproduces.

"Deterministic randomness" sounds like a contradiction. It means the values look
unpredictable but are computed from a starting number — a **seed** — by a fixed
recipe, so the same seed always produces the same values in the same order.
That is what lets a recorded game be replayed and come out identical.

The wing holds a seeded sequential generator whose whole state is readable and
restorable; three stateless index-to-value maps — spatial noise, additive
low-discrepancy recurrences (low discrepancy means the points spread themselves
out evenly instead of clumping the way independent draws do), and digital nets —
each of which turns an index or a position straight into a value and remembers
nothing in between; the one index permutation (a rearrangement of the whole
range of indices, one index in and one index out) those nets may be re-indexed
by; the invertible mix, a bit-scrambling step you can run backwards exactly,
that their keys and coordinate shifts are derived through; and an immutable
weighted-choice table. Only one type here carries mutable state, `Pcg32XshRr`,
so only one thing has to ride a snapshot — the saved copy of simulation state
that a replay resumes from. Everything else is a pure function of its arguments
(same arguments in, same result out, no hidden memory), an immutable table built
once and read many times, or `SecureRandom`, whose values come from the platform
generator's own hidden state and never enter simulation state.

The default tier is **cross-machine bit-identical**: pure integer arithmetic, no
wall clock, and identical bits out of identical arguments on every machine.
Where fractions are needed, the wing uses fixed point, which stores a fraction
as an ordinary integer scaled by a fixed power of two — `FixedQ4816`, the type
most of these routines hand back, keeps 48 integer bits and 16 fraction bits,
and that split is what the name Q48.16 records — so the arithmetic underneath
stays integer arithmetic and cannot drift from one machine to the next. One
floating-point step does sit inside the default tier: the `double` weight
overload's quantization, which snaps each weight onto one of a finite set of
representable values. Its divide, scale, and round are correctly rounded under
IEEE-754 — the floating-point standard every mainstream processor implements,
which pins the result of those operations exactly — so they land identically
everywhere.

Three types are fenced off from that guarantee on purpose, and it is friendlier
to meet them here than to discover them later.

- **`SecureRandom` is non-reproducible by design.** It draws from the platform
  cryptographic generator. Its values are unbiased and suitable for
  security-sensitive work, and they are never simulation state: nothing it
  returns may enter a snapshot, a hash, or a replay.
- **`ConeDirectionTable` is same-machine replay only.** Its azimuth and polar
  entries are computed through the platform's transcendental library — the
  runtime's implementations of `sin`, `cos`, `log` and their relatives — which
  carries no correctly-rounded guarantee, so the baked table counts as a
  per-machine input. The narrower bound is a deliberate trade: the table is a
  build-time upload, its geometric envelope holds on any machine — it bounds the
  storage rounding, whatever doubles the platform's library produced — and the
  reproducibility claim a consumer may rest on it is same-machine replay rather
  than cross-machine bit identity.
- **`ProbabilityFunctions` is presentation and tooling tier.** It is a
  `double`-valued quantile evaluator — a quantile function answers "which value
  sits at this percentile?" — and it calls the transcendental library on its
  tail branch, so it sits outside the fixed-point simulation contract. It
  belongs to authoring, analysis, and display code rather than to tick-advancing
  state.

---

## At a glance

| Type | Kind | What it's for |
|------|------|---------------|
| `Pcg32XshRr` | `struct` | The seeded sequential generator: each draw hands back a value and moves the generator on to its next state. Reference-exact PCG32 XSH-RR, one sequence per stream, logarithmic seek, uniform / bounded / fraction / standard-normal draws, and an in-place shuffle. It is the only simulation state in the wing. |
| `WeightedSampler` / `AliasTable<TElement>` | `static` / `sealed class` | Weighted choice, where some outcomes are meant to come up more often than others. Exact-integer Walker/Vose construction from ordered entries; constant-time sampling at exactly two generator advances per draw. |
| `FieldNoise` | `static` | Spatial randomness. A stateless pure-integer map from a seed and a world position to smooth value noise in `[−1, 1]`, with an exact analytic gradient (the direction and rate the noise is changing, solved in closed form rather than estimated by sampling twice) and a planet-scale hierarchical overload. |
| `Pcg3dLatticeNoise` | `static` | PCG3D hash-lattice value noise over a cell index, in `[0, 1)`. The shared kernel behind a world's field-lattice Noise/Scatter fills and a placement distribution's own Noise/Scatter regions, so both agree bit for bit on what a seed means. |
| `LowDiscrepancy` | `static` | Even coverage by additive recurrence — keep adding a fixed step and read off the fractional part. Golden-ratio (`R1`) and plastic-number (`R2`) index-to-point maps: one multiply per component, no state. |
| `DigitalNetSampler` | `static` | Even coverage by theorem. Digital `(0, m, 2)`-nets over the two-element field — the number system whose only values are 0 and 1, where addition is exclusive-or — in which a point is the exclusive-or of the direction vectors its index's set bits select. It offers only those randomizations that provably preserve stratification (the guarantee that every equal-sized box receives its exact share of the points): a digital shift of the coordinate, and a net-safe re-indexing. |
| `StratifiedShuffle` | `static` | The index permutation a net may be re-indexed by: it carries every aligned dyadic block — a run of indices whose length is a power of two, beginning at a multiple of that length — onto an aligned dyadic block of the same size. |
| `InvertibleBitMix` | `static` | The key mix beneath the types above, bijective (one input to one output, nothing lost, so it can be undone) by theorem rather than by tuning, with both directions in closed form. It derives keys and shifts; it is never used as a net's re-indexing. |
| `ConeDirectionTable` | `static` | The baked net-point-to-cap-direction table: one flat `uint` buffer that removes every square root, reciprocal, normalization, and trigonometric call from the point of use. |
| `SecureRandom` | `static` | Cryptographically secure, exactly uniform unsigned draws at any binary integer width. Deliberately non-reproducible. |
| `ProbabilityFunctions` | `static` ext. methods | The normal-distribution quantile function in `double`, by minimax rational approximation over three regions. |

## Choosing a primitive

There is one primitive per shape of randomness, and the shapes do not overlap,
so the question is usually which shape you have.

- **Sequential randomness with history** — combat rolls, wander decisions,
  anything drawn over time: `Pcg32XshRr`. Its state is simulation state.
- **Weighted choice** — loot, spawn kinds, production rules: build an
  `AliasTable<TElement>` once at load through `WeightedSampler.Create`, then
  sample it with a `Pcg32XshRr`.
- **Spatial randomness** — terrain, wind, per-cell decisions: `FieldNoise`, a
  pure function of `(seed, position)`. There is nothing to persist.
- **Even coverage** — spawn scatter, placement: `LowDiscrepancy.R1`/`R2`. They
  are stateless and cost one multiply per component.
- **Provable stratification** — Monte Carlo integration (estimating an area, an
  average, or how much light arrives by averaging many samples), area-light
  sampling, anything averaged over many draws: `DigitalNetSampler`. It has the
  same shape as `LowDiscrepancy` — index in, point out, no state — and a
  strictly stronger guarantee. The additive recurrences equidistribute
  asymptotically, meaning they even out only in the limit, and they stratify
  **nothing** exactly: there is no `m` for which you can name the boxes and say
  each holds one point. A `(0, 2)`-sequence does exactly that for every `m` and
  every box shape. Reach for `R2` when "spread out" is enough, and for the net
  when the error of what you are estimating depends on real stratification.

---

## `Pcg32XshRr`

A 64-bit linear-congruential state — each step multiplies the state and adds a
per-stream odd increment, keeping the low 64 bits — permuted to 32 output bits
by an xorshift (the word combined with a shifted copy of itself) and a rotation
whose amount is read from the state's top five bits. A *stream* is one such
sequence, picked out by an id, so separate systems can draw from one seed
without stepping on each other. The output permutes the *pre-advance* state, and
`Create` uses the reference seeding recipe — advance, add the seed, advance
again — so draws match the published PCG32 reference implementation bit for bit.

**Determinism tier.** Cross-machine bit-identical, and that includes the
standard normals.

**Simulation state.** `State`, `Increment`, and `Multiplier` are the entire
generator; `FromRawBits` reconstructs an instance that continues the captured
sequence exactly. All three ride snapshots and replays. A default-constructed
instance is degenerate, so instances come from `Create` or `FromRawBits`. Both
reject a multiplier that is not congruent to 1 (mod 4) — that is, one that does
not leave a remainder of 1 when divided by 4 — because a multiplier outside that
class costs the state its full period, the number of steps it takes before the
sequence returns to its start and repeats. The other rejection splits by entry
point: `FromRawBits` rejects an even increment, while `Create` cannot be handed
one at all, since it derives the increment as `(stream << 1) | 1`, and instead
rejects a stream id above the public `MaxStream` (`2⁶³ − 1`).

**Allocation.** None, anywhere; `Shuffle` permutes in place.

| Operation | Semantics |
|---|---|
| `Create(state, stream)` | Reference seeding at `DefaultMultiplier`; stream ids map onto the odd increments by `(stream << 1) \| 1`. |
| `Create(multiplier, state, stream)` | The same, with an explicit multiplier congruent to 1 (mod 4). |
| `FromRawBits(increment, multiplier, state)` | Exact restore from a snapshot. |
| `Advance(count)` | Skips `count` whole-state advances in logarithmic time by composing the affine step — the step's multiply-and-add — with itself; passing `2⁶⁴ − n` steps backward by `n`. |
| `NextUInt32()` | Thirty-two uniform bits; exactly one advance. |
| `NextUInt32(minimum, maximum)` | Unbiased draw on the inclusive range, bounds accepted in either order. It is a nearly-divisionless bounded draw: the high half of `draw · bound`, rejecting the small biased window of width `2³² mod bound`. The full-width range short-circuits to one plain draw. |
| `NextUnitFraction16()` / `NextUnitFraction32()` | The draw's top sixteen bits, or the whole draw, as the corresponding unit-fraction domain type — a value in `[0, 1)` carried as an integer. One advance each. |
| `NextGaussianPair()` | Two independent standard normals — draws from the bell curve with mean 0 and standard deviation 1 — as `FixedQ4816`; exactly two advances. |
| `NextGaussian()` | The pair's first component; still exactly two advances, discarding the second. |
| `Shuffle(values)` | Fisher–Yates (Durstenfeld): walk the span from the high end down, swapping each element with one chosen at random at or below it. A span of length `n` makes `n − 1` bounded draws. |

**Exactness of the normals.** The generator uses Box–Muller, the classical
recipe that turns two uniform draws into two normally distributed ones, over the
fixed-point primitives — a table-driven `log2`, an integer square root, and the
polynomial sine/cosine core — with no floating point at any stage. The radius
draw is read as `u₁ = (draw + 1)/2³² ∈ (0, 1]`, so `u₁` is bounded below by
`2⁻³²` and `s = −2·ln u₁ ≤ 2·32·ln 2 ≈ 44.4`. The magnitude of a returned normal
is therefore capped at `√44.4 ≈ 6.66` standard deviations, a truncation whose
probability is about `10⁻¹¹`. The angle is the second draw read as turns — one
turn being a full revolution — at the full `2⁻³²` resolution.

**Stream correlation.** This is the one to watch. A stream id selects a distinct
odd increment; it does not promise a statistically independent sequence.
Increments `2⁶³` apart collapse under the linear-congruential step, and the
reference id-to-increment mapping reaches that collapse at half the id distance,
so two generators whose stream ids differ by exactly `2⁶²` agree on half their
draws. The working rule is one stream per system, derived from a master seed
with small consecutive ids. Sharing a single generator across systems is the
other way to get into trouble, because it couples them through draw order.

**Advance counting.** `Advance` counts whole-state advances, never calls. A
bounded draw that rejected consumed more than one advance — deterministically,
since the same state always rejects the same way — so seek arithmetic that
counts calls will drift. The fixed-cost draws are the ones a seek can rely on:
`NextUInt32` and both fraction draws at one advance each, and the Gaussian pair
at exactly two, `NextGaussian` included even though it discards the second
value.

---

## `WeightedSampler` and `AliasTable<TElement>`

These two implement the Walker/Vose alias method, which turns a weighted choice
into a set of equal-probability columns, each holding at most two outcomes, so a
draw costs a column pick and one comparison. `WeightedSampler` is the factory
surface and `AliasTable<TElement>` is the immutable result. Construction is
`O(n)` — linear in the number of entries — in exact integer arithmetic, with
weights scaled into `UInt128` and partitioned against a column budget of the
total, and sampling is `O(1)`: one masked column draw plus one threshold
compare.

**Determinism tier.** Cross-machine bit-identical. Identical entry spans produce
identical tables on every machine, including through the `double` overload's
quantization.

**Advance cost.** Exactly two generator advances per draw, with no rejection:
the column count is rounded up to a power of two, so the column index is a mask
of one draw rather than a bounded draw.

**Exactness and rounding.** Thresholds are UQ0.32 column fractions — unsigned,
no integer bits, 32 fraction bits, so a value in `[0, 1)` — rounded to nearest,
which places each entry's sampled probability within `2⁻³³` per column of
`weight / total`. The `ulong`, `FixedQ4816`, and `UFixedQ4816` overloads are
exact: the fixed-point overloads pass raw bits straight through — a fixed-point
value's raw bits are the plain integer it is stored in, sometimes called its
carrier — and the signed overload rejects a negative weight. The `double`
overload is where quantization enters, and it is documented below. Zero-weight
entries, and the power-of-two padding columns, are never sampled.

**Allocation.** Construction allocates: the element array, the column array, a
`UInt128` scratch array of column count, and the two partition stacks; every
non-`ulong` overload allocates one converted entry array as well. Sampling
allocates nothing.

Invalid input never pays for that conversion array. Every overload settles its
entry-count refusal first — and the signed overload its negative-weight refusal
too — before asking for a buffer the refusal would only throw away. The order
matters at the top of the range: a span of more than `2³⁰` entries would need a
conversion buffer of many gigabytes, so a count checked afterwards would reach
the caller as an `OutOfMemoryException` instead of the `ArgumentException` this
API promises.

| Operation | Semantics |
|---|---|
| `Create(entries)` with `ulong` weights | Exact relative weights; the core builder. |
| `Create(entries)` with `double` weights | Quantized against the largest weight; this is the crossing point where authored document weights arrive. |
| `Create(entries)` with `FixedQ4816` / `UFixedQ4816` weights | Exact, no quantization. |
| `AliasTable.Count` | The construction entry count, excluding padding. |
| `AliasTable.SampleIndex(ref generator)` | The sampled entry's index in the construction span, always below `Count`. |
| `AliasTable.Sample(ref generator)` | The element at that index. |

Construction rejects an empty span, more than `2³⁰` entries, an all-zero weight
set, a negative weight, and — for the `double` overload — a non-finite one.

One construction detail is load-bearing rather than incidental. Rounding a
column's threshold to nearest can reach exactly `2³²` even where the column's
scaled weight is strictly below the total. Such a column always selects itself,
so its otherwise-dead alias is set to itself and the packed threshold saturates
at `uint.MaxValue` — saturating means clamping to the largest representable
value instead of wrapping round to zero — which preserves every `uint` draw's
outcome instead of letting one draw fall through to an unrelated alias.

---

## `FieldNoise`

Value noise over the unit integer lattice, the grid of whole-numbered points in
space: the corner values come from an avalanche hash of the lattice coordinates
— a mixing function in which flipping one input bit changes about half the
output bits — and are blended by the quintic fade `6t⁵ − 15t⁴ + 10t³`, which
keeps the value and its first derivative continuous across cell boundaries. One
noise unit spans one world unit, so changing frequency is a matter of scaling
the position before sampling.

**Determinism tier.** Cross-machine bit-identical, pure integer throughout: the
fade is evaluated at Q28 — 28 fraction bits — and corner values are drawn from
the hash's top 32 bits into `[−65536, 65535]`.

**Simulation state.** None. Every entry point is a pure function of its
arguments, so nothing here enters a snapshot.

**Allocation.** None. Where a sampler stages its eight corners, it does so in a
`stackalloc`, which reserves the scratch space on the call stack rather than on
the heap.

| Operation | Semantics |
|---|---|
| `Hash(seed, x, y, z)` | The raw lattice hash as 64 well-mixed bits, for per-cell decisions that need no smoothing. |
| `Sample(seed, position)` | Smooth noise in `[−1, 1]` at a `FixedVector3` position. |
| `Sample(seed, position, octaves)` | Fractal noise: `octaves` layers in `[1, 16]`, each doubling frequency and halving amplitude — one octave up is one doubling, as in music — and the sum stays within `[−1, 1]`. |
| `SampleGradient(seed, position, out gradient)` | The same value as `Sample`, plus the exact analytic gradient per world unit; each component lies in `[−3.75, 3.75]`. |
| `Sample(seed, position)` at `FixedPosition` | The hierarchical overload, exact at planet scale. |

Three invariants are established by the implementation rather than assumed.
First, the gradient shares all eight corner hashes — the sampler's real cost —
with the value and with all three partials, the rates of change along `x`, `y`,
and `z`, so it is far cheaper than differencing `Sample` and carries no
step-size choice; it is continuous across cell boundaries because the quintic
fade's derivative vanishes at both ends. Second, octave frequency doubling is
exact on a floor-split coordinate, and the cell term stays inside `long` for
every layer under the sixteen-octave limit, which checked arithmetic enforces
rather than a comment asking you to trust it. Third, the hierarchical overload
forms a signed 128-bit lattice coordinate from the cell index and the local
whole-unit offset, discarding no cell bits, and removes the low word's sign
extension — the repeated sign bit a negative number picks up when it is widened,
under the two's-complement representation integers use — before mixing, so that
the wide and native hash trees are bit-identical throughout their shared range.
A position that fits `long` therefore samples the same in both overloads.

Seed handling is domain-separated at every coordinate stage, meaning each stage
gets its own derived seed rather than a shared one: independent seed states are
injected at `x`, `y`, and `z`, so no single shift of the seed state translates
the whole field, and each octave derives its own lattice.

---

## `Pcg3dLatticeNoise`

Value noise over a 2D cell index, built on the Jarzynski & Olano PCG3D integer
mix — the same mix `Puck.ShaderVm.ShaderIsa.Pcg3d` and the renderer's
`sdfPcg3d` HLSL kernel carry, hand-kept in sync across those language
boundaries. A corner's value is the hash's top 16 bits read directly as a
`FixedQ4816` fraction; corners blend by the same quintic fade `FieldNoise`
uses. Unlike `FieldNoise`, the domain is a discrete cell index rather than a
continuous position, and the hash tree is PCG3D rather than an avalanche mix —
the two types are not interchangeable, and neither is a special case of the
other.

**Determinism tier.** Cross-machine bit-identical, pure integer and
fixed-point throughout.

**Simulation state.** None. Every entry point is a pure function of its
arguments.

**Allocation.** None.

| Operation | Semantics |
|---|---|
| `Pcg3d(x, y, z)` | The raw three-lane mix. |
| `ValueNoise01(cellX, cellZ, noiseCells, seed)` | One octave of quintic-smoothed value noise over the cell index, in `[0, 1)`. A caller sums octaves itself, as `WorldFieldLattice.ApplyNoiseFill` and `CreationStampSampling.ResolveNoise` both do. |

Both `Puck.World.Server.WorldFieldLattice` (a world's live `fields` section)
and `Puck.World.Authoring.CreationStampSampling` (a placement's Noise/Scatter
distribution regions) route their cell fills through this type instead of
each keeping its own copy, so the two agree on what "the same seed" means by
construction rather than by two hand-kept copies staying in sync.

---

## `LowDiscrepancy`

These are additive recurrences: an index multiplied by a fixed 64-bit increment,
where the natural wrap of the multiply — the high bits fall off the end and only
the low 64 survive — performs the mod-1 exactly, and the top bits are read off
as the point. One multiply per component, no state, no rounding.

**Determinism tier.** Cross-machine bit-identical.

| Operation | Semantics |
|---|---|
| `R1(index)` | `frac(index / φ)`, the fractional part of the index divided by the golden ratio, as a `UnitFraction32`. The increment is `⌊2⁶⁴/φ⌋`, which is odd, so the phase recurrence has its full `2⁶⁴` period. |
| `R2(index)` | The plastic-number pair `(frac(index / ρ), frac(index / ρ²))`, ρ being the plastic number, each a `UnitFraction32`, together covering the unit square. |

Offsetting the index shifts the whole point set deterministically. These
sequences equidistribute but stratify nothing exactly, which is the line between
them and `DigitalNetSampler`, and the reason both exist.

---

## `DigitalNetSampler`

Digital `(0, m, 2)`-nets over the two-element field, in Sobol's construction.
Dimension zero is the radical-inverse (van der Corput) matrix — the
anti-diagonal, so the coordinate is simply the index with its bits reversed —
and each further dimension is generated by a linear recurrence whose
characteristic polynomial (the polynomial that describes how the recurrence
steps) is primitive — primitive meaning the recurrence visits every nonzero
state before it repeats, the longest period it could have. Two such dimensions
form a `(0, 2)`-sequence, and here the stratification is a theorem rather than
an observation: for every `m`, the first `2ᵐ` points place exactly one point in
every dyadic box of area `2⁻ᵐ`.

A point is the exclusive-or of the direction vectors selected by its index's set
bits, so it is a pure function of that index: stateless, seekable, and pure
integer, with no accumulator to snapshot and no rounding to disagree about.

**Determinism tier.** Cross-machine bit-identical. A shader that reproduces
these routines is a hand-transcribed mirror rather than a compilation of this
source, so its agreement with them is a property of that file, not of this one.

**Allocation.** None. Every builder writes a caller-owned span, and the
recurrence stages its numerators in a `stackalloc`.

| Operation | Semantics |
|---|---|
| `DirectionNumberCount` / `PlaneDirectionNumberCount` | 32 words per dimension; 64 for the shipped plane, dimension zero first. |
| `PlaneGenerator` | The primitive polynomial `t + 1` — the smallest generator there is, and exactly what a `(0, 2)`-sequence needs; its direction matrix is Pascal's triangle modulo two. |
| `BuildBitReversalDirectionNumbers(destination)` | Dimension zero. |
| `BuildDirectionNumbers(generator, initialNumbers, destination)` | One dimension from a primitive generator and one leading numerator per degree; numerator `k` must be odd and below `2^(k+1)`, and the direction number is that numerator left-aligned in the coordinate. |
| `BuildPlaneDirectionNumbers(destination)` | The shipped `(0, 2)`-sequence: bit reversal against the `PlaneGenerator` recurrence. This is the pair a GPU sampler table carries verbatim. |
| `DeriveKey(x, y, stream)` | A per-lattice-site key: sixteen-bit coordinates packed, separated by an odd stream stride, and mixed. |
| `DeriveScramble(key)` | The two coordinates' digital shifts. |
| `Sample(index, directionNumbers, scramble)` | One coordinate at an index, as a UQ0.32 fraction's raw bits; a zero scramble gives the unshifted net. |
| `SamplePlane(index, directionNumbers, scramble)` | Both coordinates at an index. |
| `ShuffleIndex(index, salt)` | The net index a consumer's own running index maps to. |

Two randomizations are offered, and each provably preserves the net.

- **A digital shift** — exclusive-or by a fixed vector — is an affine bijection
  of the coordinate's bits, which over the two-element field means a pure
  translation, one input to one output with nothing lost. It therefore carries
  dyadic boxes onto dyadic boxes of the same shape and the one-point-per-box
  count survives. `DeriveScramble` derives the second coordinate's shift by
  mixing the key against a fixed separator rather than reusing the key, so the
  two coordinates are not shifted by correlated vectors.
- **Re-indexing through `StratifiedShuffle`** carries an aligned block of `2ᵐ`
  indices onto an aligned block of `2ᵐ` indices, and every such block of a
  `(0, 2)`-sequence is itself a `(0, m, 2)`-net, so a consumer's own first `2ᵐ`
  draws are stratified whatever its salt — the extra value that makes one
  consumer's shuffle differ from another's.

An arbitrary mixing bijection does neither of those things, so none is offered.

`DeriveKey` is injective for sites within sixteen bits — injective meaning no
two inputs share an output — since the packing is injective and the mix is a
bijection, so distinct sites in one stream never share a key. A coordinate wider
than sixteen bits would overlap the packing and alias two sites onto one key, so
it is rejected rather than truncated.

The call order a consumer follows is the coupling chain read forwards: build the
direction numbers once, derive one key per lattice site, derive that site's
scramble from the key, and use the key again as the salt that re-indexes the
site's own draws.

```csharp
using Puck.Maths;

Span<uint> directionNumbers = stackalloc uint[DigitalNetSampler.PlaneDirectionNumberCount];

DigitalNetSampler.BuildPlaneDirectionNumbers(destination: directionNumbers);

// One key per lattice site; the scramble keeps neighbouring sites from
// correlating with one another.
var key = DigitalNetSampler.DeriveKey(x: pixelX, y: pixelY, stream: viewIndex);
var scramble = DigitalNetSampler.DeriveScramble(key: key);

for (var draw = 0U; (draw < samplesPerSite); ++draw) {
    var index = DigitalNetSampler.ShuffleIndex(index: ((tick * samplesPerSite) + draw), salt: key);
    var point = DigitalNetSampler.SamplePlane(index: index, directionNumbers: directionNumbers, scramble: scramble);
    // point.X and point.Y are UQ0.32 raw bits: all fraction, no integer part.
}
```

---

## `StratifiedShuffle`

A seeded permutation of the 32-bit indices that carries every aligned dyadic
block onto an aligned dyadic block of the same size. That is precisely the
permutation a digital net may be re-indexed by without losing its
stratification.

**Determinism tier.** Cross-machine bit-identical. `Permute` and `Unpermute` are
exact inverses at every index and seed.

**Allocation.** None.

The construction is the base-two nested uniform scramble. Between two bit
reversals sits a map whose every output bit depends only on the input bits at or
below it — lower-unitriangular over the two-element field, once translation is
accounted for, which is the matrix shape with ones on the diagonal and entries
only below it. Three steps have that shape and each has a closed-form inverse,
one you can write down directly instead of searching for: adding a constant,
exclusive-or with a *left* shift of the word itself, and multiplication by an
odd constant. The composition is therefore a bijection whose low `k` bits are a
function of the input's low `k` bits alone.

The bit reversals turn that statement into the one a sampler needs. Reversal
carries the block `[0, 2ᵐ)` onto the words whose low `32 − m` bits vanish; those
bits determine the corresponding output bits, so the whole block leaves with one
common low prefix; reversing back turns that common prefix into a common high
prefix, which is exactly an aligned block `[j·2ᵐ, (j+1)·2ᵐ)`.

The inverse undoes each shift-exclusive-or step by the finite geometric sum of
its own shift, which terminates as soon as the repeated shift leaves the word:
the third shift is above sixteen and so is its own inverse, the thirteen-bit
shift needs its doubled term, and the seven-bit shift needs its doubled,
tripled, and quadrupled terms.

---

## `InvertibleBitMix`

An invertible mixing map on 32-bit words, built only from operations whose
bijectivity is a theorem: exclusive-or with a *right* shift of the word itself,
and multiplication by an odd constant. Both directions are exposed in closed
form, which makes the map a named permutation rather than a hash whose
invertibility is merely hoped for.

**Determinism tier.** Cross-machine bit-identical. `Mix` and `Unmix` are exact
inverses at every word.

**Allocation.** None.

Each `x ^= x >> k` step is multiplication by a unit-diagonal — unitriangular —
matrix over the two-element field. Its determinant, the number that says whether
a matrix can be undone, is one, so the matrix is nonsingular; and because the
nilpotent shift satisfies `S³² = 0` — shift a 32-bit word 32 times and nothing
is left — the inverse is the finite sum `I + Sᵏ + S²ᵏ + …`. Each `x *= c` step
with odd `c` is multiplication by a unit of the ring of integers modulo `2³²`, a
value that has a multiplicative partner in that arithmetic, and its inverse is
that constant's modular inverse: the odd constant you multiply by to get back to
where you started. A composition of bijections is a bijection, and that is the
whole argument.

The map is three shift-exclusive-or steps around two multiplies: shift sixteen,
multiply, shift fifteen, multiply, shift sixteen again. The particular constants
are the `lowbias32` avalanche pair. Avalanche quality is a tuning property and
carries no proof; bijectivity is the property this type exists to name, and it
holds for any odd multiplier. All six constants are public — both multipliers,
both modular inverses, and both shift amounts — so that a gate can re-derive
each inverse from its multiplier rather than take the pair on trust.

---

## `ConeDirectionTable`

This bakes the whole cost of turning a two-dimensional net point into a
direction inside a spherical cap — the patch of a sphere you get by slicing it
with a plane, which is the shape a cone of directions traces — into one flat
table of 32-bit words: the net's direction numbers, then a quantized azimuth
table (the angle around the cap's axis), then a quantized polar table (the angle
away from it). A consumer indexes both tables with the high `TableIndexBitCount`
bits of a net coordinate and combines four looked-up scalars with multiplies and
adds, so there is no square root, no reciprocal square root, no normalization,
and no trigonometry at the point of use.

**Determinism tier.** Same-machine replay only. It is the one such exception
inside the simulation value path, with `ProbabilityFunctions` sitting outside
that path altogether. The azimuth table calls the cosine and sine of the
platform library, and the tangent of the half-angle enters both polar entries:
the radial one is scaled by it, and the axial one carries it through the shared
denominator. Of the calls the builder makes, only the square roots are correctly
rounded under IEEE-754. Every value is computed once in `double` and rounded
exactly once into a `float` bit pattern, so the surface a consumer sees is a
fixed list of constants — but *which* constants is a per-machine fact. The table
is a build-time upload and the stored-norm envelope below holds everywhere, and
that is what bounds the exposure.

**Allocation.** None. The caller owns the buffer, and the table is rebuilt only
when the half-angle changes.

| Constant | Value | Meaning |
|---|---|---|
| `DirectionNumberOffset` | 0 | The net's 64 direction numbers, written by `DigitalNetSampler.BuildPlaneDirectionNumbers`. |
| `AzimuthOffset` | 64 | `AzimuthEntryCount` cosine/sine pairs. |
| `RadiusOffset` | 8256 | `RadiusEntryCount` axial/radial pairs. |
| `TableIndexBitCount` | 12 | The high bits of a net coordinate a table index consumes; both entry counts are `2¹²`. |
| `WordCount` | 16448 | The whole table. |

`Build(capHalfAngleRadians, destination)` takes a half-angle in `[0, π/2)`,
where zero degenerates to the cap's axis. It rejects a negative, NaN, or
at-or-above-`π/2` angle, and a destination of the wrong length.

Two properties come from the construction rather than from a corrective step
afterwards. The polar table is stored pre-divided as an `(axial, radial)` pair
sharing one denominator, so the pair is unit length before anything is stored
and no normalization pass is needed at the point of use: writing the cap's
half-angle as `a` and `k = tan(a)`, the direction at polar parameter `r` is the
unit vector along `axis + k·r·(radial direction)`. That is cosine-free area
sampling of the cap's projected disc, which is what an area light — a light with
real size rather than a single point — wants, and the square root that produces
`r` is the area-preserving map from a uniform parameter onto the disc's radius.
Both tables are sampled at cell centres, `(i + ½) / count`, so no entry sits on
a cell boundary and no two parameter cells coincide.

**What survives storage, exactly.** The shared denominator makes the pair unit
length in `double`. It does not make the *stored* pair unit length, and the
contract states the surviving property rather than the discarded ideal. Each
component is rounded once and independently into a `float`, so what a consumer
reads back satisfies

```text
|axial² + radial² − 1| ≤ 2⁻²³ + 2⁻⁴⁰
```

— two roundings, one per component, each at a relative error of at most `2⁻²⁴`,
which is the unit roundoff of binary32; the second term absorbs their product
and the `double` construction's own rounding, with room to spare. Exact unit
length is not generally representable by two independently stored binary32
values at all. Buying it back — reconstructing one component from the other, or
normalizing at consumption — would reinstate the very square root and division
this type exists to delete, so the envelope is the contract and the
representation stands.

**Uniqueness, and the one degeneration.** The azimuth pairs are pairwise
distinct, and so are the polar `(axial, radial)` pairs at every half-angle a cap
of real size is built for. That is a property of the half-angle rather than a
promise made for all of them, and the difference is worth stating plainly: as
the half-angle shrinks every polar entry approaches the cap's axis, and below
the resolution of a `float` the entries coincide there — continuously, exactly
as the geometry does. A half-angle of exactly zero is the limit, and it is
contract rather than accident: a cap of zero angle *is* one direction, so all
4096 polar entries are the identical axis pair `(1, 0)`. A negative-zero
half-angle is admitted on the same terms and is not canonicalized — it writes
the radial components as `-0.0` rather than `+0.0`, a difference no consumer can
observe, because a zero radial scales the whole azimuth contribution away either
way.

The table exists because square root, division, normalization, and trigonometry
are exactly the operations a shading language does not round identically
everywhere. Its specification permits three units in the last place on the
square root and two and a half on division — a unit in the last place is the gap
between one representable float and the next, so that is a licensed error of a
few such gaps — and a sampler built from those operations at the point of use
has no enumerable float surface at all. Moving them into a baked table trades
cross-machine bit identity for a *finite, inspectable* one.

---

## `SecureRandom`

Uniformly distributed, cryptographically secure unsigned integers at any binary
integer width, generic over `IBinaryInteger<T>` and `IUnsignedNumber<T>`. All
randomness comes from the platform cryptographic generator, filling the result
value's own bytes in place.

**Determinism tier.** Deliberately non-deterministic. This type is the wing's
explicit non-simulation path: its draws are not reproducible, they must not
enter snapshot or replay state, and they must not be hashed into a determinism
probe. The simulation counterpart is `Pcg32XshRr`.

**Exactness.** Bounded draws use rejection rather than a plain modulo reduction.
Candidates are drawn until one falls within the largest multiple of the bound
that the width of `T` can represent, so every value in the interval is exactly
equally likely and the output is free of modulo bias — the slight favouring of
low values you get when you take a remainder and the range does not divide the
draw space evenly.

**Allocation.** None; the fill writes through a span over the result itself.

| Operation | Semantics |
|---|---|
| `NextUInt<T>()` | Every bit pattern of `T` equally likely. |
| `NextUInt<T>(maximum, minimum)` | Uniform on the inclusive interval; an interval spanning the full range short-circuits to one unbounded draw. |

An inverted interval is rejected rather than honoured, which is worth knowing
before you meet it: an unsigned `maximum − minimum` would wrap around to an
enormous span and silently draw a value from a range nobody asked for.

---

## `ProbabilityFunctions`

The inverse cumulative distribution function — the quantile function — of the
normal distribution, as `double` extension methods. A cumulative distribution
function answers "what fraction of the draws land at or below this value?", and
the inverse runs that question backwards.

**Determinism tier.** Presentation and tooling. The value is a `double`, the
tail branch calls the platform logarithm, and the coefficient evaluation depends
on fused multiply-add, which computes `a·b + c` with a single rounding at the
end. None of that belongs in tick-advancing state. The simulation-tier normal
draw is `Pcg32XshRr.NextGaussianPair`, which is fixed-point and bit-identical
everywhere.

**Allocation.** None.

| Operation | Semantics |
|---|---|
| `InverseStandardNormalCdf(probability)` | The standard normal deviate `z` with `Φ(z) = probability`, `Φ` being the standard normal's cumulative distribution function. |
| `InverseNormalCdf(probability, mean, standardDeviation)` | `mean + standardDeviation · z`; the mean must be finite and the deviation finite and strictly positive. |

The evaluation is a minimax rational approximation — a ratio of two polynomials
whose coefficients were fitted to keep the worst-case error as small as possible
— over three regions of `q = probability − 0.5`: a central region at
`|q| ≤ 0.425`, and two tail regions split at `r ≤ 5` where
`r = √(−log(min(p, 1 − p)))`, each with its own fitted numerator and denominator
coefficients evaluated by Horner's method, which nests a polynomial into
repeated multiply-and-add steps, with fused multiply-adds. Exact probabilities
of zero and one return negative and positive infinity. A NaN probability is
rejected in the same branch as an out-of-range one, since both ordered
comparisons are false for NaN; that keeps the valid path at a single branch, two
ordered comparisons and no separate NaN test. The coefficient-heavy core is
marked non-inlining so it does not bloat callers, while the affine wrapper is
marked aggressive-inlining because it is only two operations over that call.

---

## Cross-type couplings

These are the crossing points between one type and another, and they are what
make the wing a single library rather than ten unrelated ones. Each is a real
dependency in the sources rather than a resemblance.

- **`StratifiedShuffle` consumes `InvertibleBitMix`'s multipliers and their
  modular inverses** rather than declaring its own. One set of constants means
  one thing to be right about, and the gate that re-derives an inverse from its
  multiplier covers both types at once. What `StratifiedShuffle` does *not*
  share is the shift direction: `InvertibleBitMix` mixes with right shifts and
  `StratifiedShuffle` with left shifts, because only the left-shift form makes
  an output's low bits a function of the input's low bits, which is the property
  the dyadic-block argument rests on.
- **The digital-net → shuffle → cone-table chain.**
  `DigitalNetSampler.ShuffleIndex` is `StratifiedShuffle.Permute`;
  `DigitalNetSampler.DeriveKey` and `DeriveScramble` are
  `InvertibleBitMix.Mix`; and `ConeDirectionTable.Build` opens its buffer by
  calling `DigitalNetSampler.BuildPlaneDirectionNumbers`, reserving
  `DigitalNetSampler.PlaneDirectionNumberCount` words at offset zero. A change
  to the shipped plane's direction numbers changes every baked cone table.
- **`AliasTable` is the only type here that consumes the generator**, and it
  takes it `by ref` — `Sample(ref Pcg32XshRr)` and `SampleIndex(ref Pcg32XshRr)`
  advance the caller's own generator by exactly two steps rather than copying
  it. A by-value pass would silently discard the advance and repeat the draw.
- **`DigitalNetSampler` consumes `BinaryPolynomial`** for its generator
  representation and for the primitivity decision that gates
  `BuildDirectionNumbers`.
- **The unit-fraction domain types are the wing's fraction currency.**
  `Pcg32XshRr.NextUnitFraction16` returns `UnitFraction16`;
  `Pcg32XshRr.NextUnitFraction32` and both `LowDiscrepancy` entry points return
  `UnitFraction32`. `DigitalNetSampler` deliberately returns raw `uint` UQ0.32
  bits instead, because its coordinates are exclusive-or accumulators first and
  fractions second, and a consumer wraps them in a domain type at the boundary.
- **`FixedQ4816` is the wing's value currency.** The Gaussian pair, every
  `FieldNoise` sample, and the noise gradient's components are `FixedQ4816`;
  the gradient itself is a `FixedVector3`; `FieldNoise` samples a
  `FixedVector3` or a `FixedPosition`; and `WeightedSampler` accepts
  `FixedQ4816` and `UFixedQ4816` weights exactly. `Pcg32XshRr`'s normal draw
  additionally reaches into the fixed-point kernels — the `log2` fraction, the
  integer square root, and the sine/cosine core — so the normals are exact in
  the same sense the rest of the fixed-point tier is.

---

## Load-bearing invariants

Seven facts hold these sources up. Each one explains why a piece of the wing is
shaped the way it is, and breaking any of them produces a quiet wrong answer
rather than a loud failure.

**A direction-number generator must be primitive, and the builder checks.**
`BuildDirectionNumbers` throws on a non-primitive generator rather than trusting
the caller. Irreducibility is not enough — an irreducible polynomial is one that
cannot be factored into smaller polynomials, and a merely irreducible generator
gives the shift register — the loop of bits the recurrence steps through — a
short period, which silently destroys the net property. The points still look plausible, and the one-point-per-dyadic-box
theorem no longer holds. The check is affordable precisely because it runs once
at table-build time and never per sample.

Its refusals name the argument the caller actually passed. A degree outside
`[1, 32]` is an `ArgumentOutOfRangeException` against `generator` — the degree
is derived inside the builder, and a caller who never wrote it down cannot act
on a complaint about it — carrying the derived degree as the exception's actual
value. Then, in order: the destination length, the initial-numerator count,
primitivity, and each initial numerator, the span refusals naming `destination`
and `initialNumbers`.

**Index shuffling must carry aligned dyadic blocks onto aligned dyadic
blocks.** A general mixing bijection scatters a consumer's first `2ᵐ` indices
across the whole index space, and the resulting point set is not a net at all.
`StratifiedShuffle` is used instead because it maps an aligned block of `2ᵐ`
indices onto an aligned block of the same size, and every such block of a
`(0, 2)`-sequence is itself a `(0, m, 2)`-net, so every consumer's own prefix is
stratified whatever its salt. That is also why the wing carries two
bit-permutation types instead of one.

**Entry order is part of the alias table's identity.** The Vose partition pushes
column indices in construction order and pops them from two stacks, so which
small column is paired with which large one is a function of the entry
*sequence*, not of the weight multiset. Reordering the entries yields a
different table — equally correct in distribution, and different draw for draw
at a given generator state. Identical spans therefore produce identical tables
on every machine, which is what makes a distribution baked once at load
replayable.

**The `double` weight overload quantizes, and the rule has a consequence.**
Weights are divided by the largest weight and scaled so that the largest maps to
`2⁵³`, then rounded to nearest with ties to even: a tie is a value sitting
exactly halfway between two representable results, and the rule sends it to the
even one. Every quotient of doubles in `[0, 1]` scales and rounds exactly at
that magnitude, so ratios are preserved to within `2⁻⁵⁴` and identical weight
spans quantize identically on every machine. The consequence is stated here
rather than hidden: a positive weight at or below `2⁻⁵⁴` of the largest
quantizes to zero — the boundary case lands on the midpoint, which ties to even
— and is **never sampled**. A distribution whose dynamic range reaches `2⁵⁴`
needs the exact `ulong` or fixed-point overload.

**The Gaussian magnitude is capped at ≈ 6.66σ.** The radius draw is read as
`(draw + 1)/2³²`, which is bounded below by `2⁻³²`, so `−2·ln u₁` is bounded
above by `2·32·ln 2` and the returned magnitude cannot exceed `√44.4`. The
truncation probability is about `10⁻¹¹`. This is a property of the construction
rather than a clamp: no code path rejects an extreme draw, so the cost is fixed
and the advance count stays exactly two.

**A stream id chooses an increment, which is not the same as independence.** Two
`Pcg32XshRr` streams whose ids differ by exactly `2⁶²` agree on half their
draws, because the linear-congruential step collapses increments `2⁶³` apart and
the reference id-to-increment mapping reaches that at half the id distance.
Streams derived by hashing a seed into a wide id space walk into this; small
consecutive ids from a master seed do not.

**Seek arithmetic counts advances rather than calls.** `Advance` moves the state
by whole-state advances. A bounded draw may reject, consuming extra advances —
deterministically, since the same state always rejects identically — so a
shuffle of `n` elements consumes `n − 1` *calls* and an unpredictable number of
*advances*. Any code that seeks a generator to a known offset must be built on
the fixed-cost operations: one advance per raw draw or fraction draw, two per
Gaussian pair, and two per alias-table draw.

---

## Verifying changes

### The rules a consumer inherits

Four rules keep a consumer of this wing deterministic. They are collected here
because they are the wing's contract rather than a house style you may opt out
of; the ones that bear on a single type are argued at that type above.

- **One stream per system.** Derive each consumer's `Pcg32XshRr` from a master
  seed with small, consecutive stream ids (`Create(masterSeed, streamId)`). In
  an engine run that master seed comes from the `puck.world.def.v1` document, so the
  whole draw tree is a function of the run. Sharing one generator across systems
  couples them through draw order, and two systems that drew in a different
  order diverge.
- **Generator state rides snapshots.** Persist `State`, `Increment` and
  `Multiplier` and restore with `FromRawBits`; a replayed world must resume the
  exact sequence. `FieldNoise`, `LowDiscrepancy` and `DigitalNetSampler` carry
  no state and need nothing persisted.
- **Seek arithmetic counts advances rather than calls.** `NextUInt32`, both
  fraction draws, `NextGaussianPair` (two, `NextGaussian` included) and an
  alias-table sample (two) consume a fixed number of state advances, so
  `Advance`-based seeking is exact against them. A bounded
  `NextUInt32(minimum, maximum)` may consume extra advances on rejection, as
  does `Shuffle` — one bounded draw per element from the high end down — so a
  seek must not be built on those.
- **Alias tables are order-sensitive.** Entry order is part of the table's
  identity, so build from deterministically ordered data. Weights may be
  `ulong` (exact), `double` (quantized deterministically at `2⁵³` resolution,
  which is where authored document weights come in), or `FixedQ4816` /
  `UFixedQ4816` (exact).

### The gates

The net's properties are **proved** rather than measured, which is the reason to
reach for it in the first place. The proving belonged to the `digital-net`
battery stage, which left the build in the 2026-08-02 quarantine and has no
replacement — so the exhaustive layer described next **cannot be run today**.

It showed that `InvertibleBitMix` is a bijection over all `2³²` words in both
directions; that `StratifiedShuffle` really does carry aligned dyadic blocks
onto aligned dyadic blocks; that `BinaryPolynomial.IsPrimitive` — the decision
`BuildDirectionNumbers` gates on — is correct, by re-deriving the classical
census `φ(2ⁿ−1)/n`, where `φ` is Euler's totient, over every monic polynomial
(one whose leading coefficient is 1) through degree 14; that both shipped
dimensions' direction numbers match oracles sharing no code with the recurrence,
those oracles being the anti-diagonal and Pascal's triangle modulo two by Lucas'
theorem; and that the net property survives exhaustively through order 14, under
256 digital shifts and five index shuffles, and at the shipped 12-bit
quantization. `Pcg32XshRr` is pinned against the published PCG32 reference
vectors by `sampling.pcg-transcribed-reference-and-decorrelation`; that the
logarithmic advance and the snapshot restore continue the exact sequence was the
departed `fixed-point` stage's, and is now unproven. The gaussian, alias-table,
shuffle, field-noise and low-discrepancy law families carry the rest of the wing,
each at a reduced volume and a full one.
`SecureRandom`'s one contractual edge — that an inverted interval is refused
rather than wrapped into an enormous unrelated span — was checked by the
`binary-integer-functions` stage and is likewise unchecked now; it cannot be
gated by comparison at all, because the draws themselves are non-reproducible.

Everything above went with the batteries. What remains is the fast layer, in the
ordinary law suite, under the `sampling` family:

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
```

Ten cases run there in a couple of seconds, and between them every public
member of this wing except `InvertibleBitMix` is owned by one — the published
PCG32 reference vector and the snapshot, advance, bounded-draw, fraction and
shuffle contracts; the alias factories' shared refusals and the fixed-point
overloads as twins of the raw table; the net's radical inverse and Pascal
identities and the `(0, m, 2)` property through order ten; the noise field's
bounds, its integer-lattice tie to the public hash, and its gradient against an
exact-integer central difference; `Pcg3dLatticeNoise`'s mix against a
wide-integer PCG3D reference and its corner collapse at cell boundaries; the
cone table's layout, refusals and stored-norm envelope; the quantile ladder and
its antisymmetry; the additive recurrences against a wide-integer oracle; and
the secure draw's interval contracts. `InvertibleBitMix` was deliberately
waived to the `digital-net` stage
above, which round-tripped it over all `2³²` words — a statement no fast case can
make — so with that stage gone it is **the one public member of this wing that
nothing gates at all**. What the fast layer is *for* is that a small change to
this wing cannot pass a plain `dotnet test` while quietly moving a replay.

### What a change here means

A deliberate correction to any value path is expected to move state hashes and
recorded replays; those are re-recorded in the same change rather than
preserved. The coupling chain above splits what a change reaches: only the
shipped plane's direction numbers are baked into a `ConeDirectionTable`, so only
a change there moves the table's bytes, while a change to `InvertibleBitMix`'s
constants or to `StratifiedShuffle`'s shifts moves the keys, scrambles, and
indices its consumers derive at run time.
