# Puck.SignedDistance

Puck.SignedDistance owns the signed-distance-function field as DATA: the
instruction ISA, the packed-word program representation a GPU interpreter
decodes, the fluent authoring builder that emits it, and a warp-free
deterministic fixed-point CPU interpreter that answers queries against the
exact same field. Everything here is built on Puck.Maths (and, for text
authoring, the render-agnostic Puck.Text) — no GPU, presentation, or
shader-compiler dependency of any kind.

The library describes and evaluates a field; it does not render one. A GPU
engine (Puck.SdfVm) consumes the program this library produces to march and
shade it; this library never names a device, a window, or a shader.

## ✨ Key features

- *One instruction stream, two consumers:* `SdfProgramBuilder` emits the same
  packed `SdfProgram.Words` a GPU interpreter decodes and `SdfFieldEvaluator`
  walks in fixed point — author once, render and simulate against the
  identical field.
- *Deterministic to the bit:* the CPU evaluator is `FixedQ4816`/`FixedVector3`
  throughout, with no float, wall-clock, or RNG in its walk; identical ordered
  inputs return identical results on every machine.
- *Sound instance culling:* bounded primitives contribute exact or conservative
  bounds for `SdfProgram`'s packing pass. Shapes for which no finite sound bound
  exists, including planes and the approximate ellipsoid path, deliberately stay
  always-tested instead of claiming a false finite envelope.
- *One fold, two readings:* an isometric domain operator is a point transform
  to a marcher and a set of rigid copies to anything that places geometry
  instead. `SdfDomainExpansion` derives the copies in fixed point, so an
  analytic collider compiler sees every copy the fold draws.
- *No GPU dependency:* authoring a program and querying it both work headless
  — no window, no device, no shader compiler.

## 🧮 The program model

`SdfSolidPrimitive` is the closed vocabulary of solids that carry a unit-size
law and a finite local bound; `SdfSolidGeometry` is the one place that decides
what each one measures, so an authored scale of `(1,1,1)` is its unit size. It
also answers whether a given scale emits at all —
`SdfSolidGeometry.TryValidateScaledPrimitive` is the predicate a document
validator asks before an authoring path reaches an emission that would throw.

`SdfProgramBuilder` builds an `SdfProgram` as an ordered stream of point
transforms, field operations, shapes, and materials — reset/translate/rotate,
union/subtraction/intersection blends (with smooth and chamfer variants),
domain folds (repeat, wallpaper, polar repeat, symmetry planes), warps (bend,
twist, log-spherical, cell jitter, displacement, domain warp), and the shape
vocabulary (primitives, the 2D-primitive-lift family, glyphs, screen slabs,
sampled regions). `SdfProgram.ValidateIsa` refuses an undeclared opcode by
numeric id and instruction index; `SdfProgram.AnalyzeLipschitz` bakes a
per-program step-scale bound so a march can never overstep a non-1-Lipschitz
warp.

The `SdfProgram` constructor is the packed format's own door, and accepts an
instruction stream the builder never authored, so it re-checks what the packing
writes straight into GPU words: shape/blend/material lane domains, finiteness
of every operand lane except the reinterpreted integer fields (`Glyph`'s packed
UV rect, `SampledRegion`'s packed dimensions and pool offset), the divisors an
exact core projects by (a trapezoid's profile slant, a screen surface's two
half-extents), finite non-negative material values and instance bounds, finite
screen origins, the screen frame's orthonormality, instance ranges that
partition the instructions they claim rather than overlapping, and balanced
one-deep field scopes that never cross an instance boundary.

## 🔍 The CPU query layer (`Puck.SignedDistance.Queries`)

`SdfFieldEvaluator` wraps a live `SdfProgram` and interprets its rigid,
warp-free subset directly in `FixedQ4816`/`FixedVector3` — a SECOND,
independent interpreter of the same instruction stream a GPU kernel walks,
never generated shader code. It implements `Puck.Maths`'s `IWorldQuery`
(`Raycast`/`SphereCast`/`Overlap`/`TryGroundHeight`/`LineOfSight`) and the
narrower `IFieldEvaluator` (`TryDistance`/`TryFieldGradient`, declared in
Puck.Maths) a gravity, contact, or wind consumer binds instead of the
five-verb query surface. `BakedWorldQuery`
is the sibling `Bounded`-confidence provider over a pre-baked, quantized
artifact (`WorldQueryArtifact`/`WorldQueryBaker`) for callers that do not need
per-tick exactness. Both providers rebase every position against the world
origin, so a `FixedPosition`'s hierarchy cell is part of the query; the baked
provider additionally refuses by name a radius spanning more than
`BakedWorldQuery.MaxRadiusCells` of its artifact's cells, its cell walk being
quadratic in the radius with no occupancy hierarchy behind it. `WorldQueryBaker`
also refuses a grid above `DefaultMaxCellCount` before allocating either layer;
callers with a measured larger budget can pass it explicitly.

The live evaluator applies `SdfProgram.StepScale` before subtracting a swept
sphere's radius. If that lower bound becomes too small to prove another
fixed-point step is safe before the raw field converges, the cast reports a
`Bounded` obstruction and `Overlap` resolves toward occupied. That conservative
answer prevents a chamfer or eccentric ellipsoid from turning an uncertain
sweep into a false clear result. `Overlap` likewise reports occupied when a
populated program cannot rebase an extreme hierarchical position into Q48.16;
an unrepresentable point is not evidence of empty space.

The evaluator's constructor walks the instruction stream once, asserting
every op/shape is in the supported rigid subset — it throws naming the first
excluded one rather than silently approximating. Excluded: `TransformDynamic`
(no per-frame transform table in this evaluator's signature), the runtime-trig
warps (`BendX/Y/Z`, `TwistY`, `LogSphere`, `CellJitter`, `RepeatPolar`,
`Displace`, `DomainWarp`, `NoiseDisplace`), `WallpaperFold`, and the shapes needing runtime
transcendentals or texture sampling (`RegularPolygon`, `Star`, `Ellipse`,
`Glyph`), plus `SampledRegion` (its brick pool is an engine resource unavailable
to the headless evaluator). `RoundedRectangle`, `Repeat`/`RepeatLimited`/
`SymmetryPlane`/`Elongate`/`Onion`/`Dilate` and isotropic `Scale` interpret
directly as 1-Lipschitz operations.

## 🚀 Basic use

```csharp
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

var builder = new SdfProgramBuilder();
var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new(1f, 1f, 1f)));

_ = builder.Sphere(radius: 1f, material: material);

var program = builder.Build();
var evaluator = new SdfFieldEvaluator(program: program);

var query = FixedPosition.FromLocal(local: new FixedVector3(
    X: FixedQ4816.FromInteger(value: 2),
    Y: FixedQ4816.Zero,
    Z: FixedQ4816.Zero
));

if (evaluator.TryDistance(position: query, distance: out var distance, material: out _)) {
    // distance ≈ 1.0 — one world unit outside a unit sphere at the origin.
}
```

`WorldSolidField` (`Puck.World.Server`) is the production shape this mirrors:
it compiles a world's authored solids into one `SdfProgram` and reads it
through `SdfFieldEvaluator`, so the contact surface a body solves against is
the same field the renderer draws.

## 📐 Determinism

`SdfFieldEvaluator` converts every instruction's floats to `FixedQ4816` once,
into a cached array, at construction — never per query. `TryFieldGradient` is
a 6-tap per-axis central difference over `TryDistance`. Both are pure
functions of the program and the query point: no wall-clock, no RNG, no
mutable field state. `tests/Puck.SignedDistance.Tests` directly gates the CPU
query contracts and fixed interpreter. The Post stages that once measured
cross-construction determinism and GPU drift (`world-field-evaluator-determinism`,
`world-field-drift`) remain quarantined with `Puck.Post`, so cross-backend drift
still has no live automated gate.

## 📋 Core types

- **The program** — `SdfProgram`, `SdfProgramBuilder`, `SdfInstruction`,
  `SdfMaterial`, `SdfMaterialScope`, `SdfInstanceRange`, `SdfScreenSurface`.
- **The ISA vocabulary** — `SdfOp`, `SdfShapeType`, `SdfBlendOp`, `SdfLift`,
  `SdfPolarAxis`, `SdfNoiseFlavor`, `SdfWallpaperGroup`, `SdfIsa`.
- **Solid primitives** — `SdfSolidPrimitive`, `SdfSolidGeometry`,
  `SdfSolidBounds`.
- **Domain operators** — `SdfDomainOp`, `SdfDomainOps`, `SdfDomainExpansion`,
  `SdfRigidFrame`.
- **Per-frame data** — `DynamicTransform`.
- **The instance cull grid** — `SdfInstanceGrid`, `SdfInstanceGridInput`.
- **Bricks** — `SdfBrickBake`, `SdfBrickPoolLayout`.
- **Screens** — `SdfScreenDecalLayout`.
- **Query providers** (the seams themselves are `Puck.Maths`) — `SdfFieldEvaluator`,
  `BakedWorldQuery`, `WorldQueryArtifact`, `WorldQueryBaker`,
  `WorldQueryProviders`, `WorldQueryConfidence`, `RayHit`.

## 🧪 Verification

Run the library's direct regression gate with:

```text
dotnet test tests/Puck.SignedDistance.Tests/Puck.SignedDistance.Tests.csproj -c Release
```

`tests/Puck.Physics.Tests` separately measures field-sample budgets against a
real `SdfFieldEvaluator`. The two Post stages that once pinned cross-construction
determinism and GPU drift are quarantined with `Puck.Post`; `puck parity`
(cross-backend composed-frame agreement) remains the live check that the packed
program renders consistently on both GPU backends.
