# Rig, motion, and the rest of the creation document

Everything here is presentation only, read by the client's stamp pool. None of it
moves a collider or the simulation.

## Attaching a shape

| Field | Meaning |
|---|---|
| `parent` | the `name` of a shape declared **earlier**, whose animated motion carries this one, pivots included. This is the skeleton. Null means the creation root |
| `joint` | a hinge point used only when the shape is an effector bone that declares no swings. Otherwise it poses nothing |
| `swings` | up to 8 rotations about a pivot |
| `slides` | up to 4 translations, applied after every swing |

Because a parent must be declared earlier in the list, a chain resolves in one
pass and can never cycle. The refusals are
`names no shape '<parent>'.` and
`parent '<parent>' must be declared before the shape it carries.`

A swing is `{ driver, pivot, axis, amplitude, phase, wave }`, rotating
`amplitude × wave(phase + φ)` radians about `axis` at `pivot`; amplitude is
capped at π except for the `linear` wave. A slide is the same shape without a
pivot, in creation units, capped at 4.

`wave` is `sine` (the default), `halfSine` (`max(0, sin)`), `linear` (the
argument itself, unbounded), `constant` (1, so the facet is a pose the driver's
weight blends in), or `curve:<row>`, which samples a world `curves` row by arc
fraction: the argument's fraction of a turn maps onto the row's arc length and
the sampled Z is the value. A curve wave on a `state.<row>` driver is how a part
holds still until a cell crosses a threshold and then moves: draw the row flat
to the threshold, then rising, and pick the cadence that maps the cell's range
onto one turn.

A live edit of `parent`, `swings` or `slides` (`world.row.set`, `world.row.add`,
`world.reload`) applies on the next frame: any change to a creation's content
changes its hash, and the stamp pool replaces the registration, re-resolving
the parent chain, the effectors and the tight group bounds from the edited
document. `parent` carries only the parent's animated delta, so a shape
reparented at rest does not move until the new parent's motion differs from the
old one's. `body.rig <body> <shape>` reads back the parent the shape's motion
chains under and the delta the last frame composed for it
(`shape:<name> parent=<name>|root delta=(x, y, z) turn=r`).
`WorldStampPoolLiveRigEditLawTests` pins both edits through the scene emitter.

A placement anchor naming a shape (an audio emitter, an overlay subject, a
camera view's `placement` anchor) reads `WorldStampPool.TryShapePosition`, which
answers where the last frame drew the shape: its rest or frame pose under the
composed delta (own swings and slides, the parent chain, effector corrections)
and its part follower. It reads the latch `PackRegistration` left, the same one
`TryBodyPartAuthoredPose` reads (`LatchedShapePose`), and evaluates no rig of its
own. `WorldStampPoolLiveRigEditLawTests` pins it to the drawn slot every frame.

A shape carrying `domain` folds may carry neither, since a fold rides its
parent's frame. It may still carry a `parent`: the folded shape rides its own
per-shape slot, packed with the rigid delta the parent's chain imparts (identity
with no parent, translation in placement units), and its chain applies
`Scale(placementScale)`, the domain ops, then its own rest pose, so the fold
plane travels with the parent's driver and effector motion at any look scale.
Its cull bound rides that slot with the static stamper's `RenderReach` radius.
It refuses a named `frames` entry and a look `partDynamics` follower, and a frame
posing its parent moves the parent alone, since a frame replaces a base pose
outside the delta chain. A `parts` entry may name it: `TryBodyPartPose` and
`TryBodyPartAuthoredPose` compose its rest pose onto the carried frame and agree.
`CreationDomainParentLawTests` pins this.

## Drivers

`drivers` is up to 16 named `{ name, signal, cadence, when, blendInSeconds,
blendOutSeconds, dynamics, cadenceBy, weightBy }`. Everything animated names one.
They advance in declaration order, and a driver reads only drivers declared
before it, the rule a shape's `parent` follows.

| Signal | Behavior |
|---|---|
| `planarTravel`, `travel`, `time` | integrate |
| `speed`, `verticalSpeed`, `turnRate` | set directly; `speed` is ground speed in m/s read per frame (vertical travel reads zero, and so never trips `moving`; a read is capped at 60 m/s, never by the frame's length), `turnRate` is positive turning left |
| `travelDirection` | set directly: eased planar travel off the facing, (−π, π], 0 forward, π backpedal, positive left; holds at rest |
| `lateralAcceleration`, `forwardAcceleration` | set directly: eased planar acceleration across (positive left) and along (positive forward) the facing, m/s² |
| `state.<row>[.<key>]` | set directly: the cell's numeric value at the frame's tick, times the cadence, is the phase (a cycle-trait clock, a counter, an `hp` cell) |
| `driver.<name>` | set directly: cadence × the phase an earlier driver reached this frame, so several drivers ride one cycle (cadence 2 is twice its rate) and never leave step |

`dynamics` names a world `dynamics` row a setting driver's phase follows through
(second-order: trail, overshoot, settle). An integrating driver and a
`driver.<name>` driver refuse it; the world validator refuses an undeclared row.

`cadenceBy` and `weightBy` are each `{ driver, curve }`: the height (Z) of a
world `curves` row where its X equals that earlier driver's phase, held at the
end knots outside the row. `cadenceBy` multiplies the cadence every frame, so a
stride on `planarTravel` whose radians per metre fall as a `speed` driver rises
lengthens its stride with speed. `weightBy`, clamped to [0, 1], is the level the
weight eases toward while the gate holds instead of 1, through the blend times
(in while it rises, out while it falls); a `driver.<name>` driver weighted by a
rising curve of speed fades a run's swings in on the same cycle a walk's ride.
The curve row must be open and drawn left to right, each knot further along X
than the last with its tangent toward +X, or the world validator refuses it.
Author each knot's `tangentYaw` and `curvature` from the function it samples
(`atan(f′)` and `f″/(1+f′²)^1.5`): a straight segment at a slant does not
compile, because its fixed-point tangents never line up exactly with its chord.
A driver with neither keeps its authored cadence and eases its weight toward 1.
`CreationCharacterLawTests` pins the three.

`cadence` is capped at 256 in magnitude. `when` takes up to 4 gate tokens from
the body-facts vocabulary plus `moving`, `still`, and `always`; `always` may not
appear in a conjunction and `moving` with `still` is refused. Blend times default
to a 0.15 second ease. `blendInSeconds`/`blendOutSeconds` are finite
non-negative exponential time constants; zero is immediate on a positive render
delta. They control gate weights independently of phase cadence and the shared
movement-speed filter (`CreationAnimationLawTests`).

## Chains and effectors

When every surface effector reports `target=none`, check the presentation query
field before changing probe reach or geometry. `WorldFramePresenter` builds the
field used by camera clearance and limb probes; `WorldPlacementStamper.Queryable`
excludes unsupported placements individually, as the motion CLI's camera check
does. The log names each excluded placement and reports a combined-field failure
as `[world.presentation-field: unavailable; ...]`. An excluded prop has no query
surface, but cannot remove the supported floor and other placements. `body.rig`
reads the last packed rig; a read scheduled before a screenshot is not a read of
the new frame that screenshot will compose.

`chains` groups shape **ids** root to tip. `kind` is `limb` or `spine`; null
resolves to `limb` when there are exactly three shapes, and a `limb` with any
other count demotes to `spine`. A chain naming a missing shape id is dropped.

`effectors` is up to 8 CCD solvers, `{ name, chain, tip, target, when, weight,
plant, align }`, where `chain` and `tip` name shapes **by name**, 2 to 8 bones, solved
over 64 iterations.

A `target` is `{ kind }` of `surface`, `body`, or `state`:

| Kind | Fields |
|---|---|
| `surface` | `direction` (author frame, nonzero), `reach` in (0, 8], `standoff` ≤ 1 |
| `body` | `index`, `offset` ≤ 16 |
| `state` | `reference` = `state.<row>[.<key>]` holding a world-space point |

A `plant` is `{ driver, window, swingWeight }`, where the window is a
`[from, to]` pair in [0, 2π) that wraps when `from` exceeds `to`. This is what
keeps a foot still while the gait carries the body past it.

`swingWeight` is a nullable fraction in [0, 1] controlling the target's influence
outside the plant window while the driver is active: zero releases to the
authored swing, omission keeps following the target, and driver rest restores
contact influence. A false effector `when` gate clears the latch, so
reacquisition cannot reuse a pre-flight world target. `CreationEffectorLawTests`
holds the pack-path controls; Moth opts into zero swing influence.

`replant: { distance, seconds, height }` on a plant is the resting latch: while
the driver rests, the tip holds its world point and steps to the freshly
resolved target once it drifts past `distance`, over `seconds`, along an arc
`height` high. Effectors sharing a plant driver step one at a time; `body.rig`
prints `planted=stepping` mid-step. `CreationLocomotionLawTests` pins it.

`align` in [0, 1] (omitted = 0) keeps a planted tip's sole on the contact. The
solve moves only the tip's origin, and the tip inherits the turn the chain made,
so without it a boot planted off its rest offset tilts with its shin (toe in the
ground, heel lifted). With it the solved tip turns about its own origin, the
shortest way, until its rest axis against the probe `direction` (creation up for
a `body` or `state` target) lies along the surface normal at the target: the
probe's field gradient, latched with the plant and turned over a replant step,
world up when there is no surface. It scales by the eased weight the goal blends
by, so with `swingWeight: 0` the swing's own toe-off and heel lift are untouched
off the ground, while the tip's own swings are levelled with the rest of its tilt
on the ground. `standoff` stays measured to the tip's origin, since an aligned
tip's origin sits exactly its rest height above the sole. `body.rig` appends
`align=w normal=(x, y, z)` for an effector that authors it.
`CreationLocomotionLawTests` pins it.

## How the moth moves

Every part of the moth's motion is a driver, a swing or an effector in
`avatars/moth.puck`; nothing is special-cased in the client.

| Motion | Driver | Where |
|---|---|---|
| Pace, the X every gait curve reads | `pace` (`speed` through `gait-pace`) | none |
| Stride cycle, its length opening with pace | `stride` (`planarTravel`, `Grounded` + `moving`, `cadenceBy` `moth-stride`: 1.5 steps/s and 0.7 m strides at 0.5 m/s, 2 at 1.5 m/s, 3.75 and 2.7 m at 5 m/s) | the feet's plant windows; every gait driver rides its phase |
| Stride, arm swing, knee fold and ankle, hip and chest counter-yaw, head steady | `gait` (`driver.stride`, `weightBy` `moth-gait`: half at a walk, whole from 4.5 m/s) | thighs, shins, boots, upper arms, forearms; pelvis yaw and roll, torso, helmet |
| Forward lean, arms carried out, elbows bent | `run` (`driver.stride`, `weightBy` `moth-run`: none to 1.5 m/s, whole from 4.5 m/s) | pelvis, upper arms, forearms (`constant` waves) |
| Bob and chest dip, twice per stride | `bob` (`driver.stride`, cadence 2, `weightBy` `moth-run`) | pelvis slide, torso |
| Hips into a forward diagonal, chest and head back to the facing | `heading` (`travelDirection`, cadence 2, `sine`) | pelvis +0.7, torso −0.4, helmet −0.3 |
| Stride plane steered to the travel direction (sidestep, backpedal) | `heading`, `linear` ±0.5 between `sine` ±0.7 | each thigh, about the hip, around its stride swing |
| Hips trail a turn, chest and head lead | `trail` (`turnRate` through `body-turn`) | pelvis −0.12, torso +0.08, helmet +0.07 |
| Lean into a curving run, a start or a stop | `lean`, `surge` (accelerations through `body-lean`) | pelvis, pivoting about the ground point |
| Feet plant while walking, step when turning in place or settling, soles flat on the ground | `foot-left`/`foot-right` effectors, `stride` windows plus `replant`, `align: 1` with `standoff` the boot centre's rest height | thigh–shin chains, boot tips |
| Breath, flight pose, wings, and in flight alone the bank and cruise | `breath`, `flight`, `wings`, `bank`, `cruise` (the last two gated on `state.airPose.$body` too) | torso, limbs, pods |
| Jump: legs tucked and arms open in the air, tighter rising and reaching for the ground falling | `air` (`verticalSpeed` through `body-air`, `Airborne`, `linear` swings with a phase offset, so the peak pose is amplitude × phase) | thighs, shins, boots, upper arms, forearms |
| Landing give: a crouch that springs back past rest | `land` (`verticalSpeed` through the underdamped `body-land`, `Grounded`): still the fall's speed a beat after touchdown | pelvis pitch and drop, thighs, shins, boots, forearms |

### Judging motion

Judge a change to any driver, swing or effector above with `puck motion`, never
with one-off captures: it runs the walk, run, stops, turns, jumps and flight
(suited and out of the suit) on exact ticks through the `motion` layout's four
cameras riding body 0, and writes a contact sheet per scenario and camera with
every frame labelled by tick. Read `<scenario>/sheet.png` first, then the
camera's own sheet for the part you changed; `puck motion --reproduce` shows
that a repeat stills the same poses. Add a case by editing
`worlds/debug/motion.scenarios.json`
([the verb](../../../docs/reference/cli.md#puck-motiontick-pinned-motion-stills-and-contact-sheets)).

The root shape is the pelvis, so a swing on it carries the whole body; the
upper body undoes part of it through the torso and helmet, and each thigh's
stride swing sits between yaw swings that turn the stride plane without
turning the leg at rest.

Chain refusals worth recognizing:

- `bone '<name>' does not descend from '<prev>' through parent, so the chain is not one limb.`
- `bone '<name>' carries domain operators — a fold rides its parent's frame and never its own solve, so it cannot be a bone.`
- `tip '<tip>' does not descend from the chain's last bone '<name>', so a solve could not move it.`
- `<n> bones is fewer than the 2 a chain needs; one bone has no joint to bend at, which a swing already says.`

## Frames

`frames` is a list of `{ name, transforms }` where each transform is
`{ id, position, rotation }` addressing a shape by id. A frame snapshots
**base** poses, replacing the parent's base pose rather than joining the delta
chain. There is no interpolation between frames; `rest` is the live pose. A
transform naming a missing shape id is refused, not dropped. A frame carries no
scale: its pose rides the shape's dynamic slot, which holds a position and an
orientation only, while scale is baked into the shape's geometry, so nothing in
the motion vocabulary scales a shape over time.

## The remaining sections

| Section | Shape | Caps and notes |
|---|---|---|
| `cameras` | `{ id, shapeId, position, yaw, pitch, fov, focus, feed }` | rides the anchored shape's live pose. `position` converts to the engine frame; `yaw` and `pitch` do not |
| `parts` | `{ id, shapeId }` | publishes one shape's dynamic transform as a named anchor other things can attach to |
| `behavior` | `{ locomotion, faces, sounds }` | a face is `{ name, shapeId, defaultSource }` with a source of `none`, `test`, `camera:<name>`, or `feed:<name>`; a sound carries an inline `puck.synth.v1` patch with `level` capped at 8 |
| `textRuns` | `{ text, position, rotation, emHeight, depth, mode, material, font, maxWidth, align, tracking, lineSpacing, shapeId }` | local +X advances, +Y is ascent, +Z is the relief normal. `mode` is `emboss` (default) or `engrave`; engrave forces the creation-wide scope. Each non-whitespace glyph charges the shape budget. `position` is the first line's baseline; `\n` breaks a line. An embossed run bakes alone, refined toward `min(depth, 0.1 × emHeight)` but no finer than 1.875 mm along the run (a 15 mm cell over `MaxDetailRefinement` 8, `SdfMeshPathBake.OwnFinal`), so a stroke (about a tenth of the em) needs an em near 40 mm and a relief of 2 mm or less to span two cells; smaller lettering drops strokes (`worlds/debug/devices.md`) |
| `noise` | `{ frequency, amplitude, octaves, gain, lacunarity, seed }` | frequency in (0, 8], amplitude in (0, 4], octaves 1 to 8. Static stamps only — framed, attached, inhabited, and body-look placements refuse it. Forces the creation-wide scope. Render only |
| `volumes` | see `surface.md` | up to 64 |

## What crosses the author frame

Converted on the way to the engine: shape `position`, `rotation`, and `joint`;
`symmetry` fold normals; swing `pivot` and `axis`; slide `axis`; frame transform
`position` and `rotation`; camera `position`; effector target `direction` and
`offset`; a root-parented volume's `position` and `rotation`; and an unridden
text run's `position` and `rotation`.

Not converted: scalars, camera `yaw` and `pitch`, chain `goal` and `pole`, a
shape-ridden run or volume (its host shape carries it), and a `state` target's
cell, which is already a world point.
