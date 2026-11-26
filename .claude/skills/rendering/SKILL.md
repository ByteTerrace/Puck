---
name: rendering
description: "Maps Puck's SDF renderer and GPU code: mapCore, mesh bake, HLSL kernels, sdf-isa.hlsli, SdfOp, C#/HLSL layouts, world.shaders.reload, field scopes, GPU cost and occupancy. Sculpting: sdf-authoring."
---

# Rendering

A map of Puck's GPU presentation code: where each part lives, the contracts
that bite when you change it, and how to see a change. [Rendering](../../../docs/rendering/README.md)
and its handbook explain the renderer; package READMEs own the detail. When this
file and the code disagree, the code wins and this file is corrected in the
same change.

Rendering is presentation: floats are legal, and nothing here may feed back
into simulation state. The deterministic contract lives with the fixed-point
query evaluator (`SdfFieldEvaluator`) and `maths-usage`.

## Authored shading

Read [authored shading](references/authored-shading.md) for `SdfShadeProgram`,
the op table, CPU admission, slice ownership, helper-closed cost, shadow demand
and generated interpreter/emitter semantics, shared GPU phase invocation, the
bounded dispatch planner, and the three views and glass kernels (plain,
interpreter, specialized set) with their one publication door. CPU admission
proves neither driver spill behavior nor the measured dispatch margin; those
remain in the author-shader plan. A views or glass register reading names its
kernel: `[pipeline-statistics]` lines (Vulkan) print `sdf-world-views`,
`sdf-world-views-plain` and `sdf-world-views.shade-<set>` apart.

## Camera bloom

The [bloom decision](../../../docs/decisions/rendering.md#bloom-is-a-cameras-scatter-applied-once-before-tonemapping)
owns the design; the [frame handbook](../../../docs/rendering/sdf/handbook/frame-rendering.md#camera-bloom)
owns authoring and pass order. `SdfWorldPackage.WithDisplay` is the view's last
stage: it renames the composite's output to `scene-light`, adds the optional
output-relative pyramid, and ends in the `display` pass, which sharpens, adds the
scatter, applies `render.tone`, fades by screen fidelity and encodes on the
extended sRGB curve into `color` ([the decision](../../../docs/decisions/rendering.md#scene-light-is-converted-to-display-values-once-at-the-end-of-each-views-graph)).
`SdfOpticsRecorder` records the pyramid and the display pass over one interface
(`SdfWorldPackage.OpticsMembers`, `isa/sdf-optics.interface.hlsli`).
`SdfWorldPasses.Entry.Converts` decides which views take the stage: every view
but one published as a window's light (`Unexposed`) or a sky layer
(`GeometryOnly`); the screen shading reads the same split by `SdfScreenFace`
(an opening shows linear light, a display decodes its working image).
`Entry.OpticalBloom` gates transport, diagnostic, full screen fidelity and
disabled views before allocating. Keep that gate and `world.bloom`'s reported
bypass reasons aligned. The display pass's values (tone, sharpness, fidelity,
scatter) enter its cadence signature (`SdfWorldTables.PassSignature`), or a still
view keeps its old picture after a `render.tone` edit. Root `place` only places.
Never bloom portal/window/mirror transport or raw screen textures. Never sharpen
the scatter. Every diagnostic except the bloom-only view bypasses optical scatter,
and a debug view that is not scene light passes the display pass unencoded
(`DebugViewModes.ComposesFinal`).

The tables build each view's signature inputs once a frame: its pass block, with the
world's shared cascade set as the view reads it, and the hashes every pass group takes
of it (`SdfWorldTables.CascadesOf` and the memo beside it, which also compares the
shadows' `ContentRevision`). The renderer's passes read the set through
`SdfWorldTables.CascadesOf`, never `SdfMeshShadowCascades.Fit`. A pass-block value
taken from the tables belongs in `SdfPassValues`, which the memo compares by value;
a value read from anywhere else must move the memo's epoch (`ForgetViewInputs`),
or a pass stands on a stale signature. `world.counters sdf.cadence` counts the
signatures, blocks and fits.

Another instance's image (a screen's source, a sky layer's infinity image) changes
in place behind one handle, so no table hash sees it. A pass that samples one folds
its read's publication and taint into its signature (`SdfWorldPasses.ReadSignatureOf`,
whose `ReadsOf` lists which parts sample which images; an unlisted part folds every
screen), and the rest stand on their own inputs. A screen is never a reason to
force every pass. The whole view stands on the same images: the render graph hands
`IRenderGraphPackageFactory.IsUnchanged` what each read will bind this frame
(`RenderGraphReadStands`: a producer already declared unchanged, read at its current
frame), and `SdfWorldPasses.ReadsStand` holds every screen the views pass or the sky
samples, and every infinity image, to the publication the view's latest render
observed, so a still screen in a still view renders nothing.

A window session's frustum is fitted to the part of its glass its viewer sees
(`WorldPortalVisibility.Window`, `WorldWindowFrustumFit.FitFrom`), and the host
sizes its image by that part (`WorldPortalVisibility.Coverage`), so a glass at the
near plane or past the image's edges renders about a texel a pixel of what can be
seen. The window rides the view (`SdfViewSnapshot.ImageWindow`); `SdfWorldPasses`
notes it as each render is prepared, on the frame thread, and the sampling view
writes the window of the image it binds into `screenWindows`, which
`sampleScreenSurface` maps the source through. Never compute the window again on
the sampling side: a held or standing image keeps the window it was rendered at.

A pass signs with its motion scope's revision, not the residency's
(`SdfMotionScope`, [decision](../../../docs/decisions/rendering.md#a-pass-stands-on-what-it-can-show)):
the mesh pass's scope for it and everything that shades what it drew, the glass
pass's for the glass passes, one for each cascade (`SdfWorldPasses.MotionOf`).
`SdfWorldTables`' motion scopes are the one place a staged change is judged, and
the list of what reads a transform slot lives in that file's header: a draw's
record, its frame slot, a skin's members, and a light, a medium or a paint riding
a slot. Add a kernel read of `sdfDynamicTransforms` for anything else and you must
name it there, or a pass stands on a stale image; nothing else catches it. Build a
pass's cull through `SdfWorldPasses.CullOf` and note its extent (`NoteDrawn`), so
the scope judges by the cull the image was drawn under. `world.residency` prints
each scope and why it last moved (`motion-scopes=`); `sdf.cadence.scopes` against
`scopes.met` is how many stood. A scope that never stands names its cause there.
A skinned draw's record carries the host bound the scopes judge it by
(`SdfMeshBox.TryOfSkin`, written by `NoteSkinnedRows`), so every pass culls it.
A screen's emission lights the scopes its range reaches (`NoteEmission`), moving
their lighting revision (`SdfMotionScope.Lighting`, `sdf.cadence.scopes.lit`);
the range lives in the screen surface row's `origin.w` and the shader windows the
light to zero there (`sdfLightWindow`), so the two stay one value. The views pass
folds only the screen images whose faces its mesh scope keeps
(`SdfWorldTables.ScreenFaceShows`).

`Shared/bloom-filter.hlsli` uses cell-edge reflection and conservative cell
integration. Unit-sum taps alone only preserve constants: edge clamping duplicated
a corner impulse's energy, and point resampling odd levels changed their mean.
Keep the constant, impulse and odd-grid witnesses in `BloomEnergyLawTests`; include
the final placement tent and FP16 storage. Fractional radii blend symmetric kernels
at adjacent integer widths, rather than combining their interpolation with the
resampling. Placement can minify the retained source envelope during a shrinking
layout transition. Integrate its entire footprint, including edge hats; the axis's
three cached pairs are not a tap limit. Keep the 2×, 4×, 10× and one-pixel placement
constant/impulse witnesses. Renormalizing truncated taps still discards energy.
`CameraBloom` is a value; `WorldBloomLawTests` covers on/off allocation.
Seat lens selection happens beside pose selection from the active endpoint's program.
`PlaceViews` derives optical placement from the resulting snapshots, including routed
seats and camera-mode overrides. Never infer it from unused boot-world programs.
Publish that demand for the next `BeginFrame` reconciliation. Reconfiguring from
placement mixes newly selected capture-fill/environment reads with footprints
already formed for the current frame and can make a capture refuse or crash.

Pin exposure (`world.exposure pin`, which also stops local adaptation) for on/off captures, and record each EV with
`world.exposure`. Use `world.rate pause` to hold one simulation tick for the pair.

For a motion burst, arm it inside a sustained pan and inspect its reported ticks:
an eight-tick pan can finish before the first of eight asynchronous captures lands.
Motion still runs publish an Operator attachment, as sweep and motion routes do.
Use it to inspect a stalled run; for shadow-only diagnostics, disabling indirect
lighting avoids an unrelated probe-convergence wait. Keep lighting enabled for
normal frame measurements. `world.cull frustum` changes camera selection too;
use `world.cull camera` to isolate cascade culling with camera occlusion unchanged.
`world.wait residency` follows the latest render schedule through
`SdfWorldPasses.PendingMeshesIn`: newly scheduled renders, standing and deferred
contributors owe their geometry, including before the first submission. The host
residency is checked directly. Unread views and waiting views with no produced image
do not contribute. Readiness reads the schedule on the host pump, without looking
its names up in a potentially replaced node set.
A refusal names what the residency owes (`SdfWorldTables.MeshResidencyPending`):
the staged list's owed bytes and meshes, how many restore from the store or are
in landing, whether background work may bind, and the arena's relocations, or the
required upload against those submitted and completed and its fence. Landing runs
at `SdfGpuWork.UploadBytesPerSecond` of wall time, so a boot owes the debug
world's whole mesh region for some seconds whatever the frame rate
(`world.residency` prints the region's size). The rate, like every other that
meters background work (the budget's credit, a deferred job's wait, the
irradiance grants, the intervals between lighting captures and scene builds, a
probe's light age), reads the device's clock (`IGpuDeviceContext.Time`, shared
through `SdfResidencySelector.Time` and `SdfMeshIrradiance.Time`): the host's
wall clock in a World, and in a device-free law a `VirtualClock` the rig steps one
display period for each frame it drives (`VirtualClock.Frame`), so the frames the
law drives decide when background work lands. A wait for work running on the
thread pool (a build, a trace's worker) stays real time and is bounded by
`TestLiveness`; never meter it with the device clock. A rig that settles the
irradiance volume within a count of frames waits out `SdfMeshIrradiance.HostWorking`
after each one (the worker computing on the host, not blocked on a probe job), so
the frames count device round trips, not how fast the pool runs. A frame's default
`Lights` and `Sky` are written into its content on first read, so a residency's copy
carries a rig's edits to them.
What the irradiance volume publishes reaches a view through signatures alone: each
publication moves `SdfWorldTables`' irradiance revision, which the tables' signature
and the lighting group fold, so the shading passes gather again exactly when it
publishes, and a camera sample's reading arrives through the exposures' air light and
camera light in the pass block. `SdfWorldResidency.IsUnchanged` reads no state of the
volume's background work, so nothing the pool finishes mid-frame decides whether a view
stands; `SdfMeshIrradiance.Settling` serves a converging frame, the residency's
preparation and a rig that settles the volume whole.
Waiting on every allocated residency
deadlocks on hidden routed scenes whose GPU work correctly never starts.

## Mesh pipeline limits

Glass shadow extinction is authored by `SdfGlass.ShadowDensity` (zero by
default; standard water opts in). Cascade selection's optical indirect phase
routes glass to `SdfMeshRasterPass.ShadowGlassKey`; opaque entry/exit stay in
their own phases. Count glass vertices once and each executed opaque phase.
Only a program with a drawn positive-density glass shape adds optical storage
through `WithGlassShadows`; the graph's ports govern its allocation, clear and
surface reads. Each texel holds three words (signed depth and nearest entry).
`pipeline.inspect world` (or a session node named by `world.counters gpu`)
reads the installed graph's named allocations and actual bytes. Read it beside
`world.residency` and `host.memory`; those broader reports alone do not identify
an optical buffer. A document reload clears the exposure pin, so pin the same
EV again before comparing density-only reload stills.
The glass vertex variant clips zero-density triangles before rasterization;
opaque vertices export only position and clip distance. Density has its own
cadence signature, shared by cascades and surface, including palette-only uploads.
The cascade's optical-depth buffer integrates to its first opaque depth with
the shared `frame/sdf-optical-depth.hlsli` accumulator. Surface shadow taps
read extinction and nearest glass entry beside the opaque depths. Glass is
still absent from the irradiance trace. Keep the separate glass descriptor
set: binding the sampled entry depth while it is an attachment is invalid on
Vulkan even when an earlier shader does not read that binding. Return the
entry depth to its attachment layout before the graph's outgoing transition.
Material row 25.w carries shadow density beside cel ink RGB; keep its C# pack
and HLSL load together without changing the row stride.
For a neutral-default comparison, omit density from both prototype palettes
and field materials in `state.world`; changing only the prototypes leaves
field water shadowing. Compare fresh base and head processes with lockstep and
pause enabled before startup, identical camera, tick and pinned exposure. A
surface-only hot swap cannot establish the complete renderer's neutral default.
The glass fragment returns zero depth, which fails the pipeline's Greater test
against non-negative opaque exit depth while preserving its optical side effects.
`world.shaders.reload` replaces that kernel but not `SdfMeshRasterPass`'s
occlusion/selection pipeline; a selection diagnostic needs a rebuilt host.
For an independent vertex check, `world.cull off` and one fixed `world.detail`
level make the mesh, glass and cascades select the same geometry: each cascade
must count twice the mesh vertices plus exactly the glass vertices.

Each view draws a bake group at the detail its own pixels need. Where the bake keeps
a group's floor of its own shape beside the finer tier it draws
(`SdfMeshPathBake.FrameAccess.FloorOf`), the dresser binds the floor's pieces after
the mesh's in every placement's rows and gives all of them one band (`SdfMeshBand`):
the sphere about both meshes' box and the floor's cell. `sdfSelectionBandKeeps` draws
the floor where its cell projects under half a pixel (a view) or half a texel (a
cascade) and the finer tier everywhere else, deciding from the shared words and the
draw's pose alone, so exactly one tier of a placement draws; a finer tier yields only
while every floor row it names stands. Floor rows stay out of the irradiance volume's
static scene (`SdfMeshDraw.InStaticScene`). Below the floor, the collapse's last two
levels (`SdfMeshCollapse.CellShares`) serve views far smaller than the tier was
demanded for. The selection marks clamp a level past the second to it, so
`drawn.*.level2` counts the second level's triangles for those draws.

Draw selection reads the retained `SdfWorldTables` staged draw span, the same
records whose boxes and GPU records were uploaded. Never borrow the frame source's
mutable draw list across staging: a portal handoff can clear it while the retained
count still names the previous records.


The frame rasterizes baked geometry. `SdfCompositionFrameSource` allocates the
live transform table and reports an actual `[world.render] envelope`; it builds
no worst-case probe. `WorldRigidAssets` emits local creation templates once and
supplies `SdfMeshInstance` roots and identities, one row per copy that it rewrites
in place (`SdfMeshInstance.Update`), so a moving body allocates nothing; a consumer
that compares a copy by reference compares its `Revision` too, as the composition's
copy journal, the dresser and its bindings do. `SdfMeshDresser.Dress` expands
the shared mesh into copies without program or palette duplication. Fixed placements
reuse the ordinary static emitter in prototype-local ranges (`SdfMeshTemplateRange`),
including its per-shape bounds, detail regions and separate groups. The largest
occurrence bakes at ordinary authored scale; copies use relative matrices. Do not
replace that partition with one dynamic whole-object range or promote the default
bake spacing into explicit detail: both increase tessellation. `IsDynamic` selects
static lighting membership; `UsesRootMaterialFrame` independently selects material
coordinates while retaining the root slot for skinning and portals. Dressing retains
group/copy topology and immutable per-surface picking metadata across pose changes.
Its flat reverse binding indexes consume the frame's captured transform changes;
the output draw journal and GPU tables retain independent consumer cursors. Keep
the source identity, captured serial, roster and row count together. Never use a
later producer serial for a borrowed frame or skip the final GPU history settle because no draw changed.
`GpuChangedRows` owns both journals and publishes only its `GpuRowChanges` reader.
Every consumer uses `GpuRowCursor`, which validates reader, roster, extent, revision and captured endpoint,
returns bounded runs (a whole-table run on refusal), and commits only after consumption.
The tables retain producer indices while meshes are owed: `StageMeshDraw` alone packs a live row or
retires its history and writes `SdfMeshRegion.WriteAbsent`. `SDF_MESH_ABSENT_FLAG` rejects selection
before mesh reads; zero placement words reject every triangle through the existing hit door.
`GpuRowIndex` supplies the mesh-to-row landing chains and the dresser's slot/copy chains.
`GpuBufferPages` issues the mesh destination's writable lease. Landing completion and
`MeshStands` require its generation unchanged; a moved mesh without one stays owed and
its rows stay absent. `GpuRegion` clips coalesced copy runs to this coverage and checks
the proof again at recording, restaging stale requests and retaining unmapped debt.
Mapping changes re-owe the host-known prefix and stale published meshes; a shrink
re-lands survivors even without arena relocation. Destination replacements read
the new buffer's authority. A mapped destination changes its chunks' backing behind the same leases.
Landing stages only its mesh's rows; the draw cursor remains usable while other meshes are owed.
The staging door maintains the tables' glass row set. `RecordLanding` requires the service's actual
queue to be the device's frame queue before it owes any words.
`SdfMeshRows.Create` returns the table and its sole writer. The dresser owns runs per host, group and own-mesh
occurrence; allocator tickets guard frees and fresh occupancies guard history even for same-mesh reuse.
The frame keeps its host inputs (`MeshDraws` and its journal) and references the dresser's `MeshRows` and dressed pick map, which `SdfMeshDresser.Dress` adopts onto the frame in place (`SdfFrame.AdoptDressing`) rather than copying the record; a frame another dresser dressed is copied first. The revision, journal, serial and staging cause of a dressed frame are the rows' own, read through `SdfMeshDraws` (`SdfFrame.DrawRows`), never the host fields. Claims and glass counts move inside writes.
`SdfMeshHistory.Observe` compares table identity and occupancy; free/owed/refused rows retire in `StageMeshDraw`.
Only start over rebuilds every owner: retry, the free share, forced packing or a noncanonical locked frame.
Picking stamps the mesh target's row table/revision and rejects a changed row identity before resolving its owner.
The static scene key and diagnostic comparison ignore row order.
`world.rows check on` enables independent dress, pack, generation-ledger, seed and quiescent device comparisons
(`sdf.rows.check`); `force limit <bytes|auto>`, `force relocate` and `force skip tables` exercise
refusal, relocation and an expired consumer. `force skip host`, `force skip scene`, `force pack` and
`force dress-fault [<dresses>]` exercise the remaining cursor and publication paths. `host.memory collect` and `gpu.faults lose` exercise
trim and new tables. The device-free witness is `SdfMeshArrivalLawTests`; a headless World stages
no draws. `SdfMeshRows.Writer.SetClips` compares clip values in order and revisits only their old/new slot chains,
indexed across host, rigid-copy and in-place rows. The table derives staged draws from base draws, owns one carried
run per clip, and reconciles changed membership before publication. `SdfSlotClip.Apply` is only the rebuild oracle's
clip step. `rows.written`, `rows.clipped` and `rows.restored` in `sdf.instances` count by owner kind.
`world.residency` reports logical journal/scratch array bytes separately from
mesh residency and the overlapping process/account/backend memory readings.
Pending bake demand reads the same occurrence index and transformed copy boxes;
prototype-origin boxes and a union of far-apart copies misprice both priority and
near-residency diagnostics. Animated
creation fields use root and motion-owner deltas; CPU pose scratch remains per
actual shape. Bake groups keep separate bounds and detail even when they share
a motion slot; the lattice frame and instance selector are independent. Use
`boundInSlotFrame` for rest-pose offsets on a moving owner's slot; a world-axis
offset would move the bake's box away from the geometry when that owner starts turned. A skin
past 255 members throws before queuing, instead of attaching excess members
to the root. Volume overflow is explicit. Scope depth is 4, and derived faces
follow authored screen indices. A program declares any number of screens: a
screen's index is its identity (the material id carries it), and a residency
holds one of `SdfProgramBuilder.MaxScreenSurfaces` slots for each screen that is
resident now (`SdfWorldTables.SettleScreenSlots`, the one door, nearest first;
past the slots a screen shades as one with no source, never refused). Every
per-screen table and kernel read goes through the slot (`SlotOfScreen` on the
host, `sdfScreenSlot` in the kernels, which reads the identity-to-slot map in
the mapping table's tail); never index a table by identity. Every set of
resident screens (bound, emitting, sampled, a tile's lit screens) is one
`UInt128` slot mask walked by its set bits through `BitWords`, and the kernels
read it as a `uint4`, as they read the light mask: never loop over every slot,
and never add a `uint` screen mask. A screen's glyph grid claims its run from
the shared cell pool (`SdfWorldTables.MaxScreenDecalScreens`). `SDF_MAX_INSTANCES` bounds
one bake program, not a per-world render budget.

## Moving blends

Creation noise relief requires all shapes to ride the root. `CreationMotionPlan` refuses a relieved document
with part owners; validation reports the same boundary, including look part followers. Root drivers such as
`sizeBy` keep one composed field through `WorldPlacementStamper.EmitRootSurface`, shared with rigid body assets.
It bakes in creation units with the relief's detail spacing; only the root matrix changes with size or pose.
Opaque relief follows shapes and text in their field scope; glass keeps its own surface, as static emission does.
Amplitude and wavelength scale with the creation in both render and fixed contact emission. `sizeBy` reads
authoritative state; solid roots draw the contact-sampled scale published in the tick snapshot, interpolated beside
their bodies. Never feed a presentation driver's phase into collision.

`CreationMotion.Envelope` bounds each cross-owner smooth seam; `CreationMotionPlan.RenderDocument`
is the pooled emitter's partition. A rejected seam partitions its authored group by actual motion owner.
PUCK122 prints the rotation and displacement bounds and whether it skins or creases. A regular-polygon
field, an unbounded waveform, an effector or state-bound rest cannot enter an admitted skin.
Driver weights, including `weightBy`, are clamped by `WorldGaitDrivers`.
Engraved moving creations use `WorldCreationInstances.EmitEngravedOwners`: the shared group and text
emitters carve each rigid owner's rest surface, and the lettering rides that surface. A glyph therefore
never enters a cross-owner influence reading. Followers and their descendants inherit the actual slot
partition and cannot enter a finite-envelope blend.

The pool marks its rigid delta-frame instances `SdfInstanceRange.RestPoseSkin`.
`SdfMeshGeometryBuilder` freezes those slots at identity and includes the opt-in in geometry and form; a
skin bakes once, at that rest. Do not freeze arbitrary dynamic slots: those may carry absolute poses.
`MovingBlendRestLawTests` exercises swing, slide and timeline owners through the real packer.

A cutter whose owner moves alone and only carves is an operand (`CreationMotion.OperandShapes`), never a skin
member or a crease: the pooled emitter declares it on the instance of the group it cuts
(`SdfInstanceRange.Operands`), that group rides its one surface owner's slot (`CreationMotionPlan.TryCutSlot`;
a group of several surface owners beside an operand is partitioned, `CreationMotion.SharedOperandGroups`), and
`SdfMeshBakeGroup.Seal` refuses an operand beside a skin member. The geometry hashes the operand's pose snapped
to the group's tier cell once the tier is known (`SdfMeshGeometryBuilder.AppendOperands`), in geometry and form,
and keeps it in the pose rows the bake samples. `SdfMeshPathBake.Revisit` is the one door for frame inputs that
move without a program rebuild, sampled snapshots and operand poses: it visits only their readers, snaps and
compares each operand every frame (`SdfMeshGeometryBuilder.OperandsMatch`), and plans a moved group again
through `Succeed` or as its in-flight bake's `Successor`. `world.counters sdf.mesh-inputs` counts the re-plans
and the coalesced successors (`SdfMeshInputs`). `puck mesh audit --cpu --pose` given an operand's slot offset
plans its group again with the cut there and prints the operand's snapped pose. Contact still reads the cut at
rest (G5), and relief still refuses any part owner (G2).

`SdfFieldEvaluator.ReadInfluences` shares the point interpreter with culling disabled, carrying owner
vectors through every composition and scope. `ReadInfluenceGradient` takes fixed central differences
of the renormalized pair. `SdfMeshSkin.Build` samples exact positions once and copies tuples to every
crease/material vertex there. A third share above 1/64 falls back to rigid owner contours, through
`SdfMeshSkin.Split`, `SdfMeshAudit.MemberFields` and `SdfMeshPathBake.ContourLevel`; the bake report names it.
`SdfMeshCpuField` is the shared CPU sampler for that contour and the CLI's offline tools.

`SdfMesh.Influences` carries the second member, unorm16 share and three binary16 gradient components
in twelve bytes beside `VertexMembers`. Keep both through native copies, material renumbering,
subsets, planar compaction, codec and uploads. Planar merge compares full tuples and removes no
blend-zone vertex. Motion reseats must consider either used member.

`frame/sdf-mesh-skin.hlsli` owns group-relative dual-quaternion blending, the first-owner hemisphere
rule and the Jacobian normal correction. Current, previous and shadow positions share it.
`sdfMeshWorldVertex` returns position and corrected normal together; `sdfMeshSurfaceNormal` resolves
each triangle's vertices once. The CPU audit reuses Maths' `FixedRigidTransform` carrier; posed GPU
arithmetic remains presentation float and cross-backend bit identity is not claimed.

`puck mesh audit --cpu --world <source> --prototype <id>` prints blend-zone vertices, split-tuple
places (must be zero), and geometry keys. `--pose '1=0,0,0,0,0.05,0,0.9987492'` applies a current
delta by member slot offset in the group frame and audits against the posed field. Compare both
envelope extremes. A build cannot prove motion: request a 60-frame presented burst on both backends,
with motion diagnostics on still parts, as the [moving-blends memo](../../../docs/plans/moving-blends.md) specifies.
Opaque and glass shading share `sdfLayerFrame` for material coordinates and lanes.
The striped-neck burst and quiet cost window remain lead readings.

A retained separate bake group must still ride the newly planned `Slot`, even
when its instance selector and local geometry stay unchanged. Rigid draw poses
are picking metadata; `SdfMeshDraw.MotionIdentity` names the stable body/surface
for both whole-list and changed-record uploads. Rigid copies include the entity
address and pose-discontinuity epoch in that identity, so teleports and reseats
seed the previous mesh matrix at the new pose, including authored meshes.
Noncopy groups consume the same captured transform-reseat journal at mesh staging.
A root or vertex-used skin-member cut advances the draw generation and seeds its
compact previous matrix, even when packed pose bytes are unchanged. Keep this
consumer cursor independent of dynamic-table and draw-change cursors; unknown
history cuts transform-dependent draws conservatively. Ordinary continuous motion
does not trigger a reseat roster scan. `SdfMovedTransforms.Commit` retains explicit
equal-pose cuts during both incremental updates and repacks.
Rigid root followers observe
portal carry history, reseeding when it expires. Isolation also filters rigid
templates, parented paints and volumes, text and authored meshes.

`SdfMeshLibrary` retains an age-ordered byte budget derived from available
machine memory, evicting oldest geometries rather than clearing the library.
Measure draw/vertex
counts, actual transform rows, program/material bytes, region residency and
bakes; the lead measures frame times and edit-to-running on a quiet machine.

## GPU residency

A table the host writes each frame is a `GpuRegion` (`Puck.Abstractions/Gpu/Residency`). Create it with the
profile constructor so it chooses ring, in place or staged by `GpuResidency.Select`, and grow it only through
`Grow`, which chooses again and restages a region past a ring's share of the aperture; never select a policy or
replace a region in the owner. A write owes and copies into the host copy only the words that differ, and a flush
writes only owed words (`GpuRegion.LastFlushedBytes`): `world.residency`'s `region-flushes` line names what the latest
upload flushed of each residency table, and a render node's regions count under its `region copies` pass
(`world.counters gpu counter gpu.uploads`). A ring writes each change into every slot's buffer, so a table whose rows
move every frame (the animated bodies' transforms) flushes about twice its changed rows. A fixed policy (the always-staged mesh region, uniform frame blocks) uses the policy
constructor. Round a constant-buffer block with `IGpuBindings.ConstantBufferBytes`. What `Grow` returns retires
after the submissions that read the old buffers (`SdfWorldTables.Retire`).
The mesh region is the presentation's, never a residency's: every residency on a device names its meshes in one
`SdfMeshRegionStore` (`SdfResidencySelector.AttachStore`), where a mesh any number of them name is placed, landed and
held once, and keeps only its draw records, in a staged region of its own tables bound at `sdfMeshDraws`
(`SdfWorldPackage.MeshDrawRecords`); a record's word offsets address the store's region at `sdfMeshRegion`. A kernel
reads a record through `sdfMeshRecordWord` (`frame/sdf-mesh-records.hlsli`) and a mesh word through `sdfMeshWord`
(`frame/sdf-mesh-words.hlsli`), and a reader that keeps both in one buffer binds it at both members. The store's gate
is held across a residency's staging (its names, the plan and the records it packs), so residencies packed side by side
never interleave; never read another residency's rows or draws from a store callback, only what that residency last
told the store (its landing priority). A mesh the store lands, moves or takes back has its rows staged again in every
residency that names it (`SdfWorldTables.NoteStoreChange`), and the copies no landing job carries (a compaction's
moves, a grown region's carry) ride whichever upload records next (`SdfMeshRegionStore.RecordOwed`), which relies on
every residency's upload of a frame reaching the queue before a view of that frame records. A residency's World sets
bind the buffer its own plan left (`BindMeshStore` runs after `SdfMeshRegionStore.Plan` in the staging, since the
records it packs address that plan's placements), and a buffer the region replaced is held while any residency's sets
may still name it (`SdfWorldTables.NamesReplacedStoreBuffer`, in the replacement's pending use). A set bound before
the plan reads a grown region's records through the smaller buffer, past its end.
A world is resident once: a mesh renumbered for another program's materials (`SdfMesh.GeometrySource`) is placed as
its triangle materials alone and draws the first mesh's vertices and indices (`SdfMeshRegion.PlacedWordsOf`), so use
`PlacedWordsOf`, never `WordsOf`, for anything the region places or lands; the library finds a living mesh its cache
let go of instead of decoding a second (`SdfMeshLibrary.Revived`); and a second scene of the world the presentation
draws takes that world's demand (`SdfFrameContent.DemandViews`) and its tiers (`SdfMeshLibrary.TiersOf`). Editing any
type the bake's closure reaches (`SdfMesh`, `SdfMeshLibrary`, `SdfMeshStore`, `SdfMeshPathBake`) moves the bake
identity, and the next boot bakes the world cold.
A staged region created `mapped` (the mesh region, the probe scene) asks `IGpuBufferFactory.TryCreateMapped` for a
destination that reserves `GpuRegion.MaxByteCount` and backs 1 MiB chunks on demand (`IGpuMappedBuffer`; Direct3D 12's
`DirectXGpuMappedBuffer` at reserved resources tier 2, tile runs from `DirectXHeapArena.PlaceTiles`; Vulkan's
`VulkanGpuMappedBuffer`, a sparse buffer whose chunks bind page runs from `VulkanMemoryArena.PlacePages` through
`VulkanLogicalDevice.BindSparse`). A device created without `GpuOptionalCapabilities.MappedBuffers` (the capability
missing, or `--withhold-gpu mapped-buffers`, which is how the two paths are compared on one machine) answers none and
keeps a contiguous destination. It grows and shrinks in place, with no carry and no second buffer, and the
selector leaves no replacement room for it (`SdfResidencyGrant.Mapped`). Its buffer's length is the reserved range, so
never map a region a kernel sizes with `GetDimensions` (the irradiance feedback). `SdfMeshRegionStore.Cover` is
the one place the mesh region's interior backing follows the arena: a chunk no placement covers is
unmapped behind its readers, and one a placement needs again is mapped before any landing leases it.
It gives chunks back before it backs any, and a chunk the device refuses makes it forget the meshes no name holds
and try again at every residency's service (`SdfMeshRegionStore.Service`) as the given-back chunks retire. A mesh named ahead that lies in a
refused chunk is refused ahead (`SdfMeshRegionStore.RefusesAhead`), so it takes the draws and the mesh it replaces gives
up its memory; a product waiting on words the device will not back would otherwise hold the bake pending for good.
A region has two limits, both from `SdfResidencyGrant.ReserveGrowth`: the bytes the arena places in (`LimitBytes`,
what the pool has room for) and the device bytes the region may hold while it places storage the draws can do without
(`HeldLimitBytes`), which is the whole of what the pool's limit leaves the region (`SdfResidencySelector.ShareOf`; the
selector keeps one region grant, which holds the reservation, and a detail grant per residency, which budgets its bake
by its share of the bytes its draws name, a mesh several residencies name counted in equal parts). The pool's
room at boot is the fixed storage of views that open later, so a product that waits beside the mesh it replaces must
not fill it: the fixed class then passes the pool's limit and nothing gives the room back. The arena's one placing door
(`TryPlace`) refuses a waiting product that would take the region past the held limit (for a mapped region, the chunks
its placements cover), and a held limit that falls under what the region holds takes back, at the next plan
(`SdfMeshRegionArena.SetLimit`), the meshes no list names and then the waiting products, which take their draws at once
(`RefusesAhead`). A drawn mesh and a kept floor are placed and kept whatever the held limit says, since refusing either
leaves a hole; the bake's budget (`SdfResidencyGrant.Budget`, `SdfMeshTier.BudgetEighths` of the same share) coarsens
the drawn meshes, so a region stands over its share until it has. `world.residency` prints `region-held-limit`, `arena-held`, `arena-placed` and
`shed`. A mapped region's chunks come from small heaps kept for tiles (`DirectXHeapArena.TileHeapBytes`, a few chunks each), which are freed only when empty, so chunks
given back leave spare memory only a mapped region can take (`IGpuMappedBuffer.SpareBytes`): the selector counts it as
the regions' and a region grows into it without a promise. A refused mesh presses `Retier` on its own, so pressure
raises the detail bias before it leaves a hole.
Uniform construction, growth and descriptor writes share `IGpuBindings.RequireConstantBufferSize`: aligned,
nonzero and within `IGpuBindings.MaxConstantBufferBytes`, the current backend's view limit.

A growing table picks its next size with `GpuCapacity`: `Grown` (a fraction of the current size, for amortized growth)
or `Headroom` (a fraction over the need, for a table fitted to its need). An object replaced while a queue may still
read it waits in a `GpuRetirementQueue` keyed by a completion point counted on one queue (an upload's sequence, a native
fence value). An owner that lets go of objects other nodes' submissions read, and that no longer records itself (a
released residency, a dropped portal's or routed view's tables), hands them to the device's one retirement
(`GpuDeviceServices.Retirement`, a `GpuDeviceRetirement`) instead of waiting for idle: `RenderGraphRuntime` polls it
after each frame's batch, which queues a timeline checkpoint behind it, and the hosts drain it at a loss
(`DeviceLossRecovery`) and at teardown (`LauncherHostRun.DrainRetirement`). A residency's one release
(`SdfWorldResidency.ReleaseDeviceObjects`, final or on a loss) retires every build it still leases, a kernel reload's
included, into `SdfWorldPipelineCatalog.RetiredBuilds` and waits for none; a loss alone then drains that table. So
`WorldRenderRoot` drains the device's retirement
itself before that table, in its holdings and in its loss.
A residency no reader asked for parks on a live device (`SdfWorldResidency.Park`, decided once a frame by
`SdfWorldPasses.ParkUnobserved` from `SdfResidencySelector.Excess`, the device pool past its limit, which follows the
system's budget less the usage the ledgers do not hold): `m_tables` is cleared between frames and the tables go to the
device's retirement, keeping the bake, the lighting's measured words (for `SdfWorldResidency.ForgetLightingSeconds`, after which the residency's upkeep disposes the volume and a wake lights the world from nothing), the frame and the leased pipeline set. Every
path that reaches the tables goes through `m_tables`, so a reader that comes for a parked residency prepares it, which
builds new tables, and its views keep their last images while `SdfWorldResidency.Waking` stands. Every staging
(`Stage`, `BeginStage`) claims the wake before it prepares (`ClaimWake`, which `Observe` and `Want` share), and a
preparation that finds the residency standing parked settles nothing for the frame (`BeginPack`), so whichever door
prepared it first, a render's staging builds the tables in the same frame; a new door that prepares a residency for a
render goes through a staging, or its recording reaches `Staged` with no tables. A park while
`OwesPreparation` would rebuild at once, so a parked residency owes nothing and has nothing arriving.
A view its viewer may not see now is paused (`WorldViewGraphHost.ApplyPause`) and publishes nothing; the render graph
treats such a producer with no image as it does an absent source (`RenderGraphRuntime.NoteStandIn`), so a reader drawing
its stand-in (a shut portal's glass) holds no frame completion, engine readiness or capture.
A world behind glass is observed by the presentation's sight, not by being read (`WorldViewGraphHost.WantSightedWorlds`
through `SdfWorldResidency.Want`): only a consumer whose image renders this frame judges its world's glasses, so the
walk passes on through a window only when its glass is seen or of unknown sight, never through one out of sight, held
small or merely leading (a window's camera stands where its viewer looks through it, and judging from there kept every
world behind every painting wanted). A glass out of sight leads by the walk from its consumer's seat to its facing side,
over the navigation of the seat's delivered world (`WorldPortalReach`, `IWorldSeatNavigation.Reach`, a bounded
`NavigationRuntime.MeasureRoute`), within the glass's world's `views.portalLead` plus the glass's reach, and by the
straight line on its facing side where no domain covers the walk or the consumer is a window. `world.residency`'s
parking line names who last wanted a residency and why (`wanted=seen|unknown|walk@…|line@…|rendered by=`) beside
`read-ago`, `read-by` (the instance whose render or read last observed it) and `wanted-ago`, so a world that never
parks names its holder. A world the seat stands in is read by the seat's own view (`read-by=world`) whatever its
`wanted-ago` says: check `body.where 0`'s `instance:` before calling such a read unwanted. An infinity layer's residency is released
once no output names it (`WorldScreenBinder.ReleaseUnreadInfinityResidencies`): a consumer whose target has not fitted
its sky for `InfinityConsumerRetireSeconds` retires. Never add a `TryWaitIdle` to a release
path; `SdfWorldTables.Dispose` and `IGpuSurfaceUpload.DisposeCompleted` wait for nothing. A render node's build in
flight retires into its runtime's `GpuImageLeases.RetiredBuilds`, which `DrainDisposals` waits out before the device
goes. A package recorder never sees a fence: a result its recording captured (a readback, a publication, a
submission's shading identities) lives in a `RenderGraphSlotResults` and is read only when the node reports the slot
complete (`IRenderGraphPackageReadback.Completed`), which the node does from `WaitSlot` as it reuses the slot or as
soon as an owed slot's fence reads signaled (`ShaderPipelineRenderNode.PollReadbacks`), and before it disposes a
retired graph whose submissions completed. Buffers a recording replaced retire at that slot's next completion
(`SdfMeshOcclusion.Completed`). A reusable slot fence reads signaled after an abandoned submission and after a loss's
drain, so polling one is never a proof.

## The world's shadows

A world's shadows are its residency's, never a view's: `SdfWorldShadows`, owned by the tables, drawn in the
residency's per-frame upload (`SdfWorldTables.SubmitUpload`, its `shadows` pass) ahead of every view's submission, and
read by every view through `SdfWorldTables.CascadesOf` and the layers `SdfWorldPassRecorder.BindPorts` binds. No view
fragment holds a shadow pass or storage (`SdfWorldPackage.NativeFragment`); the [decision](../../../docs/decisions/rendering.md#a-worlds-shadows-are-drawn-once-and-cost-what-moved)
records the options weighed.

- **Anchors.** The near, far and secondary entries belong to anchors, each eye-centred on one view and snapped to cells
  of an eighth of each entry's reach (`SdfMeshShadowCascades.Fit`, `CellOf`, `HalfExtentOf`). Views whose eyes share a
  near cell share an anchor; one elsewhere takes its own up to `SdfWorldShadows.MaxAnchors` while the shadows' fixed
  reservation has room for its layers (anchor 0 always), then reads the nearest (`SdfWorldShadows.Assign`). The distant entry is one for the world, centred on its first shadowed view. A view's pass
  set binds its anchor's layers and rebinds when `BindingRevisionOf` moves.
- **The one door.** `SdfWorldShadows.Decide` decides once for the upload about to be submitted (frozen by `Submitted`
  until the residency's next frame start, `BeginShadowFrame`), `Record` draws exactly that, and `Submitted` commits the
  cameras. Readers read `ReaderCamera`: the camera a redraw completing this upload drew from, else the committed one,
  else none (an entry no submitted upload drew is empty, `Holds` false), and bind `ViewOf`, which names the pair that
  camera drew (a lent pair only from the upload completing its redraw). Never read an entry's camera from a fresh
  `Fit`, or a layer from anywhere but `ViewOf`.
- **Whole redraws.** An entry redraws whole when the light turns past its tolerance (`farShadows.turnDegrees`,
  `distantShadows.turnDegrees`, else `PenumbraShare` of the drawn penumbra and a texel for a caster as tall as the
  reach), its eye's cell changes or its texels change; the distant entry also when `StaticDrawRevision` moves. A
  recording that draws a layer whole calls `CastersRead`.
- **The schedule.** `Want` names each entry's redraw, `Schedule` admits shares under `UploadVertices` in `Priority`
  order (under way, unseen, anchor, far/near/second/distant), `Finish` fixes the work. A share's cost is the entry's
  measured whole (`SdfMeshOcclusion.Measured`, the vertex words at `SdfKernelInterfaces.MeshVertexCountBase`) over its
  shares; the authored far period and distant `frames` are minimums. A standing entry's redraw of several shares borrows
  a pair from the spares (`Lend`, at most two pairs) and swaps it in on its last share (`Submitted`); a redraw of one share
  lands in the upload that draws it, so it draws over the entry's own pair as an unseen entry does. Spares no redraw used
  for `AnchorKeptUploads` uploads are given back, and the spares with the pairs lent never pass two (`KeepSpares`). A redraw that loses its
  texels or allocation ends and returns its pair (`EndRedraw`). Every drawing of one entry (shares, rectangles, glass)
  selects into one `SdfMeshOcclusion.Storage`, which is safe only because an upload records each drawing's stage, select
  and draw before the next stages.
- **Moved rectangles.** `SdfWorldTables.NoteMotion` hands every non-shading change's before and after boxes to
  `SdfWorldShadows.Moved` before the scopes' early return; keep that call first, or a staging whose scopes are all met
  drops the change. `Moved` skips a change whose box lies within the finest judged texel of the box the entries last
  judged the row by (`m_judged`), for a standalone row or a rigid slot (one box its pose carries, decided in
  `SdfWorldTables.DecideMotionSlot`); any other slot is a union that can hide motion, and owes both ends whenever it
  moves. A slot judged rigid that is not would let its casters drift a texel unseen, so a new member kind of a slot
  (a second carry, a fixed member) must leave `rigid` false. With every motion scope met, a rigid slot is not placed
  at all while its pose change bounds its box's displacement from the shadows' judged box (`TryJudged`) under
  `StillDisplacement` (`CarriedBound.Displacement`, counted in `sdf.residency.motion.rows-spared`); its judged pose
  follows `m_judged`, so a new writer of `m_judged` for a slot key must leave a box `TryJudged` answers truthfully. A passed change goes to each entry, which judges the row against its `Footprint` from
  where it last drew it (`MoveTexels`) and owes at most `MaxRectangles` merged rectangles (`OwedRects`); a redraw under
  way also owes its own layers (`m_pending`), handed over when it lands. An upload takes the owed rectangles whose
  expected vertices (the entry's measured vertices a rectangle texel) fit `RectangleVertices`, at least one, and the
  rest wait; a layer owed whole (past half its area, or `MovedEverywhere`) is redrawn by the schedule, never by one
  rectangle in one upload. A decision takes the owed rectangles into its
  work and an unsubmitted decision gives them back (`Restore`). A change no box bounds (`MovedEverywhere`, a seeded
  history) owes the whole layer, and a moved glass caster owes the extinction. A rectangle draws through a viewport of its own from the camera of that rectangle of the plane (aspect in
  `SdfFrameBlock.WriteShadowCamera`), keeping the casters in that box (`SdfMeshCull.OfLightBox`).
- **Layouts.** Every layer rests shader-readable between uploads; the recorder moves it to the attachment layout and
  back (`Entry.Move`). A new layer is cleared before anything binds it.
- **Glass.** An entry's glass extinction (`Optical`) is drawn with each whole redraw, against the front depth, from a
  selection of every draw (`OpticalKind`).
- **Block bounds.** A pair of layers carries its block bounds (`Pair.Blocks`, `frame/sdf-shadow-blocks.hlsli`): a
  fitted plane and the depth range off it for each block of `SdfWorldPackage.ShadowBlockEdge` texels and the filter's
  reach about it, which the surface pass classifies a receiver by before any tap (`sdfMeshShadowClass`: lit, umbra
  or filter, the first two the taps' own answer to the bit). They must describe the layers whenever a view reads the
  pair, and one place keeps that: `Entry.Record` reduces them (`Entry.Reduce`, `passes/sdf-shadow-blocks.comp.hlsl`)
  after the layers rest, over every block in the upload that completes a whole redraw, over the blocks a rectangle
  can change, and the glass flags alone under a redrawn extinction. A new drawing into a pair that views read owes a
  reduction there, or the surface pass classifies by bounds of what the layers held before; a change to what a tap
  reads (the filter's radius `SdfKernelInterfaces.MeshShadowFilterTexels`, the slope or cone clearances, a new term
  in `sdfMeshShadowGap`) changes `sdfMeshShadowClass`'s bounds in the same change. `SDF_SURFACE_OMIT_SHADOW_CLASSES`
  compiles the classes out for a reload, which must leave a paused still unchanged.
- **Memory.** Every layer, block-bounds and extinction buffer is held under one fixed `MemoryAccount` reservation
  (`Hold`, `TryHold`), counted in `SdfWorldTables.TableBytes`; a trim request gives back the anchors no view reads.
- **Evidence.** `world.residency`'s `shadows` line prints the uploads, the measured GPU time of the shadow pass
  (timestamps around it, read when the slot completes), moved changes and how many reached the entries, the bytes held
  and the spares, anchors, each view's anchor and each entry's state: its redraw (`2of5lent`), its last whole redraw's
  measured triangles and shares (`tri…in…`), its rectangles' average triangles and count, what ended its holds
  (`ended-turn…-cell…-static…-fit…`) and the blocks its bounds reduced in how many dispatches (`blocks…in…`); `world.rates` counts `shadow-<entry>.static|share|live|held` per world upload; the selection marks name each
  drawing (`drawn.shadow-far.a0 rect0.*`). Timings belong to the lead's quiet window.

### The distant shadow map

The cascade set's fourth entry (`SdfMeshShadowCascades.DistantEntry`) is authored per device row
(`WorldQualityPreset.DistantShadows`) and at boot (`render.distantShadows`), carried on `SdfFrame.DistantShadows`. It
draws the static scene alone (the selection's `staticOnly`, rejecting `SdfMeshRecord.DynamicFlag`, set from
`SdfMeshDraw.IsStatic`, which a skinned or displaced draw never is), follows no motion, and redraws only for a turn, a cell
or a static change; `StaticDrawRevision` follows casters, not records (`SdfMeshDraw.SameCaster`), and only
`StageChangedDraws` and `Unname` advance it. A new path that changes a static draw's record outside those two leaves a
stale map standing. Because the set's count is four while the secondary entry may be empty, test entries with `Holds`,
never `index < Count`.

## Evidence-preserving diagnostics

`world.debug-view colliders|contacts` uses the beauty path through temporal
resolve, sky, glass, fog, exposure and the display pass's sharpen, tone and encode. Keep
`DebugViewModes.PreservesScene`, `sdfDebugPreservesScene` and the temporal epoch
key together. Diagnostic marks belong only to `AnnotationWriter.EmitDiagnostics`'s separately
reserved overlay channel; its masks protect every causal footprint, including
overflow beyond the sixteen visible pins. Focus removes chroma outside those
footprints without changing luminance. Keep masks exact rather than coalescing
them into spatial bins: those bins show as rectangular chroma islands in focus.
Beyond the mask reservation, conservatively protect overflow and leave focus
off. Place badges beside witnesses with short leaders; collisions overflow to
the report rather than forming a screen-wide leader band. Never paint a diagnostic into history,
the visibility buffer or lighting.

`WorldDiagnostics` separates collision-versus-authored-field from
field-versus-baked-mesh. The latter reuses the irradiance scene's retained
triangle hierarchy only when its exact draw records and delivered document
version match and the bake has settled. The client document is not necessarily
the server's object: compare `WorldDocumentVersion` by value, never their
definition references. `SdfMesh.BakeCell` comes from the actual
contour lattice, survives cache storage and material renumbering, and supplies
the hit triangle's half-cell-diagonal budget. No saved baseline or allowlist is
involved. A second `SdfWorldPicker` per instance asynchronously reads cluster
depth without stealing pointer requests; discard results across camera, mesh,
program or view changes.

`WorldDiagnosticGeometry` must read each compiled collider as physics does.
For `RoundedBox`, `Extent` is the inner box and `Radius` is its spherical
offset: sample its faces, edge fillets and corner fillets, then apply `Rotation`.
Distance queries inverse-rotate and subtract the radius after measuring the
inner box. Treating this kind as an unrotated sharp box creates metre-scale
false findings and also misclassifies buried union surfaces. Compare witnesses
with `FixedSurfaceQuery` and the contact narrowphase. Chamfered authored Boxes
can still yield real inward discrepancies from their conservative inside
colliders; neither the fillet nor the chamfer is an error-budget allowance.

Opposing planar face interiors of touching boxes are inside their union, not exposed collision boundaries; certify that local relation before comparing them with the exterior mesh. Edges and corners stay measured, except against the mesh where no free ball of the hit triangle's half diagonal reaches the witness (`WorldDiagnosticGeometry.OpensOnto`): the fillet groove along an edge three filleted boxes share is in the field and the colliders but under the bake's cell, and counts `enclosed`. Only exact collider distances may close such a void; a bound's blocks a ball only when its centre lies inside. Mesh diagnostics use `WorldDiagnosticMeshQuery` and the retained scene's
`TryNearestSurface`, not normal-ray hits. The nearest query reuses both levels
of the trace hierarchy and tests world-space triangle distances (object-space
nearest points are wrong under nonuniform scaling). It includes edges and
zero-distance witnesses. The search radius is four times the sum of
collision/arithmetic precision and the scene's largest bake half diagonal; only the returned
triangle's own precision enters its budget. A miss is unknown. Grounded checks
require the nearest retained support surface to be walkable; a missing or
unqualified hit is unknown too. `coverage.comparisons` counts red, clean and
unknown witnesses separately from causal findings. Never add a fillet, bound
or snap-reach allowance, or suppress authored-field findings to quiet the mesh
audit. The owning decision is [diagnostic evidence](../../../docs/decisions/rendering.md#a-diagnostic-view-never-covers-its-evidence).

Captures on the synthesized overlaid root retain the beauty image, annotated
image and JSON together. Attach the companion to `WorldRootGraph.OverlayInstance`,
which serves the screenshot, not `ComparisonLiveRoot`, which is the beauty
source. The capture companion waits for both display encoders;
its closure owns pixels and JSON before background encoding starts. Custom
authored roots need an equivalent beauty/overlay split before they can supply
a triplet. See [scene diagnostics](../../../docs/reference/commands.md#scene-diagnostics)
for commands, coverage and the GPU handoff checks.

## Where things live

| Area | Code | Explanation |
|---|---|---|
| Program model and ISA | `src/Puck.SignedDistance` (`SdfOp`, `SdfShapeType`, `SdfBlendOp`, `SdfDomainOp`, `SdfProgram*.cs`, `SdfProgramBuilder*.cs`, `SdfOpRoles`) | [program model](../../../docs/rendering/sdf/handbook/program-model.md), [Lipschitz](../../../docs/rendering/sdf/reference/lipschitz-and-field-correctness.md) |
| CPU interpreter and queries | `src/Puck.SignedDistance/Queries` (`SdfFieldEvaluator`, its `Bounds`, `Cull` and `Certificates` partials, `BakedWorldQuery`) | [queries and determinism](../../../docs/rendering/sdf/handbook/queries-and-determinism.md) |
| Mesh bake | `src/Puck.SdfVm` (`SdfMeshPathBake`, `SdfMeshLibrary`, `SdfMeshStore`, `SdfMeshRegionArena`); `src/Puck.SignedDistance/Baking` (`SdfSampledLattice`, `SdfDualContouring`) | [mesh bake](references/mesh-bake.md) |
| GPU engine | `src/Puck.SdfVm` (`SdfWorldResidency`, `SdfWorldTables.*.cs`, `SdfWorldPasses`, `SdfWorldPassRecorder`, `SdfWorldPipelines`) | [`Puck.SdfVm` README](../../../src/Puck.SdfVm/README.md), [frame rendering](../../../docs/rendering/sdf/handbook/frame-rendering.md) |
| Kernels | `src/Puck.SdfVm/Assets/Shaders/Sdf`: `isa/` (generated), `field/` (the interpreter the bake samples through: `mapCore` in `sdf-map.hlsli`, the part walk in `sdf-parts.hlsli`), `frame/` (the visibility record, the mesh tables, reprojection), `shade/`, `sky/`, `debug/`, and under `passes/` one `*.comp.hlsl` wrapper per dispatch plus the stage modules | [kernels](references/kernels.md) |
| Generated kernel declarations | `src/Puck.SdfVm.Model` (`SdfIsaHlsl`, `SdfRenderHlsl`, `SdfKernelInterfaces`, `SdfSkyKindsHlsl`) and `src/Puck.Shaders.Model` (`ShaderDeclarations`); written by `src/Puck.Shaders.Generator` before any kernel compiles | [sync pairs](references/sync-pairs.md) |
| Cameras and views | `src/Puck.SdfVm/Views`; `WorldViewInstances`, `WorldScreenBinder.*` | [motion and views](../../../docs/rendering/sdf/handbook/motion-and-views.md) |
| World data into frames | `src/Puck.World.Client` (`WorldFramePresenter`, `WorldSceneEmitter`, `WorldPlacementStamper`, `WorldCreationInstances`); `CreationStampEmitter` | `puck-world` skill |
| Shader packages, pipelines, render graph | `src/Puck.Shaders` (`RenderGraphRuntime`, `ShaderPipelineRenderNode`, `ShaderPipelineCompiler`), `build/Shaders.targets` | [shaders reference](../../../docs/reference/shaders.md), [runtime contracts](references/runtime-contracts.md) |
| Backends | `src/Puck.Vulkan`, `src/Puck.DirectX` and their `.Presentation` projects | [contributing: GPU support](../../../docs/development/contributing.md#gpu-support-and-shader-builds) |

Find the existing mechanism before adding one: `puck references`,
`puck declarations` and `puck search -M 0` (the `symbol-analysis` and
`content-search` skills).

## What already does it

The map above answers "where is symbol X". A duplicate is written when the
question is "what does X" and no index answers it, so this table is by
capability. When your capability is in it, read the home before writing a line;
when it is not, search by what the code does, not by the name you expect, and
add the row you found.

| You need to | Existing mechanism | Home |
|---|---|---|
| Draw text in the world: lettering that is relief, a display's own text, or words on a surface that follow state | `SdfProgramBuilder.Text` (marchable `Glyph` shapes, a creation's `textRuns`), a screen's glyph decal (`SdfWorldTables.SetScreenDecal`, `WorldScreenTextDecal`), and a text paint's surface text (`SdfPaint.Text`, `SdfWorldTables.SetSurfaceText`, `WorldSurfaceTexts`); both shade-time tiers sample through `shade/sdf-glyph-ink.hlsli` | `src/Puck.SignedDistance`, `src/Puck.SdfVm`, `src/Puck.World.Client`; [text and glyphs](../../../docs/rendering/sdf/reference/text-and-glyphs.md) |
| Compute or check a lane the builder derives from others (a scale's correction, a repeat's reciprocal, a noise relief's normalization), or ask how the program door classes a lane | `SdfDerivedLanes` (the one function per derived lane, called by the builder and the door), `SdfNoiseDisplacement` (the decoded noise relief), `SdfProgram.LaneContractOf` | `src/Puck.SignedDistance`; [Lipschitz reference](../../../docs/rendering/sdf/reference/lipschitz-and-field-correctness.md#procedural-detail) |
| Weight a relief by regions (no relief over a bowl, a pad, a path) | `SdfReliefMask` (a band, `SdfReliefBand`, over listed region shapes, `SdfReliefRegion`), carried as `SdfOp.ReliefRow` rows before the relief and decoded once by the program's door (`SdfProgram.ReliefMasksAt`); a document's masks compile through `CreationStampEmitter.ReliefMasks` | `src/Puck.SignedDistance`; [sync pairs](references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters) |
| Evaluate the field on the CPU, deterministically (contact, picking, bounds, cull certificates) | `SdfFieldEvaluator`; its GPU partner is `mapCore` | `src/Puck.SignedDistance/Queries`; [sync pairs](references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters) |
| Make a point or box query of a large program cost what stands near it, or prune a program per region | A regional `SdfFieldEvaluator` (`regional: true`, `IndexesRegions`): a tape per cube of the world, recorded by the interval walk. A point query answers what the plain evaluator answers to the bit; a box query (`TryDistanceBounds`, which contact's clearance proofs read) is cut along region faces and answers the hull of its parts, as certain an enclosure but not the same interval, and records the regions it reads so its interval never depends on earlier queries. The server's per-collider fields are regional. A new drop rule belongs in that walk, proved against its enclosing interval, never in a pruner of its own or on a Lipschitz bound, which holds for no single candidate | `SdfFieldEvaluator.Regions.cs`; [region tapes](../../../docs/rendering/sdf/handbook/queries-and-determinism.md#region-tapes) |
| Write or change a primitive's or blend's distance arithmetic, an op's point map or field edit, a scope pop or a material winner once for every reader (fixed-point C#, precise HLSL, the wide C# for normals) | `SdfFieldFormulas` in the closed expression language of `SdfFormula`, named by the catalogue entry's `Formula`, `DistanceFormula`, `Winner` or `Operand` | `src/Puck.SignedDistance/Formulas`; [changing the instruction set](#changing-the-instruction-set) |
| Bake a mesh from the field, or find the one already baked | `SdfMeshPathBake` (the path), `SdfSampledLattice` and `SdfDualContouring` (the sampling and contouring), `SdfMeshStore` (the shared on-disk cache of baked meshes, one set per bake identity) | `src/Puck.SdfVm`, `src/Puck.SignedDistance/Baking`; [mesh bake](references/mesh-bake.md) |
| Judge which way a contour's triangle faces, or hold a vertex move to it | `SdfContourFacing` (the lattice edge a quad crosses, then its corners' patches), read through `SdfContour.QuadAxes` and `Patches`; never vertex normals or a triangle's earlier facing | `src/Puck.SignedDistance/Baking`; [mesh bake](references/mesh-bake.md#contouring) |
| Make coarser levels of a baked mesh, or choose a draw's level | `SdfMeshCollapse` (half-edge collapse, every level a subset of the mesh's own vertices), `SdfMeshDetail` (the level a draw takes) | `src/Puck.SdfVm` |
| Contour a group's field again at a coarser cell, or measure any level's error against the field (bracketed both ways, enclosure, material and normal runs) | `SdfMeshPathBake.ContourLevel`, `SdfMeshFieldLevels` (offline, `puck mesh audit --cpu --field-levels`) | `src/Puck.SdfVm`; [mesh bake](references/mesh-bake.md) |
| Skip drawing what a view, a cascade or the previous frame hides | `SdfMeshBox` and `SdfMeshCull` (host boxes), `SdfMeshOcclusion` (the depth pyramid test) | `src/Puck.SdfVm`; [the dispatch bullets](#which-dispatch-owns-the-code) |
| Put the world's placements into the program a frame draws | `WorldPlacementStamper` (static placements), `WorldCreationInstances` (animated, attached and inhabited creations), `CreationStampEmitter` (a creation's stamp); one shape's chain, field ops, panel, relief and trims are `ShapeEmission.Emit` for both, each path supplying only its `IShapeEmissionFrame` | `src/Puck.World.Client`, `src/Puck.World.Authoring/Authoring` |
| Decide when a frame rebuilds and how it presents | `WorldSceneEmitter` (the composed revision), `WorldFramePresenter` (the `ISdfFrameSource` that delivers it) | `src/Puck.World.Client` |
| Hand a residency a frame every frame without allocating, or keep one past its source's next capture | `SdfFrame.Writer`, the one door that changes a frame after construction: `Write` a frame of the producer's own from its `SdfFrameContent` (per frame) and `SdfFrameSettings` (per setting), or `CopyFrom` another frame. A source's frame is borrowed until its next capture; `SdfWorldResidency` renders its own copy, written into whichever of its two capture writers the tables do not hold. Never `with` a frame on a per-frame path | `src/Puck.SdfVm` |
| Add a camera or another view of the world | the view types in `src/Puck.SdfVm/Views`, `WorldViewInstances` | [motion and views](../../../docs/rendering/sdf/handbook/motion-and-views.md) |
| Run GPU work off the frame (a bake, a relight) | `SdfGpuWork` and its clients, `SdfMeshBakeGpuWork`. A client's per-slot buffers come from the recording (`SdfGpuWorkRecording.HostBuffer`, `DeviceBuffer`), never the buffer factory: that door reserves them (`SdfGpuWorkHold`). A client cuts each job to the allowance its take is handed (`SdfGpuWorkAllowance`: what the charged jobs in flight leave of `SdfGpuWorkBudget.FrameAllowanceSeconds`, the share the frame leaves free, which `WorldBackgroundAllowance` feeds from every node's timestamps) at its kind's `SdfGpuWorkRate`, and while work is queued takes no job priced past it, deciding before it takes anything; never a fixed share. A job past what is left holds the presentation's budget (`SdfGpuWorkBudget.Hold`, `holds:`/`holding:` on `work-budget`): other services queue nothing new until it runs, and over the frame's period it waits for the share to pay its price, so a lattice past the allowance lands behind a busy queue or frames that stay over their period An arrival's bake work (a group drawing nothing yet, or the first detail over its floor) owes the frame and takes the GPU the frame leaves idle between frames, from a quarter of the period up to one period, under its own allowance (`ISdfGpuWorkClient.TryTakeOwed`, `SdfGpuWorkBudget.OwedAllowance`), outside the credit, while ordinary background work keeps the frame allowance; so do the landing of a mesh a staged draw waits for and a seen irradiance volume's first light (`SdfMeshIrradiance.OwesFirstLight`) and the probes a view sees that a lighting jump left stale (`OwesSeenRelight`, ordered first in the worker's list), each a client whose every job is then owed (`ISdfGpuWorkClient.OwesAll`). The residencies' services divide the budget by their display share, never by pump order: each holds a turn (`SdfGpuWorkBudget.Join`, weighed by `SdfGpuWork.Weigh` from `SdfFrame.DisplayShare`) that every budgeted step, owed or not, asks first (`SdfGpuWorkBudget.Takes`, `work-turns` and `turn-holds` on `world.residency`). The budget counts frames background work may have cost (`FramesMissedBehind`, `frames-behind` on `world.residency`) | `src/Puck.SdfVm`, `src/Puck.World.Presentation/WorldBackgroundAllowance.cs`; [the bake's dispatch](#which-dispatch-owns-the-code) |
| Have the frame thread take work up while it waits between frames, or wake it for a GPU completion | `HostWaits.Serve` (a `WaitService`, served by `HostWaits.Pace`) and `IGpuSubmissionFence.SignalOnCompletion`; never a poll loop or a thread of your own that records. A wait inside the frame's open batch (a residency's staging, `SdfGpuWork.Drive`) hands its submissions off first (`IGpuSubmissionFence.HandOff`), or the completion it sleeps on never comes before the flush | `src/Puck.Abstractions/Waiting`, `src/Puck.Abstractions/Gpu/Device` |
| Compute irradiance, and check it on the CPU | `SdfMeshIrradiance`, whose CPU reference shares one probe lookup with the relight and the views shader | `src/Puck.SdfVm`; [mesh GI](references/mesh-gi.md) |
| Meter a view's picture for its exposure (histogram, percentiles, dead band, cut snap) | the composite's histogram (`passes/sdf-composite.comp.hlsl`), read back by `SdfSkyRecorder`, reduced by `SdfExposure.Meter`, followed by `SdfViewExposures` | `src/Puck.SdfVm`, `src/Puck.SignedDistance`; [the decision](../../../docs/decisions/rendering.md#lights-screens-and-the-camera-are-photometric) |
| Convert an image source's color or encode a display output | `ImageSourceConversion` and the display encode, each paired with its HLSL | `src/Puck.Abstractions/Sources`; [sync pairs](references/sync-pairs.md#image-sources) |
| Encode or decode the sRGB curve, or turn an authored hex colour into light | `SrgbTransfer` (extended, the twin of `Shared/transfer.hlsli`); `HexColor` parses to a `DisplayColor`, whose `ToScene` makes the `SceneColor` shading reads | `src/Puck.Maths` |
| Convert a view's scene light for the display (sharpen, bloom, tone curve, encode) | `SdfWorldPackage.WithDisplay`, its `display` pass (`passes/sdf-display.comp.hlsl`, `Shared/display-transform.hlsli`) and `SdfOpticsRecorder` | `src/Puck.Shaders.Model/Graph`, `src/Puck.SdfVm` |
| Share a constant or a layout between C# and HLSL | a generated declaration (`SdfIsaHlsl`, `ShaderDeclarations`) first; a hand pair only where the table lists one | [sync pairs](references/sync-pairs.md) |
| Add a pipeline variant to a pass | `GpuPassPipelineKey`: a field that changes the pipeline enters the key, or leases share the wrong pipeline | `src/Puck.Shaders/Pipeline` |
| Let an author program how a surface shades | `SdfShadeProgram` (the op table, admission, cost) | `src/Puck.SignedDistance/Shading`; [authored shading](references/authored-shading.md) |
| Find where two surfaces, objects, folds or materials meet in a view, or draw a line there | The view's edge search (`sdfViewsGatherFilter`, `sdfViewsEdgesWord`), read by a program as `edge.*` and drawn by its ink phase through `sdfShadeInkPhase`; an object is `SdfMeshDraw.ObjectKey`; the looks are `worlds/standard/ink.puck` | `passes/sdf-views-filter.hlsli`, `src/Puck.SdfVm.Model/SdfShadeHlsl.Invocation.cs`; [the handbook](../../../docs/rendering/sdf/handbook/lighting-and-shading.md#ink) |
| Read how a device can back a large buffer (sparse binding, reserved resources, memory priority, pageable device-local memory, storage-buffer array indexing) | `GpuDeviceCapabilities.Backing` (`GpuBackingSupport`) and its native figures, read at device creation and printed on `world.counters gpu`'s `capabilities` line | `src/Puck.Abstractions/Gpu/Device`; [streaming plan](../../../docs/plans/streaming-and-memory.md#the-prototype-as-built) |
| Make a kernel variant depend on something only some devices have (ray queries), or withhold it to run the fallback | `GpuOptionalCapabilities`, read into `GpuDeviceCapabilities.Optional` at device creation (Direct3D 12's options 5 tier, Vulkan's optional extensions in `VulkanLogicalDeviceFactory`); a variant names it in `SdfKernelSet.RequiresOf`, `SdfWorldPipelines.Acquire` leases it only where `SdfWorldPipelines.Runs`, its caller asks `SdfWorldPipelines.Leases`, `VulkanShaderCapabilities.Optional` admits its SPIR-V capability on that device alone, and the World's `--withhold-gpu <capability>` leaves it off | `src/Puck.Abstractions/Gpu/Device`, `src/Puck.SdfVm` |
| Build, compact or bind an acceleration structure the ray hardware walks | `GpuDeviceServices.AccelerationStructures` (`IGpuAccelerationStructures`: size, create, scratch, `GpuAccelerationStructureInstance` records, the descriptor of a `GpuBindingKind.AccelerationStructure` binding) and the recorder's `BuildAccelerationStructure`, `WriteCompactedSize` and `CompactAccelerationStructure`, which carry their own barriers; a buffer a build reads declares `GpuBufferUsage.AccelerationStructureInput`. Both backends refuse every call on a device without `GpuOptionalCapabilities.RayQuery` | `src/Puck.Abstractions/Gpu/Rays`, `DirectXGpuAccelerationStructures`, `VulkanGpuAccelerationStructures` |
| Read what the device lets the process use per segment (a DXGI segment group, a Vulkan heap), or charge a device allocation to the physical memory it was placed in | `IGpuDeviceContext.MemoryReading` (refreshed by `RefreshMemoryReading` on the frame thread), `GpuMemorySegments` (the one place a segment's pool is decided), `GpuDeviceMemoryWork.CountAllocated`'s segment argument (Direct3D 12's through `DirectXDeviceMemory.SegmentOf`, Vulkan's through `GpuMemorySegments.OfMemoryType`); `MemoryAccount.ReadPools` and `host.memory` print them | `src/Puck.Abstractions/Gpu`, `src/Puck.Hosting/Jobs/MemoryAccount.cs`; [streaming plan](../../../docs/plans/streaming-and-memory.md#step-1-as-a-brief) |
| Grow or shrink a large device buffer without holding a second copy, or give back its unused interior | `IGpuMappedBuffer` through `IGpuBufferFactory.TryCreateMapped`, used through a `GpuRegion` created `mapped` (`Grow`, `Shrink`, `MapRange`, `UnmapRange`) | `src/Puck.Abstractions/Gpu/Residency`, `src/Puck.DirectX/DirectXGpuMappedBuffer.cs` |
| Reserve device memory before allocating it, share a pool between residencies, or hold detail to a budget | `MemoryAccount.ReserveDevice` (a holder's peak, under the owner its objects are named for, `GpuObjectName.MemoryOwnerOf`), `GpuDeviceMemoryWork.TryAdmit` (the one door every native allocation asks; `CountAllocated` takes only its `GpuMemoryGrant`), `GpuMemoryAdmissionScope` (opened around the synchronous allocation of a replacement its holder can do without, so a full pool refuses it instead of admitting it past the limit), `SdfResidencySelector` (the one detail bias, each bake's budget, each mesh region's reservation, `SdfGpuWorkBudget`); never a share of `GpuMemoryProfile` | `src/Puck.Hosting/Jobs/MemoryAccount.cs`, `src/Puck.Abstractions/Gpu/Counters`, `src/Puck.SdfVm/SdfResidencySelector.cs`; [streaming plan](../../../docs/plans/streaming-and-memory.md#step-3-one-reservation) |
| Read where frame time goes | `world.counters gpu`, `world.gpu-timing`, the bake's `gpu-work=[…]` line | [performance and cost](#performance-and-cost) |
| Know which draws a raster pass selected and at which level (triangles drawn by level and tier, mesh bytes no pass selected) | The occlusion kernel's selection marks, taken only while a reading asked (`SdfWorldTables.MarksSelections`), one stage in `SdfWorldTables.SelectionMarkFrames`, read back with the portal sight verdicts through the slot's one readback (`SdfMeshOcclusion`) and read against the row epoch (`SdfWorldTables.ObserveSelectionMarks`); `world.residency`'s selection line | `src/Puck.SdfVm` |
| Show a world from far away without holding it (a planet from its moon, a neighbour across a seam) | The world's far product (`SdfFarProduct`, built by `SdfMeshCluster` from a settled residency's static draws), drawn by a sky `view` layer with `farProduct: true` through `WorldFarProductEmitter`; products are published and judged by document version in `WorldScreenBinder.Device.Far.cs` | `src/Puck.SdfVm`, `src/Puck.World.Presentation`; [decision](../../../docs/decisions/rendering.md#a-far-viewer-draws-a-worlds-far-product) |
| Let one residency serve viewers of several disclosures (a window whose session is withheld some placements), or keep a draw out of every view but one audience's | A draw's audience tag (`SdfMeshDraw.Audience`, from its rigid copy's `SdfMeshInstance.Audience`, staged at `SdfMeshRecord.AudienceShift`) and a view's audience (`SdfViewSnapshot.Audience`), filtered in the selection kernel from the controls `SdfMeshOcclusion.Stage` writes (its audience argument has no default; a window, mirror or portal seen through a home view takes that view's audience, and cascades select their own viewers' draws only while every view is theirs, `SdfMeshAudience.NeutralOnly` otherwise); a tagged draw is never static, so the far product, the irradiance volume and the distant map hold none. The World's side: `WorldSceneProof.SharesContent`, `WorldPlacementAudiences.Varies`, `WorldAudienceOverlay`, `WorldRigidAssets.AudienceRefusal` | `src/Puck.SdfVm.Model/SdfMeshAudience.cs`, `src/Puck.World.Presentation/WorldScreenBinder.Device.Session.cs`; [decision 10](../../../docs/plans/streaming-and-memory.md#decision-10-a-world-is-resident-once-and-an-instance-is-an-overlay-on-it) |
| Show something where a group's detail has not landed or was refused | The group's floor: planned first on an arrival in view (`SdfMeshDemand.Sees`; one out of view bakes its detail directly, behind the groups in view), kept while detail draws, evicted to on refusal (`SdfMeshPathBake.Floor.cs`, `Group.KeptFloor`); `world.residency`'s floor line | [mesh bake](references/mesh-bake.md#groups-poses-and-materials) |
| Name a backend's compiled-kernel extension, or its target from an extension or some bytecode | `ShaderBytecode.FileExtension` (the extension), `ShaderTargets` (`Of` a backend or bytecode, `FileExtension` and `DeployedPath` of a `ShaderTarget`); never a `.dxil`/`.spv` ternary or string compare. A kernel-loading API (`SdfKernelSet.Load`, `SdfWorldPipelineCatalog.LoadDeployed`, `GpuRegionCopyPass`, `SdfMeshRasterPass`) takes a `ShaderTarget`, never an extension string | `src/Puck.Abstractions/Gpu/Shaders`, `src/Puck.Shaders.Model/Compiler/ShaderTarget.cs` |
| Place several images into rects of one output in one pass, or let a package pass leave trailing inputs unbound | `RenderGraphPackageCatalog.PlaceViews` (the `place` kernel's body, `place.hlsli`, over up to `PlaceSourceCount` sources, recorded by `PlacePackage`, each source placed by its version's name through `IRenderGraphPlacements`), which `WorldRootGraph` places every view and pane through; `RenderGraphPackage.OptionalInputs` | `src/Puck.Shaders/Graph/PlacePackage.cs`, `src/Puck.Shaders.Model/Graph/RenderGraphPackages.cs`; [shaders reference](../../../docs/reference/shaders.md#the-default-root-graph) |
| Build a render-graph package's one cached pass pipeline and recover it in `Create` | `PassPipelineBuild.AcquireAsync` and `Of` | `src/Puck.Shaders/Graph` |
| Let a package recorder record on a job-pool worker, or change state outside its instance before it does | `IRenderGraphPackageRecorder.PrepareRecord` (frame thread, before the wave forks) and `RecordingDomain` (what recordings that must share a thread name: an SDF view's residency through `SdfViewRecorder`), grouped by `RenderGraphRuntime.RecordWave` | `src/Puck.Shaders/Graph/RenderGraphRuntime.Waves.cs`; [runtime contracts](references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader) |
| Spread one node's passes across command lists recorded on several threads | `IRenderGraphPackageEmitter` (`Decide` in pass order, `Emit` on a lane), the node's walk and lanes (`ShaderPipelineRenderNode.RecordPasses`, `EmitLane`, `FrameSlot.Lists`), the wave's emission fork (`RenderGraphRuntime.EmitWave`), lane counts (`GpuWorkLedger.EnterLane`) | `src/Puck.Shaders/ShaderPipelineRenderNode.Emission.cs`; [runtime contracts](references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader) |
| Keep a graph replacement from holding two whole graphs, or decide whether the budget may refuse it | `ShaderPipelineRenderNode.CarriedStoragesOf` (the one door a replacement takes over the installed graph's storage through, matched against the instances' own extents and sizes), `ReplacementGivesWay` (a graph whose counts moved cannot render, so its replacement is the instance's least and only a cap refuses it), `InstancesOf` (history only compute touches keeps two instances); `pipeline.inspect <instance>` reads the result | `src/Puck.Shaders`; [memory budget](../../../docs/reference/shaders.md#memory-budget) |
| Read a presented surface back to CPU pixels in a backend's presenter | `PresentationCapture` | `src/Puck.Shaders` |
| Size a bake lattice over a box at a cell, or find a sample's point | `SdfMeshLattice` (`Dims`, `Over`, `PointOf`) | `src/Puck.SdfVm` |
| Read a slot's pose the way the bake does (unset orientation is the identity), or carry a part's or group's box through one | `SdfMeshGeometryBuilder.PoseOf`, `SdfMeshBox.OfPart` (a part in its group's frame) and `SdfMeshBox.Carried` (a slot-frame box to world); never an inline `Orientation == default` test | `src/Puck.SdfVm` |
| Pack or unpack an octahedral normal as signed-normalized N-bit fields | `SdfOctahedral.PackSigned`/`UnpackSigned`/`SignedScale` | `src/Puck.SignedDistance/Illumination` |
| Divide by a fixed divisor, mix or hash packed keys, or compare keys, inside bake or presentation code | not rendering: the `Puck.Maths` primitives, which `maths-usage`'s Choose table lists | [`maths-usage`](../maths-usage/SKILL.md#choose); `puck scan --only duplicates --against <base>` before landing |

## Which dispatch owns the code

A view is a chain of separate dispatches: `mesh` (graphics, in two occlusion
phases with the occlusion kernel between them, `SdfMeshOcclusion`) → `pyramid` (the ambient occlusion's
depth pyramid from the mesh target) → `surface` (each pixel's visibility record from the mesh
target: normal, screen-space AO, the world's shared cascades' soft shadows into K;
`passes/sdf-surface-stage.hlsli`) → `views-probes` (the irradiance volume's
gather once a cell, `passes/sdf-views-probes.hlsli`) → `views` (materials and lighting) →
`resolve` (reduced or temporal views) → `sky` → `composite`. A view of a
program with a glass shape (`SdfProgram.HasGlass`) inserts `glass-clear` →
`glass` (graphics, the glass draws alone) → `glass-shade` after `views`
(`SdfWorldPackage.WithGlass`), and every later pass reads `glassLit` in the
lit image's place. A view of a frame that runs authored programs inserts
`views-filter` → `views-bins` before `views`, which then shades in bins
(`SdfWorldPackage.WithBins`, [authored shading](references/authored-shading.md#the-three-views-and-glass-kernels)). The surface and
views passes share one head and entry (`passes/sdf-pixel-pass.hlsli`,
`passes/sdf-pixel-entry.hlsli`) and call no field query. The table in
[kernels](references/kernels.md#the-frame) says what each writes.

The mesh bake's kernels (`sdf-mesh-bake-mask`, and the two samplers that share
`passes/sdf-mesh-bake-stage.hlsli`: `sdf-mesh-bake` for values and
`sdf-mesh-bake-gradient` for gradients, each one `mapCore` call site) are no pass of a view: they run as the residency's
background GPU work (`SdfGpuWork`, its bake client `SdfMeshBakeGpuWork`), a
ring of slots each submitted on its own fence and refilled when a slot's job
completes and its readback is released, stepped on the frame thread at the top
of each frame, after each upload, and inside the host's wait between frames
(`HostWaits.Pace`), which a job's fence, a client's offer and the budget's
next credit end early. A job binds the latest upload's World set
with its own point table and runs only while the tables' uploaded program is
the bake's (`SdfWorldTables.BindsBackgroundWork`, `BackgroundProgram`).
`world.gpu-timing` does not see it; the bake's done line carries its
`gpu-work=[…]` counts, each job's GPU time measured by its own timestamps.

- **AO and soft shadows are estimated in the surface pass and filtered in
  views.** The estimates (`sdfMeshAmbient`, `sdfMeshShadow` and their shared
  4×4 tap pattern `sdfMeshPattern`, `passes/sdf-surface-stage.hlsli`) are the
  surface pass's, written into the visibility record; views only averages them
  over a 4×4 window (`sdfViewsFilter`, `passes/sdf-views-filter.hlsli`) before
  shading. A wrong occlusion or shadow value (a dark band, a missing crease,
  acne) is the estimate's, so an edit to the views lighting code changes
  nothing; the window and the pattern's tile are a pair. A side is queried
  wherever the light's diffuse response is non-zero, which for a wrapped
  material reaches past the terminator to `n·l > -wrap` (`sdfMaterialShadowDemand`);
  such a side reads the cascade's exit layer (below), or the wrap would shadow
  itself away in the open. A side the response does not reach is never queried: its slot holds `SDF_SHADOW_UNQUERIED`
  (`frame/sdf-shadow-visibility.hlsli`), which decodes as fully visible, and
  the window's shadow average takes only queried pixels (`sdfShadowQueried`).
  An unmeasured slot averaged in lights a one-pixel line along every
  terminator and rounded edge the cascades shadow (indoors, a cosine just
  above zero under the 100 klx sun), and no debug view shows it, since
  `shadow` shows the visibility without its cosine. The average also leaves out neighbours whose normal
  turns away, so a crease's two lit faces keep their own shadows. A sunlit spot on a wall's top under a slab is the
  filter's (`sdfMeshShadowGap`): a tap counts the light hidden only inside the
  light's cone over the point, and the penumbra is the nearest in-cone
  blocker's. `world.debug-view ao` shows the filtered occlusion and `world.debug-view shadow` the key light's
  answer as its walk reads it (`sdfDebugKeyShadow`: grey facing the light, amber where the probes answer, violet on a
  queried side past the terminator, dark blue unqueried), and `world.ao` toggles the occlusion live. The shadow view
  runs the probe lookup (`sdfViewsProbesWanted`) and the occlusion filter, since the probes' answer it shows reads
  both; a view mode that reads the probes without asking for the lookup reads another frame's slots. Judge a lit wall
  only by grey: a wall's own room face reads visible through the exit layer whenever the sun is behind it.
- **A cascade has two layers.** Each cascade pass draws its depth (every face,
  `SdfMeshRasterPass.ShadowDescription`) and its exit layer (faces toward the
  light culled, `SdfMeshRasterPass.ShadowExitKey`) into its two outputs. A triangle the
  surface pass sees turned from the light compares against the exit layer, so
  its own body lets a wrapped material's light through while any other solid in
  front shadows it; one facing the light keeps the depth, which holds no wall's
  far face to leak light along the wall's foot. Winding says where light leaves
  only for a draw that encloses a solid (`SdfMeshDraw.EnclosesSolid`); any other
  draw (an open authored sheet, a portal-clipped body, a mirrored draw) enters
  the exit layer whole, through the depth's cull-none pipeline, so it still
  shadows a wrapped side behind it. A receiver turned from the light offsets
  straight toward the light: an offset along its reversed face normal reaches
  `1 / (n·l)` times as far and passes a near blocker. A pipeline description field
  that changes the pipeline must enter `GpuPassPipelineKey`: the cache shares one
  pipeline per key, so a lease differing only in an unkeyed field silently gets
  the other's pipeline (the exit layer then holds the depth's faces).
- **Every raster pass culls draws by a host box** (`SdfMeshBox`, `SdfMeshCull`,
  tested by `frame/sdf-mesh-selection.hlsli` through `SdfMeshOcclusion`): a
  view's frustum, a
  cascade's light box and the receivers it shadows. A box is the mesh's
  position bounds through the draw's matrix and carry, so a draw whose vertex
  stage moves vertices any other way (members' slots, a displacement) must have
  no box (`SdfMeshBox.TryOf` returns false) or it vanishes while visible. A
  missing object that `world.cull off` brings back is the cull's;
  `gpu.vertices` in `world.counters gpu` counts what each pass draws. The same
  box chooses each kept draw's level of detail (`SdfMeshDetail`): the view's by
  its error in display pixels over the box's nearest depth, with the 1.1-pixel
  band kept per draw in the GPU state, a cascade's by its texel. The residency
  uploads bounds and level metadata once
  in each changed mesh record. A draw riding its frame slot (`SdfMeshDraw.RidesSlot`:
  every draw with a frame slot, whose matrix is the slot's pose at its scale)
  keeps its scale as its pose and its box in the slot's frame
  (`SdfMeshRecord.SlotBoxFlag`); the kernels pose it from the slot's row
  (`sdfMeshPoseOf`) and derive its world box the same way (`sdfSelectionBox`), so
  a body that moves rewrites its slot's row and no record. The host judges it by
  `SdfMeshBox.Ridden`, the box's twin, held per row as last judged
  (`SdfWorldTables.RowSelection`) and moved as its slot moves (`CollectMovedSlots`, `DecideMotionSlot`).
  A moving draw riding its slot (`SdfMeshDraw.PlacedBySlot`) keeps its scale and
  no pose: every CPU reader places it through one door, `SdfMeshDraw.PlacementIn`
  (the frame's transform rows), and `SdfMeshDraw.FixedPlacement`, which a static
  scene reads, refuses it. Such a draw compares equal wherever its slot moves, so a
  body that only moved writes no draw and no row (`SdfMeshDresserSlotLawTests`);
  a moving rigid copy's pose moves no `SdfMeshInstance.Revision` either. A cut
  of a rider's pose history (`SdfMeshObservation.PoseCut`) reseats its slot's
  previous row (`ReseatRiddenSlot`). Every raster pass uploads 224 bytes of controls
  and records one counted indirect draw per phase. A draw with no
  box draws its merged mesh, so a skinned body never coarsens. A shape that
  changes when `world.detail 0` is set is the level's.
- **The mesh pass also culls by occlusion** (`SdfMeshOcclusion`,
  `passes/sdf-mesh-occlusion.comp.hlsl`, the default `world.cull occlusion`):
  phase one draws the previous frame's visible set through a counted indirect
  draw (`IGpuRecorder.DrawIndirectCount`), the kernel builds a farthest-depth
  pyramid from the target's fourth channel (forward depth) and tests every kept
  box, phase two draws the newly visible over phase one's target and depth. The
  recorder moves the mesh target between the phases itself and returns it in
  its attachment layout. The early occlusion dispatch binds a sampled filler;
  only the late set binds the target after its transition. Vulkan requires the
  descriptor's image layout even when the selected kernel branch never samples
  it, and rewriting one shared set changes both recorded dispatches.
  Draws a kernel issues are counted on the GPU past the
  counter rows (`GpuKernelCounters.DrawnWords`), so `gpu.draws` and
  `gpu.vertices` include them. Cascade selection emits entry and exactly one
  exit command per kept opaque draw: solid exits use front-face culling, thin/clipped
  exits the unculled pipeline. Glass emits once in its extinction phase.
  Count those emitted commands, without another
  selection or history write. Count entry and exit with separate wave reductions:
  64 lanes of a maximum mesh's vertices fit a uint sum, twice that does not;
  `puckAddWork` carries each addition into the 64-bit accumulator.
  `CountingRecorder.DrawIndirectCount` counts the API call,
  not its unknown draw/vertex totals. An object `world.cull frustum` brings back is the
  occlusion's. The identity is a root-constant argument on Direct3D 12 at every
  execute-indirect tier (`DirectXPipelineLayout.DrawSignature`): numbering commands with
  tier 1.1's incrementing constant (`execute-indirect-tier` on the capabilities line) measured
  no faster on any shadow pass and drew more vertices for the same draws. Vulkan reads it
  from the
  argument buffer through its pushed word base plus `DrawIndex`
  (`sdfMeshRasterIdentity`); first instance stays zero. When
  `VulkanGpuRecorder.CanDrawIndirectCount` is false, the recorder issues those same initialized
  slots in one fixed `vkCmdDrawIndirect` when multi-draw is available; otherwise
  it advances the word base for each individual slot, including empty rejected slots;
  no raster CPU cull or level path remains. Keep command-to-vertex read barriers and
  preserve level state when buffers grow. Compute binds the read-only command
  alias to the dummy, while raster binds its real command buffer; binding it
  alongside the compute writer would demand conflicting resource states.
  `SdfMeshCull.Keeps` remains a live host query: `WorldCreationInstances.Rates.Shown`
  uses it to keep an off-camera body's pose updating when its shadow is visible.
  It does not select raster arguments. Include that downstream World consumer
  when checking references; the SdfVm test project's dependency closure alone
  cannot establish that a helper is unused.
- **Reprojection interpolates displacement.** `sdfPreviousPoint` in
  `frame/sdf-reprojection.hlsli` adds the triangle's weighted vertex movements
  to the current hit. Unchanged vertices return that hit exactly before any
  barycentric calculation: a long, thin triangle's float Gram determinant can
  cancel to zero, and rebuilding a second world position can invent motion on
  a still surface. `SdfReprojectionDeviceLawTests` includes this thin-triangle
  witness on both backends. Check the static `motion` view when history fails
  to accumulate; unchanged geometry must show valid, zero object motion.
- **A receiver's depth belongs to the output ray.** Different jittered rays
  hitting one grazing plane can have very different ray distances. Project each
  tap's tangent plane onto the output pixel's unjittered ray before storing or
  validating its receiver depth (`sdfHistoryReceiverDistance`). Identity and the
  relative depth tolerance stay unchanged. The reprojection device witness
  maps two samples at distances 64 and 32 onto the same receiver depth 128.
- **Stationary reconstruction keeps a complete jitter window.** The resolve's
  stationary window retains each phase's color, coverage, transport and weight.
  It is one retained buffer the resolve updates in place (a mutable input of the
  fragment's own, `SdfWorldPackage.HistoryWindowWritten`), not two-copy history:
  every access to it goes through `sdfHistoryWindowWord`, which takes the
  invocation's own id, and a pixel keeps its window only while its history comes
  from its own pixel (`historyPixel == pixelIndex` in `stationary`). Reading a
  window at a reprojected position would read words another invocation of the
  same dispatch is writing; a moved pixel restarts its window instead. The graph
  admits such a buffer only when the updater's one mutable input is the only
  reference any pass makes to it (`ShaderPipelineCompiler.UpdatesInPlace`), so a
  second reader of the window is refused; a graph replacement keeps the window
  and the two history rings together or restarts all three
  (`ShaderPipelineRenderNode.PreserveCarriedStorages`).
  Replacing one phase and summing in fixed order removes periodic newest-phase
  bias and keeps samples the current neighborhood misses. A complete window is
  summed in full only once a period, on its 8 by 8 group's turn; on the other
  frames it keeps its mean and replaces the one phase's term in it (the
  replacing branch of `sdfResolveTemporal`). That branch rests on three things
  a change to the resolve must keep: a full stored mask means the frame before
  stored the window's mean at this pixel (the history color, the transport
  words); the window's first word holds the sum of the phases' packed weights
  above the mask (`sdfWindowSampleWeight`, `sdfWindowWeight`); and the turn is
  uniform over a group, since a turn chosen by pixel puts a summing lane in
  nearly every wave and saves nothing. Moving and reactive
  pixels use the clipped resolve while their window fills. A stationary window
  keeps its validated receiver while the footprint still sees it; choosing a
  new thin adjoining face each phase can reject the window on the next phase.
  The window describes an output footprint, which can sample different receiver
  identities on opposite jitter phases at a static silhouette. If ordinary
  receiver validation rejects it, retain the bounded window only after proving
  the unjittered camera is exactly unchanged, every tap is nonreactive, and each
  visible tap's previous point equals its current point. This extra motion check
  runs only for rejected footprints. A displaced camera keeps ordinary receiver
  rejection; a stationary current receiver alone does not prove no disocclusion.
  The package owns
  `TemporalPeriod`, `HistorySurfaceWords` and `HistoryWindowWords`; the generator
  carries them into HLSL. A layout change needs a host with the matching history
  allocation before loading its kernels. Account for this output-sized history in
  GPU memory: the surface and the color in two copies, the window in one.
  Keep the surface and window planar, every access through `sdfHistoryWord`:
  interleaved, a pixel's words put each lane of each access on its own sector and held
  the resolve at L2 bandwidth. A complete window shows its own mean, so only an
  incomplete one reads the reprojected Catmull-Rom history.
  Preserve a constant coverage component exactly before half packing and after
  averaging the phases: bound the convex mean by its samples. A rounded value
  just below one becomes 0.9995 in half storage and invents a background share
  on an opaque surface. The analytic resolve device witness checks full coverage.
- **Coincident exposed faces cannot accumulate a stable receiver.** Check
  `world.debug-view material-id` when a static patch changes identity or never
  completes its phase window. Separate meshes sharing an exposed plane can
  alternate winners as jitter or a refresh changes rasterization. Fix the
  authored overlap, rather than weakening receiver rejection. A slab owns its
  horizontal finish; a wall or lintel meeting it ends at the slab's underside.
- **A seat's character fade is view-local.** `SdfViewSnapshot.StampFade` supplies
  a transform-slot range and opacity through `SdfFrameBlock`; the mesh and glass
  fragments call `frame/sdf-stamp-fade.hlsli` before writing depth or glass sums.
  Keep view-only offsets in `SdfFrameBlock`, separate from the bake-only
  `SdfMeshBakeBlock` initializer, so view edits do not invalidate meshes.
  Keep the opaque fragment free of `earlydepthstencil`: discarded pixels must
  reveal the room. Shadow passes and independently filmed mirror/portal views
  receive no fade. Vulkan requires `shaderDemoteToHelperInvocation` at device
  creation and capability 5379 in `VulkanShaderCapabilities`; `clip` compiles to
  that feature even when the live opacity is one.
- **A view can pose one stamp its own way.** `SdfViewSnapshot.PoseOverlay`
  (`SdfPoseOverlay`: first slot, slot count, first overlay row) makes that view
  read a stamp's dynamic-transform slots from other rows of the same table: the
  pass block's `poseOverlay`, applied by `sdfViewPoseSlot` (`frame/sdf-mesh.hlsli`)
  to every skin member read (`sdfMeshSkinSlotsOf`), to the frame slot a draw rides
  (`sdfMeshPoseOf`, `sdfMeshPreviousPoseOf`, so the view poses the draw from the
  overlay row), and to a hit's shading frame
  (`sdfMeshHitFrameSlot`, `sdfMeshSurfaceNormal`). Only a seat's own view carries
  one, for its guess layer (`WorldCreationInstances.GuessOverlay`); a new reader of
  a mesh vertex's or a hit's slot goes through `sdfViewPoseSlot`, or that view draws
  the shared pose there. A light anchored to a slot, a mirror's or portal's own
  view and the host's anchors read the shared rows. The GPU culls a rigid draw by
  the box its view poses it at (`sdfSelectionBox`); the motion scopes judge it by
  its shared pose's box, and an overlay row that moves meets that view's scopes
  whole.
- **Every light answers through one interface,** `shade/sdf-light.hlsli`; a new
  light kind is a branch there and nowhere else. Views and glass visit lights
  through one walk (`sdfLightWalkStart`, `sdfLightNext`) over two masks
  (`SdfLightMasks`): the set bits of the light mask, then of the screen mask,
  so a light a mask leaves out costs no iteration. Glass walks the view's
  masks (`sdfViewLightMasks`); views walks each 8×8 tile's
  (`sdfViewsTileLights`, `passes/sdf-views-lights.hlsli`): the view's lights
  whose range sphere (and spot cone) meets the boxes about the tile's hit
  points, the non-black screens the boxes reach in front of, and of the lights
  the volume traces (`sdfLightTraces`) only those the probes about some shaded
  pixel of the tile see (`sdfViewsLightsSeen`). A mask
  may leave out only a light that adds nothing at the points its lanes shade,
  so a new light kind or reach (an authored falloff past `Param`, a screen
  that lights behind its face) widens the tile test in the same change, or
  tiles cut its light off. The probes' half rests on one rule the light
  phase's door enforces: a traced light whose probe share is zero adds nothing,
  whatever the program (`SdfLightInputs.unseen` in the generated
  `sdfShadeLightPhase`), so a change that lets an unseen traced light add
  anything (a new response term, a door that skips the test) widens
  `sdfViewsLightsSeen` in the same change. The unsplit kernels decide a tile in
  their own group; a binned view's filter pass decides it and writes the masks
  into the shade bins (`sdfShadeTileLightsWord`), which the binned kernels read,
  so they hold no barrier and shade a pixel from its records alone. The filter
  pass tests no screen's binding, since a source binds without moving the
  lighting it stands on; the walk asks. `SDF_VIEWS_OMIT_LIGHT_TILES` reads the
  tiles' saving. Light visits are counted by reloading a scratch tree that adds
  to `sdfWorkGradients` in the walk and reading `shapes.gradients` on
  `world.counters gpu pass sdf.world$views`. Index a vector at
  run time only through `sdfComponent`.
- **Cel shading is a style per material, not a whole-image filter, and the
  banding is a program.** The standard's `cel` shade program
  (`worlds/standard/shading.puck`) is the one implementation of the band; no
  kernel bands. A palette row's `toon` (`SdfMaterial.Toon`, material row 22) is
  the styled mark the engine keeps beside it, and nothing else.
  Every other material inherits the world's default (`render.toon`) through the
  pass block's `toonMode` (`SdfToon.Mode`), read by `sdfToonStyled` and
  `sdfToonAuthored` (`shade/sdf-toon.hlsli`). `world.toon off` writes a
  negative default mode, and `sdfShadeBinding` then marks a styled material's
  binding unstyled: `sdfShadeHas` reads its light, compose and ink phases as
  omitted while its surface phase (a terrain under cel) still runs.
  A program that omits the surface phase keeps the material's layers
  (`sdfShadeHas(binding, 0u)` in both stages; `SdfShadeProgram.Authors` on the
  CPU, which `SdfAppearance.ResolveShadeReads` reads). The program's result is
  scene light for the display pass's one `render.tone`; never expose or
  compress inside a material.
  Keep screen pictures, portal pictures, sky and self-emission out of both the
  banding and ink. The tuning belongs to the lighting cadence signature, never
  the bake closure.
- **Ink is a program's fourth phase over edges the view searches once.** The
  one edge search is in `passes/sdf-views-filter.hlsli` (`sdfViewsGatherFilter`
  seeds, `sdfViewsEdgesWord` measures): the filter pass of a binned view writes
  each pixel's edges as the filter record's third word, and an unsplit views
  kernel searches in its own group. Seeds lie between neighbouring pixels that
  jump (occlusion by a tangent-plane test, crease, object key, material) and
  live as bit masks a tile row and column in group memory; a pixel's distance
  is a bit scan down its column and along each row it reaches, never across
  an occlusion or an object boundary. Every bound in the search is the reach
  (`SdfShadeOps.EdgeReach`, `SDF_EDGE_REACH`) and none is the tile's: a bound
  that reads the tile's edge draws a seam every eight pixels. The comparison
  reads the record's depth, normal and material and the draw record's object
  key (`SdfMeshRecord.ObjectKeyShift`, set by `SdfMeshDresser`), never a draw
  index, so a chunk seam has nothing to differ by. `sdfShadeInkPhase`
  (`SdfShadeHlsl`) is the one door the phase runs through, with the gate and
  the composition inside it. A view searches only while `inkEdges` is set
  (`SdfShadeReads.Edges` over the frame's programs), and the views rate widens
  its tile border to the reach in such a view (`sdf-views-rate.hlsli`). `line`
  is a keyword in HLSL: the channel's kernel variables are `linework`.
- **The frame shades luminance in nits, and each view's composite applies its
  exposure** (`SdfPhotometry`, `SdfViewExposures`, the pass block's `exposure`).
  A light record carries illuminance or intensity over π; emission, sky layers
  and displays are nits. A new constant meant as a display colour (an outline,
  a grid, a placeholder glass) is divided by `passGroup.exposure` or it vanishes
  outdoors and blows out indoors. A window's view and a sky layer's view publish
  nits under the window share of their exposure (`SdfViewSnapshot.Unexposed`,
  `SdfPhotometry.WindowAdaptation`), and the opening applies the rest of the
  viewer's. A point or spot light's falloff is exactly zero at its range, each
  view shades only the lights its frustum may reach (`SdfLightSelection`, the
  pass block's `lightMask`), and a static one is occluded by the irradiance
  volume's visibility bits (`SdfLight.IsTraced`, `sdfMeshLightVisibility`): a
  fixture that lights through a slab is an anchored light or a volume that
  holds no probe there. `world.exposure` reads each view's EV, its image
  meter's reading, target and band state, and its camera sample's source. An
  automatic exposure meters the view's own composite output a few frames late
  (`SdfExposure.Meter`), so a debug view meters nothing and holds; pin the
  exposure for any before/after comparison. A view with no camera sample reads the
  open sky, so a closed room meters as daylight and a display in it reads black:
  `camera=none` names its `wait=` ([mesh GI](references/mesh-gi.md)).
- **Temporal views can share shading in smooth interiors.** The device table's
  `viewsRate` chooses the coarsest spacing and classification tile. The resolve
  publishes smooth axes in the existing history surface. `sdfRateTile` checks
  that history against current geometry and ink and packs the agreed axes in the
  probe record. The views entry packs each cell's representatives into adjacent
  lanes and writes each pixel once. For the table fields and the four tile counts
  see [temporal reconstruction](../../../docs/rendering/sdf/handbook/frame-rendering.md#temporal-reconstruction).
- **The irradiance volume is gathered in the probe lookup and read once per shading sample.** The
  probe lookup pass (`views-probes`) runs `sdfMeshProbesAt` at one representative
  pixel of each `SdfWorldPackage.ViewsProbeRate`-pixel cell (its first pixel, from
  the centre outward, that shades with the volume), at its geometric normal, and
  resolves each gather into a slot (`sdfViewsProbesStoreSlot`): everything a shading
  sample reads of the volume. It also writes every pixel's mix, one word: the pixel's
  shares of the representatives of the four cells about it, each cell's bilinear share
  weighed by how nearly the pixel lies on the representative's surface (`sdfProbeBlend`:
  off its tangent plane by no more than the normal bias, a cell's pixels, or what a
  smooth surface turns by between the two, and facing alike). A pixel none of the four
  serves joins one of its cell's extras, a gather taken at the first such pixel that
  the cell's later unserved pixels compatible with it share (`sdfViewsProbesCompatible`,
  at most `SdfViewsProbeExtras` a cell; a pixel past them blends by the bilinear
  shares alone). Views blends the slots its pixel's mix names (`sdfViewsProbesAt`) and
  holds no gather, so a blend never steps across a visibility edge the record shows, a
  surface reads the volume's light interpolated between cells rather than a cell's
  edge, and the shading kernels carry none of the gather's code. A change to which
  cells a pixel blends keeps the tile lights' rule: `sdfViewsCellLightsSeen` reads the
  cells about a tile and `sdfViewsPixelLightsSeen` a pixel's extra, and between them
  they hold every light a pixel of the tile can see. Every view, a nested one included, first
  reuses its preceding render's gather for a receiver that passes the same tolerance
  against the receiver it was taken at (`passes/sdf-probe-history.hlsli`, the
  native fragment's `viewsProbeHistory` history buffer at the view's own extent,
  bound per recording by `SdfHistoryPorts`): reuse needs only the
  preceding render's camera and grid (`previousView`), never jitter, so a history
  resource does not make a fragment temporal (`SdfWorldPackage.IsTemporal` is the
  one test, by the resolve's history color). Only a complete gather is kept, and an
  entry stands under the volume's geometry serial and, past a window's move, only
  where `sdfProbeHistoryHolds` still holds its receiver
  (`SdfMeshIrradianceParameters.Geometry` and `Windows`, stepped in
  `SdfMeshIrradiance.Publish`; a new input a gather reads must step one of them).
  The geometry serial is unique to one publication of one volume in the process and
  never zero, so an entry validates itself whatever render wrote it: a pass that
  skips cells or draws nothing leaves entries still safe to read.
  `world.counters gpu pass sdf.world$views-probes counter texels` counts the gathers
  that ran. The lookup runs a group's gathers no history serves as one list, a
  gather a lane: in foliage many pixels gather, and inside the views kernel each warp
  holding one would pay a whole gather at views' occupancy. Records and history lie in
  planes, one word of every cell a plane (`sdfViewsProbeWord`), so a wave's
  accesses coalesce. A slot holds the gather's blended cubes (the light a surface
  receives and the radiance a reflection adds, a shared-exponent word a face, and the
  sky shares), its traced lights' agreement, and the lights its corners disagree on with
  each one's share; a views pixel reads its cubes along its shading normal and mirror
  direction (`sdfViewsIndirectSeen`, `sdfViewsReflectionSeen`) and a traced light's
  visibility from the agreement and that list (`sdfViewsLightVisibility`). No views
  kernel includes a reader of a probe's corners: one pixel that read sixteen corners
  made its whole wave pay for them, and the gather's arrays set every kernel's
  registers, so a new thing views needs of the volume is a word the lookup resolves
  into the slot, never a corner read in views. A cell's representative's slot lies in
  the record's planes; an extra takes a slot from the pool that
  follows the planes in the same buffer (`sdfViewsProbePoolSlot`), whose count
  `SdfWorldPassRecorder` zeroes before the lookup's dispatch, and a group of cells the
  pool has no room for keeps no extra, its pixels reading their representatives'. The
  mixes follow the pool, a word a pixel (`sdfViewsProbeMixWord`).
  A slot lists at most `SdfViewsProbePartialLights` lights its corners disagree on and
  rounds the rest to seen or unseen. The gather
  supplies indirect light, reflections and traced static-light visibility. Glass
  gathers at its own pane (`passes/sdf-glass-stage.hlsli`), since a pane is lit
  apart from the surface behind it, and keeps the near face's gathers in a history
  of its own (`glassProbeHistory`, `sdfGlassNearProbes`) on the same functions and
  reuse test (`passes/sdf-probe-reuse.hlsli`); the cell's first pixel writes its
  entry. The glass pass draws nothing in a frame with no glass in view, which a
  history output allows only because the runtime keeps a history its one writer
  skipped (`ShaderPipelineRenderNode.KeepsHistory`). `passes/sdf-views-split.hlsli` holds the
  views pass's compile-out switches for reading each part's share of
  `sdf.world$views` with a reload; a reload re-specializes the authored shading
  sets, which takes minutes, so read `world.shaders.status` for `specialized=` equal
  to `programs=` before reading a views timing.
  Each probe publishes its own measured cubes, without neighbour filtering.
  Visibility uses a 12×12 octahedral first-hit surface map, one bounded ray
  per texel. Each word packs ray distance and two signed normal bytes;
  negative distance marks a measured miss. Four reflected angular taps reconstruct compatible surface patches before
  testing signed receiver gaps and blocked coverage. Radial depth interpolation rejects
  coplanar receivers at grazing angles, so no mean/squared-distance pair remains.
  Keep `SdfMeshProbeDepth.Visibility` and `sdfMeshProbeDepthVisibility` together.
  The sky cube comes from the same full-reach radiance rays as bounced light.
  Surface support stays within the angular taps; no neighbour vetoes or suspect sets return.
  The first sweep caches FP16 normal gradients from each texel's local angular
  neighbourhood. Interpolate compatible quadratic coefficients and intersect
  their entering root; a tangent alone shadowed
  the visible side of sparsely sampled curved geometry. The sparse-curve law
  checks against exact mesh segments. No triangle-footprint clipping is used.
  Gather weights are trilinear share × a half-spacing smooth distance fade in front of the receiver × depth
  visibility. The actual receiver is biased by `NormalBias` metres, never by
  spacing; clamping the lookup must not move the visibility-test receiver.
  The volume has three nested levels, a table per tier with a spacing per axis
  (`SdfMeshIrradiance.LevelsOf`: the tier's window, 4×2×4 m storey cells, 32 m
  cells); a receiver's stages run finest first (finer levels within their
  surface maps' reach, the holding level bounded, the same probes unbounded,
  the finest level's boundary probes), and the first that answers counts only in
  its share, its weight over the cage's front weight before visibility (a finer
  level past its window easing out over its reach's outer half), the rest
  coming from the next stage over another cage. Sixteen pre-scaled slots, named by
  each stage's cage word (`sdfMeshProbeCageWord`) rather than sixteen addresses;
  a corner answers where its weight is positive, and `sdfMeshProbeAt` is the one
  door to its probe. Taps on solids close as a majority at a coarse level and
  smoothly over their whole share at the finest (a narrower ramp there draws a
  plinth's wedge across the floor from every low probe beside it), taps a coarse
  level's trace marked sheets (the curvature half's low bit) at almost any share.
  Only the finest level keeps the behind-plane floor. Nothing a camera sees reads
  an unmeasured surface as black. Wherever no cascade holds a point (past the
  cascades, blended across the band at their edge by the K row's coverage lanes;
  on glass; in a view with none, as `world.shadows off` makes) a shadowing
  directional's visibility is the probes' light bits (`sdfShadowRoleAnswer`). `SdfMeshIrradianceGatherDeviceLawTests` holds the GPU and CPU
  gathers to the same slots, weights, share and sun visibility.
  Relocation (`SdfMeshIrradiance.PlaceBatch`) keeps a probe clear of geometry within
  its own cell; every lookup uses its recorded offset. Placement takes the nearest clear exit; its short rejection rays
  stop at one spacing, with no openness score. Debug with `world.probe` after bakes and GI settle;
  its exact reference segment distinguishes sampling errors from cube content.
  Local adaptation consumes this same gather, so an irradiance error also
  changes exposure. See [mesh GI](references/mesh-gi.md) for sampling, storage,
  boundary behavior and the fixed-camera checks on both backends.
- **Glass is a material, a group and a pass, never a blend.** A palette slot's
  `glass` (`SdfGlass`, material row 21) makes its shapes bake into groups of
  their own (`SdfMeshBakeGroup.Glass`; the stampers emit them as instances
  apart from the opaque ones) and draw in the glass raster pass over the opaque
  image (`passes/sdf-mesh-glass.frag.hlsl`, `passes/sdf-glass.comp.hlsl`). The
  opaque mesh pass and the irradiance trace skip glass draws
  (`SdfMeshDraw.Glass`); cascades select their extinction pipeline. Thickness and RGB optical depth are the glass sums,
  integers the raster accumulates with atomics (`frame/sdf-glass.hlsli`,
  `sdfGlassAccumulate`): never read a glass shape's thickness from its mesh, and
  never accumulate a sum in floats, whose rounding follows raster order and whose
  per-face terms cancel badly against the far sky. Transmission integrates every
  glass material before the opaque hit; two interfaces reflect, refract and
  scatter: the target’s nearest front face and the second the sums’ record names (`sdfGlassSecondInterface`, exact up to three front faces). Both use the same loop. Positive winding means a ray starts in glass. Its scatter and absorption compose in front of both entrances, through `sdfGlassTransmission`. The winding includes an exit hidden by opaque geometry. `SdfGlassSumsDeviceLawTests` runs the accumulation in many orders on
  both backends. The glass pass reuses the opaque path's mechanisms, never a copy:
  one probe gather an interface (`sdfMeshProbesAt`) for its reflection (`worldEnvironmentReflection`,
  `shade/sdf-sky-reflection.hlsli`, then `sdfMeshReflection`, the sky alone where
  `probes.share` is zero), the light walk (`sdfLightResponse`, uncascaded, probe
  shadowing) for its highlights and the light its medium receives, and the flow
  layer (`sdfShadeFlow`). The glass, sky and resolve recorders get the common
  frame values (`SdfWorldPackage.Values`) only by copy from
  `SdfFrameBlock.WriteInto`, which writes the irradiance volume beside the frame:
  a common value it leaves out reads zero in those passes (the glass gather once
  read the volume as off that way). A medium (`SdfGlass.Scatter`, `Scattering`, material row
  31) and a contact (`ContactReach`, `ContactCoverage`, row 20.w's halves) are
  functions of the thickness, `opaque - t` and that light alone: the pass reads
  the lit image once (`sdfGlassLitAt`, or the frost's disc). The handbook's
  [glass section](../../../docs/rendering/sdf/handbook/frame-rendering.md#glass-after-the-opaque-image)
  states the limits. A frame in which no glass draw's host box meets the view's
  frustum (`SdfWorldPasses.GlassSeen`, decided once per instance and frame for
  the three passes) skips the sums clear and the raster and answers
  `DrewNothing` from the shading pass, whose `glassLit` then stands for the
  opaque image; the sky, composite and resolve recorders key their cached lit
  binding on the image they are handed. The sums and the glass target are never
  read on such a frame, so a new reader of either must not run on one. A timing
  window keeps a skipped pass's last samples: toggle `world.gpu-timing off` and
  `on` before reading the trio at a stop.
- **A flow moves shading, never geometry, and never reads a clock.** A palette
  slot's `flow` (`SdfFlow`, material rows 27 to 30) runs `sdfApplyFlow`
  through `sdfShadeFlow` (`shade/shade-flow.hlsli`) in the light stage after the
  weathering and in the glass pass: two
  half-cycle layers of the lattice noise advected along the velocity's
  tangential part, revealing a surface and tilting the shading normal. Its
  phase enters only through the pass block's `flowClock`, which
  `SdfFrameBlock` integrates from `SdfFrame.Clock` and writes zero for a program
  without flow (`SdfAppearance.ResolveShadeReads`), so a still world's views keep standing;
  the clock is a lighting value of the cadence signature, so only the shading
  passes rerun as the flow moves. No `sdf-*` kernel reads the Frame group's
  `time` or `timeDelta`: `puck search -M 0 "frameGroup\.(time|timeDelta)"
  src/Puck.SdfVm/Assets/Shaders/Sdf` finds nothing. An `Object` velocity on a
  draw shading in world space (a static copy) turns by the draw's matrix;
  `World` keeps the velocity and the pattern in world space. On glass the
  revealed coverage covers the glass as a diffusely lit surface. `SdfFlow.Region` blends the velocity inside a world-space sphere before its lane multiplier and projection onto the surface.
- **A shading-time lane is a slot's or the world's.** Lanes 0 to 3 are the
  draw's frame-slot lanes (a body's look lanes; zero on a static copy or an
  animated placement), lanes 4 to 7 the world's `render.lanes`, which
  `WorldEnvironmentResolve` resolves at the presented tick into
  `SdfFrame.WorldLanes` and the pass block carries as `worldLanes`
  (`SdfLanes`). Every consumer (emission, weathering, a flow's velocity and
  coverage, a volume's intensity) reads a lane through `sdfLane`
  (`frame/sdf-lanes.hlsli`), the one reader of `worldLanes`; never index
  `s.lanes` directly. `SdfFrameBlock.WorldLanesOf` writes zero for a frame
  that reads no world lane, so a still world stands. Erosion and morph are
  bake-time and read slot lanes only.
- **Camera history uses the camera's sample grid.** The first recorder can be
  a shadow cascade, so `TemporalOf` receives the recording's `RenderWidth` and
  `RenderHeight`, never its pass-local `Width` and `Height`. A still camera's
  `world.debug-view motion` stays stationary with shadows both on and off.
- **A settled capture holds its residency's frame.** Under
  `world.debug-view motion` the held frame keeps the motion it was presented
  with (`SdfWorldTables.HoldsFrame`, the `held` argument of
  `SdfTemporalHistory.Prepare`); every other view settles it as a still frame,
  so a temporal view converges over the held renders. Settling motion history
  on a held upload makes every capture of the motion view read still. A frame
  held for a settled capture rates no work (`WorldFramePresenter.RatesOf`),
  but preserves the selected culler: the second phase resolves disocclusion
  in the same frame, and a capture must exercise the mode being diagnosed. Under
  `world.lockstep` every frame also presents the completed tick and advances
  presentation by the ticks completed, and its frames (`SdfFrame.TickLocked`)
  have the residency trace the irradiance window to the end before rendering
  (`SdfMeshIrradiance.Stage`; the asynchronous trace otherwise lands probes at
  wall-clock moments as the window follows the first view's camera) and adapt
  exposure over the frame's own presentation time
  (`SdfViewExposures.SecondsOf`); an ordinary settled still under lockstep
  converges over one fresh jitter period (`FrameCaptureRequest.ConvergeOver`),
  since without it a temporally resolved view's still depends on how many
  frames the display rendered before the hold. Together these make stills
  of one tick repeat across Worlds to a 1-level per-render residue.
- **A named-camera layout slot reconstructs; a screen's camera does not.**
  `world.temporal` reaches every view `WorldFramePresenter.ViewQuality()`
  dresses: seat views, the operator pose and `views.layouts` slots that name a
  camera. A camera view a screen films and a session view take the nested
  quality (`WorldRenderSettings.NestedRestriction`, `NestedViewQuality`): the
  presentation's own, with what the presentation's nested table gives up at
  the view's presented share of the display, and `Temporal` off, which
  `SdfViewQuality.Restrict` ANDs, so they never jitter. The table is the running
  quality preset's or device table's `nested`, else the world's `render.nested`
  (`WorldRenderSettings.NestedRows`, chosen by the `nested-table` lever a preset
  writes). Each named view holds its row until its share falls to
  `NestedHysteresis` of a lower row's bound (`NestedRowIndex`), since a row change
  rebuilds the view's graph and redraws its cascades cold. Every nested view's
  render-scale ceiling is the presentation's own, and its row's `renderScale`
  below it (`WorldRenderSettings.Ceiling`, through `DressResolution`). With no
  table a nested view gives up nothing else, so a portal is lit as the room
  behind it.
- **A settled capture cannot show what changes between frames.** Flicker
  (jitter moiré on a screen, a still scene whose pixels change), ghosting and
  trails show only in a presented capture (`FrameCaptureMode.Presented`:
  `puck_capture_frame` with `mode: "presented"`, `frames: 8`, or
  `world.screenshot <path.png> presented 8`), which holds and settles nothing
  and reports each frame's changed pixels and luma change against the one
  before. A still scene with `world.temporal on` that changes in a presented
  burst is flickering; a clean settled still proves nothing about it. The
  report's changed-pixel counts include every moving body in frame (the Moth's
  idle, the companion's hover, the lawn's grass), which reads as half a percent
  of the frame changing: frame a view that holds none
  (`view.override camera look`), or read the burst's files and take each
  pixel's luma range over the still region. The debug world's day runs on the
  engine's clock, a whole day in 24 minutes, so two captures minutes apart show
  different sun, haze and exposure: compare captures taken within a minute, or
  hold the day.
- **A `views.graphs` pane is not an SDF image.** It is a render-graph instance
  with its own shader; it reloads with `pipeline.reload`, not
  `world.shaders.reload`.

## Editing kernels

- **A hit read from GPU data goes through the mesh hit door.** A draw and triangle a pass reads from a target texel,
  a visibility record or the glass sums' front records name a record only through `sdfMeshResolveHit`
  (`frame/sdf-mesh.hlsli`), which accepts a draw below `passGroup.meshDraws` whose triangle lies below the record's
  level table word less its index word, and otherwise answers a miss and counts `gpu.mesh-hits.rejected`
  (`puckCountRejectedHit`). Every reader of a hit's record, indices or triangle material takes the `SdfMeshHit` it
  returns (`sdfMeshSurfaceNormal`, `sdfMeshMaterial`, `sdfGlassInterfaceAt`, `sdfShadeStamp`, `sdfShadeFlow`,
  `sdfMeshHitFrameSlot`), so a new reader that skipped the door does not compile; filling one by hand is review's to
  catch. Only raster stages, whose draw the selection kernel emitted, index `sdfMeshRecord` and
  `sdfMeshRasterMaterial` directly. A rejection is a miss, never a clamp to an in-bounds triangle. The door cannot tell
  an in-bounds wrong occupant, which `sdf.instances.targets.stale` counts (`SdfWorldPasses.RecordHits`: hit writers note
  the records' revision they recorded under, hit readers compare it). `world.rows force hit-bound [<frames>]` zeroes the
  bound for frames, so every hit is rejected and counted; both counts read zero on an ordinary run.
- **A probe kernel that traces has two builds of one body.** The trace, relight and camera sample live in
  `passes/sdf-probe-*-stage.hlsli`; `sdf-probe-*.comp.hlsl` includes it after the software interface and
  `sdf-probe-*-rays.comp.hlsl` after the ray-query interface with `SDF_PROBE_RAY_QUERY` defined, which swaps
  `sdfProbeWalk` (`shade/sdf-probe-trace.hlsli`) for an inline `RayQuery` over the scene's top level. Edit the stage or
  the walk, never one wrapper; a hit's normal and material stay `sdfProbeSurface`'s in both. The client takes the
  variant per job (`SdfMeshProbeGpuWork.TopFor`), and the structures' lifetime rule is the header of
  `SdfMeshProbeGpuWork.Rays.cs`: a structure is disposed only through `Retire` and `DrainRetired`.
- **Every residency's probe scene shares one buffer.** `SdfMeshProbeScenes` (one per mesh region store,
  `SdfMeshRegionStore.ProbeScenes`) places each trace geometry once, claimed by every probe client whose scene names it,
  beside each client's prefix, whose first two words are its root words. A traced dispatch pushes the published
  export's root word base, and the kernels read the root through `sdfProbeRootWord`, never `sdfProbeScene[0]`. Chunks
  ride whichever residency's upload opens next (`SdfWorldTables.OpenUpload`, after the mesh region's owed copies), and a
  client publishes only once the upload carrying its root words has completed (`SdfMeshProbeScenes.Upload.Rooted`). The
  region unmaps every chunk no live allocation touches (`SdfMeshProbeScenes.Cover`). `world.residency`'s mesh-store line
  ends with the `probe-scenes` reading (`live-bytes`, `geometries-shared`, `unmapped-bytes`).
- **Mesh words go through one door.** `frame/sdf-mesh-words.hlsli` reads a draw's record through
  `sdfMeshRecordWord` and a mesh's own words (vertices, triangle materials, indices, level table, members) through
  `sdfMeshWord`, and then makes `sdfMeshRegion` an undeclared name, so a read around it does not compile.
  Both reads use the one region binding. The retired slab-index prototype is recorded with its revision in
  [step 7](../../../docs/plans/streaming-and-memory.md#the-prototype-as-built).
- **Check screen eligibility before reading a screen table.** A material is
  not a screen index: `sdfScreenSurfaceShades` checks the sentinel, bounds and
  source before `material - SDF_SCREEN_MATERIAL - 1` names a screen. Temporal
  ray helpers need that check before their own table reads too; a later sample
  function's guard cannot protect them. An ordinary material becomes a huge
  unsigned index otherwise, which can lose the Vulkan device while Direct3D 12
  returns zero for the out-of-bounds read.
- **A fragment stage addresses per-pixel storage only from a covered lane.** A
  quad that straddles the last row or column of a grid with an odd height or
  width runs helper lanes outside the grid, and a helper's `SV_Position` names a record
  past the end of the visibility records or glass sums. Read or write a buffer
  addressed by the pixel only under `!IsHelperLane()`, as the glass raster
  (`passes/sdf-mesh-glass.frag.hlsl`) and the cascade glass
  (`passes/sdf-mesh-shadow-glass.frag.hlsl`) do; a texture `Load` there is
  bounded. Vulkan on NVIDIA loses the device on the read once it passes the
  allocation's slack, while Direct3D 12 returns zero, so an even-sized window
  and Direct3D 12 both hide it.
- **One `mapCore` call site a kernel.** The bake's two samplers
  (`passes/sdf-mesh-bake-stage.hlsli`) are the only GPU kernels that walk the
  field, each through one call site: the value sampler once a sample, the
  gradient sampler (`SDF_FIELD_DUAL`) through one rolled `[loop]` that ends after
  its forward-mode walk unless the walk was uncovered. Every call site is a full
  inlined copy of the interpreter, costing register pressure, compile time and a
  driver translation on every cold boot, so more samples go through that loop,
  never through a new call. Never add `[noinline]`: a real SPIR-V call loses the
  device on NVIDIA's Vulkan driver.
- **A walk arm keeps its derivatives or says it does not.** Under
  `SDF_FIELD_DUAL` the walk carries `localJacobian` beside `localPosition` and
  `SdfHit.gradient` beside `distance`. A formula arm gets its dual from the
  generator (`SdfShaderAction.Map`, `Part`); a hand-written arm that changes the
  point or the distance updates both under `#ifdef SDF_FIELD_DUAL` and sets
  `Dual = true`, or the generator marks it `SDF_FIELD_DUAL_UNCOVERED` and every
  sample whose walk runs it takes the stencil. Setting `Dual` on an arm whose
  derivatives are wrong is silent: `SdfFieldDeviceLawTests.Gradients` is what
  sees it.
- **Keep each pixel kernel's include closure its own.** A pixel kernel includes
  the common head (`passes/sdf-pixel-pass.hlsli`), only its own stage module,
  then the entry; a stage module includes what it reads beyond
  `passes/sdf-world.hlsli` itself. The shader cache keys, and a reload
  recompiles, by include closure, so routing a stage module through another
  wrapper makes an edit to it recompile both kernels.
- **The bake kernels include `passes/sdf-bake-world.hlsli`, never
  `sdf-world.hlsli`.** Their compiled bytecode keys the stored meshes
  (`SdfMeshBakeIdentity`), so their closure holds only what the bake reads:
  their own interface (`isa/sdf-mesh-bake.interface.hlsli`: a pass block of
  `SdfWorldPackage.BakeValues` alone, stamped with the field's fingerprint), the
  field interpreter (with the 2D box distance and glyph decode it shares with the
  overlays, `Puck.Shaders/Assets/Shaders/Shared/msdf.hlsli`) and the field's
  generated vocabulary (`isa/sdf-isa.hlsli`).
  A light, exposure, view, pass-block value, pass resource or World table goes
  in `isa/sdf-render.hlsli`, the world interface or after
  `SdfWorldPackage.FieldTables`, and leaves the bake's bytecode byte for byte as
  it was on both backends. SPIR-V orders its constants by first use in the whole
  source, dead code included, so a lighting, screen or view module in the
  closure would move a stored mesh's key. Add to the bake's closure only what
  the bake reads, and check with `puck mesh identity --against`.
- **A shared header is included as `Shared/<name>.hlsli`.** It resolves
  through an include directory: the build passes the checkout's
  (`$(PuckShaderIncludeDirectory)`, `src/Puck.Shaders/Assets/Shaders`), and a
  runtime reader (`SdfKernelSource.Read`, a reload) asks
  `ShaderIncludeDirectories.Of` for the deployment's `Assets/Shaders`, where
  `Puck.Shaders` deploys `Shared` as content; a reload of a checkout tree
  therefore reads the World's deployed headers. Never climb out of a
  project with `../`: the deployed tree (`Assets/Shaders/Sdf`) cannot reach it,
  its sources are refused, and every specialization stays interpreted. A
  source refused at load is named by `SdfKernelSet.SourceRefusalOf`, printed
  as `[world.shaders: <kernel> source refused ...]` and carried into each
  specialization refusal.
- **Keep control flow uniform around barriers and groupshared gathers.**
  Inactive lanes still reach the barriers.
- **Hot reload compiles kernel sources only.** With `Puck.World` running:
  `world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf`, then
  `world.shaders.status` (`unchanged` means no shipped kernel compiles the code
  you edited). A host ABI, buffer-layout or C# ISA change needs a rebuild: the
  reload keeps the previous kernels when a tree's kernels do not match the
  host's interface, so a kernel reading a field the C# side just gained keeps
  running its previous build. The instruction-set stamp covers layout, so a value only
  the shader reads is declared `Tuning` in `SdfIsaHlsl.DeclareRender` and changing it
  reloads; a real the host packs, decodes or shares with the CPU evaluator stays an
  ordinary `Real` ([classification](../../../docs/reference/shaders.md#pass-interfaces)).
  Change the C# layout and the HLSL layout together
  ([sync pairs](references/sync-pairs.md)).
  Mesh raster and cascade vertex/fragment stages are composition-owned (`SdfMeshRasterPass`), so
  edits to them need a rebuilt boot too. A shared `frame/sdf-mesh.hlsli` edit can reload its compute
  readers while the raster stages keep their deployed bytecode; a successful reload is no proof
  that a vertex-stage performance experiment took effect.
- **No signed `%` of a value that can be negative.** NVIDIA's Vulkan driver
  answers a signed remainder (`OpSRem`, which DXC emits for `%` on `int`) of a
  negative operand wrongly where Direct3D 12 answers it right: lift the value to
  non-negative and wrap it as `uint`, as `sdfMeshIrradianceSlot` does.
- **One walk in `mapCore`.** NVIDIA's Direct3D 12 driver on Turing (the RTX
  2060) takes a second walk chosen by a program-uniform flag (a linear
  all-segment walk beside the instance merge) for instanced programs too, so
  every bake group samples the whole program; Vulkan on the same card does not.
  A program with no instance lists every segment as a world segment, so the
  merge covers it. When one backend's bake disagrees, compare
  `[sdf.mesh-bake] done` on both: a group that empties on one backend alone (an
  axis line under the floor) is the walk ignoring its mask.
- **Index local arrays by constants only.** NVIDIA's Vulkan compiler keeps a
  dynamically indexed local array, a `static const` table included, in
  per-thread local memory. Unroll the loop to the array's length and guard the
  body (`if (i < count)`), or pick the element by comparison; DXC's SPIR-V
  unroller leaves a loop with a `break` rolled. RenderDoc's pipeline-executable
  statistics show the spill as "Local Memory Size" (the low 32 bits).
- **Bindings are generated.** No kernel declares a binding or register by hand;
  a new resource is a member of `SdfKernelInterfaces`.

## Changing the instruction set

First check whether the new behavior composes from existing instructions. If it
does, emit that program from a builder method.

For a new instruction, primitive, blend or relief row, add one entry to
`SdfFieldOps`, `SdfFieldShapes`, `SdfFieldBlends` or `SdfFieldReliefRows`.
Each abstract facet asks for an explicit choice, including an identity action
or a refusal. The Roslyn generator emits the packed enums and static selectors.
The CPU walks consume those selectors. Admission checks the lanes, and bake
keys take the external dependencies through `SdfFieldOps.ResourcesOf`.

A primitive's or blend's arithmetic is a formula in `SdfFieldFormulas`, which
the entry's `Formula` facet names: the generator lowers it to the evaluator's
fixed-point method and `puck shaders generate` to the kernels' precise function,
so neither reader is written by hand. A shape whose readers need a side table, a
texture or a loop is a target escape (a null `Formula`, hand-written `Shader`
and `ShapePoint`) and takes a case in `SdfFieldDeviceLawTests`; [sync pairs](references/sync-pairs.md#instruction-semantics-shared-by-both-interpreters)
lists the escapes and where generation's guarantee stops.

An op's effect on the walk is a formula too. A point op's `Formula` maps the
local point (and `DistanceFormula` gives the factor a scale puts on the
distances read through it), a field op's `Formula` maps the running distance,
a blend's `Winner` says whose material stands and its `Operand` computes what
a scope pop passes to both, and a relief row may carry one. The op's `Shader`,
`RigidShader` and `PartShader` arms are then `SdfShaderAction.Map`, `Rigid` and
`Part` over those formulas, which bind the walk's state to the name each
kernel holds it under, and the evaluator's `MapPoint`, `EditField`,
`BlendShape`, `CandidateWins` and `PopOperand` are generated. An op with no
formula (a reset, a scope, a join, the two warps and the render-only ops) keeps
a hand-written arm and a hand-written case in the evaluator's walk.

Every formula lowers three ways: fixed-point C#, HLSL, and the wide C# a
regional evaluator reads normals through (`SdfFieldEvaluator.TrySurfaceNormal`,
double precision, one point a lane). A node added to `SdfFormulaOp` states all
three in `SdfFormula.Render`; the wide form is the fixed form's text unless the
node says otherwise, so a new fixed carrier call needs the same member on the
evaluator's `Wide` carriers. The wide reader refuses a tape that holds an op
without a formula and the fixed-point gradient answers instead. The dual reader
lowers every formula a fourth way, for two targets from one emitter
(`SdfFormula.EmitAllDual`, `EmitAllFixedDual`): the bake's `SdfDual` overloads and
the query evaluator's `FixedGradient` ones (Puck.Maths' `FixedGradients` names
each function as `field/sdf-dual.hlsli` does), which
`TryFieldGradient` walks once over the point's tape or the culled stream (`SdfFieldEvaluator.Gradient.cs`).
A node with no fixed-point dual (a varying exponent, the ellipse's root, the cell noise)
leaves its formula without one, and a tape that reaches it takes the six taps.
A new dual rule goes in both libraries ([the decision](../../../docs/decisions/engine-design.md#a-field-formula-lowers-once-per-carrier)).

`ShaderDeclarations` regenerates the field switch includes and operand readers
under `Assets/Shaders/Sdf/isa/`, and `SdfIsaHlsl` stamps both the field and whole
interfaces. Resolve merges in the catalogues and regenerate with the shader
generator build or `puck shaders generate`. Use `puck shaders generate --check`
to check the resulting tree.

Cover every packed lane in `SdfEncodingProbe` and check evaluator agreement on
Direct3D 12 and Vulkan with `SdfFieldDeviceLawTests`. Keep the fixed-point and
HLSL formulas in agreement until the shared formula emitter replaces the
paired arithmetic. Carry author-facing parameters through
`CreationCanonicalizer` and the stamp emitters, then regenerate the schema.

## Field contracts that bite

- **One accumulator.** `mapCore` carries one running distance for the whole
  program. An intersection-family blend annihilates every earlier shape it does
  not overlap; field ops (`Onion`, `Dilate`, `Displace`) grow every earlier
  solid once the accumulator holds more than one object.
- **A subtraction is exact only where its subject is nearest.** Inside a carved
  void the contact field reads phantom surfaces; extend the carve past every
  point a body can reach, or build the void from union geometry.
- **An instance bound is an influence sphere** tested per sample. It must cover
  the primitive's reach, its field ops and its blend halo, or the bake clips the
  geometry at the sphere.
- **A field scope is a real per-sample cost.** Scoped segments skip the segment
  early-out and rigid planning. Never wrap one primitive in a scope for its
  Lipschitz factor; make the primitive 1-Lipschitz. Scope clamps
  (`SdfProgram.FieldScopeClamps`, `world.budget`) are field bounds, not measured
  GPU cost.
- **The CPU evaluator's rounding is part of its Lipschitz bound.** A product of
  two lengths rounded to the carrier keeps `2⁻¹⁶` of its value, a few bits at
  centimetre sizes, so a ratio, projection or nearest-sample comparison built on
  one reads a millimetre of noise and the field overstates its own distance
  (the Moth's spine joints read 26 times it). Round such quotients once
  (`maths-usage`'s Choose table). `SdfPrimitiveLipschitzLawTests` samples every
  door primitive at a centimetre and a metre; run it after changing a shape, and
  read `puck mesh audit --cpu --field-levels`' `lower/upper` witness (value and
  bracketed upper bound) to trace a group whose step scale fails.
- **Unbounded is a state, not a number** (`SdfBoundAlgebra.Unbounded`); bounds
  compose through the set operations in `SdfBoundAlgebra`.
- **An interval rule whose width compounds shrinks the whole evaluator.** The
  CPU evaluator's frame (`SdfFieldEvaluator.Frame`) is the widest cube its box
  walk keeps bounded, and every query outside it is refused, which a sphere
  cast reads as an obstruction at its origin. A blend rule that reads its
  operands twice grows geometrically along a chain on one accumulator: unscoped
  smooth unions across a few trees once collapsed the presentation field's
  frame to half a metre, so the chase camera sat on its target valley-wide.
  Bound a monotone blend by its corners instead (`SmoothUnionBounds`), and
  read `Frame` when every query far from geometry fails.
- **Deterministic versus presentation geometry:** use
  [sdf-authoring](../sdf-authoring/SKILL.md#render-only-versus-deterministic);
  the fixed-point evaluator supports `SdfShapeType.Path` through `SdfPathSolid`.

## Capacity and emission

- Program words and instances grow on upload, and the dynamic-transform region
  grows to the frame's table, whose rows are the program's slots plus one root
  per rigid copy (a body), so a `world.reload` or a new body grows it mid-run.
  A frame that supplies fewer transforms than
  `RequiredDynamicTransformCapacity` throws, because a slot rendering at
  identity is a bug. Fix the emission so the frame supplies the transforms it
  needs; increasing a reserve cannot supply missing transform rows.
- A `GpuRegion` grows under whichever policy `GpuResidency.Select` gave it
  (`GpuRegion.Grow`): a staged region carries its destination on the GPU, and a
  ring or in-place region replaces its host-visible buffers and owes each the
  whole region; the owner retires what it returns after the following upload's
  fence, which also covers earlier readers and a staged carry. Create replacement
  buffers, retirement bookkeeping and the CPU shadow before publishing growth;
  a failed allocation preserves the old buffers, contents, capacity and bindings.
  `SdfWorldTables.EnsureDynamicTransformCapacity` prepares motion history before
  growing the region and publishes capacity only after both succeed.
  A Direct3D 12 buffer or image placed by `DirectXHeapArena` keeps its range
  until its owner calls `DirectXDeviceMemory.CountReleased` before the last
  `Release`; an owner that only releases the resource leaks its range and
  keeps the heap for the run. A device with
  a large host-visible device-local heap (resizable BAR) gives a small per-frame table a ring, while the CPU fake
  device's default profile stages every region, so a region path written or
  tested for one policy alone passes the device-free suites and fails on such
  a device. Test a region path under every policy, as
  `GpuResidencyLawTests.ARegionGrowsUnderEveryPolicyAndEverySlotReadsItExact`
  does.
- Large GPU buffers give their memory back. Both arenas (`DirectXHeapArena`,
  `VulkanMemoryArena`) are one `GpuMemoryPool` each, which holds the policy:
  first fit across a key's shared blocks, a block of its own for a resource
  past half a block that no shared block holds, freed as it empties, and one
  empty shared block kept spare per key outside pressure. `MemoryAccount.TrimRequested`
  reaches both arenas through `IGpuMemoryOwners` and `GpuDeviceMemoryWork`.
  `GpuMemoryPool.Trim` frees empty blocks and marks the existing live blocks
  for release on their last return, including a delayed fenced return. It
  never revokes a live placement; new blocks do not inherit an old request.
  A backend supplies only the native
  allocate, free and bind calls and finds a resource's placement again (a
  private-data token on Direct3D 12, a handle map on Vulkan). The pool reuses a
  range only once its resource's release returns it and sees no fence: a range
  a queue may still read stays held only because the owner retires the
  resource after those submissions. A staged `GpuRegion` slot's
  staging buffer shrinks to twice its flush (`GpuRegion.StagingFloorBytes`
  floor) once it holds four times more, and a relocated mesh region shrinks to
  its need (`GpuRegion.Shrink`). Upload heaps are system memory: a staging
  buffer kept at a whole region's size costs the process that much working set.
- Vulkan enables `sparseBinding` and `sparseResidencyBuffer` together only
  with a nonempty sparse queue family, preferring graphics. The device owns
  a separate binding queue when needed. `VulkanLogicalDevice.BindSparse`
  checkpoints graphics, binds waiting that value and signaling the next,
  then submits a graphics wait before returning. Both signals share the
  device's retirement timeline; a failed bind or join prevents further
  retirement proofs until teardown. `VulkanSubmissionBatchLawTests` covers
  both queue arrangements and an open batch. `VulkanGpuMappedBuffer` is the
  one caller: it returns a chunk's pages and destroys the buffer only through
  the arena's sparse retirement (`VulkanMemoryArena.RetireSparse`), behind the
  timeline value the door answered, and a bind whose outcome is unknown strands
  what it covered until the device is torn down. Its unbound chunks are read
  by no kernel: a mesh word is read only through a record that names a placed
  mesh.
- The mesh region keeps no host copy of its meshes. Its host copy is the draw
  records alone (`GpuRegion.Shadow`). A landing job's words are packed from the
  `SdfMesh` buffers on a worker into a buffer of the landing's own and gathered into
  the region (`GpuRegion.TryOweGather`); a mesh moved on the GPU
  (`GpuRegion.TryOweMove`) passes through no host buffer, and the mesh region
  stages no word from the host at all (`SdfMeshRegionStore`). A region's moves record first, then its gathers, then its
  staged words, in one recording door (`GpuRegion.RecordCopy`), so a word written
  after a move never races it. A mesh's planes (positions, indices, normals, triangle materials and every
  level's) are a cache of its store entry: they are private to the mesh, and the one door to them is
  `SdfMesh.Held`, which only `SdfMesh.Hold` makes (a need, waiting for or running the restore). A mesh the
  library published from its store lets every plane go once it has stood unneeded for
  `SdfMesh.IdleSeconds` (`SdfMesh.ReleaseIdle`, run by each residency's store service, every unneeded one
  at a trim) and brings them back from the store when next held (`SdfMesh.Restore.cs`); the members,
  vertex members and influences stay. So read the planes under a hold and keep no view past it: a long-lived
  reader copies what it keeps, as the irradiance scene copies each traced level's triangles over the
  vertices they reach and each draw mesh's traced materials (`SdfMeshIrradianceScene.Geometry`), and a
  count comes from the mesh (`VertexCount`, `IndexCount`, `HasNormals`, `HasTriangleMaterials`), never a
  plane's length. `SdfMeshRegion.WriteMesh` takes a hold, so every region write, a coarsened copy run's
  included, reads resident planes; the owed set (`OwedMeshes`) holds a need for the owed meshes the latest
  list names so landing never waits on the frame thread. A hold of a released mesh on the frame thread
  reads and decodes its entry there (a renumbering does), so a frame-thread reader holds rarely. A
  published chunked mesh keeps no joined indices: read its chunks. Laws read planes through
  `tests/Shared/SdfMeshPlanes.cs`, which copies them through a hold.
- A scoped creation emits one instance, a scope-free one an instance per shape
  (`CreationStampEmitter.PerCopyInstanceCount`). `world.budget` prints the live
  cost sheet.

## The baked mesh

Every view renders meshes the program is baked into, with no field walk in the
frame. Read [mesh bake](references/mesh-bake.md) before changing the bake, the
contour, grouping, the mesh region or the store, or when a baked mesh looks
wrong. The contracts that bite most often:

- **One contouring:** `SdfSampledLattice` through `SdfDualContouring`; never
  write a second mesher.
- **A landed mesh is its planar polygons** (`SdfMeshPlanarMerge`, in
  `SdfMeshPathBake.Land`): a flat face's interior vertices go and every outline
  vertex stays, so no pixel changes. A change that moves or renormals a vertex
  there, or drops an outline vertex, does change pixels.
- **A landed mesh carries two coarser levels** (`SdfMeshCollapse`, after the
  merge in `Land`; `SdfMesh.Levels`): half-edge collapses over the mesh's own
  vertices, each with its measured error. The region lays a level's indices and
  materials after the merged ones', so its triangles are numbered after them. A
  draw pushes its level above its index (`SdfKernelInterfaces.MeshLevelShift`,
  in a counted indirect draw's identity too), its record points at the mesh's
  shared level table (`SdfMeshRecord.LevelTableWord`), and both raster vertex
  stages add it to the vertex id (`sdfMeshLevelIndex`). Never pass the level as
  a first vertex: Direct3D 12's `SV_VertexID` counts from zero whatever the
  draw's first vertex, so the draw fetches the merged mesh's first triangles and
  objects go missing. The surface pass reads the triangle the pixel names with
  no level of its own. The audit reads the merged mesh, level zero. The
  irradiance trace reads each static mesh's coarsest level within
  `SdfMeshIrradiance.TraceError` (`SdfMeshIrradianceScene.GeometryKey.Of`): its
  geometry copies that level's indices, its instance records and bottoms
  address the level's indices and materials past the finer levels' in the region
  (`Geometry.FirstTriangle`), a hit's triangle is the level's own, and the
  diagnostics' nearest query adds the level's error to its budget. A scene built
  with no `traceError` (the offline field-level audit) traces level zero. Each pass's choice runs beside the box cull in
  `frame/sdf-mesh-selection.hlsli`, from `SdfMeshDetail`'s packed controls;
  `world.detail 0|1|2` forces one level
  everywhere, and `world.detail auto` restores the choice.
- **Large static unions have independently drawn chunks** (`SdfMeshChunks`, before the merge). Their contour edges
  partition the surface, and every seam edge stays fixed at every level. The store holds the pieces; only those pieces
  enter the region. See the [mesh bake](references/mesh-bake.md#groups-poses-and-materials).
- **A field diagnostic is a bake lattice, never a pixel-kernel walk.**
  `SdfFieldSlice` (`world.slice`) queues points lattices with gradients on the
  bake and draws the distance bands and `|grad d|` on the CPU from the answers.
  A per-pixel field view would put a second interpreter copy in a pixel kernel.
- **Winding is one contract across three places.** The bake winds outward
  faces counter-clockwise (`SdfDualContouring`), both backends declare a
  counter-clockwise face front (`GpuGraphicsPipelineDescription`, the two
  pipeline factories), and the opaque mesh pass culls back faces
  (`SdfMeshRasterPass.Description`; a cascade's exit layer culls front faces
  seen from the light, and its depth and the glass pipeline cull nothing). An
  authored mesh whose triangles close a volume is wound outward like the bake's;
  any other authored mesh is drawn in both windings and does not enclose
  (`WorldPlacementStamper.MeshOf`, `SdfMesh.Encloses`). Probe facing with `SV_IsFrontFace` in the
  mesh fragment stage and `world.debug-view depth`, on both backends.
- **A sampled field moves without a program rebuild.** `SdfProgramBuilder.SampledValues`
  names an `SdfSampledField` by handle; the frame carries its snapshots (`SdfFrame.Sampled`),
  the geometry hashes them, and the bake compares only `SdfSampledSet.Baked` (the handles an
  instruction samples), so a height field's heights moving restart nothing. A moved baked snapshot
  resamples only the reader groups (`SdfMeshPathBake.Revisit`, replanned from each group's
  planned box); a reader still baking keeps its bake and the newest plan waits as its
  `Successor`, so a field republished every frame still lands. Never compare the whole set
  in the bake, and never replan from `Low`/`High`, which the lattices narrow. Batch tickets
  leave `Taken` once through `SdfMeshPathBake.MoveTicket`, which both `Complete` and `Abandon`
  call, so a partial answer returns exactly its unanswered lattices. Each bake job writes the snapshots its groups sealed into
  its own `sdfSampledValues` table, so a lattice never samples a newer publication; never
  put sampled values back into program words or a shared GPU buffer the bake reads
  ([sampled fields](../../../docs/rendering/sdf/handbook/sampled-fields.md)).
- **A height field moves as it is drawn.** `SdfHeightField` bakes a reference (every raised
  column at `ReferenceCells` above the origin) and names its heights as the instance's
  displacement; the frame's sampled table (`sdfFrameSampled`) carries the heights, and every
  vertex read (`sdfMeshWorldPosition`, `sdfMeshWorldNormal`, `sdfMeshPreviousPosition`)
  displaces through `sdfMeshDisplaced`. A new reader of mesh vertices goes through that
  resolver, or it draws the reference; a draw's box spans `SdfMeshDisplacement.Low`/`High`,
  and a displaced draw is `IsDynamic`, out of the irradiance volume's static scene.
- **A mesh is a function of its geometry.** Groups are keyed by
  `SdfMeshGeometryBuilder`; anything the bake samples that the instructions do
  not hold leaves a stale mesh drawing after an edit until the builder hashes
  it.
- **Baked meshes persist across runs** (`SdfMeshStore`), keyed by geometry and
  the bake's identity: the bake kernels' bytecode and the IL of every method
  the bake's code reaches, across the `Puck` assemblies, read from the build
  (`SdfMeshBakeClosure`), and the entry format's version. No list names the bake's files: code it calls is
  covered, code it does not call is not, a checkout's path is not, and a miss
  logs the part that changed (`[sdf.mesh-bake] miss …`). The stage keys cover
  the decoders that restore their products. A hot-reload update to
  a type the closure declares members of folds into the code identity
  (`SdfMeshBakeClosure.Reads`, `CodeRevision.AppliedTo`), and the residency
  follows it before each plan and stage; an update anywhere else keeps the
  store. `puck mesh identity
  --build <dir> --against <dir>` names the changed members and the stages that
  can reuse products before any boot.
- **`puck mesh audit --world <file>` checks a bake without a GPU or a person
  looking**, against the CPU field; `--cpu` bakes offline so a contour or field
  change shows with no boot. Run it before calling a bake change done.

## Performance and cost

Judge cost by counted work, code and disassembly; a wall-clock or GPU-timestamp
reading is evidence beside the counts of the same run on a quiet machine, never
the verdict alone. What a kernel costs on the hardware (registers, occupancy,
banks, coalescing, waits) and how to read it is
[gpu cost](references/gpu-cost.md).

```text
world.cadence off      # a still scene otherwise skips frames and reads near zero
world.rates off        # far or small work (far cascade, small windows, screens, bodies) otherwise updates at least 30 or 15 times a second, far bodies and tiny windows down to 5
world.debug-view rates # tint each rated item by its tier; world.rates lists each one's size and tier (worlds/debug/range.md)
world.counters gpu     # per-pass counted work: dispatches, shape evaluations, texels, uploads
world.gpu-timing on    # per-pass timestamps; `world.gpu-timing timeline` orders each node's latest span on the queue's clock with the idle gap before it
world.budget           # program words, instances, scopes, segments
world.counters gpu.queue      # native executes, lists, signals and presents by category and split cause
world.counters render.record  # record jobs' host time per phase, serial sum and per-frame critical path
```

The bodies lever rates every stamp pool the presentation packs: the boot world's
and each routed scene's (a shared session scene among them), each by the views and
shadow cascades of its own frame (`WorldCreationInstances.ShadowBy`). A pool no view
rates poses every awake registration every frame and re-stages each of its mesh rows, which
shows as `capture` and `pack` in `host.frames` and as `sdf.creations.slots.posed` far
above the transform rows `sdf.transforms` packs. `world.rates` lists the bodies as of
the frames after its previous read: the pool rates only what it must pose, and rates
every body for the listing only for `SdfUpdateRateReadings.FramesPerRead` frames after
a read or while the tier tint is on, so read twice and keep the second.

Divide `gpu.queue` and `render.record` deltas by their `frames` deltas; [gpu
cost](references/gpu-cost.md#count-queue-work-and-record-jobs) reads them, and
[runtime contracts](references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader)
owns the record job (`gpu.faults record <n>` fails one to exercise abandonment).

`world.counters gpu` runs to hundreds of kilobytes in a world with many graph
instances, past the 1 MiB an MCP result carries, so ask for what you need:
`world.counters gpu summary` (one `draws vertices texels dispatches` line per
node), `node sdf:world` (a node and the nodes under it), `pass sdf.world$mesh`
(a pass label, or its prefix by whole segments, in every node that has it; it
drops the lifetime, owned-bytes and outside lines) and `counter vertices` (a
count-name prefix). They combine, and `--json` takes them too. A text reading
past `WorldCountersCommandModule.GpuTextBudget` characters prints the summary
under a note. A pass label is not a node name: `node sdf.world$mesh` is refused
with a hint to use `pass`. The counts are read back a few frames late: the first
reading after a camera pose or a lever change can still show the previous
frames' work, so read twice and keep the second. A settled capture disables rated
updates but retains two-phase occlusion. Staged order is preserved within each
phase; an exact coplanar depth tie across phases can depend on prior visibility.

Attribute shading cost by toggling the live levers (`world.shadows`, `world.ao`,
`world.indirect`, `world.render-scale`) and isolate the raster with
`world.debug-view depth`. Compare the whole frame, not one pass. `puck counters`
boots a workload on both backends and `puck counters compare <before> <after>`
holds two reports to each other; a deterministic count must agree on both
backends. `world.debug-view` lists the diagnostic images.

On the lead machine the frame bar is read at 2560 by 1440 on the RTX 4070, the
device the work meets first (AGENTS.md, "Upper hardware enhances"), on the
benchmark route in motion, and it must hold in both topologies; the RTX 2060
and the Steam Deck have their own sizes and budgets. `puck benchmark` and `bench frames` open that
size by default (`BenchmarkCommand`'s `--width` and `--height`); a reading at a
smaller window says where time goes and is not a bar reading, and it can hide a
defect that shows only at the bar's size. The route at that
size runs once per integration batch, in the lead's quiet window, with the
authority in process: the benchmark has no remote endpoint, so it says nothing
about the remote topology, and a verdict names the device, size, backend and
topology it read. A lane that claims frame time names the passes, counters and
`host.frames` split it expects to move, with frozen builds from before and
after its change, as a measurement request for that window; it takes the
windowed before/after readings itself only when its brief grants a GPU leg
for them.

A technique is judged first by what it gives the RTX 4070, and a target that
lacks a capability never vetoes it. What the device can do is read at device
creation (`GpuDeviceCapabilities`, printed on `world.counters gpu`'s
`capabilities` line), and each device meets its bar by the path its authored
quality table enables. A path that needs a capability is a second
implementation: it reads the same data as the path it replaces, gives the same
result within the still tolerance, is measured to pay on that hardware, and
falls back automatically where the capability is missing, so every device keeps
a working path. A device's table is its `render.devices` row's own `table`, or
its tier's preset (`WorldDeviceProfile.TableIn`); a windowed boot applies it once
the device names its adapter, and the benchmark applies it again before its first
chapter (`--profile`). A table sizes storage through the levers it sets: its
indirect tier sizes the irradiance volume, its hit cache and its published and
feedback tables (`SdfMeshIrradiance.LevelsOf`), and its distant map's texels size
that map. The output-sized temporal history (`SdfWorldPackage.HistoryWindowWords`)
follows the display, not the table.

For a frame-time bar,
`host.frames` on the windowed host gives produce, simulate and their sum per
iteration (avg, p50, p99, max) and the worst iterations split into their
phases (simulate, begin, produce, present, wait) with their collections and the
pacer's lateness ([Puck.Launcher](../../../src/Puck.Launcher/README.md)).
Produce is split into `produce-work`, the frame thread's own work, and
`produce-fence`, its time blocked on submission fences (`GpuFenceBlocking`,
counted in `GpuBatchedSubmissionFence`'s one wait door): a produce that grows
by its fence share is GPU back-pressure, not CPU work. Its usual home is the
upload's ring wait (`SdfWorldTables.SubmitUpload` waits the previous upload's
fence, so the frame thread waits for the GPU to finish the frame two back). That
holds only because an upload's fence answers from a hand-over of its own
(`GpuQueueWork.OwnsSignal`); a fence answered from the frame batch's last
signal makes the frame thread wait for the frame it just submitted, and the
CPU and GPU take turns (`produce-fence` near the GPU's frame time, interval
near CPU plus GPU). `host.frames` does not name the waiting site; a stack
sample taken inside `GpuBatchedSubmissionFence`'s wait does. `begin` is the
presenter's own wait for the previous frame. The `produce-work
parts` are `HostFrameWorkPart`s: `capture` is the residency's scene capture
(`SdfWorldResidency.Capture`: the composition's pose and transform pack, the
creations' drivers and effector surface probes, and the mesh dresser), never a
screenshot, so it costs whatever the scene animates with nothing armed; `pack`
is `SdfWorldTables.Pack`, mostly the changed mesh records
(`sdf.instances.records.changed` per frame; `records.posed` of them moved their
matrix alone and wrote only their pose words through `StagePose`). A draw riding
its frame slot moves with the slot's row and packs no record, so a moving body
counts in `sdf.transforms`, never in `records.posed`.

- **Shadow cost is per world, in the upload; read what each entry drew.** A world's shadows draw in its
  residency's upload (`SdfWorldShadows`), so `world.gpu-timing` shows no shadow pass; `world.residency`'s `shadows`
  line prints their measured GPU time, the moved changes, the anchors and each entry's state, and `world.rates`
  counts each entry's whole redraws, shares, rectangle redraws and holds. A whole redraw draws every caster in a
  light box at each mesh's coarsest level whose error fits a texel, or a group's floor where its cell does
  (`sdfSelectionBandKeeps`); the entry's `ended-` counts say whether the turn or the cell ends its holds, and `owedall`
  means a change no box bounds (`MovedEverywhere`) redraws it whole. The measured `tri` counts each phase a caster draws
  in (entry and exit layers), about twice the selection marks' triangles. The selection marks (`world.residency`'s selection line) name each drawing.
- **A window whose extent swings rebuilds on a backoff.** A window a moving
  camera crosses (the endless fall's floor and ceiling paintings) requests a new
  extent every few frames; a resize build the request outran is discarded, and
  each discard doubles the advances the node waits before the next resize build
  (at most 32), cleared by an install (`ShaderPipelineRenderNode.Build.cs`,
  `AnExtentSwingingFasterThanItsBuildsBacksOffAndASettledOneInstallsAtOnce`).
  Without the backoff such a window builds and discards a graph every frame.
  Holding a window's larger allocation instead (a shrink delay in the
  scheduler's footprint quantisation) keeps full-resolution windows rendering
  and makes the fall GPU-bound at 30 Hz.
- **One decision schedules a session view.** `WorldViewGraphHost.PortalSightOf` decides every
  session footprint and every root a filming camera gives a session it films (`RootFilmedViews`):
  the frustum test (`WorldPortalVisibility.Sees`, no facing test, since a slab's back and sides
  sample its image) against the consumer's current composed camera, then the views lever's hold of a glass
  whose width and height are both at or below `SdfUpdateRateTiers.ViewHeldPixels` of the consumer's image.
  `PublishPanes` finishes demand after camera placement; display shares are measured before small-glass pruning.
  A reappearing view releases its rate floor immediately. Visibility reads `IWorldViewScenes.TryVisibilityCamera`,
  never the last-rendered camera retained for picking; an unknown current camera keeps demand conservatively.
  A session rendering
  where nothing shows it is a demand that bypasses this decision: `world.budget`'s live frame line
  names each instance's demand (`root` for a filmed root), and `world.rates` lists held views as
  `@held`. A settled capture (`IRenderGraphInstances.SettledCapturePath`) turns the graph host's
  levers off. `SdfPortalSight` supplies completed per-consumer slab tests from the mesh pass's existing
  farthest-depth pyramid. `SdfMeshOcclusion` dispatches one additional workgroup through `sdfOcclusionMaySee`
  and reads its result only once its node proves the frame slot complete. A verdict reaches the host frames after
  its camera, so each test also asks `sdfOcclusionHiddenWithin` whether the slab stays hidden from every eye within a
  reach (`SdfPortalSight.Capture`: eight of the eye's latest steps, 5 cm to 2 m), reading the pyramid's nearest chain
  (header flag 64, built beside the farthest only for a pass with portal sight): it widens the slab's region by the
  parallax an occluder at the region's nearest depth can show across the reach, and needs that region whole on
  screen. `SdfPortalSight.Occluded` is the one door: a slab is hidden only for the camera its test ran for, or for a
  later eye inside that test's reach. The test takes the occluders' depth surface as closed across its steps, so a hole
  a nearer occluder hides from the tested eye is assumed absent; that is where the guarantee stops. A changed slab,
  unknown result, or replaced view/program retains demand. Occlusion is presentation evidence from an earlier completed
  frame; moving occluders carry that delay. Settled captures and non-occlusion cull modes bypass it.
- **A hidden window renders until its world is resident.** `WorldViewGraphHost.ReserveArrivals` keeps one window,
  and the windows on the way to it, at `WorldPortalVisibility.ArrivalShare` for each world a rendered view leads to
  whose `SdfWorldResidency.MeshesFirstArriving` holds: a group owing its first mesh or the first detail over its floor (`SdfMeshPathBake.OwedGroups`), or a staged draw waiting for its mesh; a retier or another residency's landing is no arrival. A
  world the sight has not wanted for `SdfWorldResidency.PressedParkSeconds` (`SightLetGo`) takes no window: the render
  observes its residency, so a kept window would hold a world nobody approaches from parking for good. A
  residency's bake samples and its meshes land only in frames that render one of its views: a frame that renders none
  still prepares it while it owes its minimum (`SdfWorldResidency.OwesPreparation`) and may pack, which moves what its
  background work binds. In `world.residency`, a residency
  whose `uploads` stand still while `unbound` in its `gpu-work` counts every pump and `owed` stays above zero is one
  nobody renders. `gpu.faults lose` makes every residency land again, a quick check of that path.
- **A view's storage follows what is shown.** A session view no schedule renders for
  `WorldViewGraphHost.ParkedShrinkFrames`, while its glass asks no footprint (out of sight, held or occluded), renders
  at its least share until its node installs that extent (`ReserveShrinks`, which reads the node's installed `Extent`,
  never the schedule's, and keeps every window on the way to a nested one rendering with it), so a window walked
  through gives back the per-pixel storage of the extent it was last seen at; the small image stays for the face's
  emission. A view the display reaches through a consumer that asks nothing of it reads `waiting`, not `unread`, so
  the count is of schedules that did not render it. In `host.memory families 0` the `session` family falls a few
  seconds after its windows leave sight. A new view kind that renders into an image of its own takes the same rule, or
  it holds a display's storage for a glass nobody sees.
- **A pass that reads far above its kernel's cost names memory first.** When the process's device ledger
  (`host.memory`'s `pool device` `peak`, plus `usage-beyond-ledger`) passes what the system budgets it (`segment local`
  `budget`), passes that touch moved memory wait for it, and `world.gpu-timing` charges the wait to whichever pass
  touched it: a nested view's `surface` once read 21 ms where the kernel shades the display in under 2. Read
  `host.memory` and `host.memory families 0` beside any such reading before reading the kernel; `host.memory peak`
  orders the families by what each held when the device last stood at the most it ever held (`at-peak`), which names
  the holders of a benchmark chapter's `deviceMemory.peakBytes`, where each family's own `peak` is taken at a moment of
  its own
  ([the frame budget](../../../docs/plans/frame-budget.md#where-the-memory-goes)).
- **A frame captures only what it reads.** `SdfWorldPasses.BeginFrame` serves every held residency's upkeep
  (`SdfWorldResidency.Upkeep`), captures the host residency, which places the cameras, and serves the minimum of the
  residencies that owe one (`SdfWorldResidency.OwesPreparation`): one residency a frame, the one longest past the
  slowest view tier's period without a capture, on a frame that rates views; every owed residency on a frame that
  rates nothing (the views lever off, a locked tick, a converging capture, an unpaced host). A day that turns keeps
  lighting settling for good, so a minimum served every frame captures every held world every frame. The runtime
  then seals its roots and footprints (`RenderGraphFrame.Seal`) and captures only the instances
  `RenderGraphScheduler.ReadersInto` marks (`IRenderGraphPackageFactory.PrepareRead`), in rounds until none is new, and
  asks `IsUnchanged` of those alone. The reader walk shares the scheduler's cadence and demand calculation. A rated
  view only asks for its residency when it is due. A graph installed mid-frame still stages
  late (`SdfWorldResidency.Staged`, counted). `world.counters sdf.captures` reads residencies held against captures
  run, the minimums served and held, and the residencies packed and uploaded beside others.
- **Residencies pack and upload on the pool.** A captured residency's frame is packed in
  `SdfWorldPasses.ReadsPrepared` and its upload recorded in `SdfWorldPasses.RendersScheduled`, each as one
  `JobClass.Frame` fork with a residency a partition; the steps before and after each fork stay on the frame thread
  ([runtime contracts](references/runtime-contracts.md#1-image-lifetime-is-leased-per-image-and-per-reader)). A
  caller that needs a residency's packed frame before the fork calls `SdfWorldResidency.Prepare`, which packs it
  there and then. `host.frames`' `pack` part is the frame thread's wall time across the pack fork, its own partition
  and the join included; the workers' share is not in any part.
- **A dress walks only what moved.** `SdfMeshDresser.Dress` skips its group walk while the bake's product revision and
  copy layout revision stand; `SdfMeshPathBake.Replace` (the group list's one writer), Restart's clear, the
  `Group.Mesh` setter, `ShowOwn` and `FinishReplacements` move the product revision, so a new change to what
  `FrameAccess` exposes (a group's shown mesh, its tier, the list) moves it through one of them. Rigid copies come
  through the composition source's copy journal (`SdfFrame.MeshCopyChanges`, a `GpuChangedRows` read by a
  `GpuRowCursor`), which the dresser and the bake's layout check each read. `sdf.instances.dress.groups-visited`,
  `.groups-stood` and `.copies-visited` count it. A slot's rows read its units alone (`DrawBinding.Repose`), so a slot
  that moved at the units its bindings were posed at walks none of them (`SdfMeshDresser.m_slotUnits`,
  `sdf.instances.dress.slots-spared`), and binding a row to a slot forgets that record (`ForgetSlotUnits`); a row that
  came to read anything else of its slot would stand stale, so it goes through `Repose` and that record. A rigid copy's
  root slot is past every group's, so the walk stops before it.
  The host draws arrive with a journal too: `SdfMeshDrawComposer` composes each producer's draws (a scene's statics
  and stamp pool, `WorldSceneMeshDraws`; the composition's emitters) and copies and owes only the rows a producer's
  journal names, which the dresser's host cursor reads.
- **A response swaps copies, never the program.** A delivery that moved only placement holdings stays out of the
  readers' rebuild watch (`WorldClient.HoldingRevision`, `WorldSessionMirror.ComposingRevision`; the delivered rows are
  judged, before live values are bound). A responding static row, or one riding a body, is a rigid copy whose catalog
  holds a template for every creation it can show (`WorldRigidAssets.ReconcileStatic`), so the sweep moves its copies'
  template range and pose alone (`WorldRigidAssets.PresentHoldings`, `PlaceStaticCopies`, `PackRiding`). The program
  composes again only through `HoldingComposeRevision` (a responding row the catalog refuses: glass, a pattern of
  riding occurrences, a riding creation with volumes) or `StaticTemplateRevision` (a template set the latest emission did not write); a new path that
  presents a response goes through one of the two. A solid row's response still rebuilds the presentation query field
  whole (`WorldFramePresenter.RebuildStaticField`).
- **A pack visits only what woke.** The stamp pool, its rigid bodies (`WorldRigidAssets.Pack`) and the catalog
  avatars (`WorldCatalogAvatars.Pack`) each keep an awake set, so a frame costs the registrations something woke, never
  the pool: a body's presented inputs through the source's journal (`IWorldStampSource.BodyChanges`, which
  `WorldClient` fills as it resolves poses and admits snapshots), a leased state cell through the mirror's change log
  (`WorldLeaseWatch`, `WorldStateMirror.TryChangedSince`), a replay cursor or cue coming due (a deadline heap; nothing
  walks the pool to tick it), and the registration's own continuation. The header of
  `WorldCreationInstances.Awake.cs` lists every cause and its one door: an input a pose reads that neither the
  journal nor the change log carries must wake its readers there, or a sleeping registration draws a stale pose. A
  held pose (`RateOwed`) sits in a 32 m cell; each pack tests each occupied cell's box once against the views and
  cascades, leaves the members of a cell nothing may show asleep and wakes the rest to rate themselves.
  `WorldCreationInstances.VisitCount`, `WorldRigidAssets.VisitCount` and `sdf.creations.registrations.visited` count
  the visits. The composition reads only the rigid copies a pack names (`ISdfSceneEmitter.MeshInstanceChanges`) while
  every emitter's `MeshInstancesRevision` stands.
- **The pose pack forks by registration.** `WorldCreationInstances.PackTransforms` decides every visited registration in a
  serial pass (`BeginStep`), poses the woken ones across the `JobPool` as one `JobClass.Frame` fork, a run of
  registrations a partition (`PosePartitions`, through `Pose` and `PackRegistration`), and settles them in pool order (`EndStep`). A partition writes only its own
  registrations and slot ranges and keeps its scratch on its own stack; anything shared it would write (a list, a pool
  field, a counter that is not interlocked) belongs in `BeginStep` or `EndStep`. A rig that reads another body
  (`Registration.ReadsBodies`) poses after the fork, so a new read of another registration's pose goes through a body
  read or it races. The state mirror's slot table takes a lease's first acquisition from many partitions under one
  gate (`WorldStateMirror.Acquire`); its install, refresh and apply never run during the fork.
  `sdf.creations.registrations.forked` counts the forked poses.
- **A program's bake plan is made on its build thread.** The composition
  source hands each program its background build finishes to
  `ISdfFrameSource.ObserveBuilds`, the residency plans it there
  (`SdfMeshPathBake.PlanAhead`), and the first frame that draws it re-plans only
  the groups it cannot keep (`SdfMeshBakeGroup.Refresh`). Editing either file
  changes the bake identity: check `puck mesh identity` before booting.
- **Static placement emissions survive program rebuilds.** `WorldStaticPalettes`
  keeps each immutable row's `SdfProgramBuilder.Emission`: instructions and
  profile tables, plus its volumes and mesh draws. A response emits the swapped
  rows alone. The cache also checks the prototype, palette ids and extent,
  resolved frame, text catalog and isolation mask. Retained recolor windows
  bind to the new builder's material scope; never retain the old builder through
  a scope. A same-sized mesh draw list stages changed records even when meshes
  swap, while the arena leaves existing mesh addresses standing.

## Seeing a change

- A lockstep frame waits for its composition's program build and its mesh bake.
  A completed mesh is still pending until `Dress` includes it: a load can finish
  between dressing and preparation, and testing only the worker would certify
  the partial mesh list. `IRenderGraphPackageFactory.WaitingOf` keeps a rendered
  package incomplete while its residency's bake is pending. The host
  holds that tick; exposure adapts once the complete frame supplies its meter.
  A capture freezes the authored frame, then dresses newly completed meshes.
- Lockstep irradiance waits for both the current static scene's build and its
  probe trace (`SdfMeshIrradiance.Stage`). Waiting only for the trace can render
  no volume on the first frame, or the preceding scene on a replacement frame.
  The CPU irradiance law compares the first staging of each against a completed
  trace; looping staging calls until a write appears would hide that defect.
- An image source owns its extent even when a buffer graph consumes it. Propagate
  its demand without multiplying by the buffer consumer's zero pixel footprint
  (`RenderGraphScheduler`), or a cold conversion can finish building and never
  install. A package whose last frame is incomplete cannot stand unchanged
  (`RenderGraphRuntime.CollectUnchanged`): its pending sources still need renders.
  A retained "building" reason alone does not prove a worker is still building;
  inspect the live schedule and completed build as well.
- Screen-light reproduction must compare source bytes, not just graph publication
  numbers. `MachineVideoSourceUpload` numbers uploads, while a queued machine's
  video read takes the native frame due at the position its world's machines
  present (`WorldMachinePresentation.PresentedTicks`), which outside lockstep follows
  host time. Equal publications can therefore feed different images to
  `ScreenEmission`. Lockstep uses the existing
  synchronous machine advance before rendering. A fence wait, readback or GPU
  validation can mask this worker race by changing pacing; diagnose the CPU
  video bytes and native frame version before changing GPU barriers.
- Build: `dotnet build src/Puck.SdfVm -c Release -nodeReuse:false` runs DXC
  (needs `dxc` on `PATH`). A Windows build of one project builds DXIL only, and
  a World run of it refuses `--backend vulkan`; run Vulkan from a solution
  build or with `-p:PuckShaderSpirvEnabled=true`. A build of one suite under
  `tests/` builds SPIR-V (`tests/Directory.Build.rsp`). Shader outputs come
  from the per-user shader cache; never copy bytecode between trees.
- Run it and look: start a World with `puck_world_start`
  ([puck-world](../puck-world/SKILL.md#the-live-world-is-shared); `windowed: true`
  only for a granted GPU leg), edit, `world.shaders.reload`, capture, and end it
  with `puck_world_stop`. A claim about
  how something renders rests on an inspected capture; say which backends you
  looked at. A lane without a GPU leg builds, boots headless once and asks for
  the windowed check as a verification request; the lead's batch pass boots the
  debug world windowed on Vulkan as well whenever the batch changed rendering
  or GPU code (AGENTS.md). A boot bakes the groups the mesh store does not hold; a reboot of
  an unchanged world loads its completed meshes, and an empty `--cache-dir`
  bakes all of them. A cold debug-world bake can span thousands of rendered
  frames, while simulation advances many ticks per frame. Before capturing the
  finished scene, use `world.wait bakes <seconds>` and require its settled
  result, then check for `[sdf.mesh-gi] done` in the log. A tick wait or
  `world.wait ready` alone does not establish that all meshes are present.
  The debug world's full probe convergence takes minutes after the bakes
  settle, and before it a view shows the sky-only ambient: whites and shadowed
  faces read blue-grey where the converged lawn bounce reads warm green-yellow.
  A backend pair captured without lockstep at different convergence states
  looks like a colour divergence between backends; it is not one (`world.indirect off`
  reproduces the unconverged look). Compare backends under lockstep, which waits for the trace.
  A byte comparison sets `world.lockstep on` before the first tick in both legs,
  then waits for bake settlement before requesting the still. An ordinary settled screenshot converges temporal views
  but does not request `SdfFrame.ConvergesLighting`; it can capture before a background GI
  trace finishes. Pin the camera and tick in both legs and use lockstep's
  held scene/refill/trace drain for the comparison: the frame answers waiting with the
  stage it waits for (`SdfMeshIrradiance.Convergence`) while the loop holds the tick, and the console answers between.
  A program or presentation-field build answers the same way and never blocks the frame: a frame source names the
  build in `ISdfFrameSource.Waiting` (filming nothing while no program fits the slot layout), the residency keeps its
  frame and withholds its views (`SdfWorldResidency.SourceWaiting`), and `world.wait` releases naming it.
  Keep each code build's completed private mesh store for warm comparison
  legs: sharing one directory does not share payloads across changed bake
  identities, and silently turns the second leg into another cold bake.
  `world.rate pause` also stops tick waits: several timing reads separated by
  `world.wait` then repeat the same completed GPU window. Use separate control
  requests spaced in wall time when measuring a paused scene.
- Device-free suites: `dotnet test tests/Puck.SignedDistance.Tests -c Release --no-build`
  after a `-nodeReuse:false` build (ISA packing, Lipschitz, bakes) and `tests/Puck.Shaders.Tests`.
  Anything that answers a bake lattice on the CPU (`MeshCpuBake`, the SdfVm laws' kernel stand-ins) places
  each sample with `SdfMeshBakeRequest.SampleAt`, as the kernel does: a slab keeps its final lattice's
  origin and counts its planes from `Slab`, so a copied grid formula answers a slab at the wrong planes. Its
  gradient taps lie `SdfMeshBakeRequest.TapSpacing` out, which the kernel reads from the lattice's record;
  the fixed-point evaluator widens only what it cannot resolve (`SdfMeshCpuField.TapSpacing`), and
  `mesh audit --cpu` counts those samples on a `cpu taps:` line. Cross-backend
  parity (`puck parity --world <file>`) needs a parity world with its contract
  beside it, and none exists while the debug world is the one live world; until
  one does, compare the two backends' captures and counts yourself.
- Capture fixtures in a sandbox without a desktop monitor must replace both
  the debug desktop graph and its screen producer with `source.color`, and
  remove that screen's `padPointer`. Offscreen hosting alone still tries to
  open the authored capture source. Use a private copy of the whole package
  with the same entry filenames: a renamed wrapper changes document identity,
  so garden/night back-references can instantiate another debug world. Keep
  device-machine screens intact and disclose the desktop substitution in the
  capture report. After a suit change, allow its rule to run and check the
  residency's `[sdf.mesh-bake] done` log before capturing with
  `world.wait bakes <seconds>`: a tick wait alone can show the old mesh
  stretched over the new rig. Clear an operator camera with
  `view.override camera auto` before switching to a following motion layout.
- GPU captures, device faults and the validation layers:
  [kernels](references/kernels.md#captures-faults-and-validation).

## References

| File | Read it for |
|---|---|
| [references/kernels.md](references/kernels.md) | The shader build, hot reload, the frame's passes, the visibility record, bindings, diagnostics, captures and device faults |
| [references/sync-pairs.md](references/sync-pairs.md) | Every C#↔HLSL coupling and which side is generated |
| [references/mesh-bake.md](references/mesh-bake.md) | The bake's contouring, groups, mesh region, store and `puck mesh audit` |
| [references/mesh-gi.md](references/mesh-gi.md) | Probe visibility, the volume boundary, CPU bounce sampling and exposure |
| [references/gpu-cost.md](references/gpu-cost.md) | A kernel's registers and occupancy (Nsight GPU Trace, PIX, RenderDoc), divergence, coalescing, groupshared banks, wave intrinsics, half precision, dispatch limits, CPU↔GPU waits and GPU allocation |
| [references/runtime-contracts.md](references/runtime-contracts.md) | The render-graph runtime's invariants, before changing `RenderGraphRuntime`, a recorder, a capture path or pacing |

## Route adjacent work

| Skill | Route there for |
|---|---|
| `sdf-authoring` | Sculpting creations: primitives, blends, palette, rigs, bake work and bytes. |
| `puck-world` | What world-document sections mean and console verbs' semantics. |
| `puck-dsl` | `.puck` grammar and the compile/lint verbs. |
| `maths-usage` | Fixed-point primitives for the query evaluator. |
| `dotnet-performance` | Host-side C# hot paths. |
| `platform` | Native windows, display facts (VRR, colour space, display power), precision waits, camera rings that feed shared textures. |
| `physics` | Contact against the authored field, which the drawn mesh must agree with. |
| `verification` | The landing bar, GPU legs and how to read a failed check. |

