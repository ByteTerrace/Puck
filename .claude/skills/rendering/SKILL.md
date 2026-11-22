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

The tables build each view's signature inputs once a frame: its cascade fit and
its pass block with the hashes every pass group takes of it (`SdfWorldTables.CascadesOf`
and the memo beside it). The renderer's passes read the fit through
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
force every pass. Only the whole view, which sees no publication, renders while a
screen shows a source (`SdfWorldTables.ForcesRender`).

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
the new buffer's authority. Native sparse mapping remains in the streaming plan.
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
to the root. Volume overflow is explicit. Scope depth is 4, screen bindings 32,
and derived faces follow authored screen indices. `SDF_MAX_INSTANCES` bounds
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
replace a region in the owner. A fixed policy (the always-staged mesh region, uniform frame blocks) uses the policy
constructor. Round a constant-buffer block with `IGpuBindings.ConstantBufferBytes`. What `Grow` returns retires
after the submissions that read the old buffers (`SdfWorldTables.Retire`).
A staged region created `mapped` (the mesh region, the probe scene) asks `IGpuBufferFactory.TryCreateMapped` for a
destination that reserves `GpuRegion.MaxByteCount` and backs 1 MiB chunks on demand (`IGpuMappedBuffer`; Direct3D 12's
`DirectXGpuMappedBuffer` at reserved resources tier 2, tile runs from `DirectXHeapArena.PlaceTiles`; Vulkan answers
none and keeps a contiguous destination). It grows and shrinks in place, with no carry and no second buffer, and the
selector leaves no replacement room for it (`SdfResidencyGrant.Mapped`). Its buffer's length is the reserved range, so
never map a region a kernel sizes with `GetDimensions` (the irradiance feedback). `SdfWorldTables.CoverMeshRegion` is
the one place the mesh region's interior backing follows the arena: a chunk no placement or draw record covers is
unmapped behind its readers, and one a placement needs again is mapped before any landing leases it.
It gives chunks back before it backs any, and a chunk the device refuses makes it forget the meshes no draw names
and try again every prepared frame (`SdfWorldTables.CoverRefusedBacking`) as the given-back chunks retire. A mesh named ahead that lies in a
refused chunk is refused ahead (`SdfWorldTables.RefusesAhead`), so it takes the draws and the mesh it replaces gives
up its memory; a product waiting on words the device will not back would otherwise hold the bake pending for good.
The region's limit (`SdfResidencyGrant.ReserveGrowth`, stored as `LimitBytes`) is the one number both the arena
refuses by and the bake's budget is held under (`SdfResidencyGrant.Budget`, at most `SdfMeshTier.BudgetEighths` of
it), and a refused mesh presses `Retier` on its own, so pressure raises the detail bias before it leaves a hole. The
selector's pool share alone can pass a region's limit (it counts every residency's reservation as room), and a
budget read from it lets the arena refuse whole places while the bias stays at zero.
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
itself before that table, in its holdings and in its loss. Never add a `TryWaitIdle` to a release
path; `SdfWorldTables.Dispose` and `IGpuSurfaceUpload.DisposeCompleted` wait for nothing. A render node's build in
flight retires into its runtime's `GpuImageLeases.RetiredBuilds`, which `DrainDisposals` waits out before the device
goes. A package recorder never sees a fence: a result its recording captured (a readback, a publication, a
submission's shading identities) lives in a `RenderGraphSlotResults` and is read only when the node reports the slot
complete (`IRenderGraphPackageReadback.Completed`), which the node does from `WaitSlot` as it reuses the slot or as
soon as an owed slot's fence reads signaled (`ShaderPipelineRenderNode.PollReadbacks`), and before it disposes a
retired graph whose submissions completed. Buffers a recording replaced retire at that slot's next completion
(`SdfMeshOcclusion.Completed`). A reusable slot fence reads signaled after an abandoned submission and after a loss's
drain, so polling one is never a proof.

## Far-cascade caster selection

`SdfMeshOcclusion` and `sdf-mesh-occlusion.comp.hlsl` are the one classification
door for camera and cascade draws. The far cascade reads the current mesh target
through a graph dependency. `sdf-caster-receivers.hlsli` reprojects every covered
camera sample with its render grid and jitter into light-space occupancy cells;
OR-reduced parents conservatively cover a query rectangle. Expand caster boxes
by `SdfMeshCull.ShadowFilterTexels`, the complete search/filter and normal-offset
reach, before querying. Keep unboxed draws. Camera invisibility of a caster is
never evidence that it cannot cast a visible shadow.

Entry depth uses the camera pass's two-phase mechanism in the light's orthographic
projection: draw previous survivors at current transforms, build the maximum
forward-depth pyramid with infinity for holes, then draw new survivors. Phase
indices have one meaning: early entry, late entry, solid exit, thin exit, optical.
Exit (and a future identity peel) and glass lists depend only on box/receiver
selection, never entry visibility. They execute after entry depth is final.
Never use an earlier exit/peel image to reject a caster.

A receiver-conditioned cascade retains a coverage identity with each submitted
layer (`SdfWorldPasses.ReceiverSignatureOf`): the sample grid its receivers were
classified on (cull level, detail, extent, scale, cut, bindings), excluding the
light, the camera, the geometry and the jitter. The far rate can hold, or refresh
over the static layer, while both layers cover that grid; a changed grid refreshes both. The hold's own
camera tolerances and the receiver cells' neighbouring-cell margin cover the
drift the identity leaves out; a receiver a moving caster uncovers lags by the
same refresh its shadow does. Hashing the geometry signature or the jitter here
ends the hold on every frame anything moves, so a receiver-conditioned cascade
then pays a whole refresh each frame (`sdf.rates` shows `rendered` and `static` on every
frame and no `held`). Readers use each layer's committed light
camera. The graph keeps the sampling barrier, while the package projects its
cadence dependency onto the consumed receiver inputs, so unrelated mesh writes do
not invalidate it. `world.cull camera` removes the receiver input and disables cascade
depth occlusion, retaining the original far rate and camera occlusion. Count held
only when the pass stands, and rendered/split only on successful submission.
For a complete-refresh cost comparison, set `world.rates shadow-far off` on
both sides. Also measure ordinary cadence: replacing a split refresh with two
culled layers can cost more in a view with little rejection even when a whole
refresh costs less. Report those separately and read `sdf.rates` beside the
draw/vertex counters; a split frame's counts cover only its submitted layer.
Compare counts beside time and shadow diagnostic captures on both backends;
include a moving camera and reduced/temporal render grids. The receiver scratch
shares the depth allocation and its existing slot-fence retirement; early
compute samples camera depth, late compute samples light depth, in separate sets.

A far cascade at a reduced rate holds a static layer (`SdfFarShadows.HoldsStaticLayer`; the far pass's
`ShadowFarStaticDepth` and `ShadowFarStaticExit` outputs, `SdfWorldPackage.WithShadows`' `farStatic`). Its invariant is
that the layer is copied only into a cascade drawn from the camera it was drawn from, holding the static revision it
drew; `SdfWorldPasses.RecordFar` is the one door (`SdfFarStaticLayer`, its `SdfFarStaticUse`), and `SubmittedFar` commits the
layer. The recorder stages the static draws through a second `SdfMeshOcclusion` (`SdfMeshMotion.Static`, its own mesh
set group after the cascade's, `OcclusionGroups`) and the moving draws plus every glass draw through the cascade's own
(`SdfMeshMotion.Dynamic`) over a `CopyDepth` of the layer; the kernel leaves a draw a motion filter excludes untouched.
A refresh that ends the fit (`DecideFar`'s covers false) bypasses the layer (`SdfFarStaticUse.Bypasses`): every caster
straight into the cascade, no static selection and no copy, and the layer stops standing until the next refresh the rate
alone calls for draws it again; a layer drawn on a refresh that ends the fit would stand for nothing when the next one
ends it too, as a walking camera does. So under a turning sun the authored `farShadows.turnDegrees` is the layer's
lifetime. A path that draws the layer must call `SdfWorldTables.CastersRead`, or a caster absent then never redraws it on landing.
The static selection's late step samples the layer it just drew, an access the graph plan never sees; it binds because
`ShaderPipelineRenderNode.UsageOf` creates every depth sampled. A depth created without `Sampled` is a `D32_FLOAT`
texture, and Direct3D 12 removes the device (`DXGI_ERROR_INVALID_CALL`, no breadcrumb) on creating its shader resource
view, so `DirectXGpuBindings` refuses that write by name.
`sdf.rates`' `shadow-far.static` counts the layer's redraws.

## The distant shadow map

The cascade set's fourth entry (`SdfMeshShadowCascades.DistantEntry`, primary role) is the far shadows past the
far cascade, authored per device row (`WorldQualityPreset.DistantShadows`) and at boot (`render.distantShadows`),
carried on `SdfFrame.DistantShadows`; the [handbook](../../../docs/rendering/sdf/handbook/frame-rendering.md#the-distant-shadow-map)
owns the behaviour. Its invariant is that a held map draws only what cannot move while it stands: the
`shadow-distant` pass stages `staticOnly`, so the selection kernel rejects every record carrying
`SdfMeshRecord.DynamicFlag` (set from `SdfMeshDraw.IsStatic` in `SdfMeshRegion.WriteDraw`, derived so a skinned or
displaced draw, which moves without its record changing, is never static), and its
signature (`SdfWorldPasses.DistantSignatureOf`) folds `SdfWorldTables.StaticDrawRevision`, which only
`StageChangedDraws` and `Unname` advance. A new path that changes a static draw's record outside those two leaves a
stale map standing. The revision follows casters, not records (`SdfMeshDraw.SameCaster`): a tier swap within one
bake group (the same `Trace`) and an eviction leave it standing, and a landing advances it only when the caster never
stood or the distant pass recorded while it was absent (`SdfWorldTables.CastersRead`, called beside `RecordDistant`).
Under the standard's day (0.25° a second) the authored `turnDegrees` sets the redraw rate: 0.1° redraws 2.5 times a
second, and `world.gpu-timing` keeps a standing pass's last samples (as it does glass's), so its `shadow-distant`
figure is a refresh's cost, not a frame's; `world.counters sdf.rates` counts the refreshes. Readers take the camera through `CascadesOf`, which reads the committed one
(`SubmittedDistant`, which like every package commit runs only once the frame's submission reached the queue:
`ShaderPipelineRenderNode.CommitPackages`) or none; an entry without a committed camera writes no role (`SdfFrameBlock.WriteMeshShadows`
through `SdfMeshShadowCascades.Holds`), and the surface pass reads no optical term for it. Because its count makes
the set four long while the secondary entry may be empty, test entries with `Holds`, never `index < Count`.

A redraw spread over `distantShadows.frames` frames (`SdfDistantShadows.Slices`) draws one share of the casters a
frame into the back map (`SdfWorldPackage.Parts.ShadowDistantBackDepth` and `BackExit`, allocated only while the map is
sliced, `SdfWorldPackage.WithShadows`), every share from the camera the redraw started from, and the last share copies
the back map into the map receivers read (`SdfWorldPassRecorder.CopyDepth`, `IGpuRecorder.CopyImage` on two depth
images). The selection kernel keeps the draws whose index leaves the share's remainder (`SdfMeshOcclusion.Stage`'s
`slice` and `slices`), so the shares draw every draw once. The invariant is that receivers read the map only through a
committed camera: `SdfWorldPasses.RecordDistant` stages a camera for this frame's readers only when the drawing
completes a map (`SdfDistantDraw.Completes`), and `SubmittedDistant` advances the redraw past a share only on
submission, so an abandoned share is drawn again. A graph still without the back map draws a chosen share whole
(`RecordDistant`'s `hasBack`). A sliced map holds only while the static scene stands at the revision it committed
(which `SubmittedDistant` commits beside the camera), so a static change starts a sliced redraw rather than a whole one through the
signature; a change during a redraw is redrawn by the next decision. `world.rates` counts each share that completes no
map as `shadow-distant.share`.

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
| Run GPU work off the frame (a bake, a relight) | `SdfGpuWork` and its clients, `SdfMeshBakeGpuWork`. A client's per-slot buffers come from the recording (`SdfGpuWorkRecording.HostBuffer`, `DeviceBuffer`), never the buffer factory: that door reserves them (`SdfGpuWorkHold`). A client cuts each job to the allowance its take is handed (`SdfGpuWorkAllowance`: what the charged jobs in flight leave of `SdfGpuWorkBudget.FrameAllowanceSeconds`, the share the frame leaves free, which `WorldBackgroundAllowance` feeds from every node's timestamps) at its kind's `SdfGpuWorkRate`, and while work is queued takes no job priced past it, deciding before it takes anything; never a fixed share. An arrival's bake work (a group drawing nothing yet, or the first detail over its floor) owes the frame and takes the GPU the frame leaves idle between frames, from a quarter of the period up to one period, under its own allowance (`ISdfGpuWorkClient.TryTakeOwed`, `SdfGpuWorkBudget.OwedAllowance`), outside the credit, while ordinary background work keeps the frame allowance; so do the landing of a mesh a staged draw waits for and a seen irradiance volume's first light (`SdfMeshIrradiance.OwesFirstLight`), each a client whose every job is then owed (`ISdfGpuWorkClient.OwesAll`). The residencies' services divide the budget by their display share, never by pump order: each holds a turn (`SdfGpuWorkBudget.Join`, weighed by `SdfGpuWork.Weigh` from `SdfFrame.DisplayShare`) that every budgeted step, owed or not, asks first (`SdfGpuWorkBudget.Takes`, `work-turns` and `turn-holds` on `world.residency`). The budget counts frames background work may have cost (`FramesMissedBehind`, `frames-behind` on `world.residency`) | `src/Puck.SdfVm`, `src/Puck.World.Presentation/WorldBackgroundAllowance.cs`; [the bake's dispatch](#which-dispatch-owns-the-code) |
| Have the frame thread take work up while it waits between frames, or wake it for a GPU completion | `HostWaits.Serve` (a `WaitService`, served by `HostWaits.Pace`) and `IGpuSubmissionFence.SignalOnCompletion`; never a poll loop or a thread of your own that records. A wait inside the frame's open batch (a residency's staging, `SdfGpuWork.Drive`) hands its submissions off first (`IGpuSubmissionFence.HandOff`), or the completion it sleeps on never comes before the flush | `src/Puck.Abstractions/Waiting`, `src/Puck.Abstractions/Gpu/Device` |
| Compute irradiance, and check it on the CPU | `SdfMeshIrradiance`, whose CPU reference shares one probe lookup with the relight and the views shader | `src/Puck.SdfVm`; [mesh GI](references/mesh-gi.md) |
| Meter a view's picture for its exposure (histogram, percentiles, dead band, cut snap) | the composite's histogram (`passes/sdf-composite.comp.hlsl`), read back by `SdfSkyRecorder`, reduced by `SdfExposure.Meter`, followed by `SdfViewExposures` | `src/Puck.SdfVm`, `src/Puck.SignedDistance`; [the decision](../../../docs/decisions/rendering.md#lights-screens-and-the-camera-are-photometric) |
| Convert an image source's color or encode a display output | `ImageSourceConversion` and the display encode, each paired with its HLSL | `src/Puck.Abstractions/Sources`; [sync pairs](references/sync-pairs.md#image-sources) |
| Encode or decode the sRGB curve, or turn an authored hex colour into light | `SrgbTransfer` (extended, the twin of `Shared/transfer.hlsli`); `HexColor` parses to a `DisplayColor`, whose `ToScene` makes the `SceneColor` shading reads | `src/Puck.Maths` |
| Convert a view's scene light for the display (sharpen, bloom, tone curve, encode) | `SdfWorldPackage.WithDisplay`, its `display` pass (`passes/sdf-display.comp.hlsl`, `Shared/display-transform.hlsli`) and `SdfOpticsRecorder` | `src/Puck.Shaders.Model/Graph`, `src/Puck.SdfVm` |
| Share a constant or a layout between C# and HLSL | a generated declaration (`SdfIsaHlsl`, `ShaderDeclarations`) first; a hand pair only where the table lists one | [sync pairs](references/sync-pairs.md) |
| Add a pipeline variant to a pass | `GpuPassPipelineKey`: a field that changes the pipeline enters the key, or leases share the wrong pipeline | `src/Puck.Shaders/Pipeline` |
| Let an author program how a surface shades | `SdfShadeProgram` (the op table, admission, cost) | `src/Puck.SignedDistance/Shading`; [authored shading](references/authored-shading.md) |
| Read how a device can back a large buffer (sparse binding, reserved resources, memory priority, pageable device-local memory, storage-buffer array indexing) | `GpuDeviceCapabilities.Backing` (`GpuBackingSupport`) and its native figures, read at device creation and printed on `world.counters gpu`'s `capabilities` line | `src/Puck.Abstractions/Gpu/Device`; [streaming plan](../../../docs/plans/streaming-and-memory.md#the-prototype-as-built) |
| Make a kernel variant depend on something only some devices have (ray queries), or withhold it to run the fallback | `GpuOptionalCapabilities`, read into `GpuDeviceCapabilities.Optional` at device creation (Direct3D 12's options 5 tier, Vulkan's optional extensions in `VulkanLogicalDeviceFactory`); a variant names it in `SdfKernelSet.RequiresOf`, `SdfWorldPipelines.Acquire` leases it only where `SdfWorldPipelines.Runs`, its caller asks `SdfWorldPipelines.Leases`, `VulkanShaderCapabilities.Optional` admits its SPIR-V capability on that device alone, and the World's `--withhold-gpu <capability>` leaves it off | `src/Puck.Abstractions/Gpu/Device`, `src/Puck.SdfVm` |
| Build, compact or bind an acceleration structure the ray hardware walks | `GpuDeviceServices.AccelerationStructures` (`IGpuAccelerationStructures`: size, create, scratch, `GpuAccelerationStructureInstance` records, the descriptor of a `GpuBindingKind.AccelerationStructure` binding) and the recorder's `BuildAccelerationStructure`, `WriteCompactedSize` and `CompactAccelerationStructure`, which carry their own barriers; a buffer a build reads declares `GpuBufferUsage.AccelerationStructureInput`. Both backends refuse every call on a device without `GpuOptionalCapabilities.RayQuery` | `src/Puck.Abstractions/Gpu/Rays`, `DirectXGpuAccelerationStructures`, `VulkanGpuAccelerationStructures` |
| Read what the device lets the process use per segment (a DXGI segment group, a Vulkan heap), or charge a device allocation to the physical memory it was placed in | `IGpuDeviceContext.MemoryReading` (refreshed by `RefreshMemoryReading` on the frame thread), `GpuMemorySegments` (the one place a segment's pool is decided), `GpuDeviceMemoryWork.CountAllocated`'s segment argument (Direct3D 12's through `DirectXDeviceMemory.SegmentOf`, Vulkan's through `GpuMemorySegments.OfMemoryType`); `MemoryAccount.ReadPools` and `host.memory` print them | `src/Puck.Abstractions/Gpu`, `src/Puck.Hosting/Jobs/MemoryAccount.cs`; [streaming plan](../../../docs/plans/streaming-and-memory.md#step-1-as-a-brief) |
| Grow or shrink a large device buffer without holding a second copy, or give back its unused interior | `IGpuMappedBuffer` through `IGpuBufferFactory.TryCreateMapped`, used through a `GpuRegion` created `mapped` (`Grow`, `Shrink`, `MapRange`, `UnmapRange`) | `src/Puck.Abstractions/Gpu/Residency`, `src/Puck.DirectX/DirectXGpuMappedBuffer.cs` |
| Reserve device memory before allocating it, share a pool between residencies, or hold detail to a budget | `MemoryAccount.ReserveDevice` (a holder's peak, under the owner its objects are named for, `GpuObjectName.MemoryOwnerOf`), `GpuDeviceMemoryWork.TryAdmit` (the one door every native allocation asks; `CountAllocated` takes only its `GpuMemoryGrant`), `GpuMemoryAdmissionScope` (opened around the synchronous allocation of a replacement its holder can do without, so a full pool refuses it instead of admitting it past the limit), `SdfResidencySelector` (the one detail bias, each bake's budget, each mesh region's reservation, `SdfGpuWorkBudget`); never a share of `GpuMemoryProfile` | `src/Puck.Hosting/Jobs/MemoryAccount.cs`, `src/Puck.Abstractions/Gpu/Counters`, `src/Puck.SdfVm/SdfResidencySelector.cs`; [streaming plan](../../../docs/plans/streaming-and-memory.md#step-3-one-reservation) |
| Read where frame time goes | `world.counters gpu`, `world.gpu-timing`, the bake's `gpu-work=[…]` line | [performance and cost](#performance-and-cost) |
| Know which draws a raster pass selected and at which level (triangles drawn by level and tier, mesh bytes no pass selected) | The occlusion kernel's selection marks, one stage in `SdfWorldTables.SelectionMarkFrames`, read back with the portal sight verdicts through the slot's one readback (`SdfMeshOcclusion`) and read against the row epoch (`SdfWorldTables.ObserveSelectionMarks`); `world.residency`'s selection line | `src/Puck.SdfVm` |
| Show something where a group's detail has not landed or was refused | The group's floor: planned first on an arrival in view (`SdfMeshDemand.Sees`; one out of view bakes its detail directly, behind the groups in view), kept while detail draws, evicted to on refusal (`SdfMeshPathBake.Floor.cs`, `Group.KeptFloor`); `world.residency`'s floor line | [mesh bake](references/mesh-bake.md#groups-poses-and-materials) |
| Name a backend's compiled-kernel extension, or its target from an extension or some bytecode | `ShaderBytecode.FileExtension` (the extension), `ShaderTargets` (`Of` a backend or bytecode, `FileExtension` and `DeployedPath` of a `ShaderTarget`); never a `.dxil`/`.spv` ternary or string compare. A kernel-loading API (`SdfKernelSet.Load`, `SdfWorldPipelineCatalog.LoadDeployed`, `GpuRegionCopyPass`, `SdfMeshRasterPass`) takes a `ShaderTarget`, never an extension string | `src/Puck.Abstractions/Gpu/Shaders`, `src/Puck.Shaders.Model/Compiler/ShaderTarget.cs` |
| Build a render-graph package's one cached pass pipeline and recover it in `Create` | `PassPipelineBuild.AcquireAsync` and `Of` | `src/Puck.Shaders/Graph` |
| Keep a graph replacement from holding two whole graphs, or decide whether the budget may refuse it | `ShaderPipelineRenderNode.CarriedStoragesOf` (the one door a replacement takes over the installed graph's storage through, matched against the instances' own extents and sizes), `ReplacementGivesWay` (a graph whose counts moved cannot render, so its replacement is the instance's least and only a cap refuses it), `InstancesOf` (history only compute touches keeps two instances); `pipeline.inspect <instance>` reads the result | `src/Puck.Shaders`; [memory budget](../../../docs/reference/shaders.md#memory-budget) |
| Read a presented surface back to CPU pixels in a backend's presenter | `PresentationCapture` | `src/Puck.Shaders` |
| Size a bake lattice over a box at a cell, or find a sample's point | `SdfMeshLattice` (`Dims`, `Over`, `PointOf`) | `src/Puck.SdfVm` |
| Read a slot's pose the way the bake does (unset orientation is the identity), or carry a part's or group's box through one | `SdfMeshGeometryBuilder.PoseOf`, `SdfMeshBox.OfPart` (a part in its group's frame) and `SdfMeshBox.Carried` (a slot-frame box to world); never an inline `Orientation == default` test | `src/Puck.SdfVm` |
| Pack or unpack an octahedral normal as signed-normalized N-bit fields | `SdfOctahedral.PackSigned`/`UnpackSigned`/`SignedScale` | `src/Puck.SignedDistance/Illumination` |
| Divide by a fixed divisor, mix or hash packed keys, or compare keys, inside bake or presentation code | not rendering: the `Puck.Maths` primitives, which `maths-usage`'s Choose table lists | [`maths-usage`](../maths-usage/SKILL.md#choose); `puck scan --only duplicates --against <base>` before landing |

## Which dispatch owns the code

A view is a chain of separate dispatches: `mesh` (graphics, in two occlusion
phases with the occlusion kernel between them, `SdfMeshOcclusion`) →
`shadow-near`, `shadow-far`, `shadow-second`, `shadow-distant` (graphics, the far cascade reading
current camera receivers, the distant map held between changes) → `pyramid` (the ambient occlusion's
depth pyramid from the mesh target) → `surface` (each pixel's visibility record from the mesh
target: normal, screen-space AO, the cascades' soft shadows into K;
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

The mesh bake's two kernels (`sdf-mesh-bake-mask`, `sdf-mesh-bake`, the one
`mapCore` call site) are no pass of a view: they run as the residency's
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
  `shadow` shows the average without its cosine. The average also leaves out neighbours whose normal
  turns away, so a crease's two lit faces keep their own shadows. A sunlit spot on a wall's top under a slab is the
  filter's (`sdfMeshShadowGap`): a tap counts the light hidden only inside the
  light's cone over the point, and the penumbra is the nearest in-cone
  blocker's. `world.debug-view ao`
  and `world.debug-view shadow` show the filtered values, and `world.ao` toggles
  the occlusion live.
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
  (`SdfWorldTables.RowSelection`) and moved as its slot moves (`NoteMovedSlots`).
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
  bias and keeps samples the current neighborhood misses. Moving and reactive
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
  points, and the bound, non-black screens the boxes reach in front of. A mask
  may leave out only a light that adds nothing at the points its lanes shade,
  so a new light kind or reach (an authored falloff past `Param`, a screen
  that lights behind its face) widens the tile test in the same change, or
  tiles cut its light off. `SDF_VIEWS_OMIT_LIGHT_TILES` reads the tiles' saving. Index a vector at
  run time only through `sdfComponent`.
- **Cel shading is a style per material, not a whole-image filter, and the
  banding is a program.** The standard's `cel` shade program
  (`worlds/standard/shading.puck`) is the one implementation of the band; no
  kernel bands. A palette row's `toon` (`SdfMaterial.Toon`, material rows 22 and
  25) is the style row the engine keeps beside it: the styled mark and the ink.
  Every other material inherits the world's default (`render.toon`) through the
  pass block's `toonOutline` and `toonInk`, both written by `SdfToon.WriteRows`
  and read by `sdfToonStyle` and `sdfToonAuthored` (`shade/sdf-toon.hlsli`):
  keep the row layout and the pass block's alike. `world.toon off` writes a
  negative default mode, and `sdfShadeBinding` then marks a styled material's
  binding unstyled: `sdfShadeHas` reads its light and compose phases as omitted
  while its surface phase (a terrain under cel) still runs.
  A program that omits the surface phase keeps the material's layers
  (`sdfShadeHas(binding, 0u)` in both stages; `SdfShadeProgram.Authors` on the
  CPU, which `SdfAppearance.ResolveShadeReads` reads). The program's result is
  scene light for the display pass's one `render.tone`; never expose or
  compress inside a material. `sdf-toon-outline.hlsli` reads depth/normal
  edges in views, before temporal resolve, at the pixel's own style's width.
  Keep screen pictures, portal pictures, sky and self-emission out of both the
  banding and ink. The tuning belongs to the lighting cadence signature, never
  the bake closure.
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
- **The irradiance volume is gathered once a cell and read once per shading sample.** The
  probe lookup pass (`views-probes`) runs `sdfMeshProbesAt` at one representative
  pixel of each `SdfWorldPackage.ViewsProbeRate`-pixel cell, at its geometric
  normal; views reuses that gather where the pixel's receiver lies within the
  normal bias of the representative's, on its tangent plane and facing alike
  (`sdfViewsProbesAt`), and gathers for itself across a depth or crease edge, so
  the reuse never steps across a visibility edge the record shows. The cube reads
  stay per shading sample, along its shading normal and mirror direction. The gather
  supplies indirect light, reflections and traced static-light visibility; glass
  gathers at its own pane (`passes/sdf-glass-stage.hlsli`), since a pane is lit
  apart from the surface behind it. `passes/sdf-views-split.hlsli` holds the
  views pass's compile-out switches for reading each part's share of
  `sdf.world$views` with a reload.
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
  burst is flickering; a clean settled still proves nothing about it.
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
- **One `mapCore` call site.** The bake (`passes/sdf-mesh-bake.comp.hlsl`) is
  the only GPU kernel that walks the field, and it does so through one rolled
  `[loop]` over its samples. Every call site is a full inlined copy of the
  interpreter, costing register pressure, compile time and a driver translation
  on every cold boot, so more samples go through that loop, never through a new
  call. Never add `[noinline]`: a real SPIR-V call loses the device on NVIDIA's
  Vulkan driver.
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
without a formula and the fixed-point gradient answers instead.

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
  both queue arrangements and an open batch. This is the binding door only:
  native sparse buffer ownership and mesh backing selection remain step 7.
- The mesh region keeps no host copy of its meshes. Its host copy is the draw
  records alone (`GpuRegion.Shadow`); a placed mesh's words are owed without a
  compare (`GpuRegion.OweSourced`) and the upload's flush writes them from the
  `SdfMesh` buffers (`SdfWorldTables.WriteSourcedMeshWords`, an
  `IGpuRegionSource`). The irradiance scene likewise reads its triangles through
  each mesh's positions and indices, never a copy. A new consumer of baked
  triangles reads the `SdfMesh` buffers rather than copying them: the debug
  world's published meshes are native payloads with explicit consumer claims, and every copy costs that
  much again in working set. A mesh the library published from its store lets its normals and coarser
  levels' indices go once no region needs its words, and brings them back from the store before they are
  read again (`SdfMesh.Restore.cs`). `SdfMeshRegion.WriteMesh` takes an `SdfMesh.Held`, which only
  `SdfMesh.Hold` makes (a need, waiting for or running the restore), so every region write, a coarsened
  copy run's included, reads resident parts; the owed set (`OwedMeshes`) holds a need for the owed meshes
  the latest list names so landing never waits on the frame thread. A new reader of those parts takes a
  hold. A published chunked mesh keeps no joined indices: read its chunks.
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
  no level of its own. Everything that reads `SdfMesh.Indices` (the
  irradiance trace, the audit, the diagnostics' nearest query) reads the merged
  mesh, level zero. Each pass's choice runs beside the box cull in
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
world.rates off        # far or small work (far cascade, small windows, screens, bodies) otherwise updates at least 30 or 15 times a second, far bodies down to 5
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
fence, so the frame thread waits for the GPU to finish the frame two back), and
`begin` is the presenter's own wait for the previous frame. The `produce-work
parts` are `HostFrameWorkPart`s: `capture` is the residency's scene capture
(`SdfWorldResidency.Capture`: the composition's pose and transform pack, the
creations' drivers and effector surface probes, and the mesh dresser), never a
screenshot, so it costs whatever the scene animates with nothing armed; `pack`
is `SdfWorldTables.Pack`, mostly the changed mesh records
(`sdf.instances.records.changed` per frame; `records.posed` of them moved their
matrix alone and wrote only their pose words through `StagePose`). A draw riding
its frame slot moves with the slot's row and packs no record, so a moving body
counts in `sdf.transforms`, never in `records.posed`.

- **The far cascade is the frame's largest variable pass; read whether it
  holds.** With box-only selection it draws nearly every caster in a 280 m box out to 200 m toward the
  sun, at each mesh's coarsest level whose error fits a texel, in two layers,
  so a view facing the debug world's lawn, forest and studio draws many times
  the mesh pass's vertices there (`world.counters gpu pass sdf.world$shadow-far
  counter vertices`, which counts only the layers a frame draws).
  `world.counters sdf.rates` counts `shadow-far.rendered` (both layers),
  `.split` (one layer of a split refresh), `.held` and `.static` (the static
  layer drawn again). A cascade at a reduced rate holds a static layer, so each
  refresh draws both layers over it and costs only the moving casters: a still
  60 Hz view at 30 Hz shows `.rendered` and `.held` on alternate frames and no
  `.static`, while `.static` on every frame means the fit ends every frame and
  each refresh redraws the whole cascade. The rate and the light turn a hold tolerates are
  the device table's `farShadows { hertz, turnDegrees }` (`SdfFrame.FarShadows`,
  `SdfFarShadows.TurnRadiansFor`; an unauthored world refreshes every frame and
  tolerates a one-texel turn), measured from the camera the static layer was
  drawn from; only a cascade whose graph holds no static layer yet splits a
  timed refresh, drawing the depth on one frame and the exit layer on the next
  (`SdfFarLayers`). The pass
  asks `SdfWorldPasses.RecordFar`, which answers both for a layer its graph has
  not drawn, and `SubmittedFar` commits each drawn layer's camera; the surface
  pass reads the exit layer through the pass block's fourth camera. Compare
  `world.gpu-timing`'s `sdf.world$shadow-far` with `world.debug-view indirect`,
  which keeps the cascades but drops the views shading's cost and the temporal
  resolve.
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
  whose `SdfWorldResidency.MeshesArriving` holds: its bake pending, or its region owing words or their upload. A
  residency's bake samples and its meshes land only in frames that render one of its views: a frame that renders none
  still prepares it while it owes its minimum (`SdfWorldResidency.OwesPreparation`) and may pack, which moves what its
  background work binds. In `world.residency`, a residency
  whose `uploads` stand still while `unbound` in its `gpu-work` counts every pump and `owed` stays above zero is one
  nobody renders. `gpu.faults lose` makes every residency land again, a quick check of that path.
- **A frame captures only what it reads.** `SdfWorldPasses.BeginFrame` serves every held residency's upkeep
  (`SdfWorldResidency.Upkeep`), captures the host residency, which places the cameras, and prepares residencies that
  owe their minimum; the runtime then seals its roots and footprints (`RenderGraphFrame.Seal`) and prepares only the
  instances `RenderGraphScheduler.Readers` marks (`IRenderGraphPackageFactory.PrepareRead`), in rounds until none is
  new, and asks `IsUnchanged` of those alone. A scheduled render is always among them, since `Readers` follows every
  edge `Schedule` passes demand along; a new demand edge in the scheduler goes into `Readers` in the same change, or
  its producer renders from a residency nobody captured this frame. A graph installed mid-frame still stages late
  (`SdfWorldResidency.Staged`, counted). `world.counters sdf.captures` reads residencies held against captures run.
- **A dress walks only what moved.** `SdfMeshDresser.Dress` skips its group walk while the bake's product revision and
  copy layout revision stand; `SdfMeshPathBake.Replace` (the group list's one writer), Restart's clear, the
  `Group.Mesh` setter, `ShowOwn` and `FinishReplacements` move the product revision, so a new change to what
  `FrameAccess` exposes (a group's shown mesh, its tier, the list) moves it through one of them. Rigid copies come
  through the composition source's copy journal (`SdfFrame.MeshCopyChanges`, a `GpuChangedRows` read by a
  `GpuRowCursor`), which the dresser and the bake's layout check each read. `sdf.instances.dress.groups-visited`,
  `.groups-stood` and `.copies-visited` count it. A moved slot poses only the groups that ride it, and only where its
  scale moved (`DrawBinding.Repose`); a rigid copy's root slot is past every group's, so the walk stops before it.
  The host draws arrive with a journal too: `SdfMeshDrawComposer` composes each producer's draws (a scene's statics
  and stamp pool, `WorldSceneMeshDraws`; the composition's emitters) and copies and owes only the rows a producer's
  journal names, which the dresser's host cursor reads.
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
  serial pass (`BeginStep`), poses the woken ones across the `JobPool` as one `JobClass.Frame` fork, one partition a
  registration (`Pose`, `PackRegistration`), and settles them in pool order (`EndStep`). A partition writes only its own
  registration and slot range and keeps its scratch on its own stack; anything shared it would write (a list, a pool
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

