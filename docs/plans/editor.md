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

E1 is in place: every seat can enter build mode (`player.build`, the built-in
`editor` context family), draw its own grid on the ground it works on, and put
down, nudge and turn placements by whole grid and angle steps from bound keys
(`world.grid`, `world.snap`, `world.place`, `world.nudge`, `world.turn`). The
World guide's *Build mode* section describes them. The other verbs that change
a world are `world.row.set`, `world.row.add`, `world.row.remove` and
`world.row.step` (the last is bindable and steps one scalar field; it refuses a
vector field such as a position), `world.reflow.preview`/`status`/`commit`/`cancel`,
`world.undo`, `world.reload`, `world.save`, the `pipeline.*` verbs for shader
pipelines, and the `forge.*` verbs for cartridges. `world.undo` replays the
journal minus its tail and is unbindable; there is no redo anywhere except the
forge draft's one-level swap (`CartridgeDraft.Undo`). There are no gizmos, no
selection, no copy or paste, and no measurement. GPU hover identifies a
placement but does not select it; `world.place` aims at a surface. `world.nudge` and
`world.turn` act on a named placement or the one the seat last placed or moved.
`world.save` writes authored edits back to a `.puck` target through the source
printer, preserving unrelated text and proving the recomposed document before
the atomic write. Generated rows refuse by name and offer a JSON delta.

Several pieces exist with nothing using them:

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

GPU work remains counted for correctness and quality decisions. The optional
`world.gpu-timing` readout records named pass timestamp pairs through
`IGpuTimestampFactory` on both backends, after submission fences. Timing is off
by default and remains observational; it never changes a rendering decision.

The building blocks the packages reuse are in place. The overlay draws rects,
rings, wedges, panels, icons and text (`OverlayFrameBuilder`), and has no line
primitive. `MarkerWriter` projects world points into each seat's viewport.
`WorldPointerRayCapture` casts the OS pointer through a seat's camera
(`SourceRay.Through` over `WorldSeatViewports`), and the cursor already outlines
a hovered pane (`WorldViewGraphHost.Hover`, `CursorWriter`).

The presenter rebuilds a CPU `SdfFieldEvaluator`
(`WorldFramePresenter.RebuildStaticField`, published as `WorldClient.StaticField`),
and the evaluator implements `IWorldQuery.Raycast`, which returns the hit point,
normal and material. That field is not everything a builder sees. It holds only
the placements a body can touch (those with a `solid` facet), plus screens,
derived faces and adjacency geometry, and the evaluator refuses what has no
deterministic interpreter: non-uniform scale, warp ops, render-only shapes such
as Path and multi-strand Sweep. One refused instruction leaves the whole field
null, so in such a world nothing can be picked on the CPU.

The shared GPU picker is supplied by [P13](rendering.md#p13--hit-to-source-mapping-and-input-destinations):
`SdfWorldPasses.PickerOf` exposes one `SdfWorldPicker` per rendered view. Its
asynchronous readback copies the requested pixel's 16-byte visibility V row
only while demanded. Kind occupies bits 31..30 (0 background, 1 SDF, 2 mesh),
and source occupies bits 29..0: the SDF instance's program ordinal plus one,
or the mesh draw ordinal. SDF source zero means geometry outside any instance.
The request captures an immutable host lookup, so a later program rebuild or
reused draw ordinal cannot name a replacement placement. The exact winning
shape's transform slot is retained in L.x; an articulated instance's bound
slot can differ. Anonymous lanes are loaded from that transform's existing
row, preserving the 64-byte visibility record.

Free Cam exists as a gameplay mode
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
4. **A grid on the ground being worked on.** The kernel work that makes the
   grid appear where the builder is building, replacing the fixed
   0.02-of-`gridFloorY` test in `sdf-light-stage.hlsli` and `applyObjectGrid`:
   - *The working plane.* The world grid draws on a horizontal plane at the
     working height, which follows the surface under the pointer when the grid
     is set to follow, the selection's base, or a height set with
     `world.grid plane <y>`. The band that counts as "on the plane" scales with
     the pitch and the pixel footprint at that distance, so the lines neither
     vanish up close nor shimmer far away.
   - *The surface-projected grid.* In `surface` mode the lattice is drawn on
     every surface a view hits, projected along the surface normal: the XZ
     lines on floors and platforms at any height, the XY or ZY lines on walls,
     and a blend weighted by the normal on slopes, fading at grazing angles.
     The lines are the world lattice's, so a line on a ramp meets the same
     line on the floor at the ramp's foot.
   - *The object grid.* The reference's lattice draws on the reference's own
     faces and on the surface under it, inside its patch radius, rather than
     only on the floor plane.
   - New pass-block values (the plane height, the mode, the band scale) are
     rows in `SdfWorldPackage.Values`, written by `SdfFrameBlock` and
     regenerated into the interface with `puck shaders generate`, with
     `SdfFrameBlockLawTests` moving with them.
5. **`world.grid` and `world.snap`.** `world.grid on|off|pitch <x> [<z>]|plane <y>|follow|surface`
   and `world.snap on|off|angle <degrees>|surface on|off|reference <placement>|clear`,
   both bindable (a toggle and a pitch step as constant values), both echoing
   the seat's whole state when given no argument.
6. **Snapping in placement editing.** `world.place <prototype> [<id>]` places at
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
(`GridSnap`), `Puck.SdfVm` (`GridOverlayState`, `SdfFrame`, `SdfFrameBlock`,
`shade/sdf-surface-shading.hlsli`, `shade/sdf-light-stage.hlsli`),
`Puck.Shaders` (`SdfWorldPackage.Values`), a new editor command module in
`src/Puck.World`, the shipped worlds, and the World guide.

**Check:** `GridSnapLawTests` (world lattice per axis, a free axis at zero
pitch, the magnetize release band, 24 orientations at 90 degrees, face and
center candidates against a reference; red leg: a disabled config returns its
input); `WorldEditorSectionLawTests` (every shipped world validates and round
trips with the default, a non-positive pitch refuses by name); a presenter law
that a seat in `build` with the grid on renders a frame carrying exactly its
`GridOverlayState` and a seat in `play` renders none (red leg: the grid on
while in `play`); placement-verb laws through the real command registry (a
nudge lands exactly one pitch away, then `world.undo` restores it; a surface
place rests on the fixture floor); `SdfFrameBlockLawTests` for the new
values. Canary `editor-grid`, over a fixture with a floor, a raised platform, a
ramp and a wall: with the grid on the working plane at the platform's height,
the platform's region shows grid pixels and the floor's does not; in `surface`
mode all four regions show them; with the grid off each region agrees with a
grid-free capture (the red leg). Pixels are the honest check here, since the
lines exist only in the image.

**Depends on:** nothing.

**Status:** delivered. The laws are `GridSnapLawTests`,
`WorldEditorSectionLawTests`, `SdfFrameBlockLawTests`,
`WorldEditorBuildModeLawTests` (the presenter law, and a surface place through
the host's own registry resting on a fixture floor),
`WorldEditorPlacementLawTests` (nudge, snapped nudge, turn and `world.undo`
through a real registry over a live row), `WorldEditorEditQueueLawTests` (the
pending-edit queue: each edit under its own principal, refusal before queueing,
ids minted past every pending edit, order by document version, one refusal
path, retirement with the world, an atomic read and admission, and a seeded
random interleaving) and `WorldEditorSeatsLawTests`
(the save fold, the build layer and the build bar); the canary is `editor-grid`.
The placement verbs cast against the presentation's static field, so a world
whose static field is refused (see above) places ahead of the camera and does
not rest on its surfaces. A placement's extent for reference snapping is half
its scale on each axis until E2 gives placements bounds.

**Edit order.** The pending-edit queue (`WorldEditorEditQueue`) orders a
world's documents against its verdicts by the world's own
`WorldDocumentVersion`, which every mutation verdict and every delivered
document carries: the server's activation and the install it reflects. A
confirmed value is released at the first delivered document at or past its
verdict's version, whatever that document shows, so an undo, a reload or
another editor's write in the same batch is simply a newer document and a late
document never moves a placement back
(`ADocumentOlderThanAConfirmedValueNeverReplacesItAndANewerOneAlwaysDoes`,
`AnUndoneEditIsNeverRestoredByTheNextNudge`). A queue is keyed by its world's
authority and activation; every transition happens under its lock against the
world's newest known state (the newest document any of its endpoints delivered)
and its retirement state,
and each edit goes out through its own endpoint. The queue only claims an edit in
flight under its lock; the editor hands the claim to its link after the lock is
released, since a local link's submit waits on the world's authority while that
world's tick settles verdicts through the queue, so a verdict settling off the
tick never deadlocks the tick settling its own
(`AVerdictSettlingOffTheTickNeverDeadlocksTheTickSettlingItsOwn`). A crossing onward or a closed
endpoint abandons that endpoint's edits by name, a stopped or reaped instance
answers its pending edits with `world.authority.stopped`, and a malformed
version is refused at the wire and ignored by the queue. Every edit carries the
activation its base was read from, which the receiving world checks, so an edit
sent over a traveler link that already reached the next world is refused and
rolled back
(`WorldEditorEditQueueStateLawTests`, `DocumentVersionWireLawTests`,
`WorldInstanceStopLawTests`).

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
     demands hover, gives the identity, the ray parameter `t`
     and the material, and the host table turns the identity into a
     placement. It covers non-solid placements, shapes and ops the CPU
     evaluator refuses, stamped bodies and baked meshes. The readback is the
     same one the [rendering plan's GPU picking](rendering.md#p13--hit-to-source-mapping-and-input-destinations)
     supplies through `SdfWorldPicker`, and it is counted in the view's
     work ledger. One pending answer supplies backpressure; continuing demand
     refreshes after completion even when only the camera or scene moves. Its answer arrives a frame or two late, which a hover label
     tolerates.
   - *The CPU path* answers on the frame it is asked, from the client's static
     field: exact hit points and normals for surface snapping and measuring
     (E1, E3) wherever the field exists. It never decides what was picked when
     the GPU path has an answer.
   In build mode the cursor's hover label names the placement under the
   pointer.
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
record, `mapCore`/`mapGradCore` winner payload, the primary and views stages,
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
read-back). Canary `editor-selection`: selecting the non-solid placement by
pointer puts accent pixels on it and around it in an offscreen capture, and
clearing the selection removes them. With build mode off, `world.counters gpu`
reads the same per-pass counts before and after the package, so the readback
costs nothing outside the editor.

**Depends on:** E1 for build mode's binding group.

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

**Depends on:** E2's shared GPU picking mechanism; selection tools remain E2 work.

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
immediately and releases pools after their fences. Device loss releases all
query/readback ownership. GPU timestamp readback and retained CPU sample payloads
are reported separately. Times and FPS are observational and never become
correctness assertions or quality inputs.

**Depends on:** nothing; E5 for the panel; the rendering plan's one-pixel
readback for step 3.

### E10 — Live reload and before-and-after

**Status:** the source watch uses the graph watch's shared debounce and the
existing reload command. It covers compile and document-composition inputs,
reads contents only for inputs whose file-system stamp moved, keeps the latest
reload diagnostic for the inspector and toast, and reconciles selection ids
after a rebuild. A seat can hold its displayed frame and compare it with the
live view through the ordinary capture and `place` paths, under the overlay. Split,
wipe, difference, cropped sampling and return to the live view are checked on
both backends by `editor-compare`.

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
   `world.compare wipe [position]|split|diff|off` shows it against the live view in a pane
   the `place` package draws. A bound `Axis1D` value controls the wipe position
   in `0..1` without capturing or rebuilding the graph. The echo includes
   the changed-pixel count (pixels moving at least 2 LSB, measured by the shared
   `RgbaFrameDifference` used by the canaries). Captures use the existing scene
   instance target before the comparison wrapper and the overlay, so a comparison
   never holds itself or the console, cursor, toasts and inspector drawn over it.
   A capture names the frame that rendered it and is cropped by that frame's
   viewport, whatever the layout became while it was read back, and a paused
   capture keeps its last rendered viewport. Active comparisons add ordinary static uploads and `place` passes;
   off removes those resources and retains the CPU hold. A changed crop extent
   requires a new hold before measuring differences. Completed typed commands
   report once through the terminal, tape and toast; late refusals enter
   `wire.errors`. A root screenshot follows the next composed root across a
   comparison mode change, while a named instance capture keeps its target.

**Touches:** `src/Puck.World` (the watch, the compare verbs, the capture path),
`Puck.World.Client` (`WorldViewGraphHost`, editor state retention).

**Check:** `WorldWatchLawTests` (touching a compile input submits exactly one
reload; red leg: touching an unrelated file submits none; an unchanged tree
reads no content per poll);
`WorldEditorReloadRetentionLawTests` (selection and camera survive a reload, and
a removed id drops out). Canary `editor-compare`: hold a frame, move a render
lever, and the split view's halves differ. `WorldFrameComparisonLawTests`
checks crop rounding and authored root extents, owned pixels, the shared
difference threshold, graph revision, refusal atomicity, capture settlement,
paused metadata, the captured frame's own viewport across a layout change,
the comparison composed under the overlay's instance with the scene as its
capture target, and the existing axis binding contract.

**Depends on:** E2 and E8 for what a reload keeps; the compare needs nothing.

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

**Problem:** the engine's shape-inspection tools (one subject under the
microscope, every primitive side by side, the scoped and flat field-op
contrast) live in a takeover scene no host constructs and that bypasses the
document.

**Delivers:** a shipped tool world, `sdf-gallery.puck`, beside the other tool
worlds in `src/Puck.World/Assets/worlds/tools/`, with a companion `.md` as
`shader-compare.puck` has. It is browsed with build mode, selection, the debug
views, the editor camera and `world.explain`, and `world.isolate on|off` stamps
only the selection, in presentation, in place of the takeover's single subject.
Once the parity below holds, `src/Puck.SdfVm/Debug` is deleted in the same
change, with the comments in `SdfCameraRig` that cite it, and the carve drawing
`SdfCarveBakePlanner` borrows from `SdfDebugRenderer.EmitCarve` moves into the
planner, which [E13](#e13--carving-and-the-brick-bake) keeps.

**Completion condition: parity with what `Puck.SdfVm.Debug` offers.** The
directory may be deleted only when each of these has a replacement a builder
can use in a running World:

| `Puck.SdfVm.Debug` offers | Replacement |
|---|---|
| Shape browsing: every `SdfDebugShapeKind`, parameter overrides, and the 2D family's revolve and extrude lift (`SetShape`, `SetLift`) | One gallery creation per shape and lift, selected and isolated; parameters edited live with `world.row.step` |
| Op browsing: the point and field op stack (`PushOp`, `PopOp`, `ClearOps`), blend choice with its smoothing (`SetBlend`), the scoped and flat accumulator contrast (`SetScope`) and the floor toggle (`SetFloor`) | Gallery rows for each domain op, each blend family as a pair and the scoped and flat contrast as two creations; ops and blends changed live through row edits on the isolated creation |
| The slice at an axis and offset (`SetSlicePlane`) | E4's `world.debug-view slice x\|y\|z [<offset>]` |
| Analytic or finite-difference normals, the shadow cull and the grid cull (`SetFiniteDifferenceNormals`, `SetShadowCull`, `SetGridCull`) | E4's shading levers; the grid cull is either a lever there or shown to have no remaining consumer |
| Carving: add, pop, clear, the pad carve chord, the meteor shower and the brick bake (`AddCarve`, `PopCarve`, `ClearCarves`, `StartMeteors`, `AdvanceBricks`) | E13's carve brush, `world.carve erase`, undo, and the bake over the world's brick pool; the meteor shower as a gallery row that authors a dense carve cluster |
| The orbit camera, pan, zoom and pose (`SdfDebugController`, `SdfOrbitInput`, `PoseCamera`) | E8's editor camera |
| The gallery tour: each `SdfGalleryExhibit` (`LiarSpiral`, `DrosteTunnel`, `CellJitterCreases`, `NotchHorizon`, `SmoothChain`, `WallpaperP4G`, `CarveCeiling`, `LogSphereRunDoc`, `DriftMonolith`) with its framing pose and plaque | One gallery area per exhibit with an authored camera, its plaque as a text screen or HUD panel, and a bindable next and previous exhibit through `view.override camera` |

**Touches:** `Puck.SdfVm` (the deletion, `SdfCarveBakePlanner`),
`src/Puck.World/Assets/worlds/tools`, `Puck.World.Client` (the isolate filter),
the rendering handbook pages that describe the debug scene.

**Check:** the parity table, each row demonstrated in the gallery world, with
the evidence recorded in the commit that deletes the directory. The eclipse
check: `puck references` finds no caller of `Puck.SdfVm.Debug` outside the
deleted directory. The gallery source passes `ShippedSourceLintLawTests`; an
isolate law holds that the frame emits only the selection's instances (red leg:
an empty selection emits the whole world, not nothing); the gallery boots and
captures each exhibit offscreen.

**Depends on:** E1, E2, E4, E8 and E13, since every row of the parity table
must be usable before the old scene is deleted.

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
analytic bins.

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
home, and the brick bake is what makes hundreds of carves affordable, so E13
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
