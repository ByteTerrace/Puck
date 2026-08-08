# Geometry

Grids, curves, layered index spaces, and the modular group. Everything here is
**exact integer arithmetic on an index or a lattice point** — no floating point,
no accumulated drift, bit-identical on every platform. These are the
deterministic value types that are not fixed-point numbers: a hex cell is an
Eisenstein integer, a Hilbert distance is a bijection, a layer index is the
inverse of a quadratic prefix sum, and a modular transform is a 2×2 integer
matrix of determinant one.

Every public type lives flat in `namespace Puck.Maths`. The parent
[`Puck.Maths` README](../README.md) is the library's entry point; this file is
the contract for the folder.

---

## At a glance

| Type | Kind | What it's for |
|------|------|---------------|
| [`HexagonalCoordinate`](#hexagonalcoordinate) | `readonly record struct` | An exact hex-grid cell — the Eisenstein integer `Q + R·ω`. 60° rotations are integer multiplies. |
| [`HilbertCurve`](#hilbertcurve) | `static` | The locality-preserving 1D ↔ 2D bijection. Cache-coherent tile and chunk ordering. |
| [`LayerSequence`](#layersequence) | `readonly record struct` | Layered index spaces (the generalized figurate numbers), with constant-time index → layer lookup. |
| [`ModularTransform`](#modulartransform) | `readonly record struct` | An exact element of the modular group, acting on the hyperbolic plane. The one object beneath the other three motions. |

---

## `HexagonalCoordinate`

An exact hexagonal grid coordinate — the Eisenstein integer `Q + R·ω`. Because
it is a genuine number ring, a 60° rotation is an exact integer multiply
(`RotatedLeft`/`RotatedRight`, order 6) with no drift, unlike `FixedComplex`;
`Length` (hex-grid distance), the six neighbours (`Direction`/`Neighbor`), the
ring product `*` (rotation composed with scaling), and `Round` (fractional
position → nearest cell, deterministic `FixedQ4816`) are all exact. For
deterministic hex-grid games. Coordinates and scalar query results retain their
`int` API: operations evaluate in a wide exact lane and throw
`OverflowException` when the mathematical coordinate, norm, or distance cannot
be represented by that API. They never wrap to another cell or scalar.

## `HilbertCurve`

The Hilbert space-filling curve: an exact bijection between a 1D distance and a
2D grid point (`Encode`/`Decode`) that preserves locality — consecutive
distances are always grid neighbours, unlike Morton/Z-order
(`BinaryIntegerFunctions.BitwisePair`), which jumps at power-of-two seams. For
cache-coherent chunk/tile ordering, spatial hashing, texture swizzling. `order`
in `[1, 31]`.

## `LayerSequence`

Layered index spaces (the generalized figurate numbers): a `Seed`-sized core
wrapped by layers that start at `Start` and grow by `Step`, with
**constant-time** index→layer lookup by inverting the quadratic prefix sum
`Count(n) = Seed + Start·n + Step·n·(n − 1)/2` in pure integer arithmetic (an
`Int128` discriminant, the exact floor square root, a floor division) — no
walking, no floating point, bit-identical on every platform over the whole
`long` index range.

A negative `Step` bounds the space: layer sizes shrink to zero and the total
tops out at `Capacity`. `LayerOf`/`Locate` treat indices past capacity as
errors; `Project` saturates against that horizon instead, reporting `Overflow`
(linear excess) and `Depth` (the imaginary component of the layer equation's
complex root, growing with the square root of the excess past the continuous
vertex), so overflow routing and backpressure fall out as data rather than
exceptions.

The named shapes:

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

```csharp
using Puck.Maths;

var rings = LayerSequence.CenteredHexagonal;      // 1 core cell, rings of 6, 12, 18, …
var ring = rings.LayerOf(index: 100L);            // 6 — constant time, pure integer
var (layer, offset) = rings.Locate(index: 100L);  // (6, 9) — ring plus position within it

var arena = LayerSequence.Create(start: 6L, step: -2L, seed: 1L); // bounded: 13 indices, 3 shrinking layers
var probe = arena.Project(index: 20L);            // (Layer 3, Overflow 8, Depth 2) — saturates, never throws
```

## `ModularTransform`

An exact element of the modular group — a 2×2 integer matrix of determinant one
— acting on the hyperbolic plane by `z ↦ (A·z + B)/(C·z + D)`. The one object
beneath the library's three motions: `Classify` sorts it by trace into the
elliptic rotations (the sixth root of unity of `HexagonalCoordinate`), the
parabolic tick shear (the kinematics step of `LayerSequence`), and the
hyperbolic golden inflation (the step of the metallic quasicrystals).

Composition is matrix product; `Inverse` is the adjugate, no division; `Apply`
moves a cusp (rational `p/q`, with `∞ = 1/0`) exactly and an interior
`FixedComplex` point at the one rounding seam; `GaussReduce` carries a
positive-definite form into the fundamental domain by an exact word in `S` and
`T`, terminating because the leading coefficient is a strictly decreasing
positive integer. The zero-initialized/default value is the identity transform,
so every publicly constructible value preserves the determinant-one invariant.
Exact composition and cusp application use wide intermediates; they throw
`OverflowException` only when the final matrix entry or reduced cusp coordinate
cannot be represented by `long`. `Inverse` likewise throws when negating a
`long.MinValue` off-diagonal entry would make its adjugate unrepresentable.
