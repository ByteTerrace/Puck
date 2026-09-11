# Moth flight studio

[moth.world.json](moth.world.json) contains a playable SDF character rebuilt from
[the Moth Study](../studies/moth.glsl). The creation starts with new shape IDs,
directly authored dimensions and a new rig. The earlier character's geometry,
pose frames and `mothScale` / `mothRot` / `mothJoints` bindings are retired.

The Study remains the visual standard. Both render inside Puck. The native model
uses curved ivory shin shells with lilac arch trim, swept shoulder plates, an
open hood, a compact collar, inset eyes and a braid rooted in the front opening.
Exactly two curved flight pods carry two ivory bands and a recessed nozzle each.
The materials include a modest clear coat, studio reflections and restrained
edge weathering. Fine sculpting and surface finish still differ from the Study.

## Open the studio

Run from the repository root:

```powershell
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/moth.world.json --state-dir artifacts/moth-demo/state --exit-after-seconds 0
```

With an existing Release build:

```powershell
dotnet src/Puck.World/bin/Release/net10.0/Puck.World.dll --world src/Puck.World/Assets/worlds/moth.world.json --state-dir artifacts/moth-demo/state --exit-after-seconds 0
```

The initial view is the native model's three-quarter inspection. In the running
Puck console, open the live comparison:

```text
view.override layout study
study.watch moth-study on
```

The Study is on the left and the native SDF creation is on the right. Both read
the `three-quarter` camera. `study.status` reports the compile and watch state;
`study.reload moth-study` recompiles the Study when needed. Inspection layouts
switch immediately so a close-up does not render overlapping camera transitions.

## Controls

| Input | Action |
|---|---|
| W / S | Move forward / backward |
| A / D | Strafe |
| Left / right arrows | Turn |
| Space | Take off / rise |
| Ctrl | Descend and land |
| 1 | Native three-quarter inspection |
| 2 | Native front inspection |
| 3 | Native rear inspection |
| 4 | Wide flight inspection |
| 5 | Following gameplay view |
| Left mouse drag | Orbit the gameplay camera |
| Right mouse drag | Steer |

Releasing Space holds altitude. Inspection cameras stay at the studio origin;
use **5** when moving away from it. A gamepad uses the left stick to move, the
right stick to steer, the south face button to rise and the east face button to
descend. Return to the comparison with `view.override layout study`.

## Edit the native character live

The `moth` creation's `document.shapes`, `palette`, `drivers`, `frames` and
`volumes` are the authoring surface. Shape positions and pivots use a common
rest frame: +Y up, +Z forward, with the sole near Y=0 and the hood crown near
Y=2.06. Most Study dimensions are halved; the head also follows the Study's
smaller, seated head transform. Parent names carry animation deltas, so child
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

For larger edits, save the world JSON and issue `world.reload`. This replaces
the authored geometry, materials and rig without restarting the GPU host. It
also clears the live mutation journal, so preserve live edits before reloading.
The document remains the editable source; no model-generation script is needed
at runtime.

Live look-only edits currently retain the registration's previous lane
expressions. When changing `looks.rows[].motion.lanes`, also change and reload
the creation document, or restart the studio, so the registration is recreated.

For matching close-ups in the comparison, edit the `three-quarter` camera's
`rig.operations`. Both panes follow that row. If copying another camera's whole
row, keep the row name `three-quarter` and give its rig a unique name; duplicate
camera-program names are refused. Reload the world to restore the saved view.
Standalone `face`, `face-side`, `head`, `rear`, `back-close`, `shoulder`, `boots`,
`boots-side` and `boots-rear` layouts remain available through `view.override`.

The [Study workflow](../../README.md#shader-studies) describes shader edits and
its pose controls. The native rig responds to the world body independently of
the Study's selected pose.

## Geometry and motion

The creation uses superellipsoids for smooth volumes, convex extrusions for
armor plates, quadratic sweeps for digits and facial lines, and axial profiles
for flared shins. Scoped subtraction and intersection isolate the hood opening,
eye sockets, plate boundaries and ankle arches. The near-ellipsoidal forms use
exponent 2.1; exponent 2 currently takes the older ellipsoid path and must not be
substituted casually for thin features. Check `world.budget` after geometry edits:
the rebuilt world was verified at `stepScale 1`.

The new rig swings the thighs, shins, feet, arms and forearms during walking.
Two surface effectors plant the feet in alternating stride phases. Their target
is the boot origin, held 0.1315 units above the floor. Flight bends the knees and
splays the arms; speed and turn rate add a forward lean and bank. The torso has
a small breathing motion. The blink frame hides the open-eye pieces and seats
closed lids in their place, using the existing scheduled `blink` state.

The `flight` and `wings` drivers read `airPose` and `airWings`. Existing server
rules and damped state responses provide takeoff and landing transitions. Each
pod deploys approximately six degrees outward and four degrees aft. No extra
fan blades deploy. Two bounded `flow` volumes supply animated translucent
exhaust; render lane 1 uses the expression `airPose[$body]`, while the boot lights
remain illuminated at rest. Lane expressions use keyed state syntax, unlike the
drivers' `state.airPose.$body` binding. Collision and movement controls remain
the world kit's responsibility.

The rig remains a prototype: it does not implement contact-normal sole alignment,
a directional ground gait or a dedicated impact-and-recovery animation.

## Verify edits

Check document admission and the stamp budget:

```powershell
dotnet src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll creation stats --world src/Puck.World/Assets/worlds/moth.world.json --prototype moth
```

Then inspect the live front, rear and face views. Exercise takeoff and landing:

```text
body.stop
body.pose 0 0 0 0 0 0
view.override layout flight-stage
body.fly 0 0 1 0 0 0 1
world.wait 40
body.where
body.hold
body.fly 0 0 -1 0 0 0 1.4
world.wait 50
body.where
body.hold
view.override layout study
```

The first readback should report `airborne` with `hold=air`; the final readback
should report `grounded` with `hold=ground`. Capture the rear during a low hover
to inspect exhaust clearance and pod deployment. Keep native and Study cameras
matched when judging proportions, and distinguish a pose difference from a
geometry difference.
