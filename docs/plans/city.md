# The city

The owner's feature, owned end to end by the RTX 2060 machine's session (the
[orchestration ruling](../../.claude/skills/orchestration/SKILL.md): one
owner, a definition of done, authoring as first-class lanes). It takes over
the "A town" item of [the debug world at MMO scale](open-items.md#the-debug-world-at-mmo-scale),
moved from the valley into a world of its own.

## What the owner asked for

- **What:** a bustling city, a stress test of how Puck scales with content.
- **Where:** its own world, a separate document federated like the garden and
  the night field, reached through a portal painting in the gallery. Crossing
  teaches worlds without exposition, and the painting shows the city from the
  gallery.
- **Scale:** about 2,000 citizens with daily schedules, about 100 vehicles, a
  market, about 500 lights, and a day and night cycle. Every citizen and
  vehicle runs real engine systems (navigation, motion programs, state rules,
  lights), never an animated prop.
- **Look:** at least the quality of The Wind Waker and Borderlands: the debug
  world's painterly toon treatment and held palette, with authored ink
  outlines and selective hatching. One cohesive look with the gallery. Their
  quality is the bar; their look, characters and assets are never copied.
- **Frame bar:** 60 FPS at the highest quality on the RTX 4070 at 2560 by 1440
  and the RTX 2060 at 1920 by 1080, first with the authority in the same
  process, then against a remote authority. It is expected to miss at first;
  the misses are the engine gaps it exists to find.

Decided with the lead (standing rulings; the owner corrects if not): the
local topology comes first and the remote topology is stage 5's acceptance; a
player bumps and steps around citizens from stage 2, and trades at the market
from stage 3.

### Perspectives

The owner's ruling: different players see and play the same city from
different perspectives, through different game systems that all interact. One
plays it as a city builder from above, another drives a car through it,
another moves among the crowd by possessing a citizen, and shifting between
perspectives is a long-term player unlock. So every citizen and vehicle stays
a real engine body under real systems, never a prop: anything a player might
take over must already run what a player would run.

| Perspective | Built on | To build |
|---|---|---|
| City builder | The city's state rows and rules: anchors, schedules, stock, routes and crossings are cells a rule reads | A top-down camera layout and authored verbs (zoning a lot, placing a stall, routing a loop) that write those rows through the ordinary grant-checked mutations |
| Driver | Vehicles are bodies with a kit, a `route` register and the road network; possessing one stands its producer down and its route rules skip it | A driving camera and bindings |
| A citizen | Citizens are bodies on a schedule; possessing one stands its producer down, the schedule skips it, and released it routes from where it stands | A walking camera and verbs for a possessed citizen |
| Shifting | Rules gated on state | The account's progression rows decide which perspectives a seat may enter |

Possession exists in the engine: a seat's control set names another body
(`{ body:<n> }`), a Drive grant over that body authorizes it, and the seat's
camera, listener and HUD move to the possessed body
([engagement](../../.claude/skills/puck-world/references/engagement.md#possession-swaps-the-perceived-world-not-just-the-driven-body)).
The body's producer stands down while it is possessed or a rule holds it
(`holdProducer`), and rules read both through `producer(each, …)`
([the schema reference](../../src/Puck.World.Schema/README.md#a-producer-stands-down-holds-and-possession)).
A player's car yielding at a crossing is still open: the crossing rule holds a
vehicle's producer, which a possessed vehicle has already stood down.
The perspectives interact through one simulation: a builder's edit changes
where citizens go and vehicles drive, a possessed citizen still holds a
crossing, and a player's car yields at it as the others do. Many players
inhabiting the city's bodies against a remote authority is the MMO-shaped
stress stage 5 measures.

### Zoned districts and platform-generated sections

The owner's ruling, as evidence: "Does the city have residential, commercial,
agricultural, etc districts? Imagine SimCity or Cities Skylines, except with
players who might take possession of NPCs and whatnot. Also remember that
Azure integration can/should be used to generate sections of the world (the
granaries example, expanded)."

The intent, as the rule:
- **The city is a living simulation of zones.** Residential, commercial,
  industrial, agricultural (farms and fields beyond the river), civic and
  port districts are authored zone rows. Homes, jobs, shopping and the
  market's goods come from the zones, so goods flow from farm to granary to
  market to home, with demand, growth and building levels in the manner of a
  city-building game. All of it is Puck rules and programs in
  `worlds/standard`, a generic city simulation any world imports, never a
  city-only engine feature. The builder perspective's verbs zone and build
  through the same rows, and every citizen and vehicle stays possessable
  throughout.
- **Sections of a world are generated from the hosting platform.** The
  [game design](../game/design.md) already holds it: the world is the
  operational twin of the platform that hosts it, and every reading of the
  platform enters as observation rows that a module's placements, bodies and
  rules read, with no cloud noun and no game noun in the engine. A city
  district is generated that way: storage as warehouse stock, traffic as
  vehicle flow, queues as lines at the market, compute as factory activity.
  It changes as the deployment does. Provider independence holds: rows and
  abstractions only, no provider noun in engine or world vocabulary.

What exists for the generated district (read at the head this was written
against; recheck before building):
- An observation reads an approved collection into ordinary state rows and
  nothing else, a resource listing or a metrics series alike
  ([extension configuration](../../src/Puck.World.Server/ExtensionConfiguration.md#observe-a-collection));
  the debug world's [granary board](../../worlds/debug/granaries.md) reads
  storage accounts that way, and the hosting example feeds a `Fixed` row from
  a metrics observation.
- A placement's `deal` mints one child placement per state cell, so a
  warehouse can stand per observed store; an `inhabit` count can be a state
  cell, so vehicle flow or a market queue can follow a metric.
- Host approval lives outside the package (`worlds/hosting`), per world, so
  the city needs its own approved configuration; the hosted deployment's
  verification already carries a `storage-inventory` observation.


Each row cites the code it was read from; recheck it before building on it.

| Need | What the engine has | Limit at city scale |
|---|---|---|
| A federated world behind a painting | `debug.puck` `references`/`destinations` name `garden` and `night`; `paintings.puck` `wallPaintings` rows hang a canvas per destination; `night.puck` is the smallest destination document | `views.nestingDepth: 2` was sized on six paintings; a seventh window onto a crowd adds nested-view cost |
| Many bodies | `placement { inhabit { count, kit, look, source, distribution } }` (`pond.puck` `pondFlocks`); kits with `autonomy { motionSeconds, steeringSeconds }` thin update rates | A world holds its authored capacity, bounded only by the body-index format (`WorldBodiesLimits.IndexLimit`), and client tables grow with it; `garden-throng.puck` runs 10,008 bodies headless, and two tick passes still visit every awake body (gap 8) |
| Parallel tick | Snapshot rows fork in 256-row partitions (`WorldDocument.Apply.cs` `SnapshotPartitionRows`) | Kit bodies advance serially, one arena transaction each ([open items](open-items.md#engine-job-system), "Kit bodies' advance stays serial"): 2,000 citizens are 2,000 serial steps per tick |
| Walking between places | Surface domains (`navigation.puck` `walkable`); the `navigated(domain, register)` target source (`BodyTargetSource.Navigated`) routes a body to a register; a `shared: true` domain's followers read goal fields, one per goal over the domain's `network` of districts (`NavigationRuntime.GoalField`) | `MaxDomains = 16`, `MaxCellsPerDomain = 65_536`; no crowd avoidance beyond flock separation (`FixedFlockSteering`) |
| Daily schedules | `skies.puck` `dayCycle` authors a `day` clock row that advances per second, with lights and sky keyed on it; rules read it through `compareState`; decision policies with `periodSeconds` and `commitmentSeconds` | No routine construct: a schedule is composed from the clock, a decision and a destination, and the destination is the gap above |
| Vehicles | The `across` shaping facet (anisotropic drive) and `BodyTargetSource.CurveFollow` | `WorldCurves.MaxKnots = 64`, `MaxRows = 64`; no world uses curve follow; the only vehicle is quarantined prior art (`experimental/.../modules/kart.puck`), which is read, never built |
| Lights | `point(...)` and `spot(...)` in `render.lighting.lights`, powered from state cells, riding bodies by `anchor: entity(...)`, made in arrays by `map` | `SdfLights.MaxLights = 128` per frame; point and spot lights shadow only through the irradiance probes; 500 lights needs [illumination](illumination.md)'s many-lights step |
| A market | Escrow, refund and settle compose from ordinary effects (schema README, "A market"); `auction.puck` is the nearest content | None known |
| Repeated buildings | `for`/`map` placements; a rigid creation bakes once and each copy costs a transform and a draw ([rendering decision](../decisions/rendering.md)) | One near copy sets every copy's baked detail ([virtual geometry](virtual-geometry.md)) |
| Toon look | `standard/shading.puck` `cel`, `standard/styles.puck` `celMaterial`/`celStyle`, `standard/tones.puck` | None known |
| Ink and hatching | A program's `ink` phase over the view's edge search; `standard/ink.puck` `inked`, `hatched` and `standardInk`, which `celMaterial` and `celHatched` lay over the band; the city's `cityStyle` carries its own ink | No line on glass or water, no seam of a smooth blend, and no part boundary inside one bake group ([ink](ink.md#build-order)) |
| The benchmark | `benchmark-route.puck` chapters; `gardenSeam` already crosses a painting with a `cross` step, and `go` plans over the destination's navigation | Against a remote authority the route does not yet arrive in every chapter ([benchmark](benchmark.md)) |

## The city

**Layout.** A walled hill town on a river bend, in the valley and gallery
direction ([art](../game/art/README.md#the-visual-bar)): cream and ochre
modules, teal roofs and awnings, warm stone. Four districts around one square,
each a navigation domain of its own:

- **The square and market.** Stalls, a fountain, the arrival frame and the
  return painting. Most citizens pass through at least twice a day.
- **The terraces.** Housing on stepped streets: where citizens wake and sleep.
- **The works.** Workshops and a depot where vehicles load.
- **The quay.** The river edge, a ring road for vehicles and a bridge.

A ring road and two cross streets carry vehicles between the depot, the
market's loading lane and the quay. Pavements and streets are separate
domains, so walkers and vehicles never plan into each other's space.

**A citizen.** A generic standard design, so any author's world can take one:

- a kit in `worlds/standard/citizen.puck`: a walker-scale body, `overlap`
  contact (solid contact forbids batched motion), and an `autonomy` row;
- a `navigated` producer toward a `goal` register over the pavement domain,
  with flock separation for passing;
- a schedule as state rules: a per-citizen row of anchors (home, work, market
  stall, leisure spot), a decision on the `day` clock that picks the next
  anchor, and a designation of that anchor's point as the goal;
- a look from a small set of parts (body, head, coat, hat, palette slot) so
  2,000 citizens show a few hundred distinct looks, not one or 2,000.

**A vehicle.** A standard design too, in `worlds/standard/vehicle.puck`: a
kit with the `across` shaping row and curve follow along a road loop, a stop
rule at stops and crossings read from state, and headlights as `spot` lights
anchored to its body, switched on by the clock.

**Lights.** Street lamps, shop windows and vehicle headlights: about 400
fixed lights powered from the clock and about 100 riding vehicles.

**Night.** The city runs its own `dayCycle` clock, so the painting in the
gallery shows the city's time, not the valley's.

## Stages and acceptance

Each stage lands a running city on `partner/city`, merged by the lead.
Acceptance is read from the running World; frame times name the device,
size and topology.

1. **Blockout and portal.** The city document with its four districts in
   blockout, the painting in the gallery's east wing, the arrival frame and
   the return.
   - A body walks into the painting and arrives in the square; it walks back
     out through the return (`body.place` reads each side).
   - The painting shows the square from the gallery (a capture through the
     glass).
   - `world.navigation` lists the city's domains, and `world.navigation route`
     finds a route from the arrival to each district.
2. **Citizens at scale.** The citizen design in the standard and the schedule
   rules.
   - `world.counters` shows the city at 2,000 active bodies, in 500-body
     steps.
   - Over one city day every citizen visits at least two anchors (a state
     reading of each citizen's anchor history row).
   - No citizen ends the day unreachable (`$nav:<body>:unreachable` read
     across all bodies).
   - A player walking through a crowd is not blocked; citizens step around
     (captures from the square at three hours of the day).
   - `puck test --reproduce` on the city's scenario agrees bit for bit.
3. **Vehicles and market.** About 100 vehicles lapping the road loops, and the
   market's trade.
   - Every vehicle laps its loop (`vehicleStop` advancing per vehicle).
   - A vehicle in a crossing's approach box holds while the crossing's region
     has an occupant, and resumes once it is empty (`vehicleHeld` per
     vehicle), so no vehicle drives through a walker.
   - A player buys at a stall: the purse, goods, stock and till rows change
     as the market's rules say.
   - The vehicles move onto road `curve` targets once a curve follower can
     stand down and resume, the mechanism possession also needs.
4. **Look and night.** The city to the visual bar.
   - About 500 lights over the night (the lights table's count read from
     `world.counters` or the render report).
   - Ink outlines and selective hatching authored through the ink phase.
   - Captures from at least four sides at noon, dusk and night, judged by a
     look critic against the bar and the gallery side by side.
5. **The remote topology.** The city hosted on a second authority.
   - About 2,100 movers replicated to a client:
     `world.tick.interest-bytes` over `world.tick.interest-bodies` recorded
     per recipient.
   - The client's frame time in motion through the square.
6. **Perspectives.** The city played from each perspective above.
   - A seat possesses a citizen: its camera moves to the citizen
     (`body.where`'s `anchor=body:<n>`), its schedule pauses, and on release
     it walks on to its current anchor.
   - A seat possesses a vehicle and drives a lap of its loop; other vehicles
     yield to it at the crossings.
   - From the builder's view, a verb moves a stall or rezones a lot, and the
     citizens' and vehicles' routes follow on the next anchor change.
   - A perspective the seat has not unlocked is refused by name.
7. **Zoned districts.** After the vehicles move onto road curves. The city's
   districts become zone rows (residential, commercial, industrial,
   agricultural, civic, port) in a standard city-simulation module, with farm
   fields beyond the river.
   - Each citizen's home, job and shopping come from the zones' rows, read by
     state, never from a hand-written list.
   - Goods flow from farm to granary to market to home: the market's stock
     rows fill from deliveries the vehicles carry, and empty as citizens buy.
   - Demand and growth: a zone with unmet demand gains a building level (a
     placement's response swaps its prototype) over city days.
   - The builder's verbs zone a lot and place a building through the same
     rows; a possessed citizen or vehicle keeps its place in the flow.
8. **A platform-generated district.** The works and its warehouses are
   generated from the hosting platform's observation rows.
   - A warehouse stands per observed store (`deal` over the observation's
     rows), its stock read from the store's size.
   - Vehicle flow and the market's queue follow metric rows through `inhabit`
     counts read from state.
   - Run locally with the fixture provider and against the hosted deployment
     with its approved configuration: the district changes when the
     deployment does, with no provider noun in the city's documents.
   - Gaps to name as found: observation providers for traffic, queues and
     compute beyond resource listings and metrics; an approved configuration
     reaching a federated world such as the city; and a composed district
     regenerating as rows change without a reload.

**The benchmark chapter.** From stage 1, `benchmark-route.puck` gains a
`citySeam` chapter: the route walks to the city painting, crosses with a
`cross` step, walks the square and a street through each district by `go`,
and returns. Every quiet benchmark measures the city as it grows.

**The frame bar** is read at each stage on the RTX 2060 at 1920 by 1080, in
the same-process topology from stage 1 and the remote topology at stage 5.
The 4070's reading is the lead's.

## Engine gaps the city expects

Each goes to the lead when it blocks, with its file and line and a repro in
the city, and is fixed in the engine, never sculpted around:

1. **Serial kit-body advance.** 2,000 citizens are 2,000 serial transactions
   a tick (open items, "Kit bodies' advance stays serial").
2. **A rule cannot send a body to a point.** `WorldBodyDesignationKind`
   allows `Body` and `Clear` only; a point designation exists only as a
   mutation (`WorldTargetDesignation.AtPoint`). A schedule needs a rule to
   designate an anchor's point.
3. **Crowd avoidance.** Citizens heading to an anchor share its goal field
   (a district domain marked `shared: true`, the districts in one `network`),
   but they pass one another only by flock separation (`FixedFlockSteering`);
   local avoidance at crowd density (velocity obstacles or density-aware
   separation) is not built.
4. **The lights table.** 128 lights per frame against about 500; the
   many-lights step in [illumination](illumination.md).
5. **Ink.** Authored ink and hatching need [ink](ink.md)'s phase; until then
   only the fixed outline exists.
6. **Disclosure per recipient.** The placement projection is recomposed per
   recipient on every tick that reads it (`WorldRecipientView.Placements`);
   at city counts it becomes the frame thread's largest garbage source.
7. **Nested views.** The city painting's window draws a crowd through
   `views.nestingDepth`.
8. **Awake-population tick passes.** Fact derivation and the perception image
   visit every awake body each tick, not only the ones that moved (open items,
   "Two tick passes follow the awake population").

## The lanes

The city's owner (the RTX 2060 session) builds every authoring lane. Engine
lanes are the 4070's, routed by the lead from the gaps above.

- **C1. Blockout and portal:** `worlds/debug/city.puck`, its districts'
  blockout and navigation, the painting row, the destination and the
  benchmark chapter.
- **C2. The citizen:** `worlds/standard/citizen.puck`, the schedule rules,
  the city's population and looks.
- **C3. The vehicle and the market:** `worlds/standard/vehicle.puck`, the
  roads and loops, the market's rules and stalls.
- **C4. Look and night:** the city's materials, lights, ink and hatching, and
  its look-critic rounds.
- **C5. The remote run:** the city on a second authority, with the lead.
