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

`wave` is `sine` (the default), `halfSine`, `linear`, `constant`, or
`curve:<row>`.

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
| `speed`, `verticalSpeed`, `turnRate` | set directly; `turnRate` is positive turning left |
| `travelDirection` | set directly: eased planar travel off the facing, (−π, π], 0 forward, π backpedal, positive left; holds at rest |
| `lateralAcceleration`, `forwardAcceleration` | set directly: eased planar acceleration across (positive left) and along (positive forward) the facing, m/s² |
| `state.<row>[.<key>]` | read a state row |
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

`chains` groups shape **ids** root to tip. `kind` is `limb` or `spine`; null
resolves to `limb` when there are exactly three shapes, and a `limb` with any
other count demotes to `spine`. A chain naming a missing shape id is dropped.

`effectors` is up to 8 CCD solvers, `{ name, chain, tip, target, when, weight,
plant }`, where `chain` and `tip` name shapes **by name**, 2 to 8 bones, solved
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
| Feet plant while walking, step when turning in place or settling | `foot-left`/`foot-right` effectors, `stride` windows plus `replant` | thigh–shin chains, boot tips |
| Breath, flight pose, wings, airborne bank and cruise | `breath`, `flight`, `wings`, `bank`, `cruise` | torso, limbs, pods |

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
`{ id, position, rotation, scale }` addressing a shape by id. A frame snapshots
**base** poses, replacing the parent's base pose rather than joining the delta
chain. There is no interpolation between frames; `rest` is the live pose. A
transform naming a missing shape id is refused, not dropped.

## The remaining sections

| Section | Shape | Caps and notes |
|---|---|---|
| `cameras` | `{ id, shapeId, position, yaw, pitch, fov, focus, feed }` | rides the anchored shape's live pose. `position` converts to the engine frame; `yaw` and `pitch` do not |
| `parts` | `{ id, shapeId }` | publishes one shape's dynamic transform as a named anchor other things can attach to |
| `behavior` | `{ locomotion, faces, sounds }` | a face is `{ name, shapeId, defaultSource }` with a source of `none`, `test`, `camera:<name>`, or `feed:<name>`; a sound carries an inline `puck.synth.v1` patch with `level` capped at 8 |
| `textRuns` | `{ text, position, rotation, emHeight, depth, mode, material, font, maxWidth, align, tracking, lineSpacing, shapeId }` | local +X advances, +Y is ascent, +Z is the relief normal. `mode` is `emboss` (default) or `engrave`; engrave forces the creation-wide scope. Each non-whitespace glyph charges the shape budget |
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
