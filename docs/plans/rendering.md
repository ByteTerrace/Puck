# Rendering

The frame rasterizes baked SDF meshes with shadow cascades and a CPU-traced
irradiance volume. This plan holds the remaining sky vocabulary, wallpaper
bounds, rendering follow-ups and hardware observations. The debug world
(`worlds/debug`) proves the work on both backends; features from experimental
worlds are ported into it rather than reviving those worlds.

The [rendering decisions](../decisions/rendering.md) own the design, the
[shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
owns pipeline authoring, and the [frame handbook](../rendering/sdf/handbook/frame-rendering.md)
and [rendering skill](../../.claude/skills/rendering/SKILL.md) own the implemented
contracts. Delivered package history belongs in git.

## Where each package stands

A landed package can still have a hardware observation owed below. The
[shared queue](open-items.md#next) ranks work across programmes.

| Package | State | Open remainder |
|---|---|---|
| P1a, durable functional fixtures | open; software landed | [No-driver boot](#deferred-to-the-end) |
| P1b, foundation qualification | open | [Reference devices and driver removal](#p1b--foundation-qualification) |
| P4, shared opaque visibility | landed; field-marching path retired | [Baked frame](#p4--shared-opaque-visibility) |
| P6, representation experiments | withdrawn except GI | [Lighting and GI](#p6--representation-experiments) |
| P10, bound rows and tiers | open; software landed | [Hardware qualification at high](#deferred-to-the-end) |
| P12, image sources | open; software landed | [Real camera](#deferred-to-the-end) |
| P13, hit-to-source mapping and input | open; software landed | [Windows passthrough click](#p13--hit-to-source-mapping-and-input-destinations) |
| P14, SDF pass package | landed | [Rendering follow-ups](#p14--the-sdf-engine-as-a-pass-package) |
| P15, temporal reconstruction | open; software landed, march seeding withdrawn | [Steam Deck observation](#p15--temporal-reconstruction) |
| P16, display output | open; software landed | [HDR display](#deferred-to-the-end) |
| P17, assets derived from SDFs | withdrawn | [Bake ownership](#p17--assets-derived-from-sdfs) |
| P18, sky and atmosphere | open | [Celestial bodies and infinity-view admission](#p18--sky-and-atmosphere) |
| P19, wallpaper-fold bounds | open | [CPU certification and bake lattice](#p19--neighbour-content-bounds-for-wallpaper-folds) |
| P20, visibility and residency | open; P20-1, P20-2, P20-3, planar merge, quadric collapse, static-union chunks and near-first demand landed | [Residency and observations](#p20--visibility-areas-and-residency) |

## The forcing artifact

Every change proves itself in the debug world: its device row, gallery, nested
screens and portals, sky and valley. Captures and live counters show what a
change does; `host.frames` and `world.gpu-timing` on a quiet machine show its
cost. Canaries under `experimental/tests/Puck.World.Canaries` are quarantined.

The bar is the worst frame at `world.quality high`: under 16.7 ms on the
RTX 2060 and RTX 4070 (60 FPS), under 22.2 ms on the Steam Deck (45 FPS).
Read counted work beside that timing as diagnostic evidence. Lower tiers,
coarser shadow snapping, reduced contact detail and a frame of latency never
repair a missed bar. The [engine job system](open-items.md#engine-job-system)
owns scheduling, parallel tick execution and view recording; the
[bake work](open-items.md#bake-proportional-to-change) owns bake speed and identity.

## Packages

### P1b — Foundation qualification

The remaining reference-device and driver-removal observations are listed
under [deferred checks](#deferred-to-the-end). Qualify the debug world at
`high` and retain the actual memory and timing readings. No recorded work-count
ceiling substitutes for a device observation.

### P4 — Shared opaque visibility

The frame rasterizes baked meshes. The shared visibility record and projection
rules belong to the [frame handbook](../rendering/sdf/handbook/frame-rendering.md#surface-transport)
and [frame-graph decision](../decisions/rendering.md#the-frame-graph-and-nesting).

### P6 — Representation experiments

The experiments tied to the per-pixel field march are retired with that path.
Glass is part of the baked frame; its remaining work is [below](#glass).

#### P6-GI, global illumination

The [illumination plan](illumination.md) holds the decisions from the
illumination research round and their build order: the probe volume splits
into cached hits and a budgeted GPU relight, direct light is made correct at
author scale, the geometry trace moves to the GPU behind one trace-hits
interface, bitmask AO steers the gather, and light lists and area lights bound
the light loop. The [Lighting and GI packet](open-items.md#lighting-and-gi)
owns the remaining coverage, visibility, exposure and bounce findings. The
[engine job system](open-items.md#engine-job-system) owns its execution budget,
and [residency](open-items.md#residency-and-streaming) owns demand-point
windowing. No second scheduler is built for GI.

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
owns sub-cell features and contact against the drawn surface;
[bake detail](open-items.md#bake-detail) owns static round sections and
per-member detail. The remaining bake investigations are:

- Finish an in-flight group that a later plan drops and retain it in the library
  and store. Read back a boot's second, different `start` plan so churn cannot
  silently rebake discarded work on every boot.
- Lower the remaining nonuniform `Scale` fallbacks to native anisotropic
  shapes: a torus; capsule, cylinder or cone with unequal X and Z scales;
  prism profiles; and `SdfProgramBuilder.Path`. A multi-shape capsule lowering
  needs scope and emitter accounting for its elliptic cylinder and ellipsoid
  caps. Its bake samples must represent the intended surface rather than
  inheriting a loose distance bound.
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

R2 extends `SdfMeshOcclusion`'s existing indirect mechanism to opaque, every
shadow cascade and glass. Its compute kernel selects bounds and mesh levels
from the shared retained mesh records; a pass uploads constant-sized controls
and bounded portal sight queries. Each pass retains visibility, level and mesh identity per draw on
the GPU, preserving the 1/1.1 display-pixel band and resetting level history
when the mesh changes or a settled capture holds the frame. Buffer growth
copies that history. Opaque occlusion keeps its early/late phases; glass uses
one. Cascades reserve early/late entry, solid-exit, thin-exit and optional optical
argument phases. The far cascade's receiver mask and two-phase light-space depth
share this selector; exit and optical lists never use entry-depth visibility
([contract](../../.claude/skills/rendering/SKILL.md#far-cascade-caster-selection)).
CPU `Choose` and per-draw recording
are removed. Vulkan's native-count fallback reads the same initialized
argument slots through `vkCmdDrawIndirect`; `DrawIndex` reads their identities
without the optional first-instance feature. Hardware observations are
recorded in the memo's R2 row.

Shared geometry instancing remains a separate extension: the meadow has
4,215 draws for 288 geometries (14.6 draws per geometry). Sparse visibility,
per-draw level choice and stable depth-tie ordering require an instance map
and per-geometry/per-level ranges, rather than changing `instanceCount`
alone. Preserve draw identity, materials, transforms, carry and clip planes
through that map, and measure it through this same indirect mechanism. The
lead's [visibility plan](visibility.md#the-build-order) owns ordered
compaction and these instance buckets after R2.

- Re-measure RTX 2060 pixel passes at matched clocks before attributing a
  Vulkan/Direct3D 12 gap. Inspect surface and resolve register pressure and
  occupancy; measure geometry instancing in R2's indirect records rather
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

The back of a portal's glass still needs a presentation choice: no image,
fallback colour or a front-image mirror. The
[owner decisions](open-items.md#owner-decisions) hold that choice.

### P15 — Temporal reconstruction

Reconstruction and dynamic resolution are implemented. P15-8's floor-tier
tuning is removed; the Steam Deck observation below runs at `high`.
The [temporal packet](open-items.md#temporal-shimmer-and-cadence-flicker) owns
remaining image work. Delete the obsolete march-step load signal
(`WorldDynamicResolutionSignal.Steps` and `StepBudgetPerPixel`) through the
[editor's cost work](editor.md#e9--cost-per-object-and-gpu-pass-timing);
counting bake steps cannot measure the baked frame's load.

### P17 — Assets derived from SDFs

Withdrawn as a separate package. Every frame already draws baked meshes;
[bake work](open-items.md#bake-proportional-to-change) owns their derivation.

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
[residency packet](open-items.md#residency-and-streaming) owns demand-point GI,
mapped portal demand, byte-bounded eviction, authored areas and terrain units.
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
  `debug.go range`; judge held bodies, screens and windows for stepping and
  whether the discrete 30/15 Hz rates should be continuous.
- Camera views filmed onto in-world screens still render at the consumer's
  rate: the screen lever rates source instances, and `WorldViewGraphHost.Rates`
  does not look up a camera view's glass as it does a session's.
- Choose boot presentation from measured memory, cores, display pacing and
  frame time while retaining the highest quality. Qualify the Deck's Vulkan
  path on the device at 45 FPS.

The cached far-cascade proposal is removed. Coarser snapping sacrifices shadow
resolution and cannot repair a missed frame bar.

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
