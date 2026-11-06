# The water feature

The debug world's water as one feature with one owner: a glacier on the
north-west rim melts over three hours into a lake, the lake's outlet falls to
the valley floor, the river runs to the pool and drains into an aquifer that
fills and then lets the river rise, and the falls run dry while the river
settles. The owner's judgement of what stands today: "Our entire water feature
set feels half-assed, at best!" The record agrees. Each mechanism under the
water (the flow lattice, the glass medium, the flow layer, draw-time height
fields, world render lanes) landed on its own evidence; nobody owned the
feature, and the gaps inside its scope were filed as "authoring gaps" and left.

This plan is the feature's specification, its audit, its definition of done
and the lanes that build it. It replaces the water rendering research round,
whose decisions and as-built records it keeps in
[the mechanisms](#the-mechanisms-under-the-water) and whose research tables
it keeps in [the research record](#the-research-record).

## What the owner asked for

The owner's words, verbatim:

- "A lake at the top of the caldera somewhere that is fed by a melting
  glacier, with waterfall that feeds the river. This glacier should finish
  melting after 3 hours of real-world time." The melt style was left to the
  lead, who chose a tapered melt whose remaining volume is (1 − t/T)³.
- "The river should drain into an underground aquifer/reservoir system that
  eventually fills up and then allows the river level to rise. The falls run
  dry but the river reaches a homeostasis."
- "The water is meant to stress the engine a bit: visually, architecturally,
  performance, etc."

The art direction is [valley and gallery](../game/art/valley-and-gallery.md)
and the water's own target is the
[caldera water pack](../game/art/caldera-water-pack/README.md): six panels and
a stage table ("The visible melt") that this plan's acceptance reads against.
The rulings that bind every lane: authors rule (every look and rule of play is
`.puck` in `worlds/standard/` with a neutral engine default); Puck code over
engine code (behaviour is an authored program); the proper fix, never a
stopgap; the debug world is the proving ground and a feature does real work,
never faking the system it shows (AGENTS.md).

## The arc as a specification

Simulated time; the simulation runs at 30 Hz (`simulation.rateHz`), so T is
324 000 ticks. Every number is the debug world's choice in
[`water.puck`](../../worlds/debug/water.puck), passed to the standard's
[`hydrology`](../../worlds/standard/hydrology.puck) template, which owns the
mechanism; another world chooses its own.

| Quantity | Specification | Where it is authored |
|---|---|---|
| Melt duration T | 3 h of simulated time at the authored pace (`hydroSpeed` 1); `water.speed k` runs the clock and the transport k times faster | `meltSeconds: 10800`; `hydrology.rule "hydro-melt"` |
| Remaining glacier | (1 − t/T)³ of the starting ice, exactly zero at T; the `glacier` gauge reads it in [0, 1] | `meltExponent: 3`; `hydroTarget`, `hydroRemaining`, `glacier` |
| Starting ice | 20 units (two source cells of `waterPerCell` 10) | `waterPerCell: 10`, `hydrologyStages[source]` |
| Conservation | ice + water across the chain is the start's total; `world.fields` prints each row's `total` | the lattice's `transform` and `flow` reactions |
| Lake | rests at its outlet sill (0.4 m of water over a bed of 2 at a sill of 2.4) through the whole arc: it stands a few centimetres over the sill while the melt is at its peak, and on it from T | `hydrologyStages[lake, sill]`; `lakeLevel` |
| Falls | `fallsFlow`, the discharge over the sill as a fraction of the melt's peak, equals the melt's own (1 − t/T)² while the chain keeps up, and is dry (under 0.01) from T; `fallsVisible` parks the sheets when it is under 0.002 | `fallsFlow`, `fallsVisible`; the looks bind to them |
| Aquifer | confined: no cell holds more than `fullHead` (1.6 m) of water; full (`aquiferFill` ≥ 0.95) by 3T/4 | `fullHead`; `hydroWater`'s `max` |
| River | the river's four cells (bed 1) and the aquifer's ten (bed 0) level against each other until the aquifer is full; the river then takes the rest of the melt and rises (the falls' lower cells, under its head, rise with it), and is settled (drift under 0.005 m over a quarter hour) from T | `hydrologyStages[river, aquifer]`, `flow(... over: [hydroBed])` |
| Steady state | the chain's 21.77 m of water and ice: 1.2 m rests in the lake, 16 m in the aquifer (ten cells at `fullHead`), and the rest stands at one head across the river and the falls' lower cells, 1.824 m over the aquifer's bed: river 0.824 m (`riverDepth`), pool 2.324 m over its floor (`poolLevel`, under its `poolLevelMax` 2.35) | `fullHead: 1.6`, `poolBase: 1.5`, `poolLevelMax: 2.35` |
| Order | the aquifer fills before the falls dry; the river rises with the aquifer's head once that head passes the river's bed and then past it as the aquifer fills | the chain's geometry |

[The audit](#the-audit) holds the run's own probes against these. The chain's
stores hold 1 024 units to a metre of water (`hydrology`'s `resolution`), so the
flow's fixed-point floor, under which a cell stops giving, lies below the
slowest discharge the arc reads.

## The audit

Two legs, neither of which watches the three hours: the simulated arc run
headless in fast time through `puck test` (seconds over the chain alone, half
an hour on the debug basis), and the stage captures in a windowed World
driven to each stage by `water.speed` (four minutes of simulation a backend).

### The arc, run

The arc's proof is one scenario run on two worlds. `worlds/debug/water-arc.puck`
holds its probes: an Edge rule per milestone that latches the gauges into keyed
rows (`glacierAt`, `lakeAt`, `fallsAt`, `riverAt`, `aquiferAt`, `poolAt` and the
conserved `totalAt`, one cell per milestone: every eighth of T, then T + 15, 30
and 45 min). `water-arc-tests.puck` runs it on the debug basis (machines
stopped, the integration proof) and `water-chain-tests.puck` on
[`water-chain.puck`](../../worlds/debug/water-chain.puck), one `hydrology`
expansion at `water.puck`'s numbers (the inner loop, the chain independent of the
world around it). Each carries the same `expect` lines, since a `test` block
stands in its own document. Both run 405 040 ticks (T and 45 min of settling) and
reproduce under `--reproduce`; the debug basis also ticks the paintings' garden
and night field beside the chain.

The run at the authored pace (`hydroSpeed` 1), headless and unpaced, every
probe a fixed-point reading of that tick:

| Milestone | glacier | lake | falls | river | aquifer | pool |
|---|---|---|---|---|---|---|
| 0 (tick 30) | 0.9997 | 0.466 | 1.000 | 0.035 | 0.000 | 1.535 |
| T/8 | 0.670 | 0.458 | 0.781 | 0.030 | 0.368 | 1.530 |
| T/4 | 0.422 | 0.443 | 0.575 | 0.154 | 0.669 | 1.654 |
| 3T/8 | 0.244 | 0.430 | 0.401 | 0.386 | 0.832 | 1.886 |
| T/2 | 0.125 | 0.419 | 0.259 | 0.535 | 0.937 | 2.035 |
| 5T/8 | 0.053 | 0.411 | 0.147 | 0.654 | 0.997 | 2.154 |
| 3T/4 | 0.016 | 0.405 | 0.067 | 0.772 | 1.000 | 2.272 |
| 7T/8 | 0.002 | 0.401 | 0.018 | 0.817 | 1.000 | 2.317 |
| T | 0 | 0.400 | 0.0001 | 0.824 | 1.000 | 2.324 |
| T + 15 min | 0 | 0.400 | 0.0000 | 0.824 | 1.000 | 2.324 |
| T + 30 min | 0 | 0.400 | 0.0000 | 0.824 | 1.000 | 2.324 |
| T + 45 min | 0 | 0.400 | 0.0000 | 0.824 | 1.000 | 2.324 |

What it settles:

- **The glacier is the specification exactly.** `glacier` equals (1 − t/T)³ to
  the fixed-point digit at every eighth and is exactly zero at T.
- **The falls are the discharge.** `fallsFlow` tracks the melt's own (1 − t/T)²
  within a thirtieth at every eighth (0.766, 0.563, 0.391, 0.25, 0.141, 0.063,
  0.016), runs at 7T/8 and is dry at T. The lake passes what melts as it melts,
  resting within 7 cm of its sill at the start and on it from T.
- **The aquifer fills by 5T/8 and the river then backs up.** The aquifer's
  ten cells are capped at `fullHead`, full from 5T/8; the river climbs with the
  aquifer's head and past it (0.65 to 0.82 as the last of the melt arrives),
  and stands settled from T, the pool under its `poolLevelMax`.
- **Conservation holds.** `hydroTotal` (ice plus water) reads the start's
  21.77 m at every milestone.

### The captures

A windowed Direct3D 12 World of this branch (RTX 4070, 1280 × 900), bakes
settled, exposure pinned at EV100 13.83 from the river view, driven through
the arc by `water.speed 60` with each stage landed by `world.wait until`. The
stills and their contact sheet are the audit's hand-back (the commit that
lands this plan names where they were written); the gauges of each stage's
tick are in the table. The captures were taken on the chain before its retune, whose
gauges (the falls' depth, the lake's storage) read otherwise; the tour retakes
them on the current chain. Every view is an operator camera pose
(`view.override camera look <eye> <target>` in metres, or `pose`):

| View | Pose | Stage 1 (glacier 1.00, falls 0.20) | Stage 2 (glacier 0.22, falls 0.79) | Stage 3 (aquifer 0.36, river 0.53) | Stage 4 (glacier 0, falls 0.47, `meltVisible` 0) | Stage 5 (falls 0.13, river 0.84, aquifer 0.81, pool 2.34) |
|---|---|---|---|---|---|---|
| Rim and lake | `look -230 345 230 -311 324.5 311` | a flat turquoise disc lying in a smooth tan bowl, its edge clipping through the bowl's near rim; six white domes in the foreground; a grey sky | the disc risen clear of the rim, floating with its edge in the air, the ribbon's top folded to a blade standing up from the outlet | | | the disc back in the bowl |
| Glacier | `look -300 330 300 -251 300 251` | the lake's surface fills the frame; one white dome behind it | | | | |
| Falls from below | `look -95 6 95 -160 90 160 50` | a pale sage ribbon 8 m wide hanging in front of a smooth featureless wall, its fourteen sheets reading as steps; no shadow on the wall, no wet rock | the ribbon folded to a thin blade | | a bare wall: no sheet, no streak, no spray, while the chain carries 0.47 | |
| Plunge basin | `look -120 6 120 -143 -1 144` | a square turquoise sheet cut into the lawn under a flat pale band, a grey cloud at its foot; the river leaves it as a slab standing proud of the turf | | | the basin's water standing above its wall tops; no falls | |
| River at the bridge | `look -58 10 38 -80 0 62` | a flat cyan ribbon with white streaks on unbroken turf; a slab bridge with two ramps, no arch; the falls a pale column in the distance | unchanged | | unchanged: the river's surface never moves with `riverDepth` | |
| River along its length | `look -100 25 95 -76 0 70` | the ribbon's eight segments with visible seams at the bends, ending at the pool's square | | | | |
| Pool from the path's end | `pose -43 1.7 -12 90 -8` | an opaque-reading cyan surface, the far wall's tan top above it; the pavilion's column and rail in shadow fill the left third | | the water higher, the far wall's top under it | | the water at the wall tops |
| Pavilion pane | `look -31 1.6 -8 -39 -0.6 -18` | the window a dark grey slab under the canopy with a faint white arc of contact foam; no water line, no float, no inlet | | the slab faintly bluer; nothing readable | | as stage 3 |
| Aquifer passage | `look -50 -2.1 -18 -44 -1.3 -18` | from inside the pool's medium the frame is a dim brown-green wash (the camera is inside a glass body); the passage a dark rectangular tunnel with the lawn and the gauge's teal post showing through its far end; the passage's own water view empty | | | | |

**Vulkan.** A second hold took the rim, falls and pool at stages 1 and 5
(the same views, named for the backend): the same composition and look to the eye,
and by changed-pixel count against the Direct3D 12 stills, with neither
tick nor day clock pinned, 10 % (rim) and 13 % (falls) of pixels differ by
over 8 levels at stage 1 and the pool's half (its flow phase and level at
another tick), which is why the definition of done compares pinned ticks
under lockstep.

**Cost and frame readings.** `host.frames` at the river view, under other
lanes' builds: avg 21.4 ms, p50 16.67, p99 49.7, 152 of 600 iterations over
16.7 ms, produce avg 11.7 ms; a reading beside counts, not a verdict (the
lead's quiet window owns the bar). Under `world.lockstep on` the windowed World
stepped about three ticks a second (one tick a rendered frame), so a stage at
speed 60 is reached with lockstep off; the tour turns it on only for the
captures themselves. A settled capture costs about 35 s under lockstep, which
sizes the tour. The glass counts at the pool are the W1 record's (the audit's
counter reads landed on the beacon's node; the main node's are read in P1).

What the stills settle beyond the gap list: the series' time of day moves
across the stages (the sky's day runs at 60× too, so sign shadows swing
between stages 1 and 4), which the tour pins by holding the day clock; the
pool's camera at the path's end stands inside the pavilion's terrace, so the
place and the pavilion overlap (A4, A5 settle the layout together); a camera
inside a water body renders a flat wash, since the glass pass has no
inside-the-medium case (an engine gap the swimmer's own view meets, recorded
under R4's enhancements as the first thing a swimmer's camera needs).

### The gap list

Every gap, by domain, each with what the owner asked or the art shows, what
the code or capture shows, and the lane that closes it.

#### Simulation

| Id | Gap | What the code and the run show | Lane |
|---|---|---|---|
| S5 | The hidden chain is drawn | `hydroWater` and `hydroBed` carry `heightScale: 1` and a colour, and `WorldFieldEmitter` draws every row with a height scale, so the 24-cell chain 90 m under the floor bakes and draws two height fields nobody sees. The flow cannot drop the scale in data: `FieldLattice.StepFlow` reads a row's own height only when its `heightScale` is above zero, and an `over` row counts only with one, so leveling water against a bed needs both rows to carry a scale, and the emitter draws every row that carries one. Closing it takes a way to mark a lattice row as simulation only (not drawn, not collidable), which is engine code, not data ([open items](open-items.md#water)). | open item |
| S7 | No stage checkpoint | A stage is reached live by `water.speed k` or by `replay.drive` to a recorded tick; `world.save` writes the authored document with state as authored, `world.state.cell.set` writes scalar rows only, and no verb writes a lattice cell or saves a live arena with its lattices. | D1 |

#### Geometry

| Id | Gap | What the code shows | Lane |
|---|---|---|---|
| G2 | The lake is a film in the air | `lakeSurface` is a cylinder 0.24 m thick (`scale [radius, 0.12, radius]`) at y 324.5 over a bowl the valley cuts to y 319 (`valleyBasins[lake-basin]`: radii 17 at y 336), and its slide lifts it 4 m per unit of `lakeLevel` over 0.4, so during the melt it floats up to 3.6 m higher with air beneath. The art's panel 2 has clear stones at the near bank, turquoise middle depth and teal deep water. | A2 |
| G3 | The falls stand 8 m off the wall | `fallsRibbon` places fourteen flat 8 m sheets at `standoff: 8` in front of the face profile, so the ribbon hangs in the air before the rock; the art has one sheet hugging and interrupted by rock, and a residual dark wet streak when dry. | A3 |
| G4 | The pool and plunge floors are flat boxes | `waterBasin` is a flat bed with square walls and up to four steps; the art's panel 4 wants shallow stones at the margin, a dark deep end, submerged stones under caustic light and the drain throat at the bank (the pool's west opening at (−66, −1.3, −6) leads nowhere); panel 3 wants a dark rounded plunge pocket. | A4 |
| G5 | The reservoir is invisible and has no inlet | The pavilion's observation window (`glass { depth: 8, ior: 1.5 }`) lies over the reservoir's glass field surface, and the glass pass shades the nearest layer alone, so the pane reads as tint with no water line, float or medium (W1's capture). The art's panel 5 has a descending inlet, air over the water, and the float on the surface beside pale ticks; the chamber has no inlet geometry and the gauge's post tops out at y −0.1 under the window. | A5, after the two-glass-layers lane |

#### Look, light and motion

| Id | Gap | What the code and the record show | Lane |
|---|---|---|---|
| L2 | The falls core reads pale | `fallsGlass` scatters 0.8 per metre of `#1A8A94` under the full sun through a 0.2 m sheet; the record's capture reads the core as pale sage, the art's as transparent turquoise with selective white edges. | A3 |
| L3 | No wet rock | Nothing darkens the fall line, the splash zone or the banks; the art names wet rock as a coloured darker material region. | A3, A4 |
| L4 | Spray and splash | The spray is one cloud volume (`impactSpray`), scaled by `fallsFlow`; there are no foam rings in the plunge pocket, no droplets, no splash: the particle pool (W3) is not built. | R3 |
| L5 | Reflections of the near geometry | Glass reflects the probe gather and the sky (W4a); the bridge and the pavilion do not reflect in the pool (W4b, a measured enhancement). | R4 |

#### Debug tooling

| Id | Gap | What exists | Lane |
|---|---|---|---|
| T1 | A visitor cannot read the aquifer | The float gauge stands under the pavilion's window (G5), so the one readout of the aquifer's fill is invisible; the pool's level, the falls' on/off and the glacier's size read from the lawn. `world.state aquiferFill` and `world.fields` read it from the console. | A5 |
| T2 | No tour | `puck sweep --tour` has no water stops; the full-arc capture series is this plan's integration acceptance and lands as tour rows. | P1 |

#### Cost

What the GPU leg reads at the pool, the falls and the lake is in [the captures](#the-captures); the budget table in the definition of done holds the ceilings.

## Definition of done

The build is held to this section. Each criterion is checked by a capture, a
counter or a state reading named here, never by watching the arc in real time.

### The arc's proof is a scenario

The simulation half of done is `puck test worlds/debug/water-arc-tests.puck`
and `water-chain-tests.puck` green, with `--reproduce` showing the run replays
bit for bit, on the merged head. The claims, each read from a probe at a
simulated milestone (`worlds/debug/water-arc.puck` holds the probes):

| Milestone (simulated) | Claim | Probe |
|---|---|---|
| t = 0 (tick 30) | glacier = 1 within 0.01; falls at the melt's peak (over 0.95) | `glacierAt[0]`, `fallsAt[0]` |
| t = T/8 … 7T/8 | glacier = (1 − t/T)³ within 0.01 (0.670, 0.422, 0.244, 0.125, 0.053, 0.016, 0.002); `fallsFlow` = (1 − t/T)² within 0.03, and running at 7T/8 | `glacierAt[1..7]`, `fallsAt[1..7]` |
| every milestone | the lake within a few centimetres of its sill (over 0.399 m of water, under 0.5 while the melt is near its peak) and on it (under 0.401) from T | `lakeAt[0..11]` |
| t = T | glacier exactly 0 | `glacierAt[8]` |
| t = T and after | falls dry (`fallsFlow` < 0.01), the first reading after T a quarter hour on | `fallsAt[8..11]` |
| over the melt | aquifer rises monotonically at the eighths to 5T/8 and is full (`aquiferFill` ≥ 0.95) by 3T/4, while the falls still run | `aquiferAt[0..6]`, `fallsAt[7]` |
| after the fill | the river rises past the head the aquifer holds (`riverDepth` over 0.6, the river bed's 1 m below the aquifer's 1.6) and is settled: drift under 0.005 m over the quarter hours ending at T + 30 and T + 45 min; the pool above its start and under its cap | `riverAt[6..11]`, `poolAt[0]`, `poolAt[11]` |
| conservation | `hydroTotal` (ice plus water) within 0.01 m of its first reading at 4T/8, T and T + 45 min | `totalAt[0, 4, 8, 11]` |

A scenario that cannot express this cheaply is a gap fixed at its cause (the
simulation's own fast-forward or rate scaling), never an arc shortened for
the test. [The audit](#the-arc-run) holds the run's probes.

### Visual acceptance is per stage and per view

The five stages are reached by state, not by waiting: `water.speed 60` (the
authored lever, [`water.puck`](../../worlds/debug/water.puck)) runs the arc in
three minutes of wall time, and `world.wait until <tick>` lands each stage on
a known tick; `world.exposure pin` holds exposure across the series so stages
compare; `world.lockstep on` before the first tick makes the series repeat
across Worlds. The arc itself takes four minutes of wall time at speed 60
with lockstep off (under lockstep the windowed World steps one tick a
rendered frame, about three a second); a settled still costs about 35 s, so
the tour's size is its still count: the seven views at stages 1, 4 and 5 and
the rim, falls and river at stages 2 and 3, 27 stills in about 20 min a
backend, Vulkan at stages 1 and 5 only. The stages and the tick each lands on
at speed 60 (T = 5 400 ticks after the speed is set):

| Stage | Tick after `water.speed 60` | State reading that names it |
|---|---|---|
| 1 full glacier | +30 | `glacier` ≥ 0.99, `fallsFlow` > 0 |
| 2 mid-melt, falls running | +1 350 (T/4) | `glacier` 0.42 ± 0.02, `fallsFlow` > 0 |
| 3 aquifer filling, river rising | the tick at which `aquiferFill` first passes 0.5 (from the arc's probes, between T/8 and T/4) | 0.4 < `aquiferFill` < 0.6 |
| 4 falls dry, lake at its sill | +5 460 | `glacier` = 0, `fallsFlow` < 0.01, `lakeLevel` within 0.01 of 0.4 |
| 5 homeostasis | +6 750 | `riverDepth` within 0.005 of `riverAt[11]`, `aquiferFill` ≥ 0.99 |

The views, each an operator camera pose (`view.override camera look …`,
[the audit's table](#the-captures)), and what each must show at each stage,
read against the art pack's panels:

| View | Panel | Stage 1 | Stages 2–3 | Stage 4 | Stage 5 |
|---|---|---|---|---|---|
| Rim and lake | 2 | a grounded ice mass on bare moraine, the lake flat and reflecting the warm rock, clear stones at the near bank and teal depth in the middle, the outlet sill the one place it moves | the ice visibly smaller within the same cradle; the lake at its sill | bare cradle, the lake retained at its sill, nothing over the sill | as stage 4, still |
| Falls from below | 3 | one continuous sheet, transparent turquoise core, selective white edges, spray at the impact, a shadow on the wall | the sheet narrower, foam thinner | a dry face with a residual dark wet streak, no sheet, no spray | as stage 4 |
| Plunge basin | 3 | a dark plunge pocket, expanding foam rings, streaks downstream, wet rock hugging the fall line | the pocket calmer | still water in the basin, no rings | as stage 4 |
| River along its length | 4 | a channel with banks and a bed the current reads over, riffles upstream, foam at bank contacts, the bridge's arch clear above it | the river lower while the aquifer takes it | the river rising within its banks | settled, the bridge's clearance and the pool's quiet margin kept |
| Pool from the path's end | 4 | deep swimming water, clear shallow margins with stones, reflected cliffs and sky, bank contacts | as stage 1 at the river's level | higher | at its settled level |
| Pavilion pane to the reservoir | 5 | air over a low water line; the float on its post touching the surface beside the ticks; the inlet descending | the water line rising with the float | full chamber, the inlet's flow diminished | full, still |
| The aquifer's passage | 5 | the submerged passage from the pool's east wall to the chamber, lit, water on both sides | | | |

Each cell is checked by a settled capture of that view at that stage, on
Direct3D 12 for every stage and on Vulkan for stages 1 and 5, the pair
compared by changed-pixel count and inspected; a capture that fails a cell's
reading fails the stage.

### Motion is checked in presented bursts

A settled capture is still by construction. What moves (the flow layer on the
stream, falls and lake; the spray; the particles once W3 lands) is checked by
`puck_capture_frame` with `mode: "presented"` and `frames: 8` at the
falls-from-below and river views: changed pixels and luma change per frame
over the water well above the lawn baseline (the W1 record: 25–28 % of
pixels by 66–108 levels over the stream against 10–13 % at 1 level), and a
settled still of the same tick unchanged across two Worlds under lockstep.

### Performance is a budget of counts

The frame bar (60 FPS on the RTX 2060 and 4070, 45 on the Deck, highest
quality) is the floor; the lead reads `host.frames` at the five views in the
integration sweep. What a lane measures itself is counts, read with
`world.counters gpu pass sdf.world$glass`, `pass sdf.world$glass-shade`,
`pass sdf.world$mesh`, `summary` and `world.residency`, at the pool, the
falls and the lake views with bakes settled, and compared before and after:

| Count | Budget at each water view | Today (W1 record at the pool, 1280 × 900) |
|---|---|---|
| Glass raster draws | ≤ 8 (every water body, the pane, the falls) | 4 |
| Glass raster texels | ≤ 2 × the image (one layer of overdraw) | 1 346 168 |
| Glass-shade texels | the image (once per pixel, by construction) | 1 152 000 |
| Mesh-pass vertices from water groups | ≤ 150 000 across the pool, the river channel, the basins and the lake | 92 736 (glass vertices) |
| Cloud volume steps × footprint at the falls | ≤ the composite's share today; a particle pool replaces, never adds | the audit's reading |
| Mesh region bytes owed to water prototypes | ≤ today's, read from `world.residency` after a cold bake | the audit's reading |
| Lattice work | one `hydro` lattice step per tick (`physics` counters), the water views stepping every 8 ticks | as authored |

A lane that raises a count past its budget names the dividend the owner's
ruling asks for or reduces it.

### Integration acceptance

The full-arc capture series (five stages × the seven views on Direct3D 12,
stages 1 and 5 on Vulkan) run from one script on the merged head, reviewed
view by view against the art pack's panels and the table above, with the
arc scenario green and reproduced, the counts within budget and the lead's
`host.frames` reading at the bar. The series' script and its contact sheet
are committed with the plan's closing change, under `worlds/debug/sweep.json`
as a tour so `puck sweep --tour` repeats it.

## The lanes

Each lane names its outcome, the invariant it rests on and what enforces it,
the mechanisms it reuses and may not duplicate, and the check that shows it
worked. World authoring is first-class work: the carved channel, the basin
with depth, the falls' geometry and shadow, the visible reservoir, the pool
floor's stones and the readable aquifer are lanes, not follow-ups. Two glass
layers a pixel have landed ([option 3 as built](#option-3-as-built)); the
reservoir's pane reads them.

The owner's rulings on the three taste calls: the lake rests at its outlet
sill and the falls run dry within 15 simulated minutes after T; the aquifer is
confined, fills by 3T/4 and then backs the river up to a held level; and the
river runs in a carved channel, not a slab over the turf. The chain builds the
first two ([the arc](#the-arc-run)); [the river's channel](#the-rivers-channel-as-built)
is the third.

### A2. The lake and the glacier

**Outcome.** The lake as a body filling its bowl from the floor to `lakeLevel` (a field surface over the bowl or a solid whose top slides), through which the bowl's floor and bank stones read; the outlet sill the one moving edge. The glacier as a tongue in its cradle with crevasses and melt channels authored as shapes, shrinking about the cradle to bare moraine at T.

**Invariant.** The level is the gauge's, as [the river's](#the-rivers-channel-as-built) is; the ice's size is `glacier`'s.

**Reuse.** `lakeSurface`, `glacierMass`, `valleyBasins`, `waterViews`, `lakeFlowLayer`, `icePalette`.

**Check.** The rim view at stages 1, 2 and 4; `creation stats` for the glacier's bake groups; counts.

### A3. The falls on the face

**Outcome.** The ribbon hugs the face profile (standoff to the sheet's own thickness), interrupted where the rock steps out; a wet-rock material region along the fall line and the splash zone (an authored material on the wall's shapes, `weathering` or a paint, whose coverage lane follows `fallsFlow`), left as a dark wet streak when dry; the spray bound to the discharge; the core reading turquoise with white edges (the `fallsGlass` and `fallsFlowLayer` values retuned from the audit's capture).

**Invariant.** The sheet's shape, fold and visibility are functions of `fallsFlow` at the presented tick.

**Reuse.** `fallsRibbon`, `fallsProfile`, `fallsFlowLayer`, world lane 4, `impactSpray`; [authored glass shadow density](../rendering/sdf/handbook/frame-rendering.md#glass-after-the-opaque-image) for the shadow.

**Check.** The falls-from-below view at stages 1, 2 and 4; a presented burst over the sheet; `world.debug-view shadow` showing the ribbon's shadow.

### A4. The pool and the plunge basin

**Outcome.** The pool's floor with shallow stones at the margins and a dark deep end, the drain throat at the west opening joined to the reservoir's passage (one authored route, no decorative hole), the plunge pocket rounded and dark under the falls with wet rock, the steps kept. Contact foam at the bank follows from W1; foam rings and streaks come with R3.

**Invariant.** Every basin collides as it is drawn (unions of admitted primitives, `waterBasin`'s rule).

**Reuse.** `waterBasin`, `stonePalette`, the contact ramp, `waterViews`.

**Check.** The pool-from-the-path and plunge views at stages 1 and 5; the swimmer's buoyant depth scenario (`water-tests.puck`) still green; counts.

### A5. The reservoir, readable

**Outcome.** The reservoir visible through the pavilion's pane once the two-glass-layers lane lands: air over the water line, the float on the surface beside its ticks, readable from the terrace; a descending inlet from the pool's drain into the chamber (authored geometry carrying the passage's water view); the passage lit. A5's geometry lands first and its pane acceptance waits on that lane.

**Invariant.** The chamber's water line and the float read one gauge (`aquiferFill`) through one height scale.

**Reuse.** `reservoirPavilion`, `floatGauge`, `waterViews`, the glass pass.

**Check.** The pavilion-pane view at stages 1, 3 and 5; the passage view at stage 5.

### R3. The particle pool (W3)

As [W3](#w3-the-presentation-particle-pool): the splash at the plunge pocket and droplets at the falls' foot first. Checks there.

### R4. Enhancements

[W2's field source](#w2s-field-source) and [W4b](#w4b-a-bounded-screen-space-march-on-glass), each gated by its capture.

### P1. The tour and the budget

**Outcome.** The full-arc capture series as rows of `worlds/debug/sweep.json`: `water.speed 60`, the five stages at their ticks, the seven views, exposure pinned, lockstep on; the counts of the budget table read at the pool, the falls and the lake; `host.frames` read by the lead's quiet window.

**Check.** `puck sweep --tour` reports every water stop; the contact sheet reviewed against the art pack.

### D1. A stage checkpoint

Deferred to [open items](open-items.md#water) with its mechanism named: a verb that saves and restores a live arena with its lattices and bodies (what `replay.record` snapshots at its arm, `WorldPersistence`), so a stage is a file. The tour reaches its stages by `water.speed 60` (four minutes of simulation a backend, the stills the larger cost), which is within the sweep's budget; the verb is built when a second long-arc feature wants it.

### Order

The simulation is the feature's truth and every look binds to it. A2 to A5 run in parallel (A5's pane acceptance waits on the two-glass-layers lane). R3 next, since it adds a pass. P1 lands with the last authoring lane. R4 and D1 after the integration acceptance.

## The mechanisms under the water

The engine mechanisms the water uses, each built once for every user and
recorded here as built. The water is one user of each; a second
implementation for the water is a defect.

### W1 as built

`SdfGlass` carries a medium (`Scatter`, `Scattering`, material row 31) and a
contact (`ContactReach`, `ContactCoverage`, row 20.w's halves); the
[handbook](../rendering/sdf/handbook/frame-rendering.md#glass-after-the-opaque-image)
states both. The glass pass reuses the opaque path's mechanisms rather than
copying them: one probe gather (`sdfMeshProbesAt`) feeds its reflection
(`worldEnvironmentReflection`, then `sdfMeshReflection`, the sky alone where no
probe answers), the one light walk (`sdfLightResponse`, uncascaded, probe
shadowing) gives its highlights and the light its medium and revealed flow
surface receive, and the flow layer runs through `sdfShadeFlow`, shared with
the light stage. In the standard every water surface wears a glass row:
`waterGlass` on the field surfaces, variants on the stream
(`streamGlass`), the falls (`fallsGlass`) and the lake (`lakeGlass`), each a
template parameter.

**Invariant and its door.** A glass pixel reads the lit image once
(`sdfGlassLitAt`, or the frost's disc); the medium, the contact and the
revealed surface are functions of the thickness, `opaque - t` and the arriving
light alone. `sdf-glass.comp.hlsl` is the one read site. Where it stops: glass
behind glass reads the opaque image behind the nearer pane, which the two-glass-layers
lane is lifting.

**Owed.** `world.gpu-timing` for the glass passes against the build before
W1, in a quiet window, and the glass kernel's registers beside the views
kernel's.

### W2 as built

A palette slot's `flow` (`PaletteFlowDocument`, lowered to `SdfFlow` on
`SdfMaterial.Flow`) packs material rows 27 to 30 (`SdfProgram.PackMaterials`;
the stride is `SdfProgram.MaterialVectorsPerEntry`) and runs `sdfApplyFlow`
(`shade/shade-layers.hlsli`) in the views pass's light stage after
`applyWeathering`: the weathering's lattice noise with an analytic gradient
(`sdfFlowNoise`), advected along the velocity's tangential part in two layers
half a cycle apart with triangle weights, a low-frequency per-place phase
offset against pulsing, the blend renormalized so contrast holds through the
cross-fade, a stretch along the flow, a coverage that reveals an authored
surface through `sdfRevealSurface`, and a normal tilt scaled by the flow's
share along the surface. Sources: the material constant, and a render lane
scaling it (`lane`). An `Object` velocity rides a slot's frame, or turns by the
draw's matrix on a draw shading in world space (a static copy); a `World`
velocity keeps the pattern in world space too.

**The phase invariant and its door.** A flow layer's image at a presented
tick is a function of the document and that tick alone. The phase enters the
kernels only through the pass block's `flowClock` (`SdfWorldPackage.FlowClock`),
which `SdfFrameBlock` integrates from `SdfFrame.Clock` with
`PresentedTick.Integrate`, as `SdfVolumeMotion.At` does for volumes, reduced by
`SdfFlow.ClockPeriodSeconds`; the material carries a whole number of cycles a
clock period (`SdfFlow.CyclesPerClock`), so the layer joins where the clock
wraps. A program with no flow (`SdfProgram.HasFlow`) writes zero, so a still
world's views stand; the clock is a lighting member of the cadence signature
(`SdfWorldTables.Cadence.cs`), so the shading passes rerun as the flow moves
and the geometry, the cascades and the surface pass stand. No `sdf-*` kernel
reads the Frame group's `time` or `timeDelta`: `puck search -M 0
"frameGroup\.(time|timeDelta)" src/Puck.SdfVm/Assets/Shaders/Sdf` finds
nothing. A world lane (4 to 7, `render.lanes`) reads the same on every
surface, static copies included: the host resolves it from state at the
presented tick (`WorldEnvironmentResolve`) and it enters the kernels through
one pass value, `worldLanes`, read only by `sdfLane` (`frame/sdf-lanes.hlsli`).

**The water on it.** In the standard (`worlds/standard/water.puck`), the
river is a `waterViews` field body wearing `streamMaterial`, whose flow is
`streamFlowLayer` ([the river's channel](#the-rivers-channel-as-built)). The falls is fourteen sheets
wearing `fallsFlowLayer`, one world-space downward velocity that runs down
every sheet at its own slope; each sheet folds about its own flow axis by
`fallsFlow`, the chain's discharge over the sill, and its foam coverage follows
world lane 4, which the debug world binds to `state.fallsFlow`. The lake keeps its level slide and wears
`lakeFlowLayer`. The meadow's grass wears `meadowWind`, the same layer with a
wind vector.

### The river's channel as built

The river runs in a carved channel, not a slab over the turf. `riverChannel`
(`worlds/standard/water.puck`) is a patch of ground (`region`, the floor's
opening for it, `lawn.puck`'s `holes`) with one authored trapezoid profile cut
along the course by a subtraction per leg (bed `bedHalf`, banks widening to
`bankHalf` at the ground's top, each leg's ends running on by `join` so a bend
has no notch), the bed and banks in one material and the riffle and bank stones
as unions. A creation that blends other than by union collides as its composed
field (`WorldColliderSet.CollidesAsField`), so a body walks the banks and the
bed as drawn. The debug world authors the course once (`riverLegs`,
`worlds/debug/water-site.puck`): the channel, the water, the bridge and the
two basins' wall openings all read it, and the pool's and the plunge basin's
openings sit where the course crosses their walls (`riverCrossing`).

The water is one `waterViews` lattice over the channel's region, 3 m cells,
whose datum is the bed (`riverBed`, the pool's floor plus its rest depth, so the
river's surface and the pool's are the same height at every `riverDepth`). A
cell's value is the chain's `riverDepth` everywhere, so the surface stands
under the ground (the turf hides it) outside the cut and shows exactly where the
cut exposes it; the lattice is a medium, so the Moth stands in the channel and
reads `inmedium` in `body.where`. The surface wears `streamMaterial`: the
stream's glass (a hand's depth of clear water over the stones, turquoise
where deep, foam where the bed or a bank comes within `reach`) and
`streamFlowLayer`.

**Invariant and its door.** The river's drawn level is `riverDepth` at the
presented tick, never a constant: the surface is the lattice row's height
field, which the lattice's transform sets from the gauge, and every bank and
bed collides as drawn because the channel is one creation whose contact is its
own composed field. Where it stops: a flow layer carries one velocity over a
surface, so a winding course cannot be followed leg by leg. The river's
current runs along the course's mean heading (`riverHeading`) and the legs turn
only gently about it (within about 35 degrees at the first leg, under 20 over
the rest); a flow that follows a field of directions needs engine work (a
palette flow whose velocity is read from a lattice or a course), recorded
here. The bridge's deck stands over the settled surface with its piers on the
banks.

### Field surfaces move at draw time

The field surfaces (the water views, the hydrology's columns) follow their
rows' full values as they are drawn. A height field keeps one mesh while its
heights move; its drawn surface follows the frame's height snapshot in the
vertex stage; a height change uploads only the changed values; the bake runs
only when a column appears or disappears or the lattice changes shape.

**The design.** A height field is one engine type, `SdfHeightField`
(`Puck.SignedDistance`), which owns two sampled fields: a *reference* field,
the columns' distance lattice with every raised column's top at one reference
height (one cell above the lattice origin), and a *heights* field, a
one-voxel-tall lattice of the current column heights.
`SdfProgramBuilder.HeightField` emits the reference as the instance's sampled
instruction, which the bake hashes and contours as it does any sampled field,
and declares the heights as the instance's displacement
(`SdfProgram.HeightFields`). Only a change of which columns are raised
republishes the reference, so only that keys a new mesh. The heights field is
never hashed: its snapshot reaches the render passes through the frame's
sampled table, `sdfFrameSampled` (`SdfWorldPackage.FrameSampled`), each entry a
header and the current and previous frames' values. The draw record names the
handle (`SdfMeshRecord.DisplacementWord`), and the one resolver every vertex
read passes through (`sdfMeshWorldPosition`, `sdfMeshWorldNormal`,
`sdfMeshPreviousPosition` in `frame/sdf-mesh.hlsli`) maps the reference vertex
vertically before the draw's matrix.

**Invariant and its door.** A field surface's drawn geometry at a frame is a
function of its mesh key (the reference lattice: extent, cell and raised mask)
and the frame's height snapshot. The key half is enforced by `SdfHeightField`
being the only producer of a displacement; the snapshot half by the resolver
being the only path a kernel reads a mesh vertex through. Rule 4 holds: bodies
and media read the lattice's fixed-point cells; the snapshot is presentation.

**Where it stops.** The resolver is exact at every vertex of the reference
mesh; inside a merged flat polygon the drawn surface is linear between
displaced vertices, exact for a uniform level (every water view) and a one-row
lattice. Displaced draws are dynamic for lighting (`SdfMeshDraw.IsDynamic`), so
the irradiance volume's static scene leaves them out and the scene diagnostics
read them as unknown.

### Option 3 as built

**What landed.** A pixel shades up to two glass interfaces. The glass target's
depth test keeps one record, the nearest front face, and a depth test keeps one
extremum, so the second record is in the glass sums, which grow from 8 to 16
words a pixel (`SdfWorldPackage.GlassSumWords`, `frame/sdf-glass.hlsli`): every
front face before the opaque hit counts itself and claims one of three entries
(draw plus one, triangle) while one is free, and every back face raises the
nearest back face, an atomic maximum of its distance's complemented bits.
`glass-shade` names the second interface as the held front face nearest along
the pixel's ray other than the target's (`sdfGlassSecondInterface`), splits the
glass distance at the nearest back face (the near interface absorbs its own
material over its share, the far one the rest of the distance and optical
depth), shades both by one loop body (flow through `sdfShadeFlow`, contact,
medium, the probe reflection, the light walk), and composes the far interface
inside the near one's transmission. The pass counts each interface it shades as
a work step (`march.steps` in `world.counters gpu`).

**Invariant and its door.** A pixel reads the lit image once: one
`sdfGlassLitAt` (or the frost's one disc) after the interface loop, at the
point both refractions displace; the loop body has no read. The second
interface is exact and independent of raster order up to three front faces
before the opaque hit, since every one is held and the choice is by distance
with ties to the lower draw and triangle; `SdfGlassSumsDeviceLawTests` checks
the entries as a set and the rest of the words exactly in 24 orders. Where it
stops: a third front face's glass absorbs within the far interface's share and
what lies beyond the second interface is the opaque image; past three front
faces the record names no second interface and the near one alone is shaded.
A 64-bit atomic k-buffer would lift the count but needs 64-bit buffer elements
through the interface generator and int64 atomics on both backends, and DXC
lowers `RWByteAddressBuffer`'s 64-bit atomics to 32-bit ones on SPIR-V.

**Owed.** The glass passes' GPU time before and after in a quiet window.

### W3. The presentation particle pool

Not built. The weather decision
([particles are shared presentation state](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces))
fixes the shape and the research record below settles the collider.

**Outcome.** A region-owned GPU pool: a compute step recorded before `views`
in the root chain at the presented tick's fixed rate over a budget-sized
capacity, emitters as authored shapes (the `SdfPaintShape` packing, a rate
and a seed), motion as generic authored primitives (a flow vector or the W2
field row, gravity, drag, a collision response against a sampled brick, age,
size and colour over age), and a draw of instanced quads through the mesh
vertex stage's counted indirect draw and a fragment of its own, split by the
glass target's front-face depth. The brick is derived from the bake's lattices
for the region's static draws, never a second bake; the step's length is
bounded by the brick's cell. The standard authors `impactSplash`;
`impactSpray` stays the cloud volume.

**Invariant and enforcement.** The pool's state is a function of the
document, the presented tick and the seed, and nothing in simulation reads
it: the step reads only the per-frame table the host packs and the brick,
with the seed hashed per particle (`sdfPcg3d`) and no wall clock; the pool
lives in `Puck.SdfVm` with no path into the state mirror, as the volumes do.

**Reuse, no second mechanism.** The mesh vertex stage and counted indirect
draw; the glass target for the split; `SdfPaintShape` and `evaluateShape`;
the volumes' per-frame table packing (`PackVolumes`); `sdfSampledRegion` for
the brick's read; `GpuRegion` for the pool's buffers; the residency's byte
budget for capacity.

**Order.** (1) The pool, the table, the step with gravity and age, the quad
draw after `views`, no collision; the splash. (2) The brick derivation and the
collision response. (3) The glass-target split. (4) The W2 field row as a
force; the forest leaves.

**Checks.** A presented burst at the plunge basin shows the splash with
`gpu.draws` counting the one indirect draw and `world.counters gpu` the pool's
step as one dispatch; the counts in the budget table; a particle dropped on
the basin's steps rests on them; one behind the pool's surface refracts.

### W2's field source

Not built. The flow vector read from a field row the state system owns: a
`field` lattice row the author writes, or a derived row the hydrology's
`FieldLattice.StepFlow` publishes as its per-cell transfer (the `m_flowDelta`
it computes and discards today), sampled by the layer through a sampled field
from the frame's sampled table (`sdfFrameSampled`). **Invariant.** As W2's:
the row is state, so the image stays a function of the document and the tick.
**Check.** The river bends around the bridge's piers with no authored comb in
a presented burst; `world.fields` shows the derived row.

### W4b. A bounded screen-space march on glass

Not built; a measured enhancement after the lanes above: a few taps along
the reflected ray against the visibility record's depth for the near
geometry a probe blurs (the bridge, the pavilion), falling back to the probe
gather, at a tap count the material authors. Lands only when a capture at
the pool shows the bridge's reflection worth its taps on the 4070 and the
count holds on the Deck.

## The research record

The round's sources, kept as the evidence behind the mechanisms above. A row
is marked opened when the round's lane read the page, the deck's extracted
text or the shader; a summary row rests on a search engine's digest and
carries no verdict alone.

### Transparent surfaces: refraction, absorption, depth colour

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| Single Layer Water, Epic, <https://dev.epicgames.com/documentation/en-us/unreal-engine/single-layer-water-shading-model-in-unreal-engine> | An opaque-queue shading model whose material output supplies scattering coefficients, absorption coefficients per channel in reciprocal metres, a phase `G`, and a "Color Scale Behind Water"; "The fully lit scene and depth are used as input to the Single Layer Water pass"; the low-end path "reverts the material to a simple translucent material with no volume integration" and drops screen-space reflections. | The shipped surface model is Puck's glass pass plus in-scatter: a single depth layer, the lit scene read behind it, absorption per channel per metre. The fallback shows the baseline shape (no SSR). | Opened. |
| Looking Through Water, Flick, <https://catlikecoding.com/unity/tutorials/flow/looking-through-water/> | The underwater depth is the background depth less the surface depth; exponential fog on that difference; refraction offsets the grab by the tangent-space normal scaled by the depth difference; a negative difference after the offset resets the UV. | The depth-difference shoreline input and the in-front guard, both of which the glass pass has (`opaque - t`, the nearer-surface reset). | Opened. |
| The Technical Art of Sea of Thieves, SIGGRAPH 2018 Talks, <https://history.siggraph.org/wp-content/uploads/2022/09/2018-Talks-Ang_The-Technical-Art-of-Sea-of-Thieves.pdf> | Colour blends a deep and a sub-surface colour by view angle, sun and a peak mask; foam at peaks and "around objects that intersect the water surface" by depth comparison, dispersed by a feedback blur; waterfalls and streams project the camera's depth onto a mesh so a character cuts the falls and foams the stream. | Two authored colours chosen by view; contact foam as a depth comparison dispersed over time; a falls sheet as a mesh with projected depth. | Opened. |
| Horizon Forbidden West water, SIGGRAPH 2022 Advances, <https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Water-Malan.pdf> | Foam and deformation passed through vertex colour "can't create features smaller than triangles", with "foam popping" from undersampling. | Foam carried by geometry undersamples and pops; foam belongs at shade time. | Opened (notes). |
| Water capabilities, Unity HDRP 17.1, <https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition%4017.1/manual/water-capabilities-of-the-water-system.html> | Water surfaces are refractive transparent objects; foam and caustics monochrome; no baked lighting; water receives but cannot contribute to ray-traced reflections; misbehaves behind other refractive objects; temporal AA ghosts since water writes no motion vectors. | The second shipped system also stacks one refractive layer and lists glass-through-glass and temporal history as its limits. | Opened. |

### Flowing water and waterfalls

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| Water Flow in Portal 2, Vlachos, SIGGRAPH 2010 Advances, <https://www.advances.realtimerendering.com/s2010/Vlachos-Waterflow(SIGGRAPH%202010%20Advanced%20RealTime%20Rendering%20Course).pdf> | "Pixel shader flow, not geometric flow"; an artist-authored flow map; the UV offset by the flow scaled by time; two layers offset half a phase to hide the restart; noise against pulsing; normal strength scaled by speed; "2 fetches, 21 instructions" on 2010 hardware. | The whole of W2's kernel. Puck's flow source is an authored field rather than a painted texture, which is Vlachos's own future work. | Opened. |
| River water body, Epic, <https://dev.epicgames.com/documentation/en-us/unreal-engine/water-body-actors-in-unreal-engine> | A river is a spline with a velocity, width and depth per point; the velocity is written to a flow map; all bodies render as one tiled mesh. | The flow field as a product of authored data (Puck's river is bearings and lengths in `.puck`). | Opened. |
| Wave particles, Yuksel, House and Keyser, SIGGRAPH 2007, <https://cemyuksel.com/research/waveparticles/> | Unconditionally stable wave simulation; particles convert to a height field on the GPU; no global flow. | A ripple from a swimmer as a presentation height row. Watched. | Opened. |
| Waterfall in Season, realtimevfx.com, <https://realtimevfx.com/t/how-we-made-waterfall-in-season-a-letter-to-the-future/23420> | Separate meshes for body, highlight and foam; downward motion as a world-aligned pattern with an acceleration curve; masked splash particles. | A falls is a sheet whose shading flows with an acceleration curve and masked particles at the foot: W2 plus W3. | Opened. |
| Stylized VFX in RiME, <https://simonschreibt.de/gat/stylized-vfx-in-rime/>, <https://80.lv/articles/rime-breaking-down-the-stylized-water/> | A linear vertex-colour edge reshaped in the material; a cube reflection and no geometry reflection. | A stylised water reads with an authored edge ramp and a cube reflection. | Pages opened; the talk not watched. |
| Water rendering in Far Cry 5, GDC 2018, <https://80.lv/articles/the-gdc-talk-water-rendering-in-far-cry-5/> | Single-frame rendering and half-precision math for waterfalls and lakes. | A lead on half-precision shading for the Deck. | Summary only. |

### GPU particles and volumes at fixed or low cost

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| GPUParticlesCollisionSDF3D, Godot, <https://docs.godotengine.org/en/latest/classes/class_gpuparticlescollisionsdf3d.html> | A baked signed distance field as the particle collision shape, 16³ to 512³, hollow inside with a `thickness` against tunnelling. | The shipped engine with an SDF collision proxy. Puck's proxy is derived from the bake's lattices, signed inside, so no thickness hack. | Opened. |
| GPUParticles3D, Godot, <https://docs.godotengine.org/en/stable/classes/class_gpuparticles3d.html> | A fixed `amount`; `fixed_fps`; a `visibility_aabb` gating work; `use_fixed_seed` for replays. | A fixed capacity, a visibility box, a fixed step rate and a fixed seed. | Opened. |
| Initialize context, Unity VFX Graph 16, <https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@16.0/manual/Context-Initialize.html> | Capacity is a fixed element count sizing the allocation; elements past it are discarded. | The same fixed-capacity pool. | Opened. |
| Niagara GPU simulation, Epic | Forum digests: GPU collision defaults to the depth buffer; distance fields collide off screen for static meshes. | The two baseline collision sources; Puck has the depth pyramid and derives the field. | Summary only. |
| Physically Based and Unified Volumetric Rendering in Frostbite, Hillaire, SIGGRAPH 2015 Advances, <https://advances.realtimerendering.com/s2015/index.html> | A froxel volume every medium writes into, integrated once per frame. | A second integrator beside `shadeVolumes`; watched until many overlapping media make the per-volume march the larger cost. | Index opened. |
| Claybook, Aaltonen, GDC 2018 | Fluid simulated and rendered against an SDF world. | The proof that an SDF world is a fluid's natural collider. | Summary here. |

### Reflections on the baseline

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| Stochastic Screen-Space Reflections, Stachowiak, SIGGRAPH 2015 Advances, <https://advances.realtimerendering.com/s2015/index.html> | Glossy SSR at half resolution with neighbour reuse and temporal filtering. | The shape of a baseline SSR and its dependency on a temporal filter, which the reactive glass pixels lack. | Index opened. |
| Single Layer Water and Unity HDRP (above) | UE's water uses SSR with a fallback that drops it; HDRP's water receives but cannot contribute to reflections. | Every shipped water reflects by probes or sky first and adds a screen-space march where the hardware allows. | Opened. |

### What the sources settle

- Every shipped water surface is one refractive layer over the lit opaque
  scene, with absorption per channel and an in-scatter term that lightens the
  medium with depth. Puck's glass pass is that layer, with the medium W1 added.
- Contact foam is the depth difference between the surface and the opaque hit,
  dispersed over time; foam carried by geometry undersamples and pops.
- Flow is a pixel-shader distortion in two half-phase layers with noise
  against pulsing, and the flow field is a product of authored data.
- A GPU particle pool is fixed-capacity, budget-sized, gated by a visibility
  box, stepped at a fixed rate, seeded for replay, and collides with a
  distance field when it must work off screen.
- Reflections on the baseline are probes and sky first, a screen-space march
  where it pays, and never a second view per plane.

---

[Plans](README.md) · [Graphics architecture](graphics-architecture.md) · [The debug world as one valley](debug-world-composition.md) · [Open items](open-items.md#water) · [Decisions](../decisions/rendering.md)
