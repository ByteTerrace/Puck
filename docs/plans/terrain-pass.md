# The terrain pass

The owner found the valley's walls soft white masses with metre-scale grain, no
material, clipped exposure and no sense of distance, and ruled a real terrain
pass of generic engine capabilities plus two standard terrain material sets,
natural alpine and stylized painterly
([valley plan](valley-and-moons.md#the-owners-rulings)). This is the design
before code.

## What exists

| Area | Where | What limits terrain |
|---|---|---|
| Creation relief | `CreationNoiseDocument` (`src/Puck.World.Authoring/Authoring/CreationDocument.cs`), `SdfOp.NoiseDisplace`; GPU `sdfValueNoise3` (`field/sdf-noise.hlsli`), CPU `SdfFieldEvaluator.RenderEditField` (`Queries/SdfFieldEvaluator.RenderOps.cs`) | fBm value noise added to the field: `MaxAmplitude` 4 m, `MaxNoiseOctaves` 8, no ridges, warp or erosion. |
| Smooth blends | `ShapeDocument.MaxSmooth` | 0.5 m, so a 400 m wall is hard-unioned masses. |
| The valley | `worlds/debug/valley.puck`, `worlds/standard/terrain.puck` | Eight sectors of three superellipsoids, 4 m fBm at 0.02 cycles/m, one palette slot. `terrainFloorCut` keeps buried halves off the lattice. |
| Bake detail | `CreationNoiseDocument.DetailSpacing`, `WorldPlacementStamper`'s `ReliefDetail` | Declares 1.56 m, but the scoped bound is a 770 m sphere about the ring's centre and refinement stops at eight cells a coarse axis, so sectors bake at 2.8 m ([mesh bake](../../.claude/skills/rendering/references/mesh-bake.md#groups-poses-and-materials)): the metre-scale grain. |
| Contact | `WorldColliderSet`, `FixedStaticCollider` (analytic convex kinds and the relief's field collider); `FixedFieldContactSolver`, the world-wide field provider the debug world does not select | Relieved copies use field colliders, including their authored blends; unrelieved copies keep analytic primitive unions. Both field paths read one certified surface solve; what remains of it is in [certified field contact](open-items.md#certified-field-contact). |
| Materials | `shade/shade-weathering.hlsli`, `shade/shade-layers.hlsli`, called from `passes/sdf-light-stage.hlsli` | No slope, altitude or curvature rule, though weathering's up-facing `deposit` and its curvature edges are such rules in fragment form. |
| Atmosphere | `SdfSky.PackAtmosphere`, `shade/sdf-atmosphere.hlsli`, CPU twin `SdfAir` | Height fog and haze, scalar extinction; haze extinction is `-log(1 - amount) / farDistance`. The valley's fog (`clearDayAir`) fades rock toward a white sky, not toward blue. |
| Far shadows | `SdfMeshShadowCascades` | Splits at 16 and 120 m: every wall beyond 120 m is fully sunlit, with no ridge shadows. |
| Exposure | `SdfViewExposures`, `SdfExposure.Meter`, the composite's histogram | Each view meters its own image (the [rendering decision](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric)) under the Moth basis's band, percentiles and +2.5 EV compensation; a cut snaps to the first reading rendered after it. |
| Residency | [residency decision](../decisions/residency.md), `SdfMeshDemand`, `SdfMeshChunks` | Near-first has landed; chunks are 8 m bins of one contour and do not stream. Terrain tiles (the decision's step 7) are unbuilt. |

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
  them. Finer octaves are shading: they perturb the normal and the material
  layers, fading where a wavelength falls under two pixel footprints.

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

One field for drawing and contact: a solid placement whose creation carries
relief, or a smooth blend over the old cap, compiles a **field collider**, a new
`FixedStaticColliderKind` whose surface is the creation's fixed-point program
(its geometric octaves), not its primitives. `WorldColliderSet` keeps its grid
and bounds cull. Everything else stays analytic, with no world-wide switch to
field contact.

Landed for relief (`FixedStaticColliderKind.Field`, `FixedColliderField`,
`WorldColliderSet.Relief.cs`): one collider per stamped copy over the copy's
contact shapes and `NoiseDisplace` in one scope, read by the contact
`SdfFieldEvaluator` (the op is a contact op, and the field provider emits the
same relief), with the fixed-point fBm in `Pcg3dLatticeNoise.Fbm3`. The
narrowphase is the one certified surface solve the field provider's
`FixedFieldContactSolver` reads too (`FixedFieldColliderContact.Solve`): only
the evaluator's certified box bounds prove a body clear, whole or per core piece
no longer than its radius, with the relief charged only what it reaches inside
the box (`Pcg3dLatticeNoise.Fbm3Bounds`: per octave the exact range of the
trilinear blend over the box's fades, plus the point blend's pinned rounding)
and an identical box answered once (`SdfBoundsCache`); otherwise a converged
surface solve measures the Euclidean distance from the core to a bracketed
surface point. A union scope's `1/L` is carried in the proposed step
scale rather than applied to its values only where nothing after the scope reads
its magnitude, so a scale under the Q48.16 grid keeps the field's resolution
without moving the solid. A smooth blend
over the cap reuses the collider by compiling its creation the same way; `TerrainRelief` replaces `NoiseDisplace`
inside the same program.

**The step bound to implement:** after preserving a conservative Lipschitz
bound through fixed-point compilation, a body volume can advance by the
certified field bound. The analytic provider currently slices motion by half
the body's least support radius; its field colliders do not use
`CertifiedFieldSweep`. The existing interval rule (`RenderEditFieldBounds`)
bounds the displacement's field-value contribution, not its Euclidean surface
reach. Integrate certified relief sweeps and check the 64-query budget
(`DefaultSweepBoundsQueryBudget`) against the Moth's top speed.

Grounding and the slope test read the field's normal, so the walkable slope is
the drawn slope. The downslope creep's cause was the walkable push itself: it
displaced a body along the tilted normal and clamped its velocity along it, and
both leave a downslope share of each tick's fall. Walkable ground now lifts
along up by the penetration over `normal·up` and removes the approach through
the velocity's up component (`FixedContactPushMath.ComputeOrdinary`), so a body
at rest holds its place on any walkable slope and planar motion is the body's
own; on level ground the two forms are one.

### Slope, altitude and curvature materials

A palette slot gains **layers**, up to four, packed into a side table as paints
are (`SdfAppearance.PackPaint`), so the material row grows by one word. Each layer
is a material response (colour, roughness, specular, optional detail relief)
over a mask built from:

- slope: the angle between the shading normal and world up, a degree range with
  softness;
- altitude: world height range with softness;
- curvature: the visibility record's mean curvature (convex ridges, concave
  gullies), the input weathering's edge term already reads;
- breakup: lattice noise in world space, scale and seed, which jitters each
  mask's edges.

Solid 3D noise needs no triplanar projection, so steep walls do not stretch.
Layers evaluate in `sdf-light-stage.hlsli` beside `applyWeathering`, whose
up-facing `deposit` migrates into layers and is deleted. Regional weather
coatings drive these same layer masks, leaving one mechanism for retained
wetness and deposits as well as terrain slope and altitude
([weather decision](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces)).
A rigid copy's layers read its own frame, as paints do.

### Exposure at its cause

The image meter has landed (step 1). Still open from step 1's target: the
lighting packet's 18% grey card under a manual EV, which settles the output
transfer; the valley stops' rock clipping; and the composite's added cost
against the composite alone, a measurement request.

### Distance

- **Aerial perspective:** haze extinction becomes per-channel coefficients per
  metre (a Rayleigh-weighted blue and a grey Mie term with its forward phase)
  over the existing height falloff, authored as a visibility range instead of
  `HazeAmount` over `farDistance`. Haze leaves draw distance, and rock fades
  toward blue. `SdfAir` and `sdf-atmosphere.hlsli` change together; phase 5's
  spherical shell replaces this flat model later.
- **Far shadows:** a terrain cascade from 120 m to the far distance draws static
  draws at their coarsest level, refreshed only when the sun or a static draw
  changes, so ridges shadow the walls behind them. Caching preserves the
  authored shadow precision and never coarsens the cascade snap.

### What the bake needs

1. **A scoped creation's bound about its shapes**, not its origin, so a
   sector's declared spacing reaches the lattice.
2. **Terrain tiles.** A terrain placement declares a tile size; the stamper
   emits one static instance per tile, bounded by the tile's box, each its own
   group. A tile bakes at the detail its demand chooses (the residency
   decision's passive rule: the coarser level's error projected past about a
   pixel) as a separately keyed geometry, so the coarse tile draws while the
   fine one bakes, and the selector evicts by priority. Tiles at different
   spacings meet with skirts, strips hanging below each open edge by the coarser
   spacing. Contouring a shared border at the coarser spacing instead ties a
   tile's key to its neighbours' levels, so it is tried only if skirts show.
   This is the decision's step 7, taken now for terrain alone.
3. **The finest tile spacing is the relief's geometric cut**, so contact and
   the nearest drawn mesh agree within the bake's error.

## The two standard material sets

Both live in `worlds/standard/terrain.puck` as templates that emit a terrain
creation's `palette` block, as `skies.puck`'s `clearDay` emits sky layers, and
fill the same slots in the same order (`terrainRock`, `terrainLedge`,
`terrainScree`, `terrainTurf`, `terrainSnow`), so an author switches with one
call per sector. Overriding a slot or writing a third set is ordinary `.puck`.

- **`terrainAlpine()`**, natural: grey-brown rock with a ridged detail normal,
  dark ledges on concave curvature, scree on 30 to 40 degree slopes, turf below
  35 degrees under a treeline altitude, broken snow above an altitude on slopes
  under 45 degrees, rough dielectrics throughout, and a haze preset with a blue
  Rayleigh term.
- **`terrainPainterly()`**, stylized: the same masks with near-zero softness, so
  slopes and altitudes read as flat bands; saturated hues that shift with
  altitude and warm on convex curvature; brush-scale breakup and no centimetre
  detail; a stronger tinted haze. It needs no world-wide `render.toon`, which
  stays a separate choice.
  The valley uses it, with the
  [Canyon workshop pack](../game/art/canyon-workshop-pack/README.md)'s valley
  overview as the reference for its strata, ledges and floor.

Each set ships a matching relief preset (alpine: ridged, warped, eroded;
painterly: billow, broad warp, fewer octaves).

## Order of work

Stills are the sweep's six `valley` stops (`worlds/debug/sweep.json`), settled
under `world.lockstep` on both backends, before and after on one build pair.
Frame time is `host.frames` and `world.gpu-timing` at those poses, back to back;
bake numbers come from `puck mesh audit --cpu --group "(valley-"`.

| Step | Lane | Needs | Target |
|---|---|---|---|
| 0. Baseline | lead, GPU | GPU free | Six stills on both backends; frame p50/p99 and per-pass GPU ms at each pose; `world.exposure` at each stop after two tour orders on one World. |
| 1. Image meter (landed but its checks) | rendering A | 0 | The grey card reads 118/255 ±3 under its manual EV. Under 0.5% of rock pixels clip in any channel at `valley-rim` and `valley-slope`. A stop's EV agrees within 0.05 whichever stop preceded it (a cut snaps, so the band leaves no history). Composite plus meter costs under 0.2 ms more than the composite alone. |
| 2. Aerial perspective | rendering B | 0 | `SdfAir` and the kernel agree as today. At `valley-draw-distance`, rock 700 m out shifts measurably toward the sky's hue and the rim keeps its silhouette. Changing `farDistance` changes no pixel nearer than it. |
| 3. `TerrainRelief` op | field/maths C | [bake fundamental](open-items.md#bake-proportional-to-change), kernel edits batched | Fixed-point and kernel bakes of one sector match to the triangle (`--cpu` against a stored bake). The exact noise result is pinned by one Maths law. Sector CPU bake time at the same cell is within 1.5× of today's 24 to 43 s. |
| 4. Relative caps, shape bound, tiles | authoring/bake D | 3 for relief; the bound fix none | A sector bakes at its declared spacing (cell under 1.6 m before tiles). With tiles, demanded triangles at any valley stop stay under today's 2.16 M plus 1.5 M, and the mesh region grows under 150 MB. Warm boot is unchanged; time to the demanded set after a sector edit is under today's sector bake. |
| 5. Certified field contact | physics E | [certified field contact](open-items.md#certified-field-contact), with 3's relief | One certified solve for both contact paths closes R4 to R8; the valley witnesses report 0 red, feet stay within skin plus bake error on the 58° face, creep is 0, and replay agrees after deliberate corrections are re-recorded. No coarser contact cut is allowed. Still open: the 58° face, the rim's creep and clearance close to a slope (packet). |
| 6. Material layers, deposit migrated | rendering/shading F | none | Views pass GPU ms rises at most 10% at `valley-slope`. The register count on Direct3D 12 does not exceed today's. The weathering exhibits' stills are unchanged within the 2% threshold. |
| 7. Far shadows | rendering A, after 1 | 0 | `valley-rim` shows ridge shadows on the far walls. The shadow passes cost at most 0.5 ms more with the sun still. |
| 8. The sets and the valley | content G | 3, 4, 6 (2 for the haze presets) | Both sets compile and lint clean under `--strict`. Six stills demonstrate each set; the valley wears the painterly set under the owner's ruling. |

Steps 1, 2, 6, and 4's bound fix can run in parallel. Step 3's kernel edit is
batched behind the bake fundamental. Step 5 belongs to the certified-contact
packet; terrain integration and tiles follow 3, 7 follows 1, and 8 closes.
Steps 1, 2, 6 and 7 need windowed legs on both backends; 3, 4 and 5 are CPU
work until their stills.

## Risks and owner calls

Risks:

- **Bake identity churn.** Step 3 edits the field kernel, which rebakes every
  mesh, and a cold bake of about two thousand groups already takes several
  times the one-minute bar on both backends.
  Batch step 3 behind the
  [bake fundamental](open-items.md#bake-proportional-to-change) and land kernel
  edits together. A relief edit afterwards rebakes its own tiles, nearest first.
- **Field contact cost.** At the rim the certified solve reads the field a
  few dozen times a tick, each read a few samples of six octaves, and an
  identical-box cache spares most of its box-bound queries; relief contact is a
  small share of a headless tick. The next cut is the job system's parallel
  contact work.
  Contact keeps every geometric octave and its declared precision.
- **Loose fields.** Warp and amplitude raise the step factor, slowing the bake
  and the sweep; the erosion mask and relative cap hold it.
- **Views-pass registers** (168 on Direct3D 12 drove paints out of the
  material's live range): layers load inside their loop.
- **GI ignores layers:** snow bounces rock colour.
- **Tile seams:** skirts can show at grazing angles from the rim.

Owner calls:

- The valley wears the painterly set; step 8 demonstrates the ruled direction.
- Whether the "pale, bright" +2.5 EV compensation stays once the meter reads
  the image.
- How strongly distance fades toward blue (each set's haze is a proposal).
- Decided by the owner: the time of day varies and the sun's direction is
  authored. The valley's light runs through an authored cycle, never one fixed
  sun, so the image meter, GI proportional to change and the far cascade are
  measured across the cycle. That is part of the lighting-direction pass the
  lighting packet holds.
