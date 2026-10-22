# The Moth out of her suit

[moth-pilot.puck](moth-pilot.puck) is the woman inside the Moth's suit: the
1.68 m pilot of the [concept pack](../../../docs/game/art/moth-concept-pack/README.md)'s
off-suit turnaround (sheet 04), a grounded, athletic working woman, never
sexualized. She wears a charcoal work undersuit with full sleeves and a high
collar, straight-cut work trousers, soft knee and elbow pads, a contact harness
with an ochre plate between her shoulder blades, small lavender shoulder
patches, a belt with an ochre buckle and flat ankle boots. Her hands are bare,
four fingers and a thumb each, so she can hold the 75 × 133 mm handheld of
sheet 06 at true scale. The sheet draws gloves; the owner's direction is bare
hands, and that wins.

Her head is the suited Moth's own: the [`moth-head`](moth-head.puck) library's
`mothHead` template at the same 0.68 scale (about 0.26 m crown to chin), placed
so her eyes sit 1.527 m up, the suited eye line less sheet 05's 0.208 m
footbed. Its mouth and lid paints and its blink frame (`mothBlinkPose`) are
placed at her head the same way. A thick three-strand braid roots beside her
right cheek and falls over her right collarbone to an ochre tie.

## Wear her

[`suit.puck`](../suit.puck) imports this file, and the debug world, the garden
and the night field import `suit.puck`. A body whose `suit` cell is 0 runs the
`pilot` kit (a walker with one jump and no flight mode, its capsule 0.25 m in
radius and 1.68 m tall) and wears the `moth-pilot` look. In the debug world, T
or the right trigger flips the cell while she stands; from the console:

```text
body.press suit 1 0.2 0
world.state suit
```

The first time she is worn in a run, her meshes bake for a moment; every later
toggle swaps forms on the next frame. `world.reload` resets the cell to 1.

## Inspect her

The avatar studio ([debug.md](../debug.md#avatar-studio)) frames her like sheet 04:

```text
body.stop 0
body.pose 48 0.33 -48 0 0 0 0
body.press suit 1 0.2 0
view.override camera orbit 48 1.17 -48 6.2 180 0 19
```

Yaw 180 is her front, 90 her right side, 0 her back and 140 the three-quarter
front. `view.override camera orbit 48 1.36 -48 7.8 <yaw> 0 19` frames both
forms at the same scale, as sheet 05 compares them. Walk and run across a side
view with `view.override camera orbit 48 1.0 -47.5 8 90 0 26`,
`body.pose 48 0.33 -45.6 0 0 0 0` and `body.press forward 0.3 2` (a walk) or
`body.press forward 1 1` (a run).

## Hold the handheld

While her body's `pilotHold` cell holds 1, the `hold` driver brings her upper
arms forward and in, bends her elbows and tips her head down, and the look's
`hold` frame seats the handheld in her hands with its screen tilted 30 degrees
toward her eyes: its lower half between her palms, her fingers behind it and
her thumbs on its face. At rest the handheld sits hidden inside her chest.

```text
world.state.cell.set pilotHold 0 1
view.override camera orbit 48 1.45 -48 3.2 160 -6 30
```

The look's first holding frame wins, so she does not blink while she holds it.
Nothing sets the cell when she engages a handheld device: a rule cannot read a
screen engagement yet ([open item](../../../docs/plans/open-items.md)).

## How she moves

She rides the suited Moth's drivers, followers and foot effectors
([moth.md](moth.md#geometry-and-motion)) at her own joints: hips at 0.86 m,
knees at 0.475 m, ankles at 0.085 m, shoulders at 1.355 m and elbows at
1.068 m. Her stride reads her own `pilot-stride` curve, π(1.5 + 0.6v)/v
radians per metre: 1.8 steps a second at 0.5 m/s rising to 4.5 at 5 m/s, the
suited rate scaled by the suit's 1.03 m hip over her 0.85 m one. The gait
weights (`moth-gait`, `moth-run`) and dynamics rows are the suited Moth's. The
pelvis carries the body; the torso, collar and head undo part of its turn, the
thighs stride in the plane of travel, and each boot is a foot effector's tip
with its origin on the sole's top, so the effector's standoff is 0.025 m. In
the air her legs tuck and her arms open; landing, she crouches and springs
back. Swings compose in list order, then the parent's delta, which is how the
hold pose was solved.

## Geometry

The torso is one smoothly blended group (chest, waist, chest front and
trapezius) riding the torso shape; two hip masses ride and blend into the
pelvis so her hips flow into her trouser legs. Limbs are tapered sweeps. The
front harness straps are thin boxes; the shoulder and back straps, the knee-pad
straps, the strap buckles, the shoulder patches and the handheld's screen and
controls are paint. A paint sits in its parent shape's own frame. The palette's
charcoals are authored darker than the sheet reads, since the studio's light
lifts them as it lifts the suit's lavender. She stamps 136 of her own 367
shapes:

```powershell
dotnet <cli>/Puck.Cli.dll creation stats --world worlds/debug/debug.puck --prototype moth-pilot
```

`moth-pilot.puck` is a fragment: it relies on the Moth basis's dynamics and
curves, so validate it through a world that imports it, never alone.
