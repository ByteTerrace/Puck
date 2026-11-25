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
| Sky | Exists | `Disc` and `View` layers, stars, clouds, panorama. A `View` of another world stays at infinity and fills its region unless it asks to follow the viewer and to draw geometry alone (`InfinityViewSpec.Follows`, `GeometryCoverage`), or to draw the world's far product rather than the world (`InfinityViewSpec.FarProduct`). |
| Atmosphere | Partial | A dome seen from the ground (`SdfAtmosphere`, `sdf-atmosphere.hlsli`) whose fog and haze thin with height (`WorldRenderAirHeight`); a keyed look reads how high the viewer stands through a view clock (`WorldClockView`), so a sky fades to space as she climbs. No spherical shell. |
| Worlds and portals | Partial | Portals are planes; adjacency seams are planar, and the debug world's `sky` seam to the moon is the first live one. A crossing maps a body by a turn about up and carries its yaw and velocity; a body's whole frame (pitch, roll, up, angular velocity) is not carried. A rider crosses behind the body it rides (`WorldMount`). An edge authors whether its two worlds are one place drawn together or two places (`WorldAdjacencyPresentation`). |
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
| One sun | The standard's day cycle on both worlds from one start and rate (`moon-frame.puck`'s shared day) | None |
| The air's height | Keyed sky layers and the height fog; `timeline.clocks` | A view clock (`WorldClockView`): a clock whose phase is how far the viewer stands from a point |
| The ship | A kit and a `fly` program in `worlds/standard/ship.puck`: three holds (`WorldHold`) over the walk program's operations | None |
| The pilot's camera | A camera program in `worlds/standard/ship.puck` (`shipCamera`) | The kit's rider camera (`WorldMount.Camera`), and the ridden body delivered with its rider (`EntitySnapshot.Mount`) |
| The flight as one line | A `routes` row (`worlds/debug/moon-route.puck`) played by the standard's `pilot` seat program | A route's flight step (`WorldRouteStep.Fly`) and a seat program's climb and sink buttons (`WorldSeatProgramOp.Rise`) |
| Boarding | `ControlApplication` and `WorldEngagement.Compose` (possession), `WorldPerceptionAnchor`, the context button's engage probe | The kit's `mount` facet (`WorldMount`): the pilot's own body rides the body it drives, and `mount.board` (`WorldMountBoard`) boards it on a press from beside it |
| The seam | `WorldAdjacency` rows, `WorldAdjacencyOwnership.Sweep`, the transfer escrow | A rider crosses behind the body it rides, and the destination hands it that body to drive again; past an edge authored `Apart` the seat is presented in the neighbour's own scene |
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
- The moon lit in the valley's sky. Both worlds run one day (`moon-frame.puck`), so the moon's lit side is the side
  the valley's sun is on and its phase tells the hour.
- A view clock (`WorldClockView`): a `timeline.clocks` row whose phase is how far the frame's first view stands from
  a point, along an axis or in any direction. The debug world's `altitude` clock keys the day's air, the clouds and
  the stars of space on it (`day.puck`), and the fog thins with height, so the sky at the seam is the sky the moon
  draws past it.
- A rider's camera (`WorldMount.Camera`): the camera program a seat takes in place of the seat rig while it rides or
  drives a body of the kit, on whichever authority simulates it. An authority delivers the body a body rides
  (`EntitySnapshot.Mount`), so a client frames a rider through its mount without that authority's grants. The
  standard's ship authors its own (`shipCamera`).
- A flight as a route (`WorldRouteStep.Fly`, `WorldSeatProgramOp.Rise`): `world.benchmark run moon` plays the round
  trip through seat 1's own controls with the standard's `pilot` seat program (`shipPilot`), boarding included, and
  the controls follow the seat across the seam. On a headless World both chapters arrive.
- An edge that is two places (`WorldAdjacencyPresentation.Apart`): a seat that crossed the sky seam is presented in
  the moon's own scene, so the ship and its pilot are drawn as they look, under the moon's light and exposure.

- What a world shows of itself from far away (`SdfFarProduct`, [decision](../decisions/rendering.md#a-far-viewer-draws-a-worlds-far-product)):
  a sky `view` layer with `farProduct: true` draws the destination's far product, never a residency of the whole
  world. The product is the world's settled static scene, each mesh at its coarsest level, gathered into the cells
  the world authors (`render.farProduct`, `SdfMeshCluster`), built once per document version of the world on a worker
  from a residency that already drew it, and shared by every view of that world. The moon's sky shows the valley
  through it (moon.puck's `debug` layer; the debug world asks four-metre cells and leaves out parts under two
  metres): 4,031 static draws and 5.1 million source triangles became 585,568 triangles in 20 MB, and its residency
  bakes nothing. The seam is the hand-over: below it the debug world draws itself, above it the moon's sky draws the
  product, both from the same meshes. A view of a world no residency here holds draws it whole until a product is
  built from that view's own residency, then swaps to it.

- A value that follows several clocks: a keyed scalar's key may hold keys of its own on another clock
  (`BindableScalar`, `IWorldKeyTrack.NestedAt`), so the value is a table over every clock its keys nest, and a member
  that keys a field itself keeps its keys over a section's keys on that field (`WorldRenderKeys`). The valley's exposure
  floor and key are keyed on `altitude`, their ground keys holding the day's keys and their seam key the moon's, and
  its meter's dark cut (`render.exposure.low`) rises to the moon's seven tenths, so a black sky never pulls the meter
  down (hall.puck). One star field answers both the night and the height (`day.puck`'s `starsByHeight`). The invariant
  is that every reader resolves a nested key through one path, `WorldStateMirror`'s keyed read, and every clock the
  keys nest is noted for re-resolution (`WorldClockReads.Note`); the validator judges every nested track's clock and
  times as a keyed value of its own (`WorldKeyedValues`) and every literal it holds against the field's domain. A rate
  and an ordered set refuse nested keys, since neither has a sound reading of two clocks.

- A held pace climbs as it travels (`WorldBody.VerticalThrust`): a hold's thrust reads the kit's resolved move speed
  with its held pace, as its travel and a medium's thrust already did, so the ship's boost lifts it at four times its
  cruise and the route's 5,900 m climb takes about a quarter of the time it did. One function serves every bond.
- Jetpack flight (`worlds/standard/abilities.puck`'s `jetpack`): thrust, hover and boost as two hold rows and the
  `flight` action an author expands into any kit, bound through the standard's `flight` channel. The Moth's flight
  mode is that template (`avatars/moth-jetpack.puck`), no longer her own rows.
- A space mode (`ship.puck`'s `shipSpace` and `shipCamera`'s `spaceRig`): a body is `Weightless` where a world's
  gravity field no longer pulls (`ActionFact.Weightless`, the same one metre a second squared under which a body keeps
  the up it had), the ship's kit marks its `space` register by it, and a world selects the space kit by that register
  (`craft-form.puck`): a ship that coasts in vacuum, pitches further and frames its pilot from a longer boom behind a
  wider lens. The engine supplies only the fact; the medium's motion and camera are the standard's data.

Left open, in the order they matter to the trip:

1. **The crossing still holds the frame thread, and not for the sky view.** The valley now hangs in the moon's sky
   from the debug world's far product (built, below). On a windowed Direct3D 12 trip
   (`world.benchmark run moon`) the far view's residency (`routed$<moon>$sky$debug`) was planned at boot, held zero
   bake groups and handed zero meshes, and named 11.9 MB of the shared mesh region, against 398 MB for the debug
   world's own residency (`world.residency`'s `groups`, `handed` and `region-named`). The frames around the crossing
   still ran long produce iterations, and two other residencies start there: the moon's routed residency (gap 9) and
   `routed$session$18$0$shared`, a second scene of the whole debug world (7,163 groups, the count the reverted sky
   view was charged with) that a window in the moon's scene shows. That window's world should be drawn from the
   residency already holding it, or from its far product while its glass is small; the window plumbing is the next
   step, with the same check (`world.residency` before and after the crossing, and the crossing frames' produce in a
   quiet window).
2. **A refused device allocation ends the World.** With the moon's two
   residencies (the painting's window and the sky view) beside the debug world's
   own, the device's memory account refused a mesh region's growth and the
   frame threw (`SdfMeshRegionStore.EnsureCapacity`). A refusal has to
   shed detail, not end the run.
3. **A body across a joined seam is drawn as a catalog rig.** Where an edge
   presents its two worlds as one place, a seat that crossed keeps the boot
   scene's catalog avatar and the neighbour's bodies are rigs inside the
   seam's band (`WorldAdjacencySceneEmitter`), whatever looks they wear. The
   sky seam is authored apart and does not meet it; a ground seam between two
   worlds will. The pools that stamp a creation look read one world's bodies
   (`WorldBodyStampCensus`), so the fix gives the boot scene's pool the
   travelling seat and the band's bodies as stamp sources.
4. **One day across both authorities** is built: the moon's `day` row follows
   the debug world's (`follows`, [cross-world interaction](cross-world.md#1-one-day-across-both-authorities)),
   so writing `day` in the valley moves the moon's sun on the next tick, and
   writing it on the moon is refused. The night crossing no longer lights the
   ship in one frame: the valley's sun rises with height to the moon's full
   strength (hall.puck's `key` light, keyed on `altitude` with the day's sun in its ground keys).
5. **A crossing turns a body about up only.** `WorldFrameIsometry` and the
   adjacency validator admit a yaw, so the two worlds' axes agree and the side of
   the moon that faces the valley has its up pointing down in the shared frame.
   The arrival frame therefore stands at the far pole. Phase 6's whole frame
   (orientation, up, angular velocity) lets a frame stand anywhere and lets a
   banked or pitched ship cross as it is.
6. **A door does not take a rider.** A ship flown into a painting crosses
   without its pilot; only an adjacency queues the rider.
7. **The ship has no title.** It crosses as a body of whichever world holds it.
   Custody for a vehicle (who may recall it, what a visited world may do to it)
   waits for the companion plan's roles.
8. **The moon's cell.** The sectors bake at the terrain pass's cell, so the
   ground shows facets underfoot, as the valley's walls do.
9. **The crossing stalls, windowed.** Attributed on a windowed Direct3D 12
    trip (`world.benchmark run moon`'s frames): on the ticks the ship crosses
    into the moon, the frame thread's produce runs 210 to 330 ms for three
    frames while the seat's routed residency of the moon starts (193 groups,
    92 to bake, planned on the frame thread) after no residency of it existed.
    The fix is to start the seam neighbour's routed residency before the
    crossing, while the seat is inside the approach band, so the crossing
    presents a residency already planned (residency motion judgement). Headless,
    two trips stepped every tick across both crossings. The headless stalls the
    route reported came from elsewhere: the run's first ticks after boot, and a
    city tick every few seconds while the city is resident in the debug world,
    whose citizens' goal fields derived on the step thread (290 to 400 ms per
    derivation, from a sampled trace); they now derive over a route graph and
    ahead of their read ([frame production](frame-production.md), step 10).
10. **A re-drive reads the neighbour as it stands.** The tape carries when a
    neighbour delivered, never what it showed (`WorldEventFeed`'s remarks), so
    a re-drive of a tick in which a body met a neighbour's bodies or ground
    across a seam reads them as they are when it runs. The ship's own
    crossing no longer depends on them; two bodies meeting across a seam
    still do. The tape needs the contact band's content: the neighbour's
    bodies within reach of the seam, and its definition's identity.
11. **A re-drive of the tick a seat boards on diverged** in the boot world's
    input: the routed input from the pilot to the ship records the
    population's revision, 2 live and 3 replayed
    (`WorldTick.AppendRoutedInputHash`). Boarding on the ground reproduces
    (`moon-trip-tests.puck`, `puck test --reproduce`); the recorded divergence
    boarded in the air near the seam in the debug world, which no committed
    scenario yet reaches.

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
- **The journey is continuous, planet to atmosphere to space to the moon, and
  movement includes jetpack flight and a space mode** (the owner's direction,
  naming No Man's Sky for the continuous journey and Anthem and Firefall for
  jetpack movement, "but with our fancy tech"). The flight from the valley up
  through the air, into space and down onto a moon has no load or cut at any
  stage, the portal stays a second way in, and both are built on Puck's own
  properties (federated worlds, the shared mesh store, the view clock, the
  rider's mount). Jetpack flight (thrust, hover, boost) is an authored motion
  program in `worlds/standard/`, as the dash is, and a space mode is a seat
  program that changes the camera rig and motion model by altitude or medium,
  never an engine-coded feature. The rock skip exists to show continuous
  transport inside a moving frame, as a submarine carries its crew, and the
  same mount relationship carries riders in ships and stations across a seam.
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
