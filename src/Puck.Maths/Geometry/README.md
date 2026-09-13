# Geometry

Grids, curves, layered index spaces, and the modular group. Everything here has
**exact results on an index or a lattice point**—no accumulated drift,
bit-identical on every platform. These are the
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
| [`HexagonalCoordinate`](#hexagonalcoordinate) | `readonly record struct` | An exact hex-grid cell—the Eisenstein integer `Q + R·ω`. 60° rotations are integer multiplies. |
| [`HexagonalIndex`](#hexagonalindex) | `readonly record struct` | A dense, continuous hex-grid walk in complete rings. Radius, rotation, and reflection act directly on the index. |
| [`SquareCoordinate`](#squarecoordinate) | `readonly record struct` | A signed integer cell with four cardinal neighbours, exact quarter turns and Gaussian arithmetic. |
| [`SquareIndex`](#squareindex) | `readonly record struct` | A dense, continuous spiral over complete squares, with direct symmetries, scaling and norm. |
| [`HilbertCurve`](#hilbertcurve) | `static` | The locality-preserving 1D ↔ 2D bijection. Cache-coherent tile and chunk ordering. |
| [`LayerSequence`](#layersequence) | `readonly record struct` | Layered index spaces (the generalized figurate numbers), with constant-time index → layer lookup. |
| [`ModularTransform`](#modulartransform) | `readonly record struct` | An exact element of the modular group, acting on the hyperbolic plane. The one object beneath the other three motions. |

---

## `HexagonalCoordinate`

An exact hexagonal grid coordinate—the Eisenstein integer `Q + R·ω`. Because
it is a genuine number ring, a 60° rotation is an exact integer multiply
(`RotatedLeft`/`RotatedRight`, order 6) with no drift, unlike `FixedComplex`;
`Length` (hex-grid distance), the six neighbours (`Direction`/`Neighbor`), the
ring product `*` (rotation composed with scaling), and `Round` (fractional
position → nearest cell, deterministic `FixedQ4816`) are all exact. For
deterministic hex-grid games. Coordinates and scalar query results retain their
`int` API: operations evaluate in a wide exact lane and throw
`OverflowException` when the mathematical coordinate, norm, or distance cannot
be represented by that API. They never wrap to another cell or scalar.
`ToString()` prints only Q and R, so formatting remains defined even when a
derived norm or distance would overflow.

## `HexagonalIndex`

`HexagonalIndex` stores a hex cell as one nonnegative `long`. Index zero is the
origin. Ring `r` contains `6r` cells, starting at index `1 + 3r(r − 1)` at
coordinate `(1, 1-r)`. It runs counterclockwise through the six directions of
`HexagonalCoordinate`, whose axes meet at 120°. The first ring is therefore
`(1,0), (1,1), (0,1), (-1,0), (-1,-1), (0,-1)`, at indices 1 through 6.
Each ring ends at `(0, -r)`, and the next starts at `(1, -r)`: one neighbouring
step connects them. Every consecutive pair of admitted indices therefore
visits neighbouring cells, including across ring boundaries. The disk through
radius `r` occupies exactly indices `0` through `3r(r + 1)`, without holes or
repeated cells. Growing the disk appends cells without renumbering its interior.
Consecutive indices are always neighbours; other neighbouring cells can still
be far apart in the index order.

The representation makes some geometry particularly simple. `Radius` locates
the cell's ring without decoding its coordinate. For positive index `h`, set
`a = floor((h - 1)/3)`; the radius is `floor((1 + isqrt(1 + 4a))/2)`.
This reduced discriminant fits `ulong` over the entire admitted disk, so the
lookup uses the exact 64-bit integer square root. `LayerSequence` also reduces
centered sequences to 64-bit arithmetic; this locator specializes the fixed hex
coefficients and admitted domain. `Rotate(turns)` adds `turns * r` to the perimeter offset modulo
`6r`; a positive turn is 60° counterclockwise. If `t` measures the perimeter
from `(r, 0)`, the stored ring offset is `j = (t + r - 1) mod 6r`.
`Conjugate()` sends it to `(2(r - 1) - j) mod 6r`, reflecting the cell across
the real axis. `Swap()` exchanges Q and R using `(4r - 2 - j) mod 6r`.
These operations fix the origin. Their cost does not grow with
the number of requested turns.

`Scale(k)` multiplies both coordinates directly. For positive `k`, its new
radius is `kr` and its offset is `k(j + 1) - 1`; equivalently its index is
`k * Value + 3k(k - 1)r²`. Negative factors additionally apply a half-turn,
and zero produces the origin. `Norm` also avoids decoding: with
`s = (j + 1) mod r`, it is `r² - s(r - s)` (zero at the origin).

```csharp
using Puck.Maths;

var cell = HexagonalIndex.FromCoordinate(new HexagonalCoordinate(Q: 2, R: 1));
var index = cell.Value;                     // 9
var radius = cell.Radius;                   // 2 hex steps from the origin
var rotated = cell.Rotate(turns: 1);        // index 11, coordinate (1, 2)
var adjacent = cell.Neighbor(direction: 1);  // index 23, coordinate (3, 2)
var distance = HexagonalIndex.Distance(cell, adjacent); // 1
```

The arithmetic operators act on the **represented coordinates**: `+` and `-`
translate, unary `-` makes a half-turn, and `*` is the Eisenstein product,
combining rotation and scaling. `Translate` also accepts a coordinate
displacement. For nonnegative Q, R and a displacement `(k, k)` with `k >= 0`,
translation adds `k(6r + 3k - 1)` to the index. Other translations and general
multiplication decode and reuse the coordinate algebra. Arithmetic
on the raw `Value` has a different meaning and does not substitute for these
operators. `Norm` returns the field norm `Q² − QR + R²`; `Distance` returns
hex steps between two cells. Both return `long`, so distances between opposite
outer cells remain representable even when they exceed `int.MaxValue`.

The admitted disk ends at `MaxRadius = 1,753,413,055`, the largest complete
ring that fits in a nonnegative `long` index. Its final index is
`MaxValue = 9,223,372,029,593,538,240`. The small remaining tail of `long` is
rejected: admitting only part of a ring would make some rotations leave the
domain. Every admitted index decodes to a signed `int` coordinate, and every
coordinate in that disk has exactly one index. Rotation, conjugation, and
negation and swapping preserve the disk. Translation, scaling or multiplication can leave it, in
which case they throw `OverflowException`; an invalid index constructor argument
throws `ArgumentOutOfRangeException`. Nothing wraps or saturates to another cell.

The `integer.hexagonal-index-*` laws check an independent perimeter walk,
BigInteger geometry and arithmetic, symmetries, continuity within and between
rings, and overflow refusals. Boundary cases reach the largest admitted ring.
The Deep geometry mirror adds a larger deterministic sample.
The `integer.encoded-operations*` laws additionally compare direct transforms
with independent coordinate arithmetic and exercise signed scale extremes.

## `SquareCoordinate`

`SquareCoordinate(X, Y)` is the square-grid counterpart of
`HexagonalCoordinate`: signed `int` components, origin by default, checked
arithmetic, neighbours, distances, rotations, and nearest-cell rounding.
Directions 0 through 3 are east, north, west and south. `RotatedLeft()` turns
90° counterclockwise; `RotatedRight()` turns clockwise. `Round(x, y)` rounds
each `FixedQ4816` component to its nearest integer, with ties to even.

The coordinate is also a Gaussian integer `X + Yi`. Multiplication computes
`(X₁X₂ − Y₁Y₂, X₁Y₂ + Y₁X₂)`, so multiplying by `(0, 1)` makes a quarter turn.
`Norm = X² + Y²` is squared Euclidean distance. As with the hex coordinate,
component arithmetic and scalar queries throw `OverflowException` when their
exact result cannot fit the `int` result. Formatting prints only the components.

There are two useful distances. `Length = |X| + |Y|` counts cardinal steps
from the origin; `Distance(a, b)` counts steps between cells. `Radius` is
`max(|X|, |Y|)`, which measures concentric square shells. At `(2, 2)`, Length
is 4 and Radius is 2. Keeping this distinction explicit lets the index follow
square perimeters while movement stays four-way.

## `SquareIndex`

`SquareIndex` provides the same encoded operations as `HexagonalIndex`, using
square geometry. Zero is the origin. Shell `r` starts at index `(2r - 1)²`,
at `(r, 1-r)`, walks counterclockwise through `8r` cells, and ends at `(r, -r)`.
The next cell is `(r+1, -r)`, the start of the next shell. Every pair of
successive indices is therefore separated by one cardinal step. The complete
square `[-r, r]²` occupies exactly indices `0` through `(2r+1)² - 1`, with no
gaps, repeated cells or renumbering as the square grows.

The first eight cells are `(1,0), (1,1), (0,1), (-1,1), (-1,0), (-1,-1),
(0,-1), (1,-1)`. Unlike the first hex ring, this includes corners as well as
the four unit directions. Manhattan-distance rings cannot themselves form
cardinal walks: all cells in one such ring have the same checkerboard color,
whereas every cardinal step changes color. Square-shell Radius therefore uses
Chebyshev distance; `Distance` continues to count Manhattan steps.

```csharp
var cell = SquareIndex.FromCoordinate(new SquareCoordinate(X: 2, Y: 1));
var index = cell.Value;                     // 11
var radius = cell.Radius;                   // 2
var rotated = cell.Rotate(turns: 1);        // index 15, coordinate (-1, 2)
var adjacent = cell.Neighbor(direction: 1); // index 12, coordinate (2, 2)
var distance = SquareIndex.Distance(cell, adjacent); // 1
```

`Rotate`, `Conjugate` (reflect across the X axis), `Swap` and signed `Scale`
work directly on the shell offset. The radius is `(isqrt(Value) + 1) / 2` with
integer division. For offset `j` in a nonzero shell, rotation adds `2r` per
quarter turn modulo `8r`; conjugation gives `(2r - 2 - j) mod 8r`, and swapping
gives `(4r - 2 - j) mod 8r`. Positive scale `k` maps `(r, j)` to
`(kr, k(j+1)-1)`; negative scale adds a half-turn. `Norm` is also direct:
`r² + (((j+1) mod 2r) - r)²`. General translation and Gaussian multiplication
decode, operate on coordinates, and encode the result.

The largest admitted shell is `MaxRadius = 1,518,500,249`, ending at
`MaxValue = 9,223,372,030,926,249,000`. Every admitted cell fits signed `int`
coordinates. Complete shells keep every rotation and reflection in-domain;
translations, products and scales can leave the domain and then throw
`OverflowException`. Invalid constructor indices throw
`ArgumentOutOfRangeException`. `Norm` and `Distance` return `long`; the maximum
Manhattan distance between admitted cells is `4·MaxRadius`.

The `integer.square-*` laws compare against BigInteger coordinate arithmetic,
an independent growing-step spiral, and maximal-shell boundaries. Deep mirrors
increase the deterministic sample. This centered encoding differs from
`ElegantPair`, which enumerates only nonnegative coordinate pairs.

## `HilbertCurve`

The Hilbert space-filling curve: an exact bijection between a 1D distance and a
2D grid point (`Encode`/`Decode`) that preserves locality—consecutive
distances are always grid neighbours, unlike Morton/Z-order
(`BinaryIntegerFunctions.BitwisePair`), which jumps at power-of-two seams. For
cache-coherent chunk/tile ordering, spatial hashing, texture swizzling. `order`
in `[1, 31]`.

## `LayerSequence`

Layered index spaces (the generalized figurate numbers): a `Seed`-sized core
wrapped by layers that start at `Start` and grow by `Step`, with
**constant-time** index→layer lookup by inverting the quadratic prefix sum
`Count(n) = Seed + Start·n + Step·n·(n − 1)/2`—no walking, bit-identical on every
platform over the whole `long` index range. Square sequences and positive
sequences with `Start == Step` simplify to unsigned 64-bit arithmetic before
taking a root. For the latter, dividing `(index - Seed)` by `Start` reduces the
lookup to triangular numbers; a guarded discriminant or one exact boundary
comparison handles the entire range. Their offsets also use 64-bit arithmetic.
Other shapes retain the general `Int128` discriminant and floor division in a
separate helper, keeping that larger solver out of inlined common queries.
The square-root kernel may use a
hardware floating-point seed followed by exact integer correction; its final
result is always the integer floor root.

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
| `Linear(size, seed)` | size | 0 | seed | Flat layers—ordinary linear indexing. |
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

An exact element of the modular group—a 2×2 integer matrix of determinant one
—acting on the hyperbolic plane by `z ↦ (A·z + B)/(C·z + D)`. The one object
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

## Documentation

📚 [Puck.Maths](../README.md) · 🛠️ [Contributing to Puck](../../../docs/development/contributing.md)
