# Algebra

The **structure tier**: runtime-chosen relations, cross-carrier proofs,
document-driven worlds. One skeleton per idea — adjoin a root, add generators,
raise the degree, double the carrier — each generic over any carrier that
supplies the operator interfaces, each reproducing the hand-written types
elsewhere in the library as special cases.

The hand-written types in [`FixedPoint/`](../FixedPoint/README.md) are the
**speed tier**; where the two meet they agree bit-for-bit, because over
`FixedQ4816` every returned component is rounded exactly once with no opt-in
flag to misuse. Why both tiers exist, with the measured evidence and the
standing retention gates, is
the retention-gate rationale (write-up retired; the gates themselves are the record) — read
it before proposing to collapse one into the other.

Every public type lives flat in `namespace Puck.Maths`. The parent
[`Puck.Maths` README](../README.md) is the library's entry point; this file is
the contract for the folder.

---

## At a glance

| Type | Kind | What it's for |
|------|------|---------------|
| [`QuadraticAlgebra<TScalar>`](#quadraticalgebratscalar) | `readonly record struct` | Adjoin one root of `x² = P·x + Q` to any carrier — the skeleton behind every two-dimensional number system here. |
| [`GeometricAlgebra` / `Multivector`](#geometricalgebra--multivector) | `readonly struct` / `struct` | The multi-generator case over `FixedQ4816`: signatures `(p, q, r)` up to four generators. |
| [`MonogenicAlgebra<TScalar>`](#monogenicalgebratscalar) | `readonly struct` | The any-degree case: adjoin one root of one monic modulus of degree `n`. |
| [`DoublingAlgebra<TInner>` / `IConjugationRing<TSelf>`](#doublingalgebratinner--iconjugationringtself) | `readonly record struct` / interface | The Cayley–Dickson ladder: each rung from ordered pairs of the rung below. |

---

## `QuadraticAlgebra<TScalar>`

The unifying skeleton behind every two-dimensional number system in this
library: adjoin one root of `x² = P·x + Q` to any carrier satisfying six
operator interfaces. `(0,−1)` is `FixedComplex`, `(0,0)` is `FixedDual`,
`(0,+1)` is `FixedSplit`, `(k,1)` is the metallic surd world, and over
`PrimeField64` it is `F_{p²}` — all verified reproductions, pinned by the
`complex.*`, `split.*`, `algebra.quadratic-surd-twin-lane` and
`prime-field.*` law families.

Carries `Conjugate`/`Norm`/`Trace`/`Discriminant`, the division-free companion
(Möbius) step on projective pairs, and `CompanionPower` — the closed-form
engine for metallic and continued-fraction sequences. The discriminant's
sign/character (negative, zero, positive; split, ramified, inert) is the one
trichotomy every specialization inherits.

Over `FixedQ4816` the one-rounding discipline is UNCONDITIONAL: every relation
fuses (integer coefficients through the Q32 integer lane, everything else
through the Q48 fractional lane; the lane is a construction-time,
value-independent classification), so `Create(0,−1)`/`Create(0,0)`/`Create(0,+1)`
reproduce `FixedComplex`/`FixedDual`/`FixedSplit` bit-for-bit over the FULL raw
range, integer relations additionally make the Möbius step exact, and degenerate
coefficients are classified once so zero `P`/`Q` terms are skipped exactly.
There is deliberately no division.

## `GeometricAlgebra` / `Multivector`

The multi-generator quadratic algebra over `FixedQ4816`: signatures `(p, q, r)`
up to four generators (≤ 16 blades), the geometric product driven by a
once-computed blade-sign table (reordering parity × signature squares;
degenerate generators annihilate).

Signature admission is overflow-safe: each non-negative public count is checked
against the remaining four-generator capacity before the total or blade count is
formed. `default(GeometricAlgebra)` intentionally denotes the scalar algebra
`Create(0,0,0)`, not an uninitialized descriptor. A `Multivector` always carries
sixteen physical lanes, but its affinity to a receiver signature is enforced:
every lane at or above that receiver's `BladeCount` must be zero, and every
semantic operation rejects a nonzero unused lane rather than silently projecting
it away. Componentwise `Multivector` addition/subtraction and equality remain
signature-independent and therefore preserve/inspect all sixteen lanes.

Frees the generator count `QuadraticAlgebra` fixes at one: the planar trio is
the one-generator case, `FixedQuaternion` is the even subalgebra of `(3,0,0)`
(bit-identical over the full raw range, mapped explicitly; the geometric product
accumulates every blade-pair product wide and rounds once per blade), and rigid
motions are the `SandwichTransform` of motors in `(3,0,1)` — reproduced against
`FixedRigidTransform` to the family's measured fixed-point envelope.
`Exponential` branches on the bivector square's sign — circular, hyperbolic, or
degenerate — the discriminant trichotomy one more time, now steering rotors,
boosts, and translators. Verified by the `presented.clifford-*` law family,
including `presented.clifford-motor-rigid-transform-twin` for the sandwich
transform.

Frozen at four generators: the 32-blade conformal world is reached through
`Presentations.Clifford(4, 1, 0, material)` in
[`Oracle/`](../Oracle/README.md).

## `MonogenicAlgebra<TScalar>`

The any-degree adjunction that frees the degree `QuadraticAlgebra` fixes at two:
adjoin one root of one monic modulus `xⁿ + m₍ₙ₋₁₎xⁿ⁻¹ + … + m₀` to any carrier
satisfying the same six operator interfaces. Degree 2 *is* `QuadraticAlgebra`;
degree `k` over the two-element carrier *is* the `BinaryField` tower — both
verified reproductions, pinned by the `algebra.monogenic-*` and
`binary-field.*` law families.

Elements are immutable power-basis coordinate vectors; `Multiply` is schoolbook
plus one division-free companion-recurrence reduction; `CompanionPower` is the
closed-form engine for order-`n` recurrences; `ProjectiveStep` is the degree-`n`
Möbius step; `Trace`/`Norm` ride the multiplication matrix (cofactor for
`n ≤ 4`, division-free characteristic-polynomial elimination beyond — chosen
over pivot-dividing elimination precisely so the two-element carrier never
stalls); `CharacteristicDiscriminant` is the resultant of the modulus and its
derivative, the degree-2 `Δ` generalized.

`default(MonogenicAlgebra<TScalar>)` names no modulus and is invalid: all of its
public properties and semantic operations throw `InvalidOperationException`
instead of exposing a degree-zero or storage-null accident. Default `Element`
and `Projective` values likewise carry no vector and deliberately refuse their
public accessors. Operand affinity is structural and receiver-directed: every
element/window consumer requires `Dimension == receiver.Degree`, rejecting
default, shorter, and longer values with a named `ArgumentException`.
Equal-dimensional coordinates created by another modulus are intentionally
accepted and interpreted under the receiving modulus; the carriers do not retain
modulus identity.

Over the house scalar the one-rounding discipline is UNCONDITIONAL — every
degree, every coefficient: integer tails run an in-cascade wide kernel,
fractional tails and high-degree norms run an exact multi-limb accumulator sized
at construction, and degree 2 reproduces the fused `QuadraticAlgebra` twin
bit-for-bit in both lanes — except for a tail the carrier cannot negate exactly
(a coefficient at raw `long.MinValue`), which builds no twin and runs the
general lanes to the same one-rounding values.

First new ground: the degree-3 world of `x³ = x + 1`, whose companion powers
count the order-3 additive sequence the way degree 2 counts the metallic ones.

## `DoublingAlgebra<TInner>` / `IConjugationRing<TSelf>`

The doubling construction: builds each rung of the division-algebra ladder from
ordered pairs of the rung below, over any conjugation ring.

Adapters absorb `FixedComplex` (floor 1) and `FixedQuaternion` (floor 2) — both
bit-identical over the FULL raw range: the floors specialize to fused leaf
kernels (2, 4, and 8 raw products per component at floors 1, 2, 3, one rounding
each), so the doubling ladder and the hand-written types are the same
arithmetic; a third wrap reaches the octonions, whose fused discipline is gated
by a shared-nothing oracle. `Commutator`/`Associator` make the price of each
floor a computed witness — commutativity dies at the quaternions, associativity
at the octonions with alternativity retained — not a comment. Verified by
`algebra.doubling-floor1-matches-fixed-complex`,
`algebra.doubling-floor2-matches-fixed-quaternion`,
`algebra.doubling-floor2-commutator-witness` and
`algebra.doubling-floor3-octonion-norm-vs-oracle`.

---

## Verifying changes

Each type's reproduction claims are law cases in the suite — `algebra.*`,
`complex.*`, `split.*`, `quaternion.*` and the `presented.*` families cover
`QuadraticAlgebra`, `GeometricAlgebra`, `MonogenicAlgebra` and
`DoublingAlgebra` respectively, each against an independent oracle sharing no
code with the subject. A change to one of these types owes the Default tier at
minimum:

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
```

Anything under `src/Puck.Maths` owes the law suite — see
[the tests README](../../../tests/Puck.Maths.Tests/README.md) for the full
tier ladder and [docs/agent-guide.md](../../../docs/agent-guide.md) for how to
verify.
