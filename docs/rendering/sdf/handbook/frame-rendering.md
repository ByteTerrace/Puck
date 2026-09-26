# SDF frame rendering

One world frame turns an SDF program into pixels through a fixed sequence of
compute passes. Upload and sky filling precede culling; the mask pass builds
per-tile instance visibility before the beam and primary marches; surface,
ambient, and view passes finish each view's image. The sequence exposes
where the GPU work goes, why mask-first processing keeps beam cost tied to nearby
instances, how render-scale tiers trade resolution for frame budget, and how two
frames stay in flight without stalling the whole device.

## One indirect render pipeline

A world frame records its passes into one command buffer. Upload and sky filling
precede culling; camera traversal, surface evaluation, AO and lighting have separate dispatches.
Every pass after the upload runs once per view, into that view's own output image:

```text
   upload → sky → mask → beam → cull-args → primary → surface → ambient → views
```

These are the engine's `world.counters gpu` pass labels — the columns its per-pass work
counters report against. Here is what the culling and rendering passes do;
[the engine README](../../../../src/Puck.SdfVm/README.md) describes the hit
records shared by the four per-pixel passes.

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

**primary** (`sdf-world-primary.comp.hlsl`) traces camera rays from their tile's entry
depth and records accepted hits. **surface** computes geometric normals and
curvature. **ambient** evaluates contact occlusion along those normals.
**views** reads these results and computes materials, lighting, shadows and
volumes. Compare all four passes when measuring per-pixel field cost: moving
work between kernels can reduce register pressure but adds buffer traffic.

The engine never assembles its views. Each view's output is its own image,
sized to the view's render extent, and the render graph's `place` pass puts it
in its seat rect on the root image, upsampling it where the view rendered below
native. In split screen each view is a graph producer of its own (`world`,
`world$2`, and so on), so the graph schedules and places the seats the same way
it places panes.

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
the footprint to an extent, and the engine renders the view's output image at
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

## Two frames in flight

The engine overlaps CPU frame production with GPU execution using a **two-deep
frame ring** (`FrameRingSize = 2`). Each ring slot owns its own command pool,
its host-visible buffer of every host-written table (a staging buffer or the
table itself, as the next section describes), descriptor sets, and a submission
fence. The host builds and submits
frame *N* into slot *N mod 2* without waiting for frame *N−1* to finish on the
GPU; it only waits on slot *k*'s fence—which proves frame *k−2* has retired —
before it rewrites that slot's buffers. This is what lets a moving screen or a
walking player update its transform in place each frame without racing the GPU
reading last frame's copy.

### What a frame uploads

Writing host-visible memory costs CPU copies and memory bandwidth, which
matters most on unified-memory devices such as the Steam Deck. So a frame
writes only what changed since the frame before it.

Every table the kernels read from the host is a `GpuRegion`: the program
words, viewport rows, dynamic transforms, the frame instance grid, screen
surfaces, screen lights, bounded volumes, glyph decals and mesh draws. The
region keeps a host copy of its table, and a write owes only the words that
differ from that copy, one run for each stretch of changed words. Where each
region lives is the device's choice, made by `GpuResidency.Select` from its
memory profile and the table's size, with the frame ring's reader always in
flight:

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

Dynamic transforms are never compared as a table: the engine packs only the
rows the frame's moved set (`SdfFrame.MovedTransforms`) owes since the frame it
last consumed, and the region owes the words of those rows that changed. The
program is written only by a program upload, into a region sized to the live
program and grown by half again when a larger one arrives. Mesh draws (`SdfFrame.MeshDraws`)
are packed into the mesh region (`SdfMeshRegion`: one 80-byte record a draw,
then each distinct mesh's positions and indices once) only when the frame hands
a different draw list; the region is created by the first frame that draws a
mesh and grown by half again when a list outgrows it. No pass reads it yet.

A host-baked brick reaches the brick pool through a staged region whose
destination is the pool itself. Since the carve bake also writes the pool, each
brick is copied whole.

Every staged copy records `region-copy.comp`, the one region-copy pipeline each
device has, which `Puck.Shaders` ships and every owner leases from the
pass-pipeline cache (`GpuRegionCopyPass`).

A still frame therefore writes only the time word of each viewport row, because
each row carries the frame's presentation time. When a sky's clouds drift, a
few words of its environment rows are written too. A frame whose time did not
move writes nothing. The frame instance grid is rebuilt only on a frame whose
transforms moved. `world.counters gpu` reports the written bytes as
`uploads.host-visible` on its `upload` line.

There are two distinct submission entry points, and they must never be blurred:

- **`SubmitFrame`** is fire-and-forget—the live path. It records, submits, and
  returns; the ring's fences do the pacing. The window host orders frames.
- **`RenderFrame`** is submit-and-wait—the harness/readback path. It submits and
  blocks until the frame retires so a test can read the pixels back deterministically.

Because the device-local scratch (tile buffers, instance masks, indirect args,
per-view textures) is *shared* across ring slots rather than duplicated, each
frame opens with one execution-dependency barrier ordering its first write after
the previous frame's last read of that scratch. That barrier serializes GPU frames
against each other while still overlapping CPU production with GPU
execution.

## Reading per-pass GPU cost

Performance is judged by code, disassembly, and deterministic work counters —
never by wall-clock or GPU timestamps. The engine counts the work each of the
nine labeled passes (`upload`, `sky`, `mask`, `beam`, `cull-args`, `primary`,
`surface`, `ambient`, and `views`) records, with no arming and no
effect on the image: dispatches, indirect dispatches, barriers, pipeline and
descriptor-set binds, push-constant bytes, descriptor writes and host-visible
upload bytes. The `upload` pass counts the regions' writes and copies; since
they follow each device's residency policy, the pass is per-backend
deterministic, and `puck counters` does not hold the two backends to it. Work
before the first pass (brick uploads and bakes, the begin-of-frame transitions),
each view's output transitions, or work between frames (descriptor rebinds) is counted outside every pass. A frame
the cadence gate skips reports
`sky` through `views` as skipped rather than as zero. Counts are published only
once the GPU has finished the submission, so `world.counters gpu` shows the newest
completed frame, under the program and kernel revision it ran with.

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
  and `SdfWorldEngine.PassLabels`.
- Measurement method and the register-pressure lesson:
  [SDF performance](performance.md).
- The uniform-grid cull rationale and why a per-frame BVH was rejected for it:
  [Hierarchical and instance acceleration](../reference/hierarchical-and-instance-acceleration.md).
- The two-deep frame ring, its per-slot fences, and the cross-frame scratch
  barrier: `FrameRingSize` in
  [`src/Puck.SdfVm/SdfWorldEngine.cs`](../../../../src/Puck.SdfVm/SdfWorldEngine.cs)
  and the `Record` method in
  [`src/Puck.SdfVm/SdfWorldEngine.Record.cs`](../../../../src/Puck.SdfVm/SdfWorldEngine.Record.cs).
- Each view's output image and its extent: `DefaultViewExtent` and the view
  outputs in
  [`src/Puck.SdfVm/SdfWorldEngine.ViewOutputs.cs`](../../../../src/Puck.SdfVm/SdfWorldEngine.ViewOutputs.cs),
  and the `extent` viewport row in the [rendering skill's sync pairs](../../../../.claude/skills/rendering/references/sync-pairs.md).
