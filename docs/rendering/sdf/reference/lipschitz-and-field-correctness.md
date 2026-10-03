# Lipschitz and field correctness

Sphere tracing is safe only when each step is bounded by the field's rate of
change. Puck computes a program-wide conservative `stepScale` from the authored
instruction stream and applies it in `map()`, and in every other marcher that
walks the same stream.

## Marching contract

For a field with Lipschitz bound `L`, the safe scale is at most `1 / L`.
Rigid transforms and exact primitives retain factor 1. Scaling, warps,
displacement, and some composition operators require an additional bound.
Host-side analysis and HLSL evaluation must agree on every instruction's
effect.

Consumers must distinguish scaled field distance from world-space length.
Hit thresholds, AO probes, shadow steps, and bound comparisons must apply the
conversion documented by the shader contract.

## Composition bounds

A blend's bound is a property of the composition, not of the operands' authored
chains, and the two differ whenever a blend can exceed both of its inputs.

Every min, max, and lerp arm carries `max(La, Lb)`. That is idempotent and
order-free, so folding it once per chain and taking a maximum computes it.

The chamfer family does not fit that shape. Its bevel arm is `(a ± b ± r)·√½`,
whose gradient is `(∇a ± ∇b)/√2`, so the composed bound is

    L = max(La, Lb, (La + Lb)/√2)

which grows with each chamfer composition and has fixed point `1 + √2`. A
factor applied once per chain or once per program therefore understates by up
to `(1 + √2)/√2 = 1.70711×`, which is enough to march through thin geometry:
three chamfer-unioned slabs carved to a plate a few hundredths of a unit thick
are a hole at the one-`√2` scale and a hit at `1/1.70711`.

Two properties follow from the accumulator rule and must be preserved by any
implementation of the recurrence:

- the running accumulator seeds at a constant, whose bound is zero, so the
  first chamfer composition is the identity and two chamfers reach exactly
  `√2`; growth begins at the third; and
- one accumulator crosses every point reset, so splitting the stream into
  segments is not a bound on how many chamfer compositions can nest. A scope
  pop composing with a chamfer is the same composition as a chamfer shape
  blend, not a separate program-wide factor.

## Query seams

A field evaluator behind a hierarchical world position evaluates the whole
position. Reading only the cell-local offset aliases the field with the cell
period and answers for the wrong copy, and a position constructor that
re-anchors past half a cell reaches that state without any caller asking for
it. A field-only seam that cannot rebase refuses the sample. An authoritative
obstruction verb resolves the undecidable point toward occupied; it may not
answer clear.

A CPU marcher over the same instruction stream is bound by the same step scale
as the shader. Restricting the interpreted subset to rigid ops does not make the
raw field value a safe advance: the chamfer family lives in the blend tail, so a
program every one of whose ops is an isometry can still overestimate distance and
be tunnelled by a raw step.

For a swept sphere, scale the field before subtracting the radius:
`safeAdvance = field * stepScale - radius`. Scaling the clearance instead also
scales down the radius and can overstate the empty gap. `Overlap` uses the same
scaled field as a conservative separation test. If the safe advance falls below
the smallest reliable fixed-point step before the raw hit threshold converges,
the cast reports a bounded obstruction; replacing it with a larger step would
discard the Lipschitz proof and can tunnel the body through a thin surface.

The scale shortens every advance, so a fixed iteration budget shortens the
distance a march covers before it gives up, in proportion. The budget must
derive from the scale, or adding a chamfer silently converts resolving casts
into non-convergent ones.

A march that exhausts its iteration budget has proved nothing. Each verb must
resolve it toward the answer its own consumer can survive being wrong about, and
that direction is a property of what the verb's true half ASSERTS, not of the
provider:

- an obstruction verb—cast, sweep, visibility—asserts "something is there",
  so it folds exhaustion to a hit marked bounded rather than exact. Folding it
  into "clear" is a false negative that reaches authoritative simulation:
  contact resolution reads it as "no contact" and visibility reads it as a line
  through solid geometry.
- a surface verb—ground height—asserts "the terrain is at this Y". It
  returns a coordinate with no confidence channel to qualify, and a caller that
  grounds a body on a fabricated Y moves it somewhere the world does not have.
  It folds exhaustion to "not found", the same answer an empty column gives.

A grazing probe beside a vertical wall exhausts on the wall's own clearance and
is the discriminating case: it must read as blocked for a cast and as no-ground
for a height query, from the same march.

## Discontinuous folds

Repeat, polar repetition, wallpaper folds, and cell jitter can cross a domain
boundary between samples. A local Lipschitz factor alone cannot prove that a
raw step is safe across the discontinuity. The marcher therefore uses
fold-safe bounds where required.

A wallpaper fold is sound only when its fold is continuous. The fold reduces
a point to its lattice cell, then applies the group's in-cell isometry, and
the field reads only that cell's copy. When every cell wall and every in-cell
seam is a mirror of the group, the fold is built from reflections alone, so it
never lengthens a distance: for any rendered point q,
f(p) = d(F(p), S) ≤ |F(p) − F(q)| ≤ |p − q|. The field then never reads past
the nearest copy. It is exact when the prototype lies in the region the fold
leaves fixed, and reads short, which slows the march but skips nothing, when
the prototype crosses a mirror. A fold that jumps, at a translation wall or a
rotation seam, can read a cell's own copy while a neighbour's lies nearer, so a
march could step through the neighbour.

So a program folds only through a continuous group, and `SdfProgram` refuses
the others by name when it builds. `SdfWallpaperFold`, the CPU statement of
the kernels' fold, measures continuity by folding pairs of points across the
lattice: PMM, P4M, P3M1 and P6M are continuous; P1, P2, PM, PG, CM, PMG, PGG,
CMM, P4, P4G, P3, P31M and P6 jump. `SdfWallpaperFoldLawTests` holds every
accepted group to a brute-force distance to its rendered geometry and to a
sphere-traced ray, and shows the same sweep catching P2.

A continuous group stays continuous only through the lattice it is given, and
`SdfWallpaperFold` states what that is. A square lattice's `limit` is a
non-negative whole number of cells per axis: the clamp then collapses whole
cells onto the boundary cell of the same lattice row, which is still a
reflection, where a fractional limit moves the clamped cell off the lattice and
jumps at the wall. A hex lattice has no continuous clamp, because an edge cell
has two neighbours inside any boundary and one clamped cell cannot match both,
so a hex group takes only the unbounded limit and a hex wallpaper is bounded by
intersecting it with a bounding shape inside a field scope. The reciprocals the
kernel reads (`Data0.zw`) are exactly one over the cell the fold subtracts, so
the lattice round and the displacement read one cell, and a cell is refused
unless it is positive and finite and its reciprocal is at most
`SdfWallpaperFold.MaximumInverseCell`, which keeps the round finite at any point
a float resolves a cell. The builder, `SdfProgram` admission and the creation
canonicalizer state each rule once, through `LimitRefusal` and `CellRefusal`.
A fold whose limit is unbounded (`SdfWallpaperFold.IsUnbounded`) has copies at every
distance, so its influence has no bound, and so has an infinite `Repeat` or a
`RepeatLimited` whose limit reaches `SdfDomainOps.UnboundedRepeatLimit`: such a
shape's bound is `SdfBoundAlgebra.Unbounded`, and the stamper's reach for it is
that state, where a million cells of a small pitch would be a radius a camera can
leave behind. An influence with no bound still composes (see
[bounds compose through set operations](#bounds-compose-through-set-operations)). The hex reduction
clamps its fold-plane point to `MaximumHexCoordinate` before the multiply, which
keeps every axial sum within float's finite range for any cell the rule admits and
leaves the fold continuous. `SdfWallpaperFoldLawTests` folds pairs across every cell wall of finite,
fractional and unbounded limits and of cells from ten micro-units to three
thousand, and holds every configuration a builder accepts to the same stretch.

## Bounds compose through set operations

An instance's cull bound is the radius of a sphere outside which its field is at
least the distance to the sphere; `SdfBoundAlgebra.Unbounded` stands for no such
sphere. Unbounded is a state, positive infinity, never a radius: composing it, adding
a finite margin and scaling it by a positive factor all leave it unbounded, so no
placement scale overflows it, and only the program's packing turns it into
`SdfProgram.UnmaskableBoundRadius`, the number the kernels read. An instance
declares it by authoring `Unbounded` as its radius, and a tree the program finds
unbounded packs the same bound. `SdfBoundAlgebra` composes the bounds of a field's
operands through its set operations, and the program and the authoring stamper
both read it:

| Operation | Field | Bound |
|---|---|---|
| Intersection | `max(a, b)` | the smaller: it is at least either operand, so an unbounded operand imposes nothing and unbounded with finite is finite |
| Subtraction `a - b` | `max(a, -b)` | `a`'s: it is at least `a`, whatever `b` is |
| Union | `min(a, b)` | the larger: one unbounded operand makes the union unbounded |
| Smooth variant | the blend's own | the same, plus its blend radius, which is the halo `SdfProgram` adds once to the instance; a scope's compose radius is multiplied by the scope's Lipschitz factor `L`, since the scope's field joins its parent divided by `L` |

Transforms, scales and repeats compose as they always have. The algebra holds over
a field scope, whose shapes join one field by their blends: inside `PushField` an
unbounded lattice intersected with a box is as bounded as the box, a box minus a
lattice keeps the box's bound, and a lattice with a box unioned in is unbounded.
Outside a scope every intersection, field op and fold with no edge reads the one
global accumulator, so a flat instance holding one is unmaskable. Every bound is measured from the world point: a segment starts there, because the
directory and the instance mask can skip or compile a segment apart from its neighbours and a
skipped one passes the point before it along, so `SdfProgram` refuses a stream that may
carry a moved point into a segment that reads it without a `ResetPoint` of its own (a
shapeless world segment, which is never skipped, is the one reset that holds across a
boundary). A lattice opens an unbounded fold unless its limit gives it an edge; `SdfOpRoles`
is the one table of which ops move the point, which move the field, and which open a lattice,
and an op without a row refuses by name. A `Plane` is
unmaskable wherever it stands, and `SdfBoundAlgebraLawTests` samples the field
past each packed bound and holds it at least the distance to that bound.

A log-sphere fold changes its field across spheres: each shell holds the
prototype at its own scale, and the shell boundaries are spheres about the
fold's local origin. When every operation before the fold on its chain is a
translation, a rotation or a uniform scale, `SdfProgram` writes the origin into
the fold's instruction and the boundaries are spheres in world space too. When
a warp sits before the fold, the boundaries are bent, and the march knows only
the distance to them. A field value measured on one side of such a wall says
nothing about the other, so `map()` publishes the walls around each sample: the
nearest log-sphere shell whose boundaries are world spheres, and the distance
to every other log-sphere wall. No wall has a floor.

The fine marches (primary, soft shadows and the overshoot view) step through
one rule, `sdfMarchAdvance` in `field/sdf-map.hlsli`:

- A step that stays inside every wall's distance is unchanged, so a sample far
  from every wall pays one comparison.
- A step that would leave a shell with sphere walls stops where the ray meets
  the wall. When the sample's own side is clear that far, the march crosses:
  it lands just past the wall, by at most the march's own acceptance distance,
  and samples the other side there before it steps again. Geometry on the far
  side within that sliver is within the acceptance distance of the landing
  sample, so the landing accepts it. When the near side is not clear to the
  wall, the step is that clearance, which stays inside the shell.
- A wall known only by its distance is passed by at most the acceptance
  distance, which makes the same no-skip argument. Near such a wall a march
  advances at least that distance a step, so a grazing ray spends more steps
  there than at a sphere wall.
- A crossing is one step of the march's budget. A line meets a sphere at most
  twice, every sample of a ray reads the same two intersections, and a crossing
  lands strictly past its own, so a march crosses each shell wall at most
  twice, in and out: two for each shell boundary the ray meets.

The beam's cone keeps every log-sphere wall in its clearance and stops its
proof at them. A ball proof, such as a reprojected march seed, reads
`sdfMapBallClearance`, which stops at the nearest wall. Soft shadows still
stride through an occluder thinner than their minimum stride on either side,
but never across a wall. The rule is exact up to the float rounding of the
wall test itself: a grazing ray whose landing rounds back onto the side it left
crosses again from there, a tolerance further on.
`SdfLogSphereMarchDeviceLawTests` holds the marches on the device, and
`SdfLogSphereCrossingLawTests` holds a CPU reference march of the rule, over
both kinds of wall, to its no-skip property and its crossing bound.

Plain repetition is exact only when the prototype fits within its centered
cell. Cell jitter also requires conservative spacing; containment does not
guarantee that the folded cell contains the nearest displaced copy.

## Procedural detail

Bound-preserving noise must provide all of the following:

- a deterministic integer hash or sequence;
- a known output range;
- a conservative derivative bound;
- an explicit effect on `AnalyzeLipschitz`; and
- matching results across shader targets within the configured parity policy.

`NoiseDisplace` is the shipped instance: an integer-only PCG3D hash per
lattice corner, output host-normalized to `[-1, 1]`, a quintic-blend gradient
bound (`frequency·(15/4)·√3` per normalized octave sum) folded by
`AnalyzeLipschitz` into the step clamp, and cross-backend agreement inside the
relaxed parity envelope (isolated silhouette winner flips only). The
sine-product `Displace` remains the hash-free periodic sibling.

A field op emitted in a SHAPE-FREE chain (its own `ResetPoint` + transform
prefix, no shape—the spelling the creation-level `noise` facet's stamp
emission uses) never reaches a `ShapeBlend` compose, so the chain-product path
alone drops its factor. `AnalyzeLipschitz` pass 2 therefore folds a shape-free
chain's `Displace`/`NoiseDisplace` factor additively at the op's own
instruction site (`|∇(f + g)| ≤ L_f + L_g`), inside the scope the op acts on —
a `Union` pop then keeps the addition instance-local (max across instances)
instead of summing it across every stamped copy.

Two further consequences of scoping the analysis:

- **The per-scope step clamp.** A scope's own Lipschitz bound bakes onto its
  `PopField` as a `1/L_scope` candidate scale (the instruction's free
  `Data1.y` lane; zero = unpatched). The interpreters multiply the scope's
  field by it at the pop: a positively scaled distance keeps its zero set, and
  `(1/L)·f` of an `L`-Lipschitz `f` is exactly 1-Lipschitz, so scoped
  relief and warps never tax the global `stepScale`. Anything unscoped still
  folds globally. A scope is a per-sample cost of its own: a scoped segment
  never takes the segment early-out and is never rigid-planned. No primitive
  needs one—every shape body is 1-Lipschitz, and an ellipsoid (including a
  non-uniformly scaled sphere) is the superellipsoid's exact exponent-2 gauge
  `(|p/r| - 1)·min(r)`, whatever its eccentricity.
- **Continuous relief.** `Displace` and `NoiseDisplace` evaluate their basis
  at every distance in both the scalar and gradient interpreters. Replacing
  relief with `-|amplitude|` outside a band gives a lower value, but the jump
  at the band's edge is not a continuous field. Footprint-based hits can
  land there, and nearby normal and curvature samples then produce visible
  rings and speckles. A future acceleration must keep a conservative step
  bound separate from the field used for hits, materials and derivatives.

Visual plausibility is not evidence of a safe distance estimate. Validate new
field operations with grazing rays, fold boundaries, thin geometry, and a
strict/reference march comparison.
