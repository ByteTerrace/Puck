# Prime exploration

`Puck.Maths` explores primes in closed intervals throughout the `ulong` domain.
`PrimeExploration.IsPrime(ulong)` decides isolated values; `Enumerate` reports
interval primes in ascending order; `Count` returns their number without a callback.
`NumberTheoryFunctions.SegmentedPrimeSieve`
and `EnumeratePrimes` use the same enumeration implementation.

## Selecting one prime

Choose the operation from the requested answer, before choosing a marking strategy:

| Request | Preferred API | Work performed |
|---|---|---|
| The Nth prime | `PrimeExtensions.NthPrime(uint)` | A small-rank lookup or an exact prime count followed by a local search. The index is zero-based: `999U.NthPrime()` returns the thousandth prime, 7919. |
| A uniformly random prime in an interval | `PrimeExploration.TryRandomPrime` | Samples a prime table for small intervals, or samples wheel candidates and rejects composites. |
| Whether one integer is prime | `PrimeExploration.IsPrime` | An exact decision without constructing a sieve. |
| Every prime, or their count | `PrimeExploration.Enumerate` or `Count` | A segmented sieve amortizes initialization across many answers. |

`NthPrime` returns uint primes, with indices through 203280220; larger indices
return zero. The first seventeen entries use existing small-prime constants;
entries through the 6542nd prime reuse the base-prime table through 65535.
The table is generated once on first use and contains about 26 KiB of prime
payload. Larger ranks retain the counting-based search. This is not an
arbitrary-ulong rank selector.

`TryRandomPrime(low, high, ref generator, out prime, maxAttempts: 256)` accepts
an inclusive ulong interval and a caller-owned `IDrawGenerator` value type.
With independent uniform input words, every prime in the interval is equally
likely conditional on success. It samples directly from the prime table when
`high <= 65535`; otherwise it samples the eight residues modulo thirty plus
any of 2, 3 and 5 in the interval. Each rejected composite is replaced by a
fresh draw. Advancing a random integer to the next prime would instead weight
the answer by its preceding gap.

The budget counts raw 64-bit draws, each assembled from two 32-bit draws,
including draws rejected by unbiased range reduction. A failed attempt leaves
`prime` zero and does not certify that the interval has no primes. Empty
candidate sets and singleton small-table selections consume no draws. Reversed
intervals and nonpositive budgets throw. Generator state is passed by reference,
so identical starting states and arguments reproduce both the answer and final
state. The caller supplies randomness; this API adds no entropy.

The selector uses the exact uint test for small candidates. Above uint it
rejects small factors through 163 using reciprocal tables, then uses
the existing Baillie–PSW implementation. Its completeness over ulong rests on
the published exhaustive computation below 2^64, not a theorem for arbitrary
integers ([Baillie, Fiori and Wagstaff](https://www.cs.uleth.ca/~fiori/Docs/bfw-accepted.pdf)).
Unbiased range reduction uses multiply-high with rejection
([Lemire](https://arxiv.org/abs/1805.10941)). Warm request benchmarks exclude
first-use table generation; first-request latency must be considered separately.
The extra selection filter initializes 21 inverse/ceiling pairs once, retaining
336 bytes of factor payload. It leaves factorization's existing trial budget unchanged.

## Coordinates and channels

After the exceptional primes 2, 3 and 5, primes occupy eight residues modulo
30. `CandidateAddress` stores `(block << 3) | channel`, where the block is
`value / 30`. Channel `a | (b << 2)` denotes `7^a 11^b mod 30`:

| Channel | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| Residue | 1 | 7 | 19 | 13 | 11 | 17 | 29 | 23 |

The powers of 7 have order four; 11 has order two and lies outside that
subgroup. Their eight products exhaust the units modulo 30, proving
`U30 = C4 × C2` directly. `PrimeWheel30.Multiply` adds the low two coordinates
modulo four and XORs the high coordinate. `Inverse` negates the first coordinate.
`PermuteMask` rotates both four-bit nibbles and optionally swaps them. Its
rotation/swap overload accepts arbitrary integer powers of seven modulo four,
including negative powers; the channel overload validates and delegates to it.

Numeric scanning instead visits residues `1,7,11,13,17,19,23,29`, with cyclic
gaps `6,4,2,4,2,4,6,2`. Address comparisons use decoded values; packed-word order
does not express numeric order inside a block. Default address represents 1,
which is a valid wheel candidate and is composite. Addresses reject the spare
high bit, oversized blocks, and the four unrepresentable lanes in the final
partial block. The spare bit has no assigned meaning.

## Enumeration and storage

Each segment stores one byte per block of thirty integers. Numeric and
algebraic bit layouts are selectable independently from the marking strategy.
The default uses numeric bits and direct phase buckets with a 32 KiB segment maximum.
`WheelSteps` advances multiples with the eight gaps. `EightStreams` fixes a
multiplier residue and marks one destination bit at byte indices separated by
the sieving prime. This follows from `p(m + 30) = pm + 30p`. Both start at
`p²` for scalar marks, mask partial endpoints, and recompute each prime's start for each segment.
These two controls have no carried per-prime state or bucket scheduler.
Both marking strategies use the same precomputed destination masks. Strategy
and layout selection occur outside the multiple-marking loop. Both hot loops
use by-reference byte updates with established span bounds: streams guard
their offsets directly; wheel steps keep multiples between segment endpoints
and table indices in `0..7`. Neither calls a callback or channel helper per mark.

The packet alternatives share one eight-store marking loop:

| Strategy | Prime start initialization | Marking region |
|---|---|---|
| `UnrolledPackets` | Every segment | Whole segment |
| `CarriedPackets` | Once per interval | Whole segment |
| `CacheBlockedPackets` | Once per interval | At most 16 KiB inside each segment |
| `SpecializedPackets` | Once per interval | Whole segment, with fixed residue masks and lifts |
| `PhaseSortedPackets` | Once per interval | Specialized packets, with active medium states sorted by saved phase |
| `BucketPackets` | Once per interval | Cache-sized small-prime chunks and direct medium-prime phase buckets |

They retain the earlier controls so unrolling, carried positions, cache
chunking and residue specialization can be measured separately. Specialized
packets instantiate the same source body for each prime residue and bit layout,
allowing the compiler to fold masks, integer lifts and partial-packet carries into constants.
Primes are grouped by residue once with a stable eight-way counting partition;
the ascending input keeps each group's square-activation order. Its pooled
temporary buffer returns immediately after grouping. Marking visits each group
without a per-prime residue dispatch. The other packet strategies instantiate the shared marking
routines with runtime tables. Phase-sorted packets additionally counting-sort
active states above `min(segment length, 32768) / 5` within each residue group,
using a pooled temporary buffer. This adds counting, scattering and copying;
it does not implement a scheduler that transfers each processed state directly
into its next phase bucket. Only base primes through 65,535 use packets;
upper bases keep the eight-stream marker. Each carried state stores a prime quotient,
relative byte cursor, multiplier position, and multiplicative channel in eight
payload bytes. State buffers are pooled per call and returned on callback failure.
Primes activate when their squares enter the marking region; dormant cursors
retain their initial origin until activation.

Bucket packets store the prime quotient in sixteen bits; its maximum is 2184
for the supported packet bases. The prime residue comes from the group, so the
marking loop needs no division by thirty. Its requested segment size is a maximum:
`2 * floor(sqrt(high))` is clamped between `min(request, 32768)` and the request,
then rounded down to a multiple of that minimum. Small primes through
`min(actual segment size, 32768) / 8` mark chunks of at most 32 KiB. This measured
partition differs from the reference's one-fifth cutoff. Medium
primes mark the whole actual segment using phase fallthrough without the bulk
packet leaf. The starting phase is specialized once per occupied bucket, and
the marker returns its next phase directly. Each processed medium state moves directly into its next residue
and phase list; two buffers swap after the segment. There is no counting-sort
or copy-back pass. A phase list reserves its residue group's full medium-prime
capacity, so each buffer can reserve eight times the medium-state payload;
only occupied entries are scanned. Both buffers return to their pools on
normal completion and callback failure. Upper bases retain eight-stream marking.
The bitmap starts at a 64-byte boundary and stays pinned throughout the call;
up to 63 pooled padding bytes are additional workspace. Requests too large to
add that padding retain the unaligned path. Packet stores use direct pointers
only inside proven bitmap bounds. Explicit byte read/write expressions let the
JIT fold indexed addresses into the packet loop's eight memory AND instructions;
compound assignment can introduce separate address calculations. Native-sized
offsets and strides avoid widening inside that loop. Confirm this code shape in
optimized assembly on the measured target.

Write a sieving prime as `p = 30q + r`. At multiplier residue one, the byte
offset for a subsequent multiplier residue `s` is
`q(s - 1) + floor(rs / 30)`. Eight successive wheel positions return to residue
one and advance the byte cursor by exactly `p`. The multiplicative channel
determines the destination-bit permutation; small tables supply the integer
lifts and carries needed for byte offsets. Group multiplication alone does not
determine those additive carries. Partial packets preserve their wheel position
across marking regions, and relative cursors avoid forming a full-width next
multiple near the top of `ulong`.

Bulk pre-sieving is enabled by default. `usePreSieve: false` selects the scalar
control independently of marking strategy, bit layout, or survivor decisions.
The bulk filter replaces individual marks for primes 7 through 163 with sixteen
periodic byte patterns, combined four at a time in four passes over the bitmap.
The first pass initializes the bitmap; the other three intersect it with their patterns.
For a group with prime product `P`, the pattern repeats after `P` bytes:
adding `30P` to a candidate preserves divisibility by every prime in the group.
Since each prime is coprime to 30, each residue has exactly one excluded byte
position modulo that prime. Patterns are generated once from this divisibility
rule. The algebraic layout is a bit relabeling of the numeric patterns, so both
layouts share the same arithmetic construction.

The grouping follows the compact-period approach in
[primesieve's pre-sieve](https://github.com/kimwalisch/primesieve/blob/v12.15/src/PreSieve.cpp).
No upstream pattern data is copied. Both layouts' tables are retained together;
scalar-only calls do not construct them. The filter reuses the existing byte
vector abstraction at hardware-selected widths of 128, 256, or 512 bits, with
an overlapping final complete vector where the batch has room for one, and
an exact scalar tail for shorter batches. Repeating an AND or initialization
over the final vector is idempotent and stays within the batch. These extra
stores are excluded from the survey's logical input and exclusion counts.
Divisibility also removes the small
primes themselves, so their bits are restored before endpoint masking.
Pattern construction is a first-use cost; warm surveys exclude it.

`Count` shares that marking implementation and all endpoint rules. Complete
sieving counts surviving bits with four-word population-count batches, remaining words and a byte tail,
independently of bit layout. Presieving still tests every survivor exactly.

`Automatic` completely sieves intervals ending within `uint.MaxValue`. Above
that bound it presieves with the shared primes through 65,535 and tests every
survivor exactly. `Presieve` requests that bounded policy explicitly.
`Eratosthenes` generates and retains every required base prime through
`floor(sqrt(high))`, using the existing odd-window marker. Base primes fit in
`uint`; upper bases occupy chunked arrays. For the maximum endpoint, the odd
base-prime payload alone is about 775.45 MiB. Narrow intervals near the maximum
therefore normally use `Automatic` or `Presieve`.
The automatic endpoint threshold is a bounded-cost policy, not an adaptive
prediction of the fastest algorithm for every interval width.

```csharp
PrimeExploration.Enumerate(0, 1_000_000, prime => Console.WriteLine(prime));
var count = PrimeExploration.Count(0, 1_000_000);
PrimeExploration.Enumerate(ulong.MaxValue - 10_000, ulong.MaxValue,
    prime => Console.WriteLine(prime), mode: PrimeSieveMode.Presieve);
```

Compressed base-prime gaps, persistent caches, and demand-driven upper-base
generation are possible experiments. They are not implemented storage policies.
Full-domain enumeration remains an enormous workload: a compact address does
not reduce the number of candidates that must be processed.

## Primality

The ordinary decision dispatches `uint` values to the existing exact
`PrimeExtensions.IsPrime` kernel. Larger values reject factors 2, 3 and 5,
then use witnesses `2,325,9375,28178,450775,9780504,1795265022`.
Their complete unsigned-64-bit range is recorded in the
[Miller–Rabin computational results](https://miller-rabin.appspot.com/).
This is a published finite-domain result, not a proof supplied by this library's
sampled tests. A witness reducing to zero skips only its own round.

`IsPrimeCandidate` skips the wheel filters. The rounds share the existing
`ScaledResidueRing64`: exact Montgomery multiplication uses wide intermediates
and avoids division within the squaring chain. The benchmark's ordinary
`UInt128` remainder implementation provides a separate arithmetic baseline.
The arbitrary-width `BigIntegerFunctions` twelve-base threshold is unchanged.

## Constellations and statistics

`PrimeConstellation` accepts strictly increasing `ulong` offsets beginning at
zero. For example, twins use `[0,2]`, cousins `[0,4]`, sexy pairs `[0,6]`,
triplets `[0,2,6]` or `[0,4,6]`, and quadruplets `[0,2,6,8]`. Enumeration
requires every member to lie inside the requested interval. An eight-bit mask
rejects anchors whose tuple meets a factor 2, 3 or 5; exceptional small-prime
anchors are checked separately. Remaining tuple members are tested individually.
There is no shared halo bitmap across segment boundaries.

`PrimeStatistics.Measure` records counts by algebraic channel, consecutive
prime gaps, and an eight-by-eight transition matrix. Small primes have a
separate count/mask; transitions require both adjacent primes to exceed five.
Gaps connect only consecutive primes returned within the interval. A gap to a
prime outside the interval is not inferred. Gap histograms can grow with the
number of distinct gaps.

## Measuring alternatives

The [CLI benchmark reference](cli.md#puck-benchthe-puckmaths-microscope) lists
the `PrimeCandidateScan`, `PrimeChannelMasks`, `PrimePrimality`, and
`PrimeSieveSegments` kernel scenarios. Their setup compares results before
timing. Trial-divisor counts 0, 4 and 16 are benchmark parameters; production
does not acquire an extra trial ladder on the strength of an assumption.
Candidate scans share a generic traversal with value-type steppers. Their
outputs either enter the same rotating checksum or fill identical preallocated
word buffers; setup checks every stored value. Algebraic table bounds follow
from the complete channel cycle; numeric bounds follow from its masked index.
Mask alternatives share materialized operands and traversal, with both grouped
and deterministically shuffled order. They either checksum results or fill
identical byte buffers. The lookup table occupies 2 KiB; construction and
shuffling stay outside timing. The nibble case calls the shared rotation/swap
overload, so a channel-only API guard does not distort the primitive comparison.
These two sinks distinguish checksum latency
from output throughput without subtracting a guessed harness cost.

Primality alternatives share candidate traversal, trial screening, and checksum.
Both use the same witnesses, exits, and least-significant-bit-first power
schedule, including omission of the final unused square. The division reference
accepts already-reduced witnesses rather than repeating a general-purpose API's
validation and reductions. Setup checks its powers against `BigInteger.ModPow`.
Montgomery constant construction and representation conversion remain measured
parts of that implementation. The round and power bodies remain separate
implementations; assembly inspection establishes their actual call boundaries.

Inspect optimized assembly on the measured target to confirm that specialized
step, permutation, and sink operations inline, and that bounds checks do not
distort a comparison. `PrimeSieveSegments` measures count-only and callback
enumeration separately; its callback is allocated once during setup.

`PrimeSurvivorDecisions` compares the current Montgomery seven-witness test,
Baillie–PSW, and the `UInt128` remainder reference with the same candidate
traversal and checksum. Bands begin near 10¹², 10¹⁸ and the top of `ulong`.
The presieved stream excludes factors through 65,535; the prime-only stream
isolates the cost of accepting primes. Setup checks every decision outside
timing and reports the candidate count, prime count and stream identity.
Sieving and candidate construction are excluded from these kernel times.

`NthPrimeRequests` measures single rank lookups with independently checked
answers. `RandomPrimeRequests` compares complete single-prime requests against
uniform integer rejection with Miller–Rabin or Baillie–PSW. All paths share the
same generator type, seed, request loop and checksum; static value-type wrappers
allow specialization without interface dispatch. Samples contain 128 requests,
reported per request, with the same 2048-draw budget for every policy so the
integer-rejection control can finish the fixed stream. Setup checks that every
request succeeds and verifies each answer independently. This measures the
whole selection policy, including sampling and filtering, rather than only the
primality kernel. The finite benchmark stream is not a distribution proof.
Three fixed seeds exercise each range. `PrimeFilterDecisions` first screens
filter cutoffs through 5, 59, 163, 541 and 1021 with Miller–Rabin and Baillie–PSW
on identical wheel-candidate arrays. Range sampling stays outside that kernel
measurement; setup reports survivor counts and checks every decision against
the independent UInt128 reference. A promising kernel result still needs the
complete-request comparison before changing the selector.

`puck bench primes --upper 100000000 --primesieve <executable> --output <directory>`
runs a serial survey with all strategies and both layouts, bulk patterns enabled and
disabled, and 16 KiB, 32 KiB, 1 MiB and
8 MiB requested segments. `--segments` selects distinct KiB multiples in that
range; `--strategies` selects distinct marking strategies. `--layouts` selects
layouts, and `--patterns Both|Enabled|Disabled` selects the periodic-filter
controls. `--count-only` omits callback enumeration. `--low` and `--upper`
select any closed unsigned-64-bit interval. `--mode` accepts distinct modes
in the same rounds, defaulting to `Eratosthenes`; `Automatic` and `Presieve`
include exact survivor decisions where their API policies require them.
Each full-sieve call includes upper-base generation and storage; those costs
are not amortized across survey calls. Shared pattern construction remains warm.
On Windows, `--cpu` selects an available logical CPU in `0..63`; the native
child processes inherit the same affinity, and the managed process restores its
original mask on exit. Reports record the selection as `LogicalCpu`; an omitted
selection leaves scheduler placement in effect. Use the same explicit CPU for
repeat comparisons. `--warmups` defaults to three
untimed rounds and `--samples` to five measured rounds. All variants warm before
measurement; subsequent rounds rotate their starting variant and alternate
direction. `--batch` repeats each managed call within a sample and divides
elapsed time and calling-thread allocations by that count; every call checks
its prime count. This reduces timer quantization for narrow intervals.
Allocations exclude the harness but do not measure retained pools or peak memory.
Actual active segment bytes cannot exceed the number of thirty-integer blocks
intersecting the candidate interval; a request above that band does not measure a full cache
working set. JSON includes execution order, runtime settings, executable hashes,
native version and CPU/cache information. Managed rows record active bitmap
bytes, full segments, the tail, active prime/marking-chunk visits, and logical
in-interval prime-multiple exclusions where supported. Fine-grained marking
counters cover only zero-origin complete uint sieves and are null otherwise;
bitmap metadata, allocations and pattern counts remain available throughout.
Exclusions count repeats across different sieving primes and exclude primes
handled by periodic patterns. Pattern rows additionally record logical pattern
input bytes (sixteen per bitmap byte) and bitmap passes (four per segment);
these are algorithm counts, not hardware memory traffic. These deterministic
counts describe algorithm work independently of machine load. End-byte padding
can receive additional AND stores; logical exclusions do not count those stores.
Reports separate active prime/marking-chunk visits from prime-start initializations
and record the marking chunk size and count. Pooled backing capacity, carried
state buffers, shared base-prime tables, and retained pattern tables are additional workspace.
Phase-sort candidates count active states above the strategy's size-dependent
threshold, including singleton groups for which the copy is skipped.
Bucket transfers count active medium states processed once per actual segment.
Their prime visits combine small-prime cache chunks and medium-prime whole
segments; the reported marking chunk size and count describe the small-prime
partition. Actual bitmap fields use the adaptive size for bucket packets.
Managed `/count` rows use population counting;
`/enumerate` rows deliver ascending values to a counted callback allocated
once and reused outside timing. Native rows use optimized single-thread counting. Every row
checks the count over the same closed interval. Native printed internal time and
whole-process time are separate fields. Native segment size is an upper limit:
its adaptive segment selection and L1-sized small-prime chunks can use less
memory than the requested size, so equal requests do not imply equal cache work.
For the pinned native 12.15 implementation, a multi-segment `--size=16` run has
a source-established 16 KiB full bitmap: its minimum and maximum size coincide
in [segment initialization](https://github.com/kimwalisch/primesieve/blob/v12.15/src/Erat.cpp).
The report marks that guaranteed case explicitly; it leaves the field null
for other native requests. Separately, the pinned source's default factors and
a reported 32 KiB L1 imply a 32 KiB full bitmap for `[0, 1000000000]` when the
requested size is at least 32 KiB. This follows from `2 * floor(sqrt(high))`,
rounded down to an L1 multiple and clamped to the requested maximum in
[segment initialization](https://github.com/kimwalisch/primesieve/blob/v12.15/src/Erat.cpp)
and the [default configuration](https://github.com/kimwalisch/primesieve/blob/v12.15/include/primesieve/config.hpp).
This is a source-derived size under those defaults, not runtime instrumentation
of total native workspace.
Endpoint alignment, tail rounding, presieve tables, and per-prime state still
differ. Managed warm-process and native fresh-process rows compare integrated
implementations; native internal time is the closer sieve-work comparison.

The native [medium-prime marker](https://github.com/kimwalisch/primesieve/blob/v12.15/src/EratMedium.cpp)
groups states by both prime residue and saved phase in 64 buckets, transferring
each state into its next bucket during marking. With the same 32 KiB L1 and
default configuration, its small-prime cutoff is 6553 for the billion-integer
band; primes above that cutoff through 31622 use the medium marker. Matching
only the small-prime packet loop therefore does not match the complete native
algorithm. Bucket packets implement both partitions using eight-byte quotient states,
with a measured one-eighth cutoff (4096 in that band) and a static starting phase per bucket.
The native bucket memory pool and the managed reserved phase lists have different
backing-capacity policies, even when their occupied state payloads coincide.

[primesieve](https://github.com/kimwalisch/primesieve) uses mature wheel,
pre-sieve and bucket techniques. A result from this initial implementation
does not establish that algebraic channels improve those algorithms. Run the
survey on an idle machine; use a longer BenchmarkDotNet job before retaining a
small timing difference as an optimization.
