# The frame

After this chapter you'll understand how one world frame turns a program into
pixels: the five compute passes it runs, what each one buys and what it costs on
the reference GPU, why computing a per-tile instance mask *before* the march is
the move that flattened the engine's worst scaling wall, how render-scale tiers
buy frame budget, and how the engine keeps two frames in flight without a
whole-device stall.

## Five passes, one indirect pipeline

A world frame is five compute passes recorded into one command buffer and
submitted together. They run strictly in order, each feeding the next:

```
   mask ──▶ beam ──▶ cull-args ──▶ views ──▶ composite
    │        │          │           │          │
 per-tile  coarse    pack the    per-pixel   blit views
 instance  cone-     indirect    march +     into the
 bitmask   march     dispatch    shade       framebuffer
```

The pass labels are exactly `["mask", "beam", "cull-args", "views", "composite"]`
— the same names the GPU-timing instrument reports. Here is what each one is for.

**mask** (`sdf-instance-cull.comp`) computes, for every 16×16 screen tile, the
set of instances that could possibly matter to that tile — a bitmask, one bit per
instance. It reads a host-built uniform grid (instances binned by center into
world-space cells) and, for each tile, walks only the grid cells the tile's cone
overlaps, testing each binned instance's bound sphere against the cone. Dynamic
and unmaskable instances ride an always-tested list. The output is a compact
per-tile instance bitmask.

**beam** (`sdf-beam.comp`) runs a coarse cone-march per tile over the
*mask-restricted* field. One representative cone per 16×16 tile marches until it
hits something or proves a stretch of space empty, recording where the fine march
should start and one proven-empty gap it can teleport across. Because it marches
`mapMasked` — the field with masked-out instances excluded — it never pays for
instances the mask already ruled out. Its cost is dominated by the VM evaluations
performed along the representative cone.

**cull-args** (`sdf-cull-args`) reads the beam's per-tile results and packs the
indirect-dispatch arguments for the fine march: which tiles have work, how many
threads to launch. It is a prefix-sum compaction (count → scan → scatter), never
an atomic-append, so the tile ordering is deterministic and bit-identical across
backends. This is the seam that lets the expensive `views` pass launch threads
*only* where there is surface to shade.

**views** (`sdf-world-views.comp`) is the fine, per-pixel march and shade — the
real cost of the frame. Each pixel starts where its tile's beam told it to,
marches the masked field to a hit, computes the lit normal, and runs the full
shading epilogue: material, sun, screen lights, soft shadows, ambient occlusion.
Almost all per-frame GPU time lives here for any realistic scene.

**composite** blits the finished per-view surfaces into the framebuffer, applying
the per-view render-scale upsample where a view rendered below native. It is
negligible throughout — well under 0.1 ms.

## What each pass costs, and where the cost lives

On the reference GPU (a 4070, Vulkan, 1280×800) a single fullscreen shape — no
instances — spends essentially all of its time in `views`: the `beam` cone-prepass
stays well under a millisecond for every primitive because there's nothing to cull, while
`views` runs a few milliseconds of per-pixel VM interpretation and shading. The
lesson holds across the whole bench: **`views` is the scale lever for on-screen
content; `beam`+`mask` is the scale lever for instance count.**

The instance story is where the pipeline earns its shape. Before the mask pass
existed, the beam's cost grew *linearly with instance count*, and it dominated
badly. Measured on a torus instance sweep:

| instances | frame (ms) | beam (ms) | beam share |
|---|---|---|---|
| 64 | 8.9 | 3.3 | — (views leads) |
| 256 | 19.2 | 12.5 | 65% |
| 1024 | 68.3 | 50.7 | 74% |
| 4096 | 243.9 | 187.3 | 77% |

At 4096 instances the beam alone was 187 ms — the frame was ~4 fps and beam owned
three-quarters of it. The naive reading was "the per-tile instance *binning* loop
is O(instances)." Measurement said otherwise: splitting the cull into its own
kernel showed binning 4096 instances costs ~0.4 ms flat. **The O(instances) cost
was the cone march's own field evaluation** — roughly 96 march steps × 4000 tiles,
each `map()` walking every instance segment's bound early-out — about 1.6 billion
cheap checks, and *that* was the 187 ms.

## Why mask-first flattened the beam wall

The insight is that the set of instances a tile's cone actually needs is exactly
what a spatial cull computes, and it can be computed *once per tile* for about
0.1 ms — instead of re-derived at every one of the ~96 march steps. So the
pipeline puts the mask pass **first**: compute each tile's relevant-instance
bitmask up front, then let the cone march consume the already-masked field. The
march stops enumerating instances per sample; it only ever touches the handful
the mask kept.

The results on the same sweep, before → after:

| scene | beam+mask before | after | frame before → after |
|---|---|---|---|
| torus ×1024 | 50.5 ms | 1.9 ms (26×) | 67 → 19 ms |
| torus ×4096 | 187.8 ms | 6.6 ms (28×) | 244 → 61 ms |
| torus ×16384 (new cap) | ~750 ms (extrapolated) | 21.8 ms | — → 187 ms |
| scattered carves ×1024 | 119.0 ms | 1.0 ms (117×) | 131 → 13 ms (60 fps+) |

The `views` cost is *unchanged* at every matched rung — the mask is bit-identical
to a full march, so the per-pixel work is byte-for-byte the same. What changed is
that every frame past ~1024 on-screen instances is now `views`-bound rather than
`beam`-bound, which is exactly where you want the cost to live: on visible
shading, not on culling.

Two design choices are load-bearing and were both measured, not guessed. The cull
is a **separate pass, not fused into the beam** — a fused variant's per-thread
mask scratch taxed the co-resident cone march's occupancy ~12% on both backends
([chapter 8](08-performance.md) turns this into the general register-pressure
lesson). And the mask output uses **direct mask-buffer bit writes** (OR is
commutative, so insertion order doesn't matter), never a per-thread accumulation
array. Occupancy is part of the contract here, not a detail.

Correctness rides the exact-cull contract from [chapter 2](02-the-program-model.md): a masked-out
instance's bound excludes the tile's whole cone, and a far-neutral blend
(union/subtraction) returns the accumulator *to the bit* when its member is
skipped. So `grid == flat` is bit-identical, and the live `sdf.grid on|off` toggle
is render-invariant — proven by a dedicated parity gate.

One nuance worth carrying: the cull raises the *total* instance ceiling, not the
*per-tile* one. Scattered content — a persistently damaged world, carves spread
across the map — costs almost nothing per frame because each tile's cone touches
only a few grid cells. But instances **densely stacked in one spot** overlap the
same tiles and are genuinely un-cullable there; their `views` cost is real and the
grid rightly doesn't touch it. The honest ceilings after the cull are (a)
dense per-tile stacking and (b) on-screen visible-instance shading — both
per-pixel `views` costs.

## Render-scale tiers — buying budget with resolution

When the shading epilogue is the cost and you need the frame to fit a tighter
budget, the lever is to render a view at *reduced* resolution and upsample it in
the composite. Each view carries a `RenderScale`; the engine quantizes it to a
single byte and every scale-aware pass (beam, mask, views) derives the identical
reduced extent from it, so the whole pipeline agrees on the smaller render target.
The composite bilinearly upsamples the result back to native.

The important property is that **native is byte-exact by construction**: the
maximum scale value takes an exact-copy path with no filtering, so a view at full
scale is bit-identical to a pipeline with no render-scale machinery at all. You
pay nothing until you dial it down. Reduced tiers (the demo exposes a ladder of
them) trade a soft upsample for a large `views` saving — the right knob when a
heavy revealed scene needs to reach a frame-rate target that native can't hit.
Render scale is *presentation only*: it never touches simulation state, and which
tier a view uses is a policy decision made by the layout director, not baked into
the content.

## Two frames in flight

The engine overlaps CPU frame production with GPU execution using a **two-deep
frame ring** (`FrameRingSize = 2`). Each ring slot owns its own command pool,
per-frame host-visible buffers (viewports, transforms, screen surfaces and
lights), descriptor sets, and a submission fence. The host builds and submits
frame *N* into slot *N mod 2* without waiting for frame *N−1* to finish on the
GPU; it only waits on slot *k*'s fence — which proves frame *k−2* has retired —
before it rewrites that slot's buffers. This is what lets a moving screen or a
walking player update its transform in place each frame without racing the GPU
reading last frame's copy.

There are two distinct submission entry points, and they must never be blurred:

- **`SubmitFrame`** is fire-and-forget — the live path. It records, submits, and
  returns; the ring's fences do the pacing. The window host orders frames.
- **`RenderFrame`** is submit-and-wait — the harness/readback path. It submits and
  blocks until the frame retires so a test can read the pixels back deterministically.

Because the device-local scratch (tile buffers, instance masks, indirect args,
per-view textures) is *shared* across ring slots rather than duplicated, each
frame opens with one execution-dependency barrier ordering its first write after
the previous frame's last read of that scratch. That barrier serializes GPU frames
against each other while still overlapping CPU production with GPU
execution.

## Reading per-pass GPU cost

The engine writes GPU timestamp marks around each pass: one frame-start mark, then
one mark closing each of the five labeled passes. Diff adjacent marks and you have
per-pass milliseconds. Arm it live (the `gpu.timing` switch / the `world.timing` verb, or the run-doc `host.timing` field); the marks are pixel-neutral,
so determinism and capture-hash gates are unaffected by timing being on. The
`sdf.bench` instrument drives this over stdin with a *fixed deterministic camera
pose per configuration* (constant yaw/pitch, distance computed to frame the
workload) so numbers are pad-independent and reproducible —
[chapter 8](08-performance.md) turns this into the general rule: frame-index a
bench camera, never wall-clock it. When you read timing numbers, trust coarse bands,
not sub-millisecond deltas: a loaded GPU carries ±10–20% clock/thermal noise on
the cheap few-millisecond configs. The bench's `beam` column reports `beam+mask`
summed so ladders across the mask-first change stay comparable, and `views` is a
pure fine-march number.

---

## Related resources

- The five passes, the mask-first reorder, and the fused-cull occupancy tax:
  [`.agents/skills/sdf-world/SKILL.md`](../../.agents/skills/sdf-world/SKILL.md)
  ("The MASK-FIRST pass order") and `SdfWorldEngine.PassLabels`.
- The beam-slope sweep, the mask-first before/after table, and the carve ladders:
  [`docs/sdf-bench-notes.md`](../sdf-bench-notes.md).
- The uniform-grid cull rationale and why a per-frame BVH was rejected for it:
  [`docs/sdf-wiki/hierarchical-and-instance-acceleration.md`](../sdf-wiki/hierarchical-and-instance-acceleration.md).
- The two-deep frame ring, its per-slot fences, and the cross-frame scratch
  barrier: `FrameRingSize` and the `Record` method in
  [`src/Puck.SdfVm/SdfWorldEngine.cs`](../../src/Puck.SdfVm/SdfWorldEngine.cs).
- Render-scale quantization and the byte-exact native path: the `RenderScale`
  sync-pair row in the sdf-world skill.
