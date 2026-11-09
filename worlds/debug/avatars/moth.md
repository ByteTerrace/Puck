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

## Look intent

The references, in the pack's reading order: the suited turnaround (sheet 01)
for silhouette, proportion and plate layout; the isolated wing jetpack (07) for
the back; the construction details (02) for each joint; the off-suit
turnaround (04) for the pilot. The traits a capture is judged on, from any
side and at the chase camera:

- **Mass before detail.** A heavy suit of plates sitting on plates, each plate
  flat-faced with a chamfered edge that shows its thickness, over charcoal
  joints that leave room to bend. Soft, pillowy or sausage-shaped plates fail:
  a plate is a superellipsoid of exponent 4 to 6 or a chamfered prism, never
  the body's exponent 2.1.
- **The back is a flight pack flush to her back**, two pods beside a narrow
  spine, each one convex shell narrow at its domed cap, widest at the second
  band and tapering down its outer side to a long lip, with two raised ivory
  bands (the lower with a grey inset) and one nozzle tucked under the lip's
  inner end, the hanger under the hood behind the shoulder slabs and the lip at
  the upper pelvis. A flat-faced box, a pod deeper than it is wide, or one
  hanging to the thighs fails.
- **Lavender and ivory dominate**, dusty and warm (the owner source's
  `#b186b6` and a peach-cream, never white), with a small ochre collar and
  belt and restrained cyan lights; a light that blows out to a white disc at
  night fails as surely as one that vanishes by day.
- **Her face is the charm**: her real head inside the hood, the braid rooted
  by her right cheek and leaving through the hood's front opening.

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
rule).

| Keyboard and mouse | Gamepad | Action |
|---|---|---|
| W / S, up / down arrows | Left stick, d-pad up / down | Move forward / backward |
| A / D | Left stick, d-pad left / right | Strafe |
| Left / right arrows | | Turn |
| Left mouse drag | | Orbit the gameplay camera |
| Right mouse drag | Right stick | Steer: orbit the camera, the body turning to face where it looks |
| Left and right mouse buttons held | | Walk forward while the right button steers |
| Left Shift (hold) | | Run: 10 m/s where she walks at 5, and 10 where she flies at 5 |
| N | Left stick press | Switch running on or off |
| J | West | Dash along movement, or facing while idle: a 0.2 s burst with a 0.8 s cooldown, torso lean and back-thruster flare |
| Mouse wheel | | Zoom the camera boom between the standard's authored limits; while the play wheel is open its hold pages bind scrolling to the ring instead |
| Middle mouse button | | Reset camera angles and boom distance to the authored rig |
| Z held, then N | Left trigger held, then left stick press | Autorun: switch walking forward on or off; it keeps going with the trigger released, and the stick still steers and strafes |
| F | North | Switch flight mode on or off |
| Space | South | Jump, twice before landing; in flight mode, rise |
| Left Ctrl or M (hold) | Right stick press (hold) | Fly: switch flight mode on if it is off and climb; letting go hovers |
| Z held, then M | Left trigger held, then right stick press | Autofly: switch flying (flight mode on, climbing) on or off; it keeps climbing with the trigger released |
| C | East | In flight mode, descend and land |
| E | RB | Context button: engage the device in reach, and leave it again ([devices](../devices.md#engaging-a-device)) |
| L, or Z held, then L | Left trigger held, then East | While engaged, switch between the framed picture and the other view (`player.frame`) |
| R (hold) | Left trigger held, then North (hold) | Recenter the camera behind the body |
| Z held, then O (hold) | Left trigger held, then RB (hold) | Free look: the right stick orbits the camera without turning her (`player.look.free`) |
| T | Right trigger held, then South | In the debug world, take the suit off or put it back on while standing; out of it she is the 1.68 m pilot ([moth-pilot.md](moth-pilot.md)), who walks, runs and jumps once, with no flight mode ([`suit.puck`](../suit.puck)). Keyboard equivalent on the Debug page: X held, then K |
| P | Right trigger held, then left stick press | In the debug world, noclip: switch her collision off or on, anywhere ([`noclip.puck`](../noclip.puck)). With it off she passes through walls, floors, placements and other bodies and flies, in the suit or out of it: the movement keys and stick at 5 m/s, 10 running, rise (Space, Left Ctrl, M) and descend (C) climb and sink, and letting go hovers. Switching it back on collides again where she is; in the suit she is left hovering in flight mode, and out of it she falls. Keyboard equivalent on the Debug page: X held, then N |
| Enter | Start | Start on an engaged device; the Moth ignores it |
| Right Shift | Back | Select on an engaged device; the Moth ignores it |
| 1 | | Native three-quarter inspection |
| 2 | | Native front inspection |
| 3 | | Native rear inspection |
| 4 | | Wide flight inspection |
| 5 | | Following gameplay view |
| F3 | | Toggle the annotation outlines (`view.annotate`) |
| F4 | | Show the jetpack alone (`view.isolate`) |
| F2 | The play wheel's Build sector | Switch between playing and building (`player.build`); in build mode the pad's Back returns to play |
| Backspace | The play wheel's Respawn sector | Respawn at the stage spawn, standing still, from anywhere (a fall or the portal loop included) |
| Tab (hold) | LB (hold) | Open the play wheel |
| Q (hold) | | In the debug world, open the gallery wheel |
| Z (hold) | Left trigger (hold) | The View page of the bar: camera layouts, autorun and autofly, recenter, free look and the frame toggle |
| X (hold) | Right trigger (hold) | The Debug page: inspection verbs, noclip, frame comparison, stop and the suit |
| Z then X (hold) | Left then right trigger | The Go page: the debug world's places (`debug.go`) |
| X then Z (hold) | Right then left trigger | The Tour page: the gallery tour, its lights and motion, two paintings |
| Backtick | | Show or hide the console |

Out of flight mode, letting go of Space while still rising cuts the jump short.
In flight mode, releasing Space holds altitude. Fly is rise pressed without
jump, which the `fly-1` rule reads as switching flight mode on, so Space and
South, which press both, stay a jump; `body.press rise` from the console flies
too. The run toggle, autorun and autofly latch the run, forward and rise
channels on the input side: the simulation reads them as held, and a second
press lets go. Inspection cameras stay at the studio origin; use **5** when
moving away from it.

The debug world, the garden and the night field draw these controls on screen
from [`controls.puck`](../controls.puck): the standard controller bar along the
bottom edge (the d-pad diamond with LB and the left stick's press, the face
diamond with RB and the right stick's press, each plate showing its action) and
the play wheel. The bar has five pages of twelve plates, named under it, and a
row on its anchor line that lights each trigger, bumper and key that opens a
page or a wheel while it is held: nothing held is Move (the table above), and
the Moth's own controls are everything above. Each trigger page keeps the
sticks and the movement keys, and rebinds the twelve controls; its keyboard
equivalents are the arrow keys for the d-pad, I, J, K, L for North, West, South
and East, U and O for LB and RB and N and M for the stick presses
([`pad-kit.puck`](../pad-kit.puck)). N and M are the stick presses on Move
too; Move's face buttons keep their own keys (F, Left Shift, Space, C, E).

| Page | Held | Plates (d-pad up, right, down, left; LB; L3; North, West, South, East; RB; R3) |
|---|---|---|
| Move | nothing | forward, strafe right, back, strafe left; wheel (LB); run on/off; flight, run, jump, descend; interact; fly |
| View | LT or Z | chase, three-quarter, front, rear; wide; autorun; recenter, jetpack alone, face, frame; free look; autofly |
| Debug | RT or X | where, history on, console, capture; inspector on/off; noclip; debug view, hold frame, suit, compare; clear hold; stop |
| Go | LT then RT, Z then X | stage, gallery, portals, Wonderland; hero studio; devices; chess, computer, gallery levels 0 and 1; levels 2 and 3 on RB and R3 |
| Tour | RT then LT, X then Z | next exhibit, flag, previous exhibit, quality; slice; update rates; lights off, lights on, motion off, motion on; garden painting; night painting |

Go and Tour name the debug world's places and gallery
([`controls.puck`](../controls.puck)) and read empty in the garden and the night field.

The play wheel has Act, View and Bar rings, with Places added in the debug world.
Hold Tab and point with the mouse, or hold LB and aim the right stick,
then release over a sector. Up or Down on the arrow keys, the d-pad or the mouse
wheel steps the ring while it is open. Act: Console, Build, Where
(`body.where`), Channels (`body.channels`), Respawn (back to the stage), Suit
(`body.press suit`), Stop (`body.stop`) and Annotate. View: Play (the play view
alone, `view.override layout play`), three-quarter, front, rear, wide, face,
Desktop (the desktop pane, `view.override layout desktop`) and the chase
camera. Places: the stage, the hero studio, the gallery, the portals, the chess
table, the computer, the devices deck and Wonderland (`debug.go`). Bar selects
clusters, keyboard/pad strips, wings, flanking sidebars, and compact/full size;
the same per-seat settings are available through `run controls.bar` and
`run controls.bar.compact` ([standard controls](../../standard/README.md)). Releasing at
the hub cancels. The bar fades after six seconds without input, and the bar and
the cursor step aside while the seat's view frames an engaged screen (the held
view of a carried screen keeps the bar); the bar also steps aside while a wheel
is open.

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

The creation uses superellipsoids for soft volumes, extruded and tapered prisms
for armor plates (a plate is flat-faced with chamfered or rounded edges), and
quadratic sweeps for digits and facial lines. Scoped subtraction and
intersection isolate the hood opening, eye sockets, plate boundaries and the
greaves' arches. A raised ivory band is the shell it sits on, a finger proud,
cut between two planes, so it reads as part of the plate with its edges
showing depth. Detail that is colour rather than form is paint
(`palette[].paints`), drawn crisp at the pixel and absent from the bake:
the chest slab's lavender centre, the gauntlets' lavender inner halves, the vent slot pairs on the
outer faces of the gauntlets and shin panels, the boot seams, the mouth and
nostrils, the eyes' catchlights, the grey insets on the pods' lower bands, and
the upper lids and lashes where their tubes cross the eye whites. The near-ellipsoidal forms use
exponent 2.1, a slightly fuller form than the ellipsoid (exponent 2). Check `world.budget` after geometry edits:
the rebuilt world was verified at `stepScale 1`.

The palette is the concept pack's: lavender plates authored `lavenderPlate`, a
light greyed lavender chosen so the studio's lighting lands it near the owner's
dusty `#B186B6` rather than a plum, and warm ivory plates authored `ivoryPlate`. The colour-conversion path and the studio's light
brighten and cool both, so judge a palette change from a capture, sampled
against the sheet, never from the hex alone. The plates carry no wear: every
plate's weathering floor is `plateWear`, 0, and lane 0 is constant.

Her head is the [`moth-head`](moth-head.puck) library's `mothHead` template,
placed by `headAt` and scaled by `headScale` (0.7, about 0.27 m crown to chin)
about the face's front at eye level. The template scales every position, extent,
curve, sweep radius and blend radius, and takes the creation's palette slots, so
the off-suit pilot ([moth-pilot.md](moth-pilot.md)) wears it at the same scale. The mouth, nostril, catchlight and lid paints and the
blink frame (`blinkPose`) live in the creation and scale by the same
`headScale`. Her face sits recessed behind the hood's raised ivory bezel, with
dark headroom round it, and the skin's wrap and warm fill raised so it reads lit
inside the hood. The face is
the character's charm, after sheet 03: a small soft nose (one rounded tip
blended into the face's group, so it bakes as one surface with the face),
almond eyes
in softly rounded sockets, swept brows and a painted smile. Her black hair is a
scalp cut to a soft hairline and four tapering locks smoothly joined to it: a
thick fringe swept from the parting on her left across her forehead to her right
temple, a shorter one back to her left temple, and a lock down each side of her
face. The fringe locks root inside the scalp, so nothing stands up from her
crown on either form. The hood is a wide dome, wider than it is tall, with a
thick, rolled ivory bezel (`helmetRim`) leaning 14 degrees forward round a
horseshoe opening that is open over the collar, its front plane ahead of her
face. Its charcoal interior is its own cut (the cutter's
material), rounded where it meets the face opening, which is itself a rounded
cut, so the rim and the interior bake clean. Each ear disc (`hoodEar`) is broad
and flush, faced out and a little forward so it reads as a disc from the front; the ochre collar stands forward
round her neck. The braid roots under her right side lock as one smooth
tapering sweep that becomes the plait below her jaw and leaves through the
hood's front opening.

The torso carries one flat chamfered slab (`chestSlab`), ivory round a painted
lavender hexagon, with a lavender rib plate down each flank under the arms
(`flank-left`, `flank-right`), above the segmented charcoal waist and the ochre
belt; the ochre collar stands above it at the neck. A lavender plate
(`pelvicShield`) narrows below the belt, and its twin behind (`seatShield`)
covers her seat between the pods' lips. The slab and flanks inherit the
torso's breathing and lean; the belt and pelvic plate inherit the pelvis. The
flight pack is the `flightPod` template, once a side: a charcoal mount riding
the torso, with the shell, the two bands and the nozzle parented to it, and its
hinge on the torso under the hood; the narrow spine between the pods is one
chamfered prism. Each pod is about 0.33 m wide from 0.95 to 1.65 m, standing
past her sides: its rear
outline (`podOutline`) extruded and domed by an ellipsoid into one convex
lavender shell with the cap's small opening cut into its back; an elliptic
cylinder across the pod sets its side profile, thickest at the second band and
thinning to the cap and down the long lip. Each ivory band is
the same shell a finger proud of it between two planes that slope down to the
outer side, in its own group, so its edges show its depth without a seam in the
shell. The lip sweeps down and out; the nozzle, a large charcoal barrel set
square in the lip's underside on its inner end, is aimed in, down and a little
back, its cyan lens softly lit at rest.

Each shoulder stack is one solid convex lavender shell from collar height to the
bottom of the chest, wrapping the top and outside of the upper arm: its front
outline (`shoulderOutline`) slopes down and out, and an ellipsoid domes its front
and back. A lavender cap plate and an ivory band below it are the same shell a
finger proud between planes that slope down and out, so the stack reads as plates
on plates. A charcoal pivot disc with a raised boss shows in front of the joint
at the shell's inner edge. All ride the upper arm, and the shell's inner top
stays outboard of the hood through the gait and the flight splay. Each hip carries an ivory guard, a large
charcoal disc and a raised boss at the pelvis's front corner, faced out and
forward so the disc reads round from the front and the side; the thighs are bare
charcoal and swing behind them.

The arm is a short upper arm, an elbow at 1.22 m and a long forearm, as on the
sheets: the charcoal elbow joint shows between the shoulder stack and the
gauntlet, as wide as the shoulder stack: a slanted ivory tube along the forearm
from below the hinge to the wrist, its rounded section extruded with chamfered
ends and open at the cuff the hand comes out of, faced square to the body rather
than the forearm's twist. The elbow bends about its lateral axis, which a
charcoal hinge disc and boss cap on its outer side. The gloved hands are a third larger than
the first Moth's, scaled about the wrist.

Each piston is a rod entering a cylinder, attached at both ends, and needs no
driver of its own: a steel rod hangs from a charcoal eye on the upper bone and a
satin steel cylinder rises from the lower bone (from an eye seated on the
gauntlet's top at the elbow, in front, inside the bend; out of the greave's top
behind the knee's outer side). The forearm and shin swing by `elbowSwings` and `kneeSwings` at k = 1, and
the rod and cylinder take the same swings scaled about their own lugs by the
share of the bend that keeps them on one line, so the rod slides into the
cylinder as the joint bends.

The greaves flare straight from a narrow top under the knee to a wide foot,
flat-faced: each is a front trapezoid and a side trapezoid intersected with
rounded edges, under an octagonal chamfered ivory knee guard that rides the thigh
and stands proud of the greave's lavender front. A broad ivory band wraps the
greave's foot, arched over the instep where the boot runs in, and an ivory
pentagon panel with its vent pair sits on the outer face. The boots are one
lavender piece from heel to toe with a raised toe cap on one charcoal sole. The
boots carry lights and never fire (the concept pack): a flush round cyan light
low on each greave's back and each ankle hub's cyan lens in its charcoal bezel
stay softly lit by day and at night. The [debug studio](../debug.md#avatar-studio)
provides front, side, low rear and quarter views on a neutral platform.

The rig swings the thighs, shins, feet, arms and forearms during walking.
Two surface effectors plant the feet in alternating stride phases. Their target
is the boot origin, held 0.1875 units above the floor, with sole alignment to the
contact normal and replanted steps when the resting stance drifts. The torso has
a small breathing motion.

In flight mode she flies as sheet 10 shows. The airborne `air` driver tucks the
hips and knees for a jump, which flight mode would otherwise hold in a hover, so
the `flight` swings (`airPose`) undo that tuck at zero vertical speed and leave
the legs hanging straight with a slight bend. Moving in flight pitches the whole
body 0.6 rad forward about her middle (the pelvis's `cruise` swing), so the legs
trail; the head looks back up 0.4 rad, and turning banks the torso. The `cruise`
swings use a constant wave: the pitch is a pose, the same at any speed, that the
gate eases in and out. The blink frame hides the open-eye pieces and seats
closed lids in their place, using the existing scheduled `blink` state.

The `flight` and `wings` drivers read `airPose` and `airWings`. Existing server
rules and damped state responses provide takeoff and landing transitions. Each
pod deploys approximately six degrees outward and four degrees aft. No extra
fan blades deploy. Two bounded `flow` volumes, one per pod on lane 1, supply
the animated translucent exhaust, the only exhaust she makes. Each pod's plume
leaves its nozzle along the nozzle's axis and widens to two and a half times its
mouth over 0.36 m, short, as sheet 10 draws it. The pods' pale nozzle cores
use flight-mode lane 2; their cyan lenses and the boot lights glow at rest
whatever the lane. In flight mode her arms hang at her sides.
Lane expressions use keyed state syntax, unlike the
drivers' `state.airPose.$body` binding. Collision and movement controls remain
the world kit's responsibility.

Travel direction turns the gait, and the damped landing driver adds joint bend
and pelvis displacement as the body settles after impact.

## Verify edits

Check document admission, the clamps and the bake she will cost:

```powershell
dotnet src/Puck.Cli/bin/Release/net11.0/Puck.Cli.dll creation stats --world worlds/debug/avatars/moth.puck --prototype moth
dotnet src/Puck.Cli/bin/Release/net11.0/Puck.Cli.dll mesh audit --world worlds/debug/avatars/moth.puck --prototype moth --cpu
```

The audit's `triangles=` line is her pooled bake with its levels, and each
`group` line names the shapes that cost the most and the cell they baked at; a
small round section (a stud, a rod, a sphere under 4.5 cm) in a group drives
that group's cell fine, so a part that need not be round is a prism or a box.

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
