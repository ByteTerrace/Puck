# Moth flight studio

[moth.world.json](moth.world.json) is a standalone character prototype for inspecting
the futuristic Moth concept inside the real engine. Ivory and lilac armor, an open
hood, a dark braid, and folding flight vanes establish the silhouette. The model is
an initial procedural interpretation; facial construction, armor paneling, and
flight effects still need art refinement before it represents the concept's final quality.

The current refinement follows the selected Moth collage: warm brown skin and a
side braid, a tapered ivory cowl framing the face, lilac shoulder plates, a split ivory
chest collar, and a small cyan clasp. Broad boots and dark joints keep the stance
readable. Three overlapping blades on each flight vane fan out from a lower back
hinge during flight and fold upright on landing. The creation stays within the
existing 48-shape budget; the former separate toe caps now supply the outer blades.

The author frame is +Y up and +Z forward, with the soles at Y=0 and the hood crown
near Y=2.31. Parent pivots and dimensions are authored in that same frame. The
hood's rounded cone and subtractive opening share composition group 1, so the
opening cuts only the hood. The cheek and chin ellipsoids blend within group 2;
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

The rig breathes and blinks at idle, swings arms and legs with grounded travel,
and blends into an airborne pose with opened vanes. Touching down folds the vanes
and restores the grounded rig. Each leg uses the existing two-bone surface IK
and alternating stride-phase foot latch while grounded. The target is the boot
origin, held 0.13 units above the surface; the correction releases in flight.
This improves contact placement, but does not lock sole orientation or supply
separate landing/recovery clips.

## Iterate in the running window

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

The existing shadow estimator remains stochastic. Its accumulation retains the
previous screen pixel's value without motion reprojection, so movement can leave
brief history trails. `world.shadow.accumulate off` isolates that behavior but
exposes raw sample noise. Correcting the candidate masks does not fix that history
limitation or complete the model's art refinement.
