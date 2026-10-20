# Moth flight studio

[moth.puck](moth.puck) contains a playable SDF character with directly authored
dimensions and rig. The [original concept sheets](../../../docs/game/art/moth-concept-pack/README.md)
are the visual standard.

The character renders inside Puck as a native SDF creation. It uses curved ivory shin
shells with lilac arch trim, swept shoulder plates, an
open hood, a compact collar, inset eyes and a braid rooted in the front opening.
Exactly two curved flight pods carry two ivory bands and a recessed nozzle each.
The materials include a modest clear coat, studio reflections and restrained
edge weathering.

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

| Keyboard and mouse | Gamepad | Action |
|---|---|---|
| W / S, up / down arrows | Left stick | Move forward / backward |
| A / D | Left stick | Strafe |
| Left / right arrows | | Turn |
| Left mouse drag | | Orbit the gameplay camera |
| Right mouse drag | Right stick | Steer: orbit the camera, the body turning to face where it looks |
| | Right stick press (hold) | Recenter the camera behind the body |
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
play wheel. Hold Tab and point with the mouse, or hold LB and aim the right
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
eye sockets, plate boundaries and ankle arches. The shin's cream arch bands and
side panels are fitted geometry. Detail that is colour rather than form is paint
(`palette[].paints`), drawn crisp at the pixel and absent from the bake:
the hood's cream trim, the shin seams and fasteners, the boot rims,
the bracers' ovals, the mouth, the stripes on the shoulder plates and pods,
the cuirass neckline and seams, and the upper lids and lashes where their tubes cross the eye
whites. The near-ellipsoidal forms use
exponent 2.1, a slightly fuller form than the ellipsoid (exponent 2). Check `world.budget` after geometry edits:
the rebuilt world was verified at `stepScale 1`.

The torso follows the concept's fitted cream cuirass: a tapered outline meets
a shallow ridged front, with a lavender neckline and separate lilac plates
wrapping around the ribs. One small cream abdominal plate sits above the
segmented charcoal waist, compact gold belt and outlined pelvic inset. The
chest, side plates and abdomen inherit the torso's breathing and lean; the
belt and pelvic armor inherit the pelvis. Fine panel joins are paint, not
thin raised strips. The debug studio's `studio-torso` layout frames the front,
both quarters and an oblique profile with the full avatar visible.

The pauldrons follow the concept's compact domed caps, swept lower plates and
lavender front pivots with small cream fasteners. A cream stripe wraps each
lower plate; a fine painted seam divides the crown. Rounded underside cuts
clear the upper arms, and both layers follow the existing upper-arm rig.
`studio-armor` shows front, quarter, rear quarter and full-body views together
on the debug platform.
The mesh audit still finds a few small folds at the left shoulder's clipped
underside and cap rim; the groups are closed, with no uncovered surface.

The hard armor has restrained edge chips revealing primer and dark alloy,
with small asymmetric scuffs on the shoulders, cuirass, gauntlets and thigh.
The curvature mask concentrates wear on tight exposed edges; sparse parented
paints place the longer scratches. Skin, hair and the flexible charcoal suit
retain their own materials. Brass and nozzle metal use a separate duller
substrate, and the painted plates retain their clear coat between worn areas.

The boots follow the reference's lavender armor, fitted cream ankle arches,
sloping cream insteps, shaped violet toe caps, separate dark soles and split heel
pads. Each recessed heel jet points 55 degrees below rearward horizontal in the
boot's rest frame; its lens and exhaust share that axis. The cyan lens and pale
core stay softly lit at rest. An axial clearance cut passes through the cream
instep and rubber ankle behind the nozzle; the raised rear arch clears the barrel.
The two violet heel guards have softened concave cutouts. The existing airborne
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

Check document admission and the stamp budget:

```powershell
dotnet src/Puck.Cli/bin/Release/net11.0/Puck.Cli.dll creation stats --world worlds/debug/avatars/moth.puck --prototype moth
```

Then inspect the live front, rear and face views. Exercise the airborne pose
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
