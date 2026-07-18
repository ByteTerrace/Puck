# Category theory and Puck.Maths

Category theory is useful here as a discipline for identifying operations,
representations, and composition laws. It is not a reason to add categorical
names to APIs whose laws do not hold under Puck's bitwise equality contract.

## Decision index

| Idea | Current decision |
|---|---|
| .NET generic math | Adopt the finest truthful `System.Numerics` interfaces. They describe capabilities without claiming associativity or distributivity. |
| `INumber<T>` | Adopted for `FixedQ4816` and `UFixedQ4816`, with `ISignedNumber<T>` / `IUnsignedNumber<T>`. Conversions are numeric (never raw-bit reinterpretations): checked throws, saturating clamps, and truncating follows decimal-like clamp-at-range behavior; excess fractional precision quantizes to nearest/ties-even. The UQ0 fraction types cannot implement it because they cannot represent `One`. |
| Monoids and rings | Do not add general `IMonoid` or `IRing` interfaces for the rounded fixed-point products. Record and test exact laws before using algebraic vocabulary. |
| Actegories/actions | Treat transform composition acting on points as an approximate numerical action, not a strict categorical action under bitwise equality. Do not reassociate it as an optimization. |
| Positions and displacements | Express the useful heterogeneous operations through BCL interfaces: position + displacement produces a canonical position, while representable position − position produces a displacement. `TryTranslate`/`TryDelta` expose the finite carrier's boundary. This is capability-level torsor-like structure without claiming unverified laws. |
| Lenses and optics | Prototype only at a demonstrated nested immutable-data pain point, most likely run documents or creator state rather than this scalar library. Prefer generated or static accessors on hot paths. |
| Functors, Tambara modules, and Kan extensions | Keep as design tools. C# encodings require higher-kinded/rank-2 witness machinery and there is no current `Puck.Maths` consumer that repays it. |
| Tannakian reconstruction | Use the representation-independence lesson in API and oracle design; do not expose it as a runtime abstraction. |

The scalar generic-number implementation follows .NET's two-sided conversion
negotiation: Puck handles known BCL carriers and its signed/unsigned sibling
directly, while an unknown numeric gets the chance to provide its own exact
conversion. `CreateChecked` checks conversion range. Ordinary arithmetic keeps
Puck's explicit modular contract; C# `checked` operators and checked generic
arithmetic throw when the final rounded result is outside the carrier.

## Law ledger

`System.Numerics` operator interfaces say that an operation is available. They
do not prove the algebraic laws usually associated with its spelling.

Wrapping Q48.16 addition is associative under raw-bit equality and has zero as
an identity. Rounded Q48.16 multiplication has a representable identity but is
not associative. A minimal counterexample, expressed as raw Q48.16 values, is:

```text
epsilon = 1
half    = 32768
two     = 131072

(epsilon * half) * two = 0
epsilon * (half * two) = epsilon
```

The first inner product is exactly half a raw unit and rounds to even zero. The
other association first computes an exactly representable product. Complex,
quaternion, dual, and rigid-transform products inherit the same general
reassociation hazard. They remain deterministic: a fixed expression tree
returns the same bits everywhere, but an algebraic rewrite may change those
bits.

This distinction controls the vocabulary:

- use *operators*, *identities*, *carriers*, and *composition* for available
  capabilities;
- reserve *monoid*, *ring*, *action*, and *functor* for APIs whose required laws
  are established at the equality notion the API promises;
- describe ULP-bounded numerical coherence as approximate behavior, never as an
  exact categorical law.

The Tier-A fixed-point stage carries both sides of this ledger: constrained
`INumber<T>` addition exercises the exact identity/association surface, while the
counterexample above pins the multiplication non-law. Numerical composition is
checked separately with explicit ULP bounds and independent wide-integer oracles.

## Reading the source essays

[Actegories](https://bartoszmilewski.com/2026/06/30/actegories/) describes a
monoidal category acting on another category. Scalar scaling and transforms
acting on points are familiar programming shadows, while the full definition
also requires coherent associators and unitors.

[Tambara Equipment](https://bartoszmilewski.com/2026/07/11/tambara-equipment/)
places action-compatible profunctors beside monoidal functors in a double
category. Its concrete payoff for programming is the representation of optics:
an optic hides residual context used to extract a focus and rebuild the whole.
That supports lenses, prisms, and traversals when the relevant action and laws
are available.

[Tannakian reconstruction](https://bartoszmilewski.com/2026/07/14/tannakian-reconstruction/)
uses a double-Yoneda argument: natural transformations between evaluation
functors recover the original morphisms. For Puck, the useful lesson is that a
representation-independent operation should be characterized by what every
lawful interpretation observes.

[Kan Extensions in Double Categories](https://bartoszmilewski.com/2026/06/13/kan-extensions-in-double-categories/)
generalizes left and right Kan extensions from functors to profunctors. The
computational content is universal factorization—canonical ways to map out of a
free extension or into a cofree one. It is a useful adapter-design principle,
but not a scalar-arithmetic abstraction.

Supporting references:

- [.NET generic math](https://learn.microsoft.com/en-us/dotnet/standard/generics/math)
  and [`INumberBase<TSelf>`](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.inumberbase-1?view=net-10.0)
- [Categories of Optics](https://arxiv.org/abs/1809.00738)
- [Profunctor Optics: a Categorical Update](https://compositionality.episciences.org/13530/pdf)
- [Kan extensions in double categories](https://arxiv.org/abs/1402.0250)
- [Module categories, internal bimodules and Tambara modules](https://arxiv.org/abs/2210.13443)
