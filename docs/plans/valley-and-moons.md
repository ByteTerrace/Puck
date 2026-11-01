# The valley, the moons and the flight between

The owner's direction: the gallery and its surroundings sit in a relatively
deep valley, under a sky with moons that carry portals and can be reached by
flying up through the atmosphere into space. Codex and Antigravity reviewed the
existing engine independently, reconciled their inventories against the code
over three rounds, and converged on the plan below. Phases 2 onward wait for
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
| Shadows | Partial | Key-light receiver splits at 16 and 120 m, second light at 40 m, casters reaching 200 m beyond each cascade toward the light (`SdfMeshShadowCascades`); macro terrain shadowing is missing. |
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

## Measured

Headless and CPU numbers, taken on the lead machine while five other lanes
built and tested beside them, so wall times are loaded and counts are the
evidence. The windowed half (GPU bake, frame time, the HLSL edit, Vulkan) is
the `valley` group of the sweep's tour (`worlds/debug/sweep.json`), each stop
naming what its still must show.

### Phase 0: before the valley

| Reading | Value |
|---|---|
| Composition | 38 documents (16 shared), 305 creations, 1047 placements, 13 screens |
| Contact | analytic provider, 512 placement colliders, skin 0.02 m, slope 60 degrees |
| Headless simulate per iteration (`host.frames 300`) | p50 8.9 ms, avg 17.3 ms (the machines' emulation is most of it) |
| `puck bench world` | idle tick 13.3 ms median, paced tick 3.9 ms, 488 sleeping rigid bodies, 14.1 body visits a tick |
| Rules | 735 rules, 2,783,787 of 4,000,000 work units a tick |
| Cold bake, every group on the CPU (`puck mesh audit --cpu`) | 987 groups, 791 meshes, 476 M samples, 4.1 M refined cells, 13.09 M triangles (level 1 1.56 M, level 2 0.97 M), 0 discarded, 477 s |
| Mesh audit over the same bake | 774 groups audited and 213 not (the CPU audit then read only unchunked groups); 53,965 open and 84 non-manifold edges summed over the audited groups |

Warm GPU bake work, mesh and source counts at the window, and the HLSL edit,
are the window's to read: a headless World builds no renderer.

### Phase 1: the valley as built

`worlds/debug/valley.puck`: eight sectors, each one creation placed at the
centre and turned to its bearing, built from `worlds/standard/terrain.puck`
(`terrainMass`, `terrainFloorCut`, `terrainRelief`): a main superellipsoid
mass and two shoulders sunk 30% below the floor plane, cut 10 m under it, and
6-octave fBm relief of 4 m amplitude at 0.02 cycles a metre. The feet stand
205 to 230 m from the origin, the summits 300 to 430 m high; the floor
(`debug.puck`'s `floor`) is a 740 m disc that runs on under every wall, so the
old slab edge is gone. The overlook is a deck at 345 m on the south-west
face. Draw distance is 6000 m and the clear day's fog 0.0006 a metre, so the
rim keeps about two thirds of its light. A body under y −40 is stood on the
floor 190 m out below the sector whose wedge it fell in (`fall-recovery`),
checked at five points including the origin.

Per sector, baked offline on the CPU (`puck mesh audit --cpu --group
"(valley-<sector>)"`); samples are the sector's own past the 35.8 M every
other group's first lattice answers empty, seconds the whole run's.

| Sector | Peak m | Samples | Refined cells | Triangles | Level 1 / 2 | Cell mm | Off-surface p99 mm | Face normal p50 / p99 deg | Folds / flipped | Open / non-manifold | Seconds |
|---|---|---|---|---|---|---|---|---|---|---|---|
| north | 380 | 6.83 M | 52,309 | 296,724 | 153,062 / 42,844 | 2791 | 36.6 | 4.4 / 16.4 | 127 / 52 | 0 / 0 | 43.0 |
| north-east | 330 | 5.50 M | 41,117 | 241,460 | 122,034 / 33,124 | 2844 | 37.2 | 4.7 / 18.0 | 77 / 42 | 0 / 0 | 33.8 |
| east | 300 | 5.69 M | 41,827 | 257,760 | 124,480 / 31,042 | 2811 | 36.9 | 4.2 / 14.8 | 145 / 71 | 0 / 0 | 32.1 |
| south-east | 350 | 5.65 M | 43,000 | 243,376 | 131,462 / 39,132 | 2999 | 40.3 | 5.0 / 19.3 | 168 / 75 | 0 / 0 | 28.8 |
| south | 430 | 7.43 M | 56,913 | 321,524 | 172,552 / 51,876 | 2887 | 37.9 | 4.6 / 18.3 | 179 / 76 | 0 / 0 | 38.6 |
| south-west | 360 | 6.17 M | 47,207 | 265,928 | 138,638 / 39,200 | 2903 | 38.6 | 4.8 / 18.2 | 170 / 84 | 0 / 1 | 31.7 |
| west | 300 | 6.00 M | 44,492 | 268,646 | 129,444 / 32,390 | 2672 | 34.7 | 4.2 / 15.0 | 161 / 81 | 0 / 2 | 31.0 |
| north-west | 340 | 6.16 M | 47,033 | 266,288 | 136,104 / 37,712 | 2771 | 36.5 | 4.7 / 18.3 | 154 / 68 | 0 / 0 | 23.7 |
| floor (740 m disc) | | 4.82 M | 24,128 | 36,524 | 5,628 / 5,628 | 3256 | 8.4 | 1.0 / 30.8 | 4 / 12 | 0 / 0 | 13.1 |
| overlook | | 0.91 M | 12,226 | 27,364 | 5,838 / 5,838 | 251 | 8.1 | 0.1 / 10.2 | 4 / 2 | 0 / 0 | 5.9 |

No sector discarded work. Every sector's field reads well under a distance
(slope deficit p50 0.7: the anisotropic ellipsoid gauge and the noise scope's
step clamp), which the bake measures against and the audit reports as
"loose". The worst clusters are folds where the relief crosses the floor cut
10 m under the floor, out of sight; the visible ones are shading normals up
to 60 degrees off on the steepest relief, for the window to judge. The eight
sectors add 2.16 M triangles (level 1 1.11 M, level 2 0.31 M) to the world's
13.09 M.

Contact on the slopes (`world.counters physics.contact`, per tick, the world's
191 other resolves included): a Moth standing on the floor away from the walls
adds no convex query; standing at a wall's foot, 82 convex (GJK and EPA)
queries and 2,172 support evaluations; on a 22 degree slope at 368 m, 49 and
1,066; on the 58 degree face at 258 m, 51 and 1,251; the overlook deck, 41 and
1,067. `world.debug-view report colliders` finds 0 red and 0 amber over 13,832
witnesses. The rule budget rises 112,392 units a tick, to 2,896,179, for the
eight recovery wedges.

Each sector now collides as one field collider over its masses and relief
(`FixedStaticColliderKind.Field`), so the Moth stands on the drawn rock: at the
owner's summit spot (422.65, −5.51) she grounds at 290.70 m where the masses
alone held her at 289.16 m, and at rest `body.where` reads no change over 60
ticks on the 58 degree face, the 22 degree slope or the summit. Its price (headless, a loaded
machine, per tick): the Moth standing on the north rim spends about 70 field
queries and 422 relieved-field samples (a six-octave sample costs about 3.4 µs
there, the unrelieved masses alone 0.3 µs), and the world's other bodies about
315 envelope reads that prove them clear with no relieved sample.

The bake reaches these numbers because a scoped creation's relief declares its
detail (`CreationNoiseDocument.DetailSpacing`: a quarter of the wavelength of
the finest octave carrying a sixteenth of the amplitude, 1.56 m here) and the
floor cut keeps the masses' buried halves off the lattice; without either a
sector baked at 20.6 m cells into 10,364 triangles and the relief aliased away.
The gallery's NOISE exhibit, the world's other scoped noise creation, bakes at
6.3 mm instead of 18.7 mm by the same rule: 137,372 triangles instead of
15,936, its face normal p99 8.7 instead of 23.6 degrees.
The CPU audit measures a sector against a fixed-point field that now reads a
514 m superellipsoid radius at full precision (through its Q16 inverse it read
surface points 0.06 to 0.85 m off, with no gradient at a 1 mm probe), and it
reads chunked static unions whole. What the terrain still lacks is the
[terrain packet](open-items.md#the-valleys-terrain).

**Heightfield source: not added.** Measured analytic terrain does not fail the
edit bar (one sector rebakes alone; its CPU bake is 24 to 43 s on a loaded
machine and the GPU bake is the window's to time) or the contact bar (about 50
convex queries a tick for a body on a slope). The frame bar is read in the
window; a heightfield is added only if that reading fails.

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
