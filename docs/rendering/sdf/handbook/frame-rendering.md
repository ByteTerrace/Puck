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
records the package's nine passes into its own submission, reading the tables
the upload wrote. Upload and sky filling precede culling; camera traversal,
surface evaluation, AO and lighting have separate dispatches. The passes finish
that view's own output image:

```text
   upload → sky → mask → beam → cull-args → mesh → primary → surface → ambient → views
```

The render graph plans the view's passes like any other graph: the package
declares them as a fragment (`SdfWorldPackage.Fragment`) that the graph
compiler splices into the view's graph, and the planner decides every barrier
between them. `world.counters gpu` reports the upload under the residency
(`sdf:world` for the world's) and each view's passes under its instance, as
`sdf.world$sky` through `sdf.world$views`. Here is what the culling and
rendering passes do; [the engine README](../../../../src/Puck.SdfVm/README.md)
describes the visibility records the four per-pixel passes share: one per pixel
of each view, sixty bytes.

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
indirect-dispatch arguments for primary, surface, ambient and views. A parallel min/max reduction
finds the bounding rectangle of surviving tiles. Empty margins outside that
rectangle launch no threads; holes inside it remain in the dispatch.

**mesh** (`sdf-mesh.vert.hlsl`, `sdf-mesh.frag.hlsl`) rasterizes the frame's mesh draws, one draw call
each, into the mesh visibility target at the view's extent: per pixel the ray parameter the march records,
the draw plus one, and the triangle, kept nearest by a reversed-Z depth test. The hit passes resolve that
triangle from the mesh region: primary reads its material (the draw's, plus the triangle's palette entry when
its mesh carries one), and surface its normal at the hit point (the vertex normals interpolated when its mesh
carries them, carried to world space by the draw's inverse-transpose normal matrix so a nonuniform scale keeps
them perpendicular to the surface; the face normal otherwise), turned toward the camera and clamped so a grazing ray
never sees it lean away. The draw plus one and the triangle are stored as floats, so a region holds at most 2^24
draws and a mesh at most 2^24 triangles, each refused by name past that. A frame with no mesh draws records nothing
here. The target and its depth attachment, 20 bytes a pixel, are scratch the view's instance allocates with its graph.
Primary bounds its march by that ray parameter
and keeps an SDF surface only when it is strictly nearer, so a mesh pixel becomes a mesh visibility
record; while a mesh draws, cull-args covers the whole view, and a mesh pixel shades with neutral shadows
and ambient occlusion.

**primary** (`sdf-world-primary.comp.hlsl`) traces camera rays from their tile's entry
depth, raised to the mesh projection's near plane when necessary, and writes
each pixel's visibility record: ray parameter, identity, material and march data,
misses included. **surface** adds the geometric normal and curvature to it.
**ambient** evaluates contact occlusion along those normals into the record.
**views** reads the record and computes materials, lighting, shadows and
volumes. Compare all four passes when measuring per-pixel field cost: moving
work between kernels can reduce register pressure but adds buffer traffic.

No pass assembles views. Each view's output is its instance's own image, sized
to the view's render extent, and the render graph's `place` pass puts it in its
seat rect on the root image, upsampling it where the view rendered below
native. In split screen each view is an instance of its own (`world`,
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
afterwards. Each view carries a `RenderScale`. The host sets the view's
footprint in the render graph to its rect at that scale, the graph quantizes
the footprint to an extent, and the view's instance renders its output image at
exactly that extent. Every per-view pass (sky, mask, beam, primary, surface,
ambient, views) reads the same extent from the view's row, so the whole
pipeline agrees on the smaller render target. The graph's `place` pass
reconstructs the result at native resolution with a four-tap bilinear filter,
blended toward clamped Catmull-Rom by the upscale sharpness.

The important property is that **native is byte-exact by construction**:
`place` copies exactly when the output's extent equals its rect, and a single
view covering the whole display at native scale is not placed at all, so a view
at full scale is bit-identical to a pipeline with no render-scale machinery. You
pay nothing until you dial it down. Reduced tiers expose a policy ladder that
trades a soft upsample for a large `views` saving—the right knob when a
heavy revealed scene needs to reach a frame-rate target that native can't hit.
Render scale is *presentation only*: it never touches simulation state, and which
tier a view uses is a host decision, not baked into the content. In `Puck.World`,
`world.render-scale` sets it for every player view and `world.upscale-sharpness`
sets the reconstruction blend.

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
residency's tables hold, and each view's viewport row, which every pass of the
view writes into a small region of its own that the view's node copies ahead of
its passes. The region keeps a host copy of its table, and a write owes only the
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

A still frame therefore writes only the time word of each viewport row, because
each row carries the frame's presentation time. When a sky's clouds drift, a
few words of its environment rows are written too. A frame whose time did not
move writes nothing. The frame instance grid is rebuilt only on a frame whose
transforms moved. `world.counters gpu` reports the written bytes as
`uploads.host-visible` on its `upload` line.

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
`upload` pass, and each view's node counts the work each of its passes
(`sdf.world$sky` through `sdf.world$views`) records, with no arming and no
effect on the image: dispatches, indirect dispatches, barriers, pipeline and
descriptor-set binds, push-constant bytes, descriptor writes and host-visible
upload bytes. The `upload` pass counts the regions' writes and copies; since
they follow each device's residency policy, the pass is per-backend
deterministic, and `puck counters` does not hold the two backends to it. The
upload's brick copies and bakes and the fillers' first transitions are counted
outside its pass. A view the cadence gate finds unchanged is not rendered at
all: the render graph keeps its latest output, and its passes record nothing.
Counts are published only once the GPU has finished the submission, so
`world.counters gpu` shows the newest completed frame.

In `Puck.World`, read the previous frame's passes with `world.counters gpu`. A still
scene keeps each view's retained output instead of rendering, so run
`world.cadence off` before measuring one. Hold the camera at a fixed pose
while comparing runs; [SDF performance](performance.md) turns this into the
general rule: frame-index a measurement camera, never wall-clock it. The
counts are exact and the same on every backend for the same inputs — no
banding or averaging is needed the way a timestamp sample would require. When
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
  and the `extent` viewport row in the [rendering skill's sync pairs](../../../../.claude/skills/rendering/references/sync-pairs.md).
