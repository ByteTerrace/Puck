# The valley, the moons and the flight between

The owner's direction: the gallery and its surroundings sit in a relatively
deep valley, under a sky with moons that carry portals and can be reached by
flying up through the atmosphere into space. Phases 2 onward wait for
the [terrain pass](terrain-pass.md) and the
[engine job system](open-items.md#engine-job-system). Each phase proves its
work in the debug world; quarantined worlds stay quarantined.

## What exists, in part or whole

| Area | State | Evidence and limit |
|---|---|---|
| Fixed-point positions | Exists | `FixedPosition` origins with bounded fixed local coordinates; the presentation path narrows to float without subtracting an exact render origin, so precision falls off with distance from the world origin. |
| Static field contact | Exists, with admitted subsets | `GridCellSize` defaults to 0, which means exact evaluation with no distance grid (`WorldCollider.cs`); an oversized grid declines and falls back rather than throwing (`SdfDistanceGrid.TryCoverBox`). Solid placements skip authored sweeps and detail shapes (`CreationStampEmitter.EmitFixed`) and refuse non-detail revolve lifts (`CreationShapeLaw`), so walkable terrain is authored from admitted shapes. |
| Gravity | Partial | Authored planet sources by surface gravity and radius and local gravity areas exist (`WorldGravity`); wide arithmetic at planetary distances needs widening and unrounded composition. |
| Changing up | Exists | `WorldBodyUpPolicy` (Ambient, SurfaceFollowing) with the separate `GradientDerivedUp` contact requirement. |
| Flight | Partial | The Moth's Free and Lift motion (`avatars/moth.puck`) is body-frame 6DOF free integration by design, with no ground pin or gravity; assisted leveling and cruise are new authored policy, not a defect. `World` and `Heading` intent are composed correctly and must not be swapped. Grounding facts are written under every motion program; the ownership gate controls only write-back. |
| Terrain bake | Partial | The bake's reach is `max(1, farDistance)`, and an instance's radius is clamped to it, which can clip a huge instance; post-contour chunks do not stream. Coarse sampling is 65 samples on a shape's longest axis with conditional refinement (4, detail 8). |
| Draw distance | Partial | `farDistance` is capped at 8192 m (`WorldRenderDefaults.MaxFarDistance`); haze extinction is `-log(1 - haze) / farDistance`, which couples haze to draw distance. |
| Shadows | Partial | Key-light receiver splits at 16 and 120 m, second light at 40 m, casters reaching 200 m beyond each cascade toward the light (`SdfMeshShadowCascades`, authored as `render.shadowCoverage`); macro terrain shadowing is missing, and stretching the far cascade to the walls costs 25 times its vertices ([illumination step 1](illumination.md#step-1-status)). |
| Depth transport | Exists, to qualify | Reconstruction packs coverage-weighted inverse distance in half lanes for harmonic-mean filtering; single samples already carry float `t`. Longer ranges are qualified, and lanes widened together only if precision runs out. |
| Sky | Exists | `Disc` and `View` layers, stars, clouds, panorama; a `World` infinity view is opaque, while a far view uses geometry coverage. |
| Atmosphere | Partial | A dome seen from the ground (`SdfAtmosphere`, `sdf-atmosphere.hlsli`); no altitude dependence, no spherical shell, no transition to vacuum. |
| Worlds and portals | Partial | Portals are planes; adjacency seams are planar and horizontal, with prior art in the quarantined island root (four neighbours at 132 m); handoff of an arbitrary frame (orientation, up, velocities) is incomplete. |
| Regions | Partial | One physical field topology per world today (the combat and weather decisions widen it). |
| Streaming | Missing | No predictive residency of surface tiles. |

## Phases

0. **Baseline.** Drive the Moth's existing flight, portal round trips and the
   gallery routes; record whole-frame time, body, mesh, source and staging
   counts, cold and warm bake work, and a real C# and HLSL edit-to-running
   time.
1. **The valley.** Keep the gallery precinct at y 0 on the floor of a basin or
   caldera with relatively close slopes (provisionally 250 to 450 m of relief,
   sized to keep the walls near), a clear floor about 400 m across that
   keeps every exhibit, aperture, the studio, the camera course, the devices
   and the rate range, and reserves the combat yard (north) and the climate wing
   (east). Merge the old slab edge into the terrain, add an overlook, and
   replace the global fall floor with local recovery. Terrain uses the current
   SDF, contact and mesh systems in eight separately editable macro sectors
   with local detail; a heightfield source is added only if measured analytic
   terrain fails the frame or edit bar. Draw distance starts at 6000 m inside the
   8192 m cap, haze is decoupled from draw distance, macro terrain shadows are
   added, and displayed surface error stays inside the one-pixel rule with its
   hysteresis below one pixel.
2. **One real moon, then the second.** A moon source world with a surface arrival and a return
   portal, through the existing references, destinations and sessions. The
   valley sky shows that same world as a `View` layer with an authored coverage
   mode and intentional moon lighting. One moon is finished and measured before
   more.
3. **Exact frames for finite bodies.** One centre, radius, frame and source
   relationship gives the moon's sky image, physical placement, gravity,
   apparent size, phase and approach camera. The precision boundary moves: exact
   frame and local pose travel through snapshots, disclosure and transfer, and
   the renderer subtracts an exact render origin before narrowing to float,
   for interpolation, cameras, views, shading, shadows, GI and picking. Gravity
   arithmetic is widened. The 8192 m cap rises only after the precision work.
4. **Residency.** Canonical bounded source tiles with shared borders and coarse
   exteriors, admitted and evicted across all views, planned against the
   authored flight speed and braking; tile size is measured, not fixed. This
   consumes [residency step 7](../decisions/residency.md#order-of-work)'s source
   tiles and the job system's scheduler.
5. **Atmosphere, clouds and flight.** A body-frame spherical-shell atmosphere
   with surface occlusion, keeping local valley mist; bounded clouds before
   flying through them; exposure continuous into vacuum. Flight reuses the
   Moth's free flight unassisted (the owner's ruling); assisted cruise and
   touchdown are a later pass, authored as a standard ability template in
   `worlds/standard/` that the Moth refines; the engine executes its motion
   program generically.
6. **Approach and handoff.** The ascent stays under the parent authority;
   the moon's far image and its detailed surface depict one surface with no
   duplicate silhouette; the frame handoff carries orientation, up, linear and
   angular velocity, integrator remainder, held input, identity and history
   exactly once.
7. **The round trip.** Gallery, rim, clouds, the atmosphere's limb, vacuum, moon
   approach, landing, portal, gallery, and the reverse by flight, including cold
   work and a delayed or refused transfer, at a steady 60 FPS on the RTX 4070 at
   the highest settings, both backends, edit-to-running under 30 s. More moons
   and content follow it. The debug world also proves the highest-quality
   target of 60 FPS on RTX 2060 and 45 FPS (every frame under 22.2 ms) on
   Steam Deck.

## The build so far

Headless and CPU counts are the evidence here. The windowed half (GPU bake,
frame time, the HLSL edit, Vulkan) is the `valley` group of the sweep's tour
(`worlds/debug/sweep.json`), each stop naming what its still must show.

### Phase 0: before the valley

The debug world's cold bake with every group on the CPU (`puck mesh audit
--cpu`) takes about eight minutes for 13 M triangles, so the one-minute bar is
missed by roughly eight times. The rule budget is read against
`RuleCapacity.MaxWorkUnitsPerTick`, and the world's idle tick is read with
`puck bench world`. Warm GPU bake work, mesh and source counts at the window,
and the HLSL edit are the window's to read: a headless World builds no
renderer.

### Phase 1: the valley as built

`worlds/debug/valley.puck`: eight sectors, each one creation placed at the
centre and turned to its bearing, built from `worlds/standard/terrain.puck`
(`terrainMass`, `terrainFloorCut`, `terrainRelief`): a main superellipsoid
mass and two shoulders sunk below the floor plane, cut under it, and
6-octave fBm relief of 4 m amplitude. The feet stand 205 to 230 m from the
origin, the summits 300 to 430 m high; the floor (`lawn.puck`'s `floor`) is a
740 m disc that runs on under every wall, so the old slab edge is gone. The
overlook is a deck at 345 m on the south-west face. Draw distance is 6000 m
and the clear day's fog 0.0006 a metre, so the rim keeps about two thirds of
its light. A body under y −40 is stood on the floor 190 m out below the sector
whose wedge it fell in (`fall-recovery`).

Each sector bakes alone offline on the CPU (`puck mesh audit --cpu --group
"(valley-<sector>)"`) in well under a minute with no discarded work, at hundreds of thousands of
triangles; the floor and the overlook are small. The eight sectors
add 2.16 M triangles to the world's 13.09 M. Every sector's field reads well
under a distance (the anisotropic ellipsoid gauge and the noise scope's step
clamp), which the bake measures against and the audit reports as "loose". The
worst clusters are folds where the relief crosses the floor cut under the
floor, out of sight; the visible ones are shading normals up to 60 degrees off
on the steepest relief, for the window to judge.

Each sector collides as one field collider over its masses and relief
(`FixedStaticColliderKind.Field`), so the Moth stands on the drawn rock, and at
rest `body.where` reads no change over 60 ticks on the steepest face, a gentle
slope or the summit. A body on a slope costs about 50 convex (GJK and EPA)
queries a tick; a body clear of the walls costs none, and the colliders
debug view (`world.debug-view report colliders`) finds no red or amber
witnesses. A relieved six-octave field sample costs about ten times an
unrelieved mass sample, and the world's other bodies prove themselves clear by
envelope reads with no relieved sample.

The bake reaches these cell sizes because a scoped creation's relief declares
its detail (`CreationNoiseDocument.DetailSpacing`: a quarter of the wavelength
of the finest octave carrying a sixteenth of the amplitude) and the floor cut
keeps the masses' buried halves off the lattice; without either a sector bakes
at coarse cells and the relief aliases away. The gallery's NOISE exhibit, the
world's other scoped noise creation, bakes at a third of its former cell size
by the same rule. What the terrain still lacks is the
[terrain packet](open-items.md#the-valleys-terrain).

**Heightfield source: not added.** Analytic terrain does not fail the edit bar
(one sector rebakes alone, in well under a minute on the CPU, and the GPU bake is the
window's to time) or the contact bar. The frame bar is read in the window; a
heightfield is added only if that reading fails.

## The owner's rulings

- **The valley** is a basin or caldera with relatively close slopes, not far
  away: the walls rise near the clear floor rather than beyond open plains.
  Phase 1 sizes the relief and floor to that.
- **The Nexus island is not part of this design yet.** The valley stands on its
  own.
- **Two moons** that look relatively close in the sky, one lighter and one
  darker, both pocked by asteroid craters; no rings yet. Phase 2 finishes one
  and measures it before the second.
- **Flight is the Moth's wings** for now; no craft, and no assistance in this
  wave. Assisted flight (horizon leveling, cruise) is a later pass, so phase 5
  reuses the Moth's existing free flight as it is. The later assistance is a
  standard ability template that the Moth refines.
- **Landing is open,** anywhere a body can stand, as it is now.
- **Each moon is a federated world of its own**, beside the debug world rather
  than inside it: "it actually makes sense in Puck for a moon and the debug
  world to be federated worlds", whether both run in one process or on two
  hosts. Crossing to a moon exercises custody, what a visited world may write,
  and seams ([worlds and federation](../decisions/worlds-and-federation.md)).
  The moons are places to explore, not backdrops.
- **The valley as built falls short** (the owner, walking it): its walls read as
  soft white masses with metre-scale grain, without material, with clipped
  exposure and no sense of distance. It needs a real terrain pass, not tuning:
  terrain-scale noise (ridged and domain-warped, octaves from kilometres to
  centimetres), slope and altitude materials, contact on the drawn relief, the
  exposure meter fixed at its cause, and distance haze, each a generic engine
  capability. The standard package ships **two terrain material sets an author
  chooses, a natural alpine one and a stylized painterly one**; the valley uses
  the painterly one, after the owner's selected Canyon workshop direction (warm
  terracotta and peach strata, green ledges, a grassy floor;
  [references](../game/art/valley-and-gallery.md)). The design and its order of work are
  [the terrain pass](terrain-pass.md).

Still open, for when the work reaches them: whether the moons dominate the day
or the night, and how long the ascent is.
