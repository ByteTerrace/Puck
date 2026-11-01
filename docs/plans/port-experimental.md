# Port the experimental worlds' features into the debug world

The quilt, the island (the One World) and their games, districts and tools are
quarantined under [`experimental/`](../../experimental/README.md). They are not
re-admitted: what they have that the debug world lacks is ported into
`worlds/debug/` (or `worlds/standard/` where it is a reusable design), because
the debug world is the proving ground that shows the engine's systems compose
and run well, and the inspiration an author draws on. This page is the
inventory and the lane list behind the
[port packet](open-items.md#port-the-experimental-worlds-missing-features).

## How to read it

Each feature names its quarantined source, then its support, found with
`puck search`, `puck references` and the owning skill's references:

- **wire:** the engine and the documents already support it; the port is `.puck`
  content in `worlds/debug/`.
- **standard:** a reusable design (a kit-row template, an ability, a deck) that
  belongs in `worlds/standard/`, with the debug world importing it.
- **gap:** engine code is needed, and the missing mechanism is named.

Sizes are small (hours), medium (a day or two) or large (several days, or more
than one sitting at the windowed World). Paths without a prefix are under
`experimental/src/Puck.World/Assets/worlds/`; `tests/` means
`experimental/tests/Puck.World.Canaries/` (one directory per canary) and `games
tests` means `experimental/tests/Puck.World.Games.Tests/`.

Two facts bind every lane. The debug document's rule sheet is about 2,765,000 of
the 4,250,000 work-unit ceiling ([card tables](open-items.md#card-tables-in-the-board-game-room)),
so a lane that adds a whole game lands it as a destination document behind a
painting, as the garden and the night field do, unless the game is priced to fit.
And the debug world runs one local seat (`bodies { localSeats: 1 }`,
`worlds/debug/avatars/moth.puck:536`), so a lane that needs a second seat waits
for lane 5.

## What the debug world already has in another form

Not ported again:

| Quarantined | Debug world |
|---|---|
| `modules/granaries.puck` (the storage court) | `worlds/debug/granaries.puck` |
| Klondike, FreeCell, the solitaire collection (`games/freecell.puck`, `games/README.md`) | `klondike.puck`, `freecell.puck`, `solitaire.puck` behind `cardtable.puck` |
| Physical chess, Go, Reversi, Alchemy (the parlor and market) | `chess.puck`, `go.puck`, `reversi.puck`, `alchemy.puck` |
| `modules/arcade.puck` (cabinets and a handheld), `tools/hgb-mirror.puck` | `devices.puck`, `desktop.puck`, `machine-debugger.puck`, `cartridges/` (the tetromino, light-gun and hgb-mirror cartridges) |
| `modules/studio.puck` (a stage, a turntable) | `studio.puck` (the avatar studio) |
| The island's picture-frame doors to dungeons | `paintings.puck`, `garden.puck`, `night.puck` |
| `drinkMe`/`eatMe`, the wren's shrink on contact | `size.puck`, `wonderland.puck` |
| The `wander` producer, the companion body | `range.puck`, `companion.puck`, `worlds/standard/companions.puck` |
| `Assets/probes/tracks/brio-head.probe-track.json` | `probe-beacon.puck`, `probes/beacon.probe-track.json` (the brio-head track is lane 14) |
| The `captures` stations and the `parity` layout | `sweep.json` and `puck sweep` |
| Sky, atmosphere and indirect-light canaries | `skies.puck`, the `gi-corner` room, `quality.puck` |

## Lanes, in order

**The ports that force the fundamentals are the job system's forcing
workloads (the owner's ruling).** Each lands just before the step it proves,
so every optimization is measured on the load it exists for:
- the meadow (lane 9) now, beside the bake and memory work;
- the creature garden (lane 4) now, ahead of the parallel tick's body
  advance (T2);
- the second seat (lane 5) and authored passes (lane 1) ahead of parallel
  view recording (R1);
- the quilt (lane 2) with residency step 2;
- water and fields (lane 3), the rules arcade (lane 7) and the combat yard's
  pair primitive (lane 13) as the field, rule-VM and pair-rule workloads.

The rest (lanes 6, 8, 10, 11, 12 and 14) is content, taken after the
fundamentals ([job system](engine-job-system.md#6-build-order)).

Within that, order is value to the proving ground: lanes that exercise engine
systems the debug world does not touch come first. Beyond the garden's pond
plot (lane 4: `interactions`, `navigation`, flocks, a `Pull` hold), a search of
`worlds/` finds no `adjacencies`, `curves` track, `region(` trigger, medium,
`field` lattice, authored HLSL package, row `visibility` audience, `embeds` row
or identity fact.

### 1. Authored render passes

The debug world mounts one graph instance, the engine's `source.capture`
package (`worlds/debug/desktop.puck:28`), and ships no HLSL. The pipeline
subsystem (`views.graphs`, shader packages, `world.shaders.reload`) is the one
a 30-second edit-to-running ruling most depends on.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A state lattice row bound into an authored pass that draws one tile a cell | `experimental/worlds/rulepush/board.hlsl`, `board.graph.json`, `level.puck` (the `graph "board"` row) | wire: `views.graphs` ([documents-render](../../.claude/skills/puck-world/references/documents-render.md)), canaries `pipeline-*`, `post-pass` | An exhibit room in the gallery hall, a screen whose pane draws a live state grid; the first test of state-to-pass binding and shader hot reload in the debug world, and the drawn surface lane 7 needs | medium |
| A compare studio: a pipeline shader and a native SDF model in two slots under one paired camera | `tools/shader-compare.puck`, `tools/shader-compare.md`, `pipeline.puck` (its `pipelines/*.hlsl` sources are not in the tree; author a fresh pair) | wire | The same room; shows a procedural pass beside the creation system and exercises paired cameras | small |
| A procedural raymarched playing card (a Didone Q and a heart, pop-up and camera presets) | `experimental/worlds/genesis/card.hlsl` | wire | The room's sample shader only; the card look itself is the standard deck's, `worlds/standard/cards.puck` | small |
| Machine against machine, side by side | `tools/hgb-compare.puck` | wire | The Wonderland deck (`devices.puck`); low priority | small |

### 2. Seamless topology and carried identity

Debug crossings are all portals. Adjacency (a seam you walk across, with
authority handoff) and identity facts carried across a crossing are built
([adjacency and federation](../../.claude/skills/puck-world/references/adjacency-and-federation.md),
`src/Puck.World.Schema/WorldAdjacencies.cs`,
`src/Puck.World.Client/WorldAdjacencySceneEmitter.cs`) and `worlds/` has no
`adjacencies` row and no identity fact.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| The quilt: ground shards stitched by invisible seams, walked across with no portal | `shards/quilt-{nw,ne,se,sw}.world.json`; canaries `seamless-adjacency`, `seamless-four-corners-circuit`, `four-corners-sharded` | wire; the island once outgrew the instance ceiling ([design](../game/design.md)), so keep each shard a ground-only document and measure | A terrace past the hall's yard; the benchmark for streaming a neighbour authority | medium |
| A shard with two seams dropped, so the border closes (`unavailable: Closed`) and a body past the edge is carried back | `shards/quilt-nw-gap.puck`; canary `quilt-nw-gap-edge-carry` | wire | The terrace's far edge; the failure half of the seam | small |
| A pitched seam that hands a falling body to the world below | canaries `rim-drop`, `rim-refused`, `rim-round-trip` | wire | Beside the endless fall (`paintings.md`), which is the portal form of the same drop | small |
| Authored composition: `entry world`, `world`, `border` and a module that carries its own tests | `experimental/worlds/beacons/` (`beacon.puck`, `beacons.puck`) | wire ([Transpiler README](../../src/Puck.World.Transpiler/README.md)) | The terrace is authored this way, so the package doubles as the worked example | small |
| A win written onto the visitor's record that opens a gate on return | `experimental/worlds/rulepush/hub.puck`, `rulepush.puck`; island rules `seat1-returning-identity-wakes-at-plaza`; the studio gate that opens once awakened | wire (`WorldIdentityProjection`, `src/Puck.World.Schema/WorldIdentity.Records.cs`) | The night field awards a fact; a hall door opens on it | medium |

### 3. Water and fields

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A pool whose `field` lattice is a medium: swimming, buoyancy, depth rows read from regions | `modules/dive.puck` (`lattices field(name: "pool"...)`, `bond: Medium` holds, `dive-depth-*` rules); canaries `dive-medium`, `medium-submersion` | wire ([documents-motion](../../.claude/skills/puck-world/references/documents-motion.md)); standard: a swim hold row the Moth refines, as `abilities.puck` holds `dashAbility` | A courtyard pool; the first `field` lattice and `Medium` hold in the debug world, and the base the climate wing's wetness and puddles stand on | medium |
| Flow across basins: a source topped up each step, boundary spill, a drawn whole-field pass | canaries `flow-conservation-live`, `lattice-draw-fill` | wire | The pool's inflow; proves reactions run live | small |

### 4. Creature garden — done

Ported into the garden destination's pond plot ([garden.md](../../worlds/debug/garden.md),
[pond.puck](../../worlds/debug/pond.puck)), all four rows wired: the hound pack
flocks round its designated companion (the visitor, or the pack-mate it trusts
most), the spiders stalk the visitor over the `plot` navigation domain and face
her, the dragonflies flock over the pond, and the rumour rules run as the
island wrote them, translated through `puck decompile`; a hedge beside the bone
gives a hound something to stand behind, since on open ground every hound sees
the holder and `rumor` never fires. The fish school in air
inside the pond's walls on a full-lift hold until lane 3 gives the garden a
water field. The crawl is the standard's `crawlHold`
(`worlds/standard/abilities.puck`), which the spiders expand; the Moth can
expand it for her walker as she does `dashAbility`, but does not yet (a feel
decision for the owner). No engine code was needed.

The body count is one parameter, `pondFlocks(creatures)`, and
`worlds/debug/garden-crowd.puck` is the garden at 128 creatures, the parallel
tick's forcing workload. `puck test --reproduce` passes on
`worlds/debug/garden-tests.puck` (the pack and the bone, the rumour behind
the hedge, the stalker, the crawl) and on the crowd's own scenario.

The tick, headless and unpaced on the lead machine while other lanes held the
CPU at 100 % (so read the ratios over the absolutes), over 900 ticks after a
300-tick warmup, `host.frames` simulate avg / p50:

| Creatures | 12 | 32 (the garden) | 128 | 256 | 512 |
|---|---|---|---|---|---|
| Tick, ms | 1.37 / 1.29 | 1.65 to 1.96 / 1.39 to 1.43 | 3.72 to 3.83 / 3.20 to 3.49 | 4.88 / 3.90 | 5.78 / 5.69 |

At 128 creatures the tick is about 11 % of a 30 Hz period. By `dotnet-trace`
sampled thread time over 30 s of the unpaced run (32 / 128 creatures), as a
share of `WorldTick.StepCore`: body advance 89 / 80 % (motion programs 50 /
33 %, of which contact sweeps 48 / 30 %; producers 13 / 17 %, of which flock
steering 1.1 / 1.7 % and navigation 0.3 / 0.2 %), the event feed 2.7 / 9.0 %,
snapshot 2.5 / 3.8 %, the rule VM 1.7 / 1.1 % (decisions 0.3 / 0.1 %), pair
contacts 0.8 / 0.6 %, and no crossing scan (the scenarios drop the frames).
Every body is a kit body and none is rigid, so the parallel tick's E1 (rigid
advance) takes none of it; this workload is the case for partitioning kit
bodies ([job system packet](open-items.md#engine-job-system)).

The source was the island's garden district, Puck programs the engine only
schedules (`Puck.Physics.FixedFlockSteering`, the `ProduceFlockIntent`,
`SenseNearestInCone` and `FaceSensorTarget` motion operations).

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Flocks: a hound pack that follows the companion, a dragonfly swarm, fish that school in the medium | `puck.world.json` `bodyMotionPrograms` `pack`, `swarm`, `school`, `fishMotion`; canaries `flock-local`, `navigation-shared` (128 bodies) | wire | The garden destination grows a pond plot; the 128-body workload is the parallel-tick forcing case ([job system](open-items.md#engine-job-system)) | large |
| A spider that stalks the visitor over a navigation domain and faces its quarry | `stalk` program, `navigation.domains`, `targetRegisters` | wire | The same plot | medium |
| Wall and ceiling crawling with a stamina spend | kit rows with `hold: Pull` (`puck.world.json` `kits`); the player's `wall` hold | wire; standard: a crawl hold row | The same plot; also gives the Moth a climb | small |
| Rumor: hounds that witness the bone holder within sight and nine units raise trust, report first, spread claims, and let them expire | rules `witness-claim`, `first-witness`, `rumor`, `claim-expire`; interaction `bone-claim`; rows `boneHolderTrust`, `claimSeq` | wire (`los`, `distance` and `pair` channels, `scheduleState`) | The same plot, a second step; a deterministic multi-agent workload | medium |

### 5. Second seat and hidden information

Every private-information game needs a second reader that the first cannot read
through. The debug world has no row `visibility` audience, no `embeds` row and
no second seat.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Two local seats and a `split` layout | `puck.world.json` `bodies.localSeats: 2`, `views` layout `split`; canary `split-seats`; `worlds/debug/suit-tests.puck:29` already runs two | wire; cost in the entity table and the per-seat rules (`moth.puck:7718`) | `debug.puck`'s capacity; the prerequisite for the rest of this lane and for poker and Hearts in lane 12 | medium |
| Lineup: hidden busts, yes-or-no questions, a computer that asks the best splitting question | `experimental/worlds/parlor/lineup.puck`, `parlor.basis.puck`; canary `lineup`; games tests `LineupLawTests`, `LineupDealLawTests` | wire (seat-scoped `visibility`, a `deal` facet) | A board game room table | medium |
| Hidden ranks: concealed armies, per-side knowledge boards, a reveal by widening the readers | `games/hiddenranks.puck`; `experimental/tests/Puck.World.Fixtures/minimal-hiddenranks-host.puck` | wire (`visibility`, `readersFrom`, `knowledge` boards, `removeStateCell`) | A board game room table | medium |
| Word spy: a key card only the spymasters read, an aim computed in a vector space | `games/wordspy.puck`, `games/wordspy.embeddings.json` | wire (`Puck.Embeddings`, `embeds(...)`, `puck embed`; the fixture generator has no semantics, so clues are reproducible rather than apt) | The only embeddings consumer; four roles, so addon or computer seats | large |

Poker (seats, blinds, a bit-mask hand-strength judge, `games/poker.puck`,
games tests `PokerHandStrengthLawTests`) is step 2 of the
[card tables](open-items.md#card-tables-in-the-board-game-room) packet (lane 12)
and waits on this lane's seats.

### 6. Traversal course

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A kart on a closed curve: a drive program, gates taken in order, a lap time | `modules/kart.puck` (`kartTrack` curve, `kartDrive`, `kart-gate-*`); canary `kart-lap` | wire; a vehicle kit swapped on the seat, as `noclip.puck` swaps one | The north ground; the first `curves` track and `region(` trigger | medium |
| A ledge course: steps, a ledge-grab hold, a trophy region, a fall counter | `modules/jump.puck` (`vaulter`, `jumpTrophyReached`, `jumpFallsCounter`); canary `jump-trophy` | wire | The same ground; standard: the ledge-grab hold row | small |
| Planetoids: radial gravity areas a body walks round | `puck.world.json` `gravity.areas`, placements `planetoid-*` | wire (`src/Puck.World.Schema/WorldGravity.cs`) | The same ground, above the course | small |
| A grapple: attach, detach and reel channels to posts | the `walker` kit's `tether` facet, `tetherPost1` | wire; standard: a grapple ability | The same ground | small |

### 7. Rules arcade: puzzles on a drawn board

The tetromino cartridge in `worlds/debug/cartridges/` is an emulated machine;
this is the same game as state rules, which exercises different systems. Each
needs a drawn surface, which is lane 1's pass or a placement grid; the island
left the well and the snake field undrawn and read through the console.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Tetromino as rules: a `piece` workflow, a seven-piece bag draw site, `fullRow` pattern clears, lock delay by `schedule`, saturating score, wall kicks | `games/tetromino.puck`; games tests `TetrominoTickLawTests`; canary `tetromino-clear` | wire | An arcade room; priced at about 109,000 of 4,000,000 work units a tick | medium |
| Snake: a `ring`-domain body, a tick-indexed beat from two `cycle` cells, an `evicts` meal table | `games/snake.puck`, `experimental/tests/Puck.World.Fixtures/minimal-snake-host.puck` | wire | The same room | small |
| Rulepush: sentences of word tiles rewrite what objects do; one `stabilize` turn, a 32-turn undo ring, push chains | `experimental/worlds/rulepush/` (`rules.puck`, `room.puck`, `level.puck`); canaries `rulepush-undo`, `rulepush-board`, `push-ray`; games tests `RulePushTurnLawTests` | wire; needs lane 1's pass for the board | The same room; the `stabilize` and undo proof | large |

### 8. Physical judges at the table

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Billiards: a held `attack` charges a cue, the release strikes the ball with `applyRigidImpulse` | `games/billiards.puck` (`cue-charge`, `cue-release`), `experimental/tests/Puck.World.Fixtures/minimal-billiards-host.puck` | wire (`applyRigidImpulse`, `$channel:1:attack`) | A table in the board game room; the first charged gesture on a table | medium |
| Bowling: a ball and pins judged by settling | `games/bowling.puck` | wire | A lane beside it | small |
| Dominoes: a run of tiles toppling | `games/dominoes.puck` | wire | The same room | small |
| Paddleball: a physical court judged by three `interactions` latches (Distance and Region, Edge and Level), a spin follower | `games/paddleball.puck`; games tests `PaddleballContactLawTests` | wire; first user of `interactions` in the debug world | The same room | medium |

### 9. Meadow and foliage

**Done:** [the debug meadow](../../worlds/debug/meadow.md), reached with
`debug.go meadow`, centers the sixteen tiles and 1,994 two-part blades at
`(119.5, 0, -55.3)`, east of the hero studio and about 72 m from its spawn.
Six trees, seven mossy rocks, five banks, eight sand caps and thirteen tuft
clusters use the standard's new `foliage.puck` library and table scatter.
The debug module owns the placement tables. The eight pose stations are skipped.
The prior art is `experimental/src/Puck.World/Assets/worlds/moth-courtyard.puck`
and its adjacent `moth-courtyard.md`; neither was edited, built or run.

The before/after measurements use one Release engine build, RTX 4070, the
authored 1280×900 view and high quality, `--devices none`, and a GPU lease per
leg. Each cold leg starts with its own empty cache; the shared store is left
alone. Cold time is the main residency's `sdf.mesh-bake done seconds`, followed
by successful `world.wait bakes 600` settlement of every residency. Builds,
boot and post-bake GI settlement are outside that clock. Other lanes build
during these runs: these are loaded-machine observations, not quiet speed claims.

| Reading | Before | After |
|---|---:|---:|
| Direct3D 12 cold bake | 209.996 s | 306.639 s |
| Vulkan cold bake | 321.246 s | 499.573 s |
| Direct3D 12 standing, avg / p99 | 26.40 / 68.15 ms | 46.59 / 73.94 ms |
| Direct3D 12 walking, avg / p99 | 23.77 / 61.81 ms | 55.21 / 82.89 ms |
| Vulkan standing, avg / p99 | 23.50 / 48.18 ms | 63.11 / 102.76 ms |

The Direct3D reads each cover 600 host iterations. Walking alternates
`player.move 0.2 0` and `player.move -0.2 0` twenty times, 45 ticks per leg,
through the patch. The Direct3D timing table uses matching 1,800-tick
post-bake settling, a frame reset, then a 900-tick standing interval and the
walking script. The matched baseline reuses the original package snapshot,
the preserved measurement executable and its own completed cache (zero bake
dispatches, 5.588 s loading); it does not rerun the changed integration code.
The initial cold-leg Direct3D averages, after only 900 post-bake ticks, are
23.49 ms standing and 24.38 ms walking. Vulkan's before window contains
600 iterations after 900 post-bake ticks; its after contains 479 following
the longer settling and reset. `world.cadence off` keeps both views rendering.
`world.rates` keeps bodies, views, screens and far shadows on. Its 30/15 Hz
periods are 33.3/66.7 ms; the after Direct3D longest screen/far-shadow intervals
reach 77.0/76.2 ms, Vulkan 78.0/78.0 ms. These are frame overruns, not lower
authored quality. Every cold bake misses the one-minute ruling and the frame
observations miss the 16.7 ms bar.

`host.memory`, immediately after the cold bake and before collection:

| Backend | Working set, before → after | Private, before → after | Managed heap, before → after | Account live bytes, before → after |
|---|---:|---:|---:|---:|
| Direct3D 12 | 7,812 → 7,779 MiB | 14,807 → 15,032 MiB | 3,024 → 2,913 MiB | 1,526,790,926 → 1,746,075,254 B |
| Vulkan | 7,365 → 7,536 MiB | 14,382 → 14,768 MiB | 2,923 → 3,000 MiB | 1,526,473,676 → 1,745,713,084 B |

The live-account increase is about 209 MiB per backend. Working set and
managed garbage depend on collection timing; private bytes include driver heaps.

`world.budget`, `world.counters` and the cold bake plan show the work added:

| Count | Before | After | Added |
|---|---:|---:|---:|
| Placement rows | 1,173 | 1,294 | 121 |
| Field instances | 2,842 | 7,403 | 4,561 |
| Dynamic transform slots | 990 | 4,994 | 4,004 |
| Retained mesh draws | 3,363 | 7,578 | 4,215 |
| Bake groups | 2,090 | 6,183 | 4,093 |
| Unique cold bakes | 975 | 1,368 | 393 |
| Groups sharing a bake | 1,115 | 4,815 | 3,700 |

The blades share repeated geometry across tiles, rather than one bake for all
blades: 144 varying stems and 144 tips account for 288 unique blade geometries;
the other 105 new bakes are scenery. Each blade part keeps its own motion
slot and draw. Direct3D's second sampled primary mesh-pass read rises from
163 to 3,906 draws and from 1,080,414 to 5,547,060 vertices; the whole primary
node reads 589 → 12,467 draws across its passes.
The retained mesh region grows from 1,346,365,816 to 1,588,961,748 B.
Cold field samples rise from 999,550,075 to 1,101,618,980 on Direct3D and from
999,477,183 to 1,101,543,801 on Vulkan. Moving exhibit poses account for small
backend differences; these counts are observations, not recorded expectations.

Triangle LOD already exists. The named gap is that animated placements bypass
body pose-rate holds and readings (`WorldCreationInstances.Pack.cs:247`,
`WorldCreationInstances.Rates.cs:193`). The
[Engine job system packet](open-items.md#engine-job-system) owns measuring
and reducing that cost through the existing generic machinery. No engine code
is added here. Direct3D and Vulkan settled captures are inspected; the new
meadow contact scenarios and the two input-clock fixtures pass `--reproduce`.

### 10. Packages as worked examples

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| A rules module with no setting: twelve seasonal elements, an effectiveness chart, a real-time fight, orders by console, stamped as `use rules(...)` | `experimental/worlds/familiars/` (`rules.puck`, `seasons.puck`, `familiars.puck`) | wire | A destination behind a painting; the parametrised-module showcase, and the example an author copies | large |

### 11. Board tables II

New topologies and one new search node on the `board(` primitive chess, Go and
Reversi already use.

| Feature | Source | Support | Place and why | Size |
|---|---|---|---|---|
| Chinese checkers: 121 holes, jump chains, a bounded one-ply opponent | `experimental/worlds/parlor/chinese-checkers.puck` | wire | A board game room table | medium |
| Hex lines on a hex topology | `games/hexlines.puck`, `experimental/tests/Puck.World.Fixtures/minimal-hexlines-host.puck`; games tests `HexLinesModuleImportLawTests` | wire | The same room | small |
| Mancala: sowing and captures | `games/mancala.puck`; games tests `MancalaModuleLawTests` | wire | The same room | small |
| Tic-tac-toe, a two-table twin | `games/tictactoe.puck`, `experimental/tests/Puck.World.Fixtures/twin-tictactoe-host.puck` | wire | The smallest worked table, an author's first | small |
| Backgammon: a dice row, a `chance` node in the search; hitting and bearing off are authored here | `games/backgammon.puck`; games tests `BackgammonLawTests` | wire (`src/Puck.State.Search/ArenaSearch.Chance.cs`) | The same room; the first `chance` node | medium |

### 12. Card tables continued

The [card tables packet](open-items.md#card-tables-in-the-board-game-room) owns
the order: Spider (`games/spider.puck`, 104 cards, medium), then poker (lane 5's
seats), then Hearts (`experimental/worlds/parlor/hearts.puck`, four seats with AI, large). All wire
over `worlds/standard/cards.puck`.

### 13. Combat yard

`modules/arena.puck` (nearest and target-of-target targeting, auto-attack,
fire and frost, burn and thaw, death and respawn, a fire pit and an ice pool)
and `games/arena.puck` (frags, health pickups, respawn, a contended momentum
cell) are the prior art the
[debug world content packet](open-items.md#debug-world-content) names. Support:
wire, except one **gap**: the island dropped the body-pair `heat-ignites` and
`cold-spreads` interactions because a pair rule costs capacity squared against
the work budget, so spread between bodies needs a region-scoped pair primitive
(the packet's `FixedSpatialNeighborhood` item). Large; it waits on the job-system
memo.

### 14. Small items

- `Assets/probes/tracks/brio-head.probe-track.json`: a recorded four-channel head
  track, the input for the camera-as-controller stretch goal (wire).
- The studio gate and the mirror wall (`modules/studio.puck`): folds into lane 2's
  identity gate and the avatar studio (small).

## Not ported here

The quarantined gates, canary runner, parity and counters fixtures
(`experimental/tests/Puck.Parity`, `Puck.Counters`, `Puck.Determinism`, the CLI
test projects) test tools and engine seams, not world features; the
[quarantined gates](open-items.md#quarantined-gates) packet owns them. The
canaries listed above are prior art for each lane's own `test` blocks, which
run in the debug package with `puck test`. `Puck.Post`, `Puck.Bench` and
`Puck.BareMetal` hold no world.
