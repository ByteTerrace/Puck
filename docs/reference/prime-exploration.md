# Prime exploration

`Puck.Maths` answers prime questions throughout the `ulong` domain with these
public types:

| Type | Members |
|---|---|
| `PrimeExtensions` | `IsPrime(uint)`, `IsPrime(ulong)`, `NthPrime(uint)`, `NthPrime(ulong, CancellationToken)`, `PrimeCountingFunction(uint)`, `PrimeCountingFunction(ulong, CancellationToken)`, and the 32-bit factorization helpers |
| `PrimeExploration` | `TryRandomPrime`, `Enumerate(low, high, onPrime, cancellationToken)` and `Count(low, high, cancellationToken)` |
| `PrimeConstellation` | Prime patterns such as twins and quadruplets, enumerated over an interval |
| `PrimeWheel30` | The coordinates and multiplication of the eight units modulo thirty |
| `PrimeKernels`, `PrimeDivisionKernels`, `PrimeLeafKernels`, `PrimeSieveKernels` | The rungs beneath those entry points, public so that a law can drive each one: the word-sized primality and rank machinery, the leaf-quotient and floor-reciprocal divisions, the hard-leaf survivor filters with the leaf sieve and its prefix cursor, and the presieve, packet and wheel tables. See [the kernel classes](#the-kernel-classes). |

`SymmetryLattice` owns the E8 Coxeter data the wheel is built from.

## Selecting one prime

Choose the operation from the answer you need:

| Request | API | Work performed |
|---|---|---|
| The Nth prime | `NthPrime(uint)` or `NthPrime(ulong, CancellationToken)` | A small-rank lookup, or an exact prime count followed by a local search. The index is zero-based: `999U.NthPrime()` returns the thousandth prime, 7919. |
| The number of primes through a bound | `PrimeCountingFunction(uint)` or `PrimeCountingFunction(ulong, CancellationToken)` | Exact combinatorial counting, with shortcuts near published checkpoints. |
| A uniformly random prime in an interval | `PrimeExploration.TryRandomPrime` | Samples a prime table for small intervals, or samples wheel candidates and rejects composites. |
| Whether one integer is prime | `IsPrime(uint)` or `IsPrime(ulong)` | An exact decision without a sieve. |
| Every prime of an interval, or their count | `PrimeExploration.Enumerate` or `Count` | A segmented sieve shares its setup across many answers. |

The two single-prime requests cost different things. An Nth-prime request must
establish how many primes precede its answer. A random-prime request samples a
candidate and tests it without finding any global rank.

| Request and route | When it applies | Advantage | Cost or limitation |
|---|---|---|---|
| Nth prime: direct lookup | The first 17 primes, then the shared table through 65535 | No counting or local sieve once the table exists | Building the table is a first-use cost; only small ranks |
| Nth prime: uint counting and correction | The answer fits `uint` and exceeds the table | Exact count plus a short correction; bounded pooled arrays | Counting work grows with the estimated answer |
| Nth prime: checkpoint correction | The estimated correction fits the local sieve budget | Skips the global count; tests a few candidates or sieves local windows | Exhausting the budget falls back to an exact count |
| Nth prime: quotient count and correction | Other ranks whose count estimate is at most 17592177655809 | Exact counting with pooled storage, then local selection | Workspace grows with the square root, up to about 52 MiB of pooled payload |
| Nth prime: Gourdon count and correction | Other, larger ranks | Any rank without sieving from a checkpoint; setup is reused between counts; a local sieve replaces a second count whenever its cost bound is lower | The global count can dominate the request |
| Random prime: table sampling | The interval ends at or below 65535 | Samples actual primes; no composite candidates | Limited range; first-use table cost |
| Random prime: wheel sampling and rejection | Other intervals | Samples only the eight units modulo 30 per block, plus 2, 3 and 5; no global count | A variable number of draws and decisions; the finite budget may expire |

### The Nth prime

The `uint` overload returns `uint` primes for indices through 203280220;
larger indices return zero. The first seventeen entries are constants; entries
through the 6542nd prime read the base-prime table through 65535, about 26 KiB
of payload generated once on first use. Larger ranks count.

The `ulong` overload supports every representable prime, with valid zero-based
indices below 425656284035217743; higher indices return zero. For example,
`203280221UL.NthPrime()` returns 4294967311, the first prime above the `uint`
range. Ranks with `uint` results use the narrow implementation. Larger ranks
start from an inverse Riemann R estimate and correct it with exact prime
counts, keeping an integer bracket `pi(lower) < ordinal <= pi(upper)` around
the requested rank. The estimate only chooses where to count; it never decides
the returned prime. A count that contradicts its own exact bracket raises
`InvalidOperationException` rather than returning the out-of-range zero.

After each exact count, a local sieve may replace the next count. It receives a
cumulative integer budget: the widest interval whose cost bound stays at or
below the work floor of the global count it replaces. A local window spans at
most 502333410 integers, so it touches one windowed segment, and each window
makes one upper-base pass. A window is charged its width plus that pass: the
square root of the bound for regenerating base primes, and thirty integers, one
complete wheel byte, for each Rosser–Schoenfeld-bounded base prime started by
division. The quotient recurrence's floor charges the same wheel byte for each
of its `ceil(sqrt(x)/2)` initialization divisions; the Gourdon count's floor is
its hard-leaf frontier, `floor(x/(y+1))+1` integers. The budget is about 695
thousand integers just above 2^32, 477 million near 10^15, 1.3 billion near
10^16, two billion near 3.7·10^16 and six billion near the top of `ulong`; ranks
10^15 and 10^16 make one global count followed by a single windowed segment.
These charges choose work only; they assert neither prime gaps nor cycle costs.

Exhausting a budget carries the exact local count into the bracket before
another global count. Forward selection skips complete segments with
population counts and decodes only the segment holding the answer. Reverse
selection keeps the window's bitmap, at most 16744449 wheel bytes padded to
16744456 for word reads, and selects downward from its surviving bits. Each
candidate needing a primality decision is tested once, and successive counts
within one rank search share a request-local workspace.

### Prime counting

`PrimeCountingFunction(ulong)` widens the `uint` quotient-counting recurrence for
bounds whose square root is at most 4194303, renting at most about 52 MiB of
array payload. Larger bounds use a managed Gourdon counter. Published
power-of-two counts are shortcuts: an exact checkpoint returns immediately, and
a bound within the local sieve budget of one uses one interval count. Distant
bounds count combinatorially, so their work does not depend on the distance to
a checkpoint. Both `ulong` overloads check their `CancellationToken` during
table construction, leaf processing and sieve traversal.

Gourdon partitions the count into ordinary leaves, easy leaves, hard leaves and
correction terms, following
[primecount's Gourdon decomposition](https://github.com/kimwalisch/primecount/blob/master/src/gourdon/pi_gourdon.cpp)
with its corrected integer bounds. It chooses `z = y`: the factor cutoff also
bounds the ordinary leaves, so the compact factor table needs no
maximum-prime-factor field. The B term and the Sigma0 correction share a
triangular term that cancels exactly, so the streamed semiprime count P2
supplies their difference; the remaining Sigma terms, the Phi0 ordinary leaves,
the A+C easy leaves and the D hard leaves are evaluated separately.

The cutoff `y` is `r / max(14, 0.84 ln r)`, with `r = floor(sqrt(x))`, within exact integer
bounds: above the integer cube root, below the integer square root, and at most
134217728. The count's cost is a constant plus `s·x/y` for the two sieves (the
hard-leaf sieve and the semiprime sieve, about 1.3 cycles per integer between
them) plus `l(x)·y` for the tables, easy-leaf terms and hard-leaf special-factor
coordinates, whose per-`y` cost grows with `x`. That cost is least at
`y = sqrt(s·x/l(x))`; the counted per-unit costs put `sqrt(x)/y` at 14 from 10^13
to 10^15, 15 to 16 near 10^16 and 10^17, and 17 to 18 near 10^18 on the AVX-512
and AVX2 kernels and on the Zen 2 and Alder Lake loop models alike. The minimum is
wide: a divisor within 2 of the best costs about 1% at any size. The logarithm
uses the deterministic fixed-point kernel; binary64 only scales its result and
proposes the cutoff. Evaluating at `r` keeps the cutoff constant between square
boundaries and non-decreasing across them, within one unit of
`floor(sqrt(x)) / max(14, 0.42 ln x)`. The cutoff moves work between the phases and never
changes the count. The factor tables keep only
residues coprime to 30. Each entry combines the least prime factor and the
Möbius sign in sixteen bits, with zero for a repeated prime factor; prime counts
use a bitmap and a prefix count per word. Each stored prime keeps its binary64
reciprocal beside it. The tables need about 7/12 of a byte per covered integer
plus twelve bytes per stored prime, and their capacity grows in 65536-entry
steps.

- **Ordinary leaves** use a periodic partial sieve through 19.
- **Easy leaves** use reflected grouping and one persistent sieve through the
  square root, with a 16 KiB bitmap and 8 KiB prefix-count array. Each segment
  is copied once into zero-padded words followed by one zero word, and each
  word's prefix counts the segment's primes through that word. Every easy-leaf
  divisor is a stored prime, so its quotients come from the reciprocal rungs
  below, which turn each quotient as they divide into its bit position: the
  number of integers coprime to 30 from the segment's start through the
  quotient, packed with the word in the high 32 bits and the bit within it in
  the low six. A prime-count lookup then reads the two halves, the word's
  prefix and the word, and subtracts the primes at and above the bit, a shift
  and a population count with no mask table and no edge branch.
- **Hard leaves** use 32 KiB wheel segments with incrementally updated
  population counters. Crossing off a prime unrolls its eight-step wheel cycle,
  which advances exactly that prime's number of wheel bytes. Each counter block
  covers eight bitmap words, 64 bytes, and keeps two 16-bit slots whose sum
  modulo 2^16 is its count, so a block's counters are one 32-bit word and one
  512-bit vector holds sixteen blocks' worth. The sieve keeps its counters, its
  word prefix masks, its window masks and its bitmap in one pinned, cache-line
  aligned store at fixed offsets, so a prefix cursor addresses all four from
  the bitmap's address. A small prime instead masks each 128-byte stride of two
  blocks with one period of masks and recounts each block in registers. The
  512-bit kernel, on AVX-512 VBMI, counts a byte's low seven bits with one
  two-table byte permute and adds its top bit under a mask; the AVX2 and SSSE3
  kernels look up both nibbles in a sixteen-entry table, and a 512-bit host
  without VBMI takes the AVX2 kernel. Each kernel writes both blocks' counts with
  one 64-bit store, splitting each count between the block's two slots. A
  counted crossing costs about two vector steps, so the limit is 16SW/(S+2W)
  bytes per wheel cycle for the 128-byte stride S and the kernel's vector width
  W in bytes: 512, 341 and 204 at 512, 256 and 128 bits. Every larger prime's
  k-th crossing of a cycle
  decrements slot k modulo 2, so crossings that share a slot are two wheel
  steps apart, at least 6q bytes for a prime 30q+r, and share a block only
  below 320; each crossing also prefetches its own byte one wheel cycle on, so
  a prime's next cycle is in the first-level cache when its crossings reach
  it. A prefix query two or more words past its cursor counts
  from its block's start. The 512- and 256-bit SIMD rungs multiply-add the
  sixteen blocks' slots ending at the query's block by a window mask, adding
  whole windows first for longer walks, and count the query block's words
  before the query through the eight words ending at it; the scalar rung sums
  the blocks one by one and the words through eight masked population counts.
  Primes up to the square root of the cutoff are
  filtered in batches of at most 128 by fixed-width SIMD comparisons of 32, 16
  or 8 packed factors, with a scalar tail; the masks keep exactly the factors
  the scalar predicate keeps, and the filter writes each surviving leaf's
  number and Möbius sign for the batch. The survivors move into place without a
  loop over them: AVX-512 VBMI2 compresses them, and the AVX2 and SSSE3 rungs
  shuffle each eight lanes by a table entry chosen by their survivor mask.
  The 512- and 256-bit filters pin the factor span and consume one masked head
  to align the following loads to their vector width; those loads never cross
  a 64-byte line. Short heads and tails can still cross a line. The VBMI2 rung
  packs offsets and signs in one compress and omits the upper sixteen-divisor
  store when at most sixteen lanes survive, reducing output stores from 192
  to 128 bytes for those vectors. AVX2 keeps its two eight-lane packs and
  four stores per sixteen input factors; SSSE3 and scalar remain fallbacks.
  The driver prepares two floor reciprocals per segment instead of dividing
  twice per prime, the same division the widened quotient-counting recurrence
  uses for its divisors. For a positive divisor d, r=floor((2^64-1)/d) makes
  the high half of n*r exact or one low for every ulong dividend, including
  d=1 and 1e19-scale n. One nonoverflowing remainder comparison supplies the
  missing unit. Each batch sum fits 64 bits, and the full sums use signed
  128-bit arithmetic.
- **Leaf quotients** divide one dividend by runs of divisors with 512-, 256- or
  128-bit SIMD rungs and a scalar rung. A rung estimates each quotient in
  binary64 and corrects it with one exact integer step: for a dividend below
  2^64, a divisor below 2^32 and a quotient below 2^48, the estimate lies within
  1/8 of the true quotient, so the corrected quotient is exact on every host
  and at every width. The correction's 64-bit product of a quotient and a
  32-bit divisor takes two 32-bit multiplies on the 256- and 128-bit rungs,
  which have no 64-bit multiply. The easy leaves' reciprocal rungs (512- and 256-bit SIMD
  and scalar) multiply by the divisor's stored binary64 reciprocal instead of
  dividing; with that extra rounding the estimate still lies within 1/4 of the
  quotient, and the same integer step corrects it. Their divisors stay below
  2^31, so the correction needs only the low 32 bits of the remainder, one
  32-bit multiply. A rung divides the quotient's offset by 30 with one multiply
  and a shift, finds the units below its remainder in a sixteen-entry byte
  shuffle, and packs word and bit with one more multiply; on AVX-512 VBMI the
  fraction bits of the division's product index a 64-entry permute instead,
  with no remainder formed. The windowed sieve divides
  each streamed batch of upper base primes with the dividing rungs. No
  floating-point value reaches the count.
- **The semiprime correction** streams primes downward from the square root in
  1 MiB reverse bitmaps while the forward sieve advances through their
  increasing quotients in 32 KiB segments. Each query reads one bitmap word
  under a mask; no square-root-sized prime table is kept.

The Gourdon kernels, the factor-table sieve, the leaf-quotient rungs and the
sieve's marking, pattern and segment kernels compile fully optimized on their
first call, because a
count spends seconds in them: under tiered compilation an unoptimized method
waits for promotion, which a single-processor host delays by a second or more
whenever new code is compiled. A rank search keeps its factor tables and leaf
buffers between counts; they become collectable after the request. Near the top
of `ulong` the cutoff is the table-capacity limit of 134217728 and the remaining
sieve frontier about 137 billion integers. The implementation is single-threaded.

### A random prime

`TryRandomPrime(low, high, ref generator, out prime, maxAttempts: 256)` takes an
inclusive `ulong` interval and a caller-owned `IDrawGenerator` value type. With
independent uniform input words, every prime in the interval is equally likely
conditional on success. Intervals ending at or below 65535 sample the prime
table directly; others sample the eight units modulo thirty plus any of 2, 3
and 5 in the interval, replacing each rejected composite with a fresh draw.
Advancing a random integer to the next prime would instead weight each prime by
the gap before it.

The budget counts raw 64-bit draws, each assembled from two 32-bit draws,
including draws rejected by unbiased range reduction
([Lemire](https://arxiv.org/abs/1805.10941)). A failed attempt leaves `prime`
zero and does not prove the interval has no primes. Empty candidate sets
consume no draws, and a singleton candidate is decided once without drawing.
Reversed intervals and nonpositive budgets throw. Identical starting states and
arguments reproduce both the answer and the final generator state; this API adds
no entropy.

Candidates within `uint` use the exact `uint` test. Above it, reciprocal filters
reject factors from 7 through 163 (35 inverse/ceiling pairs, 560 bytes built
once), then Baillie–PSW decides; its completeness over `ulong` rests on the
published exhaustive computation below 2^64
([Baillie, Fiori and Wagstaff](https://www.cs.uleth.ca/~fiori/Docs/bfw-accepted.pdf)).
Small intervals find their first and last table index with one population count
each over an odd-prime bitmap of the base-prime table, about 5 KiB built on
first use.
An interval ending at or below `uint.MaxValue` decides each draw with the exact
`uint` test as it arrives, since no wide decision applies to any of its
candidates. The table, wheel and narrow selection paths and the primality
kernels they call compile fully optimized on their first call. A request takes
microseconds, so under tiered compilation its setup and every candidate's
decision would run unoptimized until promotion, which a single-processor host
delays by a second or more. The dispatch between the paths stays tiered, so a
small interval compiles only the table path.

When the generator type holds no references, the selector treats it as a pure
value: a copy taken after a draw restores the stream exactly. It then keeps
drawing past an undecided wide survivor until four survivors are pending, runs
their base-two rounds as four interleaved Montgomery chains, and confirms them
with the strong Lucas test in draw order. The first prime wins and the generator
is restored to the copy taken after its draw. A narrow or exceptional candidate
ends the batch and is decided after the pending survivors; budget expiry
decides a partial batch survivor by survivor. The result, the final generator
state and success or failure are exactly those of deciding each candidate in
draw order. A generator whose draws depend on anything outside its own fields
must hold a reference, which keeps the sequential order.

Four chains because a chain's step, one REDC and one conditional doubling,
issues four multiplies, and on current cores the multiplier retires about four
in the time a lazy step's dependency chain takes, so four chains keep it busy
for exactly as long as a step lasts and a fifth would wait on it. At the top of
the carrier one survivor in six is prime, so a width of three decides 2.0
batches per selected prime, four 1.6 and five 1.4. A batch walks in the widest
of the arithmetics the [primality](#primality) section describes that its
largest modulus admits: lazily below 2^58, folded lazily below 2^62, exactly
above.
The filter depth stays at 163: with interleaved rounds the modelled request
cost is flat from 163 to 331 and rises beyond, and the depth never changes the
selected prime, the draws or the final state.

## Enumerating an interval

`Enumerate(low, high, onPrime, cancellationToken)` reports every prime of a
closed interval in ascending order; `Count(low, high, cancellationToken)`
returns their number. Both accept the whole `ulong` domain, and a reversed
interval is empty. The token is checked before starting, between segments,
during upper-base generation and before returning, so a token canceled before
the final check throws even when the result is complete. Cancellation does not
interrupt individual marks or callbacks within a segment. A callback may stop
enumeration by throwing; pooled bitmaps and state buffers are returned and
native state slabs freed even then.

```csharp
PrimeExploration.Enumerate(0, 1_000_000, prime => Console.WriteLine(prime));
var count = PrimeExploration.Count(ulong.MaxValue - 10_000, ulong.MaxValue);
```

### Policy

The interval chooses how it is sieved. Every policy reports the same primes;
the crossovers are cost heuristics.

| Interval | Policy |
|---|---|
| Ends below 65537² | Complete sieving with the shared base-prime table through 65535, which holds a factor of every composite below that bound |
| Narrower than `ceil(floor(sqrt(high)) / 64)` | Presieving with that table, then an exact decision for each surviving candidate at or above 65537² |
| Wider, with a conservative upper-base workspace bound within 128 MiB | Complete sieving that carries every upper base prime through `floor(sqrt(high))` in sparse buckets |
| Wider, beyond that bound | Windowed complete sieving: every upper base prime streams through each segment again, carrying no per-prime state |

The workspace bound starts from
[Rosser and Schoenfeld's Corollary 1, equation (3.6)](https://doi.org/10.1215/ijm/1255631807),
`pi(x) < 1.25506*x/ln(x)` for `x > 1`, with the logarithm replaced by a
rational lower bound and rounded upward in integers. It budgets eight-byte
states, partial pages, slab growth and the sliding bucket-head window, plus
3 MiB for medium states and the streamed base generator. It admits wide
intervals ending at 10^16 and sends wide intervals ending at 2^55 or higher,
including the full `ulong` interval, to the windowed policy. Shared tables, the
bitmap, small-prime states, allocator bookkeeping and pool retention are
additional memory.

### Segments and the presieve

Each segment stores one byte per thirty integers, one bit per unit residue in
ascending order, and starts at a 64-byte boundary. A segment holds 32 KiB, a
conservative size for a level-one data cache, or less when the interval is
shorter. The
windowed policy repeats its upper-base pass for every segment, so its segments
hold 16744448 bytes, 16 MiB less one 32 KiB chunk, which lets the bitmap and its
alignment pad share one 16 MiB pooled array. Two, three and five are
reported separately, partial first and final bytes are masked, and one is never
reported. Complete sieving counts survivors with word population counts.

Sixteen periodic byte patterns remove the multiples of 7 through 163, combined
four at a time in four passes: the first pass initializes the bitmap and the
others intersect it. A group with prime product `P` repeats every `P` bytes,
because adding `30P` to a candidate keeps its divisibility by the group. The
patterns are generated from divisibility on first use, with groups following
the compact periods of
[primesieve's pre-sieve](https://github.com/kimwalisch/primesieve/blob/v12.15/src/PreSieve.cpp);
no upstream data is copied. They are applied with byte vectors of 128, 256 or
512 bits, overlapping the final complete vector, and the primes 7 through 163
themselves are restored before the endpoint masks.

The patterns' intersection is exactly the set of integers coprime to
`2·3·5·…·163`, so Jacobsthal's function bounds the gaps the presieve can leave.
For that primorial it is 492 ([OEIS A048670](https://oeis.org/A048670), entry
38): every run of 492 consecutive integers holds one coprime to the product. A
presieved bitmap therefore never has more than 491 consecutive non-candidates,
at most two consecutive all-zero 64-bit words, and a candidate in any 17
consecutive wheel bytes. The law `prime-exploration.presieve-jacobsthal-run-bound`
checks the patterns against trial division, the reading of A048670 against the
smaller primorials, and the bound against real presieved bitmaps.

### Marking

Write a sieving prime as `p = 30q + r`. Its multiples `p·m`, for the multipliers
`m` coprime to 30 in ascending order, advance through eight wheel phases, and
eight phases advance the byte cursor by exactly `p`. The prime's residue
channel fixes which bit each phase clears; small tables supply the byte carries.

- **Small primes**, from 167 through `min(segment, 32 KiB) / 8`, mark 32 KiB
  chunks of the segment. Each base prime below 65536 carries an eight-byte state
  (a sixteen-bit quotient, the saved phase, the residue channel and a relative
  cursor) between segments. States are grouped by residue once, and each residue
  group runs a marker specialized for that residue, so masks, carries and byte
  offsets fold to constants; the inner loop clears eight bytes per wheel cycle
  with eight memory AND instructions.
- **Medium primes** above that cutoff mark the whole segment. Each residue keeps
  eight phase lists; a processed state moves straight into its next phase's list
  for the next segment, and the two buffers swap. The starting phase is
  specialized once per occupied list.
- **Upper primes**, from 65537 through `floor(sqrt(high))`, stream from an
  internal generator that reuses the same presieve and packet marking with a
  32 KiB bitmap and a 4096-entry buffer of packed quotient and residue
  coordinates; no complete upper-prime table is kept. Under complete sieving,
  upper primes through 98304 join the medium packet marker, so packet quotients
  reach 3276; larger ones enter a sparse bucket scheduler. Each scheduled state
  occupies eight bytes, the quotient, a 210-wheel phase and an offset inside a
  32 KiB bucket, in aligned 8 KiB native pages of 1022 states. A full bucket
  makes one mark per state and moves it to the bucket of its next multiple, so
  upper primes are not rescanned every segment; this follows
  [primesieve's large-prime marker](https://github.com/kimwalisch/primesieve/blob/v12.15/src/EratBig.cpp),
  with transitions generated from the local tables.
- **Windowed segments** stream every upper base prime through each segment
  again. A batch of streamed coordinates decodes to primes, and one leaf-quotient
  rung divides the segment start by the whole batch, giving each prime's first
  multiple at or above the segment start. A table rounds that multiplier up to a
  unit modulo 30, a wide product rejects primes with no wheel multiple in the
  segment, and the wheel steps mark the multiples. No
  per-prime state outlives the segment, so the workspace is independent of the
  number of base primes while base generation repeats once per segment.

Presieved survivors below 65537² need no decision, because every composite
below that bound has a factor in the shared table; at or above it, each
surviving candidate is decided exactly. Relative cursors and wide products keep
every computation inside `ulong`, including the final partial block.

## Coordinates and channels

After the exceptional primes 2, 3 and 5, primes occupy eight residues modulo
30. `PrimeWheel30` labels each by a channel `a | (b << 2)` for the residue
`7^a 11^b mod 30`:

| Channel | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| Residue | 1 | 7 | 19 | 13 | 11 | 17 | 29 | 23 |

The powers of 7 have order four; 11 has order two and lies outside that
subgroup. Their eight products exhaust the units modulo 30, so
`U30 = C4 × C2`. `PrimeWheel30.Multiply` adds the low two coordinates modulo
four and XORs the high one; `Inverse` negates the first coordinate;
`PermuteMask` rotates both four-bit nibbles of a channel mask and optionally
swaps them. Sieve bitmaps use the ascending residues `1, 7, 11, 13, 17, 19, 23,
29` (`NumericResidues`), whose cyclic gaps are `6, 4, 2, 4, 2, 4, 6, 2`.

### Where the numbers come from

The wheel's numbers are the Coxeter data of the exceptional root system E8,
which `SymmetryLattice` builds and which is their one home in code. A Coxeter
element of E8 (`SymmetryLattice.Cycle`) is a rotation of eight-dimensional space
whose order, the Coxeter number `h`, is 30 (`SymmetryLattice.CycleOrder`). Its
eigenvalues are `e^(2πim/30)` for the exponents `m` =
`1, 7, 11, 13, 17, 19, 23, 29` (`SymmetryLattice.CycleExponents`): exactly the
units modulo 30. The rotation maps the lattice of roots to itself, so its
characteristic polynomial has integer coefficients, and `e^(2πi/h)` is always
one of its eigenvalues. A polynomial with integer coefficients that vanishes at
one primitive `h`-th root of unity vanishes at all of them, so the cyclotomic
polynomial `Φh` divides it and every unit modulo `h` is an exponent. For E8 the
rank, 8, equals `φ(30)`, so the characteristic polynomial is exactly `Φ30`. The
same exact fit holds for G2 (`h` = 6) and F4 (`h` = 12); E6 and E7 carry
exponents beyond the units. The law `integer.symmetry-lattice-coxeter-data`
checks the E8 case against the lattice itself.

The multiplication table is the same structure seen from the sieve. `U30`,
generated by 7 and 11, is the Galois group of the 30th roots of unity and
permutes the eight eigenvalues; marking the multiples of a prime `p ≡ r (mod 30)`
permutes the eight channels by multiplication by `r`. `CyclicRotation`'s four
planes turn at the speeds `1, 7, 11, 13`, one per complex-conjugate pair of
eigenvalues `m` and `30 − m`.

Thirty is also the largest very round number
([OEIS A048597](https://oeis.org/A048597), shown by Schatunowsky and by
Wolfskehl): every unit below 30 is 1 or a prime, so before any sieving every
candidate bit of the first block other than the one for 1 is a prime. One 64-bit
word holds eight wheel bytes, 240 integers (`PrimeWheel30.WordIntegers`); E8
has `8 × 30 = 240` roots (`SymmetryLattice.NodeCount`). The counts are equal as
numbers; no correspondence between words and roots is claimed.

`PrimeWheel30` derives the wheel from these constants: `Modulus` is
`SymmetryLattice.CycleOrder`, `ChannelCount` is the rank and `NumericResidues`
is `SymmetryLattice.CycleExponents`. One internal derivation, the walk of
`p = 30q + r` through its multipliers, generates the gaps, carries, lifts,
target bits, clear masks and prefix masks the sieves, packet markers and Gourdon
counter read. Tables a hot loop indexes by a variable position, and constants
the specialized markers fold, are written out; the law
`prime-exploration.wheel-tables-match-their-derivation` proves every copy equals
the derivation.

## Primality

`IsPrime(ulong)` sends `uint` values to the exact `IsPrime(uint)` kernel.
Larger values reject factors 2, 3 and 5, then
use Baillie–PSW: a base-two strong round followed by the strong Lucas test with
Selfridge Method A. Its exactness throughout `ulong` rests on published
exhaustive computation and the equivalence of Methods A and A*, as the
[finite-field reference](../../src/Puck.Maths/FiniteFields/README.md#primality-on-ulong)
explains. `IsPrime(ulong)`, enumeration, factorization, finite fields and random
selection share this decision; the random selector adds its small-factor filter
first.

Each multiplication chain uses `ScaledResidueRing64`'s REDC, with no division
inside the chain. Baillie–PSW derives the modulus's inverse and reduced radix
once and shares them between its two rounds. The base-two round takes two as
`one + one` in Montgomery form, and the strong Lucas test walks a V-only ladder,
reading `U_d = 0` off `2V_(d+1) = V_d`. The `uint` kernel's base-two round uses
a 64-bit radix with lazy reduction. The arbitrary-width `BigIntegerFunctions`
twelve-base threshold is separate.

Below `ScaledResidueRing64.LazyModulusCeiling`, 2^58, every chain of the word
decision carries residues lazily: a product comes back below twice the modulus
through `ReduceLazy`, a sum is a plain sum, a difference is lifted by four times
the modulus, and nothing is folded until a residue is compared, where
`Canonical` recovers it. Every operand a chain multiplies stays below eight
times the modulus, which is what the ceiling guarantees keeps the lifted
quotient below twice the modulus. Below `FoldedLazyModulusCeiling`, 2^62, the
same chains fold each sum and difference once, by twice the modulus, so every
operand stays below twice the modulus and the product bound holds up to that
ceiling; the product's own fold is still gone. The decision is the exact
chain's decision at every value, pinned by
`prime-field.lazy-chains-vs-exact-decision`; what changes is the chain's step,
which drops the compare-and-mask fold after every product, and below 2^58 after
every doubling too, so a step there is the three multiplies of a REDC and one
subtraction. The JIT turns a select inside a loop into a branch rather than a
conditional move, and a data-dependent branch on the chain mispredicts about
half the time, so the exact chains above both ceilings keep their masks.

## Constellations

`PrimeConstellation` accepts strictly increasing `ulong` offsets beginning at
zero: twins `[0,2]`, cousins `[0,4]`, sexy pairs `[0,6]`, triplets `[0,2,6]` or
`[0,4,6]`, quadruplets `[0,2,6,8]`. `Enumerate(low, high, visit,
cancellationToken)` reports each anchor whose whole pattern lies inside the
interval, in ascending order, with no addition wrapping at the top of `ulong`.
An eight-bit channel mask rejects anchors whose tuple meets a factor 2, 3 or 5;
exceptional anchors 2, 3 and 5 are checked separately, and the remaining members
are decided individually. Anchors come from `PrimeExploration.Enumerate` with
the same cancellation contract.

## The kernel classes

The counting, selection and sieving paths are built from kernels that are
public so a law can drive each rung alone and compare it with a direct oracle.
Four classes hold them, one per domain:

| Class | Holds |
|---|---|
| `PrimeKernels` | The word-sized primality and factorization machinery, the odd-prime rank `CountOddPrimesBelow` and the Selfridge symbol table `SelfridgeSymbol` |
| `PrimeDivisionKernels` | The leaf quotients `DivideGourdonQuotients512`, `256`, `128` and `Scalar`, the packed bitmap positions `DivideGourdonSegmentPositions512`, `256` and `Scalar` with their units-below lookups, and division by a fixed divisor through `FloorReciprocal` and `DivideByFloorReciprocal` |
| `PrimeLeafKernels` | The hard-leaf survivor filters `FilterGourdonHardLeavesCompress`, `Pack256`, `Pack128` and `Scalar`, the thirty-wheel leaf sieve `CombinatorialLeafSieve` with its `CombinatorialPrefixCursor`, and the wheel tables they read |
| `PrimeSieveKernels` | The presieve groups and patterns, `FilterSmallPrimes`, the packet residues and `PacketOffset`, and the wheel's clear-mask and unit-count tables |

Each method that could otherwise read or write out of bounds is a checked shell
over an internal core with the same body. The shell checks every span length,
index, alignment and bound the kernel relies on, and, for the quotient and
segment-position rungs, makes one pass over the divisors to check each divisor,
reciprocal and quotient range. It throws
`ArgumentOutOfRangeException` or `ArgumentException` naming the parameter
before it writes anything. A rung that executes an instruction set directly
(the AVX-512 VBMI lookup, the AVX-512 VBMI2, AVX2 and SSSE3 hard-leaf filters,
the AVX-512BW and AVX2 prefix windows) throws `PlatformNotSupportedException`
on a host without it. The counting engine's loops call the cores directly, so
the checks cost them nothing. What a shell cannot check, such as the order of a
prime list or a cursor made stale by a later crossing, is the caller's, and each
member's documentation says so. The law
`prime-selection.kernel-shells-refuse-violations` drives every shell with each
violated precondition.
