# Puck.World.Authoring

The authored-content document families `Puck.World` embeds inline:
`puck.creation.v1` (`CreationDocument`/`CreationCanonicalizer`),
`puck.music.v1` (`MusicDocument`), and `puck.judge.v1` (`JudgeDocument`), all
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

## Animation: drivers, waveforms, joints

A creation animates itself from three composable parts, none of which names a
creature or a vehicle in engine code.

A creation-level `drivers` list (≤ 8) declares the **driver** — a scalar signal
read off the body the creation is stamped on, times a cadence, gated by a
conjunction of condition tokens. Each driver yields a phase φ and an eased weight
w ∈ [0, 1]; w eases toward 1 while every `when` token holds and toward 0
otherwise, over 0.15 s, and a driver at rest (w = 0) stops advancing.

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
| a `Puck.Physics.Motion.BodyFacts` name — `Grounded`, `Airborne`, `Rising`, `Falling`, `Submerged`, `AtSurface`, `HoldingUnwalkable`, `Unsupported`, `AffectedBy` | the simulation publishes that fact |
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
| Fish tail | `time`, `when: ["Submerged"]` | swing, axis Y at the tail root |
| Bobbing hull | `time`, `when: ["AtSurface"]` | slide, axis Y |
| Breathing chest | `time`, `when: ["still"]` (an idle breath) | slide, axis Z, small amplitude |

Every one of these is presentation-only: the facets are read where a body-rooted
stamp's per-frame transforms are packed (`Puck.World.Client.WorldStampPool`) and
nowhere else, so the emitted SDF program, the analytic colliders, the compiled
solid field, and simulation state are all blind to them. `CreationFrame` carries
a swing's `pivot` and both facets' `axis` across the author frame; a half turn
about +Y is a proper rotation, so a rotation axis takes the same
`(−x, y, −z)` flip a direction does and the amplitude, phase, and waveform are
unchanged. A shape carrying `domain` operators rides the placement root's
transform rather than its own, leaving nowhere for either facet to compose, so
the combination is refused by name.

## Effectors: chains, targets, planting

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
| `repeat` | `RepeatLimited(spacing, limit)` | one copy per lattice cell; needs a whole-number `limit` (an absent one is unbounded and refuses) |
| `polar` | `RepeatPolar(count, axis, mirror, materialStride)` | `count` copies, doubled when `mirror` is set |
| `wallpaper` | `WallpaperFold(group, cell, limit, plane, materialStride, lodDistance)` | none — refused on a solid placement |

Copies compose across the list, capped by `SdfDomainExpansion.DefaultCopyBudget`.
Expansion is exact only for a prototype inside the fold's fundamental domain:
on a symmetry plane's positive side, inside a repeat's centre cell, between a
polar sector's walls. A prototype straddling a wall renders clipped and
collides whole.
