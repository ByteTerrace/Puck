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
| Shadows | Partial | Key-light receiver splits at 16 and 120 m, second light at 40 m, casters reaching 200 m beyond each cascade toward the light (`SdfMeshShadowCascades`, authored as `render.shadowCoverage`); macro terrain shadowing is missing, and stretching the far cascade to the walls costs 25 times its vertices ([illumination step 1](illumination.md#the-build-order)). |
| Depth transport | Exists, to qualify | Reconstruction packs coverage-weighted inverse distance in half lanes for harmonic-mean filtering; single samples already carry float `t`. Longer ranges are qualified, and lanes widened together only if precision runs out. |
| Sky | Exists | `Disc` and `View` layers, stars, clouds, panorama; a `World` infinity view is opaque, while a far view uses geometry coverage. |
| Atmosphere | Partial | A dome seen from the ground (`SdfAtmosphere`, `sdf-atmosphere.hlsli`); no altitude dependence, no spherical shell, no transition to vacuum. |
| Worlds and portals | Partial | Portals are planes; adjacency seams are planar and horizontal, with prior art in the quarantined island root (four neighbours at 132 m); handoff of an arbitrary frame (orientation, up, velocities) is incomplete. |
| Regions | Partial | One physical field topology per world today (the combat and weather decisions widen it). |
| Streaming | Missing | No predictive residency of surface tiles. |

## Phases

The valley itself is built: `worlds/debug/valley.puck` is eight separately
editable terrain sectors from `worlds/standard/terrain.puck` around a clear
floor that keeps every exhibit, with an overlook, a 6000 m draw distance and
local fall recovery in place of the global fall floor. Each sector collides as
one field collider over its masses and relief, so a body stands on the drawn
rock. No heightfield source was added: analytic terrain does not fail the edit
bar or the contact bar, and one is added only if the frame bar's reading at the
valley stops fails. What the walls still lack is the
[terrain pass](terrain-pass.md). The phases that remain keep their numbers,
which other plans cite:

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
4. **Residency and merged world adjacency.** Terrain tiles as residency units,
   admitted and evicted across all views and planned against the authored
   flight speed and braking, then the moons through routed residency. The unit
   and its seam rule are the streaming plan's
   ([the source tile and its seam rule](streaming-and-memory.md#the-source-tile-and-its-seam-rule)),
   built in its [step 10](streaming-and-memory.md#step-10-areas-tiles-then-the-moons);
   this phase is that step read on the valley. Beside it, author an adjacency
   between two worlds of the debug world so a far neighbour's ridges draw from
   its floors beyond the border-crossing band using the geometry the
   neighbour's authority permits onward to this viewer, with no body acquired
   there, feeding [visibility V5's multi-residency instance table](visibility.md#v5-multi-view-and-shadow-work).
   Check: from the valley's rim, test one draw the neighbour admits and one it
   refuses for this viewer (recording any extra view or pass cost); a viewer
   allowed only pictures sees pictures; withdrawing permission while a floor is
   resident drops it; public scenery published into the near world draws for
   everyone; and across the border, a ridge in one world casts its shadow into
   the next while a body hands across in motion.
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
   work and a delayed or refused transfer, at
   [the frame bar](../decisions/rendering.md#the-pipeline-foundation) on every
   bar device and both backends, with edit-to-running inside its bar. More moons
   and content follow it.

## The owner's rulings

- **The valley** is a basin or caldera with relatively close slopes, not far
  away: the walls rise near the clear floor rather than beyond open plains.
  The built valley is sized to that.
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
