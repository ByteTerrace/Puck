# Puck.World.Authoring

The authored-content document families `Puck.World` embeds inline:
`puck.creation.v1` (`CreationDocument`/`CreationCanonicalizer`) and
`puck.music.v1` (`MusicDocument`), both
riding the shared `DocumentCanonicalizer` core in `Puck.Assets` — which also
owns the `puck.audio.v1`/`puck.synth.v1` families (`Puck.Assets.Documents`),
so the ROM forges can consume them without a world assembly. `CreationFrame`
and `GridSnap` live here too. Host-side float on purpose —
authoring/presentation math, outside the simulation-state determinism
contract.

The SM83 ROM forge is `Puck.HumbleGamingBrick.Forge` (see the `rom-forge`
skill); the AGB forge is `Puck.AdvancedGamingBrick.Forge`.

## The creation author frame

Every `puck.creation.v1` position, rotation, and camera offset is authored in
ONE frame — right-handed, +Y up, +Z the front a shape faces, +X screen-right
when looking at that front — a 180° yaw about +Y away from the engine's own
frame (+Y up, −Z forward). `CreationFrame` is the one place that crosses
between them; nothing else in the document or the engine names either frame.

The primitive vocabulary and its dimensions belong to `Puck.SignedDistance`:
`SdfSolidPrimitive` names the shapes and `SdfSolidGeometry` is the one place
that decides a primitive's unit shape, so an authored `scale` of `(1,1,1)` is
the primitive's unit size. `CreationGeometry` keeps only the document-shaped
half — the reach a whole creation implies, shapes and text runs together.

| Primitive | Unit shape | `scale` reads as |
|---|---|---|
| Sphere | r = 1 | radius |
| Box | half-extents (1,1,1) | half-extent per axis |
| Capsule | r = 1, endpoint (0, 0.5, 0) | `x`/`z` = radius, `y` = cylindrical section length (total height = 2·radius + length) |
| Cylinder | r = 1, half-height 1 | `x`/`z` = radius, `y` = half-height |
| Cone | base r = 1, half-height 1, apex r = 0 | `x`/`z` = base radius, `y` = half-height |
| Ellipsoid | radii (1,1,1) | radius per axis |
| RoundCone | lower r = 1, upper r = 0.5, height 1 | scaled per axis |
| Torus | major 1, minor 0.4 | scaled per axis |
| Prism | XY profile extruded along Z | profile X/Y half-extents, extrusion half-depth |
| Superellipsoid | radii (1,1,1), `exponent` (default 2) | radius per axis |
| Sweep | a quadratic Bezier `curve` (creation-unit control points/radii, NOT a unit shape) | must be uniform; bakes onto the curve's own lengths |

A `Prism` shape's optional `taper` is its top width divided by its bottom width:
0 gives a triangle, 1 a rectangle, and omission uses 0.5. Rotate the shape to
reverse or redirect the taper. This makes tapered armor plates and roof sections
without assembling them from overlapping boxes. `dilate` adds a rounded edge;
its radius expands the authored profile rather than cutting a flat chamfer.
An optional `profile` selects `RoundedRectangle` (`cornerRadius` is a fraction
of the smaller half-extent), `Polygon` (`sides`, 3–32), `Ellipse`,
`ChamferedRectangle` (`cornerRadius` maps to a 45-degree bevel radius instead
of a fillet), or `Convex` (`vertices`, 3–8 clockwise points in local XY
inside the unit square — each coordinate in [-1, 1], the frame every profile
is scaled from and the frame the prism's cull reach covers — convex and
non-degenerate; `cornerRadius` is a fraction of the raw vertices' own
inradius, the profile's one rounding control). Omission uses `Trapezoid`.
Extrusion caps stay flat; corner rounding or chamfering shapes the outline,
while `dilate` rounds the depth edges too. Polygon and ellipse profiles scale
their canonical outline in X/Y; a convex profile's vertices carry their own
shape and ride the shape's scale the same way. These are existing VM
instructions (a convex profile's vertices live in a side table the packed
program's own word stream carries), not meshes or additional shader opcodes.

A shape's optional `chamfer` (Box, Cylinder, and an extruded Prism only) cuts a
flat 45-degree bevel — the edge treatment `dilate` explicitly does not — by a
world-unit radius, refused by name past `SdfSolidGeometry.MaxChamfer` and
refused by name alongside a nonzero `rounding` on the same shape. A Box emits
as an extruded `ChamferedRectangle` at the plain Box's own outer extent and
with its conventional edge fillet on top of the chamfer (so its collider and
its `chamfer: 0` limit are the plain Box), a Cylinder as a revolved one; a
Prism's chamfer instead bevels its existing profile's cap rims
(RoundedRectangle, Trapezoid, or Ellipse profiles — a Polygon or Convex
profile's lanes are already spoken for and reads a zero ceiling). A cap
chamfer never erodes the profile, so its ceiling is the profile's true
inradius and the extrude half-depth: a triangular prism admits a chamfer
where it admits no rounding.

A shape authored `type: "Superellipsoid"` generalizes `Ellipsoid` with an
`exponent` field (finite in [2, 8]; null = 2, the ellipsoid limit — the two
spellings then agree bit-for-bit). Larger exponents round the solid toward a
box. Exact and 1-Lipschitz for the whole admitted range, so — unlike
`Ellipsoid` — it earns no separate march correction.

A shape authored `type: "Sweep"` requires a `curve` (`ShapeCurveDocument`): a
quadratic Bezier `a`/`b`/`c` (each a literal `[x,y,z]` or a
`state.<row>[.<key>]` reference) swept with a radius tapering between
`radiusStart`/`radiusEnd` plus a mid-span `bulge`, optionally 1-4 `strands`
orbiting the curve at `strandOffset` and rate `twist` — the study's hair locks
(one strand) and braid (three, `twist: 4`). `curve` is admitted only on, and
required on, this type; the curve's own control points and radii already carry
creation-unit dimensions, so `scale` must be uniform. Not a closed solid: no
collider, no panel/trims/flare/shear/bumps/domain. `bulge`/the radius taper/
`strandOffset` are each capped as a ratio to the authored radii, past which the
shape's field can no longer be proven conservative — see the sdf-world skill's
`Sweep` row for the exact ratios and what they guard.

Animated and static stamps use the same profile. The deterministic field
supports trapezoids, rounded rectangles, chamfered rectangles, convex
profiles, and superellipsoids; polygon and ellipse profiles are refused for
field contact until that interpreter supports them. The analytic contact
provider retains conservative primitive bounds.
Character looks do not replace the body's authored collision kit.
World stamps admit up to `WorldPlacementPolicy.MaxShapesPerStamp` shapes
(367), including expanded text glyphs, with a panelled shape charged as 2 and
each of a shape's trims charged 2 more. This is headroom for authored detail,
not a requirement to fill every slot.

## Animation: drivers, waveforms, joints

A creation animates itself from three composable parts, none of which names a
creature or a vehicle in engine code.

In a world, a placed creation with drivers uses the animated stamp pool even
without timeline frames. Its root pose supplies motion signals; time and shared
state signals work without an inhabitant. Such a placement has no body facts or
body-index state key. Animated placements retain the pool's shape and registration
limits and cannot use creation noise, distribution or mirror facets.

A creation-level `drivers` list (≤ 8) declares the **driver** — a scalar signal
read off the body the creation is stamped on, times a cadence, gated by a
conjunction of condition tokens. Each driver yields a phase φ and an eased weight
w ∈ [0, 1]; w eases toward 1 while every `when` token holds and toward 0
otherwise, over 0.15 s, and a driver at rest (w = 0) stops advancing.

`blendInSeconds` and `blendOutSeconds` override that exponential time constant
independently for each driver. Both accept finite, non-negative seconds; zero
snaps on the next positive frame delta. These are time constants, not clip
durations: about 95% of a transition completes after three time constants.
For example, a quick body pose and a slower wing assembly can read the same
`Airborne` gate through separate drivers. This changes presentation only.

```json
{ "name": "stride", "signal": "planarTravel", "cadence": 8.0, "when": ["Grounded", "moving"] }
```

| `signal` | Reads | Phase |
|---|---|---|
| `planarTravel` | horizontal rendered travel, m | integrates: φ += cadence · Δ, wrapped mod 2π |
| `travel` | total rendered travel, m | integrates |
| `time` | elapsed presentation time, s | integrates |
| `speed` | total rendered speed, m/s | sets φ = cadence · value |
| `verticalSpeed` | rendered vertical speed, m/s (positive rises) | sets |
| `turnRate` | rendered yaw rate about world up, rad/s | sets |

An integrating driver charges at most `WorldGaitDrivers.MaxTravelPerFrame` of
travel per frame, so a teleport cannot spin a limb through dozens of cycles.

`when` is one token or an array of tokens that must all hold (a bare string reads
as a one-token gate and canonicalizes to the array form; absent is ungated, ≤ 4
tokens). A token is:

| Token | Holds while |
|---|---|
| a `Puck.Physics.Motion.BodyFacts` name — `Grounded`, `Airborne`, `Rising`, `Falling`, `InMedium`, `AtMediumBand`, `HoldingUnwalkable`, `Unsupported`, `AffectedBy` | the simulation publishes that fact |
| `moving` | the body's eased rendered speed is above `WorldGaitDrivers.MovingSpeed` (0.05 m/s) |
| `still` | the negation of `moving` |
| `always` | unconditionally — refused alongside any other token |

`moving`/`still` are derived by the client from the rendered pose, low-passed
over the same 0.15 s the weight eases over so the gate does not flicker at the
threshold. They are why a walker gated `["Grounded", "moving"]` returns its limbs
to vertical when the body stops without the simulation publishing anything;
gated on `Grounded` alone it would hold its last stride pose instead.
`moving` with `still` is refused — the gate could never hold. A token naming
no fact is refused by the world validator, which alone sees both vocabularies,
rather than gating the driver off silently.

A shape's `swings` (≤ 4) and `slides` (≤ 4) name a driver and turn (φ, w) into
motion — the **joint** — through a **waveform**:

```json
"swings": [{ "driver": "stride", "pivot": [0.5, 1.25, 0], "axis": [1, 0, 0],
             "amplitude": 0.6, "phase": 3.14159, "wave": "sine" }]
"slides": [{ "driver": "swell", "axis": [0, 0, 1], "amplitude": 0.05, "wave": "sine" }]
```

A swing turns the shape about `axis` at `pivot` by
`amplitude · wave(φ + phase) · w`; a slide displaces it along `axis` by the same
scalar. `wave` is `sine` (the default), `constant` (1 whatever the argument, so
the facet is `amplitude · w`: a POSE the driver's gate blends in — arms raised
while climbing — rather than a cycle), `halfSine` (`max(0, sin)` — a knee or
an elbow bends one way, so it takes this and a phase that puts the lobe on the
swing-through), or `linear` (the identity on its argument — a wheel or a rotor
takes amplitude 1 so the cadence alone reads as radians per metre or per
second). Character comes from the world, not the document: a driver's `cadence` and a
facet's `amplitude`/`phase` may be a `state.<row>[.<key>]` reference to a
numeric cell (resolved by the containing world like every other document
reference — a `draw` site rolls it at boot, a console write retunes it live);
a driver's `signal` may be `state.<row>[.<key>]`, whose value at the frame's
tick IS the phase (times the cadence) — a `cycle`-trait row is a clock every
client and every replay agree on; and `wave` may be `curve:<row>`, sampling the
world's `curves` row by arc fraction (Z is the value) so the shape of a motion
is drawn, not typed. The world validator refuses a curve or signal row the world
does not declare. A shape's `parent` names an EARLIER shape whose motion carries it,
pivots included: a forearm swung at the elbow with `"parent": "upperArmLeft"`
also rides the upper arm's swing at the shoulder, and a hand parented to the
forearm rides both — the skeleton is the parent chain, and a chain resolves in
declaration order so it can never cycle.

Worked rigs, all built from the same three parts:

| Rig | Driver | Facet |
|---|---|---|
| Walker limbs | `planarTravel`, cadence 8, `when: ["Grounded", "moving"]` | swings, axis X at the shoulder (y ≈ 1.25, x ±0.5) and the hip (y ≈ 0.45, x ±0.3), amplitude 0.6, contralateral (left arm φ+π, right arm φ, left leg φ, right leg φ+π) |
| Climber limbs | `travel`, cadence 8, `when: ["HoldingUnwalkable", "moving"]` | the same joints, axis Z, amplitude 0.5, diagonal pairs (left arm φ, right arm φ+π, left leg φ+π, right leg φ) |
| Wheel | `planarTravel`, cadence = 1 / wheel radius, `when: ["moving"]` | swing, axis X at the hub, amplitude 1, `wave: linear` |
| Rotor | `time`, cadence = radians per second, `when: always` | swing, axis Y at the mast, amplitude 1, `wave: linear` |
| Fish tail | `time`, `when: ["InMedium"]` | swing, axis Y at the tail root |
| Bobbing hull | `time`, `when: ["AtMediumBand"]` | slide, axis Y |
| Breathing chest | `time`, `when: ["still"]` (an idle breath) | slide, axis Z, small amplitude |

Every one of these is presentation-only: the facets are read where a body-rooted
stamp's per-frame transforms are packed (`Puck.World.Client.WorldStampPool`) and
nowhere else, so the emitted SDF program, the analytic colliders, the compiled
solid field, and simulation state are all blind to them. `CreationFrame` carries
a swing's `pivot` and both facets' `axis` across the author frame; a half turn
about +Y is a proper rotation, so a rotation axis takes the same
`(−x, y, −z)` flip a direction does and the amplitude, phase, and waveform are
unchanged. A shape carrying `domain` operators admits a `parent` but never a
facet of its own: the animated pool packs its slot with the rigid delta its
parent's chain imparts to creation space (identity with no parent) and applies
the domain ops against that carried frame before the shape's own static
rest pose, so the fold plane travels with the parent — a fold rides its
parent's frame, never its own swing, and the combination with an own
`swings`/`slides`, a named `frames` entry, an effector-chain bone, or a look's
`partDynamics` follower is refused by name. Only driver/effector motion reaches
it through the chain: a `frames` pose replaces the parent's BASE pose, which is
not part of the delta chain, so a frame-posed parent moves without its fold
(exactly as it moves without any other child the frame does not itself name).
The static stamper ignores `parent` entirely — a static placement carries no
dynamic-transform buffer for a delta to ride.

## Effectors: chains, targets, planting

For a limb that must lift clear during its swing, author `plant.swingWeight: 0`.
Outside the plant window, contact influence eases out while the named driver is
active; when that driver returns to rest, surface following returns so both feet
can settle. Omission keeps continuous target following. Intermediate values in
[0, 1] retain partial contact influence during the swing. Releasing the effector's
`when` gate clears its latched world point, so a later landing acquires a fresh one.

A creation's `effectors` list (≤ `CreationDocument.MaxEffectors`, 8) corrects the
driver-posed skeleton so a named tip reaches a target. The drivers still decide the
pose; the solve bends it.

```json
"effectors": [
  { "name": "handLeft", "chain": ["upperArmLeft", "forearmLeft"], "tip": "handLeft",
    "target": { "kind": "surface", "direction": [0, 0, 1], "reach": 0.6, "standoff": 0.03 },
    "when": ["HoldingUnwalkable"], "weight": 1.0 },
  { "name": "footLeft", "chain": ["thighLeft", "shinLeft"], "tip": "bootLeft",
    "target": { "kind": "surface", "direction": [0, -1, 0], "reach": 0.5, "standoff": 0.05 },
    "when": ["Grounded"], "plant": { "driver": "stride", "window": [0.0, 3.14159] } }
]
```

`chain` names the bones root→tip. Each must DESCEND from the one before it through
`parent`, and `tip` must be the last bone or descend from it — that is what makes the
chain one limb rather than a list of shapes. A bone's **joint** is the pivot of its
first `swings` entry, its authored `joint: [x, y, z]` when it swings nothing, and its
own position when it has neither. Two bones close analytically, bending in the plane
the driver-posed limb already bends in, so the authored pose decides which way an
elbow or a knee folds; three to eight bones — a tail, a tentacle, a spider leg with a
coxa — sweep by cyclic coordinate descent, stopping early once the tip is within
`WorldEffectorSolver.ReachedTolerance`.

| `target.kind` | Reads |
|---|---|
| `surface` | marches the client's shared static-scene query field from the posed tip along `direction` (author frame, so it turns with the body) up to `reach`, and places the tip `standoff` off the hit along the surface's own normal. A miss eases the correction out. |
| `body` | another population entity's root pose (`index`) plus `offset`, rotated into THAT body's attitude — a hand on a carried crate stays on its corner as it turns. |
| `state` | a `state.<row>[.<key>]` text cell spelling a world-space `[x, y, z]`, read at the frame's tick. |

`when` is the same gate a driver takes, eased over the same
`CreationDriverDocument.WeightSeconds`, and `weight` is a constant ceiling in [0, 1] on
top of it. The eased weight blends the GOAL rather than the solved pose — at weight w
the tip is asked for a point w of the way to the target — so a released effector eases
back onto the driver-posed limb through poses the chain can hold.

`plant` is the contact latch: while the named driver's wrapped phase is inside
`window` (radians, each end in [0, 2π); a `from` past its `to` names the interval
through the phase origin), the world target is held where it was when the window
opened. A quadruped's stance, a climber's hand on a hold, and a tentacle tip gripping
while the trunk sways are one mechanism with different windows. A teleport or a reused
body slot drops every latch, exactly as it reseeds a follower.

| Rig | Chain | Target | Plant |
|---|---|---|---|
| Climber's hands | `["upperArmLeft", "forearmLeft"]`, tip the hand | `surface`, `direction: [0, 0, 1]` (the body's own front), reach 0.6, standoff the palm's thickness | one per hold, windowed on the reach driver |
| Walker's feet | `["thighLeft", "shinLeft"]`, tip the boot | `surface`, `direction: [0, -1, 0]`, reach a stride's clearance, standoff the sole | windowed on the stride driver, contralateral halves |
| Tail | four to eight segments, tip the last | `state`, a point a rule publishes | none — a tail tracks, it does not latch |
| Spider leg | `["coxaLeft1", "femurLeft1", "tibiaLeft1", "tarsusLeft1"]` | `surface`, `direction` the body's own down, which on a ceiling points UP the world | windowed per leg on a shared gait driver |

Effectors are presentation-only on exactly the terms the swings are: the correction is
folded into each bone's own per-frame delta in `Puck.World.Client.WorldStampPool` and
read nowhere else. `CreationFrame` carries a probe `direction`, a body `offset`, and a
shape's `joint` across the author frame; the reach, standoff, weight, and window are
frame-invariant. A bone carrying `domain` operators is refused, on the same grounds a
swing on one is.

A `surface` target probes the field built from the world's SOLID placements only — the
same evaluator the chase camera's clearance sweep reads. A presentation-only placement
(a wallpaper-folded ground texture) is not in it, and neither is any body: a limb probes
what a body could stand on, never its own geometry or the decoration over it. A world
whose solid placements carry render-only warps admits no fixed-point query at all; every
probe there misses and every effector over one eases out.

`body.rig [body]` is the read-back: per driver its phase and eased weight, per effector
its weight, whether its latch is holding, and the world point its tip is being asked for
(`target=(x, y, z)`, or `none` when nothing resolved). It reads the pool's latched
values, never a fresh advance, so a piped run can fence twice and assert a planted
target is unchanged while `body.where` moved.

## Shape domain operators

`ShapeDocument.Domain` (`ShapeDomainOp`) is a `$type`-discriminated, ordered
list mirroring `SdfProgramBuilder`'s domain-operator family, applied in
creation space — after the placement/creation frame chain, before the
shape's own translate/rotate/scale. An absent/empty list is a no-op and
keeps a creation's canonical bytes and hash unchanged.

The render path applies them as point folds. The contact paths — the analytic
collider set and the fixed-point solid field — take the rigid copies
`SdfDomainExpansion` derives instead, so contact carries every copy the fold
draws. An op with no expansion is refused by name on a solid placement.

| `$type` | Builder call | Contact |
|---|---|---|
| `symmetry` | `SymmetryPlane(normal, offset)` | 2 copies |
| `repeat` | `RepeatLimited(spacing, limit)`, sandwiched between a translate to and from `origin` | one copy per lattice cell; needs a whole-number `limit` (an absent one is unbounded and refuses) |
| `polar` | `RepeatPolar(count, axis, mirror, materialStride)`, sandwiched between a translate to and from `origin` | `count` copies, doubled when `mirror` is set |
| `wallpaper` | `WallpaperFold(group, cell, limit, plane, materialStride, lodDistance)` | none — refused on a solid placement |

`repeat`/`polar` carry an optional `origin` (creation units; null = the creation
root, unchanged behaviour) — the point their fold centres on instead of the
root, so an off-centre lattice or pivot does not saturate its cell/sector
clamp against the wrong boundary. A repeat's physical copies sit at the same
offsets from the fold regardless of `origin` (a translation's copies are
origin-invariant); a polar's pivot moves with it, and its render-bound reach
widens by twice the origin's distance from the root (`ShapeDomainOps.Reach`).
`SdfDomainExpansion` applies the same origin to the rigid copies it derives,
so a solid placement's colliders land where the render's fold does.

Copies compose across the list, capped by `SdfDomainExpansion.DefaultCopyBudget`.
Expansion is exact only for a prototype inside the fold's fundamental domain:
on a symmetry plane's positive side, inside a repeat's centre cell, between a
polar sector's walls. A prototype straddling a wall renders clipped and
collides whole.

## Field ops: Dilate, Onion, and the blend radius

`ShapeDocument.Dilate`/`Onion` (an inflation radius, a shell thickness) and
`Smooth` (the blend radius against the field accumulated before the shape) are
creation units, but unlike `Rounding`/`Chamfer` they are never baked into the
primitive's own local geometry — `SdfOp.Dilate`/`Onion` act on the running
WORLD-space accumulator directly, and a `ShapeBlend` instruction's blend
radius composes the shape's already-world-scaled candidate into it, so
neither is reached by a preceding `Scale` chain op the way a baked-local
modifier is. Both emission paths therefore multiply them by the placement
scale explicitly before emission (`CreationStampEmitter.EmitShapeChain` for a
static placement, `Client.WorldStampPool.EmitShape`/`EmitGroup` for the
animated pool) rather than relying on the chain's own `Scale` op. `Dilate`/
`Onion` on an otherwise scope-free shape open the same one-deep field scope a
panel or an eccentric primitive would (below) — unscoped, they would inflate
or hollow every shape emitted before them in the whole program, not just this
one.

## Round seams and cellular relief

`GrooveUnion` removes a round tube along the intersection of two fields;
`PipeUnion` adds that tube to their union. Both use `smooth` as the radius
in creation units. With incoming distances a and b and h = sqrt(a²+b²),
groove is max(min(a,b), r-h), and pipe is min(min(a,b), h-r).
There is no separate groove-width field. Radius measures the tube in field
coordinates; its physical cross-section depends on the angle between surfaces.

A panel seam uses two overlapping Box shapes pitched toward each other:
give them positions [-0.6,0,0] and [0.6,0,0], scales [1,1,0.3], and Y
rotations +0.36 and -0.36 radians. The first uses Union; the second uses
`"blend": "GrooveUnion", "smooth": 0.12`. Their intersecting front faces
carry a recessed line.

A pipe-joint bead uses two Cylinder shapes of scale [0.65,1,0.65], at
[0,-0.25,0] and [0.45,0.35,0]. Rotate the second by pi/2 about Z and give
it `"blend": "PipeUnion", "smooth": 0.28`.

A shape's `cells` object adds actual surface relief:

```json
{
  "amplitude": 0.28,
  "frequency": 4,
  "mode": "F1",
  "randomness": 0.46,
  "seed": 3751
}
```

Attach this object as `cells` on a unit Sphere to produce a cellular
surface. For ridged cell boundaries, use `mode: "F2MinusF1"`,
`amplitude: 0.24`, and `randomness: 0.2`.
Frequency is cells per creation unit; amplitude is field displacement in
creation units. The emitter restores the shape's rigid frame after emitting
its geometry and before sampling the relief, including on warped shapes.
Placement scaling preserves the pattern by converting frequency inversely
and amplitude directly.

Randomness is the side length of the centered feature-position box inside
each unit lattice cell. F1 admits [0,0.46]; F2MinusF1 admits [0,0.20].
These conservative ceilings keep both nearest features inside a fixed
27-cell neighborhood. Frequency must be finite in (0,8], amplitude in [0,4],
and 1 + amplitude*frequency*L must not exceed 8, with L=1 for F1 and
L=2 for F2MinusF1. Invalid fields are refused by name.

Flare, shear, bumps, and erode take a per-shape clamp when a field scope is
available. Inside a creation or group scope, the same warp stays on its own
shape chain, while the enclosing scope's clamp applies to all its siblings.
Static erosion is emitted in either scope form and reads zero-valued lanes;
live state-driven erosion requires a body's dynamic lanes.
Panels, trims, and cells require field isolation for their operation and are
refused when that scope is unavailable. This is a semantic restriction;
warps sharing a conservative bound remain valid.

`puck creation stats --world <path> --prototype <id>` lists warp-bearing
shapes and the actual global and scoped clamps for unit-scale static and
pooled rest geometry. `world.budget` reports the live global scale, count of
scoped/shared clamps, and the worst scope's scale, instruction range,
instance, and shape count. A global scale of one can coexist with strong
shared clamps; their factors are field bounds, not measured GPU cost.

Cells require a closed primitive and a per-shape field scope. Sweep,
grouped shapes, detail-only shapes,
and creations that need an outer field scope are refused; use separate
prototypes when combining these features in a scene. Authoring cells affect
render geometry; solid-placement colliders retain the base primitive.
The low-level `CellDisplace` op also has a deterministic fixed-point evaluator.

## Shape panel (second-material inset)

`ShapeDocument.Panel` (`ShapePanelDocument`) is a second-material inset face
region: an eroded copy of the same primitive, offset along a local `face`
(a direction, normalized at canonicalization; null = `+Z`; zero-length or
non-finite refused by name) and composed with its own `material` — positive
`depth` recesses it (Subtraction, the floor exactly `depth` below the plate's
face) and negative raises it proud by exactly `|depth|` (Union). `inset` and
`depth` are creation units, scaling with the placement on both emission paths
(the static stamper's own `Scale(transform.Scale)` chain op reaches them
automatically; the animated pool multiplies them by the placement scale
explicitly before resolving). `inset` erodes the copy on every local axis
(`shape scale − inset`,
floored above zero — a sharp-cornered shrink, not a rounded Minkowski erosion,
since nothing can isolate a `Dilate` field op to the copy alone inside the
shared scope below), clamped to the shape's smallest local half-extent
(`SdfSolidGeometry.HalfExtent` over X/Y/Z); `depth` clamps to `±2·h′`, `h′`
being the ERODED copy's half-extent along `face` — deeper, a recess is an
enclosed void and a raise a detached slab. `ShapePanelDocument.Resolve` is the
one derivation both paths emit from: with `h` the plate's half-extent, a recess
translates the copy by `h + h′ − depth` and a raise by `|depth| + (h − h′)`.
`CreationDocument.StampShapeCount` charges a panelled shape as 2, and so does
`CreationStampEmitter.PerCopyInstanceCount` (the static per-shape probe
reservation: the copy's own transform chain and shape need a second probe
chain's words).

Both emission paths open a one-deep field scope
(`SdfProgramBuilder.PushField`/`PopField`) around `[plate, panel]` so the
panel's compose bites only this shape, never a sibling occupying the same
space — `CreationStampEmitter.EmitShapeChain` (static placements) and
`Client.WorldStampPool.EmitShape` (the animated stamp pool) — and emit the copy
from its own `ResetPoint` chain rather than after the plate's shape
instruction, whose emission may leave a persistent `Scale` op behind. Because
a field scope nests no deeper than 1, a panel is refused by name wherever that
scope would already be spent: a `Plane` (no meaningful face), a domain-folded
shape, a grouped shape (the pool's own group scope), and a creation whose OTHER
shapes force `CreationStampEmitter.RequiresScope` (a non-Union blend, an
engraved text run, or a noise facet) — the static path then shares one scope
across the whole creation, leaving a panel nowhere of its own to nest.

Render-only, like `Domain`: `CreationStampEmitter.EmitFixed`/
`VisitFixedPrimitiveCopies` never read it, so a panelled solid placement's
collider is identical to the same shape with no panel. Verified by
`ShapePanelLawTests`.

## Shape trim (second-material band against a reference)

`ShapeDocument.Trims` (`ShapeTrimDocument[]`, at most `ShapeTrimDocument.MaxTrims`
= 4) paint a second-material band onto a shape's own surface wherever it sits
near another, EARLIER-declared shape in the same creation — `Shape` names it
by `ShapeDocument.Name`, mirroring `Parent`'s own "declared before" rule. A
Subtraction cutter riding the host's own `Group` is the common case (the
carve's own edge becomes the trim's guide); a plain plane-like Box with blend
Union works too. Only the reference's own primitive geometry (type, scale,
taper, profile, lift, rounding, chamfer) and pose are re-read — its own
blend, panel, and trims play no part, and a reference carrying `Domain` ops
or a non-zero `Group` is refused by name (its copy is re-emitted from its own
slot and authored pose alone, so a fold's images or a group's chain could not
be followed). `Width` (creation units, finite
and positive) is how far outward from the reference's own surface the band
reaches; `Material` is the band's own palette slot; `Inset` (creation units,
`[0, ShapeTrimDocument.MaxInset]`, default `0.003`) is described below.
`CreationDocument.StampShapeCount` charges each trim as 2 (the host's own
copy plus the reference's own copy), matching `Panel`'s own convention.

Each trim opens a scope of its own (`SdfProgramBuilder.PushField`/`PopField`)
AFTER the host's own emission — sequential per trim, never nested (a shape
carrying several trims opens and closes one scope per trim in turn). The
scope composes two candidates against the host's own already-present, plain
instance: the host's own copy, nudged narrowly CLOSER than the plain surface
by `Inset` — a real `Dilate` field op at a POSITIVE radius, exact and
isolated since nothing else has joined the scope's accumulator yet — then
the reference's own copy, its own scale grown by `Width`
(`ShapeTrimDocument.DilatedScale`, the mirror image of a panel's own eroded
scale — a sharp-cornered growth, not a rounded Minkowski dilation), composed
with blend Intersection. The scope pops via Union into the SAME accumulator
the host's own plain shape already folded into: since `max(a, b) >= a`, a
genuine EROSION of the host copy (a negative `Dilate` radius) is provably
always beaten by that already-present plain surface and would never render —
`Inset` growing the copy outward instead is what lets it win narrowly, and
only near the reference (Intersection with the reference's dilated copy does
not additionally push the candidate past the plain surface there); away from
the reference it loses by default, since `Inset` is small.

Refused by name for the same reasons `Panel` is: a shape carrying `Domain`
ops or a non-zero `Group` (both leave the trim's scope nowhere of its own to
open), and a creation whose OTHER shapes force
`CreationStampEmitter.RequiresScope`. Render-only, like `Panel`:
`CreationStampEmitter.EmitFixed`/`VisitFixedPrimitiveCopies` never read it, so
a trimmed solid placement's collider is unchanged. Verified by
`ShapeTrimLawTests`.

## Shape detail (shading-only geometry)

`ShapeDocument.Detail` (bool, null = false) marks a shape SHADING-ONLY: the
SDF VM's march (the beam, the fine march, shadow, AO) skips it entirely — it
never carves the silhouette — and includes it only in the shading evaluation
at an already-found hit, where its own material and its perturbation of the
surface normal paint its footprint. A thin subtraction against a plate reads
as a seam; a small union carrying its own material reads as a rivet; either
stays a crisp mark at any distance instead of the dots a march-carved groove
thinner than the footprint-relative acceptance produces.

Both emission paths (`CreationStampEmitter.EmitShapeChain`,
`Client.WorldStampPool.EmitShape`) thread `Detail` into
`SdfSolidGeometry.AppendScaledPrimitive`'s emission of the shape's OWN
instruction only — never into a panel's or a trim's second copy, so `Detail`
is refused by name alongside `Panel` or `Trims` on the same shape rather than
leaving those copies' own detail visibility undescribed.
`CreationStampEmitter.EmitFixed`/`VisitFixedPrimitiveCopies` skip a detail
shape outright (no collider, not even a plain one — unlike Panel/Trims, which
emit their host shape's own collider unchanged and only skip their second
copy), and `WorldDefinitionValidator`'s solid-placement collider count does
the same. Verified by `ShapeDetailLawTests`.

## Bounded volumes (`volumes[]`)

`CreationDocument.Volumes` declares bounded participating media beside
the shapes. Each `flow` uses an oriented `halfExtent` box, with its mouth
at +Y and advection toward -Y. A `cloud` fills a smooth ellipsoidal envelope
inside that box with advected three-dimensional noise. The renderer integrates
both on sky rays and after opaque shading, clipped against opaque depth;
neither creates a collider. `enabled: false` omits a volume from emission.

A volume declares `position`, `rotation`, `halfExtent`, and a `ramp`
of one to four ascending `{ density, color }` stops. The ramp maps local
density to emission before integration. `axis`, `width`, `speed`,
`seed`, `steps`, `intensity`, `extinction`, `pulseAmplitude`, and
`pulseFrequency` control its shape, motion, and integration.
Optional `intensityLane` selects anonymous render lane 0..3; omission
uses unit gain. Zero extinction uses the transparent integration limit.

For clouds, `width` is the noise-cell size, `coverage` (default 0.55) is in
[0, 1], and `softness` (default 0.18) is in (0, 1]. `axis` is flow-only;
`coverage` and `softness` are cloud-only. Clouds use the ramp and a height tint
for illumination, without cloud shadows or multiple scattering. They are
bounded media, independent of the sky's two-dimensional cloud layer.

Optional `parent` names a shape frame; omission uses the creation root.
The frame and all creation-unit lengths follow placement scaling.
`CreationCanonicalizer.ValidateVolumes` refuses invalid values and more
than `SdfProgramBuilder.MaxVolumes` entries. The frame budget is 64 volumes
shared by all placements and bodies, not 64 per character. World emission
keeps the first 64 enabled entries; `world.budget` reports the submitted count.
Stay within that budget when composing a scene. Overlapping volumes composite
in entry-distance order; their densities are not integrated as a combined medium.

## Sculpting: authoring creations in code (`Sculpting/`)

A creation may be authored in code instead of by hand — the document stays
the source of truth either way, since a sculpt only ever produces a patch
over it. `CreationBuilder` is a fluent, allocation-light builder over
`CreationDocument`: `Shape(...)` authors one shape (every `ShapeDocument`
field, including `Chamfer`/`Panel`), enforcing the topological invariant
(a `parent` must already be declared, ids are unique) at author time rather
than deferring to a final pass; `Mirror(side => ...)` runs `side` once for
`Left` (sign +1, suffix "L") then once for `Right` (sign -1, suffix "R");
`Chain(...)` authors a parent-chained bead sequence (a braid, a tail);
`AxisAngleDegrees`/`Multiply`/`Identity` build quaternions that INTERN by
value — two shapes authoring the same rotation share one instance, named by
axis letter and magnitude (`"z90"`, `"zm90"` for -90) or, for a product, by
its two operands' names joined with `-` (`"z4-z180"`); `FixedId` pins a
shape's id for a world's looks/parts to reference; `CarryRig` copies a
shape's `swings`/`slides` forward by name from an existing document (the
rig-preservation primitive every re-generation needs) and is a no-op on an
already-referenced facet, so applying it twice changes nothing the second
time.

`StateHoisting.Apply` is the generator's post-pass, generalized: every
interned rotation (or only ones two or more shapes share, by policy) becomes
a shared `Text` state cell; every scale two or more shapes share becomes one
too, its name derived from the first sharing shape's own name (trailing
digits, then a lone trailing "L"/"R", stripped); a swing's LITERAL pivot
equal to a named joint, or LITERAL amplitude equal to a named tuning value
(under a caller-supplied `(driver, shapeName) -> cell` selector), binds to
it. A value that is ALREADY a `state.<row>.<name>` reference — carried
forward by `CarryRig` — is left untouched, which is what makes hoisting
idempotent: applying a sculpt to its own settled output changes nothing.

`SculptPatch` is the world-document patch primitive: an ordered list of
`UpsertRow`/`RemoveRow` (by an id/name key — never an array index) and
`SetMember`/`RemoveMember` (a dotted path whose segments may carry ONE
`[field=value]` selector each, e.g. `looks.rows[name=moth].motion.poses`)
operations over a raw `JsonObject`/`JsonArray` tree. It understands JSON
only — no `Puck.World.Schema` type, since that project depends on this one
(the reverse reference would cycle) — so a sculpt builds the sections it
does not own a Schema type for (`render`, `rules`, `state`, `dynamics`,
`cameras`, `views.layouts`, a look row) as raw JSON matching the document's
own wire spelling, and its caller (the `creation.sculpt` console verb, or
`puck creation sculpt`) is the one that serializes a `CreationDocument` row
through `DocumentJsonOptions.Shared` and validates the patched WHOLE
document through `WorldDefinitionSerialization`/`WorldDefinitionValidator`.

`ICreationSculpt` is one registered generator: `Sculpt(SculptContext)`
returns the `SculptPatch` it would apply, where `SculptContext.Document` is
the world document as it stands right now (live or on disk) — a sculpt reads
it to carry rig data forward by name; it never writes to it directly.
`CreationSculptRegistry` ships empty — a sculpt is registered by a
composition root or a test through `CreationSculptRegistry.Register`. Apply
a registered sculpt live through `creation.sculpt <name>`
(`Puck.World.Console`), or offline against a file through
`puck creation sculpt <name> --world <path>` (`Puck.Cli`) — see that
project's README.

`SculptPatch.TouchedRows` groups a patch's results by row for a caller that
resubmits whole rows: a `RemoveMember` is a modification of the row it
descends into (re-upsert from the patched tree), and only a `RemoveRow` that
found its row marks it `Removed`. `UpsertRow` into an absent section creates
object intermediates and the array itself only at the final segment
(`state.world` on `{}` yields `{"state":{"world":[…]}}`). A patch fault (a
path descending through a non-object, a malformed selector) throws
`InvalidOperationException`/`FormatException`; both callers turn it into a
named refusal that writes and submits nothing. Laws: `SculptPatchLawTests`.
