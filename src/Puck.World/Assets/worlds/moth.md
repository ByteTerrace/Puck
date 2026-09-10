# Moth flight studio

[moth.world.json](moth.world.json) is a standalone character prototype for inspecting
the futuristic Moth concept inside the real engine. Ivory and lilac armor, an open
hood, a dark braid, and folding flight vanes establish the silhouette. The model is
an initial procedural interpretation; facial construction, armor paneling, and
flight effects still need art refinement before it represents the concept's final quality.

The current refinement follows the selected Moth collage: warm brown skin and a
side braid, a five-sided ivory cowl framing the face, lilac shoulder plates, a split ivory
chest collar, and a small cyan clasp. Broad boots and dark joints keep the stance
readable. Three overlapping blades on each flight vane fan out from a lower back
hinge during flight and fold upright on landing. The creation stays within the
`WorldPlacementPolicy.MaxShapesPerStamp` stamp budget (`puck creation stats
--world src/Puck.World/Assets/worlds/moth.world.json --prototype moth` reports
the live count against it). The shapes include separate toe caps, soles, temple
guards, shoulder layers, bracer insets, and thigh plates.

The author frame is +Y up and +Z forward, with the soles at Y=0 and the hood crown
near Y=2.31. Parent pivots and dimensions are authored in that same frame. The
hood's pentagonal extrusion and rounded-rectangle subtractive opening share
composition group 1, so the opening cuts only the hood. Tapered and polygonal
armor shells mix straight edges with rounded profile corners. These remain
procedural art studies rather than a finished production model. The cheek and chin ellipsoids blend within group 2;
eyes, brows, hair, and braid remain separate moving parts. Lower armor highlights
and slightly turned-out boots keep the stance from looking rigidly mirrored.
Armor and vane edits change appearance only; movement speeds and collision stay
in the existing walker kit. AO and high shadows are enabled in the studio document.

## Open the studio

Run from the repository root in an interactive desktop terminal:

```powershell
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/moth.world.json --state-dir artifacts/moth-demo/state --exit-after-seconds 0
```

If the Release build already exists, skip the build:

```powershell
dotnet src/Puck.World/bin/Release/net10.0/Puck.World.dll --world src/Puck.World/Assets/worlds/moth.world.json --state-dir artifacts/moth-demo/state --exit-after-seconds 0
```

The document opens a window and uses a separate state directory in these recipes.
For agent-driven sessions, launch on the user's interactive desktop; a sandboxed
process can have a native window that the user cannot see. Confirm visibility
through the desktop window list and bring **Puck: World** to the foreground.

## Controls

The selected movement direction is nimble, with visible weight and function in
the wing assembly. Body flight pose transitions use 0.07 s in / 0.10 s out time
constants; the separate wing driver uses 0.22 s in / 0.34 s out. Wings therefore
continue deploying after the body responds and stow after the grounded body has
settled. These are damped pose transitions, not a physical wing-inertia solver.
Turn and speed still articulate the vanes around their authored hinges. Keep
those hinges and shoulder clearance when replacing the current armor geometry.

| Input | Action |
|---|---|
| W / S | Move forward / backward |
| A / D | Strafe |
| Left / right arrows | Turn |
| Space | Take off / rise |
| Ctrl | Descend; continue to the floor to land |
| 1 | Three-quarter inspection |
| 2 | Front inspection |
| 3 | Rear inspection |
| 4 | Wide flight inspection |
| 5 | Following gameplay view |
| Left mouse drag | Orbit the gameplay camera |
| Right mouse drag | Steer |

Releasing Space holds altitude. Inspection cameras stay at the studio origin;
press **5** when moving away from it. A gamepad uses the left stick to move, the
right stick to steer, the south face button to rise, and the east face button to
descend.

The pelvis carries the rig's weight shift and step bounce; the torso counter-rotates
with the stride and the neck compensates to keep the face readable. Knees flex on
the swing half of the cycle, with smaller ankle and elbow articulation. The two
surface IK chains latch during alternating support phases and ease their influence
out during swing (`plant.swingWeight: 0`), returning to surface following at rest.
The target is the boot origin, held 0.13 units above the surface. Leaving the ground
clears the contact latch so landing acquires a fresh world point.

Flight opens the three blades of each vane by different amounts. Turn rate drives
body banking and differential vane rotation; speed supplies a restrained forward
lean. One `vertical` driver adds the ascent and descent poses: its ascending
consumers take `halfSine` at phase 0, the descending ones the same wave a half
turn later. These read rendered motion: the powered lift hold does not publish
`Rising` and `Falling` during every vertical movement. The flight pose and the wing
deployment are simulation state: the `airborne-on`/`airborne-off` rules copy the
body's `$fact:each:Airborne` bit into the `airPose` and `airWings` cells, each
eased through its own `dynamics` row (`flight-pose`, `wing-deploy`), and the
`flight`/`wings` drivers read those cells through `$body` with `linear` waves, so
every client shows the same pose at the same tick. The blink is scheduled the
same way: `blink-close`/`blink-open` flip the `blink` cell against a `$tick`
deadline in `blinkAt`, redrawing the rest from the `blinkRest` uniform site, and
the look's `motion.poses` selects the `blink` frame while the cell is nonzero.
Three named second-order position followers add progressively softer braid
follow-through, while armor stays rigid. Every rotation, every shared scale, every
joint pivot, and the stride/breath tuning are state cells (`mothRot`, `mothScale`,
`mothJoints`, `mothTuning`); `world.state.cell.set mothTuning strideCadence 6.5`
retunes the rig live. The shape budget leaves room for further refinement.

This is still a motion prototype. It has no contact-normal sole alignment, authored
directional ground gait, impact/recovery sequence, climbing transition, or hurt
reaction. The speed signal includes vertical travel, so the flight lean is not a
directional acceleration model. Braid followers do not enforce strand length or
collision. These limits remain visible acceptance work, not finished animation.

## Iterate in the running window

`view.override layout head` opens a close-up inspection camera for the face and
helmet. The front, three-quarter, rear, and flight-stage layouts remain available.

Edit the world document and issue `world.reload` through the process stdin console.
It reloads model geometry, palette, rig, cameras, and controls without restarting
the GPU host. It also clears the live mutation journal; copy any live edits into
the file before reloading. The running host's render capacity still limits how
much new geometry a reload can add.

This reload replaces VM programs and authored data. For an HLSL edit, run
`dotnet msbuild src/Puck.SdfVm/Puck.SdfVm.csproj -t:CompileShaders -p:Configuration=Release`,
then issue `world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf` in the live
console. `world.shaders.status` reports completion or failure. No application
rebuild, bytecode copy, or restart is needed for a shader-only change. The
[engine guide](../../../Puck.SdfVm/README.md#reload-compiled-shaders) explains
the transaction and the fixed host-binding boundary.

The character is the `moth` prototype; its `document.shapes`, `palette`, and
`drivers` are the authoring surface. Parent names form the animation hierarchy.
The `looks` section assigns that creation to the player. The floor explicitly
allows surface holds through `grip.holdable`, so the grounded hold wins over free
hover on landing.

The rig's shapes are authored as document data, not generated: live edits go
through `world.row.set`/`world.row.step`, and the document is saved with
`world.save`. No generator is shipped for this rig.

### Live edits

`world.row.set`/`.add`/`.remove` reach one field or one list element inside the
`moth` row directly — no reload, no journal, applied at the next tick boundary —
through the console path `creations` (the document's own JSON member is spelled
`prototypes`; the console path stays `creations`, its C# section name). A shape
is addressed by `document.shapes[name=<shapeName>]`, a palette entry by its
0-based index:

```text
world.row.set creations moth document.shapes[name=forearmL].rounding 0.03
world.row.set creations moth document.palette[1].specular 0.25
world.row.add creations moth document.shapes {"id":900,"type":"Sphere","name":"probe","position":[0,2,0],"rotation":[0,0,0,1],"scale":[0.05,0.05,0.05]}
```

A field that reads a state cell (most of the rig's rotations bind
`state.mothRot.*`) keeps its binding across an edit, and a binding can be
written the same way: `world.row.set creations moth
document.shapes[name=forearmL].rotation "state.mothRot.identity"`.

Read a field or list back with `world.row creations moth <fieldPath>`; a bare
list field (`document.shapes`) lists every element as `[world.row <index>:
<name> <json>]`, and `world.row creations moth` echoes the whole row without
its `hash`, ready to paste back through the whole-row `world.row.set creations
<json>` (the key rides inside the JSON) with a field changed. Two edits to the
`moth` row in the same tick window collide — fence with `world.wait` between
them, or paste one edited whole row instead.

For a repeatable takeoff, hover, and landing check, enter:

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
view.override layout three-quarter
```

At the authored 30 Hz simulation rate, the waits cover the driven segments.
The hover readback should show `airborne` and `hold=air`; the final readback should
show `grounded` and `hold=ground`. Device input remains live during this sequence.
To capture a settled view, issue `world.screenshot artifacts/moth-demo/moth.png`,
then `world.wait 4`, and wait for the capture completion message.

Use `world.shadow-mask exact`, `world.shadow-march exact`, and
`world.ao-quality exact` to pin the fidelity path. `world.shadow-mask camera-tile`
and `world.ao-quality fast` expose the cheaper approximations for comparison.
The exact shadow mask covers reserved instance pools; exact AO includes all live
instances rather than the camera tile's candidate set.

The soft shadow is one deterministic penumbra march per lit pixel, keyed on the
shadowing light's `angularRadius`; there is no frame history, so a moving body
leaves no trail. `render.lighting.lights` and `render.sky.layers` are the lighting
rig; `world.lighting` echoes them.