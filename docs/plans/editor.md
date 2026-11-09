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

The forcing artifact is a place in the debug world laid out, lit and
debugged in build mode without typing a coordinate: every placement put down,
lined up and duplicated by pointer and grid, a dark corner diagnosed with
`world.explain` and fixed, the slowest object found with `world.cost`, and the
session saved back into the place's `.puck` source. Highest-quality frame targets are 60 FPS on the RTX
2060 and RTX 4070, and 45 FPS (every frame under 22.2 ms) on Steam Deck.

## What the packages build on

Build mode (E1), the inspector (E5), cost per object and GPU pass timing (E9),
and deterministic time travel (`world.history`) are in place; the
[World guide](../../src/Puck.World/README.md#build-mode) and the
[state reference](../reference/state/worlds.md#travel-through-recorded-time)
describe them. `world.undo` replays the journal minus its tail and is
unbindable; there is no redo anywhere except the forge draft's one-level swap
(`CartridgeDraft.Undo`). There are no gizmos, no selection, no copy or paste,
and no measurement. GPU hover identifies a placement but does not select it.
The inspector's `selection=` field reads the placement the seat last put down
or moved until E2 gives a seat a selection.

Several pieces exist with nothing using them:

- `world.debug-view` is bindable and selects the modes declared by `DebugViewModes`,
  including [evidence-preserving diagnostics](../reference/commands.md#scene-diagnostics).
  Raw-term modes set the mode on the main world residency only: its camera
  views show it, but a session screen's residency and a routed scene's never do.
- One shading lever reaches the pass block with no producer:
  `DisableScreenLights`.
- Sampled fields (`SdfSampledField`) carry data-driven surfaces beside the
  program without rebuilding it; height fields (`SdfHeightField`, published by
  `WorldFieldEmitter`) are their one producer.

The overlay draws rects, rings, wedges, panels, icons and text
(`OverlayFrameBuilder`), and has no line primitive. `MarkerWriter` projects
world points into each seat's viewport. `WorldPointerRayCapture` casts the OS
pointer through a seat's camera, and the cursor already outlines a hovered pane
(`WorldViewGraphHost.Hover`, `CursorWriter`).

The presenter's CPU `SdfFieldEvaluator` (`WorldClient.StaticField`) implements
`IWorldQuery.Raycast`, but it is not everything a builder sees: it holds only
the placements a body can touch, plus screens, derived faces and adjacency
geometry, and it refuses what has no deterministic interpreter (non-uniform
scale, warp ops, render-only shapes such as Path and multi-strand Sweep), and
one refused instruction leaves the whole field null. The shared GPU picker is
[P13](rendering.md#p13--hit-to-source-mapping-and-input-destinations)'s
`SdfWorldPicker`, one per rendered view (`SdfWorldPasses.PickerOf`), whose
answer names the source and the winning shape's transform slot against the
frame the record was rendered from (`SdfPickResult`).

Free Cam exists as a gameplay mode that possesses an authored
`camera-seat-<n>` body (`WorldSeatModeState.CameraTarget`).

Prior art lives in git history under `experimental/Puck.Demo/`
(`git log -- experimental/Puck.Demo` finds the last revision holding it): the
world sculptor (`World/WorldSculptController.cs`), the shared undo and redo
ring (`EditHistory`), the snap-to-overlay policy
(`Editing/GridOverlayFactory.cs`), the creator workbench (`Creator/`) and the
SDF debug verbs (`SdfDebug/SdfDebugCommandModule.cs`). Read it as evidence of a
solved problem, never as code to revive.

## How a package closes

Each package is verified by running what it changed in the World. A package
that adds a verb adds its read-back and its entry in the build-mode binding
group in the same change.

## Packages

### E2 — Selection, picking and highlight

**Problem:** a builder cannot point at a thing and say "this one"; every edit
names a placement id they had to look up.

**Delivers:**

1. **A line primitive.** A `WriteSegment` on `OverlayFrameBuilder` (endpoints,
   thickness, role, alpha), drawn by the overlay kernel as an antialiased
   capsule distance, charged to a new `Editor` overlay channel leased in
   `OverlayChannelLeases`. A world-space polyline projects through the seat's
   viewport camera, the way `MarkerWriter` projects a point, and clips to the
   viewport.
2. **An identity for every drawn instance.** P13 supplies the shared identity
   and readback mechanism described above. `WorldPickMapBuilder` maps the
   emitted SDF ordinal range and static and pooled mesh draws to their
   placement or stamped body. Multiple shape ordinals may name one placement.
   This step consumes that mechanism; it does not create another picker.
3. **Pointer picking, two paths.** The pointer's display point becomes a ray
   through `SourceRay.Through` over `WorldSeatViewports`, as
   `WorldPointerRayCapture` does, but the ray stays in presentation and is never
   sustained into the command plane.
   - *The GPU path* picks everything the builder sees: a one-pixel readback of
     the visibility record under the pointer, while build mode
     demands hover, gives the identity, the ray parameter `t`, the material
     and the winning shape's transform slot with its transform, and the host
     table turns the identity into a placement. It covers non-solid
     placements, shapes and ops the CPU evaluator refuses, stamped bodies and
     baked meshes. The readback is the
     same one the [rendering plan's GPU picking](rendering.md#p13--hit-to-source-mapping-and-input-destinations)
     supplies through `SdfWorldPicker`, and it is counted in the view's
     work ledger. At most one copy is in flight: a moving pointer's latest
     coordinate waits for it and records when it completes, so the pointer
     gets an answer every round trip, each carrying the pixel it was sampled
     at. Continuing demand refreshes after an answer even when only the
     camera or scene moves. An answer arrives a frame or two late, which a
     hover label tolerates because it names the pixel it answers for.
   - *The CPU path* answers on the frame it is asked, from the client's static
     field: exact hit points and normals for surface snapping and measuring
     (E1, E3) wherever the field exists. It never decides what was picked when
     the GPU path has an answer.
   In build mode the cursor's hover label names the placement a pick
   answered and the pixel it answered at.
4. **A per-seat selection.** `world.select pointer|<id>…|add <id>|toggle <id>|clear|prototype <name>|box`
   (`box` selects every placement whose bounds fall inside a dragged
   rectangle), with bindable forms for pointer, add, clear and cycle.
   `world.selection` echoes each selected id, prototype, bounds and pivot. A
   removed row leaves the selection.
5. **Highlight.** Each selected placement draws its oriented bounds, its pivot
   and its id through the line primitive; the hovered placement draws thinner.
   With per-instance identities in the record, the views pass also tints and
   outlines the selected placement's pixels: the host writes the selected
   ordinals into a small presentation table, and the views stage compares each
   pixel's source against it, with an edge where a neighbouring pixel's
   identity differs. A selection larger than the table falls back to bounds
   alone and says so.
6. **Verbs act on the selection.** E1's `world.nudge` and `world.turn`, and
   every later editing verb, act on the selection when no id is named.

**Touches:** `Puck.Overlays` (`OverlayFrameBuilder`, the overlay kernel and its
generated interface, `OverlayChannels`, `OverlayChannelLeases`),
`Puck.SignedDistance` (`SdfFieldEvaluator`), `Puck.SdfVm` (the visibility
record, the surface and views stages,
the instance table, the readback), `Puck.World.Client` (the stamper's
registration, the ordinal-to-placement table, the selection, the cursor feed),
`src/Puck.World` (the editor command module).

**Check:** `OverlaySegmentLawTests` (records, viewport clipping, a steady frame
allocating nothing) and `OverlayLeaseTableFitsBackstopsLawTests` moving with the
new lease. `WorldEditorPickLawTests` over a fixture holding both kinds of
geometry: a solid, query-compatible placement, and a placement the CPU cannot
answer for (no `solid` facet, plus one using a warp op so the static field is
null). The CPU path resolves the first by id and the nearer of two overlapping
placements wins; the host table maps each ordinal a scope-free creation emits
back to its one placement; red leg: a ray into the sky resolves none. A device
pick law on both backends (in `tests/Puck.World.Tests`, beside the other device
laws) reads back the pixel under each fixture placement and resolves both
kinds, two static placements to two different ids; red legs: a background
pixel reads 0, and with the ordinal forced to the old frame-slot source both
static placements read `0x40000000` and the law fails.
`WorldEditorSelectionLawTests` (box selection, removal drops the id, the
read-back, and the selection surviving a reload through
`WorldEditorReloadRetentionLawTests`). Canary `editor-selection`: selecting the non-solid placement by
pointer puts accent pixels on it and around it in an offscreen capture, and
clearing the selection removes them. With build mode off, `world.counters gpu`
reads the same per-pass counts before and after the package, so the readback
costs nothing outside the editor.

**Depends on:** E1 for build mode's binding group.

**Status:** open; items 2 and 3 are in place, items 1, 4, 5 and 6 are not.
`WorldPickMapBuilder` maps the emitted SDF ordinals and mesh draws to their
placement or stamped body (`WorldComposedPickMapLawTests`,
the `sdf-picking` canary). The GPU path runs while a seat builds or has its
inspector on (`WorldCursorFeed`), and the cursor label names the placement and
the pixel it answered at (`WorldPickLabelLawTests`). The CPU path is the static
field's `IWorldQuery.Raycast`, which E1's placement verbs use. Still to build:
the overlay line primitive (`OverlayFrameBuilder` has none), the per-seat
selection with `world.select` and `world.selection`, the highlight, and the
editing verbs acting on the selection. The laws and canary named under Check for
those items (`OverlaySegmentLawTests`, `WorldEditorPickLawTests`,
`WorldEditorSelectionLawTests`, `editor-selection`) do not exist yet.
The selection also joins the reload retention that
`WorldEditorReloadRetentionLawTests` holds
(the live reload's, [World guide](../../src/Puck.World/README.md)), and fills the inspector's
`selection=` field.

### E3 — Undo, redo, duplicate, delete and measure

**Problem:** a builder who makes a mistake can undo but not redo, cannot copy
what they made, and cannot tell how far apart two things are.

**Delivers:**

1. **Undo and redo per principal.** `world.undo [n]` undoes the acting
   principal's own last n edits, not whoever edited last, so two people
   building one world never undo each other's work. `world.redo [n]` re-applies
   what that principal's undo removed. Both run through the same apply path.
   An undo or redo is refused by name, naming the row and the other principal,
   when a later edit by someone else touched a row it would change; nothing is
   partly applied. A principal's redo tail is cleared by that principal's next
   edit, bounded by `host.journalDepth`, and cleared by a load or reload.
   `world.undo` and `world.redo` become bindable, checked at dispatch under the
   pressing seat's principal as `world.reload` is, and `world.status` echoes the
   acting principal's undo and redo depths.
2. **One gesture, one step.** Every editor gesture is one journal entry: a
   gesture over several rows submits one `WorldMutation.Batch`, and a drag
   ([E7](#e7--gizmos-and-pointer-dragging)) commits once on release.
3. **Duplicate, delete, copy and paste.** `world.duplicate [<offset>] [<name>…]`
   copies the selection as new rows offset by one grid step and selects the
   copies; `world.delete` removes the selection; `world.copy` and `world.paste
   [<name>…]` keep a session clipboard of rows and paste at the pointer,
   snapped. Each is one batch. A copy takes the names given; without them its
   id is minted through `GeneratedName` (such as `crate$2`), which no author can
   spell. `world.rename <id> <name>` gives any placement an authored id,
   rewriting the rows that name it in the same batch.
4. **Measure.** `world.measure` between the pointer's next two hits, or between
   two selected pivots, echoes the distance, its three components and the
   angle, and draws a labelled segment until `world.measure clear`.

**Touches:** `Puck.World.Server` (the journal and its undo path,
`WorldJournalEntry`), `Puck.World.Protocol` (`IServerLink`), the mutation
command module, the editor command module.

**Check:** `WorldUndoRedoLawTests` (undo n then redo n restores the pre-undo
document hash; the principal's own edit after an undo leaves nothing to redo,
refused by name; the journal horizon refuses by name). Two principals, A and B,
edit different rows, and A's undo reverts only A's edit (red leg: the
whoever-edited-last undo reverts B's); B then edits a row A's next undo would
change, and A's undo is refused by name, naming the row and B, with the
document unchanged. `WorldEditorDuplicateLawTests` (one batch, one undo removes
every copy, ids stay unique, an unnamed copy's id is a `GeneratedName`, and
`world.rename` rewrites every row that names it); a measure law over two
fixture placements (red leg: one hit measures nothing).

**Depends on:** E2.

### E4 — Debug views everywhere

**Problem:** the debug views exist, but a builder must type them, and they show
only in the main world residency.

**Delivers:**

1. **Bindable views.** `world.debug-view` becomes bindable: a constant value
   selects a mode by index, and two reserved values step to the next and
   previous mode, following `view.override`'s convention.
2. **Every residency.** The mode reaches the main world residency (whose
   camera views already show it) and every session and routed residency the
   screen binder creates (`WorldScreenBinder.Device.Views.cs`, `WorldScreenBinder.Device.Routed.cs`),
   including one created after the switch.
3. **The screen-lights lever.** `world.screen-lights`, in
   `WorldRenderLeverCommandModule` beside `world.shadows` and `world.ao`,
   bindable and echoed.
4. **Views for lighting questions.** New modes `lighting` (shading over white
   albedo), `albedo` (unlit color) and `shadow` (the key light's visibility, the
   record's K word), beside the existing `ao` (the filtered occlusion views
   shades with). The mode names in
   `DebugViewModes` and the kernel's count and switch
   (`frame/sdf-debug-modes.hlsli`, `debug/sdf-debug-views.hlsli`) move together.

The frame walks no field, so a field slice or a field-health view needs a
kernel of its own over the program, which this package does not add.

Debug views and shading levers are session levers: `world.save` never folds
them into the document.

**Touches:** `src/Puck.World` (`WorldRenderLeverCommandModule`,
`WorldScreenBinder.Device.Views.cs`), `Puck.SdfVm` (`DebugViewModes`,
`SdfWorldResidency`, the debug and lever kernels), `Puck.World.Client`
(`WorldFramePresenter`).

**Check:** a sync law holding `DebugViewModes.Names` to the kernel's mode
count; `WorldDebugViewResidencyLawTests` (the mode reaches every residency the
binder holds and one it creates later; red leg: the main residency alone, as
today); a presenter law that the screen-lights lever reaches the frame.
Canary `debug-views`: a screen showing a camera view changes to the `normals`
colors when the mode is on. The rendering plan's capability rows for debug views and shading levers already
hold frame-to-pass-block and pass-scheduling checks
(`SdfFrameBlockLawTests.EachShadingLeverWritesItsOwnMemberAlone`,
`SdfWorldResidencyReportLawTests.TheDebugViewModeReachesEveryPassBlockWhenSetBeforeTheTablesOrAfter`);
this package adds the producers' side, a World setting reaching the frame, and
the canary.

**Depends on:** nothing.

### E5 — The inspector

**Problem:** to learn anything about what they are looking at, a builder types a
read-back verb and reads its output in a terminal.

**Delivers:** an inspector panel drawn on the `Editor` overlay channel, toggled
by the bindable `world.inspect on|off|toggle`. It shows the placement under the
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

**Storage rule:** composition sums every overlay writer's declared text-word
reservation and rounds the sum up to a power of two. The current four-seat
composition reserves 26,721 words and allocates 32,768. A writer exceeding its
own reservation refuses by channel name; it cannot consume another writer's
share. The inspector reserves 32 lines of 96 characters per seat, and its
content fits them line by line: a long line wraps onto indented continuation
lines and elides its end past its own budget, a reload diagnostic leads with
its file and location relative to the world's document directory, and pass
times past the last line are counted in one closing line. Only a formatter
whose fixed lines exceed the reservation by construction is replaced by an
explicit `editor refused` diagnostic, also charged to the existing overlay
refusal counters. There is no on-demand reallocation.
The off state emits no inspector records, but its shared backing storage remains
allocated. Graph inspection reports the installed regions' device and
host-visible bytes, CPU shadow payloads, and package/row/upload scratch payloads
separately. These are logical payload bytes, excluding managed object headers,
backend alignment, retired graphs and unrelated application state.

The material name is its existing authored address `<prototype>.palette[slot]`,
shown beside the exact program material index. The asynchronous pixel carries
that frame's immutable identity map and camera; a later camera or palette never
changes the answer's world point or material name. Pointer steps and query counts
come from the same visibility record. A passthrough pane supplies its own rendered
residency and quality to the inspector, cost report and pass timings. Pass timing
and FPS appear only while `world.gpu-timing` is enabled.

**Depends on:** the shared GPU picking mechanism, which is in place.

**Status:** built ([World guide](../../src/Puck.World/README.md)). The panel's
text fits a fixed reservation on the overlay lease table, long diagnostics with
their file and location first, and a steady frame allocates nothing; that is
the storage rule overlay writers follow. The summary of the selection reads the
placement the seat last put down or moved, so it is `selection=none` on a fresh
seat; [E2](#e2--selection-picking-and-highlight) owns feeding it a real
selection.

### E6 — Why is this dark or invisible

The renderer-backed explain pick already exists: `WorldInspectionCommandModule.Explain`
requests the pointer's rendered pixel through `WorldExplainPick`, retains that
exact result and view, and settles completion or cancellation. E6 extends this
path with placement visibility and light, shadow, probe, and material
contributions from the renderer. Every answer names the authored field that
changes it. A contribution the captured renderer data cannot establish says
`unavailable` and why; there is no separate CPU lighting implementation.

The same diagnostics path absorbs Debug tools' findings, pins, `goto`, and
`annotate`. `world.problems` gathers spatial refusals and source warnings,
selects and frames findings through E2/E8, and retains pins and annotations
through one overlay writer. Consolidate `MarkerWriter` and `AnnotationWriter`
rather than adding a third path. `world.goto <placement>` selects and frames
the target with the editor camera.

**Touches:** `Puck.World`'s inspection commands and picker, `Puck.World.Client`
and `Puck.SdfVm`'s renderer diagnostics, and `Puck.Overlays`.

**Check:** use planted dark, occluded, missing, and refused placements in the
debug world; compare each answer with its captured pixel on both backends.
Exercise unavailable data and cancellation, and show findings, pins, goto, and
annotations through the same overlay path.

**Depends on:** E2; E8 for framing; the overlay-writer consolidation.

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
drag; a right click cancels and sends nothing, as does opening the pause menu
(Escape is the menu's, [play decisions](../decisions/play.md#the-game)), which
releases every held input. A gamepad drags along
the camera-relative plane with the sticks, with the same commit rule, as the
sculptor's pad model did. Before adding the ghost, check whether
`world.reflow.preview` already draws proposed positions (AGENTS.md rule 7).
A pointer ray meeting a horizontal plane in fixed point already exists for
tabletops (`WorldTabletopFrame.TryPointerCell`, read by rules as
`board(pointer, …)`), with the debug world's chess table lifting a piece on
press and putting it down on a click or a release elsewhere; the plane solve
here is its sibling.

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
pitch clamp, the distance range) and bindings are authored in
`worlds/standard/editor.puck`, imported by debug and overridden by id. The
engine executes generic camera operations; schema fallback constants do not
complete the standard design. Leaving build mode blends back to the
seat's own rig through a `ViewTransition`. `world.camera orbit|fly|frame|pivot|reset`
is bindable, and `world.view.camera` echoes the editor pose.

**Touches:** `Puck.World.Client` (`WorldCameraRigCompiler`, the seat's view
selection), `Puck.SdfVm/Views`, `Puck.World.Schema` (the `editor` section's camera
fields), the editor command module.

**Check:** `WorldEditorCameraLawTests` (an orbit input sequence puts the eye on
the sphere around the pivot at the expected yaw and pitch, and the clamp holds;
framing puts the selection's bounds inside the frustum; the server's state hash
is equal before and after a flight, and the camera's pose survives a reload
through `WorldEditorReloadRetentionLawTests`; red leg: removing the clamp lets
pitch pass the pole).

**Depends on:** E1; E2 for framing.

**Status:** the feel is authored (`editor.camera` in `worlds/standard/editor.puck`,
every field required and validated by name); the camera that reads it is open.

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
   and query counts at the pointer's pixel, from the one-pixel readback that
   GPU picking supplies.

**Touches:** `Puck.World.Client` and `src/Puck.World` (`world.cost`),
`Puck.Abstractions` (the timing query service), `Puck.Vulkan` and
`Puck.DirectX` (query pools and heaps, timestamp periods),
`Puck.Shaders` (`ShaderPipelineRenderNode`), `Puck.SdfVm` (`SdfWorldTables`).

**Check:** `WorldCostLawTests` (fixture placements' counts equal their emitter's;
red leg: a scoped creation counted per shape fails); `ShaderPipelineRenderNodeLawTests` over
the fake device (on: two queries per pass, named and fault-wrapped, released on
device loss; off: zero queries; red leg: a node that forgets `LeavePass` fails
the pair count; a faulted pool or readback refuses timing by name while frames
keep rendering, and a later frame tries nothing). The laws assert the mechanism
and never a time.

**Accounting rule:** placement program words are the words owned by its live
instance rows, instruction headers/data/bounds, segments, rigid leaves, shape
side tables and part bindings. Shared headers, palettes, grid and part assets
are printed separately, alongside other instances such as bodies. Placement
words plus other-instance words plus shared words equal the live `world.budget`
program total. No prototype is re-emitted to estimate this number. A hidden
field instance and its live mesh draw remain visible in the report. Bake representation
is captured at emission with that frame's map; another world's cache cannot change it.

Timestamp pools are allocated only on demand, on the frame thread after the
slot's fence, like the other resources a node allocates there. A device with
no timestamps, or a pool or readback the device will not create, refuses
timing for that node by name (`ShaderPipelineRenderNode.TimingRefusal`, echoed
by `world.gpu-timing` and the inspector), releases what the failed slot
created, and leaves the graph rendering; nothing is tried again until timing
is enabled anew, the operator's GPU faults move, or the device is lost. Each
recorded pass gets one pair;
readback waits for its submission fence, rejects a replaced graph or an earlier
enable epoch, and averages at most 32 completed pairs. Disabling hides readings
immediately and releases pools after their fences, unless dynamic resolution
still asks for the timestamps. Device loss releases all
query/readback ownership. GPU timestamp readback and retained CPU sample payloads
are reported separately. Times and FPS never become correctness assertions;
dynamic resolution is the one quality input that reads the timing.

**Depends on:** nothing.

**Status:** built ([World guide](../../src/Puck.World/README.md)): `world.cost`
for step 1, the optional `world.gpu-timing` readout for step 2, and the
`pixel=` line of `world.cost` with the inspector's `steps=` and `queries=` for
step 3. [E13](#e13--carving-and-its-sampled-bake) extends `world.cost` with a
placement's carves.

### E11 — Save edits back to source

**Status:** source-preserving save is in place for authored rows. The printer
rewrites changed source spans, retains untouched text and a UTF-8 byte-order
mark, and proves the recomposed document before writing atomically. Template,
loop and module-generated rows refuse by name. The generated-name refusal is
also in place; E3 supplies the live duplicate and rename workflow.

**Delivers:** `world.save` to a `.puck` source writes the rows the session
changed back into that source through the transpiler's printer, keeping `let`s,
templates, comments and formatting outside those rows byte for byte. A row a
template or a compile-time `for` generated is refused by name, naming the
construct that made it, and the refusal offers a JSON delta with the source as
its basis instead (the delta save `world.save` already makes). A row whose id
was minted through `GeneratedName` is refused by name until `world.rename`
gives it an authored id, since a `.puck` source cannot spell a generated name.

**Touches:** `Puck.World.Transpiler` (printer and decompiler),
`Puck.Transpiler`, the mutation command module (`world.save`).

**Check:** `SourceSaveLawTests` covers exact unrelated text, basis composition,
row removal and insertion, byte-order marks, and named refusals for generated
rows. `WorldSourceSaveIntegrationLawTests` nudges a live placement, saves and
reloads it, and compares the resulting document while preserving the source's
unrelated heading and tail. The laws fail when the source rewrite is bypassed.

**Depends on:** E3.

### E12 — The shape gallery as a world

The gallery already lives in `worlds/debug/shapes.puck`: primitives, lifts,
composition, blends, domain operations, motion, form, and surface exhibits,
with plaques, spawn points, and authored gallery commands. Extend that gallery
rather than creating a separate tool world. `WorldViewCommandModule.Isolate`
already implements `view.isolate [toggle] <prototype> <shape-name>... | off`
for named shapes in a prototype.

The remaining gaps are placement-selection isolation through E2, the slice's
clipped far field on Direct3D 12 and its Vulkan check, a sampled slice drawn
in place, and the primitives whose non-uniform scale still yields a distance
bound or an uncertified gain. The detailed cases stay in [open items](#open-items).
The dense carve exhibit follows E13. No pixel kernel walks the field to draw
the slice.

**Touches:** `worlds/debug/shapes.puck`, the existing view-isolation path, and
the editor's selection and camera mechanisms.

**Check:** tour the existing gallery and capture each added case on both
backends; placement-selection isolation shows only the selected placements
and handles an empty selection explicitly.

**Depends on:** E2 for placement isolation; E4 and E8 for inspection; E13 for
the carve exhibit.

### E13 — Carving and its sampled bake

**Problem:** a builder cannot dig into terrain or carve a hole, and dense
carving needs a cache once its carves settle.

**Delivers:** carves as document data: sphere subtractions, one row per brush
dab, in a section or a placement facet the package settles against the
existing vocabulary first. `world.carve [<radius>]` carves at the pointer's hit
(bindable; held, it paints with a spacing of half the radius), and
`world.carve erase` removes the dabs under the pointer; a stroke is one authored edit submitted as one atomic batch, so one undo
reverts the entire stroke.
The static solid field includes the carves, so collision follows them, and
each carve extends past every point a body can reach (the phantom-surface
caution in the rendering skill). Presentation settles each carve bin, bakes its carve union on the CPU into a
sampled field ([sampled fields](../rendering/sdf/handbook/sampled-fields.md),
composed by subtraction in place of the bin's carve instances), and re-emits a
bin analytic when a carve lands in it, through the shared field/bake
invalidation path and the engine job system, with residency supplying demand
and priority. `world.budget` prices carves and baked bins, `world.cost` names a
placement's carves, and `world.carves` echoes the carve count and the baked and
analytic bins.

**Touches:** `Puck.World.Schema`, `Puck.World.Server` (the static field),
`Puck.World.Client` (`WorldFramePresenter`, the shared invalidation consumer),
`Puck.SignedDistance` (`SdfSampledField`).

**Check:** a collision law that a point inside a carve reads outside the solid
field; shared invalidation checks over the fake device (a bin with enough settled carves
bakes one sampled field and emits it in place of its carves, a new carve in a
baked bin re-emits it analytic in the same rebuild; red leg: a bin below the
threshold never bakes). Canary `carve-bake`: a carved region's capture agrees
before and after its bin bakes.

**Depends on:** E1 and E2 for the pointer's surface hit, E3 for one step per
stroke, and the shared field/bake invalidation and
[engine job system](open-items.md#engine-job-system) for scheduled bake work.

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

**The standard authors editor design.** Bars, pages, keys, grid and snap values,
and camera feel live in `worlds/standard/editor.puck`, imported by debug. A
live verb overrides them per seat and `world.save` folds the result back into
source. Debug views, shading levers, and timing are session levers. The engine
provides mode switching, commands, layout, selection, snapping, and camera ops;
a schema fallback with the same design constants leaves the authoring work open.

**Every drawn instance has its own identity.** P13 supplies the SDF source as
the winning instance's ordinal plus one and the mesh source as its draw
ordinal, with an immutable host table resolving each to its placement or
stamped body. E2 consumes that shared picker for selection and in-render
highlight. Bounds, pivots, gizmos and measurements will use the overlay's
line primitive.

**Picking has two paths, and the GPU decides.** The GPU readback sees what the
builder sees, including non-solid placements and geometry the CPU evaluator
refuses, so it decides what was picked. The CPU static field answers on the
same frame with exact points and normals, so snapping and measuring use it
wherever it exists.

**The grid is drawn where the builder works.** A grid confined to one floor
height fails on every platform, ramp and wall, so E1 carries the kernel work for
a working plane and a surface-projected grid rather than leaving it for later.

**Undo is per principal.** Two people building one world each undo their own
edits, and an undo that would overwrite someone else's later edit to the same
row is refused by name rather than applied.

**Copies may be named; generated ids never reach source.** `world.duplicate`
and `world.paste` take optional names, an unnamed copy gets a `GeneratedName`
id, `world.rename` gives it an authored one, and a `.puck` save refuses a
generated id by name.

**A `.puck` save rewrites the touched rows in place.** A builder keeps one
source file. The JSON delta over the source as its basis remains the fallback a
refusal offers, never the default.

**Carving is built.** Digging and carving are builder features with no other
home, and the sampled bake is what makes hundreds of carves affordable, so E13
builds a carve brush whose dabs are document rows and feeds the bake from them.

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
editor's own tools. The directory is deleted only once E12's parity table
holds, so no capability is lost on the way.

## Open items

- Decide (the lead's call): Primitives that lower to a distance bound under a non-uniform scale: iterate an exact distance, change the zero set, or refuse such scales at authoring. The [w1-limits recommendation](modeling-authoring.md#17-non-uniform-scale-limit-10-recommendation-for-the-lead) preserves the authored surface and supplies bounded exact-distance lowerings; it is a recommendation, not a settled decision.

What the plan still owes. A line is deleted when its work lands, never ticked;
the [open items](open-items.md#next) rank the work packets drawn from it. E1,
E5, E9, E10, and E14's recording and scrubbing are in; E8's feel values and
bindings are authored in `worlds/standard/editor.puck`.

- [E2](#e2--selection-picking-and-highlight): the identity map and GPU hover
  picking are in place through P13; the line primitive, `world.select`,
  `world.selection`, the highlight and verbs on a selection are open.
- [E3](#e3--undo-redo-duplicate-delete-and-measure) undo, redo, duplicate,
  delete and measure.
- [E4](#e4--debug-views-everywhere) debug views everywhere (startable today),
  which also wires the `DisableScreenLights` lever: it reaches the pass block
  and the kernels, and no World setting or verb sets it.
- [E6](#e6--why-is-this-dark-or-invisible): extend the existing renderer-backed
  explain pick with light, shadow, probe, and material contributions and explicit
  unavailable diagnostics; absorb findings, pins, goto, and annotate through one
  overlay writer.
- [E7](#e7--gizmos-and-pointer-dragging): pointer dragging on a tabletop is in
  place (a seated seat's pointer ray meets the board's playing surface in fixed
  point, `board(pointer, …)`, `WorldTabletopFrame.TryPointerCell`, and the debug
  world's chess rules lift a piece on press and put it down on a click or a
  release elsewhere); the gizmos and the placement drag remain.
- [E8](#e8--editor-camera): the editor camera itself (the view override, its
  camera-program ops and `world.camera`); its feel values are authored and
  validated in the standard's `editor.camera`, and nothing reads them yet.
- [E11](#e11--save-edits-back-to-source): source-preserving save for authored
  rows has landed, with named generated-row refusals; E3's live duplicate and
  rename workflow remains.
- [E12](#e12--the-shape-gallery-as-a-world), gaps in the existing debug gallery:
  - Placement-selection isolation remains open; prototype shape isolation exists.
  - `world.slice` on the GPU: on Direct3D 12 the sampler's answered field stops
    rising about 0.13 m from the surfaces of `shape-capsule-xz` (|grad d| near
    zero beyond), which the slice reports as `clipped=` and paints magenta.
    Find what holds the bake sampler's far field there (an instance bound, a
    glyph or sampled-region band, or the unowned segments, which `world=` tells
    apart), and run it on Vulkan.
  - The slice is a file, not a view: drawing the sampled plane in the frame (a
    textured quad the mesh pass draws, or the views pass reading the slice's
    samples where a ray crosses the plane) would show it live in place. No pixel
    kernel may walk the field for it.
  - Primitives that still lower to a bound or worse, which `creation.shape`
    reports and the gallery shows: a capsule, cone or torus scaled apart in X
    and Z, and a round cone scaled apart at all ride the generic `Scale` (gain min/max,
    refused by the fixed-point evaluator and so by solid contact; the gallery's
    `shape-*-xz` and `shape-round-cone-xyz` read 0.5 to
    0.33); regular polygons, including `shape-polygon-12-xy`, now lower their
    scaled vertices to the exact ConvexPolygon field; an ellipsoid or sphere scaled apart is the exponent-2 superellipsoid
    gauge (gain about the smallest radius over the largest, contact admits it);
    a `Path` profile and a sweep certify no gain. Whether to iterate an exact
    distance, change the zero set or refuse such scales at authoring is the
    owner's call.
- [E13](#e13--carving-and-its-sampled-bake): a carve brush whose dabs are document
  rows, one authored edit per stroke, using shared field/bake invalidation and
  the engine job system for bake work.
- Time travel's remainder (E14's scrubbing is built):
  - A live coupled machine link still refuses a checkpoint by name; ordinary
    console machine operations are refused while a recording is armed (no tape
    entry), while receipt-pinned addon-derived operations are re-derived by
    their guest. `content.insert` reads a content folder at apply time, so a
    re-driven tape diverges if the folder changed.
