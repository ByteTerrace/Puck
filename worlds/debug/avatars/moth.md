# Moth flight studio

[moth.puck](moth.puck) contains a playable SDF character with directly authored
dimensions and rig. The [original concept sheets](../../../docs/game/art/moth-concept-pack/README.md)
are the visual standard.

The character renders inside Puck as a native SDF creation: the suited Moth of
the concept pack's turnaround, a 2.06 m mech suit of thick lavender and ivory
plates over charcoal joints. Her real head sits inside the open hood with dark
lining round it, the braid rooted by her right cheek. A flat chest slab, two
stacked shoulder slabs on round pivots, heavy gauntlets, hip pivot discs,
pistons at the elbows and knees, flared greaves and heavy boots on thick
charcoal soles carry the silhouette. Exactly two curved flight pods carry two
ivory bands and a recessed nozzle each. The plates are pristine, as on the
sheets: a modest clear coat and studio reflections, and no wear.

## Open the studio

Run from the repository root:

```powershell
dotnet run --project src/Puck.World -c Release -- --world worlds/debug/avatars/moth.puck --state-dir artifacts/moth-demo/state --exit-after-seconds 0
```

With an existing Release build:

```powershell
dotnet src/Puck.World/bin/Release/net11.0/Puck.World.dll --world worlds/debug/avatars/moth.puck --state-dir artifacts/moth-demo/state --exit-after-seconds 0
```

The initial view is the seat's chase camera; 1 switches to the native model's
three-quarter inspection. Inspection layouts switch immediately so a close-up
does not render overlapping camera transitions.

## Controls

While a mouse drag orbits or steers the camera the cursor hides and stays in
the window, so the drag keeps turning however far the mouse travels, Remote
Desktop included; releasing the button shows the cursor again where the drag
began.

Every control the controller bar shows has a keyboard equivalent (the owner's
rule): the bar is the debug world's only on-screen UI.

| Keyboard and mouse | Gamepad | Action |
|---|---|---|
| W / S, up / down arrows | Left stick, d-pad up / down | Move forward / backward |
| A / D | Left stick, d-pad left / right | Strafe |
| Left / right arrows | | Turn |
| Left mouse drag | | Orbit the gameplay camera |
| Right mouse drag | Right stick | Steer: orbit the camera, the body turning to face where it looks |
| Left and right mouse buttons held | | Walk forward while the right button steers |
| R (hold) | Right stick press (hold) | Recenter the camera behind the body |
| F | North | Switch flight mode on or off |
| Space | South | Jump, twice before landing; in flight mode, rise |
| Ctrl | East | In flight mode, descend and land |
| E | West | Context button: engage the device in reach, and leave it again ([devices](../devices.md#engaging-a-device)) |
| 1 | | Native three-quarter inspection |
| 2 | | Native front inspection |
| 3 | | Native rear inspection |
| 4 | | Wide flight inspection |
| 5 | | Following gameplay view |
| F3 | | Toggle the annotation outlines (`view.annotate`) |
| F4 | | Show the jetpack alone (`view.isolate`) |
| F2 | Back | Switch between playing and building (`player.build`) |
| Backspace | L3 | Respawn at the stage spawn, standing still, from anywhere (a fall or the portal loop included) |
| Tab (hold) | LB (hold) | Open the play wheel |
| Backtick | | Show or hide the console |

Out of flight mode, letting go of Space while still rising cuts the jump short.
In flight mode, releasing Space holds altitude. Inspection cameras stay at the
studio origin; use **5** when moving away from it.

The debug world and the garden draw these controls on screen from
[`controls.puck`](../controls.puck): the standard controller bar along the bottom
edge (the d-pad diamond with LB and the left stick's press, the face diamond
with RB and the right stick's press, each plate showing its action) and the
play wheel. The bar shows a chord only over controls it places, as their caps
and the action's icon, so the both-mouse-buttons walk stays off this
controller bar. Hold Tab and point with the mouse, or hold LB and aim the right
stick, then release over a sector: Console, Build, Where (`body.where`),
Channels (`body.channels`), Annotate, Respawn (back to the stage), Desktop
(the desktop pane, `view.override layout desktop`) and Play (the play view
alone, `view.override layout play`). Releasing at the hub cancels. The bar
fades after six seconds without input, and the bar and the cursor step aside
while the seat is engaged on a device.

## Edit the native character live

The `moth` creation's `document.shapes`, `palette`, `drivers`, `frames` and
`volumes` are the authoring surface. Shape positions and pivots use a common
rest frame: +Y up, +Z forward, with the sole near Y=0 and the hood crown near
Y=2.06. Parent names carry animation deltas, so child
positions are authored in the common frame rather than relative to a parent.

For a small live change:

```text
world.row creations moth document.shapes[name=shoulder-left]
world.row.set creations moth document.palette[1].roughness 0.34
world.wait 4
world.screenshot artifacts/moth-demo/paint.png
```

Wait for the capture completion message, not just `world.screenshot: pending`.
Two mutations to the same creation row in one tick window collide. Put
`world.wait` between edits, or submit the edited whole row once with
`world.row.set creations <json>`. Read the whole row with
`world.row creations moth`; its echoed JSON is suitable for that command.

For larger edits, edit the canonical `.puck` source and issue `world.reload`. This replaces
the authored geometry, materials and rig without restarting the GPU host. It
also clears the live mutation journal, so preserve live edits before reloading.
The document remains the editable source; no model-generation script is needed
at runtime.

Live look-only edits currently retain the registration's previous lane
expressions. When changing `looks.rows[].motion.lanes`, also change and reload
the creation document, or restart the studio, so the registration is recreated.

For a different close-up, edit the `three-quarter` camera's
`rig.operations`. If copying another camera's whole
row, keep the row name `three-quarter` and give its rig a unique name; duplicate
camera-program names are refused. Reload the world to restore the saved view.
Standalone `face`, `face-side`, `head`, `rear`, `back-close`, `shoulder`, `knee`, `boots`,
`boots-side` and `boots-rear` layouts remain available through `view.override`.
`view.isolate moth thigh-left thigh-right` renders only the legs, both thighs and
everything parented beneath them, still posed and animated by the hidden body;
`view.isolate off` restores the whole Moth.

## Geometry and motion

The creation uses superellipsoids for smooth volumes, convex extrusions for
armor plates, quadratic sweeps for digits and facial lines, and axial profiles
for flared shins. Scoped subtraction and intersection isolate the hood opening,
eye sockets, plate boundaries and ankle arches. The shin's ivory arch bands and
side panels are fitted geometry. Detail that is colour rather than form is paint
(`palette[].paints`), drawn crisp at the pixel and absent from the bake:
the hood's ivory trim, the chest slab's lavender centre, the shoulder slabs'
ivory bands, the gauntlets' lavender inner halves, the vent slot pairs on the
outer faces of the gauntlets and shin panels, the boot seams, the mouth, the
pods' plates, and the upper lids and lashes where their tubes cross the eye
whites. The near-ellipsoidal forms use
exponent 2.1, a slightly fuller form than the ellipsoid (exponent 2). Check `world.budget` after geometry edits:
the rebuilt world was verified at `stepScale 1`.

The palette is the concept pack's: lavender plates authored `#744080`, which
the studio's lighting renders at the owner's dusty `#B186B6`, and warm ivory
plates authored `#F2C49A`. The colour-conversion path and the studio's light
brighten and cool both, so judge a palette change from a capture, sampled
against the sheet, never from the hex alone. The plates carry no wear: every
plate's weathering floor is `plateWear`, 0, and lane 0 is constant.

Her head is the [`moth-head`](moth-head.puck) library's `mothHead` template,
placed by `headAt` and scaled by `headScale` (0.68, about 0.26 m crown to chin)
about the face's front at eye level. The template scales every position, extent,
curve, sweep radius and blend radius, and takes the creation's palette slots, so
the off-suit pilot reuses it at its own scale. The mouth and lid paints and the
blink frame (`blinkPose`) live in the creation and scale by the same
`headScale`. A charcoal onion shell (`hoodLining`) lines the hood, so the
headroom round her face reads as padded interior. The braid's root sweep starts
at her cheek and bridges to the unscaled braid below it.

The torso carries one flat chamfered slab (`chestSlab`), ivory round a painted
lavender hexagon, above the segmented charcoal waist and the ochre belt; the
ochre collar stands above it at the neck. A lavender plate (`pelvicShield`)
narrows below the belt. The slab inherits the torso's breathing and lean; the
belt and pelvic plate inherit the pelvis.

Each shoulder is a stack of two thick rounded slabs on a charcoal superellipsoid
pivot, all riding the upper arm: the upper slab lavender with an ivory band, the
lower one ivory above a lavender skirt. The upper slab's inner edge stays
outboard of the hood through the gait and the flight splay. The gauntlets are
squared blocks from the elbow to the wrist, faced square to the body rather than
the forearm's twist. Each hip carries a large charcoal disc on the hip axis over
an ivory guard, both riding the thigh; the thighs are bare charcoal.

The pistons need no driver of their own. Each is a satin steel cylinder on the
upper bone and a steel capsule rod on the lower bone, each aimed at the hinge,
so at any bend they meet inside the joint ball and nothing stretches. The
elbow's cylinder runs from under the shoulder stack, inside the bend, and its
rod continues the same line at rest into the gauntlet, which covers most of the
forearm, so the rod shows only between the joint and the gauntlet's top. The
knee's cylinder and rod converge behind the knee from lugs on the thigh and the
greave's top.

The boots follow the reference's lavender armor, fitted ivory ankle arches,
sloping ivory insteps, broad lavender toe caps on thick charcoal soles and split
heel pads. Each recessed heel jet points 55 degrees below rearward horizontal in the
boot's rest frame; its lens and exhaust share that axis. The cyan lens and pale
core stay softly lit at rest. An axial clearance cut passes through the cream
instep and rubber ankle behind the nozzle; the raised rear arch clears the barrel.
The two lavender heel guards have softened concave cutouts. The existing airborne
driver splays the outer guards 16 degrees and the inner guards 6 degrees, leaving
space between the boots, and folds the centre tread 60 degrees forward,
sliding it toward the toe, before the second-jump burst. They close again on
landing. Nozzle, ankle and cutouts follow the boot so the passage stays aligned
as it articulates. The [debug studio](../debug.md#avatar-studio) provides front,
side, low rear and quarter views on a neutral platform, including `studio-jets`
for the nozzle clearance study.
At close range the curved ankle trim still shows some uneven baked shading;
`mesh audit` locates small folds at its clipped edges even though the mesh is
closed. Judge the silhouette separately from that remaining surface defect.

The rig swings the thighs, shins, feet, arms and forearms during walking.
Two surface effectors plant the feet in alternating stride phases. Their target
is the boot origin, held 0.1875 units above the floor, with sole alignment to the
contact normal and replanted steps when the resting stance drifts. Flight bends the knees and
splays the arms; speed and turn rate add a forward lean and bank. The torso has
a small breathing motion. The blink frame hides the open-eye pieces and seats
closed lids in their place, using the existing scheduled `blink` state.

The `flight` and `wings` drivers read `airPose` and `airWings`. Existing server
rules and damped state responses provide takeoff and landing transitions. Each
pod deploys approximately six degrees outward and four degrees aft. No extra
fan blades deploy. Four bounded `flow` volumes supply animated translucent
exhaust: two back pods use lane 1 and two heels use lane 3. The pods' lens emission
uses flight-mode lane 2; the boot pilot lights are independent of that lane.
Lane expressions use keyed state syntax, unlike the
drivers' `state.airPose.$body` binding. Collision and movement controls remain
the world kit's responsibility.

Travel direction turns the gait, and the damped landing driver adds joint bend
and pelvis displacement as the body settles after impact.

## Verify edits

Check document admission and the stamp budget (355 of 367):

```powershell
dotnet src/Puck.Cli/bin/Release/net11.0/Puck.Cli.dll creation stats --world worlds/debug/avatars/moth.puck --prototype moth
```

Then compare the live suit with the turnaround at the sheet's framing, standing
at the stage spawn: `view.override camera orbit 0 1.03 0 7.5 <yaw> 0 19`, yaw
180 front, 90 right side, 0 back and 140 three-quarter. Sheet 10's rear
three-quarter is `view.override camera orbit 0 <her height + 1> 0 7 35 -4 22`
after `body.press flight 1 0.1` and `body.press rise 1 0.7`. Exercise the airborne pose
and landing through the declared jump channel:

```text
body.stop
body.pose 0 0 0 0 0 0
view.override layout flight-stage
body.press jump 1 0.4
world.wait 15
body.where
world.wait 90
body.where
body.stop
view.override layout play
```

The first readback should report `airborne`; the final readback should report
`grounded`. Capture the rear while airborne to inspect exhaust clearance and
pod deployment. Keep cameras matched when comparing against a concept sheet,
and distinguish a pose difference from a geometry difference.
