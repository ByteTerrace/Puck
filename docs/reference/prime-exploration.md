# Prime exploration

`Puck.Maths` keeps the prime operations used by the language, factorization,
finite fields and presented algebra. The wide prime-counting and interval
exploration research engine has no production consumer and is removed under
the owner's prime-research cleanup ruling. The separate `Research/`, `Algebra/`
and `Oracle/` wings remain outside that cleanup.

## Selecting one prime

`PrimeExtensions.NthPrime(uint)` takes a zero-based index: zero returns two,
and `999U.NthPrime()` returns 7919. Indices through 203280220 return a `uint`
prime; larger indices return zero. The language builds its first 64 primes
with this entry point.

The first seventeen primes use small-factor constants. Remaining primes below
65536 use the shared base-prime table, generated once on first use. Larger
ranks use a Cipolla estimate, align it with an exact narrow prime count and
correct the residual by primality decisions or a window sieve. The estimate
changes work, never the result. `PrimeCountingFunction(uint)` supplies the
count through an inclusive bound using a square-root-sized quotient recurrence
with pooled storage. The narrow counter and sieve stay because the retained
nth-prime operation needs them.

There is no wide nth-prime overload, wide prime count, random-prime selector,
interval enumeration or constellation API.

## Primality

`IsPrime(ulong)` dispatches values in the `uint` range to `IsPrime(uint)`.
Larger values reject even numbers and multiples of the 128 odd primes through
727, then run Baillie–PSW: one base-two strong round followed by the strong
Lucas test with Selfridge Method A. Exactness across `ulong` rests on the
published exhaustive computation described in the
[finite-field reference](../../src/Puck.Maths/FiniteFields/README.md#primality-on-ulong).
The language, arbitrary-width arithmetic, finite fields and presented algebra
share this decision.

`IsPrime(uint)` reads an odd value below 65536 from the base-prime bitmap.
Above that, narrow inverse tests reject the same 128 trial factors, and a
survivor below 733² is prime. Larger survivors take the signed seeded base-two
round. The complete list of 2256 base-two strong pseudoprimes below 2³² coprime
to 105 corrects that round, with a fingerprint bitmap avoiding almost every
binary search. This correction is what makes the narrow test exact.

Trial division folds four balanced 16-bit digits onto one 32-bit value per
prime and uses an inverse test. The AVX-512 and AVX2 paths test sixteen and
eight primes per vector; narrower and scalar fallbacks remain. Against wide
products, the folding measured 8–19% faster per call on AVX2 and no slower on
AVX-512. The depth through 727 follows the round's cost: a vector of `k` primes
is useful while `p < k·R/c`, where `R` is the round's cost and `c` the vector's.
Factorization strips the first sixteen odd trial primes, through 59, with the
same table.

The multiplication chains share `ScaledResidueRing64`. Below 2^63 the
base-two round carries signed residues and absorbs seven leading exponent
bits into a seed. Its square-and-double step branches on no exponent bit and
needs no fold. Above 2^63 its squares carry a borrow mask, while products of
the set bits form a separate chain. The Lucas test uses a norm-one trace
ladder, selecting with Gray-code masks and carrying no power of Q. The twelve
witness bases and their exact failure bound used by `BigIntegerFunctions`
remain separate from this finite-domain decision.

## The kernel classes

`PrimeKernels` owns the shared base-prime table, primality rounds, narrow
window marker and deterministic factor splitter. `PrimeKernelGuard` checks
instruction-set support at the trial-division shells. `ScaledResidueRing64`
owns the modular reductions. Their exact arithmetic and forced-rung laws
remain; the laws for the deleted counting, selection and sieve engine do not.

The bitmap stays shared with narrow primality; the base-prime table also
serves `RealQuadraticField`. Factorization serves both `PrimeExtensions` and
`UnsignedNumberFunctions`. Those second users make these primitives live
independently of the exploration engine.

## Useful research conclusions

The deleted research does not justify a second production decision path.
These conclusions still guide work on the retained kernels:

- Decide a single integer directly. A segmented sieve amortizes setup across
  many answers; a global prime rank needs an exact count, which can dominate
  the request. Neither cost belongs in the language's primality decision.
- A uniform prime sampler must reject composite candidates drawn uniformly
  from its candidate set. Walking to the next prime weights each answer by
  its preceding gap. The removed sampler's speculative batching also had to
  restore the generator to the state immediately after the winning draw.
- Floating-point estimates can choose work or propose a quotient only when
  integer bounds and corrections determine the answer. The removed Gourdon
  counter's reciprocal estimates were followed by exact integer correction.
- On .NET 11, `Math.BigMul` keeps both product halves in registers. A REDC uses
  one widening multiply, one truncated multiply and one high multiply;
  reintroducing the fourth multiply measured slower on primality.
- The JIT can branch a ternary inside a loop. The exact Lucas ladder's final
  subtraction uses a non-inlined conditional-select leaf; it measured 16%
  faster in isolation. Fully unrolling that ladder moved 924 values through
  the stack for only 3% improvement; a ternary in its loop lost 63%.
- A branching trace ladder, an `Int128` signed product whose low word crosses
  memory, and a borrowed base-two walk below 2^63 measured slower than the
  retained signed path. Revisit these only with new runtime evidence.

These timings are research evidence, not gates. Inspect generated instructions
and counted work, warm tiered code before measuring, and compare timing on a
quiet machine. The [open items](../plans/open-items.md) retain the small
hashed-witness question because it concerns production `IsPrime`, even though
wide counting and random selection are gone.
