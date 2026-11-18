# The terrain pass

The owner found the valley's walls soft white masses with metre-scale grain, no
material, clipped exposure and no sense of distance, and ruled a real terrain
pass of generic engine capabilities plus two standard terrain material sets,
natural alpine and stylized painterly
([valley plan](valley-and-moons.md#the-owners-rulings)). The image exposure
meter and relief contact through a field collider are built; this plan holds
the rest.

## What exists

| Area | Where | What limits terrain |
|---|---|---|
| Creation relief | `CreationNoiseDocument` (`src/Puck.World.Authoring/Authoring/CreationDocument.cs`), `SdfOp.NoiseDisplace`; GPU `sdfValueNoise3` (`field/sdf-noise.hlsli`), CPU `SdfFieldEvaluator.RenderEditField` (`Queries/SdfFieldEvaluator.RenderOps.cs`) | fBm value noise added to the field: `MaxAmplitude` 4 m, `MaxNoiseOctaves` 8, no ridges, warp or erosion. |
| Smooth blends | `ShapeDocument.MaxSmooth` | 0.5 m, so a 400 m wall is hard-unioned masses. |
| The valley | `worlds/debug/valley.puck`, `worlds/standard/terrain.puck` | Eight sectors of three superellipsoids, 4 m fBm at 0.02 cycles/m, one palette slot. `terrainFloorCut` keeps buried halves off the lattice. |
| Bake detail | `CreationNoiseDocument.DetailSpacing`, `WorldPlacementStamper`'s `ReliefDetail` | The scoped bound is a sphere about the ring's centre and refinement stops at eight cells a coarse axis, so a sector bakes coarser than it declares ([mesh bake](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)): the metre-scale grain. |
| Contact | `WorldColliderSet`, `FixedStaticCollider` (analytic convex kinds and the relief's field collider, `FixedStaticColliderKind.Field`); `FixedFieldContactSolver`, the world-wide field provider the debug world does not select | A relieved copy collides as its fixed-point field, authored blends included; an unrelieved copy keeps analytic primitive unions. Both field paths read one certified surface solve; what remains of it is in [certified field contact](open-items.md#certified-field-contact). |
| Materials | The fixed weathering layer (`shade/shade-weathering.hlsli`) and the standard's `weathered` program (`worlds/standard/shading.puck`) | No slope or altitude rule, though the weathering's up-facing deposit and its curvature edges are such rules. |
| Atmosphere | `SdfSky.PackAtmosphere`, `shade/sdf-atmosphere.hlsli`, CPU twin `SdfAir` | Height fog and haze, scalar extinction; haze extinction is `-log(1 - amount) / farDistance`. The valley's fog (`clearDayAir`) fades rock toward a white sky, not toward blue. |
| Far shadows | `SdfMeshShadowCascades` | Splits at 16 and 120 m: every wall beyond 120 m is fully sunlit, with no ridge shadows. |
| Exposure | `SdfViewExposures`, `SdfExposure.Meter`, the composite's histogram | Each view meters its own image (the [rendering decision](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric)) under the Moth basis's band, percentiles and compensation; a cut snaps to the first reading rendered after it. |
| Residency | [residency decision](../decisions/residency.md), `SdfMeshDemand`, `SdfMeshChunks` | Chunks are bins of one contour and do not stream. Terrain tiles are unbuilt. |

## Design

### One relief, three consumers

Terrain relief becomes one generic field op, `TerrainRelief`, replacing
`NoiseDisplace` (rule 5: the creation `noise` facet migrates and the old op
goes). Its parameters are authored data on the creation:

- **Kind per band:** fBm, ridged (Musgrave's multifractal: `1 - |n|`, squared,
  weighted by the previous octave) or billow.
- **Domain warp:** a low-frequency vector noise offsets the point first.
- **Erosion mask:** each octave's amplitude is divided by `1 + k·|∇|²` of the
  sum so far, smoothing slopes and keeping detail in gullies and on ridges; the
  quintic interpolant's derivative is closed-form.
- **Octaves from kilometres to centimetres**, one series each consumer cuts at
  its own spacing. Octaves whose quarter-wavelength is at least the finest
  declared terrain spacing are geometry: the bake draws them and contact reads
  them. Finer octaves are shading: the terrain's shading programs read them to
  perturb the normal and their masks, fading where a wavelength falls under two
  pixel footprints.

The cut derives from the document alone, so the geometric field is the same on
every machine and nothing feeds back from presentation.

The evaluator already calls `Pcg3dLatticeNoise.Fbm3` in `Puck.Maths`
(`SdfFieldEvaluator.PointWalk.cs`). Extend that shared implementation with the
closed-form derivative, band kinds, and warp. The HLSL twin keeps the PCG3D corner hash, so
CPU and GPU agree to the float blend's LSB as today. The step factor
(`SdfProgram.NoiseDisplaceStepFactor`'s successor) keeps the undamped bound,
which the erosion divisor can only lower. Caps become relative: amplitude at
most half the creation's smallest shape radius (past that, 3D relief detaches
blobs), smooth radius at most half the smaller blended shape's least
half-extent. The octave limit rises to 12, about 2 km down to the bake's cell.
Shading octaves hash on the periodic lattice (`sdfPeriodicNoise3`): a
centimetre octave 1 km from the origin leaves a float about 2^-7 of a cell.

### Contact on the drawn relief

One field serves drawing and contact: a solid placement whose creation carries
relief, or a smooth blend over the old cap, compiles a field collider whose
surface is the creation's fixed-point program (its geometric octaves), not its
primitives. `WorldColliderSet` keeps its grid and bounds cull, everything else
stays analytic, and there is no world-wide switch to field contact.
`TerrainRelief` replaces `NoiseDisplace` inside the same collider program, and
a smooth blend over the cap reuses the collider by compiling its creation the
same way.

**The step bound to implement:** after preserving a conservative Lipschitz
bound through fixed-point compilation, a body volume can advance by the
certified field bound. The analytic provider currently slices motion by half
the body's least support radius; its field colliders do not use
`CertifiedFieldSweep`. The existing interval rule (`RenderEditFieldBounds`)
bounds the displacement's field-value contribution, not its Euclidean surface
reach. Integrate certified relief sweeps and check the 64-query budget
(`DefaultSweepBoundsQueryBudget`) against the Moth's top speed.

Grounding and the slope test read the field's normal, so the walkable slope is
the drawn slope.

### Slope, altitude and curvature materials

Terrain materials are authored shading programs, never a fixed layer table in
the engine ([authored shading](../decisions/rendering.md#authored-shading)): a
fixed table would be a second shading mechanism beside the programs, built
against rows the shader plan's
[step 6](author-shaders.md#the-remaining-steps) deletes.

The standard package gains a `shade` template for terrain that composes up to a
few bands, each a material response (colour, roughness, specular, optional
detail relief) under a mask the program computes from the stamp:

- slope, from `surface.normal` against world up, a degree range with softness;
- altitude, from `surface.position`, a height range with softness;
- curvature, from `surface.curvature`, the mean curvature the surface pass
  derives from the baked normals (convex ridges, concave gullies), which the
  standard's `weathered` program already reads for its edges;
- breakup, from `lattice` in world space with a scale and a seed, which jitters
  each mask's edges.

Solid 3D noise needs no triplanar projection, so steep walls do not stretch.
The band count is a structural parameter and every range, softness and colour a
runtime one, so the two standard sets and an author's third are one program
with different data. A rigid copy's bands read its own frame through the
layer-space stamp members, as paints do.

Regional weather coatings reach the same programs as lanes and parameters, so
retained wetness and deposits and terrain's slope and altitude bands are one
mechanism
([weather decision](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces)).

What this needs from the engine is named as it is found, each as an op or a
stamp member with a declared cost: the shading octaves of the relief above the
geometric cut (the terrain program reads the same series), and a curvature that
describes more than the baked normal field if a look needs gullies finer than
the bake's cell. The second is open generic work with no producer chosen.

### Exposure at its cause

The image meter is built. Still open from its target: the lighting packet's 18%
grey card under a manual EV, which settles the output transfer; the valley
stops' rock clipping; and the composite's added cost against the composite
alone, a measurement request.

### Distance

- **Aerial perspective:** haze extinction becomes per-channel coefficients per
  metre (a Rayleigh-weighted blue and a grey Mie term with its forward phase)
  over the existing height falloff, authored as a visibility range instead of
  `HazeAmount` over `farDistance`. Haze leaves draw distance, and rock fades
  toward blue. `SdfAir` and `sdf-atmosphere.hlsli` change together; the valley
  plan's spherical shell replaces this flat model later.
- **Far shadows:** a terrain cascade from 120 m to the far distance draws static
  draws at their coarsest level, refreshed only when the sun or a static draw
  changes, so ridges shadow the walls behind them. Holding it between changes
  keeps the authored shadow precision; the cascade's snap is never coarsened.
  This step is the one home of the terrain cascade, and the
  [illumination plan](illumination.md#the-build-order) waits on it.

### What the bake needs

1. **A scoped creation's bound about its shapes**, not its origin, so a
   sector's declared spacing reaches the lattice.
2. **Terrain tiles as residency units.** A terrain placement declares a tile
   size and the stamper emits one static instance per tile, bounded by the
   tile's box, each its own group, so a tile is demanded, baked at the tier its
   demand chooses, kept and evicted like any group. The unit and the rule where
   two tiles meet are the streaming plan's
   ([the source tile and its seam rule](streaming-and-memory.md#the-source-tile-and-its-seam-rule):
   neighbours overlap and each tile is closed on its own), built in its step
   10; terrain consumes it and does not take it early for itself.
3. **The finest tile spacing is the relief's geometric cut**, so contact and
   the nearest drawn mesh agree within the bake's error.

## The two standard material sets

Both live in `worlds/standard/terrain.puck` as templates that fill a terrain
creation's palette slots in the same order (`terrainRock`, `terrainLedge`,
`terrainScree`, `terrainTurf`, `terrainSnow`) with the terrain program's bands,
so an author switches with one call per sector. Overriding a slot or writing a
third set is ordinary `.puck`.

- **`terrainAlpine()`**, natural: grey-brown rock with a ridged detail normal,
  dark ledges on concave curvature, scree on 30 to 40 degree slopes, turf below
  35 degrees under a treeline altitude, broken snow above an altitude on slopes
  under 45 degrees, rough dielectrics throughout, and a haze preset with a blue
  Rayleigh term.
- **`terrainPainterly()`**, stylized: the same masks with near-zero softness, so
  slopes and altitudes read as flat bands; saturated hues that shift with
  altitude and warm on convex curvature; brush-scale breakup and no centimetre
  detail; a stronger tinted haze. It needs no cel style.
  The valley uses it, with the
  [Canyon workshop pack](../game/art/canyon-workshop-pack/README.md)'s valley
  overview as the reference for its strata, ledges and floor.

Each set ships a matching relief preset (alpine: ridged, warped, eroded;
painterly: billow, broad warp, fewer octaves).

## Order of work

**Engine gaps first.** Four of the engine gaps in the
[rendering plan's list](rendering-research.md#engine-gaps-filled-first) are
this plan's, and each is filled before work that sculpts or tunes around it
(the owner's ruling,
[AGENTS.md](../../AGENTS.md#pucks-purpose)). Each is a step below:

- **Step 4's bound fix, with its relative caps (gap 1).** A creation whose parts
  carve each other, or that carries relief, is emitted as one instance for the
  whole creation, so a sector's bound, not its declared spacing, sets its
  cell, and relief and smooth blends are capped at prop sizes. The first
  action is the one count the gap entry names: the triangles a bound about the
  shapes costs.
- **Step 3's `TerrainRelief` op, carrying a mask (gap 4).** A relief has no
  region, so it buries anything set into the terrain (the lake's bowl, the
  pool, the river's channel, the glacier's cradle). The op's design gains a
  way to mask or flatten the relief over a region, and that is the step's
  first part. Its CPU side starts now; its kernel edit lands in the bake
  fundamental's batch of kernel edits (see Risks).
- **Step 6's terrain programs (gap 2).** The default frame runs the fixed
  layers, which have no slope or altitude rule, because authored programs
  sit behind a switch that is left off. The step needs the colour conversion
  (decision 5 of the rendering plan, which starts now, and which step 1's grey
  card reads) and the RTX 4070's shading columns, which
  [the shader plan](author-shaders.md#the-decision-matrix) already holds. The
  RTX 2060's and the Steam Deck's columns do not hold it back.
- **Step 7's far shadows (gap 3).** Nothing casts a shadow past 120 m. The step
  starts when shadow maps are assigned by light role (the rendering plan's
  work that starts now).

The rows below are in landing order, and a step's number is its name.
Steps 0 and 1 come first because the gap steps read against them. Steps 2, 5
and 8 are not gaps and follow: step 8 is the work that would otherwise tune
around steps 3, 4 and 6.

Stills are the sweep's six `valley` stops (`worlds/debug/sweep.json`), settled
under `world.lockstep` on both backends, before and after on one build pair.
The frame bar is read by [the one recipe](streaming-and-memory.md#how-the-bars-are-read)
at those poses, with `world.gpu-timing` beside it; bake numbers come from
`puck mesh audit --cpu --group "(valley-"`. A step's before reading is taken in
the same window as its after, never from a recorded figure.

| Step | Lane | Needs | Target |
|---|---|---|---|
| 0. Baseline | lead, GPU | GPU free | Six stills on both backends; the frame and per-pass GPU time at each pose; `world.exposure` at each stop after two tour orders on one World. |
| 1. Image meter's checks | rendering A | 0 | The grey card reads 118/255 ±3 under its manual EV. Under 0.5% of rock pixels clip in any channel at `valley-rim` and `valley-slope`. A stop's EV agrees within 0.05 whichever stop preceded it (a cut snaps, so the band leaves no history). The composite's added cost with the meter is stated beside the composite alone. |
| 4. Relative caps, shape bound (gap 1) | authoring/bake D | The bound fix none, and its first action is the triangle count of a bound about the shapes; 3 for relief | A sector bakes at its declared spacing. Warm boot's counted bake work is unchanged. |
| 3. `TerrainRelief` op, with a mask over a region (gap 4) | field/maths C | The kernel edit lands in the [bake fundamental](open-items.md#bake-proportional-to-change)'s batch of kernel edits; the CPU side and the mask's design start now | A fixed-point CPU bake and a kernel bake of one sector, both derived in the run, agree within the bake's tolerance. The exact noise result is pinned by one Maths law. A sector's CPU bake at the same cell costs at most 1.5× its `NoiseDisplace` bake in counted samples. With the mask over the lake's bowl, the bowl's floor and walls stand where their shapes put them. |
| 6. Terrain programs (gap 2) | rendering/shading F | The colour conversion (step 1's grey card reads it); the RTX 4070's shading columns ([the shader plan's matrix](author-shaders.md#the-decision-matrix)), which are read | The terrain template with its slope, altitude, curvature and breakup masks, and the weathering's up-facing deposit expressed through it. The frame bar holds at `valley-slope` with the program on every wall; the stills read as the two sets' references. |
| 7. Far shadows (gap 3) | rendering A, after 1 | 0; shadow maps assigned by light role (the rendering plan's work that starts now) | `valley-rim` shows ridge shadows on the far walls, and the frame bar holds there with the sun moving. |
| 2. Aerial perspective | rendering B | 0 | `SdfAir` and the kernel agree as today. At `valley-draw-distance`, rock 700 m out shifts measurably toward the sky's hue and the rim keeps its silhouette. Changing `farDistance` changes no pixel nearer than it. |
| 5. Certified field contact | physics E | [certified field contact](open-items.md#certified-field-contact), with 3's relief | One certified solve serves both contact paths; the valley witnesses report 0 red, feet stay within skin plus bake error on the 58° face, creep is 0, and two runs reproduce. No coarser contact cut is allowed. Still open: the 58° face, the rim's creep and clearance close to a slope (packet). |
| 8. The sets and the valley | content G | 3, 4, 6 (2 for the haze presets) | Both sets compile and lint clean under `--strict`. Six stills demonstrate each set; the valley wears the painterly set under the owner's ruling. |

Steps 0, 1, 3, 4, 6 and 7 start together: the gap steps do not wait on one
another, and each waits only on what its row names. Step 3's kernel edit lands
with the bake fundamental's batch. Step 5 belongs to the certified-contact
packet and follows 3's relief; step 8 closes. Tiles arrive with the streaming
plan's step 10 and are not a step here. Steps 1, 2, 6 and 7 need windowed legs
on both backends; 3, 4 and 5 are CPU work until their stills.

## Risks and owner calls

Risks:

- **Bake identity churn.** Step 3 edits the field kernel, which rebakes every
  mesh. Land step 3's kernel edit in the
  [bake fundamental](open-items.md#bake-proportional-to-change)'s batch of
  kernel edits, so every mesh rebakes once; the rest of step 3 does not wait.
  A relief edit afterwards rebakes its own groups, nearest first.
- **Field contact cost.** The certified solve reads the field a few dozen times
  a tick at the rim, each read a few samples per octave, and an identical-box
  cache spares most of its box-bound queries. The next cut is the job system's
  parallel contact work. Contact keeps every geometric octave and its declared
  precision.
- **Loose fields.** Warp and amplitude raise the step factor, slowing the bake
  and the sweep; the erosion mask and relative cap hold it.
- **A program on every wall.** The valley's walls fill the view, so the terrain
  program's static cost is paid on most pixels; its bands are priced in
  `world.budget` and its registers by the shader plan's matrix.
- **The probes see the base response**, so snow bounces its slot's base colour,
  not the band a program chose
  ([the contract](../decisions/rendering.md#authored-shading)). Each terrain
  slot's base colour is authored as its lighting-representative value.
- **Tile seams.** The seam rule admits a step no taller than two tiers' summed
  error where neighbours differ; its rim check is the streaming plan's.

Owner calls:

- The valley wears the painterly set; step 8 demonstrates the ruled direction.
- Whether the "pale, bright" exposure compensation stays once the meter reads
  the image.
- How strongly distance fades toward blue (each set's haze is a proposal).
- Decided by the owner: the time of day varies and the sun's direction is
  authored. The valley's light runs through an authored cycle, never one fixed
  sun, so the image meter, GI proportional to change and the far cascade are
  measured across the cycle. That is part of the lighting-direction pass the
  lighting packet holds.
