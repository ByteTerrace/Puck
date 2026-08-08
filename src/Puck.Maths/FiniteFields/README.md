# FiniteFields

This folder does exact arithmetic in finite structures. Each folder under
`Puck.Maths` is called a **wing**, and each wing's README carries the full
contracts for the types inside it; this is the wing for exact algebra.

If finite fields are new to you, here is the whole idea in a paragraph. A
**field** is a number system in which you can add, subtract, multiply, and
divide by anything except zero, and in which the laws you already trust hold:
`a·b` equals `b·a`, `(a·b)·c` equals `a·(b·c)`, and `a·(b + c)` equals
`a·b + a·c`. The rational numbers form a field, and so do the real numbers. A
**finite field** is a field with only finitely many elements in it — the
arithmetic closes up and wraps around inside a fixed set of values, the way the
hours on a clock face wrap around twelve. Finite fields always have a prime
power of elements, which is a theorem rather than a design choice, and it is why
the families here are the two-power fields `GF(2^k)` and the odd-prime fields
`F_p` with their square extensions `F_{p²}`. Every one of those fits in ordinary
machine integers, which is what makes the wing possible at all.

Exactness is the point of the whole folder. Nothing here rounds, saturates
(clamps at a boundary instead of wrapping), or approximates: a field product is
exactly associative, exactly commutative, and exactly distributive, and every
non-zero element has an exact inverse. An expression built out of these products
can therefore be regrouped freely without changing its value, which is a liberty
floating point never gives you.

Four public surfaces cover the wing.

- **Polynomials over the two-element field.** That field is written `GF(2)`: its
  only values are `0` and `1`, its addition is exclusive or, and its
  multiplication is `and`. A polynomial over it — something like `t⁵ + t² + 1` —
  is a list of one-bit coefficients, so it packs one coefficient per bit into a
  machine integer.
- **The fields of two-power order at every degree from 1 through 128** — the
  fields with `2^k` elements, written `GF(2^k)` — built over a **modulus** the
  caller chooses or a canonical one shipped here. A modulus is the polynomial
  you divide by and keep the remainder against, and it has to be
  **irreducible**, meaning it cannot be written as a product of two
  lower-degree polynomials. Irreducible is to polynomials what prime is to whole
  numbers, and it is what makes the arithmetic a field rather than something
  weaker.
- **The odd-prime field below `2⁶²`** — ordinary whole-number arithmetic modulo
  a prime `p`, where every value is a remainder in `[0, p)` — **together with
  its quadratic extension**, which adjoins a square root the base field does not
  contain.
- **The primality toolkit** that decides which moduli are admissible in the
  first place.

The determinism tier is **cross-machine bit-identical**: the same inputs return
the same bits on every machine, every operating system, and every backend. Every
value path is integer, with no wall clock, no ambient state, and no floating
point in any result. There is exactly one floating-point touch in the whole
wing, and it is worth knowing where it is: the integer square root behind the
Lucas test's square pre-check takes its first estimate from hardware floating
point, and branchless integer corrections — fixed arithmetic with no branch in
it, so the same instructions run whatever the input — settle that estimate to
the exact floor before anything reads it.

The hardware paths exist for throughput and nothing else: the carryless-multiply
instruction and the vector region rungs produce exactly the bits their portable
counterparts produce. A **rung** is one implementation of a bulk operation over
a whole region of values, and the rungs are arranged like a ladder, from the
plain scalar loop up through progressively wider vector instructions; [the
region-scaling ladder](#the-region-scaling-ladder) below walks all seven of
them. Part of the agreement between rungs is structural and part of it has to be
executed to be believed. Everything above the carryless product — reduction,
squaring, inversion, division, exponentiation — is one shared implementation, so
no tier can differ there, and every vector rung's tables and matrices are
computed through the field's own scalar multiply rather than deriving the
modulus a second time. The product itself is two independent implementations,
and each vector rung applies its tables through code the scalar rung does not
share, so agreement at that level is something the verification stage has to
execute and compare — see [Verifying changes](#verifying-changes) — rather than
something the structure supplies for free.

Three internal types carry the engine, and the public types are thin fronts over
them.

- `BinaryFieldKernels` — the free functions every binary-field operation
  resolves to.
- `BinaryFieldRegionTier` — the enum naming the bulk region rungs.
- `ScaledResidueRing64` — the Montgomery-form residue ring every chain of
  odd-characteristic multiplications runs in.

A `BinaryField<T>` is its degree and its modulus tail plus delegation, and a
`PrimeField64` is its modulus plus delegation. The three internal types are
documented below as **substrate** — the machinery underneath — rather than as
surface, because a consumer reaches them only through the public types.

The operation tables below carry the arithmetic surface only, so the plain
accessors are not repeated in them: `BinaryPolynomial`'s packed-bits
constructor, `Bits`, `Indeterminate`, `IsOne`, and `IsZero`; the `One` and
`Zero` identities on all four value types, which `BinaryPolynomial` also
declares as `MultiplicativeIdentity` and `AdditiveIdentity`; `BinaryField<T>`'s
`Degree` and `ReductionTail`; `PrimeField64.Modulus`; and the extension's
`BaseField`, `NonSquare`, and nested `Element` record.

---

## At a glance

The six public types come first, then the three internal ones that
[Substrate](#substrate) documents.

| Type | Kind | What it's for |
|---|---|---|
| `BinaryPolynomial` | `readonly record struct` | Polynomials over the two-element field, with bit `i` carrying the coefficient of `t^i` inside a `ulong` (so degree ≤ 63). It offers exact Euclidean division, a monic gcd, an irreducibility decision, a primitivity decision through degree 32, and the factorization of `tⁿ+1` for odd `n ≤ 31`. It is also the type that carries a modulus for `BinaryField<T>`. |
| `BinaryField<T>` | `readonly record struct` | The field `GF(2^k)`, formed by dividing by a fixed irreducible modulus and keeping the remainder, for `k` from 1 through the width of the **carrier** — the integer type the bits are packed into: `byte`, `ushort`, `uint`, `ulong`, or `UInt128`, so 1 through 128. Elements are bare packed integers, and the field object describes the structure they live in. Scalar arithmetic plus the bulk region primitives. |
| `BinaryFields` | `static` | The canonical minimum-weight fields at degrees 8, 16, 32, 64, and 128 — the widths the library accelerates. Every one of them runs its product on the carryless-multiply instruction; only the byte-wide and sixteen-bit ones reach a vector region rung. |
| `ReedSolomon` | `static` | Systematic Reed–Solomon coding over any `BinaryField<T>`: the generator polynomial whose roots are consecutive powers of a chosen element, the check symbols a message's division by it leaves behind, and the syndromes that read a codeword back. Generic in the carrier, span-based, and allocation-free. |
| `PrimeField64` | `readonly record struct` | The prime field `F_p` for an odd prime `p < 2⁶²`, whose elements are bare `ulong` values in `[0, p)`. Field arithmetic, the quadratic character (the test for whether a value is a square), modular square roots, a batch inversion, and the static primality surface. |
| `QuadraticExtensionField64` | `readonly record struct` | The extension `F_{p²} = F_p(√d)` over a fixed non-square `d`. An element is the pair `(A, B)`, standing for `A + B·√d`. It adds `Frobenius`, `Norm`, `Trace`, and a deterministic chooser for the smallest non-square. |
| `BinaryFieldKernels` | `internal static` | The free functions beneath `BinaryField<T>`: both carryless-multiply tiers, tail-fold reduction, the inversion chain, the irreducibility criterion, and the region ladder. Seven named tiers become ten kernels, because the sixteen-bit width has a kernel of its own at each of the three affine tiers. |
| `BinaryFieldRegionTier` | `internal enum` | Names the seven rungs of the bulk region-scaling ladder and does nothing else; dispatch lives in the kernels. |
| `ScaledResidueRing64` | `internal readonly struct` | The residue ring `Z/nZ` for an odd `n` above one — the arithmetic of remainders modulo `n` — carried in Montgomery form so that a chain of modular multiplications performs no hardware division. It requires oddness only, never primality. |

---

## `BinaryPolynomial`

An element of `GF(2)[t]`, which is the set of all polynomials in one variable
`t` whose coefficients come from the two-element field, packed into a `ulong`.
The type carries a polynomial rather than a field element, so it has no modulus,
no inverse, and no order. `Degree` is the largest exponent with a non-zero
coefficient, and it is `-1` for the zero polynomial. Addition is the
coefficient-wise exclusive or, and it is also subtraction; negation returns the
value unchanged, because every coefficient is its own additive inverse. The
declared operator interfaces are addition, subtraction, unary negation,
multiplication, division, modulus, both identities, and shifts. There are no
comparison or ordering interfaces.

| Operation | Semantics |
|---|---|
| `+` / `-` / unary `-` | Exclusive or, exclusive or, and identity. |
| `*` | The low limb — the lower 64 bits — of the exact carryless product, so coefficients above degree 63 are discarded. |
| `checked *` | The exact product, throwing `OverflowException` when the high limb is non-zero. |
| `/` / `%` / `DivRem` | Euclidean quotient and remainder, which is long division: they satisfy `(quotient * divisor) + remainder == this` exactly. A zero divisor throws `DivideByZeroException`. |
| `<<` / `>>` / `>>>` | Multiplication and division by `t^count`; `>>` and `>>>` are the same operation. |
| `GreatestCommonDivisor` | The monic gcd — leading coefficient one — computed by the Euclidean loop. When one operand is zero, the other is returned. |
| `IsIrreducible()` | Degree below one is `false`, degree one is `true`, and a zero constant term above degree one is `false` (because `t` divides it). Everything else is delegated to `BinaryField<ulong>`. |
| `IsPrimitive()` | Irreducible **and** the root generates the whole multiplicative group. A zero constant term is never primitive, `t` itself included. |
| `FactorOddCycle(cycleOrder)` | The distinct monic irreducible factors of `tⁿ+1` for an odd `n` in `[1, 31]`, ordered by degree and then by packed value. |
| `ToString()` | The conventional written form, such as `t^5+t^2+1`, and `0` for the zero polynomial. |

**Truncation.** Ordinary `*` truncates in the same way every other fixed-width
operator in the library wraps — the bits that do not fit are simply gone — and
`checked *` reports the loss instead. Both come from one carryless multiply, so
the truncating form and the reporting form cannot disagree about the product. A
product that genuinely needs its coefficients above degree 63 is a field
operation, and `BinaryField<T>` keeps that wide intermediate internally.

**Shift-count masking.** A negative count throws `ArgumentOutOfRangeException`.
A count above 63 returns zero rather than reaching the carrier's own shift,
whose count is masked to the carrier width; that shift would wrap around and
resurrect exactly the coefficients the operator promises to discard.

**Primitivity.** Primitivity is strictly stronger than irreducibility. It
additionally says that `t` has order `2^degree − 1` — that is, you must multiply
`t` by itself that many times before returning to one, rather than some proper
divisor of that count — which is what makes the polynomial the characteristic
polynomial of a maximal-period linear recurrence. Those turn up as a shift
register's direction numbers, a maximal-length sequence, and a full-period
scrambler. The decision rejects a zero constant term first — `t` divides such a
polynomial, so its root is zero in the quotient and generates nothing — then a
reducible one, and then checks that `t` raised to `(2^degree − 1) / p` is not one
for every prime `p` dividing the group order. The constant-term rule is the one
that answers `t` itself: `IsIrreducible` refuses every *other* zero-constant-term
polynomial, because they all have degree at least two, but `t` is irreducible and
would otherwise reach a quotient that is not a field. The decision therefore
needs the group order factored, and the shipped factorization is trial division,
so the degree is capped at `MaximumPrimitiveDegree` (32) and a larger degree
throws `NotSupportedException` rather than running. The prime divisors are
deduplicated into a `stackalloc` —
a small buffer on the call stack, never the heap — of nine, which is the most
distinct primes that can divide a value below `2³²`, since the product of the
first ten primes already exceeds it. A prime group order is reported by the
factor enumerator as itself, which is the only prime divisor the check then
needs; no special case exists or is needed for it.

**Allocation.** The operators, the division, and the gcd allocate nothing. Three
members do allocate: `FactorOddCycle` builds a `List<T>` and returns a sorted
array, `IsPrimitive` allocates the state machine of the prime-factor enumerator
it walks over the group order, and `ToString` allocates its builder and the
string. All three are construction-time or diagnostic work rather than a hot
path — the inner loop a simulation runs every tick.

**Diagnostics.** `ToString` is a diagnostic form, and no parsing round trip is
claimed for it.

---

## `BinaryField<T>`

The field you get by dividing `GF(2)[t]` by the fixed modulus
`t^Degree + ReductionTail` and keeping remainders. Elements are packed `T`
values reduced to a degree below `Degree`, so a region of field values is a
plain span and the field object describes the structure those values live in.
The value of a `BinaryField<T>` is exactly its degree and its tail, and two
fields are equal when both agree. The type describes a structure rather than
being a value in one, so it carries no arithmetic operators.

| Operation | Semantics |
|---|---|
| `Create(degree, reductionTail)` | The validated constructor. |
| `FromModulus(modulus)` | The same field, named instead by its whole modulus polynomial, whose leading term is stripped to form the tail. |
| `Add` | Exclusive or, which is also the difference. |
| `Multiply` / `Square` | The carryless product, reduced. Squaring runs the same kernel. |
| `SquareRoot` | The element whose square is the argument — unique under an irreducible modulus — reached by `Degree − 1` further squarings. |
| `Inverse` / `Divide` | The addition-chain inversion, and a multiply against it. |
| `Exponentiate` | Square-and-multiply over the exponent's binary expansion. |
| `Reduce` / `IsReduced` | Fold an arbitrary packed value into the field; test whether one is already reduced. |
| `IsIrreducible()` | The on-demand decision that makes the quotient ring a field. |
| `AddRegion` | Elementwise exclusive or over two regions. |
| `MultiplyAccumulateRegion` / `ScaleRegion` / `ScaleRegionInPlace` | `destination ^= scalar * source`, `destination = scalar * source`, and the same overwriting form on a single span. |

**What construction validates.** `Create` rejects an unsupported carrier
(`NotSupportedException`, since a binary field requires a fixed carrier width),
a degree below one or above the carrier's width, a tail with a zero constant
term (then `t` divides the modulus and the quotient is not a field), and a tail
with a non-zero coefficient at or above the degree. That last check is skipped
when the degree equals the carrier width, where nothing can sit above it.
`FromModulus` runs the same rules on the degree and tail it derives from the
modulus, so a modulus of degree below one — the zero and constant polynomials
included — lands in the same below-one refusal.

**What construction deliberately does not validate.** Irreducibility. The test
costs a real fraction of a millisecond at the top degrees, and callers who had
already validated their modulus would pay for it twice, so it is offered as
`IsIrreducible()` and run on demand. Nothing else is precomputed either — the
value is the degree and the tail — so constructing a field costs nothing and no
class initializer sits in front of any operation.

**What the hot path deliberately does not guard.** `Multiply`, `Square`,
`SquareRoot`, `Inverse`, `Divide`, `Exponentiate`, and the scaling region
primitives all require reduced operands. `AddRegion` is the exception, because
exclusive or is degree-independent and takes any packed value. `Inverse` and
`Divide` additionally require an irreducible modulus, as does `SquareRoot`'s
claim to a unique root. Neither precondition is enforced. `IsReduced(value)` and
`IsIrreducible()` test them, and a caller who skips both gets no diagnostic at
all: the operations run to completion and return values that are not the
field's. The two guards that do fire are `DivideByZeroException` from `Inverse`
and `Divide` on a zero operand, and the region validation described next.

**Region validation.** `AddRegion`, `MultiplyAccumulateRegion`, and
`ScaleRegion` require equal lengths (`ArgumentOutOfRangeException` otherwise)
and require the two regions to be either disjoint or exactly identical
(`ArgumentException` otherwise). What decides is the offset rather than the mere
fact of overlap, because a region operation reads and writes the same element
index: a region laid exactly on top of another is well defined, and only a
shifted overlap has no defined result. `ScaleRegionInPlace` passes one span as
both source and destination, so it needs no check at all.

**Allocation.** None, anywhere. Each nibble-split rung — a nibble is four bits —
stages its two tables in two `stackalloc`s of sixteen bytes each; the affine
rungs carry their matrices in registers, and every other kernel is table-free.

**Chain shape.** `Exponentiate` is square-and-multiply over the exponent's
binary expansion, so its operation count depends on the exponent and it is not
constant-time in it. `Inverse` is the opposite: the shape of its chain depends
only on the degree and never on the value.

---

## `BinaryFields`

The canonical minimum-weight fields at the widths the library accelerates. All
five run their product on the carryless-multiply instruction, and the region
ladder's vector rungs reach only the byte-wide and sixteen-bit ones.

| Field | Modulus |
|---|---|
| `Degree8` | `t⁸ + t⁴ + t³ + t + 1` |
| `Degree16` | `t¹⁶ + t⁵ + t³ + t + 1` |
| `Degree32` | `t³² + t⁷ + t³ + t² + 1` |
| `Degree64` | `t⁶⁴ + t⁴ + t³ + t + 1` |
| `Degree128` | `t¹²⁸ + t⁷ + t² + t + 1` |

Each modulus is the standard minimum-weight irreducible at its degree — weight
counts the non-zero terms, and fewer terms means reduction folds less back down,
completing in two passes. Swan's theorem rules out a trinomial (three terms)
whenever the degree is a multiple of eight, which every degree here is, so a
weight-five pentanomial is the floor at each one. That is a fact about the
degrees rather than a preference. `Degree64`'s tail coinciding with `Degree8`'s
is a genuine coincidence of the minimum-weight pattern at those two degrees, and
not a transcription error. `Degree128`'s modulus is the minimum-weight
irreducible in the natural, unreflected domain; the near-degree-127 reciprocal
polynomial you may have seen elsewhere — the same coefficients read backwards —
exists only to make a bit-reversed wire format work, and that is a problem this
library does not have.

Two concrete readings of `Degree8`, the `GF(2⁸)` that every byte-wide consumer
sits on. First, `0x53` and `0xCA` are multiplicative inverses there, so their
product is `1`. Second, `Exponentiate(0x03, 255)` is `1` for a structural reason
rather than a coincidental one: the multiplicative group has order
`2⁸ − 1 = 255`, so every non-zero element raised to `255` is the identity.

None of these constants was trusted on its word: the `binary-field` battery
stage re-proved each one irreducible at run time. That stage left the build on
2026-08-02, so the constants are on their word again until something replaces
it.

---

## `ReedSolomon`

Systematic Reed–Solomon coding over any `BinaryField<T>`. A code is named by
three data rather than by an object — the field, the element whose consecutive
powers are the generator's roots, and the exponent the run of roots starts at —
and everything else is a span the caller owns. Nothing here caches, locks, or
runs a class initializer, so a consumer that encodes one shape repeatedly builds
its generator once and keeps it.

| Operation | Semantics |
|---|---|
| `BuildGenerator(field, rootBase, firstRootExponent, generator)` | Writes the coefficients of `∏(t + rootBase^(firstRootExponent + i))`, highest-order first, monic. The span's length is one more than the degree. Quadratic in the degree, which is why it is a build step rather than something the encode path repeats. |
| `ComputeCheckSymbols(field, generator, message, checkSymbols)` | The remainder of the message's division by the generator. The check span's length must be the generator's degree, which is also the bound the remainder's own degree satisfies. |
| `ComputeSyndromes(field, rootBase, firstRootExponent, codeword, syndromes)` | The codeword evaluated at each root, by Horner. Every syndrome is zero exactly when the codeword is divisible by the generator. |

**The symbol order is highest-order coefficient first everywhere** — generator,
message, check symbols, and codeword — so a systematic codeword is the message
span followed by the check span with no reversal anywhere.

**The division rides the region ladder.** Each message symbol contributes one
`MultiplyAccumulateRegion` over the generator's tail, so a code whose
check-symbol count fills a vector is accelerated without this type knowing which
rung ran, and a short one falls to the scalar rung the same call. The working
buffer is stack-allocated below 512 symbols and pooled above it, and a pooled
buffer is cleared before it re-enters the shared pool; nothing reaches the
managed heap either way.

**Preconditions, documented and not enforced** — the same posture
`BinaryField<T>` takes toward irreducibility, and for the same reason. The roots
must be DISTINCT, which holds when `rootBase`'s multiplicative order exceeds the
largest root exponent used; a primitive element gives the longest code the field
admits. Every symbol must already be reduced.

**What is here and what is not.** Encoding and verification are here; LOCATING
and correcting errors — a key-equation solver, a root search, an error
evaluator — are not. Syndromes are the honest boundary: they answer "is this
codeword intact" on their own, which is the half a consumer needs before it needs
a decoder, and a decoder is a separate arc rather than an omission.

**A consumer names its own field.** `QrReedSolomon` in `Puck.World.Data`
constructs `GF(256)` under `0x11D` at its point of use, because that modulus is
ISO/IEC 18004's choice rather than a canonical minimum-weight one, and
`BinaryFields` catalogs one canonical field per accelerated width rather than one
per standard. Naming a field costs nothing, so there is no saving to chase by
hoisting it. `BinaryFields.Degree8` is the DIFFERENT degree-8 field `0x11B`; both
are irreducible pentanomials, and a code computed in one does not decode in the
other.

---

## `PrimeField64`

`F_p` for an odd prime `p` below `MaximumModulus` (`2⁶²`). Elements are bare
`ulong` values in `[0, p)`; the field object describes the structure they live
in and carries no element of its own. Two fields are equal when their moduli
agree.

The modulus bound does real work rather than being incidental: it keeps addition
and subtraction to a single conditional fold, because two representatives sum
below `2⁶³` and so never overflow the carrier.

| Operation | Semantics |
|---|---|
| `Create(modulus)` | The validated constructor. |
| `Add` / `Subtract` / `Negate` | One conditional fold each. |
| `Multiply` | Widen to `UInt128`, then reduce once. |
| `Reduce(ulong)` / `Reduce(long)` | The representative in `[0, p)`; the signed form folds negatives up by the modulus. |
| `Pow` | Square-and-multiply, run in Montgomery form. |
| `Inverse` | `value^(p − 2)`. |
| `LegendreCharacter` | `0` at zero, `1` at a non-zero square, and `-1` at a non-square, decided by the exponentiation criterion. |
| `TrySqrt` | One of the two roots — a square has two, `r` and `p − r`, so negate the result for the other. It decides the character itself and reports a non-square rather than throwing. |
| `BatchInverse` | A whole region turned over through one field inversion. |
| `IsPrime` / `IsStrongProbablePrime` / `IsStrongLucasProbablePrime` / `IsBaillieProbablePrime` | The static primality surface — see [Primality](#primality-on-ulong). |

**What construction validates.** `Create` rejects a modulus at or above
`MaximumModulus` (`ArgumentOutOfRangeException`), an even modulus, and a
composite modulus (`ArgumentException` for both). The even check is a mask
against one rather than a comparison against two, and there is history behind
that: comparing against two never fired, which let `Create(2)` through, and an
even modulus reaches arithmetic that assumes an odd one, where `TrySqrt`'s
non-residue walk does not terminate. Primality is decided exactly, so `Create`
never admits a modulus on probabilistic evidence. Nothing else is precomputed,
so construction costs only the primality test.

**What the hot path deliberately does not guard.** Every operation expects
reduced operands in `[0, p)`, and none of them checks. `Inverse` throws
`DivideByZeroException` on zero, and `BatchInverse` throws it when any element
is zero, because the shared running product is then zero and has no inverse.

**One-shot versus chain.** `Multiply` stays on the hardware divide deliberately.
`ScaledResidueRing64` wins only across a chain of multiplications, and the two
conversions a one-shot would pay cost more than the divide they replace.
Everything that is a chain rather than a single product — `Pow` and everything
built on it (`Inverse`, `LegendreCharacter`), `TrySqrt`'s descent, and every
static primality entry point — runs in the ring instead: one ring per call, each
value encoded as it enters, at most one decode where an ordinary residue has to
come back out, and no hardware division inside any chain. `Pow` is the clean
case, one value in and one out; `IsPrime` never decodes at all, because its
comparisons are made against the ring's own one and minus one. The results are
identical either way, and only the arithmetic differs.

**`TrySqrt`.** Zero roots to zero and returns `true`. The character is decided
first, so a non-square returns `false` with a zero root. When `p ≡ 3 (mod 4)`, a
square's root is the single power `value^((p + 1) / 4)`. Otherwise
`p ≡ 1 (mod 4)`, and the root comes from the nonresidue-assisted descent:
writing `p − 1 = q · 2^s` with `q` odd, the routine seeds a root of the odd part
and a `2^s`-th root of unity built from the smallest non-square, then repeatedly
squares a running residue to locate the least power of two at which it becomes
one, and corrects the root by the matching power of that root of unity. Each
correction strictly lowers that power, so the loop always halts. The character
decision, the seeding powers, and the descent's squarings all run inside one
ring. Every test the descent performs is made against the ring's own one, and
the walk to the smallest non-square against the ring's minus one; because the
representation is a bijection — a one-to-one relabelling of the residues —
either of those is the same decision as a test against the ordinary `1` or
`p − 1`.

**`BatchInverse`.** This is the running-product method: a forward pass
accumulates the partial products, one inversion turns the whole product over,
and a backward pass peels each element off that inverse. The cost is one
inversion plus about three multiplications per element, in place of the `n`
inversions a naive loop would perform.

**Allocation.** None on the managed heap. `BatchInverse` is the one operation
with scratch space: its partial-product buffer is a `stackalloc` at 512 elements
or fewer, and is rented from the shared array pool above that and returned in a
`finally`, with its written prefix cleared first so the caller-derived partial
products never re-enter the shared pool.

---

## `QuadraticExtensionField64`

`F_{p²} = F_p(√d)` over a `PrimeField64` and a fixed quadratic non-square `d` —
a value that is not the square of anything in the base field. An element is the
nested `Element` pair `(A, B)` standing for `A + B·√d`, with both parts reduced
in the base field. The extension exists precisely because `d` is a non-square:
`t² − d` is then irreducible, and its root generates a two-dimensional space over
`F_p`. Two extension fields are equal when their base fields and their
non-squares agree.

| Operation | Semantics |
|---|---|
| `Create(baseField, nonSquare)` | The validated constructor. |
| `CreateCanonical(baseField)` | The extension over `SmallestNonSquare`. |
| `SmallestNonSquare(baseField)` | The least of `2, 3, 5, …` whose quadratic character is `-1`. |
| `Add` / `Subtract` / `Negate` | Coordinate-wise in the base field. |
| `Multiply` | Schoolbook over the pair — five base-field products — with the square of the root folded back to the non-square. |
| `Inverse` | The conjugate divided by the norm, which costs one base-field inversion. |
| `Pow` | Square-and-multiply over the exponent's binary expansion. |
| `Frobenius` | The non-trivial automorphism `A + B·√d ↦ A − B·√d` — a relabelling of the field that preserves both addition and multiplication — which is the `p`-th power map. |
| `Norm` / `Trace` | `A² − d·B²` and `2A`, both landing back in the base field. |
| `FromBase` | The lift of a base-field element, with a zero root coefficient. |
| `BatchInverse` | A whole region turned over through one base-field inversion. |

The **conjugate** of `A + B·√d` is `A − B·√d`, the same element with the sign of
its root part flipped, and the **norm** is an element multiplied by its own
conjugate. The norm always lands in the base field, which is what lets an
inverse in the extension be computed from a single inversion down there.

**What construction validates.** `Create` rejects a generator that is not a
reduced base-field element — one at or above the modulus
(`ArgumentOutOfRangeException`) — and then one that is zero or a square
(`ArgumentException`), since `t² − nonSquare` then factors and the quotient is
not a field. The reduced bound is enforced rather than folded, for two reasons.
It is what makes record equality mean "the supplied reduced generator agrees",
so `d` and `d + p` cannot become two unequal descriptors of one extension. And
it is what keeps the modulus itself out: `p` reduces to zero in the residue ring
the character exponentiates in, where the resulting zero power reads as a
non-square, and admitting it would build `F_p[t]/(t²)` — the dual numbers, whose
nonzero `√d` has vanishing norm and no inverse — behind a type that promises a
field. Every factory also refuses a default-initialized `baseField`
(`ArgumentException`), so an uninitialized descriptor is named where it was
passed. `CreateCanonical` needs no generator check, because the character is
exactly what `SmallestNonSquare` searched on. That search is deterministic and
terminates quickly: non-squares are half of the non-zero residues, so the
smallest one is small for every prime, and perfect squares along the way are
skipped by the character itself.

**What the hot path does not guard.** The extension inherits the base field's
posture exactly: both coordinates are expected reduced, and nothing checks.
`Inverse` throws `DivideByZeroException` through the base field when the norm
vanishes, which for a genuine field element it cannot.

**Chain shape.** The extension's own `Pow` is a chain of extension multiplies,
each of whose base-field products stays on the divide. Montgomery form is
entered by the base field rather than by the extension: it is `BaseField.Inverse`
and `BaseField.Pow` that convert in once and out once.

**Allocation.** None on the managed heap. `BatchInverse` carries the same
threshold as the base field's: a `stackalloc` of `Element` at 512 or fewer,
pooled above that, and returned in a `finally` with its written prefix cleared
first.

---

## Substrate

These are the internal pieces the public types are built out of. You never call
them directly, but they are where the guarantees above actually come from, so
they are worth reading if you intend to change anything here.

### The dual-tier carryless multiply

A carryless multiply is an ordinary multiply with the carries removed: the
partial products are combined with exclusive or instead of addition, which is
what polynomial multiplication over `GF(2)` needs.

`CarrylessMultiply64` dispatches to `CarrylessMultiply64Hardware` when the
carryless-multiply instruction set is available, and to
`CarrylessMultiply64Portable` otherwise. Both are named separately, and the
portable one queries no instruction-set support, so a verifier can execute both
tiers over the same inputs inside one process and compare them. Reaching the
hardware kernel without support throws `PlatformNotSupportedException`, which
only a tier verifier can arrange.

The hardware kernel states its limb-to-lane correspondence in source rather than
inheriting it from struct layout — a lane is one slot of a vector register.
Operands go in through `Vector128.CreateScalar`, limbs come back out through
explicit element reads, and the control byte selects the low half of both
operands, which is the only pairing the scalar code above it ever asks for.

The portable kernel assembles the 128-bit product from four 32-bit carryless
products in the schoolbook arrangement, and it is table-free, branch-free,
allocation-free, and constant-time. The 32-bit comb underneath it is where the
width bound comes from: comb spacing is four bits and a 32-bit comb holds at
most eight set bits, so a slot accumulates at most eight and can never carry
into the next slot. With no carry, each slot's low bit is exactly the XOR-parity
the carryless product wants, and that is what turns ordinary integer multiplies
into carryless ones. The same construction on full 64-bit operands would let a
slot reach sixteen, carry, and silently corrupt the neighbouring parity bit.

`CarrylessMultiplyWide<T>` widens that to each carrier. The narrow carriers route
through the 64-bit product and split it. `UInt128` uses four independent 64-bit
carryless products rather than the three a recursive split would need, because
the four fill the multiply latency with instruction-level parallelism and keep
the combining XOR chain short, whereas the recursive split trades a shorter
instruction count for a longer chain. An unsupported carrier throws
`NotSupportedException` from an out-of-line helper, which keeps the inlined
multiply body lean.

### Tail-fold reduction

`ReduceWide` reduces a two-limb product modulo `t^degree + tail`. Because
`t^degree` is congruent to the tail, the part of the value at or above
`t^degree` is multiplied by the tail and folded back down; the tail's degree is
strictly below the field's, so the folded part's degree strictly decreases and
the loop always halts. Nothing is precomputed, so there is no constant that
could be derived differently on two paths, and the iteration count depends only
on the operand values rather than on which multiplication tier produced them. It
is written as a loop rather than as the two passes the canonical minimum-weight
moduli happen to need, because a dense caller-supplied modulus needs more than
two and a fixed unroll would be silently wrong for exactly that case.

Two shift-masking hazards are handled explicitly. The low mask is built by
right-shifting an all-ones value rather than as `(one << degree) - one`, whose
shift count at `degree == width` is masked back to zero and would yield a mask
of one. The split at the degree separates `degree == width` outright, where both
shift counts reach the carrier's width and would otherwise return the value
unshifted. The upward shift in the general case cannot lose a coefficient,
because a product of two reduced elements has degree at most `2·degree − 2`, so
its high limb has degree at most `degree − 2` and stays inside the carrier.

### The addition-chain inversion

`Inverse` is the Itoh–Tsujii Frobenius addition chain — a fixed recipe of
squarings and multiplications that reaches a high power in few steps.
`value^(2^degree − 2)` is assembled from the doubling identity
`a^(2^2i − 1) = (a^(2^i − 1))^(2^i) · a^(2^i − 1)`, walked over the binary
expansion of `degree − 1`, with one further Frobenius step at the end. The
chain's shape therefore depends only on the degree and never on the value. That
is shape rather than timing: every multiply in the chain ends in the tail fold,
whose iteration count depends on the operand values, so the routine is not
constant-time. It replaces roughly half of a naive Fermat exponentiation's
general multiplies with repeated squarings; on the hardware tier a squaring is
itself a carryless multiply, so the saving is a factor near two rather than the
order of magnitude a "squarings are free" framing would suggest. Degree one
returns one, and zero throws `DivideByZeroException`.

### The on-demand irreducibility decision

`IsIrreducible` is the Ben-Or/Rabin criterion, in the stronger form that tests
every exponent through half the degree rather than only the quotients by the
degree's prime divisors. It is construction-time validation and never a hot
path, which is why `BinaryField<T>.Create` leaves it to the caller to run.

Its gcd step is where the stored-tail representation shows through. The modulus
needs one bit more than the carrier holds, so only Euclid's first step can touch
it. That step is taken by shifting a running remainder up to `t^degree` and
adding the tail's own remainder, after which every value fits the carrier and
the ordinary Euclidean loop finishes the job. A zero value would make the
divisor the modulus itself, whose degree is at least one, so the result is
certainly not one; reporting zero says that without ever materializing the value
the carrier cannot hold.

### The region-scaling ladder

There are seven rungs, named by `BinaryFieldRegionTier`: `Scalar`, `Split128`,
`Affine128`, `Split256`, `Affine256`, `Split512`, and `Affine512`. The enum
ascends narrowest-first, and within a width it puts the nibble-split byte
shuffle before the hardware Galois-field affine transform. Dispatch runs in the
reverse order — widest-first, and affine before split — because region
throughput is dominated by vector width, while both kernel families cost one or
two vector operations per vector either way. The enum only names the rungs;
dispatch lives in the kernels, where ten of them implement the seven tiers.

Byte-wide elements reach all seven rungs. Sixteen-bit elements reach the three
affine tiers — through three kernels of their own, which borrow the tiers only
to ask the support question — and the scalar loop. There is deliberately no
nibble-split rung at that width: a sixteen-bit product would need four tables of
sixteen sixteen-bit entries, which costs more setup than the four matrices the
affine rungs need and buys nothing the affine rungs do not already give. Every
other carrier and degree runs the scalar loop, which is the reference rung: it
queries no instruction-set support, it runs at every carrier and degree, and it
is what every vector rung is compared against.

- **Intrinsics are deliberately unguarded inside the kernels.** Each rung is a
  separately named kernel that queries no instruction-set support of its own and
  documents `PlatformNotSupportedException` as the consequence of being called
  where its instruction set is absent. Support-gating is the caller's job,
  through `IsRegionTierSupported`, and that is what lets a verifier holding the
  matching support flag execute any two rungs over the same region inside one
  process and compare them.
- **Every width is an independent processor-feature leaf.** Support for the
  256-bit Galois-field affine instruction implies neither the 128-bit nor the
  512-bit form, so each rung asks its own question: an affine rung asks the
  Galois-field leaf at its width, and a split rung the byte-shuffle leaf at its
  width. None of them asks the generic wide-vector support query, which reports
  true everywhere because the wide vector types are emulated in narrower chunks
  when the hardware is absent; that query would select a rung whose intrinsics
  are not on the machine. The hardware-acceleration query is the right one only
  where a loop is written entirely in the cross-platform vector API, which here
  means `AddRegion` and nothing else.
- **Every sub-vector remainder is finished by the scalar loop**, rather than by
  a masked store, which would be one more kernel to prove for the sake of the
  last few bytes of a region measured in kilobytes.
- **The amortization constants are throughput tuning rather than correctness
  bounds.** Every rung produces the same bytes at every length, so moving a
  threshold can only change how fast the answer arrives. A nibble-split rung is
  preferred from four whole vectors up, because building the two sixteen-entry
  tables costs about thirty-two scalar field multiplies — one per entry — which a
  shorter region never earns back. The affine rungs carry thresholds for the same
  reason and, more importantly, so that **widest-first ranks only rungs that can
  actually run**: a matrix is one field multiply per input bit, eight in all at
  the byte width and thirty-two at the sixteen-bit width, and a rung whose vector
  loop cannot complete a single iteration pays every one of those and then hands
  the whole region back to the scalar loop. Unthresholded, a thirty-byte region on
  a machine carrying the 512-bit transform built a 512-bit matrix, vectorized
  nothing, and ran the scalar loop anyway; a hundred-byte region vectorized
  sixty-four and left thirty-six to the scalar tail when a narrower rung would
  have covered all hundred. The byte rungs are preferred from two whole vectors up
  and the sixteen-bit rungs from four, and short-region throughput improved by
  between 1.4× and 15× across the affected lengths when those thresholds landed on
  2026-08-05, with lengths above them provably unaffected because they select the
  same rung as before.
- **Tables and matrices are computed through the field's own multiply**, so a
  vector rung inherits the scalar rung's correctness rather than deriving the
  modulus a second time. Only the packing is subtle: the transform reads the row
  governing output bit `row` from byte `7 - row` of the matrix qword, so rows
  are packed from the top of the qword downwards, and writing them in the
  obvious order instead produces a byte-reversed matrix that still runs and
  still looks plausible.
- **The wide affine rungs split the 16×16 bit matrix into four 8×8 blocks**,
  since the transform is defined over bytes. Rotating every element by eight
  bits presents the opposite half in each byte lane, so four transforms and a
  lane-parity blend cover all four pieces. The lane assignment rests on the
  processor being little-endian — storing the least significant byte first —
  which it is on every platform this library targets, and that assumption is
  stated at the mask rather than left implicit.
- **Accumulation is applied as a mask rather than as a branch.** A zero mask
  turns accumulation into a plain store, and the destination is loaded either
  way, so each vector rung keeps one branch-free loop body instead of two nearly
  identical ones. The scalar reference rung is the exception: it branches once
  on the flag and runs one of two loops.

`AddRegion` sits outside the ladder entirely. Addition in characteristic two —
where adding any element to itself gives zero — is the exclusive or at every
degree, so region addition is one degree-independent byte-wise loop rather than
one implementation per carrier, and its vector width is chosen by hardware
acceleration alone, because the result is the same at every width. Its byte
count and every region loop's cursor are pointer-width, so a region of two
gibibytes or more — reachable as an ordinary managed array of any carrier — is
processed in full rather than wrapping a 32-bit count.

### The Montgomery-form residue ring

`ScaledResidueRing64` represents a residue `a` by `a · R mod n`, where the radix
`R` is `2⁶⁴`. In that representation a product reduces by REDC — two widening
multiplies, one truncated multiply against the modulus inverse, two additions,
and one conditional subtraction — instead of by the 128-by-64 divide that a
direct `(a * b) % n` costs. The saving belongs to the chain rather than to any
one product: `Encode` and `Decode` each spend a REDC of their own, so a lone
product is cheaper left on the divide. The pattern is to convert once on the way
in, stay in the ring for the whole chain, and convert back once at the end. The
additive operations — `Add`, `Subtract`, and `Halve` — are linear in the
representation, so they apply to Montgomery-form elements unchanged, and a
recurrence that mixes them with products never has to leave the ring.

Construction computes three constants and repeats none of them per operation:
the radix reduced, the radix squared reduced, and the negated 2-adic inverse of
the modulus (its inverse modulo a power of two). Only the first two are
reductions and only they cost a division; the inverse comes from a
division-free Newton–Hensel iteration whose step count is fixed by the carrier
width. The radix is one above a value the carrier can hold, so it is reduced as
`R − 1` and lifted afterwards; an odd modulus never divides the radix, so the
reduced value is never zero and the lift cannot carry out of range.

**Branchless-mask discipline.** Every conditional fold is a mask rather than a
branch, and each one is stated against the case where the untruncated value no
longer fits the carrier. `Add` detects the wrap rather than assuming it away
above `2⁶³`, and folding a wrapped sum lands on the right value anyway because
the radix vanishes modulo the carrier. `Multiply`'s true quotient is 65 bits
wide — the sum, plus the radix when that addition wrapped — and is below twice
the modulus either way, so one conditional subtraction lands it in range and a
wrapped difference is already exact. `Subtract` adds the modulus back under a
borrow mask, for the same reason. `Halve` folds the odd lift into the shifted
half rather than adding the modulus and then shifting, which is what keeps the
whole operation inside the carrier for a modulus above `2⁶³`, and it writes
`(Modulus >> 1) + 1` for `(Modulus + 1) / 2` so that the largest odd modulus
does not overflow it either. The widening multiply is `UInt128` multiplication
rather than the framework's big-multiply helper, because the JIT expands it
inline and beats the helper.

**A measured exponentiation choice.** `Power` is square-and-multiply over the
exponent's binary expansion, **least significant bit first**. The
most-significant-bit-first walk that would make the per-bit multiply
unconditional turns out to be slower here, and that is measured rather than
assumed: its branchless select lands inside the squaring dependency chain, which
is the critical path. It is recorded as a measurement rather than re-derived
from first principles.

**Why oddness alone.** The ring requires only an odd modulus greater than one,
and nothing in it presumes the modulus prime. That is exactly what admits it as
the arithmetic of a primality test on a candidate not yet decided, rather than
only of one already settled. The precondition is not enforced, and neither is
`Multiply`'s exactness range: the reduction is exact whenever the product of the
operands stays below `2⁶⁴ · Modulus`, which two reduced operands always satisfy,
as does one arbitrary operand against a reduced one. Elements are bare `ulong`
values in `[0, Modulus)`, so the ring object describes the representation and
carries no element of its own, which is the same convention `PrimeField64`
follows.

---

## Primality on `ulong`

`PrimeField64.IsPrime` is the exact decision for every 64-bit unsigned value. It
runs strong-probable-prime rounds against the fixed twelve-base witness set
`2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37`. A **witness** is a base you test
the candidate against, and a set of them is **complete** below some bound when
no composite under that bound survives all of them. This set is proven complete
for every value strictly below `318665857834031151167461` (about `3.18 × 10²³`
— the exact threshold, quoted rather than rounded, because rounding it up would
place the one counterexample inside the promise), which is four orders of
magnitude past `ulong.MaxValue` and far past this field's `2⁶²` ceiling, so the
decision is deterministic rather than probabilistic. The even candidates are settled before
the rounds begin, so the survivors are odd and one `ScaledResidueRing64` carries
every round's squaring chain: the twelve rounds share one ring, the chains
themselves spend no hardware division, and the only division per witness is the
remainder that reduces it into the field. The ring is a bijective re-encoding of
the residues, so comparing a power against the ring's own one and minus one
decides exactly what comparing the ordinary residues against `1` and `value − 1`
would. A witness that reduces to zero is skipped.

Three **probable**-prime tests sit beside it, named as such and contracted as
such. A probable-prime test can prove a value composite, but passing it is not a
proof of primality; a composite that passes one anyway is called a
**pseudoprime** for that test.

`IsStrongProbablePrime(value, witness)` is one round. Writing
`value − 1 = d · 2^s` with `d` odd, it accepts when `witness^d` is one, or when
`witness^(d · 2^r)` is minus one for some `r` below `s` — the two ways a prime
modulus allows the square roots of one. A failed round proves compositeness,
while a passed one proves nothing. A base that reduces to zero carries no
evidence and passes.

`IsStrongLucasProbablePrime(value)` is the strong Lucas test with Selfridge's
Method A parameters. It works from a Lucas sequence, which is a two-term
recurrence whose terms `U` and `V` play the part that powers of a base play in
a Fermat round.
Every prime passes it, and so do infinitely many composites — the strong Lucas
pseudoprimes, of which `5459` is the smallest — so its worth lies not in its own
strength but in the independence of its failures from a Fermat round's.

- **Parameter search.** `D` is the first of `5, −7, 9, −11, 13, …` whose Jacobi
  symbol over the value is `-1`; then `P = 1` and `Q = (1 − D) / 4`. The Jacobi
  symbol is a `+1`, `-1`, or `0` valued function of a value against an odd
  modulus, and it generalizes the question "is this a square?" to moduli that
  may not be prime. Every candidate is congruent to one modulo four, which is
  what makes `Q` an integer and what fixes its sign — `Q` is positive exactly
  when `D` is negative — so the sign is read off the candidate's magnitude
  rather than tracked separately. The candidates step by four within each sign,
  so they sweep every residue class modulo an odd value, and every non-square
  has a class whose symbol is `-1`; the search therefore reaches one.
- **The square pre-check bounds the cost; it is not what guarantees
  termination.** A perfect square has no `D` of symbol `-1` at all, but the
  search still ends there, on the vanishing symbol, because the candidates sweep
  every odd magnitude and so meet a factor the square shares. Without the
  integer square root ahead of the search, a square costs Jacobi evaluations
  proportional to its least prime factor, which for the square of a large prime
  is the whole search. The root is exact, and a square above one is composite
  anyway.
- **The vanishing symbol.** A symbol of `0` ends the search in general: the
  candidate and the value share a factor, which is a proper divisor of the value
  — so the value is composite — unless the value divides the candidate outright,
  which leaves the search uninformed and is why the composite verdict is
  conditioned on a non-zero magnitude residue.
- **The ladder.** With `value + 1 = d · 2^s` and `d` odd, the test accepts when
  `U_d` vanishes modulo the value, or when `V_(d·2^r)` does for some `r` below
  `s`. The terms come from the doubling ladder `U_2k = U_k · V_k` and
  `V_2k = V_k² − 2·Q^k`, followed, where the exponent's bit is set, by the
  index-incrementing pair `U_(2k+1) = (U_2k + V_2k) / 2` and
  `V_(2k+1) = (D·U_2k + V_2k) / 2`, walked most-significant-bit first over `d`
  with `Q^k` squared alongside it. That is logarithmic in the value, where the
  recurrence's own definition would be linear. Both increment formulas halve,
  which modulo an odd value is a multiplication by `(value + 1) / 2`.
- **One ring, first term to last.** The ladder's additions, subtractions, and
  halvings are linear in the representation and so apply to Montgomery-form
  elements unchanged, its products are the ring's own, and zero represents zero
  — so the acceptance tests read exactly as they would on ordinary residues, and
  the ladder spends no hardware division. The order `value + 1` is split in
  `UInt128` because the carrier cannot hold it at its own maximum; the narrow
  split is in fact indistinguishable there, but the widened split states the
  decomposition rather than resting on that coincidence.

`IsBaillieProbablePrime(value)` is one base-two round of the strong
probable-prime test composed with the strong Lucas test. Both halves are
probable-prime tests, and so is the composition: passing is not a proof of
primality at any size. What the composition buys is that the two halves fail on
unrelated composites. One reads the order of a residue in the multiplicative
group; the other reads a recurrence in the quadratic extension that the value's
own Jacobi symbol selects, and the parameter search deliberately picks the
extension in which the value would be inert if it were prime — that is, the
extension in which a prime would stay prime. A composite would therefore have to
be exceptional in two unrelated ways at once, and no such composite is known at
any size. The cheaper half runs first: it costs one exponentiation and rejects
all but a vanishing fraction of composites, so the ladder is reached rarely.

Below `2⁶⁴` the composition is not merely unrefuted but verified
counterexample-free, and that region is exactly `ulong`, so nothing is
extrapolated. The complete set of base-two Fermat pseudoprimes below `2⁶⁴` was
enumerated exhaustively and independently by Feitsma and by Galway — the strong
ones are a derived subset of it — and no member of that subset is simultaneously
a strong Lucas pseudoprime to these parameters. That guarantee rests on a
third-party exhaustive computation, which puts it in the same epistemic class as
the `318665857834031151167461` bound the twelve-base witness set rests on:
Sorenson and Webster's computed value of the twelfth strong-pseudoprime
threshold, quoted exactly rather than rounded. The verification is for Selfridge
Method A with the strong Lucas test, which is exactly what this implementation
runs.

`prime-field.baillie-psw-exhaustive` adds one more comparison, and it is careful
about what that comparison buys. It runs the composition against
`PrimeExtensions.IsPrime(uint)` — the exhaustive 32-bit decision — at **every**
32-bit value and finds perfect agreement. Subject and oracle share the base-two
round, so what the agreement establishes is that the strong Lucas half rejects
exactly the base-two strong pseudoprimes below `2³²`. That is a real gate on the
Lucas half; it is not an independent-witness-set comparison, and it is stated as
the former.

`IsPrime` remains the exact decision, and it is the oracle this composition is
measured against. Whether to re-point it at the composition is a separate
decision, and it has not been taken.

---

## Cross-type couplings

- **`BinaryPolynomial.IsIrreducible` delegates into `BinaryField<ulong>`.** A
  non-constant polynomial with a non-zero constant term defines a quotient ring
  that is a field exactly when it is irreducible, so the decision is taken by
  `BinaryField<ulong>.FromModulus(this).IsIrreducible()` rather than derived a
  second time in the polynomial type. `IsPrimitive` builds the same field and
  runs `Reduce` and `Exponentiate` on it.
- **`BinaryField<T>` delegates into `BinaryFieldKernels`.** Every arithmetic
  member is a call through to the kernels with the field's degree and tail; the
  field itself holds only the XOR addition, `IsReduced`, and the region
  validation. `IsIrreducible` delegates the same way the arithmetic does.
- **`PrimeField64` constructs a fresh `ScaledResidueRing64` per chain-shaped
  call.** `Pow`, `TrySqrt`, and each static primality entry point build their own
  ring and discard it; nothing is cached on the field, which stays exactly its
  modulus. The ring's two construction divisions and its 2-adic inverse are the
  price of entering a chain, and they are why the one-shot `Multiply` does not
  enter one.
- **`QuadraticExtensionField64` wraps `PrimeField64` by value** and routes every
  coordinate operation into it. Its `Create` decides admissibility through the
  base field's `LegendreCharacter`, its `Inverse` is one base-field inversion
  against the norm, and its `BatchInverse` turns a whole region over through
  that single base-field inversion.
- **`BinaryFields` is built from the field factory and does not take its own
  constants on trust.** Each entry is a `BinaryField<T>.Create` call at static
  initialization, which validates the tail's shape but deliberately not its
  irreducibility; the catalog expects an external gate to re-prove each modulus
  irreducible at run time.
- **The Sampling wing consumes this one.** The digital-net direction-number
  builder — digital nets are point sets that spread evenly by construction —
  requires a primitive generator polynomial, and the shipped plane's generator
  is `t + 1`, the smallest there is. `BinaryPolynomial.IsPrimitive` is the
  decision that certifies one. See
  [`../Sampling/README.md`](../Sampling/README.md).
- **Root-level helpers this wing leans on.** The prime-factor enumerator
  supplies the group-order factorization behind `IsPrimitive`; the machine-word
  Jacobi symbol drives Method A's discriminant search; the modular inverse of an
  odd value, negated, is the factor the ring's REDC folds its low half away
  with; and the floor integer square root is the Lucas test's square pre-check.
- **Cyclic-incidence analysis consumes the factorizer.** `FactorOddCycle` is
  bounded to odd orders of at most 31 on purpose: larger systems supply
  already-known factors of `tⁿ+1` to the analysis, which validates them
  independently.

---

## Load-bearing invariants

These are the facts the rest of the wing is built on. Each one is here because
something else quietly depends on it, so they are the things to check first when
a change misbehaves.

- **The modulus is stored as its tail.** A degree-`k` modulus needs `k + 1` bits
  and would not fit the element carrier at the largest degree each carrier
  supports, so the leading `t^Degree` term is implicit. The consequence reaches
  the API: `FromModulus` takes a `BinaryPolynomial`, whose packed carrier tops
  out at degree 63, so the degree-64 through degree-128 fields — including the
  catalog's own `Degree64` and `Degree128` — can only be built through `Create`.
  The same fact is why the irreducibility gcd can touch the modulus only in
  Euclid's first step.
- **Construction is free.** A `BinaryField<T>` is exactly its degree and its
  tail: nothing is precomputed, and no class initializer sits in front of any
  operation. A `PrimeField64` is exactly its modulus, so its construction costs
  only the primality test.
- **The degree-8 catalog field is the field the hardware Galois-field multiply
  is defined over**, which is why the published byte-field test vectors pin it
  directly. The affine transform the rungs actually run carries no field of its
  own — it is a bit matrix over the two-element field — and that is what lets an
  affine rung serve any degree and tail the caller supplies.
- **Squaring has no separate kernel on any tier.** It is the Frobenius map and
  it is additive — the square of a sum is the sum of the squares — and the square
  root is `Degree − 1` further squarings, since under an irreducible modulus
  squaring is a bijection in characteristic two and every element therefore has
  exactly one root.
- **`ToString` on a polynomial is diagnostic.** No parsing round trip is claimed.
- **Primitivity is strictly stronger than irreducibility** and is capped at
  degree 32, because it needs the group order `2^degree − 1` factored and the
  shipped factorization is trial division. Above the cap the decision throws
  rather than degrading.
- **Odd-cycle factorization is bounded to odd orders of at most 31**, and the
  bound is on the automatic trial factorization rather than on what may be
  analyzed.
- **A default descriptor refuses use rather than computing.** `BinaryField<T>`,
  `PrimeField64`, and `QuadraticExtensionField64` are record structs whose
  validating constructors are private, so `default(…)`, an unassigned array
  element, and a deserializer can all produce a value that names no field: degree
  zero, modulus zero, generator zero. Every member that performs or asserts field
  arithmetic — the identities `One` and `Zero` included — throws
  `InvalidOperationException` there, uniformly across the three types and
  including an empty `BatchInverse`, because the descriptor is read before the
  span is. The one policy replaces what each type used to do on its own: plausible
  carryless answers from a degree-zero binary field, unreduced integer arithmetic
  from `Add`/`Subtract`/`Negate` on a zero-modulus prime field, and an incidental
  divide by zero from the members that happened to reduce. The alternative —
  encoding the all-zero backing state as a valid smallest field — was rejected
  because it makes `default` a silent lie about which field a caller is in, and
  because a wrong field is the failure this wing is least able to detect
  downstream. The data readers `Degree`, `ReductionTail`, `Modulus`, `NonSquare`,
  and `BaseField` are deliberately outside the policy and report the uninitialized
  state as it stands, so a default value stays printable, comparable, and
  inspectable in a debugger. `ToString` prints exactly those data readers — each
  type hand-writes its `PrintMembers`, because the compiler-synthesized body
  walks every public readable property, guarded identities included, and would
  throw on the default value the promise covers. It is the same posture
  `FixedRateAccumulator` takes toward an unbound time base.
- **Zero has no inverse anywhere.** `BinaryField.Inverse` and `Divide`,
  `PrimeField64.Inverse` and `BatchInverse`, and the extension's `Inverse` by way
  of the norm all throw `DivideByZeroException` rather than returning a value.
- **`PrimeField64`'s `2⁶²` ceiling is what keeps addition and subtraction a
  single conditional fold.** It is a representation invariant rather than a
  policy.
- **The residue ring needs only an odd modulus above one**, which is what lets
  primality testing run the machinery on candidates not yet proven prime.
- **A region operation accepts an exactly identical destination and source** —
  that case is well defined, and it is what in-place scaling rides on — and
  refuses only a shifted overlap. Both region refusals, the shifted overlap and
  the length mismatch, name `source`: a region operation writes what it was told
  to write and reads what it was given, so the supplied region is the one
  described as wrong.
- **A refusal names a parameter the caller supplied.** `FromModulus` derives its
  degree and its tail from the polynomial it was handed, so both derived failures
  are reported against `modulus` rather than against `Create`'s `degree` and
  `reductionTail`, which appear in no signature the caller can see. The two
  factories share one validation body, in one order, and differ only in the names
  they report.

---

## Verifying changes

**"Bit-identical to the fallback" is something this wing measures rather than
asserts — and as of the 2026-08-02 quarantine, nothing measures it.** The
`binary-field` battery stage that did left the build, and no replacement has
been built. Read what follows as the shape of the check that is owed, not as a
check you can run.

It executed every region rung against the scalar reference rung on the same
inputs — **including** on machines that support the fast paths, which is
possible only because the portable kernels are named separately and query no
instruction-set support of their own — and then relaunched itself as a child
process with each instruction set suppressed in turn, requiring an identical
result digest from every relaunch. A rung that agreed only where its
instructions were absent would fail the first half; a rung whose selection
depended on a support query rather than on the values would fail the second. The
same stage was where the catalog's distrust of its own constants was discharged:
it called `IsIrreducible()` on all five `BinaryFields` moduli, so no published
constant was taken on its word.

What DID survive is this wing's value-level oracles, which run in the law suite.
`binary-field.product-and-reduction-vs-oracle` runs `GF(2^k)` at degrees 8,
16, 32 and 64 against both `BinaryFields` and a schoolbook `BigInteger`
oracle. `presented.binary-field-wide-degrees-twin` and
`binary-field.wide-degree-irreducibility-certificates` carry the wide degrees,
and the every-32-bit-value comparison described above is
`prime-field.baillie-psw-exhaustive`, whose declared legs say in so many words
what it does and does not prove.
The `prime-field.*` and `extension-field.*` law families are the
odd-characteristic envelope: `PrimeField64` and `QuadraticExtensionField64`
respectively, both against `BigInteger` modular arithmetic.

**What a change here means.** A deliberate correction to any value path is
expected to move state hashes and recorded replays, and those are re-recorded
in the same change rather than preserved. The substrate splits what a change
reaches: a correction inside the shared implementation above the carryless
product moves every tier at once, while a change confined to one region rung
moves nothing a caller can observe, because every rung produces the same bytes
at every length.
