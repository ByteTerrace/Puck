# Editor

Puck has no separate scene editor: the running World is the editor, and a
missing editing affordance is a feature of the World
([engine design decisions](../decisions/engine-design.md#documents-describe-content)).
This programme lists those missing features, in the order a person building a
world needs them. Picture an artist or a player making a level. They want to
place things on a grid and line them up, see what they have selected and how
big it is, undo and redo without fear, find out why something is dark or
missing, and change a file and see the result straight away. Each package below
removes one obstacle in that workflow. Every package lands on its own and
leaves the World more useful than before.

Every package is built from pieces that already fit Puck's model. An edit is a
document mutation through the ordinary door, so it is journaled, replayable,
gated by grants and undoable. Everything else the editor keeps (selection, grid,
snapping, camera, drags, measurements, comparisons) is per-seat presentation
state that never reaches the simulation. The controls are console verbs, levers,
overlay drawing and views, and every control a builder reaches for is bindable
to a key or a button.

The forcing artifact is a district of
[the forcing world](state-and-language.md#the-forcing-world) laid out, lit and
debugged in build mode without typing a coordinate: every placement put down,
lined up and duplicated by pointer and grid, a dark corner diagnosed with
`world.explain` and fixed, the slowest object found with `world.cost`, and the
session saved back into the district's `.puck` source.

## Implementation status

Editing is console-only today. The verbs that change a world are
`world.row.set`, `world.row.add`, `world.row.remove` and `world.row.step` (the
last is bindable and steps one scalar field; it refuses a vector field such as
a position), `world.reflow.preview`/`status`/`commit`/`cancel`, `world.undo`,
`world.reload`, `world.save`, the `pipeline.*` verbs for shader pipelines, and
the `forge.*` verbs for cartridges. `world.undo` replays the journal minus its
tail and is unbindable; there is no redo anywhere except the forge draft's
one-level swap (`CartridgeDraft.Undo`). There are no gizmos, no selection, no
pointer picking of placements, no copy, paste or duplicate, and no measurement.
`world.save` refuses a `.puck` target, so a live edit to a `.puck` world can only
be saved as JSON.

Several pieces exist with nothing using them:

- `src/Puck.World.Authoring/Authoring/GridSnap.cs` holds the snapping math
  (`SnapConfig`, `SnapReference`, `RotationSnap`, `GridSnap.Apply`,
  `SnapRotation`, `SnapYawDegrees`, `SnapToWorldLattice`) and has no caller and
  no law.
- The kernels draw a world grid on every surface and an object grid around a
  reference (`applyWorldFloorGrid` and `applyObjectGrid` in
  `shade/sdf-surface-shading.hlsli`, called from `sdf-light-stage.hlsli`), and
  `SdfFrameBlock` writes `SdfFrame`'s `Grid*` fields into the pass block, but
  `WorldFramePresenter` sets none of them. `GridOverlayState` has no producer.
- `world.debug-view` selects one of twelve modes (`off`, `depth`, `normals`,
  `raydir`, `material-id`, `iteration-count`, `termination`, `slice`, `mask`,
  `overshoot`, `evals`, `visibility`). It is unbindable and sets the mode on
  the main world residency only, so a camera view or a session view never
  shows it. The slice mode is locked to the camera through the origin:
  `SdfFrame.DebugSliceAxis` and `DebugSliceOffset` reach the pass block, and
  nothing sets them.
- Four shading levers reach the pass block with no producer:
  `DisableShadowCull`, `DisableScreenLights`, `EnableShadowProxy` and
  `UseFiniteDifferenceNormals`.
- `src/Puck.SdfVm/Debug` (`SdfDebugMode`, `SdfDebugScene`, `SdfDebugRenderer`,
  `SdfDebugController`, `SdfOrbitInput`, `SdfGalleryScene`, `SdfDriftMonolith`)
  renders a takeover scene no host constructs.
- The GPU carve bake (`SdfWorldTables.RequestBrickBake`, the `sdf.bricks`
  pass) is driven only by `SdfCarveBakePlanner`, which only `SdfDebugScene`
  constructs, and the planner draws its analytic carves through
  `SdfDebugRenderer.EmitCarve`. The brick pool itself is live: height fields
  upload their bricks into it (`WorldFieldEmitter`).
- GPU work is counted, never timed. The timestamp interfaces that once existed
  (`IGpuTimingPool`, `GpuTimingStage`, `GpuTimestampCapabilities` under
  `Puck.Abstractions/Gpu/Timing`) were deleted and survive only in git history.

The building blocks the packages reuse are in place. The overlay draws rects,
rings, wedges, panels, icons and text (`OverlayFrameBuilder`), and has no line
primitive. `MarkerWriter` projects world points into each seat's viewport.
`WorldPointerRayCapture` casts the OS pointer through a seat's camera
(`SourceRay.Through` over `WorldSeatViewports`), and the cursor already outlines
a hovered pane (`WorldViewGraphHost.Hover`, `CursorWriter`). The presenter
rebuilds a CPU `SdfFieldEvaluator` over the static placements
(`WorldFramePresenter.RebuildStaticField`, published as `WorldClient.StaticField`),
and the evaluator implements `IWorldQuery.Raycast`, which returns the hit point,
normal and material. The visibility record names what each pixel sees, but every
static placement shares identity 0 (`frame/sdf-visibility.hlsli`), so the GPU
cannot tell two static placements apart. Free Cam exists as a gameplay mode
that possesses an authored `camera-seat-<n>` body
(`WorldSeatModeState.CameraTarget`).

Prior art lives in git history under `experimental/Puck.Demo/`, which has left
the tree (`git log -- experimental/Puck.Demo` finds the last revision holding
it): the world sculptor (`World/WorldSculptController.cs`: ghost placement preview,
pad-driven select, move, rotate, scale and delete, and a grid-snap toggle), the
shared undo and redo ring (`EditHistory`), the snap-to-overlay policy
(`Editing/GridOverlayFactory.cs`), the creator workbench (`Creator/`), the SDF
debug verbs (`SdfDebug/SdfDebugCommandModule.cs`: `sdf.slice`, `sdf.normals`,
`sdf.shadowcull`, `sdf.gallery`, `sdf.carve` and more) and the bench's
per-pass GPU timing. Read it as evidence of a solved problem, never as code to
revive.

## How a package closes

Each package's check is a set of laws in the test project that owns the code.
Every law has a red leg: the same assertion run against the feature switched
off, or against a planted defect, which must fail. A check that cannot fail
proves nothing. A package adds a `puck canary` only where pixels are the honest
evidence (a grid, a highlight or a debug view drawn on screen), and a new
canary in the merge or automatic set raises
`src/Puck.Cli/Canary/CanaryCeilings.cs` in the same change. A package that adds
a verb adds its read-back and its entry in the build-mode binding group in the
same change.

## Packages

### E1 — Build mode, the grid and snapping

**Problem:** a builder cannot place anything on a grid, because positions are
typed numbers and the grid the kernels can already draw is never switched on.

**Delivers:**

1. **Build mode.** A built-in seat mode family, `editor`, with the states `play`
   and `build`, flipped with `player.mode editor build` and bound to one
   key or button. In `build` the seat's movement input drives the editor rather
   than the body, through the same context-family machinery layouts use
   (`WorldContextFamilies`), and the build binding group ships as a default
   layer shown on the binding bar, so a builder can see what every button does.
   A seat whose principal lacks `Mutate` on the section it points at still
   enters build mode and sees a read-only badge; its edits are refused by
   name, as they are from the console.
2. **The `editor` section.** A document section with a `Default` that keeps
   every current world unchanged: grid hidden, snapping off, a world pitch, an
   angle step, surface snapping, the object-grid patch radius, and the editor
   camera's feel values that [E8](#e8--editor-camera) reads. It is validated by
   name, swept across every shipped world, regenerated with `puck schema`, and
   folded back by `world.save` when a live verb has moved one of its values.
3. **The grid overlay.** The presenter computes a `GridOverlayState` per seat
   from that seat's snap state (the policy `GridOverlayFactory` had: the world
   grid whenever the grid is visible, the object grid when a reference is
   captured) and writes it into the `Grid*` fields of the frame the seat's view
   renders. The grid rides the view's pass block, so one seat can build on a
   grid while another plays; this is the per-view lever work the
   [rendering plan's P14-8 follow-up](rendering.md#p14--the-sdf-engine-as-a-pass-package)
   also needs, and whichever lands first owns it.
4. **`world.grid` and `world.snap`.** `world.grid on|off|pitch <x> [<z>]` and
   `world.snap on|off|angle <degrees>|surface on|off|reference <placement>|clear`,
   both bindable (a toggle and a pitch step as constant values), both echoing
   the seat's whole state when given no argument.
5. **Snapping in placement editing.** `world.place <prototype> [<id>]` places at
   the surface under the seat's pointer, or ahead of its camera when the pointer
   hits nothing. `world.nudge <placement> <axis> <steps>` moves a placement by
   whole grid steps and `world.turn <placement> <steps>` turns it by whole angle
   steps; both are bindable, with axis and sign carried as constant values. Each
   verb snaps through `GridSnap` (grid, angle, the captured object reference's
   faces, and the surface through `IWorldQuery.Raycast` over the client's
   static field) and submits one `placements` upsert through the section upsert
   `world.row.step` uses, never a second write path. Snapping is authoring
   float math: its result is the authored number, which the server quantizes as
   it does any authored position.

**Touches:** `Puck.World.Schema` (the `editor` section, validator, schema
output), `Puck.World.Client` (`WorldContextFamilies`, `WorldSeatBindings`,
`WorldFramePresenter`, a per-seat editor state), `Puck.World.Authoring`
(`GridSnap`), `Puck.SdfVm` (`GridOverlayState`, `SdfFrame`), a new editor
command module in `src/Puck.World`, the shipped worlds, and the World guide.

**Check:** `GridSnapLawTests` (world lattice per axis, a free axis at zero
pitch, the magnetize release band, 24 orientations at 90 degrees, face and
center candidates against a reference; red leg: a disabled config returns its
input); `WorldEditorSectionLawTests` (every shipped world validates and round
trips with the default, a non-positive pitch refuses by name); a presenter law
that a seat in `build` with the grid on renders a frame carrying exactly its
`GridOverlayState` and a seat in `play` renders none (red leg: the grid on
while in `play`); placement-verb laws through the real command registry (a
nudge lands exactly one pitch away, then `world.undo` restores it; a surface
place rests on the fixture floor). Canary `editor-grid`: an offscreen capture
of a floor region differs with the grid on and agrees with the grid off.

**Depends on:** nothing.

### E2 — Selection, picking and highlight

**Problem:** a builder cannot point at a thing and say "this one"; every edit
names a placement id they had to look up.

**Delivers:**

1. **A line primitive.** `OverlayFrameBuilder.WriteSegment` (endpoints,
   thickness, role, alpha), drawn by the overlay kernel as an antialiased
   capsule distance, charged to a new `Editor` overlay channel leased in
   `OverlayChannelLeases`. A world-space polyline projects through the seat's
   viewport camera, the way `MarkerWriter` projects a point, and clips to the
   viewport.
2. **Pointer picking.** The pointer's display point becomes a ray through
   `SourceRay.Through` over `WorldSeatViewports`, as `WorldPointerRayCapture`
   does, but the ray stays in presentation and is never sustained into the
   command plane. It is cast against the client's static field and the drawn
   bodies. A nearest-instance query on `SdfFieldEvaluator` and the stamper's
   instance-to-placement registration turn the hit into a placement. In build
   mode the cursor's hover label names the placement under the pointer.
3. **A per-seat selection.** `world.select pointer|<id>…|add <id>|toggle <id>|clear|prototype <name>|box`
   (`box` selects every placement whose bounds fall inside a dragged
   rectangle), with bindable forms for pointer, add, clear and cycle.
   `world.selection` echoes each selected id, prototype, bounds and pivot. A
   removed row leaves the selection.
4. **Highlight.** Each selected placement draws its oriented bounds, its pivot
   and its id through the line primitive; the hovered placement draws thinner.
   An in-render tint waits for static placements to have their own visibility
   identity (see [Decisions](#decisions)).
5. **Verbs act on the selection.** E1's `world.nudge` and `world.turn`, and
   every later editing verb, act on the selection when no id is named.

**Touches:** `Puck.Overlays` (`OverlayFrameBuilder`, the overlay kernel and its
generated interface, `OverlayChannels`, `OverlayChannelLeases`),
`Puck.SignedDistance` (`SdfFieldEvaluator`), `Puck.World.Client` (the stamper's
registration, the selection, the cursor feed), `src/Puck.World` (the editor
command module).

**Check:** `OverlaySegmentLawTests` (records, viewport clipping, a steady frame
allocating nothing) and `OverlayLeaseTableFitsBackstopsLawTests` moving with the
new lease; `WorldEditorPickLawTests` (a ray through a known placement resolves
its id, the nearer of two overlapping placements wins; red leg: a ray into the
sky resolves none); `WorldEditorSelectionLawTests` (box selection, removal
drops the id, the read-back). Canary `editor-selection`: selecting a placement
by id puts accent pixels around it in an offscreen capture, and clearing the
selection removes them.

**Depends on:** E1 for build mode's binding group.

### E3 — Undo, redo, duplicate, delete and measure

**Problem:** a builder who makes a mistake can undo but not redo, cannot copy
what they made, and cannot tell how far apart two things are.

**Delivers:**

1. **Redo.** `world.redo [n]` re-applies the mutations the last undo removed,
   through the same apply path. The undone tail is kept until a new mutation
   applies, which clears it; `host.journalDepth` bounds it; a load or reload
   clears it. `world.undo` and `world.redo` become bindable, checked at
   dispatch under the pressing seat's principal as `world.reload` is, and
   `world.status` echoes both depths.
2. **One gesture, one step.** Every editor gesture is one journal entry: a
   gesture over several rows submits one `WorldMutation.Batch`, and a drag
   ([E7](#e7--gizmos-and-pointer-dragging)) commits once on release.
3. **Duplicate, delete, copy and paste.** `world.duplicate [<offset>]` copies
   the selection as new rows offset by one grid step and selects the copies;
   `world.delete` removes the selection; `world.copy` and `world.paste` keep a
   session clipboard of rows and paste at the pointer, snapped. Each is one
   batch. How a copy is named is an [open decision](#open-decisions).
4. **Measure.** `world.measure` between the pointer's next two hits, or between
   two selected pivots, echoes the distance, its three components and the
   angle, and draws a labelled segment until `world.measure clear`.

**Touches:** `Puck.World.Server` (the journal and its undo path,
`WorldJournalEntry`), `Puck.World.Protocol` (`IServerLink`), the mutation
command module, the editor command module.

**Check:** `WorldRedoLawTests` (undo n then redo n restores the pre-undo
document hash; a mutation applied after an undo leaves nothing to redo, refused
by name; the journal horizon refuses by name); `WorldEditorDuplicateLawTests`
(one batch, one undo removes every copy, ids stay unique); a measure law over
two fixture placements (red leg: one hit measures nothing).

**Depends on:** E2.

### E4 — Debug views everywhere

**Problem:** the debug views exist, but a builder must type them, they show
only in the main view, and the slice cannot be moved.

**Delivers:**

1. **Bindable views.** `world.debug-view` becomes bindable: a constant value
   selects a mode by index, and two reserved values step to the next and
   previous mode, following `view.override`'s convention.
2. **Every residency.** The mode reaches the main world residency and every
   camera and session residency the screen binder holds
   (`WorldScreenBinder.TryResolveView`), including one created after the switch.
3. **A movable slice.** `world.debug-view slice x|y|z|camera [<offset>]` sets
   `SdfFrame.DebugSliceAxis` and `DebugSliceOffset`, with a bindable offset step.
4. **The four shading levers.** `world.shadow-cull`, `world.screen-lights`,
   `world.shadow-proxy` and `world.normals analytic|finite-difference`, in
   `WorldRenderLeverCommandModule` beside `world.shadows` and `world.ao`,
   bindable and echoed.
5. **Views for lighting questions.** New modes `lighting` (shading over white
   albedo), `albedo` (unlit color), `shadow` (the key light's visibility, the
   record's K row) and `ao` (the occlusion half of the S row). The mode names in
   `DebugViewModes` and the kernel's count and switch
   (`frame/sdf-levers.hlsli`, `debug/sdf-debug-views.hlsli`) move together.

Debug views and shading levers are session levers: `world.save` never folds
them into the document.

**Touches:** `src/Puck.World` (`WorldRenderLeverCommandModule`,
`WorldScreenBinder.Views.cs`), `Puck.SdfVm` (`DebugViewModes`,
`SdfWorldResidency`, the debug and lever kernels), `Puck.World.Client`
(`WorldFramePresenter`).

**Check:** a sync law holding `DebugViewModes.Names` to the kernel's mode
count; `WorldDebugViewResidencyLawTests` (the mode reaches every residency the
binder holds and one it creates later; red leg: the main residency alone, as
today); presenter laws that the slice and the four levers reach the frame.
Canary `debug-views`: a screen showing a camera view changes to the `normals`
colors when the mode is on. This package supplies the missing checks for the
rendering plan's debug-view and shading-lever capability rows.

**Depends on:** nothing.

### E5 — The inspector

**Problem:** to learn anything about what they are looking at, a builder types a
read-back verb and reads its output in a terminal.

**Delivers:** an inspector panel drawn on the `Editor` overlay channel, toggled
by the bindable `world.inspect on|off`. It shows the placement under the
pointer (id, prototype, material and its name, world position, normal, distance
from the camera), a summary of the selection, the camera's pose and field of
view, the simulation and presentation ticks, the render scale, the active debug
view and shading levers, the frame's counted work from `world.counters`
(dispatches, uploads, created objects), the `world.budget` headline (words,
instances, headroom), and the GPU pass times while
[E9](#e9--cost-per-object-and-gpu-pass-timing)'s readout is on. `world.inspect`
with no argument prints the same text; the panel and the verb share one
formatter, so the read-back and the drawing cannot disagree.

**Touches:** `Puck.Overlays` (an inspector writer), `Puck.World.Client`, the
editor command module.

**Check:** `WorldInspectorLawTests` (the formatter's text for a fixture hit
names the placement and material and equals the verb's echo; a steady frame
allocates nothing; red leg: the pointer on the sky prints `hit=none`).

**Depends on:** E2.

### E6 — Why is this dark or invisible

**Problem:** when something does not show up or looks wrong, a builder cannot
ask the engine why, and refusals name rows rather than places.

**Delivers:**

1. **`world.explain [<placement>]`** (the pointer's placement by default) walks
   one placement's path to the screen and names the first thing that hides it:
   absent from the document, withheld by the reader's disclosure, refused by
   stamp capacity or the render envelope (quoting the refusal), drawn as a bake
   with the mesh hidden, a zero or degenerate scale, an instance bound shorter
   than its geometry's reach, beyond the far distance, buried inside another
   solid, or subtracted away. For a placement that is drawn it names what
   lights the surface under the pointer: each light's term at the hit point
   computed on the CPU, whether a shadow ray toward the key light hits
   something (and which placement), the sky and ambient level at the current
   time of day, screen lights in range, emission, and a near-black albedo. Each
   line names the document field that changes the answer.
2. **Problems at their location.** `world.problems` gathers every current
   diagnostic that belongs to a place: refusals naming a placement
   (`world.refusals`), stamp and envelope refusals, instances with unmaskable
   influence, and lint warnings from the world's `.puck` source mapped to their
   placements. In build mode each problem draws a marker chip at its position
   (`MarkerWriter`). `world.problems next` (bindable) selects the next problem
   and, once [E8](#e8--editor-camera) lands, frames it.
3. **`world.goto <placement>`** selects a placement and frames it with the
   editor camera.

**Touches:** `Puck.World.Client` (the stamper's and envelope's refusals, the
presenter's lighting inputs), `Puck.SignedDistance` (CPU shadow rays over the
static field), `src/Puck.World`, `Puck.Overlays` (markers).

**Check:** `WorldExplainLawTests`, one planted defect per reason (stamp
capacity, far distance, an occluder between the surface and the key light, black
albedo, zero scale), each with a red leg that removes the defect and sees the
line disappear; `WorldProblemsLawTests` (a refused placement is listed at its
position).

**Depends on:** E2; E8 for framing.

### E7 — Gizmos and pointer dragging

**Problem:** a builder cannot drag a thing to where they want it; every move is
a number or a step.

**Delivers:** a translate gizmo (three axis arrows, three plane handles and a
screen-plane center), a rotate gizmo (rings drawn as projected polylines, the
yaw ring first since placements author yaw), and a uniform scale handle, drawn
with E2's line primitive in local or world space. Handles are hit-tested in
screen space. A pointer drag solves a constrained delta (the closest point
between the pointer ray and the axis, or the ray's intersection with the plane),
snaps it through E1, and moves a presentation ghost of the placement while the
button is held: the stamper draws the placement at the dragged pose and no
mutation is sent. Releasing commits one mutation, so one undo reverts the whole
drag; Escape or a right click cancels and sends nothing. A gamepad drags along
the camera-relative plane with the sticks, with the same commit rule, as the
sculptor's pad model did. Before adding the ghost, check whether
`world.reflow.preview` already draws proposed positions (rule 8).

**Touches:** `Puck.Overlays`, `Puck.World.Client` (the stamper's presentation
override, the drag state), `Puck.World.Authoring` (`GridSnap`), the editor
command module.

**Check:** `GizmoDragLawTests` (a pointer ray sequence on the X handle moves
only X, snapped to the pitch; red leg: the constraint off moves every axis);
`WorldEditorDragCommitLawTests` (a drag of many frames submits exactly one
mutation, one undo reverts it, a cancel submits none). Canary `editor-gizmo`:
the selected placement's gizmo axis colors appear in an offscreen capture.

**Depends on:** E1, E2, E3.

### E8 — Editor camera

**Problem:** a builder sees the world only through their avatar's camera; they
cannot orbit a thing, fly across the map or frame what they selected.

**Delivers:** in build mode each seat gets an editor view: orbit around a pivot,
pan, dolly, fly, frame the selection (the distance that fits its bounds in the
field of view), and pick the pointer's hit as the pivot. It compiles through the
camera program vocabulary (`orbit`, `clampPitch`, `dynamics`, `fieldOfView`)
with `WorldCameraRigCompiler`, over a world anchor the editor moves. It is a
view override, never a simulated body, so flying through a shared world moves
nothing anyone else sees. Its feel values (orbit, pan, zoom and fly rates, the
pitch clamp, the distance range) are `editor` section fields whose defaults are
the values `SdfDebugController` used. Leaving build mode blends back to the
seat's own rig through a `ViewTransition`. `world.camera orbit|fly|frame|pivot|reset`
is bindable, and `world.view.camera` echoes the editor pose.

**Touches:** `Puck.World.Client` (`WorldCameraRigCompiler`, the seat's view
selection), `Puck.SdfVm/Views`, `Puck.World.Schema` (the `editor` section's camera
fields), the editor command module.

**Check:** `WorldEditorCameraLawTests` (an orbit input sequence puts the eye on
the sphere around the pivot at the expected yaw and pitch, and the clamp holds;
framing puts the selection's bounds inside the frustum; the server's state hash
is equal before and after a flight; red leg: removing the clamp lets pitch pass
the pole).

**Depends on:** E1; E2 for framing.

### E9 — Cost per object and GPU pass timing

**Problem:** a builder cannot tell which object makes the frame slow.

**Delivers:**

1. **`world.cost [<placement>]`** echoes one placement's counted cost: shapes,
   program words, instances (one for a scoped creation, one per shape for a
   scope-free one, `CreationStampEmitter.PerCopyInstanceCount`), scope clamps,
   its bound radius and blend halo, whether it has unmaskable influence (which
   costs every tile), stamp-pool slots, and whether it draws as a bake. Its
   share of `world.budget`'s totals is printed beside it. `world.cost top [<n>]`
   lists the heaviest placements. Every number is a deterministic count.
2. **GPU pass timing, as a readout.** Timestamp queries around each pass a
   render node records, at the sites that already enter and leave passes for
   the work ledger, on both backends; read back after the slot's fence and
   averaged over a window of frames. `world.gpu-timing on|off` (bindable, off
   by default) turns it on, `world.gpu-timing` prints each node's passes in
   milliseconds, and the inspector shows them. A query pool is a creating member
   of `GpuDeviceServices`: named, counted under `gpu.created`, wrapped by the
   creation faults, and released on device loss. The frame rate readout in the
   inspector follows the same rule. The readout never judges: see
   [Decisions](#decisions).
3. **Cost at the pointer.** The inspector reads the visibility record's step
   and query counts at the pointer's pixel, once a one-pixel readback exists
   (the same readback the rendering plan's GPU picking needs).

**Touches:** `Puck.World.Client` and `src/Puck.World` (`world.cost`),
`Puck.Abstractions` (the timing query service), `Puck.Vulkan` and
`Puck.DirectX` (query pools and heaps, timestamp periods),
`Puck.Shaders` (`ShaderPipelineRenderNode`), `Puck.SdfVm` (`SdfWorldTables`).

**Check:** `WorldCostLawTests` (fixture placements' counts equal their emitter's;
red leg: a scoped creation counted per shape fails); `GpuTimingLawTests` over
the fake device (on: two queries per pass, named and fault-wrapped, released on
device loss; off: zero queries; red leg: a node that forgets `LeavePass` fails
the pair count). The laws assert the mechanism and never a time.

**Depends on:** nothing; E5 for the panel; the rendering plan's one-pixel
readback for step 3.

### E10 — Live reload and before-and-after

**Problem:** a builder who saves a `.puck` file must still type `world.reload`,
loses their place when they do, and cannot compare the world before and after a
change.

**Delivers:**

1. **`world.watch on|off`** watches every file the loaded source's compile read
   (the compile cache's `CompileInputs`) and submits `world.reload` after a
   quiet period, as `pipeline.watch` does for a graph. The reload is the
   existing verb, recorded on the tape as always. A failed reload shows its
   diagnostic, with file and line, in the inspector and as a toast as well as
   on stderr.
2. **Reload keeps your place.** Build mode, the grid, the snap state, the
   editor camera and the selection (by id) survive a reload; an id the reload
   removed leaves the selection.
3. **Before and after.** `world.compare hold` keeps the seat's current frame;
   `world.compare wipe|split|diff|off` shows it against the live view in a pane
   the `place` package draws, with a bindable wipe position. The echo includes
   the changed-pixel count (pixels moving at least 2 LSB, as `CanaryFrameNoise`
   counts them).

**Touches:** `src/Puck.World` (the watch, the compare verbs, the capture path),
`Puck.World.Client` (`WorldViewGraphHost`, editor state retention).

**Check:** `WorldWatchLawTests` (touching a compile input submits exactly one
reload; red leg: touching an unrelated file submits none);
`WorldEditorReloadRetentionLawTests` (selection and camera survive a reload, and
a removed id drops out). Canary `editor-compare`: hold a frame, move a render
lever, and the split view's halves differ.

**Depends on:** E2 and E8 for what a reload keeps; the compare needs nothing.

### E11 — Save edits back to source

**Problem:** live edits to a `.puck` world cannot be saved back, because
`world.save` refuses a `.puck` target, so a builder's session ends in a JSON
file beside their source or is lost.

**Delivers:** `world.save` to a `.puck` source writes the rows the session
changed back into that source through the transpiler's printer, keeping `let`s,
templates, comments and formatting outside those rows byte for byte. A row a
template or a compile-time `for` generated is refused by name, naming the
construct that made it, and the refusal offers a JSON delta with the source as
its basis instead (the delta save `world.save` already makes). The approach is
an [open decision](#open-decisions).

**Touches:** `Puck.World.Transpiler` (printer and decompiler),
`Puck.Transpiler`, the mutation command module (`world.save`).

**Check:** a round-trip law over a sample source: edit a placement live, save,
compile again, and the lowered document equals the live one while every
untouched byte is unchanged; red leg: an edit to a loop-generated row is
refused by name.

**Depends on:** E3.

### E12 — The shape gallery as a world

**Problem:** the engine's shape-inspection tools (one subject under the
microscope, every primitive side by side, the scoped and flat field-op
contrast) live in a takeover scene no host constructs and that bypasses the
document.

**Delivers:** a shipped tool world, `sdf-gallery.puck`, beside the other tool
worlds in `src/Puck.World/Assets/worlds/tools/`, with a companion `.md` as
`shader-compare.puck` has. It authors every primitive, each blend family as a
pair, the domain operations, and the scoped and flat field-op contrast as two creations, browsed with build
mode, selection, the debug views, the editor camera and `world.explain`.
`world.isolate on|off` stamps only the selection, in presentation, which
replaces the takeover's single subject. `src/Puck.SdfVm/Debug` is deleted in the
same change, with the comments in `SdfCameraRig` that cite it. The carve
drawing `SdfCarveBakePlanner` borrows from `SdfDebugRenderer.EmitCarve` moves
into the planner if [E13](#e13--carving-and-the-brick-bake) keeps it, or goes
with it.

**Touches:** `Puck.SdfVm` (the deletion), `src/Puck.World/Assets/worlds/tools`,
`Puck.World.Client` (the isolate filter), the rendering handbook pages that
describe the debug scene.

**Check:** the eclipse check recorded in the commit: `puck references` finds no
caller of `Puck.SdfVm.Debug` outside the deleted directory. The gallery source
passes `ShippedSourceLintLawTests`; an isolate law holds that the frame emits
only the selection's instances (red leg: an empty selection emits the whole
world, not nothing); the gallery boots and captures offscreen.

**Depends on:** E1, E2, E4 and E8, since the replacement must be usable before
the old scene is deleted.

### E13 — Carving and the brick bake

**Problem:** a builder cannot dig into terrain or carve a hole, and the GPU
bake built to make dense carving cheap has nothing to bake.

**Delivers:** carves as document data: sphere subtractions, one row per brush
dab, in a section or a placement facet the package settles against the
existing vocabulary first. `world.carve [<radius>]` carves at the pointer's hit
(bindable; held, it paints with a spacing of half the radius), and
`world.carve erase` removes the dabs under the pointer; a stroke is one batch.
The static solid field includes the carves, so collision follows them, and
each carve extends past every point a body can reach (the phantom-surface
caution in the rendering skill). Presentation hands the carves to
`SdfCarveBakePlanner`, which settles, bakes, swaps and invalidates bins against
the world residency's brick pool as
[the handbook](../rendering/sdf/handbook/bricks-and-baking.md#settle-bake-swap-and-invalidate)
describes. `world.budget` prices carves and bricks, `world.cost` names a
placement's carves, and `world.carves` echoes the carve count and the baked and
analytic bins. Whether to build this or delete the bake is an
[open decision](#open-decisions).

**Touches:** `Puck.World.Schema`, `Puck.World.Server` (the static field),
`Puck.World.Client` (`WorldFramePresenter`, the planner's host),
`Puck.SdfVm` (`SdfCarveBakePlanner`, `SdfWorldTables.BrickBake.cs`).

**Check:** a collision law that a point inside a carve reads outside the solid
field; planner laws over the fake device (a bin with enough settled carves
requests one bake and emits one brick after `Ready`, a new carve in a baked
bin re-emits it analytic in the same rebuild; red leg: a bin below the
threshold never bakes). Canary `carve-bake`: a carved region's capture agrees
before and after its bin bakes. This supplies the missing check for the
rendering plan's brick-baking capability row.

**Depends on:** E1 and E2 for the pointer's surface hit, E3 for one step per
stroke.

## Decisions

**The editor is the World.** Every package is a verb, a lever, overlay drawing
or a view inside `Puck.World`. Nothing here adds an application.

**Editor state is presentation; edits are mutations.** Selection, grid, snap
state, the editor camera, drags, measurements, the clipboard and comparisons are
per-seat presentation state. The only thing an edit changes is the document,
through the ordinary mutation door, stamped with the seat's principal and gated
by its grants, so replay, undo, authority and federation work unchanged. A
bindable editing verb is checked at dispatch under the pressing seat's
principal, exactly as the same line from the console would be.

**One gesture, one journal entry.** A drag previews in presentation and commits
once; a gesture over several rows is one batch. Undo and redo therefore move in
the steps a builder remembers.

**Tunables are document fields, debug levers are not.** Grid pitch, angle
step, snap defaults and camera feel are `editor` section fields whose default is
today's behavior; a live verb overrides them per seat and `world.save` folds
the result back. Debug views, shading levers and timing are session levers that
are never saved.

**Build mode is built in.** Every world can be built in without authoring
anything, so `editor` is a built-in mode family beside `layout`, with its
bindings in a default layer.

**Overlay first, in-render highlight later.** Every static placement shares
visibility identity 0, so the GPU cannot outline one of them. Bounds,
highlight, gizmos and measurements are drawn with the overlay's line primitive.
An in-render tint follows once the visibility record names static instances,
which the rendering plan's GPU picking needs as well.

**Picking starts on the CPU.** The client's static field already answers
`IWorldQuery.Raycast`, so picking works without a GPU readback and is testable
by law. GPU picking is a later accelerator, not a prerequisite.

**The editor camera is a view override, not Free Cam.** Free Cam possesses a
simulated camera body a world authors; the editor camera moves nothing in the
simulation, so building in a shared world disturbs no one.

**GPU timing is a readout and never a judge.** A builder hunting a slow frame
needs to see where the time goes, and counts alone do not show them. Timing is
shown to the person at the screen and nowhere else: it never
enters `world.counters --json`, `puck counters`, `puck qualify`, a canary
assertion, a ceiling or any law's asserted value, and performance is still
judged by code and counts. [The rendering decisions](../decisions/rendering.md#the-pipeline-foundation)
record this exception.

**SdfDebug is deleted, not hosted.** Hosting it would add a second
presentation path beside the `sdf.world` views: a takeover that emits C#-built
scenes the document never sees, with an orbit camera whose rates are baked
constants. Everything it shows is expressible as a world document plus the
editor's own tools, which E12 ships before deleting it.

## Open decisions

1. **Carve bake: build the producer or delete it.** Recommended: build it (E13).
   Digging and carving is a builder feature with no other home, and the bake is
   the mechanism that makes hundreds of carves affordable. If the lead declines
   the brush, delete `SdfCarveBakePlanner`, the `sdf.bricks` bake pass, its
   kernel and `SdfWorldTables.BrickBake.cs`'s request path together, keeping
   the brick pool the height fields upload into.
2. **Whose edit does undo undo?** `world.undo` removes the last applied
   mutations whoever made them. In a world two people build together, that
   undoes someone else's work. Recommended: undo and redo per principal, refused
   by name when a later edit by someone else touched the same row.
3. **How a copy is named.** The rule that engine-minted names go through
   `GeneratedName` gives a duplicate an id like `crate$2`, which a `.puck`
   source cannot spell. Recommended: `world.duplicate` takes optional names;
   without them it mints through `GeneratedName`, `world.rename` gives a copy an
   authored name, and a `.puck` save (E11) refuses a generated id by name until
   it is renamed.
4. **How edits reach a `.puck` source.** Recommended: rewrite the touched rows
   in place (E11). The alternative is to keep the source untouched and save a
   JSON delta with the source as its basis, which works today but leaves a
   builder with two files that must travel together.
