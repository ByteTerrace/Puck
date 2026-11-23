# The valley, the moons and the flight between

The owner's direction: the gallery and its surroundings sit in a relatively
deep valley, under a sky with moons that carry portals and can be reached by
flying up through the atmosphere into space, by ship or through a portal. Phases 2 onward wait for
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
| Flight | Partial | The Moth's Free and Lift motion (`avatars/moth.puck`) is body-frame 6DOF free integration by design, with no ground pin or gravity. The standard's ship (`worlds/standard/ship.puck`) authors the assisted kind: a `fly` program whose holds level it against the world's pull, settle it near ground and hover it elsewhere. `World` and `Heading` intent are composed correctly and must not be swapped. Grounding facts are written under every motion program; the ownership gate controls only write-back. |
| Terrain bake | Partial | The bake's reach is `max(1, farDistance)`, and an instance's radius is clamped to it, which can clip a huge instance; post-contour chunks do not stream. Coarse sampling is 65 samples on a shape's longest axis with conditional refinement (4, detail 8). |
| Draw distance | Partial | `farDistance` is capped at 8192 m (`WorldRenderDefaults.MaxFarDistance`); haze extinction is `-log(1 - haze) / farDistance`, which couples haze to draw distance. |
| Shadows | Partial | Key-light receiver splits at 16 and 120 m, second light at 40 m, casters reaching 200 m beyond each cascade toward the light (`SdfMeshShadowCascades`, authored as `render.shadowCoverage`); macro terrain shadowing is missing, and stretching the far cascade to the walls costs 25 times its vertices ([illumination step 1](illumination.md#the-build-order)). |
| Depth transport | Exists, to qualify | Reconstruction packs coverage-weighted inverse distance in half lanes for harmonic-mean filtering; single samples already carry float `t`. Longer ranges are qualified, and lanes widened together only if precision runs out. |
| Sky | Exists | `Disc` and `View` layers, stars, clouds, panorama. A `View` of another world stays at infinity and fills its region unless it asks to follow the viewer and to draw geometry alone (`InfinityViewSpec.Follows`, `GeometryCoverage`); a far view always uses geometry coverage. |
| Atmosphere | Partial | A dome seen from the ground (`SdfAtmosphere`, `sdf-atmosphere.hlsli`); no altitude dependence, no spherical shell, no transition to vacuum. |
| Worlds and portals | Partial | Portals are planes; adjacency seams are planar, and the debug world's `sky` seam to the moon is the first live one. A crossing maps a body by a turn about up and carries its yaw and velocity; a body's whole frame (pitch, roll, up, angular velocity) is not carried. A rider crosses behind the body it rides (`WorldMount`). |
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

## The trip to the moon

The owner's request: "Can someone fly to the moon yet? We need a portal there
AND we need to be able to fly in a ship from debug land to debug moon.
Implement what is needed to make each perspective as seamless/natural as
possible." This section is that feature's plan: what it is made of, how each
stage is checked, the order it is built in and the engine gaps on the way.
The phases above stay the long plan; this feature builds the parts of phases
2, 3, 5 and 6 that one round trip needs.

### What it is made of

The moon is a world of its own, `worlds/debug/moon.puck`: a cratered sphere
about its origin, with a pull toward its centre inside a reach and none beyond
it. The debug world and the moon share one frame up to a shift
(`moon-frame.puck`'s `moonCentre`), and that one relation places everything:
the moon's image in the valley's sky, the valley's image in the moon's sky,
and both sides of the seam a ship crosses. Each world stays within a few
kilometres of its own origin, so presentation float is exact enough everywhere
without a render origin: the seam is where the coordinates change, which is
what a federated world is for.

| Part | Built from | New engine work |
|---|---|---|
| The moon's ground | The standard terrain program with a radial mode; eight octant sectors of one description (`moon-surface.puck`) | None |
| Its pull and a body's up | `WorldGravityArea` (a radial sphere), `WorldBodyUpPolicy`, `WorldUpTurnRates` | None |
| The portal | The painting wing's `paintingFace` and the shared `arrivalFrame`; references, destinations and sessions | None |
| The moon in the sky | `WorldRenderSkyLayer.View` and the infinity-view machinery (`InfinityViewSpec`, `InfinityViewFit`) | A view that moves with the viewer, bounds itself by a sphere and draws geometry alone |
| The ship | A kit and a `fly` program in `worlds/standard/ship.puck`: three holds (`WorldHold`) over the walk program's operations | None |
| Boarding | `ControlApplication` and `WorldEngagement.Compose` (possession), `WorldPerceptionAnchor`, the context button's engage probe | The kit's `mount` facet (`WorldMount`): the pilot's own body rides the body it drives, and `mount.board` (`WorldMountBoard`) boards it on a press from beside it |
| The seam | `WorldAdjacency` rows, `WorldAdjacencyOwnership.Sweep`, the transfer escrow | A rider crosses behind the body it rides, and the destination hands it that body to drive again |
| A ship that stays a ship | `kits.assignment.select` and `looks.assignment.select` by a body register, as the Moth's suit crosses | None |

### Stages and how each is checked

| Stage | Done when | Check |
|---|---|---|
| The moon | A body stands anywhere on the sphere and keeps its feet toward the centre | `body.pose` above four points far apart, then `body.where`: grounded, at the sphere's radius give or take the relief |
| The portal | Walking into the moon painting arrives out of the frame at the pole, and walking back into the frame arrives out of the painting | The two `world.transfer` lines and the instance `body.where` names |
| The moon in the sky | From the valley floor the moon hangs where `moonCentre` puts it, drawn from the moon's own world and lit by its sun, with the valley's sky round it | A capture from the launch field; the `sky$moon` instance in `world.counters gpu` |
| The ship | A seat boards, lifts off, cruises, and letting go near the ground lands it | `body.where` on the ship and the seat (`anchor=body:<ship>`), `body.hold` reading `landed`, `touchdown` and `cruise` |
| The seam | The ship and its pilot cross in one tick, land at the same point of the shared frame, and the pilot drives the ship again there | One `world.adjacency` line per body, and the seat's `world.transfer` line reading `driving body:<n> (kit 'ship')` |
| The landing | Inside the moon's reach the ship rolls to stand against the pull and settles on the ground | `body.where` on the seat: grounded at the sphere's radius, its up along the radius |
| The way home | The same flight downward crosses the seam back and lands in the valley | The same lines, reversed |
| Each perspective | Walker: no load screen, the destination shows in the glass, held input carries. Pilot: the camera follows the ship across the seam with no jump. Watcher: the moon in the sky is the place the others stand on | Presented captures in motion across each crossing, on Direct3D 12 and one pass on Vulkan |

### Engine gaps on the path

Built by this feature:

- A rider (`WorldMount`, `WorldBody.FollowMount`, `WorldEngagement.ResolveMounts`).
- A rider's crossing (`WorldInstanceHost.RiddenMount`, `WorldTransferCommitMember.Drives`).
- A view of a place at a distance (`InfinityViewSpec.Follows`, `Bound` and `GeometryCoverage`).
- A collider read from a creation in an adjacency's reach proof (`WorldAdjacencyPolicy.TryBodyReach`).
- A hold's probe from the middle of the collider along the body's own up, so a hold takes ground all round a
  sphere (`WorldBody`'s hold probe origin).
- Boarding on a seat's own press (`WorldMountBoard`, through the engage probe every screen and tabletop uses).
- A landing across a seam that puts a body where it left: its carried registers select its kit before its segment
  is swept, the sweep meets no neighbour body (`WorldPopulation.LandingArrival`), every arrival pins the tick's
  adjacency input again (`WorldServer.PrepareAdjacencyContacts`), and a rider takes its seat as it lands
  (`WorldPopulation.SeatRider`).
- An adjacency that resolves its neighbours at a host boundary (`WorldAdjacencyFields.ResolveNeighbours`), so two
  adjacent worlds step in one wave.

Left open, in the order they matter to the trip:

1. **The moon in the sky is unlit.** The view draws the moon where the frame
   puts it, with the valley's sky round it, as a black disc: the sun that
   lights the same ground for a body standing on it does not light it in the
   sky view's instance. Publishing the view's image unexposed
   (`SdfFrameBlock`'s expose share for a geometry-only view) did not change
   it, so the image itself holds no light. Untried: the layer's
   `shadows: false`, and the lights the sky view's instance is handed. The
   check is a capture from the launch field in which the moon's sunlit side
   is as bright as the valley's sunlit walls.
2. **The valley is not in the moon's sky.** A view of the debug world from the
   moon draws that whole world again, every exhibit included, as one more
   residency. What a world shows of itself from far away has to be a product of
   its own (phase 4's scenery published for a far viewer) before the moon's sky
   can hold the valley.
3. **A refused device allocation ends the World.** With the moon's two
   residencies (the painting's window and the sky view) beside the debug world's
   own, the device's memory account refused a mesh region's growth and the
   frame threw (`SdfWorldTables.EnsureMeshRegionCapacity`). A refusal has to
   shed detail, not end the run.
4. **The pilot has no camera of her own.** The context button boards and
   leaves the ship, but the camera is the Moth's chase rig on the ship: a
   4.2 m boom on a 3 m hull, which fills the frame. A rig the seat takes while
   it drives a body (a longer boom, the hull's pivot) is the possession work
   the city's driver needs too ([the city plan](city.md)).
5. **The air has no altitude.** The valley's sky is the same blue at the seam
   as on the floor, and the moon's sky is black, so the sky changes in one frame
   at the crossing. Phase 5's shell atmosphere is the fix: the air's layers and
   the fog thin with the viewer's height, and exposure follows. The authored
   surface is a third kind of `timeline.clocks` row beside the tick clock and
   the state clock: a view clock, whose phase is how far the viewer stands
   from a point, along an axis for a flat world or in any direction for a
   round one, over an authored span. Every keyed look already reads a clock
   through `WorldKeyResolver` and the state mirror, so a sky layer's opacity,
   a star field's luminance, the fog and the exposure key on altitude as they
   key on the day, and a last key eased `Step` holds past the span. One
   environment resolves a frame (`WorldEnvironmentResolve`), so the clock
   reads the frame's first view; seats sharing a split screen share it.
6. **A crossing turns a body about up only.** `WorldFrameIsometry` and the
   adjacency validator admit a yaw, so the two worlds' axes agree and the side of
   the moon that faces the valley has its up pointing down in the shared frame.
   The arrival frame therefore stands at the far pole. Phase 6's whole frame
   (orientation, up, angular velocity) lets a frame stand anywhere and lets a
   banked or pitched ship cross as it is.
7. **A door does not take a rider.** A ship flown into a painting crosses
   without its pilot; only an adjacency queues the rider.
8. **The ship has no title.** It crosses as a body of whichever world holds it.
   Custody for a vehicle (who may recall it, what a visited world may do to it)
   waits for the companion plan's roles.
9. **The moon's cell.** The sectors bake at the terrain pass's cell, so the
   ground shows facets underfoot, as the valley's walls do.
10. **The pilot's view past the seam.** Across the seam the window shows a
    grey gradient and the pilot and ship as bare part primitives, where a
    walker out of the painting sees the moon's ground and sky (and the same
    primitives over the Moth). The crossing also bakes the whole debug world a
    second time, as the moon's neighbour across the seam, and skips eight
    presentations. The neighbour a seam shows has to be the far product of
    gap 2, not the world again.
11. **A re-drive reads the neighbour as it stands.** The tape carries when a
    neighbour delivered, never what it showed (`WorldEventFeed`'s remarks), so
    a re-drive of a tick in which a body met a neighbour's bodies or ground
    across a seam reads them as they are when it runs. The ship's own
    crossing no longer depends on them; two bodies meeting across a seam
    still do. The tape needs the contact band's content: the neighbour's
    bodies within reach of the seam, and its definition's identity.
12. **A re-drive of the tick a seat boards on diverges** in the boot world's
    input: the routed input from the pilot to the ship records the
    population's revision, 2 live and 3 replayed
    (`WorldTick.AppendRoutedInputHash`). Until it reproduces, the trip has no
    committed scenario.
13. **A scripted flight ends at a crossing.** `body.fly` holds its input for
    the body's source world only, so a scripted trip is one line per leg.

## The owner's rulings

- **The valley** is a basin or caldera with relatively close slopes, not far
  away: the walls rise near the clear floor rather than beyond open plains.
  The built valley is sized to that.
- **The Nexus island is not part of this design yet.** The valley stands on its
  own.
- **Two moons** that look relatively close in the sky, one lighter and one
  darker, both pocked by asteroid craters; no rings yet. Phase 2 finishes one
  and measures it before the second.
- **A ship flies from the valley to the moon, and a portal reaches it too**
  (the owner's ruling, superseding "the Moth's wings, no craft"): a player
  can fly a ship from the debug world up through the atmosphere to the debug
  moon and land, and can also step through a portal to it. Each way in is made
  as seamless and natural as its perspective allows (the owner: "Implement what
  is needed to make each perspective as seamless/natural as possible"), in
  keeping with [one world, many perspectives](../decisions/play.md#the-game).
  The ship is a real engine body a seat possesses; its flight is an authored
  motion program in `worlds/standard/`, and the Moth's own free flight stays.
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
