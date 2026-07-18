# Bricks and baking

After this chapter you'll understand the one place Puck deliberately caches a
distance field instead of computing it analytically: why dense clusters of carves
defeat every spatial trick the engine has, what a "brick" is and why it's a small
3D photograph of distance, the `√3` scaling trick that keeps a sampled field
march-safe for free, the settle → bake → swap → invalidate lifecycle that keeps
the cache honest, and the firm doctrine that the analytic program always remains
the source of truth.

## The problem: a cluster no cull can save

Everywhere else in the engine, the answer to "too many instances" is spatial
culling — bin instances into a grid, and each tile touches only the few near it
([chapter 3](03-the-frame.md)). That works beautifully when content is *spread out*. It does
nothing when content is *stacked*.

Picture a wall the player has shot a thousand craters into, all overlapping in one
spot. Each crater is a subtraction instance. The spatial cull bins them, but they
all land in the same handful of tiles, so every march sample in that region still
enumerates all thousand of them. The per-tile mask can't narrow a set where every
member overlaps the same tile. Measured on the reference GPU, clustered carves
cost about 6.3× more than the same count spread out, and the ceiling is steep:
1024 carves densely stacked in frame spend ~54 ms in the fine-march `views` pass
alone, plus a matching shadow re-march, for a frame well under 30 fps. That
`views` ceiling is real geometry cost, and no amount of culling removes it —
because the work genuinely *is* a thousand overlapping surfaces in those pixels.

The observation that unlocks the fix: once a cluster of carves has *settled* — the
player stopped shooting, the damage is final — its combined distance field in that
region is a fixed function of space. Computing it fresh a thousand-carves-deep,
every sample, every frame, is paying analytic cost for an answer that no longer
changes. So cache it.

## What a brick is

A **brick** is a small 3D photograph of distance. Take the axis-aligned box that
bounds a settled cluster, lay a cubic-voxel lattice over it (up to 128³ voxels,
one float per voxel), and at each voxel store the distance to the nearest carved
surface. At render time, instead of evaluating a thousand carve instances, the
kernel does one thing: read the eight voxels around the sample point and
trilinearly interpolate. That is an **O(1)** field lookup — constant cost no
matter how many carves went into the brick.

Crucially, a brick stores the settled carves' **union distance field** —
`min over carves of (|p − center| − radius)` — *not* the finished scene. The
program then composes the brick into the scene as one ordinary **subtraction**
instance: `max(accumulator, −brickSample)`. This is the crack-free formulation,
and the choice is deliberate. Subtraction is local and far-neutral (the
accumulator rule, [chapter 2](02-the-program-model.md)): outside the carve volumes the sampled candidate
never wins and the accumulator returns *to the bit*. The subject the carves bite
into — the wall, the terrain — stays fully analytic everywhere, inside the brick's
box and out. Nothing is dual-represented, so there is no boundary to stitch and no
seam to get wrong. The alternative — baking the subject *and* its carves into one
replacement brick — would need the whole thing re-baked every time the subject
changed (a relit screen, an edit) and a shell blend at the box faces to hide
cracks. The subtraction brick needs neither.

In the ISA a brick is a single shape, `SampledRegion`, evaluated like any other
primitive and composed through the existing subtraction blend tail. It gets a real
cull bound (the box's circumscribed sphere), bins into the spatial grid by center
like any static instance, and is automatically classified shadow-transparent
because it's a pure subtraction. A thousand carve instances in a cluster's tiles
collapse to *one* brick instance — which means the beam's per-tile enumeration
collapses with the same stroke that the fine march does.

## The √3 trick: march-safe sampling for free

Here is the subtlety that makes a sampled field safe to march. Sphere tracing is
safe only if the field never overshoots — formally, if it's a valid lower bound on
the distance to its own zero set, which any 1-Lipschitz field satisfies
automatically. The *true* carve-union distance is exactly 1-Lipschitz, and each
axis finite-difference of its samples is therefore at most 1. But the trilinear
**interpolant** between samples has a gradient that can reach `√3` — all three
axis slopes near 1 at once, at a crease where carves meet (the 2D `min(x, y)`
corner is the intuition: its interpolated gradient reaches `√2`). A field whose
gradient exceeds 1 can underestimate how fast a surface approaches, and an
unclamped march could tunnel through it.

The fix costs nothing at runtime: **the bake divides every stored value by
`λ = √3`.** The interpolant of `c/√3` is 1-Lipschitz by construction, so it is
march-safe with *no* change to the program's `stepScale`, no runtime multiply, and
an unchanged zero set (dividing by a positive constant doesn't move where the
field is zero). The march simply approaches carved surfaces at `1/√3` speed —
*inside the brick's cull bound only*, so there is no global step tax on the rest of
the scene. This is the same `distanceScale`-channel discipline the rest of the
engine uses: a value-level scale, never touching the step clamp. The only visible
consequence is a slightly darker penumbra right at carved edges — the same
accepted posture as the step-clamped warps.

Outside the box, the brick reports `distance-to-box + boundaryFloor` (a
host-baked margin term), a valid positive lower bound that keeps the subtraction
saturated and the accumulator exact. The brick's actual zero set is grown a margin
*inside* the box by construction, so its surface never touches a face and no
rendered seam can exist. Kernels that don't bind the brick pool at all fall back
to `SDF_FAR_DISTANCE` — the subtraction never bites and the region renders as the
uncarved hull: solid, never a hole. That is the same conservative-fallback
precedent the glyph shape set, and it means a brick program stays honest even
through a kernel that can't sample it.

Sampling is a plain storage-buffer read with **manual trilinear interpolation** —
eight explicit loads and lerps with floating-point contraction pinned off — so it
is bit-stable across both GPU backends by the same argument as the point
evaluator. (Hardware 3D textures with sampler filtering were evaluated and
rejected for now: their lerp precision is driver-variant, which would break
cross-backend parity, and they'd need new abstraction seams where buffers already
flow everywhere.)

## The lifecycle: settle, bake, swap, invalidate

The cache is kept honest by a four-stage lifecycle, all of it off the live edit
path so a carve never waits on a bake.

**Settle.** A planner bins carves by center into brick-sized boxes. A bin becomes
bake-eligible only when it holds enough hard carves to be worth a brick's fixed
footprint (below a threshold, the analytic instances are simply cheaper), and it
"settles" only after a couple of seconds pass with no edit touching it. In-flight
carves — a live meteor shower, a cluster mid-edit — stay fully analytic. Nothing
is cached until it stops changing.

**Bake.** When a bin settles, a tiny standalone compute kernel writes its brick:
one thread per voxel, each computing the closed-form min-of-spheres distance and
storing it as `c/√3`. A worst-case brick is 128³ voxels against ~1000 carves —
about 2 billion evaluations — so the bake is **sliced** across frames at ≤256K
voxels per frame (a background-budget ~1–2 ms), prepended to the frame's pass list
*before* the timing marks so it doesn't charge the render budget. Slicing changes
no values; each voxel is written once regardless of slice boundaries. A full
worst-case brick finishes in a handful of frames, well inside the settle window.

**Swap.** The handoff is two ordinary revision-bump rebuilds — the same machinery
the spatial cull already uses, nothing new. First the planner requests the bake
and *keeps emitting the analytic carves*: the frame is never wrong, merely still
slow. Then, when the engine reports the slot `Ready` (its final slice and the
state flip are ordered on the GPU queue, so `Ready` means the pool contents are
complete), the planner bumps its revision and the next rebuild emits the single
brick instance in place of that bin's carve instances. No callbacks, no
cross-thread seams — the frame source polls a slot state each frame.

**Invalidate.** A new or removed carve inside a baked bin re-emits that bin
analytic in the *same* rebuild that adds the carve — atomic by construction, so
the moment you edit a settled cluster it snaps instantly back to the exact
analytic field, and the slot re-bakes on the next settle. Other bins' bricks are
untouched: invalidation is per-region, not global.

The measured payoff is the whole reason the tier exists. The clustered-carve
scene that sat at ~28 fps with analytic carves and a shadow proxy is reasoned to
reach the ~120-fps class once baked — the beam's per-tile enumeration collapses
(~1000 carve instances → ~3: floor, subject, brick), the primary march trades
O(1024) per sample for one trilinear read, and shadows and ambient occlusion pay
one brick fetch instead of a thousand. Every non-clustered scene is untouched:
where nothing settles, the switch does nothing and the output is bit-identical to
the analytic path.

## Cache, not representation — the doctrine

The engine flirts here with something it otherwise firmly rejects: a baked,
discretized volume. That whole family (voxel DAGs, brickmaps, distance-field
grids, the various GI caches) was surveyed and rejected *as a core-representation
change*, with exactly one recorded reconsider-trigger — "if a baked grid is ever
added as a distinct tier *alongside* the analytic VM." The brick tier is that
trigger, and it is built to honor the rejection's substance, not evade it. The
governing rules:

- **The analytic program is the representation of record.** A brick is a compiled
  *cache* of a bounded region's settled subtraction content. It can be rebuilt
  from the analytic carve list at any time and is invalidated by any edit inside
  its bounds. **Deleting every brick must always reproduce the identical scene**,
  only slower. This is the load-bearing invariant — a brick is never the truth,
  only a fast copy of it.

- **Bricks are session-transient GPU state, never durable data.** They are never
  written to run documents, creations, replays, or content-addressed storage. The
  carve *list* is the durable data; the brick is derived. Reload the scene and the
  bricks re-bake from scratch.

- **Bricks are never load-bearing for simulation.** CPU physics queries and the
  field evaluator ([chapter 7](07-queries-and-determinism.md)) never read a brick — they walk the analytic
  program. The brick exists for the render marches and nothing else.

- **You never author *into* a brick.** No voxel editing, no CSG against sampled
  data. Every edit goes to the analytic carve list; the brick is re-derived. And
  the brick never holds the *subject* — only the subtraction content — which is
  exactly what keeps the seam crack-free.

The single sentence to remember: **the brick is a small photograph of a field the
program already knows how to compute; the program is always the negative you can
reprint from.** That discipline is what lets a discretized cache live inside a
purely-analytic engine without quietly becoming the thing the engine promised not
to be.

---

## Related resources

- API contracts for bake requests, pool layout, planning, and sampled-region
  packing: `SdfBrickBake`, `SdfBrickPoolLayout`, `SdfCarveBakePlanner`, and
  `SdfProgramBuilder.SampledRegion` in [`src/Puck.SdfVm`](../../src/Puck.SdfVm/).
- The `SampledRegion` shape, its lane layout, cull bound, and conservative
  fallback: the `SampledRegion` sync-pair row in
  [`.agents/skills/sdf-world/SKILL.md`](../../.agents/skills/sdf-world/SKILL.md)
  and the enum doc in [`src/Puck.SdfVm/SdfShapeType.cs`](../../src/Puck.SdfVm/SdfShapeType.cs).
- The clustered-vs-spread penalty and the clustered `views` ceiling the brick
  targets: [`docs/sdf-bench-notes.md`](../sdf-bench-notes.md) (the carve ladders).
- The baked-volume family rejection and its single reconsider-trigger:
  [`docs/sdf-wiki/negative-results-and-rejections.md`](../sdf-wiki/negative-results-and-rejections.md).
