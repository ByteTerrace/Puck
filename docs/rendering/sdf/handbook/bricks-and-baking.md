# Bricks and baking

Puck caches a distance field only for settled, dense carve clusters where
spatial culling cannot reduce the work. A brick stores a small 3D sample of the
carve distance; the `√3` scale keeps trilinear interpolation safe to march. The
settle → bake → swap → invalidate lifecycle keeps that cache current while the
analytic program remains the source of truth. A prototype bake, the mesh,
textures and impostor a creation's field is sampled into, is the other kind of
baking.

## The problem: a cluster no cull can save

Everywhere else in the engine, the answer to "too many instances" is spatial
culling—bin instances into a grid, and each tile touches only the few near it
([SDF frame rendering](frame-rendering.md)). That works beautifully when content is *spread out*. It does
nothing when content is *stacked*.

Picture a wall the player has shot a thousand craters into, all overlapping in one
spot. Each crater is a subtraction instance. The spatial cull bins them, but they
all land in the same handful of tiles, so every march sample in that region still
enumerates all thousand of them. The per-tile mask can't narrow a set where every
member overlaps the same tile. The ceiling is steep: with 1024 carves densely
stacked in frame, every fine-march sample in the `views` pass enumerates all
1024 instances, and the matching shadow re-march does the same again. That
`views` cost is real geometry cost, and no amount of culling removes it —
because the work genuinely *is* a thousand overlapping surfaces in those pixels.

The observation that unlocks the fix: once a cluster of carves has *settled*—the
player stopped shooting, the damage is final—its combined distance field in that
region is a fixed function of space. Computing it fresh a thousand-carves-deep,
every sample, every frame, is paying analytic cost for an answer that no longer
changes. So cache it.

## What a brick is

A **brick** is a small 3D photograph of distance. Take the axis-aligned box that
bounds a settled cluster, lay a cubic-voxel lattice over it (up to 128³ voxels,
one float per voxel), and at each voxel store the distance to the nearest carved
surface. At render time, instead of evaluating a thousand carve instances, the
kernel does one thing: read the eight voxels around the sample point and
trilinearly interpolate. That is an **O(1)** field lookup—constant cost no
matter how many carves went into the brick.

Crucially, a brick stores the settled carves' **union distance field** —
`min over carves of (|p − center| − radius)`—*not* the finished scene. The
program then composes the brick into the scene as one ordinary **subtraction**
instance: `max(accumulator, −brickSample)`. This is the crack-free formulation,
and the choice is deliberate. Subtraction is local and far-neutral (the
accumulator rule, [SDF program model](program-model.md)): outside the carve volumes the sampled candidate
never wins and the accumulator returns *to the bit*. The subject the carves bite
into—the wall, the terrain—stays fully analytic everywhere, inside the brick's
box and out. Nothing is dual-represented, so there is no boundary to stitch and no
seam to get wrong. The alternative—baking the subject *and* its carves into one
replacement brick—would need the whole thing re-baked every time the subject
changed (a relit screen, an edit) and a shell blend at the box faces to hide
cracks. The subtraction brick needs neither.

In the ISA a brick is a single shape, `SampledRegion`, evaluated like any other
primitive and composed through the existing subtraction blend tail. It gets a real
cull bound (the box's circumscribed sphere), bins into the spatial grid by center
like any static instance, and is automatically classified shadow-transparent
because it's a pure subtraction. A thousand carve instances in a cluster's tiles
collapse to *one* brick instance—which means the beam's per-tile enumeration
collapses with the same stroke that the fine march does.

## The √3 rule keeps sampled marching safe

Here is the subtlety that makes a sampled field safe to march. Sphere tracing is
safe only if the field never overshoots—formally, if it's a valid lower bound on
the distance to its own zero set, which any 1-Lipschitz field satisfies
automatically. The *true* carve-union distance is exactly 1-Lipschitz, and each
axis finite-difference of its samples is therefore at most 1. But the trilinear
**interpolant** between samples has a gradient that can reach `√3`—all three
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
consequence is a slightly darker penumbra right at carved edges—the same
accepted posture as the step-clamped warps.

Outside the box, the brick reports `distance-to-box + boundaryFloor` (a
host-baked margin term), a valid positive lower bound that keeps the subtraction
saturated and the accumulator exact. The brick's actual zero set is grown a margin
*inside* the box by construction, so its surface never touches a face and no
rendered seam can exist. Kernels that don't bind the brick pool at all fall back
to `SDF_FAR_DISTANCE`—the subtraction never bites and the region renders as the
uncarved hull: solid, never a hole. That is the same conservative-fallback
precedent the glyph shape set, and it means a brick program stays honest even
through a kernel that can't sample it.

Sampling is a plain storage-buffer read with **manual trilinear interpolation** —
eight explicit loads and lerps with floating-point contraction pinned off—so it
is bit-stable across both GPU backends by the same argument as the point
evaluator. (Hardware 3D textures with sampler filtering were evaluated and
rejected for now: their lerp precision is driver-variant, which would break
cross-backend parity, and they'd need new abstraction seams where buffers already
flow everywhere.)

## Settle, bake, swap, and invalidate

The cache is kept honest by a four-stage lifecycle, all of it off the live edit
path so a carve never waits on a bake.

**Settle.** A planner bins carves by center into brick-sized boxes. A bin becomes
bake-eligible only when it holds enough hard carves to be worth a brick's fixed
footprint (below a threshold, the analytic instances are simply cheaper), and it
"settles" only after 120 produced frames pass with no edit touching it
(`SdfCarveBakePlanner.DefaultSettleFrames`). In-flight
carves—a live meteor shower, a cluster mid-edit—stay fully analytic. Nothing
is cached until it stops changing.

**Bake.** When a bin settles, a tiny standalone compute kernel writes its brick:
one thread per voxel, each computing the closed-form min-of-spheres distance and
storing it as `c/√3`. A worst-case brick is 128³ voxels against ~1000 carves —
about 2 billion evaluations—so the bake is **sliced** across frames at ≤256K
voxels per brick per produced frame, prepended to the frame's pass list
*before* the counted passes so it doesn't charge the render budget. Slicing changes
no values; each voxel is written once regardless of slice boundaries. A full
worst-case brick finishes in a handful of frames, well inside the settle window.

**Swap.** The handoff is two ordinary revision-bump rebuilds—the same machinery
the spatial cull already uses, nothing new. First the planner requests the bake
and *keeps emitting the analytic carves*: the frame is never wrong, merely still
slow. Then, when the engine reports the slot `Ready` (its final slice and the
state flip are ordered on the GPU queue, so `Ready` means the pool contents are
complete), the planner bumps its revision and the next rebuild emits the single
brick instance in place of that bin's carve instances. No callbacks, no
cross-thread seams—the frame source polls a slot state each frame.

**Invalidate.** A new or removed carve inside a baked bin re-emits that bin
analytic in the *same* rebuild that adds the carve—atomic by construction, so
the moment you edit a settled cluster it snaps instantly back to the exact
analytic field, and the slot re-bakes on the next settle. Other bins' bricks are
untouched: invalidation is per-region, not global.

The payoff in counted work is the whole reason the tier exists. Once the
clustered-carve scene bakes, the beam's per-tile enumeration collapses
(~1000 carve instances → ~3: floor, subject, brick), the primary march trades
O(1024) per sample for one trilinear read, and shadows and ambient occlusion pay
one brick fetch instead of a thousand. Every non-clustered scene is untouched:
where nothing settles, the switch does nothing and the output is bit-identical to
the analytic path.

## Bricks are a cache, not the representation

The engine flirts here with something it otherwise firmly rejects: a baked,
discretized volume. That whole family (voxel DAGs, brickmaps, distance-field
grids, the various GI caches) was surveyed and rejected *as a core-representation
change*, with exactly one recorded reconsider-trigger—"if a baked grid is ever
added as a distinct tier *alongside* the analytic VM." The brick tier is that
trigger, and it is built to honor the rejection's substance, not evade it. The
governing rules:

- **The analytic program is the representation of record.** A brick is a compiled
  *cache* of a bounded region's settled subtraction content. It can be rebuilt
  from the analytic carve list at any time and is invalidated by any edit inside
  its bounds. **Deleting every brick must always reproduce the identical scene**,
  only slower. This is the key invariant—a brick is never the truth,
  only a fast copy of it.

- **Bricks are session-transient GPU state, never durable data.** They are never
  written to world documents, creations, replays, or content-addressed storage. The
  carve *list* is the durable data; the brick is derived. Reload the scene and the
  bricks re-bake from scratch.

- **Bricks are never used for simulation.** CPU physics queries and the
  field evaluator ([Queries and determinism](queries-and-determinism.md)) never read a brick—they walk the analytic
  program. The brick exists for the render marches and nothing else.

- **You never author *into* a brick.** No voxel editing, no CSG against sampled
  data. Every edit goes to the analytic carve list; the brick is re-derived. And
  the brick never holds the *subject*—only the subtraction content—which is
  exactly what keeps the seam crack-free.

The single sentence to remember: **the brick is a small photograph of a field the
program already knows how to compute; the program is always the negative you can
reprint from.** That discipline is what lets a discretized cache live inside a
purely-analytic engine without quietly becoming the thing the engine promised not
to be.

## Prototype bakes

`SdfBaker` (`src/Puck.SignedDistance/Baking`) turns a program into presentation
assets by reading the field through `SdfFieldEvaluator`, the same interpreter
contact and queries read ([Queries and determinism](queries-and-determinism.md)),
so there is no second evaluator to keep in step. A bake is a mesh, five surface
textures, and an impostor. Like a brick, it is a cache of a field the program
already computes: it is presentation only, and nothing it produces is read back
into simulation state. Unlike a brick, it is durable data keyed by its creation's
content, so a build ships it in its bake pack and a device keeps it.

**The mesh** is extracted by dual contouring over a cube of cells around the
program's reach, one empty cell of padding on each side. A block of four cells a
side whose center's scaled field value proves no surface lies within the block
takes that center's sign at every corner, so empty space costs one evaluation
per block; a corner is evaluated only when a sign-changing edge needs its value.
Each crossed cell's vertex is the least-squares point of the tangent planes at
its edges' crossings, drawn weakly toward their mean and clamped into the cell,
and its normal is the field's gradient there. Each quad is split along the
diagonal whose triangles face the way the corner normals point, else the shorter
one. The mesh stays within half a cell of the field: the largest field magnitude
at any vertex, edge midpoint or triangle centroid is at most half the cell size.
A feature thinner than a cell can fold a quad inward; every feature the standard
tier is expected to show spans more than two cells.

**The surface textures** give each quad one 4-texel-square tile of an atlas, its
corners on the centers of the tile's corner texels, so bilinear filtering inside a
quad never reads another quad's texels and a 4x4 block-compressed block never
spans two quads. A texel samples the field at the quad's bilinear point, steps
onto the surface along the gradient, and records:

| Texture | Stored as | Values |
|---|---|---|
| Albedo | BC7, sRGB | The winning material's albedo; alpha is opaque |
| Normal | BC5, linear | The unit gradient in the prototype's frame, as an octahedral pair |
| Occlusion | BC4, linear | One minus the weighted occlusion of four probes along the normal, one to four cells out |
| Material | R8, linear, uncompressed | The winning material id, an identity that is never blended or compressed lossily |
| Emission | BC6H, linear | The winning material's linear albedo times its emissive strength, in half precision, which may exceed one |

A texel shared by two quads, on a corner or a shared edge, is sampled once.
`SdfBakedTexture.PlanFor` is the one statement of each usage's format, color
space and mip filter.

The octahedral pair (`OctahedralNormal` in `Puck.Assets.Textures`) stores any
direction in two channels: the direction projects onto the octahedron
`|x| + |y| + |z| = 1`, the lower half folds across the diagonals, and the decode
reconstructs `z` and normalizes. It holds object-space normals, which point every
way, where a tangent-space `z = sqrt(1 - x² - y²)` could not; one pair of 8-bit
codes stays within about a degree of the direction it stores.

**Mips are tile-aware.** Every texture is a mip chain whose levels halve with a
2x2 box inside one tile, so no level mixes two tiles, and the chain ends at the
level where each tile is one texel (`TextureMipChain`): three levels for a
surface tile of four texels, and five for a standard impostor view of sixteen.
Below that a texel would span several tiles, so no such level exists. A renderer
sampling level `l` keeps each lookup inside its tile's `TileTexels >> l` texels,
because a bilinear tap at a tile's edge otherwise reads the neighbor; that
clamped per-tile filtering is chosen over gutters, which would break the 4x4
block alignment. Each usage filters its own way:

- albedo decodes its sRGB codes to linear light through the exact
  `ImageSourceConversion.Srgb8ToLinear`, averages them weighted by alpha, and
  encodes again through `LinearToSrgb8`;
- normals decode, sum, and encode again, which renormalizes them;
- occlusion and depth average their codes, and emission averages its halves in
  double and rounds to the nearest half;
- material identity keeps the id most of its four texels hold, the smallest id
  winning a tie. A box footprint has no nearest texel, since all four are
  equidistant from its center, and a blended id would name a material nothing
  authored.

**Compression** is `Puck.Assets.Textures`: a CPU encoder per format whose bytes
are the same on every machine, and an exact decoder that is its test oracle.
BC7 writes whichever of its modes decodes nearest: mode 6, mode 5 when a block's
alpha runs independently of its color, or a partitioned mode, which splits the
block into two or three subsets with their own endpoints, when the block holds
colors no one line does. It stores a one-color block exactly. BC5 is two BC4
blocks, and BC4 keeps the better of its two palettes. BC6H writes the one-region
modes 11 to 14 and stores a one-value block exactly; it does not write a
two-region mode yet, and its decoder refuses one. On the codec laws' natural
test image (gradients, noise and a hard-edged disc), BC4 decodes within 2 codes
of the source (root-mean-square 0.63), BC5 within 8 (0.88), and BC7 within 10
(2.2); BC6H
stays within 8% of each value in smooth blocks and 23% in a block across a hard
edge between two colors, which a one-region mode cannot hold both of. From the
second level down a block holds four
or sixteen tiles; the filter never mixes them, but they share the block's
endpoints, so compression error there is shared within those bounds.

**The impostor** is octahedral: a grid of orthographic views, each marched one
ray per texel through the evaluator's own march, covering every direction with +Y
as the octahedron's pole, so a camera below a flying body is covered as well as
one above a prop. It stores albedo with coverage in alpha (BC7), the normal as an
octahedral pair (BC5; a miss holds the direction toward the view's camera), and
the hit's depth across the bounding sphere (BC4; a miss is the far side). Each
view is a tile of its chains, and the normal and depth mips are weighted by the
albedo's coverage, so empty texels never bend a silhouette's normals or pull its
depth. A single billboard is right from one direction only, which no orbiting or
overhead camera satisfies.
| Tier | Cells a side | Impostor |
|---|---|---|
| `Preview` | 12 | 4 x 4 views of 8 x 8 texels |
| `Standard` | 32 | 8 x 8 views of 16 x 16 texels |

`SdfBakeWork` counts every evaluation a bake made, split by mesh, textures and
impostor, and the rays, vertices and triangles; a bake is judged by those counts,
never by wall-clock time.

A bake is the same bytes on every machine, which it must be: a pack built on one
machine stands in for a bake made on another. Everything a bake reads from the
field is fixed point. Every float it writes comes from scalar addition,
subtraction, multiplication, division and square root, each correctly rounded,
in a written order. No transcendental function is used, and no `Vector3`
operation whose multiply-add the JIT may fuse. Albedo is sRGB-encoded by
`ImageSourceConversion.LinearToSrgb8`, which compares the linear value with the
exact decoded midpoints between codes rather than calling `Math.Pow`. The
lattice's size comes from the creation's reach, taken without its flares:
contact emits no flare, and a flare's extrema go through `MathF.Acos` and
`MathF.Sin`. The mip filters and block encoders are integer arithmetic or scalar
double arithmetic in a written order, and `TextureCodecLawTests` pins each
encoder's bytes for a fixed image.

A creation reaches the baker through `CreationBaker`, which emits it at its own
origin and unit scale with `CreationStampEmitter.EmitFixed`, the emission contact
reads. A bake therefore shows only what the fixed-point interpreter answers for:
detail shapes, sweeps, text runs, noise relief, volumes, and the warp facets
contact omits are absent, and a creation the interpreter refuses has no bake. A
palette slot whose color is bound to a state cell bakes the fallback gray, though
its material texels still name the slot. How bakes are keyed, shipped in a
build's bake pack, and baked on a device is in
[creation bakes](../../../architecture/worlds.md#creation-bakes).

---

## Related resources

- API contracts for bake requests, pool layout, and sampled-region packing:
  `SdfBrickBake`, `SdfBrickPoolLayout`, and `SdfProgramBuilder.SampledRegion`
  in [`src/Puck.SignedDistance`](../../../../src/Puck.SignedDistance/); the
  interactive carve-pool planner, `SdfCarveBakePlanner`, stays in
  [`src/Puck.SdfVm`](../../../../src/Puck.SdfVm/) (an engine behavior, not
  field-as-data).
- The `SampledRegion` shape, its lane layout, cull bound, and conservative
  fallback: the `SampledRegion` sync-pair row in
  [`.claude/skills/rendering/SKILL.md`](../../../../.claude/skills/rendering/SKILL.md)
  and the enum doc in [`src/Puck.SignedDistance/SdfShapeType.cs`](../../../../src/Puck.SignedDistance/SdfShapeType.cs).
- The clustered-vs-spread penalty and the clustered `views` ceiling the brick
  targets: the carve ladders that measured both were removed and
  are recorded nowhere else. The motivation for the brick survives in this
  chapter; the numbers that sized it do not.
- The prototype baker and its key and cache: `SdfBaker` in
  [`src/Puck.SignedDistance/Baking`](../../../../src/Puck.SignedDistance/Baking/),
  `CreationBaker` in [`src/Puck.World.Authoring`](../../../../src/Puck.World.Authoring/),
  and [creation bakes](../../../architecture/worlds.md#creation-bakes).
- The baked-volume family rejection and its single reconsider-trigger:
  [Rejected and conditional SDF techniques](../reference/negative-results-and-rejections.md).
