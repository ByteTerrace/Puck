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
| P10, bound rows and tiers | [Hardware qualification at high](#deferred-to-the-end) |
| P12, image sources | [Real camera](#deferred-to-the-end) |
| P13, hit-to-source mapping and input | [Windows passthrough click](#p13--hit-to-source-mapping-and-input-destinations) |
| P14, SDF pass package | [Bake quality, glass, submission and routed worlds](#p14--the-sdf-engine-as-a-pass-package) |
| P15, temporal reconstruction | [The march-step signal and the Steam Deck observation](#p15--temporal-reconstruction) |
| P16, display output | [HDR display](#deferred-to-the-end) |
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
  What still rides the generic `Scale` bound, which `puck creation shape`
  reports as a gain: a capsule, cone or torus scaled apart in X and Z, and a
  round cone scaled apart at all; an ellipsoid or sphere scaled apart is the
  exponent-2 superellipsoid gauge, which contact admits; a `Path` profile and a
  sweep certify no gain. Regular polygons already lower their scaled vertices
  to the exact convex polygon. Each gets a closed-form lowering where one
  exists (a multi-shape capsule lowering needs scope and emitter accounting for
  its elliptic cylinder and ellipsoid caps) and otherwise a bounded
  closest-point solve certified by an enclosing interval and a residual bound;
  no such solver exists yet, and its design is the step's first part. Check:
  read every stretched gallery primitive through `puck creation shape`; audit
  each field against closest-surface distances on both sides and at the
  degeneracies; audit a subtraction and a smooth blend using each as a cutter;
  report distance error, certification, field work and mesh defects, with the
  CPU and GPU fields agreeing; then settled captures in the gallery and the
  bake's added work in a quiet window before choosing which solves to admit.
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

A distributed mesh cache waits until a presenting client downloads packages.
At that point a GPU runner bakes through a `puck mesh bake` verb, retains only
used entries, and supplies an optional read-only package cache ahead of the
user store. Deployment proceeds without it on a miss; the client derives
missing meshes. WARP and the fixed-point CPU audit are not substitutes for
that GPU product. The [derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)
owns identity and cache semantics; the bake packet owns the derivation closure
and shared store before this distribution work.

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

- Re-measure RTX 2060 pixel passes at matched clocks before attributing a
  Vulkan/Direct3D 12 gap. Inspect surface and resolve register pressure and
  occupancy; measure geometry instancing in the indirect records rather
  than repeating the slower one-draw vertex-search experiment.
- Reopen either unattributed Vulkan device-loss case on recurrence: the
  first-frame fault whose last checkpoint is `glass-clear`, or the fault as
  Nsight GPU Trace begins whose checkpoint is root `place`. Repeated successful
  boots do not establish a cause; retain fresh fault and validation logs.
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
- Measure live residency and view-target bytes on the RTX 2060, including
  device-local and host-visible-device-local usage and Vulkan aperture heaps.
  Share tables for the same mirror and stage infrequently changed data where
  the measured budget calls for it.
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

### P15 — Temporal reconstruction

Reconstruction and dynamic resolution are implemented. P15-8's floor-tier
tuning is removed; the Steam Deck observation below runs at `high`.
The [temporal packet](open-items.md#temporal-shimmer-and-cadence-flicker) owns
remaining image work. Dynamic resolution follows GPU frame timing, with present
timing as its fallback; without timing or a display period it holds its grid
([frame handbook](../rendering/sdf/handbook/frame-rendering.md)).

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
gives up shadow resolution and cannot repair a missed frame bar. A cascade
held between changes is a different thing and is still planned, for terrain
alone: the [terrain pass](terrain-pass.md#distance)'s far-shadow step is its
one home.

## Deferred to the end

These observations need a device, display or operating-system interaction.
They run on the debug world at `high`, with counted work beside quiet-machine
worst-frame timing, and keep their packages open until observed.

- P1a: a windowed boot without a usable GPU driver exits 2 with the named
  unsupported result, including render-root teardown.
- P1b: qualification on the reference AMD devices and a driver-initiated
  removal/recovery exercise. Record actual peak device-local memory alongside
  timing; the RTX 4070 observations do not qualify another device.
- P10 and sky/GI: paired-backend hardware observations at `high` on the
  RTX 2060, including live and retiring residency/graph bytes and aperture
  usage. The frame remains under 16.7 ms; lower-tier observations cannot close it.
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
