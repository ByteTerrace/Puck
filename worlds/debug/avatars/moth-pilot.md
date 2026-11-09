# The Moth out of her suit

[moth-pilot.puck](moth-pilot.puck) is the woman inside the Moth's suit: the
1.68 m pilot of the [concept pack](../../../docs/game/art/moth-concept-pack/README.md)'s
off-suit turnaround (sheet 04), a grounded, athletic working woman, never
sexualized. She wears a charcoal work undersuit with full sleeves and a high
turtleneck collar, straight-cut work trousers whose hems sit on her boots, soft
elbow pads and domed knee pads, a contact harness with an ochre plate between
her shoulder blades, lavender patches over her shoulders, a belt with an ochre
buckle, padded charcoal work gloves with a strap round each full cuff, four
thick fingers and a thumb each, and ankle boots with a round toe box, a strap
over the instep, a padded top cuff and a thick lugged sole with a heel step, as
sheet 04 draws them.

Her head is the suited Moth's own: the [`moth-head`](moth-head.puck) library's
`mothHead` template at the same 0.68 scale (about 0.26 m crown to chin), placed
so her eyes sit 1.527 m up, the suited eye line less sheet 05's 0.208 m
footbed. Its mouth and lid paints and its blink frame (`mothBlinkPose`) are
placed at her head the same way. Out of the hood her black hair has sheet 04's
volume, which is her own: a few tapered locks (`pilotHairLocks`, joined to the
head's scalp group through `mothHairLock`) over a smaller inner mass
(`hairMass`) and the hair over her nape (`hairNape`). A fringe swoop lifts from
a parting over her right eye across to her left, three locks sweep back over her
crown to points at the nape, loose hair falls down her left side past the ear
to the jaw, and her right side is swept back to the braid. The braid roots
beside her right cheek at its full thickness: a core along `braidAt` blended into
the hair at its root, and twelve plait lobes alternating down it over her right
collarbone to an ochre tie and a tapered tassel.

## Wear her

[`suit.puck`](../suit.puck) imports this file, and the debug world, the garden
and the night field import `suit.puck`. A body whose `suit` register is 0 runs the
`pilot` kit (a walker at the suit's 5 m/s, running at 10 with the run channel
held, with one grounded jump of 3.7 m/s, which lifts her feet 0.56 m where the
suit clears 1.1 m, and no flight mode, so fly does nothing for her and the
`suit-off-flight-1` rule keeps flight mode off; its capsule 0.25 m in
radius and 1.68 m tall) and wears the `moth-pilot` look. In the debug world, T,
or South while the right trigger is held (K while X is held; the bar's Debug
page, `SU`), flips the register while she stands, as does the play wheel's Suit
sector; from the console:

```text
body.press suit 1 0.2 0
body.state 0
```

The register is her own, so she walks through a painting into the garden or
the night field and back in the form she left in. The first time she is worn
in a run, her meshes bake for a moment; every later toggle swaps forms on the
next frame. `world.reload` resets the register to 1.

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
`body.pose 48 0.33 -45.6 0 0 0 0` and `body.press forward 0.6 2` (a walk) or
`body.press run 1 1` with `body.press forward 1 1` (a run).

## Hold the handheld

While her body's `pilotHold` cell holds 1, the `hold` driver brings her upper
arms forward and in, bends her elbows and tips her head down, and the look's
`hold` frame seats the handheld in her hands with its screen tilted 30 degrees
toward her eyes: its lower half between her palms, her fingers behind it and
her thumbs on its face. At rest the handheld sits hidden inside her chest.

The handheld is Wonderland's Humble Gaming Brick ([devices](../devices.md)),
75 × 133 × 27.5 mm: a faithful simplified copy, since a look wears one creation.
Its shell and battery bay are the device's atomic-orange acrylic glass, so its
pale green board, processor, cartridge and two blue cells read through the
orange; the dark lens, the picture's box (`brickPicture`, the device's 44 by
39.6 mm glass), the d-pad on the face's left as she looks at it, the smoked A and
B, select, start and the red power light sit on its face. A frame poses each
shape it names and not the shapes riding it, so the hold frame poses every part
of the brick with the shell.

She picks the real brick up to play it. Out of the suit and at her full size,
engaging it (E or the pad's RB beside it) sets her `pilotHold` cell through
`devices.puck`'s `brick-pick-up` rule, which reads the engagement as
`engaged(each, 0)`. The brick's placement then shows nothing on its rest, and
screen 0's `carry` puts the device's live picture on `brickPicture` in her
hands. The engagement starts on a view of her holding it (the `brick-hold`
camera, which the carry names), and the Frame control (`L`, or the pad's East with the left trigger held) goes
to the picture alone and back ([devices](../devices.md)). Leaving puts it back on
its rest and lowers her arms. In the suit, or at a quarter her size, she plays it where it
stands. Setting the cell by hand holds the brick the same way, its live picture
included:

```text
world.state.cell.set pilotHold 0 1
view.override camera orbit 48 1.45 -48 3.2 160 -6 30
```

The look's first holding frame wins, so she does not blink while she holds it.

## How she moves

She rides the suited Moth's drivers, followers and foot effectors
([moth.md](moth.md#geometry-and-motion)) at her own joints: hips at 0.86 m,
knees at 0.475 m, ankles at 0.085 m, shoulders at 1.355 m and elbows at
1.068 m. Her arms rest in sheet 04's slight A: each forearm swings out
`elbowRestSpread` and bends `elbowRestBend` forward at the elbow, so her hands
hang a hand's width clear of her thighs. The forearm and glove are measured on
the straight arm and placed through `rested` and `forearmRest`, which compose
the standard package's [turns](../../standard/turns.puck); the hold pose's first
two forearm swings undo the bend and then the swing out, so the pose turns the
straight arm as it was solved. Her stride reads her own `pilot-stride` curve, π(1.5 + 0.6v)/v
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

The torso is one smoothly blended group (chest, waist, chest front, trapezius
and the two rounded shoulder caps, which carry the deltoid) riding the torso
shape; the sleeve's top sits inside the cap, so the shoulder reads as one form.
The pelvis stands proud of the thighs at the front and seat and the thighs
proud of it at the hips, so their surfaces cross rather than lie on each other.
Each moving part of a limb is its own blended group, so no blend crosses a
joint: the upper arm (the sleeve's sweep, biceps and triceps), the forearm (its
sweep and the muscle below the elbow), the glove (cuff, wrist, palm, padded
back, thumb and fingers, webbed at their roots) and the boot (shaft, instep and
toe box over the foot effector's tip). The joints between them are plain
unions, which the elbow and knee pads cover; at the knee the shin is the wider
tube, so the two do not lie on each other. The sole's lugs are one bar repeated
along the foot. The front harness straps are thin boxes; the shoulder and back
straps, the knee-pad straps, the glove straps, the boot straps, the strap
buckles, the shoulder patches and the handheld's picture are paint. A paint sits in its parent shape's own frame. The palette's charcoals
are authored darker than the sheet reads, since the studio's light lifts them
as it lifts the suit's lavender. `creation stats` counts her shapes:

```powershell
dotnet <cli>/Puck.Cli.dll creation stats --world worlds/debug/debug.puck --prototype moth-pilot
```

`moth-pilot.puck` is a fragment: it relies on the Moth basis's dynamics and
curves, so validate it through a world that imports it, never alone.
