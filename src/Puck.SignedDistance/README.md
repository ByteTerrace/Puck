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
`Sweep` is the one member outside that unit-size law: its curve's own control
points and radii already carry creation-unit dimensions directly (see
`SdfSolidGeometry.SweepReach`), so only a uniform scale bakes onto it.

`SdfProgramBuilder` builds an `SdfProgram` as an ordered stream of point
transforms, field operations, shapes, and materials — reset/translate/rotate,
union/subtraction/intersection blends (with smooth, chamfer, and round seam variants),
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

Round seams use the existing smooth-radius lane: `GrooveUnion` carves the
complement of sqrt(a²+b²)-r from the union, and `PipeUnion` adds that tube;
`GrooveSubtraction` and `PipeSubtraction` do the same against `max(a, -b)`.
Their derivative bounds compose as hypot(La,Lb) at each blend.

`Morph` and the two `Stairs` blends compose only at a `PopField`. A morph reads
its weight from an instance render lane through the same (lane, from, to)
mapping `LaneErode` uses; this evaluator has no dynamic transform table, so it
reads every lane as zero and a morphed scope resolves at the weight that lane
value maps to. A stairs pop carries its integer step count in Data1.z; Data1.y
is the analyzer's candidate scale on every pop.

`CellDisplace` adds amplitude*(F-0.5) to the running field. A fixed 27-cell
PCG3D search evaluates F1 or F2MinusF1, with conservative centered-randomness
ceilings 0.46 and 0.20. The fixed-point evaluator shares feature identity and
visit order with the shader. Its coordinate derivative is bounded separately
from a primitive's distance correction; restore a rigid frame before relief
when a preceding fold cannot supply a global continuous bound.
[Shape authoring examples](../Puck.World.Authoring/README.md#round-seams-and-cellular-relief)
show the higher-level scope and placement rules.

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

`SdfDistanceGrid` is the third reading of the same field: a lattice of the
exact evaluator's values at the corners of world-space cells of one authored
size, held in 8×8×8 blocks that fill on first touch, so a corner is evaluated
once and never again. Because the field is `LipschitzBound`-Lipschitz, the
nearest corner's value less the grid's `Slack` (the half-diagonal reach of a
cell at that bound, plus the rounding an evaluation carries) is a sound lower
bound anywhere in the cell. `SdfBandedFieldEvaluator` reads the exact evaluator
through that grid: `TryDistance` answers the exact field wherever the bound
falls below its `Band` (a contact reach the consumer supplies plus the slack)
and the bound above it, both gradient overloads always read the exact
evaluator, `Overlap` decides identically everywhere, and the cast, ground, and
visibility verbs run the one march loop (`SdfFieldMarch`) over samples that
are exact wherever the exact march could accept or stop and bounds elsewhere,
so a march that stays inside the band is bit-identical and one that crosses
the bound region differs only in its steps. `SdfDistanceGrid.TryCover` sizes a
grid from a program's finite static instance bounds plus a padding; a
plane-only or shape-free program has nothing to cover and gets none.

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
`Displace`, `DomainWarp`, `NoiseDisplace`, `FlareY`), `WallpaperFold`, and the shapes needing runtime
transcendentals or texture sampling (`RegularPolygon`, `Star`, `Ellipse`,
`Glyph`), plus `SampledRegion` (its brick pool is an engine resource unavailable
to the headless evaluator). `RoundedRectangle`, `ChamferedRectangle`,
`Superellipsoid`, `ConvexPolygon`, `Sweep` (strands == 1 only — a strand count
above 1 is render-only and refused for deterministic field contact by name),
`Repeat`/`RepeatLimited`/`SymmetryPlane`/`Elongate`/`Onion`/`Dilate` and
isotropic `Scale` interpret directly as 1-Lipschitz operations.

A `ShapeBlend` instruction carrying `SdfInstruction.Detail` composes nothing —
this evaluator has no shade-mode counterpart to the GPU's hit-only
re-evaluation, so a detail shape (a shading-only seam or rivet) is simply
absent from every field it walks; a program whose only shape is flagged
`Detail` reads exactly as shape-free.

`SdfInstruction.Secondary` is the opposite exclusion set (false marks a shape
that skips only the GPU's soft-shadow/AO field walks) and has no counterpart
here either — this evaluator has no shadow/AO concept, so it composes a
non-secondary shape exactly like an ordinary one.

`TryDistance` culls an instance (`SdfProgram.Instances`) whose whole compose
chain is a plain `SdfBlendOp.Union`: its authored world-space bound proves the
instance cannot lower the running best-so-far distance, so its instruction
slice is skipped entirely — bit-identical to evaluating it, never an
approximation. Smooth/chamfer/subtraction/intersection/Xor blends, and any
instance holding `PushField`/`PopField` or a bare `Onion`/`Dilate`, are never
culled, since their compose can depend on a candidate farther than the current
best. Nor is an instance culled unless the instruction right after it (if any)
is `ResetPoint`: skipping an instance also skips its own point-transform
chain, so the interpreter's local position and distance scale would otherwise
carry through unchanged from before the instance instead of what the
instance's own transforms would have left them as — safe only when a
following `ResetPoint` discards that value before anything reads it. The cull
needs no opt-in: it inspects whatever instances the caller
declared, and does nothing when there are none — `WorldSolidField`, below, does
not currently call `BeginInstance`/`Instance` for its placements, so today it
declares zero instances and this cull has no effect on the shipped world's
contact field until a caller wraps its per-object content in instance bounds.

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
through `SdfFieldEvaluator` — banded over an `SdfDistanceGrid` when the world
authors `collision.gridCellSize` — so the contact surface a body solves
against is the same field the renderer draws.

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
  `SdfDistanceGrid`, `SdfBandedFieldEvaluator`, `BakedWorldQuery`,
  `WorldQueryArtifact`, `WorldQueryBaker`, `WorldQueryProviders`,
  `WorldQueryConfidence`, `RayHit`.

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
