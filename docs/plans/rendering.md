# Rendering

The frame rasterizes baked SDF meshes with shadow cascades and an irradiance
volume traced on the GPU. This plan holds the remaining sky vocabulary, wallpaper
bounds, rendering follow-ups and hardware observations. The debug world
(`worlds/debug`) proves the work on both backends; features from experimental
worlds are ported into it rather than reviving those worlds.

The [rendering decisions](../decisions/rendering.md) own the design, the
[shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
owns pipeline authoring, and the [frame handbook](../rendering/sdf/handbook/frame-rendering.md)
and [rendering skill](../../.claude/skills/rendering/SKILL.md) own the implemented
contracts. Delivered package history belongs in git.

## What is open

Each package below still owes something; a package with only a hardware
observation left is listed under [deferred checks](#deferred-to-the-end). The
[shared queue](open-items.md#next) ranks work across programmes.

| Package | Open remainder |
|---|---|
| P1a, durable functional fixtures | [No-driver boot](#deferred-to-the-end) |
| P1b, foundation qualification | [Reference devices and driver removal](#p1b--foundation-qualification) |
| The RTX 2060's frame | [A reading of the player build, then the passes it names](#the-rtx-2060s-frame) |
| P10, bound rows and tiers | [Hardware qualification at high](#deferred-to-the-end) |
| P12, image sources | [Real camera](#deferred-to-the-end) |
| P13, hit-to-source mapping and input | [Windows passthrough click](#p13--hit-to-source-mapping-and-input-destinations) |
| P14, SDF pass package | [Bake quality, glass, submission and routed worlds](#p14--the-sdf-engine-as-a-pass-package) |
| P15, temporal reconstruction | [The march-step signal and the Steam Deck observation](#p15--temporal-reconstruction) |
| P16, display output | [HDR display](#deferred-to-the-end); [the colour conversion's remainder](#colour-conversion--scene-light-to-display-values-once) |
| P18, sky and atmosphere | [Celestial bodies and infinity-view admission](#p18--sky-and-atmosphere) |
| P19, wallpaper-fold bounds | [CPU certification and bake lattice](#p19--neighbour-content-bounds-for-wallpaper-folds) |
| P20, visibility and residency | [Observations](#p20--visibility-areas-and-residency) |

## The forcing artifact

Every change proves itself in the debug world: its device row, gallery, nested
screens and portals, sky and valley. Captures and live counters show what a
change does; `host.frames` and `world.gpu-timing` on a quiet machine show its
cost. Canaries under `experimental/tests/Puck.World.Canaries` are quarantined.

[The frame bar](../decisions/rendering.md#the-pipeline-foundation) is the
worst frame at the highest quality on every bar device, read by
[one recipe](streaming-and-memory.md#how-the-bars-are-read) with counted work
beside it. The [engine job system](open-items.md#engine-job-system)
owns scheduling, parallel tick execution and view recording; the
[bake work](open-items.md#bake-proportional-to-change) owns bake speed and identity.

## Packages

### P1b — Foundation qualification

The remaining reference-device and driver-removal observations are listed
under [deferred checks](#deferred-to-the-end). Qualify the debug world at
`high` and retain the actual memory and timing readings. No recorded work-count
ceiling substitutes for a device observation.

### The RTX 2060's frame

The RTX 2060 machine is a release target that stands in for a consumer
([AGENTS.md](../../AGENTS.md#the-bars)). It runs the player build of a
release, installed and updated by the launcher from the player channel, with
the mesh caches the lead machine baked
([the player channel](../decisions/runtime-and-delivery.md#the-player-channel)).
It never builds from source, compiles a cartridge or cold-bakes, so every
reading taken on it is a reading of what a player runs. Its frame bar is read
at 1920 by 1080 on its own device table, the `rtx2060` row of `render.devices`
in `worlds/standard/quality.puck`. A windowed boot applies that table on the
frame thread once the device names its adapter, through the same door
`world.device-profile` takes (`WorldDeviceProfileState.Select`).

No current reading exists. The only one was taken from a source Release build
before the table ever applied (`world.device-profile` echoed `table=none`),
before the far cascade held a static layer, before distant redraws were
sliced, and before the frame thread stopped waiting on pipeline-cache writes
and device idles. None of its figures stand. It showed where to look first:
the views pass was the largest GPU pass by far, the shadow passes and the
resolve came next, the simulation and the frame's produce each took a large
share of the CPU's frame, and host memory, not device memory, was what the
16 GB machine ran out of.

The steps, each after the one it names:

1. **The reading.** The prewarmed player build of a release, through the
   player channel, booted windowed at 1920 by 1080 with `--devices none` on
   each backend. At the stage and over the debug route (`world.benchmark run
   debug`, typed into the build's console, as
   [the benchmark's frame-bar reading](benchmark.md#comparing-runs) describes for
   this machine), read `world.device-profile` and `world.quality`,
   `world.iteration`, `host.frames`, `world.gpu-timing` with its timeline,
   `world.rates`, `host.memory` and a gc-verbose trace. A pass's rolling mean
   counts only the frames that recorded it (`ShaderPipelineRenderNode`'s
   timing window), so a held pass's mean is the cost of one redraw: the
   timeline shows the frame's real shape. A gap between the backends is
   attributed only at matched clocks or from a capture
   ([the Vulkan packet](open-items.md#vulkan-regressions-and-gpu-robustness)
   records the earlier, unattributed one). Check: the reading names the
   device, size, backend, topology and the table it ran (`table=rtx2060`), and
   the Direct3D 12 boot shades through its prepared kernels with no shader
   compiler on the machine (`PreparedShaderBindings`), not through the
   interpreter.
2. **The views pass.** After 1. Nothing yet attributes this pass on this
   device. The table's render scale shades a little over half the display's
   pixels, which by arithmetic alone leaves the old reading's views pass above
   the whole frame's 16.7 ms. Read its share on the table first, then capture
   the views kernel on this device (register count, spills and occupancy, and
   its walk over the lights) to name what limits it, with the surface pass,
   which also shades by pixel, read in the same capture. Compare the 2060
   table's `viewsRate.maxRate` at 1 and 2, keeping render scale and the other
   rows fixed. Read `sdf.world$views`, `sdf.world$views-probes` (which
   classifies the tiles) and `sdf.world$resolve` together. Pair the
   [tile counts](../rendering/sdf/handbook/frame-rendering.md#temporal-reconstruction)
   with gallery and valley stills and a pan past ink and specular detail.
   The 4070 can run the same profile for the initial comparison. The player
   build on the 2060 supplies the frame-bar reading.
3. **The resolve.** After 1. It runs at the display's size whatever the render
   scale, since its history follows the display
   (`SdfWorldPackage.HistoryWindowWords`). Its window's memory traffic is not
   what limits it: a still pixel moves about 168 bytes of window a frame,
   about 0.35 GB a frame at 1920 by 1080, about 1 ms of the RTX 2060's
   336 GB/s, where the old reading's resolve took ten times that. Its limiter
   is unknown, so the step is a capture first (register spills of the luma
   window it gathers, and occupancy), and no change is made before it. A
   running sum over the window is not pursued unless the capture shows memory
   traffic: it would save about a third of a small share and give up the
   window's fixed-order sum. A vendor's upscaler as an authored alternative is
   [rank 9](rendering-research.md#gpu-capabilities-puck-does-not-use-yet).
   The [state-of-the-art audit](state-of-the-art/audit.md#1-the-resolve-does-per-output-pixel-work)
   adds the design the capture decides between. The resolve loads a 5 by 5
   luma window, more colour, fog and a rebuilt motion vector for every output
   pixel, so each render sample is read about 44 times at a render scale of
   0.75 (arithmetic, not measured), where FSR 2 and Unreal's temporal super
   resolution do that work at render resolution. The alternative is one
   compact record per render pixel, written in the views pass's epilogue
   (luma and chroma halves, a feature bit, reactivity, identity, distance,
   packed motion and packed transport), with the resolve reading nine records
   or a 12 by 12 group-shared tile. Build it only if the capture shows the
   limiter is the live registers and memory operations (about 180 a pixel)
   and not bandwidth. Check: the capture names the limiter; then the same
   route reads fewer loads for each output pixel and the resolve pass is no
   slower, with the history identity and motion vectors unchanged.
4. **The shadows.** First on the RTX 4070, since neither change below has run
   on a GPU. At an authored rate below every frame (`farShadows`,
   `SdfFarShadows.HoldsStaticLayer`), the far cascade holds a static layer and
   draws only the moving casters over it. The distant map redraws only for a
   changed caster (`SdfMeshDraw.SameCaster`), and a table that spreads a
   redraw over frames (`distantShadows.frames`, `SdfDistantShadows.Slices`)
   draws one share a frame into a back map and commits the map when the last
   share lands. Check: `world.rates` counts the far cascade held between
   refreshes and its static layer redrawn (`shadow-far.static`) only when a
   static caster changes; a body walking under the far cascade casts a shadow
   that keeps up at the authored rate; a sliced redraw shows no partly drawn
   map in motion; then, on the 2060, its shadow passes' timeline at the stage
   and on the route.
5. **Device and host memory.** After 1. Read live residency and view-target
   bytes, including device-local and host-visible device-local use and
   Vulkan's aperture heaps, live and retiring residency and graph bytes, and
   the host's private bytes and managed heap. Share tables for the same
   mirror and stage infrequently changed data where the measured budget calls
   for it. The `rtx2060` row sets a device bar and no host bar, so its host
   pool follows the account's own budget (`MemoryAccount.DefaultBudget`); the
   [streaming plan](streaming-and-memory.md#how-the-bars-are-read) sets the
   RTX 4070's and the Steam Deck's memory bars and no plan yet sets this
   machine's host bar.
6. **The one-off passes.** After 1. The old reading held one sample of a
   `glass-clear` pass costing about as much as the resolve. Whether it
   recurs, and on what event, is read from the timeline before it is
   attributed.

The CPU half of the frame (the simulation's steps, produce and its fence)
belongs to the [engine job system](open-items.md#engine-job-system).

### Global illumination

The [illumination plan](illumination.md) holds the lighting decisions and their
build order. The [Lighting and GI packet](open-items.md#lighting-and-gi)
owns the remaining coverage, visibility, exposure and bounce findings. The
[engine job system](open-items.md#engine-job-system) owns its execution budget,
and the streaming plan's
[decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand)
owns what the lighting demands and keeps. No second scheduler is built for GI.

### P13 — Hit-to-source mapping and input destinations

The shared picker and mapping are implemented. The one remaining P13b-4
observation is a real Windows editor click and focus return, listed under
[deferred checks](#deferred-to-the-end). The
[editor plan](editor.md#e2--selection-picking-and-highlight) consumes the same
picker.

### P14 — The SDF engine as a pass package

The frame-graph package is implemented. Its editor levers belong to the
[editor plan](editor.md). Crossings, Vulkan robustness and GPU
qualification belong to [cross-backend work](open-items.md#vulkan-regressions-and-gpu-robustness);
first-use JIT belongs to [boot work](open-items.md#edit-to-running-hot-reload-and-boot).
The remaining rendering-specific details follow.

#### Bake quality

The [collision-versus-mesh packet](open-items.md#collision-versus-drawn-mesh)
owns sub-cell features and contact against the drawn surface; every shape's bake detail is one rule
(`WorldShapeDetail.Of`, [mesh bake](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)).
The remaining bake investigations are below, with an engine gap first. The
[rendering plan's list](rendering-research.md#engine-gaps-filled-first) numbers
it 11: a primitive under a non-uniform scale answers a bound on distance, not a
distance, so any stretched cutter (the lake's bowl among them) frays the
surface it cuts. Its design is decided, and it goes ahead of every item after
it (the owner's ruling, [AGENTS.md](../../AGENTS.md#pucks-purpose)).

- Give every primitive under a non-uniform scale a true distance, keeping the
  authored surface
  ([the decision](../decisions/engine-design.md#a-scaled-primitive-keeps-its-authored-surface-at-a-true-distance)).
  What reads a bound today, which `puck creation shape` reports as a gain: a
  capsule, cone or torus scaled apart in X and Z, a round cone scaled apart at
  all, and an ellipsoid or sphere scaled apart, which is the exponent-2
  superellipsoid gauge. A `Path` profile and a sweep certify no gain, and are
  not part of this item. The solve, its proof, its exhibits and its build
  order are [designed below](#a-true-distance-under-a-non-uniform-scale); its
  first two steps need no new solver and unblock the lake's bowl.
- Finish an in-flight group that a later plan drops and retain it in the library
  and store. Read back a boot's second, different `start` plan so churn cannot
  silently rebake discarded work on every boot.
- Inspect the Moth's hood opening, shin knife edges, left pauldron underside,
  lower eyelid, scalp, pod bands and greave rim. Touching mesh groups expose
  seams at back-pod plates and nozzle discs; contour a touching pair together
  where that expresses the authored shape. Material cuts still leave a hairline
  at concave creases, and the arch bands' faceted outer rims are geometry.
- Inspect the gallery's anisotropic cone, ellipse prism, path fill and shear,
  revolved and flared feet, polar-fold corners and cell ridges. Resolve the
  refined contour's two-patch ambiguity and fine-column crease slivers,
  including `shape-jitter-tumble`, `shape-polygon-3` and `shape-blend-xor`.
  The Direct3D 12-only blisters on `shape-wallpaper-pmm` need a GPU/CPU tile-height
  slice; agreement of the fields still leaves the bake sampling to inspect.
- Give long thin static-union members appropriate spacing and sample capacity.
  The studio grid and hall shell expose the mismatch between a long member's
  bound and its thin section. The audit's uncovered-surface lattice must cover
  the planned group or each member, including a member dropped whole, rather
  than only the surviving mesh's box.
- Inspect sharp-edge rounding from corner-gradient interpolation: measure
  crossing gradients and surface positions, and compare the contour with its
  field. Keep the actual bake-cell error visible.
- Extend the live mesh audit to annotate clusters through the existing overlay
  writer. Key the segments no instance owns, which the offline rest scene
  cannot identify while the World emits adjacent fields and documents.
- Review the warps II row, motion exhibits and hall-light switches on the GPU.
  A switch's reach check can gain a body-yaw operand beside `bodyPosition`;
  switching a level's lights must not rebake its geometry. Vesica and Star
  remain builder-only shape types without creation primitives.

Player builds ship their mesh store as a cache set a GPU machine produces
([the player channel](../decisions/runtime-and-delivery.md#the-player-channel)).
What remains is the read-only package cache: an install should read the shipped
set as a layer ahead of the user's store, holding only the entries a package
uses, instead of reading and filling the store inside its version directory,
which duplicates the set on disk beside the install's object store. The
[derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)
owns identity and cache semantics.

##### A true distance under a non-uniform scale

This is the design for gap 11, revised after its adversarial review: what the
gap is in the code, the two exhibits and the reading each fails, the contract
a solve answers to, the solve for each primitive, why its interval holds the
distance, where it stops, the build order with a check for each step, and
where each of the review's findings is closed.

**What the gap is.** `SdfSolidGeometry.AppendScaledPrimitive` spells a scale
into a primitive's own dimensions wherever a closed form exists. A capsule,
cone or torus whose X and Z scales differ, and a round cone under any uneven
scale, have none and fall to its last arm, a `Scale` instruction over the unit
shape. That instruction evaluates `f(p / s) * min(s)`
(`SdfProgramBuilder.Scale`): zero on the authored surface, and between
`min(s) / max(s)` and one times the distance everywhere else
(`SdfProgram.InstanceDistanceGain`). The fixed-point evaluator refuses it for
contact.

A stretched sphere or ellipsoid is in the same position. It lowers to the
exponent-2 superellipsoid gauge, `(|p / r| - 1) * min(r)`, which is a bound
with the same gain: the gallery's `shape-ellipsoid-xyz` reads
`distance=bound gain=0.366`. The lake's bowl is one (`terrainBasin`, radii 46,
17 and 46 m, so a gain of 0.37). The ellipsoid is therefore the first solve
here, and the capsule and the round cone are built from it.

**The two effects, measured.** Two exhibits end the gallery's stretched row.
Each presses a capsule, a cone and a torus, scaled apart in X and Z, half way
into a slab's face. `puck creation shape` reads the three as bounds with gains
0.313, 0.25 and 0.471.

| Effect | Exhibit | Reading that fails | The same reading with exact cutters |
|---|---|---|---|
| The distances a smooth blend reads away from the surface | `shape-stretch-smooth`, cut at a 0.04 m radius | The fillet bends the slab's face out to 96 to 128 mm from the capsule's rim, 104 to 160 mm from the cone's and 40 to 85 mm from the torus's, where the author wrote 40 mm all round. `puck mesh audit --cpu` reads `field slope deficit` over the threshold on 4177 of 76778 triangles, with 10 folds and 3 flipped triangles | Deficit over the threshold on none, no fold, no flip |
| Defects in a hard cut's contour | `shape-stretch-cut` | `puck mesh audit --cpu` reads 6 folds, 1 flipped triangle and 1263 of 45817 faces flagged for turning from the field's gradient, its worst clusters all along the three rims, and the deficit over the threshold on 8358 triangles | No fold, no flip, 300 of 19476 faces flagged |

The fillet's reach is the true distance from the rim at which the cutter's
field reads the blend radius, computed from the kernel's formulas and a
brute-force closest point in double precision. The control column is the same
exhibit with each cutter's X and Z made equal, so every cutter takes an exact
lowering: its rims are other curves, so it shows what an exact cutter leaves,
not what these cutters will read. In both, 99 in 100 of the hard cut's
vertices lie within half a millimetre of the field's surface, which is why
the two effects are apart: a bound keeps a hard cut's surface and frays only
its rim, and moves a smooth cut's surface itself.

**What exists.** One closest-point solve serves both interpreters: the root
specified below, `FixedEllipsoidProjection` in fixed point (a side, an
interval and a scalar read from it) and `sdfEllipse2D` in
`field/sdf-ellipse.hlsli` in float, with the ellipse as its two-term caller.
`EllipseDistanceLawTests` and `EllipsoidProjectionLawTests` hold it to
double-precision references for two and three terms. It is what makes an
elliptic cylinder, a torus stretched in Y alone, a revolved ellipse and a
spheroid exact, the spheroid's contact lowering included, and
`SdfProgramBuilder.Ellipse` keeps both radii as authored. The fixed-point
root solves lengths up to 2^31 raws (32,768 m) directly and a larger shape
or point scaled down by a power of two, its interval widened by that move.

The sweep searches its curve by samples and golden-section rounds with no
bound on what it missed. The reach certificates behind the cull tables
(`SdfFieldEvaluator`) admit a `Scale` only when it is uniform. The bake
searches each crossing along its lattice edge and reads a value as a distance
only over the gradient's length (`SdfSampledLattice.PassesBetween`), which is
why a hard cut survives a bound as well as it does. Nothing projects onto a
triaxial ellipsoid, a cone or a torus.

**Options weighed.**

| Option | What it gives | Why it is or is not taken |
|---|---|---|
| Reduce each primitive to bracketed roots of one variable, each answering an interval | The distance on both sides, with an interval that holds whatever the search did | Taken. The ellipsoid's reduction is published; the cone's and the torus's are derived below and checked against brute force and the review's counterexamples |
| Newton on the surface's two parameters, with a bound afterwards | A nearby surface point quickly | The point is only an upper bound. Outside a convex shape a lower bound exists (claim 1), inside none does, so it cannot answer a cutter's inside |
| A union of exact pieces (an elliptic cylinder and two ellipsoids for the capsule) | The distance outside | Inside a union the field is the depth in one piece, which is a bound again, and a cutter is read inside |
| Divide the bound by its gradient's length | The distance to first order at the surface | It has no bound away from the surface, where a blend reads it. The bake already does this where it helps |
| Resample the shape into a sampled region | A distance as fine as its cell | Memory for each stretched shape, and exact nowhere |

Refusing such scales or changing the surface is ruled out by
[the decision](../decisions/engine-design.md#a-scaled-primitive-keeps-its-authored-surface-at-a-true-distance).

**The contract.** A solve answers three things about the point it was asked,
and consumers rely on these, not on a scalar being the distance.

- *A side.* Outside, inside or on the surface, from the shape's own implicit
  test at the query point (for an ellipsoid, `sum((p_i / e_i)^2)` against
  one). The root search never decides it. In fixed point the test is rounded
  both ways, and a test rounding cannot decide answers "on the surface".
- *An interval.* `lower <= |distance| <= upper`. The upper end is always the
  distance to a point the solve put on the surface. The lower end comes from
  the bracket (below) and is zero when the side is undecided. With several
  candidates of intervals `[L_i, U_i]` the interval is
  `[min L_i, min U_i]`. The interval is valid at every stop, including a
  stop that made no progress.
- *A scalar, with its error.* `side * upper`, and `upper - lower` as its
  error: it reads no nearer than the surface is and no farther than the
  error. It is zero only where the side is "on the surface". Two points `a`
  and `b` read scalars no more than `|a - b|` plus both errors apart. The
  scalar is not claimed 1-Lipschitz, and no proof may use it as if it were.

Who reads what:

| Consumer | Reads | Stops |
|---|---|---|
| The box rule (`TryDistanceBounds`), the certified sweep and line of sight, the cull's reach | The signed interval at the box's centre, widened by the box's half diagonal. Never the scalar | As soon as its own decision is made |
| The point walk feeding blends, offsets and the audit; the bake's kernel | The scalar. The fixed-point walk also carries the error, and the kernel does not | When the search can go no further in its format |
| `SdfProgram`'s Lipschitz and gain analyses | Nothing from the solve: the shape is charged a gain of one, and the evaluator's box rule adds the scalar's error where it bounds point answers | |

The kernel claims no certificate. It runs the same search in float to the
format's resolution, takes no early stop, and answers the `Scale` bound's
value when its arithmetic is not finite. Its agreement with the fixed-point
interval is measured by a law, which reads an error at its points and proves
nothing between them.

**The root.** For semi-axes `e` and a point with no negative coordinate, let
`m` be a least axis, `gap_i = (e_i - e_m) * (e_i + e_m)`, and
`w_i(u) = e_i * p_i / (u + gap_i)`. `F(u) = sum(w_i^2) - 1` falls strictly
for `u > 0`, the foot at `u` is `q_i = e_i * w_i`, and each `q_i` falls as
`u` rises. `u` is the multiplier measured from the least axis's pole, so no
step subtracts two like quantities.

- *Scaling.* In float every length is first multiplied by one power of two
  that puts the largest of `e` and `p` below two; fixed point keeps its
  raws. In both, the sign of `F` is read without
  squaring anything above one: a `w_i` at or above one makes it positive,
  and otherwise every square is below one.
- *The bracket.* Its lower end is `e_m * p_m`, the largest over tied least
  axes, where that `w` is one. Its upper end is `|e * p|` outside and
  `e_m^2` inside. It is halved geometrically while its ends differ by more
  than two to one and arithmetically after, so the count of halvings is
  bounded by the format's mantissa plus a few steps for its exponent range.
- *The pole.* When every least axis's coordinate is zero the lower end is
  `u = 0`. If the other terms' squares there sum to one or less, the root
  is the pole: the other coordinates come from `q_i` at `u = 0`, and
  `e_m * sqrt(1 - sum)` goes to one least axis. That is zero at equality
  (the evolute's cusp) and `e_m` at the centre. Otherwise the search starts
  from zero, where `F` is positive.
- *Certified signs.* The fixed-point search rounds each `w_i` down and up
  and takes a sign only when both agree. A sign it cannot take ends the
  search.
- *The interval from a bracket.* The true foot lies in the box between the
  feet at the bracket's two ends. `lower` is the distance from `p` to that
  box. `upper` is the distance to the foot at the bracket's middle after it
  is scaled back onto the surface, plus what rounding can leave that point
  off the surface. Outside, `lower` is also at least
  `n . (p - q)` for that surface point `q` and its outward normal `n`
  (claim 1).
- *The carrier.* At the root a foot coordinate moves at most `1 / p_i` per
  unit of `u`, and a nonzero coordinate is at least one raw, so `u` needs
  34 fractional bits to hold the foot to a quarter raw. The fixed-point
  search therefore runs `u` with 48 fractional bits in 128-bit integers and
  each ratio `w_i` with 62, from two exact 31-bit quotient steps, for lengths
  below 2^31 raws; a larger shape or point is solved scaled down by a power of
  two and its interval widened by the move (claim 9, and radii moved by at
  most `d` move the surface by at most `d`). In 16 the review's point `(0.5, 2^-16, 0)` of the
  ellipsoid `(2, 1, 3)` stalls at `[0.527, 1.014]`; in 48 it closes on
  `0.957412082`.
- *A faster step.* A Newton or secant step may replace a halving only in a
  bracket that holds one root with both end signs taken, and only when it
  lands inside. Two proposals in a row that do not halve the bracket are
  followed by a halving.
- *The stop.* The search ends when the bracket has no middle in its format,
  when a sign cannot be taken, or when a bound consumer has its answer. It
  returns the interval it holds. An interval wider than a consumer can use
  is still the answer: the `Scale` bound's own interval, `[g, g / gain]`
  for its value `g`, is intersected with it, so no stop is worse than
  today's field.

The exact lowering is taken for scales whose axis ratio the root's law covers
and whose interval the law finds no wider than a raw; a shape past that keeps
its `Scale` and `puck creation shape` keeps printing `bound`. The ratio starts
from 1024 to 1. The spheroid's ellipse takes no ratio limit: its interval
holds the distance at every stop, so an extreme ratio can widen it but never
make it wrong (the law reads it at 100 to 1).

**The solves.** Every shape here is symmetric in X and in Z, so a point is
read by its absolute X and Z, and by its absolute Y where the shape is
symmetric in Y. `s = (sx, sy, sz)` is the scale and `p` the point in the
shape's own frame. A coordinate that is exactly zero takes the branch written
for it; nothing is moved toward zero to reach one.

*The ellipsoid.* The root with three terms. Two equal radii are the ellipse
revolved at offset zero, which is the root with two terms.

*The capsule.* The scaled capsule is the segment from the origin to
`(0, h, 0)`, `h = sy / 2`, swept by the ellipsoid of radii `s`. Its surface
is the lower half of that ellipsoid, an elliptic cylinder between the planes
`y = 0` and `y = h`, and the upper half of the ellipsoid about `(0, h, 0)`.

- `p.y <= 0`: the ellipsoid about the origin, on both sides. `p.y >= h`: the
  ellipsoid about `(0, h, 0)`.
- Between the planes and outside: the ellipse `(sx, sz)` at `(p.x, p.z)`.
- Between the planes and inside: the least of the ellipse's depth and, only
  when `sy` is under both `sx` and `sz`, the far foot on each cap: the
  nearest point of a cap from a point on the other side of its equator. With
  `y'` the height over that cap's centre and `v = -(t + sy^2)`, the cap's
  feet are the roots of
  `F(v) = (sy * y' / v)^2 + (sx * p.x / (sx^2 - sy^2 - v))^2 + (sz * p.z / (sz^2 - sy^2 - v))^2 - 1`
  over `0 < v < V`, `V = min(sx, sz)^2 - sy^2`. `F` is convex there and
  unbounded at `v = 0`. At `V` it is unbounded when the coordinate of that
  pole's axis is not zero, and finite otherwise.
  - A finite value below zero at `V`: one root, bracketed directly, which
    is the far foot. On the axis this is the cap's pole, at `y' + sy`.
  - Otherwise two roots or none. The search halves on the sign of `F'`
    until it meets a `v` where `F` is negative, then halves on `F` between
    zero and that `v`. The root nearer zero is the far foot.
  - A search that ends without a negative `F` and without excluding one
    adds the cap as a candidate that lowers `lower` only: its distance is
    bounded below by the distance from `p` to the box of feet over the
    bracket it ended on.

*The cone.* The scaled cone has its apex at `(0, sy, 0)` and its base, the
ellipse `(sx, sz)`, in the plane `y = -sy`. With `Y = p.y - sy` and
`b = 1 / (4 * sy^2)`, its side is the nappe
`x^2 / sx^2 + z^2 / sz^2 = b * Y^2`, `Y <= 0`. A foot on the nappe is
`(sx^2 * p.x / (sx^2 + k), Y / (1 - k * b), sz^2 * p.z / (sz^2 + k))`, where
`k` is a root of
`G(k) = (sx * p.x / (sx^2 + k))^2 + (sz * p.z / (sz^2 + k))^2 - b * Y^2 / (1 - k * b)^2`.
The foot's normal points out from the solid nappe exactly when `k >= 0`.

- `Y < 0`: `G` falls strictly from the pole `-min(sx, sz)^2` to the pole
  `1 / b`, and has one root between. When the coordinate of the lesser
  horizontal axis is zero and the rest of `G` is zero or less at that
  pole, the root is the pole and that coordinate comes from the nappe's
  equation.
- `Y = 0`: if `p.x` and `p.z` are zero the point is the apex. Otherwise the
  root is the pole `k = 1 / b`: the horizontal coordinates are
  `p.x * b * sx^2 / (b * sx^2 + 1)` and its twin in Z, and the height comes
  from the nappe's equation.
- `Y > 0`: if `sx^2 * p.x^2 + sz^2 * p.z^2 <= Y^2 / b` the nearest point
  of the nappe is the apex. Otherwise, with `u = 1 / k`,
  `H(u) = (sx * p.x / (1 + u * sx^2))^2 + (sz * p.z / (1 + u * sz^2))^2 - b * Y^2 / (u - b)^2`
  falls strictly over `(0, b)` and has one root.

The cone's answer, by where the point is:

- Inside the cone (inside the nappe and at or above the base): the lesser
  of the height over the base, `Y + 2 * sy`, and the distance to the
  nappe's foot.
- Inside the solid nappe and below the base: the base's answer. That is
  `-2 * sy - Y` straight down when the point is under the base and
  `(p.x, p.z)` lies in its ellipse, and otherwise the rim: the root of the
  squares of `Y + 2 * sy` and the ellipse's distance at `(p.x, p.z)`.
- Outside the solid nappe: the nappe's foot when it is at or above the
  base, or the apex. Otherwise the base's answer, by the same rule.

*The round cone.* Its surface is the ellipsoid of radii `s` below the plane
`y = sy / 2`, the ellipsoid of radii `s / 2` about `(0, sy, 0)` above
`y = 5 * sy / 4`, and between them a nappe with its apex at `(0, 2 * sy, 0)`
and `b = 1 / (3 * sy^2)`. Outside, the nappe's foot is the answer when the
point is outside the solid nappe and the foot lies between the planes.
Otherwise the answer is the nearer of the two ellipsoids' nearest points
among those that lie on their own caps. Its inside is not designed here:
step 7 owns it.

*The torus.* The unit torus has a major radius of one and a tube of ratio
`r = 0.4`. Each half of the tube, outer and inner, is measured from its own
equator by `t` in `[0, 1]`, the tangent of half the angle from that equator:
`sin a = 2 * t / (1 + t^2)`, and `cos a = (1 - t^2) / (1 + t^2)` on the outer
half and its negative on the inner. The top is `t = 1` on both. A latitude is
the ellipse `(1 + r * cos a) * (sx, sz)` at the height `sy * r * sin a`, and
`D(t)` is the distance from `p` to it: the root of the squares of the
height's difference and the ellipse's distance at `(p.x, p.z)`. `D` is a
distance to the surface at every `t`, and it changes by at most
`2 * r * max(s)` per unit of `t`.

For `p.y > 0` let `m = sy * (p.y / sin a - sy * r)`,
`A = sx * (1 + r * cos a) + m * cos a / sx`, and `B` the same with `sz`. A
foot at that latitude has an azimuth `f` with `p.x = A * cos f` and
`p.z = B * sin f`, so its `t` is a root of
`G = (p.x / A)^2 + (p.z / B)^2 - 1` where `A` and `B` are positive, or a
zero of `A` or `B` where that coordinate of `p` is zero. `m` is the foot's
signed reach along its normal: zero or more outside, below zero inside.

- *Outside, `p.y > 0`.* Feet have `m >= 0`, which is
  `sin a <= p.y / (sy * r)`: every `t` when `p.y >= sy * r`, and otherwise
  `t` up to `t_m` on each half. The search runs on the outer half when
  `(p.x / sx)^2 + (p.z / sz)^2 >= 1` and on the inner half when not. On the
  outer half `A` and `B` are positive and `G` rises with `t` from below
  zero at the equator to zero or more at the half's end. On the inner half
  `G` is zero or less at the half's end and rises as `t` falls, and any
  `t` at which `A` or `B` is not positive counts as past the root,
  whatever the point's coordinates. One root or one such edge lies between.
  The answer is `D` over the final bracket: `[D - 2 * r * max(s) * w, D]`
  for `D` at its middle and its width `w`.
- *Outside, `p.y = 0`.* `D` at the outer equator when
  `(p.x / sx)^2 + (p.z / sz)^2 >= 1`, and at the inner equator when not.
- *Inside.* Feet have `m < 0`: `t` from `t_m` to one on each half, with
  `m = -sy^2 * r` and `t_m = 0` when `p.y` is zero. Each half is cut where
  the derivative of `A` or of `B` changes sign, which is at
  `sin a = cbrt(sy * p.y / (r * (sy^2 - sx^2)))` for `A` when `sy > sx` and
  that is below one, and likewise for `B`. On a piece `A` and `B` are each
  monotone, so their end values bound them, padded for the cut's own
  rounding. A piece on which `A` or `B` is nowhere positive holds no foot.
  Otherwise `(p.x / A)^2` lies between its value at the larger end of `A`
  and its value at the smaller, which is unbounded when the smaller end is
  not positive, and likewise for `B`. A piece whose bounds keep `G` off
  zero holds no foot. Any other piece is halved, widest first, until it is
  as narrow as its consumer needs, until its whole range of `G` is inside
  the rounding of its bounds, or until the budget of pieces is spent. Each
  piece left answers `[D - 2 * r * max(s) * w, D]`, and one whose range
  could not be decided is cut into equal parts for its `D` alone. When
  `p.y` is zero, `D` at both equators is a candidate too. The depth's
  interval is the least lower end and the least upper end over all of
  them.
- *Inside, `sy` no more than `sx` and `sz`.* `A` and `B` are positive and
  rise toward the outer equator over the whole arc, so `G` has one root
  and a plain bracket finds it, unless `A` and `B` are both constant
  (`p.y = 0`, with `p` on the one horizontal axis whose scale equals `sy`).
  Then `G` is constant; when it is zero every latitude is a foot at one
  distance, which `D` at any `t` reads.

**Why the interval holds the distance.** Each claim is used where a solve
names a region, drops a candidate or bounds a piece.

1. *A convex shape, outside.* For a convex solid `K`, a point `p` outside
   it, any point `q` of its surface and the outward unit normal `n` of a
   plane that supports `K` at `q`, `n . (p - q) <= distance <= |p - q|`.
   `q` is the nearest point exactly when `p - q` is `n` times a multiplier
   that is zero or more. A foot whose normal points the other way is a
   point of the surface and no more: for the nappe that is `k < 0`, a point
   inside the solid nappe. The lower end converges with the foot only at a
   smooth foot; at the cone's rim and apex the answers are closed forms and
   take no support plane. The capsule, the cone, the round cone and the
   ellipsoid are convex. So is the torus's hull, whose flat faces are
   ellipses' disks, so outside it with
   `(p.x / sx)^2 + (p.z / sz)^2 >= 1` the hull's nearest point is on the
   torus's outer half.
2. *An intersection, inside.* A ball about `p` lies in an intersection of
   sets when it lies in each, so the depth in the intersection is the least
   of the depths. The cone is its base's half-space cut by the solid nappe.
3. *The octant.* A shape symmetric in a coordinate has a nearest point whose
   coordinate has the point's sign or is zero: reflecting a point of the
   surface to that side brings it no farther. A foot on the wrong side of a
   coordinate the point is strictly off is therefore never the nearest,
   which is why a negative `A` or `B` is inadmissible. Where the point's
   coordinate is zero, its nearest latitude is a limit of such feet (the
   distance is continuous in the point), so it has `A` and `B` zero or
   more.
4. *One root, where the search looks.* Each term of `F` falls strictly in
   `u`, so `F` has one root over `u > 0`, and the pole branch covers the
   case where it has none. `G` and `H` fall strictly over their intervals
   by the same argument on their terms. Eberly's "Distance from a Point to
   an Ellipse, an Ellipsoid, or a Hyperellipsoid" gives the ellipsoid's
   root, bracket and zero branches, and Hart's "Distance to an Ellipsoid"
   (Graphics Gems IV) the same equation; neither was fetched for this
   design, and the engine's ellipse follows the first. The nappe's `G` and
   `H` and their poles are derived here.
5. *The capsule's regions.* Outside and under `y = 0`, the nearest point
   cannot lie on the cylinder, whose normals are level, nor on the upper
   cap, whose normals rise, so it is on the lower cap, where it is the
   ellipsoid's own nearest point by claim 3. Inside and under `y = 0`, a
   segment to the cylinder or to the upper cap leaves the ellipsoid first,
   and the ellipsoid's nearest point is on the lower cap. Between the planes
   and outside, the caps' normals point away, so the foot is on the
   cylinder at the point's own height.
6. *The far foot.* Between the planes and inside, the nearest point of a cap
   is on its equator, which the cylinder's depth already covers, or at a
   local minimum on its open half. Those are roots of `F` over `0 < v < V`,
   an interval that is empty unless `sy` is under `sx` and `sz`. `F` is
   convex there. With one root, it rises through it in `t` and is the
   minimum. With two, a root is a local minimum exactly where `F` rises in
   `t` (the bordered Hessian's determinant is the product of the three
   denominators' signs times the derivative), which is the root nearer
   `v = 0`; from the other, a saddle, the distance falls to the equator.
   Only that two-root case uses the saddle, and a search that cannot tell
   it from none uses the candidate's lower bound instead.
7. *The torus, outside.* The normal at a surface point is the unit torus's
   normal through the inverse scale, so a foot's point is
   `S u + m * S^-1 v` for the unit point `u` and its unit normal `v`, which
   gives `m`, `A` and `B`. The derivative of `A` in `c = cos a` is
   `r * (sx^2 - sy^2) / sx + sy * p.y / (sx * (1 - c^2)^1.5)`. Where `m`
   is not negative its second term is at least `sy^2 * r / sx`, so the
   derivative is at least `r * sx`, and `B` likewise. `1 / A^2` falls as
   `A` rises only while `A` is positive, so `G` falls strictly in `c` on
   the part of the feasible arc where `A` and `B` are positive, which is an
   interval running from the outer end, and claim 3 leaves no foot beyond
   it. The squared equation has roots beyond it, which are not feet in the
   point's octant.
8. *The torus, inside.* Every foot whose coordinates keep the point's signs
   is at a latitude in a piece the enumeration keeps: the derivative above
   is even in `c` and rises with its size, so it changes sign at most once
   on each half, the pieces are monotone, and bounds that become unbounded
   across a zero of `A` or `B` discard nothing that holds a root or such a
   zero. The nearest point's latitude is therefore in some piece left, and
   over a piece of width `w` the distance to any latitude in it is within
   `2 * r * max(s) * w` of `D` at its middle. Nothing here counts the
   feet: five occur, the nearest need not be first or last, and a foot may
   touch zero without crossing it.
9. *A nearby point.* The distance is 1-Lipschitz in the point. An
   implementation that evaluates at `p'` in place of `p` widens its
   interval by `|p' - p|`, the Euclidean length of the whole move, and
   keeps the side of `p`. This is not used to reach a zero branch.

**The prototypes.** Two double-precision prototypes ran against a brute-force
closest point (a surface grid, then a zoom about its best samples). Both were
scratch and are not in the tree: the laws of steps 2 to 6 replace them.

- The first ran the first design over 2,400 points and agreed everywhere,
  and the review then broke it with points it had not drawn: on axes and
  planes, under the cone's base, at an equator, and at the formats' edges.
  Random points in general position did not test the regions, the poles or
  the arithmetic.
- The second runs the solves above. Each of the review's counterexamples
  reads its expected value, to the digits the review gives: the cone
  under its base `0.25`, at its apex's height `0.707106781` and beside its
  apex `0.00135520413`; the round cone under its bottom `0.125`; the
  capsule on its axis `-1.125`; the torus `0.945362405` outside,
  `-0.165194546` across a zero of `A`, `-0.0697433254` with five feet,
  `-0.2` on top within `[0.199999998, 0.2]`, `-0.4` at both midplane
  points, `-0.0741877347` at the foot that touches zero, and `-4915.2` and
  `-0.15` beside the inner equator; the ellipsoid `-0.5` at the cusp,
  `-1` at the centre and zero at the near-circle's authored pole. Over
  1,200 further random points no solve read farther than the brute force
  by more than `1e-15`, no side differed and no lower end passed the
  distance.
- Emulations of the two formats ran the root: the fixed-point search with
  certified signs in 16 and in 48 fractional bits (above), and the float
  search at radii `2^20` and `2^-13`, where the bracket as built answers
  `2097152` with its foot at the origin and the scaled root answers
  `1048576.1`.

**Steps, each with its check.** The invariant is that a primitive
`SdfSolidGeometry.AppendScaledPrimitive` lowers to an exact shape answers,
in both interpreters, a side from its own implicit test and an interval that
holds the distance to its authored surface. Two things enforce it. That
method is the one door a scaled primitive is lowered through
(`ShapeEmission.Emit` is its one caller for creations), and after step 8 no
arm of it reaches a `Scale` with unequal components inside the admitted
ratios. And the fixed-point solve returns the interval as its only result
type, so a consumer cannot read a scalar without its error. The guarantee
stops at the kernel, whose float answer has a measured error and no proof;
at what is not a scaled primitive (a superellipsoid past exponent 2 stays a
gauge, warps, flares and field ops stay bounds, a sweep and a path certify
no gain); at scales past the admitted ratios; and at a C# caller that writes
`SdfProgramBuilder.Scale` itself.

1. The record is corrected, and the exhibits and this design exist.
2. Built: a spheroid lowers to the ellipse revolved at offset zero, whose
   radii the builder keeps as authored. Owed: the lead's window reads the
   valley sector's bake work before and after, since the bowl's samples now
   each run a root.
3. Built: one root for both interpreters (`FixedEllipsoidProjection`,
   `sdfEllipse2D`), the ellipse its two-term caller and the spheroid's
   contact lowering on it; the box rule reads its interval. Owed: a device
   law reading the float kernel against the fixed-point interval, at the
   review's float radii (`2^20` and `2^-13` m) among its points.
4. The triaxial ellipsoid takes the root, and the gauge is left to exponents
   past 2. Check: `shape-sphere-xyz` and `shape-ellipsoid-xyz` read
   `distance=exact`, and the two interpreters agree over a lattice within
   the law's error.
5. The capsule. Check: `shape-capsule-xz` reads `distance=exact` and
   `contact=admitted`, and the law covers capsules with `sy` under `sx`
   and `sz`, on and off their axis.
6. The cone, then the torus, each with the same law and the same two reads.
   The cone's law covers points under the base, at the apex's height and
   on its planes. The torus's covers `sy` above `sx` or `sz`, `p.y` at
   zero and one raw from it, points on its planes, and the review's
   five-foot and touching-foot points.
7. The round cone. Its first task is the inside: the capsule's claims 5 and
   6 with each cap ending at its tangent plane instead of its equator,
   proved and run against brute force and against points on its axis.
   Until then it keeps its bound and `puck creation shape` says so.
8. `AppendScaledPrimitive` loses its `Scale` arm for unequal components
   inside the admitted ratios, and `SdfSolidGeometry.DilationGain` its
   stretched case. Check: `puck creation shape` over the stretched row
   prints `bound` for `shape-superellipsoid-xyz` alone.
9. The exhibits and the cost. `puck mesh audit --cpu` reads no triangle over
   the slope deficit's threshold, no fold and no flip on
   `shape-stretch-cut` and `shape-stretch-smooth`, and the smooth cut's
   fillet reaches 40 mm from each rim. Then, in the lead's window: the same
   audit over the stored bakes on both backends, captures of both exhibits
   and of the lake's bowl, and the field work on the bake's `done` line
   before and after. That work decides whether a program of hard unions
   alone may keep a bound, which would need its own proof that nothing in
   it reads a distance.

Each new shape is an instruction-set change with every partner in one change
([the checklist](../../.claude/skills/rendering/SKILL.md#changing-the-instruction-set)):
the kernel, the fixed-point evaluator with its bounds and reach rules, the
shading cost row beside the ellipse's (`SdfShadeCost`), and the contact
lowering, which reads the same shape.

**What the review found, and where each is closed.**

| Finding | Closed by |
|---|---|
| 1. The cone returned its side for a point under its base | The cone's answer is by region, and the nappe's foot is taken only outside the solid nappe (claim 1's multiplier) |
| 2. No root at the apex's height | The `Y = 0` branch at the pole `k = 1 / b`, and the apex itself |
| 3. The outside torus equation has roots that are not feet | The search is clipped to positive `A` and `B` (claims 3 and 7) |
| 4. A piece lost a foot across a zero of a denominator | Bounds are unbounded across a zero, and a piece with no positive part is the only one dropped for sign |
| 5. Five feet inside a torus | No count is assumed; the pieces' budget and intervals stand in for one, and five is a law case |
| 6. A box of end feet missed the distance | Pieces no longer span the top, and a piece answers `D` at its middle less its width's reach, not a box of feet |
| 7. The round cone took an inward foot | The same multiplier rule, and the caps compared by which holds its own foot |
| 8. A far foot with one root and no saddle | The end's one-sided value is read first; the saddle argument is kept for two roots |
| 9. A lower end returned early moved the zero set and broke the Lipschitz bound | The contract: the side from the point, the interval to bound consumers, a scalar with its error and no Lipschitz claim, no early stop for scalar consumers |
| 10. Q48.16 runs out of multiplier values | The pole-relative parameter in 128 bits with 48 fractional, signs certified, and a stop that returns the interval it holds |
| 11. The float bracket overflows | Power-of-two scaling, no squared ratio, the term-above-one sign rule, geometric halving, and the `Scale` bound's value when not finite |
| 12. Snapping coordinates | Zero branches are taken only at zero; a moved point widens the interval by the whole move and keeps its side (claim 9) |
| 13. The torus's midplane was incomplete | Inside at `p.y = 0` the open arc is searched with the constant `m`, both equators are candidates, and the constant family is named |
| 14. The support interval does not converge at a rim | Claim 1 limits it to smooth feet; rim and apex are closed forms |
| 15. A foot that touches zero has no sign change | Pieces are kept by range, not by end signs, and an undecided piece ends at its width; a faster step needs a one-root bracket |
| 16. An angle from the outer equator loses the inner foot | Each half is measured from its own equator by the half-angle tangent |
| 17. A secant step can stall | Two proposals that do not halve the bracket are followed by a halving |
| 18. The pole rule missed equality and ties | The pole branch takes "one or less", allots the rest once among tied least axes, and reads the centre |
| 19. The spheroid lowering moved the authored surface | Step 2 removes the builder's move and checks the authored poles read zero |

**What a second review should attack.**

- The contract's table: whether any consumer of a point answer in
  `SdfFieldEvaluator` or the bake treats it as 1-Lipschitz in a way the
  scalar's error breaks.
- The root's carrier: the `1 / p_i` bound away from the root, and the count
  of fractional bits under the admitted ratios.
- Claim 8's cuts in the tangent parameter and their padding, and the bound
  `2 * r * max(s)` on `D`.
- The cone's regions at their seams: the nappe at the base's rim, and a
  point on the nappe below the base.
- The round cone's rule for choosing a cap, which is argued from convexity
  and checked at one point.
- The far foot's candidate lower bound when its search stalls.

#### Glass

The [glass handbook](../rendering/sdf/handbook/frame-rendering.md#glass-after-the-opaque-image)
owns the implemented path. Remaining slices are:

- The owner's paired-backend look at `shape-glass`: depth and thickness,
  frost blur, refraction and the `glass`/`glass-shade` work readings.
- More than one glass layer per pixel when a scene stacks panes.
- Compose refracted, tinted sky within the glass path.
- Glass shadows, occlusion and coloured transmission/bounce into irradiance.

#### GPU submission and routed worlds

The [engine job system](open-items.md#engine-job-system) owns frame scheduling
and recording. The remaining GPU and routed-world investigations are:

Every raster pass selects its draws on the GPU through `SdfMeshOcclusion`
([contract](../../.claude/skills/rendering/SKILL.md#far-cascade-caster-selection)).
Shared geometry instancing is a separate extension the
[visibility plan](visibility.md#the-build-order) owns (ordered compaction and
instance buckets): the meadow draws many copies of few geometries, and sparse
visibility, per-draw level choice and stable depth-tie ordering need an
instance map and per-geometry, per-level ranges, never `instanceCount` alone.

- Reopen either unattributed Vulkan device-loss case on recurrence: the
  first-frame fault whose last checkpoint is `glass-clear`, or the fault as
  Nsight GPU Trace begins whose checkpoint is root `place`. Repeated successful
  boots do not establish a cause; retain fresh fault and validation logs. A
  Direct3D 12 device removal on the integration branch is under diagnosis in
  a lane.
- Measure geometry instancing in the indirect records rather than repeating
  the slower one-draw vertex-search experiment.
- Measure front-to-back order or a depth prepass against mesh overdraw,
  retained descriptor writes for Vulkan pane sources, batching graph command
  lists into fewer submissions and queue idle time.
- Keep graph targets across a crossing where extents and ownership allow it,
  then re-measure Vulkan's first submissions after reconfiguration. The shared
  GPU memory arenas already exist.
- Complete clipped straddling for catalog avatars, prototype mesh draws and
  cross-world bodies using the existing portal carry and clip mechanisms.
- Make a presented world's probes world-scoped: ring and camera-export keys,
  per-socket refusal without a constructor exception, local-device sockets
  refused by name, and lifetime following the presented world. Remote screen
  sessions, machine frames and text also need their transport and font origin.
- A package pass takes its extent from its first image port. Resolve the
  output-extent contract for a pass whose only image is a fixed-size atlas.
- Account a graph's storage by lifetime before reusing any of it. The plan
  forbids unrelated storage aliasing today, and no allocator proves a handoff.
  First, from the plan's first and last uses and the actual dimensions and
  formats, compute the unaliased bytes and a conservative estimate of what
  compatible, non-overlapping transients could share, by lifetime class
  (external, published, history or retained, frame-slot, transient, retired,
  background), with flight-slot multiplicity and graph-replacement overlap
  counted and no reference summed twice
  ([how memory is read](streaming-and-memory.md#what-adds-and-what-only-names)).
  Only if that estimate is large: assign physical allocations to eligible
  transient chains in the compiler's immutable plan, within one node and queue
  first. The invariant is that reuse is legal only after every GPU access to
  the old allocation and every external lease has ended, which the compiler
  can prove locally and the allocator's retirement door must enforce; history,
  held portal images, capture inputs, standing pass contents and background
  bindings are never eligible. Check: planned against actual peak allocation
  across resize, graph replacement, held views, capture and background work on
  both backends, kept only for a measured byte dividend.

The back of a portal's glass still needs a presentation choice: no image,
fallback colour or a front-image mirror. The
[owner decisions](open-items.md#owner-decisions) hold that choice.

### Colour conversion — scene light to display values once

Each view a display shows converts its scene light once, in its own display
pass, and authored hex colours are display colours decoded where they enter
shading ([decision](../decisions/rendering.md#scene-light-is-converted-to-display-values-once-at-the-end-of-each-views-graph)).
The tone curve is authored (`render.tone`, `worlds/standard/tones.puck`),
displays decode their pictures in the screen shading and the glow reduction, and
the light range threshold stays a share of linear exposed light, so lamp ranges
are unchanged. The gallery's STYLES row holds the grey card (`shape-grey-card`),
and a presented capture reports each frame's mean display level over its region
(`PresentedFrameLevel`). The debug world keys its picture by day with
`render.exposure.compensation` (the Moth basis and the hall), a look value: the
image meter alone lands every picture's log-average near sRGB 90. What remains:

- **The image meter keys every scene to one grey.** An automatic exposure meters
  the luminance of the view's own image (`SdfExposure.Meter`) and aims the
  log-average at `SdfPhotometry.MeterCalibration` over the saturation headroom,
  about 0.104 of display white. The glacier's captures from the valley rim read a
  log-average of 0.106 to 0.114 display-linear at every day value tried, which is
  that aim and nothing else: reflected metering makes a frame of snow, a white
  room or a black-sand beach the same grey, and an authored compensation only
  shifts the grey. The Puck-native answer is an incident meter: the views stage
  already computes each surface's arriving light for the local adaptation
  (`sdfLocalAdaptationGain`'s `incoming`), so the histogram can bin the luminance
  a reference card would show under that light, and the sky its own luminance,
  making the exposure follow the light while each surface keeps its albedo.
  It needs the arriving light carried from views to the composite (a lit-image
  channel or a views-side histogram across the three views kernels and glass),
  the local adaptation's reference re-derived from the same reading, and an
  authored choice between the two meters with image metering as the neutral
  default. Check: a view filled with the glacier's snow and one filled with the
  wall's rock under the same sun settle on one EV within a tenth of a stop, and the
  snow reads above sRGB 230 in both while the rock keeps its own value.

- **A join of two image kinds does not compose.** The kind a view publishes is
  decided once (`SdfWorldPasses` `Entry.Converts`) and the kind a screen reads
  once (`SdfScreenFace`), from matching predicates, but nothing refuses a graph
  or a screen binding that joins a window's light to a working-image reader or
  the reverse. Declare the kind on a fragment's output and on each consumer's
  input (`ImageColorEncoding` gains a nits scale and the exposure applied), and
  have the runtime refuse a mismatched join by name. Check: a document whose
  display screen films a `Window`-projection session is refused.
- **The window's exposure share is an engine constant.** Move
  `SdfPhotometry.WindowAdaptation` into authored data the window image's
  declaration carries, with the standard package authoring three quarters.
- **A sky layer reading an image source.** The `disc` and `panorama` sky kinds
  read their screen source as linear light, which an infinity view publishes; an
  authored image source there is a working image and needs its decode, chosen by
  the source's declared kind.
- **Display-referred constants in shading.** The editor grid, an unbound
  screen's glass and the curvature ink are display values added in exposed light
  (`passes/sdf-light-stage.hlsli`); since the encode follows, they read brighter
  than authored. Decode each where it is defined.
- **The display-format readback.** A presented capture reads the root's working
  image through the SDR encode, so its `level` is what an SDR display is sent;
  reading the swapchain in its own format (scRGB, HDR10) waits for an HDR display
  (P16).
- **The checks across every view path.** With `render.tone` removed and the
  exposure pinned, the 18% patch reads 118 within 3 codes, and one stop darker it
  reads what the 9% patch read (the conversion, not a single point, is right), in
  a single view, a split view, a filmed view on a screen, a view beside a pane, a
  fully framed screen, a sharpened view with bloom, and a window from a dim room
  onto a bright world. One swatch per scene-colour kind (surface, sky, fog,
  light, keyed colour) reads right while letterbox and HUD colours do not move;
  a bright edge in motion shows no new shimmer; every route chapter meets its
  reference in motion and the gallery's exhibits read as their plaques say.

### P15 — Temporal reconstruction

Reconstruction and dynamic resolution are implemented. P15-8's floor-tier
tuning is removed; the Steam Deck observation below runs at `high`.
The [temporal packet](open-items.md#temporal-shimmer-and-cadence-flicker) owns
remaining image work. Dynamic resolution follows GPU frame timing, with present
timing as its fallback; without timing or a display period it holds its grid
([frame handbook](../rendering/sdf/handbook/frame-rendering.md)).

The [state-of-the-art audit](state-of-the-art/audit.md#10-temporal-reconstruction-details)
names four open improvements, each a step of this package with its own check
and each judged on presented frames in motion:

- **The jitter period scales with the upscale ratio.** It is fixed at eight
  samples (`SdfWorldPackage.TemporalPeriod`), where FSR 2 scales the phase
  count with the squared ratio, about 14 at a render scale of 0.75. Check:
  at 0.75 a pan across thin geometry shows no more shimmer than at full scale,
  and the history weight a pixel saturates at follows the period.
- **History is clipped by variance, not by a min and max box.** Variance
  clipping on luma (Salvi 2016) keeps more of a stable history than the box.
  Check: a pan past specular and ink detail shows less ghosting at equal
  shimmer, with a still pixel's history unchanged.
- **Shading rate is measured on the RTX 4070.** Per-tile `viewsRate` is
  authored only in the RTX 2060's row (`worlds/standard/quality.puck`);
  whether it pays at 2560 by 1440 on the 4070 is read, not assumed. Check:
  the 4070 route read at `maxRate` 1 and 2, with captures of the tiles it
  coarsens.
- **Ink is remembered as a feature.** The views pass writes an ink bit so the
  resolve treats ink as a remembered sub-pixel feature, as FSR 2's lock does.
  This joins [the ink plan](ink.md)'s resolve work. Check: a moving ink line
  holds its width without crawl.

### P18 — Sky and atmosphere

Only the celestial `bodies` vocabulary and the remaining infinity-view image
and admission observations stay here. The [frame handbook](../rendering/sdf/handbook/frame-rendering.md#the-sky-once-and-a-composite-last)
owns clocks, the open layer stack, atmosphere and artist controls.
[Author leaks](open-items.md#author-leaks-to-the-standard) owns the move of
sky and quality presets into `worlds/standard/`.

P18-7 adds `render.sky.bodies`: discs, crescents and rings; directions,
orbits, keys and state-row motion; binding to named lights; and illumination
by other bodies. A body becomes a shadow candidate through its bound light.
Appearance and illumination remain distinct from the existing shadow-slot
selection and rasterized cascade path. Per-light penumbra stays on the light.
Remove the sun-disc-specific binding and fallback directions when their callers
use bodies: `WorldRenderSkyLayer.SunDisc`, `SdfSkyDisc.Light`,
`worldSunDirection`, `SdfSunDirection` and `DefaultSunDirection`. Show two
moving bodies lighting one another and their named lights casting the expected
cascade shadows in the debug world, with stable identities through a reorder.

P18-11 part two uses the existing `view`/`far` layer vocabulary, binder, final
per-view fits, image routing and completed shown-texel demand. Its remainder is:

- A `sky-portal` image and discriminator: the destination turns with the viewer
  and never translates with them; an unseen layer stops rendering on the next
  frame; a far instance renders only its angular rectangle.
- Actual admission at the infinity-view cap and named refusal one past it,
  including live edits, with count and memory visible in `world.budget`.
- Windowed observations of the live external source against capture fill and
  an independent nonexternal control, including withholding when a source
  never supplies the safe fill. Nesting retains the existing depth fallback.

Read actual work at `high` beside frame timing. These observations introduce no
floor-tier counter matrix, recorded ceiling or shadow-march path.

### P19 — Neighbour-content bounds for wallpaper folds

`SdfWallpaperFold.IsContinuous` admits PMM, P4M, P3M1 and P6M. The other thirteen
groups (P1, P2, PM, PG, CM, PMG, PGG, CMM, P4, P4G, P3, P31M and P6) jump at
cell walls or rotation seams: sampling only the local copy can miss a nearer
neighbour. Their admission needs both the CPU queries in `Puck.SignedDistance`
and the bake lattice to bound content across those discontinuities.

Derive a tight local content box widened over every in-cell image, including
P4G's offset mirror and the hexagonal sector images. Bound distance to the
neighbouring cells' content, and certify a region crossing a wall or sector
seam against all content that can affect it. Content touching a wall still
needs a certified crossing or a named refusal. Consult `FixedInterval` and the
existing shape bounds before adding per-shape logic; a loose sphere is not a
replacement for a tight tile box. Store the bound in a side table rather than
assuming spare instruction lanes.

Complete this work when all seventeen groups accept off-centre prototypes,
CPU distance and region-query results agree with explicit neighbouring copies,
and bake samples and contours remain correct across cell walls and sector
seams. Exercise them in the debug gallery on both backends.

### P20 — Visibility, areas and residency

Per-pass box culling, depth occlusion, rated presentation, planar merging,
quadric collapse, static-union chunks, per-view light range selection and
near-first mesh demand are implemented. The
[streaming plan](streaming-and-memory.md) owns lighting on the same demand (its
decision 9), mapped portal demand, byte-bounded eviction, authored areas and
terrain tiles.
The [engine job system](open-items.md#engine-job-system) owns execution and
memory ownership; the [rendering decision](../decisions/rendering.md#a-raster-pass-draws-only-what-it-can-show)
owns the one-display-pixel detail rule. How the frame decides what to draw
and submits it (compaction, instanced buckets, authored motion on the GPU,
geometry at the source, the early-depth variant and multi-view selection) is
[the visibility plan](visibility.md), with its build order and the
measurements that order it.

Remaining observations specific to visibility are:

- On a quiet machine compare `world.cull off`, `frustum` and `occlusion` in the
  lobby, aisle, hall and open entrance, using `host.frames`, mesh and cascade
  timings. Isolate the one-code lit-image difference between box and occlusion
  culling even when their geometry matches.
- On a display presenting at least 60 Hz, compare `world.rates on`/`off`, the
  far cascade averaged over held and rendered frames, nested views and pose
  work. The VRR play observation uses `world.target vrr`, `world.rates` and
  `debug.go range`; judge held bodies, screens and windows for stepping under
  the 30 and 15 Hz tiers. The tiers themselves are settled
  ([presentation rates stay tiers](../decisions/residency.md#only-presentation-follows-the-observer)):
  a continuous rate is reopened only by an observation the tiers cannot
  explain, named with it.
- Camera views filmed onto in-world screens still render at the consumer's
  rate: the screen lever rates source instances, and `WorldViewGraphHost.Rates`
  does not look up a camera view's glass as it does a session's.
- Choose boot presentation from measured memory, cores, display pacing and
  frame time while retaining the highest quality. Qualify the Deck's Vulkan
  path on the device at 45 FPS.

The far cascade's snap is never coarsened to save frame time: a coarser snap
gives up shadow resolution and cannot repair a missed frame bar. Holding the
cascade between changes is the answer taken instead: at an authored rate below
every frame it keeps a static layer and redraws only the moving casters over
it (`SdfFarShadows`). Its GPU check is step 4 of
[the RTX 2060's frame](#the-rtx-2060s-frame).

## Deferred to the end

These observations need a device, display or operating-system interaction.
They run on the debug world at `high`, with counted work beside quiet-machine
worst-frame timing, and keep their packages open until observed.

- P1a: a windowed boot without a usable GPU driver exits 2 with the named
  unsupported result, including render-root teardown.
- P1b: qualification on the reference AMD devices and a driver-initiated
  removal/recovery exercise. Record actual peak device-local memory alongside
  timing; the RTX 4070 observations do not qualify another device.
- P10 and sky/GI: paired-backend observations on the RTX 2060's own table,
  from the player build, after [its reading](#the-rtx-2060s-frame). Every
  frame stays under 16.7 ms; a lower tier cannot close it.
- P12b-4: a real camera feeds a screen on both backends.
- P13b-4: a real Windows editor receives the click at the mapped point, and the
  focus-return chord returns input to the game.
- P15: the Steam Deck runs the debug world at the highest quality with every
  frame under 22.2 ms; inspect reconstruction during motion. Dynamic-resolution
  behaviour is observed separately and cannot lower the qualification quality.
- P16: an HDR display reports the HDR swapchain colour space; a ramp exceeds
  SDR white; HDR desktop capture stays unclipped; toggling HDR reopens a still
  or changing captured desktop in the new encoding; and UI stays at paper white.
- Both backends: a final review of matching captures and device readings,
  with settled bakes and irradiance and matching camera/lighting state.

Later display work is HDR calibration, per-display metadata and Linux HDR on
the Steam Deck OLED. Later source work is PipeWire DMA-BUF capture and V4L2
camera producers.

---

[Plans](README.md) · [Decisions](../decisions/rendering.md)
