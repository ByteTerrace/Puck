# Rig, motion, and the rest of the creation document

Everything here is presentation only, read by the client's stamp pool. None of it
moves a collider or the simulation.

## Attaching a shape

| Field | Meaning |
|---|---|
| `parent` | the `name` of a shape declared **earlier**, whose animated motion carries this one, pivots included. This is the skeleton. Null means the creation root |
| `joint` | a hinge point used only when the shape is an effector bone that declares no swings. Otherwise it poses nothing |
| `swings` | up to 4 rotations about a pivot |
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
parent's frame.

## Drivers

`drivers` is up to 8 named `{ name, signal, cadence, when, blendInSeconds,
blendOutSeconds }`. Everything animated names one.

| Signal | Behavior |
|---|---|
| `planarTravel`, `travel`, `time` | integrate |
| `speed`, `verticalSpeed`, `turnRate` | set directly |
| `state.<row>[.<key>]` | read a state row |

`cadence` is capped at 256 in magnitude. `when` takes up to 4 gate tokens from
the body-facts vocabulary plus `moving`, `still`, and `always`; `always` may not
appear in a conjunction and `moving` with `still` is refused. Blend times default
to a 0.15 second ease.

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
| `volumes` | see `surface.md` | up to 8 |

## What crosses the author frame

Converted on the way to the engine: shape `position`, `rotation`, and `joint`;
`symmetry` fold normals; swing `pivot` and `axis`; slide `axis`; frame transform
`position` and `rotation`; camera `position`; effector target `direction` and
`offset`; a root-parented volume's `position` and `rotation`; and an unridden
text run's `position` and `rotation`.

Not converted: scalars, camera `yaw` and `pitch`, chain `goal` and `pole`, a
shape-ridden run or volume (its host shape carries it), and a `state` target's
cell, which is already a world point.
