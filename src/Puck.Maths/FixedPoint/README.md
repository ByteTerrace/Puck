# FixedPoint

This folder holds the numbers a simulation is allowed to keep. If a value gets
advanced by a tick, compared, hashed into a state hash, or replayed later, it
is carried by a type from here: a signed and an unsigned Q48.16 scalar, a
signed Q16.48 scalar that leans the opposite way — range traded for
resolution — and a signed Q32.32 scalar splitting the two evenly, three
fraction types that live on the unit interval, two- and three-component
vectors, the three planar number systems (complex, dual, and split),
quaternions and the rigid transforms built on them, a hierarchical world
position, two exact-tick rate accumulators, and the scalar-field seam a
gravity or contact consumer reads a direction from.

*Fixed point* is the idea underneath all of it. Where `float` and `double`
store a fraction plus an exponent that slides the point around, a fixed-point
type stores one ordinary integer and agrees in advance where the point sits.
`Q48.16` is the shorthand for that agreement, and the Q-notation is worth
learning because it turns up everywhere below: the number before the dot is
how many bits carry the whole-number part, and the number after it is how many
carry the fraction. Q48.16 is therefore 48 bits of integer and 16 bits of
fraction packed into one 64-bit machine word. When you see a single number —
Q16, Q32, Q60, Q61, Q62 — it is counting fraction bits only, and the integer
bits are whatever is left over in the word being used.

There is no floating point in any result here. Every operation is integer
arithmetic on the stored bits, so identical inputs produce identical bits on
every machine and every backend. That is why this wing exists — each folder
under `Puck.Maths` is a wing of the library, and this one holds the value
types the determinism contract is written in.

`FixedQ4816` is the carrier that the rest of the folder is built out of. Two
words recur constantly, so here they are up front: the **raw** is the plain
integer actually stored, and the **carrier** is the type doing the storing —
`long` for this one — and, by extension, the scalar type every composite is
made from. `FixedQ4816`'s raw is the real number it represents multiplied by
`2¹⁶`, and every composite type in this folder is a tuple of those raws: a
vector is two or three of them, a quaternion four, a rigid transform eight.

Addition, subtraction, negation, and scaling or dividing by a scalar really
are composed componentwise out of the scalar operators, and a few members are
scalar programs end to end (`FixedQuaternion.Slerp`, and `FixedComplex`'s
narrow divide lane). **The products are not composed that way.** A fused
product widens every leaf product — each individual multiplication inside the
expression — to `Int128`, accumulates the complete expression exactly, and
rounds **once per returned component**. That is the fused one-rounding
discipline, and its single narrowing kernel is `FixedQ4816.RoundProductSum`.
(*Kernel* is used throughout for the small routine at the centre of an
operation that does the actual numeric work.) Rounding twice where the
contract says once really does produce a different value at some operands, and
the test suite has *laws* — named, registered statements that must hold across
every input the suite throws at them — that say so.

**Ties go to even, and the exceptions are named.** Most results do not land
exactly on a representable value, so they are rounded to the nearest one that
is. Occasionally a result lands exactly halfway between two neighbours; that
is a **tie**, and this folder breaks ties by taking the neighbour whose last
bit is zero — the even one. It is the same default IEEE-754 floating point
uses, and it keeps long runs of arithmetic from drifting the way "always round
up" would. The multiply, the divide, `Round`, `FromDouble`, `Parse`, every
fused narrowing, and every normalizer round to nearest with ties to even.

Three places round otherwise, and each says so at the member. `Exp2` rounds
its final half-ULP tie **up** — *ULP* stands for *unit in the last place*, the
gap between one representable value and the next, which for Q48.16 is `2⁻¹⁶`
— which is why `Exp2(−17)` answers `Epsilon` rather than `Zero`. `Log2` and
`Atan2` narrow their Q61 intermediates with a half-up shift. `SinCos` narrows
Q60 → Q16 with ties toward `+∞` before clamping to `±1`. All three are
internal narrowings inside a kernel that is already correctly rounded to
within an ULP; they are not a second public rounding discipline.

**Wrapping is the default; saturation and refusal are named where they
happen.** Three things can happen when a result will not fit the type that has
to hold it. It can **wrap**, rolling past the top of the range back to the
bottom the way a car odometer does — that is what plain machine integer
arithmetic does, and what the bare operators here do. It can **saturate**,
stopping at the largest (or smallest) representable value and staying there.
Or the operation can **refuse**, throwing an exception instead of answering.
The bare arithmetic operators follow unchecked integer semantics; their
`checked` forms throw `OverflowException` *after* the operation's rounding;
and the explicit `AddSaturating` / `SubtractSaturating` helpers clamp.

Saturation is the exception rather than the rule, and it is neither confined
to the norms nor always paired with a `Try…` sibling. (A **norm** is a size
measurement — how long a vector is, how big a rotation is — and it is normally
non-negative.) The norms are paired: `Length`, `Magnitude` and their squared
forms answer `MaxValue` when the true result will not fit, and each has a
`Try…` sibling that reports the boundary instead. The unpaired saturators are
bare members with no `Try…` form at all — the unit fractions' `operator /`,
`Exp2` at exponents of 47 and above, `Pow` at a zero base with a negative
exponent, the hyperbolic pair behind `FixedSplit.FromRapidity` (cosh and sinh
saturate to `MaxValue` once the true value leaves the carrier, the sine
carrying its argument's sign), and every `FromDouble`. And `FixedSplit.Norm`
is the counter-case
that keeps the reading honest: a member called a norm whose form is
*indefinite* (it can come out negative), so there is no non-negative boundary
to clamp against and it wraps instead of saturating. The [load-bearing
invariants](#load-bearing-invariants) below enumerate all of them.

**What crosses out of the contract.** `(double)`, `ToVector3`,
`ToQuaternion`, `ToComplex`, and `FixedPosition.ToRenderRelative` convert
values *out* of the determinism contract, for the renderer and for
diagnostics. Nothing they return may flow back into simulation state. These
crossing points are what the rest of Puck calls **presentation seams** — a
seam being a boundary where a value passes from one world into another. The
`double`-taking direction (`FromDouble`, `FromDoubleChecked`, and the generic
`TryConvertFrom*` overloads) is a boundary too, but a deterministic one:
`double.Round(value·2^F, ToEven)` is correctly rounded under IEEE-754, so it
lands on the same result everywhere.

The folder is an organizational unit and nothing more — every public type here
lives flat in `namespace Puck.Maths`. The parent [`Puck.Maths`
README](../README.md) is the library's entry point; this file is the contract
for the folder.

---

## At a glance

Everything in the folder and what each one is for. The sections that follow
give each type its full contract.

| Type | Kind | What it's for |
|------|------|---------------|
| `FixedQ4816` | `readonly record struct` | The signed Q48.16 scalar, and the carrier every other type here is built from. It is a `long` in two's complement — the standard signed-integer layout, in which the highest bit carries a negative weight — with 48 integer bits including that sign bit and 16 fraction bits. It offers the complete `INumber<T>` and `ISignedNumber<T>` surface plus `IPowerFunctions<T>`, and the transcendentals `Sqrt`, `Atan2`, `Sin`/`Cos`/`SinCos`, `Log2`, `Exp2` and `Pow`, all computed with integer arithmetic only. |
| `UFixedQ4816` | `readonly record struct` | The unsigned UQ48.16 companion, range `[0, 2⁴⁸)`. Its top bit is an ordinary magnitude bit rather than a sign. It offers `INumber<T>` and `IUnsignedNumber<T>` plus the bitwise and shift operator families, and the two truncating pairs `DivideUnchecked` / `MultiplyUnchecked`, each of which can hand the remainder back. |
| `FixedQ1648` | `readonly record struct` | The signed Q16.48 scalar — the same 64-bit carrier split the other way, with 16 integer bits including the sign and 48 fraction bits, so its range is about `±32768` and its resolution `2⁻⁴⁸`. That trade suits a quantity whose useful values sit close to zero but span many decades of magnitude — a reciprocal such as an inverse mass or an inverse inertia entry is a motivating example: it spans roughly ten decades across plausible bodies, and Q48.16's `2⁻¹⁶` floor rounds the small end to zero — a five-unit block's inverse inertia is `0.126` raw there, which is to say nothing at all, leaving the body infinitely resistant to torque. It offers `INumber<T>` and `ISignedNumber<T>` but no transcendentals, and converts to and from `FixedQ4816` with the narrowing rounded once and the widening range-gated. |
| `FixedQ3232` | `readonly record struct` | The signed Q32.32 scalar — the same 64-bit carrier split evenly, with 32 integer bits including the sign and 32 fraction bits, so its range is about `±2,147,483,648` and its resolution `2⁻³²`. It is the balanced point between `FixedQ4816`'s range-leaning Q48.16 split and `FixedQ1648`'s resolution-leaning Q16.48 split, suited to a quantity that needs both meaningfully wide range and finer-than-Q48.16 resolution at once. It offers `INumber<T>` and `ISignedNumber<T>` but no transcendentals, and converts to and from `FixedQ4816` with the narrowing rounded once and the widening range-gated. |
| `UnitFraction16` | `readonly record struct` | UQ0.16 — a real number in `[0, 1)` in sixteen bits. The interval is *half-open*: zero is included, one is not. Every bit is fractional, so there is no representable one and therefore no `One`, no `MultiplicativeIdentity` and no `++`. Multiplication is *closed*, meaning the product of two values in the range is always back in the range, so it cannot overflow. |
| `UnitFraction32` | `readonly record struct` | UQ0.32 — the same half-open contract at a resolution of `2⁻³²`, stored in a `uint`. This is the grid the samplers draw on. |
| `UnitInterval32` | `readonly record struct` | The **closed** interval `[0, 1]` — one included this time — on that same `2⁻³²` grid, stored in a `ulong` under a single invariant: `Value ≤ 2³²`. The thirty-third bit buys a multiplicative identity, exact absorbing elements at both ends (an absorbing element swallows whatever it meets, the way zero times anything is zero), and closure of `Multiply`. There are no arithmetic operators at all; every combining operation is a named method. |
| `FixedVector2` | `readonly record struct` | Two `FixedQ4816` components. `Dot` and `Wedge` — the signed area of the parallelogram the two vectors span, which is the winding test — accumulate wide and round once. |
| `FixedVector3` | `readonly record struct` | Three components, with `Dot`, `Cross`, `Lerp`, a scale-free `Normalize`, and saturating `Length` / `LengthSquared` alongside `Try…` siblings. This is the world-space displacement type. |
| `FixedComplex` | `readonly record struct` | The deterministic planar **rotation**, built on `i² = −1`. `FromAngle` is the 2D exponential map, `*` composes turns, `Rotate` applies one, and `Argument` is the logarithm. Division is full-range with exact rounding. |
| `FixedDual` | `static` | The factory and derivative-lift surface for the dual construction: `Constant`, `Variable`, `Divide`, and the lifted `Log2`, `SinCos` and `Sqrt`. |
| `FixedDual<TValue>` | `readonly record struct` | The dual construction `a + b·ε`, where `ε² = 0`, over any carrier that supplies six operator interfaces. Over `FixedQ4816` it is a *quantized* — that is, rounded onto the fixed-point grid — forward-mode sensitivity; over `FixedQuaternion` it is the dual quaternion beneath `FixedRigidTransform`. Both house carriers get a fused kernel selected by a type test the JIT folds to a constant. |
| `FixedSplit` | `readonly record struct` | The planar **scaling** primitive, built on `j² = +1`. Multiplication composes squeezes, `FromRapidity` is the split exponential map, and the quadratic form `Norm = u² − v²` is indefinite — the ring has zero divisors, so division is unit-checked. |
| `FixedQuaternion` | `readonly record struct` | Deterministic 3D rotation. A quaternion is a four-number way of writing an orientation in space, and this is the type the folder rotates things with: fused Hamilton `*` and `Rotate`, `FromAxisAngle`, `FromTo`, `Slerp`, `Exp`/`Log` between unit rotations and the half-angle bivector, an exact-denominator `Inverse`, and a scale-free `Normalize`. |
| `FixedRigidTransform` | `readonly record struct` | Rotation plus translation carried as one unit dual quaternion — literally a `FixedDual<FixedQuaternion>`. Raw composition by `*`, `ComposeNormalized` for long chains, `TransformPoint`, `Exp`/`Log` to and from the generating screw, and `ScLerp`. |
| `FixedPosition` | `readonly record struct` | The hierarchical world position: three signed 64-bit cell indices plus a centred `FixedVector3` offset, where a cell spans `2²⁰` world units. This is the floating-origin coordinate — position + displacement → position, and position − position → displacement, both exactly. |
| `FixedRateAccumulator` | `struct` | Exact-tick integration of a Q48.16 per-second rate. The part of the division too small to represent is kept as a remainder across calls, so a constant rate advances by exactly one unit after `ticksPerSecond` one-tick steps. That remainder is authoritative simulation state. |
| `FixedVector3RateAccumulator` | `struct` | Three independent axes of the same integration under one shared time base, bound once. Four readers, four selective resets. |
| `SecondOrderDynamics` (with `SecondOrderStep`, `SecondOrderState`/`SecondOrderState3`, `SecondOrderSample`) | `readonly record struct` | A pole-matched second-order response — `Create(f, ζ, r)` derives the coefficients from an authored frequency, damping ratio, and initial response; `Compile`+`Step` advance per tick/frame, `Evaluate`+`Retarget` read a closed form from initial conditions. Q32 authoritative state; a `MathF` float twin lives in `Puck.SdfVm.Views` for presentation-only followers. |
| `FixedVectorMath` | `internal static` | **Substrate.** The scale-free normalizers and norm helpers that every direction and length operation in the folder routes through: the common power-of-two preconditioner, the exact sums of squares, the restoring per-component division (restoring division is schoolbook long division, one bit at a time), and the `Try…` boundary reports. |
| `FusedArithmetic` (with `LimbBig`) | `public static` | The public refusing faces provide one-rounding mixed-scale products, three-lane dot products, scaled reciprocals, and the generalized `TryDivideMagnitudeRounded` divider. Their sign-plus-`UInt128` accumulation and wrapping siblings remain internal substrate. `LimbBig`, sharing the file, remains the internal exact signed multi-limb accumulator serving `Algebra/MonogenicAlgebra`'s higher-degree lanes. |
| `FixedSymmetricSolve` | `public static` | Scale-free 2×2/3×3 symmetric apply, solve, and invert for the effective-mass matrices a rigid-body solver uses. `TryApplySymmetric3`, `TryApplySymmetric2` and `TryInvertSymmetric2` are public; the 2×2/3×3 solve kernels and `TryInvertSymmetric3` stay internal until a consumer needs them. Raw-`long` operands may use any shared caller scale; each output rounds exactly once and every refusing call clears its outputs. |
| `FixedMassProperties` | `public static` | Volume, mass and centroidal inertia for the solid primitives (sphere, box, capsule bodies; all four volumes), the parallel-axis transfer, compound accumulation, and mass/inertia inversion. `TrySphereBody`, `TryBoxBody`, `TryCapsuleBody`, `TryTranslateInertia`, `TryInvertMass` and `TryInvertInertia` are public — the construction path a rigid body needs from a collider; the four `Try*Volume` overloads, `TryCylinderBody`/`TryCylinderVolume` and `TryCompound` stay internal until a consumer needs volume alone, a cylinder collider, or a compound body. |
| `IWorldQuery` (with `RayHit`, `WorldQueryConfidence`, `QueryCapabilities`) | `interface` | The five geometric verbs over a world — raycast, sphere cast, overlap, ground height, line of sight — each answer tagged `Exact` or `Bounded` so a caller knows whether it read a live field or a quantized bake. Declared here for the same reason as `IFieldEvaluator`: it names no representation. |
| `IFieldEvaluator` (with `FieldEvaluatorCapabilities`) | `interface` | A scalar field and its gradient over `FixedPosition`, read as `TryDistance` (signed: negative inside geometry) and `TryFieldGradient` (unit-length, pointing away from the nearest surface). It names no representation, so a field's producer and its gravity, contact or wind consumers can sit in sibling libraries that never reference each other; a consumer wanting "down" computes `-gradient.Normalize()`. |
| `FixedPointRounding` | `public static` | The shared nearest-result decision for integer kernels: compare the exact distance to the truncated result with the exact distance to its next neighbour, then resolve an equal-distance tie toward the even raw. `TryRoundRational` applies that decision to a whole exact `BigInteger` rational — the scale shift folded onto the numerator, one division, one rounding, refusing rather than wrapping — and is where both the mass-property chain here and Physics's softness chain round, so simulation subsystems cannot drift onto different tie rules. |
| `SignedFixedPointArithmetic` | `internal static` | **Substrate.** The common signed-raw division, fused interpolation, and magnitude selection for Q48.16, Q32.32 and Q16.48. The binary-point count is an input where the operation depends on it; the x64 division fast path, `UInt128` fallback, tie comparison, sign application, checked narrowing, and shared generic-math tie rules each live once. |
| `FixedPointText` | `internal static` | **Substrate.** Exact decimal parsing and rendering shared by all six formattable carriers. Rendering is always allocation-free. Parsing is allocation-free too, in `UInt128`, for every carrier at or below thirty-seven fraction bits — `FixedQ4816`, `UFixedQ4816`, `UnitFraction16`, `UnitFraction32` and `FixedQ3232` all sit under that today. Only `FixedQ1648`'s Q16.48 crosses it: a format reads `F + 1` decimal digits, and forty-nine of them no longer fit `UInt128`, so its accumulation and rounding alone route through `BigInteger` (and therefore allocate) — a strict generalization of the narrow path that changes no result where both could run. The platform parser validates the culture syntax and supplies only the sign; the original digits are then quantized directly, so an arbitrarily long run of digits sitting on a midpoint cannot get rounded twice. On the rendering side it owns the format-specifier check and terminating fraction digits for every carrier, plus the raw prefix, exact length check, and culture-token splicing shared by the four Q formats as an unsigned magnitude plus a sign flag. |
| `FixedPointConvert` | `internal static` | **Substrate.** The single `INumberBase<T>` conversion body for all three signed Q formats, including their signed/unsigned or cross-width peer seams, plus the recognized-source predicates and exact scaling steps. A known BCL numeric is expressed at the target scale with no range clamp before the requested checked, saturating or truncating policy is applied. Decimal sources are read from their own bits and rounded once; Q16.48 and Q32.32 use the wide `BigInteger` lane their fraction counts require, while Q48.16 stays on `Int128`. |

## Choosing a scalar

The composites follow from their components, so the only real decision you
have to make is which scalar a quantity is.

- **A value with an integer part** — positions, sizes, accumulated advances.
  Reach for `FixedQ4816` when the value can go negative and `UFixedQ4816` when
  it cannot. The unsigned range is `[0, ~2.8×10¹⁴)`, and the resolution on
  both is `2⁻¹⁶ ≈ 1.5×10⁻⁵`. The signed one is the authoritative simulation
  scalar and the component of every composite in this folder; the unsigned one
  is there for when you want that top bit working as an ordinary magnitude bit
  instead of a sign.
- **A pure fraction in `[0, 1)`** — normalized coordinates, blend factors,
  sub-pixel offsets. `UnitFraction16` gives you `2⁻¹⁶` resolution and
  `UnitFraction32` gives you `2⁻³²`. There is **no representable `1.0`** in
  either — hence no `One`, no `MultiplicativeIdentity` and no `++` — and
  multiplication is closed over the range and cannot overflow, while addition,
  division and left shift can all leave it.
- **The same grid plus the point `1`** — probabilities, memberships,
  certainties, and weights that have to be able to say "all the way". That is
  `UnitInterval32`. Reach for it whenever a value needs an identity or a
  saturation target rather than an open upper bound. `FromUnitFraction32`
  carries a sampler draw across exactly, and `TryToUnitFraction32` carries it
  back whenever it is still below one; storage is 64 bits for 33 bits of
  value, which is what naming `1` at all costs you. It is also the carrier of
  three materials of the presented charged algebra — a *material* is a
  swappable arithmetic pack (a commutative semiring: one operation to combine,
  another to chain) that the [`Oracle/`](../Oracle/README.md) wing's solvers
  run on. Because of that, a *quiver* over it — a quiver being a directed
  graph, objects joined by arrows — answers "most probable route", "widest
  bottleneck" and "route within a total shortfall of one" with no kernel code
  of its own.

---

## `FixedQ4816`

A 64-bit two's-complement value whose low sixteen bits are the fraction.
`Value` is the real number it represents scaled by `2¹⁶`, and `FromRawBits` /
`Value` are the reinterpretation boundary — the one place a stored integer is
read out or built back into a value — that every other type in the folder
uses.

**Determinism tier.** Cross-machine bit-identical, transcendentals included.
For a raw below `2⁴⁸` the scaled radicand `raw·2¹⁶` (the radicand is the
number under the square-root sign) fits in 64 bits, and `Sqrt` is the exact
integer square root. Above that, the value seeds from a `double` square root
and then *settles to the exact integer floor* through two integer loops, so
nothing about the seed can leak into the answer. The table-and-polynomial
kernels are integer throughout.

**Rounding.** `*` and `/` round to nearest with ties to even. The multiply
takes the exact `Int128` product, rounds the magnitude, and re-applies the
sign; that is legitimate because the two integer neighbours share parity, so
rounding the magnitude cannot pick a different one than rounding the signed
value would. The divide takes the magnitude quotient at 128-bit width, using
the x64 `DivRem` instruction when the quotient provably fits 64 bits (that is,
when the dividend's high word is below the divisor) and `UInt128` arithmetic
otherwise, so every platform wraps identically instead of the instruction
faulting. Its rounding compare is written as `r` versus `d − r` rather than
`2r` versus `d`, which cannot overflow.

**Wrap versus refusal.** `+`, `-`, `*`, `/`, `++`, `--` and unary `-` wrap,
and each has a `checked` form that throws `OverflowException` after the
rounding. `%` returns the raw remainder carrying the sign of the dividend, and
it short-circuits a divisor of `±1` straight to `Zero` — every integer divides
exactly by one, and that short-circuit is what keeps `MinValue % -1` from
raising the CLR's signed-division overflow instead of answering. A zero
divisor still throws `DivideByZeroException`.

**Refusals.** `Abs(MinValue)` and `CopySign(MinValue, non-negative)` throw
`OverflowException`, because the positive magnitude `2⁶³` is not
representable. `Clamp` with an inverted range throws `ArgumentException`
naming **no** parameter; that is the platform's own `Math.Clamp` diagnosis
surfacing through the forward. `Ceiling` and `Round` throw when the rounded
result would leave the carrier. `CompareTo(object)` throws `ArgumentException`
naming `obj` for a foreign type, and sorts `null` first.

**Allocation.** None. `ToString` renders into a `stackalloc` buffer and
allocates only the returned string; `TryFormat` allocates nothing at all.

| Operation | Semantics |
|---|---|
| `+` `-` `++` `--` unary `-` | Exact on the raw carrier and wrapping; the `checked` forms throw instead. |
| `*` | The `Int128` product, one ties-to-even Q16 rounding, wrapping. |
| `/` | A 128-bit magnitude quotient, one ties-to-even rounding, wrapping; a zero divisor throws. |
| `%` | The raw remainder with the dividend's sign; `±1` answers `Zero`; a zero divisor throws. |
| `Floor` / `Ceiling` / `Truncate` / `Round` / `Fractional` | A mask, a checked mask-plus-one, toward zero, ties-to-even onto a whole number, and the non-negative part above the floor. |
| `Abs` / `Sign` / `CopySign` | The magnitude, `-1`/`0`/`1`, and the magnitude carrying another value's sign. |
| `Min` / `Max` / `Clamp` | Ordinary order; `Clamp` refuses an inverted range. |
| `Lerp(from, to, amount)` | `from + (to − from)·amount` — exactly `from` at zero and exactly `to` at one, extrapolating outside `[0, 1]`, and wrapping like the operators do. |
| `Sqrt` | Exactly `⌊√(raw·2¹⁶)⌋`; a non-positive input yields `Zero`. |
| `Log2` | The integer part from the bit length, plus a 128-interval reciprocal table and a quartic (fourth-degree polynomial) residual, narrowed Q61 → Q16. The range is the closed `[−16, 47]` with both ends attained; a non-positive input yields `MinValue`. Maximum observed error 0.50 ULP. |
| `Exp2` | A 128-entry mantissa table indexed by the exponent's top seven fraction bits, plus a quartic at Q62. Saturates to `MaxValue` at exponents of 47 and above; lands exactly on `Epsilon` at −17; answers `Zero` strictly below −17. The error is half a ULP from the closing narrowing plus the mantissa's own relative error, which stays under `2⁻⁴⁴`: 0.51 ULP observed below `2²⁰`, rising to 0.82 just under `2²⁷` as the relative term catches up, and relative from there on — under roughly `2⁻⁴³`. |
| `Pow` | Whole exponents of zero and ±1 answer exactly: `One`, the base itself, and the single correctly-rounded inverse. Other whole exponents within ±32 square the base's **magnitude** (a negative one squares the correctly-rounded inverse) on the carrier, so each ladder multiply rounds once to Q16 and the accumulated error grows with the exponent's binary weight — the result is not in general the single correct rounding of the true power. Overflow on that path is decided exactly, by the ladder's own rounded magnitude leaving the carrier, so near the top of the range a power whose correctly rounded value is representable can still saturate — only within the ladder's accumulated rounding; a log-derived shortcut answers `Zero` below an exponent product of −18. Everything else goes through `Exp2(y·Log2(\|x\|))`, whose relative error grows with `\|y·log₂ x\|`. The sign is applied last, from the exponent's parity, so a **negative base is supported at every whole exponent** — `(−2)³` is `−8` — and an overflowing negative result saturates to `MinValue` rather than `MaxValue`. A negative base at a *non-whole* exponent answers `Zero`: the real power is not a real number and this carrier has no not-a-number to say so with. A zero base answers `One`, `Zero`, or `MaxValue` depending on the exponent's sign; every base answers `One` at exponent zero and itself at exponent one. `MinValue` never enters the squaring loop — its magnitude 2⁴⁷ is one raw past the carrier — because every exponent of magnitude two or more saturates or underflows anyway. |
| `Atan2(y, x)` | An octant fold, one 128-by-64 ratio division, then a per-interval cubic at Q61. The range is `(−π, π]`; both arguments zero answers `Zero`. Maximum observed 0.51 ULP. |
| `SinCos` / `Sin` / `Cos` | Turn-domain reduction by the single Q64 constant `round(2⁶⁴/2π)` — the two's-complement wrap of the 128-bit product *is* the exact mod-one-turn — then seven- and eight-term odd and even Taylor polynomials at Q60, narrowed and clamped to `±1`. Maximum observed 0.51 ULP within a few turns, and around 2 ULP at extreme magnitudes. |
| `ToString` / `TryFormat` / `Parse` / `TryParse` | The exact decimal expansion, which always terminates within sixteen fraction digits; parsing quantizes the original digits, so `Parse(x.ToString()) == x` at every raw. The parameterless overloads are invariant. The `format`-taking ones accept only an empty format or `G`/`g` and throw `FormatException` on anything else — there is only one rendering, so offering a menu of numeric formats would be a lie — while still honouring an explicit provider's `NumberDecimalSeparator` **and** its `NegativeSign`, both spliced into the invariant expansion, each of which may be several characters wide. `TryFormat` sizes itself from those widths and is all-or-nothing: a short destination returns `false` and reports zero written. |
| `T.CreateChecked` / `CreateSaturating` / `CreateTruncating` | Numeric conversion, never raw storage, and **three different operations**. Checked throws on range, NaN or infinity; saturating clamps, with NaN becoming zero; truncating reduces the scaled value **modulo the target's width** — no clamp — for *integer* sources, while a `double`/`float`/`Half` source inherits the platform's saturating floating-to-integer convention through `FromDouble` (NaN becomes zero), exactly as the BCL's own `CreateTruncating` saturates a floating source. Across the signed/unsigned peer the reduction is the low sixty-four bits verbatim, because the two share a width and a scale — spelled through a constrained type parameter, since the `Create*` members are `INumberBase` default implementations no static call on the carrier can name: with `T` bound to `FixedQ4816`, `T.CreateTruncating(UFixedQ4816.MaxValue)` is `−2⁻¹⁶`, and with `T` bound to `UFixedQ4816`, `T.CreateTruncating(FixedQ4816.FromRawBits(−1))` is the whole unsigned top. A BCL integer target gets the integer part and makes its *own* truncation decision, rather than inheriting `decimal`'s saturating one. All three quantize a fractional input to nearest, ties to even — a `decimal` source is read from its own bits and rounded once, never through a decimal multiply that could round twice. Outbound, a `float` target rounds the raw once (the integer-to-float conversion is the only lossy step; the power-of-two scale is exact), so the returned single is correctly rounded rather than double-rounded through `double`. |

`SinCos` inverts `Atan2` — `SinCos(Atan2(y, x))` recovers the unit direction —
and `Exp2` inverts `Log2`.

---

## `UFixedQ4816`

The unsigned companion, covering `[0, 2⁴⁸)` at the same `2⁻¹⁶` resolution. Its
most-significant bit is an ordinary magnitude bit and never a sign bit, so
`MinValue` **is** `Zero`. That single fact is what separates this family from
its signed sibling.

**Determinism tier.** Cross-machine bit-identical.

**Division has five entry points and one refusal.** `/` rounds ties to even
and wraps; `checked /` rounds the same way and throws `OverflowException`; `%`
is the raw remainder; and the two `DivideUnchecked` overloads truncate without
a range check, one of them reporting the remainder so a caller can build its
own rounding on top. All five throw `DivideByZeroException` at a zero divisor
— *unchecked* names the missing range check, never a missing divisor check.
The truncating pair uses the same hardware-128-by-64 / `UInt128` split the
signed divide uses, so a quotient wider than 64 bits wraps identically on every
platform.

**Multiplication mirrors it.** `*` rounds ties to even and wraps, `checked *`
throws, and the two `MultiplyUnchecked` overloads truncate.

**Shifts act on the raw storage, counted modulo sixty-four.** `<<`, `>>` and
`>>>` follow the raw `ulong`'s own shift semantics: an amount of sixty-four
returns the operand unchanged, and a negative amount shifts by its masked
residue. `>>` and `>>>` coincide, because the storage is unsigned.

**Refusals.** `FromInteger` throws `ArgumentOutOfRangeException` naming
`value` above `2⁴⁸ − 1`. `Clamp` refuses an inverted range with the platform's
parameterless `ArgumentException`. `Ceiling` and `Round` are checked at the
top of the range: `Round` answers at fraction `0x7FFF` and refuses at
`0x8000`, where the tie rounds up because the integer part `2⁴⁸ − 1` is odd.
Every checked operator refuses at its named corner — `MaxValue + Epsilon`,
`Zero − Epsilon`, incrementing `MaxValue`, decrementing `Zero`, negating
`Epsilon`, `MaxValue · 2`, `MaxValue / Epsilon` — while negating `Zero`
answers `Zero`, the one value the checked negation admits.

**Text.** The same exact-expansion contract as the signed type, running up to
fifteen integer and sixteen fraction digits. Both parse surfaces speak the BCL
unsigned grammar: a leading sign is admitted, `Parse("+1")` answers one and
`"-0"` answers `Zero`. The two surfaces part company wherever rounding decides
representability, at **both** ends of the range. The default surface rejects
any text whose exact value lies outside `[0, MaxValue]` even when rounding
would land it inside — a negative magnitude and above-`MaxValue` text alike
fold into its single `FormatException` verdict. The `NumberStyles` overloads
round **first**: negative text within half an ULP of zero succeeds and answers
`Zero` (no `OverflowException` at all), text just above the top whose rounding
falls back onto `MaxValue` answers `MaxValue`, and only a rounded magnitude
that is itself out of range reports an `OverflowException`.

---

## `UnitFraction16` and `UnitFraction32`

The half-open unit fraction at two widths: `[0, 1)` as `Value / 2¹⁶` in a
`ushort`, and as `Value / 2³²` in a `uint`. Every bit is fractional. There is
no representable `1.0`, and therefore no `One`, no `MultiplicativeIdentity`
and no `++` — that absence *is* the type rather than an omission in it.

**Determinism tier.** Cross-machine bit-identical.

**Multiplication is closed and cannot overflow.** The exact product of two
raws is at most `(2^F − 1)²`; shifting it down by `F` and applying the
ties-to-even correction lands at most on `MaxValue`. That is the property the
half-open range buys you, and it is why these types exist beside
`UnitInterval32`.

**Division rounds first and saturates second.** The dividend is shifted up by
`F`, divided, corrected to nearest with ties to even, and only then clamped to
`MaxValue` — so a true quotient of `MaxValue + ½` rounds up to `2^F` and *then*
saturates. The tie branch in that correction is unreachable; the argument for
why is in [Load-bearing invariants](#load-bearing-invariants).

| Operation | Semantics |
|---|---|
| `+` / `-` / unary `-` | Wrapping addition, wrapping subtraction, and modular negation — negating any non-zero value wraps. |
| `AddSaturating` / `SubtractSaturating` | The same two operations, clamped at `MaxValue` and at zero. |
| `*` | Closed, one ties-to-even rounding, no overflow possible. |
| `/` | One ties-to-even rounding, then saturation at `MaxValue`; a zero divisor throws `DivideByZeroException`. |
| `%` | The raw remainder; a zero divisor throws. |
| `~` | The **bitwise** complement `2^F − 1 − raw`, which sits one unit away from the arithmetic complement `UnitInterval32.Complement` means. |
| `&` / `\|` / `^` | Bitwise on the raw storage. |
| `<<` / `>>` / `>>>` | Raw shifts. The carrier promotes to a thirty-two-bit word before the shift, so the count acts **modulo thirty-two** at both widths — for `UnitFraction16` the mask is 31, not the 15 its own width would suggest. An amount of thirty-two returns the operand unchanged, and a negative amount shifts by its masked residue. `>>` and `>>>` coincide, because the storage is unsigned. |
| `Min` / `Max` / `Clamp` | Ordinary order; `Clamp` refuses an inverted range with the platform's parameterless `ArgumentException`. |
| `FromDouble` | `double.Round(value·2^F, ToEven)`, then a clamp into `[0, MaxValue]`. Inputs at or above one land on `MaxValue` and never on an unrepresentable one. NaN is tested explicitly **before** the narrowing cast and answers zero — the same mechanism `UnitInterval32.FromDouble` uses — because the CLI does not specify a NaN-to-unsigned conversion, and resting on the cast would rest on one architecture's choice. |
| `FromRawBits` / `Value` | The reinterpretation boundary: read the stored integer out, or build a value from one. |
| `ToString` / `TryFormat` / `Parse` / `TryParse` | The exact dyadic decimal expansion, terminating within `F` fraction digits. **Rendering** is always invariant: `ToString(format, provider)` ignores **both** arguments by contract, where the Q48.16 carriers validate the format and honour the provider's separator — that divergence is formatting-only. **Parsing** honours the supplied provider's numeric conventions (`null` means invariant), same as the Q48.16 carriers, and refuses any literal whose exact value exceeds `MaxValue` **before** rounding — the whole one-ULP slice `(MaxValue, 1)` of in-`[0, 1)` text is rejected even where rounding would land it back on the grid, the same top-of-range rule the `UFixedQ4816` default surface applies. |
| `CompareTo(object)` | `null` sorts first, a value compares zero against itself, and a foreign type throws `ArgumentException` naming `obj`. |

**Allocation.** None beyond `ToString`'s returned string.

---

## `UnitInterval32`

The **closed** unit interval on the `2⁻³²` grid, carried in a `ulong` under
the single invariant `Value ≤ 2³²`. Containing one costs a thirty-third bit —
a binary type with `F` fraction bits needs `F + 1` bits to hold the value one
— and that bit buys a multiplicative identity, exact absorbing elements at
both ends, and closure of `Multiply` over the whole interval.

**Why this grid.** Q1.31 is coarser than the sampler grid, so every crossing
between the two would round. Q1.63 spends `2⁻⁶³` that nothing consumes and
would round twice on the way back. A denominator of `2³² − 1` poisons the
conversions and turns multiplication into a divide-and-correct. The remaining
thirty-one bits pay for identical values at every crossing, for vectorization
headroom (`32×32→64` lanes fit), and for a one-compare validity check.

**There are no arithmetic operators, and the reason is the spellings.** `Max`,
`Min` and `Complement` are exact at every raw, so exactness is not the
argument. The argument is that the operator spellings already mean something
else on the type that shares this grid: `UnitFraction32`'s `~` is the bitwise
complement, one unit away from the arithmetic `1 − x` meant here, and its `+`
and `-` wrap where `AddSaturating` and `SumExcess` clamp. A bare `*` would
silently round and a bare `+` would silently saturate. So every combining
operation is a named method that says which it does, comparison is the one
operator family with no collision, and there is no `INumber<T>`, because the
closed interval is not a ring.

| Operation | Semantics |
|---|---|
| `Create` / `TryCreate` | The invariant is checked: a raw above `2³²` throws `ArgumentOutOfRangeException`, or returns `false` with `Zero`. |
| `Multiply(x, y)` | The exact `UInt128` product narrowed by 32 with one ties-to-even rounding. Closed at both ends: `One` is a two-sided identity and `Zero` a two-sided annihilator, both exactly, because neither case rounds. |
| `Multiply(x, y, z)` | **One** rounding for the whole triple product rather than one per pair — the two are different values at some operands. Three raws reach at most `2⁹⁶`, so the exact product still fits 128 bits; four would reach `2¹²⁸` and wrap, which is why three is the ceiling. |
| `AddSaturating` | The sum of two raws is at most `2³³`, so the addition itself is exact and only the clamp at `One` loses information. |
| `SumExcess` | The exact `max(0, x + y − 1)`, branchless, and exact at every raw. |
| `Complement` | The exact `1 − x`. It is an involution — applying it twice returns the original — and it carries the endpoints onto each other. |
| `Max` / `Min` | Exact at every raw. |
| `FromUnitFraction32` / `TryToUnitFraction32` | Exact both ways: `UnitFraction32` is precisely the part of this type below `One`, so a sampler draw is already a value here and nothing has to be re-represented. The narrowing back reports `false` only at `One`. |
| `FromFixedQ4816` / `ToFixedQ4816` | Inward, widening sixteen fraction bits to thirty-two is exact, so only the clamp into `[0, 1]` loses anything. Outward, one ties-to-even rounding lands on the coarser grid, and that direction is **not** injective — every raw within half a ULP of one, which is the top `2¹⁵` of them, carries up onto the exact `1.0`. |
| `FromDouble` | Ties to even, then saturation into the interval on the *scaled* value (`2³²` is exactly representable, so both endpoints are reached exactly). NaN is tested explicitly and answers `Zero`. |
| `ToString` | The exact invariant-culture expansion, terminating within thirty-two digits; `One` renders as `"1"`. |
| `CompareTo` / `<` `<=` `>` `>=` | The only operator family offered, and exact. |

**Allocation.** None beyond `ToString`'s buffer and string.

---

## `FixedVector2` and `FixedVector3`

Two and three `FixedQ4816` components. Addition, subtraction and negation are
componentwise and exact on the raws (wrapping); scaling by a scalar and
dividing by a scalar are componentwise scalar operations, so the division is a
genuine per-component divide rounded to nearest rather than a multiply by a
rounded reciprocal, and a zero scalar throws `DivideByZeroException`.

**The products are fused.** `Dot`, `FixedVector2.Wedge` and
`FixedVector3.Cross` widen every leaf product and round once per returned
component. Each also carries a **narrow lane** — a faster code path taken when
the operands are small enough to make it safe. When every operand raw is below
the member's limit (`2³¹` for the two-component dot, the wedge and the cross,
and `2³⁰` for the three-component dot, which sums a third term) the exact
product sum fits a signed `long` and that path is taken. It is bit-identical
to the `Int128` path by construction, because the two are the same
`RoundProductSum` kernel and differ only in the width of the accumulator, so
the lane is a cost choice and never a semantic one.

`Wedge` is the signed area of the parallelogram the two vectors span, positive
when the right operand lies counterclockwise of the left. It is exactly
antisymmetric — swapping the arguments flips the sign — and it vanishes on
parallel vectors, which makes it the winding and orientation test, and the
planar restriction of `Cross`.

**Norms saturate; the `Try` siblings do not.** `LengthSquared` is the exact
raw Q32 sum of squares rounded once to Q16, and `Length` roots the exact sum
so that only the final root rounds, which is strictly better than rooting
`LengthSquared`. Both answer `MaxValue` when the non-negative result does not
fit the carrier, and `TryLengthSquared` and `TryLength` report that boundary
instead.

`FixedVector3.Normalize` is **scale-free**: one common power-of-two
preconditioner lands the largest component's magnitude at a fixed bit
position, the sum of squares is taken exactly there, and each component is
divided by the shared denominator with one ties-to-even rounding. Tiny
directions therefore do not disappear and extreme ones do not overflow. A zero
vector normalizes to `Zero`.

`ToVector3` is the single-precision presentation boundary, and never feeds
back.

---

## `FixedComplex`

The planar rotation primitive — the yaw-plane analogue of the quaternion.
`FromAngle` is the 2D exponential map `exp(i·θ)` with no half-angle, because
planar rotations compose one-sided; `*` composes turns; `Rotate` applies one
to a `FixedVector2`; and `Argument` is `Atan2`, which makes it the logarithm
inverting `FromAngle`.

**Rounding.** The product and `Rotate` accumulate each component's two leaf
products wide and round once. The product's narrow gate sits at `2³¹`, and
`Rotate`'s pairs a rotation side below `2¹⁷` against a vector side below
`2⁴⁵`, which is what lets a unit rotation carry a full-scale world vector down
the long path. Division is the exact `left·conj(right) / |right|²`, where
`conj` is the *conjugate* — the same complex number with the sign of its
imaginary part flipped. The numerators are formed as a sign plus a `UInt128`
magnitude, because a signed `Int128` sum is one bit too narrow for the
positive extreme, where `MinValue² + MinValue²` reaches exactly `2¹²⁷`, and
then one restoring division rounds each component once. A zero divisor throws
`DivideByZeroException`. A narrow fast path handles operands below `2³¹`
through the scalar divider and produces the same value.

`FromTo` is scale-free in the same sense `Normalize` is: the exact raw product
sums are shifted into a fixed magnitude window before any Q16 rounding (the
down-shift itself rounds its discarded low bits to even, far below the
result's grid), so the
angle survives inputs at any representable scale. It answers
`MultiplicativeIdentity` when either vector is zero, and the exact half turn
`(−1, 0)` for antiparallel directions, which in two dimensions is unambiguous.

`Magnitude` and `MagnitudeSquared` saturate to `MaxValue`, with `TryMagnitude`
and `TryMagnitudeSquared` beside them. `Normalize` answers
`MultiplicativeIdentity` at zero. `ToComplex` is the presentation boundary.

---

## `FixedSplit`

The hyperbolic sibling that completes the planar trio: `j² = +1`, against
`FixedComplex`'s `i² = −1` and `FixedDual`'s `ε² = 0`. Multiplication composes
**squeezes** — stretching one axis while shrinking the other; `Transform`
applies one to a `FixedVector2`; and `FromRapidity` is the split exponential
map `exp(j·φ) = cosh φ + j·sinh φ`, built on `FixedQ4816.Exp2` with the
halving folded into the exponent (`cosh φ = 2^(s−1) + 2^(−s−1)` for
`s = φ·log₂ e`, formed wide and clamped), so rapidities (the hyperbolic angle
`φ`, the squeeze's own additive measure) add under multiplication. The result
is a *unit* squeeze only over a bounded band: the norm tracks one while the
backward exponential `e^−|φ|` is comfortably representable, degrades as that
term approaches a Q16 ULP, and from raw rapidity ±726822 (`|φ| > 16·ln 2 ≈
11.09`, where the term rounds to zero) the two components collide bit-for-bit
onto the light cone — `IsUnit` false, no inverse, division throws. Past
`|φ| ≈ 33.27` both components saturate to `MaxValue`, the sine carrying the
rapidity's sign. The `split.rapidity-ladder` law pins the band, the boundary,
and the saturation rows.

**The form is indefinite on purpose.** `Norm = u² − v²` is
positive inside the light cone, zero *on* it, and negative outside. The ring
therefore has zero divisors — `(1 + j)(1 − j) = 0`, two non-zero elements
whose product vanishes — so a non-zero element need not be invertible.
`IsUnit` is the test for that, and `operator /` throws
`DivideByZeroException` with a message naming the real condition (`|u| = |v|`)
rather than merely a zero operand. Everything else follows the planar shape:
the product and `Transform` fuse two leaf products per component with one
rounding behind a `2³¹` narrow gate, `Norm` fuses its own difference of
squares behind the same gate, and `Conjugate` satisfies `s·conj(s) = (Norm, 0)`
— the inverse squeeze for a norm-*one* squeeze, minus the inverse for a
norm-minus-one unit such as `j` itself.

---

## `FixedDual` and `FixedDual<TValue>`

The dual construction `a + b·ε` with `ε² = 0`, generic over any carrier that
supplies six operator interfaces (add, subtract, multiply, negate, and both
identities). Those constraints describe *available operations* and never
algebraic laws: rounded fixed-point multiplication is not associative under
bitwise equality, and the interfaces do not claim it is.

`FixedDual` — the static class — carries the factories and the derivative
lifts. `Constant` seeds a zero dual part and `Variable` a unit one, while
`Divide`, `Log2`, `SinCos` and `Sqrt` are the lifted operations. Over
`FixedQ4816` the dual part is a **quantized formal forward-mode sensitivity**:
it follows the chain rule for the ideal operator expression, which is not the
same thing as the discrete raw-bit program's derivative, and the type says so.

**There are two fused kernels, and the choice between them is made at JIT
time.** `operator *` tests `typeof(TValue)` against `FixedQ4816` and
`FixedQuaternion`. Both comparisons fold to constants for every closed
value-type instantiation, so a third carrier never sees the fused code or its
raw casts and takes the generic three-multiply path instead.

- Over `FixedQ4816` the real part is one rounding of the raw product and the
  dual part is **one** rounding of `a·d + b·c`, bit-identical to the fused
  `(0, 0)` quadratic-algebra kernel.
- Over `FixedQuaternion` — the production dual quaternion behind
  `FixedRigidTransform` — the dual part is fused **across the boundary between
  the real and dual halves**: each output component accumulates all **eight**
  leaf products (four from the Hamilton product `a·d`, four from `b·c`, in the
  exact signed term layout `FixedQuaternion.operator *` uses) before a single
  ties-to-even rounding, rather than rounding the two Hamilton products
  separately and adding them. Two narrow gates cover it: a symmetric one at
  `2²⁹` (eight products of operands below `2^B` sum below `8·2^2B`, which fits
  a signed `long` while `B ≤ 29`) and an asymmetric one pairing a rotation
  side below `2¹⁷` with a translation side below `2⁴²`, which is what covers
  unit rotations carrying real-world translations.

`FixedDual.Divide` is fused too. Over `FixedQ4816` the dual part is evaluated
at full width as `(b·c − a·d)/c²` with one restoring division, where the
textbook quotient-rule form `(b − (a/c)·d)/c` would round the intermediate
`a/c`, push it through a product, and round again. The real part is the
carrier's own correctly-rounded division, which throws on a zero divisor
before the dual denominator is ever squared.

---

## `FixedQuaternion`

Unit quaternions for deterministic 3D rotation. The vector part is the
rotation bivector — an oriented plane, which you can read as an axis — and
`Exp`/`Log` convert between unit rotations and that half-angle-scaled bivector
form. So `Exp(axis·(θ/2))` equals `FromAxisAngle(axis, θ)`, and angular
velocity integrates as `Exp(ω·(dt/2)) * q`.

**Rounding.** `*` is the Hamilton product with all four leaf products per
component accumulated wide and rounded once, behind the narrow gate at `2³⁰`.
`Dot` fuses its four products the same way. `Rotate` runs two fused stages —
`v' = v + 2·u×(u×v + w·v)` — each rounding once per component, with a narrow
gate pairing a rotation side below `2¹⁷` against a vector side below `2⁴⁰`.
Composition rounds, so the norm drifts slowly; renormalize with `Normalize`
after long chains.

**Constructions and their poles.**

- `FromAxisAngle(axis, angle)` takes a unit axis and halves the angle through
  `SinCos`.
- `FromTo(from, to)` is the geometric-product rotor `(f̂ × t̂, 1 + f̂·t̂)`
  normalized, and normalization is what halves the full-angle rotor into the
  half-angle quaternion. Its norm is `2·cos(θ/2)`, which vanishes at a half
  turn, so within about 0.45° of antiparallel — a candidate norm below 512 raw
  — it falls back to π about a deterministic axis perpendicular to `from`,
  chosen by whichever basis vector is least aligned with it. Either input zero
  answers `Identity`.
- `Slerp` walks the shortest arc, negating the far endpoint when the dot is
  negative, and falls back to a normalized linear blend above a cosine of
  65503 raw, where the sine ratio is unstable. One `SinCos` serves both
  weights through `sin((1−t)θ)/sin θ = cos(tθ) − cos θ·sin(tθ)/sin θ`.
- `Log` answers `FixedVector3.Zero` for a vector-free quaternion. At the
  `W < 0` pole the plane is genuinely undefined, and `Zero` is the fixed-point
  "no direction" answer there, mirroring `FixedVector3.Normalize`.
  `Exp(q.Log())` recovers `q` and not `−q` — the sign survives the round trip
  — except at that pole.

**Norms and inversion.** `Length` and `LengthSquared` saturate to `MaxValue`,
with `TryLength` / `TryLengthSquared` beside them. `Inverse` is the conjugate
over the exact full-width squared norm with each component rounded once; an
inverse smaller than half a raw Q16 unit quantizes to zero, and a zero
quaternion inverts to `Identity`. When the four-square sum reaches the
`UInt128` ceiling the member answers the zero quaternion directly, and that is
the correctly rounded inverse rather than a sentinel: at any such magnitude
every exact inverse component is at most `2⁻³³` of a raw unit, so the early-out
and the full computation agree component for component, and a zero result
never encodes a failure. `Normalize` is Q16-accurate at every representable
input scale and answers `Identity` at zero.

`ToQuaternion` is the single-precision presentation boundary, and
`FromQuaternion` is the inbound one — the counterpart to
`FixedVector3.FromVector3`, so an authored rotation's rounding into the
contract is decided once here rather than per caller. It does not
renormalize: Q16 quantization moves a unit rotation off the sphere, so callers
pair it with `Normalize`.

---

## `FixedRigidTransform`

A rigid motion carried as one unit dual quaternion. The type **is** a
`FixedDual<FixedQuaternion>` in a wrapper, so composition is exactly the fused
eight-product dual-quaternion kernel described above, and the encoding is
`q + ε·½·t·q`.

**Composition is deliberately unnormalized.** `*` is the raw dual-quaternion
product. Fixed-point normalization is comparatively expensive and it changes
rounding, so restoring the unit constraints is a separate, named call:
`ComposeNormalized`, at long-chain or untrusted boundaries. `Normalize` scales
all eight components by one common ratio — a power-of-two numerator over a
64-bit denominator, so no reciprocal is quantized before it is applied — and
then enforces `real·dual = 0` by projecting out the parallel component.

**Boundaries.** Positional construction is the documented unchecked
representation boundary. `FromRotationTranslation` normalizes the rotation
first and encodes the dual part fused: the leaf products of `t·q` accumulate
exactly and the halving folds into one ties-to-even rounding at shift 17 —
rounding the Hamilton product first and halving after would round twice.
`FromDualQuaternion` throws `ArgumentException` on a zero real
quaternion, and `TryFromDualQuaternion` reports that instead and answers
`Identity`. `Normalize` (and therefore `ComposeNormalized`) answers `Identity`
for that same zero-real value, discarding the dual part — the family's
normalize-to-identity convention rather than a refusal, and the member says
so.

**`Exp` and `Log`.** `Exp(real, dual)` maps a screw — a dual bivector, which
is a rotation about an axis combined with a slide along it — to the transform
it generates, with the zero screw giving `Identity` and a rotation-free screw
giving the pure translation branch exactly. The dual part closes as
`dual·(sin θ/θ) + û·(d/2)·(cos θ − sin θ/θ)` evaluated at Q63, where every
product fits `Int128`, the small-angle `cos − sin/θ` difference keeps about
thirty fractional bits, and the half slide `d/2 = û·dual` is taken from an
**exact** `Int128` dot rather than from a Q16-quantized axis, which would
manufacture spurious slide of order `|dual|·2⁻¹⁷`. Both dual terms share one
product scale, so each component fuses into one ties-to-even rounding. `Log`
is the inverse; at the rotation-free pole it returns `(Zero, dual.vector)`,
which is `translation/2` when `W > 0` and **minus** that when `W < 0`, because
`Translation` is `2·dual·conj(real)` and conjugating a negated real flips the
sign.

**`ScLerp`** is `from * Exp(amount · Log(from⁻¹·to))` — the same identity
`Slerp` has with the quaternion `Exp` — with the shortest-path negation in
front and a normalized-linear-blend fallback above a relative-rotation cosine
of 65534 raw, where `Log`'s screw division would amplify quantization by about
`1/sin`.

**Precision.** For the representation and for composition, about `2⁻¹⁵`
relative to translation magnitude, which is the Q16 unit-quaternion norm
quantization, so sub-millimetre at ten world units. `ScLerp`'s screw path sits
outside that envelope: the `1/sin` amplification of the delta's quantized
operands reaches a measured ~2.7 mm per component at ten world units near the
blend threshold, tightening as the relative rotation grows. That band belongs
to the operands and to `Exp` — `Log`'s lanes each close in a single
`DivideProductSum` rounding, and fusing them left the measured worst case
unchanged.
`Rotation` and `Translation` read the parts back out; `TransformPoint` rotates
and then translates; `Inverse` conjugates both quaternion parts.

---

## `FixedPosition`

The floating-origin world coordinate: three signed 64-bit cell indices plus a
centred `FixedVector3` offset. A cell spans `2²⁰ = 1,048,576` world units, so
the pair reaches from astronomical down to microscopic scale — something one
flat 64-bit fixed-point coordinate cannot do, because it has to trade range
for resolution.

**Canonical by construction.** The public constructor, `WithLocal`,
`FromLocal` and `TryCreate` all carry any out-of-cell offset into the cell
indices, leaving each local component in `[−CellSize/2, CellSize/2)`. The
normalization is arithmetic-right-shift floor division by the power-of-two
cell size plus a non-negative-remainder correction, which selects the centred
representative without an overflowing half-cell bias. `Normalize()` therefore
returns `this`: the invariant already holds, and the member exists so callers
can say so idempotently — calling it again changes nothing — in `O(1)`. A
position near a cell's centre carries the same component values a flat
fixed-point vector would.

**Exactness and refusal.** Differences and render rebases are exact integer
arithmetic. `Delta` and `operator -` answer the displacement from an origin,
and throw `OverflowException` when it leaves signed Q48.16; `TryDelta` reports
that instead. `operator +` and the constructor throw `OverflowException` when
canonicalization would move a cell index outside the signed 64-bit range;
`TryTranslate` and `TryCreate` report it. Both the delta and the translate
paths take a narrow `long` route when the values provably fit and an `Int128`
route otherwise, and the two answer identically.

**The generic-math interfaces are heterogeneous on purpose.**
`IAdditionOperators<FixedPosition, FixedVector3, FixedPosition>` and
`ISubtractionOperators<FixedPosition, FixedPosition, FixedVector3>` expose
position + displacement → position and position − position → displacement
without pretending that positions form a vector space.

`ToRenderRelative(origin)` is the presentation boundary: the camera-relative
position stays small and precise whenever the camera is near, no matter how
far both of them are from the world origin.

---

## `FixedRateAccumulator` and `FixedVector3RateAccumulator`

Exact-tick integration. Each call evaluates
`(rate.Raw × elapsedTicks + remainder) / ticksPerSecond` in `Int128`: the
quotient is the Q48.16 quantity advanced during the interval, and the signed
remainder is retained for the next call. A constant rate of one unit per
second therefore advances by exactly one represented unit after
`ticksPerSecond` one-tick calls, even where no individual step can represent
the exact fraction. Do not pre-round a per-second velocity into one
fixed-update delta and then repeat it.

**The time base is bound once, at construction.** The retained remainder is a
numerator over that denominator, so re-interpreting it under a different one
would fabricate motion. Binding removes that transition from the API surface
entirely, and `Integrate` therefore takes only the rate and the tick count.

**The remainder is authoritative simulation state.** Persist both `Remainder`
(or the three axis remainders) and `TicksPerSecond` in snapshots and state
hashes, and restore them together with `FromRemainder` / `FromRemainders`.
Call `Reset` — or `ResetX` / `ResetY` / `ResetZ`, which are exactly as
selective as their names suggest — whenever the integrated quantity is
assigned, clamped, teleported, or otherwise rewritten outside the accumulator.
Keep one accumulator per independently integrated scalar or vector.

**Hold it in writable storage.** Both accumulators are mutable structs whose
`Integrate` and reset members write the receiver in place, so the value must
live in a mutable field, an array slot, or a `ref` local. A `readonly` field, a
get-only property, an `in` parameter, or a `List<T>` / `Dictionary<,>` indexer
result hands those members a compiler-inserted defensive copy — no warning, no
exception — and every advance is silently discarded: the holder's remainder
reads zero forever while the integration degrades into exactly the pre-rounded
per-step delta the opening paragraph tells you never to use. Note the split
that bites hardest: an array element is a writable reference and integrates
correctly; a `List<T>` element is a copy and does not.

**Refusals.** A non-positive `ticksPerSecond` throws
`ArgumentOutOfRangeException` naming `ticksPerSecond`, from the constructor
and from the restore. A remainder whose magnitude is not smaller than the base
throws as well: `FromRemainder` names `remainder`, and `FromRemainders` names
the axis it rejected — `xRemainder`, `yRemainder`, or `zRemainder` — checking
the three in that order and reporting the first one out of band, so a caller
restoring a snapshot reads the failing axis straight off the exception. A
default-initialized value carries denominator zero and throws
`InvalidOperationException` from `Integrate` rather than dividing by zero. A
quotient that does not fit the Q48.16 raw storage throws `OverflowException`.

The vector form is three independent axes under one shared base: an axis-only
schedule leaves the other two remainders exactly zero, and the four readers
(`XRemainder`, `YRemainder`, `ZRemainder`, `TicksPerSecond`) plus
`FromRemainders` round-trip a snapshot exactly.

---

## `SecondOrderDynamics`

A pole-matched second-order response — `y'' + 2ζω y' + ω² y = ω² x + rζω x'`,
`ω = 2πf` — for a target that should ease toward, overshoot, or anticipate a
moving value rather than snap to it. Authors declare only `f` (natural
frequency, Hz), `ζ` (damping ratio), and `r` (initial response); everything
else is derived. `Create(frequencyHz, dampingRatio, initialResponse)` derives
the closed-form coefficients once (an exact rational derivation, `FixedQ4816`
inputs, refusing a non-positive `frequencyHz` or a negative `dampingRatio` by
name) and selects the branch by damping: `Branch` is `Underdamped` (`ζ < 1`,
rings and overshoots), `CriticallyDamped` (`ζ = 1`, the fastest approach with
no overshoot), or `Overdamped` (`ζ > 1`, slower, still no overshoot). `ζ = 0`
is admitted and rings forever — there is no floor beyond non-negative.

Two evaluation forms of the same system, both exact matched-Z-transform state
transitions (never naive Euler):

- **`Compile(stepTicks, ticksPerSecond)` → `SecondOrderStep`, then
  `Step(state, target, targetVelocity)`** — the per-tick/per-frame advance,
  for simulation state that is stepped every tick (a kit's planar velocity
  follower) or a presentation follower stepped every frame (a camera boom, a
  stamped part). `SecondOrderState`/`SecondOrderState3` carry both raw Q32
  position and velocity lanes — persist both in snapshots; narrowing to Q16
  loses the sixteen guard bits that make rest exact. `r` acts through
  `targetVelocity`, held constant over the step (zero-order hold).
- **`Evaluate(initialValue, initialVelocity, target, elapsedTicks,
  ticksPerSecond)` → `SecondOrderSample`** — the closed form from initial
  conditions, computed lazily on read with no per-tick work, mirroring
  `WorldStateAdvance`'s epoch-based accumulation. `Retarget(sample, oldTarget,
  newTarget)` adds the velocity kick a piecewise-constant target change
  implies, so a rewritten target keeps the sample continuous instead of
  snapping. `r` is inert in `Evaluate` — the closed form has no history of
  target motion to react to; it only shapes `Step`'s per-interval response.

Both forms round every returned raw exactly once (ties to even); `Step`
overflow throws `OverflowException` and leaves the input state untouched. A
default-initialized `SecondOrderDynamics`/`SecondOrderStep` throws
`InvalidOperationException` from `Compile`/`Evaluate`/`Retarget` rather than
computing against unbound coefficients. A float twin
(`Puck.SdfVm.Views.SecondOrderFollower.cs`, `Puck.SdfVm` project) transcribes
the identical closed forms in `MathF` for presentation-only followers that
never feed back into the tick.

---

## Substrate

Three internal types carry the arithmetic that the public surface is a thin
front over. A consumer only ever reaches them through the public types; they
are documented here because the contracts above rest on them.

### `FixedVectorMath`

The scale-free direction and norm helpers. `DirectionShift` computes the
common power-of-two preconditioner that lands the largest raw magnitude at
bit 45, chosen so that four preconditioned squares, shifted by 32 for a Q16
norm, still fit `UInt128` (at most `2¹²⁶`) while retaining roughly 46 bits of
the source direction. `ScaleRaw` applies it, rounding a negative shift to
even. `Normalize` comes in two-, three- and four-component forms, and each
does the same four things: precondition, take the exact sum of squares, root
it at Q32 to form one shared denominator, then divide each component by that
denominator with one restoring ties-to-even rounding.

`TryCreateNormalizationScale` returns that ratio *unquantized* — an integer
power-of-two numerator over a 64-bit denominator — so a caller (the rigid
transform) can apply the identical ratio to eight components with exactly one
rounding each, rather than quantizing a reciprocal first.

`TryNormalizeWithMagnitude` produces the unit direction **and** its raw Q16
magnitude in one pass, with the magnitude spanning the full unsigned 64-bit
range, so a caller can phase-reduce a norm that exceeds the signed carrier
instead of saturating. It excludes the `2³²`-wide band just below `2⁹⁶`, where
the rounded Q16-scaled root can carry to exactly `2⁶⁴`, which a `ulong`
denominator cannot hold; above that band it roots the exact sum once and
divides the Q16-shifted components by it.

`TryMagnitude`, `TrySquaredMagnitude` and `TrySumSquares` are the boundary
reports behind every `Length` / `TryLength` pair, and `DivideBySquaredSum` is
the exact per-component division behind `FixedQuaternion.Inverse`.
`RootOfSquaredSum` exploits the fact that consecutive squares differ by
`2r + 1`, so an integer radicand is nearer `(r+1)²` exactly when its remainder
above `r²` exceeds `r`. There is no integral halfway case at all, which means
the root needs no tie rule.

### `FusedArithmetic` and `LimbBig`

The one-rounding kernels. `RawMagnitude` is the branchless sign trick, and it
maps `long.MinValue` exactly onto `2⁶³` — the asymmetry the signed carrier
forces, made explicit rather than hidden. `AddProducts` accumulates
`a·b ± c·d` as a sign plus a `UInt128` magnitude, *because* a signed `Int128`
sum is one bit too narrow at the extremes. `Product` and `SquareMagnitude` are
the one-term forms. `DivideProductSum` rounds `numerator/denominator · 2¹⁶` to
raw Q16 once, ties to even, by splitting off the integer quotient and
generating the sixteen fraction bits with overflow-safe restoring division
(the compare is `denominator − remainder`, never `2·remainder`).
`ScaleProductSum` scales a sign-magnitude value by a power of two, rounding a
negative shift to even.

`RoundQ48SumToRaw` is the Q48 → Q16 narrowing this folder **exports outward**,
rather than a boundary any type here sits on: nothing under `FixedPoint/`
calls it, and its only callers are `QuadraticAlgebra`'s fused fractional
multiply and norm (`Algebra/QuadraticAlgebra.cs`) and `FixedMaterial`'s fused
charged folds (`Oracle/MaterialOps.cs`). It is the shift-32 face of
`FixedQ4816.RoundProduct`, the one sign-magnitude ties-to-even kernel — as is
`FixedQ4816.RoundProductSum(Int128)` at shift 16 — which is why the
wrap-parity argument below covers both.

`TryDivideMagnitudeRounded` generalizes `DivideProductSumCore`'s own fixed
16-bit count to any caller-supplied non-negative fraction bit count, returning
the rounded `UInt128` magnitude unnarrowed (so the caller decides how — and
whether — to fit it into its own carrier) and refusing outright on a zero
denominator or on a shift that would overflow `UInt128` before it starts.
The public `TryMixedScaleProduct`, `TryMixedScaleDotProduct`, and
`TryScaledReciprocal` faces keep mixed-scale callers on the same exact
accumulation and one-rounding rule without exposing those sign-magnitude
building blocks.

### `FixedSymmetricSolve`

Scale-free symmetric apply, solve, and invert kernels for 2×2 and 3×3 systems,
the shapes a rigid-body solver needs when applying or inverting an
effective-mass matrix. `TryApplySymmetric3`, `TryApplySymmetric2` and
`TryInvertSymmetric2` are public; the 2×2/3×3 solve kernels and
`TryInvertSymmetric3` stay internal until a consumer needs them. The
raw-`long` API keeps the scale explicit at every call, and a refusing
operation clears all of its outputs.

Every kernel preconditions its operands by one common power-of-two shift
(mirroring `FixedVectorMath`'s own shape, but with its OWN target bit — reusing
`DirectionShift`'s bit 45 would overflow a 3×3 determinant, whose six triple
products need `3k`, not `2k`, bits of headroom), forms the determinant and the
adjugate cofactors as exact sign-plus-`UInt128` products through
`FusedArithmetic`, and rounds each returned component exactly once through
`TryDivideMagnitudeRounded`. Solve's ratio is exactly scale-invariant under any
shared preconditioning shift (the matrix's degree-`n` homogeneity and the
right-hand side's missing degree cancel exactly) whenever that shift is itself
exact — a lossless left shift, true whenever every operand's own magnitude is
already within the type's target band, which any realistic effective-mass or
velocity entry is. Invert has no right-hand side to supply the missing degree,
so it folds the shift into its own fraction-bit request and refuses outright
rather than manufacture an answer when that would go negative. Both refuse
(every `out` parameter zero) on an exactly singular matrix.

`LimbBig` shares the file but serves a different floor: it is the exact signed
multi-limb accumulator behind `Algebra/MonogenicAlgebra`'s higher-degree
lanes, and no `FusedArithmetic` kernel calls it — the widest kernel here,
`DivideProductSumCore`, runs on `UInt128` restoring division. Numbers are
sign-magnitude, with a fixed-width little-endian `ulong` span for the
magnitude and an `sbyte` for the sign, and every operation is schoolbook and
exact **only under the caller's obligation** to size the width to bound the
largest value. The failure shapes on an undersized destination differ:
`MultiplyByInt64` and `MultiplyFull` throw `IndexOutOfRangeException`, while
`AddMagnitudeInto` drops its top carry and `CopyMagnitude` and `ShiftLeft`
clamp, all silently. The magnitude operations scan for the significant limb
count so that cost tracks the actual magnitude rather than the size of the
buffer.

### `FixedPointText`

Exact decimal parsing shared by all six formattable
carriers, plus the rendering pieces they share: `ValidateGeneralFormat`
refuses any specifier but empty/`G`/`g`, and `WriteFractionDigits` emits the
point and terminating expansion of a raw fraction. The four Q formats —
signed and unsigned — route their raw prefix, exact-length check, and
provider-token splicing through `TryFormatRaw`, `TryFormat`, and
`SpliceProviderTokens`, decomposed as an unsigned magnitude plus a sign flag;
the unit carriers retain their distinct prefix and length policies. The
platform number parser
validates the culture and style syntax
and supplies **only the sign**; its rounded magnitude is never used. The
original digits are then quantized directly against the reduced denominator
`2·5^(F+1)`, so an arbitrarily long tail of digits sitting on a midpoint
cannot be rounded twice.

`Parse`'s fraction-digit accumulation and rounding branch on `F`: at or below
thirty-seven fraction bits it stays in `UInt128` and allocates nothing —
`FixedQ4816`, `UFixedQ4816`, both unit-fraction carriers, and `FixedQ3232`,
every format below `FixedQ1648`. Above it, `F + 1` decimal digits can reach `10^(F+1) − 1`,
which overflows `UInt128` (`FixedQ1648`'s forty-eight fraction bits needs
forty-nine digits, about 163 bits), so only that one carrier's accumulation
and rounding route through `BigInteger` and allocate. Both branches share one
formula and differ only in carrier width; the quotient either narrows to is
provably below `2^F`, so switching branches at the limit changes no result.

`FixedPointParseStatus` distinguishes `Invalid` from
`Overflow`, which is what lets the two Q48.16 carriers' `NumberStyles`
overloads throw `FormatException` and `OverflowException` apart while
`TryParse` returns `false` for both. Only the surfaces that route through
those overloads inherit the distinction: `FixedQ4816`'s provider-only `Parse`
forwards to its styled sibling and does, while `UFixedQ4816`'s provider-only
`Parse` and both unit-fraction parsers own their entry point and collapse the
two into `FormatException`.

A hand-built `NumberFormatInfo` can otherwise let the BCL validate one number
while the exact pass quantizes another, so the ambiguous shapes are **refused**
rather than scanned: a sign or currency token that bears a digit; a currency
symbol that aliases an enabled sign token, by equality or by prefix, since
that is what the separator-family choice keys on; an active separator that
aliases an enabled sign token, which the platform classifies by grammar
position and this scanner by string match; a currency symbol that *contains*
the active decimal separator, which the platform consumes whole while this
scanner finds the separator inside it; a decimal separator whose text begins
with parser white space under a white-space-admitting style, which the
platform consumes in its white-space phase; a separator token carrying the
exponent marker under an exponent-admitting style, which the platform reads as
an exponent where this scanner reads a split; and — under a currency-admitting
style — separator families that disagree, on any input carrying a separator,
because the platform classifies a separator by whether a currency symbol has
already been consumed and this scanner picks one family for the whole input.
Refusal there is deliberately coarser than the platform's rule; nothing is
quantized under a configuration the scanner cannot classify.

The exponent magnitude **saturates at `s.Length + F + 21`**, a bound derived
from the input rather than a fixed cap. The significand carries at most
`s.Length` digits, so an exponent at or above that bound leaves more than
twenty integer digits — an overflow whatever the true exponent was — and one
at or below its negative pushes every stored digit past the `F + 1`-digit
fraction prefix, quantizing to zero whatever the true exponent was. Both
saturated verdicts are therefore the unsaturated verdicts, which a fixed cap
cannot promise: a long enough run of leading fractional zeros compensates any
constant.

---

## Cross-type couplings

These are the connections that make the folder one thing rather than a pile of
types. Each one is a real dependency in the sources, not a resemblance.

- **`FixedQ4816` is the component currency.** Every composite here is a tuple
  of its raws: `FixedVector2`/`FixedVector3`, `FixedComplex`, `FixedSplit`,
  `FixedQuaternion`, `FixedDual<FixedQ4816>`, and — transitively —
  `FixedRigidTransform` and `FixedPosition`. A change to the carrier's
  rounding is a change to every one of them.
- **`FixedQ4816.RoundProductSum` is the shared narrowing kernel.** Both
  overloads (a `long` fast form and the full `Int128` form) are what
  `FixedComplex.operator *` and `Rotate`, `FixedSplit.operator *`, `Norm` and
  `Transform`, `FixedQuaternion.operator *`, `Dot`, `FromTo` and `Rotate`,
  `FixedVector2.Dot`/`Wedge`, `FixedVector3.Dot`/`Cross`, and both of
  `FixedDual<TValue>`'s fused kernels all call. The `Int128` overload is
  `RoundProduct`, the arbitrary-shift form, at shift 16;
  `FixedRigidTransform.Exp` reaches `RoundProduct` directly at Q62 and
  `FusedArithmetic.RoundQ48SumToRaw` at shift 32. That one
  member is why "one rounding per returned component" is a single fact rather
  than eleven parallel ones — and it is also why a *leg*, one named piece of
  evidence a law stands on, that proves it in one type proves the *kernel*
  everywhere and the *assembly* nowhere else.
- **`FusedArithmetic` sits beneath the divisions and the magnitude gates.**
  `FixedComplex.operator /`, `FixedSplit.operator /` and `FixedDual.Divide`
  all form sign-plus-magnitude numerators with `AddProducts` / `Product` and
  round through `DivideProductSum`; `FixedComplex.FromTo` scales through
  `ScaleProductSum` and `BitLength`; and every composite's narrow-lane gate is
  built from `RawMagnitude` compared against a power-of-two limit. The usual
  spelling is one bitwise-or of the operand magnitudes against one limit, but
  the shape varies with the gate: `FixedComplex.operator /` compares its four
  magnitudes separately and ands the results, and the two-limit gates
  (`FixedQuaternion.Rotate`, `FixedDual`'s quaternion kernel) combine two such
  comparisons with `&&` or `||`.
- **`FixedVectorMath` sits beneath every normalization and every norm.**
  `FixedVector3.Normalize`, `FixedComplex.Normalize` and `FromTo`,
  `FixedQuaternion.Normalize`, `FromTo`, `Exp`, `Inverse` and `VectorNorm`,
  and `FixedRigidTransform.Normalize`, `TryFromDualQuaternion` and `Exp` are
  all fronts over it. `FixedQuaternion.Exp` and `FixedRigidTransform.Exp`
  share the *same* call — `TryNormalizeWithMagnitude` — which is what makes
  the rigid exponential's real part bit-identical to the quaternion one.
- **The quaternion / rigid-transform tower.** `FixedRigidTransform` wraps
  `FixedDual<FixedQuaternion>`, whose `operator *` selects
  `MultiplyQuaternion` by a JIT-constant `typeof` test, which in turn calls
  `FixedQuaternion.operator *` for the real part and open-codes the eight-leaf
  fused sum for the dual part. Three types, one product.
- **The `UnitInterval32` bridge.** `FromUnitFraction32` and
  `TryToUnitFraction32` are exact in both directions below one, because
  `UnitFraction32` *is* the part of the closed interval below `One` on the
  same grid. `FromFixedQ4816` clamps inward with an exact widening, while
  `ToFixedQ4816` carries one ties-to-even rounding outward and is not
  injective. That asymmetry is what the crossing amounts to: a sampler draw
  comes in free, and the scalar crossing costs a rounding in one direction
  only.
- **The rate accumulators share one integrator.**
  `FixedVector3RateAccumulator.Integrate` calls
  `FixedRateAccumulator.IntegrateRaw` three times — the identical internal
  member, not a sibling copy — and `FromRemainders` validates each axis through
  the same internal band check the scalar restore uses, handing it that axis's
  own parameter name so the refusal identifies the axis. The two consume
  `FixedQ4816` and `FixedVector3` respectively, and produce the same.
- **`FixedPointText` serves six carriers.** `FixedQ4816`, `UFixedQ4816`,
  `UnitFraction16`, `UnitFraction32`, `FixedQ1648` and `FixedQ3232` each hold a
  `ParsingDenominator` built by `CreateParsingDenominator(FractionBitCount)`
  and route every parse through the one `Parse` entry point, so the "validate
  with the platform, quantize the original digits" rule has exactly one
  implementation.
- **`FixedPosition` and `FixedVector3` are the position/displacement pair.**
  The heterogeneous operators are the only way to combine them, and the
  displacement type is the ordinary vector, so a delta drops straight into the
  rest of the folder's arithmetic.

---

## Load-bearing invariants

These are the facts the sources rest on. Each one is the reason some piece of
the folder is shaped the way it is, and breaking one of them is a silent
correctness failure rather than a loud one.

**The signed minimum is asymmetric, and the asymmetry is handled rather than
avoided.** `long.MinValue`'s magnitude `2⁶³` is not representable as a
positive `FixedQ4816`, so `Abs` and `CopySign` (with a non-negative sign)
throw rather than wrap, `checked -` throws, and `MaxMagnitude`/`MinMagnitude`
carry an asymmetric corner at `(MinValue, MaxValue)` where the two magnitudes
are `2⁶³` and `2⁶³ − 1`. Underneath all that, `FusedArithmetic.RawMagnitude`
maps `MinValue` onto `2⁶³` **exactly**, because it works in `ulong`. Every
fused kernel therefore sees the true magnitude while the public surface
refuses to name it, and that split is deliberate.

**The unit-fraction division tie is unreachable — a 2-adic fact, not a
measurement.** The 2-adic valuation of a number is simply how many times 2
divides it. In `min(RTE((x·2^F)/y), M)` the dividend is `x·2^F`, so its 2-adic
valuation is at least `F`; every divisor `y < 2^F` has valuation `a ≤ F − 1`,
so `2^a` divides both the dividend and `y` and therefore divides
`remainder = dividend mod y`; but `y/2` has valuation `a − 1 < a`, so
`remainder ≠ y/2` at **no** legal operand pair, at either width. The
`equalToValue` correction in both types is dead code that must stay: it is
what makes the operation's rounding rule stated rather than accidental, and it
is why agreement with an oracle here pins the truncation, the round-up branch
and the saturation — and says nothing at all about the tie rule.

**Unchecked `Int128` accumulation is safe by a wrap-parity argument.** The
fused kernels accumulate raw Q32 products in an `Int128` that can, at the
carrier's extremes, overflow. Wrapping changes the Q32 sum by `k·2¹²⁸`, hence
the rounded Q16 result by `k·2¹¹²`, which vanishes under the public raw
operators' final 64-bit wrapping policy (`2¹¹² ≡ 0 mod 2⁶⁴`) **without
changing tie parity**. The same argument at shift 32 covers
`FusedArithmetic.RoundQ48SumToRaw`, where the wrap lands at `k·2⁹⁶`. This is
what licenses `unchecked` on eight-term quaternion sums instead of a
multi-limb accumulator, and it is also why the wide accumulators are `Int128`
and not something narrower.

**A narrow lane must be bit-identical to the wide one.** Every composite tests
its operand magnitudes against a power-of-two limit and takes a
`long`-arithmetic path when every raw is small enough for the exact product
sum to fit. Those gates are cost choices, and each is sized to its own term
count: `2³¹` for the planar products, the two-component dot, the wedge and the
cross; `2³⁰` for the three-component dot and the four-term quaternion product
and dot; `2²⁹` (or the asymmetric `2¹⁷`/`2⁴²` pair) for the eight-term fused
dual quaternion; `2¹⁷`/`2⁴⁵` for `FixedComplex.Rotate` and `2¹⁷`/`2⁴⁰` for
`FixedQuaternion.Rotate`. Moving a threshold may only change how fast the
answer arrives, never what it is — a lane that disagrees with its sibling is a
bug, and the suite has laws that sweep both.

**Saturation is an answer, and the definite norms are the ones that pair with
a `Try` sibling.** `Length`, `LengthSquared`, `Magnitude` and
`MagnitudeSquared` on the vectors, the complex and the quaternion all answer
`MaxValue` when the non-negative result does not fit, and `TryLength` /
`TryLengthSquared` / `TryMagnitude` / `TryMagnitudeSquared` report the
boundary instead. The rest saturate unpaired: the unit fractions saturate
their division at `MaxValue`; `Exp2` saturates at exponents of 47 and above;
`Pow` answers `MaxValue` for a zero base at a negative exponent, and saturates
an overflowing power WITH its sign — to `MinValue` when the mathematical
result is negative, which is what an odd power of a negative base makes
reachable. On the whole-exponent squaring path that overflow verdict is the
ladder's own: the loop saturates exactly when its rounded magnitude leaves
the carrier, so a power whose correctly rounded value is representable can
still saturate near the very top of the range, but only within the ladder's
accumulated per-step rounding — never from a log-derived estimate. The
hyperbolic pair behind `FixedSplit.FromRapidity` saturates both components to
`MaxValue` once the true cosh leaves the carrier, the sine carrying its
argument's sign. And
`FromDouble` saturates rather than wrapping the cast. `FixedSplit.Norm`
saturates at neither end, because its form `u² − v²` is indefinite — there is
no non-negative boundary to clamp to, so the single Q16 rounding wraps like
the operators. Saturation never silently substitutes for a refusal that exists
elsewhere on the same boundary.

**One rounding per returned component, and the count is part of the
contract.** `UnitInterval32.Multiply(x, y, z)` exists precisely because
nesting two pairwise multiplies rounds twice and is a *different value*;
`FixedDual`'s quaternion kernel fuses across the real/dual boundary for the
same reason; and `FixedDual.Divide` avoids the quotient-rule form because it
would round three times. Anywhere the contract says one, adding a rounding is
a behaviour change even when the ideal value has not moved.

**The closed interval costs exactly one bit, and it is spent on the point
one.** `UnitInterval32` stores `F + 1` significant bits in a 64-bit carrier
under `Value ≤ 2³²`. That single invariant is what makes `Complement` total,
`AddSaturating`'s addition exact (two raws sum to at most `2³³`),
`SumExcess`'s guarded subtraction exact, and `One` a genuine two-sided
identity. It is also why three is the ceiling on the fused product: four raws
reach `2¹²⁸`, one past `UInt128.MaxValue`, so a four-factor product would wrap
rather than saturate, and `1⁴` would read as zero.

**Normalization is scale-free by preconditioning, not by luck.** The
normalizers apply one common power-of-two shift before any Q16 rounding (the
down-shift itself rounds its discarded low bits, far below the result's
grid), so a
direction's precision is independent of its absolute scale. In
`FixedComplex.FromTo`, rounding the products to Q16 first would erase vectors
below `2⁻⁸`; in `FixedQuaternion.FromTo`, taking a rounded Q16 length-squared
would erase the tiny near-antiparallel candidate and leave a non-unit result.
Both facts are stated at the call sites they protect.

**The retained remainder is state, and the time base rides with it.** A rate
accumulator's `Remainder` is not a cache — dropping it, or restoring it under
a different `TicksPerSecond`, changes future motion. Both fields belong in
snapshots and state hashes together.

**`FixedPosition`'s invariant is established at construction, so nothing
downstream re-establishes it.** Every public entry point canonicalizes, which
is what makes `Normalize()` a no-op, `Delta` exact, and the narrow `long`
route in `TryDeltaComponent` sound: canonical locals differ by less than one
cell, so a conservative cell-difference bound cannot overflow.

---

## Verifying changes

The proof story lives in [`tests/Puck.Maths.Tests`](../../../tests/Puck.Maths.Tests/README.md)
— a declaration-first law suite where every test is an entry in
`LawRegistry.cs`, and every gate statement declares the legs — those named
pieces of evidence — that it stands on. That suite is the fine-grained gate of
record, and since the 2026-08-02 quarantine it is the ONLY gate of record: the
engine battery carried **two** coarse cross-checks beside it — both ahead of the
determinism stages, because a determinism gate on its own cannot catch an
operation that is wrong but deterministically so — and both left the build with
it. Neither has been replaced, so what they covered is covered by the law suite
or by nothing:

- `fixed-point` (A1) ran `FixedQ4816`'s arithmetic, square root,
  `Atan2`/`SinCos` and banker's rounding against a `double` reference within
  the Q48.16 resolution.
- `worldcoord3` (A2) ran `FixedPosition`'s canonical construction,
  `Delta`, and translating `operator +` — including the cross-cell paths a
  single-cell scene never exercises — against an absolute fixed-point
  reference (`cell·CellSize + local`), on each of the three axes
  independently, plus the centred-offset invariant and far-cell translation
  invariance. It was the stage a `FixedPosition` change owed; a `FixedPosition`
  change now owes the `position.*` law family below and nothing else.

**The law-id families that cover this folder.**

| Family | Covers |
|---|---|
| `scalar.*` | `FixedQ4816`: construction, the four arithmetic operators and their checked forms, order and magnitude selection, the text ladder, and each transcendental against a shared-nothing `BigInteger` oracle. |
| `unsigned-scalar.*` | `UFixedQ4816`: the same shape, plus the five division entry points, the two unchecked pairs, and the `double` boundary. |
| `q1648.*` | `FixedQ1648`: the same non-transcendental shape as `scalar.*`, retargeted at forty-eight fraction bits and a sixteen-bit integer range, plus the `FixedQ4816` peer conversion. |
| `q3232.*` | `FixedQ3232`: the same non-transcendental shape as `scalar.*`, retargeted at thirty-two fraction bits and a thirty-two-bit integer range, plus the `FixedQ4816` peer conversion. |
| `unit-fraction16.*` / `unit-fraction32.*` | The half-open fractions: the grid, the exact operations, order, the shift masks, the refusal ladder, and text. |
| `closed-unit.*` | `UnitInterval32`: the pairwise and triple products, the exact bounded operations, the kinship conversions, and the construction refusals. |
| `complex.*` / `split.*` / `dual.*` | The planar trio: products, division, the exponential maps, norms, and the conjugation identities. |
| `quaternion.*` | Hamilton products, `Dot`, `Exp`/`Log`, `FromTo`, `Slerp`, `Inverse`, and normalization. |
| `rigid.*` | Composition, `TransformPoint`, the `Exp`/`Log` crossing, `ScLerp`, and the normalized-composition twin. |
| `vector.*` | Componentwise algebra, the fused plane and space products, norms, normalization, and the divergence canaries that prove the fused discipline is load-bearing. |
| `position.*` | Canonicalization, `Delta`, `Translate`, the group structure, and the render-relative ladder. |
| `rate.*` | Scalar and vector integration against an exact `BigInteger` ledger, the unit-advance closure, and both refusal ladders. |
| `symmetric-solve.*` | `FixedSymmetricSolve`: the 2×2/3×3 solve kernels and `TryInvertSymmetric3` (internal), and the public `TryApplySymmetric3`, `TryApplySymmetric2` and `TryInvertSymmetric2`, against independent `BigInteger` Cramer's-rule and Bareiss-elimination oracles, the six-term-determinant bit budget at hand-picked extreme operands, the singularity refusal, and Invert's own large-magnitude refusal envelope. |
| `smoke.*` | The fast mirrors — a ties-to-even witness for four of the five rounding carriers (`FixedQ4816`'s multiply and its divide, `UFixedQ4816`, `UnitFraction32`, `UnitInterval32`), a fused-product witness, and the twin spot checks. `UnitFraction16` has **no** smoke witness; its only mirror is `deep.unit-fraction16-exhaustive`, which lives in the Deep tier. |
| `deep.*` | The exhaustive and full-range mirrors of everything above, including the complete `UnitFraction16` sweep. |

**The tiers.**

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/bench.runsettings
```

The bare command is the default tier (Smoke + Default). Its **budget** is under
30 s, which it meets on an idle machine — but that is a budget the tests README
owns, not a promise about your run: a contended machine has been measured at
three times it. Nothing records it for you. The suite's machine-written
[`RESULTS.md`](../../../tests/Puck.Maths.Tests/RESULTS.md) deliberately carries
no duration at all — every figure in it is machine-independent, so it stays
meaningful across the machines this engine is tested on, and a wall time would
have been the one line that did not. Time your own run if you need the number,
on an idle machine, and compare it only against that same machine. Deep is the
exhaustive tier, and it is the one that has to pass before a rounding change
lands.

**Where doc-versus-code divergences are tracked.** They are not tracked by
hand. A law that pins what a kernel *does* where the member's own XML doc says
otherwise must be spelled `Leg.PinnedAsObserved`, and the ledger derives a
register from those declarations on every run — a register here being a table
the test run rebuilds from scratch rather than one anyone edits. That one is
**Register: behaviour pinned as observed against its own XML doc**, in
[`tests/Puck.Maths.Tests/leg-ledger.md`](../../../tests/Puck.Maths.Tests/leg-ledger.md).
A row cannot go stale, and a divergence cannot be closed by editing the
register: closing one means correcting the doc (or the code) and re-spelling
the leg, after which the row drops out by itself. Every row the campaign
raised against this folder is now closed — the last two rulings landed as the
BCL unsigned parse grammar and the proof that `Inverse`'s overflow early-out
answers the correctly rounded zero — and no divergence stands against the
types documented above.

**What a change here means.** Rule 4 governs: determinism pins the mapping,
not the values. A deliberate correction to any value path is *expected* to
move state hashes and recorded replays, and those get re-recorded in the same
change rather than preserved — never keep a wrong result just to keep a hash
stable. The coupling map above tells you what a change reaches: a correction
inside `FixedQ4816.RoundProductSum` or `FusedArithmetic` moves every composite
at once, a change to one composite's own assembly moves only it, and a change
to a narrow-lane threshold should move nothing observable at all.
