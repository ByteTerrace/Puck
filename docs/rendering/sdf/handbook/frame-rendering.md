# SDF frame rendering

One world frame turns an SDF program and opaque meshes into pixels through a
fixed sequence of compute and graphics passes. Upload and sky filling precede
culling; the mask pass builds per-tile instance visibility before the beam and primary marches; surface,
ambient, and view passes finish each view's image. The sequence exposes
where the GPU work goes, why mask-first processing keeps beam cost tied to nearby
instances, how render-scale tiers trade resolution for frame budget, and how
frames stay in flight without stalling the whole device.

## One indirect render pipeline

A world frame has two halves. The scene's shared tables — the program, the
moving transforms, the screens, lights, volumes and mesh draws — live in one
**residency** (`SdfWorldResidency`) per frame source, and the frame first
submits one **upload** that brings those tables up to date. Then every view of
the scene is an instance of the render graph's `sdf.world` package, and its node
records the package's ten native passes into its own submission, reading the tables
the upload wrote. Upload and sky filling precede culling; camera traversal,
surface evaluation, AO, the key light's shadow and lighting have separate dispatches. The passes finish
that view's own output image:

```text
   upload → sky → mask → beam → cull-args → mesh → primary → surface → ambient → views
```

The render graph plans the view's passes like any other graph: the package
declares them as a fragment (`SdfWorldPackage.NativeFragment`) that the graph
compiler splices into the view's graph, and the planner decides every barrier
between them. `world.counters gpu` reports the upload under the residency
(`sdf:world` for the world's) and each view's passes under its instance, as
`sdf.world$sky` through `sdf.world$views`. Here is what the culling and
rendering passes do; [the engine README](../../../../src/Puck.SdfVm/README.md)
describes the visibility records the four per-pixel passes share: one per pixel
of each view's render grid, 64 bytes. Reduced or variable views append the
full-output `resolve` pass described under [render scale](#render-scale-tiers-trade-resolution-for-frame-time).

**mask** (`sdf-instance-cull.comp.hlsl`) computes, for every 16×16 screen tile, the
set of instances that could possibly matter to that tile—a bitmask, one bit per
instance. It reads a host-built uniform grid (instances binned by center into
world-space cells) and, for each tile, walks only the grid cells the tile's cone
overlaps, testing each binned instance's bound sphere against the cone. Dynamic
and unmaskable instances ride an always-tested list. The output is a compact
per-tile instance bitmask.

**beam** (`sdf-beam.comp.hlsl`) runs a coarse cone-march per tile over the
*mask-restricted* field. One representative cone per 16×16 tile marches until it
hits something or proves a stretch of space empty, recording where the fine march
should start and, when worthwhile, an empty gap it can teleport across. Programs
admitted to independent part tracing use a short entry search; their cheaper
per-part marches finish the work. Other programs also search gap and tail bounds.
Budget exhaustion leaves the unproven bounds disabled. Because the beam marches
`mapMasked`—the field with masked-out instances excluded—it never pays for
instances the mask already ruled out. Its cost is dominated by the VM evaluations
performed along the representative cone.

**cull-args** (`sdf-cull-args.comp.hlsl`) reads the beam's per-tile results and packs the
indirect-dispatch arguments for primary, surface, ambient, shadow and views. A parallel min/max reduction
finds the bounding rectangle of surviving tiles. Empty margins outside that
rectangle launch no threads; holes inside it remain in the dispatch.

**mesh** (`sdf-mesh.vert.hlsl`, `sdf-mesh.frag.hlsl`) rasterizes the frame's mesh draws, one draw call
each, into the mesh visibility target at the view's extent: per pixel the ray parameter the march records,
the draw plus one, and the triangle, kept nearest by a reversed-Z depth test. Primary alone reads the
target: it resolves the triangle's material from the mesh region (the draw's, plus the triangle's palette entry
when its mesh carries one) and records the draw and the triangle in the pixel's visibility record, from which
surface resolves its normal at the hit point (the vertex normals interpolated when its mesh
carries them, carried to world space by the draw's inverse-transpose normal matrix so a nonuniform scale keeps
them perpendicular to the surface; the face normal otherwise), turned toward the camera and clamped so a grazing ray
never sees it lean away, and views a textured mesh's albedo, material and emission. The draw plus one and the triangle are stored as floats, so a region holds at most 2^24
draws and a mesh at most 2^24 triangles, each refused by name past that. A frame with no mesh draws skips the pass,
its target's and depth's barriers included, and the hit passes read nothing of the target. The target and its depth attachment, 20 bytes a pixel, are scratch the view's instance allocates with its graph.
Primary bounds its march by that ray parameter
and keeps an SDF surface only when it is strictly nearer, so a mesh pixel becomes a mesh visibility
record; while a mesh draws, cull-args covers the whole view, and a mesh pixel shades with neutral shadows
and ambient occlusion.

**primary** (`sdf-world-primary.comp.hlsl`) traces camera rays from their tile's entry
depth, raised to the mesh projection's near plane when necessary, and writes
each pixel's visibility record: ray parameter, identity, material and march data,
misses included. **surface** adds the geometric normal and curvature to it.
**ambient** evaluates contact occlusion along those normals into the record.
**shadow** marches the key light's soft shadow from each lit surface into the
record. **views** reads the record and computes materials, lighting and
volumes. A view whose quality (`SdfViewSnapshot.Quality`) turns ambient occlusion or
soft shadows off skips that pass; quality is each view's, so views of one frame
render at different cost. Compare all five passes when measuring per-pixel field cost: moving
work between kernels can reduce register pressure but adds buffer traffic.

No pass assembles views. Each view's output is its instance's own image, sized
to its output extent. Reduced views reconstruct their current color in `resolve`
before the render graph's `place` pass puts that output in its seat rect. In split screen each view is an instance of its own (`world`,
`world$2`, and so on) over the one residency, so the graph schedules and places
the seats the same way it places panes.

## What each pass costs

The passes scale with different things. `mask` and `beam` scale with how many
instances lie near each tile's cone. `primary`, `surface`, `ambient`, and
`views` scale with on-screen content: how many pixels hit a surface and how
much of the program each field query walks. `sky` and `cull-args` are
small. **The four per-pixel passes are the scale lever for
on-screen content; `mask`+`beam` is the scale lever for instance count.**
Moving work between the per-pixel passes can relieve register pressure but
adds hit-buffer traffic, so compare their sum as well as each label.

## Why the mask pass flattens beam cost

Without a mask, every beam march step evaluates the whole program, and each
evaluation checks every instance segment's bound early-out. The beam's cost
then grows linearly with instance count even when the instances are nowhere
near the tile, because binning the instances is cheap while re-checking all of
them at every march step is not.

The set of instances a tile's cone actually needs is exactly what a spatial
cull computes, and it can be computed *once per tile* instead of re-derived at
every march step. So the pipeline puts the mask pass **first**: compute each
tile's relevant-instance bitmask up front, then let the cone march consume the
already-masked field. The march stops enumerating instances per sample; it
only ever touches the handful the mask kept.

The per-pixel passes do the same work with or without the mask, because the
masked field is bit-identical to the full field inside the tile's cone. With
the mask in place, a frame with many on-screen instances is bound by the
per-pixel passes rather than by `beam`, which is where the cost belongs: on
visible shading, not on culling.

Two design choices determine both correctness and occupancy. The cull is a
**separate pass, not fused into the beam**—a fused variant's per-thread mask
scratch lowers the co-resident cone march's occupancy
([SDF performance](performance.md) turns this into the general register-pressure
lesson). And the mask output uses **direct mask-buffer bit writes** (OR is
commutative, so insertion order doesn't matter), never a per-thread accumulation
array. Occupancy is part of the contract here, not a detail.

Correctness rides the exact-cull contract from [SDF program model](program-model.md): a masked-out
instance's bound excludes the tile's whole cone, and a far-neutral blend
(union/subtraction) returns the accumulator *to the bit* when its member is
skipped. So the masked march is bit-identical to the flat one. No runtime
switch disables the mask, and no automated check compares the two.

One nuance worth carrying: the cull raises the *total* instance ceiling, not the
*per-tile* one. Scattered content—a persistently damaged world, carves spread
across the map—costs almost nothing per frame because each tile's cone touches
only a few grid cells. But instances **densely stacked in one spot** overlap the
same tiles and are genuinely un-cullable there; their `views` cost is real and the
grid rightly doesn't touch it. The honest ceilings after the cull are (a)
dense per-tile stacking and (b) on-screen visible-instance shading—both
per-pixel `views` costs.

## Render-scale tiers trade resolution for frame time

When the shading epilogue is the cost and you need the frame to fit a tighter
budget, the lever is to render a view at *reduced* resolution and upsample it
afterwards. Each view carries a `RenderScale` ceiling inside its output extent.
The graph keeps the view's footprint at its native rect size, and an authored
camera or session resolution stays exact even when its reader shrinks. The
camera aspect uses that authored width and height from the first frame.

Traversal and shading use the current render grid inside a ceiling allocation.
A smaller current grid changes dispatch dimensions and the visibility stride
without reallocating the ceiling's targets. The final `resolve` pass writes
full-output color with the same bilinear and clamped Catmull-Rom filter as
`place`. It also writes the nearest ray distance and its exact identity from the
filter's taps, without filtering either; coverage remains the color's alpha.
`place` copies that output into the view's rect. A lone whole-display view can
stand directly, including at a reduced internal render scale.

A fixed native view with temporal reconstruction off keeps the original ten-pass fragment and writes its output
directly. It allocates no resolve resources. A reduced or variable view adds one
output-sized dispatch; its memory account includes the output beside the render
ceiling, and its scheduling price sums the passes' current grids.
Render scale is *presentation only*: it never touches simulation state, and which
tier a view uses is a host decision, not baked into the content. In `Puck.World`,
`world.render-scale` sets it for every player view and `world.upscale-sharpness`
sets the reconstruction blend.

The final `place` pass sharpens an equal-sized source with a contrast-adaptive
five-tap filter when sharpness is positive; zero stays an exact copy. A lone
full-display view at zero sharpness bypasses that pass. Positive sharpness runs
the existing placement, so its dispatch and written output pixels become
counted work. [The package reference](../../../reference/shaders.md#built-in-packages)
states the arithmetic.

## Temporal reconstruction

`world.temporal on` lets each local view combine samples from its own earlier
renders. It is off by default, including in the shipped quality presets. Camera
screens read their world's authored `render.temporal`; session and window
screens read the destination world's setting. Toggling the viewer's live lever
does not turn those screens' histories on. Routed player views retain the
player's live preference.

An enabled view renders an eight-position jitter sequence, starting at the
pixel center. Its final resolve removes that offset and keeps output-sized
color and surface histories through the render graph's ordinary history
resources. The surface stores the exact visibility identity and ray distance.
The resolver follows the preceding camera and the winning shape or mesh
transform, rejects a different identity or depth, and clamps surviving history
to the current neighborhood before blending it. Background pixels follow the
camera's direction without scene translation.

Screens, bounded volumes, and hits with changing dynamic value lanes write a
separate render-sized reactivity image. Such color changes cannot be explained
by rigid motion, so the resolver reduces their history contribution. Coverage
stays in premultiplied color alpha. The reactivity image uses one R32Float
channel, four bytes per render pixel, and exists only while temporal
reconstruction is enabled.

A cut, view follow, scene-program replacement, output or ceiling change, debug
change, toggle, or gap in the residency's consumed frames resets the sample
count. The first sample reads no history and produces the spatial result.
Resetting does not clear or reallocate the retained storage; graph history is
initialized only when its storage is created. Ordinary motion keeps valid
history and restarts the eight unchanged samples needed before the view stands.

Temporal shading and resolve use optional pipeline slots in the same kernel
reload transaction as the spatial path. Kernel bytecode is loaded and counted
with the immutable set, including when temporal reconstruction is off. Off
views create no temporal pipelines, reactivity image, or history resources.

## Frames in flight

The host keeps producing frames while the GPU finishes earlier ones. Each
view's node has its own frame slots, as every render-graph node does, and the
residency's tables keep a **two-deep upload ring** (`FrameRingSize = 2`). Each
upload slot owns its own command pool, its host-visible buffer of every
host-written table (a staging buffer or the table itself, as the next section
describes), and a submission fence. The first pass of frame *N* to record
submits the frame's upload into slot *N mod 2*, ahead of every view's
submission, which reads what it wrote. Before it rewrites a slot, an upload
waits on the previous upload's fence; that fence signals once every submission
queued before it has finished, so the views that read the slot two uploads
earlier are done with it. This is what lets a moving screen or a walking player
update its transform in place each frame without racing the GPU reading last
frame's copy. A rewrite of what every slot shares — a program or instance grid
that outgrew its region, a grown mesh region, a new glyph atlas, a reloaded
kernel — waits for the whole device to go idle instead, because the submissions
of other nodes read it too.

### What a frame uploads

Writing host-visible memory costs CPU copies and memory bandwidth, which
matters most on unified-memory devices such as the Steam Deck. So a frame
writes only what changed since the frame before it.

Every table the kernels read from the host is a `GpuRegion`: the program
words, dynamic transforms, the frame instance grid, screen surfaces, screen
mappings, screen lights, bounded volumes, glyph decals and mesh draws, which the
residency's tables hold. What is not a table rides each pass's block instead:
the view's camera, far distance and debug view mode, the frame's shading levers
and its environment's rows, which every pass of a view writes into its pass
block (`SdfFrameBlock`) at the offsets the generated `sdf-world` interface
declares. A region keeps a host copy of its table, and a write owes only the
words that differ from that copy, one run for each stretch of changed words.
Where each region lives is the device's choice, made by `GpuResidency.Select`
from its memory profile and the table's size, with a reader always in flight:

- **Staged**, on a device the host cannot write in its own memory (a discrete
  adapter without an aperture): each ring slot has a staging buffer, and the
  `upload` pass copies the owed words into one device-local buffer the kernels
  read, one dispatch per region however scattered the changes are. The staging
  buffer states the copy: a 16-byte header, 8 bytes for each run, then the
  words. A word nobody changed stays as an earlier frame left it, so a
  transform changed on frame *N* is still correct on frame *N+1*, even though
  that frame stages in the other slot's buffer.
- **Ring**, on a device with a host-visible aperture or unified memory: each
  ring slot has its own buffer the kernels read directly, and each receives
  the words it is behind by when its turn comes. Nothing is copied. On a
  discrete adapter the buffers live in its aperture, so the kernels read them
  from device memory; on unified memory they are the one pool.

Every pass of every view reads the tables through one descriptor set, the
`sdf.world` interface's World group at set 1: each ring slot's buffers, the
brick pool, the glyph atlas, the samplers and the mesh atlases. The tables own
one such set per ring slot and write both once; a frame binds the slot its
upload wrote. They rewrite the sets only when what they bind moves (a region
grows, or the glyph or mesh atlases change), after the device is idle, since
every view's submission in flight binds them.

Dynamic transforms are never compared as a table: the residency packs only the
rows the frame's moved set (`SdfFrame.MovedTransforms`) owes since the frame it
last consumed, and the region owes the words of those rows that changed. The
program is written only by a program upload, into a region sized to the live
program and grown by half again when a larger one arrives. Mesh draws (`SdfFrame.MeshDraws`)
are packed into the mesh region (`SdfMeshRegion`: one 80-byte record a draw,
then each distinct mesh's positions and indices once) only when the frame hands
a different draw list. Each scene emitter states its draws, a static placement's
fixed at its rebuild and a stamp's posed at its root each frame, and returns the
same list while none moved, so a still scene packs nothing. The region starts with one draw record when the tables are built
and grows by half again when a list outgrows it. The mesh pass reads its triangles,
and primary reads the winning mesh's material.

A host-baked brick reaches the brick pool through a staged region whose
destination is the pool itself. Since the carve bake also writes the pool, each
brick is copied whole.

Every staged copy records `region-copy.comp`, the one region-copy pipeline each
device has, which `Puck.Shaders` ships and every owner leases from the
pass-pipeline cache (`GpuRegionCopyPass`). An owner records a frame's owed copies through
`GpuRegionCopyRecording`: a barrier ordering the earlier reads of every staged
destination before the first copy, then the copies, then one transition per
copied buffer for its readers.

A still frame therefore writes no table. The sky and the bounded media animate
on the frame's presented tick (`SdfFrame.Clock`, the tick the state mirror
presented the frame's bound state at), reduced on the host so no pass reads a
clock: the twinkle's phase and the clouds' drift ride the pass blocks, which the
view's node writes whole each frame it renders, and each medium's advection and
pulse ride the volume table, which a frame writes only when the tick moves
them. The frame instance grid is rebuilt only on a frame whose
transforms moved. `world.counters gpu` reports the tables' written bytes as
`uploads.host-visible` on its `upload` line, and a brick's on its `bricks` line.

Nothing on the live path waits for its own submission: the upload and each
view's passes are submitted and the fences do the pacing. A capture reads a
view back through the render graph, which serves it from a frame it renders.

A view's device-local scratch (tile buffers, instance masks, indirect
arguments, visibility records, the mesh target) is *transient*: one allocation
per instance, shared by every frame slot rather than duplicated. The planner
orders each scratch resource's first use in a frame after the previous frame's
last use of it, which serializes that view's GPU frames against each other
while still overlapping CPU production with GPU execution. Only the view's
color output is kept per frame slot, so a consumer can read the previous
frame's image while the next one renders.

## Reading per-pass GPU cost

Performance is judged by code, disassembly, and deterministic work counters —
never by wall-clock or GPU timestamps. The residency counts the work of its
upload's passes, and each view's node counts the work each of its passes
(`sdf.world$sky` through `sdf.world$views`) records, with no arming and no
effect on the image: dispatches, indirect dispatches, barriers, pipeline and
descriptor-set binds, push-constant bytes, descriptor writes and host-visible
upload bytes. The upload has three passes: `fillers`, the fillers' first
transitions and clears, which only the first upload runs; `bricks`, a queued
brick's staging copy and the carve bake's slices with the pool's barriers,
which an upload runs only when it writes the pool; and `upload`, the regions'
writes and copies. The `bricks` and `upload` passes follow each device's
residency policy, so they are per-backend deterministic, and `puck counters`
does not hold the two backends to them.

Each view's passes also count their own work on the GPU: the march steps they
take, one for each field evaluation of a march or a query and for each sample of
a bounded volume, and the texels they write, one for each pixel whose output
they write (an image texel, or any word of a pixel's visibility record, so a
stage that returns before it stores counts nothing) and, in the mesh pass, one
for each fragment. The root graph's `place` passes, the overlay, the source
conversions and the post passes count the texels they write the same way. Each
wave sums its lanes' counts, a fragment stage's over the lanes that are not
helper lanes, and adds them with one atomic into the pass's row of the node's
kernel counters, which the node clears ahead of the view's first pass and
copies to the frame slot's readback behind its last; the counts join the pass's
line once the submission completes, as `march.steps` and `texels.written`. The
marches run in floats and an indirect pass runs only the tiles culling leaves
it, so these counts are per backend deterministic. The clear, the copy and their
three barriers (after the clear, before the copy, and from the copy to the host)
count outside every pass. A pass the frame skips, such as the ambient and
shadow passes at a tier that turns them off, is reported as skipped, not as a
pass that ran and counted nothing. A view the cadence gate finds unchanged is not rendered at
all: the render graph keeps its latest output, and its passes record nothing.
Counts are published only once the GPU has finished the submission, so
`world.counters gpu` shows the newest completed frame.

In `Puck.World`, read the previous frame's passes with `world.counters gpu`. A still
scene keeps each view's retained output instead of rendering, so run
`world.cadence off` before measuring one. Hold the camera at a fixed pose
while comparing runs; [SDF performance](performance.md) turns this into the
general rule: frame-index a measurement camera, never wall-clock it. The
counts are exact: the calls a pass records are the same on every backend for
the same inputs, and its march steps and texels written the same on every run
of one backend, so no banding or averaging is needed the way a timestamp sample
would require. `puck counters --check` holds a pinned workload's counts, pass by
pass, to the counted-cost ceilings recorded for it. When
a counted-work comparison alone cannot answer the question, read the kernel
disassembly or trace the code path instead.

---

## Related resources

- The pass order and what each pass must respect when edited:
  [the rendering skill's kernel reference](../../../../.claude/skills/rendering/references/kernels.md)
  and the fragment in
  [`src/Puck.Shaders/Graph/SdfWorldPackage.cs`](../../../../src/Puck.Shaders/Graph/SdfWorldPackage.cs).
- Measurement method and the register-pressure lesson:
  [SDF performance](performance.md).
- The uniform-grid cull rationale and why a per-frame BVH was rejected for it:
  [Hierarchical and instance acceleration](../reference/hierarchical-and-instance-acceleration.md).
- The two-deep upload ring and its per-slot fences: `FrameRingSize` in
  [`src/Puck.SdfVm/SdfWorldTables.cs`](../../../../src/Puck.SdfVm/SdfWorldTables.cs)
  and `SubmitUpload` in
  [`src/Puck.SdfVm/SdfWorldTables.Upload.cs`](../../../../src/Puck.SdfVm/SdfWorldTables.Upload.cs).
- How a view records its passes and binds the residency's tables:
  [`src/Puck.SdfVm/SdfWorldPasses.cs`](../../../../src/Puck.SdfVm/SdfWorldPasses.cs)
  and [`src/Puck.SdfVm/SdfWorldPassRecorder.cs`](../../../../src/Puck.SdfVm/SdfWorldPassRecorder.cs),
  [`src/Puck.SdfVm/SdfFrameBlock.cs`](../../../../src/Puck.SdfVm/SdfFrameBlock.cs), which writes each
  pass's block, and the pass block in the [rendering skill's sync pairs](../../../../.claude/skills/rendering/references/sync-pairs.md).

## Presentation picking

A rendered view exposes `SdfWorldPicker` through its `SdfWorldPasses`. A request
uses normalized view coordinates and reads one visibility pixel asynchronously.
The V row supplies kind, source, ray distance and material; only its 16 bytes are
copied. The source is the SDF program instance ordinal plus one, or the mesh draw
ordinal. Static instances therefore remain distinguishable even with the same
material. A queued coordinate captures the program and immutable host identity
map when its pixel copy records, so a content revision before recording keeps the
request. A view change cancels it; after recording, a changed program or map
rejects the answer. Pose-only mesh changes keep that map.

The 64-byte visibility record keeps the winning shape's exact transform slot in
L.x. This slot can differ from an articulated instance's bound slot. Surface
shading reads the four anonymous lanes from the existing dynamic transform row;
static hits read zero. The remaining L words are reserved.

Build mode and locally opened passthrough panes demand hover from the same
picker. Pending answers provide backpressure; after completion, continuing
hover samples the next rendered frame so geometry moving beneath a stationary
pointer stays current. No demand records no copy. `world.view.pick <instance>
[<x> <y>]` exposes the same request/result seam to presentation automation.
`world.view.pointer <client-x> <client-y>` supplies an in-bounds console-only
presentation cursor override; `clear` restores the real pointer feed. It does
not move the OS cursor or send simulation input. The argument-free pointer
query keeps its existing readout.
